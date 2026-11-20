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
    /// <summary>Stream safety for desktop audio (applies from the next <see cref="OpenPipes"/>).</summary>
    void SetSafety(AudioSafetyConfig config) { }
    /// <summary>Delays desktop-audio pipes by the safety delay (the pipeline shifts everything else to match).</summary>
    bool DelaysDesktopAudio => false;
    /// <summary>Reopens one source's device in place (Refresh): the stream keeps going, a moment of silence at most.</summary>
    bool Reopen(Guid sourceId) => false;
}

public interface IPlatformServices
{
    /// <summary>Renders a text source to a transparent PNG sized for the output height. Null if empty.</summary>
    string? RenderText(StudioSource source, int outputHeight);
    /// <summary>
    /// Draws a timer card to a transparent PNG at <paramref name="path"/> (true on success). Null platforms and
    /// those without a drawing surface return false and the card is rendered through FFmpeg instead.
    /// </summary>
    bool RenderTimerCard(IReadOnlyList<TimerCard.Piece> pieces, int width, int height, string fontFile, string path) => false;
    (int X, int Y, int W, int H)? MonitorBounds(int index);
    /// <summary>Handle of the visible top-level window with exactly this title (null if not open).</summary>
    long? FindWindow(string title) => null;
    /// <summary>The n-th window with that title, in the order the clients were opened (several game clients share one title).</summary>
    long? FindWindow(string title, int instance) => instance <= 0 ? FindWindow(title) : null;
    /// <summary>Client-area size of a window in physical pixels (null if unknown).</summary>
    (int W, int H)? WindowClientSize(long hwnd) => null;
    /// <summary>Starts rendering a web page off-screen (null where browsers aren't supported).</summary>
    IFrameSource? OpenBrowser(StudioSource source) => null;
    /// <summary>Layers scenes on the graphics card (null where that isn't available).</summary>
    IGpuCanvas? CreateGpuCanvas(int width, int height, int fps, Func<string, GpuFrame?> frames) => null;
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
public sealed partial class StreamEngine : IDisposable
{
    private readonly IAudioBridge _audio;
    private readonly IPlatformServices _platform;
    private readonly SegmentCache _cache;
    private readonly string _segmentsRoot;
    /// <summary>The live 9:16 clip canvas's own recent segments (Settings → 9:16 clips).</summary>
    private readonly SegmentCache _clipCache;
    private readonly SegmentCache _vertCache;
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
    private sealed record Shape(bool Run, bool Main, bool MainBus, bool Vertical, bool VerticalEncode, string Own, bool Clip = false);

    private FfmpegRunner? _runner;
    private Shape? _runnerShape;
    private Dictionary<string, TsBus> _buses = new();
    private ProgressInfo? _lastProgress;
    private readonly Queue<(DateTime t, ProgressInfo p)> _progressWindow = new();
    private CancellationTokenSource? _reconnectCts;
    private int _reconnectAttempt;
    private DateTime? _liveSince;
    private readonly PerformanceGovernor _governor = new();
    private bool _governorPaused;
    /// <summary>This PC's class (decided after the encoder probe).</summary>
    public HardwareTier Tier { get; private set; } = HardwareTier.Mid;
    /// <summary>Current rung of the performance ladder (0 = as configured, or adaptation switched off).</summary>
    public PerfLevel Performance => PerfLevel.At(LevelFor(Settings));
    private int LevelFor(StudioSettings s) => s.AdaptivePerformance ? _governor.Level : 0;
    /// <summary>Tests / diagnostics: jump to a rung (applies on the next pipeline start).</summary>
    public void SimulatePerformanceLevel(int level) => _governor.Force(level);
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
    public int CameraReaderStarts => _cameras.Starts;
    /// <summary>Tests: behave as if the game had closed its window.</summary>
    public void SimulateWindowClosed(StudioSource s) => _windows.SimulateWindowClosed(PipelineBuilder.InputKey(s));
    /// <summary>Returns an output's saved stream key (decrypted). Set by the app.</summary>
    public Func<StreamOutput, string>? KeyResolver { get; set; }
    public IReadOnlyList<ProbedEncoder> Encoders { get; private set; } = Array.Empty<ProbedEncoder>();
    public bool EncodersProbed { get; private set; }
    public bool InputThreadQueueSupported { get; private set; } = true;
    /// <summary>FFmpeg 8+ "gfxcapture" (Windows.Graphics.Capture): GPU window capture that works with DirectX games.</summary>
    public bool GpuWindowCaptureSupported { get; private set; }
    private bool _gpuDisplayFailed, _gpuDisplayScaleFailed;
    private string _runnerCommand = "";

    /// <summary>
    /// If a pipeline using GPU screen capture (desktop duplication) died before producing a frame, it probably isn't
    /// available on this PC (old driver, remote desktop, no Direct3D 11): switch this session to GDI capture instead
    /// of failing. (Game windows have their own reader with the same fallback.)
    /// </summary>
    private bool TryGpuCaptureFallback(TimeSpan ranFor, bool producedFrames)
    {
        if (ranFor > TimeSpan.FromSeconds(15) || producedFrames) return false;
        if (!_runnerCommand.Contains("ddagrab=", StringComparison.Ordinal) || _gpuDisplayFailed) return false;
        // first without the GPU scaling (older drivers), then without GPU capture at all
        if (!_gpuDisplayScaleFailed && Regex.IsMatch(_runnerCommand, "ddagrab=[^ ]*scale_d3d11"))
        {
            _gpuDisplayScaleFailed = true;
            return true;
        }
        _gpuDisplayFailed = true;
        Message?.Invoke(this, new EngineMessageEventArgs("GPU capture failed. Using compatibility capture (30 FPS).", true));
        return true;
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
    public bool Recording => IsRecording;
    public bool Running => _runner is not null;
    public bool Reconnecting => _reconnectCts is not null;
    public EncodePlan? CurrentPlan { get; private set; }
    public EncodePlan? VerticalPlan { get; private set; }
    public PlanSet? CurrentPlans { get; private set; }
    /// <summary>How many times an ffmpeg pipeline has been started (diagnostics/tests: transitions must not bump it).</summary>
    public int PipelineStarts { get; private set; }
    /// <summary>Pipeline rebuilds the live destinations rode through without reconnecting.</summary>
    public int OutputHandovers => _outputs.Handovers;
    public string CameraStats() => _cameras.Stats();
    public bool IsOutputActive(Guid id) => _outputs.IsActive(id);
    /// <summary>Diagnostics/tests: process id of the running compositor/encoder.</summary>
    public int? PipelineProcessId => _runner?.ProcessId;
    public IReadOnlyList<OutputStatus> OutputStatuses() => _outputs.Snapshot();

    /// <summary>Preview frame layout: total width/height, and where the vertical canvas starts (0 = none).</summary>
    /// <summary>Preview frame: [staged scene (studio mode) | program | vertical canvas], side by side.</summary>
    public (int Width, int Height, int HorizontalWidth, int VerticalWidth, int StagedWidth) PreviewFrame { get; private set; } = (768, 432, 768, 0, 0);

    // ------------------------------------------------------------------ studio mode

    /// <summary>Studio mode: the Preview shows a staged scene next to the Program; "Transition" puts it on air.</summary>
    public bool StudioMode { get; private set; }
    private int _stagedIndex;

    /// <summary>Turns studio mode on/off (rebuilds the preview once; live outputs stay connected).</summary>
    public Task SetStudioModeAsync(bool on, StudioScene? staged = null) => Locked(async () =>
    {
        if (staged is not null && IndexOf(staged) is int i and >= 0) _stagedIndex = i; else _stagedIndex = _activeIndex;
        if (StudioMode == on) return;
        StudioMode = on;
        if (_runner is not null) await RestartAsync();
    });

    /// <summary>Studio mode: shows <paramref name="scene"/> in the Preview (instant; nothing changes on air).</summary>
    public void StageScene(StudioScene scene)
    {
        int i = IndexOf(scene);
        if (i < 0) return;
        _stagedIndex = i;
        var r = _runner; var spec = _runnerSpec;
        if (r is null || spec is null || spec.PreviewStageWidth == 0 || ActiveSceneOnly) return;
        if (spec.SceneCount > 1) r.SendCommands(new[] { $"{PipelineBuilder.StageFilter} -1 map {i}" });
        ApplyLiveState(); // the staged scene's big overlays draw, the previously staged one's stop
    }

    private int IndexOf(StudioScene scene)
    {
        int i = _scenes.FindIndex(s => ReferenceEquals(s, scene));
        return i >= 0 ? i : _scenes.FindIndex(s => s.Id == scene.Id);
    }

    /// <summary>The staged scene's index in the composited list (null when it isn't composited).</summary>
    private int? StagedComposited => StudioMode && !ActiveSceneOnly && _stagedIndex < _scenes.Count ? _stagedIndex : null;

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

    /// <summary>
    /// How far the encoder's output lags the frames being composited (its look-ahead / B-frame buffer, ~0–1.5 s):
    /// the smallest (wall − reported output time) seen in the progress window. Runtime commands act on the
    /// compositor's clock, which is the reported output time plus this.
    /// </summary>
    private double EncoderLatency()
    {
        double origin = _runnerOrigin, best = double.MaxValue;
        lock (_progressWindow)
            foreach (var (t, p) in _progressWindow)
            {
                double wall = new DateTimeOffset(t).ToUnixTimeMilliseconds() / 1000.0 - origin;
                best = Math.Min(best, wall - p.OutTime.TotalSeconds);
            }
        return best == double.MaxValue ? 0 : Math.Clamp(best, 0, 3 + (_runnerRequest?.SafetyDelay ?? 0));
    }

    /// <summary>The compositor's current time (what runtime filter commands are scheduled against).</summary>
    public double CompositorClock()
    {
        var (wall, reported) = StreamClock();
        bool fresh = _lastProgress is not null && (DateTime.UtcNow - _lastProgressAt) < TimeSpan.FromSeconds(1.5);
        return fresh && !double.IsNaN(reported) ? Math.Min(wall, reported + EncoderLatency()) : wall;
    }

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
        _lightTimer = new Timer(_ => AutoLightTick(), null, 1500, 1000);
        _timerTick = new Timer(_ => TimerTick(), null, 250, 250);
        _perfTick = new Timer(_ => PerformanceTick(), null, 10_000, 10_000);
        // ffmpeg readers/writers (preview, progress, outputs) must never queue behind each other on a small pool
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, 32), Math.Max(io, 32));
        _segmentsRoot = Path.Combine(dataRoot, "cache", "segments");
        _cache = new SegmentCache(_segmentsRoot);
        _clipCache = new SegmentCache(Path.Combine(dataRoot, "cache", "clips-9x16"));
        _vertCache = new SegmentCache(Path.Combine(dataRoot, "cache", "vertical-9x16"));
        _logDir = Path.Combine(dataRoot, "logs");
        _tempDir = Path.Combine(dataRoot, "cache", "overlays");
        _pipeDir = Path.Combine(dataRoot, "cache", "pipes");
        Directory.CreateDirectory(_tempDir);
        _cameras = new CameraBridge(_pipeDir);
        _cameras.Error += msg => Message?.Invoke(this, new EngineMessageEventArgs(msg, true));
        _windows = new WindowCaptureBridge(_pipeDir, (title, n) => _platform.FindWindow(title, n), hwnd => _platform.WindowClientSize(hwnd));
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
        Tier = HardwareProfile.Classify(Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Encoders);
        _governor.Reset(HardwareProfile.StartLevel(Tier));
        // GPU crop + scale of game windows (Direct3D 11 video processor): the CPU only receives the finished picture
        var (_, d3dHelp) = await EncoderCatalog.RunQuietAsync(FfmpegPath, new[] { "-hide_banner", "-h", "filter=scale_d3d11" },
            TimeSpan.FromSeconds(10), _life.Token);
        _windows.GpuScale = GpuWindowCaptureSupported && d3dHelp.Contains("scale_d3d11 AVOptions", StringComparison.Ordinal);
        EncodersProbed = true;
    }

    /// <summary>The main (horizontal) encode these settings would use.</summary>
    public EncodePlan PlanFor(StudioSettings s) => PlansFor(s).Main;

    /// <summary>Every encode for these settings and the current outputs.</summary>
    public PlanSet PlansFor(StudioSettings s) => PlansFor(s, LevelFor(s));

    /// <summary>Every encode for these settings at a rung of the performance ladder.</summary>
    private bool _stuckWarned;

    private PlanSet PlansFor(StudioSettings s, int level)
    {
        var pool = Pool(s);
        bool vertical = s.VerticalCanvas || _outputs.ActiveOutputs().Any(o => o.IsVertical);
        var set = OutputBudget.Resolve(s, pool, vertical, Encoders, Environment.ProcessorCount, _governor.BitrateScale);
        var plan = set.Main;
        var reasons = plan.Reasons.ToList();
        var shown = _scenes.SelectMany(sc => PipelineBuilder.CompositedLayers(sc.Sources)).ToList();
        // Old-style (GDI) capture can't deliver 60 smooth frames from a DirectX game; 60 FPS output would just
        // repeat frames unevenly (judder). Cap at 30 when it's in use.
        bool gdi = shown.Any(x =>
            (SourceTypes.KindOf(x.Type) == SourceKind.Window
             && (!GpuWindowCaptureSupported || string.Equals(x.CaptureMethod, "GDI", StringComparison.OrdinalIgnoreCase)))
            || (SourceTypes.KindOf(x.Type) == SourceKind.Display
                && (_gpuDisplayFailed || string.Equals(x.CaptureMethod, "GDI", StringComparison.OrdinalIgnoreCase))));
        if (gdi && plan.Fps > 30)
        {
            plan = plan with { Fps = 30 };
            reasons.Add("GDI capture: limited to 30 FPS.");
        }
        bool wizard = shown.Any(x => x.WindowTitle.Contains("Wizard101", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("Wizard101", StringComparison.OrdinalIgnoreCase));
        if (wizard && s.ContentType == "HighMotion")
            reasons.Add("Tip: Low motion (30 FPS) suits Wizard101 and halves the bitrate needed.");

        // the performance governor's rung: lighter encoder, lower frame rate, smaller picture
        var rung = PerfLevel.At(level);
        bool lightOk = Encoders.FirstOrDefault(e => e.Name == plan.Encoder)?.LightOk ?? true;
        EncodePlan Rung(EncodePlan p)
        {
            var q = rung.Apply(p);
            return lightOk ? q : q with { Light = false };
        }
        // the user's encoder preset (Settings → Stream), before the governor's rung
        EncodePlan Preset(EncodePlan p) => s.EncoderPreset switch
        {
            "Performance" => lightOk ? p with { Light = true } : p,
            "Quality" => p with { HighQuality = true },
            _ => p,
        };
        if (s.EncoderPreset == "Performance") reasons.Add("Preset: Performance.");
        else if (s.EncoderPreset == "Quality") reasons.Add("Preset: Quality.");
        plan = Preset(plan);
        var adapted = Rung(plan);
        if (level > 0)
            reasons.Add($"Reduced for performance: {rung.Explanation} ({adapted.Width}×{adapted.Height} · {adapted.Fps} FPS).");
        plan = adapted with { Reasons = reasons };
        // every canvas runs at the main frame rate (one shared compositor clock)
        var vplan = set.Vertical is null ? null : Rung(Preset(set.Vertical)) with { Fps = plan.Fps };
        var own = set.Own.ToDictionary(kv => kv.Key, kv => Rung(Preset(kv.Value)) with { Fps = plan.Fps });
        return new PlanSet(plan, vplan, own) { UploadKbps = set.UploadKbps };
    }

    /// <summary>What a rung changes for the current scenes and settings (rungs with the same effect are skipped).</summary>
    private string RungEffect(int level)
    {
        var p = PlansFor(Settings, level).Main;
        var r = PerfLevel.At(level);
        bool hiddenWork = r.ActiveSceneOnly && HasHiddenSceneWork();
        return $"{p.Width}x{p.Height}@{p.Fps}|{p.Light}|{r.PreviewFps}|{r.SmallPreview}|{hiddenWork}";
    }

    /// <summary>Scenes other than the one on air capture or draw something (what "active scene only" saves).</summary>
    private bool HasHiddenSceneWork() =>
        _scenes.Count > 1 && _scenes.Where((sc, i) => i != _activeIndex)
            .Any(sc => PipelineBuilder.CompositedLayers(sc.Sources).Any(x => SourceTypes.IsVideo(SourceTypes.KindOf(x.Type))));

    /// <summary>
    /// The performance governor has put the pipeline in "active scene only" mode (the PC can't keep up). Otherwise
    /// every scene is composited, so switching is always a seamless fade; light mode (Settings → Advanced) only
    /// leaves hidden sources unloaded. Never in studio mode, whose Preview needs the next scene running.
    /// </summary>
    private bool ActiveSceneOnly => Performance.ActiveSceneOnly && !StudioMode && _scenes.Count > 1;

    /// <summary>The scenes the running pipeline composites (by id), fixed while it runs.</summary>
    private List<Guid>? _runSceneIds;

    /// <summary>
    /// The scenes the pipeline composites: all of them, or (governor) the one on air plus the scenes that only show
    /// what it already captures (they cost almost nothing, and switching between them stays seamless).
    /// </summary>
    private List<StudioScene> Composited()
    {
        if (!ActiveSceneOnly) return _scenes;
        if (_runner is not null && _runSceneIds is { Count: > 0 } ids)
        {
            var kept = ids.Select(id => _scenes.FirstOrDefault(s => s.Id == id)).ToList();
            if (kept.All(s => s is not null) && kept.Any(s => ReferenceEquals(s, ActiveScene))) return kept!;
        }
        return FreshComposited();
    }

    private StudioScene ActiveScene => _scenes[Math.Clamp(_activeIndex, 0, _scenes.Count - 1)];

    private List<StudioScene> FreshComposited()
    {
        if (!ActiveSceneOnly) return _scenes;
        var active = ActiveScene;
        static IEnumerable<string> VideoKeys(StudioScene sc) => PipelineBuilder.CompositedLayers(sc.Sources)
            .Where(x => SourceTypes.IsVideo(SourceTypes.KindOf(x.Type))).Select(PipelineBuilder.InputKey);
        var have = VideoKeys(active).ToHashSet();
        return _scenes.Where(sc => ReferenceEquals(sc, active) || VideoKeys(sc).All(have.Contains)).ToList();
    }

    /// <summary>Where a scene sits among the composited ones (the graph's scene number).</summary>
    private int CompositedIndex(int sceneIndex)
    {
        if (!ActiveSceneOnly) return sceneIndex;
        var c = Composited();
        var sc = _scenes[Math.Clamp(sceneIndex, 0, _scenes.Count - 1)];
        int i = c.FindIndex(x => ReferenceEquals(x, sc) || x.Id == sc.Id);
        return i < 0 ? 0 : i;
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
        foreach (var sc in list.Append(active)) sc.ApplyFolderVisibility(); // a hidden folder hides what's in it
        int index = list.FindIndex(s => ReferenceEquals(s, active));
        if (index < 0) index = list.FindIndex(s => s.Id == active.Id);
        if (index < 0) { list.Add(active); index = list.Count - 1; }
        _scenes = list;
        Scene = active;
        Settings = settings.Clone();
        PipelineBuilder.HiddenSourcesLive = Settings.InstantSwitching;
        _outputs.AutoReconnect = Settings.AutoReconnect;

        // new settings from the user: give them a fair try from this PC's starting rung
        if (restartIfRunning && !_governorPaused) _governor.Reset(HardwareProfile.StartLevel(Tier));
        if (_runner is null) { _activeIndex = index; return; }
        if (ActiveSceneOnly && index != _activeIndex && !Composited().Any(sc => ReferenceEquals(sc, list[index]) || sc.Id == list[index].Id))
        {
            // the performance governor composites only the scene on air (and those sharing its captures): a switch
            // to a scene that needs something else rebuilds with it
            _activeIndex = index;
            _audio.Configure(AudioSources());
            await RestartAsync();
            return;
        }
        bool structural = RunnerFingerprint(FfScenes(Composited())) != _runnerFingerprint
                          || GpuWanted() != _gpuRun
                          || (_runnerRequest is not null && Math.Abs(_runnerRequest.SafetyDelay - (_audio.DelaysDesktopAudio ? Settings.SafetyConfig().DelaySeconds : 0)) > 1e-6);
        _audio.SetSafety(Settings.SafetyConfig()); // word list, guard on/off: live
        if (restartIfRunning || structural)
        {
            _activeIndex = index;
            // Audio devices are only opened while the engine runs (no "app is using your microphone" at idle).
            _audio.Configure(AudioSources());
            await RestartAsync();
            return;
        }
        int? fadeFrom = null;
        if (index != _activeIndex)
        {
            fadeFrom = CompositedIndex(_activeIndex);
            Transition(CompositedIndex(_activeIndex), CompositedIndex(index));
            _activeIndex = index;
        }
        ApplyLiveState(); // sources shown/hidden, volume changes: switched live, no rebuild
        if (_gpuRun) GpuChanged(fadeFrom); // the GPU canvas: new readers if needed, then the new state
        else RefreshLayout();
        await ReconcileAsync(); // e.g. vertical canvas switched on, an output enabled with its own encoder
    });

    // ------------------------------------------------------------------ refresh a source

    /// <summary>
    /// What Refresh does for this source while the picture is running: true when it's reloaded on its own (a web
    /// page, a window, a webcam, a microphone or desktop audio: nothing else is touched and the stream keeps going),
    /// false when the picture has to be rebuilt to read it again (a screen, an image, a text: a moment's pause, and
    /// a live stream reconnects for a second).
    /// </summary>
    public bool RefreshesInPlace(StudioSource s)
    {
        string key = PipelineBuilder.InputKey(s);
        return SourceTypes.KindOf(s.Type) switch
        {
            SourceKind.Browser or SourceKind.Camera or SourceKind.Microphone or SourceKind.DesktopAudio => true,
            SourceKind.Window => _windows.FramesOf(key) > 0 || _windows.StartsOf(key) > 0,
            _ => false,
        };
    }

    /// <summary>
    /// Refresh: the source is opened again, as if just added. A web page reloads (<paramref name="clearCache"/>:
    /// with the browser's cache emptied first, so every file comes fresh from the site), a window capture finds
    /// its window again, a webcam and an audio device reopen; each keeps its last picture/sound until the new one
    /// comes, and nothing else is touched. A screen, an image or a text is read again by rebuilding the picture
    /// (images are decoded again from their files). Returns what happened, for the status bar.
    /// </summary>
    public Task<string> RefreshSourceAsync(StudioSource s, bool clearCache = false) => Locked<string>(async () =>
    {
        var kind = SourceTypes.KindOf(s.Type);
        string key = PipelineBuilder.InputKey(s);
        if (clearCache) _gpuImages?.Clear();
        if (kind is SourceKind.Microphone or SourceKind.DesktopAudio)
            return _audio.Reopen(s.Id) ? $"{s.Name}: reopened." : $"{s.Name}: not open right now (it opens when the picture runs).";
        if (_runner is null) return $"{s.Name}: not running right now (it's loaded fresh when the picture starts).";
        switch (kind)
        {
            case SourceKind.Browser:
                return _browsers.Refresh(key, clearCache) ? (clearCache ? $"{s.Name}: cache cleared, reloading." : $"{s.Name}: reloading.")
                    : $"{s.Name}: not on the picture right now (it loads fresh when it's shown).";
            case SourceKind.Camera:
                return _cameras.Refresh(key) ? $"{s.Name}: reopening the camera." : $"{s.Name}: not on the picture right now (it opens fresh when it's shown).";
            case SourceKind.Window:
                if (_windows.Refresh(key)) return $"{s.Name}: finding the window again.";
                break;
        }
        // read again from scratch (a text is drawn again, an image read from its file): rebuild the picture
        await RestartAsync();
        return $"{s.Name}: reloaded.";
    });

    // ------------------------------------------------------------------ camera auto light

    private readonly Timer _lightTimer;
    private readonly Timer _timerTick;
    private readonly Dictionary<string, CameraAutoLight> _light = new();
    private readonly HashSet<string> _denoiseOff = new();

    /// <summary>Webcams whose denoise is off right now (bright room): for the health log and tests.</summary>
    public int CamerasDenoising
    {
        get { var spec = _runnerSpec; if (spec is null) return 0; lock (_light) return spec.CameraDenoiseFilters.Select(f => f.Key).Distinct().Count(k => !_denoiseOff.Contains(k)); }
    }

    private PreviewRate _previewRate = PreviewRate.Full;
    private DateTime _runnerBuiltUtc;
    public PreviewRate PreviewRate => _previewRate;

    /// <summary>
    /// How fast the preview runs: all frames while you look at it, a third while Leapcast is behind another window,
    /// none while it's minimized or the preview is off. Live (no restart); the stream itself never depends on it.
    /// </summary>
    public void SetPreviewRate(PreviewRate rate)
    {
        _previewWanted = rate;
        ApplyPreviewRate();
    }

    // The stream always wins over the preview: when the pipeline falls behind real time, the preview is slowed
    // (a third of its frames, then a couple a second) before anything the viewers see is touched, and it comes back
    // after a calm minute. (Your log: 19-27 fps with the preview at full speed, a steady 30 without it.)
    private PreviewRate _previewWanted = PreviewRate.Full, _previewCap = PreviewRate.Full;
    private DateTime? _previewSlowSince, _previewCalmSince;
    private DateTime _previewCapChanged = DateTime.MinValue;
    /// <summary>The slowest the preview is being held to because the PC was falling behind (Full: not held).</summary>
    public PreviewRate PreviewCap => _previewCap;
    /// <summary>Slow the preview when the PC falls behind (on; tests that time the preview turn it off).</summary>
    public bool PreviewShedding { get; set; } = true;

    private void ApplyPreviewRate()
    {
        var rate = (PreviewRate)Math.Max((int)_previewWanted, (int)_previewCap);
        if (rate == _previewRate) return;
        _previewRate = rate;
        var r = _runner; var spec = _runnerSpec;
        if (r is not null && spec is not null) r.SendCommands(PipelineBuilder.PreviewGateCommands(spec, rate));
    }

    /// <summary>Once a second, with the pipeline's speed (output seconds per second) and its frame rate against the target.</summary>
    internal void ShedPreview(double speed, double fpsRatio, bool measured, DateTime now)
    {
        if (!measured) return;
        bool behind = speed > 0 && speed < 0.93 && fpsRatio < 0.93;
        if (behind)
        {
            _previewCalmSince = null;
            _previewSlowSince ??= now;
            if (_previewCap < PreviewRate.Trickle && now - _previewSlowSince >= TimeSpan.FromSeconds(3) && now - _previewCapChanged >= TimeSpan.FromSeconds(6))
            {
                _previewCap = _previewCap == PreviewRate.Full ? PreviewRate.Slow : PreviewRate.Trickle;
                _previewCapChanged = now; _previewSlowSince = null;
                LogEvent($"PREVIEW slowed to {_previewCap} (the stream was at {fpsRatio:P0} of its frame rate): the stream comes first");
                if (_previewCap == PreviewRate.Slow)
                    Message?.Invoke(this, new EngineMessageEventArgs("Your PC was falling behind, so the preview was slowed down to keep the stream smooth. Viewers aren't affected.", false));
                ApplyPreviewRate();
            }
        }
        else
        {
            _previewSlowSince = null;
            if (_previewCap == PreviewRate.Full) return;
            _previewCalmSince ??= now;
            if (now - _previewCalmSince >= TimeSpan.FromSeconds(60) && now - _previewCapChanged >= TimeSpan.FromSeconds(60))
            {
                _previewCap = _previewCap == PreviewRate.Trickle ? PreviewRate.Slow : PreviewRate.Full;
                _previewCapChanged = now; _previewCalmSince = null;
                LogEvent($"PREVIEW back to {_previewCap}");
                ApplyPreviewRate();
            }
        }
    }
    private int _lightBusy;

    private IReadOnlySet<string> CameraDenoiseOffNow() { lock (_light) return _denoiseOff.ToHashSet(); }

    private IReadOnlyDictionary<string, LightSettings> CameraLightNow()
    {
        lock (_light) return _light.ToDictionary(x => x.Key, x => x.Value.Current);
    }

    /// <summary>Where each camera's auto light is (for the log / tests): "key: gamma …".</summary>
    public string CameraLightStats()
    {
        lock (_light) return string.Join(" | ", _light.Select(x => FormattableString.Invariant($"{x.Key}: mean {x.Value.LastMean:0.00} → gamma {x.Value.Current.Gamma:0.00}")));
    }

    /// <summary>Once a second: measure each auto-lit camera's latest frame, send a correction when it changed enough to see.</summary>
    private void AutoLightTick()
    {
        if (Interlocked.Exchange(ref _lightBusy, 1) == 1) return;
        try
        {
            var r = _runner; var spec = _runnerSpec;
            // a preview rate sent in the first moments may arrive before ffmpeg's graph is ready: repeat it then
            if (r is not null && spec is not null && _runnerRequest is { } rq && rq.PreviewRate != _previewRate
                && DateTime.UtcNow - _runnerBuiltUtc < TimeSpan.FromSeconds(6))
                r.SendCommands(PipelineBuilder.PreviewGateCommands(spec, _previewRate));
            if (_gpuRun) { GpuAutoLight(); return; }
            if (r is null || spec is null || spec.CameraLightFilters.Count == 0) return;
            foreach (var key in spec.CameraLightFilters.Select(f => f.Key).Distinct())
            {
                if (_cameras.Latest(key) is not { } frame) continue;
                var (mean, p95) = CameraAutoLight.Measure(frame.Frame, frame.Width, frame.Height);
                CameraAutoLight light;
                lock (_light) { if (!_light.TryGetValue(key, out light!)) _light[key] = light = new CameraAutoLight(); }
                // denoise only while it's dark (a bright picture has little noise; the filter is real work every frame)
                var dn = spec.CameraDenoiseFilters.Where(f => f.Key == key).ToList();
                if (dn.Count > 0)
                {
                    bool off;
                    lock (_light) off = _denoiseOff.Contains(key);
                    bool want = off ? mean > 0.30 : mean > 0.36;   // a little hysteresis
                    if (want != off)
                    {
                        lock (_light) { if (want) _denoiseOff.Add(key); else _denoiseOff.Remove(key); }
                        r.SendCommands(dn.Select(f => $"{f.Name} -1 enable {(want ? 0 : 1)}"));
                    }
                }
                if (light.Update(mean, p95) is not { } next) continue;
                r.SendCommands(spec.CameraLightFilters.Where(f => f.Key == key).SelectMany(f => next.Commands(f.Name)));
            }
        }
        catch { }
        finally { Volatile.Write(ref _lightBusy, 0); }
    }

    /// <summary>The safety delay the running pipeline was built with (0 = none).</summary>
    public double SafetyDelayActive => _runnerRequest?.SafetyDelay ?? 0;

    public Task UpdateAsync(StudioScene scene, StudioSettings settings, bool restartIfRunning) =>
        UpdateAsync(new[] { scene }, scene, settings, restartIfRunning);

    /// <summary>
    /// Call while dragging sources on the preview: re-reads every source's X/Y (and VX/VY) and moves the running
    /// overlays to match, without a restart. Cheap; safe to call on every mouse move.
    /// </summary>
    public void RefreshLayout()
    {
        if (_runner is null || _runnerSpec is null || _runnerRequest is null) return;
        if (_gpuRun) { PushGpuState(); return; } // the canvas draws every source itself
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
                var layers = PipelineBuilder.VerticalLayers(sc.Sources.ToList(), includeHidden: true)
                    .Where(s => names.Contains(PipelineBuilder.OverlayName(s.Id, CanvasKind.Vertical))).ToList();
                bool hasCam = layers.Any(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera && x.VAuto && x.Shown);
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
        PipelineBuilder.TransitionCommands(spec, from, to, 0, seconds, out int lineCount, IsHiddenFilter, CurrentGains);
        // Schedule against the pipeline's own clock (ffmpeg's latest output time, which tracks the frames being
        // composited), not the wall clock: if the machine falls behind real time, a wall-clock start time would
        // lie seconds in the future and the switch would appear frozen. Start after every command line has landed
        // (ffmpeg reads one per ~100 ms).
        // the encoder's reported time lags the compositor by its look-ahead buffer: add that back, or the fade
        // would be scheduled in the past and land as a cut
        double now = CompositorClock();
        double startAt = now + 0.15 * lineCount + 0.25;
        var lines = PipelineBuilder.TransitionCommands(spec, from, to, startAt, seconds, out _, IsHiddenFilter, CurrentGains);
        _pumpPausedUntil = DateTime.UtcNow + TimeSpan.FromSeconds(0.15 * lines.Count + 0.3); // keep stdin free for the fade
        r.SendCommands(lines);
        // what the switch left on: the incoming scene's big overlays, the outgoing scene's off; levels of the new scene
        lock (_liveLock)
        {
            foreach (var st in spec.Visibility.Where(v => v.OnlyWhileSceneShown && (v.Scene == from || v.Scene == to)))
                _sentEnable[st.Filter] = (st.Scene == to || st.Scene == StagedComposited) && !IsHiddenFilter(st.Filter);
            foreach (var g in spec.AudioGains) { var lv = CurrentGains(g); if (to < lv.Count) _sentGain[g.FilterName] = lv[to]; }
        }
    }

    // ------------------------------------------------------------------ live show / hide / volume

    private readonly object _liveLock = new();
    private readonly Dictionary<string, bool> _sentEnable = new();
    private readonly Dictionary<string, double> _sentGain = new();
    private Dictionary<string, Guid> _filterSource = new();

    private StudioSource? SourceById(Guid id) =>
        _scenes.SelectMany(sc => sc.Sources).FirstOrDefault(s => s.Id == id);

    /// <summary>Is this overlay's source hidden right now (transitions must leave it switched off)?</summary>
    private bool IsHiddenFilter(string filter) =>
        _filterSource.TryGetValue(filter, out var id) && SourceById(id) is { Visible: false };

    /// <summary>Each scene's level for an audio device as the scenes are now (hidden = 0).</summary>
    private IReadOnlyList<double> CurrentGains(AudioGainStage g) => PipelineBuilder.SceneGains(Composited(), g.Key);

    private int ActiveCompositedIndex => CompositedIndex(_activeIndex);

    /// <summary>What a visibility switch should be now.</summary>
    private bool WantEnabled(VisibilityStage st, int active)
    {
        var s = SourceById(st.SourceId);
        bool visible = s?.Shown ?? false;
        return st.Cover ? !visible : visible && (!st.OnlyWhileSceneShown || st.Scene == active || st.Scene == StagedComposited);
    }

    /// <summary>Remember what the freshly built graph starts with.</summary>
    private void InitLiveState(PipelineSpec spec)
    {
        int active = ActiveCompositedIndex;
        lock (_liveLock)
        {
            _sentEnable.Clear(); _sentGain.Clear();
            _filterSource = spec.Visibility.GroupBy(v => v.Filter).ToDictionary(g => g.Key, g => g.First().SourceId);
            foreach (var st in spec.Visibility) _sentEnable[st.Filter] = WantEnabled(st, active);
            foreach (var g in spec.AudioGains) if (active < g.LinearGainPerScene.Count) _sentGain[g.FilterName] = g.LinearGainPerScene[active];
            if (spec.HasMasterVolume) _sentGain[PipelineBuilder.MasterFilter] = MasterGain;
        }
    }

    /// <summary>
    /// Shows/hides sources and sets audio levels in the running pipeline: a timeline switch on the overlay (or a cover
    /// over the bottom layer) and the device's gain stage. Instant, and the stream never notices.
    /// </summary>
    private void ApplyLiveState()
    {
        var r = _runner; var spec = _runnerSpec;
        if (r is null || spec is null) return;
        int active = ActiveCompositedIndex;
        var lines = new List<string>();
        lock (_liveLock)
        {
            foreach (var st in spec.Visibility)
            {
                bool want = WantEnabled(st, active);
                if (_sentEnable.TryGetValue(st.Filter, out var cur) && cur == want) continue;
                _sentEnable[st.Filter] = want;
                lines.Add($"{st.Filter} -1 enable {(want ? 1 : 0)}");
            }
            foreach (var g in spec.AudioGains)
            {
                var lv = CurrentGains(g);
                if (active >= lv.Count) continue;
                double want = lv[active];
                if (_sentGain.TryGetValue(g.FilterName, out var cur) && Math.Abs(cur - want) < 1e-4) continue;
                _sentGain[g.FilterName] = want;
                lines.Add($"{g.FilterName} -1 volume {want.ToString("0.######", CultureInfo.InvariantCulture)}");
            }
            if (spec.HasMasterVolume)
            {
                double want = MasterGain;
                if (!(_sentGain.TryGetValue(PipelineBuilder.MasterFilter, out var cur) && Math.Abs(cur - want) < 1e-4))
                {
                    _sentGain[PipelineBuilder.MasterFilter] = want;
                    lines.Add($"{PipelineBuilder.MasterFilter} -1 volume {want.ToString("0.######", CultureInfo.InvariantCulture)}");
                }
            }
        }
        if (lines.Count > 0) { LiveSwitches += lines.Count; r.SendCommands(lines); }
    }

    private double MasterGain => Math.Pow(10, Math.Clamp(Settings.MasterVolumeDb, -60, 20) / 20.0);

    /// <summary>
    /// The mixer moved: re-reads every source's level (VolumeDb, shown/hidden) and the master level and sets them in
    /// the running pipeline. Live, cheap, safe to call while a fader is being dragged.
    /// </summary>
    public void SetAudioLevels(double masterDb)
    {
        Settings.MasterVolumeDb = masterDb;
        ApplyLiveState();
    }

    /// <summary>Diagnostics/tests: show/hide/volume commands sent without a rebuild.</summary>
    public int LiveSwitches { get; private set; }

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
                _governor.ResetBitrate();
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

    /// <summary>
    /// What a recording is cut from. Normally the widescreen canvas; set to record in 9:16 it is your own vertical
    /// canvas when you have one laid out, and otherwise the automatic TikTok layout clips use.
    /// </summary>
    private SegmentCache RecCache => !Settings.RecordVertical ? _cache
        : Settings.VerticalCanvas ? _vertCache : _clipCache;

    /// <summary>A recording is running, whichever canvas it is being cut from.</summary>
    public bool IsRecording => _cache.IsRecording || _clipCache.IsRecording || _vertCache.IsRecording;

    public Task StartRecordingAsync() => Locked(async () =>
    {
        var cache = RecCache;
        if (cache.IsRecording) return;
        cache.StartRecording(Settings.RecordingFolder);
        try { await ReconcileAsync(requireSuccess: true); }
        catch
        {
            try { await cache.StopRecordingAsync(FfmpegPath!, Path.Combine(_tempDir, "discard.mp4")); } catch { }
            try { await ReconcileAsync(); } catch { }
            throw;
        }
    });

    public async Task<string> StopRecordingAsync()
    {
        Task<string>? export = null;
        await Locked(async () =>
        {
            var cache = _clipCache.IsRecording ? _clipCache : _vertCache.IsRecording ? _vertCache : _cache;
            if (!cache.IsRecording) return;
            Directory.CreateDirectory(Settings.RecordingFolder);
            string file = Path.Combine(Settings.RecordingFolder,
                $"Leapcast {DateTime.Now:yyyy-MM-dd HH-mm-ss}{(cache == _cache ? "" : " 9x16")}.mp4");
            export = cache.StopRecordingAsync(RequireFfmpeg(), file); // captures the range synchronously
            await ReconcileAsync();
        });
        return export is null ? "" : await export;
    }

    /// <summary>
    /// Recordings that were running when the app or the PC went down, rebuilt from what the encoder had written
    /// ("… (recovered).mp4" in the recording folder). Call once at start-up, after FFmpeg is found.
    /// </summary>
    public async Task<List<string>> RecoverRecordingsAsync()
    {
        if (FfmpegPath is not { } ff) return new();
        try { return await SegmentCache.RecoverAsync(_segmentsRoot, ff, Settings.RecordingFolder); }
        catch { return new(); }
    }

    /// <summary>When the last recording's first frame was captured (UTC): lines up the session log with the file.</summary>
    public DateTime? LastRecordingStartUtc => _cache.LastRecordingStartUtc;

    public Task SetReplayBufferAsync(bool on) => Locked(async () =>
    {
        ReplayBuffer = on;
        try { await ReconcileAsync(requireSuccess: on); }
        catch { ReplayBuffer = false; try { await ReconcileAsync(); } catch { } throw; }
    });

    public async Task<string> SaveReplayAsync()
    {
        if (!(_runnerShape?.Main == true && _runner is not null))
            throw new InvalidOperationException("Start the replay buffer, recording or stream first.");
        Directory.CreateDirectory(Settings.RecordingFolder);
        string file = Path.Combine(Settings.RecordingFolder, $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        return await _cache.SaveReplayAsync(RequireFfmpeg(), Math.Clamp(Settings.ReplaySeconds, 5, 600), file);
    }

    /// <summary>Something to clip: the encode that feeds the replay cache is running (live, recording or replay buffer).</summary>
    public bool CanClip => _runnerShape?.Main == true && _runner is not null;

    /// <summary>
    /// Saves the last <paramref name="seconds"/> of the stream to <paramref name="folder"/>, named by the day and
    /// time (voice clips: "Clip"). Cut from the already-encoded stream: no extra encoding, nothing interrupted.
    /// </summary>
    /// <summary>Where the camera and the game are on the program canvas right now (for 9:16 clips).</summary>
    public ClipLayout? CurrentClipLayout()
    {
        var plan = CurrentPlan;
        if (plan is null || _scenes.Count == 0) return null;
        var scene = _scenes[Math.Clamp(_activeIndex, 0, _scenes.Count - 1)];
        var layers = PipelineBuilder.VisibleLayers(scene.Sources).Where(x => SourceTypes.IsVideo(SourceTypes.KindOf(x.Type))).ToList();
        LayoutRect Rect(StudioSource s) => LayoutMath.IsBaseLayer(s, layers)
            ? LayoutMath.FillRect(s, plan.Width, plan.Height, SourceAspect(s))
            : LayoutMath.Horizontal(s, plan.Width, plan.Height, SourceAspect(s), SourceSize(s));
        var game = layers.FirstOrDefault(x => LayoutMath.IsGame(SourceTypes.KindOf(x.Type)));
        var cam = layers.LastOrDefault(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera && !LayoutMath.IsBaseLayer(x, layers));
        return new ClipLayout(plan.Width, plan.Height, game is null ? null : Rect(game), cam is null ? null : Rect(cam));
    }

    /// <summary>
    /// Saves a 9:16 copy of a saved clip next to it ("… (9x16).mp4"), with the GPU encoder when there is one, at low
    /// priority (the stream comes first).
    /// </summary>
    /// <summary>Encoder settings for an edited YouTube video: the stream's encoder at YouTube's recommended bitrates.</summary>
    public IReadOnlyList<string> VodVideoArgs(int w, int h, double fps)
    {
        if (CurrentPlan is null) return new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p" };
        int kbps = h >= 1400 ? 24000 : h >= 1000 ? (fps > 40 ? 12000 : 8000) : (fps > 40 ? 7500 : 5000);
        var plan = CurrentPlan with { Width = w, Height = h, Fps = (int)Math.Round(fps), CapKbps = kbps, LowLatency = false, Light = false };
        return EncoderCatalog.BuildVideoArgs(plan);
    }

    /// <summary>Encoder settings for re-encoding a saved clip of w×h (captions): the stream's encoder, a clip's bitrate.</summary>
    public IReadOnlyList<string> ClipVideoArgs(int w, int h)
    {
        var basePlan = CurrentPlan ?? new EncodePlan(w, h, 30, 4500, 22, "libx264", EncoderTier.Rich, false, false, "veryfast", "", Array.Empty<string>());
        int big = Math.Max(w, h);
        var plan = basePlan with { Width = w, Height = h, CapKbps = big >= 1900 ? 8000 : big >= 1270 ? 5000 : 3000, LowLatency = false, Light = false };
        return EncoderCatalog.BuildVideoArgs(plan);
    }

    public async Task<string> SaveVerticalAsync(string clipPath, ClipLayout layout, CancellationToken ct = default)
    {
        var (w, h) = VerticalClip.OutputSize(layout.CanvasH);
        var basePlan = CurrentPlan ?? new EncodePlan(w, h, 30, 4500, 22, "libx264", EncoderTier.Rich, false, false, "veryfast", "", Array.Empty<string>());
        var plan = basePlan with { Width = w, Height = h, Fps = 30, CapKbps = w >= 1080 ? 8000 : 5000, LowLatency = false };
        string output = Path.Combine(Path.GetDirectoryName(clipPath)!, Path.GetFileNameWithoutExtension(clipPath) + " (9x16).mp4");
        await VerticalClip.RenderAsync(RequireFfmpeg(), clipPath, output, layout, EncoderCatalog.BuildVideoArgs(plan), Settings.ClipHandle, ct);
        return output;
    }

    /// <summary>The live 9:16 clip canvas is running: <see cref="SaveClipAsync"/> can cut a 9:16 clip straight away.</summary>
    public bool CanClipVertical => _runnerShape?.Clip == true && _runner is not null && _clipCache.HasActiveSession;

    /// <summary>Off: 9:16 clips are rendered from the 16:9 clip after it's saved (tests, or a GPU out of encoder sessions).</summary>
    public bool ClipCanvasEnabled { get; set; } = true;

    /// <summary>
    /// A clip from the live 9:16 clip canvas when it's running (<paramref name="vertical"/>), else from the stream.
    /// Returns the file and whether it is 9:16 already.
    /// </summary>
    public async Task<(string Path, bool Vertical)> SaveClipAsync(int seconds, string folder, bool vertical, DateTime? endUtc = null, string? label = null,
        CancellationToken ct = default)
    {
        if (vertical && CanClipVertical)
        {
            try { return (await SaveClipFromAsync(_clipCache, seconds, folder, ct, endUtc, label), true); }
            catch (Exception ex) { Message?.Invoke(this, new EngineMessageEventArgs("9:16 clip: " + ex.Message + " (saving 16:9 instead)", true)); }
        }
        return (await SaveClipAsync(seconds, folder, ct, endUtc, label), false);
    }

    /// <param name="endUtc">End the clip here (just before the word was said); null = the last seconds up to now.</param>
    public Task<string> SaveClipAsync(int seconds, string folder, CancellationToken ct = default, DateTime? endUtc = null, string? label = null)
        => SaveClipFromAsync(_cache, seconds, folder, ct, endUtc, label);

    private async Task<string> SaveClipFromAsync(SegmentCache cache, int seconds, string folder, CancellationToken ct, DateTime? endUtc, string? label)
    {
        if (!CanClip) throw new InvalidOperationException("Nothing to clip. Start streaming, recording or the replay buffer.");
        Directory.CreateDirectory(folder);
        var now = DateTime.UtcNow;
        var end = endUtc is { } e && e <= now && now - e < TimeSpan.FromSeconds(20) ? e : (DateTime?)null;
        string file = VoiceClips.PathFor(folder, (end ?? now).ToLocalTime(), label);
        seconds = Math.Clamp(seconds, 5, 600);
        return end is { } at
            ? await cache.SaveEndingAtAsync(RequireFfmpeg(), seconds, at, file, ct)
            : await cache.SaveReplayAsync(RequireFfmpeg(), seconds, file, ct);
    }

    public sealed record BenchmarkResult(double Fps, int TargetFps, double Speed, long Dropped, string Health)
    {
        /// <summary>Worst queue of frames waiting in front of the compositor during the test (seconds).</summary>
        public double Backlog { get; init; }
        /// <summary>Kept up with real time at (nearly) the full frame rate, with no frames piling up.</summary>
        public bool Smooth => Fps >= TargetFps * 0.93 && (Speed <= 0 || Speed >= 0.97) && Backlog < 0.4;
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
        int oldLevel = _governor.Level;
        // test the candidate exactly as configured: the governor stays out of it
        _governorPaused = true;
        _governor.Force(0);
        try
        {
            await UpdateAsync(scenes, scene, candidate, restartIfRunning: true);
            await SetReplayBufferAsync(true); // encodes like a stream does
            await Task.Delay(2500, ct);        // devices settle, encoder look-ahead fills
            lock (_progressWindow) _progressWindow.Clear();
            double worstBacklog = 0;
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(seconds, 4, 30));
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(500, ct);
                worstBacklog = Math.Max(worstBacklog, Math.Max(_cameras.MaxBacklogSeconds(), Math.Max(_windows.MaxBacklogSeconds(), _browsers.MaxBacklogSeconds())));
            }
            var (fps, speed, _) = WindowedRates();
            var p = _lastProgress;
            return new BenchmarkResult(fps, CurrentPlan?.Fps ?? candidate.FrameRate, speed, p?.DroppedFrames ?? 0, _health) { Backlog = worstBacklog };
        }
        finally
        {
            _governorPaused = false;
            _governor.Force(oldLevel);
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
        if (!scene.Sources.Contains(camera) || !camera.Shown)
            throw new InvalidOperationException("Show the camera in the current scene first.");
        if (!scene.Sources.Any(x => x.Shown && SourceTypes.KindOf(x.Type) == SourceKind.Microphone))
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
        if (IsRecording) { try { await StopRecordingAsync(); } catch { } }
    }

    // ------------------------------------------------------------------ pipeline management

    private List<StudioSource> AudioSources() => PipelineBuilder.AudioRepresentatives(Composited());

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
        bool mainBus = pool.Any(o => !o.IsVertical && o.OwnBitrateKbps <= 0) || OutputBudget.MainRiders(pool).Ids.Count > 0;
        // while live, the main encode also keeps the last seconds for voice clips
        bool main = ReplayBuffer || _cache.IsRecording || mainBus || (anyOut && Settings.VoiceClipEnabled);
        bool vEnc = vCanvas && (pool.Any(o => o.IsVertical && o.OwnBitrateKbps <= 0) || _vertCache.IsRecording);
        string own = string.Join(",", pool.Where(o => o.OwnBitrateKbps > 0 && (!o.IsVertical || vCanvas))
            .Select(o => $"{o.Id:N}:{o.OwnBitrateKbps}:{(o.IsVertical ? "v" : "h")}").OrderBy(x => x, StringComparer.Ordinal));
        bool run = PreviewOn || main || vEnc || own.Length > 0;
        // 9:16 clips are made live alongside the clip history (from the raw game and camera), not re-rendered after
        // recording in 9:16 runs the clip canvas on its own: no 16:9 encode is needed just to record for TikTok
        bool clip = ClipCanvasEnabled && ((main && Settings.VerticalClips) || _clipCache.IsRecording);
        return new Shape(run || clip, main, mainBus, vCanvas, vEnc, own, clip);
    }

    private async Task ReconcileAsync(bool requireSuccess = false)
    {
        var want = DesiredShape();
        if (!want.Run) { await StopRunnerAsync(); CloseDevices(); _gpu?.Dispose(); _gpu = null; Publish(); return; }
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
        if (!DesiredShape().Run) return;
        try { await StartRunnerAsync(false); }
        catch (Exception ex) when (MustKeepRunning)
        {
            // a recording or a stream is never ended by a rebuild that failed: keep trying
            LogEvent($"RESTART FAILED ({ex.Message}): trying again in 5 s");
            Message?.Invoke(this, new EngineMessageEventArgs($"Couldn't restart the picture ({ex.Message}). Trying again in 5 s; your {WhatsRunning()} keeps going.", true));
            ScheduleReconnect(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Something you started is on: a recording, a destination or the replay buffer. The app never ends
    /// these by itself; only you do.</summary>
    private bool MustKeepRunning => _outputs.AnyActive || IsRecording || ReplayBuffer;

    private string WhatsRunning() =>
        _outputs.AnyActive && IsRecording ? "stream and recording" : _outputs.AnyActive ? "stream" : IsRecording ? "recording" : "replay buffer";

    private readonly Timer _perfTick;
    private int _perfBusy;
    private string _perfShape = "";

    /// <summary>
    /// Every ten seconds while something is running, a line in logs\performance.log: what is being composited
    /// and encoded, how the encoder is keeping up, CPU and memory per role, and how far behind each capture
    /// reader is. Tuning the app's appetite without these numbers is guesswork.
    /// </summary>
    private void PerformanceTick()
    {
        if (!Running || Interlocked.Exchange(ref _perfBusy, 1) == 1) return;
        try
        {
            var shape = _runnerShape;
            var plan = CurrentPlan;
            if (shape is null || plan is null) return;
            // the resource monitor's CPU figures are deltas since its last call, so take the app's sample when it is fresh
            var usage = ResourceMonitor.Recent(TimeSpan.FromSeconds(20));
            var p = _lastProgress;
            var canvases = new List<string> { "16:9" };
            if (shape.Vertical) canvases.Add("9:16");
            if (shape.Clip) canvases.Add("9:16 clip");
            int encodes = (shape.Main ? 1 : 0) + (shape.VerticalEncode ? 1 : 0) + (shape.Clip ? 1 : 0)
                          + (shape.Own.Length == 0 ? 0 : shape.Own.Split(',').Length);
            string head = $"{plan.Width}x{plan.Height}@{plan.Fps} {plan.Encoder} q{plan.Quality} cap{plan.CapKbps}k";
            // scenes and sources are in here because every scene in the graph is composited every frame,
            // whether or not it is the one on air: the cost of simply having scenes is what this measures
            var sc = Composited();
            int shown = sc.Sum(x => x.Sources.Count(y => y.Shown && SourceTypes.IsVideo(SourceTypes.KindOf(y.Type))));
            string what = $"canvases={string.Join("+", canvases)} encodes={encodes} scenes={sc.Count} sources={shown}" +
                          $" gpu={GpuStatus} preview={(PreviewOn ? "on" : "off")}" +
                          $" outputs={_outputs.ActiveOutputs().Count()} {WhatsRunning()}";
            if (what != _perfShape) { Perf("shape " + head + " " + what); _perfShape = what; }
            string enc = p is null ? "encoder: no progress yet"
                : $"encoder fps={p.Fps:0.0} speed={p.Speed:0.00}x dropped={p.DroppedFrames} duped={p.DupFrames}";
            string cpu = string.Join(" ", usage.Select(u => $"{Role(u.Role)}={u.CpuPercent:0.0}%"));
            string ram = string.Join(" ", usage.Select(u => $"{Role(u.Role)}={u.MemoryBytes / 1048576}M"));
            string backlog = $"windows={_windows.MaxBacklogSeconds():0.00}s cameras={_cameras.MaxBacklogSeconds():0.00}s pages={_browsers.MaxBacklogSeconds():0.00}s";
            Perf($"{enc} | cpu {cpu} total={usage.Sum(u => u.CpuPercent):0.0}% | ram {ram} total={usage.Sum(u => u.MemoryBytes) / 1048576}M | backlog {backlog}");
        }
        catch { }
        finally { Interlocked.Exchange(ref _perfBusy, 0); }
    }

    private static string Role(string role) => role switch
    {
        ResourceMonitor.App => "app",
        ResourceMonitor.Encoder => "enc",
        ResourceMonitor.Capture => "cap",
        ResourceMonitor.Destinations => "dest",
        ResourceMonitor.Browser => "web",
        _ => role.ToLowerInvariant(),
    };

    private void Perf(string text)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            string file = Path.Combine(_logDir, "performance.log");
            // one session's worth: trimmed when it gets long, so it stays easy to read and send
            try { if (new FileInfo(file) is { Exists: true, Length: > 2_000_000 } f) File.Move(file, file + ".1", true); } catch { }
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>A line in logs\stream-health.log (why something stopped or restarted: for finding the cause later).</summary>
    private void LogEvent(string text)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            File.AppendAllText(Path.Combine(_logDir, "stream-health.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>The governor's rung before its last change, and when: a run that dies right after a change goes back.</summary>
    private int _levelBeforeChange = -1;
    private DateTime _levelChangedUtc = DateTime.MinValue;
    /// <summary>After a lighter/heavier rung broke the encoder, the governor holds still for a while.</summary>
    private DateTime _governorHoldUntil = DateTime.MinValue;

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
        var scenes = FreshComposited();
        var timerFilters = BuildTimerCards(scenes, plan.Height);
        _runSceneIds = scenes.Select(sc => sc.Id).ToList();
        foreach (var s in scenes.SelectMany(sc => sc.Sources).Where(s => SourceTypes.KindOf(s.Type) == SourceKind.Text))
        {
            try { if (_platform.RenderText(s, plan.Height) is { } png) rendered[s.Id] = png; } catch { }
        }

        _audio.Configure(AudioSources());
        double origin = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        IReadOnlyDictionary<Guid, string> pipes = new Dictionary<Guid, string>();
        // stream safety: desktop audio waits in Leapcast (filtered words muted, ear-rape turned down), the encodes follow
        var safety = Settings.SafetyConfig();
        _audio.SetSafety(safety);
        double safetyDelay = _audio.DelaysDesktopAudio ? safety.DelaySeconds : 0;
        if (encode) pipes = _audio.OpenPipes(origin);
        var delayedAudio = new HashSet<Guid>(safetyDelay > 0
            ? AudioSources().Where(x => (SourceTypes.KindOf(x.Type) == SourceKind.DesktopAudio
                                         || (safety.FilterMic && SourceTypes.KindOf(x.Type) == SourceKind.Microphone)) && pipes.ContainsKey(x.Id)).Select(x => x.Id)
            : Enumerable.Empty<Guid>());

        // Webcams: one warm reader per device, fed to this run through counted pipes.
        var camUses = new Dictionary<string, (InputSpec device, List<StudioSource> uses)>();
        var shownVideo = scenes.SelectMany(sc => PipelineBuilder.CompositedLayers(sc.Sources)
            .Concat(shape.Vertical ? PipelineBuilder.VerticalLayers(sc.Sources, includeHidden: true) : Enumerable.Empty<StudioSource>()));
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
        foreach (var src in scenes.SelectMany(sc => PipelineBuilder.CompositedLayers(sc.Sources)
                     .Concat(shape.Vertical ? PipelineBuilder.VerticalLayers(sc.Sources, includeHidden: true) : Enumerable.Empty<StudioSource>()))
                     .Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Browser && x.Url.Trim().Length > 0))
            browserUses.TryAdd(PipelineBuilder.InputKey(src), src);
        foreach (var (k, src) in browserUses) _knownSizes[k] = BrowserBridge.SizeOf(src);   // its layout size, whatever it's drawn at
        var browserFeeds = _browsers.Open(browserUses, origin, w => Message?.Invoke(this, new EngineMessageEventArgs(w, true)),
            src => BrowserRenderSize(src, plan, shape.Vertical ? vSize : null, scenes));
        lock (_windows.CaptureSizes) foreach (var (k, v) in _windows.CaptureSizes) _knownSizes[k] = v;
        // (with the safety delay, encoded timestamps run that much ahead of when things happened)
        string? segPattern = shape.Main ? _cache.BeginSession(origin - safetyDelay) : null;
        string? clipPattern = shape.Clip ? _clipCache.BeginSession(origin - safetyDelay) : null;
        string? vertPattern = shape.VerticalEncode && _vertCache.IsRecording ? _vertCache.BeginSession(origin - safetyDelay) : null;
        var clipSize = VerticalClip.OutputSize(plan.Height);
        // a clip is a short upload; a 9:16 recording is the whole session, so it keeps the stream's frame rate and room to breathe
        bool recVertical = _clipCache.IsRecording;
        var clipPlan = plan with { Width = clipSize.W, Height = clipSize.H,
            Fps = recVertical ? plan.Fps : 30,
            CapKbps = recVertical ? Math.Max(plan.CapKbps, clipSize.W >= 1080 ? 12000 : 8000) : clipSize.W >= 1080 ? 6000 : 4500,
            LowLatency = false, Light = false };
        _stuckWarned = false;

        // One TS bus per encode that feeds destinations; outputs subscribe/unsubscribe at will.
        var buses = new Dictionary<string, TsBus>();
        // what each destination was connected with: same codec + picture size → a rebuild keeps the connection
        var signatures = new Dictionary<string, string>();
        static string Sig(EncodePlan p) => $"{p.Encoder}|{p.Width}x{p.Height}";
        var extra = new List<(string BusPath, bool Vertical, EncodePlan Plan)>();
        if (shape.MainBus) { buses["main"] = new TsBus(_pipeDir, "main"); signatures["main"] = Sig(plan); }
        if (shape.VerticalEncode && plans.Vertical is not null) { buses["vertical"] = new TsBus(_pipeDir, "vertical"); signatures["vertical"] = Sig(plans.Vertical); }
        var riders = _outputs.AnyActive ? OutputBudget.MainRiders(Pool(Settings)).Ids : new HashSet<Guid>();
        _outputs.MainRiders = riders;
        if (shape.Own.Length > 0)
            foreach (var o in Pool(Settings).Where(o => o.OwnBitrateKbps > 0 && (!o.IsVertical || shape.Vertical)))
            {
                string k = OutputManager.BusKey(o, riders);
                if (buses.ContainsKey(k) || !plans.Own.TryGetValue(o.Id, out var op)) continue;
                var bus = new TsBus(_pipeDir, "own" + extra.Count);
                buses[k] = bus;
                signatures[k] = Sig(op);
                extra.Add((bus.Path, o.IsVertical, op));
            }

        // GPU mode: the canvas is FFmpeg's only picture (every scene shows it; audio stays per scene)
        string? blocker = GpuBlocker(shape, scenes);
        _gpuRun = blocker is null && EnsureGpuCanvas(plan);
        if (!_gpuRun && Settings.GpuCompositing && blocker is not null && blocker != "off") GpuStatus = blocker;
        if (!Settings.GpuCompositing) { _gpu?.Dispose(); _gpu = null; GpuStatus = "off"; }
        if (_gpuRun)
        {
            int gw = _gpu!.Width, gh = _gpu.Height;
            _gpuPipe?.Dispose();
            var canvas = _gpu;
            _gpuPipe = new CountedFramePipe(_pipeDir, gw * gh * 3 / 2, plan.Fps, origin, () => canvas.Latest(), Frames.BlankI420(gw, gh));
            camFeeds = new Dictionary<string, CameraBridge.CameraFeed>(camFeeds) { [PipelineBuilder.InputKey(CanvasSource)] = new CameraBridge.CameraFeed(_gpuPipe.Path, gw, gh) };
            _gpuReaders = null;
            GpuStatus = $"on: {_gpu.Adapter}";
        }
        var request = new PipelineRequest
        {
            Scenes = FfScenes(scenes),
            ActiveSceneIndex = Math.Max(0, scenes.FindIndex(sc => ReferenceEquals(sc, ActiveScene))),
            StagedSceneIndex = StagedComposited,
            Plan = plan,
            MainBusPath = buses.TryGetValue("main", out var mb) ? mb.Path : null,
            VerticalCanvas = shape.Vertical ? vSize : null,
            VerticalPlan = shape.Vertical ? plans.Vertical : null,
            VerticalBusPath = buses.TryGetValue("vertical", out var vb) ? vb.Path : null,
            VerticalSegmentPattern = vertPattern,
            ExtraEncodes = extra,
            KnownSizes = new Dictionary<string, (int, int)>(_knownSizes),
            SegmentPattern = segPattern,
            SegmentSeconds = _cache.SegmentSeconds,
            ClipCanvas = shape.Clip ? clipSize : null,
            ClipPlan = shape.Clip ? clipPlan : null,
            ClipSegmentPattern = clipPattern,
            ClipHandle = Settings.ClipHandle,
            ClipFont = Recap.FontFile(),
            Preview = true,
            // lighter rungs: smaller, slower preview (every preview frame is scaled, converted and copied to the app)
            PreviewWidth = Performance.SmallPreview ? 640 : PreviewWidthPx, PreviewHeight = Performance.SmallPreview ? 360 : PreviewHeightPx,
            PreviewFps = Performance.PreviewFps,
            PreviewRate = _previewRate,
            CameraDenoiseOff = CameraDenoiseOffNow(),
            AudioInputs = pipes,
            RenderedImages = rendered,
            TimerFilters = timerFilters,
            MonitorBounds = _platform.MonitorBounds,
            GpuDisplayCapture = !_gpuDisplayFailed,
            GpuDisplayScale = _windows.GpuScale && !_gpuDisplayScaleFailed,
            DisplayFullSize = shape.Vertical || shape.Clip,
            AudioBitrateKbps = Math.Clamp(Settings.AudioBitrateKbps, 64, 320),
            MasterVolumeDb = Settings.MasterVolumeDb,
            WallclockOrigin = origin,
            SafetyDelay = safetyDelay,
            CameraLight = CameraLightNow(),
            DelayedAudio = delayedAudio,
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
        InitLiveState(spec);
        _runnerRequest = request;
        _runnerOrigin = request.WallclockOrigin;
        _runnerFingerprint = RunnerFingerprint(FfScenes(scenes));
        var (phw, pvw, ph) = spec.PreviewLayout;
        PreviewFrame = (spec.PreviewStageWidth + phw + pvw, ph, phw, pvw, spec.PreviewStageWidth);
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
        _runnerBuiltUtc = DateTime.UtcNow;
        if (_gpuRun) { RefreshGpuReaders(); PushGpuState(); }
        // the preview's rate changed while this was being built: catch up
        if (request.PreviewRate != _previewRate) runner.SendCommands(PipelineBuilder.PreviewGateCommands(spec, _previewRate));
        _runnerShape = shape;
        _buses = buses;
        _lastProgress = null;
        lock (_progressWindow) _progressWindow.Clear();
        _health = _outputs.AnyActive ? "Connecting…" : "Running";
        _ = runner.Completion.ContinueWith(t => OnRunnerExited(runner, t.Result), TaskScheduler.Default);
        _outputs.BindBuses(buses, signatures);

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
        foreach (var sc in Composited())
        {
            var layers = PipelineBuilder.CompositedLayers(sc.Sources).Where(x => SourceTypes.IsVideo(SourceTypes.KindOf(x.Type))).ToList();
            foreach (var s in layers.Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Window && x.WindowTitle.Trim().Length > 0))
            {
                if (Anchors.Resolve(s) == Anchors.Fill) fill.TryAdd(PipelineBuilder.InputKey(s), new WindowCaptureBridge.Need(s, W, H, s.FillMode, plan.Light));
                else Want(s, LayoutMath.Horizontal(s, W, H, PipelineBuilder.AspectFor(s, _knownSizes)).W);
            }
            if (vertical is { } v)
                foreach (var s in PipelineBuilder.VerticalLayers(sc.Sources.ToList(), includeHidden: true).Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Window && x.WindowTitle.Trim().Length > 0))
                    Want(s, PipelineBuilder.VerticalWidth(s, v.W, v.H));
        }
        var needs = new Dictionary<string, WindowCaptureBridge.Need>(fill);
        foreach (var (key, (src, w)) in widths)
        {
            if (needs.ContainsKey(key)) continue;
            int ww = SmartTuner.Even((int)Math.Clamp(Math.Round(w), 64, 3840));
            int hh = SmartTuner.Even((int)Math.Clamp(Math.Round(ww / PipelineBuilder.AspectFor(src, _knownSizes)), 36, 2160));
            needs[key] = new WindowCaptureBridge.Need(src, ww, hh, Anchors.FillFit, plan.Light);
        }
        return needs;
    }

    private static readonly Regex InputHeader = new(@"^Input #(\d+),", RegexOptions.Compiled);
    private static readonly Regex VideoStream = new(@"^\s+Stream #(\d+):\d+.*?: Video: .*?\b(\d{2,5})x(\d{2,5})\b", RegexOptions.Compiled);

    /// <summary>Learns each input's real picture size from ffmpeg's log (exact layout instead of guessed aspect ratios).</summary>
    /// <summary>
    /// The size a web page is drawn at: as big as it appears on the biggest canvas it's on, never bigger than the
    /// page itself (a 1920×1080 alert box on a 960×540 stream is drawn at 960×540: a quarter of the pixels to
    /// render, convert and pipe). A cropped page keeps its full size (crops are in page pixels).
    /// </summary>
    public static (int W, int H) BrowserRenderSize(StudioSource src, EncodePlan plan, (int W, int H)? vertical, IEnumerable<StudioScene> scenes)
    {
        var (pw, ph) = BrowserBridge.SizeOf(src);
        string key = PipelineBuilder.InputKey(src);
        double aspect = pw / (double)ph, f = 0;
        foreach (var sc in scenes)
            foreach (var s in sc.Sources.Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Browser && PipelineBuilder.InputKey(x) == key))
            {
                if (s.CropLeft + s.CropTop + s.CropRight + s.CropBottom > 0) return (pw, ph);
                var r = LayoutMath.Horizontal(s, plan.Width, plan.Height, aspect, (pw, ph));
                f = Math.Max(f, Math.Max(r.W / pw, r.H / ph));
                if (vertical is { } v && LayoutMath.OnVerticalCanvas(s))
                {
                    var vr = LayoutMath.Vertical(s, v.W, v.H, aspect, sc.Sources.Any(c => SourceTypes.KindOf(c.Type) == SourceKind.Camera && c.Shown));
                    f = Math.Max(f, Math.Max(vr.W / pw, vr.H / ph));
                }
            }
        if (f <= 0 || f >= 0.9) return (pw, ph);
        return (SmartTuner.Even(Math.Max(64, (int)Math.Ceiling(pw * f))), SmartTuner.Even(Math.Max(64, (int)Math.Ceiling(ph * f))));
    }

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
        if (key.StartsWith("browser|", StringComparison.Ordinal)) return; // drawn at the size it's shown: its layout size is known
        var size = (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        if (_knownSizes.TryGetValue(key, out var old) && old == size) return;
        _knownSizes[key] = size;
        RefreshLayout(); // vertical auto layout depends on the real aspect ratios
    }

    // ------------------------------------------------------------------ the stream timer

    /// <summary>The clock behind every Timer source (the Timer dock sets it; one clock across all scenes).</summary>
    public StreamTimerState Timer { get; set; } = new();
    private readonly string _timerDir = Path.Combine(StateStore.Folder, "cache", "timer");
    private volatile string[] _timerFiles = Array.Empty<string>();
    private string _timerShown = "", _timerSized = "";

    private string TimerFile(Guid id) => Path.Combine(_timerDir, $"timer_{id:N}.txt");

    private static string FfColour(string? hex)
    {
        string h = (hex ?? "").Trim().TrimStart('#');
        return h.Length is 6 or 8 && h.All(Uri.IsHexDigit) ? "0x" + h[..6] : "0xFFFFFF";
    }

    /// <summary>
    /// Works out each timer card, books its size for the layout and writes its first reading. The card is sized for
    /// the longest reading the clock can show, so it never changes width while it runs.
    /// </summary>
    private Dictionary<Guid, string> BuildTimerCards(IEnumerable<StudioScene> scenes, int outputHeight)
    {
        var filters = new Dictionary<Guid, string>();
        var sources = scenes.SelectMany(sc => sc.Sources)
            .Where(s => SourceTypes.KindOf(s.Type) == SourceKind.Timer).ToList();
        if (sources.Count == 0) { _timerFiles = Array.Empty<string>(); return filters; }
        if (TimerCard.Face() is not { } face)
        {
            Message?.Invoke(this, new EngineMessageEventArgs("The timer needs a fixed-width font (Consolas) and none was found, so it isn't drawn.", true));
            _timerFiles = Array.Empty<string>();
            return filters;
        }
        var now = DateTime.UtcNow;
        string widest = Timer.Widest(now), text = Timer.Display(now, DateTime.Now);
        _timerShown = text; _timerSized = widest;
        try { Directory.CreateDirectory(_timerDir); } catch { }
        var files = new List<string>();
        foreach (var s in sources)
        {
            double fs = Math.Clamp(s.FontSize, 14, 400) * outputHeight / 1080.0;
            var L = TimerCard.Measure(s.TimerStyle, s.TimerTitle, widest, fs, face.Advance);
            string file = TimerFile(s.Id);
            try { TimerCard.Write(file, text); } catch { continue; }
            _knownSizes[PipelineBuilder.InputKey(s)] = (L.W, L.H);
            filters[s.Id] = TimerCard.Filter(L, s.TimerStyle, s.TimerTitle, face.Path, file, FfColour(s.TextColor));
            files.Add(file);
        }
        _timerFiles = files.ToArray();
        return filters;
    }

    /// <summary>Four times a second: the reading goes to disk and drawtext picks it up on the next frame.</summary>
    private void TimerTick()
    {
        var files = _timerFiles;
        if (files.Length == 0) return;
        try
        {
            var now = DateTime.UtcNow;
            string text = Timer.Display(now, DateTime.Now);
            if (text == _timerShown) return;
            _timerShown = text;
            foreach (var f in files) TimerCard.Write(f, text);
            // the reading has outgrown the card it was drawn for (a longer title of a finish, or time added)
            if (text.Length > _timerSized.Length) TimerOutgrew?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    /// <summary>Raised when the reading no longer fits the card, so the scene is rebuilt at the new width.</summary>
    public event EventHandler? TimerOutgrew;

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
        bool encoded = _runnerShape is { } sh && (sh.Main || sh.VerticalEncode || sh.Own.Length > 0 || sh.Clip);
        if (_runnerShape?.Main == true) _cache.EndSession();
        if (_runnerShape?.Clip == true) _clipCache.EndSession();
        if (_vertCache.HasActiveSession) _vertCache.EndSession();
        if (encoded) _audio.ClosePipes();
        _cameras.ClosePipes(); // readers stay open (warm) for the next run
        StopGpu();
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
            try { File.Copy(Path.Combine(_logDir, "ffmpeg-last.log"), Path.Combine(_logDir, "ffmpeg-crash.log"), true); } catch { } // for finding the cause
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

            if (MustKeepRunning)
            {
                // the encoder died while you're recording / live: it's restarted, again and again if it must; the
                // destinations wait ("reconnecting") and the recording carries on in the new run. Never ended here.
                bool afterChange = _levelBeforeChange >= 0 && DateTime.UtcNow - _levelChangedUtc < TimeSpan.FromSeconds(45) && ranFor < TimeSpan.FromSeconds(30);
                if (afterChange && _governor.Level != _levelBeforeChange)
                {
                    // it broke right after the governor changed the settings: back to the ones that were working
                    LogEvent($"ENCODER STOPPED {ranFor.TotalSeconds:0}s after switching to \"{PerfLevel.At(_governor.Level).Name}\": back to \"{PerfLevel.At(_levelBeforeChange).Name}\"");
                    _governor.Force(_levelBeforeChange);
                    _governorHoldUntil = DateTime.UtcNow + TimeSpan.FromMinutes(10);
                    _levelBeforeChange = -1;
                }
                // every second without an encoder is a second missing from the recording: the first tries are quick
                if (ranFor > TimeSpan.FromSeconds(30)) _reconnectAttempt = 0;
                double delay = new[] { 0.5, 1, 2, 4, 8, 15, 30 }[Math.Min(_reconnectAttempt, 6)];
                _reconnectAttempt++;
                LogEvent($"ENCODER STOPPED after {ranFor.TotalSeconds:0}s (exit {code}): {err} | restarting in {delay:0.#}s (attempt {_reconnectAttempt}), {WhatsRunning()} kept");
                Message?.Invoke(this, new EngineMessageEventArgs($"The encoder stopped ({err}). Restarting in {Math.Max(1, Math.Round(delay)):0}s; your {WhatsRunning()} keeps going.", true));
                ScheduleReconnect(TimeSpan.FromSeconds(delay));
            }
            else
            {
                // only the preview was running
                LogEvent($"PREVIEW STOPPED after {ranFor.TotalSeconds:0}s (exit {code}): {err}");
                Message?.Invoke(this, new EngineMessageEventArgs(err, true));
                if (DesiredShape().Run && ranFor > TimeSpan.FromSeconds(8))
                {
                    try { await StartRunnerAsync(false); } catch (Exception ex) { Message?.Invoke(this, new EngineMessageEventArgs(ex.Message, true)); }
                }
                else
                {
                    PreviewOn = false;
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
                // keep enough history for a voice clip too
                // keep enough for the longest clip that can be asked for (a death clip covers the whole fight)
                int keep = Math.Max(Settings.ReplaySeconds, Settings.VoiceClipEnabled ? Settings.VoiceClipSeconds : 0);
                if (Settings.DeathClips) keep = Math.Max(keep, (int)(DeathDetector.MaxFight + DeathDetector.BeforeFight).TotalSeconds + 10);
                _cache.Housekeep(Math.Clamp(keep, 5, 600));
                _clipCache.Housekeep(Math.Clamp(keep, 5, 600));
                _vertCache.Housekeep(Math.Clamp(keep, 5, 600));
                if (!_stuckWarned && _cache.SegmentsStuck())
                {
                    _stuckWarned = true;
                    Message?.Invoke(this, new EngineMessageEventArgs($"{EncoderCatalog.DisplayName(CurrentPlan?.Encoder ?? "")} is not producing keyframes. Replays and clips will fail. " +
                        "Choose another encoder in Settings → Stream.", true));
                }
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

    /// <summary>Diagnostics: the governor's inputs right now (speed over 4 s, compositor backlog).</summary>
    public (double Speed, double Backlog) PerformanceSignals() =>
        (WindowedRates(4).speed, Math.Max(_cameras.MaxBacklogSeconds(), Math.Max(_windows.MaxBacklogSeconds(), _browsers.MaxBacklogSeconds())));

    /// <summary>Rolling (not since-start) output fps and speed over the last ~15 s of progress reports.</summary>
    /// <returns>fps, speed and the window length in seconds.</returns>

    private (double fps, double speed, double span) WindowedRates(double seconds = 15)
    {
        (DateTime t, ProgressInfo p)[] w;
        var since = DateTime.UtcNow - TimeSpan.FromSeconds(seconds);
        lock (_progressWindow) w = _progressWindow.Where(x => x.t >= since).ToArray();
        if (w.Length < 2) return (0, 0, 0);
        double dt = (w[^1].t - w[0].t).TotalSeconds;
        if (dt < 0.5) return (0, 0, dt);
        double fps = (w[^1].p.Frame - w[0].p.Frame) / dt;
        double speed = (w[^1].p.OutTime - w[0].p.OutTime).TotalSeconds / dt;
        return (fps, speed, dt);
    }

    /// <summary>
    /// Once a second: feed the governor the pipeline's speed (over the last ~4 s, so heavy lag is caught quickly),
    /// how far frames are queuing up in front of the compositor, and the deepest upload queue; apply its decision.
    /// </summary>
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(10);
    private DateTime _lastStallRestart = DateTime.MinValue;
    /// <summary>How often the stall watchdog had to rebuild a pipeline that stopped producing frames.</summary>
    public int StallRestarts { get; private set; }

    private void EvaluateHealth()
    {
        var r = _runner;
        if (r is null) { if (!Reconnecting) _health = "Idle"; return; }
        var (fpsNow, speed, span) = WindowedRates(4);
        var outs = _outputs.Snapshot();
        double backlog = Math.Max(_cameras.MaxBacklogSeconds(), Math.Max(_windows.MaxBacklogSeconds(), _browsers.MaxBacklogSeconds()));
        double upload = outs.Where(o => o.State == OutputState.Live).Select(o => o.BufferedSeconds).DefaultIfEmpty(0).Max();
        if (span < 2) speed = 0; // not enough progress reports yet

        // ---- stall watchdog: a pipeline that stops producing frames (a capture that hangs, a driver hiccup)
        // shows no lag numbers at all, so it is caught by silence: no progress for 10 s → rebuild it ----
        var lastSign = _lastProgress is null ? r.StartedUtc : _lastProgressAt;
        if (DateTime.UtcNow - lastSign > StallAfter && DateTime.UtcNow - _lastStallRestart > StallAfter)
        {
            _lastStallRestart = DateTime.UtcNow;
            StallRestarts++;
            _health = "Restarting a stalled pipeline";
            _ = Locked(async () =>
            {
                if (!ReferenceEquals(_runner, r)) return;
                try { File.Copy(Path.Combine(_logDir, "ffmpeg-last.log"), Path.Combine(_logDir, "ffmpeg-stalled.log"), true); } catch { } // for bug reports
                LogEvent("STALLED: no frames for 10 s, rebuilding the pipeline");
                Message?.Invoke(this, new EngineMessageEventArgs("The video pipeline stopped producing frames, so Leapcast restarted it.", true));
                _audio.Configure(AudioSources());
                await RestartAsync();
            });
            return;
        }
        {
            double tfps = CurrentPlan?.Fps ?? 0;
            if (PreviewShedding) ShedPreview(speed, tfps > 0 ? fpsNow / tfps : 1, span >= 2 && DateTime.UtcNow - r.StartedUtc > TimeSpan.FromSeconds(8), DateTime.UtcNow);
        }
        if (_governorPaused || DateTime.UtcNow < _governorHoldUntil) return;
        bool adapt = Settings.AdaptivePerformance;

        int before = _governor.Level;
        double target = CurrentPlan?.Fps ?? 0;
        double fpsRatio = span >= 2 && target > 0 ? fpsNow / target : 0;
        var d = _governor.Evaluate(new PerformanceGovernor.Sample(speed, backlog, upload, DateTime.UtcNow - r.StartedUtc, fpsRatio), DateTime.UtcNow,
            useful: level => adapt && RungEffect(level) != RungEffect(before),
            // stepping up rebuilds the pipeline: freely when nothing is live; mid-stream only to a rung with the same
            // picture size (a brief pause, destinations stay connected: e.g. back from 30 to 60 FPS) after 5 calm minutes
            mayStepUp: (!_outputs.AnyActive && !IsRecording)
                       || (DateTime.UtcNow - _governor.LastChange >= TimeSpan.FromMinutes(5) && SameSizeAbove()),
            adaptBitrate: Settings.AutoAdjustBitrate && _outputs.AnyActive);

        // status text: the governor's view, plus which destination is in trouble
        var slow = outs.Where(o => o.State == OutputState.Live && o.BufferedSeconds > 2.5).Select(o => o.Name).ToList();
        _health = slow.Count > 0 ? "Upload struggling: " + string.Join(", ", slow)
            : outs.FirstOrDefault(o => o.State == OutputState.Reconnecting) is { } rc ? $"Reconnecting {rc.Name}…"
            : _governor.Condition;

        if (d.Action == PerformanceGovernor.Action.None) return;
        string text = d.Action switch
        {
            PerformanceGovernor.Action.LowerBitrate =>
                $"Upload congestion ({string.Join(", ", slow)}): bitrate lowered 25%.",
            PerformanceGovernor.Action.StepDown =>
                $"Encoder overloaded ({d.Reason}). Switched to {PerfLevel.At(d.Level).Explanation}" +
                (!_outputs.AnyActive ? "."
                 : PlansFor(Settings, before).Main is var was && PlansFor(Settings, d.Level).Main is var now && (was.Width, was.Height) == (now.Width, now.Height)
                    ? " (brief pause, stream stays connected)." : " (destinations reconnect)."),
            _ => $"Performance recovered. Back to {PerfLevel.At(d.Level).Name.ToLowerInvariant()}.",
        };
        if (d.Action != PerformanceGovernor.Action.LowerBitrate) { _levelBeforeChange = before; _levelChangedUtc = DateTime.UtcNow; }
        LogEvent("GOVERNOR " + text);
        _ = Locked(async () =>
        {
            if (_runner is null) return;
            Message?.Invoke(this, new EngineMessageEventArgs(text, d.Action != PerformanceGovernor.Action.StepUp));
            _audio.Configure(AudioSources());
            await RestartAsync();
            PerformanceChanged?.Invoke(PerfLevel.At(_governor.Level));
        });
    }

    /// <summary>Would the next rung up keep the same picture size (so a mid-stream step up keeps destinations connected)?</summary>
    private bool SameSizeAbove()
    {
        int up = Math.Max(_governor.Floor, _governor.Level - 1);
        if (up == _governor.Level) return false;
        var (now, then) = (PlansFor(Settings, _governor.Level).Main, PlansFor(Settings, up).Main);
        return (now.Width, now.Height) == (then.Width, then.Height);
    }

    /// <summary>Raised after the governor moved to another rung (the app can show it).</summary>
    public event Action<PerfLevel>? PerformanceChanged;

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
        _lightTimer.Dispose();
        _timerTick.Dispose();
        _perfTick.Dispose();
        _gpuPipe?.Dispose();
        _gpu?.Dispose();
        _cameras.Dispose();
        _windows.Dispose();
        _browsers.Dispose();
    }
}
