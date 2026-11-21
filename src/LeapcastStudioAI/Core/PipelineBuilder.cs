using System.Globalization;
using System.Text;

namespace LeapcastStudioAI;

/// <summary>One ffmpeg input: everything up to and including "-i path".</summary>
public sealed record InputSpec(IReadOnlyList<string> Args, bool Wallclock);

public sealed class PipelineRequest
{
    /// <summary>All scenes. Every scene is composited live so switching is a seamless in-encoder transition.</summary>
    public IReadOnlyList<StudioScene>? Scenes { get; init; }
    public int ActiveSceneIndex { get; init; }
    /// <summary>Single-scene shorthand (used when <see cref="Scenes"/> is null).</summary>
    public IReadOnlyList<StudioSource>? Sources { get; init; }
    public required EncodePlan Plan { get; init; }

    /// <summary>TS bus the main (horizontal) encode streams into for multistream outputs. Null = none.</summary>
    public string? MainBusPath { get; init; }
    /// <summary>Where the 9:16 canvas's segments go, when it is being recorded (null: not recorded).</summary>
    public string? VerticalSegmentPattern { get; init; }
    /// <summary>Vertical (9:16) canvas: composited when set; encoded to <see cref="VerticalBusPath"/> when that is set.</summary>
    public (int W, int H)? VerticalCanvas { get; init; }
    /// <summary>The program itself is the 9:16 picture: no widescreen canvas is composited at all.</summary>
    public bool MainIsVertical { get; init; }
    public EncodePlan? VerticalPlan { get; init; }
    public string? VerticalBusPath { get; init; }
    /// <summary>Outputs with their own quality: an extra encode of a canvas at their own bitrate.</summary>
    public IReadOnlyList<(string BusPath, bool Vertical, EncodePlan Plan)> ExtraEncodes { get; init; } = Array.Empty<(string, bool, EncodePlan)>();
    /// <summary>Known source picture sizes (input key → w×h), for exact vertical auto-layout positions.</summary>
    public IReadOnlyDictionary<string, (int W, int H)> KnownSizes { get; init; } = new Dictionary<string, (int, int)>();
    /// <summary>
    /// 9:16 clips: a canvas of its own, made live from the raw game capture and camera (no webcam baked into the game,
    /// no overlays), encoded into its own segment cache so a 9:16 clip is just cut, never re-rendered. All three set = on.
    /// </summary>
    public (int W, int H)? ClipCanvas { get; init; }
    public EncodePlan? ClipPlan { get; init; }
    public string? ClipSegmentPattern { get; init; }
    /// <summary>Text along the bottom of 9:16 clips (e.g. your handle), and the font file to draw it with.</summary>
    public string? ClipHandle { get; init; }
    public string? ClipFont { get; init; }
    /// <summary>Segment file pattern (e.g. C:/…/seg_%06d.ts). Null = no recording/replay cache.</summary>
    public string? SegmentPattern { get; init; }
    public int SegmentSeconds { get; init; } = 2;

    public bool Preview { get; init; } = true;
    public int PreviewWidth { get; init; } = 768;
    public int PreviewHeight { get; init; } = 432;
    public int PreviewFps { get; init; } = 30;
    /// <summary>Studio mode: the scene staged in the Preview (shown left of the program in the preview frame, and
    /// switched live with "streamselect@stage map k"). Null = no studio mode.</summary>
    public int? StagedSceneIndex { get; init; }

    /// <summary>Audio source id → raw f32le 48 kHz stereo input path (Windows named pipe).
    /// Keys are the representatives returned by <see cref="PipelineBuilder.AudioRepresentatives"/>.</summary>
    public IReadOnlyDictionary<Guid, string> AudioInputs { get; init; } = new Dictionary<Guid, string>();
    /// <summary>Text source id → pre-rendered transparent PNG.</summary>
    public IReadOnlyDictionary<Guid, string> RenderedImages { get; init; } = new Dictionary<Guid, string>();
    /// <summary>Timer sources: the ready-made filter chain that draws each card (the engine builds it).</summary>
    public IReadOnlyDictionary<Guid, string> TimerFilters { get; init; } = new Dictionary<Guid, string>();
    /// <summary>Monitor index → virtual-desktop rectangle (only needed for GDI display capture).</summary>
    public Func<int, (int X, int Y, int W, int H)?>? MonitorBounds { get; init; }
    /// <summary>False after desktop duplication failed on this PC: display capture falls back to GDI.</summary>
    public bool GpuDisplayCapture { get; init; } = true;
    /// <summary>
    /// Screen capture is scaled and converted on the GPU before it's copied to the CPU (a 1920×1200 desktop is ~9 MB
    /// a frame in BGRA; at the canvas size in NV12 it's a fraction): <see cref="DisplayFullSize"/> keeps the
    /// screen's own size (the 9:16 canvases want the game big) and only converts.
    /// </summary>
    public bool GpuDisplayScale { get; init; }
    public bool DisplayFullSize { get; init; }
    public int AudioBitrateKbps { get; init; } = 160;
    /// <summary>Master level of the mixed audio (dB; set live through "volume@master").</summary>
    public double MasterVolumeDb { get; init; }

    /// <summary>Common time origin (Unix seconds). Every live input is wall-clock stamped and shifted by this,
    /// so game, camera, mic and desktop audio line up no matter how long each device took to open.</summary>
    public double WallclockOrigin { get; init; }
    /// <summary>
    /// Stream safety delay (s): <see cref="DelayedAudio"/> inputs carry audio this much late (it waited in Leapcast
    /// so filtered words could be muted); every encode is shifted by the same amount so picture and sound stay
    /// together. The preview isn't delayed.
    /// </summary>
    public double SafetyDelay { get; init; }
    /// <summary>Auto light: where each camera's correction is now (a rebuild carries on from there).</summary>
    public IReadOnlyDictionary<string, LightSettings>? CameraLight { get; init; }
    /// <summary>Cameras whose denoise starts off (a bright room: switched on live when it gets dark).</summary>
    public IReadOnlySet<string>? CameraDenoiseOff { get; init; }
    /// <summary>How fast the preview runs at the start (changed live with <see cref="PipelineSpec.PreviewGates"/>).</summary>
    public PreviewRate PreviewRate { get; init; } = PreviewRate.Full;
    public IReadOnlySet<Guid> DelayedAudio { get; init; } = new HashSet<Guid>();

    /// <summary>Test/alternate platform hook: return an input for a source instead of the Windows device input.</summary>
    /// <remarks>Receives the wall-clock origin so sample/frame-counted inputs can be aligned to it.</remarks>
    public Func<StudioSource, double, InputSpec?>? InputOverride { get; init; }
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();
    /// <summary>Older ffmpeg needs per-input -thread_queue_size for live capture; newest builds removed the input option.</summary>
    public bool InputThreadQueueSupported { get; init; } = true;
    /// <summary>Camera input key → counted raw-video pipe from the <see cref="CameraBridge"/>.</summary>
    public IReadOnlyDictionary<string, CameraBridge.CameraFeed> CameraInputs { get; init; } = new Dictionary<string, CameraBridge.CameraFeed>();
    /// <summary>Window input key → counted raw-video pipe from the <see cref="WindowCaptureBridge"/> (already cropped/filled).</summary>
    public IReadOnlyDictionary<string, WindowCaptureBridge.WindowFeed> WindowInputs { get; init; } = new Dictionary<string, WindowCaptureBridge.WindowFeed>();
    /// <summary>Browser input key → counted BGRA pipe from the <see cref="BrowserBridge"/>.</summary>
    public IReadOnlyDictionary<string, BrowserBridge.BrowserFeed> BrowserInputs { get; init; } = new Dictionary<string, BrowserBridge.BrowserFeed>();
}

/// <summary>A per-device audio gain stage: its level per scene (0 where hidden), crossfaded on scene switch and
/// set live when a source is shown or hidden.</summary>
public sealed record AudioGainStage(string FilterName, IReadOnlyList<double> LinearGainPerScene)
{
    /// <summary>The audio device (input key) this stage controls.</summary>
    public string Key { get; init; } = "";
}

/// <summary>
/// A filter whose timeline "enable" shows or hides one source live (no rebuild): its overlay, or for a full-screen
/// bottom layer, a blank cover laid over it (<see cref="Cover"/>: enabled = hidden).
/// </summary>
public sealed record VisibilityStage(Guid SourceId, int Scene, string Filter, bool Cover, bool OnlyWhileSceneShown);

public sealed record PipelineSpec(
    IReadOnlyList<string> Args,
    IReadOnlyList<string> Warnings,
    bool Encoded,
    bool HasPreview,
    string FilterGraph,
    int SceneCount,
    IReadOnlyList<AudioGainStage> AudioGains)
{
    public bool HasVertical { get; init; }
    /// <summary>Auto-light filters in the graph: camera input key → eq instance name.</summary>
    public IReadOnlyList<(string Key, string Name)> CameraLightFilters { get; init; } = Array.Empty<(string, string)>();
    /// <summary>Camera denoisers that are switched on/off live (input key, filter name).</summary>
    public IReadOnlyList<(string Key, string Name)> CameraDenoiseFilters { get; init; } = Array.Empty<(string, string)>();
    /// <summary>The preview's gates (setpts filters): <see cref="PipelineBuilder.PreviewGateCommands"/> sets how fast it runs.</summary>
    public IReadOnlyList<string> PreviewGates { get; init; } = Array.Empty<string>();
    /// <summary>The 9:16 clip canvas is composited and encoded (its scenes switch with "eq@cfade").</summary>
    public bool HasClip { get; init; }
    /// <summary>ffmpeg input index → input key (to learn real picture sizes from ffmpeg's log).</summary>
    public IReadOnlyDictionary<int, string> InputKeys { get; init; } = new Dictionary<int, string>();
    /// <summary>Preview frame layout: horizontal part width, vertical part width (0 if none), height.</summary>
    public (int HW, int VW, int H) PreviewLayout { get; init; }
    /// <summary>Per scene: every overlay filter that only matters while that scene is visible (switched on/off).</summary>
    public IReadOnlyList<IReadOnlyList<string>> SceneOverlays { get; init; } = Array.Empty<IReadOnlyList<string>>();
    /// <summary>Studio mode: width of the staged scene's picture at the left of the preview frame (0 = none).</summary>
    public int PreviewStageWidth { get; init; }
    /// <summary>The mix has a master fader ("volume@master").</summary>
    public bool HasMasterVolume { get; init; }
    /// <summary>Every source's live show/hide switch.</summary>
    public IReadOnlyList<VisibilityStage> Visibility { get; init; } = Array.Empty<VisibilityStage>();
}

public static class PipelineBuilder
{
    private static string I(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Mask filter name prefix; scene k (k ≥ 1) is controlled by "eq@fade{k}".</summary>
    public const string FadeFilter = "eq@fade";

    /// <summary>Studio mode's scene picker for the Preview picture.</summary>
    public const string StageFilter = "streamselect@stage";

    /// <summary>Master audio fader on the mix.</summary>
    public const string MasterFilter = "volume@master";

    /// <summary>Two sources that capture the same thing share one ffmpeg input (a camera can only be opened once).</summary>
    public static string InputKey(StudioSource s)
    {
        var k = SourceTypes.KindOf(s.Type);
        return k switch
        {
            SourceKind.Display => $"display|{s.MonitorIndex}|{s.CaptureMethod}|{s.CaptureCursor}",
            SourceKind.Window => $"window|{s.WindowTitle.Trim().ToLowerInvariant()}{(s.WindowInstance > 0 ? "#" + s.WindowInstance : "")}|{s.CaptureCursor}",
            SourceKind.Camera => $"camera|{s.DeviceName}|{s.CameraResolution}|{s.CameraFps}|{s.CameraFormat}",
            SourceKind.Image => $"image|{s.FilePath.ToLowerInvariant()}",
            SourceKind.Text => $"text|{s.Id}",
            SourceKind.Timer => $"timer|{s.Id}",
            SourceKind.Browser => $"browser|{s.Url.Trim()}|{s.BrowserWidth}x{s.BrowserHeight}|{s.BrowserFps}|{s.BrowserCss}",
            SourceKind.Microphone or SourceKind.DesktopAudio => $"audio|{k}|{s.AudioDeviceId}",
            _ => $"other|{s.Id}"
        };
    }

    /// <summary>
    /// Visible sources minus anything completely hidden: a full-screen (Fill) capture covers every layer below it,
    /// so those are never captured at all (capturing a hidden 1080p60 desktop costs as much as the visible one).
    /// </summary>
    public static IEnumerable<StudioSource> VisibleLayers(IEnumerable<StudioSource> sources)
    {
        var list = sources.Where(s => s.Shown).ToList();
        int cover = list.FindLastIndex(s => SourceTypes.KindOf(s.Type) is SourceKind.Display or SourceKind.Window or SourceKind.Camera
                                            && Anchors.Resolve(s) == Anchors.Fill);
        for (int i = 0; i < list.Count; i++)
        {
            bool isVideo = SourceTypes.IsVideo(SourceTypes.KindOf(list[i].Type));
            if (isVideo && i < cover) continue;
            yield return list[i];
        }
    }

    /// <summary>
    /// What the pipeline composites: every video-capable source, shown <b>or hidden</b> (hidden ones are switched off
    /// in the graph, so showing or hiding one is instant and never interrupts the stream), minus sources under a
    /// visible full-screen capture (never seen, so never captured).
    /// </summary>
    /// <summary>
    /// Hidden sources stay in the graph (instant show/hide). Off in light mode: a hidden source isn't captured at all
    /// (a hidden 60 FPS desktop or window capture costs as much as a shown one), so showing it rebuilds.
    /// </summary>
    public static bool HiddenSourcesLive { get; set; } = true;

    public static IEnumerable<StudioSource> CompositedLayers(IEnumerable<StudioSource> sources)
    {
        var list = HiddenSourcesLive ? sources.ToList() : sources.Where(s => s.Shown || !SourceTypes.IsVideo(SourceTypes.KindOf(s.Type))).ToList();
        int cover = list.FindLastIndex(s => s.Shown && SourceTypes.KindOf(s.Type) is SourceKind.Display or SourceKind.Window or SourceKind.Camera
                                            && Anchors.Resolve(s) == Anchors.Fill);
        for (int i = 0; i < list.Count; i++)
        {
            bool isVideo = SourceTypes.IsVideo(SourceTypes.KindOf(list[i].Type));
            if (isVideo && i < cover) continue;
            yield return list[i];
        }
    }

    /// <summary>Linear gain of one audio device in each scene (0 where it's hidden or absent).</summary>
    public static List<double> SceneGains(IEnumerable<StudioScene> scenes, string key) =>
        scenes.Select(sc =>
        {
            var s = sc.Sources.FirstOrDefault(x => x.Shown && InputKey(x) == key);
            return s is null ? 0.0 : Math.Pow(10, s.VolumeDb / 20.0);
        }).ToList();

    /// <summary>Lanczos is the sharpest downscale (good for Wizard101's UI text) but costs ~3× bicubic per frame.</summary>
    /// <remarks>On the governor's light rungs the cheapest good-looking scaler is used (several times faster).</remarks>
    private static string ScaleFlags(int fps, bool light = false) => light ? "fast_bilinear" : fps > 30 ? "bicubic" : "lanczos";

    /// <summary>One representative source per distinct audio device across all scenes (first occurrence wins).</summary>
    public static List<StudioSource> AudioRepresentatives(IEnumerable<StudioScene> scenes)
    {
        var seen = new HashSet<string>();
        var list = new List<StudioSource>();
        foreach (var sc in scenes)
            foreach (var s in sc.Sources.Where(s => SourceTypes.IsAudio(SourceTypes.KindOf(s.Type)))) // hidden too: shown live
                if (seen.Add(InputKey(s))) list.Add(s);
        return list;
    }

    public static string OverlayName(Guid id, CanvasKind canvas) => (canvas == CanvasKind.Horizontal ? "overlay@h" : "overlay@v") + id.ToString("N");

    public static PipelineSpec Build(PipelineRequest r)
    {
        var plan = r.Plan;
        int W = plan.Width, H = plan.Height, F = plan.Fps;
        _bridged = r.WindowInputs;
        _lightNames = new(); _lightKeys = new(); _lightStart = r.CameraLight; _denoiseKeys = new(); _denoiseOff = r.CameraDenoiseOff;
        var scenes = r.Scenes is { Count: > 0 }
            ? r.Scenes
            : new[] { new StudioScene { Name = "Scene", Sources = new(r.Sources ?? Array.Empty<StudioSource>()) } };
        int N = scenes.Count;
        int active = Math.Clamp(r.ActiveSceneIndex, 0, N - 1);
        bool vertical = r.VerticalCanvas is not null;
        bool mainVertical = r.MainIsVertical;
        int VW = r.VerticalCanvas?.W ?? 0, VH = r.VerticalCanvas?.H ?? 0;
        // info level: ffmpeg reports each input's real picture size (used for exact layout); stats_period also sets
        // how often ffmpeg polls stdin for runtime commands (ffmpeg 7+: once per period).
        // "level+" tags every log line with its severity, so real errors can be told apart from information.
        var args = new List<string> { "-hide_banner", "-loglevel", "level+info", "-nostats",
            "-progress", "pipe:2", "-stats_period", "0.1", "-copyts", "-y" };
        var warnings = new List<string>();
        var graph = new List<string>();
        var inputKeys = new Dictionary<int, string>();
        int inputCount = 0;
        bool clip = r.ClipCanvas is not null && r.ClipPlan is not null && r.ClipSegmentPattern is not null;
        int CW = r.ClipCanvas?.W ?? 0, CH = r.ClipCanvas?.H ?? 0;
        bool encoded = r.SegmentPattern is not null || r.MainBusPath is not null || r.VerticalBusPath is not null
                       || r.VerticalSegmentPattern is not null || r.ExtraEncodes.Count > 0 || clip;
        string shift = "setpts=PTS-" + I(r.WallclockOrigin) + "/TB,";

        // ---------- which sources each canvas shows ----------
        var hLayers = scenes.Select(sc => CompositedLayers(sc.Sources)
            .Where(s => SourceTypes.IsVideo(SourceTypes.KindOf(s.Type))).ToList()).ToList();
        var vLayers = scenes.Select(sc => vertical ? VerticalLayers(sc.Sources, includeHidden: true) : new List<StudioSource>()).ToList();
        var visibility = new List<VisibilityStage>();

        // ---------- video inputs (deduplicated across scenes and canvases) ----------
        var inputs = new Dictionary<string, (int index, bool wall, List<string> uses)>();
        var warned = new HashSet<Guid>();
        var failedKeys = new HashSet<string>();
        foreach (var s in scenes.SelectMany(sc => sc.Sources))
        {
            var kind = SourceTypes.KindOf(s.Type);
            if (kind == SourceKind.Folder) continue;
            if (kind == SourceKind.Unsupported)
            {
                if (warned.Add(s.Id)) warnings.Add($"\"{s.Name}\": source type \"{s.Type}\" isn't supported, skipped.");
                continue;
            }
        }
        bool anyLive = false;
        bool Ensure(StudioSource s)
        {
            var kind = SourceTypes.KindOf(s.Type);
            string key = InputKey(s);
            if (failedKeys.Contains(key)) return false;
            if (inputs.ContainsKey(key)) return true;
            var spec = r.InputOverride?.Invoke(s, r.WallclockOrigin) ?? CameraFeedInput(s, kind, key, r)
                       ?? WindowFeedInput(kind, key, r) ?? BrowserFeedInput(kind, key, r)
                       ?? VideoInput(s, kind, r, warned.Add(s.Id) ? warnings : new List<string>());
            if (spec is null) { failedKeys.Add(key); return false; }
            // live = delivers frames in real time by itself (capture devices, counted pipes from the bridges)
            if (spec.Wallclock || spec.Args.Contains("-re") || r.CameraInputs.ContainsKey(key) || r.WindowInputs.ContainsKey(key)
                || r.BrowserInputs.ContainsKey(key)) anyLive = true;
            args.AddRange(WithWallclock(spec));
            inputKeys[inputCount] = key;
            inputs[key] = (inputCount++, spec.Wallclock, new List<string>());
            return true;
        }
        for (int k = 0; k < N; k++)
        {
            hLayers[k] = hLayers[k].Where(Ensure).ToList();
            vLayers[k] = vLayers[k].Where(Ensure).ToList();
        }

        string Use(string key)
        {
            var e = inputs[key];
            string label = $"i{e.index}u{e.uses.Count}";
            e.uses.Add(label);
            return label;
        }

        // Prepared layers: the same source with the same crop/scale in several scenes (a "BRB" copy of the game
        // scene, both canvases) is processed once and split, instead of once per scene.
        var prepared = new Dictionary<string, (int id, string input, string chain, List<string> outs)>();
        string Prep(string key, string chain)
        {
            string pk = key + "\u001f" + chain;
            if (!prepared.TryGetValue(pk, out var e))
            {
                e = (prepared.Count, Use(key), chain, new List<string>());
                prepared[pk] = e;
            }
            string label = $"p{e.id}o{e.outs.Count}";
            e.outs.Add(label);
            return label;
        }

        // Shared blank canvases for scenes whose bottom layer isn't a full-screen capture.
        var colorUses = new Dictionary<string, (int index, List<string> uses)>();
        string ColorUse(int cw, int ch)
        {
            string ck = $"{cw}x{ch}";
            if (!colorUses.TryGetValue(ck, out var c))
            {
                // With live sources in the graph the blank canvas is generated inside it, paced by those sources:
                // a separate real-time (-re) input can only catch up at 1.05× after any hiccup, which kept the
                // whole stream seconds behind and stuttering. Only a graph with nothing live needs -re pacing.
                if (anyLive) c = (-1, new List<string>());
                else
                {
                    args.AddRange(new[] { "-re", "-f", "lavfi", "-i", $"color=c=0x0E1016:s={cw}x{ch}:r={F}" });
                    c = (inputCount++, new List<string>());
                }
                colorUses[ck] = c;
            }
            string label = $"bg{ck.Replace('x', '_')}u{c.uses.Count}";
            c.uses.Add(label);
            return label;
        }

        // Only the scene on air (and, during a fade, the one coming in) is blended: the overlays of hidden scenes
        // are switched off with ffmpeg's timeline "enable" and switched on by the transition commands.
        var sceneOverlays = Enumerable.Range(0, N).Select(_ => new List<string>()).ToList();
        int? staged = r.Preview && r.StagedSceneIndex is int si ? Math.Clamp(si, 0, N - 1) : null;
        // a scene's own big overlays draw while it's on air or staged in the studio-mode preview
        string Enabled(int k) => k == active || k == staged ? "" : ":enable=0";
        bool Large(LayoutRect rect, int cw, int ch) => rect.W * rect.H >= 0.25 * cw * ch;

        var sceneGraph = new List<string>();
        // ---------- horizontal canvas, per scene ----------
        for (int k = 0; k < N && !mainVertical; k++)
        {
            var layers = hLayers[k];
            string p = $"s{k}";
            int start = 0;
            if (layers.Count > 0 && LayoutMath.IsBaseLayer(layers[0], layers))
            {
                var b = layers[0];
                string chain = $"{Crop(b)}{CameraPrep(b, SourceTypes.KindOf(b.Type), null)}{FillChain(b, W, H, F, pad: true, light: plan.Light)}format=yuv420p";
                // hiding the full-screen bottom layer lays a blank cover over it (switched live, no rebuild)
                string coverName = "overlay@x" + b.Id.ToString("N");
                sceneGraph.Add($"[{Prep(InputKey(b), chain)}][{ColorUse(W, H)}]{coverName}=eof_action=repeat:format=yuv420:enable={(b.Shown ? 0 : 1)}[{p}base]");
                visibility.Add(new VisibilityStage(b.Id, k, coverName, Cover: true, OnlyWhileSceneShown: false));
                start = 1;
            }
            else
            {
                sceneGraph.Add($"[{ColorUse(W, H)}]format=yuv420p[{p}base]");
                if (layers.Count == 0 && N == 1) warnings.Add("No visible video source in this scene: streaming a blank canvas.");
            }
            string cur = $"{p}base";
            for (int j = start; j < layers.Count; j++)
            {
                var src = layers[j];
                var kind = SourceTypes.KindOf(src.Type);
                bool alpha = SourceTypes.IsGraphic(kind);
                string scale = HorizontalScale(src, W, H, F, plan.Light);
                var (px, py) = HorizontalPosition(src, W, H);
                string pos = $"x={px}:y={py}";
                string name = OverlayName(src.Id, CanvasKind.Horizontal);
                bool big = Large(LayoutMath.Horizontal(src, W, H, AspectFor(src, r.KnownSizes)), W, H);
                if (big) sceneOverlays[k].Add(name);
                visibility.Add(new VisibilityStage(src.Id, k, name, Cover: false, OnlyWhileSceneShown: big));
                string layer = Prep(InputKey(src), $"{Crop(src)}{TimerPrep(src, kind, r)}{CameraPrep(src, kind, scale)}format={AlphaFormat(alpha, kind)}");
                string on = !src.Shown ? ":enable=0" : big ? Enabled(k) : "";
                sceneGraph.Add($"[{cur}][{layer}]{name}={pos}:eof_action=repeat:format=yuv420{on}[{p}c{j}]");
                cur = $"{p}c{j}";
            }
            sceneGraph.Add($"[{cur}]fps={F},format=yuv420p{(staged is not null ? $",split=2[scene{k}][stg{k}]" : $"[scene{k}]")}");
        }

        // ---------- the 9:16 picture, per scene (game over a blurred copy of itself, camera on top) ----------
        // Built either as a second canvas beside the widescreen one, or — when 9:16 is the whole show — as the
        // program itself, in which case it writes the scene labels the rest of the graph reads.
        void VerticalScenes(int cw, int ch, string outPrefix, bool staging)
        {
            int bw = SmartTuner.Even(Math.Max(16, cw / 4)), bh = SmartTuner.Even(Math.Max(16, ch / 4));
            for (int k = 0; k < N; k++)
            {
                var layers = vLayers[k];
                string p = $"v{k}";
                bool hasCam = layers.Any(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera && x.VAuto && x.Shown);
                var bgGame = layers.FirstOrDefault(x => LayoutMath.IsGame(SourceTypes.KindOf(x.Type)));
                if (bgGame is not null)
                {
                    // blur at quarter resolution: ~1/16 of the work of a full-size blur, looks the same
                    string coverName = "overlay@y" + bgGame.Id.ToString("N");
                    sceneGraph.Add($"[{Prep(InputKey(bgGame), $"{Crop(bgGame)}scale={bw}:{bh}:force_original_aspect_ratio=increase:flags=fast_bilinear," +
                                   $"crop={bw}:{bh},boxblur=6:2,scale={cw}:{ch}:flags=bilinear,setsar=1,format=yuv420p,eq=brightness=-0.08")}]" +
                                   $"[{ColorUse(cw, ch)}]{coverName}=eof_action=repeat:format=yuv420:enable={(bgGame.Shown ? 0 : 1)}[{p}base]");
                    visibility.Add(new VisibilityStage(bgGame.Id, k, coverName, Cover: true, OnlyWhileSceneShown: false));
                }
                else
                {
                    sceneGraph.Add($"[{ColorUse(cw, ch)}]format=yuv420p[{p}base]");
                    if (layers.Count == 0 && N == 1)
                        warnings.Add("No visible video source on the 9:16 canvas in this scene: streaming a blank canvas.");
                }
                string cur = $"{p}base";
                for (int j = 0; j < layers.Count; j++)
                {
                    var src = layers[j];
                    var kind = SourceTypes.KindOf(src.Type);
                    bool alpha = SourceTypes.IsGraphic(kind);
                    double aspect = AspectFor(src, r.KnownSizes);
                    var rect = LayoutMath.Vertical(src, cw, ch, aspect, hasCam);
                    string pos = $"x={LayoutMath.Px(rect.X)}:y={LayoutMath.Px(rect.Y)}";
                    string name = OverlayName(src.Id, CanvasKind.Vertical);
                    bool big = Large(rect, cw, ch);
                    if (big) sceneOverlays[k].Add(name);
                    visibility.Add(new VisibilityStage(src.Id, k, name, Cover: false, OnlyWhileSceneShown: big));
                    string layer = Prep(InputKey(src), $"{Crop(src)}{TimerPrep(src, kind, r)}{CameraPrep(src, kind, VerticalScale(src, cw, ch))}format={AlphaFormat(alpha, kind)}");
                    string on = !src.Shown ? ":enable=0" : big ? Enabled(k) : "";
                    sceneGraph.Add($"[{cur}][{layer}]{name}={pos}:eof_action=repeat:format=yuv420{on}[{p}c{j}]");
                    cur = $"{p}c{j}";
                }
                sceneGraph.Add($"[{cur}]fps={F},format=yuv420p" +
                    (staging && staged is not null ? $",split=2[{outPrefix}{k}][stg{k}]" : $"[{outPrefix}{k}]"));
            }
        }
        if (mainVertical) VerticalScenes(W, H, "scene", staging: true);
        else if (vertical) VerticalScenes(VW, VH, "vscene", staging: false);

        // ---------- 9:16 clip canvas, per scene: the raw game big, the raw camera small on top, blurred game behind ----------
        if (clip)
        {
            int cf = r.ClipPlan!.Fps;
            int bw = SmartTuner.Even(Math.Max(16, CW / 4)), bh = SmartTuner.Even(Math.Max(16, CH / 4));
            for (int k = 0; k < N; k++)
            {
                var layers = hLayers[k];
                string p = $"c{k}";
                var game = layers.FirstOrDefault(x => LayoutMath.IsGame(SourceTypes.KindOf(x.Type)))
                           ?? layers.FirstOrDefault(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera && LayoutMath.IsBaseLayer(x, layers));
                var cam = layers.LastOrDefault(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera && x != game);
                double gameAspect = game is null ? 16 / 9.0 : AspectFor(game, r.KnownSizes);
                double camAspect = cam is null ? 4 / 3.0 : AspectFor(cam, r.KnownSizes);
                var plan9 = VerticalClip.LivePlan(CW, CH, gameAspect, cam is null ? null : camAspect);
                if (game is not null)
                {
                    string coverName = "overlay@z" + game.Id.ToString("N");
                    sceneGraph.Add($"[{Prep(InputKey(game), $"{Crop(game)}scale={bw}:{bh}:force_original_aspect_ratio=increase:flags=fast_bilinear," +
                                   $"crop={bw}:{bh},boxblur=6:2,scale={CW}:{CH}:flags=bilinear,setsar=1,format=yuv420p,eq=brightness=-0.12")}]" +
                                   $"[{ColorUse(CW, CH)}]{coverName}=eof_action=repeat:format=yuv420:enable={(game.Shown ? 0 : 1)}[{p}base]");
                    visibility.Add(new VisibilityStage(game.Id, k, coverName, Cover: true, OnlyWhileSceneShown: false));
                    // the game panel: the middle of the raw capture, as wide as the panel's shape allows
                    var gb = plan9.GameBox;
                    string ga = I(gb.W / (double)gb.H);
                    string panel = Prep(InputKey(game), $"{Crop(game)}crop='min(iw,ih*{ga})':'min(ih,iw/{ga})',scale={gb.W}:{gb.H}:flags=bicubic,setsar=1,format=yuv420p");
                    string name = "overlay@c" + game.Id.ToString("N");
                    sceneOverlays[k].Add(name);
                    visibility.Add(new VisibilityStage(game.Id, k, name, Cover: false, OnlyWhileSceneShown: true));
                    sceneGraph.Add($"[{p}base][{panel}]{name}=x={gb.X}:y={gb.Y}:eof_action=repeat:format=yuv420{(!game.Shown ? ":enable=0" : Enabled(k))}[{p}g]");
                }
                else sceneGraph.Add($"[{ColorUse(CW, CH)}]format=yuv420p[{p}g]");
                string cur = $"{p}g";
                if (cam is not null && plan9.CamBox is { } cb)
                {
                    // rounded corners: a mask drawn once and repeated
                    int rad = Math.Max(4, Math.Min(cb.W, cb.H) / 9);
                    string ca = I(cb.W / (double)cb.H);
                    string layer = Prep(InputKey(cam), $"{Crop(cam)}crop='min(iw,ih*{ca})':'min(ih,iw/{ca})'," +
                                                       $"{CameraPrep(cam, SourceKind.Camera, $"scale={cb.W}:{cb.H}:flags=bicubic,")}format=yuva420p");
                    sceneGraph.Add($"color=c=black:s={cb.W}x{cb.H}:r=1:d=1,format=gray," +
                                   $"geq=lum='if(lte(hypot(max(0,abs(X-W/2)-(W/2-{rad})),max(0,abs(Y-H/2)-(H/2-{rad}))),{rad}),255,0)'," +
                                   $"loop=loop=-1:size=1,fps={cf}[{p}mask]");
                    sceneGraph.Add($"[{layer}][{p}mask]alphamerge[{p}cam]");
                    string name = "overlay@d" + cam.Id.ToString("N");
                    visibility.Add(new VisibilityStage(cam.Id, k, name, Cover: false, OnlyWhileSceneShown: false));
                    sceneGraph.Add($"[{cur}][{p}cam]{name}=x={cb.X}:y={cb.Y}:eof_action=repeat:format=yuv420{(cam.Shown ? "" : ":enable=0")}[{p}k]");
                    cur = $"{p}k";
                }
                sceneGraph.Add($"[{cur}]fps={cf},format=yuv420p[cscene{k}]");
            }
        }

        // input splits (timestamp shift happens once, before the split), then the shared prepared layers
        foreach (var (_, e) in inputs)
        {
            if (e.uses.Count == 0) continue;
            graph.Add(e.uses.Count == 1 && !e.wall
                ? $"[{e.index}:v]null[{e.uses[0]}]"
                : $"[{e.index}:v]{(e.wall ? shift : "")}split={e.uses.Count}{string.Concat(e.uses.Select(u => $"[{u}]"))}");
        }
        foreach (var (ck, c) in colorUses)
        {
            string src = c.index < 0 ? $"color=c=0x0E1016:s={ck}:r={F},format=yuv420p," : $"[{c.index}:v]";
            graph.Add(c.uses.Count == 1 && c.index < 0
                ? $"{src.TrimEnd(',')}[{c.uses[0]}]"
                : $"{src}split={c.uses.Count}{string.Concat(c.uses.Select(u => $"[{u}]"))}");
        }
        foreach (var (_, e) in prepared)
            graph.Add(e.outs.Count == 1
                ? $"[{e.input}]{e.chain}[{e.outs[0]}]"
                : $"[{e.input}]{e.chain},split={e.outs.Count}{string.Concat(e.outs.Select(o => $"[{o}]"))}");
        graph.AddRange(sceneGraph);

        // ---------- scene switcher (per canvas) ----------
        // Scenes are stacked bottom→top; every scene above the first has its own alpha mask (an eq brightness
        // expression of stream time t). Switching only rewrites mask expressions: no filter reconfiguration,
        // no restart, frame-accurate fades. Invariant: every mask above the on-air scene is transparent.
        // Every scene is opaque, so only the on-air scene's layer of the stack needs blending (the others are off).
        // Every scene in the graph is composited on every frame, on air or not, so the switching machinery must
        // not grow with the number of scenes: a stack of per-scene masks and overlays cost a measured ~8% of a
        // core each at 1080p30. Instead two selectors pick the outgoing and incoming pictures and a single mask
        // crossfades between them, which is flat in scene count. Steady state: both selectors on the scene that
        // is up, the mask opaque, so the output is that scene.
        void Stack(string scenePrefix, string fadePrefix, string selectPrefix, int cw, int ch, string outLabel, int fps = 0)
        {
            int sf = fps > 0 ? fps : F;
            if (N == 1) { graph.Add($"[{scenePrefix}0]format=nv12[{outLabel}]"); return; }
            var under = new System.Text.StringBuilder();
            var over = new System.Text.StringBuilder();
            for (int k = 0; k < N; k++)
            {
                graph.Add($"[{scenePrefix}{k}]split=2[{scenePrefix}u{k}][{scenePrefix}o{k}]");
                under.Append($"[{scenePrefix}u{k}]");
                over.Append($"[{scenePrefix}o{k}]");
            }
            graph.Add($"{under}streamselect@{selectPrefix}under=inputs={N}:map={active}[{scenePrefix}under]");
            graph.Add($"{over}streamselect@{selectPrefix}over=inputs={N}:map={active}[{scenePrefix}over]");
            graph.Add($"color=c=black:s={cw}x{ch}:r={sf},format=gray,{fadePrefix}=eval=frame:brightness=1[{scenePrefix}mask]");
            graph.Add($"[{scenePrefix}over][{scenePrefix}mask]alphamerge[{scenePrefix}oa]");
            graph.Add($"[{scenePrefix}under][{scenePrefix}oa]overlay@{selectPrefix}fade=format=yuv420:eof_action=repeat[{scenePrefix}stacked]");
            graph.Add($"[{scenePrefix}stacked]format=nv12[{outLabel}]");
        }
        Stack("scene", FadeFilter, SceneSelect, W, H, "vfinal");
        if (vertical) Stack("vscene", VerticalFadeFilter, VerticalSceneSelect, VW, VH, "vvfinal");
        if (clip)
        {
            Stack("cscene", ClipFadeFilter, ClipSceneSelect, CW, CH, "cvfinal", r.ClipPlan!.Fps);
            if (!string.IsNullOrWhiteSpace(r.ClipHandle) && !string.IsNullOrWhiteSpace(r.ClipFont))
                graph.Add($"[cvfinal]drawtext=fontfile='{VerticalClip.EscapeFilter(r.ClipFont)}':text='{VerticalClip.EscapeFilter(r.ClipHandle.Trim())}':" +
                          $"fontcolor=white:fontsize={SmartTuner.Even((int)(CW * 0.045))}:x=(w-text_w)/2:y=h-{SmartTuner.Even((int)(CH * 0.06))}-text_h:" +
                          "shadowcolor=black@0.6:shadowx=2:shadowy=2,format=yuv420p[cenc]");
            else graph.Add("[cvfinal]format=yuv420p[cenc]");
            if (r.SafetyDelay > 0)
            {
                // the 9:16 clip encode runs behind like the others (its sound is the delayed mix)
                int ci = graph.FindLastIndex(x => x.EndsWith("[cenc]"));
                graph[ci] = graph[ci][..^"[cenc]".Length] + "[cencpre]";
                graph.Add($"[cencpre]setpts=PTS+{I(r.SafetyDelay)}/TB[cenc]");
            }
        }

        // fan the composited canvases out to encodes + preview
        var hOuts = new List<string>(); var vOuts = new List<string>();
        bool mainEncode = r.SegmentPattern is not null || r.MainBusPath is not null;
        if (mainEncode) hOuts.Add("venc");
        bool vertEncode = vertical && r.VerticalPlan is not null && (r.VerticalBusPath is not null || r.VerticalSegmentPattern is not null);
        if (vertEncode) vOuts.Add("vvenc");
        for (int e = 0; e < r.ExtraEncodes.Count; e++)
            if (r.ExtraEncodes[e].Vertical && vertical) vOuts.Add($"vxenc{e}"); else if (!r.ExtraEncodes[e].Vertical) hOuts.Add($"xenc{e}");
        if (r.Preview) { hOuts.Add("hprevsrc"); if (vertical) vOuts.Add("vprevsrc"); }
        void Fan(string src, List<string> outs)
        {
            if (outs.Count == 0) { graph.Add($"[{src}]nullsink"); return; }
            // stream safety: every encode is shifted by the delay to meet its (delayed) sound; the preview isn't
            bool Delayed(string o) => r.SafetyDelay > 0 && !o.EndsWith("prevsrc");
            string L(string o) => Delayed(o) ? o + "pre" : o;
            graph.Add(outs.Count == 1 ? $"[{src}]null[{L(outs[0])}]" : $"[{src}]split={outs.Count}{string.Concat(outs.Select(o => $"[{L(o)}]"))}");
            foreach (var o in outs.Where(Delayed)) graph.Add($"[{o}pre]setpts=PTS+{I(r.SafetyDelay)}/TB[{o}]");
        }
        Fan("vfinal", hOuts);
        if (vertical) Fan("vvfinal", vOuts);

        int PH = r.PreviewHeight;
        // a 9:16 program must not be squashed into a widescreen preview pane
        int PW = mainVertical ? SmartTuner.Even(Math.Max(16, (int)Math.Round(PH * W / (double)H))) : r.PreviewWidth;
        int VPW = vertical ? SmartTuner.Even((int)Math.Round(PH * VW / (double)VH)) : 0;
        int SPW = staged is not null ? PW : 0;
        var gates = new List<string>();
        // every preview picture is scaled, converted and copied to the app: a gate right after the frame-rate cut
        // drops them (live, no restart) while nobody is looking, or keeps 1 in 3 while you're in another app
        string Gate() { string g = $"setpts@pv{gates.Count}"; gates.Add(g); return $"{g}={GateExpr(r.PreviewRate).Replace(",", "\\,")},select=not(isnan(t)),"; }
        if (r.Preview)
        {
            int pf = Math.Min(r.PreviewFps, F);
            var parts = new List<string>();
            if (staged is int st)
            {
                // studio mode: the staged scene, picked live from every scene's composite (streamselect "map")
                graph.Add(N == 1 ? "[stg0]null[stagesrc]"
                    : $"{string.Concat(Enumerable.Range(0, N).Select(k => $"[stg{k}]"))}{StageFilter}=inputs={N}:map={st}[stagesrc]");
                graph.Add($"[stagesrc]fps={pf},{Gate()}scale={PW}:{PH}:flags=bilinear,format=bgra[sprev]");
                parts.Add("[sprev]");
            }
            graph.Add($"[hprevsrc]fps={pf},{Gate()}scale={PW}:{PH}:flags=bilinear,format=bgra[hprev]");
            parts.Add("[hprev]");
            if (vertical)
            {
                graph.Add($"[vprevsrc]fps={pf},{Gate()}scale={VPW}:{PH}:flags=bilinear,format=bgra[vprev]");
                parts.Add("[vprev]");
            }
            graph.Add(parts.Count == 1 ? "[hprev]null[prev]" : $"{string.Concat(parts)}hstack=inputs={parts.Count}[prev]");
        }

        // ---------- audio: one input per device; per-scene level via a runtime-adjustable volume stage ----------
        var gains = new List<AudioGainStage>();
        bool hasMaster = false;
        int audioOuts = (mainEncode ? 1 : 0) + (vertEncode ? 1 : 0) + r.ExtraEncodes.Count(x => !x.Vertical || vertical) + (clip ? 1 : 0);
        if (encoded && audioOuts > 0)
        {
            var mixInputs = new List<string>();
            foreach (var rep in AudioRepresentatives(scenes))
            {
                var kind = SourceTypes.KindOf(rep.Type);
                string key = InputKey(rep);
                InputSpec? spec = r.InputOverride?.Invoke(rep, r.WallclockOrigin);
                // Audio pipes are sample-counted from the shared origin (the writer pads silence up to "now" on
                // connect), so their timestamps stay exact even when ffmpeg pauses reading an input that runs ahead.
                if (spec is null && r.AudioInputs.TryGetValue(rep.Id, out var path))
                    spec = new InputSpec(new[] { "-f", "f32le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "1024", "-i", path }, false);
                if (spec is null) { warnings.Add($"\"{rep.Name}\": audio device unavailable, skipped."); continue; }
                args.AddRange(WithWallclock(spec));
                int idx = inputCount++;

                var perScene = SceneGains(scenes, key);
                bool cleanup = scenes.SelectMany(sc => sc.Sources).Any(x => InputKey(x) == key && x.NoiseCleanup)
                               && kind == SourceKind.Microphone;
                string gainName = $"volume@g{idx}";

                var chain = new StringBuilder($"[{idx}:a]");
                if (spec.Wallclock) chain.Append("asetpts=PTS-" + I(r.WallclockOrigin) + "/TB,");
                double sd = r.SafetyDelay;
                // stream safety: this pipe's sound arrives late (it waited in Leapcast); back to when it was heard,
                // so scene fades and volume changes land on the right moment
                if (sd > 0 && r.DelayedAudio.Contains(rep.Id)) chain.Append($"asetpts=PTS-{I(sd)}/TB,atrim=start=0,");
                chain.Append("aresample=48000:async=1000,"); // absorbs device clock drift + pipe jitter
                if (cleanup) chain.Append("highpass=f=80,afftdn=nr=12:nf=-40:tn=1,"); // adaptive background-noise removal
                // always a named gain stage: scene switches crossfade it, show/hide sets it live
                chain.Append($"{gainName}=volume={I(perScene[active])}:eval=frame,");
                gains.Add(new AudioGainStage(gainName, perScene) { Key = key });
                if (sd > 0) chain.Append($"asetpts=PTS+{I(sd)}/TB,"); // …then everything moves back by the delay, with the picture
                chain.Append($"aformat=sample_fmts=fltp:channel_layouts=stereo[a{idx}]");
                graph.Add(chain.ToString());
                mixInputs.Add($"[a{idx}]");
            }
            string mixed;
            // master fader on the mix (before the limiter, so a boost can't clip), set live like the per-device gains
            string master = $"{MasterFilter}=volume={I(Math.Pow(10, Math.Clamp(r.MasterVolumeDb, -60, 20) / 20.0))}:eval=frame,";
            hasMaster = mixInputs.Count > 0;
            if (mixInputs.Count == 0)
            {
                args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo" });
                mixed = $"[{inputCount++}:a]{(r.SafetyDelay > 0 ? $"asetpts=PTS+{I(r.SafetyDelay)}/TB," : "")}aformat=sample_fmts=fltp:channel_layouts=stereo";
            }
            else if (mixInputs.Count == 1)
                mixed = $"{mixInputs[0]}{master}alimiter=limit=0.95:level=0";
            else
                mixed = $"{string.Concat(mixInputs)}amix=inputs={mixInputs.Count}:duration=longest:dropout_transition=0:normalize=0," +
                        $"{master}alimiter=limit=0.95:level=0";
            graph.Add(audioOuts == 1 ? $"{mixed}[aout0]"
                : $"{mixed},asplit={audioOuts}{string.Concat(Enumerable.Range(0, audioOuts).Select(i => $"[aout{i}]"))}");
        }

        if (!r.InputThreadQueueSupported)
        {
            for (int i = args.Count - 2; i >= 0; i--)
                if (args[i] == "-thread_queue_size") args.RemoveRange(i, 2);
        }
        string filterGraph = string.Join(";", graph);
        args.AddRange(new[] { "-filter_complex", filterGraph });

        // ---------- outputs ----------
        // Every encode repeats its stream headers on each keyframe (dump_extra) so a destination can join mid-stream.
        int aout = 0;
        void AudioArgs() => args.AddRange(new[] { "-c:a", "aac", "-b:a", I(r.AudioBitrateKbps) + "k", "-ar", "48000", "-ac", "2" });
        if (mainEncode)
        {
            args.AddRange(new[] { "-map", "[venc]", "-map", $"[aout{aout++}]" });
            args.AddRange(EncoderCatalog.BuildVideoArgs(plan));
            AudioArgs();
            args.AddRange(new[] { "-flags", "+global_header", "-bsf:v", "dump_extra=freq=keyframe", "-f", "tee" });
            var slaves = new List<string>();
            if (r.SegmentPattern is not null)
                slaves.Add($"[f=segment:segment_time={r.SegmentSeconds}:segment_format=mpegts:onfail=ignore]" +
                           TeeEscape(r.SegmentPattern.Replace('\\', '/')));
            if (r.MainBusPath is not null)
                slaves.Add("[f=mpegts:onfail=ignore]" + TeeEscape(r.MainBusPath.Replace('\\', '/')));
            args.Add(string.Join("|", slaves));
        }
        if (vertEncode)
        {
            args.AddRange(new[] { "-map", "[vvenc]", "-map", $"[aout{aout++}]" });
            args.AddRange(EncoderCatalog.BuildVideoArgs(r.VerticalPlan!));
            AudioArgs();
            var vSlaves = new List<string>();
            if (r.VerticalSegmentPattern is not null)
                vSlaves.Add($"[f=segment:segment_time={r.SegmentSeconds}:segment_format=mpegts:onfail=ignore]" +
                            TeeEscape(r.VerticalSegmentPattern.Replace('\\', '/')));
            if (r.VerticalBusPath is not null)
                vSlaves.Add("[f=mpegts:onfail=ignore]" + TeeEscape(r.VerticalBusPath.Replace('\\', '/')));
            args.AddRange(new[] { "-flags", "+global_header", "-bsf:v", "dump_extra=freq=keyframe", "-f", "tee", string.Join("|", vSlaves) });
        }
        for (int e = 0; e < r.ExtraEncodes.Count; e++)
        {
            var x = r.ExtraEncodes[e];
            if (x.Vertical && !vertical) continue;
            args.AddRange(new[] { "-map", x.Vertical ? $"[vxenc{e}]" : $"[xenc{e}]", "-map", $"[aout{aout++}]" });
            args.AddRange(EncoderCatalog.BuildVideoArgs(x.Plan));
            AudioArgs();
            args.AddRange(new[] { "-flags", "+global_header", "-bsf:v", "dump_extra=freq=keyframe", "-f", "mpegts", x.BusPath });
        }
        if (clip)
        {
            args.AddRange(new[] { "-map", "[cenc]", "-map", $"[aout{aout++}]" });
            args.AddRange(EncoderCatalog.BuildVideoArgs(r.ClipPlan!));
            AudioArgs();
            args.AddRange(new[] { "-flags", "+global_header", "-bsf:v", "dump_extra=freq=keyframe", "-f", "tee",
                $"[f=segment:segment_time={r.SegmentSeconds}:segment_format=mpegts:onfail=ignore]" + TeeEscape(r.ClipSegmentPattern!.Replace('\\', '/')) });
        }
        if (r.Preview)
            // passthrough: a paused preview leaves gaps; ffmpeg mustn't fill them with copies
            args.AddRange(new[] { "-map", "[prev]", "-fps_mode:v", "passthrough", "-c:v", "rawvideo", "-f", "rawvideo", "pipe:1" });

        var lightFilters = _lightKeys?.ToList() ?? new();
        return new PipelineSpec(args, warnings, encoded, r.Preview, filterGraph, N, gains)
        {
            CameraLightFilters = lightFilters,
            CameraDenoiseFilters = _denoiseKeys?.ToList() ?? new(),
            PreviewGates = gates,
            HasVertical = vertical, HasClip = clip, InputKeys = inputKeys, PreviewLayout = (PW, VPW, PH), PreviewStageWidth = SPW,
            SceneOverlays = sceneOverlays, Visibility = visibility, HasMasterVolume = hasMaster,
        };
    }

    /// <summary>Sources on the vertical canvas, bottom → top. In auto layout only the top-most game capture is used.</summary>
    public static List<StudioSource> VerticalLayers(IEnumerable<StudioSource> sources, bool includeHidden = false)
    {
        var shown = sources.Where(includeHidden ? LayoutMath.OnVerticalCanvas : LayoutMath.ShownVertical).ToList();
        var autoGames = shown.Where(s => s.VAuto && LayoutMath.IsGame(SourceTypes.KindOf(s.Type))).ToList();
        if (autoGames.Count > 1) shown = shown.Where(s => !autoGames.Take(autoGames.Count - 1).Contains(s)).ToList();
        return shown;
    }

    public static double AspectFor(StudioSource s, IReadOnlyDictionary<string, (int W, int H)> known)
    {
        if (known.TryGetValue(InputKey(s), out var sz) && sz.W > 0 && sz.H > 0)
        {
            double w = sz.W - Math.Max(0, s.CropLeft) - Math.Max(0, s.CropRight);
            double h = sz.H - Math.Max(0, s.CropTop) - Math.Max(0, s.CropBottom);
            if (w > 0 && h > 0) return w / h;
        }
        return LayoutMath.EstimateAspect(s);
    }

    public const string VerticalFadeFilter = "eq@vfade";
    public const string ClipFadeFilter = "eq@cfade";
    /// <summary>Which selector pair belongs to which canvas: "streamselect@&lt;this&gt;under" and "…over".</summary>
    public const string SceneSelect = "s", VerticalSceneSelect = "v", ClipSceneSelect = "c";

    /// <summary>
    /// Runtime commands (ffmpeg stdin "C" lines) that crossfade from scene <paramref name="from"/> to <paramref name="to"/>.
    /// The fade starts at stream time <paramref name="startAt"/> (seconds since the wall-clock origin), so every line
    /// can be delivered before the first faded frame and nothing ever flashes.
    /// </summary>
    public static List<string> TransitionCommands(PipelineSpec spec, int from, int to, double startAt, double seconds) =>
        TransitionCommands(spec, from, to, startAt, seconds, out _);

    /// <param name="critical">How many leading lines must reach ffmpeg before the fade starts (the rest are
    /// expressions of time that may land later, such as switching the old scene off once it is hidden).</param>
    public static List<string> TransitionCommands(PipelineSpec spec, int from, int to, double startAt, double seconds, out int critical,
        Func<string, bool>? hidden = null, Func<AudioGainStage, IReadOnlyList<double>>? gainsNow = null)
    {
        hidden ??= _ => false;
        gainsNow ??= g => g.LinearGainPerScene;
        var lines = new List<string>();
        critical = 0;
        if (spec.SceneCount < 2 || from == to) return lines;
        if (from < 0 || to < 0 || from >= spec.SceneCount || to >= spec.SceneCount) return lines;
        double d = Math.Max(0.001, seconds);
        string ramp = $"clip((t-{I(startAt)})/{I(d)},0,1)";
        // the incoming scene's switched-off overlays come on first (still invisible: the top is transparent)
        if (to < spec.SceneOverlays.Count)
            foreach (var name in spec.SceneOverlays[to].Where(n => !hidden(n))) lines.Add($"{name} -1 enable 1");
        // Between switches the two selectors both show the scene that is up and the mask is opaque. To change
        // scene: put what is on screen underneath, hide the top, swap the top to the new scene while nothing can
        // see it, then fade it in. Every step leaves the picture as it was, so there is no flash at any point.
        // brightness is added to a black mask: ≤ -0.1 → fully transparent, ≥ 1 → fully opaque.
        var canvases = new List<(string Fade, string Select)> { (FadeFilter, SceneSelect) };
        if (spec.HasVertical) canvases.Add((VerticalFadeFilter, VerticalSceneSelect));
        if (spec.HasClip) canvases.Add((ClipFadeFilter, ClipSceneSelect));
        foreach (var (fade, sel) in canvases)
        {
            lines.Add($"streamselect@{sel}under -1 map {from}");
            lines.Add($"{fade} -1 brightness -1");
            lines.Add($"streamselect@{sel}over -1 map {to}");
            lines.Add($"{fade} -1 brightness -0.1+1.1*{ramp}");
        }
        foreach (var g in spec.AudioGains)
        {
            var levels = gainsNow(g);
            if (from >= levels.Count || to >= levels.Count) continue;
            double a = levels[from], b = levels[to];
            if (Math.Abs(a - b) < 1e-6) continue;
            lines.Add($"{g.FilterName} -1 volume {I(a)}+({I(b - a)})*{ramp}");
        }
        critical = lines.Count;
        // the outgoing scene stops being blended once the fade has finished
        if (from < spec.SceneOverlays.Count)
            foreach (var name in spec.SceneOverlays[from].Where(n => !hidden(n))) lines.Add($"{name} -1 enable lt(t,{I(startAt + d + 0.1)})");
        return lines;
    }

    private static IEnumerable<string> WithWallclock(InputSpec spec)
    {
        if (!spec.Wallclock) return spec.Args;
        // insert before the final "-i path"
        var list = spec.Args.ToList();
        int i = list.LastIndexOf("-i");
        list.InsertRange(Math.Max(0, i), new[] { "-use_wallclock_as_timestamps", "1" });
        return list;
    }

    private static InputSpec? CameraFeedInput(StudioSource s, SourceKind kind, string key, PipelineRequest r)
    {
        if (kind != SourceKind.Camera || !r.CameraInputs.TryGetValue(key, out var feed)) return null;
        // Frame-counted from the shared origin: exact timestamps however ffmpeg schedules its reads.
        // Each sampled picture left the camera ~100 ms earlier (reader process + sampling, measured), so shift it back.
        double shift = -(CameraBridge.PathLatencySeconds + s.SyncOffsetMs / 1000.0);
        return new InputSpec(new[] { "-itsoffset", I(shift), "-f", "rawvideo", "-pix_fmt", "yuv420p", "-video_size", $"{feed.Width}x{feed.Height}",
            "-framerate", I(r.Plan.Fps), "-thread_queue_size", "8", "-i", feed.Path }, false);
    }

    private static InputSpec? WindowFeedInput(SourceKind kind, string key, PipelineRequest r)
    {
        if (kind != SourceKind.Window || !r.WindowInputs.TryGetValue(key, out var feed)) return null;
        // same frame-counted path as the camera: shift back by the reader + sampling delay
        return new InputSpec(new[] { "-itsoffset", I(-CameraBridge.PathLatencySeconds), "-f", "rawvideo", "-pix_fmt", "yuv420p",
            "-video_size", $"{feed.Width}x{feed.Height}", "-framerate", I(r.Plan.Fps), "-thread_queue_size", "8", "-i", feed.Path }, false);
    }

    /// <summary>
    /// A layer's pixel format: video as YUV, graphics with transparency as RGBA, except browser pages, which arrive
    /// already in the YUV-with-alpha the overlay blends (converted once per page change, not every frame).
    /// </summary>
    private static string AlphaFormat(bool alpha, SourceKind kind) => !alpha ? "yuv420p" : kind == SourceKind.Browser ? "yuva420p" : "rgba";

    private static InputSpec? BrowserFeedInput(SourceKind kind, string key, PipelineRequest r)
    {
        if (kind != SourceKind.Browser || !r.BrowserInputs.TryGetValue(key, out var feed)) return null;
        return new InputSpec(new[] { "-f", "rawvideo", "-pix_fmt", BrowserBridge.PixelFormat, "-video_size", $"{feed.Width}x{feed.Height}",
            "-framerate", I(feed.Fps), "-thread_queue_size", "16", "-i", feed.Path }, false);
    }

    /// <summary>The DirectShow input for a webcam (opened by the camera reader process, not the main pipeline).</summary>
    public static InputSpec? CameraDeviceInput(StudioSource s)
    {
        if (string.IsNullOrWhiteSpace(s.DeviceName) || !OperatingSystem.IsWindows()) return null;
        // A small real-time buffer: if the reader ever falls behind, DirectShow drops a frame instead of queueing
        // seconds of them (64 MB holds several seconds of a compressed webcam: that backlog is pure camera delay).
        string rtbuf = "16M";
        if (SmartTuner.TryParseResolution(s.CameraResolution, out var bw, out var bh))
            rtbuf = Math.Clamp(bw * bh * 2 * 6 / (1024 * 1024) + 1, 4, 32) + "M"; // ~6 raw frames
        var c = new List<string> { "-f", "dshow", "-rtbufsize", rtbuf };
        if (!string.IsNullOrWhiteSpace(s.CameraResolution) && s.CameraResolution != "Default")
            c.AddRange(new[] { "-video_size", s.CameraResolution.Replace("×", "x").Replace(" ", "") });
        if (s.CameraFps > 0) c.AddRange(new[] { "-framerate", I(s.CameraFps) });
        if (s.CameraFormat.StartsWith("vcodec=", StringComparison.Ordinal)) c.AddRange(new[] { "-vcodec", s.CameraFormat[7..] });
        else if (s.CameraFormat.StartsWith("pixel_format=", StringComparison.Ordinal)) c.AddRange(new[] { "-pixel_format", s.CameraFormat[13..] });
        c.AddRange(new[] { "-i", "video=" + s.DeviceName });
        return new InputSpec(c, false);
    }

    private static InputSpec? VideoInput(StudioSource s, SourceKind kind, PipelineRequest r, List<string> warnings)
    {
        int F = r.Plan.Fps;
        string mouse = s.CaptureCursor ? "1" : "0";
        if (!r.IsWindows && kind is SourceKind.Display or SourceKind.Window or SourceKind.Camera)
        {
            warnings.Add($"\"{s.Name}\": device capture needs Windows, skipped.");
            return null;
        }
        switch (kind)
        {
            case SourceKind.Display:
                if (!r.GpuDisplayCapture || string.Equals(s.CaptureMethod, "GDI", StringComparison.OrdinalIgnoreCase))
                {
                    var a = new List<string> { "-thread_queue_size", "512", "-f", "gdigrab", "-framerate", I(F), "-draw_mouse", mouse };
                    if (r.MonitorBounds?.Invoke(s.MonitorIndex) is { } m)
                        a.AddRange(new[] { "-offset_x", I(m.X), "-offset_y", I(m.Y), "-video_size", $"{m.W}x{m.H}" });
                    a.AddRange(new[] { "-i", "desktop" });
                    return new InputSpec(a, true);
                }
                // Desktop Duplication: GPU capture, works with fullscreen DirectX games, very low CPU.
                string down = "hwdownload,format=bgra";
                if (r.GpuDisplayScale && r.MonitorBounds?.Invoke(s.MonitorIndex) is { W: > 0, H: > 0 } mon)
                {
                    // shrink to what the canvas can show (covering it), on the GPU, and copy NV12 (1.5 bytes a pixel, not 4)
                    int tw = mon.W, th = mon.H;
                    if (!r.DisplayFullSize)
                    {
                        double k = Math.Max((double)r.Plan.Width / mon.W, (double)r.Plan.Height / mon.H);
                        if (k < 1) { tw = (int)Math.Ceiling(mon.W * k / 2) * 2; th = (int)Math.Ceiling(mon.H * k / 2) * 2; }
                    }
                    down = $"scale_d3d11=width={tw}:height={th}:format=nv12,hwdownload,format=nv12";
                }
                return new InputSpec(new[] { "-thread_queue_size", "512", "-f", "lavfi", "-i",
                    $"ddagrab=output_idx={Math.Max(0, s.MonitorIndex)}:framerate={F}:draw_mouse={mouse},{down}" }, true);

            // Windows and webcams are captured by their own reader processes (WindowCaptureBridge / CameraBridge)
            // and arrive as counted pipes; reaching here means there was nothing to capture.
            case SourceKind.Window:
                warnings.Add($"\"{s.Name}\": no window chosen, skipped.");
                return null;

            case SourceKind.Camera:
                warnings.Add($"\"{s.Name}\": no camera chosen, skipped.");
                return null;

            case SourceKind.Image:
                if (string.IsNullOrWhiteSpace(s.FilePath) || !File.Exists(s.FilePath))
                { warnings.Add($"\"{s.Name}\": image file not found, skipped."); return null; }
                // A single frame; the overlay repeats it forever (eof_action=repeat). A paced image loop would make
                // every overlay above it wait for the next image frame and stall the live layers.
                return new InputSpec(new[] { "-i", s.FilePath }, false);

            case SourceKind.Browser:
                warnings.Add($"\"{s.Name}\": the page couldn't be loaded (browser sources need the Microsoft Edge WebView2 runtime).");
                return null;

            case SourceKind.Text:
                if (!r.RenderedImages.TryGetValue(s.Id, out var png) || !File.Exists(png))
                { warnings.Add($"\"{s.Name}\": text is empty, skipped."); return null; }
                return new InputSpec(new[] { "-i", png }, false);

            case SourceKind.Timer:
                // a see-through frame the card is drawn onto; the reading itself is re-read from disk every frame
                if (!r.TimerFilters.ContainsKey(s.Id) || !r.KnownSizes.TryGetValue(InputKey(s), out var tz))
                { warnings.Add($"\"{s.Name}\": the timer couldn\u2019t be drawn, skipped."); return null; }
                return new InputSpec(new[] { "-f", "lavfi", "-i",
                    $"color=c=0x000000@0.0:s={tz.W}x{tz.H}:r={I(r.Plan?.Fps ?? 30)}" }, false);
        }
        return null;
    }

    /// <summary>Windows captured by the bridge in the build in progress (their crop happens in the reader).</summary>
    [ThreadStatic] private static IReadOnlyDictionary<string, WindowCaptureBridge.WindowFeed>? _bridged;
    [ThreadStatic] private static Dictionary<string, string>? _lightNames;
    [ThreadStatic] private static List<(string Key, string Name)>? _lightKeys;
    [ThreadStatic] private static IReadOnlyDictionary<string, LightSettings>? _lightStart;
    [ThreadStatic] private static List<(string Key, string Name)>? _denoiseKeys;
    [ThreadStatic] private static IReadOnlySet<string>? _denoiseOff;

    /// <summary>The preview gate's expression for a rate: every frame, 1 in 3, or none (a frame with no time is dropped).</summary>
    public static string GateExpr(PreviewRate rate) => rate switch
    {
        PreviewRate.Slow => "if(mod(N,3),0/0,PTS)",
        PreviewRate.Trickle => "if(mod(N,8),0/0,PTS)",   // a couple of pictures a second: enough to spot a death screen
        PreviewRate.Paused => "0/0",
        _ => "PTS",
    };

    /// <summary>Live commands that set how fast the preview runs.</summary>
    public static IEnumerable<string> PreviewGateCommands(PipelineSpec spec, PreviewRate rate) =>
        spec.PreviewGates.Select(g => $"{g} -1 expr {GateExpr(rate)}");

    private static string Crop(StudioSource s)
    {
        if (SourceTypes.KindOf(s.Type) == SourceKind.Window && _bridged?.ContainsKey(InputKey(s)) == true) return "";
        int l = Math.Max(0, s.CropLeft), t = Math.Max(0, s.CropTop), rr = Math.Max(0, s.CropRight), b = Math.Max(0, s.CropBottom);
        if (l + t + rr + b == 0) return "";
        return $"crop=iw-{l + rr}:ih-{t + b}:{l}:{t},";
    }

    /// <summary>Camera: scale down first, then temporal denoise (cheap at small size, and noise is pure wasted bitrate).</summary>
    /// <summary>The timer card's own chain (it is drawn, not captured), ahead of the usual layer work.</summary>
    private static string TimerPrep(StudioSource s, SourceKind kind, PipelineRequest r) =>
        kind == SourceKind.Timer && r.TimerFilters.TryGetValue(s.Id, out var chain) ? chain + "," : "";

    private static string CameraPrep(StudioSource s, SourceKind kind, string? scale)
    {
        if (kind != SourceKind.Camera) return scale ?? "";
        var sb = new StringBuilder(scale ?? "");
        if (s.CameraDenoise && s.CameraAutoLight && _denoiseKeys is not null)
        {
            // with auto light, denoise runs only while the room is dark (switched live; in a bright room there's
            // little noise to remove and it saves the work on every frame)
            string key = InputKey(s), name = $"hqdn3d@dn{_denoiseKeys.Count}";
            _denoiseKeys.Add((key, name));
            sb.Append($"{name}=1.5:1.5:6:6:enable={(_denoiseOff?.Contains(key) == true ? 0 : 1)},");
        }
        else if (s.CameraDenoise) sb.Append("hqdn3d=1.5:1.5:6:6,");
        // auto light: after the scale (so it touches only the pixels shown), adjusted live by the engine
        if (s.CameraAutoLight && _lightNames is not null)
        {
            string key = InputKey(s), sig = $"{key}|{Crop(s)}|{scale}|{s.CameraDenoise}|{s.Mirror}";
            if (!_lightNames.TryGetValue(sig, out var name)) { name = $"cl{_lightNames.Count}"; _lightNames[sig] = name; _lightKeys!.Add((key, name)); }
            var start = _lightStart?.TryGetValue(key, out var st) == true ? st : LightSettings.Neutral;
            sb.Append(start.Filter(name)).Append(',');
        }
        if (s.Mirror) sb.Append("hflip,");
        return sb.ToString();
    }

    /// <summary>
    /// Overlay x/y on the horizontal canvas: pixels for free placement, expressions of the overlay's own size for
    /// anchors (exact for any source size). Also sent as live commands when a source is moved.
    /// </summary>
    public static (string X, string Y) HorizontalPosition(StudioSource src, int W, int H)
    {
        string anchor = Anchors.Resolve(src);
        if (anchor == Anchors.Free) return (LayoutMath.Px(src.X * W), LayoutMath.Px(src.Y * H));
        string m = I(Math.Max(0, src.MarginPx));
        return anchor switch
        {
            Anchors.TopLeft => (m, m),
            Anchors.TopRight => ($"W-w-{m}", m),
            Anchors.BottomLeft => (m, $"H-h-{m}"),
            Anchors.BottomRight => ($"W-w-{m}", $"H-h-{m}"),
            _ => ("(W-w)/2", "(H-h)/2"), // Center and Fill
        };
    }

    /// <summary>How an overlay is scaled on the horizontal canvas ("" = natural size, for text and logos).</summary>
    public static string HorizontalScale(StudioSource src, int W, int H, int F, bool light = false)
    {
        var kind = SourceTypes.KindOf(src.Type);
        string anchor = Anchors.Resolve(src);
        bool natural = SourceTypes.IsGraphic(kind);
        if (anchor == Anchors.Fill) return FillChain(src, W, H, F, pad: false, light);
        if (anchor == Anchors.Free)
        {
            if (src.W <= 0 && natural) return "";
            double frac = src.W > 0 ? src.W : 0.25;
            int sw = Math.Max(16, SmartTuner.Even((int)Math.Round(frac * W)));
            if (src.H > 0 && src.W > 0) // stretched on purpose (Shift + drag)
                return $"scale={sw}:{Math.Max(16, SmartTuner.Even((int)Math.Round(src.H * H)))}:flags={(light ? "fast_bilinear" : "bicubic")},setsar=1,";
            return $"scale={sw}:-2:flags={(light ? "fast_bilinear" : "bicubic")},";
        }
        if (kind is SourceKind.Text or SourceKind.Timer || (kind is SourceKind.Image or SourceKind.Browser && src.SizePercent <= 0)) return "";
        return $"scale={LayoutMath.OverlayWidth(src, W)}:-2:flags={(light ? "fast_bilinear" : "bicubic")},";
    }

    /// <summary>
    /// Scales a full-screen source to the canvas. "Crop" (default) fills the whole canvas without stretching by
    /// trimming the edges, so an 800×500 (16:10) game window fills a 16:9 stream with no bars and no distortion.
    /// </summary>
    public static string FillChain(StudioSource s, int cw, int ch, int fps, bool pad, bool light = false)
    {
        string flags = ScaleFlags(fps, light);
        return s.FillMode switch
        {
            Anchors.FillStretch => $"scale={cw}:{ch}:flags={flags},setsar=1,",
            Anchors.FillFit => $"scale={cw}:{ch}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags={flags}," +
                               (pad ? $"pad={cw}:{ch}:(ow-iw)/2:(oh-ih)/2:color=black," : "") + "setsar=1,",
            _ => $"scale={cw}:{ch}:force_original_aspect_ratio=increase:force_divisible_by=2:flags={flags},crop={cw}:{ch},setsar=1,",
        };
    }

    /// <summary>Overlay width on the vertical canvas (independent of the source's aspect ratio).</summary>
    public static int VerticalWidth(StudioSource src, int VW, int VH) =>
        Math.Max(16, SmartTuner.Even((int)Math.Round(LayoutMath.Vertical(src, VW, VH, 16.0 / 9, false).W)));

    /// <summary>How an overlay is scaled on the vertical canvas (keeps its shape unless stretched by hand).</summary>
    public static string VerticalScale(StudioSource src, int VW, int VH)
    {
        int w = VerticalWidth(src, VW, VH);
        return !src.VAuto && src.VH > 0
            ? $"scale={w}:{Math.Max(16, SmartTuner.Even((int)Math.Round(src.VH * VH)))}:flags=bicubic,setsar=1,"
            : $"scale={w}:-2:flags=bicubic,";
    }

    /// <summary>
    /// What a scene collection composites, minus anything that can change live (names, positions, anchors that
    /// keep the same size): a different value means the pipeline must be rebuilt.
    /// </summary>
    public static string LayoutFingerprint(IEnumerable<StudioScene> scenes, int W, int H, (int W, int H)? vertical)
    {
        var placement = new[] { nameof(StudioSource.Name), nameof(StudioSource.X), nameof(StudioSource.Y), nameof(StudioSource.W),
            nameof(StudioSource.VX), nameof(StudioSource.VY), nameof(StudioSource.VW), nameof(StudioSource.VAuto), nameof(StudioSource.VShow),
            nameof(StudioSource.Anchor), nameof(StudioSource.MarginPx), nameof(StudioSource.SizePercent),
            nameof(StudioSource.H), nameof(StudioSource.VH), // sizes: covered by the scale strings below
            nameof(StudioSource.Locked), // editor-only: locking never rebuilds the pipeline
            nameof(StudioSource.Visible), nameof(StudioSource.VolumeDb), // switched live (enable / volume commands)
            nameof(StudioSource.Hotkey), nameof(StudioSource.FolderId), nameof(StudioSource.Collapsed) }; // folders: list-only
        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var sc in scenes)
        {
            var sources = sc.Sources.ToList();
            var layers = CompositedLayers(sources).Where(x => SourceTypes.IsVideo(SourceTypes.KindOf(x.Type))).ToList();
            var vLayers = vertical is null ? new List<StudioSource>() : VerticalLayers(sources, includeHidden: true);
            var list = new System.Text.Json.Nodes.JsonArray();
            foreach (var src in sources.Where(x => !x.IsFolder))
            {
                var o = System.Text.Json.JsonSerializer.SerializeToNode(src)!.AsObject();
                foreach (var k in placement) o.Remove(k);
                o["lh"] = !layers.Contains(src) ? "-" : LayoutMath.IsBaseLayer(src, layers) ? "base" : HorizontalScale(src, W, H, 30);
                o["lv"] = vertical is { } v && vLayers.Contains(src) ? VerticalScale(src, v.W, v.H) : "-";
                list.Add(o);
            }
            arr.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = sc.Id.ToString(), ["s"] = list });
        }
        return arr.ToJsonString();
    }

    /// <summary>Escapes a tee slave target (the tee muxer treats \ ' | as special).</summary>
    public static string TeeEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            if (ch is '\\' or '\'' or '|') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// <summary>
/// How fast the preview runs: all frames (you're looking), a third (Leapcast is behind another window), an eighth
/// (not visible, but death clips watch the picture), none (minimized or preview off, nothing watching).
/// </summary>
public enum PreviewRate { Full, Slow, Trickle, Paused }
