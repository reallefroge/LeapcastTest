using System.Globalization;
using LeapcastStudioAI;

namespace CoreTests;

/// <summary>The stream timer: the counting itself, and the card drawn on the scene (which must tick without a rebuild).</summary>
public static class TimerTests
{
    public static async Task RunAsync(string ffmpeg, string dir, Action<bool, string> check)
    {
        Directory.CreateDirectory(dir);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // ---- counting, pausing and picking up where it left off
        var t = new StreamTimerState { Mode = TimerMode.CountDown, DurationSeconds = 600 };
        t.Start(t0);
        string at90 = t.Display(t0.AddSeconds(90), DateTime.Now);
        t.Pause(t0.AddSeconds(90));
        string paused = t.Display(t0.AddSeconds(300), DateTime.Now);   // time passes, the clock doesn't
        t.Start(t0.AddSeconds(300));
        string resumed = t.Display(t0.AddSeconds(310), DateTime.Now);
        check(at90 == "08:30" && paused == "08:30" && resumed == "08:20",
            $"timer: counts down, holds while paused and picks up again ({at90} → paused {paused} → {resumed})");

        var up = new StreamTimerState { Mode = TimerMode.CountUp };
        up.Start(t0);
        check(up.Display(t0.AddSeconds(3671), DateTime.Now) == "1:01:11", "…counting up rolls into hours (1:01:11)");

        // ---- the end of a countdown: the finish word, or overtime
        var fin = new StreamTimerState { Mode = TimerMode.CountDown, DurationSeconds = 5, FinishedText = "TIME!" };
        fin.Start(t0);
        string done = fin.Display(t0.AddSeconds(9), DateTime.Now);
        var over = new StreamTimerState { Mode = TimerMode.CountDown, DurationSeconds = 5, Overtime = true };
        over.Start(t0);
        string ot = over.Display(t0.AddSeconds(9), DateTime.Now);
        check(done == "TIME!" && ot == "-00:04", $"…at zero it says the finish word, or runs into overtime ({done} / {ot})");

        // ---- adding time mid-run (the usual subathon button)
        var sub = new StreamTimerState { Mode = TimerMode.CountDown, DurationSeconds = 600 };
        sub.Start(t0);
        sub.Add(300, t0.AddSeconds(60));
        check(sub.Display(t0.AddSeconds(60), DateTime.Now) == "14:00", "…+5 minutes while it runs adds to what's left (14:00)");

        // ---- typed lengths
        var want = new (string In, double Secs)[] { ("1:30:00", 5400), ("25m", 1500), ("90", 90), ("1h30", 5400), ("09:58", 598) };
        var bad = want.Where(w => !StreamTimerState.TryParseDuration(w.In, out double got) || Math.Abs(got - w.Secs) > 0.01).ToList();
        check(bad.Count == 0 && !StreamTimerState.TryParseDuration("soon", out _),
            $"…lengths typed as 1:30:00, 25m, 90 or 1h30 are understood ({bad.Count} wrong)");

        // ---- the card is sized for the longest reading, so it never jumps about
        var face = TimerCard.Face();
        if (face is not { } f) { check(false, "timer: no fixed-width font on this machine"); return; }
        var hour = new StreamTimerState { Mode = TimerMode.CountDown, DurationSeconds = 3600 };
        var mins = new StreamTimerState { Mode = TimerMode.CountDown, DurationSeconds = 600 };
        var wide = TimerCard.Measure(TimerCard.Stacked, "Subathon", hour.Widest(t0), 60, f.Advance);
        var narrow = TimerCard.Measure(TimerCard.Stacked, "Subathon", mins.Widest(t0), 60, f.Advance);
        check(hour.Widest(t0) == "1:00:00" && mins.Widest(t0) == "10:00" && wide.W > narrow.W,
            $"timer card: sized for the longest reading it can show ({narrow.W}px for minutes, {wide.W}px once there are hours)");

        PiecesMatchTheCard(check, f.Advance);

        // ---- it is drawn, see-through outside the plate, and the reading can be read back
        string file = Path.Combine(dir, "t.txt");
        TimerCard.Write(file, "1:23:45");
        var L = TimerCard.Measure(TimerCard.Stacked, "Subathon", "1:23:45", 64, f.Advance);
        string chain = TimerCard.Filter(L, TimerCard.Stacked, "Subathon", f.Path, file);
        string card = Path.Combine(dir, "card.png");
        var (code, log) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-y", "-v", "error", "-f", "lavfi", "-i",
            $"color=c=0x000000@0.0:s={L.W}x{L.H}:r=1", "-vf", chain, "-frames:v", "1", card }, TimeSpan.FromSeconds(60), CancellationToken.None);
        var (corner, middle, ink) = await ProbeAsync(ffmpeg, card, L);
        check(code == 0 && corner < 40 && middle > 150 && ink > 0.02,
            $"timer card: drawn with see-through corners and a readable plate (corner alpha {corner}, plate alpha {middle}, {ink:P1} lit) {log.Trim()}");

        // ---- the clock ticks while the pipeline runs: drawtext re-reads the file, nothing is rebuilt
        string live = Path.Combine(dir, "live.txt");
        TimerCard.Write(live, "00:09");
        string liveChain = TimerCard.Filter(L, TimerCard.Stacked, "Subathon", f.Path, live);
        string frames = Path.Combine(dir, "f_%03d.png");
        var writer = Task.Run(async () =>
        {
            foreach (var v in new[] { "00:08", "00:07", "00:06", "00:05" })
            { await Task.Delay(400); TimerCard.Write(live, v); }
        });
        var (lcode, llog) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-y", "-v", "error", "-re", "-f", "lavfi", "-i",
            $"color=c=0x000000@0.0:s={L.W}x{L.H}:r=5", "-vf", liveChain, "-frames:v", "10", frames },
            TimeSpan.FromSeconds(60), CancellationToken.None);
        await writer;
        var shots = Directory.GetFiles(dir, "f_*.png").OrderBy(x => x).ToList();
        int changed = shots.Count > 1 ? shots.Skip(1).Count(p => !SameBytes(shots[0], p)) : 0;
        check(lcode == 0 && changed > 0,
            $"timer: the reading changes on screen while the pipeline keeps running ({changed} of {Math.Max(0, shots.Count - 1)} later frames differ) {llog.Trim()}");

        // ---- on a scene: the card lands where it was anchored, and is the only thing added
        var scene = new StudioScene { Name = "Live" };
        var timerSrc = new StudioSource { Type = SourceTypes.Timer, Name = "Timer", TimerTitle = "Subathon",
            TimerStyle = TimerCard.Stacked, FontSize = 56, Anchor = Anchors.TopRight, MarginPx = 28 };
        scene.Sources.Add(timerSrc);
        var plan = new EncodePlan(1280, 720, 30, 4000, 22, "libx264", EncoderTier.Rich, false, false, "veryfast", "t", Array.Empty<string>());
        double fs = 56 * 720 / 1080.0;
        var sl = TimerCard.Measure(TimerCard.Stacked, "Subathon", "1:00:00", fs, f.Advance);
        string sceneFile = Path.Combine(dir, "scene.txt");
        TimerCard.Write(sceneFile, "1:00:00");
        var spec = PipelineBuilder.Build(new PipelineRequest
        {
            Scenes = new[] { scene }, ActiveSceneIndex = 0, Preview = false, Plan = plan, IsWindows = true,
            KnownSizes = new Dictionary<string, (int, int)> { [PipelineBuilder.InputKey(timerSrc)] = (sl.W, sl.H) },
            TimerFilters = new Dictionary<Guid, string>
                { [timerSrc.Id] = TimerCard.Filter(sl, TimerCard.Stacked, "Subathon", f.Path, sceneFile) },
        });
        var rect = LayoutMath.Horizontal(timerSrc, 1280, 720, sl.W / (double)sl.H, (sl.W, sl.H));
        bool placed = Math.Abs(rect.X + rect.W - (1280 - 28)) < 2 && Math.Abs(rect.Y - 28) < 2
                      && Math.Abs(rect.W - sl.W) < 2 && Math.Abs(rect.H - sl.H) < 2;
        bool drawn = spec.FilterGraph.Contains("reload=1") && spec.FilterGraph.Contains("drawbox")
                     && !spec.Warnings.Any(w => w.Contains("Timer", StringComparison.OrdinalIgnoreCase));
        check(placed && drawn,
            $"timer on a scene: sized {sl.W}x{sl.H} and anchored top-right at the 28px margin, re-read every frame " +
            $"[rect {rect.X:0},{rect.Y:0} {rect.W:0}x{rect.H:0}; shown {timerSrc.Shown}; reload {spec.FilterGraph.Contains("reload=1")}; " +
            $"warnings: {string.Join("; ", spec.Warnings)}]");
    }

    /// <summary>
    /// The pieces handed to the in-process renderer must describe the same card the FFmpeg chain draws:
    /// both come from one Measure, so this catches the two drifting apart.
    /// </summary>
    public static void PiecesMatchTheCard(Action<bool, string> check, double advance)
    {
        var bad = new List<string>();
        foreach (var (style, title, widest, fs) in new[] {
            (TimerCard.Stacked, "Subathon", "1:23:45", 64.0),
            (TimerCard.Inline, "Break ends in", "09:58", 54.0),
            (TimerCard.Plain, "", "2:00:00", 60.0) })
        {
            var L = TimerCard.Measure(style, title, widest, fs, advance);
            var ps = TimerCard.Pieces(L, style, title, widest);
            bool plate = ps.Any(x => x.Kind == TimerCard.PlateKind);
            if (plate != (style != TimerCard.Plain)) bad.Add($"{style}: plate {plate}");
            if (plate && ps.First(x => x.Kind == TimerCard.PlateKind) is { } pl
                && (pl.W != L.W || pl.H != L.H)) bad.Add($"{style}: plate {pl.W}x{pl.H} not {L.W}x{L.H}");
            var time = ps.LastOrDefault(x => x.Kind == TimerCard.TextKind && x.Text == widest);
            if (time is null) { bad.Add($"{style}: no reading"); continue; }
            if (Math.Abs(time.Size - L.TimeSize) > 0.01) bad.Add($"{style}: reading at {time.Size} not {L.TimeSize}");
            // where the text lands, worked out the way each renderer would: it has to sit inside the card
            double w = widest.Length * advance * time.Size * 1.06;
            double x0 = time.AlignX switch
            {
                TimerCard.Anchor.Middle => time.X - w / 2,
                TimerCard.Anchor.End => time.X - w,
                _ => time.X,
            };
            if (x0 < -1 || x0 + w > L.W + 1) bad.Add($"{style}: reading spans {x0:0}..{x0 + w:0} of {L.W}");
            double y1 = (time.AlignY == TimerCard.Anchor.Middle ? time.Y + time.Size * 0.6 : time.Y + time.Size * 1.2);
            if (time.Y < -1 || y1 > L.H + 2) bad.Add($"{style}: reading ends at y {y1:0} of {L.H}");
            foreach (var x in ps.Where(x => x.Kind != TimerCard.TextKind))
                if (x.X < 0 || x.Y < 0 || x.X + x.W > L.W + 0.5 || x.Y + x.H > L.H + 0.5)
                    bad.Add($"{style}: {x.Kind} outside the card");
        }
        check(bad.Count == 0, $"timer card: what the GPU canvas draws matches what FFmpeg draws{(bad.Count == 0 ? "" : ": " + string.Join("; ", bad))}");
    }

    private static bool SameBytes(string a, string b) => File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));

    /// <summary>A picture as raw RGBA bytes (ffmpeg decodes it, so the tests need no image library).</summary>
    private static async Task<(byte[] Px, int W, int H)> RgbaAsync(string ffmpeg, string png, int w, int h)
    {
        string raw = png + ".rgba";
        await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-y", "-v", "error", "-i", png, "-pix_fmt", "rgba",
            "-f", "rawvideo", raw }, TimeSpan.FromSeconds(30), CancellationToken.None);
        return (File.ReadAllBytes(raw), w, h);
    }

    /// <summary>Alpha in a corner, alpha in the middle, and how much of the card is lit text.</summary>
    private static async Task<(int Corner, int Middle, double Ink)> ProbeAsync(string ffmpeg, string png, TimerCard.Layout L)
    {
        var (px, w, h) = await RgbaAsync(ffmpeg, png, L.W, L.H);
        int At(int x, int y, int c) => px[(y * w + x) * 4 + c];
        long lit = 0, total = (long)w * h;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i2 = (y * w + x) * 4;
                if (px[i2 + 3] > 120 && px[i2] + px[i2 + 1] + px[i2 + 2] > 330) lit++;
            }
        return (At(1, 1, 3), At(w / 2, h / 2, 3), total == 0 ? 0 : lit / (double)total);
    }

    /// <summary>The box around everything brighter than the blank canvas.</summary>
    private static async Task<(int X0, int Y0, int X1, int Y1)?> LitAsync(string ffmpeg, string png, int w, int h)
    {
        var (px, _, _) = await RgbaAsync(ffmpeg, png, w, h);
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i2 = (y * w + x) * 4;
                if (px[i2] + px[i2 + 1] + px[i2 + 2] > 60)
                { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            }
        return x1 < 0 ? null : (x0, y0, x1, y1);
    }
}
