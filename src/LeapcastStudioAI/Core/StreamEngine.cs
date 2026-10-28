using System.Globalization;

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
}

public sealed record EngineStatus(
    bool Running, bool Live, bool Recording, bool ReplayBuffer, bool Reconnecting,
    string? PlanSummary, int CapKbps,
    double Fps, long Dropped, long Duplicated, double Speed,
    double RecentKbps, double AverageKbps,
    TimeSpan LiveFor, TimeSpan RecordingFor,
    string Health, string? LastError);

public sealed class EngineMessageEventArgs(string text, bool isError) : EventArgs
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
}

/// <summary>
/// Orchestrates one ffmpeg pipeline: capture → composite → single encode → (RTMP + segment cache) + preview.
/// Handles reconnects, bandwidth adaptation, recording and replay. All public operations are serialized.
/// </summary>
public sealed class StreamEngine : IDisposable
{
    private readonly IAudioBridge _audio;
    private readonly IPlatformServices _platform;
    private readonly SegmentCache _cache;
    private readonly CameraBridge _cameras;
    private readonly string _logDir;
    private readonly string _tempDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _life = new();

    private FfmpegRunner? _runner;
    private bool _runnerEncodes, _runnerStreams;
    private ProgressInfo? _lastProgress;
    private readonly Queue<(DateTime t, ProgressInfo p)> _progressWindow = new();
    private CancellationTokenSource? _reconnectCts;
    private int _reconnectAttempt;
    private DateTime? _liveSince;
    private DateTime _lastAdjust = DateTime.MinValue;
    private double _bitrateScale = 1.0;
    private readonly HashSet<long> _countedSegments = new();
    private long _liveBytes;
    private string? _lastError;
    private string _health = "Idle";

    public string? FfmpegPath { get; set; }
    /// <summary>Optional input substitution (used by tests and non-Windows hosts).</summary>
    public Func<StudioSource, double, InputSpec?>? InputOverride { get; set; }
    /// <summary>Replaces the webcam device input of the camera reader (tests / non-Windows hosts).</summary>
    public Func<StudioSource, InputSpec?>? CameraDeviceOverride { get; set; }
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
    private double _runnerOrigin;
    private string? _runnerFingerprint;
    public StudioSettings Settings { get; private set; } = new();
    private string _streamKey = "";

    public bool PreviewOn { get; private set; }
    public bool Live { get; private set; }
    public bool ReplayBuffer { get; private set; }
    public bool Recording => _cache.IsRecording;
    public bool Running => _runner is not null;
    public bool Reconnecting => _reconnectCts is not null;
    public EncodePlan? CurrentPlan { get; private set; }
    /// <summary>How many times an ffmpeg pipeline has been started (diagnostics/tests: transitions must not bump it).</summary>
    public int PipelineStarts { get; private set; }
    public string CameraStats() => _cameras.Stats();

    /// <summary>Diagnostics: (wall clock − origin, last reported ffmpeg output time + age of that report).</summary>
    public (double wall, double reported) StreamClock()
    {
        double wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - _runnerOrigin;
        var p = _lastProgress;
        double rep = p is null ? double.NaN : p.OutTime.TotalSeconds + (DateTime.UtcNow - _lastProgressAt).TotalSeconds;
        return (wall, rep);
    }
    private DateTime _lastProgressAt;

    public event Action<byte[]>? FrameReady;
    public event EventHandler<EngineMessageEventArgs>? Message;
    public event Action<EngineStatus>? StatusChanged;
    /// <summary>Raised when the engine itself turns a mode off (e.g. stream failed and won't reconnect).</summary>
    public event Action? ModesChanged;

    public int PreviewWidth => 640;
    public int PreviewHeight => 360;

    public StreamEngine(IAudioBridge audio, IPlatformServices platform, string dataRoot)
    {
        _audio = audio;
        _platform = platform;
        _cache = new SegmentCache(Path.Combine(dataRoot, "cache", "segments"));
        _logDir = Path.Combine(dataRoot, "logs");
        _tempDir = Path.Combine(dataRoot, "cache", "overlays");
        Directory.CreateDirectory(_tempDir);
        _cameras = new CameraBridge(Path.Combine(dataRoot, "cache", "pipes"));
        _cameras.Error += msg => Message?.Invoke(this, new EngineMessageEventArgs(msg, true));
        _ = Task.Run(MonitorLoopAsync);
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
        EncodersProbed = true;
    }

    public EncodePlan PlanFor(StudioSettings s)
    {
        var plan = SmartTuner.Resolve(s, Encoders, Environment.ProcessorCount);
        var reasons = plan.Reasons.ToList();
        var shown = _scenes.SelectMany(sc => PipelineBuilder.VisibleLayers(sc.Sources)).ToList();
        // Old-style (GDI) window capture can't deliver 60 smooth frames from a DirectX game; 60 FPS output would
        // just repeat frames unevenly (judder). Cap at 30 when it's in use.
        bool gdiWindow = shown.Any(x =>
            (SourceTypes.KindOf(x.Type) == SourceKind.Window
             && (!GpuWindowCaptureSupported || string.Equals(x.CaptureMethod, "GDI", StringComparison.OrdinalIgnoreCase)))
            || (SourceTypes.KindOf(x.Type) == SourceKind.Display
                && (_gpuDisplayFailed || string.Equals(x.CaptureMethod, "GDI", StringComparison.OrdinalIgnoreCase))));
        if (gdiWindow && plan.Fps > 30)
        {
            plan = plan with { Fps = 30 };
            reasons.Add("Capture is using the slower GDI method, so the stream runs at 30 FPS to stay smooth.");
        }
        bool wizard = shown.Any(x => x.WindowTitle.Contains("Wizard101", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("Wizard101", StringComparison.OrdinalIgnoreCase));
        if (wizard && s.ContentType == "HighMotion")
            reasons.Add("Tip: Wizard101 is turn-based. \"Low-motion\" (30 FPS) looks the same on stream, halves the data and leaves more power for smooth capture.");
        plan = plan with { Reasons = reasons };
        if (_bitrateScale < 0.999)
        {
            int cap = Math.Max(1000, (int)(plan.CapKbps * _bitrateScale));
            plan = plan with { CapKbps = cap, Reasons = plan.Reasons.Append($"Lowered to {cap} kbps because the connection was struggling.").ToList() };
        }
        return plan;
    }

    // ------------------------------------------------------------------ public operations

    public Task UpdateAsync(StudioScene scene, StudioSettings settings, string streamKey, bool restartIfRunning) =>
        UpdateAsync(new[] { scene }, scene, settings, streamKey, restartIfRunning);

    /// <summary>
    /// Updates the scene collection / active scene / settings. Switching the active scene while running is a
    /// seamless in-encoder transition (no restart). Editing sources, or <paramref name="restartIfRunning"/>,
    /// rebuilds the pipeline.
    /// </summary>
    public Task UpdateAsync(IReadOnlyList<StudioScene> scenes, StudioScene active, StudioSettings settings, string streamKey,
        bool restartIfRunning) => Locked(async () =>
    {
        var list = scenes.Count > 0 ? scenes.ToList() : new List<StudioScene> { active };
        int index = list.FindIndex(s => ReferenceEquals(s, active));
        if (index < 0) index = list.FindIndex(s => s.Id == active.Id);
        if (index < 0) { list.Add(active); index = list.Count - 1; }
        _scenes = list;
        Scene = active;
        Settings = settings.Clone();
        _streamKey = streamKey ?? "";

        if (_runner is null) { _activeIndex = index; return; }
        bool structural = Fingerprint(_scenes) != _runnerFingerprint;
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
    });

    private void Transition(int from, int to)
    {
        var r = _runner; var spec = _runnerSpec;
        if (r is null || spec is null) return;
        double seconds = Settings.Transition == "Cut" ? 0.001 : Math.Clamp(Settings.TransitionMs, 100, 3000) / 1000.0;
        int lineCount = PipelineBuilder.TransitionCommands(spec, from, to, 0, seconds).Count;
        // Schedule against the pipeline's own clock (ffmpeg's latest output time, which tracks the frames being
        // composited), not the wall clock: if the machine falls behind real time, a wall-clock start time would
        // lie seconds in the future and the switch would appear frozen. Start after every command line has landed
        // (ffmpeg reads one per ~100 ms).
        var (wall, reported) = StreamClock();
        bool fresh = _lastProgress is not null && (DateTime.UtcNow - _lastProgressAt) < TimeSpan.FromSeconds(1.5);
        double now = fresh && !double.IsNaN(reported) ? reported : wall;
        double startAt = now + 0.15 * lineCount + 0.2;
        r.SendCommands(PipelineBuilder.TransitionCommands(spec, from, to, startAt, seconds));
    }

    /// <summary>Everything about the scene collection that changes the ffmpeg graph (names excluded).</summary>
    public static string Fingerprint(IEnumerable<StudioScene> scenes) =>
        System.Text.Json.JsonSerializer.Serialize(scenes.Select(sc => new
        {
            sc.Id,
            Sources = sc.Sources.Select(src => { var c = System.Text.Json.JsonSerializer.Deserialize<StudioSource>(System.Text.Json.JsonSerializer.Serialize(src))!; c.Name = ""; return c; }).ToList()
        }));

    public Task SetPreviewAsync(bool on) => Locked(async () => { PreviewOn = on; await ReconcileAsync(); });

    public Task StartLiveAsync() => Locked(async () =>
    {
        if (string.IsNullOrWhiteSpace(_streamKey))
            throw new InvalidOperationException("Add your stream key in Settings first.");
        if (string.IsNullOrWhiteSpace(Settings.ServerUrl))
            throw new InvalidOperationException("Add the stream server URL in Settings first.");
        Live = true;
        _bitrateScale = 1.0;
        _liveSince = DateTime.UtcNow;
        _countedSegments.Clear();
        _liveBytes = 0;
        _reconnectAttempt = 0;
        await ReconcileAsync(requireSuccess: true);
    });

    public Task StopLiveAsync() => Locked(async () =>
    {
        Live = false;
        _liveSince = null;
        CancelReconnect();
        await ReconcileAsync();
    });

    public Task StartRecordingAsync() => Locked(async () =>
    {
        if (_cache.IsRecording) return;
        _cache.StartRecording();
        try { await ReconcileAsync(requireSuccess: true); }
        catch { try { await _cache.StopRecordingAsync(FfmpegPath!, Path.Combine(_tempDir, "discard.mp4")); } catch { } throw; }
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

    public Task SetReplayBufferAsync(bool on) => Locked(async () => { ReplayBuffer = on; await ReconcileAsync(requireSuccess: on); });

    public async Task<string> SaveReplayAsync()
    {
        if (!(_runnerEncodes && _runner is not null))
            throw new InvalidOperationException("Turn on the replay buffer (or go live / record) first. Replays are cut from the encoded stream.");
        Directory.CreateDirectory(Settings.RecordingFolder);
        string file = Path.Combine(Settings.RecordingFolder, $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        return await _cache.SaveReplayAsync(RequireFfmpeg(), Math.Clamp(Settings.ReplaySeconds, 5, 600), file);
    }

    public async Task StopAllAsync()
    {
        await Locked(async () =>
        {
            Live = false; ReplayBuffer = false; PreviewOn = false;
            CancelReconnect();
            await StopRunnerAsync();
            _cameras.CloseAll();
        });
        if (_cache.IsRecording) { try { await StopRecordingAsync(); } catch { } }
    }

    // ------------------------------------------------------------------ pipeline management

    private List<StudioSource> AudioSources() => PipelineBuilder.AudioRepresentatives(_scenes);

    private (bool run, bool encode, bool stream) Desired()
    {
        bool encode = Live || ReplayBuffer || _cache.IsRecording;
        return (PreviewOn || encode, encode, Live);
    }

    private async Task ReconcileAsync(bool requireSuccess = false)
    {
        var (run, encode, stream) = Desired();
        if (!run) { await StopRunnerAsync(); _audio.Configure(Array.Empty<StudioSource>()); _cameras.CloseAll(); Publish(); return; }
        if (_runner is not null && _runnerEncodes == encode && _runnerStreams == stream) return;
        await StopRunnerAsync();
        await StartRunnerAsync(requireSuccess);
    }

    private async Task RestartAsync()
    {
        await StopRunnerAsync();
        if (Desired().run) await StartRunnerAsync(false);
    }

    private string RequireFfmpeg() =>
        FfmpegPath ?? throw new InvalidOperationException("FFmpeg wasn't found. Reinstall Leapcast or set the FFmpeg path in Settings → Advanced.");

    private async Task StartRunnerAsync(bool requireSuccess)
    {
        var ffmpeg = RequireFfmpeg();
        if (Scene is null) throw new InvalidOperationException("No scene selected.");
        var (_, encode, stream) = Desired();
        var plan = PlanFor(Settings);
        CurrentPlan = plan;

        // Pre-render text overlays.
        var rendered = new Dictionary<Guid, string>();
        foreach (var s in _scenes.SelectMany(sc => PipelineBuilder.VisibleLayers(sc.Sources)).Where(s => SourceTypes.KindOf(s.Type) == SourceKind.Text))
        {
            try { if (_platform.RenderText(s, plan.Height) is { } png) rendered[s.Id] = png; } catch { }
        }

        _audio.Configure(AudioSources());
        double origin = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        IReadOnlyDictionary<Guid, string> pipes = new Dictionary<Guid, string>();
        if (encode) pipes = _audio.OpenPipes(origin);

        // Webcams: one warm reader per device, fed to this run through counted pipes.
        var camUses = new Dictionary<string, (InputSpec device, List<StudioSource> uses)>();
        foreach (var src in _scenes.SelectMany(sc => PipelineBuilder.VisibleLayers(sc.Sources)).Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera))
        {
            string key = PipelineBuilder.InputKey(src);
            if (camUses.TryGetValue(key, out var e)) { e.uses.Add(src); continue; }
            var dev = CameraDeviceOverride?.Invoke(src) ?? PipelineBuilder.CameraDeviceInput(src);
            if (dev is not null) camUses[key] = (dev, new List<StudioSource> { src });
        }
        var camFeeds = _cameras.Open(ffmpeg, camUses, plan.Width, plan.Height, plan.Fps, origin);
        string? segPattern = encode ? _cache.BeginSession() : null;

        var request = new PipelineRequest
        {
            Scenes = _scenes,
            ActiveSceneIndex = _activeIndex,
            Plan = plan,
            StreamUrl = stream ? Platforms.BuildUrl(Settings.ServerUrl, _streamKey) : null,
            SegmentPattern = segPattern,
            SegmentSeconds = _cache.SegmentSeconds,
            Preview = true,
            PreviewWidth = PreviewWidth, PreviewHeight = PreviewHeight,
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
        };
        var spec = PipelineBuilder.Build(request);
        _runnerSpec = spec;
        _runnerOrigin = request.WallclockOrigin;
        _runnerFingerprint = Fingerprint(_scenes);
        foreach (var w in spec.Warnings) Message?.Invoke(this, new EngineMessageEventArgs(w, false));

        PipelineStarts++;
        _runnerCommand = string.Join(" ", spec.Args);
        var runner = new FfmpegRunner(ffmpeg, spec.Args, PreviewWidth, PreviewHeight, Path.Combine(_logDir, "ffmpeg-last.log"));
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
        _runner = runner;
        _runnerEncodes = encode;
        _runnerStreams = stream;
        _lastProgress = null;
        lock (_progressWindow) _progressWindow.Clear();
        _health = stream ? "Connecting…" : "Running";
        _ = runner.Completion.ContinueWith(t => OnRunnerExited(runner, t.Result), TaskScheduler.Default);

        if (requireSuccess)
        {
            // Give ffmpeg a moment: device/encoder/RTMP errors surface within the first seconds.
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
                if (stream) { Live = false; _liveSince = null; }
                else if (encode) ReplayBuffer = false;
                // If only going live failed (bad key, no internet), keep preview/recording alive.
                if (stream && Desired().run)
                {
                    try { await StartRunnerAsync(false); } catch { }
                }
                Publish();
                throw new InvalidOperationException(err);
            }
        }
        Publish();
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
        await r.StopAsync();
        r.Dispose();
        if (_runnerEncodes) { _cache.EndSession(); _audio.ClosePipes(); }
        _cameras.ClosePipes(); // readers stay open (warm) for the next run
        _runnerEncodes = _runnerStreams = false;
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
            bool wasStreaming = _runnerStreams;
            bool hadFrames = _lastProgress is { Frame: > 0 };
            await StopRunnerAsync();
            _lastError = err;
            if (TryGpuCaptureFallback(ranFor, hadFrames) && Desired().run)
            {
                try { await StartRunnerAsync(false); }
                catch (Exception ex) { Message?.Invoke(this, new EngineMessageEventArgs(ex.Message, true)); }
                Publish();
                return;
            }

            if (wasStreaming && Live && Settings.AutoReconnect && ranFor > TimeSpan.FromSeconds(8))
            {
                if (ranFor > TimeSpan.FromSeconds(60)) _reconnectAttempt = 0;
                int delay = new[] { 2, 4, 8, 15, 30 }[Math.Min(_reconnectAttempt, 4)];
                _reconnectAttempt++;
                Message?.Invoke(this, new EngineMessageEventArgs($"Stream dropped ({err}). Reconnecting in {delay}s…", true));
                ScheduleReconnect(TimeSpan.FromSeconds(delay));
            }
            else
            {
                if (wasStreaming) { Live = false; _liveSince = null; }
                Message?.Invoke(this, new EngineMessageEventArgs(err, true));
                // keep recording/replay/preview going without the stream if possible
                if (Desired().run && ranFor > TimeSpan.FromSeconds(8))
                {
                    try { await StartRunnerAsync(false); } catch (Exception ex) { Message?.Invoke(this, new EngineMessageEventArgs(ex.Message, true)); }
                }
                else
                {
                    PreviewOn = false; ReplayBuffer = false;
                    if (!Desired().run) { _audio.Configure(Array.Empty<StudioSource>()); _cameras.CloseAll(); }
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
        _health = "Reconnecting…";
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, cts.Token); } catch { return; }
            await Locked(async () =>
            {
                if (cts.IsCancellationRequested || !Live) return;
                _reconnectCts = null;
                try { await StartRunnerAsync(false); }
                catch (Exception ex)
                {
                    Message?.Invoke(this, new EngineMessageEventArgs("Reconnect failed: " + ex.Message, true));
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
                if (Live && _runnerStreams)
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
        if (span < 2) return;
        // With wall-clock input timestamps the output can only fall behind real time when the encoder or the
        // network can't keep up, so "output seconds per real second" is the honest congestion signal.
        bool struggling = span >= 8 && speed > 0 && speed < 0.95;
        double expected = CurrentPlan?.Fps ?? 30;
        bool bad = fps > 0 && fps < expected * 0.9;

        if (!struggling)
        {
            _health = _runnerStreams ? (bad ? "OK (frame rate dipping)" : "Excellent") : (bad ? "Running (frame rate dipping)" : "Running");
            return;
        }
        _health = "Struggling: output is falling behind real time";
        if (_runnerStreams && Live && Settings.AutoAdjustBitrate
            && DateTime.UtcNow - r.StartedUtc > TimeSpan.FromSeconds(20)
            && DateTime.UtcNow - _lastAdjust > TimeSpan.FromSeconds(60)
            && (CurrentPlan?.CapKbps ?? 0) > 1200)
        {
            _lastAdjust = DateTime.UtcNow;
            _bitrateScale *= 0.75;
            _ = Locked(async () =>
            {
                if (!Live || _runner is null) return;
                Message?.Invoke(this, new EngineMessageEventArgs(
                    "Your connection or encoder couldn't keep up, so the bitrate cap was lowered 25%. The stream will blink for a second.", true));
                await RestartAsync();
            });
        }
    }

    public EngineStatus Snapshot()
    {
        var p = _lastProgress;
        var (wfps, wspeed, _) = WindowedRates();
        double recent = _runnerEncodes ? _cache.RecentKbps() : 0;
        double avg = _countedSegments.Count > 0 ? _liveBytes * 8.0 / 1000.0 / (_countedSegments.Count * _cache.SegmentSeconds) : 0;
        return new EngineStatus(
            _runner is not null, Live, _cache.IsRecording, ReplayBuffer, Reconnecting,
            CurrentPlan?.Summary, CurrentPlan?.CapKbps ?? 0,
            wfps, p?.DroppedFrames ?? 0, p?.DupFrames ?? 0, wspeed,
            recent, avg,
            _liveSince is { } ls ? DateTime.UtcNow - ls : TimeSpan.Zero,
            _cache.RecordingStartedUtc is { } rs ? DateTime.UtcNow - rs : TimeSpan.Zero,
            _health, _lastError);
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
            || r.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase) || r.Contains("Cannot open connection", StringComparison.OrdinalIgnoreCase))
            return "Couldn't reach the streaming server. Check your internet connection, server URL and stream key.";
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

    public void Dispose()
    {
        _life.Cancel();
        // Run on the thread pool so a UI SynchronizationContext can't deadlock the wait.
        try { Task.Run(StopAllAsync).Wait(TimeSpan.FromSeconds(10)); } catch { }
        _audio.Dispose();
        _cameras.Dispose();
    }
}
