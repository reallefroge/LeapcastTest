namespace LeapcastStudioAI;

/// <summary>
/// GPU scene compositing: the scenes are layered on the graphics card (<see cref="IGpuCanvas"/>), and FFmpeg gets
/// the finished canvas as one input, which it encodes, records and streams as before. Every scene's pictures then
/// reach FFmpeg as the same canvas; scene switches, fades, moving, resizing, showing and hiding happen on the GPU,
/// so none of them rebuilds anything. Audio is untouched (FFmpeg still mixes it per scene).
/// </summary>
public sealed partial class StreamEngine
{
    /// <summary>The source that stands for the whole canvas in FFmpeg's scenes (a counted pipe like a webcam's).</summary>
    public static readonly Guid GpuCanvasSourceId = new("6c0f2a9e-0b8e-4c1e-9b7a-6a4d1f3c2b10");
    private static readonly StudioSource CanvasSource = new()
    {
        Id = GpuCanvasSourceId, Name = "Scene (GPU)", Type = SourceTypes.Camera, DeviceName = "leapcast-gpu-canvas",
        Anchor = Anchors.Fill, FillMode = Anchors.FillStretch, CameraDenoise = false, CameraAutoLight = false,
    };

    private IGpuCanvas? _gpu;
    private CountedFramePipe? _gpuPipe;
    private bool _gpuRun;                    // the running pipeline takes its picture from the GPU canvas
    private string? _gpuReaders;             // what the capture readers were opened for (reopened when it changes)
    private GpuImages? _gpuImages;
    private volatile Dictionary<string, (SourceKind Kind, StudioSource Src)> _gpuKeys = new();
    private readonly Dictionary<Guid, (string Sig, string? Png)> _gpuText = new();

    /// <summary>Scenes are layered on the graphics card right now.</summary>
    public bool GpuCompositing => _gpuRun && _gpu is not null;
    /// <summary>The GPU canvas: which graphics card and how long a frame takes, or why it isn't used.</summary>
    public string GpuStatus { get; private set; } = "off";
    /// <summary>Tests: the canvas itself (frames, uploads).</summary>
    public IGpuCanvas? GpuCanvas => _gpu;

    private bool _gpuUnavailable;   // the canvas couldn't start on this PC: don't keep trying on every change

    /// <summary>The scenes and settings as they are now want the GPU canvas.</summary>
    private bool GpuWanted() => !_gpuUnavailable && GpuBlocker(DesiredShape(), Composited()) is null;

    /// <summary>For the health log: GPU canvas on (which card, ms a frame) or why not.</summary>
    public string GpuStatusLine => _gpuRun && _gpu is { } g ? FormattableString.Invariant($"GPU canvas {g.Adapter}, {g.FrameMs:0.0} ms a frame, {g.Uploads} uploads") : "GPU canvas " + GpuStatus;

    /// <summary>Why the GPU canvas can't layer these scenes (null = it can).</summary>
    private string? GpuBlocker(Shape shape, IEnumerable<StudioScene> scenes)
    {
        if (!Settings.GpuCompositing) return "off";
        if (StudioMode) return "studio mode needs the classic compositor";
        if (shape.Vertical) return "the 9:16 canvas needs the classic compositor";
        if (shape.Clip) return "9:16 clips need the classic compositor";
        var display = scenes.SelectMany(sc => PipelineBuilder.CompositedLayers(sc.Sources))
            .FirstOrDefault(s => SourceTypes.KindOf(s.Type) is SourceKind.Display or SourceKind.Unsupported);
        if (display is not null) return $"\"{display.Name}\" (screen capture) needs the classic compositor";
        return null;
    }

    /// <summary>FFmpeg's view of the scenes in GPU mode: each scene's pictures are the canvas; its sounds are its own.</summary>
    private static List<StudioScene> GpuSwap(IEnumerable<StudioScene> scenes) =>
        scenes.Select(sc => new StudioScene
        {
            Id = sc.Id, Name = sc.Name,
            Sources = new(sc.Sources.Where(s => SourceTypes.IsAudio(SourceTypes.KindOf(s.Type))).Prepend(CanvasSource)),
        }).ToList();

    private List<StudioScene> FfScenes(IEnumerable<StudioScene> scenes) => _gpuRun ? GpuSwap(scenes) : scenes.ToList();

    /// <summary>The canvas for this run (kept across rebuilds of the same size and rate); false = use the classic compositor.</summary>
    private bool EnsureGpuCanvas(EncodePlan plan)
    {
        if (_gpu is { } g && g.Width == (plan.Width & ~1) && g.Height == (plan.Height & ~1) && g.Fps == plan.Fps && g.Error is null) return true;
        _gpu?.Dispose();
        _gpu = null;
        try { _gpu = _platform.CreateGpuCanvas(plan.Width, plan.Height, plan.Fps, GpuFrameOf); }
        catch (Exception ex) { GpuStatus = "couldn't start: " + ex.Message; _gpuUnavailable = true; return false; }
        if (_gpu is null) { GpuStatus = "not available on this system"; _gpuUnavailable = true; return false; }
        _gpuImages ??= new GpuImages(() => FfmpegPath);
        return true;
    }

    /// <summary>A source's latest picture, by input key (called by the canvas, on its own thread).</summary>
    private GpuFrame? GpuFrameOf(string key) => _gpuKeys.TryGetValue(key, out var e) ? GpuFrameFor(key, e.Kind, e.Src) : null;

    private GpuFrame? GpuFrameFor(string key, SourceKind kind, StudioSource src)
    {
        switch (kind)
        {
            case SourceKind.Window:
                return _windows.Latest(key) is { } w ? new GpuFrame(w.Frame, w.Width, w.Height, FramePixels.I420) : null;
            case SourceKind.Camera:
                return _cameras.Latest(key) is { } c ? new GpuFrame(c.Frame, c.Width, c.Height, FramePixels.I420) : null;
            case SourceKind.Browser:
                return _browsers.LatestBgra(key);
            case SourceKind.Image:
                return _gpuImages?.Get(src.FilePath);
            case SourceKind.Text:
                string? png;
                lock (_gpuText) png = _gpuText.TryGetValue(src.Id, out var t) ? t.Png : null;
                return png is null ? null : _gpuImages?.Get(png);
        }
        return null;
    }

    /// <summary>
    /// The canvas's state from the scenes as they are now (every scene, the one on air, and a fade when one is
    /// starting). Cheap: called on every change, drag and auto-light step.
    /// </summary>
    private void PushGpuState(int? fadeFrom = null)
    {
        if (!_gpuRun || _gpu is not { } gpu) return;
        int W = gpu.Width, H = gpu.Height;
        var scenes = Composited();
        var keys = new Dictionary<string, (SourceKind, StudioSource)>();
        var gs = new List<GpuScene>();
        foreach (var sc in scenes)
        {
            var layers = new List<GpuLayer>();
            foreach (var src in PipelineBuilder.CompositedLayers(sc.Sources))
            {
                var kind = SourceTypes.KindOf(src.Type);
                if (!SourceTypes.IsVideo(kind) || !src.Shown) continue;
                string key = PipelineBuilder.InputKey(src);
                keys[key] = (kind, src);
                var frame = GpuFrameFor(key, kind, src);
                if (frame is not null && kind is not SourceKind.Window) _knownSizes[key] = (frame.Width, frame.Height);
                (int, int)? natural = frame is not null && SourceTypes.IsGraphic(kind) ? (frame.Width, frame.Height) : null;
                // a window's picture is already cut and sized by its reader; everything else is cut here
                double u0 = 0, v0 = 0, u1 = 1, v1 = 1;
                if (kind != SourceKind.Window && frame is not null)
                {
                    u0 = Math.Max(0, src.CropLeft) / (double)frame.Width; u1 = 1 - Math.Max(0, src.CropRight) / (double)frame.Width;
                    v0 = Math.Max(0, src.CropTop) / (double)frame.Height; v1 = 1 - Math.Max(0, src.CropBottom) / (double)frame.Height;
                    if (u1 <= u0 || v1 <= v0) (u0, v0, u1, v1) = (0, 0, 1, 1);
                }
                double aspect = kind == SourceKind.Window && frame is not null ? frame.Width / (double)frame.Height : PipelineBuilder.AspectFor(src, _knownSizes);
                var rect = LayoutMath.Horizontal(src, W, H, aspect, natural);
                LightSettings? light = null;
                if (kind == SourceKind.Camera && src.CameraAutoLight) lock (_light) light = _light.TryGetValue(key, out var l) ? l.Current : null;
                layers.Add(new GpuLayer(key, rect, u0, v0, u1, v1, src.Mirror, light));
            }
            gs.Add(new GpuScene(layers));
        }
        _gpuKeys = keys;
        int active = Math.Max(0, scenes.FindIndex(s => ReferenceEquals(s, ActiveScene)));
        var prev = _gpuState;
        GpuState st;
        if (fadeFrom is int from && from != active && Settings.Transition != "Cut")
            st = new GpuState(gs, active, from, DateTime.UtcNow, Math.Clamp(Settings.TransitionMs, 0, 5000) / 1000.0);
        else if (prev is { From: int pf } && prev.Mix(DateTime.UtcNow) < 1 && prev.Active == active)
            st = new GpuState(gs, active, pf, prev.FadeStartUtc, prev.FadeSeconds);   // a change during a fade keeps fading
        else st = new GpuState(gs, active);
        _gpuState = st;
        gpu.SetState(st);
        RenderGpuText(scenes);
    }

    private GpuState? _gpuState;

    /// <summary>Text sources: drawn to an image when their text or style changes (no rebuild).</summary>
    private void RenderGpuText(IEnumerable<StudioScene> scenes)
    {
        int h = _gpu?.Height ?? 720;
        foreach (var s in scenes.SelectMany(sc => sc.Sources).Where(s => SourceTypes.KindOf(s.Type) == SourceKind.Text && s.Shown))
        {
            string sig = $"{s.Text}|{s.FontSize}|{s.TextColor}|{h}";
            lock (_gpuText) if (_gpuText.TryGetValue(s.Id, out var t) && t.Sig == sig) continue;
            string? png = null;
            try { png = _platform.RenderText(s, h); } catch { }
            lock (_gpuText) _gpuText[s.Id] = (sig, png);
        }
    }

    /// <summary>
    /// In GPU mode FFmpeg never sees the sources, so a new camera, window or page doesn't rebuild anything: its
    /// reader is just started (and one no longer used, stopped).
    /// </summary>
    private void RefreshGpuReaders()
    {
        if (!_gpuRun || CurrentPlan is not { } plan || FfmpegPath is not { } ffmpeg) return;
        var scenes = Composited();
        var camUses = new Dictionary<string, (InputSpec device, List<StudioSource> uses)>();
        foreach (var src in scenes.SelectMany(sc => PipelineBuilder.CompositedLayers(sc.Sources)).Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera))
        {
            string key = PipelineBuilder.InputKey(src);
            if (camUses.TryGetValue(key, out var e)) { if (!e.uses.Contains(src)) e.uses.Add(src); continue; }
            var dev = CameraDeviceOverride?.Invoke(src) ?? PipelineBuilder.CameraDeviceInput(src);
            if (dev is not null) camUses[key] = (dev, new List<StudioSource> { src });
        }
        var winNeeds = WindowNeeds(plan, null);
        var browserUses = new Dictionary<string, StudioSource>();
        foreach (var src in scenes.SelectMany(sc => PipelineBuilder.CompositedLayers(sc.Sources))
                     .Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Browser && x.Url.Trim().Length > 0))
            browserUses.TryAdd(PipelineBuilder.InputKey(src), src);
        string sig = string.Join(";", camUses.Select(c => c.Key + "#" + string.Join(",", c.Value.uses.Select(u => $"{u.Id}:{LayoutMath.Horizontal(u, plan.Width, plan.Height, 16 / 9.0).W:0}")).OrderBy(x => x)).OrderBy(x => x))
                     + "|" + string.Join(";", winNeeds.Select(w => $"{w.Key}#{w.Value.Width}x{w.Value.Height}#{w.Value.Fill}").OrderBy(x => x))
                     + "|" + string.Join(";", browserUses.Keys.OrderBy(x => x));
        if (sig == _gpuReaders) return;
        bool first = _gpuReaders is null;
        _gpuReaders = sig;
        if (first) return; // the run just opened them
        double origin = _runnerOrigin;
        // (their pipes aren't read in GPU mode: the canvas takes each reader's latest picture directly)
        _cameras.Open(ffmpeg, camUses, plan.Width, plan.Height, plan.Fps, origin);
        _windows.Open(ffmpeg, winNeeds, plan.Fps, origin);
        _browsers.Open(browserUses, origin, w => Message?.Invoke(this, new EngineMessageEventArgs(w, true)));
    }

    /// <summary>After any change while running: readers for new sources, then the new state.</summary>
    private void GpuChanged(int? fadeFrom = null)
    {
        if (!_gpuRun) return;
        try { RefreshGpuReaders(); } catch (Exception ex) { Message?.Invoke(this, new EngineMessageEventArgs("GPU canvas: " + ex.Message, true)); }
        PushGpuState(fadeFrom);
    }

    /// <summary>Auto light in GPU mode: the correction is part of the canvas state.</summary>
    private void GpuAutoLight()
    {
        if (!_gpuRun || _gpu is null) return;
        bool changed = false;
        foreach (var key in Composited().SelectMany(sc => sc.Sources)
                     .Where(s => SourceTypes.KindOf(s.Type) == SourceKind.Camera && s.CameraAutoLight && s.Shown)
                     .Select(PipelineBuilder.InputKey).Distinct())
        {
            if (_cameras.Latest(key) is not { } frame) continue;
            var (mean, p95) = CameraAutoLight.Measure(frame.Frame, frame.Width, frame.Height);
            CameraAutoLight light;
            lock (_light) { if (!_light.TryGetValue(key, out light!)) _light[key] = light = new CameraAutoLight(); }
            if (light.Update(mean, p95) is not null) changed = true;
        }
        if (changed) PushGpuState();
    }

    private void StopGpu()
    {
        _gpuPipe?.Dispose();
        _gpuPipe = null;
        _gpuRun = false;
        _gpuReaders = null;
    }
}
