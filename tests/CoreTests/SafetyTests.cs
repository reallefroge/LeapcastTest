using LeapcastStudioAI;

/// <summary>Stream safety: the slur list, the loud-audio guard, the delay line with its mutes, and the pipeline's shift.</summary>
static class SafetyTests
{
    public static void Run(Action<bool, string> check)
    {
        // ---- the word list (tested through its encoded form, so this file holds no slurs either)
        string R(string enc) => SlurWords.Rot13(enc);
        check(SlurWords.Is(R("avttre"), false, null) && SlurWords.Is(R("snttbgf"), false, null) && SlurWords.Is(R("Puvax!"), false, null),
            "Slur filter: built-in slurs are caught (plurals, capitals, punctuation)");
        check(!SlurWords.Is("bigger", false, null) && !SlurWords.Is("spice", false, null) && !SlurWords.Is("snigger", false, null)
              && !SlurWords.Is("Niger", false, null) && !SlurWords.Is("hello", false, null),
            "Slur filter: ordinary words that look alike are left alone");
        check(!SlurWords.Is(R("ergneq"), false, null) && SlurWords.Is(R("ergneq"), true, null), "Slur filter: the r-word only when that option is on");
        check(SlurWords.Is("noob", false, SlurWords.Custom("noob\nbaddie")) && !SlurWords.Is("noob", false, null), "Slur filter: your own words are muted too");

        // ---- loud-audio guard: Discord at a normal level, then a blown-out blast, then normal again
        var g = new LoudGuard();
        var rng = new Random(1);
        float[] Chunk(double amp) { var c = new float[960]; for (int i = 0; i < 480; i++) { float v = (float)(amp * Math.Sin(i * 0.3) * (0.8 + 0.4 * rng.NextDouble())); c[2 * i] = v; c[2 * i + 1] = v; } return c; }
        float worstNormal = 1;
        for (int i = 0; i < 800; i++) worstNormal = Math.Min(worstNormal, g.Feed(Chunk(0.06)));   // ≈ −27 dBFS for 8 s
        var blastGains = new List<float>();
        for (int i = 0; i < 150; i++) blastGains.Add(g.Feed(Chunk(0.95)));                          // ≈ −3 dBFS for 1.5 s
        float after3s = 1;
        for (int i = 0; i < 300; i++) after3s = g.Feed(Chunk(0.06));
        float firstDb = 20 * MathF.Log10(blastGains[1]), heldDb = 20 * MathF.Log10(blastGains.Skip(5).Max());
        check(worstNormal > 0.89f, $"Loud guard: ordinary audio is left alone ({20 * MathF.Log10(worstNormal):0.0} dB)");
        check(firstDb < -12 && heldDb < -12, $"Loud guard: a sudden blast is turned right down within 20 ms ({firstDb:0} dB) and kept down ({heldDb:0} dB)");
        check(after3s > 0.89f, $"Loud guard: back to normal once it calms down ({20 * MathF.Log10(after3s):0.0} dB after 3 s)");
        var g2 = new LoudGuard();
        float steadyLoud = 1;
        for (int i = 0; i < 600; i++) steadyLoud = g2.Feed(Chunk(0.2)); // a loud game (≈ −17 dBFS) all along
        check(steadyLoud > 0.5f, $"Loud guard: a game that's just loud all along isn't crushed ({20 * MathF.Log10(steadyLoud):0.0} dB)");
        var g3 = new LoudGuard();
        for (int i = 0; i < 800; i++) g3.Feed(Chunk(0.008));          // a quiet menu (≈ −45 dBFS)
        float battleStart = 1, battle5s = 1;
        for (int i = 0; i < 500; i++) { float x = g3.Feed(Chunk(0.12)); if (i == 5) battleStart = x; battle5s = x; } // battle music (≈ −22 dBFS)
        check(battle5s > 0.7f, $"Loud guard: a big but ordinary jump (quiet menu → battle) settles as the new normal ({20 * MathF.Log10(battleStart):0} dB at first, {20 * MathF.Log10(battle5s):0.0} dB after 5 s)");

        // ---- delay line + mutes: 2 s late, a muted moment comes out silent, nothing else changes
        var ds = new DesktopSafety(new AudioSafetyConfig(2, false, false, "", false), null);
        var outLevels = new List<float>();
        for (long n = 0; n < 600; n++) // 6 s of a tone whose loudness encodes the time it was heard
        {
            var c = new float[960];
            float a = 0.1f + 0.001f * (n % 300);
            for (int i = 0; i < 960; i++) c[i] = a;
            if (n == 250) MuteVia(ds, 3.0, 3.5); // decided while the audio from 3.0–3.5 s is still waiting
            ds.Process(c, n * 480);
            outLevels.Add(c[480]);
        }
        bool silentFirst = outLevels.Take(200).All(x => x == 0);
        bool delayed = Math.Abs(outLevels[210] - (0.1f + 0.001f * 10)) < 1e-6 && Math.Abs(outLevels[450] - (0.1f + 0.001f * 250)) < 1e-6;
        bool muted = outLevels.Skip(502).Take(46).All(x => x == 0) && outLevels[580] > 0.05f && outLevels[495] > 0.05f;
        check(silentFirst && delayed, $"Safety delay: desktop audio comes out exactly 2 s later (first {silentFirst}, [199]={outLevels[199]}, [200]={outLevels[200]}, [210]={outLevels[210]})");
        check(muted, "Safety delay: a word muted while it waits never comes out, the rest does");
        // ---- near misses: the n-word and the f-word said differently are caught; ordinary words never are
        var nearYes = new[] { "avttn", "avttnu", "avtthu", "avttnm", "avttnf", "avtre", "avttre", "snttvg", "snttrg" };
        var nearMissed = nearYes.Where(e => !SlurWords.Near(R(e), false, null)).ToList();
        check(nearMissed.Count == 0, $"Slur filter: said differently or cut short, still caught (missed: {string.Join(", ", nearMissed)})");
        var innocent = new[] { "bigger", "trigger", "snicker", "snickers", "nickel", "Niagara", "Nigeria", "neighbor", "nigh", "nag", "nags", "naggy",
            "Nigel", "digger", "nugget", "ninja", "nicker", "speaker", "decay", "cafe", "trainer", "darker", "packers", "checker", "Goku", "kinky",
            "whopper", "dagger", "negative", "figure", "faculty", "fidget", "regard", "retired", "tiger", "vigor", "jigger", "chigger", "Nick", "Nika" };
        var falseHits = innocent.Where(w => SlurWords.Near(w, true, null)).ToList();
        check(falseHits.Count == 0, $"Slur filter: ordinary words that sound a bit alike are never muted (wrongly matched: {string.Join(", ", falseHits)})");

        // ---- the judge: the quick listener holds a stretch back; the full recognizer lets it go or keeps it muted
        {
            var cg = new ContentGains();
            var j = new SafetyJudge(cg, w => SlurWords.Near(w, false, null), codes: false);
            int confirmed = 0; j.Muted += (_, _, _) => confirmed++;
            HeardWord W(string t, double a, double b) => new(t, a, b);
            j.Suspect(10.0, 10.4, R("avttre"), 10.6);
            bool heldAtOnce = cg.Muted(1020);
            j.Heard(new[] { W("that's", 9.6, 9.9), W("bigger", 10.0, 10.4), W("than", 10.5, 10.7) }, 11.2, false);
            check(heldAtOnce && !cg.Muted(1020) && confirmed == 0, $"Slur filter: a maybe from the quick listener is held at once ({heldAtOnce}) and let go when it was an ordinary word ({!cg.Muted(1020)})");
            j.Suspect(20.0, 20.4, R("avttn"), 20.6);
            j.Heard(new[] { W("you", 19.6, 19.9), W(R("avttn"), 20.0, 20.4), W("man", 20.5, 20.8) }, 21.2, false);
            check(cg.Muted(2020) && confirmed == 1, "Slur filter: confirmed by the full recognizer, it stays muted");
            j.Suspect(30.0, 30.4, R("avttre"), 30.5);
            j.Heard(new[] { W("so", 29.0, 29.3) }, 29.6, false);
            check(cg.Muted(3020), "Slur filter: while the full recognizer hasn't got there yet, it stays muted");
            j.Heard(new[] { W("hey", 39.5, 39.8), W(R("avtthu"), 40.0, 40.3) }, 40.6, false);
            check(cg.Muted(4015) && !cg.Muted(3970), "Slur filter: a near miss heard by the full recognizer alone is muted (and only it)");
            j.Suspect(50.0, 50.3, R("avttre"), 51.1);
            j.Heard(new[] { W("and", 49.5, 49.7), W("said", 49.7, 50.02) }, 51.6, false);
            bool stillHeld = cg.Muted(5015);
            j.Heard(new[] { W("and", 49.5, 49.7), W("said", 49.7, 50.02), W("bigger", 50.02, 50.3), W("than", 50.4, 50.6) }, 51.9, false);
            check(stillHeld && !cg.Muted(5015), "Slur filter: a slow running guess that hasn't reached the word yet can't let it go; the ordinary word, once written, does");
        }

        // ---- word times for the recognizer's running guess (from when each word showed up)
        {
            var gt = new GuessTimes();
            gt.Finished(4.0);
            gt.Update("hello", 5.3);
            gt.Update("hello there", 5.8);
            var gg = gt.Update("hello their friend how", 6.6);
            bool ok = Math.Abs(gg[0].End - 5.05) < 0.01 && Math.Abs(gg[1].End - 5.55) < 0.01 && gg[1].Text == "their"
                      && gg[2].End > gg[1].End && Math.Abs(gg[3].End - 6.35) < 0.01 && gg.All(w => w.Start < w.End && w.Start >= 4.0 - 1e-9);
            check(ok, $"Running guess: each word timed from when it showed up, revised words keep their place ({string.Join(" ", gg.Select(w => $"{w.Text}:{w.Start:0.00}-{w.End:0.00}"))})");
        }

        // ---- spoken game codes
        {
            // words at a steady pace (s per word), then fed the way the recognizer reports them: growing guesses, then the sentence
            (ContentGains Gains, List<HeardWord> Words, bool EverMuted, int Codes) Say(string text, double pace, bool codes = true)
            {
                var cg = new ContentGains();
                var j = new SafetyJudge(cg, w => SlurWords.Near(w, false, null), codes);
                int n = 0; j.Muted += (_, _, what) => { if (what == "code") n++; };
                var ws = new List<HeardWord>();
                double t = 5;
                foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries)) { double len = Math.Min(pace * 0.75, 0.15 + 0.05 * w.Length); ws.Add(new HeardWord(w, t, t + len)); t += pace; }
                bool ever = false;
                for (int k = 1; k <= ws.Count; k++)
                {
                    j.Heard(ws.Take(k).ToList(), ws[k - 1].End + 0.35, false);
                    ever |= ws.Take(k).Any(w => cg.Muted((long)((w.Start + w.End) * 50)));
                }
                j.Heard(ws, ws[^1].End + 0.6, true);
                j.Heard(new List<HeardWord>(), ws[^1].End + 2.0, false);
                return (cg, ws, ever, n);
            }
            bool Silent(ContentGains g, HeardWord w) => g.Muted((long)((w.Start + w.End) * 50));
            var c1 = Say("the code is c e u w y b okay", 0.5);
            check(c1.Words.Skip(3).Take(6).All(w => Silent(c1.Gains, w)) && !Silent(c1.Gains, c1.Words[1]) && !Silent(c1.Gains, c1.Words[^1]) && c1.Codes == 1,
                "Codes: \"the code is C E U W Y B\" — every letter hidden, \"code\" and \"okay\" not");
            var c2 = Say("join us see e you double you why bee alright", 0.55);
            check(c2.Words.Skip(2).Take(7).All(w => Silent(c2.Gains, w)) && !Silent(c2.Gains, c2.Words[0]) && !Silent(c2.Gains, c2.Words[^1]),
                "Codes: spelled-out letters without the word \"code\" (see, e, you, double-you, why, bee) are hidden");
            var c3 = Say("we are in k q x z j m now", 0.45);
            check(c3.Words.Skip(3).Take(6).All(w => Silent(c3.Gains, w)) && !Silent(c3.Gains, c3.Words[^1]), "Codes: six bare letters read out are hidden");
            var c4 = Say("why are you a bee", 0.5);
            check(c4.Words.All(w => Silent(c4.Gains, w)), "Codes: the fallback — 4+ letter-words read out slowly one by one count as a code");
            var c5 = Say("oh i see you later", 0.2);
            check(!c5.EverMuted && !c5.Words.Any(w => Silent(c5.Gains, w)), "Codes: \"oh I see you\" said normally is never cut");
            var c6 = Say("are you okay with that", 0.25);
            check(!c6.EverMuted, "Codes: \"are you okay\" is never cut");
            var c7 = Say("plan b is fine", 0.3);
            check(!c7.Words.Any(w => Silent(c7.Gains, w)), "Codes: a lone letter (\"plan B\") is held for a moment at most, then let go");
            var c9 = Say("get in here k que acts the j m hurry up", 0.7);
            check(c9.Words.Skip(3).Take(6).All(w => Silent(c9.Gains, w)) && !Silent(c9.Gains, c9.Words[^2]) && c9.Codes == 1,
                "Codes: letters the recognizer wrote as words (\"que\", \"acts\", \"the\") inside a code are hidden too");
            var c10 = Say("b r b guys", 0.3);
            check(c10.Codes == 0 && !c10.Words.Any(w => Silent(c10.Gains, w)), "Codes: \"B R B\" (three letters) isn't a code");
            var c8 = Say("the code is c e u w y b", 0.5, codes: false);
            check(!c8.EverMuted, "Codes: off unless turned on");
        }

        // ---- replays of what Windows' recognizer really wrote, and when (CI traces): checked as the audio goes out 2 s later
        {
            // (now, running guess) … then the finished sentence with exact times
            (int Silent, int Wrong, string Detail) Replay((double Now, string Text)[] guesses, (string W, double S, double E)[] final, double finalAt,
                Func<int, string, bool?> want, (double Now, string Word)[]? keywords = null, bool codes = true)
            {
                var cg = new ContentGains();
                var j = new SafetyJudge(cg, w => SlurWords.Near(w, true, null), codes);
                var gt = new GuessTimes();
                gt.Finished(3.5);
                var events = guesses.Select(g => (Now: g.Now, Kind: 0, Text: g.Text))
                    .Concat((keywords ?? Array.Empty<(double Now, string Word)>()).Select(k => (Now: k.Now, Kind: 1, Text: k.Word)))
                    .Append((Now: finalAt, Kind: 2, Text: "")).OrderBy(e => e.Now).ThenBy(e => e.Kind).ToList();
                // five points across each word, each judged when it goes out
                var points = new Dictionary<(int, int), bool>();
                double last = 0;
                void Out(double upTo)
                {
                    for (int i = 0; i < final.Length; i++)
                        for (int k = 0; k < 5; k++)
                        {
                            double t = final[i].S + (final[i].E - final[i].S) * (0.15 + 0.175 * k);
                            if (t + 2 > last && t + 2 <= upTo) points[(i, k)] = cg.Muted((long)(t * 100));
                        }
                    last = upTo;
                }
                foreach (var (now, kind, text) in events)
                {
                    Out(now);
                    if (kind == 0) j.Heard(gt.Update(text, now), now, false);
                    else if (kind == 1) j.Suspect(now - 0.95, now - 0.15, text, now);
                    else { j.Heard(final.Select(f => new HeardWord(f.W, f.S, f.E)).ToList(), now, true); gt.Finished(final[^1].E); }
                }
                j.Heard(new List<HeardWord>(), finalAt + 3, false);
                Out(finalAt + 5);
                int silent = 0, wrong = 0; var detail = new List<string>();
                for (int i = 0; i < final.Length; i++)
                {
                    int cut = Enumerable.Range(0, 5).Count(k => points.TryGetValue((i, k), out var v) && v);
                    var w = want(i, final[i].W);
                    bool m = w == true ? cut == 5 : cut >= 3;   // to be hidden: all of it; to be heard: most of it
                    if (w == true && m) silent++;
                    if (w is { } ww && ww != m) { wrong++; detail.Add($"{(SlurWords.Near(final[i].W, true, null) ? "#" + R(final[i].W) : final[i].W)} {(m ? "cut" : "audible")}"); }
                }
                return (silent, wrong, string.Join(", ", detail));
            }
            var r1 = Replay(new[] { (4.71, "get"), (4.91, "get in here"), (5.21, "get in here k"), (5.71, "get in here k p"), (6.01, "get in here k"), (6.21, "get in here k que"),
                    (6.71, "get in here k few"), (6.91, "get in here k que acts"), (7.61, "get in here k que acts the"), (8.41, "get in here k que acts the j"),
                    (8.81, "get in here k que acts the j m"), (9.31, "get in here k que acts the j m hurry up") },
                new[] { ("get", 4.06, 4.3), ("in", 4.33, 4.43), ("here", 4.45, 4.6), ("k", 4.63, 4.9), ("que", 5.35, 5.6), ("acts", 6.1, 6.4), ("the", 6.88, 7.1),
                    ("j", 7.54, 7.8), ("m", 8.26, 8.5), ("hurry", 8.53, 8.8), ("up", 8.86, 9.0) }, 10.01,
                (i, w) => i is >= 3 and <= 8 && w != "the" ? true : w is "hurry" or "up" or "get" ? false : null);
            check(r1.Wrong == 0, $"Codes (replayed from Windows): \"K Q X Z J M\" heard as \"k que acts the j m\" is hidden as it goes out ({r1.Detail})");
            var r2 = Replay(new[] { (4.91, "oh i see"), (5.01, "oh i see you"), (5.21, "oh i see you are"), (5.51, "oh i see you are going to"), (5.71, "oh i see you are going to the"),
                    (6.21, "oh i see you are going to the store") },
                new[] { ("oh", 4.09, 4.28), ("i", 4.3, 4.4), ("see", 4.42, 4.6), ("you", 4.63, 4.78), ("are", 4.81, 4.9), ("going", 4.93, 5.2), ("to", 5.26, 5.35), ("the", 5.38, 5.45), ("store", 5.47, 5.8) },
                6.71, (i, w) => false);
            check(r2.Wrong == 0, $"Codes (replayed from Windows): \"oh I see you are going to the store\" goes out untouched ({r2.Detail})");
            var r3 = Replay(new[] { (4.71, "and"), (4.91, "and then"), (5.01, "and then he"), (5.21, "and then he said"), (5.51, "and then he said " + R("avttre")),
                    (5.81, "and then he said " + R("avttre") + " and"), (6.01, "and then he said " + R("avttre") + " and left the"), (6.31, "and then he said " + R("avttre") + " and left the game") },
                new[] { ("and", 4.09, 4.25), ("then", 4.27, 4.45), ("he", 4.48, 4.58), ("said", 4.6, 4.84), (R("avttre"), 4.87, 5.18), ("and", 5.2, 5.35), ("left", 5.38, 5.6), ("the", 5.62, 5.69), ("game", 5.71, 6.0) },
                6.91, (i, w) => SlurWords.Near(w, true, null) ? true : w is "then" or "left" or "game" ? false : null, new[] { (5.51, R("avttre")) });
            check(r3.Wrong == 0, $"Slur filter (replayed from Windows): the word is cut, the words around it aren't ({r3.Detail})");
            var r4 = Replay(new[] { (4.91, "what is"), (5.11, "what is up my"), (5.21, "what is up my " + R("avttn")), (5.71, "what is up my " + R("avttn") + " how are"),
                    (6.01, "what is up my " + R("avttn") + " how are you doing") },
                new[] { ("what", 4.09, 4.3), ("is", 4.33, 4.45), ("up", 4.48, 4.6), ("my", 4.63, 4.76), (R("avttn"), 4.78, 5.03), ("how", 5.05, 5.25), ("are", 5.29, 5.4), ("you", 5.41, 5.57), ("doing", 5.59, 5.9) },
                6.71, (i, w) => SlurWords.Near(w, true, null) ? true : w is "what" or "how" or "doing" ? false : null, new[] { (5.21, R("avttnm")), (5.51, R("ubaxl")) });
            check(r4.Wrong == 0, $"Slur filter (replayed from Windows): said casually, cut; a false alarm on \"how\" let go ({r4.Detail})");
        }

        // ---- camera auto light
        {
            byte[] Frame(double luma, double windowLuma = -1)
            {
                int w = 160, h = 90; var f = new byte[w * h * 3 / 2];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        double l = windowLuma >= 0 && x > w * 0.8 ? windowLuma : luma;
                        f[y * w + x] = (byte)Math.Round(16 + l * 219);
                    }
                return f;
            }
            LightSettings? First(double luma, double windowLuma = -1)
            {
                var al = new CameraAutoLight();
                var (m, p) = CameraAutoLight.Measure(Frame(luma, windowLuma), 160, 90);
                al.Update(m, p);
                return al.Current;
            }
            var dark = First(0.12); var bright = First(0.86); var fine = First(0.47); var window = First(0.15, 0.98);
            check(dark is { Gamma: > 1.4 } && dark.Value.Saturation > 1, $"Camera auto light: a dark room is lifted (gamma {dark?.Gamma:0.00}, colour {dark?.Saturation:0.00})");
            check(bright is { Gamma: < 0.95 }, $"Camera auto light: a blown-out picture is calmed down (gamma {bright?.Gamma:0.00})");
            check(fine is { Gamma: 1, Contrast: 1 }, $"Camera auto light: a well-lit picture is left exactly as it is (gamma {fine?.Gamma:0.00})");
            check(window is { Gamma: > 1.1 and <= 1.5 }, $"Camera auto light: a dark room with a bright window behind is lifted, gently (gamma {window?.Gamma:0.00})");
            var lamp = First(0.15, 0.9);
            double lampAfter = Math.Pow(0.9, 1 / (lamp?.Gamma ?? 1));
            check(lamp is { Gamma: > 1.2 } && lampAfter <= 0.97, $"Camera auto light: the room is lifted but a lamp in view isn't pushed into white (gamma {lamp?.Gamma:0.00}, lamp {lampAfter:0.00})");
            var al2 = new CameraAutoLight();
            var (bm, bp) = CameraAutoLight.Measure(Frame(0.0), 160, 90);
            check(al2.Update(bm, bp) is null, "Camera auto light: a black frame (lens covered) changes nothing");
            var al3 = new CameraAutoLight();
            var (m1, p1) = CameraAutoLight.Measure(Frame(0.12), 160, 90); al3.Update(m1, p1);
            double g0 = al3.Current.Gamma;
            var (m2, p2) = CameraAutoLight.Measure(Frame(0.47), 160, 90); al3.Update(m2, p2, 1);
            double g1 = al3.Current.Gamma;
            check(g1 < g0 && g1 > 1.05, $"Camera auto light: the lights coming on is eased into over a few seconds (gamma {g0:0.00} → {g1:0.00} after 1 s)");
            check(LightSettings.Neutral.Filter("cl0").StartsWith("eq@cl0=gamma=1:contrast=1"), "Camera auto light: the filter it drives");
            // the stand-in picture while a reader restarts at a new size
            var big = Frames.BlankI420(8, 4);
            for (int y = 0; y < 4; y++) for (int x = 0; x < 8; x++) big[y * 8 + x] = (byte)(x < 4 ? 50 : 200);
            var small = Frames.ResizeI420(big, 8, 4, 4, 2);
            check(small.Length == 12 && small[0] == 50 && small[3] == 200 && small[8] == 128, $"Resize stand-in: a picture resized for a moment keeps its content ({string.Join(",", small.Take(8))})");
        }
    }


    /// <summary>The spotter's report, as it lands (timeline seconds).</summary>
    private static void MuteVia(DesktopSafety ds, double from, double to) => ds.Gains.Mute(from, to);

    /// <summary>
    /// Windows (text-to-speech): a voice says sentences into desktop audio, run through the safety stage in real time
    /// at the 2 s delay. Slurs (ROT13 here) and codes must come out silent; look-alike words and ordinary letters
    /// must come out untouched.
    /// </summary>
    public static async Task SpokenAsync(Action<bool, string> check, Func<string, int, short[]> say)
    {
        if (!VoiceListener.Available(out _)) { Console.WriteLine("    note: no speech model: spoken safety test skipped"); return; }
        string R(string enc) => SlurWords.Rot13(enc);
        bool IsLetter(VoskApi.Word w) => CodeLetters.Of(w.Text) != CodeLetters.Kind.None;
        var cases = new (string Name, string Text, AudioSafetyConfig Cfg, Func<VoskApi.Word, int, List<VoskApi.Word>, bool?> Expect)[]
        {
            // true: must be silent; false: must be heard; null: don't care
            ("your own word", "the pineapple was sitting on the kitchen table", new AudioSafetyConfig(2, true, false, "pineapple", false),
                (w, i, all) => w.Text == "pineapple" ? true : w.Text == "table" ? false : null),
            ("a slur", "and then he said " + R("avttre") + " and left the game", new AudioSafetyConfig(2, true, true, "", false),
                (w, i, all) => SlurWords.Near(w.Text, true, null) ? true : w.Text is "left" or "game" or "then" ? false : null),
            ("said casually", "what is up my " + R("avttn") + " how are you doing", new AudioSafetyConfig(2, true, true, "", false),
                (w, i, all) => SlurWords.Near(w.Text, true, null) ? true : w.Text is "how" or "doing" or "what" ? false : null),
            ("look-alikes", "that tiger is bigger than the trigger near niagara falls", new AudioSafetyConfig(2, true, true, "", false, true),
                (w, i, all) => false),
            ("a code after code", "okay the code is C, E, U, W, Y, B. join now", new AudioSafetyConfig(2, true, true, "", false, true),
                (w, i, all) => i > all.FindIndex(x => x.Text == "code") && i < all.FindIndex(x => x.Text == "join") && IsLetter(w) ? true
                               : w.Text is "code" or "now" or "okay" ? false : null),
            ("a code read out", "get in here K, Q, X, Z, J, M. hurry up", new AudioSafetyConfig(2, true, true, "", false, true),
                (w, i, all) => IsLetter(w) && i > 1 && i < all.FindIndex(x => x.Text == "hurry") ? true : w.Text is "hurry" or "up" or "get" ? false : null),
            ("ordinary letters", "oh i see you are going to the store", new AudioSafetyConfig(2, true, true, "", false, true),
                (w, i, all) => false),
        };
        foreach (var (name, text, cfg, expect) in cases)
        {
            var speech = say(text, 0); // 16 kHz
            var pcm16 = new List<short>();
            // a few seconds of quiet first: in the app the filter has been running since the stream started
            pcm16.AddRange(new short[16000 * 4]); pcm16.AddRange(speech); pcm16.AddRange(new short[16000 * 4]);
            // where the words really are (the same recognizer, offline)
            var heard = new List<VoskApi.Word>();
            var firstSeen = new Dictionary<string, double>(); // word@start → where the running guess first had it
            var cpu = System.Diagnostics.Stopwatch.StartNew();
            using (var rec = new VoskApi.Recognizer(VoiceListener.SharedModel(), alternatives: 0))
            {
                bool pw = rec.TryPartialWords();
                var all = pcm16.ToArray();
                for (int at = 0; at < all.Length; at += 1600)
                {
                    var chunk = all.Skip(at).Take(1600).ToArray();
                    double pos = (at + chunk.Length) / 16000.0;
                    if (rec.Accept(chunk, chunk.Length) is { } js)
                    {
                        var ws = VoskApi.Parse(js).FirstOrDefault()?.Words ?? new List<VoskApi.Word>();
                        heard.AddRange(ws);
                        foreach (var w in ws) firstSeen.TryAdd($"{w.Text}@{w.Start:0.0}", pos);
                    }
                    else if (pw)
                        try
                        {
                            using var pj = System.Text.Json.JsonDocument.Parse(rec.Partial());
                            if (pj.RootElement.TryGetProperty("partial_result", out var arr))
                                foreach (var w in arr.EnumerateArray())
                                    firstSeen.TryAdd($"{w.GetProperty("word").GetString()}@{w.GetProperty("start").GetDouble():0.0}", pos);
                        }
                        catch { }
                }
                heard.AddRange(VoskApi.Parse(rec.Final()).FirstOrDefault()?.Words ?? new List<VoskApi.Word>());
            }
            double rtf = cpu.Elapsed.TotalSeconds / (pcm16.Count / 16000.0);
            // the same without word times in the running guess (does asking for them delay it?)
            var textLags = new List<double>();
            using (var rec2 = new VoskApi.Recognizer(VoiceListener.SharedModel(), alternatives: 0))
            {
                var all = pcm16.ToArray();
                int done = 0;
                for (int at = 0; at < all.Length; at += 1600)
                {
                    var chunk = all.Skip(at).Take(1600).ToArray();
                    double pos = (at + chunk.Length) / 16000.0;
                    if (rec2.Accept(chunk, chunk.Length) is not null) break;
                    int n = 0;
                    try { using var pj2 = System.Text.Json.JsonDocument.Parse(rec2.Partial()); n = (pj2.RootElement.GetProperty("partial").GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length; } catch { }
                    while (done < n && done < heard.Count) { textLags.Add(pos - heard[done].End); done++; }
                }
            }
            Console.WriteLine($"    spoken test ({name}) without word times: {(textLags.Count > 0 ? textLags.Average() : double.NaN):0.00} s after a word ends (worst {(textLags.Count > 0 ? textLags.Max() : double.NaN):0.00} s)");
            var lags = heard.Select(w => firstSeen.TryGetValue($"{w.Text}@{w.Start:0.0}", out var p) ? p - w.End : double.NaN).Where(x => !double.IsNaN(x)).ToList();
            Console.WriteLine($"    spoken test ({name}) recognizer: {rtf:0.000}× real time; a word shows up in the running guess {(lags.Count > 0 ? lags.Average() : double.NaN):0.00} s after it ends (worst {(lags.Count > 0 ? lags.Max() : double.NaN):0.00} s)");
            string Show(string w) => SlurWords.Near(w, true, null) ? "#" + R(w) : w;
            Console.WriteLine($"    spoken test ({name}) heard: " + string.Join(" ", heard.Select(w => $"{Show(w.Text)}@{w.Start:0.00}")));
            var wants = heard.Select((w, i) => (w, want: expect(w, i, heard))).Where(x => x.want is not null).ToList();
            if (!wants.Any(x => x.want == true) && !(name is "look-alikes" or "ordinary letters"))
            {
                check(false, $"Safety (spoken, {name}): the recognizer heard the test sentence the way it's needed (heard: {string.Join(" ", heard.Select(w => Show(w.Text)))})");
                continue;
            }
            // 48 kHz stereo, through the safety stage at real speed
            var ds = new DesktopSafety(cfg, () => VoiceListener.SharedModel());
            var trace = new List<string>();
            ds.Trace = t => { lock (trace) trace.Add(t); };
            int chunks = pcm16.Count / 160;
            var outRms = new double[chunks];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int c = 0; c < chunks; c++)
            {
                var st = new float[960];
                for (int i = 0; i < 480; i++) { float v = pcm16[c * 160 + i / 3] / 32768f; st[2 * i] = v; st[2 * i + 1] = v; }
                ds.Process(st, (long)c * 480);
                double sum = 0; foreach (var v in st) sum += v * v;
                outRms[c] = Math.Sqrt(sum / st.Length);
                while (clock.Elapsed.TotalMilliseconds < (c + 1) * 10.0) await Task.Delay(1);
            }
            bool keywords = ds.KeywordListener;
            ds.Dispose();
            double d = cfg.DelaySeconds;
            double Level(double a, double b) { int i0 = (int)((a + d) * 100), i1 = Math.Min(chunks - 1, (int)((b + d) * 100)); double m = 0; for (int i = i0; i <= Math.Max(i0, i1); i++) m = Math.Max(m, outRms[i]); return m; }
            var wrong = new List<string>();
            foreach (var (w, want) in wants)
            {
                double lvl = Level(w.Start + 0.04, w.End - 0.04);
                if (want == true && lvl >= 0.002) wrong.Add($"{Show(w.Text)} audible ({lvl:0.000})");
                if (want == false && lvl < 0.01) wrong.Add($"{Show(w.Text)} cut ({lvl:0.000})");
            }
            if (wrong.Count > 0) lock (trace) foreach (var t in trace.Take(40)) Console.WriteLine($"    listener ({name}) {t}");
            check(wrong.Count == 0,
                $"Safety (spoken, {name}, 2 s delay): {(wrong.Count == 0 ? "right" : string.Join("; ", wrong))} — {ds.Muted} word(s), {ds.CodesMuted} code(s) muted, "
                + $"{ds.Judge.Held} held / {ds.Judge.Released} let go, decided {ds.LastMargin:0.00} s before it would have gone out{(keywords ? "" : ", no keyword listener")}");
        }
    }
}
