namespace LeapcastStudioAI.Vod;

/// <summary>
/// Decides the edit the way a highlights editor would: finds the moments worth watching (your voice getting loud,
/// what you say, clips you saved, deaths, chat going off, action on screen), drops dead air and loading screens,
/// and builds a story around them: a cold open on the best moment, a title, chapters named after your best line in
/// each, quick jump cuts, punch-ins and face zooms on reactions, slow motion when a fight is lost, whip transitions
/// across time jumps, and a music change per chapter.
/// </summary>
public static class VodPlanner
{
    public const double Bin = 0.5;

    /// <summary>Words and phrases that mark a reaction (weight).</summary>
    public static readonly Dictionary<string, double> Hype = new()
    {
        ["lets go"] = 3, ["let's go"] = 3, ["no way"] = 3, ["oh my god"] = 3, ["oh my gosh"] = 2.5, ["what the"] = 2.5, ["are you kidding"] = 3,
        ["holy"] = 2.5, ["insane"] = 2.5, ["crazy"] = 2, ["clutch"] = 3, ["wow"] = 2, ["yes"] = 1.5, ["yeah"] = 0.8, ["no"] = 1,
        ["dude"] = 1.2, ["bro"] = 1.2, ["omg"] = 2.5, ["gg"] = 2, ["dead"] = 1.5, ["died"] = 1.5, ["run"] = 1, ["help"] = 1.2,
        ["come on"] = 2, ["why"] = 1, ["wait"] = 1, ["what"] = 1.2, ["haha"] = 2, ["hahaha"] = 2.5, ["lol"] = 1.5, ["nice"] = 1.5,
        ["finally"] = 2, ["boss"] = 1, ["critical"] = 1.5, ["perfect"] = 1.5, ["unbelievable"] = 3, ["oh no"] = 2.5, ["please"] = 1.2,
    };

    private static double WordWeight(IReadOnlyList<VodWord> words, int i)
    {
        string w = words[i].Text.ToLowerInvariant();
        string two = i + 1 < words.Count ? w + " " + words[i + 1].Text.ToLowerInvariant() : "";
        string three = i + 2 < words.Count ? two + " " + words[i + 2].Text.ToLowerInvariant() : "";
        double best = 0;
        if (three.Length > 0 && Hype.TryGetValue(three, out var a)) best = Math.Max(best, a);
        if (two.Length > 0 && Hype.TryGetValue(two, out var b)) best = Math.Max(best, b);
        if (Hype.TryGetValue(w, out var c)) best = Math.Max(best, c);
        return best;
    }

    public static EditPlan Plan(VodSignals s, VodEditOptions o, EditTweaks? tw = null)
    {
        tw ??= new EditTweaks();
        bool Removed(double a, double b) => tw.Remove.Any(r => a < r[1] && b > r[0]);
        int n = Math.Max(1, (int)Math.Ceiling(s.Duration / Bin));
        var t0 = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var words = Captions.Dedupe(s.Words.Select(w => new CaptionWord(w.Text, t0.AddSeconds(w.Start), t0.AddSeconds(w.End))))
            .Select(w => new VodWord(w.Text, (w.StartUtc - t0).TotalSeconds, (w.EndUtc - t0).TotalSeconds)).ToList();
        double target = s.Duration >= o.TargetSeconds * 2 ? o.TargetSeconds : Math.Max(45, Math.Min(o.TargetSeconds, s.Duration * 0.45));
        var rng = new Random((int)(s.Duration * 1000) ^ words.Count);
        var _momentIndex = new Dictionary<Piece, int>(ReferenceEqualityComparer.Instance); // which moment each piece shows

        // ---------------------------------------------------------------- scoring, every half second
        var speech = new double[n]; var excite = new double[n]; var marker = new double[n]; var chat = new double[n];
        var loud = new double[n]; var motion = new double[n]; var still = new bool[n];
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            for (int b = (int)(w.Start / Bin); b <= (int)(w.End / Bin) && b < n; b++) if (b >= 0) speech[b] = 1;
            double wt = WordWeight(words, i);
            int wb = (int)(w.Start / Bin);
            if (wt > 0 && wb >= 0 && wb < n) excite[wb] += wt;
        }
        for (int b = 0; b < n; b++)
        {
            double t = b * Bin;
            loud[b] = Math.Max(s.LoudAt(t), s.LoudAt(t + 0.25));
            motion[b] = s.MotionAt(t);
        }
        double medLoud = Median(Enumerable.Range(0, n).Where(b => speech[b] > 0).Select(b => loud[b]).DefaultIfEmpty(Median(loud)).ToList());
        // still pictures (loading screens, menus, AFK): no change for 3 s or more, or a dark screen
        for (int b = 0; b < n; b++)
        {
            int run = 0;
            for (int k = Math.Max(0, b - 3); k <= Math.Min(n - 1, b + 3); k++) if (motion[k] < 0.006) run++;
            float br = s.Brightness.Length == 0 ? 0.5f : s.Brightness[Math.Clamp(b, 0, s.Brightness.Length - 1)];
            still[b] = run >= 6 || br < 0.05f;
        }
        // ---------------------------------------------------------------- what kind of play each moment is
        // Among Us: the meeting tablet on screen (talk is the content there, the still picture isn't dead air), the
        // game itself (moving around the map), and the lobby or menus (the same room for ages: nothing happens)
        var phase = GamePhases(s, n, out bool amongUs);
        if (amongUs)
            for (int b = 0; b < n; b++) still[b] = phase[b] == PhaseKind.Idle || (phase[b] != PhaseKind.Meeting && still[b]);
        var others = s.Others ?? new List<VodWord>();
        var otherSpeech = new double[n];
        var otherExcite = new double[n];
        for (int i = 0; i < others.Count; i++)
        {
            var w = others[i];
            for (int b = (int)(w.Start / Bin); b <= (int)(w.End / Bin) && b < n; b++) if (b >= 0) otherSpeech[b] = Math.Min(3, otherSpeech[b] + 1);
            double wt = WordWeight(others, i);
            int wb = (int)(w.Start / Bin);
            if (wt > 0 && wb >= 0 && wb < n) otherExcite[wb] += wt;
        }
        // where sentences start and end, anyone's (cuts land between sentences, not in someone's)
        var allWords = amongUs ? words.Concat(others).OrderBy(w => w.Start).ToList() : words;
        foreach (var m in s.Markers)
        {
            (double from, double to, double w) = m.Kind switch
            {
                "clip" => (m.At - 30, m.At + 1, 4.0),   // you asked for a clip: the half minute before it mattered
                "death" => (m.At - 20, m.At + 4, 5.0),
                "fight" => (m.At - 2, m.At + 10, 1.0),
                "chat" => (m.At - 20, m.At + 5, 0.06),
                _ => (m.At - 10, m.At + 5, 1.0),
            };
            for (int b = Math.Max(0, (int)(from / Bin)); b < Math.Min(n, (int)(to / Bin)); b++)
                if (m.Kind == "chat") chat[b] += w; else marker[b] = Math.Max(marker[b], w);
        }
        double chatMean = chat.Average(), chatSd = Math.Sqrt(chat.Select(x => (x - chatMean) * (x - chatMean)).Average()) + 1e-6;
        var raw = new double[n];
        for (int b = 0; b < n; b++)
        {
            double lz = Math.Max(0, (loud[b] - medLoud) / 6.0);
            // what you asked for more of: the funny moments (talk, laughs) or the action (loud, moving)
            double talkW = tw.Emphasis > 0 ? 1.5 : tw.Emphasis < 0 ? 0.7 : 1, actW = tw.Emphasis < 0 ? 1.6 : tw.Emphasis > 0 ? 0.7 : 1;
            raw[b] = talkW * (1.0 * speech[b] + 2.2 * Math.Min(3, excite[b])) + actW * (1.3 * Math.Min(3, lz) + 0.7 * Math.Min(1, motion[b] * 6)) + marker[b]
                     + 0.8 * Math.Max(0, (chat[b] - chatMean) / chatSd)
                     - (still[b] ? 3 : 0) - (speech[b] == 0 && motion[b] < 0.02 ? 1.2 : 0)
                     - (Removed(b * Bin, b * Bin + Bin) ? 50 : 0);
            if (amongUs)
            {
                // others arguing counts too (they're half the show in a meeting); the lobby counts for nothing
                raw[b] += 0.5 * otherSpeech[b] + 1.6 * Math.Min(3, otherExcite[b]);
                if (phase[b] == PhaseKind.Idle) raw[b] -= 6;
                if (phase[b] == PhaseKind.Play) raw[b] += 1.4 * Math.Min(1, Math.Max(0, s.NoveltyAt(b * Bin)) * 8);
            }
        }
        var smooth = MovingAverage(raw, 12);

        // ---------------------------------------------------------------- episodes: the stream's own scenes
        // A loading screen (a zone change in Wizard101) or a long lull ends one stretch of play and starts the next:
        // each is a little story of its own (going in, the build-up, the big moment, the reaction).
        var episodes = new List<(int A, int B)>();
        {
            int start = 0, lull = 0;
            for (int b = 0; b < n; b++)
            {
                bool quiet = still[b] || (speech[b] == 0 && motion[b] < 0.02 && excite[b] == 0 && marker[b] == 0);
                lull = quiet ? lull + 1 : 0;
                if ((still[b] && lull >= 8) || lull >= 40)
                {
                    int end = b - lull + 1;
                    if (end - start >= 20) episodes.Add((start, end));
                    start = b + 1;
                }
            }
            if (n - start >= 20) episodes.Add((start, n));
            if (episodes.Count == 0) episodes.Add((0, n));
            // very long ones (a 20-minute dungeon) split at their quietest point, so each part is one push
            for (int e = 0; e < episodes.Count; e++)
            {
                var (a, b) = episodes[e];
                if ((b - a) * Bin <= 360) continue;
                int lo = a + (int)(90 / Bin), hi = b - (int)(90 / Bin), cut = lo;
                for (int k = lo; k < hi; k++) if (smooth[k] < smooth[cut]) cut = k;
                episodes[e] = (a, cut); episodes.Insert(e + 1, (cut, b)); e--;
            }
        }
        int EpisodeOf(double t) { int b = (int)(t / Bin); int e = episodes.FindIndex(x => b >= x.A && b < x.B); return e < 0 ? 0 : e; }

        // how much each is worth: its big moment, plus what happened in it (deaths, clips you saved, hype)
        var epPeak = new int[episodes.Count];
        var epValue = new double[episodes.Count];
        var epFun = new double[episodes.Count];
        for (int e = 0; e < episodes.Count; e++)
        {
            var (a, b) = episodes[e];
            int pk = a;
            for (int k = a; k < b; k++) if (!still[k] && smooth[k] > smooth[pk]) pk = k;
            double ea = a * Bin, eb = b * Bin;
            // the climax of a stretch is what happened in it, when something did: the death, or the clip you saved
            var ev = s.Markers.Where(m => m.Kind is "death" or "clip" && m.At >= ea && m.At < eb).OrderByDescending(m => m.Kind == "death" ? 2 : 1).ThenByDescending(m => smooth[Math.Clamp((int)(m.At / Bin), 0, n - 1)]).FirstOrDefault();
            if (ev is not null) pk = Math.Clamp((int)((ev.Kind == "clip" ? ev.At - 4 : ev.At) / Bin), a, b - 1);
            epPeak[e] = pk;
            int events = s.Markers.Count(m => m.Kind is "death" or "clip" && m.At >= ea && m.At < eb);
            var top = Enumerable.Range(a, b - a).Select(k => smooth[k]).OrderByDescending(x => x).Take((int)(20 / Bin)).DefaultIfEmpty(0).Average();
            epValue[e] = smooth[pk] + 0.6 * Math.Min(3, events) + 0.35 * top;
            epFun[e] = words.Count(w => w.Start >= ea && w.Start < eb && Laughs.Contains(w.Text.ToLowerInvariant()))
                       + 0.4 * Enumerable.Range(a, b - a).Sum(k => excite[k]) / Math.Max(1, (b - a) * Bin / 60);
        }

        // the pieces of one episode's story (source seconds); value orders what's dropped first when it's too long
        var chosen = new List<(double From, double To, double Peak, double Value)>();
        var segEpisode = new List<int>();
        bool AddSeg(double from, double to, double peak, double value, int ep)
        {
            from = Math.Max(episodes[ep].A * Bin, from);
            to = Math.Min(Math.Min(s.Duration, episodes[ep].B * Bin + 2), to);
            while (from < to - 4 && still[Math.Clamp((int)(from / Bin), 0, n - 1)]) from += Bin;
            while (to > from + 4 && still[Math.Clamp((int)(to / Bin) - 1, 0, n - 1)]) to -= Bin;
            if (to - from < 4) return false;
            // a stretch you asked to leave out: keep only the side of it that's long enough
            foreach (var r in tw.Remove.Where(r => from < r[1] && to > r[0]))
            {
                if (r[0] - from >= to - r[1]) to = Math.Min(to, r[0]); else from = Math.Max(from, r[1]);
                if (to - from < 4) return false;
            }
            // overlapping or nearly touching another piece: join them (one continuous shot reads better)
            for (int k = 0; k < chosen.Count; k++)
            {
                var c = chosen[k];
                if (from < c.To + 3 && to > c.From - 3)
                {
                    chosen[k] = (Math.Min(c.From, from), Math.Max(c.To, to), value > c.Value ? peak : c.Peak, Math.Max(c.Value, value));
                    return true;
                }
            }
            chosen.Add((from, to, peak, value));
            segEpisode.Add(ep);
            return true;
        }
        double Len() => chosen.Sum(c => (c.To - c.From) * 0.85);

        double PaceScale = tw.Pace < 0 ? 0.7 : tw.Pace > 0 ? 1.35 : 1;
        double overhead = (tw.ColdOpen ? 8 : 0) + 3 + 10; // cold open, title, end card
        double budget = Math.Max(20, target - overhead);
        var byValue = Enumerable.Range(0, episodes.Count).OrderByDescending(e => epValue[e]).ToList();
        var picked = new List<int>();

        Dictionary<int, string>? episodeNames = null;
        if (amongUs)
        {
            // the stream's own structure: every round of play and every meeting is an episode
            episodes.Clear();
            episodeNames = new();
            int rounds = 0, meetings = 0;
            for (int b = 0; b < n;)
            {
                int e = b;
                while (e < n && phase[e] == phase[b]) e++;
                if (phase[b] != PhaseKind.Idle)
                {
                    episodeNames[episodes.Count] = phase[b] == PhaseKind.Meeting ? $"Meeting {++meetings}" : $"Round {++rounds}";
                    episodes.Add((b, e));
                }
                b = e;
            }
            if (episodes.Count == 0) { episodes.Add((0, n)); episodeNames = null; }
            else PickGameAndMeetings(episodes, phase, smooth, still, motion, s, allWords, budget, PaceScale, AddSeg, Len);
        }
        // 1. the big moment of the best episodes (with the lead-up that makes it land); a stretch with a death or a
        //    clip you saved always gets its moment if there's any room
        foreach (int e in amongUs ? Enumerable.Empty<int>() : byValue)
        {
            bool eventful = s.Markers.Any(m => m.Kind is "death" or "clip" && m.At >= episodes[e].A * Bin && m.At < episodes[e].B * Bin);
            if (Len() >= budget * (eventful ? 0.9 : 0.62)) continue;
            double pt = epPeak[e] * Bin;
            bool clipNear = s.Markers.Any(m => m.Kind is "clip" or "death" && m.At >= pt - 5 && m.At <= pt + 30);
            double lead = (clipNear ? 22 : 14) * PaceScale, tail = 8 * PaceScale;
            double from = SentenceStart(words, Math.Max(0, pt - lead));
            double to = SentenceEnd(words, Math.Min(s.Duration, pt + tail), s.Duration);
            if (to - from > 75) { from = Math.Max(from, pt - 50); to = Math.Min(to, pt + 25); }
            if (AddSeg(from, to, pt, epValue[e] + 10, e)) picked.Add(e);
        }
        // moments you asked for are always in
        foreach (var inc in tw.Include)
        {
            int e = Math.Min(episodes.Count - 1, EpisodeOf(inc));
            if (AddSeg(SentenceStart(words, Math.Max(0, inc - 10 * PaceScale)), SentenceEnd(words, Math.Min(s.Duration, inc + 8 * PaceScale), s.Duration), inc, 1000, e)
                && !picked.Contains(e)) picked.Add(e);
        }
        // 2. a breather: the funniest calm stretch not already in, between the intense ones
        if (episodes.Count > 2 && !amongUs)
        {
            int fun = Enumerable.Range(0, episodes.Count).Where(e => !picked.Contains(e)).OrderByDescending(e => epFun[e]).FirstOrDefault(-1);
            if (fun >= 0 && epFun[fun] >= 2)
            {
                var (a, b) = episodes[fun];
                int best2 = a;
                double bestScore = -1;
                for (int k = a; k < b; k++)
                {
                    double sc = 0;
                    for (int q = k; q < Math.Min(b, k + 24); q++) sc += excite[q] + speech[q] * 0.3;
                    if (sc > bestScore) { bestScore = sc; best2 = k; }
                }
                double f0 = SentenceStart(words, best2 * Bin), f1 = SentenceEnd(words, Math.Min(s.Duration, best2 * Bin + 11), s.Duration);
                if (AddSeg(f0, f1, best2 * Bin + 4, epValue[fun] + 4, fun)) picked.Add(fun);
            }
        }
        // 3. each picked episode gets its story: what you said going in, the reaction after
        foreach (int e in amongUs ? Enumerable.Empty<int>() : picked.OrderByDescending(e => epValue[e]))
        {
            if (Len() >= budget) break;
            var (a, b) = episodes[e];
            double ea = a * Bin, pt = epPeak[e] * Bin;
            var climax = chosen.FirstOrDefault(c => pt >= c.From - 1 && pt <= c.To + 1);
            // going in: the first thing you said in this stretch (the plan, the goal, the joke)
            var first = words.FirstOrDefault(w => w.Start >= ea + 1 && w.Start < climax.From - 10 && w.Text.Length > 1);
            if (first is not null)
            {
                double s0 = SentenceStart(words, first.Start), s1 = SentenceEnd(words, Math.Min(climax.From - 2, first.Start + 7), s.Duration);
                if (s1 - s0 >= 3) AddSeg(s0, Math.Min(s1, s0 + 12), s0 + 1, epValue[e] + 2, e);
            }
            // after: the reaction (a few more seconds of you, if you kept talking)
            double after = climax.To;
            if (after > 0 && words.Any(w => w.Start >= after && w.Start < after + 6))
                AddSeg(after - 0.5, SentenceEnd(words, Math.Min(after + 6, b * Bin), s.Duration), after, epValue[e] + 3, e);
        }
        // 4. the build-up: a quick beat between going in and the big moment, best episodes first
        foreach (int e in amongUs ? Enumerable.Empty<int>() : picked.OrderByDescending(e => epValue[e]))
        {
            if (Len() >= budget) break;
            var (a, b) = episodes[e];
            var mine = chosen.Where((c, k) => segEpisode[k] == e).OrderBy(c => c.From).ToList();
            if (mine.Count < 2) continue;
            for (int g = 0; g + 1 < mine.Count && Len() < budget; g++)
            {
                double ga = mine[g].To + 6, gb = mine[g + 1].From - 6;
                if (gb - ga < 8) continue;
                int pk = (int)(ga / Bin);
                for (int k = (int)(ga / Bin); k < (int)(gb / Bin) && k < n; k++) if (!still[k] && smooth[k] > smooth[pk]) pk = k;
                if (smooth[pk] < smooth[epPeak[e]] * 0.45) continue;
                double bt = pk * Bin;
                AddSeg(SentenceStart(words, bt - 3), SentenceEnd(words, bt + 4, s.Duration), bt, epValue[e] + 1, e);
            }
        }
        // close to the target: drop the weakest pieces (build-ups and set-ups go before big moments) while well over,
        // then trim the longest
        while (chosen.Count > 1 && Len() > budget * 1.06)
        {
            int wi = Enumerable.Range(0, chosen.Count).OrderBy(k => chosen[k].Value).First();
            var weakest = chosen[wi];
            double over = Len() - budget;
            if ((weakest.To - weakest.From) * 0.85 <= over * 1.5) { chosen.RemoveAt(wi); segEpisode.RemoveAt(wi); continue; }
            var longest = chosen.OrderByDescending(c => c.To - c.From).First();
            int li = chosen.IndexOf(longest);
            double cut = Math.Min(over / 0.85, (longest.To - longest.From) - 12);
            if (cut < 1) break;
            // keep the peak: take from the start when the peak is late, else from the end
            chosen[li] = longest.Peak - longest.From > (longest.To - longest.From) / 2
                ? (Math.Min(longest.Peak - 6, longest.From + cut), longest.To, longest.Peak, longest.Value)
                : (longest.From, Math.Max(longest.Peak + 6, longest.To - cut), longest.Peak, longest.Value);
        }
        // Among Us: not a frame of the lobby, whatever joined what
        if (amongUs)
        {
            var clean = new List<(double From, double To, double Peak, double Value)>();
            var cleanEp = new List<int>();
            for (int k = 0; k < chosen.Count; k++)
            {
                var c = chosen[k];
                double cur = c.From;
                for (double t = c.From; t <= c.To + Bin; t += Bin)
                {
                    bool idle = t < c.To && phase[Math.Clamp((int)(t / Bin), 0, n - 1)] == PhaseKind.Idle;
                    if (idle || t > c.To)
                    {
                        double end = Math.Min(t, c.To);
                        if (end - cur >= 2) { clean.Add((cur, end, Math.Clamp(c.Peak, cur, end), c.Value)); cleanEp.Add(segEpisode[k]); }
                        cur = t + Bin;
                    }
                }
            }
            chosen = clean; segEpisode = cleanEp;
        }
        // the story plays in order
        var order = Enumerable.Range(0, chosen.Count).OrderBy(k => chosen[k].From).ToList();
        chosen = order.Select(k => chosen[k]).ToList();
        segEpisode = order.Select(k => segEpisode[k]).ToList();
        // pieces that grew into each other while joining: one piece (never the same seconds twice)
        for (int k = 1; k < chosen.Count;)
        {
            var (p, c) = (chosen[k - 1], chosen[k]);
            if (c.From < p.To || (c.From < p.To + 3 && segEpisode[k] == segEpisode[k - 1]))
            {
                chosen[k - 1] = (p.From, Math.Max(p.To, c.To), c.Value > p.Value ? c.Peak : p.Peak, Math.Max(p.Value, c.Value));
                chosen.RemoveAt(k); segEpisode.RemoveAt(k);
            }
            else k++;
        }
        var plan = new EditPlan { Width = s.Width, Height = s.Height, Fps = s.Fps, Title = o.Title, Layout = s.Layout };
        if (chosen.Count == 0) { chosen.Add((0, Math.Min(s.Duration, Math.Max(10, target - overhead)), Math.Min(s.Duration, 5), 0)); segEpisode.Add(0); }
        var best = chosen.OrderByDescending(c => c.Value).First();
        plan.BestMoment = best.Peak;

        // ---------------------------------------------------------------- cold open + title
        // a cliffhanger: the run-up to the best moment, cut right as it happens (the payoff comes later, in its place)
        double coldTo = Math.Min(best.To, best.Peak + 0.5);
        double coldFrom = Math.Max(best.From, coldTo - 7.5);
        if (coldTo - coldFrom < 4) { coldFrom = Math.Clamp(best.Peak - 5.5, best.From, Math.Max(best.From, best.To - 8)); coldTo = Math.Min(best.To, coldFrom + 8); }
        var brand = o.Brand ?? new VodBrand();
        plan.Brand = brand;
        bool hook = tw.ColdOpen && tw.Hook && brand.Hook;
        var cold = new Piece { Role = "cold", In = coldFrom, Out = coldTo, Letterbox = tw.Letterbox, Push = tw.Zooms > 0 ? 0.07 : 0, TransIn = hook ? TransitionKinds.Flash : null };
        var hookShots = new List<Piece>();
        if (hook)
        {
            // the hook: two flashes of other big moments (a second and a half each), then the run-up to the best one
            foreach (var c in chosen.Where(c => Math.Abs(c.Peak - best.Peak) > 20).OrderByDescending(c => c.Value).Take(2).OrderBy(c => c.Value))
            {
                double a = Math.Max(c.From, c.Peak - 1.0), b = Math.Min(c.To, a + 1.5);
                if (b - a < 1) continue;
                hookShots.Add(new Piece { Role = "cold", In = a, Out = b, Letterbox = tw.Letterbox, TransIn = hookShots.Count > 0 ? TransitionKinds.Flash : null, Push = tw.Zooms > 0 ? 0.05 : 0 });
            }
            plan.Pieces.AddRange(hookShots);
        }
        if (tw.ColdOpen) plan.Pieces.Add(cold);
        if (hookShots.Count == 0) cold.TransIn = null;
        // the intro: your intro clip (the same every video) or the stream itself, darkened, with your logo and name
        double titleSrc = amongUs ? Math.Max(0, coldTo - 4.0) : Math.Min(Math.Max(0, s.Duration - 4.2), coldTo);
        string? introClip = brand.IntroClip is { Length: > 0 } ic && File.Exists(ic) ? ic : null;
        string? logo = brand.Logo is { Length: > 0 } lg && File.Exists(lg) ? lg : null;
        plan.Pieces.Add(introClip is not null
            ? new Piece { Role = "title", Source = introClip, In = 0, Out = 4.0, Mute = true, Darken = true, Image = logo }
            : new Piece { Role = "title", In = titleSrc, Out = titleSrc + 4.0, Blur = logo is null && brand.Channel.Length == 0, Darken = true, Mute = true, Image = logo, Push = tw.Zooms > 0 ? 0.05 : 0 });

        // ---------------------------------------------------------------- chapters: one per stretch of play (at most 7)
        var chapterOf = new int[chosen.Count];
        {
            var eps = segEpisode.Distinct().ToList(); // in story order
            int groups = Math.Min(7, eps.Count);
            double totalLen = chosen.Sum(c => (c.To - c.From) * 0.85), per = totalLen / Math.Max(1, groups), acc = 0;
            var epChapter = new Dictionary<int, int>();
            int ch = 0;
            foreach (int e in eps)
            {
                if (eps.Count > 7 && acc >= per * (ch + 1) && ch < groups - 1) ch++;
                else if (eps.Count <= 7 && epChapter.Count > 0) ch = epChapter.Values.Max() + 1;
                epChapter[e] = ch;
                acc += chosen.Where((c, k) => segEpisode[k] == e).Sum(c => (c.To - c.From) * 0.85);
            }
            for (int k = 0; k < chosen.Count; k++) chapterOf[k] = epChapter[segEpisode[k]];
        }
        int chapters = chapterOf.Length == 0 ? 1 : chapterOf.Max() + 1;

        // ---------------------------------------------------------------- moments → pieces (jump cuts, zooms, slow motion)
        double lastFace = -999;
        int punches = 0;
        var cam = tw.FaceCam ? s.Layout?.Camera : null;
        double zoomGap = tw.Zooms switch { 0 => double.MaxValue, 1 => 14, 3 => 3.5, _ => 6 };
        double zoomAdd = tw.Zooms switch { 1 => -0.04, 3 => 0.04, _ => 0 };
        for (int i = 0; i < chosen.Count; i++)
        {
            var (from, to, peak, _) = chosen[i];
            var spans = amongUs
                ? JumpCuts(from, to, allWords, s, s.Markers, peak, tw.Pace < 0 ? 0.9 : tw.Pace > 0 ? 2.2 : 1.4, silenceBelow: medLoud - 16)
                : JumpCuts(from, to, words, s, s.Markers, peak, tw.Pace < 0 ? 0.5 : tw.Pace > 0 ? 1.6 : 0.8);
            var deaths = tw.SlowMotion ? s.Markers.Where(m => m.Kind == "death" && m.At > from + 2 && m.At < to).Select(m => m.At).ToList() : new List<double>();
            var momentPieces = new List<Piece>();
            foreach (var (a, b) in spans)
            {
                // a death inside: half speed through the moment it happens
                var cuts = new List<(double A, double B, bool Slow)>();
                double cur = a;
                foreach (var d in deaths.Where(d => d > a + 1.3 && d < b - 0.9))
                {
                    cuts.Add((cur, d - 1.3, false));
                    cuts.Add((d - 1.3, d + 0.9, true));
                    cur = d + 0.9;
                }
                cuts.Add((cur, b, false));
                foreach (var (ca, cb, slow) in cuts)
                {
                    if (cb - ca < 0.6) continue;
                    if (slow)
                    {
                        var sp = new Piece { Role = "slow", In = ca, Out = cb, Speed = 0.5, Flash = false, Shake = false };
                        sp.Zooms.Add(new Zoom(0, (cb - ca) / 0.5, 1.16));
                        momentPieces.Add(sp);
                        continue;
                    }
                    // reactions: a face zoom (sparingly) or a punch-in
                    var reacts = Reactions(ca, cb, words, s, medLoud);
                    double pos = ca;
                    foreach (var (rt, strong) in reacts)
                    {
                        bool face = cam is not null && strong && rt - lastFace > 35 && rt - 0.2 - pos > 1.2 && cb - (rt + 1.6) > 0.8;
                        if (!face) continue;
                        momentPieces.Add(new Piece { Role = "moment", In = pos, Out = rt - 0.2 });
                        var fp = new Piece { Role = "face", In = rt - 0.2, Out = rt + 1.4, Crop = cam };
                        fp.Zooms.Add(new Zoom(0, 1.6, 1.06));
                        momentPieces.Add(fp);
                        pos = rt + 1.4;
                        lastFace = rt;
                    }
                    var mp = new Piece { Role = "moment", In = pos, Out = cb };
                    foreach (var (rt, _) in reacts.Where(r => r.Time > pos + 0.3 && r.Time < cb - 0.8))
                    {
                        if (tw.Zooms == 0) break;
                        if (mp.Zooms.Count > 0 && rt - (pos + mp.Zooms[^1].At) < zoomGap) continue;
                        mp.Zooms.Add(new Zoom(rt - pos - 0.08, Math.Min(2.4, cb - rt), 1.12 + zoomAdd + 0.04 * (punches++ % 3)));
                    }
                    if (tw.Zooms > 0 && mp.Zooms.Count == 0 && mp.Out - mp.In > 10) mp.Push = rng.NextDouble() < 0.6 ? 0.07 : -0.06;
                    momentPieces.Add(mp);
                }
            }
            momentPieces.RemoveAll(p => p.Out - p.In < 0.5);
            if (momentPieces.Count == 0) continue;

            foreach (var p in momentPieces) p.Shake = p.Role == "slow" && tw.Shake;
            // tag pieces with their moment index for chapter/music decisions
            foreach (var p in momentPieces) _momentIndex[p] = i;
            plan.Pieces.AddRange(momentPieces);
        }

        // ---------------------------------------------------------------- cuts between clips: plain, unless you asked for a transition
        // (the editor still works out what would suit each cut: shown in the review, and used for "varied")
        var cutPlans = new List<(int Moment, Piece Last, Piece First, string Style, string Sound, string Suggested, string SuggestedSound, string Why, List<string> Options, Piece? Lapse)>();
        {
            string prevStyle = "";
            int variety = 0;
            for (int i = 1; i < chosen.Count; i++)
            {
                int lastIdx = plan.Pieces.FindLastIndex(p => _momentIndex.TryGetValue(p, out var mi) && mi < i);
                int firstIdx = plan.Pieces.FindIndex(p => _momentIndex.TryGetValue(p, out var mi) && mi == i);
                if (lastIdx < 0 || firstIdx < 0 || firstIdx != lastIdx + 1) continue;
                var last = plan.Pieces[lastIdx]; var first = plan.Pieces[firstIdx];
                double gapFrom = last.Out, gapTo = first.In, gap = gapTo - gapFrom;
                bool newChapter = chapterOf[i] != chapterOf[i - 1];
                // a timelapse needs the time between to be one stretch of the same game (never through a lobby)
                // (Among Us: gameplay, never a lobby, and not the talk of a meeting: that's cut, not fast-forwarded)
                bool lapseOk = gap >= 6 && gap <= 600;
                if (lapseOk && amongUs)
                {
                    int play = 0, all = 0;
                    for (int b = Math.Max(0, (int)(gapFrom / Bin)); b < Math.Min(n, (int)(gapTo / Bin) + 1); b++)
                    {
                        all++;
                        if (phase[b] == PhaseKind.Idle) { lapseOk = false; break; }
                        if (phase[b] == PhaseKind.Play) play++;
                    }
                    if (play < all * 0.7) lapseOk = false;
                }
                PhaseKind PhaseAt(double t) => amongUs ? phase[Math.Clamp((int)(t / Bin), 0, n - 1)] : PhaseKind.Play;
                // what the outgoing clip ends on, what the incoming one starts on
                PhaseKind leaving = PhaseAt(chosen[i - 1].From + 1), arriving = PhaseAt(chosen[i].From + 1);
                bool intoMeeting = amongUs && leaving == PhaseKind.Play && arriving == PhaseKind.Meeting;
                bool outOfMeeting = amongUs && leaving == PhaseKind.Meeting && arriving == PhaseKind.Play;
                bool inMeeting = amongUs && leaving == PhaseKind.Meeting && arriving == PhaseKind.Meeting;
                bool intoDeath = first.Role == "slow" || s.Markers.Any(m => m.Kind == "death" && m.At >= gapTo && m.At < gapTo + 3);
                // what suits this cut, best first, and why
                var options = new List<string>();
                string why;
                if (amongUs && lapseOk && !intoMeeting && !outOfMeeting) { options.Add(TransitionKinds.Timelapse); why = $"{gap:0} s of the same round in between: fast-forward through it"; }
                else if (intoMeeting) { options.Add(TransitionKinds.Flash); why = "a meeting is called"; }
                else if (outOfMeeting) { options.Add(TransitionKinds.Fade); why = "back to the game after the vote"; }
                else if (inMeeting) { options.Add(TransitionKinds.None); options.Add(TransitionKinds.Blur); why = "within the meeting: a plain cut keeps the conversation flowing"; }
                else if (intoDeath) { options.Add(TransitionKinds.Zoom); why = "straight into a kill"; }
                else if (gap >= 300) { options.Add(TransitionKinds.Fade); why = $"{gap / 60:0} minutes later"; }
                else if (gap >= 45 && !newChapter) { options.Add(TransitionKinds.Whip); why = $"a jump of {gap:0} s"; }
                else if (lapseOk && gap >= 20) { options.Add(TransitionKinds.Timelapse); why = $"{gap:0} s in between: fast-forward through it"; }
                else { options.Add(TransitionKinds.None); options.Add(TransitionKinds.Zoom); why = "a short skip: a plain cut keeps it moving"; }
                foreach (var st in new[] { TransitionKinds.Whip, TransitionKinds.Fade, TransitionKinds.Zoom, TransitionKinds.Flash, TransitionKinds.Glitch, TransitionKinds.Blur, TransitionKinds.Timelapse, TransitionKinds.None })
                    if (!options.Contains(st) && (st != TransitionKinds.Timelapse || lapseOk)) options.Add(st);
                string suggested = options[0];
                // "varied": the suggestion, but never the same look twice in a row
                // (you asked for transitions: where a plain cut would suit best, the next best look; never the same twice running)
                var looks = options.Where(o => o != TransitionKinds.None).ToList();
                string variedPick = looks.FirstOrDefault(o => o != prevStyle) ?? looks[0];
                if (variedPick == prevStyle) variedPick = looks[(1 + variety++) % looks.Count];
                var pick = tw.Cuts.FirstOrDefault(c => Math.Abs(c.From - gapTo) < 0.5);
                string style = pick?.Style ?? (tw.TransitionStyle == TransitionKinds.Mixed ? variedPick : tw.TransitionStyle);
                if (style == TransitionKinds.Timelapse && !lapseOk) style = gap >= 45 ? TransitionKinds.Fade : TransitionKinds.None; // nothing to fast-forward (or a lobby in between)
                if (!TransitionKinds.Styles.Contains(style)) style = TransitionKinds.None;
                string suggestedSound = TransitionKinds.SoundFor(suggested);
                string[] soundCycle = { "whoosh", "soft", "pop", "riser", "impact" };
                string sound = pick?.Sound ?? tw.TransitionSound switch
                {
                    "auto" => TransitionKinds.SoundFor(style),
                    TransitionKinds.Mixed => style == TransitionKinds.None ? "none" : soundCycle[cutPlans.Count % soundCycle.Length],
                    var x => style == TransitionKinds.None && pick?.Sound is null ? "none" : x,
                };
                if (!tw.TransitionSounds) sound = "none";
                Piece? lapse = null;
                if (style == TransitionKinds.Timelapse)
                {
                    // the time between, fast: ~1/10 of it, 2–6 s
                    double len = Math.Clamp(gap / 10, Math.Min(2, gap / 3), 6);
                    lapse = new Piece { Role = "lapse", In = gapFrom, Out = gapTo, Speed = gap / len, Mute = true };
                    plan.Pieces.Insert(firstIdx, lapse);
                    _momentIndex[lapse] = i;
                }
                else if (style != TransitionKinds.None)
                {
                    last.TransOut = style; first.TransIn = style;
                    if (style == TransitionKinds.Whip) { last.WhipOut = true; first.WhipIn = true; }
                }
                prevStyle = style;
                cutPlans.Add((i, last, lapse ?? first, style, sound, suggested, suggestedSound, why, options, lapse));
            }
        }

        // ---------------------------------------------------------------- VAR checks (Among Us): you explain where you were, the replay shows it
        var varCues = new List<(Piece Piece, PieceOverlay Ov)>();
        if (amongUs && tw.VarChecks && s.WordsFromMic && words.Count > 0)
        {
            // your own words (the mic track only: never another player's) claiming where you were or what you did
            var claim = new System.Text.RegularExpressions.Regex(@"^(i was|i went|i came|i did|i walked|i saw|i stayed|i ran|i followed|i fixed|i scanned|my tasks?|i had|i finished|i started|i left)$|^(electrical|electric|medbay|med bay|admin|cafeteria|cafe|navigation|nav|o2|oxygen|shields|weapons|reactor|security|storage|comms|communications|engine|specimen|laboratory|lab|office|decontam|boiler|dropship|vault|kitchen|cockpit|records|lounge|showers|greenhouse|garage|crates)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            double lastVar = -999;
            int perMeetingCount = 0; double meetingStartSeen = -1;
            for (int k = 0; k + 1 < words.Count; k++)
            {
                double tc = words[k].Start;
                int b = Math.Clamp((int)(tc / Bin), 0, n - 1);
                if (phase[b] != PhaseKind.Meeting) continue;
                string pair = (words[k].Text + " " + words[k + 1].Text).ToLowerInvariant().Trim('.', ',', '!', '?');
                string one = words[k].Text.ToLowerInvariant().Trim('.', ',', '!', '?');
                string before = k > 0 ? words[k - 1].Text.ToLowerInvariant().Trim('.', ',', '!', '?') : "";
                // "I was…", "I went…", or a room said as a place ("in electrical", "to admin"), not just the word
                bool isClaim = claim.IsMatch(pair) && pair.StartsWith("i") || claim.IsMatch(one) && one.StartsWith("my")
                               || claim.IsMatch(one) && before is "in" or "at" or "to" or "from" or "by" or "near" or "the" or "through";
                if (!isClaim) continue;
                static bool isClaimAfterPrep(string b) => b is "in" or "at" or "to" or "from" or "by" or "near" or "through";
                // the meeting this is in, and the round of play just before it (what you did)
                int ms = b; while (ms > 0 && phase[ms - 1] == PhaseKind.Meeting) ms--;
                if (ms != (int)(meetingStartSeen / Bin)) { meetingStartSeen = ms * Bin; perMeetingCount = 0; }
                if (perMeetingCount >= 2 || tc - lastVar < 20) continue;
                int pe = ms; while (pe > 0 && phase[pe - 1] != PhaseKind.Play) pe--;
                int ps = pe; while (ps > 0 && phase[ps - 1] == PhaseKind.Play) ps--;
                double roundFrom = ps * Bin, roundTo = pe * Bin;
                if (roundTo - roundFrom < 6) continue;
                // (a round starts with the ejection and the role screen: the replay starts after them)
                if (roundTo - roundFrom >= 20) roundFrom += Math.Min(8, (roundTo - roundFrom) * 0.15);
                var host = plan.Pieces.FirstOrDefault(p => p.Role == "moment" && p.Speed == 1 && p.Overlay is null && tc >= p.In && tc <= p.Out - 3);
                if (host is null) continue;
                double start = Math.Max(0, tc - host.In - 0.2), len = Math.Min(8, host.Out - host.In - start - 0.3);
                if (len < 3) continue;
                // what you said, as the replay's caption (up to the end of the sentence)
                var said = words.Skip(Math.Max(0, k - (isClaimAfterPrep(before) ? 1 : 0))).TakeWhile(w => w.Start < tc + len).Take(7).Select(w => w.Text);
                var ov = new PieceOverlay(roundFrom, roundTo, start, len, Captions.Tidy(string.Join(' ', said)));
                host.Overlay = ov;
                varCues.Add((host, ov));
                lastVar = tc; perMeetingCount++;
            }
        }

        // ---------------------------------------------------------------- end card: the last frame, frozen and blurred
        var lastMoment = plan.Pieces.Last(p => p.Role is "moment" or "slow" or "face");
        // (a frame safely inside the file: the very last instant may have no picture)
        double endFrame = Math.Max(0, Math.Min(lastMoment.Out, s.Duration - 0.6) - 0.15);
        bool endScreen = brand.EndScreen && tw.EndScreen;
        // the end screen: 20 s (YouTube's most), laid out for its elements: previous video, next video, subscribe
        plan.Pieces.Add(new Piece { Role = "end", In = endFrame, Out = endFrame + 0.1, Hold = endScreen ? 20 : 10, Blur = true, Darken = endScreen, Mute = true });
        if (!s.HasAudio) foreach (var p in plan.Pieces) p.Mute = true;

        // ---------------------------------------------------------------- the timeline
        double at = 0;
        foreach (var p in plan.Pieces) { p.At = at; at += plan.FramesOf(p) / plan.Fps; } // whole frames, so nothing drifts

        foreach (var c in cutPlans)
        {
            double cutAt = c.First.At;
            if (TransitionKinds.Kind(c.Sound) is { } kind)
                plan.Sfx.Add(new SfxCue(kind is SfxKind.Riser ? cutAt - 0.9 : kind is SfxKind.Whoosh or SfxKind.SoftWhoosh ? cutAt - 0.22 : cutAt, kind, kind is SfxKind.Impact ? 0.8f : 0.7f));
            plan.Cuts.Add(new CutInfo(cutAt, c.Lapse?.Out ?? c.First.In, c.Style, c.Sound, c.Suggested, c.SuggestedSound, c.Why, c.Options));
            if (c.Lapse is { } lp) plan.Texts.Add(new TextCue("lapse", lp.At + 0.1, Math.Max(0.5, lp.Length - 0.2), $"▶▶ {lp.Speed:0}×"));
        }
        foreach (var (host, ov) in varCues)
        {
            plan.Texts.Add(new TextCue("var", host.At + ov.Start, ov.Length, "VAR CHECK", ov.Quote.Length > 0 ? $"“{ov.Quote}”" : null));
            plan.Sfx.Add(new SfxCue(host.At + ov.Start - 0.5, SfxKind.Riser, 0.55f));
            plan.Sfx.Add(new SfxCue(host.At + ov.Start, SfxKind.Pop, 0.6f));
        }

        // title, chapters, "later" tags
        var titlePiece = plan.Pieces.First(p => p.Role == "title");
        plan.Texts.Add(new TextCue("title", titlePiece.At, titlePiece.Length, o.Title, DateTime.Now.ToString("MMMM d, yyyy")));
        plan.Chapters.Add((0, "Intro"));
        int[] chapterStartMoment = Enumerable.Range(0, chapters).Select(c => Array.IndexOf(chapterOf, c)).ToArray();
        var usedTopics = new List<string>();
        var chapterStart = new double[chapters];
        for (int c = 0; c < chapters; c++)
        {
            var firstPiece = plan.Pieces.FirstOrDefault(p => _momentIndex.TryGetValue(p, out var mi) && mi == chapterStartMoment[c]);
            chapterStart[c] = firstPiece?.At ?? titlePiece.At + titlePiece.Length;
            var ranges = chosen.Where((_, k) => chapterOf[k] == c).Select(x => (x.From, x.To)).ToList();
            string quote = BestQuote(words, ranges);
            // named after where you are when you keep saying it (Khrysalis, Darkmoor…), else your best line
            string? place = Topic(words, ranges, c > 0 ? usedTopics : null);
            if (place is not null) usedTopics.Add(place);
            int firstMoment = chapterStartMoment[c];
            string? phaseName = episodeNames is not null && firstMoment >= 0 && episodeNames.TryGetValue(segEpisode[firstMoment], out var pn) ? pn : null;
            string name = phaseName ?? place ?? (quote.Length > 0 ? $"“{quote}”" : $"Part {c + 1}");
            if (c == 0) { plan.Chapters.Add((chapterStart[0], phaseName is not null || place is not null || quote.Length > 0 ? name : "Part 1")); continue; }
            // your own card text when you changed it
            var card = tw.Cards.FirstOrDefault(x => x.Chapter == c + 1);
            string? sub = !string.IsNullOrWhiteSpace(card?.Sub) ? card!.Sub!.Trim() : place is not null || quote.Length > 0 ? name : null;
            string head = !string.IsNullOrWhiteSpace(card?.Heading) ? card!.Heading!.Trim().ToUpperInvariant()
                : phaseName is not null ? phaseName.ToUpperInvariant() : $"PART {c + 1}";
            if (phaseName is not null && card?.Sub is null) sub = quote.Length > 0 ? $"“{quote}”" : null;
            plan.Chapters.Add((chapterStart[c], phaseName ?? sub ?? name));
            if (!o.Chapters) continue; // chapters off: no cards on screen (and none in the description)
            plan.Texts.Add(new TextCue("chapter", chapterStart[c] + 0.15, 2.6, head, sub));
            plan.Sfx.Add(new SfxCue(chapterStart[c] - 0.05, SfxKind.SoftWhoosh, 0.7f));
            plan.Sfx.Add(new SfxCue(chapterStart[c] + 0.25, SfxKind.Pop, 0.6f));
        }
        for (int i = 1; i < chosen.Count; i++)
        {
            double gap = chosen[i].From - chosen[i - 1].To;
            if (gap < 300) continue;
            var fp = plan.Pieces.FirstOrDefault(p => _momentIndex.TryGetValue(p, out var mi) && mi == i);
            if (fp is null || fp.Role == "lapse") continue;
            string later = gap >= 3300 ? $"{Math.Round(gap / 3600.0, 1):0.#} HOURS LATER".Replace("1 HOURS", "AN HOUR") : $"{Math.Round(gap / 60)} MINUTES LATER";
            plan.Texts.Add(new TextCue("later", fp.At + 0.1, 2.0, later));
            if (!fp.WhipIn) plan.Sfx.Add(new SfxCue(fp.At, SfxKind.SoftWhoosh, 0.6f));
        }
        if (!o.Chapters) plan.Chapters.Clear();
        plan.Texts.Add(new TextCue(endScreen ? "endscreen" : "end", plan.Pieces[^1].At + 0.3, plan.Pieces[^1].Length - 0.3, "THANKS FOR WATCHING", o.Socials));
        // the hook's line: your best words of the stream, over the run-up to the best moment
        if (hook && plan.Pieces.FirstOrDefault(p => p.Role == "cold" && ReferenceEquals(p, cold)) is { } cp)
        {
            string line = BestQuote(words, new() { (best.From, best.To) });
            if (line.Length > 0) plan.Texts.Add(new TextCue("hook", cp.At + 0.35, Math.Max(1, cp.Length - 0.45), $"“{line}”"));
            foreach (var hs in hookShots) plan.Sfx.Add(new SfxCue(hs.At, SfxKind.Impact, 0.7f, Seed: 7101));
            plan.Sfx.Add(new SfxCue(cp.At, SfxKind.Impact, 0.75f, Seed: 7101));
        }
        plan.MusicVolume = tw.MusicVolume;
        plan.SfxVolume = tw.SfxVolume;
        for (int i = 0; i < chosen.Count; i++)
        {
            var fp = plan.Pieces.FirstOrDefault(p => _momentIndex.TryGetValue(p, out var mi) && mi == i);
            if (fp is null) continue;
            plan.Moments.Add(new EditMoment(chosen[i].From, chosen[i].To, fp.At, BestQuote(words, new() { (chosen[i].From, chosen[i].To) }) is { Length: > 0 } q ? q
                : Captions.Tidy(string.Join(' ', words.Where(w => w.Start >= chosen[i].From && w.Start < chosen[i].To).Take(8).Select(w => w.Text)))));
        }

        // ---------------------------------------------------------------- sound design
        // the intro's sting: the same sounds every video (fixed seeds), so the intro is recognisable
        plan.Sfx.Add(new SfxCue(titlePiece.At - 1.35, SfxKind.Riser, 0.75f, Seed: 7001));
        plan.Sfx.Add(new SfxCue(titlePiece.At, SfxKind.Impact, 0.9f, Seed: 7002));
        plan.Sfx.Add(new SfxCue(titlePiece.At + 0.35, SfxKind.Shimmer, 0.6f, Seed: 7003));
        foreach (var p in plan.Pieces)
        {
            if (p.WhipIn) plan.Sfx.Add(new SfxCue(p.At - 0.22, SfxKind.Whoosh, 0.8f));
            if (p.Role == "slow") { plan.Sfx.Add(new SfxCue(p.At, SfxKind.Impact, 0.85f)); plan.Sfx.Add(new SfxCue(p.At + 0.05, SfxKind.SubDrop, 0.7f)); }
            if (p.Role == "face") plan.Sfx.Add(new SfxCue(p.At, SfxKind.Pop, 0.55f));
        }
        plan.Sfx.Add(new SfxCue(plan.Pieces[^1].At, SfxKind.SoftWhoosh, 0.6f));
        if (!tw.TransitionSounds)
        {
            // only the slow-motion hits stay (they're the moment's own sound, not a transition)
            var slowStarts = plan.Pieces.Where(p => p.Role == "slow").Select(p => p.At).ToList();
            plan.Sfx.RemoveAll(c => !(c.Kind is SfxKind.Impact or SfxKind.SubDrop && slowStarts.Any(a => Math.Abs(c.At - a) < 0.1))
                                    && !varCues.Any(v => Math.Abs(c.At - (v.Piece.At + v.Ov.Start)) < 0.6));
        }

        // ---------------------------------------------------------------- loudness: every part at the same level
        plan.EvenLoudness = tw.EvenLoudness;
        if (tw.EvenLoudness && s.HasAudio && s.Loud.Length > 0)
        {
            // each piece's speaking level: the loud end of its sound (quiet gaps don't count)
            double Level(Piece p)
            {
                var v = new List<double>();
                for (double t = p.In; t < p.Out; t += 0.1) { double l = s.LoudAt(t); if (l > -50) v.Add(l); }
                if (v.Count < 5) return double.NaN;
                v.Sort();
                return v[(int)(v.Count * 0.8)];
            }
            var sounding = plan.Pieces.Where(p => !p.Mute && p.Role is "cold" or "moment" or "slow" or "face").ToList();
            var levels = sounding.Select(p => (p, L: Level(p))).Where(x => !double.IsNaN(x.L)).ToList();
            if (levels.Count >= 2)
            {
                // the target: the length-weighted middle of all parts
                var byLevel = levels.OrderBy(x => x.L).ToList();
                double total = byLevel.Sum(x => x.p.Length), acc = 0, middle = byLevel[^1].L;
                foreach (var x in byLevel) { acc += x.p.Length; if (acc >= total / 2) { middle = x.L; break; } }
                foreach (var (p, l) in levels) p.GainDb = Math.Clamp(middle - l, -10, 8);
            }
        }

        // ---------------------------------------------------------------- music: a track per chapter, cut for deaths
        if (o.Music)
        {
            bool InChapter(int c, double t) => chosen.Where((_, k) => chapterOf[k] == c).Any(x => t >= x.From && t <= x.To);
            var moods = ChapterMoods(chapters, c => chosen.Where((_, k) => chapterOf[k] == c).Select(x => x.Value).DefaultIfEmpty(0).Average(),
                                     c => s.Markers.Any(m => m.Kind is "death" or "fight" && InChapter(c, m.At)),
                                     c => words.Count(w => Laughs.Contains(w.Text.ToLowerInvariant()) && InChapter(c, w.Start)));
            // a different style for every chapter (fitting its mood), never the same twice in a video
            var used = new HashSet<MusicStyle>();
            var styleRng = new Random((int)(s.Duration * 13) ^ chapters);
            MusicStyle StyleFor(Mood m)
            {
                var options = MusicSynth.StylesFor(m).Where(x => !used.Contains(x)).ToArray();
                if (options.Length == 0) options = Enum.GetValues<MusicStyle>().Where(x => !used.Contains(x)).DefaultIfEmpty(MusicSynth.StylesFor(m)[0]).ToArray();
                var pick = options[styleRng.Next(options.Length)];
                used.Add(pick);
                return pick;
            }
            var styles = moods.Select(StyleFor).ToArray();
            double end = plan.Duration;
            for (int c = 0; c < chapters; c++)
            {
                double from = c == 0 ? titlePiece.At : chapterStart[c];
                double to = c + 1 < chapters ? chapterStart[c + 1] : end;
                // a death stops the music (tape stop); it starts again after
                var stops = plan.Pieces.Where(p => p.Role == "slow" && p.At > from && p.At < to).Select(p => (p.At, End: p.At + p.Length)).ToList();
                double cur = from;
                int seed = (int)(from * 7919) ^ c;
                foreach (var (sa, se) in stops)
                {
                    if (sa - cur > 2) plan.Music.Add(new MusicSpan(cur, sa + 0.6, moods[c], seed, true, styles[c]));
                    cur = se;
                    seed += 31;
                }
                if (to - cur > 2) plan.Music.Add(new MusicSpan(cur, to, moods[c], seed, false, styles[c]));
            }
        }

        // ---------------------------------------------------------------- captions on the timeline
        if (o.Captions) PlaceCaptions(plan, words);
        return plan;
    }

    /// <summary>Where captions are needed (source seconds): the pieces that keep their own sound.</summary>
    public static IEnumerable<Piece> CaptionedPieces(EditPlan plan) => plan.Pieces.Where(p => p.Role is "cold" or "moment" or "face");

    /// <summary>Lays the words said in each captioned piece onto the finished video's timeline (replacing any before).</summary>
    public static void PlaceCaptions(EditPlan plan, IReadOnlyList<VodWord> sourceWords)
    {
        var t0 = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var words = Captions.Dedupe(sourceWords.Select(w => new CaptionWord(w.Text, t0.AddSeconds(w.Start), t0.AddSeconds(w.End))))
            .Select(w => new VodWord(w.Text, (w.StartUtc - t0).TotalSeconds, (w.EndUtc - t0).TotalSeconds)).ToList();
        plan.Captions.Clear();
        foreach (var p in CaptionedPieces(plan))
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                if (w.Start < p.In || w.Start >= p.Out) continue;
                double st = p.At + (w.Start - p.In), en = p.At + Math.Min(w.End, p.Out) - p.In;
                plan.Captions.Add(new CaptionCue(w.Text, st, Math.Max(st + 0.12, en), WordWeight(words, i) >= 1.5));
            }
    }

    private static readonly HashSet<string> Laughs = new() { "haha", "hahaha", "ha", "lol", "lmao", "funny", "hilarious", "laughing", "hehe" };

    private static Mood[] ChapterMoods(int chapters, Func<int, double> energy, Func<int, bool> fights, Func<int, int> laughs)
    {
        var e = Enumerable.Range(0, chapters).Select(energy).ToArray();
        double med = e.OrderBy(x => x).ElementAt(e.Length / 2);
        var moods = new Mood[chapters];
        for (int c = 0; c < chapters; c++)
        {
            moods[c] = fights(c) ? Mood.Tense : laughs(c) >= 3 ? Mood.Fun : e[c] >= med ? Mood.Hype : Mood.Chill;
            if (c > 0 && moods[c] == moods[c - 1])
                moods[c] = moods[c] switch { Mood.Hype => Mood.Epic, Mood.Chill => Mood.Fun, Mood.Tense => Mood.Epic, Mood.Fun => Mood.Chill, _ => Mood.Hype };
        }
        if (chapters > 1) moods[^1] = moods[^2] == Mood.Epic ? Mood.Hype : Mood.Epic;
        return moods;
    }

    /// <summary>Where to cut inside a moment: drop stretches with nobody talking, little happening and nothing
    /// marked, leaving a beat either side so it doesn't feel chopped.</summary>
    private static List<(double, double)> JumpCuts(double from, double to, List<VodWord> words, VodSignals s, List<VodMarker> markers, double peak, double minGap = 0.8, double? silenceBelow = null)
    {
        var inside = words.Where(w => w.End > from && w.Start < to).OrderBy(w => w.Start).ToList();
        var spans = new List<(double, double)>();
        double cur = from;
        double prevEnd = from;
        foreach (var w in inside.Append(new VodWord("", to, to)))
        {
            double gapA = prevEnd + 0.2, gapB = w.Start - 0.2;
            if (gapB - gapA > minGap)
            {
                double still = 0; int k = 0;
                for (double t = gapA; t < gapB; t += 0.5) { still += s.MotionAt(t); k++; }
                bool quiet = k > 0 && still / k < 0.035;
                // (when told: only a real silence goes, never someone talking or the game going off)
                if (quiet && silenceBelow is double sb)
                    for (double t = gapA; t < gapB && quiet; t += 0.1) if (s.LoudAt(t) > sb) quiet = false;
                bool important = Math.Abs(peak - (gapA + gapB) / 2) < 3 || markers.Any(m => m.Kind is "death" or "clip" && m.At > gapA - 2 && m.At < gapB + 2);
                if (quiet && !important)
                {
                    if (gapA - cur > 0.6) spans.Add((cur, gapA));
                    cur = gapB;
                }
            }
            prevEnd = Math.Max(prevEnd, w.End);
        }
        if (to - cur > 0.6) spans.Add((cur, to));
        if (spans.Count == 0) spans.Add((from, to));
        return spans;
    }

    public enum PhaseKind : byte { Play, Meeting, Idle }

    /// <summary>
    /// What's on screen every half second: an Among Us meeting (its tablet's green frame), the game itself, or a
    /// lobby / menu (the picture barely changes for a long while). <paramref name="amongUs"/>: meetings were seen,
    /// so the edit is built from rounds and meetings.
    /// </summary>
    public static PhaseKind[] GamePhases(VodSignals s, int n, out bool amongUs)
    {
        var ph = new PhaseKind[n];
        var meet = new bool[n];
        for (int b = 0; b < n; b++) meet[b] = s.MeetingAt(b * Bin) >= 0.15;
        // a map or chat opened during a meeting hides the frame for a while (people check the map for 15–20 s):
        // still the meeting. A real round between two meetings is longer (the ejection alone takes ~10 s).
        int lastMeet = -999;
        for (int b = 0; b < n; b++)
        {
            if (!meet[b]) continue;
            if (b - lastMeet > 1 && b - lastMeet <= 44) for (int k = lastMeet + 1; k < b; k++) meet[k] = true;
            lastMeet = b;
        }
        // a flash of green isn't a meeting: at least 6 s
        for (int b = 0; b < n;)
        {
            if (!meet[b]) { b++; continue; }
            int e = b; while (e < n && meet[e]) e++;
            if (e - b < 12) for (int k = b; k < e; k++) meet[k] = false;
            b = e;
        }
        amongUs = meet.Count(x => x) >= 20;
        if (!amongUs) return ph;
        // the lobby ("WAITING FOR HOST"): never in the video. A moment where it wasn't recognized (someone walked
        // over the box) between lobby frames is still the lobby, and so is a second either side of it.
        var lobby = new bool[n];
        for (int b = 0; b < n; b++) lobby[b] = s.LobbyAt(b * Bin);
        int lastLobby = -999;
        for (int b = 0; b < n; b++)
        {
            if (!lobby[b]) continue;
            if (b - lastLobby > 1 && b - lastLobby <= 20) for (int k = lastLobby + 1; k < b; k++) lobby[k] = true;
            lastLobby = b;
        }
        var near = (bool[])lobby.Clone();
        for (int b = 0; b < n; b++) if (lobby[b]) for (int k = Math.Max(0, b - 2); k <= Math.Min(n - 1, b + 2); k++) near[k] = true;
        for (int b = 0; b < n; b++)
            ph[b] = near[b] && !meet[b] ? PhaseKind.Idle : meet[b] ? PhaseKind.Meeting : PhaseKind.Play;
        // a few seconds of play between a meeting and the lobby (the game ending) go with the lobby
        for (int b = 0; b < n;)
        {
            int e = b; while (e < n && ph[e] == ph[b]) e++;
            if (ph[b] == PhaseKind.Play && e - b < 12 && e < n && ph[e] == PhaseKind.Idle && b > 0 && ph[b - 1] != PhaseKind.Play)
                for (int k = b; k < e; k++) ph[k] = PhaseKind.Idle;
            b = e;
        }
        return ph;
    }

    /// <summary>
    /// An Among Us edit: the game itself (each round's best stretches: moving around, tasks, kills, reports) and the
    /// meetings' best moments (accusations, everyone talking at once, the big reactions), always with how each
    /// meeting ended (the vote and the ejection). The lobby is left out.
    /// </summary>
    private static void PickGameAndMeetings(List<(int A, int B)> episodes, PhaseKind[] phase, double[] smooth, bool[] still, double[] motion,
        VodSignals s, List<VodWord> allWords, double budget, double pace, Func<double, double, double, double, int, bool> addSeg, Func<double> len)
    {
        int n = phase.Length;
        double playTotal = 0, meetTotal = 0;
        foreach (var (a, b) in episodes) { if (phase[a] == PhaseKind.Meeting) meetTotal += (b - a) * Bin; else playTotal += (b - a) * Bin; }
        // the game gets at least a third of the video (more when there's more of it), never more than it has
        double playShare = playTotal + meetTotal <= 0 ? 0.5 : Math.Clamp(playTotal / (playTotal + meetTotal), 0.35, 0.65);
        double playBudget = Math.Min(playTotal * 0.9, budget * playShare);
        double meetBudget = budget - playBudget;
        double playLen = 0, meetLen = 0;

        // the best window around a peak, cut between sentences
        (double From, double To) Window(double peak, double before, double after, int a, int b)
        {
            double from = SentenceStart(allWords, Math.Max(a * Bin, peak - before));
            double to = SentenceEnd(allWords, Math.Min(b * Bin, peak + after), s.Duration);
            return (Math.Max(a * Bin - 0.5, from), Math.Min(b * Bin + 1, to));
        }
        // best bins of an episode not yet in the edit
        var taken = new bool[n];
        int BestBin(int a, int b)
        {
            int best = -1;
            for (int k = a; k < b; k++) if (!taken[k] && (best < 0 || smooth[k] > smooth[best])) best = k;
            return best;
        }
        void Take(double from, double to) { for (int k = Math.Max(0, (int)(from / Bin) - 6); k < Math.Min(n, (int)(to / Bin) + 6); k++) taken[k] = true; }
        bool Add(double from, double to, double peak, double value, int e, bool play)
        {
            double before = len();
            if (!addSeg(from, to, peak, value, e)) return false;
            Take(from, to);
            double added = Math.Max(0, len() - before);
            if (play) playLen += added; else meetLen += added;
            return true;
        }

        // 1. how every meeting ended: the vote coming in, and the ejection after it (the payoff of the argument)
        for (int e = 0; e < episodes.Count; e++)
        {
            var (a, b) = episodes[e];
            if (phase[a] != PhaseKind.Meeting) continue;
            double end = b * Bin;
            // the ejection after it, up to where the lobby starts (a game that ended there)
            int stop = b; while (stop < n && stop < b + 22 && phase[stop] != PhaseKind.Idle) stop++;
            Add(SentenceStart(allWords, end - 7), Math.Min(s.Duration, stop * Bin), end, 30, e, play: false);
        }
        // 2. one stretch of every round of play, and the hottest moment of every meeting
        for (int e = 0; e < episodes.Count; e++)
        {
            var (a, b) = episodes[e];
            bool play = phase[a] != PhaseKind.Meeting;
            if ((b - a) * Bin < 8) continue;
            int pk = BestBin(a, b);
            if (pk < 0) continue;
            var (f, t) = play ? Window(pk * Bin, 9 * pace, 11 * pace, a, b) : Window(pk * Bin, 8 * pace, 7 * pace, a, b);
            Add(f, t, pk * Bin, 20 + smooth[pk], e, play);
        }
        // 3. then the best of what's left, game and meetings each up to their share
        for (int guard = 0; guard < 60 && len() < budget; guard++)
        {
            bool wantPlay = playLen < playBudget && (meetLen >= meetBudget || playLen / Math.Max(1, playBudget) <= meetLen / Math.Max(1, meetBudget));
            int bestE = -1, bestK = -1;
            for (int e = 0; e < episodes.Count; e++)
            {
                var (a, b) = episodes[e];
                if ((phase[a] != PhaseKind.Meeting) != wantPlay) continue;
                int k = BestBin(a, b);
                if (k >= 0 && (bestK < 0 || smooth[k] > smooth[bestK])) { bestE = e; bestK = k; }
            }
            if (bestK < 0)
            {
                if (wantPlay) playBudget = playLen; else meetBudget = meetLen; // nothing left of that kind
                if (playLen >= playBudget && meetLen >= meetBudget) break;
                continue;
            }
            var (ea, eb) = episodes[bestE];
            var (f, t) = wantPlay ? Window(bestK * Bin, 7 * pace, 9 * pace, ea, eb) : Window(bestK * Bin, 6 * pace, 6 * pace, ea, eb);
            if (!Add(f, t, bestK * Bin, 10 + smooth[bestK], bestE, wantPlay)) Take(bestK * Bin - 2, bestK * Bin + 2);
        }
    }

    /// <summary>Reaction moments inside a stretch: a hype word, or the voice jumping well above its usual level.</summary>
    private static List<(double Time, bool Strong)> Reactions(double a, double b, List<VodWord> words, VodSignals s, double medLoud)
    {
        var list = new List<(double, bool)>();
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (w.Start < a || w.Start >= b) continue;
            double wt = WordWeight(words, i);
            double lz = (s.LoudAt(w.Start + 0.1) - medLoud) / 6.0;
            if (wt >= 2 || wt >= 1 && lz > 1.2 || lz > 2.2) list.Add((w.Start, wt >= 2.5 || lz > 2.4));
        }
        return list;
    }

    private static readonly HashSet<string> NotTopics = new(StringComparer.OrdinalIgnoreCase) { "Wizard101", "tasks", "task", "crew", "crewmate", "imp" };

    /// <summary>The place or name said most in these stretches (at least twice), if it's one Leapcast knows.</summary>
    private static string? Topic(List<VodWord> words, List<(double From, double To)> ranges, List<string>? avoidRepeat)
    {
        var counts = new Dictionary<string, int>();
        foreach (var w in words)
        {
            if (!ranges.Any(r => w.Start >= r.From - 20 && w.Start <= r.To + 20)) continue;
            if (Vocabulary.NameOf(w.Text) is { Length: >= 4 } name && !NotTopics.Contains(name)) counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        var top = counts.Where(x => x.Value >= 2).OrderByDescending(x => x.Value).Select(x => x.Key).ToList();
        if (avoidRepeat is { Count: > 0 } && top.FirstOrDefault() is { } t0 && avoidRepeat[^1] == t0) return null; // same place as the last chapter: a quote reads better
        return top.FirstOrDefault();
    }

    private static string BestQuote(List<VodWord> words, List<(double From, double To)> ranges)
    {
        double best = 0; string quote = "";
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (!ranges.Any(r => w.Start >= r.From && w.Start <= r.To)) continue;
            double wt = WordWeight(words, i);
            if (wt <= best) continue;
            // the sentence around it, at most 7 words
            int a = i, b = i;
            while (a > 0 && i - a < 3 && words[a].Start - words[a - 1].End < 0.6) a--;
            while (b + 1 < words.Count && b - a < 6 && words[b + 1].Start - words[b].End < 0.6) b++;
            var text = string.Join(' ', words.Skip(a).Take(b - a + 1).Select(x => x.Text));
            if (text.Split(' ').Length < 2) continue;
            best = wt;
            quote = Captions.Tidy(text);
        }
        return quote;
    }

    private static double SentenceStart(List<VodWord> words, double t)
    {
        // back to the start of the sentence being said at t (a pause of 0.6 s or more), at most 6 s
        var before = words.Where(w => w.Start <= t + 0.5 && w.End >= t - 6).OrderByDescending(w => w.Start).ToList();
        double start = t;
        for (int i = 0; i < before.Count; i++)
        {
            if (before[i].End < t - 0.2 && (i == 0 || before[i - 1].Start - before[i].End >= 0.6)) break;
            start = Math.Min(start, before[i].Start - 0.25);
            if (i + 1 < before.Count && before[i].Start - before[i + 1].End >= 0.6) break;
        }
        return Math.Max(0, start);
    }

    private static double SentenceEnd(List<VodWord> words, double t, double max)
    {
        // on to the end of the sentence (a pause of 0.6 s), at most 8 s more
        var after = words.Where(w => w.End >= t - 0.3 && w.Start <= t + 8).OrderBy(w => w.Start).ToList();
        double end = t;
        for (int i = 0; i < after.Count; i++)
        {
            if (after[i].Start > end + 0.6) break;
            end = Math.Max(end, after[i].End + 0.35);
        }
        return Math.Min(max, end);
    }

    private static double Median(IList<double> v)
    {
        if (v.Count == 0) return -30;
        var s = v.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    private static double[] MovingAverage(double[] x, int width)
    {
        var o = new double[x.Length];
        double sum = 0;
        int h = width / 2;
        var pre = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++) pre[i + 1] = pre[i] + x[i];
        for (int i = 0; i < x.Length; i++)
        {
            int a = Math.Max(0, i - h), b = Math.Min(x.Length, i + h + 1);
            o[i] = (pre[b] - pre[a]) / (b - a);
        }
        _ = sum;
        return o;
    }
}
