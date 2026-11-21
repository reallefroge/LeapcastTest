using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;

namespace LeapcastStudioAI;

/// <summary>
/// A raw-video pipe whose frame <c>n</c> is, by construction, the picture at <c>origin + n/fps</c>:
/// a sampler thread grabs the latest picture on every frame tick and queues it; a writer thread feeds the queue to
/// ffmpeg, padding with blank frames up to "now" on connect. ffmpeg (7+) only reads an input when the compositor
/// needs it, so wall-clock-at-read timestamps on buffered devices drift and starve the overlay; counted frames can't.
/// </summary>
public sealed class CountedFramePipe : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly BlockingCollection<byte[]> _queue;
    private readonly Func<byte[]?> _latest;
    private readonly byte[] _blank;
    private readonly double _fps, _origin;
    private readonly string? _fifoPath;
    private NamedPipeServerStream? _server;
    private string? _pipeName;

    public long Sampled, Dropped, Written, FromLatest;

    /// <summary>Seconds of frames waiting for ffmpeg to read them: grows as soon as the compositor falls behind.</summary>
    public double BacklogSeconds => _queue.Count / _fps;

    /// <summary>ffmpeg input path (\\.\pipe\… on Windows, a FIFO elsewhere).</summary>
    public string Path { get; }

    private readonly int _frameBytes;
    private readonly ConcurrentBag<byte[]> _free = new();

    /// <summary>A buffer for one queued frame: recycled once it's written (a handful circulate, not one per frame).</summary>
    private byte[] Copy(byte[] src)
    {
        if (ReferenceEquals(src, _blank) || src.Length != _frameBytes) return src;
        if (!_free.TryTake(out var b)) b = GC.AllocateUninitializedArray<byte>(_frameBytes);
        Buffer.BlockCopy(src, 0, b, 0, _frameBytes);
        return b;
    }

    private void Recycle(byte[] b)
    {
        if (!ReferenceEquals(b, _blank) && b.Length == _frameBytes && _free.Count < 8) _free.Add(b);
    }

    public CountedFramePipe(string scratchDir, int frameBytes, double fps, double origin, Func<byte[]?> latest, byte[] blank)
    {
        // the source's buffers are reused by its reader: what's queued here is copied (and blank is ours)
        _fps = fps; _origin = origin; _latest = latest; _blank = (byte[])blank.Clone(); _frameBytes = frameBytes;
        // up to 3 s of backlog (start-up, momentary stalls), capped at ~96 MB for full-screen cameras
        int cap = (int)Math.Clamp(Math.Min(fps * 3, 96L * 1024 * 1024 / Math.Max(1, frameBytes)), 4, fps * 3);
        _queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), cap);
        string name = $"leapcast_cam_{Guid.NewGuid():N}";
        Stream? stream = null;
        if (OperatingSystem.IsWindows())
        {
            _server = LocalPipe.Server(name, PipeDirection.Out, 0, Math.Max(1 << 16, frameBytes * 2));
            _pipeName = name;
            Path = @"\\.\pipe\" + name;
        }
        else
        {
            Directory.CreateDirectory(scratchDir);
            _fifoPath = System.IO.Path.Combine(scratchDir, name + ".yuv");
            using (var p = Process.Start(new ProcessStartInfo("mkfifo", new[] { _fifoPath }) { UseShellExecute = false })!) p.WaitForExit();
            Path = _fifoPath;
        }
        new Thread(() => Run(stream)) { IsBackground = true, Name = "Leapcast camera pipe", Priority = ThreadPriority.AboveNormal }.Start();
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private void Run(Stream? _)
    {
        var ct = _cts.Token;
        Stream? s = null;
        try
        {
            if (_server is not null) { LocalPipe.WaitForClient(_server, _pipeName!, ct); s = _server; }
            else s = new FileStream(_fifoPath!, FileMode.Open, FileAccess.Write); // blocks until ffmpeg opens it

            // One clock for everything, anchored to wall time *now* (before any padding is written): frame k is
            // sampled at origin + k/fps exactly, no matter how long ffmpeg takes to read the padding.
            double wallAtStart = Now();
            var clock = Stopwatch.StartNew();
            double WallNow() => wallAtStart + clock.Elapsed.TotalSeconds;
            long first = Math.Max(0, (long)Math.Ceiling((wallAtStart - _origin) * _fps)); // first live frame

            var sampler = new Thread(() =>
            {
                long k = first;
                while (!ct.IsCancellationRequested)
                {
                    double wait = _origin + k / _fps - WallNow();
                    if (wait > 0.002) { Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait, 0.05))); continue; }
                    var frame = Copy(_latest() ?? _blank);
                    if (_queue.TryAdd(frame)) Interlocked.Increment(ref Sampled); else { Recycle(frame); Interlocked.Increment(ref Dropped); }
                    k++;
                }
            }) { IsBackground = true, Name = "Leapcast camera sampler" };
            sampler.Start();

            // frames before the first live one are history: blanks, so the frame count equals the timeline
            long n = 0;
            while (n < first && !ct.IsCancellationRequested) { s.Write(_blank); n++; }

            while (!ct.IsCancellationRequested)
            {
                // Never skip a frame number: timestamps must stay exact. Queued pictures were captured on time;
                // only if ffmpeg fell more than the queue behind do we fall back to the newest picture.
                if (_queue.TryTake(out var q, 20, ct)) { s.Write(q); Recycle(q); }
                else
                {
                    if (_origin + n / _fps > WallNow()) continue; // not due yet (sampler is just a bit late)
                    var f = Copy(_latest() ?? _blank);
                    s.Write(f);
                    Recycle(f);
                    Interlocked.Increment(ref FromLatest);
                }
                n++;
                Interlocked.Increment(ref Written);
            }
        }
        catch { /* ffmpeg closed the pipe or we were disposed */ }
        finally
        {
            try { s?.Dispose(); } catch { }
            if (_fifoPath is not null) { try { File.Delete(_fifoPath); } catch { } }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _server?.Dispose(); } catch { }
        if (_fifoPath is not null)
        {
            // unblock a writer still waiting for ffmpeg to open the FIFO
            try { using var r = new FileStream(_fifoPath, FileMode.Open, FileAccess.Read | FileAccess.Write); } catch { }
        }
    }
}

/// <summary>
/// Keeps each webcam open in its own small ffmpeg process that reads the device continuously (so DirectShow's
/// buffer never backs up), scales it to the largest size any scene needs, and keeps only the latest frame.
/// The main pipeline consumes it through a <see cref="CountedFramePipe"/>. Cameras stay warm across pipeline
/// restarts, so scene edits and reconnects don't re-open the device.
/// </summary>
public sealed class CameraBridge : IDisposable
{
    private sealed class Reader : IDisposable
    {
        public required string Signature;
        public required int Width, Height;
        public Process? Proc;
        public byte[]? Latest;
        public readonly object Lock = new();
        public string LastError = "";
        public volatile bool Exited;
        public long Frames;
        // to relaunch the same reader when the camera stalls
        public string Ffmpeg = "", Name = "Camera";
        public InputSpec? Device;
        public int Fps;
        public DateTime LaunchedUtc, LastFrameUtc;
        public long FramesAtCheck;
        public int Restarts;
        public volatile bool Disposed;

        public byte[]? Snapshot()
        {
            lock (Lock) return Latest;
        }

        public void Dispose()
        {
            Disposed = true;
            try { if (Proc is { HasExited: false }) { Proc.StandardInput.Write("q"); Proc.StandardInput.Flush(); if (!Proc.WaitForExit(1500)) Proc.Kill(true); } } catch { }
            try { Proc?.Dispose(); } catch { }
        }
    }

    /// <summary>Measured delay from a picture leaving the device to it being sampled into the counted pipe.</summary>
    public static readonly double PathLatencySeconds =
        double.TryParse(Environment.GetEnvironmentVariable("LEAPCAST_CAMERA_LATENCY"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.10;

    private readonly Dictionary<string, Reader> _readers = new();
    private readonly List<CountedFramePipe> _pipes = new();
    private readonly string _scratch;
    public event Action<string>? Error;

    public CameraBridge(string scratchDir)
    {
        _scratch = scratchDir;
        new Thread(Watchdog) { IsBackground = true, Name = "Leapcast camera watchdog" }.Start();
    }

    /// <summary>
    /// Webcams sometimes stop delivering (USB hiccups, driver resets, another app grabbing them). A reader with no new
    /// picture for 4 s is relaunched in place; the stream keeps showing the last picture meanwhile.
    /// </summary>
    private void Watchdog()
    {
        while (!_disposed)
        {
            Thread.Sleep(1000);
            try
            {
                List<Reader> readers;
                lock (_readers) readers = _readers.Values.ToList();
                foreach (var r in readers)
                {
                    if (r.Disposed || r.Device is null) continue;
                    long f = Interlocked.Read(ref r.Frames);
                    if (f != r.FramesAtCheck) { r.FramesAtCheck = f; r.LastFrameUtc = DateTime.UtcNow; continue; }
                    var now = DateTime.UtcNow;
                    if (now - r.LaunchedUtc < TimeSpan.FromSeconds(6) || now - r.LastFrameUtc < TimeSpan.FromSeconds(4)) continue;
                    r.Restarts++;
                    if (r.Restarts == 1 || r.Restarts % 10 == 0)
                        Error?.Invoke($"\"{r.Name}\" stopped sending pictures, reconnecting it…");
                    Launch(r);
                }
            }
            catch { }
        }
    }

    private volatile bool _disposed;

    public sealed record CameraFeed(string Path, int Width, int Height);

    /// <summary>Largest picture size any visible use of this camera needs, at the output's resolution.</summary>
    public static (int w, int h) NeededSize(IEnumerable<StudioSource> uses, int outW, int outH)
    {
        int w = 0;
        double aspect = 16.0 / 9;
        foreach (var s in uses)
        {
            // as drawn: full width, a free size (dragged on the preview), or a corner's size percent
            int need = (int)Math.Round(LayoutMath.Horizontal(s, outW, outH, 16.0 / 9).W);
            w = Math.Max(w, need);
            if (SmartTuner.TryParseResolution(s.CameraResolution, out var cw, out var ch) && ch > 0) aspect = cw / (double)ch;
        }
        w = Math.Clamp(SmartTuner.Even(Math.Max(w, 64)), 64, outW);
        int h = Math.Clamp(SmartTuner.Even((int)Math.Round(w / aspect)), 36, outH);
        return (w, h);
    }

    /// <summary>
    /// Ensures a reader per camera (key → device input + uses) and returns fresh counted pipes for one pipeline run.
    /// </summary>
    public Dictionary<string, CameraFeed> Open(string ffmpeg, IReadOnlyDictionary<string, (InputSpec device, List<StudioSource> uses)> cameras,
        int outW, int outH, int fps, double origin)
    {
        ClosePipes();
        lock (_readers)
            foreach (var key in _readers.Keys.Except(cameras.Keys).ToList()) { _readers[key].Dispose(); _readers.Remove(key); }

        var result = new Dictionary<string, CameraFeed>();
        foreach (var (key, (device, uses)) in cameras)
        {
            var (w, h) = NeededSize(uses, outW, outH);
            // the rate this camera is asked for: the highest any scene wants of it, 0 meaning the canvas rate
            int want = uses.Select(u => u.CaptureFps).DefaultIfEmpty(0).Max();
            int rate = want > 0 ? Math.Clamp(want, 1, Math.Max(1, fps)) : fps;
            string dev = string.Join("\u001f", device.Args), tail = $"|{rate}";
            _readers.TryGetValue(key, out var reader);
            // resized on the canvas: a camera that's already read big enough keeps going (no reopening the device,
            // no flicker); one too small is reopened bigger, with room to grow
            if (reader is not null && !reader.Exited && reader.Signature.StartsWith(dev + "|", StringComparison.Ordinal) && reader.Signature.EndsWith(tail, StringComparison.Ordinal)
                && Math.Abs(reader.Width / (double)reader.Height - w / (double)h) < 0.03 * (w / (double)h))
            {
                if (reader.Width >= w && reader.Width <= w * 1.8) { w = reader.Width; h = reader.Height; }
                else if (reader.Width < w)
                {
                    int gw = Math.Min(outW, SmartTuner.Even((int)Math.Round(Math.Max(w * 1.35, outW * 0.5))));
                    h = Math.Min(outH, SmartTuner.Even((int)Math.Round(gw * (double)h / w))); w = gw;
                }
            }
            string sig = dev + $"|{w}x{h}" + tail;
            byte[]? carry = null;
            if (reader is null || reader.Signature != sig || reader.Exited)
            {
                if (reader is not null)
                {
                    // while the device reopens: the last picture, resized (a short freeze instead of a black flash)
                    var last = reader.Snapshot();
                    if (last is not null && last.Length == reader.Width * reader.Height * 3 / 2) carry = Frames.ResizeI420(last, reader.Width, reader.Height, w, h);
                    reader.Dispose();
                }
                reader = StartReader(ffmpeg, device, w, h, rate, sig, uses.FirstOrDefault()?.Name ?? "Camera");
                lock (_readers) _readers[key] = reader;
            }
            int bytes = w * h * 3 / 2;
            // what the pipe sends before the reader's next picture: its last one (a rebuild shows you, not black)
            var latest = reader.Snapshot();
            var blank = carry ?? (latest is not null && latest.Length == bytes ? latest : null) ?? Frames.BlankI420(w, h);
            var r = reader;
            var pipe = new CountedFramePipe(_scratch, bytes, rate, origin, r.Snapshot, blank);
            _pipes.Add(pipe);
            result[key] = new CameraFeed(pipe.Path, w, h);
        }
        return result;
    }

    /// <summary>Camera readers opened so far (tests: a resize mustn't reopen the device).</summary>
    public int Starts { get; private set; }

    private Reader StartReader(string ffmpeg, InputSpec device, int w, int h, int fps, string sig, string name)
    {
        Starts++;
        var reader = new Reader { Signature = sig, Width = w, Height = h, Ffmpeg = ffmpeg, Device = device, Fps = fps, Name = name };
        Launch(reader);
        return reader;
    }

    /// <summary>(Re)starts the reader process of <paramref name="reader"/>; frames keep landing in the same object.</summary>
    private void Launch(Reader reader)
    {
        var old = reader.Proc;
        if (old is not null) { try { if (!old.HasExited) old.Kill(true); old.WaitForExit(1500); } catch { } }
        if (reader.Disposed || reader.Device is null) return;
        int w = reader.Width, h = reader.Height;
        var psi = new ProcessStartInfo(reader.Ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // low latency: no input buffering, and every frame is flushed to us immediately (not in 32 KB blocks)
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostats", "-fflags", "nobuffer" }) psi.ArgumentList.Add(a);
        var dev = reader.Device.Args.ToList();
        for (int i = dev.Count - 2; i >= 0; i--)          // single-input process: no queue tuning needed
            if (dev[i] is "-thread_queue_size" or "-use_wallclock_as_timestamps") dev.RemoveRange(i, 2);
        foreach (var a in dev) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-an", "-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease:flags=bicubic,pad={w}:{h}:-1:-1:color=black,format=yuv420p",
                     "-fps_mode", "passthrough", "-flush_packets", "1", "-f", "rawvideo", "pipe:1" })
            psi.ArgumentList.Add(a);
        reader.LaunchedUtc = DateTime.UtcNow;
        try
        {
            var p = Process.Start(psi)!;
            ResourceMonitor.Track(p, ResourceMonitor.Capture);
            // A starved reader falls behind and every frame it queues becomes camera delay: keep it ahead of the game.
            try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            reader.Proc = p;
            reader.Exited = false;
            int size = w * h * 3 / 2;
            new Thread(() =>
            {
                var s = p.StandardOutput.BaseStream;
                try
                {
                    var ring = new FrameRing(size);
                    while (true)
                    {
                        var buf = ring.Next();
                        int got = 0;
                        while (got < size)
                        {
                            int n = s.Read(buf, got, size - got);
                            if (n <= 0) return;
                            got += n;
                        }
                        lock (reader.Lock) reader.Latest = buf;
                        Interlocked.Increment(ref reader.Frames);
                    }
                }
                catch { }
            }) { IsBackground = true, Name = "Leapcast camera reader " + reader.Name, Priority = ThreadPriority.AboveNormal }.Start();
            _ = Task.Run(async () =>
            {
                string err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (reader.Proc != p) return; // relaunched
                reader.Exited = true;
                reader.LastError = err.Trim();
                if (!string.IsNullOrWhiteSpace(err) && !reader.Disposed && reader.Restarts == 0)
                    Error?.Invoke($"\"{reader.Name}\": " + StreamEngine.Explain(err.Split('\n').Last(l => l.Trim().Length > 0)));
            });
        }
        catch (Exception ex) { reader.Exited = true; Error?.Invoke($"\"{reader.Name}\": couldn't start the camera reader ({ex.Message})."); }
    }

    /// <summary>Reopens a camera (Refresh): the same reader relaunches, its last picture stays up meanwhile.</summary>
    public bool Refresh(string key)
    {
        Reader? r;
        lock (_readers) _readers.TryGetValue(key, out r);
        if (r is null || r.Disposed) return false;
        Starts++;
        Task.Run(() => Launch(r));
        return true;
    }

    /// <summary>The newest frame from a camera (YUV 4:2:0) and its size, for auto light.</summary>
    public (byte[] Frame, int Width, int Height)? Latest(string key)
    {
        Reader? r;
        lock (_readers) _readers.TryGetValue(key, out r);
        return r?.Snapshot() is { } f ? (f, r.Width, r.Height) : null;
    }

    public double MaxBacklogSeconds() => _pipes.ToList().Select(p => p.BacklogSeconds).DefaultIfEmpty(0).Max();

    /// <summary>Diagnostics: reader frames and pipe counters.</summary>
    public string Stats() =>
        string.Join("; ", _readers.Select(r => $"reader {r.Value.Width}x{r.Value.Height} frames={r.Value.Frames} exited={r.Value.Exited} restarts={r.Value.Restarts}")) + " | " +
        string.Join("; ", _pipes.Select(p => $"pipe sampled={p.Sampled} dropped={p.Dropped} written={p.Written} fromLatest={p.FromLatest}"));

    public void ClosePipes()
    {
        foreach (var p in _pipes) p.Dispose();
        _pipes.Clear();
    }

    public void CloseAll()
    {
        ClosePipes();
        lock (_readers)
        {
            foreach (var r in _readers.Values) { r.Signature = "disposed"; r.Dispose(); }
            _readers.Clear();
        }
    }

    public void Dispose() { _disposed = true; CloseAll(); }
}

/// <summary>
/// A reader's few frame buffers, used in turn instead of a new one per frame: 30–60 large arrays a second per
/// camera/window otherwise land on .NET's large-object heap, and collecting those is what causes small hitches.
/// Whoever needs a picture for longer than a frame or two copies it (<see cref="CountedFramePipe"/> does).
/// </summary>
public sealed class FrameRing(int size, int count = 4)
{
    private readonly byte[][] _bufs = new byte[count][];
    private int _i;
    public byte[] Next()
    {
        var b = _bufs[_i] ??= GC.AllocateUninitializedArray<byte>(size);
        _i = (_i + 1) % _bufs.Length;
        return b;
    }
}

/// <summary>Raw picture helpers (YUV 4:2:0, the layout the readers deliver).</summary>
public static class Frames
{
    public static byte[] BlankI420(int w, int h)
    {
        var b = new byte[w * h * 3 / 2];
        Array.Fill(b, (byte)16, 0, w * h);
        Array.Fill(b, (byte)128, w * h, b.Length - w * h);
        return b;
    }

    /// <summary>Nearest-neighbour resize of a YUV 4:2:0 picture (a stand-in for a moment, so speed over quality).</summary>
    public static byte[] ResizeI420(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (src.Length < sw * sh * 3 / 2 || sw < 2 || sh < 2) return BlankI420(dw, dh);
        var d = new byte[dw * dh * 3 / 2];
        void Plane(int srcOff, int pw, int ph, int dstOff, int qw, int qh)
        {
            for (int y = 0; y < qh; y++)
            {
                int sy = Math.Min(ph - 1, (int)((long)y * ph / qh));
                int srow = srcOff + sy * pw, drow = dstOff + y * qw;
                for (int x = 0; x < qw; x++) d[drow + x] = src[srow + Math.Min(pw - 1, (int)((long)x * pw / qw))];
            }
        }
        int sc = (sw / 2) * (sh / 2), dc = (dw / 2) * (dh / 2);
        Plane(0, sw, sh, 0, dw, dh);
        Plane(sw * sh, sw / 2, sh / 2, dw * dh, dw / 2, dh / 2);
        Plane(sw * sh + sc, sw / 2, sh / 2, dw * dh + dc, dw / 2, dh / 2);
        return d;
    }
}
