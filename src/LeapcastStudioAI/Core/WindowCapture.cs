using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LeapcastStudioAI;

/// <summary>
/// Game window capture in its own small ffmpeg process per window, feeding the main pipeline through a
/// <see cref="CountedFramePipe"/>. Three things fall out of that:
/// <list type="bullet">
/// <item>Games recreate their window (loading screens, resolution or fullscreen changes). The capture is re-attached
///   to the new window automatically while the stream keeps running (it shows the last picture for a moment)
///   instead of freezing on a dead window or restarting the whole pipeline.</item>
/// <item>The compositor receives frames at an exact, steady rate no matter how unevenly the game presents them,
///   which is what makes motion look smooth.</item>
/// <item>Scaling/cropping the game picture happens in parallel with compositing and encoding (another process).</item>
/// </list>
/// </summary>
public sealed class WindowCaptureBridge : IDisposable
{
    private sealed class Reader
    {
        public required string Key, Title, Signature, Name;
        public required int Width, Height, Fps;
        public required string Fill;
        public bool Light;
        public required bool Cursor;
        public required (int L, int T, int R, int B) Crop;
        public Process? Proc;
        public byte[]? Latest;
        public readonly object Lock = new();
        public long Frames;
        public int Starts;
        public volatile bool Running;
        public DateTime StartedUtc, NextAttemptUtc;
        public string State = "starting";
        public bool UsedGpu;
        /// <summary>Crop + scale happen on the GPU (Direct3D 11 video processor) before the picture is downloaded.</summary>
        public bool UsedGpuScale;
        public long Hwnd;
        public (int W, int H)? Client;
        public DateTime LastSizeCheck;
        public bool Retired;
    }

    /// <summary>
    /// How to cut and size a window picture on the GPU: crop (pixels from each edge, user crop included) so its shape
    /// matches the target, then scale to (ScaleW × ScaleH). Fit leaves bars to pad on the CPU (cheap).
    /// </summary>
    public sealed record GpuCut(int L, int T, int R, int B, int ScaleW, int ScaleH, bool Pad);

    public static GpuCut PlanGpuCut(int srcW, int srcH, (int L, int T, int R, int B) userCrop, string fill, int w, int h)
    {
        int l = Math.Max(0, userCrop.L), t = Math.Max(0, userCrop.T), r = Math.Max(0, userCrop.R), b = Math.Max(0, userCrop.B);
        int cw = Math.Max(16, srcW - l - r), ch = Math.Max(16, srcH - t - b);
        double target = w / (double)h, src = cw / (double)ch;
        if (fill == Anchors.FillStretch) return new GpuCut(l, t, r, b, w, h, false);
        if (fill == Anchors.FillFit)
        {
            int fw = w, fh = SmartTuner.Even((int)Math.Round(w / src));
            if (fh > h) { fh = h; fw = SmartTuner.Even((int)Math.Round(h * src)); }
            return new GpuCut(l, t, r, b, Math.Max(16, fw), Math.Max(16, fh), fw != w || fh != h);
        }
        // crop to fill: trim the longer side so the shape matches, centred
        if (src > target + 1e-3)
        {
            int keep = (int)Math.Round(ch * target);
            int cut = cw - keep;
            l += cut / 2; r += cut - cut / 2;
        }
        else if (src < target - 1e-3)
        {
            int keep = (int)Math.Round(cw / target);
            int cut = ch - keep;
            t += cut / 2; b += cut - cut / 2;
        }
        return new GpuCut(l, t, r, b, w, h, false);
    }

    private static readonly Regex VideoSize = new(@"Stream #0:0.*?: Video: .*?\b(\d{2,5})x(\d{2,5})\b", RegexOptions.Compiled);

    private readonly Dictionary<string, Reader> _readers = new();
    private readonly List<CountedFramePipe> _pipes = new();
    private readonly object _lock = new();
    private readonly string _scratch;
    private readonly Func<string, long?> _findWindow;
    private readonly Func<long, (int W, int H)?>? _clientSize;
    private readonly CancellationTokenSource _cts = new();
    private string? _ffmpeg;

    /// <summary>GPU (Windows.Graphics.Capture) capture is available; turned off for this session if it fails.</summary>
    public bool GpuCapture { get; set; }
    /// <summary>scale_d3d11 is available: crop/scale windows on the GPU; turned off for this session if it fails.</summary>
    public bool GpuScale { get; set; }
    /// <summary>Test / non-Windows hook: the capture input for a window source instead of gfxcapture/gdigrab.</summary>
    public Func<StudioSource, InputSpec?>? DeviceOverride { get; set; }
    /// <summary>(message, isError)</summary>
    public event Action<string, bool>? Message;
    /// <summary>Real (native) size of a window's picture, once its reader has reported it. Keyed by input key.</summary>
    public Dictionary<string, (int W, int H)> CaptureSizes { get; } = new();

    public sealed record WindowFeed(string Path, int Width, int Height);

    public WindowCaptureBridge(string scratchDir, Func<string, long?> findWindow, Func<long, (int W, int H)?>? clientSize = null)
    {
        _scratch = scratchDir;
        _findWindow = findWindow;
        _clientSize = clientSize;
        new Thread(Supervise) { IsBackground = true, Name = "Leapcast window capture supervisor" }.Start();
    }

    private static bool SameShape(int w1, int h1, int w2, int h2) => Math.Abs(w1 / (double)h1 - w2 / (double)h2) < 0.03 * (w2 / (double)h2);

    /// <summary>What a window source needs from its reader for one pipeline run.</summary>
    public sealed record Need(StudioSource Source, int Width, int Height, string Fill, bool Light = false);

    /// <summary>Keeps a reader per window (restarting only those whose needs changed) and returns fresh pipes.</summary>
    public Dictionary<string, WindowFeed> Open(string ffmpeg, IReadOnlyDictionary<string, Need> needs, int fps, double origin)
    {
        _ffmpeg = ffmpeg;
        var result = new Dictionary<string, WindowFeed>();
        lock (_lock)
        {
            ClosePipesLocked();
            foreach (var key in _readers.Keys.Except(needs.Keys).ToList()) { Stop(_readers[key]); _readers.Remove(key); }
            foreach (var (key, need) in needs)
            {
                var s = need.Source;
                var crop = (Math.Max(0, s.CropLeft), Math.Max(0, s.CropTop), Math.Max(0, s.CropRight), Math.Max(0, s.CropBottom));
                string what = $"{s.WindowTitle.Trim()}|{need.Fill}|{fps}|{s.CaptureCursor}|{crop}|{s.CaptureMethod}|{need.Light}";
                int nw = need.Width, nh = need.Height;
                _readers.TryGetValue(key, out var reader);
                // resized on the canvas: a reader that's already big enough keeps going (the pipeline scales it
                // down), so nothing restarts and nothing flickers; one that's too small grows with room to spare
                if (reader is not null && !reader.Retired && reader.Signature.EndsWith("|" + what, StringComparison.Ordinal)
                    && need.Fill == Anchors.FillFit && SameShape(reader.Width, reader.Height, nw, nh))
                {
                    if (reader.Width >= nw && reader.Width <= nw * 1.6) { nw = reader.Width; nh = reader.Height; }
                    else if (reader.Width < nw)
                    {
                        int gw = SmartTuner.Even((int)Math.Min(3840, Math.Round(nw * 1.35)));
                        nh = SmartTuner.Even((int)Math.Round(gw * (double)nh / nw)); nw = gw;
                    }
                }
                string sig = $"{nw}x{nh}|{what}";
                byte[]? carry = null;
                if (reader is null || reader.Signature != sig)
                {
                    if (reader is not null)
                    {
                        // until the new reader's first picture: the last one, resized (never a black flash)
                        lock (reader.Lock) if (reader.Latest is { } last && last.Length == reader.Width * reader.Height * 3 / 2) carry = Frames.ResizeI420(last, reader.Width, reader.Height, nw, nh);
                        Stop(reader);
                    }
                    reader = new Reader
                    {
                        Key = key, Title = s.WindowTitle.Trim(), Name = s.Name, Signature = sig, Width = nw, Height = nh,
                        Fill = need.Fill, Fps = fps, Cursor = s.CaptureCursor, Crop = crop, Light = need.Light,
                    };
                    reader.UsedGpu = GpuCapture && !string.Equals(s.CaptureMethod, "GDI", StringComparison.OrdinalIgnoreCase);
                    _readers[key] = reader;
                    TryStart(reader);
                }
                int bytes = nw * nh * 3 / 2;
                // what the pipe sends before the reader's next picture: its last one (a rebuild shows the game, not black)
                byte[]? now;
                lock (reader.Lock) now = reader.Latest is { } l && l.Length == bytes ? l : null;
                var blank = carry ?? now ?? Frames.BlankI420(nw, nh);
                var r = reader;
                var pipe = new CountedFramePipe(_scratch, bytes, fps, origin, () => { lock (r.Lock) return r.Latest; }, blank);
                _pipes.Add(pipe);
                result[key] = new WindowFeed(pipe.Path, nw, nh);
            }
        }
        return result;
    }

    private void TryStart(Reader r)
    {
        var ffmpeg = _ffmpeg;
        if (ffmpeg is null || r.Retired) return;
        var src = new StudioSource { Type = SourceTypes.Window, WindowTitle = r.Title, CaptureCursor = r.Cursor };
        InputSpec? input = DeviceOverride?.Invoke(src);
        GpuCut? gpuCut = null;
        if (input is null)
        {
            if (r.UsedGpu)
            {
                var hwnd = _findWindow(r.Title);
                if (hwnd is null or 0) { Wait(r, "waiting for the window"); return; }
                r.Hwnd = hwnd.Value;
                r.Client = _clientSize?.Invoke(hwnd.Value);
                r.UsedGpuScale = GpuScale && r.Client is not null;
                string cap = $"gfxcapture=hwnd={hwnd}:max_framerate={r.Fps}:capture_cursor={(r.Cursor ? 1 : 0)}";
                if (r.UsedGpuScale && r.Client is { } c)
                {
                    // crop + scale + colour conversion on the GPU: only a finished NV12 picture is downloaded
                    var cut = PlanGpuCut(c.W, c.H, r.Crop, r.Fill, r.Width, r.Height);
                    input = new InputSpec(new[] { "-f", "lavfi", "-i",
                        $"{cap}:crop_left={cut.L}:crop_top={cut.T}:crop_right={cut.R}:crop_bottom={cut.B}," +
                        $"scale_d3d11=width={cut.ScaleW}:height={cut.ScaleH}:format=nv12,hwdownload,format=nv12" }, false);
                    gpuCut = cut;
                    lock (CaptureSizes) CaptureSizes[r.Key] = (c.W, c.H); // the window's own size: crop is subtracted by the layout, as for CPU captures
                }
                else
                    input = new InputSpec(new[] { "-f", "lavfi", "-i", $"{cap},hwdownload,format=bgra" }, false);
            }
            else
            {
                if (_findWindow(r.Title) is null or 0 && OperatingSystem.IsWindows()) { Wait(r, "waiting for the window"); return; }
                input = new InputSpec(new[] { "-f", "gdigrab", "-framerate", r.Fps.ToString(CultureInfo.InvariantCulture),
                    "-draw_mouse", r.Cursor ? "1" : "0", "-i", "title=" + r.Title }, false);
            }
        }

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "info", "-nostats" }) psi.ArgumentList.Add(a);
        var dev = input.Args.ToList();
        for (int i = dev.Count - 2; i >= 0; i--)
            if (dev[i] is "-thread_queue_size" or "-use_wallclock_as_timestamps") dev.RemoveRange(i, 2);
        foreach (var a in dev) psi.ArgumentList.Add(a);
        var fillSrc = new StudioSource { FillMode = r.Fill };
        string crop = r.Crop is (0, 0, 0, 0) ? "" : $"crop=iw-{r.Crop.L + r.Crop.R}:ih-{r.Crop.T + r.Crop.B}:{r.Crop.L}:{r.Crop.T},";
        string vf = gpuCut is { } g
            ? (g.Pad ? $"pad={r.Width}:{r.Height}:(ow-iw)/2:(oh-ih)/2:color=black," : "") + "format=yuv420p" // already cut and sized
            : crop + PipelineBuilder.FillChain(fillSrc, r.Width, r.Height, r.Fps, pad: true, light: r.Light) + "format=yuv420p";
        foreach (var a in new[] { "-an", "-vf", vf, "-fps_mode", "passthrough", "-flush_packets", "1", "-f", "rawvideo", "pipe:1" })
            psi.ArgumentList.Add(a);

        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) { Wait(r, "couldn't start: " + ex.Message); return; }
        ResourceMonitor.Track(p, ResourceMonitor.Capture);
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        r.Proc = p;
        r.Running = true;
        r.Starts++;
        r.StartedUtc = DateTime.UtcNow;
        r.State = "capturing";
        long framesAtStart = Interlocked.Read(ref r.Frames);
        int size = r.Width * r.Height * 3 / 2;
        new Thread(() =>
        {
            var s = p.StandardOutput.BaseStream;
            try
            {
                while (true)
                {
                    var buf = new byte[size];
                    int got = 0;
                    while (got < size)
                    {
                        int n = s.Read(buf, got, size - got);
                        if (n <= 0) return;
                        got += n;
                    }
                    lock (r.Lock) r.Latest = buf;
                    Interlocked.Increment(ref r.Frames);
                }
            }
            catch { }
        }) { IsBackground = true, Name = "Leapcast window reader " + r.Name, Priority = ThreadPriority.AboveNormal }.Start();
        new Thread(() =>
        {
            var errors = new List<string>();
            try
            {
                string? line;
                bool inInput = false;
                while ((line = p.StandardError.ReadLine()) is not null)
                {
                    if (line.StartsWith("Input #", StringComparison.Ordinal)) inInput = true;
                    else if (line.StartsWith("Output #", StringComparison.Ordinal) || line.StartsWith("Stream mapping", StringComparison.Ordinal)) inInput = false;
                    var m = VideoSize.Match(line);
                    if (m.Success && inInput && !r.UsedGpuScale) // GPU-cut captures report the scaled size: the window size is known already
                        lock (CaptureSizes) CaptureSizes[r.Key] = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                    if (line.Contains("Capture item closed", StringComparison.OrdinalIgnoreCase))
                    {
                        // the game closed or recreated its window: re-attach to the new one
                        try { p.Kill(true); } catch { }
                    }
                    if (line.Contains("rror", StringComparison.Ordinal) || line.Contains("fail", StringComparison.OrdinalIgnoreCase))
                    { errors.Add(line.Trim()); if (errors.Count > 10) errors.RemoveAt(0); }
                }
                p.WaitForExit();
            }
            catch { }
            OnExited(r, p, framesAtStart, errors);
        }) { IsBackground = true, Name = "Leapcast window reader log " + r.Name }.Start();
    }

    private void OnExited(Reader r, Process p, long framesAtStart, List<string> errors)
    {
        lock (_lock)
        {
            if (r.Proc != p) return;
            r.Proc = null;
            r.Running = false;
            if (r.Retired) return;
            bool producedNothing = Interlocked.Read(ref r.Frames) == framesAtStart;
            if (producedNothing && r.UsedGpuScale && DateTime.UtcNow - r.StartedUtc < TimeSpan.FromSeconds(8) && _findWindow(r.Title) is not (null or 0))
            {
                // the GPU video processor couldn't take this window: scale on the CPU instead (capture stays on the GPU)
                GpuScale = false;
                r.UsedGpuScale = false;
                Message?.Invoke("GPU scaling isn't available for window capture on this PC, so Leapcast scales on the CPU.", false);
                r.NextAttemptUtc = DateTime.UtcNow;
                r.State = "restarting (CPU scaling)";
                return;
            }
            if (producedNothing && r.UsedGpu && DateTime.UtcNow - r.StartedUtc < TimeSpan.FromSeconds(8) && _findWindow(r.Title) is not (null or 0))
            {
                // the window exists but GPU capture produced nothing: fall back to GDI for this session
                GpuCapture = false;
                r.UsedGpu = false;
                Message?.Invoke("GPU window capture isn't working on this PC, so Leapcast switched to compatibility capture.", true);
                r.NextAttemptUtc = DateTime.UtcNow;
                r.State = "restarting (compatibility capture)";
                return;
            }
            Wait(r, producedNothing && errors.Count > 0 ? "waiting for the window (" + errors[^1] + ")" : "window closed, re-attaching");
        }
    }

    private static void Wait(Reader r, string state)
    {
        r.State = state;
        r.Running = false;
        r.NextAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
    }

    private void Supervise()
    {
        while (!_cts.IsCancellationRequested)
        {
            Thread.Sleep(500);
            try
            {
                lock (_lock)
                {
                    foreach (var r in _readers.Values)
                    {
                        // a GPU-cut capture is sized for the window's shape: if the window is resized, cut again
                        if (r.Running && r.UsedGpuScale && r.Proc is { } p && DateTime.UtcNow - r.LastSizeCheck > TimeSpan.FromSeconds(2))
                        {
                            r.LastSizeCheck = DateTime.UtcNow;
                            var now = _clientSize?.Invoke(r.Hwnd);
                            if (now is not null && now != r.Client) { try { p.Kill(true); } catch { } }
                            continue;
                        }
                        if (r.Retired || r.Running || r.Proc is not null || DateTime.UtcNow < r.NextAttemptUtc) continue;
                        TryStart(r);
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>Diagnostics: per window, how often its reader (re)started and how many frames arrived.</summary>
    public string Stats()
    {
        lock (_lock)
            return string.Join("; ", _readers.Values.Select(r =>
                $"{r.Title}: {r.Width}x{r.Height} {(r.UsedGpu ? (r.UsedGpuScale ? "gpu+gpuscale" : "gpu") : "gdi")} starts={r.Starts} frames={Interlocked.Read(ref r.Frames)} {r.State}"));
    }

    /// <summary>Deepest frame backlog of any window pipe (seconds).</summary>
    public double MaxBacklogSeconds() { lock (_lock) return _pipes.Select(p => p.BacklogSeconds).DefaultIfEmpty(0).Max(); }

    /// <summary>Frames received from a window's reader so far (tests / health).</summary>
    public long FramesOf(string key) { lock (_lock) return _readers.TryGetValue(key, out var r) ? Interlocked.Read(ref r.Frames) : 0; }
    public int StartsOf(string key) { lock (_lock) return _readers.TryGetValue(key, out var r) ? r.Starts : 0; }
    /// <summary>Kills a reader as if its window had closed (tests).</summary>
    public void SimulateWindowClosed(string key)
    {
        Process? p;
        lock (_lock) p = _readers.TryGetValue(key, out var r) ? r.Proc : null;
        try { p?.Kill(true); } catch { }
    }

    private static void Stop(Reader r)
    {
        r.Retired = true;
        var p = r.Proc;
        r.Proc = null;
        if (p is null) return;
        try { if (!p.HasExited) { p.StandardInput.Write("q"); p.StandardInput.Flush(); if (!p.WaitForExit(1500)) p.Kill(true); } } catch { }
        try { p.Dispose(); } catch { }
    }

    private void ClosePipesLocked()
    {
        foreach (var p in _pipes) p.Dispose();
        _pipes.Clear();
    }

    public void ClosePipes() { lock (_lock) ClosePipesLocked(); }

    public void CloseAll()
    {
        lock (_lock)
        {
            ClosePipesLocked();
            foreach (var r in _readers.Values) Stop(r);
            _readers.Clear();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        CloseAll();
    }
}
