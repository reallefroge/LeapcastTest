using LeapcastStudioAI;

namespace CoreTests;

/// <summary>
/// Which canvases run: widescreen on its own, 9:16 on its own (it replaces the widescreen one rather than
/// sitting beside it), or both.
/// </summary>
public static class CanvasTests
{
    public static async Task RunAsync(string ffmpeg, string dir, Action<bool, string> check)
    {
        Directory.CreateDirectory(dir);
        var scene = new StudioScene { Name = "Live" };
        var game = new StudioSource { Name = "Game", Type = SourceTypes.Window, WindowTitle = "red", Anchor = Anchors.Fill };
        var cam = new StudioSource { Name = "Cam", Type = SourceTypes.Camera, DeviceName = "blue", Anchor = Anchors.TopRight, SizePercent = 25 };
        scene.Sources.Add(game); scene.Sources.Add(cam);
        var wide = new EncodePlan(1280, 720, 30, 4000, 22, "libx264", EncoderTier.Rich, false, false, "veryfast", "t", Array.Empty<string>());
        var tall = wide with { Width = 720, Height = 1280 };
        InputSpec Feed(StudioSource s, double _) => new(new[] { "-f", "lavfi", "-i",
            $"color=c={(SourceTypes.KindOf(s.Type) == SourceKind.Camera ? "blue" : "red")}:s=640x360:r=30" }, false);

        PipelineSpec Build(EncodePlan plan, bool mainVertical, (int, int)? second) => PipelineBuilder.Build(new PipelineRequest
        {
            Scenes = new[] { scene }, ActiveSceneIndex = 0, Preview = true, PreviewWidth = 768, PreviewHeight = 432,
            Plan = plan, IsWindows = true, MainIsVertical = mainVertical, VerticalCanvas = second,
            VerticalPlan = second is null ? null : tall, InputOverride = Feed,
        });

        // widescreen on its own: nothing 9:16 anywhere
        var w = Build(wide, false, null);
        check(!w.HasVertical && !w.FilterGraph.Contains("vscene"),
            "canvas: widescreen on its own builds no 9:16 picture");

        // both: two canvases, each composited
        var b = Build(wide, false, (720, 1280));
        check(b.HasVertical && b.FilterGraph.Contains("[vscene0]") && b.FilterGraph.Contains("[scene0]"),
            "canvas: both builds the widescreen picture and the 9:16 one beside it");

        // 9:16 only: the program is the 9:16 picture, and there is no second canvas
        var v = Build(tall, true, null);
        check(!v.HasVertical && !v.FilterGraph.Contains("vscene") && v.FilterGraph.Contains("[scene0]"),
            "canvas: 9:16 only replaces the widescreen picture rather than adding to it");
        bool wideScale = v.FilterGraph.Contains(":1280") || v.FilterGraph.Contains("1280:720");
        check(!wideScale, $"…and nothing is composited at widescreen size{(wideScale ? " (found a 1280-wide stage)" : "")}");

        // the 9:16 program keeps its shape in the preview pane instead of being squashed into a widescreen one
        var tallPreview = System.Text.RegularExpressions.Regex.Match(v.FilterGraph, @"scale=(\d+):432:flags=bilinear,format=bgra\[hprev\]");
        int pw = tallPreview.Success ? int.Parse(tallPreview.Groups[1].Value) : 0;
        check(pw is > 230 and < 260, $"…and the preview pane is the same shape as the program ({pw}x432, expected about 243)");

        // it has to actually run, and what goes out has to be 9:16
        string ts = Path.Combine(dir, "vonly.ts");
        var live = PipelineBuilder.Build(new PipelineRequest
        {
            Scenes = new[] { scene }, ActiveSceneIndex = 0, Preview = false, Plan = tall, IsWindows = true,
            MainIsVertical = true, MainBusPath = ts, InputOverride = Feed,
        });
        var args = new List<string> { "-y", "-v", "error" };
        args.AddRange(live.Args);
        args.AddRange(new[] { "-t", "1" });
        var (code, log) = await EncoderCatalog.RunQuietAsync(ffmpeg, args, TimeSpan.FromSeconds(90), CancellationToken.None);
        string size = File.Exists(ts) ? await SizeAsync(ffmpeg, ts) : "";
        check(size == "720x1280", $"canvas: what goes out really is 9:16 ({(size == "" ? "nothing came out" : size)}) {log.Trim()}");
    }

    private static async Task<string> SizeAsync(string ffmpeg, string png)
    {
        var (_, log) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-v", "info", "-i", png, "-f", "null", "-" },
            TimeSpan.FromSeconds(30), CancellationToken.None);
        var m = System.Text.RegularExpressions.Regex.Match(log, @"(\d{3,4})x(\d{3,4})");
        return m.Success ? $"{m.Groups[1].Value}x{m.Groups[2].Value}" : "";
    }
}
