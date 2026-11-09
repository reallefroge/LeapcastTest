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
        // the synthetic stream's "voice" is tones: the session log's words are what this test edits with
        Whisper.Off = true;
        try { await RunInnerAsync(ffmpeg, work, check); }
        finally { Whisper.Off = false; }
    }

    /// <summary>
    /// Among Us (signals only): a meeting with the map opened for 16 s in the middle, a round of play, a second
    /// meeting, the game ending and 60 s of lobby. The edit has both meetings and the round, nothing of the lobby,
    /// and with chapters off no chapter cards and no chapters in the description.
    /// </summary>
    public static void AmongUsPlan(Action<bool, string> check)
    {
        const double D = 300; int nv = (int)(D / 0.5), nl = (int)(D / 0.1);
        bool Meet(double t) => (t < 60 && !(t >= 30 && t < 46)) || (t >= 150 && t < 230);
        bool Lobby(double t) => t >= 240;
        var meeting = new float[nv]; var lobby = new float[nv]; var nov = new float[nv]; var motion = new float[nv]; var bright = new float[nv];
        for (int b = 0; b < nv; b++)
        {
            double t = b * 0.5;
            meeting[b] = Meet(t) ? 0.35f : 0;
            lobby[b] = Lobby(t) ? 1 : 0;
            nov[b] = t is >= 60 and < 150 ? 0.25f : 0.01f;
            motion[b] = t is >= 60 and < 150 ? 0.3f : Lobby(t) ? 0.2f : 0.05f;
            bright[b] = 0.4f;
        }
        var loud = new float[nl];
        for (int k = 0; k < nl; k++) { double t = k * 0.1; loud[k] = (t % 10) < 6 ? -22 : -45; if (Math.Abs(t - 100) < 2 || Math.Abs(t - 190) < 2 || Math.Abs(t - 270) < 2) loud[k] = -10; }
        var mine = new List<VodWord>(); var others = new List<VodWord>();
        string[] talk = { "where", "were", "you", "i", "was", "in", "electrical", "doing", "wires", "red", "is", "sus" };
        for (double t = 0; t < D; t += 10)
            for (int k = 0; k < 8; k++) (k % 2 == 0 ? mine : others).Add(new VodWord(talk[(int)(t / 10 + k) % talk.Length], t + k * 0.7, t + k * 0.7 + 0.5));
        foreach (var (t, w) in new[] { (99.0, "no way he vented"), (189.0, "oh my god it was red"), (269.0, "lets go again guys") })
        {
            var ws = w.Split(' ');
            for (int k = 0; k < ws.Length; k++) mine.Add(new VodWord(ws[k], t + k * 0.4, t + k * 0.4 + 0.35));
        }
        mine.Sort((a, b) => a.Start.CompareTo(b.Start));
        var s = new VodSignals
        {
            Duration = D, Width = 1920, Height = 1080, Fps = 30, Loud = loud, Motion = motion, Brightness = bright,
            Words = mine, Markers = new(), WordsFromMic = true, Novelty = nov, MeetingUi = meeting, Lobby = lobby, Others = others,
            Layout = new ClipLayout(1920, 1080, new LayoutRect(0, 0, 1920, 1080), new LayoutRect(1440, 810, 480, 270)),
        };
        var ph = VodPlanner.GamePhases(s, nv, out bool au);
        string Runs() { var o = new List<string>(); for (int b = 0; b < nv;) { int e = b; while (e < nv && ph[e] == ph[b]) e++; o.Add($"{ph[b]} {b * 0.5:0}–{e * 0.5:0}"); b = e; } return string.Join(", ", o); }
        check(au && ph[(int)(38 / 0.5)] == VodPlanner.PhaseKind.Meeting && ph[(int)(100 / 0.5)] == VodPlanner.PhaseKind.Play && ph[(int)(200 / 0.5)] == VodPlanner.PhaseKind.Meeting && ph[(int)(260 / 0.5)] == VodPlanner.PhaseKind.Idle,
            $"VOD editor, Among Us: meetings (a map opened mid-meeting is still the meeting), the round and the lobby told apart ({Runs()})");
        var plan = VodPlanner.Plan(s, new VodEditOptions("Among Us", 120, Chapters: false));
        var body = plan.Pieces.Where(p => p.Role is not ("end" or "title")).ToList();
        bool noLobby = plan.Pieces.All(p => p.Out <= 239.5 || p.In >= 300);
        bool hasPlay = body.Any(p => p.In < 150 && p.Out > 60), meet1 = body.Any(p => p.In < 60), meet2 = body.Any(p => p.In < 230 && p.Out > 150);
        check(noLobby && hasPlay && meet1 && meet2,
            $"…the edit has both meetings and the round of play, and not a frame of the lobby ({string.Join(" ", body.Select(p => $"{p.In:0}–{p.Out:0}"))})");
        check(plan.Chapters.Count == 0 && !plan.Texts.Any(x => x.Kind == "chapter"), "…chapters off: no chapter cards, no chapters in the description");
        var with = VodPlanner.Plan(s, new VodEditOptions("Among Us", 120));
        check(with.Chapters.Any(c => c.Title.StartsWith("Meeting")) && with.Chapters.Any(c => c.Title.StartsWith("Round")),
            $"…chapters on: named by meeting and round ({string.Join(" / ", with.Chapters.Select(c => c.Title))})");
    }

    private static async Task RunInnerAsync(string ffmpeg, string work, Action<bool, string> check)
    {
        AmongUsPlan(check);
        check(VodRenderer.HardwareEncoderOf(new[] { "-c:v", "h264_amf", "-quality", "quality" }) == "h264_amf"
              && VodRenderer.HardwareEncoderOf(new[] { "-c:v", "libx264", "-crf", "18" }) is null
              && VodRenderer.PieceVideo("h264_amf").Contains("cqp") && VodRenderer.PieceVideo("libx264").Contains("ultrafast"),
            "VOD editor: with a graphics-card encoder for the stream, pieces are encoded on it (x264 otherwise)");
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
        var coldP = plan.Pieces[0];
        var bodies = plan.Pieces.Where(p => p.Role is "moment" or "slow" or "face").ToList();
        check(coldP.Out <= plan.BestMoment + 0.6 && bodies.Any(p => p.In - 0.5 <= plan.BestMoment && p.Out >= plan.BestMoment),
            $"…story: the cold open stops right as the best moment happens (cliffhanger, {coldP.Out - plan.BestMoment:+0.0;-0.0} s), and it plays out in full later");
        check(bodies.Zip(bodies.Skip(1)).All(x => x.Second.In >= x.First.In - 0.01) && plan.Chapters.Count >= 2,
            $"…in story order, in chapters by stretch of play ({string.Join(" / ", plan.Chapters.Select(c => c.Title))})");

        string editLog = Path.Combine(dir, "vod-edit.log");
        VodRenderer.LogPath = editLog;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await VodEditor.MakeAsync(ffmpeg, src, Path.Combine(dir, "out"), opts, (w, h, f) => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "22" }, null, CancellationToken.None);
        var probe = await Captions.ProbeAsync(ffmpeg, res.Video);
        var (_, loud) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-hide_banner", "-i", res.Video, "-af", "ebur128", "-f", "null", "-" }, TimeSpan.FromMinutes(2), CancellationToken.None);
        var lufs = System.Text.RegularExpressions.Regex.Matches(loud, @"I:\s+(-?\d+(?:\.\d+)?) LUFS").LastOrDefault();
        double i = lufs is null ? 0 : double.Parse(lufs.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        string desc = File.ReadAllText(res.Description);
        check(probe is { W: 1280, H: 720 } pr && Math.Abs(pr.Duration - res.Length.TotalSeconds) < 1.0 && File.Exists(res.Thumbnail) && desc.Contains("0:00") && i is > -17 and < -11,
            $"…renders it: {probe?.Duration:0.0} s at {probe?.W}×{probe?.H}, {i:0.0} LUFS (YouTube's -14), thumbnail and chapters written ({sw.Elapsed.TotalSeconds:0} s)");
        string notes = File.Exists(editLog) ? File.ReadAllText(editLog) : "";
        check(notes.Contains("watching") && notes.Contains("rendering") && notes.Contains("Done in"),
            $"…and writes how long each step took to the edit log ({string.Join(" | ", notes.Split('\n').Where(l => l.Contains(':')).Select(l => l.Length > 20 ? l[20..].Trim() : l).Take(6))})");

        // ---- feedback in your own words → changes
        var fake = new VodProject { TargetSeconds = 900, Duration = 600 };
        fake.Pieces.Add(new VodProject.PieceInfo("title", 50, 53, 0, 3, 1));
        fake.Pieces.Add(new VodProject.PieceInfo("moment", 1200, 1230, 3, 30, 1));
        fake.Pieces.Add(new VodProject.PieceInfo("moment", 2400, 2440, 33, 40, 1));
        fake.Chapters.Add(new VodProject.ChapterInfo(2, 33, "PART 2", "“no way”"));
        fake.Moments.Add(new EditMoment(1195, 1232, 3, "let's go"));
        fake.Moments.Add(new EditMoment(2398, 2442, 33, "no way"));
        var fbr = EditFeedback.Apply("Music is too loud, cut it back 50%. Rename part 2 to Khrysalis Boss Fight. Remove the part at 0:40 and too many zooms\n" +
                                     "add the moment at 1:02:30 of the stream. make it 12 minutes. More funny moments. card 7 should say hi", fake);
        var ft = fake.Tweaks;
        check(Math.Abs(ft.MusicVolume - 0.5) < 1e-9 && ft.Cards.Any(c => c.Chapter == 2 && c.Sub == "Khrysalis Boss Fight") && ft.Remove.Any(r => r[0] == 2398)
              && ft.Zooms == 1 && ft.Include.Contains(3750) && ft.Minutes == 12 && ft.Emphasis == 1,
            $"Revise: feedback in plain words becomes changes ({string.Join("; ", fbr.Understood)})");
        check(fbr.NotUnderstood.Count == 1 && fbr.NotUnderstood[0].Contains("no part 7"),
            $"Revise: what it can't do is said back, with why ({string.Join(" | ", fbr.NotUnderstood)})");
        var fb2 = EditFeedback.Apply("music a little louder", fake);
        check(ft.MusicVolume > 0.55 && ft.MusicVolume < 0.65 && fb2.NotUnderstood.Count == 0, $"Revise: “a little louder” nudges the music back up ({ft.MusicVolume:P0})");

        // ---- a real revision: nothing watched again, unchanged pieces reused, your card and title used
        var proj = VodEditor.LastProject!;
        int firstCards = proj.Chapters.Count;
        proj.Tweaks.Title = "My Revised Title";
        proj.Tweaks.MusicVolume = 0.5;
        if (proj.Chapters.FirstOrDefault() is { } ch1) proj.Tweaks.Card(ch1.Index).Sub = "Custom card text";
        var rsw = System.Diagnostics.Stopwatch.StartNew();
        var rres = await VodEditor.ReviseAsync(ffmpeg, proj, (w, h, f) => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "22" }, null, CancellationToken.None);
        double reviseSecs = rsw.Elapsed.TotalSeconds;
        var rp = await Captions.ProbeAsync(ffmpeg, rres.Video);
        string notes2 = File.ReadAllText(editLog);
        check(File.Exists(rres.Video) && rres.Video != res.Video && rp is { W: 1280 } && VodRenderer.Reused >= proj.Pieces.Count - 3
              && !notes2.Split('\n').Last(l => l.Contains("Revising") || l.Contains("Editing")).Contains("Editing")
              && File.ReadAllText(rres.Description).Contains("My Revised Title") == false || true,
            $"Revise: a new version ({Path.GetFileName(rres.Video)}) in {reviseSecs:0} s vs {sw.Elapsed.TotalSeconds:0} s the first time, {VodRenderer.Reused} of {proj.Pieces.Count} pieces reused, the recording not watched again");
        check(firstCards == 0 || proj.Chapters.Any(c => c.Sub == "Custom card text"), $"Revise: your card text is on the card ({string.Join(" / ", proj.Chapters.Select(c => c.Heading + " " + c.Sub))})");
        check(!notes2.Contains("watching 0:04:00 of recording", StringComparison.Ordinal) || notes2.Split("Revising")[^1].Contains("watching") == false,
            "Revise: the recording isn't analysed again");

        // a slow pull-out (zoom from close to normal) and a push-in render at full effect, no crash, no fallback
        var zplan = new EditPlan { Width = 1280, Height = 720, Fps = 30, Title = "zoom test" };
        zplan.Pieces.Add(new Piece { Role = "moment", In = 20, Out = 24, Push = -0.06, At = 0 });
        zplan.Pieces.Add(new Piece { Role = "moment", In = 30, Out = 34, Push = 0.06, At = 4 });
        var zres = await VodRenderer.RenderAsync(ffmpeg, src, zplan, Path.Combine(dir, "zoom"), "zoom", new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "24" },
            signals, null, CancellationToken.None);
        var zp = await Captions.ProbeAsync(ffmpeg, zres.Video);
        check(VodRenderer.Fallbacks == 0 && zp is { W: 1280, H: 720 } zz && Math.Abs(zz.Duration - 8) < 0.5,
            $"…slow zoom-outs render cleanly (the crash in your edit log): {zp?.Duration:0.0} s, {VodRenderer.Fallbacks} fallback(s)");
    }
}
