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

    /// <summary>Full RTMP(S) URL including key. Null = not live.</summary>
    public string? StreamUrl { get; init; }
    /// <summary>Segment file pattern (e.g. C:/…/seg_%06d.ts). Null = no recording/replay cache.</summary>
    public string? SegmentPattern { get; init; }
    public int SegmentSeconds { get; init; } = 2;

    public bool Preview { get; init; } = true;
    public int PreviewWidth { get; init; } = 640;
    public int PreviewHeight { get; init; } = 360;
    public int PreviewFps { get; init; } = 15;

    /// <summary>Audio source id → raw f32le 48 kHz stereo input path (Windows named pipe).
    /// Keys are the representatives returned by <see cref="PipelineBuilder.AudioRepresentatives"/>.</summary>
    public IReadOnlyDictionary<Guid, string> AudioInputs { get; init; } = new Dictionary<Guid, string>();
    /// <summary>Text source id → pre-rendered transparent PNG.</summary>
    public IReadOnlyDictionary<Guid, string> RenderedImages { get; init; } = new Dictionary<Guid, string>();
    /// <summary>Monitor index → virtual-desktop rectangle (only needed for GDI display capture).</summary>
    public Func<int, (int X, int Y, int W, int H)?>? MonitorBounds { get; init; }
    public int AudioBitrateKbps { get; init; } = 160;

    /// <summary>Common time origin (Unix seconds). Every live input is wall-clock stamped and shifted by this,
    /// so game, camera, mic and desktop audio line up no matter how long each device took to open.</summary>
    public double WallclockOrigin { get; init; }

    /// <summary>Test/alternate platform hook: return an input for a source instead of the Windows device input.</summary>
    public Func<StudioSource, InputSpec?>? InputOverride { get; init; }
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();
}

/// <summary>A per-device audio gain stage whose level differs between scenes (crossfaded on scene switch).</summary>
public sealed record AudioGainStage(string FilterName, IReadOnlyList<double> LinearGainPerScene);

public sealed record PipelineSpec(
    IReadOnlyList<string> Args,
    IReadOnlyList<string> Warnings,
    bool Encoded,
    bool HasPreview,
    string FilterGraph,
    int SceneCount,
    IReadOnlyList<AudioGainStage> AudioGains);

public static class PipelineBuilder
{
    private static string I(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Mask filter name prefix; scene k (k ≥ 1) is controlled by "eq@fade{k}".</summary>
    public const string FadeFilter = "eq@fade";

    /// <summary>Two sources that capture the same thing share one ffmpeg input (a camera can only be opened once).</summary>
    public static string InputKey(StudioSource s)
    {
        var k = SourceTypes.KindOf(s.Type);
        return k switch
        {
            SourceKind.Display => $"display|{s.MonitorIndex}|{s.CaptureMethod}|{s.CaptureCursor}",
            SourceKind.Window => $"window|{s.WindowTitle.Trim().ToLowerInvariant()}|{s.CaptureCursor}",
            SourceKind.Camera => $"camera|{s.DeviceName}|{s.CameraResolution}|{s.CameraFps}",
            SourceKind.Image => $"image|{s.FilePath.ToLowerInvariant()}",
            SourceKind.Text => $"text|{s.Id}",
            SourceKind.Microphone or SourceKind.DesktopAudio => $"audio|{k}|{s.AudioDeviceId}",
            _ => $"other|{s.Id}"
        };
    }

    /// <summary>One representative source per distinct audio device across all scenes (first occurrence wins).</summary>
    public static List<StudioSource> AudioRepresentatives(IEnumerable<StudioScene> scenes)
    {
        var seen = new HashSet<string>();
        var list = new List<StudioSource>();
        foreach (var sc in scenes)
            foreach (var s in sc.Sources.Where(s => s.Visible && SourceTypes.IsAudio(SourceTypes.KindOf(s.Type))))
                if (seen.Add(InputKey(s))) list.Add(s);
        return list;
    }

    public static PipelineSpec Build(PipelineRequest r)
    {
        var plan = r.Plan;
        int W = plan.Width, H = plan.Height, F = plan.Fps;
        var scenes = r.Scenes is { Count: > 0 }
            ? r.Scenes
            : new[] { new StudioScene { Name = "Scene", Sources = new(r.Sources ?? Array.Empty<StudioSource>()) } };
        int N = scenes.Count;
        int active = Math.Clamp(r.ActiveSceneIndex, 0, N - 1);
        // stats_period also sets how often ffmpeg polls stdin for transition commands (ffmpeg 7: once per period).
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats",
            "-progress", "pipe:2", "-stats_period", "0.1", "-copyts", "-y" };
        var warnings = new List<string>();
        var graph = new List<string>();
        int inputCount = 0;
        bool encoded = r.StreamUrl is not null || r.SegmentPattern is not null;
        string shift = "setpts=PTS-" + I(r.WallclockOrigin) + "/TB,";

        // ---------- video inputs (deduplicated across scenes) ----------
        var inputs = new Dictionary<string, (int index, bool wall, List<string> uses)>();
        var sceneLayers = new List<List<(StudioSource src, SourceKind kind, string key)>>();
        var warned = new HashSet<Guid>();
        var failedKeys = new HashSet<string>();
        foreach (var sc in scenes)
        {
            var layers = new List<(StudioSource, SourceKind, string)>();
            foreach (var s in sc.Sources.Where(s => s.Visible))
            {
                var kind = SourceTypes.KindOf(s.Type);
                if (kind == SourceKind.Unsupported)
                {
                    if (warned.Add(s.Id)) warnings.Add($"\"{s.Name}\": source type \"{s.Type}\" isn't supported, skipped.");
                    continue;
                }
                if (!SourceTypes.IsVideo(kind)) continue;
                string key = InputKey(s);
                if (failedKeys.Contains(key)) continue;
                if (!inputs.ContainsKey(key))
                {
                    var spec = r.InputOverride?.Invoke(s) ?? VideoInput(s, kind, r, warned.Add(s.Id) ? warnings : new List<string>());
                    if (spec is null) { failedKeys.Add(key); continue; }
                    args.AddRange(WithWallclock(spec));
                    inputs[key] = (inputCount++, spec.Wallclock, new List<string>());
                }
                layers.Add((s, kind, key));
            }
            sceneLayers.Add(layers);
        }

        // Label every use of every input; a single split per input feeds all scenes.
        string Use(string key)
        {
            var e = inputs[key];
            string label = $"i{e.index}u{e.uses.Count}";
            e.uses.Add(label);
            return label;
        }

        // Shared blank canvas for scenes whose bottom layer isn't a full-screen capture.
        var colorUses = new List<string>();
        int colorIndex = -1;
        string ColorUse()
        {
            if (colorIndex < 0)
            {
                args.AddRange(new[] { "-re", "-f", "lavfi", "-i", $"color=c=0x0E1016:s={W}x{H}:r={F}" });
                colorIndex = inputCount++;
            }
            string label = $"bg{colorUses.Count}";
            colorUses.Add(label);
            return label;
        }

        var sceneGraph = new List<string>();
        for (int k = 0; k < N; k++)
        {
            var layers = sceneLayers[k];
            string p = $"s{k}";
            int start = 0;
            if (layers.Count > 0 && Anchors.Resolve(layers[0].src) == Anchors.Fill
                && layers[0].kind is SourceKind.Display or SourceKind.Window or SourceKind.Camera)
            {
                var b = layers[0];
                sceneGraph.Add($"[{Use(b.key)}]{Crop(b.src)}{CameraPrep(b.src, b.kind, null)}" +
                               $"scale={W}:{H}:force_original_aspect_ratio=decrease:flags=lanczos," +
                               $"pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,format=yuv420p[{p}base]");
                start = 1;
            }
            else
            {
                sceneGraph.Add($"[{ColorUse()}]format=yuv420p[{p}base]");
                if (layers.Count == 0 && N == 1) warnings.Add("No visible video source in this scene: streaming a blank canvas.");
            }
            string cur = $"{p}base";
            for (int j = start; j < layers.Count; j++)
            {
                var (src, kind, key) = layers[j];
                string anchor = Anchors.Resolve(src);
                bool alpha = kind is SourceKind.Image or SourceKind.Text;
                string scale;
                if (anchor == Anchors.Fill)
                    scale = $"scale={W}:{H}:force_original_aspect_ratio=decrease:flags=lanczos,";
                else if (kind == SourceKind.Text || (kind == SourceKind.Image && src.SizePercent <= 0))
                    scale = "";
                else
                {
                    int tw = Math.Max(16, SmartTuner.Even((int)Math.Round(W * Math.Clamp(src.SizePercent, 1, 100) / 100.0)));
                    scale = $"scale={tw}:-2:flags=bicubic,";
                }
                sceneGraph.Add($"[{Use(key)}]{Crop(src)}{CameraPrep(src, kind, scale)}format={(alpha ? "rgba" : "yuv420p")}[{p}l{j}]");
                sceneGraph.Add($"[{cur}][{p}l{j}]overlay={Position(anchor, src.MarginPx)}:eof_action=repeat:format=yuv420[{p}c{j}]");
                cur = $"{p}c{j}";
            }
            sceneGraph.Add($"[{cur}]fps={F},format=yuv420p[scene{k}]");
        }

        // input splits (timestamp shift happens once, before the split)
        foreach (var (_, e) in inputs)
        {
            if (e.uses.Count == 0) continue;
            graph.Add($"[{e.index}:v]{(e.wall ? shift : "")}split={e.uses.Count}{string.Concat(e.uses.Select(u => $"[{u}]"))}");
        }
        if (colorIndex >= 0)
            graph.Add($"[{colorIndex}:v]split={colorUses.Count}{string.Concat(colorUses.Select(u => $"[{u}]"))}");
        graph.AddRange(sceneGraph);

        // ---------- scene switcher ----------
        // Scenes are stacked bottom→top; every scene above the first has its own alpha mask (an eq brightness
        // expression of stream time t). Switching only rewrites mask expressions: no filter reconfiguration,
        // no restart, frame-accurate fades. Invariant: every mask above the on-air scene is transparent.
        if (N == 1)
            graph.Add("[scene0]format=nv12[vfinal]");
        else
        {
            string cur = "scene0";
            for (int k = 1; k < N; k++)
            {
                string initial = k == active ? "1" : "-1";
                graph.Add($"color=c=black:s={W}x{H}:r={F},format=gray,{FadeFilter}{k}=eval=frame:brightness={initial}[m{k}]");
                graph.Add($"[scene{k}][m{k}]alphamerge[sa{k}]");
                graph.Add($"[{cur}][sa{k}]overlay=format=yuv420:eof_action=repeat[stack{k}]");
                cur = $"stack{k}";
            }
            graph.Add($"[{cur}]format=nv12[vfinal]");
        }

        string venc = "vfinal", vprev = "vfinal";
        if (encoded && r.Preview)
        {
            graph.Add("[vfinal]split=2[venc][vprevsrc]");
            venc = "venc"; vprev = "vprevsrc";
        }
        if (r.Preview)
            graph.Add($"[{vprev}]fps={r.PreviewFps},scale={r.PreviewWidth}:{r.PreviewHeight}:flags=fast_bilinear,format=bgra[prev]");

        // ---------- audio: one input per device; per-scene level via a runtime-adjustable volume stage ----------
        var gains = new List<AudioGainStage>();
        if (encoded)
        {
            var mixInputs = new List<string>();
            foreach (var rep in AudioRepresentatives(scenes))
            {
                var kind = SourceTypes.KindOf(rep.Type);
                string key = InputKey(rep);
                InputSpec? spec = r.InputOverride?.Invoke(rep);
                if (spec is null && r.AudioInputs.TryGetValue(rep.Id, out var path))
                    spec = new InputSpec(new[] { "-f", "f32le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "1024", "-i", path }, true);
                if (spec is null) { warnings.Add($"\"{rep.Name}\": audio device unavailable, skipped."); continue; }
                args.AddRange(WithWallclock(spec));
                int idx = inputCount++;

                var perScene = scenes.Select(sc =>
                {
                    var s = sc.Sources.FirstOrDefault(x => x.Visible && InputKey(x) == key);
                    return s is null ? 0.0 : Math.Pow(10, s.VolumeDb / 20.0);
                }).ToList();
                bool cleanup = scenes.SelectMany(sc => sc.Sources).Any(x => x.Visible && InputKey(x) == key && x.NoiseCleanup)
                               && kind == SourceKind.Microphone;
                string gainName = $"volume@g{idx}";

                var chain = new StringBuilder($"[{idx}:a]");
                if (spec.Wallclock) chain.Append("asetpts=PTS-" + I(r.WallclockOrigin) + "/TB,");
                chain.Append("aresample=48000:async=1000,"); // absorbs device clock drift + pipe jitter
                if (cleanup) chain.Append("highpass=f=80,afftdn=nr=12:nf=-40:tn=1,"); // adaptive background-noise removal
                bool varies = perScene.Distinct().Count() > 1;
                if (varies || Math.Abs(perScene[active] - 1.0) > 1e-6)
                {
                    chain.Append($"{gainName}=volume={I(perScene[active])}:eval=frame,");
                    if (varies) gains.Add(new AudioGainStage(gainName, perScene));
                }
                chain.Append($"aformat=sample_fmts=fltp:channel_layouts=stereo[a{idx}]");
                graph.Add(chain.ToString());
                mixInputs.Add($"[a{idx}]");
            }
            if (mixInputs.Count == 0)
            {
                args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo" });
                graph.Add($"[{inputCount++}:a]aformat=sample_fmts=fltp:channel_layouts=stereo[aout]");
            }
            else if (mixInputs.Count == 1)
                graph.Add($"{mixInputs[0]}alimiter=limit=0.95:level=0[aout]");
            else
                graph.Add($"{string.Concat(mixInputs)}amix=inputs={mixInputs.Count}:duration=longest:dropout_transition=0:normalize=0," +
                          "alimiter=limit=0.95:level=0[aout]");
        }

        string filterGraph = string.Join(";", graph);
        args.AddRange(new[] { "-filter_complex", filterGraph });

        // ---------- outputs ----------
        if (encoded)
        {
            args.AddRange(new[] { "-map", $"[{venc}]", "-map", "[aout]" });
            args.AddRange(EncoderCatalog.BuildVideoArgs(plan));
            args.AddRange(new[] { "-c:a", "aac", "-b:a", I(r.AudioBitrateKbps) + "k", "-ar", "48000", "-ac", "2",
                "-flags", "+global_header", "-f", "tee" });
            var slaves = new List<string>();
            if (r.StreamUrl is not null)
                slaves.Add("[f=flv:onfail=abort:flvflags=no_duration_filesize]" + TeeEscape(r.StreamUrl));
            if (r.SegmentPattern is not null)
                slaves.Add($"[f=segment:segment_time={r.SegmentSeconds}:segment_format=mpegts:onfail=ignore]" +
                           TeeEscape(r.SegmentPattern.Replace('\\', '/')));
            args.Add(string.Join("|", slaves));
        }
        if (r.Preview)
            args.AddRange(new[] { "-map", "[prev]", "-c:v", "rawvideo", "-f", "rawvideo", "pipe:1" });

        return new PipelineSpec(args, warnings, encoded, r.Preview, filterGraph, N, gains);
    }

    /// <summary>
    /// Runtime commands (ffmpeg stdin "C" lines) that crossfade from scene <paramref name="from"/> to <paramref name="to"/>.
    /// The fade starts at stream time <paramref name="startAt"/> (seconds since the wall-clock origin), so every line
    /// can be delivered before the first faded frame and nothing ever flashes.
    /// </summary>
    public static List<string> TransitionCommands(PipelineSpec spec, int from, int to, double startAt, double seconds)
    {
        var lines = new List<string>();
        if (spec.SceneCount < 2 || from == to) return lines;
        double d = Math.Max(0.001, seconds);
        string ramp = $"clip((t-{I(startAt)})/{I(d)},0,1)";
        // brightness is added to a black mask: ≤ -0.1 → fully transparent, ≥ 1 → fully opaque
        if (to > from)
        {
            // everything between is already transparent: just fade the new scene in on top
            lines.Add($"{FadeFilter}{to} -1 brightness -0.1+1.1*{ramp}");
        }
        else
        {
            // reveal a lower scene: prepare it (hidden under the current one), then fade the current one out
            for (int k = to + 1; k < from; k++) lines.Add($"{FadeFilter}{k} -1 brightness -1");
            if (to > 0) lines.Add($"{FadeFilter}{to} -1 brightness 1");
            lines.Add($"{FadeFilter}{from} -1 brightness 1-1.1*{ramp}");
        }
        foreach (var g in spec.AudioGains)
        {
            double a = g.LinearGainPerScene[from], b = g.LinearGainPerScene[to];
            if (Math.Abs(a - b) < 1e-6) continue;
            lines.Add($"{g.FilterName} -1 volume {I(a)}+({I(b - a)})*{ramp}");
        }
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
                if (string.Equals(s.CaptureMethod, "GDI", StringComparison.OrdinalIgnoreCase))
                {
                    var a = new List<string> { "-thread_queue_size", "512", "-f", "gdigrab", "-framerate", I(F), "-draw_mouse", mouse };
                    if (r.MonitorBounds?.Invoke(s.MonitorIndex) is { } m)
                        a.AddRange(new[] { "-offset_x", I(m.X), "-offset_y", I(m.Y), "-video_size", $"{m.W}x{m.H}" });
                    a.AddRange(new[] { "-i", "desktop" });
                    return new InputSpec(a, true);
                }
                // Desktop Duplication: GPU capture, works with fullscreen DirectX games, very low CPU.
                return new InputSpec(new[] { "-thread_queue_size", "512", "-f", "lavfi", "-i",
                    $"ddagrab=output_idx={Math.Max(0, s.MonitorIndex)}:framerate={F}:draw_mouse={mouse},hwdownload,format=bgra" }, true);

            case SourceKind.Window:
                if (string.IsNullOrWhiteSpace(s.WindowTitle)) { warnings.Add($"\"{s.Name}\": no window chosen, skipped."); return null; }
                return new InputSpec(new[] { "-thread_queue_size", "512", "-f", "gdigrab", "-framerate", I(F),
                    "-draw_mouse", mouse, "-i", "title=" + s.WindowTitle.Trim() }, true);

            case SourceKind.Camera:
                if (string.IsNullOrWhiteSpace(s.DeviceName)) { warnings.Add($"\"{s.Name}\": no camera chosen, skipped."); return null; }
                var c = new List<string> { "-thread_queue_size", "512", "-f", "dshow", "-rtbufsize", "256M" };
                if (!string.IsNullOrWhiteSpace(s.CameraResolution) && s.CameraResolution != "Default")
                    c.AddRange(new[] { "-video_size", s.CameraResolution.Replace("×", "x").Replace(" ", "") });
                if (s.CameraFps > 0) c.AddRange(new[] { "-framerate", I(s.CameraFps) });
                c.AddRange(new[] { "-i", "video=" + s.DeviceName });
                return new InputSpec(c, true);

            case SourceKind.Image:
                if (string.IsNullOrWhiteSpace(s.FilePath) || !File.Exists(s.FilePath))
                { warnings.Add($"\"{s.Name}\": image file not found, skipped."); return null; }
                return new InputSpec(new[] { "-re", "-loop", "1", "-framerate", "2", "-i", s.FilePath }, false);

            case SourceKind.Text:
                if (!r.RenderedImages.TryGetValue(s.Id, out var png) || !File.Exists(png))
                { warnings.Add($"\"{s.Name}\": text is empty, skipped."); return null; }
                return new InputSpec(new[] { "-re", "-loop", "1", "-framerate", "2", "-i", png }, false);
        }
        return null;
    }

    private static string Crop(StudioSource s)
    {
        int l = Math.Max(0, s.CropLeft), t = Math.Max(0, s.CropTop), rr = Math.Max(0, s.CropRight), b = Math.Max(0, s.CropBottom);
        if (l + t + rr + b == 0) return "";
        return $"crop=iw-{l + rr}:ih-{t + b}:{l}:{t},";
    }

    /// <summary>Camera: scale down first, then temporal denoise (cheap at small size, and noise is pure wasted bitrate).</summary>
    private static string CameraPrep(StudioSource s, SourceKind kind, string? scale)
    {
        if (kind != SourceKind.Camera) return scale ?? "";
        var sb = new StringBuilder(scale ?? "");
        if (s.CameraDenoise) sb.Append("hqdn3d=1.5:1.5:6:6,");
        if (s.Mirror) sb.Append("hflip,");
        return sb.ToString();
    }

    private static string Position(string anchor, int margin)
    {
        string m = I(Math.Max(0, margin));
        return anchor switch
        {
            Anchors.TopLeft => $"x={m}:y={m}",
            Anchors.TopRight => $"x=W-w-{m}:y={m}",
            Anchors.BottomLeft => $"x={m}:y=H-h-{m}",
            Anchors.BottomRight => $"x=W-w-{m}:y=H-h-{m}",
            _ => "x=(W-w)/2:y=(H-h)/2", // Center and Fill
        };
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
