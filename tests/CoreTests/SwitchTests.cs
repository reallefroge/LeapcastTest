using System.Diagnostics;
using System.Globalization;
using LeapcastStudioAI;

namespace CoreTests;

/// <summary>
/// Scene switching on the real graph: the selectors and the single crossfade have to behave exactly like the
/// per-scene mask stack they replaced — switch to any scene, up or down, with in-between frames and no flash.
/// </summary>
public static class SwitchTests
{
    public static async Task RunAsync(string ffmpeg, string dir, Action<bool, string> check)
    {
        Directory.CreateDirectory(dir);
        var colours = new[] { "red", "lime", "blue" };
        var scenes = colours.Select((c, i) =>
        {
            var sc = new StudioScene { Name = c };
            sc.Sources.Add(new StudioSource { Name = c, Type = SourceTypes.Window, WindowTitle = c, Anchor = Anchors.Fill });
            return sc;
        }).ToArray();
        var plan = new EncodePlan(320, 180, 10, 800, 26, "libx264", EncoderTier.Rich, false, false, "ultrafast", "t", Array.Empty<string>());
        var spec = PipelineBuilder.Build(new PipelineRequest
        {
            Scenes = scenes, ActiveSceneIndex = 0, Preview = false, Plan = plan, IsWindows = false,
            SegmentPattern = null, MainBusPath = null,
            InputOverride = (s, _) => new InputSpec(new[] { "-re", "-f", "lavfi", "-i", $"color=c={s.WindowTitle}:s=320x180:r=10" }, false),
        });
        check(spec.FilterGraph.Contains("streamselect@sunder") && spec.FilterGraph.Contains("streamselect@sover")
              && System.Text.RegularExpressions.Regex.Matches(spec.FilterGraph, "alphamerge").Count == 1,
            $"switching: one crossfade for the whole program, not one per scene " +
            $"({System.Text.RegularExpressions.Regex.Matches(spec.FilterGraph, "alphamerge").Count} alphamerge, " +
            $"{System.Text.RegularExpressions.Regex.Matches(spec.FilterGraph, "overlay@").Count} overlays)");

        // the graph grows by a split and a selector input per scene, not by a mask + merge + overlay
        var six = Enumerable.Range(0, 6).Select(i =>
        {
            var sc = new StudioScene { Name = "s" + i };
            sc.Sources.Add(new StudioSource { Name = "s" + i, Type = SourceTypes.Window, WindowTitle = colours[i % 3], Anchor = Anchors.Fill });
            return sc;
        }).ToArray();
        var bigSpec = PipelineBuilder.Build(new PipelineRequest
        {
            Scenes = six, ActiveSceneIndex = 0, Preview = false, Plan = plan, IsWindows = false,
            InputOverride = (s, _) => new InputSpec(new[] { "-re", "-f", "lavfi", "-i", $"color=c={s.WindowTitle}:s=320x180:r=10" }, false),
        });
        int merges = System.Text.RegularExpressions.Regex.Matches(bigSpec.FilterGraph, "alphamerge").Count;
        int masks = System.Text.RegularExpressions.Regex.Matches(bigSpec.FilterGraph, "format=gray").Count;
        check(merges == 1 && masks == 1,
            $"…and six scenes cost the same switching machinery as two ({merges} alphamerge, {masks} mask)");

        // drive it: run the real graph, switch 0 → 2, then back down 2 → 1, and look at what came out
        string ts = Path.Combine(dir, "prog.ts");
        var live = PipelineBuilder.Build(new PipelineRequest
        {
            Scenes = scenes, ActiveSceneIndex = 0, Preview = false, Plan = plan, IsWindows = false,
            MainBusPath = ts,
            InputOverride = (s, _) => new InputSpec(new[] { "-re", "-f", "lavfi", "-i",
                $"color=c={s.WindowTitle}:s=320x180:r=10" }, false),
        });
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-v", "error" }) psi.ArgumentList.Add(a);
        foreach (var a in live.Args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEndAsync();
        bool alive = true;
        void Send(IEnumerable<string> lines)
        {
            if (!alive || proc.HasExited) { alive = false; return; }
            try { foreach (var l in lines) proc.StandardInput.Write("C" + l + "\n"); proc.StandardInput.Flush(); }
            catch { alive = false; }
        }
        await Task.Delay(2000);
        Send(PipelineBuilder.TransitionCommands(live, 0, 2, 3.0, 0.6));
        await Task.Delay(3500);
        Send(PipelineBuilder.TransitionCommands(live, 2, 1, 6.5, 0.6));
        await Task.Delay(3500);
        try { if (!proc.HasExited) { proc.StandardInput.Write("q\n"); proc.StandardInput.Flush(); } } catch { }
        if (!proc.WaitForExit(15000)) { try { proc.Kill(true); } catch { } }
        string log = (await err).Trim();

        // every frame of what was broadcast, in order
        string shots = Path.Combine(dir, "out_%04d.png");
        await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-y", "-v", "error", "-i", ts, "-vf", "crop=8:8:156:86,scale=1:1",
            "-f", "image2", shots }, TimeSpan.FromSeconds(60), CancellationToken.None);
        var seen = new List<(int N, string C, int R, int G, int B)>();
        foreach (var f in Directory.GetFiles(dir, "out_*.png").OrderBy(x => x))
        {
            var (r, g, b) = await PixelAsync(ffmpeg, f);
            seen.Add((int.Parse(Path.GetFileNameWithoutExtension(f)[4..], CultureInfo.InvariantCulture), Name(r, g, b), r, g, b));
        }
        string first = seen.FirstOrDefault().C ?? "none", last = seen.LastOrDefault().C ?? "none";
        int mids = seen.Count(x => x.C == "mixed");
        var order = seen.Select(x => x.C).Where(c => c != "mixed").Distinct().ToList();
        check(first == "red" && last == "green" && order.SequenceEqual(new[] { "red", "blue", "green" }),
            $"switching: red → blue → green on the real graph ({string.Join(" → ", order)}, {seen.Count} frames) {log}");
        check(mids >= 2, $"…as a crossfade, not a cut ({mids} blended frames over the two switches)");
        var flash = seen.FirstOrDefault(x => (x.R > 170 && x.G > 170 && x.B > 170) || (x.R + x.G + x.B) < 40);
        check(flash.C is null, "…never flashing through white or black" + (flash.C is null ? "" : $" (frame {flash.N}: {flash.R},{flash.G},{flash.B})"));
    }

    private static string Name(int r, int g, int b)
    {
        if (r > 120 && g < 70 && b < 70) return "red";
        if (g > 120 && r < 90 && b < 90) return "green";
        if (b > 120 && r < 70 && g < 70) return "blue";
        return "mixed";
    }

    private static async Task<(int R, int G, int B)> PixelAsync(string ffmpeg, string png)
    {
        string raw = png + ".rgb";
        await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-y", "-v", "error", "-i", png,
            "-pix_fmt", "rgb24", "-f", "rawvideo", raw }, TimeSpan.FromSeconds(20), CancellationToken.None);
        var b = File.ReadAllBytes(raw);
        return b.Length >= 3 ? (b[0], b[1], b[2]) : (0, 0, 0);
    }
}
