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
    }

    /// <summary>The spotter's report, as it lands (timeline seconds).</summary>
    private static void MuteVia(DesktopSafety ds, double from, double to) => ds.Gains.Mute(from, to);

    /// <summary>
    /// Windows (text-to-speech): a voice says a sentence in desktop audio; the filter (with a test word in "More
    /// words to mute", so no slur has to be spoken) must silence exactly that word after the delay, in real time.
    /// </summary>
    public static async Task SpokenAsync(Action<bool, string> check, Func<string, int, short[]> say)
    {
        if (!VoiceListener.Available(out _)) { Console.WriteLine("    note: no speech model: spoken safety test skipped"); return; }
        var speech = say("the pineapple was sitting on the kitchen table", 0); // 16 kHz
        var pcm16 = new List<short>();
        // a few seconds of quiet first: in the app the filter has been running since the stream started
        pcm16.AddRange(new short[16000 * 4]); pcm16.AddRange(speech); pcm16.AddRange(new short[16000 * 4]);
        // where the words really are (the same recognizer, offline)
        double ps = -1, pe = -1, ts = -1, te = -1;
        using (var rec = new VoskApi.Recognizer(VoiceListener.SharedModel(), alternatives: 0))
        {
            var all = pcm16.ToArray();
            var heard = new List<VoskApi.Word>();
            // fed like the app does (0.1 s at a time); a finished sentence comes back from Accept, the rest from Final
            for (int at = 0; at < all.Length; at += 1600)
            {
                var chunk = all.Skip(at).Take(1600).ToArray();
                if (rec.Accept(chunk, chunk.Length) is { } js) heard.AddRange(VoskApi.Parse(js).FirstOrDefault()?.Words ?? new List<VoskApi.Word>());
            }
            heard.AddRange(VoskApi.Parse(rec.Final()).FirstOrDefault()?.Words ?? new List<VoskApi.Word>());
            Console.WriteLine("    spoken test heard: " + string.Join(" ", heard.Select(w => $"{w.Text}@{w.Start:0.00}")));
            foreach (var w in heard)
            {
                if (w.Text == "pineapple") { ps = w.Start; pe = w.End; }
                if (w.Text == "table") { ts = w.Start; te = w.End; }
            }
        }
        if (ps < 0 || ts < 0) { check(false, $"Slur filter (spoken): the test sentence was recognized (pineapple {ps:0.00}, table {ts:0.00})"); return; }
        // 48 kHz stereo, through the safety stage at real speed
        var ds = new DesktopSafety(new AudioSafetyConfig(3, true, false, "pineapple", false), () => VoiceListener.SharedModel());
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
        ds.Dispose();
        double Level(double a, double b) { int i0 = (int)((a + 3) * 100), i1 = Math.Min(chunks - 1, (int)((b + 3) * 100)); double m = 0; for (int i = i0; i <= i1; i++) m = Math.Max(m, outRms[i]); return m; }
        double inPine = Level(ps + 0.04, pe - 0.04), inTable = Level(ts, te);
        lock (trace) foreach (var t in trace.Take(30)) Console.WriteLine("    spotter " + t);
        check(ds.Muted >= 1 && inPine < 0.002 && inTable > 0.01,
            $"Slur filter (spoken): the filtered word is silent on the delayed stream, the rest of the sentence isn't (word {inPine:0.0000}, “table” {inTable:0.000}, {ds.Muted} mute(s), caught {ds.LastMargin:0.00} s before it would have gone out)");
    }
}
