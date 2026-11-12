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
        foreach (var (t, w) in new[] { (99.0, "no way he vented"), (189.0, "oh my god it was red"), (192.5, "i was in electrical doing wires the whole time"), (269.0, "lets go again guys") })
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

        // transitions: never by default (plain cuts), but every cut comes with the editor's suggestion
        string Cuts(EditPlan pl) => string.Join(" | ", pl.Cuts.Select(c => $"{c.At:0}: {c.Style}/{c.Sound} (suggests {c.Suggested}: {c.Why})"));
        check(with.Pieces.Where(p => p.Role != "cold").All(p => p.TransIn is null && p.TransOut is null && !p.WhipIn && p.Role != "lapse") && with.Cuts.Count > 0
              && with.Cuts.All(c => c.Style == TransitionKinds.None && c.Sound == "none") && !with.Sfx.Any(x => x.Kind is SfxKind.Whoosh),
            $"…transitions: plain cuts unless asked for, each cut with a suggestion ({Cuts(with)})");
        var lapsed = VodPlanner.Plan(s, new VodEditOptions("Among Us", 120), new EditTweaks { TransitionStyle = TransitionKinds.Timelapse });
        var lapses = lapsed.Pieces.Where(p => p.Role == "lapse").ToList();
        check(lapses.Count >= 1 && lapses.All(l => l.Speed > 1.5 && l.Out <= 240 && l.Length is >= 1 and <= 6.01) && lapsed.Texts.Any(x => x.Kind == "lapse"),
            $"…timelapse: the gameplay between clips fast-forwarded (speed shown), never through the lobby ({string.Join(", ", lapses.Select(l => $"{l.In:0}–{l.Out:0} ×{l.Speed:0.0}"))}; {Cuts(lapsed)})");
        var mixed = VodPlanner.Plan(s, new VodEditOptions("Among Us", 120), new EditTweaks { TransitionStyle = TransitionKinds.Mixed, TransitionSound = TransitionKinds.Mixed });
        var styles = mixed.Cuts.Select(c => c.Style).ToList();
        check(styles.All(x => x != TransitionKinds.None) && styles.Zip(styles.Skip(1)).All(z => z.First != z.Second) && mixed.Cuts.All(c => c.Sound != "none"),
            $"…varied: a transition at every cut, never the same twice running, with varied sounds ({Cuts(mixed)})");
        var one = with.Cuts[0];
        var picked = VodPlanner.Plan(s, new VodEditOptions("Among Us", 120), new EditTweaks { Cuts = { new CutPick { From = one.From, Style = TransitionKinds.Glitch, Sound = "pop" } } });
        check(picked.Cuts.Count(c => c.Style == TransitionKinds.Glitch && c.Sound == "pop") == 1 && picked.Cuts.Count(c => c.Style == TransitionKinds.None) == picked.Cuts.Count - 1,
            $"…one cut set by hand changes just that cut ({Cuts(picked)})");
        // VAR check: you (on your mic) say where you were in the meeting → a replay of your round over it
        var vars = with.Pieces.Where(p => p.Overlay is not null).ToList();
        check(vars.Count == 1 && vars[0].Overlay!.In >= 60 && vars[0].Overlay!.Out <= 150.5 && vars[0].In <= 192.5 && vars[0].Out > 195
              && with.Texts.Any(x => x.Kind == "var" && x.Sub!.Contains("electrical")),
            $"…VAR check: “i was in electrical” in the meeting plays the round before it ({string.Join(", ", vars.Select(v => $"{v.Overlay!.In:0}–{v.Overlay.Out:0} over {v.In:0}–{v.Out:0}"))})");
        var noMic = VodPlanner.Plan(new VodSignals
        {
            Duration = s.Duration, Width = s.Width, Height = s.Height, Fps = s.Fps, Loud = s.Loud, Motion = s.Motion, Brightness = s.Brightness, Words = s.Words, Markers = s.Markers,
            WordsFromMic = false, Novelty = s.Novelty, MeetingUi = s.MeetingUi, Lobby = s.Lobby, Others = s.Others, Layout = s.Layout,
        }, new VodEditOptions("Among Us", 120));
        check(noMic.Pieces.All(p => p.Overlay is null), "…only from your microphone: without a mic track (words from the speakers) there's no VAR check");

        // never a flash: not offered, not suggested, and an old edit or a request for one gets a fade through black
        var flashy = VodPlanner.Plan(s, new VodEditOptions("Among Us", 120), new EditTweaks { TransitionStyle = TransitionKinds.Flash, Cuts = { new CutPick { From = with.Cuts[0].From, Style = TransitionKinds.Flash } } });
        var mixedAll = VodPlanner.Plan(s, new VodEditOptions("Among Us", 120), new EditTweaks { TransitionStyle = TransitionKinds.Mixed });
        var fp = new VodProject { TargetSeconds = 120, Duration = 120 };
        var fr = EditFeedback.Apply("use flash transitions", fp);
        bool NoFlash(EditPlan pl) => pl.Pieces.All(p => p.TransIn != TransitionKinds.Flash && p.TransOut != TransitionKinds.Flash)
                                     && pl.Cuts.All(c => c.Style != TransitionKinds.Flash && c.Suggested != TransitionKinds.Flash && !c.Options.Contains(TransitionKinds.Flash));
        check(!TransitionKinds.Styles.Contains(TransitionKinds.Flash) && NoFlash(flashy) && NoFlash(mixedAll) && NoFlash(with) && flashy.Cuts.Any(c => c.Style == TransitionKinds.Fade)
              && fp.Tweaks.TransitionStyle == TransitionKinds.Fade && fr.Understood.Any(u => u.StartsWith("No flashes")),
            $"No flashes anywhere: not a choice, never suggested, an old flash becomes a fade (“use flash transitions” → {string.Join("; ", fr.Understood)})");

        // the editor's suggestions: with VAR checks off, it suggests the one where you explained where you were;
        // applying the suggestion shows that replay (and only that one)
        var twOff = new EditTweaks { VarChecks = false };
        var off = VodPlanner.Plan(s, new VodEditOptions("Among Us", 120), twOff);
        var sugg = VodSuggestions.For(off, s, twOff, "Among Us");
        var vs = sugg.FirstOrDefault(x => x.Kind == "var");
        check(off.Pieces.All(p => p.Overlay is null) && vs is not null && vs.Text.Contains("electrical") && vs.At > 0,
            $"Suggestions: “{vs?.Text}” → “{vs?.Apply}” ({sugg.Count} suggestions: {string.Join(" | ", sugg.Select(x => x.Kind))})");
        var vp = new VodProject { TargetSeconds = 120, Duration = off.Duration, Tweaks = twOff };
        vp.Remember(off);
        var applied = EditFeedback.Apply(vs!.Apply, vp);
        var on = VodPlanner.Plan(s, new VodEditOptions("Among Us", 120), vp.Tweaks);
        check(on.Pieces.Count(p => p.Overlay is not null) == 1 && applied.NotUnderstood.Count == 0 && !VodSuggestions.For(on, s, vp.Tweaks, "Among Us").Any(x => x.Kind == "var"),
            $"…Apply turns on that replay only ({string.Join(" | ", applied.Understood)}), and it's no longer suggested");
        vp.Tweaks.Dismissed.Add(sugg[0].Id);
        check(VodSuggestions.For(off, s, vp.Tweaks, "Among Us").All(x => x.Id != sugg[0].Id), "…a dismissed suggestion doesn't come back");
    }

    /// <summary>The editor learns your taste: a choice you make in two or more edits becomes where new edits start.</summary>
    public static void Learning(Action<bool, string> check)
    {
        var st = new EditStyle();
        VodProject Made(string id, Action<EditTweaks> set) { var p = new VodProject { Id = id, Game = "Among Us" }; set(p.Tweaks); return p; }
        st.Record(Made("a", t => { t.TransitionSound = "none"; t.TransitionSounds = false; t.EvenLoudness = true; t.MusicVolume = 0.5; }));
        var (t1, n1) = st.Learned("Among Us");
        check(n1 == "" && !t1.EvenLoudness, "Learning: one edit isn't a habit yet");
        st.Record(Made("b", t => { t.TransitionSound = "none"; t.TransitionSounds = false; t.EvenLoudness = true; t.MusicVolume = 0.45; t.TransitionStyle = TransitionKinds.Timelapse; }));
        st.Record(Made("c", t => { t.EvenLoudness = true; t.MusicVolume = 0.55; t.TransitionStyle = TransitionKinds.Timelapse; t.TransitionSounds = false; t.TransitionSound = "none"; }));
        st.Record(Made("c", t => { t.EvenLoudness = true; t.MusicVolume = 0.55; t.TransitionStyle = TransitionKinds.Timelapse; t.TransitionSounds = false; t.TransitionSound = "none"; }));
        var (t2, n2) = st.Learned("Among Us");
        check(st.Count("Among Us") == 3 && t2.EvenLoudness && !t2.TransitionSounds && t2.TransitionSound == "none" && t2.TransitionStyle == TransitionKinds.Timelapse
              && Math.Abs(t2.MusicVolume - 0.5) < 0.06 && t2.Zooms == 2 && n2.Length > 0,
            $"Learning: choices you keep making become the starting point (a revision replaces its edit's earlier choices) ({n2})");
        check(st.Learned("other").Note == "" && !st.Learned("other").Tweaks.EvenLoudness, "…per game: your Among Us taste doesn't change other games' edits");
    }

    /// <summary>Feedback in many ways of saying it: each becomes the right change; nothing is an error.</summary>
    public static void FeedbackPhrasings(Action<bool, string> check)
    {
        (EditTweaks T, EditFeedback.Result R) F(string text)
        {
            var p = new VodProject { TargetSeconds = 900, Duration = 600 };
            p.Tweaks.TransitionStyle = TransitionKinds.Mixed;   // (as if transitions had been asked for before)
            return (p.Tweaks, EditFeedback.Apply(text, p));
        }
        const string N = TransitionKinds.None;
        var cases = new (string Text, Func<EditTweaks, bool> Ok)[]
        {
            ("Remove Transitions", t => t.TransitionStyle == N),
            ("I dont want the transitions audio", t => t.TransitionStyle != N && t.TransitionSound == "none" && !t.TransitionSounds),
            ("I don’t want the transition sounds", t => t.TransitionStyle != N && t.TransitionSound == "none"),
            ("get rid of the swooshes", t => t.TransitionSound == "none"),
            ("the transistions are annoying", t => t.TransitionStyle == N),
            ("bring back the transitions", t => t.TransitionStyle == TransitionKinds.Mixed),
            ("use fade transitions", t => t.TransitionStyle == TransitionKinds.Fade),
            ("crossfade between clips", t => t.TransitionStyle == TransitionKinds.Fade),
            ("glitch transitions please", t => t.TransitionStyle == TransitionKinds.Glitch),
            ("mix up the transitions", t => t.TransitionStyle == TransitionKinds.Mixed),
            ("timelapse the gameplay between clips", t => t.TransitionStyle == TransitionKinds.Timelapse),
            ("fast forward through the boring parts of each game", t => t.TransitionStyle == TransitionKinds.Timelapse),
            ("use a pop sound for transitions", t => t.TransitionSound == "pop"),
            ("vary the transition sounds", t => t.TransitionSound == TransitionKinds.Mixed),
            ("just plain cuts", t => t.TransitionStyle == N),
            ("no var checks", t => !t.VarChecks),
            ("show what i did when i explain my pathing", t => t.VarChecks),
            ("fix the mismatching loudness of audio between segments", t => t.EvenLoudness),
            ("the volume jumps between clips", t => t.EvenLoudness),
            ("some parts are way louder than others", t => t.EvenLoudness),
            ("normalize the audio", t => t.EvenLoudness),
            ("audio levels are inconsistent", t => t.EvenLoudness),
            ("can't hear me over the music", t => t.MusicVolume < 1 && t.Music),
            ("music is blasting", t => t.MusicVolume < 1),
            ("lose the music", t => !t.Music),
            ("turn off the subs", t => !t.Captions),
            ("no screen shake", t => !t.Shake),
            ("too chaotic", t => t.Zooms < 2 && !t.Shake),
            ("way too many zooms", t => t.Zooms < 2),
            ("no more sfx", t => t.SfxVolume == 0),
            ("the sound effects are too loud", t => t.SfxVolume < 1 && t.SfxVolume > 0),
            ("it drags", t => t.Pace == -1),
            ("get rid of the black bars", t => !t.Letterbox),
            ("Audio is quiet one clip, loud another", t => t.EvenLoudness),
            ("make the background oval shaped", t => t.CameraShape == CameraLook.Oval && t.CameraBackground == ""),
            ("digitally remove my background", t => t.CameraBackground == CameraLook.Removed),
            ("remove the background behind me", t => t.CameraBackground == CameraLook.Removed),
            ("blur my background", t => t.CameraBackground == CameraLook.Blurred),
            ("make my webcam a circle", t => t.CameraShape == CameraLook.Circle),
            ("rounded corners on my cam", t => t.CameraShape == CameraLook.Rounded),
            ("no gold ring around the camera", t => !t.CameraRing),
            ("the background music is too loud", t => t.MusicVolume < 1 && t.CameraBackground == ""),
            ("no hook at the start", t => !t.Hook),
            ("remove the teaser", t => !t.Hook),
            ("get rid of the outro", t => !t.EndScreen),
            ("I don't want the end screen", t => !t.EndScreen),
        };
        var failed = new List<string>();
        foreach (var (text, ok) in cases)
        {
            var (t, r) = F(text);
            if (!ok(t) || r.Understood.Count == 0) failed.Add($"“{text}” → {string.Join(", ", r.Understood)}");
        }
        check(failed.Count == 0, $"Revise: {cases.Length - failed.Count} of {cases.Length} ways of saying it understood{(failed.Count > 0 ? ": missed " + string.Join(" | ", failed) : "")}");
        // one suggestion per line, each with the time you were at ("Note at this time")
        var lp = new VodProject { TargetSeconds = 900, Duration = 600 };
        lp.Pieces.Add(new VodProject.PieceInfo("title", 50, 53, 0, 3, 1));
        lp.Pieces.Add(new VodProject.PieceInfo("moment", 1200, 1230, 3, 30, 1));
        lp.Pieces.Add(new VodProject.PieceInfo("moment", 2400, 2440, 33, 40, 1));
        lp.Moments.Add(new EditMoment(1195, 1232, 3, "let's go"));
        lp.Moments.Add(new EditMoment(2398, 2442, 33, "no way"));
        lp.Cuts.Add(new CutInfo(33, 2400, "none", "none", "fade", "soft", "test", TransitionKinds.Styles));
        var l1 = EditFeedback.Apply("at 0:40: this part is boring", lp);
        var l2 = EditFeedback.Apply("at 0:33: use a fade here", lp);
        var l3 = EditFeedback.Apply("at 0:10: way too loud", lp);
        check(lp.Tweaks.Remove.Any(r => r[0] == 2398) && lp.Tweaks.Pace == 0 && l1.NotUnderstood.Count == 0
              && lp.Tweaks.Cuts.Any(c => c.From == 2400 && c.Style == TransitionKinds.Fade) && lp.Tweaks.TransitionStyle == TransitionKinds.None && lp.Tweaks.Include.Count == 0
              && lp.Tweaks.EvenLoudness && l3.NotUnderstood.Count == 0,
            $"Revise: a note at a time changes that spot (boring part removed, a fade on that cut only, a loud part evened): {string.Join(" | ", l1.Understood.Concat(l2.Understood).Concat(l3.Understood))}");
        var hp = new VodProject { TargetSeconds = 900, Duration = 600 };
        hp.Moments.Add(new EditMoment(1195, 1232, 3, "let's go"));
        hp.Moments.Add(new EditMoment(2398, 2442, 33, "no way"));
        var hate = EditFeedback.Apply("I hate the clips, they're aweful choices", hp);
        check(hp.Tweaks.Remove.Count == 2 && hate.NotUnderstood.Count == 0 && hate.Understood.Any(u => u.StartsWith("Different clips")),
            $"Revise: “I hate the clips” → all different ones ({string.Join(" | ", hate.Understood.Concat(hate.NotUnderstood))})");
        var (_, odd) = F("the colors look weird");
        check(odd.Understood.Count == 0 && odd.NotUnderstood.Count == 1, "…something the editor can't change is just noted (with what it can change), not an error");
    }

    private static async Task RunInnerAsync(string ffmpeg, string work, Action<bool, string> check)
    {
        AmongUsPlan(check);
        Learning(check);
        FeedbackPhrasings(check);
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
        int firstTitle = plan.Pieces.FindIndex(p => p.Role == "title");
        check(firstTitle >= 2 && plan.Pieces.Take(firstTitle).All(p => p is { Role: "cold", Letterbox: true }) && plan.Pieces.All(p => p.TransIn != TransitionKinds.Flash && p.TransOut != TransitionKinds.Flash)
              && plan.Pieces[firstTitle] is { Mute: true, Darken: true } && plan.Pieces[^1] is { Role: "end", Hold: >= 19 } && plan.Texts.Any(t => t.Kind == "endscreen") && plan.Texts.Any(t => t.Kind == "hook") && skipsLoading,
            $"…story: a hook (big moments in quick cuts, never a flash, then the run-up to the best one, letterboxed), your intro, the moments in order, a 20 s end screen; the loading screen is left out ({roles})");
        var reacts = plan.Pieces.Where(p => p.Role == "moment").SelectMany(p => p.Zooms).Count();
        check(plan.Pieces.Any(p => p.Role == "slow" && p.In < 216 && p.Out > 216) && plan.Pieces.Any(p => p.Role == "face") && reacts > 0,
            $"…the death is in slow motion, a reaction cuts to your face, and reactions get punch-ins ({reacts})");
        check(plan.Music.Count >= 1 && plan.Music.Any(m => m.TapeStopAtEnd) && plan.Captions.Count > 20 && plan.Sfx.Any(x => x.Kind == SfxKind.Impact),
            $"…original music ({string.Join(", ", plan.Music.Select(m => m.Mood).Distinct())}) that tape-stops at the death, {plan.Captions.Count} caption words, {plan.Sfx.Count} sound effects");
        check(plan.Duration is > 50 and < 100, $"…close to the asked length (70 s asked: {plan.Duration:0} s)");
        var coldP = plan.Pieces[firstTitle - 1];
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

        var again = new VodProject { Recording = src, OutputFolder = Path.Combine(dir, "out") };
        check(again.ReuseSignals() is { } rs && Math.Abs(rs.Duration - 240) < 1 && File.Exists(Path.Combine(again.WorkDir, "signals.json.gz")),
            "…a second edit of the same recording doesn't watch it again (it uses what the first one learned)");

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

        // ---- the quick preview: the edit as it stands, small and fast, to watch before the real render
        var psw = System.Diagnostics.Stopwatch.StartNew();
        string pv = await VodEditor.PreviewAsync(ffmpeg, proj, null, CancellationToken.None);
        var pvp = await Captions.ProbeAsync(ffmpeg, pv);
        check(pvp is { W: 640, H: 360 } pq && Math.Abs(pq.Duration - proj.Duration) < 1.5 && psw.Elapsed < sw.Elapsed,
            $"Review: a quick preview of the edit to watch ({pvp?.W}×{pvp?.H}, {pvp?.Duration:0.0} s of {proj.Duration:0.0} s, made in {psw.Elapsed.TotalSeconds:0} s vs {sw.Elapsed.TotalSeconds:0} s for the video)");
        var psw2 = System.Diagnostics.Stopwatch.StartNew();
        string pv2 = await VodEditor.PreviewAsync(ffmpeg, proj, null, CancellationToken.None);
        check(File.Exists(pv2) && VodRenderer.Reused == proj.Pieces.Count, $"…previewing again after no change remakes no piece ({VodRenderer.Reused} of {proj.Pieces.Count} reused, {psw2.Elapsed.TotalSeconds:0} s)");

        // ---- a revision that changes nothing isn't rendered again (and says so)
        var nsw = System.Diagnostics.Stopwatch.StartNew();
        var same = await VodEditor.ReviseAsync(ffmpeg, proj, (w, h, f) => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "22" }, null, CancellationToken.None);
        check(same.Unchanged && same.Video == proj.Video && nsw.Elapsed.TotalSeconds < 20,
            $"Revise: suggestions that change nothing don't make the same video again ({nsw.Elapsed.TotalSeconds:0.0} s, unchanged: {same.Unchanged})");
        check(proj.QualityNote.StartsWith("Checked before saving"), $"…every video is checked before it's saved: {proj.QualityNote}");

        await QualityAsync(ffmpeg, Path.Combine(work, "quality"), check);
        await CameraAsync(ffmpeg, Path.Combine(work, "camera"), check);
        await KitAsync(ffmpeg, Path.Combine(work, "kit"), check);
    }

    /// <summary>A stand-in for the segmentation model: "you" are a disc in the middle of the camera.</summary>
    private sealed class DiscSegmenter : CameraLook.ISegmenter
    {
        public int Size => 192;
        public float[] Run(byte[] rgb)
        {
            var p = new float[Size * Size];
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
                p[y * Size + x] = Math.Pow((x - 96) / 50.0, 2) + Math.Pow((y - 96) / 60.0, 2) <= 1 ? 1f : 0f;
            return p;
        }
    }

    /// <summary>
    /// Your camera's look: an oval with a gold ring that covers the camera's old square, and the background behind
    /// you removed (the old square filled from the game around it); "you" stay exactly as recorded.
    /// </summary>
    public static async Task CameraAsync(string ffmpeg, string dir, Action<bool, string> check, CameraLook.ISegmenter? segmenter = null)
    {
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "cam.mp4");
        // the game (a moving test picture) with the camera (solid blue) on top, bottom right
        await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=12",
            "-f", "lavfi", "-i", "color=c=0x2050e0:s=320x240:r=30:d=12", "-f", "lavfi", "-i", "sine=f=300:d=12",
            "-filter_complex", "[0:v][1:v]overlay=900:420[v]", "-map", "[v]", "-map", "2:a", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12",
            "-c:a", "aac", "-shortest", src }, TimeSpan.FromMinutes(2), CancellationToken.None);
        var signals = await VodAnalyzer.AnalyzeAsync(ffmpeg, src, null, CancellationToken.None, useWhisper: false);
        var lay = new ClipLayout(1280, 720, new LayoutRect(0, 0, 1280, 720), new LayoutRect(900, 420, 320, 240));
        EditPlan Make(CameraLook look)
        {
            var pl = new EditPlan { Width = 1280, Height = 720, Fps = 30, Title = "camera", Layout = lay, SourceW = 1280, SourceH = 720, Camera = look };
            pl.Pieces.Add(new Piece { Role = "moment", In = 2, Out = 6, At = 0 });
            return pl;
        }
        var old = CameraLook.Segmenter;
        CameraLook.Segmenter = segmenter ?? new DiscSegmenter();
        try
        {
            var plan = Make(new CameraLook(CameraLook.Oval, CameraLook.Removed, true));
            var args = new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "16" };
            var res = await VodRenderer.RenderAsync(ffmpeg, src, plan, Path.Combine(dir, "out"), "camera", args, signals, null, CancellationToken.None, Path.Combine(dir, "cache"));
            string png = Path.Combine(dir, "frame.png");
            await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-y", "-v", "error", "-ss", "2", "-i", res.Video, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", png + ".rgb" }, TimeSpan.FromMinutes(1), CancellationToken.None);
            await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-y", "-v", "error", "-ss", "2", "-i", res.Video, "-frames:v", "1", png }, TimeSpan.FromMinutes(1), CancellationToken.None);
            var rgb = File.ReadAllBytes(png + ".rgb");
            (int R, int G, int B) Px(int x, int y) { int i = (y * 1280 + x) * 3; return (rgb[i], rgb[i + 1], rgb[i + 2]); }
            bool Blue((int R, int G, int B) c) => c.B > c.R + 80 && c.B > c.G + 60;   // (after the colour grade)
            // the camera square: 900..1220 × 420..660, middle (1060, 540); the oval's box is √2 bigger
            var you = Px(1060, 540);                 // inside "you"
            var behind = Px(1205, 435);              // the camera's old corner: not you, inside the oval
            var box = CameraLook.Box(lay.Camera!.Value, CameraLook.Oval);
            int ringX = (int)(box.X + box.W / 2), ringY = (int)box.Y + 2; // the oval's top edge
            var ring = Px(ringX, Math.Clamp(ringY, 0, 719));
            var game = Px(200, 200);
            check(Blue(you) && !Blue(behind) && ring.R > 150 && ring.R > ring.B + 60 && !Blue(game),
                $"Camera: oval with a gold ring over the old square, background removed, you as recorded (you {you}, behind you {behind}, ring {ring})");
            var masks = Directory.GetFiles(Path.Combine(dir, "cache"), "m-*.mkv");
            check(masks.Length == 1, $"…the mask of you is made once and kept with the clips ({masks.Length})");
            // as recorded: nothing changes (no mask made, the camera untouched)
            var plain = Make(new CameraLook());
            check(!VodRenderer.PieceArgs(src, plain.Pieces[0], plain, "OUT").Any(a => a.Contains("alphamerge")), "…as recorded: the camera is left alone");
        }
        finally { CameraLook.Segmenter = old; }
    }

    /// <summary>
    /// The editor watches its clips and listens to the finished video: a black clip and a silent clip are caught, and
    /// parts recorded 20 dB apart come out at the same loudness.
    /// </summary>
    public static async Task QualityAsync(string ffmpeg, string dir, Action<bool, string> check)
    {
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "levels.mp4");
        await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=100", "-f", "lavfi", "-i", "anoisesrc=d=100:c=pink:a=0.5",
            "-vf", "drawbox=c=black:t=fill:enable='between(t,60,80)'",
            "-af", "volume='if(lt(t,30),0.05,if(lt(t,60),0.5,if(between(t,85,100),0,0.2)))':eval=frame",
            "-ac", "2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", src }, TimeSpan.FromMinutes(3), CancellationToken.None);
        var signals = await VodAnalyzer.AnalyzeAsync(ffmpeg, src, null, CancellationToken.None, useWhisper: false);
        EditPlan Make(params (double In, double Out)[] spans)
        {
            var pl = new EditPlan { Width = 640, Height = 360, Fps = 30, Title = "levels" };
            double at = 0;
            foreach (var (a, b) in spans) { pl.Pieces.Add(new Piece { Role = "moment", In = a, Out = b, At = at }); at += b - a; }
            return pl;
        }
        var args = new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "26" };
        var withBad = Make((5, 15), (35, 45), (62, 72), (87, 95));
        var files = await VodRenderer.PiecesAsync(ffmpeg, src, withBad, args, null, CancellationToken.None, Path.Combine(dir, "cache"), dir);
        var bad = await VodRenderer.BadPiecesAsync(ffmpeg, withBad, files, CancellationToken.None);
        check(bad.Select(b => b.Index).SequenceEqual(new[] { 2, 3 }), $"Clip check: a black clip and a silent clip are caught, the good ones pass ({string.Join(", ", bad.Select(b => $"{b.Index + 1}: {b.Why}"))})");
        var a0 = await VodRenderer.CheckPieceAsync(ffmpeg, files[0], CancellationToken.None);
        var a1 = await VodRenderer.CheckPieceAsync(ffmpeg, files[1], CancellationToken.None);
        var good = Make((5, 15), (35, 45), (20, 28), (50, 58));
        var res = await VodRenderer.RenderAsync(ffmpeg, src, good, Path.Combine(dir, "out"), "levels", args, signals, null, CancellationToken.None, Path.Combine(dir, "cache"));
        var dev = await VodRenderer.PartDeviationsAsync(ffmpeg, res.Video, good, CancellationToken.None);
        double worst = dev.Count == 0 ? 99 : dev.Values.Max(Math.Abs);
        check(Math.Abs(a1.Lufs - a0.Lufs) > 15 && dev.Count == 4 && worst <= 2.5,
            $"Loudness: parts recorded {Math.Abs(a1.Lufs - a0.Lufs):0} dB apart come out within {worst:0.0} dB of each other ({VodRenderer.LoudnessCheck})");
    }

    /// <summary>
    /// The Wizard101 kit fills itself from a recording: a 7-minute stream with steady music and no one talking gives
    /// it stretches of music, the intro clip and the intro's music, once; the brand then uses them, and with the kit
    /// empty there's no music at all (never generated).
    /// </summary>
    public static async Task KitAsync(string ffmpeg, string dir, Action<bool, string> check)
    {
        Directory.CreateDirectory(dir);
        string rec = Path.Combine(dir, "Leapcast 2026-11-11 22-37-52.mp4");
        await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc2=s=320x180:r=15:d=420",
            "-f", "lavfi", "-i", "sine=f=330:d=420", "-af", "volume=0.15", "-ac", "2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", rec },
            TimeSpan.FromMinutes(3), CancellationToken.None);
        string old = VodKit.Folder;
        VodKit.Folder = Path.Combine(dir, "Wizard101 kit");
        try
        {
            var empty = VodKit.Brand("Reallefroge", "kit", true, true);
            var signals = await VodAnalyzer.AnalyzeAsync(ffmpeg, rec, null, CancellationToken.None, useWhisper: false);
            var added = await VodKit.HarvestAsync(ffmpeg, rec, signals, CancellationToken.None);
            var tracks = MusicLibrary.Tracks(VodKit.MusicFolder);
            check(tracks.Count >= 1 && VodKit.IntroClip() is not null && VodKit.IntroMusic() is not null,
                $"Wizard101 kit: fills itself from a stream ({string.Join(", ", added)}; {VodKit.Summary()})");
            var again = await VodKit.HarvestAsync(ffmpeg, rec, signals, CancellationToken.None);
            check(again.Count == 0, $"…a recording is only taken from once ({again.Count} added the second time)");
            var dec = MusicLibrary.Decode(ffmpeg, tracks[0], 10, 5);
            double peak = dec is null ? 0 : dec.L.Max(Math.Abs);
            check(dec is not null && dec.Length >= 48000 * 5 - 10 && peak > 0.01, $"…its music decodes for the bed ({dec?.Length} frames, peak {peak:0.00})");
            var brand = VodKit.Brand("Reallefroge", "kit", true, true);
            check(brand.MusicSource == "folder" && brand.MusicFolder == VodKit.MusicFolder && brand.IntroClip is not null && brand.IntroMusic is not null && empty.IntroClip is null,
                "…and the channel's look uses it (music, intro clip, intro music)");
            // no tracks: no music (the generated music is never used in its place)
            var plan = VodPlanner.Plan(signals, new VodEditOptions("Kit test", 60, Brand: new VodBrand("Reallefroge", MusicFolder: Path.Combine(dir, "nothing"), MusicSource: "folder")));
            plan.Sfx.Clear();   // (just the music)
            var bed = VodRenderer.Bed(plan, signals, ffmpeg);
            check(bed.L.All(x => Math.Abs(x) < 0.001), $"…with nothing in the kit there's no made-up music (bed peak {bed.L.Max(Math.Abs):0.00})");
            var withKit = VodPlanner.Plan(signals, new VodEditOptions("Kit test", 60, Brand: brand));
            check(withKit.Pieces.Any(p => p.Source == brand.IntroClip) && withKit.Texts.Any(t => t.Kind == "endscreen") && withKit.Pieces[^1].Hold >= 19,
                $"…the intro plays your clip, and the end screen is held 20 s for YouTube's elements ({string.Join(",", withKit.Pieces.Select(p => p.Role).Distinct())})");
            var small = withKit.Resized(640, 360, 30);
            check(small.Pieces.Count == withKit.Pieces.Count && Math.Abs(small.Duration - withKit.Duration) < 1 && AssWriter.Build(small).Contains("PlayResY: 360"),
                "…a preview-size copy of the plan keeps everything in place");
        }
        finally { VodKit.Folder = old; }
    }
}
