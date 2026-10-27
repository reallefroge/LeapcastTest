using System.Globalization;
using System.Text;

namespace LeapcastStudioAI;

/// <summary>One ffmpeg input: everything up to and including "-i path".</summary>
public sealed record InputSpec(IReadOnlyList<string> Args, bool Wallclock);

public sealed class PipelineRequest
{
    public required IReadOnlyList<StudioSource> Sources { get; init; }
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

    /// <summary>Audio source id → raw f32le 48 kHz stereo input path (Windows named pipe).</summary>
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

public sealed record PipelineSpec(
    IReadOnlyList<string> Args,
    IReadOnlyList<string> Warnings,
    bool Encoded,
    bool HasPreview,
    string FilterGraph);

public static class PipelineBuilder
{
    private static string I(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static PipelineSpec Build(PipelineRequest r)
    {
        var plan = r.Plan;
        int W = plan.Width, H = plan.Height, F = plan.Fps;
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats",
            "-progress", "pipe:2", "-stats_period", "1", "-copyts", "-y" };
        var warnings = new List<string>();
        var graph = new List<string>();
        int inputCount = 0;
        bool encoded = r.StreamUrl is not null || r.SegmentPattern is not null;
        string shift = "setpts=PTS-" + I(r.WallclockOrigin) + "/TB,";

        // ---------- video inputs ----------
        var layers = new List<(StudioSource src, SourceKind kind, int index, bool wallclock)>();
        foreach (var s in r.Sources.Where(s => s.Visible))
        {
            var kind = SourceTypes.KindOf(s.Type);
            if (kind == SourceKind.Unsupported) { warnings.Add($"\"{s.Name}\": source type \"{s.Type}\" isn't supported, skipped."); continue; }
            if (!SourceTypes.IsVideo(kind)) continue;
            var spec = r.InputOverride?.Invoke(s) ?? VideoInput(s, kind, r, warnings);
            if (spec is null) continue;
            args.AddRange(WithWallclock(spec));
            layers.Add((s, kind, inputCount++, spec.Wallclock));
        }

        // ---------- composite ----------
        string cur;
        int start = 0;
        if (layers.Count > 0 && Anchors.Resolve(layers[0].src) == Anchors.Fill
            && layers[0].kind is SourceKind.Display or SourceKind.Window or SourceKind.Camera)
        {
            var b = layers[0];
            graph.Add($"[{b.index}:v]{(b.wallclock ? shift : "")}{Crop(b.src)}{CameraPrep(b.src, b.kind, null)}" +
                      $"scale={W}:{H}:force_original_aspect_ratio=decrease:flags=lanczos," +
                      $"pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,format=yuv420p[base]");
            start = 1;
        }
        else
        {
            args.AddRange(new[] { "-re", "-f", "lavfi", "-i", $"color=c=0x0E1016:s={W}x{H}:r={F}" });
            graph.Add($"[{inputCount++}:v]format=yuv420p[base]");
            if (layers.Count == 0) warnings.Add("No visible video source in this scene: streaming a blank canvas.");
        }
        cur = "base";

        for (int j = start; j < layers.Count; j++)
        {
            var (src, kind, index, wall) = layers[j];
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
            string prep = CameraPrep(src, kind, scale);
            graph.Add($"[{index}:v]{(wall ? shift : "")}{Crop(src)}{prep}format={(alpha ? "rgba" : "yuv420p")}[l{j}]");
            graph.Add($"[{cur}][l{j}]overlay={Position(anchor, src.MarginPx)}:eof_action=repeat:format=yuv420[c{j}]");
            cur = $"c{j}";
        }

        graph.Add($"[{cur}]fps={F},format=nv12[vfinal]");
        string venc = "vfinal", vprev = "vfinal";
        if (encoded && r.Preview)
        {
            graph.Add("[vfinal]split=2[venc][vprevsrc]");
            venc = "venc"; vprev = "vprevsrc";
        }
        if (r.Preview)
            graph.Add($"[{vprev}]fps={r.PreviewFps},scale={r.PreviewWidth}:{r.PreviewHeight}:flags=fast_bilinear,format=bgra[prev]");

        // ---------- audio (only needed when encoding) ----------
        if (encoded)
        {
            var mixInputs = new List<string>();
            foreach (var s in r.Sources.Where(s => s.Visible))
            {
                var kind = SourceTypes.KindOf(s.Type);
                if (!SourceTypes.IsAudio(kind)) continue;
                InputSpec? spec = r.InputOverride?.Invoke(s);
                if (spec is null && r.AudioInputs.TryGetValue(s.Id, out var path))
                    spec = new InputSpec(new[] { "-f", "f32le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "1024", "-i", path }, true);
                if (spec is null) { warnings.Add($"\"{s.Name}\": audio device unavailable, skipped."); continue; }
                args.AddRange(WithWallclock(spec));
                int idx = inputCount++;
                var chain = new StringBuilder($"[{idx}:a]");
                if (spec.Wallclock) chain.Append("asetpts=PTS-" + I(r.WallclockOrigin) + "/TB,");
                chain.Append("aresample=48000:async=1000,"); // absorbs device clock drift + pipe jitter
                if (kind == SourceKind.Microphone && s.NoiseCleanup)
                    chain.Append("highpass=f=80,afftdn=nr=12:nf=-40:tn=1,"); // adaptive background-noise removal
                if (Math.Abs(s.VolumeDb) > 0.01) chain.Append("volume=" + I(s.VolumeDb) + "dB,");
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

        return new PipelineSpec(args, warnings, encoded, r.Preview, filterGraph);
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
