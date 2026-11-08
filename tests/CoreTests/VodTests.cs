using System.Diagnostics;
using LeapcastStudioAI;
using LeapcastStudioAI.Vod;

/// <summary>A synthetic "stream": moving picture, a facecam box, a loading screen, talking and loud reactions,
/// a saved clip and a death, with the session log Leapcast would have written.</summary>
static class VodTests
{
    public static async Task MakeSyntheticStreamAsync(string ffmpeg, string path, double seconds = 240, bool sidecar = true, bool audio = true, int size = 720)
    {
        // talking every 20 s for 9 s; loud reactions at 100, 160 and 215 s; a loading screen 60–90 s
        string voice = $"if(lt(mod(t,20),9),0.12,0)+if(lt(abs(t-100),2.5)+lt(abs(t-160),2.5)+lt(abs(t-215),2.5)+lt(abs(t-{seconds - 2}),2),0.55,0)";
        int vw = size * 16 / 9, vh = size;
        string aexpr = $"0.015*(random(0)-0.5)+({voice})*sin(2*PI*210*t)*(0.55+0.45*sin(2*PI*5*t))";
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-y", "-v", "error",
            "-f", "lavfi", "-i", $"testsrc2=s={vw}x{vh}:r={(size >= 1080 ? 60 : 30)}:d={seconds}",
            "-f", "lavfi", "-i", $"color=c=0x101830:s={vw}x{vh}:r={(size >= 1080 ? 60 : 30)}:d={seconds}",
            "-f", "lavfi", "-i", $"aevalsrc='{aexpr}':s=48000:d={seconds}",
            "-filter_complex", $"[0:v][1:v]overlay=enable='between(t,60,90)'[g];[g]drawbox=x={vw * 3 / 4}:y={vh - vh / 4}:w={vw / 4}:h={vh / 4}:color=red:t=fill[v]",
            "-map", "[v]" }.Concat(audio ? new[] { "-map", "2:a", "-c:a", "aac" } : Array.Empty<string>())
            .Concat(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28", "-g", "60", "-shortest", path }))
            psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!) { var e = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); if (p.ExitCode != 0) throw new Exception(await e); }

        if (!sidecar) { File.Delete(VodSidecar.PathFor(path)); return; }
        // the camera in the bottom-right corner, running a few pixels past the canvas edge (as layouts can)
        var side = new VodSidecar { Layout = new ClipLayout(1920, 1080, new LayoutRect(0, 0, 1920, 1080), new LayoutRect(1440, 810, 490, 280)) };
        string[] chatter = { "okay", "so", "we", "need", "to", "get", "the", "key", "first", "then", "go", "left" };
        for (double t = 0; t < seconds; t += 20)
            for (int k = 0; k < 9; k++) side.Words.Add(new VodWord(chatter[(int)(t / 20 + k) % chatter.Length], t + 0.3 + k * 0.85, t + 0.3 + k * 0.85 + 0.5));
        void Say(double t, params string[] w) { for (int k = 0; k < w.Length; k++) side.Words.Add(new VodWord(w[k], t + k * 0.4, t + k * 0.4 + 0.35)); }
        Say(99, "no", "way", "that", "just", "happened");
        Say(159, "lets", "go", "clutch");
        Say(214, "oh", "my", "god", "no");
        side.Words.Sort((a, b) => a.Start.CompareTo(b.Start));
        side.Markers.Add(new VodMarker("clip", 162, "voice"));
        side.Markers.Add(new VodMarker("fight", 200));
        side.Markers.Add(new VodMarker("death", 216));
        for (int k = 0; k < 15; k++) side.Markers.Add(new VodMarker("chat", 158 + k * 0.5));
        side.Save(path);
    }

    public static async Task RunAsync(string ffmpeg, string work, Action<bool, string> check)
    {
        string dir = Path.Combine(work, "vod");
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "stream.mp4");
        await MakeSyntheticStreamAsync(ffmpeg, src);
        var signals = await VodAnalyzer.AnalyzeAsync(ffmpeg, src, null, CancellationToken.None);
        check(Math.Abs(signals.Duration - 240) < 1 && signals.Loud.Length > 2000 && signals.Motion.Length > 400 && signals.WordsFromMic,
            $"VOD editor: reads a 4-minute recording (sound level every 0.1 s, motion every 0.5 s, the session log's words and events)");
        var opts = new VodEditOptions("Boss Rush", 70, Socials: "twitch.tv/example");
        var plan = VodPlanner.Plan(signals, opts);
        var roles = string.Join(",", plan.Pieces.Select(p => p.Role).Distinct());
        bool skipsLoading = !plan.Pieces.Any(p => p.Role is "moment" or "slow" or "face" && p.In < 88 && p.Out > 62);
        check(plan.Pieces[0] is { Role: "cold", Letterbox: true } && plan.Pieces[1] is { Role: "title", Mute: true } && plan.Pieces[^1] is { Role: "end", Hold: > 0 } && skipsLoading,
            $"…story: cold open (letterboxed), title, the moments in order, end card; the loading screen is left out ({roles})");
        var reacts = plan.Pieces.Where(p => p.Role == "moment").SelectMany(p => p.Zooms).Count();
        check(plan.Pieces.Any(p => p.Role == "slow" && p.In < 216 && p.Out > 216) && plan.Pieces.Any(p => p.Role == "face") && reacts > 0,
            $"…the death is in slow motion, a reaction cuts to your face, and reactions get punch-ins ({reacts})");
        check(plan.Music.Count >= 1 && plan.Music.Any(m => m.TapeStopAtEnd) && plan.Captions.Count > 20 && plan.Sfx.Any(x => x.Kind == SfxKind.Impact),
            $"…original music ({string.Join(", ", plan.Music.Select(m => m.Mood).Distinct())}) that tape-stops at the death, {plan.Captions.Count} caption words, {plan.Sfx.Count} sound effects");
        check(plan.Duration is > 50 and < 100, $"…close to the asked length (70 s asked: {plan.Duration:0} s)");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await VodEditor.MakeAsync(ffmpeg, src, Path.Combine(dir, "out"), opts, (w, h, f) => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "22" }, null, CancellationToken.None);
        var probe = await Captions.ProbeAsync(ffmpeg, res.Video);
        var (_, loud) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-hide_banner", "-i", res.Video, "-af", "ebur128", "-f", "null", "-" }, TimeSpan.FromMinutes(2), CancellationToken.None);
        var lufs = System.Text.RegularExpressions.Regex.Matches(loud, @"I:\s+(-?\d+(?:\.\d+)?) LUFS").LastOrDefault();
        double i = lufs is null ? 0 : double.Parse(lufs.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        string desc = File.ReadAllText(res.Description);
        check(probe is { W: 1280, H: 720 } pr && Math.Abs(pr.Duration - res.Length.TotalSeconds) < 1.0 && File.Exists(res.Thumbnail) && desc.Contains("0:00") && i is > -17 and < -11,
            $"…renders it: {probe?.Duration:0.0} s at {probe?.W}×{probe?.H}, {i:0.0} LUFS (YouTube's -14), thumbnail and chapters written ({sw.Elapsed.TotalSeconds:0} s)");
    }
}
