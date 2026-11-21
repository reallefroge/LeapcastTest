namespace LeapcastStudioAI;

public sealed record PlatformPreset(string Name, string ServerUrl, int MaxKbps, bool SupportsBandwidthTest, string KeyHint);

public static class Platforms
{
    public static readonly PlatformPreset[] All =
    {
        new("Twitch", "rtmp://live.twitch.tv/app", 6000, true, "Twitch dashboard → Settings → Stream → Primary Stream key"),
        new("YouTube", "rtmp://a.rtmp.youtube.com/live2", 9000, false, "YouTube Studio → Go live → Stream → Stream key"),
        new("TikTok", "", 6000, false, "TikTok LIVE Producer (livecenter.tiktok.com/producer in a desktop browser) → Save & Go LIVE: copy the Server URL and Stream key shown there. TikTok issues a new key every stream."),
        new("Kick", "", 8000, false, "Kick dashboard → Settings → Stream URL & Key (paste both)"),
        new("Custom", "", 9000, false, "Any RTMP/RTMPS server URL and key"),
    };

    public static PlatformPreset Get(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[^1];

    public static string BuildUrl(string server, string key)
    {
        server = server.Trim();
        key = key.Trim();
        if (key.Length == 0) return server;
        return server.EndsWith('/') ? server + key : server + "/" + key;
    }
}

public enum EncoderTier { Rich, Basic }

/// <summary>The fully-resolved encode the engine will run.</summary>
public sealed record EncodePlan(
    int Width, int Height, int Fps,
    int CapKbps,          // never exceed this (protects upload + platform limit)
    int Quality,          // CRF/CQ-style target: lower = better
    string Encoder, EncoderTier Tier,
    bool Cbr, bool LowLatency, string X264Preset,
    string ProfileName,
    IReadOnlyList<string> Reasons)
{
    public int GopFrames => Fps * 2; // 2 s keyframes: required by Twitch/YouTube, also our segment length
    /// <summary>Faster encoder settings (performance governor): less GPU/CPU per frame for slightly more bits.</summary>
    public bool Light { get; init; }
    /// <summary>Slower, higher-quality encoder settings (the "Quality" encoder preset).</summary>
    public bool HighQuality { get; init; }
    public string Summary =>
        $"{Width}×{Height} · {Fps} FPS · {EncoderCatalog.DisplayName(Encoder)} · " +
        (Cbr ? $"CBR {CapKbps} kbps" : $"Smart VBR ≤ {CapKbps} kbps");
}

public sealed record ProbedEncoder(string Name, EncoderTier Tier)
{
    /// <summary>The faster "light" argument set also works on this PC (tested at startup).</summary>
    public bool LightOk { get; init; } = true;
}

/// <summary>
/// Picks resolution / frame rate / bitrate cap / encoder from the content type, measured upload,
/// platform limit and detected hardware. Pure logic so it can be unit tested.
/// </summary>
public static class SmartTuner
{
    private sealed record Profile(string Name, int W, int H, int Fps, int CapKbps, int Quality);

    // Wizard101-style content: turn-based, lots of static UI and slow camera moves.
    // 30 FPS is visually identical for this kind of game and halves the bits needed per second of motion.
    private static readonly Profile[] LowMotion =
    {
        new("Data saver", 1280, 720, 30, 2500, 23),
        new("Balanced",   1600, 900, 30, 3500, 22),
        new("High",       1920, 1080, 30, 5000, 21),
    };

    private static readonly Profile[] HighMotion =
    {
        new("Data saver", 1280, 720, 30, 3000, 23),
        new("Balanced",   1280, 720, 60, 4500, 22),
        new("High",       1920, 1080, 60, 6000, 21),
    };

    public static EncodePlan Resolve(StudioSettings s, IReadOnlyList<ProbedEncoder> available, int cpuThreads)
    {
        var reasons = new List<string>();
        var platform = Platforms.Get(s.Platform);
        var table = s.ContentType == "HighMotion" ? HighMotion : LowMotion;
        Profile p;

        switch (s.QualityProfile)
        {
            case "DataSaver": p = table[0]; break;
            case "Balanced": p = table[1]; break;
            case "High": p = table[2]; break;
            case "Custom":
                var (cw, ch) = ParseResolution(s.Resolution);
                p = new Profile("Custom", cw, ch, Math.Clamp(s.FrameRate, 10, 120), Math.Clamp(s.MaxBitrateKbps, 500, 50000), 22);
                break;
            default:
                p = PickAuto(table, s, platform, reasons);
                break;
        }

        if (s.ContentType != "HighMotion" && s.QualityProfile != "Custom")
            reasons.Add("Low motion: 30 FPS.");

        int cap = Math.Min(p.CapKbps, platform.MaxKbps);
        if (cap < p.CapKbps) reasons.Add($"Capped at {platform.Name}'s {platform.MaxKbps} kbps limit.");

        // Never plan more than ~75% of a measured upload (audio + overhead + other devices on the network).
        if (s.UploadKbps > 0)
        {
            int safe = (int)(s.UploadKbps * 0.75) - s.AudioBitrateKbps;
            if (safe < cap)
            {
                cap = Math.Max(800, safe);
                reasons.Add($"Limited to {cap} kbps to fit your {s.UploadKbps:N0} kbps upload.");
            }
        }

        var (encoder, tier) = PickEncoder(s.Encoder, available, cpuThreads, reasons);
        // the game runs on the same CPU: light presets (x264 "veryfast" keeps ~90% of "medium" quality at ~1/3 the CPU)
        string preset = cpuThreads >= 16 ? "faster" : cpuThreads >= 8 ? "veryfast" : "superfast";
        bool cbr = s.RateControl == "Cbr";
        reasons.Add(cbr
            ? "Constant bitrate."
            : "Smart VBR: static scenes use less bandwidth.");

        return new EncodePlan(Even(p.W), Even(p.H), p.Fps, cap, p.Quality, encoder, tier, cbr, s.LowLatencyMode,
            preset, p.Name, reasons);
    }

    private static Profile PickAuto(Profile[] table, StudioSettings s, PlatformPreset platform, List<string> reasons)
    {
        if (s.UploadKbps <= 0)
        {
            reasons.Add("Auto: no upload speed set, using Balanced. Run the connection test to tune it.");
            return table[1] with { Name = "Auto (Balanced)" };
        }
        int budget = Math.Min(platform.MaxKbps, (int)(s.UploadKbps * 0.75) - s.AudioBitrateKbps);
        var best = table.LastOrDefault(t => t.CapKbps <= budget);
        if (best is null)
        {
            // Very slow upload: drop to 540p rather than streaming a blocky 720p.
            var low = budget < 1800
                ? new Profile("Auto (Low)", 960, 540, 30, Math.Max(800, budget), 24)
                : table[0] with { CapKbps = budget };
            reasons.Add($"Auto: {low.W}×{low.H} for {s.UploadKbps:N0} kbps upload.");
            return low with { Name = "Auto (" + (budget < 1800 ? "Low" : "Data saver") + ")" };
        }
        reasons.Add($"Auto: {best.Name} ({best.W}×{best.H}) for {s.UploadKbps:N0} kbps upload.");
        return best with { Name = $"Auto ({best.Name})" };
    }

    public static (string, EncoderTier) PickEncoder(string requested, IReadOnlyList<ProbedEncoder> available, int cpuThreads, List<string> reasons)
    {
        if (requested != "Auto")
        {
            var hit = available.FirstOrDefault(e => e.Name == requested);
            if (hit is not null) return (hit.Name, hit.Tier);
            reasons.Add($"{EncoderCatalog.DisplayName(requested)} isn't available. Using automatic.");
        }

        // Streaming and playing on one PC: a GPU's media engine encodes without touching the CPU the game needs,
        // so any modern hardware encoder wins (NVIDIA → Intel Quick Sync → AMD).
        foreach (var name in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            var hw = available.FirstOrDefault(e => e.Name == name && e.Tier == EncoderTier.Rich);
            if (hw is null) continue;
            reasons.Add($"Encoder: {EncoderCatalog.DisplayName(name)} (GPU).");
            return (hw.Name, hw.Tier);
        }
        var x264 = available.FirstOrDefault(e => e.Name == "libx264");
        // Older hardware encoders look noticeably worse; with a big CPU, x264 is the better trade.
        if (x264 is not null && cpuThreads >= 16)
        {
            reasons.Add($"Encoder: x264 (CPU, {cpuThreads} threads). The GPU encoder is an older generation.");
            return (x264.Name, x264.Tier);
        }
        foreach (var name in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            var hw = available.FirstOrDefault(e => e.Name == name);
            if (hw is not null)
            {
                reasons.Add($"Encoder: {EncoderCatalog.DisplayName(name)}.");
                return (hw.Name, hw.Tier);
            }
        }
        if (x264 is not null)
        {
            reasons.Add("No GPU encoder found. Using x264 (CPU).");
            return (x264.Name, x264.Tier);
        }
        reasons.Add("Detecting encoders…");
        return ("libx264", EncoderTier.Basic);
    }

    public static (int w, int h) ParseResolution(string? text) =>
        TryParseResolution(text, out var w, out var h) ? (w, h) : (1920, 1080);

    public static bool TryParseResolution(string? text, out int w, out int h)
    {
        w = h = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.ToLowerInvariant().Replace("×", "x").Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var pw) && int.TryParse(parts[1], out var ph)
            && pw is >= 320 and <= 7680 && ph is >= 180 and <= 4320)
        {
            w = Even(pw); h = Even(ph);
            return true;
        }
        return false;
    }

    public static int Even(int v) => v % 2 == 0 ? v : v - 1;
}

/// <summary>Every encode the engine runs for one set of outputs.</summary>
public sealed record PlanSet(EncodePlan Main, EncodePlan? Vertical, IReadOnlyDictionary<Guid, EncodePlan> Own)
{
    /// <summary>Total upload (video + audio) if every destination in the pool is live.</summary>
    public int UploadKbps { get; init; }
}

/// <summary>
/// Splits the measured upload between multistream destinations. A shared canvas encode is uploaded once per
/// destination that uses it, so 3 destinations of 4,000 kbps need 12,000 kbps of upload even though only one encode runs.
/// </summary>
/// <summary>
/// Multistream bitrates, the Aitum Multistream way: every destination that shares an encode gets that encode's
/// <b>full</b> bitrate (nothing is divided between destinations); the vertical canvas has its own encode and bitrate;
/// an output set to its own bitrate gets its own encode at exactly that bitrate. The only limit applied to a shared
/// encode is the strictest platform maximum among the destinations sharing it (e.g. Twitch's 6000 kbps when also
/// sending to YouTube). If everything together needs more upload than is safe, it says so instead of quietly
/// lowering the quality.
/// <para>
/// Extra destinations never cost encoding work: every destination on a shared encode only copies the finished
/// stream (a tiny remux per platform). Only two things add an encode: the vertical canvas (a different picture) and
/// an output the user explicitly set to its own bitrate.
/// </para>
/// </summary>
public static class OutputBudget
{
    /// <summary>Automatic vertical bitrate: this share of the main bitrate per pixel, roughly.</summary>
    public const double VerticalShare = 0.6;

    /// <summary>
    /// Outputs with their own bitrate that ride the main encode anyway. When no destination uses the main bitrate,
    /// the main encode would only feed recording, replay and voice clips, so the largest group of horizontal outputs
    /// with the same own bitrate becomes the main encode instead: one encode serves them all (and clips are exactly
    /// what viewers saw), rather than one encode for the cache plus one per bitrate.
    /// </summary>
    public static (int Kbps, HashSet<Guid> Ids) MainRiders(IReadOnlyList<StreamOutput> pool)
    {
        if (pool.Any(o => !o.IsVertical && o.OwnBitrateKbps <= 0)) return (0, new HashSet<Guid>());
        var group = pool.Where(o => !o.IsVertical && o.OwnBitrateKbps > 0).GroupBy(o => o.OwnBitrateKbps)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).FirstOrDefault();
        return group is null ? (0, new HashSet<Guid>()) : (group.Key, group.Select(o => o.Id).ToHashSet());
    }

    public static PlanSet Resolve(StudioSettings s, IReadOnlyList<StreamOutput> pool, bool verticalCanvas,
        IReadOnlyList<ProbedEncoder> encoders, int cpuThreads, double scale = 1.0)
    {
        // with the 9:16 canvas on its own there is one picture, so "vertical" destinations are just destinations
        bool only = s.VerticalOnly && verticalCanvas;
        var hShared = pool.Where(o => (only || !o.IsVertical) && o.OwnBitrateKbps <= 0).ToList();
        var vShared = verticalCanvas && !only ? pool.Where(o => o.IsVertical && o.OwnBitrateKbps <= 0).ToList() : new List<StreamOutput>();
        var riders = MainRiders(pool);
        var riding = pool.Where(o => riders.Ids.Contains(o.Id)).ToList();
        var own = pool.Where(o => o.OwnBitrateKbps > 0 && (only || !o.IsVertical || verticalCanvas) && !riders.Ids.Contains(o.Id)).ToList();
        int audio = Math.Clamp(s.AudioBitrateKbps, 64, 320);

        // The strictest horizontal service sets the main cap (e.g. Twitch 6000 while also on YouTube).
        string platform = hShared.Count > 0
            ? hShared.OrderBy(o => Platforms.Get(o.Service).MaxKbps).First().Service
            : (s.Outputs.FirstOrDefault(o => only || !o.IsVertical)?.Service ?? s.Platform);
        var tuned = s.Clone();
        tuned.Platform = platform;
        var main = SmartTuner.Resolve(tuned, encoders, cpuThreads);
        if (only)
        {
            var (ow, oh) = LayoutMath.VerticalSize(s.VerticalResolution);
            main = main with { Width = ow, Height = oh,
                Reasons = main.Reasons.Append($"9:16 only: the whole show is {ow}x{oh}.").ToList() };
        }
        if (riding.Count > 0)
        {
            main = main with { CapKbps = Math.Clamp(riders.Kbps, 500, 50000) };
            main = main with
            {
                Reasons = main.Reasons.Append(riding.Count > 1
                    ? $"{string.Join(" + ", riding.Select(o => o.Name))}: one shared encode, {riders.Kbps} kbps each."
                    : $"{riding[0].Name}: {riders.Kbps} kbps.").ToList(),
            };
        }
        if (hShared.Count > 1)
            main = main with { Reasons = main.Reasons.Append($"{string.Join(" + ", hShared.Select(o => o.Name))}: one shared encode, {main.CapKbps} kbps each.").ToList() };
        if (scale < 0.999)
        {
            int cap = Math.Max(1000, (int)(main.CapKbps * scale));
            main = main with { CapKbps = cap, Reasons = main.Reasons.Append($"Lowered to {cap} kbps: upload congestion.").ToList() };
        }

        EncodePlan? vertical = null;
        if (verticalCanvas && !only)
        {
            var (vw, vh) = LayoutMath.VerticalSize(s.VerticalResolution);
            int platformCap = vShared.Count > 0 ? vShared.Min(o => Platforms.Get(o.Service).MaxKbps) : 6000;
            int cap;
            string why;
            if (s.VerticalBitrateKbps > 0)
            {
                cap = Math.Clamp(s.VerticalBitrateKbps, 500, platformCap);
                why = $"Vertical {vw}×{vh}: {cap} kbps.";
            }
            else
            {
                double pixels = vw * (double)vh / (main.Width * (double)main.Height);
                cap = (int)Math.Clamp(main.CapKbps * pixels * 1.1, 1500, platformCap);
                why = $"Vertical {vw}×{vh}: up to {cap} kbps (automatic).";
            }
            if (scale < 0.999) cap = Math.Max(800, (int)(cap * scale));
            vertical = main with { Width = vw, Height = vh, CapKbps = cap, ProfileName = "Vertical", Reasons = new[] { why } };
        }

        // outputs with the same own bitrate (and canvas) share one encode
        var ownPlans = new Dictionary<Guid, EncodePlan>();
        foreach (var group in own.GroupBy(o => (o.OwnBitrateKbps, o.IsVertical)))
        {
            var names = group.Select(o => o.Name).ToList();
            var basePlan = group.Key.IsVertical && vertical is not null ? vertical : main;
            var plan = basePlan with
            {
                CapKbps = Math.Clamp(group.Key.OwnBitrateKbps, 500, 50000),
                Reasons = new[] { names.Count > 1
                    ? $"{string.Join(" + ", names)}: one shared encode, {group.Key.OwnBitrateKbps} kbps each."
                    : $"{names[0]}: separate encode, {group.Key.OwnBitrateKbps} kbps." },
            };
            foreach (var o in group) ownPlans[o.Id] = plan;
        }

        int upload = (hShared.Count + riding.Count) * (main.CapKbps + audio) + vShared.Count * ((vertical?.CapKbps ?? 0) + audio)
                     + own.Sum(o => o.OwnBitrateKbps + audio);
        if (s.UploadKbps > 0 && upload > s.UploadKbps * 0.75 && hShared.Count + riding.Count + vShared.Count + own.Count > 1)
            main = main with
            {
                Reasons = main.Reasons.Append($"Heads-up: {upload:N0} kbps total exceeds 75% of your {s.UploadKbps:N0} kbps upload. Lower the bitrate or use fewer destinations.").ToList(),
            };
        return new PlanSet(main, vertical, ownPlans) { UploadKbps = upload };
    }
}
