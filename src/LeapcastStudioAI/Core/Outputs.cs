using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

namespace LeapcastStudioAI;

public enum OutputState { Off, Starting, Live, Reconnecting, Error }

public sealed record OutputStatus(
    Guid Id, string Name, string Service, bool Vertical, OutputState State,
    double Kbps, TimeSpan LiveFor, double BufferedSeconds, string? Message);

/// <summary>
/// A pipe the encoder writes MPEG-TS into, fanned out to any number of subscribers (one per destination).
/// Subscribers join and leave at any time; each gets its own bounded queue, so a slow destination can never stall
/// the encoder, the recording or the other destinations.
/// </summary>
public sealed class TsBus : IDisposable
{
    public sealed class Subscriber
    {
        internal readonly BlockingCollection<byte[]> Queue = new(new ConcurrentQueue<byte[]>());
        internal long QueuedBytes;
        internal bool Aligned;
        public bool Overflowed { get; internal set; }
        public long Bytes => Interlocked.Read(ref QueuedBytes);
        /// <summary>Max backlog before the subscriber is cut off (its destination can't keep up).</summary>
        public long LimitBytes { get; init; } = 24L * 1024 * 1024;
    }

    private readonly List<Subscriber> _subs = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _fifo;
    private readonly NamedPipeServerStream? _server;
    private readonly string? _pipeName;
    private long _total;

    public string Path { get; }
    public bool Ended { get; private set; }
    public string Tag { get; }

    public TsBus(string scratchDir, string tag)
    {
        Tag = tag;
        string name = $"leapcast_bus_{tag}_{Guid.NewGuid():N}";
        if (OperatingSystem.IsWindows())
        {
            _server = LocalPipe.Server(name, PipeDirection.In, 1 << 20, 0);
            _pipeName = name;
            Path = @"\\.\pipe\" + name;
        }
        else
        {
            Directory.CreateDirectory(scratchDir);
            _fifo = System.IO.Path.Combine(scratchDir, name + ".ts");
            using (var p = Process.Start(new ProcessStartInfo("mkfifo", new[] { _fifo }) { UseShellExecute = false })!) p.WaitForExit();
            Path = _fifo;
        }
        new Thread(Pump) { IsBackground = true, Name = "Leapcast TS bus " + tag, Priority = ThreadPriority.AboveNormal }.Start();
    }

    private void Pump()
    {
        Stream? s = null;
        try
        {
            if (_server is not null) { LocalPipe.WaitForClient(_server, _pipeName!, _cts.Token); s = _server; }
            else s = new FileStream(_fifo!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            var buf = new byte[1 << 16];
            while (!_cts.IsCancellationRequested)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                long start = _total;
                _total += n;
                lock (_lock)
                {
                    foreach (var sub in _subs)
                    {
                        if (sub.Overflowed) continue;
                        int offset = 0;
                        if (!sub.Aligned)
                        {
                            // a new subscriber starts on a 188-byte TS packet boundary
                            offset = (int)((188 - start % 188) % 188);
                            if (offset >= n) continue;
                            sub.Aligned = true;
                        }
                        var chunk = new byte[n - offset];
                        Buffer.BlockCopy(buf, offset, chunk, 0, chunk.Length);
                        if (Interlocked.Add(ref sub.QueuedBytes, chunk.Length) > sub.LimitBytes)
                        {
                            sub.Overflowed = true;
                            sub.Queue.CompleteAdding();
                            continue;
                        }
                        sub.Queue.Add(chunk);
                    }
                }
            }
        }
        catch { }
        finally
        {
            Ended = true;
            lock (_lock) foreach (var sub in _subs) sub.Queue.CompleteAdding();
            try { s?.Dispose(); } catch { }
            if (_fifo is not null) { try { File.Delete(_fifo); } catch { } }
        }
    }

    public Subscriber Subscribe()
    {
        var sub = new Subscriber();
        lock (_lock)
        {
            if (Ended) sub.Queue.CompleteAdding();
            _subs.Add(sub);
        }
        return sub;
    }

    public void Unsubscribe(Subscriber sub)
    {
        lock (_lock) _subs.Remove(sub);
        sub.Queue.CompleteAdding();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _server?.Dispose(); } catch { }
        if (_fifo is not null)
        {
            try { using var w = new FileStream(_fifo, FileMode.Open, FileAccess.Write | FileAccess.Read); } catch { }
        }
    }
}

/// <summary>
/// Aitum-style multistream outputs: each destination is its own small ffmpeg "pusher" that stream-copies the
/// shared encode to its RTMP(S) server. Outputs start/stop independently and reconnect on their own.
/// </summary>
public sealed class OutputManager : IDisposable
{
    private sealed class Pusher
    {
        public required StreamOutput Output;
        public required string Url;
        public OutputState State = OutputState.Starting;
        public Process? Proc;
        public TsBus? Bus;
        public TsBus.Subscriber? Sub;
        /// <summary>The buses this process is fed from, in order: a pipeline rebuild hands the running connection
        /// over to the new encoder's bus instead of reconnecting.</summary>
        public BlockingCollection<TsBus.Subscriber>? Feeds;
        /// <summary>What the destination was set up with (codec + picture size): a rebuild with the same signature
        /// can continue on the same connection.</summary>
        public string Signature = "";
        public volatile bool Overflowed;
        public DateTime? LiveSince;
        public int Attempt;
        public string? Message;
        public CancellationTokenSource? RetryCts;
        public readonly Queue<(DateTime t, long bytes)> Window = new();
        public long LastBytes;
        public double Kbps;
        public bool Stopping;
    }

    private readonly Dictionary<Guid, Pusher> _pushers = new();
    /// <summary>Outputs that failed before going live (bad key/URL): shown with their error, not running.</summary>
    private readonly Dictionary<Guid, (StreamOutput Output, string Message)> _failed = new();
    private readonly Dictionary<string, TsBus> _buses = new();
    private readonly Dictionary<string, string> _signatures = new();
    /// <summary>How long a connection waits for the next encoder while the pipeline is rebuilt.</summary>
    public static readonly TimeSpan HandoverWait = TimeSpan.FromSeconds(12);
    /// <summary>Rebuilds that kept every connection open (no reconnect).</summary>
    public int Handovers { get; private set; }
    private readonly object _lock = new();
    private readonly Func<string?> _ffmpeg;
    private readonly string _logDir;

    public bool AutoReconnect { get; set; } = true;
    public event Action<string, bool>? Message;
    public event Action? Changed;

    public OutputManager(Func<string?> ffmpeg, string logDir)
    {
        _ffmpeg = ffmpeg;
        _logDir = logDir;
    }

    /// <summary>
    /// Which encode a destination takes: the main or vertical one, or the shared encode for its own bitrate (outputs
    /// with the same own bitrate share one). <paramref name="mainRiders"/>: own-bitrate outputs that ride the main
    /// encode (<see cref="OutputBudget.MainRiders"/>).
    /// </summary>
    public static string BusKey(StreamOutput o, IReadOnlySet<Guid>? mainRiders = null) =>
        mainRiders is not null && mainRiders.Contains(o.Id) ? "main"
        : o.OwnBitrateKbps > 0 ? $"own_{o.OwnBitrateKbps}_{(o.IsVertical ? "v" : "h")}"
        : (o.IsVertical ? "vertical" : "main");

    /// <summary>Own-bitrate outputs that currently ride the main encode (set with each new encoder run).</summary>
    public IReadOnlySet<Guid> MainRiders { get; set; } = new HashSet<Guid>();
    private string KeyOf(StreamOutput o) => BusKey(o, MainRiders);

    public bool AnyActive { get { lock (_lock) return _pushers.Count > 0; } }
    public bool IsActive(Guid id) { lock (_lock) return _pushers.ContainsKey(id); }
    public IReadOnlyList<StreamOutput> ActiveOutputs() { lock (_lock) return _pushers.Values.Select(p => p.Output).ToList(); }

    /// <summary>
    /// New encoder run: attach every active output to its (new) bus. A connection whose encode is unchanged (same
    /// codec and picture size) simply continues on the new bus: the destination never sees a disconnect, only a
    /// short pause while the pipeline was rebuilt. Anything else reconnects.
    /// </summary>
    public void BindBuses(IReadOnlyDictionary<string, TsBus> buses, IReadOnlyDictionary<string, string>? signatures = null)
    {
        bool handedOver = false;
        lock (_lock)
        {
            _buses.Clear();
            _signatures.Clear();
            foreach (var (k, v) in buses) _buses[k] = v;
            if (signatures is not null) foreach (var (k, v) in signatures) _signatures[k] = v;
            foreach (var p in _pushers.Values)
            {
                string key = KeyOf(p.Output);
                _signatures.TryGetValue(key, out var sig);
                if (p.Proc is { HasExited: false } && p.Feeds is { IsAddingCompleted: false } feeds && !p.Overflowed
                    && sig is not null && sig == p.Signature && _buses.TryGetValue(key, out var bus) && !bus.Ended)
                {
                    p.Bus = bus;
                    p.Sub = bus.Subscribe();
                    feeds.Add(p.Sub);
                    if (p.State == OutputState.Live) p.Message = null;
                    handedOver = true;
                }
                else Restart(p, immediate: true);
            }
            if (handedOver) Handovers++;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Encoder stopped (or is being rebuilt): connections stay open for <see cref="HandoverWait"/>, waiting for the
    /// next encoder's bus; after that they close and wait as "Waiting for the encoder…".
    /// </summary>
    public void UnbindBuses()
    {
        lock (_lock)
        {
            _buses.Clear();
            foreach (var p in _pushers.Values)
            {
                if (p.Sub is not null && p.Bus is not null) p.Bus.Unsubscribe(p.Sub); // the feed thread drains it, then waits
                p.Sub = null;
                p.Bus = null;
                if (p.Proc is null) { p.State = OutputState.Reconnecting; p.Message = "Waiting for the encoder…"; }
            }
        }
        Changed?.Invoke();
    }

    public void Start(StreamOutput output, string url)
    {
        lock (_lock)
        {
            if (_pushers.ContainsKey(output.Id)) return;
            _failed.Remove(output.Id);
            var p = new Pusher { Output = output, Url = url };
            _pushers[output.Id] = p;
            Restart(p, immediate: true);
        }
        Changed?.Invoke();
    }

    public async Task StopAsync(Guid id)
    {
        Pusher? p;
        lock (_lock)
        {
            _failed.Remove(id);
            if (!_pushers.Remove(id, out p)) return;
            p.Stopping = true;
            p.RetryCts?.Cancel();
        }
        await GracefulStopAsync(p);
        Changed?.Invoke();
    }

    public async Task StopAllAsync()
    {
        List<Pusher> all;
        lock (_lock) { all = _pushers.Values.ToList(); _pushers.Clear(); _failed.Clear(); foreach (var p in all) { p.Stopping = true; p.RetryCts?.Cancel(); } }
        await Task.WhenAll(all.Select(GracefulStopAsync));
        Changed?.Invoke();
    }

    private static async Task GracefulStopAsync(Pusher p)
    {
        var proc = p.Proc;
        p.Feeds?.CompleteAdding();
        if (p.Sub is not null && p.Bus is not null) p.Bus.Unsubscribe(p.Sub); // closes the pusher's stdin → clean RTMP close
        if (proc is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await proc.WaitForExitAsync(cts.Token);
        }
        catch { try { proc.Kill(true); } catch { } }
    }

    private void Restart(Pusher p, bool immediate)
    {
        KillProcess(p);
        p.RetryCts?.Cancel();
        if (!_buses.TryGetValue(KeyOf(p.Output), out var bus) || bus.Ended)
        {
            p.State = OutputState.Reconnecting;
            p.Message = "Waiting for the encoder…";
            return;
        }
        if (immediate) Spawn(p, bus);
        else
        {
            int delay = new[] { 2, 4, 8, 15, 30 }[Math.Min(p.Attempt, 4)];
            p.Attempt++;
            p.State = OutputState.Reconnecting;
            p.Message = $"Reconnecting in {delay}s…";
            var cts = new CancellationTokenSource();
            p.RetryCts = cts;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token); } catch { return; }
                lock (_lock)
                {
                    if (p.Stopping || !_pushers.ContainsKey(p.Output.Id)) return;
                    if (_buses.TryGetValue(KeyOf(p.Output), out var b) && !b.Ended) Spawn(p, b);
                }
                Changed?.Invoke();
            });
        }
    }

    private void Spawn(Pusher p, TsBus bus)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null) { p.State = OutputState.Error; p.Message = "FFmpeg not found."; return; }
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "warning", "-nostats", "-progress", "pipe:2", "-stats_period", "1",
                     // probe at most one keyframe interval (2 s GOP): joins in ~1-2 s instead of ~5 s
                     "-probesize", "500000", "-analyzeduration", "2500000", "-fpsprobesize", "0",
                     "-fflags", "+discardcorrupt", "-f", "mpegts", "-i", "pipe:0",
                     "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-bsf:a", "aac_adtstoasc",
                     "-f", "flv", "-flvflags", "no_duration_filesize", p.Url })
            psi.ArgumentList.Add(a);
        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex) { p.State = OutputState.Error; p.Message = ex.Message; return; }
        ResourceMonitor.Track(proc, ResourceMonitor.Destinations);
        try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        p.Proc = proc;
        p.Bus = bus;
        p.Sub = bus.Subscribe();
        p.Overflowed = false;
        p.Signature = _signatures.TryGetValue(KeyOf(p.Output), out var sig) ? sig : "";
        var feeds = new BlockingCollection<TsBus.Subscriber>();
        feeds.Add(p.Sub);
        p.Feeds = feeds;
        p.State = OutputState.Starting;
        p.Message = "Connecting…";
        p.LastBytes = 0;
        lock (p.Window) p.Window.Clear();

        // feed: bus → pusher stdin, one bus after another (a pipeline rebuild hands over to the next bus without
        // closing the connection; the demuxer re-bases the new encoder's timestamps). A dedicated thread: this
        // loop blocks on the pusher's stdin, and blocking thread-pool threads would starve other readers.
        new Thread(() =>
        {
            var stdin = proc.StandardInput.BaseStream;
            try
            {
                while (feeds.TryTake(out var sub, HandoverWait))
                {
                    foreach (var chunk in sub.Queue.GetConsumingEnumerable())
                    {
                        stdin.Write(chunk, 0, chunk.Length);
                        Interlocked.Add(ref sub.QueuedBytes, -chunk.Length);
                    }
                    if (sub.Overflowed) { p.Overflowed = true; break; }
                    stdin.Flush();
                }
            }
            catch { }
            try { stdin.Close(); } catch { }
        }) { IsBackground = true, Name = "Leapcast output " + p.Output.Name, Priority = ThreadPriority.AboveNormal }.Start();
        _ = proc.StandardOutput.ReadToEndAsync();

        // progress + errors
        _ = Task.Run(async () =>
        {
            var errors = new List<string>();
            var log = OpenLog(p.Output);
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                {
                    log?.WriteLine(line);
                    int eq = line.IndexOf('=');
                    if (FfmpegRunner.IsProgressLine(line, eq))
                    {
                        if (line.StartsWith("total_size=", StringComparison.Ordinal) &&
                            long.TryParse(line.AsSpan(11), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > 0)
                            OnBytes(p, proc, bytes);
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(line)) { errors.Add(line.Trim()); if (errors.Count > 30) errors.RemoveAt(0); }
                }
                await proc.WaitForExitAsync();
            }
            catch { }
            finally { log?.Dispose(); }
            OnExited(p, proc, errors);
        });
    }

    private void OnBytes(Pusher p, Process proc, long bytes)
    {
        bool changed = false;
        lock (_lock)
        {
            if (p.Proc != proc) return;
            var now = DateTime.UtcNow;
            lock (p.Window)
            {
                p.Window.Enqueue((now, bytes));
                while (p.Window.Count > 0 && now - p.Window.Peek().t > TimeSpan.FromSeconds(6)) p.Window.Dequeue();
                if (p.Window.Count >= 2)
                {
                    var a = p.Window.Peek(); double dt = (now - a.t).TotalSeconds;
                    if (dt > 0.5) p.Kbps = (bytes - a.bytes) * 8 / 1000.0 / dt;
                }
            }
            // The muxer only writes after the server accepted the publish, so bytes that keep growing across two
            // reports mean the destination is receiving the stream (a static scene can be only ~100 kbps).
            if (p.State != OutputState.Live && p.LastBytes > 0 && bytes > p.LastBytes)
            {
                p.State = OutputState.Live;
                p.LiveSince ??= now;
                p.Message = null;
                p.Attempt = 0;
                changed = true;
            }
            p.LastBytes = bytes;
        }
        if (changed) Changed?.Invoke();
    }

    private void OnExited(Pusher p, Process proc, List<string> errors)
    {
        lock (_lock)
        {
            if (p.Proc != proc) return; // replaced (restart on a new bus) or stopped
            p.Feeds?.CompleteAdding();
            if (p.Bus is not null && p.Sub is not null) p.Bus.Unsubscribe(p.Sub);
            p.Proc = null;
            if (p.Stopping || !_pushers.ContainsKey(p.Output.Id)) return;
            string why = p.Overflowed
                ? "Upload couldn't keep up with this destination"
                : StreamEngine.Explain(errors.LastOrDefault(e => e.Contains("rror", StringComparison.Ordinal) || e.Contains("fail", StringComparison.OrdinalIgnoreCase)) ?? errors.LastOrDefault() ?? "disconnected");
            bool busEnded = p.Bus?.Ended ?? true;
            if (busEnded) { p.State = OutputState.Reconnecting; p.Message = "Waiting for the encoder…"; }
            else if (p.LiveSince is not null || p.Attempt > 0)
            {
                // a destination that was live is never dropped by the app: it reconnects until you end the stream
                Message?.Invoke($"{p.Output.Name}: {why.TrimEnd('.')}. Reconnecting…", true);
                Restart(p, immediate: false);
            }
            else
            {
                // failed before ever going live (bad key / URL): stop it instead of hammering the server
                _pushers.Remove(p.Output.Id);
                _failed[p.Output.Id] = (p.Output, why);
                Message?.Invoke($"{p.Output.Name}: {why}", true);
            }
        }
        Changed?.Invoke();
    }

    private StreamWriter? OpenLog(StreamOutput o)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            string safe = string.Concat(o.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var fs = new FileStream(Path.Combine(_logDir, $"output-{safe}.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(fs) { AutoFlush = true };
        }
        catch { return null; }
    }

    private static void KillProcess(Pusher p)
    {
        var proc = p.Proc;
        p.Proc = null;
        p.Feeds?.CompleteAdding();
        if (p.Sub is not null && p.Bus is not null) p.Bus.Unsubscribe(p.Sub);
        p.Sub = null;
        if (proc is null) return;
        try { if (!proc.HasExited) proc.Kill(true); } catch { }
    }

    public IReadOnlyList<OutputStatus> Snapshot()
    {
        lock (_lock)
        {
            var list = _pushers.Values.Select(p =>
            {
                double buffered = p.Sub is { } s && p.Kbps > 50 ? s.Bytes * 8 / 1000.0 / p.Kbps : 0;
                return new OutputStatus(p.Output.Id, p.Output.Name, p.Output.Service, p.Output.IsVertical, p.State,
                    p.State == OutputState.Live ? p.Kbps : 0,
                    p.LiveSince is { } t ? DateTime.UtcNow - t : TimeSpan.Zero, buffered, p.Message);
            }).ToList();
            list.AddRange(_failed.Values.Select(f => new OutputStatus(f.Output.Id, f.Output.Name, f.Output.Service,
                f.Output.IsVertical, OutputState.Error, 0, TimeSpan.Zero, 0, f.Message)));
            return list;
        }
    }

    /// <summary>Forget a shown error (e.g. after the user fixed the key).</summary>
    public void ClearError(Guid id) { lock (_lock) _failed.Remove(id); Changed?.Invoke(); }

    public void Dispose()
    {
        try { Task.Run(StopAllAsync).Wait(TimeSpan.FromSeconds(6)); } catch { }
    }
}
