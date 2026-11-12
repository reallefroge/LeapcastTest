namespace LeapcastStudioAI.Vod;

/// <summary>
/// One thing the editor suggests for this edit: what and why, where (to watch it first), and the suggestion line
/// that makes it happen (applied exactly like one you type, so you see what it changed).
/// </summary>
/// <param name="At">Where in the video (seconds), or -1 when it isn't in the video yet.</param>
/// <param name="SourceAt">Where in the recording (seconds), or -1.</param>
public sealed record EditSuggestion(string Id, string Kind, double At, double SourceAt, string Text, string Apply);

/// <summary>
/// The editor's own suggestions for an edit, from what it knows about the recording: your explanations in meetings
/// that could show a replay of your round (VAR checks), clips you saved and big reactions that didn't make the cut,
/// cuts where a transition would mark a change, stretches where nothing is said, and the hook / end screen /
/// slow motion if they're off.
/// </summary>
public static class VodSuggestions
{
    public const int Max = 14;

    public static List<EditSuggestion> For(EditPlan plan, VodSignals s, EditTweaks tw, string game)
    {
        var list = new List<EditSuggestion>();
        string C(double t) => EditFeedback.Clock(t);
        // where a moment of the recording plays in the video (null: it isn't in it)
        double? VideoAt(double src)
        {
            var p = plan.Pieces.FirstOrDefault(p => p.Source is null && p.Role is "moment" or "slow" or "face" && src >= p.In - 0.05 && src <= p.Out + 0.05);
            return p is null ? null : p.At + (src - p.In) / Math.Max(0.01, p.Speed);
        }
        bool InVideo(double src, double margin = 3) => plan.Pieces.Any(p => p.Source is null && p.Role is "moment" or "slow" or "face" && src >= p.In - margin && src <= p.Out + margin);
        bool Removed(double src) => tw.Remove.Any(r => src >= r[0] && src <= r[1]);
        var words = s.Words.Concat(s.Others).OrderBy(w => w.Start).ToList();
        string Said(double from, double to)
        {
            var w = words.Where(x => x.Start >= from && x.Start <= to).Take(8).Select(x => x.Text).ToList();
            return w.Count == 0 ? "" : Captions.Tidy(string.Join(' ', w));
        }

        // ---- VAR checks: where you explained what you did in a meeting
        foreach (var v in plan.VarClaims.Where(v => !v.Shown))
            if (VideoAt(v.At) is { } at)
                list.Add(new("var@" + (int)v.At, "var", at, v.At,
                    $"Meeting {v.Meeting}: you said “{v.Quote}”. Show what you did that round as a replay over it (VAR check).",
                    $"show what I did at {C(at)}"));

        // ---- clips you saved while live, and deaths, that aren't in the video
        foreach (var m in s.Markers.Where(m => m.Kind is "clip" or "death").OrderBy(m => m.At))
        {
            double src = m.Kind == "clip" ? Math.Max(0, m.At - 12) : Math.Max(0, m.At - 4);
            if (InVideo(src, 8) || Removed(src) || list.Any(x => x.SourceAt >= 0 && Math.Abs(x.SourceAt - src) < 45)) continue;
            string said = Said(src - 3, src + 12);
            list.Add(new($"{m.Kind}@{(int)src}", m.Kind, -1, src,
                m.Kind == "clip"
                    ? $"You saved a clip at {C(m.At)} of the stream, and it isn't in the video{(said.Length > 0 ? $" (“{said}”)" : "")}. Add it."
                    : $"You died at {C(m.At)} of the stream and that's not in the video{(said.Length > 0 ? $" (“{said}”)" : "")}. Add it.",
                $"add the moment at {C(src)} of the stream"));
        }

        // ---- your biggest reactions that didn't make the cut (loud, and you're talking)
        if (s.Loud.Length > 0)
        {
            var levels = s.Loud.Where(x => x > -80).OrderBy(x => x).ToList();
            double median = levels.Count == 0 ? -40 : levels[levels.Count / 2];
            var peaks = new List<(double T, float L)>();
            for (int i = 0; i < s.Loud.Length; i++)
                if (s.Loud[i] > median + 14) peaks.Add((i * VodSignals.LoudStep, s.Loud[i]));
            int added = 0;
            foreach (var (t, _) in peaks.OrderByDescending(p => p.L))
            {
                if (added >= 3) break;
                if (InVideo(t, 6) || Removed(t) || s.LobbyAt(t) || list.Any(x => x.SourceAt >= 0 && Math.Abs(x.SourceAt - t) < 60)) continue;
                string said = Said(t - 2, t + 3);
                if (said.Length == 0) continue; // a loud game sound, not you
                list.Add(new($"react@{(int)t}", "reaction", -1, Math.Max(0, t - 6),
                    $"A big reaction at {C(t)} of the stream (“{said}”) isn't in the video. Add it.",
                    $"add the moment at {C(Math.Max(0, t - 6))} of the stream"));
                added++;
            }
        }

        // ---- cuts where a transition would mark a change (you chose plain cuts: these are the editor's picks)
        foreach (var c in plan.Cuts.Where(c => c.Style == TransitionKinds.None && c.Suggested != TransitionKinds.None).Take(4))
        {
            string word = c.Suggested switch
            {
                TransitionKinds.Blur => "blur transition", TransitionKinds.Zoom => "zoom transition", TransitionKinds.Timelapse => "timelapse transition",
                _ => c.Suggested + " transition",
            };
            list.Add(new($"cut@{(int)c.From}", "cut", Math.Max(0, c.At - 2.5), c.From,
                $"Cut at {C(c.At)}: {c.Why}. A {TransitionKinds.Name(c.Suggested).ToLowerInvariant()} would mark it.",
                $"at {C(c.At)}: use a {word} here"));
        }

        // ---- long stretches where nothing is said and little happens
        var motions = s.Motion.Where(x => x >= 0).OrderBy(x => x).ToList();
        double quietMotion = motions.Count == 0 ? 0 : motions[motions.Count / 2] * 0.6;
        int dead = 0;
        foreach (var p in plan.Pieces.Where(p => p.Role == "moment" && p.Source is null && p.Speed == 1))
        {
            if (dead >= 3) break;
            double runFrom = p.In;
            double lastWord = p.In;
            foreach (var w in words.Where(w => w.Start >= p.In && w.Start <= p.Out).Append(new VodWord("", p.Out, p.Out)))
            {
                if (w.Start - lastWord >= 10)
                {
                    double mid = (lastWord + w.Start) / 2;
                    double motion = Enumerable.Range(0, (int)((w.Start - lastWord) * 2)).Select(k => s.MotionAt(lastWord + k * 0.5)).DefaultIfEmpty(0).Average();
                    if (motion <= quietMotion)
                    {
                        double at = p.At + (mid - p.In);
                        list.Add(new($"dead@{(int)mid}", "dead", Math.Max(0, p.At + (lastWord - p.In)), lastWord,
                            $"{C(p.At + (lastWord - p.In))}: {(w.Start - lastWord):0} s where nothing is said and little happens. Cut it.",
                            $"remove the part at {C(at)}"));
                        dead++;
                        break;
                    }
                }
                lastWord = Math.Max(lastWord, w.End);
            }
        }

        // ---- your camera's look
        if (plan.Layout?.Camera is not null)
        {
            if (tw.CameraShape.Length == 0)
                list.Add(new("cam-shape", "camera", -1, -1, "Give your camera a shape: an oval with a thin gold ring (drawn big enough to cover its old square).", "make my camera oval"));
            if (tw.CameraBackground.Length == 0 && CameraLook.Segmenter is not null)
                list.Add(new("cam-bg", "camera", -1, -1, "Remove the background behind you in your camera (you're cut out frame by frame).", "remove my background"));
        }

        // ---- the big pieces, if they're off
        if (!tw.Hook || !tw.ColdOpen)
            list.Add(new("hook", "hook", -1, -1, "Start with a hook: your biggest moments in quick flashes, then the run-up to the best one, before the intro.", "add a hook"));
        if (!tw.EndScreen)
            list.Add(new("endscreen", "endscreen", -1, -1, "End with the end screen: 20 s laid out for YouTube's previous video, watch next and subscribe.", "add the end screen"));
        if (!tw.SlowMotion && plan.Pieces.Any(p => p.Role == "moment" && s.Markers.Any(m => m.Kind == "death" && m.At > p.In && m.At < p.Out)))
            list.Add(new("slowmo", "slowmo", -1, -1, "Your deaths are in the video at normal speed: slow motion would land them.", "slow motion on"));
        if (!tw.Captions)
            list.Add(new("captions", "captions", -1, -1, "Captions are off: most people watch with the sound low, and captions keep them watching.", "captions on"));

        return list.Where(x => !tw.Dismissed.Contains(x.Id)).Take(Max).ToList();
    }

    /// <summary>How to ask for a fix in your own words (shown with the suggestions).</summary>
    public const string HowTo =
        "To fix a spot: press ▶ on a moment or cut (or play the video), pause where it's wrong, press “Note at this time”, then write what's wrong on that line and press Enter. " +
        "For example: “this part is boring”, “use a fade here”, “show what I did” (Among Us: a replay of your round over the meeting), “too loud”, “cut it”. " +
        "For the whole video: “different clips”, “music quieter”, “no transition sounds”, “even out the loudness”, “make it 12 minutes”, “more funny moments”.";
}
