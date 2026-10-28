using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LeapcastStudioAI;

/// <summary>Captures audio devices and serves them to ffmpeg as raw 48 kHz stereo float streams (Windows: named pipes).</summary>
public interface IAudioBridge : IDisposable
{
    /// <summary>Start/stop device captures so they match these sources (also drives the level meters).</summary>
    void Configure(IReadOnlyList<StudioSource> audioSources);
    /// <summary>
    /// Creates a fresh set of pipes for one ffmpeg run. Returns source id → ffmpeg input path.
    /// Each pipe's first sample corresponds to <paramref name="wallclockOrigin"/> (Unix seconds): on connect the
    /// writer sends silence up to "now", then paces by the wall clock and never skips, so sample count == time.
    /// </summary>
    IReadOnlyDictionary<Guid, string> OpenPipes(double wallclockOrigin);
    void ClosePipes();
    /// <summary>Peak level in dBFS (−60 … 0).</summary>
    double LevelDb(Guid sourceId);
}

public interface IPlatformServices
{
    /// <summary>Renders a text source to a transparent PNG sized for the output height. Null if empty.</summary>
    string? RenderText(StudioSource source, int outputHeight);
    (int X, int Y, int W, int H)? MonitorBounds(int index);
    /// <summary>Handle of the visible top-level window with exactly this title (null if not open).</summary>
    long? FindWindow(string title) => null;
    /// <summary>Client-area size of a window in physical pixels (null if unknown).</summary>
    (int W, int H)? WindowClientSize(long hwnd) => null;
    /// <summary>Starts rendering a web page off-screen (null where browsers aren't supported).</summary>
    IFrameSource? OpenBrowser(StudioSource source) => null;
}

public sealed record EngineStatus(
    bool Running, bool Live, bool Recording, bool ReplayBuffer, bool Reconnecting,
    string? PlanSummary, int CapKbps,
    double Fps, long Dropped, long Duplicated, double Speed,
    double RecentKbps, double AverageKbps,
    TimeSpan LiveFor, TimeSpan RecordingFor,
    string Health, string? LastError)
{
    /// <summary>Every multistream output that is running, reconnecting or failed.</summary>
    public IReadOnlyList<OutputStatus> Outputs { get; init; } = Array.Empty<OutputStatus>();
    public string? VerticalSummary { get; init; }
    /// <summary>Planned upload if every enabled destination is live (video + audio).</summary>
    public int PlannedUploadKbps { get; init; }
    /// <summary>What is actually being uploaded right now (sum over live outputs).</summary>
    public double UploadKbps { get; init; }
}

public sealed class EngineMessageEventArgs(string text, bool isError) : EventArgs
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
}

/// <summary>
/// Orchestrates the studio: one ffmpeg pipeline captures and composites every scene on the horizontal canvas (and
/// optionally a 9:16 vertical canvas), encodes each canvas once and fans the result out to recording/replay
/// segments and to independent multistream outputs (Aitum-style: start/stop/reconnect each destination on its own).
/// All public operations are serialized.
/// </summary>
public sealed class StreamEngine : IDisposable
{
    private readonly IAudioBridge _audio;
    private readonly IPlatformServices _platform;
    private readonly SegmentCache _cache;
    private readonly CameraBridge _cameras;
    private readonly WindowCaptureBridge _windows;
    private readonly BrowserBridge _browsers;
    private readonly OutputManager _outputs;
    private readonly string _logDir;
    private readonly string _tempDir;
    private readonly string _pipeDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _life = new();

    /// <summary>Which encodes the running pipeline has. A different shape needs a new pipeline.</summary>
    private sealed record Shape(bool Run, bool Main, bool MainBus, bool Vertical, bool VerticalEncode, string Own);

    private FfmpegRunner? _runner;
    private Shape? _runnerShape;
    private Dictionary<string, TsBus> _buses = new();
    private ProgressInfo? _lastProgress;
    private readonly Queue<(DateTime t, ProgressInfo p)> _progressWindow = new();
    private CancellationTokenSource? _reconnectCts;
    private int _reconnectAttempt;
    private DateTime? _liveSince;
    private DateTime _lastAdjust = DateTime.MinValue;
    // performance guard: steps taken this session because the PC fell behind (0 = none, 1 = 30 FPS, 2 = smaller too)
    private int _guardStep;
    private DateTime? _behindSince;
    private DateTime _lastGuard = DateTime.MinValue;
    /// <summary>The automatic performance guard has lightened the settings this session.</summary>
    public int PerformanceGuardStep => _guardStep;
    /// <summary>Tests: pretend the guard has already stepped in this far.</summary>
    public void SimulatePerformanceGuard(int step) => _guardStep = Math.Clamp(step, 0, 2);
    private DateTime? _uploadTroubleSince;
    private double _bitrateScale = 1.0;
    private readonly HashSet<long> _countedSegments = new();
    private long _liveBytes;
    private string? _lastError;
    private string _health = "Idle";
    private readonly ConcurrentDictionary<string, (int W, int H)> _knownSizes = new();

    // live layout: overlay positions are changed with runtime commands (no restart), coalesced by a pump
    private readonly object _posLock = new();
    private HashSet<string> _overlayNames = new();
    private readonly Dictionary<string, (string x, string y)> _wantPos = new();
    private readonly Dictionary<string, (string x, string y)> _sentPos = new();
    private DateTime _pumpPausedUntil;

    public string? FfmpegPath { get; set; }
    /// <summary>Optional input substitution (used by tests and non-Windows hosts).</summary>
    public Func<StudioSource, double, InputSpec?>? InputOverride { get; set; }
    /// <summary>Replaces the webcam device input of the camera reader (tests / non-Windows hosts).</summary>
    public Func<StudioSource, InputSpec?>? CameraDeviceOverride { get; set; }
    /// <summary>Replaces the capture input of a window reader (tests / non-Windows hosts).</summary>
    public Func<StudioSource, InputSpec?>? WindowDeviceOverride { get => _windows.DeviceOverride; set => _windows.DeviceOverride = value; }
    /// <summary>Diagnostics: game window readers (starts, frames, state).</summary>
    public string WindowCaptureStats() => _windows.Stats();
    public long WindowFrames(StudioSource s) => _windows.FramesOf(PipelineBuilder.InputKey(s));
    public int WindowReaderStarts(StudioSource s) => _windows.StartsOf(PipelineBuilder.InputKey(s));
    /// <summary>Tests: behave as if the game had closed its window.</summary>
    public void SimulateWindowClosed(StudioSource s) => _windows.SimulateWindowClosed(PipelineBuilder.InputKey(s));
    /// <summary>Returns an output's saved stream key (decrypted). Set by the app.</summary>
    public Func<StreamOutput, string>? KeyResolver { get; set; }
    public IReadOnlyList<ProbedEncoder> Encoders { get; private set; } = Array.Empty<ProbedEncoder>();
    public bool EncodersProbed { get; private set; }
    public bool InputThreadQueueSupported { get; private set; } = true;
    /// <summary>FFmpeg 8+ "gfxcapture" (Windows.Graphics.Capture): GPU window capture that works with DirectX games.</summary>
    public bool GpuWindowCaptureSupported { get; private set; }
    private bool _gpuDisplayFailed;
    private string _runnerCommand = "";

    /// <summary>
    /// If a pipeline using GPU capture died before producing a frame, GPU capture probably isn't available on this
    /// PC (old driver, remote desktop, no Direct3D 11): switch this session to GDI capture instead of failing.
    /// </summary>
    private bool TryGpuCaptureFallback(TimeSpan ranFor, bool producedFrames)
    {
        if (ranFor > TimeSpan.FromSeconds(15) || producedFrames) return false;
        bool changed = false;
        if (_runnerCommand.Contains("gfxcapture=", StringComparison.Ordinal) && GpuWindowCaptureSupported)
        {
            GpuWindowCaptureSupported = false; changed = true;
            Message?.Invoke(this, new EngineMessageEventArgs("GPU window capture isn't working on this PC, so Leapcast switched to compatibility capture (30 FPS).", true));
        }
        if (_runnerCommand.Contains("ddagrab=", StringComparison.Ordinal) && !_gpuDisplayFailed)
        {
            _gpuDisplayFailed = true; changed = true;
            Message?.Invoke(this, new EngineMessageEventArgs("GPU screen capture isn't working on this PC, so Leapcast switched to compatibility capture (30 FPS).", true));
        }
        return changed;
    }
    /// <summary>The scene currently on air.</summary>
    public StudioScene? Scene { get; private set; }
    private List<StudioScene> _scenes = new();
    private int _activeIndex;
    private PipelineSpec? _runnerSpec;
    private PipelineRequest? _runnerRequest;
    private double _runnerOrigin;
    private string? _runnerFingerprint;
    public StudioSettings Settings { get; private set; } = new();

    public bool PreviewOn { get; private set; }
    /// <summary>At least one multistream output is running (or reconnecting).</summary>
    public bool Live => _outputs.AnyActive;
    public bool ReplayBuffer { get; private set; }
    public bool Recording => _cache.IsRecording;
    public bool Running => _runner is not null;
    public bool Reconnecting => _reconnectCts is not null;
    public EncodePlan? CurrentPlan { get; private set; }
    public EncodePlan? VerticalPlan { get; private set; }
    public PlanSet? CurrentPlans { get; private set; }
    /// <summary>How many times an ffmpeg pipeline has been started (diagnostics/tests: transitions must not bump it).</summary>
    public int PipelineStarts { get; private set; }
    public string CameraStats() => _cameras.Stats();
    public bool IsOutputActive(Guid id) => _outputs.IsActive(id);
    /// <summary>Diagnostics/tests: process id of the running compositor/encoder.</summary>
    public int? PipelineProcessId => _runner?.ProcessId;
    public IReadOnlyList<OutputStatus> OutputStatuses() => _outputs.Snapshot();

    /// <summary>Preview frame layout: total width/height, and where the vertical canvas starts (0 = none).</summary>
    public (int Width, int Height, int HorizontalWidth, int VerticalWidth) PreviewFrame { get; private set; } = (768, 432, 768, 0);

    /// <summary>Picture size of a source as ffmpeg reported it (null until the pipeline has opened it once).</summary>
    public (int W, int H)? SourceSize(StudioSource s) =>
        _knownSizes.TryGetValue(PipelineBuilder.InputKey(s), out var v) ? v : null;
    /// <summary>Width/height of a source after cropping (estimated until ffmpeg reports the real size).</summary>
    public double SourceAspect(StudioSource s) => PipelineBuilder.AspectFor(s, _knownSizes);

    /// <summary>Diagnostics: (wall clock − origin, last reported ffmpeg output time + age of that report).</summary>
    public (double wall, double reported) StreamClock()
    {
        double wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - _runnerOrigin;
        var p = _lastProgress;
        double rep = p is null ? double.NaN : p.OutTime.TotalSeconds + (DateTime.UtcNow - _lastProgressAt).TotalSeconds;
        return (wall, rep);
    }
    private DateTime _lastProgressAt;

    /// <summary>Raw BGRA preview frame (<see cref="PreviewFrame"/> sized). The array is reused.</summary>
    public event Action<byte[]>? FrameReady;
    public event EventHandler<EngineMessageEventArgs>? Message;
    public event Action<EngineStatus>? StatusChanged;
    /// <summary>Raised when the engine itself turns a mode off (e.g. the encoder failed and won't restart).</summary>
    public event Action? ModesChanged;

    public const int PreviewHeightPx = 432;
    public const int PreviewWidthPx = 768;

    public StreamEngine(IAudioBridge audio, IPlatformServices platform, string dataRoot)
    {
        _audio = audio;
        _platform = platform;
        // ffmpeg readers/writers (preview, progress, outputs) must never queue behind each other on a small pool
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, 32), Math.Max(io, 32));
        _cache = new SegmentCache(Path.Combine(dataRoot, "cache", "segments"));
        _logDir = Path.Combine(dataRoot, "logs");
        _tempDir = Path.Combine(dataRoot, "cache", "overlays");
        _pipeDir = Path.Combine(dataRoot, "cache", "pipes");
        Directory.CreateDirectory(_tempDir);
        _cameras = new CameraBridge(_pipeDir);
        _cameras.Error += msg => Message?.Invoke(this, new EngineMessageEventArgs(msg, true));
        _windows = new WindowCaptureBridge(_pipeDir, title => _platform.FindWindow(title), hwnd => _platform.WindowClientSize(hwnd));
        _windows.Message += (msg, err) =>
        {
            if (!_windows.GpuCapture) GpuWindowCaptureSupported = false;
            Message?.Invoke(this, new EngineMessageEventArgs(msg, err));
        };
        _browsers = new BrowserBridge(_pipeDir, src => _platform.OpenBrowser(src));
        _outputs = new OutputManager(() => FfmpegPath, _logDir);
        _outputs.Message += (text, err) => Message?.Invoke(this, new EngineMessageEventArgs(text, err));
        _outputs.Changed += OnOutputsChanged;
        _ = Task.Run(MonitorLoopAsync);
        _ = Task.Run(CommandPumpAsync);
    }

    public async Task ProbeEncodersAsync()
    {
        if (FfmpegPath is null) return;
        try { Encoders = await EncoderCatalog.ProbeAsync(FfmpegPath, _life.Token); }
        catch { Encoders = Array.Empty<ProbedEncoder>(); }
        // Feature probe instead of version parsing (builds are named n9.0…, N-12345…, etc.).
        var (code, _) = await EncoderCatalog.RunQuietAsync(FfmpegPath, new[] { "-hide_banner", "-loglevel", "error",
            "-thread_queue_size", "16", "-f", "lavfi", "-i", "nullsrc=s=16x16:d=0.1", "-f", "null", "-" }, TimeSpan.FromSeconds(10), _life.Token);
        InputThreadQueueSupported = code == 0;
        var (_, help) = await EncoderCatalog.RunQuietAsync(FfmpegPath, new[] { "-hide_banner", "-h", "filter=gfxcapture" },
            TimeSpan.FromSeconds(10), _life.Token);
        GpuWindowCaptureSupported = help.Contains("gfxcapture AVOptions", StringComparison.Ordinal);
        _windows.GpuCapture = GpuWindowCaptureSupported;
        // GPU crop + scale of game windows (Direct3D 11 video processor): the CPU only receives the finished picture
        var (_, d3dHelp) = await EncoderCatalog.RunQuietAsync(FfmpegPath, new[] { "-hide_banner", "-h", "filter=scale_d3d11" },
            TimeSpan.FromSeconds(10), _life.Token);
        _windows.GpuScale = GpuWindowCaptureSupported && d3dHelp.Contains("scale_d3d11 AVOptions", StringComparison.Ordinal);
        EncodersProbed = true;
    }

    /// <summary>The main (horizontal) encode these settings would use.</summary>
    public EncodePlan PlanFor(StudioSettings s) => PlansFor(s).Main;

    /// <summary>Every encode for these settings and the current outputs.</summary>
    public PlanSet PlansFor(StudioSettings s)
    {
        var pool = Pool(s);
        bool vertical = s.VerticalCanvas || _outputs.ActiveOutputs().Any(o => o.IsVertical);
        var set = OutputBudget.Resolve(s, pool, vertical, Encoders, Environment.ProcessorCount, _bitrateScale);
        var plan = set.Main;
        var reasons = plan.Reasons.ToList();
        var shown = _scenes.SelectMany(sc => PipelineBuilder.VisibleLayers(sc.Sources)).ToList();
        // Old-style (GDI) capture can't deliver 60 smooth frames from a DirectX game; 60 FPS output would just
        // repeat frames unevenly (judder). Cap at 30 when it's in use.
        bool gdi = shown.Any(x =>
            (SourceTypes.KindOf(x.Type) == SourceKind.Window
             && (!GpuWindowCaptureSupported || string.Equals(x.CaptureMethod, "GDI", StringComparison.OrdinalIgnoreCase)))
            || (SourceTypes.KindOf(x.Type) == SourceKind.Display
                && (_gpuDisplayFailed || string.Equals(x.CaptureMethod, "GDI", StringComparison.OrdinalIgnoreCase))));
        if (_guardStep >= 1 && plan.Fps > 30)
        {
            plan = plan with { Fps = 30 };
            reasons.Add("Running at 30 FPS for now: this PC fell behind real time at the higher frame rate.");
        }
        if (_guardStep >= 2 && plan.Height > 540)
        {
            int w = Math.Max(960, SmartTuner.Even((int)(plan.Width * 0.8))), h = Math.Max(540, SmartTuner.Even((int)(plan.Height * 0.8)));
            plan = plan with { Width = w, Height = h };
            reasons.Add($"Running at {w}×{h} for now: this PC still fell behind real time at 30 FPS.");
        }
        bool capFps = gdi && plan.Fps > 30;
        if (capFps)
        {
            plan = plan with { Fps = 30 };
            reasons.Add("Capture is using the slower GDI method, so the stream runs at 30 FPS to stay smooth.");
        }
        bool wizard = shown.Any(x => x.WindowTitle.Contains("Wizard101", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("Wizard101", StringComparison.OrdinalIgnoreCase));
        if (wizard && s.ContentType == "HighMotion")
            reasons.Add("Tip: Wizard101 is turn-based. \"Low-motion\" (30 FPS) looks the same on stream, halves the data and leaves more power for smooth capture.");
        plan = plan with { Reasons = reasons };
        // every canvas runs at the main frame rate (one shared compositor clock)
        var vplan = set.Vertical is null ? null : set.Vertical with { Fps = plan.Fps };
        var own = set.Own.ToDictionary(kv => kv.Key, kv => kv.Value with { Fps = plan.Fps });
        return new PlanSet(plan, vplan, own) { UploadKbps = set.UploadKbps };
    }

    // ------------------------------------------------------------------ public operations

    /// <summary>
    /// Updates the scene collection / active scene / settings. Switching the active scene while running is a
    /// seamless in-encoder transition, and moving sources is applied live; adding/removing/resizing sources (or
    /// <paramref name="restartIfRunning"/>) rebuilds the pipeline.
    /// </summary>
    public Task UpdateAsync(IReadOnlyList<StudioScene> scenes, StudioScene active, StudioSettings settings,
        bool restartIfRunning) => Locked(async () =>
    {
        var list = scenes.Count > 0 ? scenes.ToList() : new List<StudioScene> { active };
        int index = list.FindIndex(s => ReferenceEquals(s, active));
        if (index < 0) index = list.FindIndex(s => s.Id == active.Id);
        if (index < 0) { list.Add(active); index = list.Count - 1; }
        _scenes = list;
        Scene = active;
        Settings = settings.Clone();
        _outputs.AutoReconnect = Settings.AutoReconnect;

        if (_runner is null) { _activeIndex = index; return; }
        bool structural = RunnerFingerprint(_scenes) != _runnerFingerprint;
        if (restartIfRunning) _guardStep = 0; // new settings from the user: give them a fair try
        if (restartIfRunning || structural)
        {
            _activeIndex = index;
            // Audio devices are only opened while the engine runs (no "app is using your microphone" at idle).
            _audio.Configure(AudioSources());
            await RestartAsync();
            return;
        }
        if (index != _activeIndex)
        {
            Transition(_activeIndex, index);
            _activeIndex = index;
        }
        RefreshLayout();
        await ReconcileAsync(); // e.g. vertical canvas switched on, an output enabled with its own encoder
    });

    public Task UpdateAsync(StudioScene scene, StudioSettings settings, bool restartIfRunning) =>
        UpdateAsync(new[] { scene }, scene, settings, restartIfRunning);

    /// <summary>
    /// Call while dragging sources on the preview: re-reads every source's X/Y (and VX/VY) and moves the running
    /// overlays to match, without a restart. Cheap; safe to call on every mouse move.
    /// </summary>
    public void RefreshLayout()
    {
        if (_runner is null || _runnerSpec is null || _runnerRequest is null) return;
        var want = DesiredPositions(_runnerSpec, _runnerRequest);
        lock (_posLock)
            foreach (var (k, v) in want) _wantPos[k] = v;
    }

    private Dictionary<string, (string x, string y)> DesiredPositions(PipelineSpec spec, PipelineRequest req)
    {
        var d = new Dictionary<string, (string, string)>();
        HashSet<string> names;
        lock (_posLock) names = _overlayNames;
        int W = req.Plan.Width, H = req.Plan.Height;
        foreach (var sc in _scenes)
            foreach (var src in sc.Sources.ToList())
            {
                string n = PipelineBuilder.OverlayName(src.Id, CanvasKind.Horizontal);
                if (names.Contains(n)) d[n] = PipelineBuilder.HorizontalPosition(src, W, H);
            }
        if (spec.HasVertical && req.VerticalCanvas is { } vc)
        {
            foreach (var sc in _scenes)
            {
                var layers = PipelineBuilder.VerticalLayers(sc.Sources.ToList())
                    .Where(s => names.Contains(PipelineBuilder.OverlayName(s.Id, CanvasKind.Vertical))).ToList();
                bool hasCam = layers.Any(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera && x.VAuto);
                foreach (var src in layers)
                {
                    var rect = LayoutMath.Vertical(src, vc.W, vc.H, PipelineBuilder.AspectFor(src, _knownSizes), hasCam);
                    d[PipelineBuilder.OverlayName(src.Id, CanvasKind.Vertical)] = (LayoutMath.Px(rect.X), LayoutMath.Px(rect.Y));
                }
            }
        }
        return d;
    }

    /// <summary>
    /// ffmpeg reads one runtime command per ~100 ms, so moves are coalesced: only the newest position of each overlay
    /// is sent, one axis per tick. A fast drag never builds up a backlog.
    /// </summary>
    private async Task CommandPumpAsync()
    {
        while (!_life.IsCancellationRequested)
        {
            try { await Task.Delay(110, _life.Token); } catch { return; }
            try
            {
                var r = _runner;
                if (r is null || DateTime.UtcNow < _pumpPausedUntil) continue;
                string? line = null;
                lock (_posLock)
                {
                    foreach (var (k, want) in _wantPos)
                    {
                        bool known = _sentPos.TryGetValue(k, out var sent);
                        if (!known || sent.x != want.x) { line = $"{k} -1 x {want.x}"; _sentPos[k] = (want.x, known ? sent.y : ""); break; }
                        if (sent.y != want.y) { line = $"{k} -1 y {want.y}"; _sentPos[k] = want; break; }
                    }
                }
                if (line is not null) r.SendCommands(new[] { line });
            }
            catch { }
        }
    }

    private void Transition(int from, int to)
    {
        var r = _runner; var spec = _runnerSpec;
        if (r is null || spec is null) return;
        double seconds = Settings.Transition == "Cut" ? 0.001 : Math.Clamp(Settings.TransitionMs, 100, 3000) / 1000.0;
        PipelineBuilder.TransitionCommands(spec, from, to, 0, seconds, out int lineCount);
        // Schedule against the pipeline's own clock (ffmpeg's latest output time, which tracks the frames being
        // composited), not the wall clock: if the machine falls behind real time, a wall-clock start time would
        // lie seconds in the future and the switch would appear frozen. Start after every command line has landed
        // (ffmpeg reads one per ~100 ms).
        var (wall, reported) = StreamClock();
        bool fresh = _lastProgress is not null && (DateTime.UtcNow - _lastProgressAt) < TimeSpan.FromSeconds(1.5);
        double now = fresh && !double.IsNaN(reported) ? reported : wall;
        double startAt = now + 0.15 * lineCount + 0.2;
        var lines = PipelineBuilder.TransitionCommands(spec, from, to, startAt, seconds, out _);
        _pumpPausedUntil = DateTime.UtcNow + TimeSpan.FromSeconds(0.15 * lines.Count + 0.3); // keep stdin free for the fade
        r.SendCommands(lines);
    }

    private string RunnerFingerprint(IEnumerable<StudioScene> scenes)
    {
        var req = _runnerRequest;
        int w = req?.Plan.Width ?? 1920, h = req?.Plan.Height ?? 1080;
        return PipelineBuilder.LayoutFingerprint(scenes, w, h, req?.VerticalCanvas);
    }

    public Task SetPreviewAsync(bool on) => Locked(async () => { PreviewOn = on; await ReconcileAsync(); });

    // ------------------------------------------------------------------ multistream outputs

    /// <summary>Why this output can't start (null = fine).</summary>
    public static string? ValidateOutput(StreamOutput o, string key)
    {
        string url = o.ServerUrl.Trim();
        if (url.Length == 0)
            return o.Service == "TikTok"
                ? "paste the Server URL from TikTok LIVE Producer (livecenter.tiktok.com/producer → Save & Go LIVE)."
                : "add the server URL.";
        if (!url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
            return "the server URL must start with rtmp:// or rtmps://.";
        if (key.Trim().Length == 0 && o.Service != "Custom")
            return o.Service == "TikTok" ? "paste this stream's key from TikTok LIVE Producer (it changes every stream)." : "add the stream key.";
        return null;
    }

    /// <summary>
    /// Starts one or more outputs (each with an optional key typed just now). Outputs on a canvas that is already
    /// being encoded join instantly without touching the others. Returns problems for outputs that couldn't start.
    /// </summary>
    public Task<IReadOnlyList<string>> StartOutputsAsync(IEnumerable<(Guid Id, string? Key)> requests) => Locked<IReadOnlyList<string>>(async () =>
    {
        var problems = new List<string>();
        var started = new List<Guid>();
        bool wasActive = _outputs.AnyActive;
        _outputs.AutoReconnect = Settings.AutoReconnect;
        foreach (var (id, keyOverride) in requests)
        {
            var o = Settings.Outputs.FirstOrDefault(x => x.Id == id);
            if (o is null) { problems.Add("That output no longer exists."); continue; }
            if (_outputs.IsActive(id)) continue;
            string key = (keyOverride ?? KeyResolver?.Invoke(o) ?? "").Trim();
            if (ValidateOutput(o, key) is { } why) { problems.Add($"{o.Name}: {why}"); continue; }
            _outputs.Start(o, Platforms.BuildUrl(o.ServerUrl, key));
            started.Add(id);
        }
        if (started.Count > 0)
        {
            if (!wasActive)
            {
                _bitrateScale = 1.0;
                _liveSince = DateTime.UtcNow;
                _countedSegments.Clear();
                _liveBytes = 0;
                _reconnectAttempt = 0;
            }
            try { await ReconcileAsync(requireSuccess: true); }
            catch
            {
                foreach (var id in started) await _outputs.StopAsync(id);
                if (!_outputs.AnyActive) _liveSince = null;
                try { await ReconcileAsync(); } catch { }
                Publish();
                throw;
            }
        }
        Publish();
        return problems;
    });

    public Task<IReadOnlyList<string>> StartOutputAsync(Guid id, string? key = null) => StartOutputsAsync(new[] { (id, key) });

    /// <summary>Starts every enabled output (keys from <see cref="KeyResolver"/> unless given).</summary>
    public Task<IReadOnlyList<string>> StartAllOutputsAsync(IReadOnlyDictionary<Guid, string>? keys = null) =>
        StartOutputsAsync(Settings.Outputs.Where(o => o.Enabled)
            .Select(o => (o.Id, keys is not null && keys.TryGetValue(o.Id, out var k) ? k : (string?)null)).ToList());

    public Task StopOutputAsync(Guid id) => Locked(async () =>
    {
        await _outputs.StopAsync(id);
        if (!_outputs.AnyActive) _liveSince = null;
        await ReconcileAsync();
        Publish();
    });

    public Task StopAllOutputsAsync() => Locked(async () =>
    {
        await _outputs.StopAllAsync();
        _liveSince = null;
        await ReconcileAsync();
        Publish();
    });

    public void ClearOutputError(Guid id) => _outputs.ClearError(id);

    private void OnOutputsChanged()
    {
        // an output stopped itself (failed before going live) or changed state: drop encodes nobody needs
        _ = Locked(async () =>
        {
            if (!_outputs.AnyActive) _liveSince = null;
            if (_runner is not null && DesiredShape() != _runnerShape)
            {
                try { await ReconcileAsync(); } catch (Exception ex) { Message?.Invoke(this, new EngineMessageEventArgs(ex.Message, true)); }
            }
            Publish();
        });
    }

    // ------------------------------------------------------------------ recording / replay

    public Task StartRecordingAsync() => Locked(async () =>
    {
        if (_cache.IsRecording) return;
        _cache.StartRecording();
        try { await ReconcileAsync(requireSuccess: true); }
        catch
        {
            try { await _cache.StopRecordingAsync(FfmpegPath!, Path.Combine(_tempDir, "discard.mp4")); } catch { }
            try { await ReconcileAsync(); } catch { }
            throw;
        }
    });

    public async Task<string> StopRecordingAsync()
    {
        Task<string>? export = null;
        await Locked(async () =>
        {
            if (!_cache.IsRecording) return;
            Directory.CreateDirectory(Settings.RecordingFolder);
            string file = Path.Combine(Settings.RecordingFolder, $"Leapcast {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            export = _cache.StopRecordingAsync(RequireFfmpeg(), file); // captures the range synchronously
            await ReconcileAsync();
        });
        return export is null ? "" : await export;
    }

    public Task SetReplayBufferAsync(bool on) => Locked(async () =>
    {
        ReplayBuffer = on;
        try { await ReconcileAsync(requireSuccess: on); }
        catch { ReplayBuffer = false; try { await ReconcileAsync(); } catch { } throw; }
    });

    public async Task<string> SaveReplayAsync()
    {
        if (!(_runnerShape?.Main == true && _runner is not null))
            throw new InvalidOperationException("Turn on the replay buffer (or go live / record) first. Replays are cut from the encoded stream.");
        Directory.CreateDirectory(Settings.RecordingFolder);
        string file = Path.Combine(Settings.RecordingFolder, $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        return await _cache.SaveReplayAsync(RequireFfmpeg(), Math.Clamp(Settings.ReplaySeconds, 5, 600), file);
    }

    public sealed record BenchmarkResult(double Fps, int TargetFps, double Speed, long Dropped, string Health)
    {
        /// <summary>Kept up with real time at (nearly) the full frame rate.</summary>
        public bool Smooth => Fps >= TargetFps * 0.93 && (Speed <= 0 || Speed >= 0.97);
    }

    /// <summary>
    /// Auto-config stress test: runs the real pipeline (capture, every scene, encode) with <paramref name="candidate"/>
    /// for a few seconds and measures whether it keeps up; then restores the previous settings and modes.
    /// </summary>
    public async Task<BenchmarkResult> BenchmarkAsync(StudioSettings candidate, int seconds = 10, CancellationToken ct = default)
    {
        if (Live) throw new InvalidOperationException("End the stream before running auto-config.");
        if (Scene is null) throw new InvalidOperationException("No scene selected.");
        var oldSettings = Settings;
        bool oldReplay = ReplayBuffer;
        var scenes = _scenes.ToList();
        var scene = Scene;
        try
        {
            await UpdateAsync(scenes, scene, candidate, restartIfRunning: true);
            await SetReplayBufferAsync(true); // encodes like a stream does
            await Task.Delay(2500, ct);        // devices settle, encoder look-ahead fills
            lock (_progressWindow) _progressWindow.Clear();
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 4, 30)), ct);
            var (fps, speed, _) = WindowedRates();
            var p = _lastProgress;
            return new BenchmarkResult(fps, CurrentPlan?.Fps ?? candidate.FrameRate, speed, p?.DroppedFrames ?? 0, _health);
        }
        finally
        {
            try { if (!oldReplay) await SetReplayBufferAsync(false); } catch { }
            try { await UpdateAsync(scenes, scene, oldSettings, restartIfRunning: true); } catch { }
        }
    }

    /// <summary>The game window's real size as captured (null until its capture has started once).</summary>
    public (int W, int H)? CaptureSize(StudioSource s)
    {
        if (SourceTypes.KindOf(s.Type) == SourceKind.Window)
            lock (_windows.CaptureSizes)
                if (_windows.CaptureSizes.TryGetValue(PipelineBuilder.InputKey(s), out var v)) return v;
        return SourceSize(s);
    }

    /// <summary>
    /// Clap test: records the next <paramref name="seconds"/> (the user claps a few times in view of the camera)
    /// and measures how late the camera's picture is compared with the sound. Add the result to the camera's
    /// <see cref="StudioSource.SyncOffsetMs"/>.
    /// </summary>
    public async Task<SyncAnalyzer.Result?> MeasureCameraSyncAsync(StudioSource camera, int seconds = 8, CancellationToken ct = default)
    {
        var ffmpeg = RequireFfmpeg();
        var scene = Scene ?? throw new InvalidOperationException("No scene selected.");
        if (!scene.Sources.Contains(camera) || !camera.Visible)
            throw new InvalidOperationException("Show the camera in the current scene first.");
        if (!scene.Sources.Any(x => x.Visible && SourceTypes.KindOf(x.Type) == SourceKind.Microphone))
            throw new InvalidOperationException("Add your microphone to the current scene first (the clap is heard through it).");
        bool hadReplay = ReplayBuffer;
        if (!(_runnerShape?.Main == true && _runner is not null)) await SetReplayBufferAsync(true);
        string tmp = Path.Combine(_tempDir, $"sync-{Guid.NewGuid():N}.mp4");
        long mark = _cache.Mark(); // exactly the claps from here on, never older footage from before a restart
        SyncAnalyzer.Result? result = null;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 4, 30)), ct);
            await _cache.ExportSinceAsync(ffmpeg, mark, tmp, ct);
            var plan = CurrentPlan ?? throw new InvalidOperationException("The pipeline stopped.");
            var layers = PipelineBuilder.VisibleLayers(scene.Sources).Where(x => SourceTypes.IsVideo(SourceTypes.KindOf(x.Type))).ToList();
            if (!layers.Contains(camera)) throw new InvalidOperationException("The camera is hidden behind a full-screen source in this scene.");
            var rect = LayoutMath.IsBaseLayer(camera, layers)
                ? LayoutMath.FillRect(camera, plan.Width, plan.Height, SourceAspect(camera))
                : LayoutMath.Horizontal(camera, plan.Width, plan.Height, SourceAspect(camera));
            double x0 = Math.Max(0, rect.X), y0 = Math.Max(0, rect.Y);
            var visible = new LayoutRect(x0, y0, Math.Min(plan.Width, rect.Right) - x0, Math.Min(plan.Height, rect.Bottom) - y0);
            result = await SyncAnalyzer.MeasureAsync(ffmpeg, tmp, visible, plan.Width, plan.Height, ct);
            return result;
        }
        finally
        {
            try
            {
                _cache.Release(mark);
                // keep the last unreadable attempt for diagnosis (Settings → Open log folder)
                if (result is not { Reliable: true } && File.Exists(tmp))
                    File.Copy(tmp, Path.Combine(_logDir, "sync-last-failed.mp4"), true);
                if (Environment.GetEnvironmentVariable("LEAPCAST_KEEP_SYNC") is { Length: > 0 } keep && File.Exists(tmp))
                    File.Copy(tmp, Path.Combine(keep, Path.GetFileName(tmp)), true);
                File.Delete(tmp);
            }
            catch { }
            if (!hadReplay) { try { await SetReplayBufferAsync(false); } catch { } }
        }
    }

    public async Task StopAllAsync()
    {
        await Locked(async () =>
        {
            ReplayBuffer = false; PreviewOn = false;
            CancelReconnect();
            await _outputs.StopAllAsync();
            _liveSince = null;
            await StopRunnerAsync();
            CloseDevices();
        });
        if (_cache.IsRecording) { try { await StopRecordingAsync(); } catch { } }
    }

    // ------------------------------------------------------------------ pipeline management

    private List<StudioSource> AudioSources() => PipelineBuilder.AudioRepresentatives(_scenes);

    /// <summary>Enabled outputs plus any running ones: every canvas encode they need is kept warm while live.</summary>
    private List<StreamOutput> Pool(StudioSettings s)
    {
        var list = s.Outputs.Where(o => o.Enabled).ToList();
        foreach (var a in _outputs.ActiveOutputs())
            if (!list.Any(o => o.Id == a.Id)) list.Add(a);
        return list;
    }

    private Shape DesiredShape()
    {
        bool anyOut = _outputs.AnyActive;
        var pool = anyOut ? Pool(Settings) : new List<StreamOutput>();
        bool vCanvas = Settings.VerticalCanvas || _outputs.ActiveOutputs().Any(o => o.IsVertical);
        bool mainBus = pool.Any(o => !o.IsVertical && o.OwnBitrateKbps <= 0);
        bool main = ReplayBuffer || _cache.IsRecording || mainBus;
        bool vEnc = vCanvas && pool.Any(o => o.IsVertical && o.OwnBitrateKbps <= 0);
        string own = string.Join(",", pool.Where(o => o.OwnBitrateKbps > 0 && (!o.IsVertical || vCanvas))
            .Select(o => $"{o.Id:N}:{o.OwnBitrateKbps}:{(o.IsVertical ? "v" : "h")}").OrderBy(x => x, StringComparer.Ordinal));
        bool run = PreviewOn || main || vEnc || own.Length > 0;
        return new Shape(run, main, mainBus, vCanvas, vEnc, own);
    }

    private async Task ReconcileAsync(bool requireSuccess = false)
    {
        var want = DesiredShape();
        if (!want.Run) { await StopRunnerAsync(); CloseDevices(); Publish(); return; }
        if (_runner is not null && want == _runnerShape) return;
        await StopRunnerAsync();
        await StartRunnerAsync(requireSuccess);
    }

    /// <summary>Nothing is running: release cameras, game capture, web pages and audio devices.</summary>
    private void CloseDevices()
    {
        _audio.Configure(Array.Empty<StudioSource>());
        _cameras.CloseAll();
        _windows.CloseAll();
        _browsers.CloseAll();
    }

    private async Task RestartAsync()
    {
        await StopRunnerAsync();
        if (DesiredShape().Run) await StartRunnerAsync(false);
    }

    private string RequireFfmpeg() =>
        FfmpegPath ?? throw new InvalidOperationException("FFmpeg wasn't found. Reinstall Leapcast or set the FFmpeg path in Settings → Advanced.");

    private async Task StartRunnerAsync(bool requireSuccess)
    {
        var ffmpeg = RequireFfmpeg();
        if (Scene is null) throw new InvalidOperationException("No scene selected.");
        var shape = DesiredShape();
        var plans = PlansFor(Settings);
        var plan = plans.Main;
        CurrentPlans = plans;
        CurrentPlan = plan;
        VerticalPlan = shape.Vertical ? plans.Vertical : null;
        bool encode = shape.Main || shape.VerticalEncode || shape.Own.Length > 0;
        var vSize = LayoutMath.VerticalSize(Settings.VerticalResolution);

        // Pre-render text overlays.
        var rendered = new Dictionary<Guid, string>();
        foreach (var s in _scenes.SelectMany(sc => sc.Sources).Where(s => s.Visible && SourceTypes.KindOf(s.Type) == SourceKind.Text))
        {
            try { if (_platform.RenderText(s, plan.Height) is { } png) rendered[s.Id] = png; } catch { }
        }

        _audio.Configure(AudioSources());
        double origin = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        IReadOnlyDictionary<Guid, string> pipes = new Dictionary<Guid, string>();
        if (encode) pipes = _audio.OpenPipes(origin);

        // Webcams: one warm reader per device, fed to this run through counted pipes.
        var camUses = new Dictionary<string, (InputSpec device, List<StudioSource> uses)>();
        var shownVideo = _scenes.SelectMany(sc => PipelineBuilder.VisibleLayers(sc.Sources)
            .Concat(shape.Vertical ? PipelineBuilder.VerticalLayers(sc.Sources) : Enumerable.Empty<StudioSource>()));
        foreach (var src in shownVideo.Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera))
        {
            string key = PipelineBuilder.InputKey(src);
            if (camUses.TryGetValue(key, out var e)) { if (!e.uses.Contains(src)) e.uses.Add(src); continue; }
            var dev = CameraDeviceOverride?.Invoke(src) ?? PipelineBuilder.CameraDeviceInput(src);
            if (dev is not null) camUses[key] = (dev, new List<StudioSource> { src });
        }
        var camFeeds = _cameras.Open(ffmpeg, camUses, plan.Width, plan.Height, plan.Fps, origin);
        var winFeeds = _windows.Open(ffmpeg, WindowNeeds(plan, shape.Vertical ? vSize : null), plan.Fps, origin);
        var browserUses = new Dictionary<string, StudioSource>();
        foreach (var src in _scenes.SelectMany(sc => PipelineBuilder.VisibleLayers(sc.Sources)
                     .Concat(shape.Vertical ? PipelineBuilder.VerticalLayers(sc.Sources) : Enumerable.Empty<StudioSource>()))
                     .Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Browser && x.Url.Trim().Length > 0))
            browserUses.TryAdd(PipelineBuilder.InputKey(src), src);
        var browserFeeds = _browsers.Open(browserUses, origin, w => Message?.Invoke(this, new EngineMessageEventArgs(w, true)));
        lock (_windows.CaptureSizes) foreach (var (k, v) in _windows.CaptureSizes) _knownSizes[k] = v;
        string? segPattern = shape.Main ? _cache.BeginSession() : null;

        // One TS bus per encode that feeds destinations; outputs subscribe/unsubscribe at will.
        var buses = new Dictionary<string, TsBus>();
        var extra = new List<(string BusPath, bool Vertical, EncodePlan Plan)>();
        if (shape.MainBus) buses["main"] = new TsBus(_pipeDir, "main");
        if (shape.VerticalEncode) buses["vertical"] = new TsBus(_pipeDir, "vertical");
        if (shape.Own.Length > 0)
            foreach (var o in Pool(Settings).Where(o => o.OwnBitrateKbps > 0 && (!o.IsVertical || shape.Vertical)))
            {
                string k = OutputManager.BusKey(o);
                if (buses.ContainsKey(k) || !plans.Own.TryGetValue(o.Id, out var op)) continue;
                var bus = new TsBus(_pipeDir, "own" + extra.Count);
                buses[k] = bus;
                extra.Add((bus.Path, o.IsVertical, op));
            }

        var request = new PipelineRequest
        {
            Scenes = _scenes,
            ActiveSceneIndex = _activeIndex,
            Plan = plan,
            MainBusPath = buses.TryGetValue("main", out var mb) ? mb.Path : null,
            VerticalCanvas = shape.Vertical ? vSize : null,
            VerticalPlan = shape.Vertical ? plans.Vertical : null,
            VerticalBusPath = buses.TryGetValue("vertical", out var vb) ? vb.Path : null,
            ExtraEncodes = extra,
            KnownSizes = new Dictionary<string, (int, int)>(_knownSizes),
            SegmentPattern = segPattern,
            SegmentSeconds = _cache.SegmentSeconds,
            Preview = true,
            PreviewWidth = PreviewWidthPx, PreviewHeight = PreviewHeightPx,
            AudioInputs = pipes,
            RenderedImages = rendered,
            MonitorBounds = _platform.MonitorBounds,
            FindWindow = _platform.FindWindow,
            GpuWindowCapture = GpuWindowCaptureSupported,
            GpuDisplayCapture = !_gpuDisplayFailed,
            AudioBitrateKbps = Math.Clamp(Settings.AudioBitrateKbps, 64, 320),
            WallclockOrigin = origin,
            InputOverride = InputOverride,
            InputThreadQueueSupported = InputThreadQueueSupported,
            CameraInputs = camFeeds,
            WindowInputs = winFeeds,
            BrowserInputs = browserFeeds,
        };
        PipelineSpec spec;
        try { spec = PipelineBuilder.Build(request); }
        catch { foreach (var b in buses.Values) b.Dispose(); throw; }
        _runnerSpec = spec;
        _runnerRequest = request;
        _runnerOrigin = request.WallclockOrigin;
        _runnerFingerprint = RunnerFingerprint(_scenes);
        var (phw, pvw, ph) = spec.PreviewLayout;
        PreviewFrame = (phw + pvw, ph, phw, pvw);
        foreach (var w in spec.Warnings) Message?.Invoke(this, new EngineMessageEventArgs(w, false));
        lock (_posLock)
        {
            _overlayNames = Regex.Matches(spec.FilterGraph, @"overlay@[hv][0-9a-f]{32}").Select(m => m.Value).ToHashSet();
            _wantPos.Clear(); _sentPos.Clear();
        }
        // the graph was built with the current positions: that is what ffmpeg already has
        var initial = DesiredPositions(spec, request);
        lock (_posLock) foreach (var (k, v) in initial) { _wantPos[k] = v; _sentPos[k] = v; }

        PipelineStarts++;
        _runnerCommand = string.Join(" ", spec.Args);
        var runner = new FfmpegRunner(ffmpeg, spec.Args, PreviewFrame.Width, PreviewFrame.Height, Path.Combine(_logDir, "ffmpeg-last.log"));
        runner.Frame += f => FrameReady?.Invoke(f);
        runner.Progress += p =>
        {
            _lastProgress = p;
            _lastProgressAt = DateTime.UtcNow;
            lock (_progressWindow)
            {
                var nowT = DateTime.UtcNow;
                _progressWindow.Enqueue((nowT, p));
                while (_progressWindow.Count > 0 && nowT - _progressWindow.Peek().t > TimeSpan.FromSeconds(15)) _progressWindow.Dequeue();
            }
        };
        var parsing = new int[] { -1 }; // index of the "Input #n" block being printed
        runner.LogLine += line => OnLogLine(line, spec, parsing);
        _runner = runner;
        _runnerShape = shape;
        _buses = buses;
        _lastProgress = null;
        lock (_progressWindow) _progressWindow.Clear();
        _health = _outputs.AnyActive ? "Connecting…" : "Running";
        _ = runner.Completion.ContinueWith(t => OnRunnerExited(runner, t.Result), TaskScheduler.Default);
        _outputs.BindBuses(buses);

        if (requireSuccess)
        {
            // Give ffmpeg a moment: device/encoder errors surface within the first seconds.
            await Task.WhenAny(runner.Completion, WaitForFirstFrameAsync(runner, TimeSpan.FromSeconds(12)));
            if (runner.Completion.IsCompleted && !runner.StopRequested)
            {
                string err = Explain(runner.ErrorSummary());
                bool hadFrames = _lastProgress is { Frame: > 0 };
                await StopRunnerAsync();
                if (TryGpuCaptureFallback(DateTime.UtcNow - runner.StartedUtc, hadFrames))
                {
                    await StartRunnerAsync(requireSuccess); // flags changed, so this can't loop
                    return;
                }
                Publish();
                throw new InvalidOperationException(err);
            }
        }
        Publish();
    }

    /// <summary>
    /// What each game window's reader must deliver: the full canvas (already cropped/fitted) when the window fills the
    /// screen, otherwise its picture at the largest size any scene or canvas draws it.
    /// </summary>
    private Dictionary<string, WindowCaptureBridge.Need> WindowNeeds(EncodePlan plan, (int W, int H)? vertical)
    {
        int W = plan.Width, H = plan.Height;
        var fill = new Dictionary<string, WindowCaptureBridge.Need>();
        var widths = new Dictionary<string, (StudioSource src, double w)>();
        void Want(StudioSource s, double w)
        {
            string key = PipelineBuilder.InputKey(s);
            widths[key] = widths.TryGetValue(key, out var e) ? (e.src, Math.Max(e.w, w)) : (s, w);
        }
        foreach (var sc in _scenes)
        {
            var layers = PipelineBuilder.VisibleLayers(sc.Sources).Where(x => SourceTypes.IsVideo(SourceTypes.KindOf(x.Type))).ToList();
            foreach (var s in layers.Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Window && x.WindowTitle.Trim().Length > 0))
            {
                if (Anchors.Resolve(s) == Anchors.Fill) fill.TryAdd(PipelineBuilder.InputKey(s), new WindowCaptureBridge.Need(s, W, H, s.FillMode));
                else Want(s, LayoutMath.Horizontal(s, W, H, PipelineBuilder.AspectFor(s, _knownSizes)).W);
            }
            if (vertical is { } v)
                foreach (var s in PipelineBuilder.VerticalLayers(sc.Sources.ToList()).Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Window && x.WindowTitle.Trim().Length > 0))
                    Want(s, PipelineBuilder.VerticalWidth(s, v.W, v.H));
        }
        var needs = new Dictionary<string, WindowCaptureBridge.Need>(fill);
        foreach (var (key, (src, w)) in widths)
        {
            if (needs.ContainsKey(key)) continue;
            int ww = SmartTuner.Even((int)Math.Clamp(Math.Round(w), 64, 3840));
            int hh = SmartTuner.Even((int)Math.Clamp(Math.Round(ww / PipelineBuilder.AspectFor(src, _knownSizes)), 36, 2160));
            needs[key] = new WindowCaptureBridge.Need(src, ww, hh, Anchors.FillFit);
        }
        return needs;
    }

    private static readonly Regex InputHeader = new(@"^Input #(\d+),", RegexOptions.Compiled);
    private static readonly Regex VideoStream = new(@"^\s+Stream #(\d+):\d+.*?: Video: .*?\b(\d{2,5})x(\d{2,5})\b", RegexOptions.Compiled);

    /// <summary>Learns each input's real picture size from ffmpeg's log (exact layout instead of guessed aspect ratios).</summary>
    private void OnLogLine(string line, PipelineSpec spec, int[] parsing)
    {
        if (line.StartsWith("Output #", StringComparison.Ordinal) || line.StartsWith("Stream mapping", StringComparison.Ordinal)) { parsing[0] = -1; return; }
        var h = InputHeader.Match(line);
        if (h.Success) { parsing[0] = int.Parse(h.Groups[1].Value, CultureInfo.InvariantCulture); return; }
        int parsingInput = parsing[0];
        if (parsingInput < 0) return;
        var m = VideoStream.Match(line);
        if (!m.Success || int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) != parsingInput) return;
        if (!spec.InputKeys.TryGetValue(parsingInput, out var key)) return;
        if (key.StartsWith("window|", StringComparison.Ordinal)) return; // fed already cropped/filled: the bridge knows the real size
        var size = (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        if (_knownSizes.TryGetValue(key, out var old) && old == size) return;
        _knownSizes[key] = size;
        RefreshLayout(); // vertical auto layout depends on the real aspect ratios
    }

    private async Task WaitForFirstFrameAsync(FfmpegRunner runner, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !runner.Completion.IsCompleted)
        {
            if (_runner == runner && _lastProgress is { Frame: > 0 }) { await Task.Delay(1500); return; }
            await Task.Delay(200);
        }
    }

    private async Task StopRunnerAsync()
    {
        var r = _runner;
        if (r is null) return;
        _runner = null;
        _outputs.UnbindBuses();
        await r.StopAsync();
        r.Dispose();
        foreach (var b in _buses.Values) b.Dispose();
        _buses = new();
        bool encoded = _runnerShape is { } sh && (sh.Main || sh.VerticalEncode || sh.Own.Length > 0);
        if (_runnerShape?.Main == true) _cache.EndSession();
        if (encoded) _audio.ClosePipes();
        _cameras.ClosePipes(); // readers stay open (warm) for the next run
        _windows.ClosePipes();
        _browsers.ClosePipes();
        _runnerShape = null;
        _lastProgress = null;
        _health = "Idle";
    }

    private void OnRunnerExited(FfmpegRunner runner, int code)
    {
        if (runner.StopRequested) return;
        _ = Locked(async () =>
        {
            if (_runner != runner) return; // already replaced
            string err = Explain(runner.ErrorSummary());
            var ranFor = DateTime.UtcNow - runner.StartedUtc;
            bool hadFrames = _lastProgress is { Frame: > 0 };
            await StopRunnerAsync();
            _lastError = err;
            if (TryGpuCaptureFallback(ranFor, hadFrames) && DesiredShape().Run)
            {
                try { await StartRunnerAsync(false); }
                catch (Exception ex) { Message?.Invoke(this, new EngineMessageEventArgs(ex.Message, true)); }
                Publish();
                return;
            }

            if (_outputs.AnyActive && Settings.AutoReconnect && ranFor > TimeSpan.FromSeconds(8))
            {
                // the encoder itself died while live: outputs wait (they stay "reconnecting") for the new one
                if (ranFor > TimeSpan.FromSeconds(60)) _reconnectAttempt = 0;
                int delay = new[] { 2, 4, 8, 15, 30 }[Math.Min(_reconnectAttempt, 4)];
                _reconnectAttempt++;
                Message?.Invoke(this, new EngineMessageEventArgs($"The encoder stopped ({err}). Restarting in {delay}s…", true));
                ScheduleReconnect(TimeSpan.FromSeconds(delay));
            }
            else
            {
                if (_outputs.AnyActive) { await _outputs.StopAllAsync(); _liveSince = null; }
                Message?.Invoke(this, new EngineMessageEventArgs(err, true));
                // keep recording/replay/preview going if possible
                if (DesiredShape().Run && ranFor > TimeSpan.FromSeconds(8))
                {
                    try { await StartRunnerAsync(false); } catch (Exception ex) { Message?.Invoke(this, new EngineMessageEventArgs(ex.Message, true)); }
                }
                else
                {
                    PreviewOn = false; ReplayBuffer = false;
                    if (!DesiredShape().Run) CloseDevices();
                }
                ModesChanged?.Invoke();
            }
            Publish();
        });
    }

    private void ScheduleReconnect(TimeSpan delay)
    {
        CancelReconnect();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        _health = "Restarting the encoder…";
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, cts.Token); } catch { return; }
            await Locked(async () =>
            {
                if (cts.IsCancellationRequested) return;
                _reconnectCts = null;
                if (!DesiredShape().Run || _runner is not null) return;
                try { await StartRunnerAsync(false); }
                catch (Exception ex)
                {
                    Message?.Invoke(this, new EngineMessageEventArgs("Restart failed: " + ex.Message, true));
                    ScheduleReconnect(TimeSpan.FromSeconds(15));
                }
            });
        });
    }

    private void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = null;
    }

    // ------------------------------------------------------------------ monitoring / adaptation

    private async Task MonitorLoopAsync()
    {
        while (!_life.IsCancellationRequested)
        {
            try { await Task.Delay(1000, _life.Token); } catch { return; }
            try
            {
                _cache.Housekeep(Math.Clamp(Settings.ReplaySeconds, 5, 600));
                if (_outputs.AnyActive && _runnerShape?.MainBus == true)
                {
                    foreach (var s in _cache.Scan())
                        if (s.Closed && s.Size > 0 && _countedSegments.Add(s.Key)) _liveBytes += s.Size;
                }
                EvaluateHealth();
                Publish();
            }
            catch { /* never let monitoring kill the app */ }
        }
    }

    /// <summary>Rolling (not since-start) output fps and speed over the last ~15 s of progress reports.</summary>
    /// <returns>fps, speed and the window length in seconds.</returns>
    private (double fps, double speed, double span) WindowedRates()
    {
        (DateTime t, ProgressInfo p)[] w;
        lock (_progressWindow) w = _progressWindow.ToArray();
        if (w.Length < 2) return (0, 0, 0);
        double dt = (w[^1].t - w[0].t).TotalSeconds;
        if (dt < 0.5) return (0, 0, dt);
        double fps = (w[^1].p.Frame - w[0].p.Frame) / dt;
        double speed = (w[^1].p.OutTime - w[0].p.OutTime).TotalSeconds / dt;
        return (fps, speed, dt);
    }

    private void EvaluateHealth()
    {
        var r = _runner;
        if (r is null) { if (!Reconnecting) _health = "Idle"; return; }
        var (fps, speed, span) = WindowedRates();
        var outs = _outputs.Snapshot();
        bool live = outs.Any(o => o.State == OutputState.Live);

        // Network: destinations are decoupled from the encoder, so a slow upload shows up as a growing backlog
        // in that output's queue (not as the encoder slowing down).
        var slow = outs.Where(o => o.State == OutputState.Live && o.BufferedSeconds > 2.5).ToList();
        if (slow.Count > 0)
        {
            _uploadTroubleSince ??= DateTime.UtcNow;
            _health = "Upload struggling: " + string.Join(", ", slow.Select(o => o.Name));
            if (Settings.AutoAdjustBitrate
                && DateTime.UtcNow - _uploadTroubleSince > TimeSpan.FromSeconds(10)
                && DateTime.UtcNow - _lastAdjust > TimeSpan.FromSeconds(60)
                && (CurrentPlan?.CapKbps ?? 0) > 1200)
            {
                _lastAdjust = DateTime.UtcNow;
                _uploadTroubleSince = null;
                _bitrateScale *= 0.75;
                _ = Locked(async () =>
                {
                    if (!_outputs.AnyActive || _runner is null) return;
                    Message?.Invoke(this, new EngineMessageEventArgs(
                        $"Your upload couldn't keep up ({string.Join(", ", slow.Select(o => o.Name))}), so the bitrate was lowered 25%. Outputs will reconnect for a second.", true));
                    await RestartAsync();
                });
            }
            return;
        }
        _uploadTroubleSince = null;
        if (outs.FirstOrDefault(o => o.State == OutputState.Reconnecting) is { } rc) { _health = $"Reconnecting {rc.Name}…"; return; }
        if (span < 2) return;
        // With wall-clock input timestamps the output only falls behind real time when this PC can't keep up.
        bool behind = span >= 8 && speed > 0 && speed < 0.95;
        double expected = CurrentPlan?.Fps ?? 30;
        bool dipping = fps > 0 && fps < expected * 0.9;
        if (behind)
        {
            _health = "Struggling: this PC can't keep up (try Auto-config)";
            _behindSince ??= DateTime.UtcNow;
            // Falling behind real time makes previews, recordings and streams stutter alike. After 10 s of it, take
            // the cheapest step that helps (60 → 30 FPS first, then a smaller picture), once a minute at most.
            if (DateTime.UtcNow - _behindSince > TimeSpan.FromSeconds(10) && DateTime.UtcNow - _lastGuard > TimeSpan.FromSeconds(60)
                && Settings.AutoAdjustBitrate && _guardStep < 2 && DateTime.UtcNow - r.StartedUtc > TimeSpan.FromSeconds(15))
            {
                int fpsNow = CurrentPlan?.Fps ?? 30, hNow = CurrentPlan?.Height ?? 720;
                int next = fpsNow > 30 ? 1 : hNow > 540 ? 2 : 0;
                if (next > _guardStep)
                {
                    _guardStep = next;
                    _lastGuard = DateTime.UtcNow;
                    _behindSince = null;
                    string what = next == 1 ? $"from {fpsNow} to 30 FPS" : "to a smaller picture";
                    _ = Locked(async () =>
                    {
                        if (_runner is null) return;
                        Message?.Invoke(this, new EngineMessageEventArgs(
                            $"This PC couldn't keep up, so Leapcast switched {what} to stay smooth (until you change settings). Auto-config can find the best fit.", true));
                        await RestartAsync();
                    });
                }
            }
            return;
        }
        _behindSince = null;
        _health = live ? (dipping ? "OK (frame rate dipping)" : "Excellent") : (dipping ? "Running (frame rate dipping)" : "Running");
    }

    public EngineStatus Snapshot()
    {
        var p = _lastProgress;
        var (wfps, wspeed, _) = WindowedRates();
        double recent = _runnerShape?.Main == true ? _cache.RecentKbps() : 0;
        double avg = _countedSegments.Count > 0 ? _liveBytes * 8.0 / 1000.0 / (_countedSegments.Count * _cache.SegmentSeconds) : 0;
        var outs = _outputs.Snapshot();
        return new EngineStatus(
            _runner is not null, _outputs.AnyActive, _cache.IsRecording, ReplayBuffer,
            Reconnecting || outs.Any(o => o.State == OutputState.Reconnecting),
            CurrentPlan?.Summary, CurrentPlan?.CapKbps ?? 0,
            wfps, p?.DroppedFrames ?? 0, p?.DupFrames ?? 0, wspeed,
            recent, avg,
            _liveSince is { } ls ? DateTime.UtcNow - ls : TimeSpan.Zero,
            _cache.RecordingStartedUtc is { } rs ? DateTime.UtcNow - rs : TimeSpan.Zero,
            _health, _lastError)
        {
            Outputs = outs,
            VerticalSummary = VerticalPlan?.Summary,
            PlannedUploadKbps = CurrentPlans?.UploadKbps ?? 0,
            UploadKbps = outs.Sum(o => o.Kbps),
        };
    }

    private void Publish()
    {
        try { StatusChanged?.Invoke(Snapshot()); } catch { }
    }

    /// <summary>Turns raw ffmpeg errors into something a streamer can act on.</summary>
    public static string Explain(string raw)
    {
        string r = raw.Trim();
        if (r.Contains("Could not find video device", StringComparison.OrdinalIgnoreCase) || r.Contains("Could not enumerate video", StringComparison.OrdinalIgnoreCase))
            return "Camera not found. Pick it again in the camera source's properties (it may be in use by another app).";
        if (r.Contains("Could not set video options", StringComparison.OrdinalIgnoreCase))
            return "The camera doesn't support that resolution/FPS. Choose \"Default\" in the camera's properties.";
        if (r.Contains("Can't find window", StringComparison.OrdinalIgnoreCase) || r.Contains("title=", StringComparison.OrdinalIgnoreCase) && r.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "Game window not found. Open Wizard101 first, or pick the window again in the source's properties.";
        if (r.Contains("ddagrab", StringComparison.OrdinalIgnoreCase) || r.Contains("DXGI", StringComparison.OrdinalIgnoreCase) || r.Contains("Duplicat", StringComparison.OrdinalIgnoreCase))
            return "Screen capture failed (" + r + "). Try Capture method \"GDI\" in the display source's properties.";
        if (r.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) || r.Contains("Connection reset", StringComparison.OrdinalIgnoreCase)
            || r.Contains("Input/output error", StringComparison.OrdinalIgnoreCase) || r.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase)
            || r.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase) || r.Contains("Cannot open connection", StringComparison.OrdinalIgnoreCase)
            || r.Contains("Error opening output", StringComparison.OrdinalIgnoreCase) || r.Contains("Error number -138", StringComparison.Ordinal)
            || r.Contains("Error number -10061", StringComparison.Ordinal) || r.Contains("Error number -10054", StringComparison.Ordinal))
            return "Couldn't reach the streaming server. Check your internet connection, server URL and stream key.";
        if (r.Contains("403", StringComparison.Ordinal) || r.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) || r.Contains("NetStream.Publish.BadName", StringComparison.OrdinalIgnoreCase))
            return "The server rejected the stream key. Copy a fresh key (TikTok issues a new one every stream).";
        if (r.Contains("nvenc", StringComparison.OrdinalIgnoreCase) || r.Contains("qsv", StringComparison.OrdinalIgnoreCase) || r.Contains("amf", StringComparison.OrdinalIgnoreCase))
            return "The hardware encoder failed (" + r + "). Update your GPU driver or choose x264 in Settings.";
        return r.Length > 300 ? r[..300] + "…" : r;
    }

    private async Task Locked(Func<Task> action)
    {
        await _gate.WaitAsync();
        try { await action(); }
        finally { _gate.Release(); }
    }

    private async Task<T> Locked<T>(Func<Task<T>> action)
    {
        await _gate.WaitAsync();
        try { return await action(); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _life.Cancel();
        // Run on the thread pool so a UI SynchronizationContext can't deadlock the wait.
        try { Task.Run(StopAllAsync).Wait(TimeSpan.FromSeconds(10)); } catch { }
        _outputs.Dispose();
        _audio.Dispose();
        _cameras.Dispose();
        _windows.Dispose();
        _browsers.Dispose();
    }
}
