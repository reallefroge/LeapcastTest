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
            reasons.Add("Low-motion game: 30 FPS keeps Wizard101 smooth and uses about half the data of 60 FPS.");

        int cap = Math.Min(p.CapKbps, platform.MaxKbps);
        if (cap < p.CapKbps) reasons.Add($"Capped at {platform.Name}'s {platform.MaxKbps} kbps limit.");

        // Never plan more than ~75% of a measured upload (audio + overhead + other devices on the network).
        if (s.UploadMbps > 0)
        {
            int safe = (int)(s.UploadMbps * 1000 * 0.75) - s.AudioBitrateKbps;
            if (safe < cap)
            {
                cap = Math.Max(800, safe);
                reasons.Add($"Bitrate held to {cap} kbps so it fits inside your measured {s.UploadMbps:0.#} Mbps upload.");
            }
        }

        var (encoder, tier) = PickEncoder(s.Encoder, available, cpuThreads, reasons);
        // the game runs on the same CPU: light presets (x264 "veryfast" keeps ~90% of "medium" quality at ~1/3 the CPU)
        string preset = cpuThreads >= 16 ? "faster" : cpuThreads >= 8 ? "veryfast" : "superfast";
        bool cbr = s.RateControl == "Cbr";
        reasons.Add(cbr
            ? "Constant bitrate: always sends the full cap, even on static screens."
            : "Smart VBR: static screens (menus, waiting for your turn) use far less upload; battles can use up to the cap.");

        return new EncodePlan(Even(p.W), Even(p.H), p.Fps, cap, p.Quality, encoder, tier, cbr, s.LowLatencyMode,
            preset, p.Name, reasons);
    }

    private static Profile PickAuto(Profile[] table, StudioSettings s, PlatformPreset platform, List<string> reasons)
    {
        if (s.UploadMbps <= 0)
        {
            reasons.Add("Auto: no connection test yet, using Balanced. Run \"Test connection\" in Settings to tune it.");
            return table[1] with { Name = "Auto (Balanced)" };
        }
        int budget = Math.Min(platform.MaxKbps, (int)(s.UploadMbps * 1000 * 0.75) - s.AudioBitrateKbps);
        var best = table.LastOrDefault(t => t.CapKbps <= budget);
        if (best is null)
        {
            // Very slow upload: drop to 540p rather than streaming a blocky 720p.
            var low = budget < 1800
                ? new Profile("Auto (Low)", 960, 540, 30, Math.Max(800, budget), 24)
                : table[0] with { CapKbps = budget };
            reasons.Add($"Auto: picked {low.W}×{low.H} for your {s.UploadMbps:0.#} Mbps upload.");
            return low with { Name = "Auto (" + (budget < 1800 ? "Low" : "Data saver") + ")" };
        }
        reasons.Add($"Auto: picked {best.Name} ({best.W}×{best.H}) for your {s.UploadMbps:0.#} Mbps upload.");
        return best with { Name = $"Auto ({best.Name})" };
    }

    public static (string, EncoderTier) PickEncoder(string requested, IReadOnlyList<ProbedEncoder> available, int cpuThreads, List<string> reasons)
    {
        if (requested != "Auto")
        {
            var hit = available.FirstOrDefault(e => e.Name == requested);
            if (hit is not null) return (hit.Name, hit.Tier);
            reasons.Add($"{EncoderCatalog.DisplayName(requested)} isn't available on this PC, choosing automatically.");
        }

        // Streaming and playing on one PC: a GPU's media engine encodes without touching the CPU the game needs,
        // so any modern hardware encoder wins (NVIDIA → Intel Quick Sync → AMD).
        foreach (var name in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            var hw = available.FirstOrDefault(e => e.Name == name && e.Tier == EncoderTier.Rich);
            if (hw is null) continue;
            reasons.Add($"Using {EncoderCatalog.DisplayName(name)}: the GPU encodes, so the game keeps your CPU.");
            return (hw.Name, hw.Tier);
        }
        var x264 = available.FirstOrDefault(e => e.Name == "libx264");
        // Older hardware encoders look noticeably worse; with a big CPU, x264 is the better trade.
        if (x264 is not null && cpuThreads >= 16)
        {
            reasons.Add($"Using x264 on your {cpuThreads}-thread CPU: your GPU's encoder is an older generation.");
            return (x264.Name, x264.Tier);
        }
        foreach (var name in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            var hw = available.FirstOrDefault(e => e.Name == name);
            if (hw is not null)
            {
                reasons.Add($"Using {EncoderCatalog.DisplayName(name)} so the game keeps your CPU.");
                return (hw.Name, hw.Tier);
            }
        }
        if (x264 is not null)
        {
            reasons.Add("No hardware encoder found, using x264 (CPU) with a light preset.");
            return (x264.Name, x264.Tier);
        }
        reasons.Add("Encoder detection hasn't finished; assuming x264.");
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
/// destination that uses it, so 3 destinations of 4 Mbps need 12 Mbps of upload even though only one encode runs.
/// </summary>
public static class OutputBudget
{
    /// <summary>A vertical (9:16 720p) destination gets about this share of the main destination's bitrate.</summary>
    public const double VerticalShare = 0.6;

    public static PlanSet Resolve(StudioSettings s, IReadOnlyList<StreamOutput> pool, bool verticalCanvas,
        IReadOnlyList<ProbedEncoder> encoders, int cpuThreads, double scale = 1.0)
    {
        var hShared = pool.Where(o => !o.IsVertical && o.OwnBitrateKbps <= 0).ToList();
        var vShared = verticalCanvas ? pool.Where(o => o.IsVertical && o.OwnBitrateKbps <= 0).ToList() : new List<StreamOutput>();
        var own = pool.Where(o => o.OwnBitrateKbps > 0 && (!o.IsVertical || verticalCanvas)).ToList();
        int audio = Math.Clamp(s.AudioBitrateKbps, 64, 320);

        // The strictest horizontal service sets the main cap (e.g. Twitch 6000 while also on YouTube).
        string platform = hShared.Count > 0
            ? hShared.OrderBy(o => Platforms.Get(o.Service).MaxKbps).First().Service
            : (s.Outputs.FirstOrDefault(o => !o.IsVertical)?.Service ?? s.Platform);
        var tuned = s.Clone();
        tuned.Platform = platform;

        int nh = Math.Max(1, hShared.Count), nv = vShared.Count;
        int destinations = hShared.Count + nv + own.Count;
        int perMain = int.MaxValue;
        if (s.UploadMbps > 0 && destinations > 1)
        {
            int safe = (int)(s.UploadMbps * 1000 * 0.75) - own.Sum(o => o.OwnBitrateKbps + audio);
            perMain = (int)((safe - audio * (nh + nv)) / (nh + VerticalShare * nv));
            perMain = Math.Max(800, perMain);
            // let the tuner pick the profile that fits one destination's share
            tuned.UploadMbps = (perMain + audio) / 750.0;
        }
        var main = SmartTuner.Resolve(tuned, encoders, cpuThreads);
        if (perMain != int.MaxValue)
        {
            var reasons = main.Reasons.Where(r => !r.Contains("Mbps upload", StringComparison.Ordinal)).ToList();
            reasons.Add($"Multistream: {destinations} destinations share your {s.UploadMbps:0.#} Mbps upload, so each horizontal one gets up to {main.CapKbps} kbps.");
            main = main with { Reasons = reasons };
        }
        if (scale < 0.999)
        {
            int cap = Math.Max(1000, (int)(main.CapKbps * scale));
            main = main with { CapKbps = cap, Reasons = main.Reasons.Append($"Lowered to {cap} kbps because a destination's upload was struggling.").ToList() };
        }

        EncodePlan? vertical = null;
        if (verticalCanvas)
        {
            var (vw, vh) = LayoutMath.VerticalSize(s.VerticalResolution);
            int platformCap = vShared.Count > 0 ? vShared.Min(o => Platforms.Get(o.Service).MaxKbps) : 6000;
            double pixels = vw * (double)vh / (main.Width * (double)main.Height);
            int cap = (int)Math.Clamp(main.CapKbps * pixels * 1.1, 1500, platformCap);
            if (s.UploadMbps > 0)
            {
                // whatever the horizontal destinations leave of the safe upload is shared by the vertical ones
                int safe = (int)(s.UploadMbps * 1000 * 0.75) - own.Sum(o => o.OwnBitrateKbps + audio);
                int left = safe - hShared.Count * (main.CapKbps + audio) - Math.Max(1, nv) * audio;
                cap = Math.Min(cap, Math.Max(800, left / Math.Max(1, nv)));
            }
            if (scale < 0.999) cap = Math.Max(800, (int)(cap * scale));
            vertical = main with
            {
                Width = vw, Height = vh, CapKbps = cap, ProfileName = "Vertical",
                Reasons = new[] { $"Vertical canvas {vw}×{vh} for TikTok/Shorts: up to {cap} kbps." },
            };
        }

        var ownPlans = new Dictionary<Guid, EncodePlan>();
        foreach (var o in own)
        {
            var basePlan = o.IsVertical && vertical is not null ? vertical : main;
            ownPlans[o.Id] = basePlan with
            {
                CapKbps = Math.Clamp(o.OwnBitrateKbps, 500, 50000),
                Reasons = new[] { $"{o.Name}: its own encode at up to {o.OwnBitrateKbps} kbps." },
            };
        }

        int upload = hShared.Count * (main.CapKbps + audio) + nv * ((vertical?.CapKbps ?? 0) + audio)
                     + own.Sum(o => o.OwnBitrateKbps + audio);
        return new PlanSet(main, vertical, ownPlans) { UploadKbps = upload };
    }
}
