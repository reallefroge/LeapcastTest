using System.Globalization;

namespace LeapcastStudioAI;

/// <summary>Auto-config: proposes settings from the game's real size, the upload, the encoders and the priority,
/// plus lighter fallbacks to try when the stress test shows the PC can't keep up.</summary>
public static class AutoConfig
{
    /// <param name="UploadKbps">Upload speed in kbps (0 = unknown).</param>
    /// <param name="Destinations">Horizontal destinations sharing the main encode (each gets its full bitrate).</param>
    /// <param name="VerticalKbps">Upload the vertical canvas needs (0 = none).</param>
    public sealed record Input(string Content, bool PreferQuality, int UploadKbps, (int W, int H)? GameSize,
        IReadOnlyList<ProbedEncoder> Encoders, int CpuThreads, int Destinations, int AudioKbps = 160, int VerticalKbps = 0);

    public sealed record Candidate(int Width, int Height, int Fps, int CapKbps, string Encoder, IReadOnlyList<string> Reasons)
    {
        public string Label => $"{Width}×{Height} · {Fps} FPS · {EncoderCatalog.DisplayName(Encoder)} · up to {CapKbps} kbps";

        public StudioSettings Apply(StudioSettings current, string content)
        {
            var s = current.Clone();
            s.ContentType = content;
            s.QualityProfile = "Custom";
            s.Resolution = $"{Width} × {Height}";
            s.FrameRate = Fps;
            s.MaxBitrateKbps = CapKbps;
            s.Encoder = Encoder;
            s.RateControl = "SmartVbr";
            return s;
        }
    }

    private static readonly (int W, int H)[] Sizes = { (1920, 1080), (1600, 900), (1280, 720), (960, 540) };

    /// <summary>Best first; each next one is lighter (lower frame rate first, then resolution).</summary>
    public static List<Candidate> Ladder(Input i)
    {
        var reasons = new List<string>();
        var (encoder, _) = SmartTuner.PickEncoder("Auto", i.Encoders, i.CpuThreads, reasons);
        bool hw = encoder != "libx264";
        bool high = i.Content == "HighMotion";

        // never stream more pixels than the game actually draws (upscaling only spends bandwidth)
        int maxH = 1080;
        if (i.GameSize is { } g && g.W > 0 && g.H > 0)
        {
            int content = (int)Math.Min(g.H, g.W * 9.0 / 16); // its 16:9 part
            maxH = Math.Max(540, (int)(content * 1.1));
            reasons.Add($"Your game picture is {g.W}×{g.H}: streaming above ~{Math.Min(1080, maxH / 1.1):0}p would only upscale it.");
        }
        if (!i.PreferQuality) maxH = Math.Min(maxH, 900);
        if (!hw && i.CpuThreads < 12) maxH = Math.Min(maxH, 720); // CPU encoding next to the game: keep it light

        // per-destination upload share (75% of the upload is the safe ceiling)
        int dests = Math.Max(1, i.Destinations);
        // Every destination receives the full bitrate, so the best bitrate is what fits all of them together in
        // 75% of the upload (the rest is headroom for audio, overhead and the rest of the household).
        int safe = (int)(i.UploadKbps * 0.75) - i.VerticalKbps - (i.VerticalKbps > 0 ? i.AudioKbps : 0);
        int budget = i.UploadKbps > 0 ? safe / dests - i.AudioKbps : 6000;
        if (i.UploadKbps > 0)
            reasons.Add($"{i.UploadKbps:N0} kbps upload: {dests} destination{(dests == 1 ? "" : "s")}" +
                        (i.VerticalKbps > 0 ? $" plus the vertical canvas ({i.VerticalKbps:N0} kbps)" : "") +
                        $" fit at up to {Math.Max(800, budget):N0} kbps each, within 75% of it.");

        int fps = high && hw && budget >= 5000 ? 60 : 30;
        if (!high) reasons.Add("Low-motion game: 30 FPS looks the same and halves the data.");

        var ladder = new List<Candidate>();
        var fpsSteps = fps == 60 ? new[] { 60, 30 } : new[] { 30 };
        foreach (var (w, h) in Sizes.Where(z => z.H <= maxH).DefaultIfEmpty((960, 540)))
            foreach (var f in fpsSteps)
            {
                int need = BitrateFor(h, f, high);
                int cap = Math.Clamp(Math.Min(need, Math.Min(budget, 6000)), 800, 6000);
                // too little bitrate for this size looks worse than the next size down at the same bitrate
                if (cap < need * 0.6 && h > 540) continue;
                ladder.Add(new Candidate(w, h, f, cap, encoder, reasons));
            }
        if (ladder.Count == 0) ladder.Add(new Candidate(960, 540, 30, Math.Clamp(budget, 800, 2000), encoder, reasons));
        // order: best first, then frame rate before resolution
        return ladder.OrderByDescending(c => c.Height).ThenByDescending(c => c.Fps).ToList();
    }

    /// <summary>Smart-VBR cap that looks clean for this size/rate (static scenes use far less).</summary>
    public static int BitrateFor(int height, int fps, bool highMotion)
    {
        double b = height switch { >= 1080 => 5000, >= 900 => 4000, >= 720 => 3000, _ => 1800 };
        if (highMotion) b *= 1.3;
        if (fps > 30) b *= 1.25;
        return (int)Math.Round(b / 100) * 100;
    }
}
