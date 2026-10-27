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

    public long Sampled, Dropped, Written, FromLatest;

    /// <summary>ffmpeg input path (\\.\pipe\… on Windows, a FIFO elsewhere).</summary>
    public string Path { get; }

    public CountedFramePipe(string scratchDir, int frameBytes, double fps, double origin, Func<byte[]?> latest, byte[] blank)
    {
        _fps = fps; _origin = origin; _latest = latest; _blank = blank;
        // up to 3 s of backlog (start-up, momentary stalls), capped at ~96 MB for full-screen cameras
        int cap = (int)Math.Clamp(Math.Min(fps * 3, 96L * 1024 * 1024 / Math.Max(1, frameBytes)), 4, fps * 3);
        _queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), cap);
        string name = $"leapcast_cam_{Guid.NewGuid():N}";
        Stream? stream = null;
        if (OperatingSystem.IsWindows())
        {
            _server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, Math.Max(1 << 16, frameBytes * 2));
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
            if (_server is not null) { _server.WaitForConnectionAsync(ct).GetAwaiter().GetResult(); s = _server; }
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
                    var frame = _latest() ?? _blank;
                    if (_queue.TryAdd(frame)) Interlocked.Increment(ref Sampled); else Interlocked.Increment(ref Dropped);
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
                if (_queue.TryTake(out var q, 20, ct)) { s.Write(q); }
                else
                {
                    if (_origin + n / _fps > WallNow()) continue; // not due yet (sampler is just a bit late)
                    s.Write(_latest() ?? _blank);
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

        public byte[]? Snapshot()
        {
            lock (Lock) return Latest;
        }

        public void Dispose()
        {
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

    public CameraBridge(string scratchDir) => _scratch = scratchDir;

    public sealed record CameraFeed(string Path, int Width, int Height);

    /// <summary>Largest picture size any visible use of this camera needs, at the output's resolution.</summary>
    public static (int w, int h) NeededSize(IEnumerable<StudioSource> uses, int outW, int outH)
    {
        int w = 0;
        double aspect = 16.0 / 9;
        foreach (var s in uses)
        {
            int need = Anchors.Resolve(s) == Anchors.Fill ? outW : (int)Math.Round(outW * Math.Clamp(s.SizePercent, 1, 100) / 100.0);
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
        foreach (var key in _readers.Keys.Except(cameras.Keys).ToList()) { _readers[key].Dispose(); _readers.Remove(key); }

        var result = new Dictionary<string, CameraFeed>();
        foreach (var (key, (device, uses)) in cameras)
        {
            var (w, h) = NeededSize(uses, outW, outH);
            string sig = string.Join("\u001f", device.Args) + $"|{w}x{h}|{fps}";
            if (!_readers.TryGetValue(key, out var reader) || reader.Signature != sig || reader.Exited)
            {
                if (reader is not null) reader.Dispose();
                reader = StartReader(ffmpeg, device, w, h, fps, sig, uses.FirstOrDefault()?.Name ?? "Camera");
                _readers[key] = reader;
            }
            int bytes = w * h * 3 / 2;
            var blank = new byte[bytes];
            Array.Fill(blank, (byte)16, 0, w * h);
            Array.Fill(blank, (byte)128, w * h, bytes - w * h);
            var r = reader;
            var pipe = new CountedFramePipe(_scratch, bytes, fps, origin, r.Snapshot, blank);
            _pipes.Add(pipe);
            result[key] = new CameraFeed(pipe.Path, w, h);
        }
        return result;
    }

    private Reader StartReader(string ffmpeg, InputSpec device, int w, int h, int fps, string sig, string name)
    {
        var reader = new Reader { Signature = sig, Width = w, Height = h };
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // low latency: no input buffering, and every frame is flushed to us immediately (not in 32 KB blocks)
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostats", "-fflags", "nobuffer" }) psi.ArgumentList.Add(a);
        var dev = device.Args.ToList();
        for (int i = dev.Count - 2; i >= 0; i--)          // single-input process: no queue tuning needed
            if (dev[i] is "-thread_queue_size" or "-use_wallclock_as_timestamps") dev.RemoveRange(i, 2);
        foreach (var a in dev) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-an", "-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease:flags=bicubic,pad={w}:{h}:-1:-1:color=black,format=yuv420p",
                     "-fps_mode", "passthrough", "-flush_packets", "1", "-f", "rawvideo", "pipe:1" })
            psi.ArgumentList.Add(a);
        try
        {
            var p = Process.Start(psi)!;
            reader.Proc = p;
            int size = w * h * 3 / 2;
            _ = Task.Run(async () =>
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
                            int n = await s.ReadAsync(buf.AsMemory(got, size - got));
                            if (n <= 0) return;
                            got += n;
                        }
                        lock (reader.Lock) reader.Latest = buf;
                        Interlocked.Increment(ref reader.Frames);
                    }
                }
                catch { }
            });
            _ = Task.Run(async () =>
            {
                string err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                reader.Exited = true;
                reader.LastError = err.Trim();
                if (!string.IsNullOrWhiteSpace(err) && !reader.Signature.StartsWith("disposed"))
                    Error?.Invoke($"\"{name}\": " + StreamEngine.Explain(err.Split('\n').Last(l => l.Trim().Length > 0)));
            });
        }
        catch (Exception ex) { reader.Exited = true; Error?.Invoke($"\"{name}\": couldn't start the camera reader ({ex.Message})."); }
        return reader;
    }

    /// <summary>Diagnostics: reader frames and pipe counters.</summary>
    public string Stats() =>
        string.Join("; ", _readers.Select(r => $"reader {r.Value.Width}x{r.Value.Height} frames={r.Value.Frames} exited={r.Value.Exited}")) + " | " +
        string.Join("; ", _pipes.Select(p => $"pipe sampled={p.Sampled} dropped={p.Dropped} written={p.Written} fromLatest={p.FromLatest}"));

    public void ClosePipes()
    {
        foreach (var p in _pipes) p.Dispose();
        _pipes.Clear();
    }

    public void CloseAll()
    {
        ClosePipes();
        foreach (var r in _readers.Values) { r.Signature = "disposed"; r.Dispose(); }
        _readers.Clear();
    }

    public void Dispose() => CloseAll();
}
