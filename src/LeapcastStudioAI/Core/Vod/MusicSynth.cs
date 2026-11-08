namespace LeapcastStudioAI.Vod;

/// <summary>The feel a chapter needs.</summary>
public enum Mood { Chill, Hype, Tense, Epic, Fun }

/// <summary>A style of track: its own tempo, drums, bass, chords, lead and space.</summary>
public enum MusicStyle
{
    Edm, Lofi, Tense, Epic,
    Synthwave, Trap, DrumAndBass, Funk, Orchestral, Chiptune, House, PianoAmbient, JazzHop, Quirky,
}

/// <summary>
/// Original background music, composed and synthesized on the PC for each video (nothing anyone can claim).
/// Fourteen styles, each with its own instruments, drum patterns and tempo; chords from a progression, a bass line,
/// and a melody written for the chords (a two-bar idea, repeated and answered). Arranged in sections — intro,
/// groove, full, breakdown — to the requested length. Same style + seed = same track.
/// </summary>
public static class MusicSynth
{
    // ------------------------------------------------------------------ styles

    private static readonly int[] Minor = { 0, 2, 3, 5, 7, 8, 10 }, Major = { 0, 2, 4, 5, 7, 9, 11 }, Dorian = { 0, 2, 3, 5, 7, 9, 10 };

    private sealed record Def(
        int BpmMin, int BpmMax, double Swing, int[] Scale, int[][] Progressions, int ChordNotes,
        Dictionary<string, string> Drums, string Bass, string BassPattern, string Chords, string ChordPattern,
        string Lead, string LeadRhythm, bool LeadArp, float Reverb, float Delay, bool Vinyl, double Lowpass, int LeadOctave = 24);

    private static Dictionary<string, string> D(params string[] kv)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
        return d;
    }

    // drum steps: 'X' accent, 'x' hit, 'g' ghost, 'r' roll (three quick hits), '.' rest — 16 per bar
    // bass steps: 'r' root, 'o' octave up, 'f' fifth, 'w' walking step, '-' hold, '.' rest
    // chord steps: 'x' hit, '-' hold, '.' rest
    private static readonly Dictionary<MusicStyle, Def> Defs = new()
    {
        [MusicStyle.Edm] = new(122, 128, 0.5, Minor, new[] { new[] { 0, 5, 2, 6 }, new[] { 0, 6, 5, 6 }, new[] { 5, 2, 6, 0 } }, 3,
            D("kick", "x...x...x...x...", "clap", "....x.......x...", "hat", "..x...x...x...x.", "shaker", "x.x.x.x.x.x.x.x."),
            "saw", ".r.r.r.r.r.r.r.r", "stab", "x..x..x...x..x..", "pluck", "xxxxxxxxxxxxxxxx", true, 0.28f, 0.12f, false, 0),
        [MusicStyle.Lofi] = new(78, 88, 0.6, Dorian, new[] { new[] { 3, 6, 2, 5 }, new[] { 0, 3, 6, 2 }, new[] { 5, 4, 2, 0 } }, 4,
            D("kick", "x......x..x.....", "snare", "....x.......x...", "hat", "x.x.x.x.x.x.x.xg"),
            "sub", "r------.r.--r---", "keys", "x---------x-----", "vibes", "..x...x.x.....x.", false, 0.35f, 0.18f, true, 6500, 12),
        [MusicStyle.Tense] = new(94, 104, 0.5, Minor, new[] { new[] { 0, 0, 5, 4 }, new[] { 0, 5, 0, 1 }, new[] { 0, 3, 0, 4 } }, 3,
            D("tom", "x.g.x.g.x.g.x.gx", "hat", "xgxgxgxgxgxgxgxg", "kick", "x.......x.......", "snare", "............x..."),
            "saw", "rrrrrrrrrrrrrrrr", "strings", "x---------------", "bell", "x.......x...x...", false, 0.3f, 0.15f, false, 0),
        [MusicStyle.Epic] = new(98, 110, 0.5, Minor, new[] { new[] { 5, 6, 0, 0 }, new[] { 0, 5, 6, 4 }, new[] { 0, 6, 5, 6 } }, 3,
            D("kick", "x.....x.........", "snare", "........X.......", "tom", "............xxxx", "hat", "x...x...x...x..."),
            "saw", "r-------r-------", "pad", "x---------------", "pluck", "x.x.x.x.x.x.x.x.", true, 0.38f, 0.1f, false, 0),
        [MusicStyle.Synthwave] = new(100, 112, 0.5, Minor, new[] { new[] { 0, 5, 2, 6 }, new[] { 0, 3, 5, 4 }, new[] { 5, 3, 0, 6 } }, 3,
            D("kick", "x...x...x...x...", "gsnare", "....X.......X...", "hat", "xxxxxxxxxxxxxxxx"),
            "saw", "roroororroroorro", "pad", "x---------------", "sawlead", "x..x..x.x...x.x.", false, 0.32f, 0.3f, false, 0),
        [MusicStyle.Trap] = new(136, 144, 0.5, Minor, new[] { new[] { 0, 0, 5, 6 }, new[] { 0, 5, 3, 4 }, new[] { 0, 6, 5, 4 } }, 3,
            D("k808", "x.....x...x.....", "clap", "........X.......", "hat", "x.x.x.xrx.x.x.rr", "ohat", "......x........."),
            "808", "r.....r...r-....", "pad", "x---------------", "bell", "x..x..x...x..x..", false, 0.3f, 0.2f, false, 0),
        [MusicStyle.DrumAndBass] = new(170, 176, 0.5, Minor, new[] { new[] { 0, 5, 3, 4 }, new[] { 0, 6, 5, 3 } }, 3,
            D("kick", "x.........x.....", "snare", "....X..g.g..X...", "hat", "x.x.x.x.x.x.x.x.", "shaker", "xxxxxxxxxxxxxxxx"),
            "reese", "r---------------", "pad", "x---------------", "pluck", "x...x..x....x...", false, 0.3f, 0.15f, false, 0),
        [MusicStyle.Funk] = new(100, 112, 0.54, Dorian, new[] { new[] { 0, 3, 0, 3 }, new[] { 0, 0, 3, 4 }, new[] { 0, 6, 3, 4 } }, 4,
            D("kick", "x..x..x...x..x..", "snare", "....x..g....x.g.", "hat", "xxxxxxxxxxxxxxxx", "ohat", "..............x."),
            "slap", "r..o.r.wr..o.rf.", "organ", "..x...x..x..x...", "brass", "x...........x.x.", false, 0.22f, 0.08f, false, 0, 12),
        [MusicStyle.Orchestral] = new(92, 104, 0.5, Minor, new[] { new[] { 0, 5, 6, 4 }, new[] { 0, 3, 4, 0 }, new[] { 5, 6, 0, 4 } }, 3,
            D("timp", "x.......x...x.x.", "cymbal", "x..............."),
            "strings", "rrrrrrrrrrrrrrrr", "choir", "x---------------", "harp", "xxxxxxxxxxxxxxxx", true, 0.42f, 0.05f, false, 0, 12),
        [MusicStyle.Chiptune] = new(140, 152, 0.5, Major, new[] { new[] { 0, 5, 3, 4 }, new[] { 0, 3, 4, 4 }, new[] { 5, 3, 0, 4 } }, 3,
            D("chipk", "x...x...x...x...", "chips", "....x.......x...", "chiph", "..x...x...x...x."),
            "tri", "rororororororoor", "pulse", "xxxxxxxxxxxxxxxx", "square", "x.x.xx.xx.x.x.xx", false, 0.08f, 0.12f, false, 0),
        [MusicStyle.House] = new(122, 126, 0.5, Minor, new[] { new[] { 0, 5, 3, 4 }, new[] { 0, 3, 5, 3 } }, 4,
            D("kick", "x...x...x...x...", "clap", "....x.......x...", "ohat", "..x...x...x...x.", "shaker", "xxxxxxxxxxxxxxxx"),
            "saw", "..r...r...r...r.", "piano", "..x..x....x..x..", "pluck", "x.....x...x.....", false, 0.26f, 0.15f, false, 0),
        [MusicStyle.PianoAmbient] = new(68, 78, 0.5, Major, new[] { new[] { 0, 4, 5, 3 }, new[] { 5, 3, 0, 4 }, new[] { 0, 5, 3, 4 } }, 4,
            D("rim", "....x.......x..."),
            "sub", "r---------------", "piano", "x-------x-------", "piano", "x..x..x...x.....", false, 0.5f, 0.12f, false, 0, 12),
        [MusicStyle.JazzHop] = new(84, 92, 0.62, Dorian, new[] { new[] { 1, 4, 0, 0 }, new[] { 3, 6, 2, 5 }, new[] { 0, 3, 1, 4 } }, 5,
            D("kick", "x......x.x......", "rim", "....x.......x...", "brush", "x.xxx.xxx.xxx.xx"),
            "upright", "w...w...w...w...", "keys", "x-----x---------", "vibes", ".x...x.x...x..x.", false, 0.33f, 0.12f, true, 7000, 12),
        [MusicStyle.Quirky] = new(110, 120, 0.5, Major, new[] { new[] { 0, 3, 4, 0 }, new[] { 0, 5, 3, 4 } }, 3,
            D("kick", "x.......x.......", "clap", "....x.......x...", "wood", "..x...x...x.x.x."),
            "tuba", "r...f...r...f...", "pizz", "x.x.x.x.x.x.x.x.", "marimba", "x.xx..x.x.x.xx..", false, 0.2f, 0.06f, false, 0),
    };

    /// <summary>The styles that fit a mood (the editor picks one a chapter, never the same twice in a video).</summary>
    public static MusicStyle[] StylesFor(Mood mood) => mood switch
    {
        Mood.Hype => new[] { MusicStyle.Edm, MusicStyle.House, MusicStyle.Trap, MusicStyle.Synthwave, MusicStyle.DrumAndBass },
        Mood.Chill => new[] { MusicStyle.Lofi, MusicStyle.JazzHop, MusicStyle.PianoAmbient },
        Mood.Tense => new[] { MusicStyle.Tense, MusicStyle.DrumAndBass, MusicStyle.Trap },
        Mood.Epic => new[] { MusicStyle.Epic, MusicStyle.Orchestral, MusicStyle.Synthwave },
        _ => new[] { MusicStyle.Funk, MusicStyle.Chiptune, MusicStyle.Quirky, MusicStyle.JazzHop },
    };

    /// <summary>Backwards-compatible: a mood's first style.</summary>
    public static Dsp.Stereo Track(Mood mood, double seconds, int seed) => Track(StylesFor(mood)[0], seconds, seed);

    // ------------------------------------------------------------------ composing

    private sealed class Song
    {
        public required Def Def;
        public required Random Rng;
        public required double Beat, Bar;
        public required int Key;
        public required int[] Scale;
        public required int[][] Chords;     // midi notes per bar of the progression
        public required int[] Roots;
        public Dsp.Stereo Dry = null!, Rev = null!, Dly = null!;
        public float[] Duck = null!;
        public double Step(int i) => Beat * i / 4 + (i % 2 == 1 ? (Def.Swing - 0.5) * Beat / 2 : 0);
    }

    public static Dsp.Stereo Track(MusicStyle style, double seconds, int seed)
    {
        var def = Defs[style];
        var rng = new Random(seed * 31 + (int)style);
        double bpm = rng.Next(def.BpmMin, def.BpmMax + 1);
        int key = 45 + rng.Next(0, 10);
        var prog = def.Progressions[rng.Next(def.Progressions.Length)];
        var scale = def.Scale;
        int[] ChordOn(int degree)
        {
            var notes = new List<int>();
            for (int k = 0; k < def.ChordNotes; k++)
            {
                int d = degree + k * 2;
                notes.Add(key + scale[d % 7] + 12 * (d / 7));
            }
            return notes.ToArray();
        }
        var song = new Song
        {
            Def = def, Rng = rng, Beat = 60 / bpm, Bar = 240 / bpm, Key = key, Scale = scale,
            Chords = prog.Select(ChordOn).ToArray(), Roots = prog.Select(d => key + scale[d % 7]).ToArray(),
        };
        int bars = Math.Max(4, (int)Math.Ceiling(seconds / song.Bar) + 1);
        int frames = Dsp.Frames(bars * song.Bar + 3);
        song.Dry = new Dsp.Stereo(frames); song.Rev = new Dsp.Stereo(frames); song.Dly = new Dsp.Stereo(frames);
        song.Duck = new float[frames];
        Array.Fill(song.Duck, 1f);
        var music = new Dsp.Stereo(frames); // chords + bass: they breathe with the kick

        // the melody: a two-bar idea, played again, then answered (ending home)
        var motif = Motif(song);

        int Section(int bar)
        {
            if (bar < 4) return 0;
            if (bar >= bars - 1) return 4;
            int b = (bar - 4) % 28;
            return b < 8 ? 1 : b < 20 ? 2 : b < 24 ? 3 : 2;
        }

        for (int bar = 0; bar < bars; bar++)
        {
            int sec = Section(bar);
            double t0 = bar * song.Bar;
            int ci = bar % song.Chords.Length;
            bool fill = bar % 4 == 3 && sec is 1 or 2;
            bool beforeLift = Section(bar + 1) is 2 && sec is 1 or 3;
            // drums: none in the intro (only light percussion), lighter in the breakdown
            foreach (var (part, pattern) in def.Drums)
            {
                bool light = part is "hat" or "shaker" or "brush" or "rim" or "chiph" or "wood";
                if (sec == 0 && !light) continue;
                if (sec == 3 && !light && part != "timp") continue;
                if (sec == 4) continue;
                for (int i = 0; i < 16; i++)
                {
                    char c = pattern[i];
                    if (c == '.') continue;
                    float vel = c == 'X' ? 1f : c == 'g' ? 0.35f : 0.8f;
                    if (sec == 0) vel *= 0.6f;
                    if (fill && i >= 12 && part is "snare" or "clap" or "gsnare" or "chips") continue;
                    if (c == 'r') { for (int k = 0; k < 3; k++) Drum(song, part, t0 + song.Step(i) + k * song.Beat / 12, 0.5f); continue; }
                    Drum(song, part, t0 + song.Step(i), vel);
                }
                if (fill && part is "snare" or "clap" or "gsnare" or "chips")
                    for (int i = 12; i < 16; i++) Drum(song, part, t0 + song.Step(i), 0.45f + 0.12f * (i - 12));
            }
            if (sec == 4) { Drum(song, "cymbal", t0, 0.6f); Chord(song, def.Chords is "pulse" ? "pad" : def.Chords, song.Chords[0], t0, song.Bar * 1.6, 0.7f, music); continue; }
            if (beforeLift) Riser(song, t0 + song.Bar * 0.5, song.Bar * 0.5);

            // chords
            for (int i = 0; i < 16; i++)
            {
                if (def.ChordPattern[i] != 'x') continue;
                int len = 1;
                while (i + len < 16 && def.ChordPattern[i + len] == '-') len++;
                double dur = len * song.Beat / 4;
                if (def.Chords == "pulse") { Note(song, "pulse", song.Chords[ci][i % song.Chords[ci].Length] + 12, t0 + song.Step(i), song.Beat / 4, 0.5f, music); continue; }
                Chord(song, def.Chords, song.Chords[ci], t0 + song.Step(i), dur, sec == 3 ? 1.1f : 0.85f, music);
            }
            // bass (not in the intro or the breakdown)
            if (sec is 1 or 2)
            {
                int root = song.Roots[ci] - 12;
                int walk = 0;
                for (int i = 0; i < 16; i++)
                {
                    char c = def.BassPattern[i];
                    if (c is '.' or '-') continue;
                    int len = 1;
                    while (i + len < 16 && def.BassPattern[i + len] == '-') len++;
                    int note = c switch
                    {
                        'o' => root + 12,
                        'f' => root + 7,
                        'w' => key - 12 + scale[((Array.IndexOf(scale, (root - key + 120) % 12) < 0 ? 0 : Array.IndexOf(scale, (root - key + 120) % 12)) + walk++) % 7],
                        _ => root,
                    };
                    double dur = def.Bass is "808" or "reese" or "sub" ? len * song.Beat / 4 : Math.Min(len * song.Beat / 4, song.Beat / 4 * 0.9 * len);
                    Note(song, def.Bass, note, t0 + song.Step(i), dur, c == 'r' && i % 4 == 0 ? 1f : 0.85f, music);
                }
            }
            // the melody (in the full sections and the breakdown)
            if (sec is 2 or 3 || sec == 1 && def.LeadArp)
            {
                var notes = motif[bar % 4];
                for (int i = 0; i < 16; i++)
                {
                    if (def.LeadRhythm[i] != 'x') continue;
                    int note = def.LeadArp ? Arp(song.Chords[ci], i) + def.LeadOctave : notes[i] + def.LeadOctave;
                    float vel = (i % 4 == 0 ? 1f : 0.78f) * (sec == 1 ? 0.7f : 1f);
                    Note(song, def.Lead, note, t0 + song.Step(i), song.Beat / 4 * (def.Lead is "sawlead" or "brass" or "square" ? 1.8 : 1), vel, null);
                }
            }
        }

        // mix: chords and bass dip under the kick, everything gets its room and echoes
        for (int i = 0; i < frames; i++) { music.L[i] *= song.Duck[i]; music.R[i] *= song.Duck[i]; }
        var mix = new Dsp.Stereo(frames);
        mix.Mix(song.Dry, 0);
        mix.Mix(music, 0);
        mix.Mix(Echo(song.Dly, song.Beat * 0.75), 0, def.Delay * 2.2f);
        mix.Mix(Dsp.Reverb(song.Rev, 0.84f, 0.35f, def.Reverb), 0);
        if (def.Lowpass > 0 || def.Vinyl)
        {
            var lpL = Dsp.Biquad.LowPass(def.Lowpass > 0 ? def.Lowpass : 16000); var lpR = Dsp.Biquad.LowPass(def.Lowpass > 0 ? def.Lowpass : 16000);
            for (int i = 0; i < frames; i++)
            {
                float crackle = def.Vinyl ? (rng.NextDouble() < 0.0006 ? (float)(rng.NextDouble() - 0.5) * 0.22f : (float)(rng.NextDouble() - 0.5) * 0.004f) : 0;
                mix.L[i] = lpL.Process(mix.L[i]) + crackle;
                mix.R[i] = lpR.Process(mix.R[i]) + crackle;
            }
        }
        // a bed under a voice: lighter lows (about −5 dB under 120 Hz, nothing under 30 Hz), so the tune carries, not the boom
        {
            var sL = Dsp.Biquad.LowPass(120); var sR = Dsp.Biquad.LowPass(120);
            var hL = Dsp.Biquad.HighPass(30); var hR = Dsp.Biquad.HighPass(30);
            for (int i = 0; i < frames; i++)
            {
                float l = hL.Process(mix.L[i]), r = hR.Process(mix.R[i]);
                mix.L[i] = l - 0.45f * sL.Process(l);
                mix.R[i] = r - 0.45f * sR.Process(r);
            }
        }
        int fadeFrom = Dsp.Frames((bars - 1) * song.Bar);
        for (int i = fadeFrom; i < frames; i++)
        {
            float g = 1f - (float)(i - fadeFrom) / (frames - fadeFrom);
            mix.L[i] *= g * g; mix.R[i] *= g * g;
        }
        Dsp.Master(mix);
        return mix;
    }

    /// <summary>
    /// Four bars of melody (one note per step) over the progression: strong beats land on chord tones, the rest step
    /// through the scale; bars 1–2 state the idea, bar 3 repeats it, bar 4 answers and comes home to the root.
    /// </summary>
    private static int[][] Motif(Song s)
    {
        var bars = new int[4][];
        int Near(int from, int[] chord)
        {
            // the chord tone (any octave) closest to where the melody is
            int best = chord[0], dist = 99;
            foreach (var n in chord) for (int o = -12; o <= 12; o += 12) { int c = n + o; if (Math.Abs(c - from) < dist) { dist = Math.Abs(c - from); best = c; } }
            return best;
        }
        int ScaleStep(int from, int steps)
        {
            int rel = ((from - s.Key) % 12 + 12) % 12, oct = (from - s.Key - rel) / 12;
            int idx = Array.IndexOf(s.Scale, rel);
            if (idx < 0) { idx = 0; while (idx < 6 && s.Scale[idx + 1] <= rel) idx++; }
            int ni = idx + steps, octave = oct + (int)Math.Floor(ni / 7.0);
            return s.Key + s.Scale[((ni % 7) + 7) % 7] + 12 * octave;
        }
        int cur = s.Chords[0][s.Rng.Next(s.Chords[0].Length)];
        for (int b = 0; b < 2; b++)
        {
            var chord = s.Chords[b % s.Chords.Length];
            bars[b] = new int[16];
            for (int i = 0; i < 16; i++)
            {
                cur = i % 4 == 0 ? Near(cur + (s.Rng.NextDouble() < 0.5 ? 2 : -2), chord) : ScaleStep(cur, s.Rng.Next(-2, 3));
                cur = Math.Clamp(cur, s.Key - 5, s.Key + 14);
                bars[b][i] = cur;
            }
        }
        bars[2] = bars[0].ToArray();
        bars[3] = bars[1].ToArray();
        // the answer: the last beat walks down to the root
        int home = s.Key + 12 * ((bars[3][12] - s.Key) / 12);
        for (int i = 12; i < 16; i++) bars[3][i] = i == 15 ? home : ScaleStep(bars[3][i - 1], -1);
        // keep each bar's strong beats on its own chord
        for (int b = 0; b < 4; b++)
        {
            var chord = s.Chords[b % s.Chords.Length];
            for (int i = 0; i < 16; i += 4) if (!(b == 3 && i == 12)) bars[b][i] = Near(bars[b][i], chord);
        }
        return bars;
    }

    private static int Arp(int[] chord, int step)
    {
        var up = chord.Concat(new[] { chord[0] + 12 }).ToArray();
        int cycle = up.Length * 2 - 2, k = step % cycle;
        return up[k < up.Length ? k : cycle - k];
    }

    private static Dsp.Stereo Echo(Dsp.Stereo src, double delay)
    {
        // ping-pong echo: left, then right, fading
        int d = Dsp.Frames(delay);
        var o = new Dsp.Stereo(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            float l = i >= d ? o.R[i - d] * 0.45f : 0, r = i >= d ? o.L[i - d] * 0.45f : 0;
            o.L[i] = l + (i >= d ? src.L[i - d] * 0.6f : 0);
            o.R[i] = r;
        }
        return o;
    }

    // ------------------------------------------------------------------ drums

    private static float N(Random r) => (float)(r.NextDouble() * 2 - 1);

    private static void Drum(Song s, string part, double t, float vel)
    {
        var r = s.Rng;
        int at = Dsp.Frames(t);
        switch (part)
        {
            case "kick":
            case "chipk":
            {
                bool chip = part == "chipk";
                int n = Dsp.Frames(0.42);
                var b = new float[n];
                double ph = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate, f = 48 + 120 * Math.Exp(-x * 32);
                    ph += f / Dsp.Rate;
                    double wave = chip ? 2 * Math.Abs(2 * (ph - Math.Floor(ph + 0.5))) - 1 : Math.Sin(2 * Math.PI * ph);
                    float click = !chip && x < 0.003 ? N(r) * (float)(1 - x / 0.003) * 0.6f : 0;
                    b[i] = MathF.Tanh(((float)(wave * Math.Exp(-x * 7.5)) + click) * 1.6f) * vel;
                }
                s.Dry.Mix(b, at, 0.78f);
                Pump(s, at);
                break;
            }
            case "k808":
            {
                int n = Dsp.Frames(0.25);
                var b = new float[n];
                double ph = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    ph += (55 + 90 * Math.Exp(-x * 45)) / Dsp.Rate;
                    b[i] = (float)(Math.Sin(2 * Math.PI * ph) * Math.Exp(-x * 14)) * vel;
                }
                s.Dry.Mix(b, at, 0.7f);
                Pump(s, at);
                break;
            }
            case "snare":
            case "gsnare":
            case "chips":
            {
                int n = Dsp.Frames(part == "gsnare" ? 0.5 : 0.28);
                var b = new float[n];
                var bp = Dsp.Biquad.BandPass(part == "chips" ? 3000 : 1900, 0.8);
                var hp = Dsp.Biquad.HighPass(400);
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    float noise = part == "chips" ? (MathF.Round(N(r) * 3) / 3) * (float)Math.Exp(-x * 20) * 0.8f
                                                  : hp.Process(bp.Process(N(r))) * (float)Math.Exp(-x * (part == "gsnare" ? 9 : 16)) * 2.2f;
                    float tone = (float)(Math.Sin(2 * Math.PI * 185 * x) * Math.Exp(-x * 28)) * 0.5f;
                    b[i] = (noise + tone) * vel * 0.5f;
                }
                s.Dry.Mix(b, at, 1f, 0.05f);
                s.Rev.Mix(b, at, part == "gsnare" ? 1.6f : 0.6f);
                break;
            }
            case "clap":
            {
                int n = Dsp.Frames(0.35);
                var b = new float[n];
                var bp = Dsp.Biquad.BandPass(1300, 1.1);
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    double env = x < 0.03 ? Math.Exp(-((x % 0.01) * 380)) : Math.Exp(-(x - 0.03) * 14);
                    b[i] = bp.Process(N(r)) * (float)env * 1.3f * vel;
                }
                s.Dry.Mix(b, at); s.Rev.Mix(b, at, 0.8f);
                break;
            }
            case "hat":
            case "ohat":
            case "shaker":
            case "brush":
            case "chiph":
            {
                bool open = part == "ohat";
                double len = open ? 0.3 : part == "brush" ? 0.12 : 0.05;
                int n = Dsp.Frames(len);
                var b = new float[n];
                var hp = Dsp.Biquad.HighPass(part == "shaker" ? 5500 : part == "brush" ? 3000 : 7200, 0.9);
                double decay = open ? 11 : part == "brush" ? 30 : 70;
                float g = part switch { "shaker" => 0.05f, "brush" => 0.06f, "chiph" => 0.08f, "ohat" => 0.11f, _ => 0.12f } * vel;
                for (int i = 0; i < n; i++)
                {
                    float v = part == "chiph" ? MathF.Round(N(r)) : hp.Process(N(r));
                    double x = i / (double)Dsp.Rate;
                    double env = part == "shaker" ? Math.Sin(Math.PI * Math.Min(1, x / len)) : Math.Exp(-x * decay);
                    b[i] = v * (float)env * g;
                }
                s.Dry.Mix(b, at, 1f, part == "shaker" ? -0.3f : 0.25f);
                break;
            }
            case "rim":
            case "wood":
            {
                int n = Dsp.Frames(0.08);
                var b = new float[n];
                double hz = part == "wood" ? 880 + r.Next(0, 2) * 260 : 1700;
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    b[i] = (float)(Math.Sin(2 * Math.PI * hz * x) * Math.Exp(-x * 60)) * 0.35f * vel;
                }
                s.Dry.Mix(b, at, 1f, part == "wood" ? 0.3f : -0.15f); s.Rev.Mix(b, at, 0.4f);
                break;
            }
            case "tom":
            case "timp":
            {
                bool timp = part == "timp";
                int n = Dsp.Frames(timp ? 1.2 : 0.5);
                var b = new float[n];
                double hz = timp ? Dsp.NoteHz(s.Key - 12) : 70 + r.Next(0, 3) * 18, ph = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    ph += hz * (1 + (timp ? 0.15 : 0.6) * Math.Exp(-x * 20)) / Dsp.Rate;
                    float boom = (float)(Math.Sin(2 * Math.PI * ph) * Math.Exp(-x * (timp ? 3.2 : 6)));
                    float skin = timp && x < 0.02 ? N(r) * 0.2f * (float)(1 - x / 0.02) : 0;
                    b[i] = (boom + skin) * vel * (timp ? 0.75f : 0.5f);
                }
                s.Dry.Mix(b, at, 1f, (float)(r.NextDouble() - 0.5) * 0.4f);
                if (timp) s.Rev.Mix(b, at, 0.8f);
                break;
            }
            case "cymbal":
            {
                int n = Dsp.Frames(2.4);
                var b = new float[n];
                var hp = Dsp.Biquad.HighPass(5200, 0.7);
                for (int i = 0; i < n; i++) b[i] = hp.Process(N(r)) * (float)Math.Exp(-i / (double)Dsp.Rate * 2) * 0.3f * vel;
                s.Dry.Mix(b, at, 1f, -0.2f); s.Rev.Mix(b, at, 0.6f);
                break;
            }
        }
    }

    /// <summary>The music dips under each kick and comes back over ~180 ms (sidechain).</summary>
    private static void Pump(Song s, int at)
    {
        int rel = Dsp.Frames(0.18);
        for (int i = 0; i < rel && at + i < s.Duck.Length; i++)
            if (at + i >= 0) s.Duck[at + i] = MathF.Min(s.Duck[at + i], 0.5f + 0.5f * (float)Math.Pow(i / (double)rel, 1.6));
    }

    private static void Riser(Song s, double t, double len)
    {
        int n = Dsp.Frames(len);
        var b = new float[n];
        var bp = Dsp.Biquad.BandPass(400, 1.4);
        for (int i = 0; i < n; i++)
        {
            double p = i / (double)n;
            if (i % 64 == 0) bp.Tune(400 + 6000 * p * p);
            b[i] = bp.Process(N(s.Rng)) * (float)(p * p) * 0.25f;
        }
        s.Dry.Mix(b, Dsp.Frames(t));
    }

    // ------------------------------------------------------------------ pitched instruments

    private static void Chord(Song s, string kind, int[] chord, double t, double len, float vel, Dsp.Stereo music)
    {
        if (kind == "none") return;
        int k = 0;
        foreach (var n in chord)
        {
            // a hint of strum on plucked and struck instruments
            double strum = kind is "piano" or "keys" or "harp" or "pizz" or "marimba" ? k * 0.014 : 0;
            Note(s, kind, n + 12, t + strum, len, vel / MathF.Sqrt(chord.Length) * 0.9f, music, k++ % 2 == 0 ? -0.35f : 0.35f);
        }
    }

    /// <summary>One note of an instrument into the mix (and its reverb / echo sends).</summary>
    private static void Note(Song s, string kind, int midi, double t, double len, float vel, Dsp.Stereo? music, float pan = 0)
    {
        double hz = Dsp.NoteHz(midi), inc = hz / Dsp.Rate;
        var r = s.Rng;
        double tail = kind switch
        {
            "pad" or "strings" or "choir" => 0.6, "piano" or "keys" or "harp" or "vibes" or "bell" or "marimba" => 1.2,
            "pluck" or "pizz" => 0.6, "808" => 0.3, _ => 0.06,
        };
        int n = Dsp.Frames(len + tail);
        var b = new float[n];
        float gain = 0.2f, send = 0.3f, dly = 0;
        switch (kind)
        {
            case "saw":
            case "reese":
            case "tuba":
            case "slap":
            case "tri":
            case "upright":
            case "sub":
            {
                var lp = Dsp.Biquad.LowPass(kind == "reese" ? 700 : kind == "slap" ? 2600 : 900, 0.9);
                double ph = 0, ph2 = r.NextDouble();
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    double env = Math.Min(1, x / 0.005) * (x > len ? Math.Max(0, 1 - (x - len) / 0.06) : 1);
                    float v;
                    switch (kind)
                    {
                        case "reese": v = (Dsp.Saw(ph, inc) + Dsp.Saw(ph2, inc * 1.012)) * 0.5f; ph2 += inc * 1.012; break;
                        case "tri": v = (float)(2 * Math.Abs(2 * (ph - Math.Floor(ph + 0.5))) - 1); break;
                        case "sub": v = (float)Math.Sin(2 * Math.PI * ph); break;
                        case "upright": v = (float)(Math.Sin(2 * Math.PI * ph) + 0.3 * Math.Sin(4 * Math.PI * ph)) * (float)Math.Exp(-x * 3.5); break;
                        case "tuba": v = MathF.Tanh(MathF.Sign((float)Math.Sin(2 * Math.PI * ph)) * 0.8f + (float)Math.Sin(2 * Math.PI * ph)); break;
                        default: v = Dsp.Saw(ph, inc); break;
                    }
                    if (kind is "saw" or "slap" or "reese" or "tuba")
                    {
                        if (i % 32 == 0) lp.Tune((kind == "slap" ? 2600 : 900) * (0.6 + (kind == "slap" ? 2.6 : 1.6) * Math.Exp(-x * (kind == "slap" ? 18 : 12))));
                        v = lp.Process(v) * 0.8f + (float)Math.Sin(2 * Math.PI * ph) * (kind == "reese" ? 0.6f : 0.5f);
                    }
                    b[i] = v * (float)env;
                    ph += inc;
                }
                gain = kind switch { "sub" => 0.34f, "upright" => 0.36f, "tri" => 0.24f, "tuba" => 0.18f, _ => 0.27f };
                send = kind is "upright" ? 0.1f : 0;
                break;
            }
            case "808":
            {
                double ph = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    double f = hz * (1 + 0.8 * Math.Exp(-x * 30));
                    ph += f / Dsp.Rate;
                    double env = Math.Min(1, x / 0.003) * Math.Exp(-x * 1.4) * (x > len ? Math.Max(0, 1 - (x - len) / 0.3) : 1);
                    b[i] = MathF.Tanh((float)(Math.Sin(2 * Math.PI * ph) * env) * 2.2f);
                }
                gain = 0.32f; send = 0;
                Pump(s, Dsp.Frames(t));
                break;
            }
            case "pad":
            case "strings":
            case "choir":
            case "stab":
            case "brass":
            case "sawlead":
            {
                double[] det = kind is "sawlead" or "brass" ? new[] { -0.06, 0.06 } : new[] { -0.11, -0.04, 0.04, 0.11 };
                var phs = det.Select(_ => r.NextDouble()).ToArray();
                var f1 = Dsp.Biquad.BandPass(800, 2.5); var f2 = Dsp.Biquad.BandPass(1150, 3);
                var lp = Dsp.Biquad.LowPass(kind switch { "pad" => 2600, "strings" => 3200, "stab" => 5200, "brass" => 1400, "sawlead" => 3400, _ => 3000 }, 0.8);
                double attack = kind switch { "pad" => 0.25, "strings" => 0.12, "choir" => 0.3, "brass" => 0.04, _ => 0.006 };
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    double vib = kind is "strings" or "sawlead" or "choir" ? 1 + 0.004 * Math.Sin(2 * Math.PI * 5.2 * x) * Math.Min(1, x / 0.4) : 1;
                    float v = 0;
                    for (int k = 0; k < det.Length; k++) { double di = inc * Math.Pow(2, det[k] / 12) * vib; v += Dsp.Saw(phs[k], di); phs[k] += di; }
                    v /= det.Length;
                    if (kind == "choir") v = f1.Process(v) * 1.6f + f2.Process(v) * 1.1f;
                    if (kind == "brass" && i % 32 == 0) lp.Tune(1400 * (0.7 + 1.8 * Math.Min(1, x / 0.06) * Math.Exp(-x * 4)));
                    v = lp.Process(v);
                    double env = Math.Min(1, x / attack) * (kind == "stab" ? Math.Exp(-x * 5) : 1) * (x > len ? Math.Max(0, 1 - (x - len) / tail) : 1);
                    b[i] = v * (float)env;
                }
                gain = kind switch { "pad" => 0.3f, "strings" => 0.3f, "choir" => 0.32f, "stab" => 0.24f, "brass" => 0.22f, _ => 0.17f };
                send = kind is "sawlead" ? 0.4f : 0.6f;
                dly = kind is "sawlead" or "brass" ? 0.5f : 0;
                break;
            }
            case "organ":
            {
                double[] parts = { 1, 2, 3, 4, 6 }; double[] amp = { 1, 0.7, 0.45, 0.3, 0.15 };
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate, v = 0;
                    for (int k = 0; k < parts.Length; k++) v += Math.Sin(2 * Math.PI * hz * parts[k] * x) * amp[k];
                    double env = Math.Min(1, x / 0.004) * (x > len ? Math.Max(0, 1 - (x - len) / 0.05) : 1) * (1 + 0.1 * Math.Sin(2 * Math.PI * 6.5 * x));
                    b[i] = (float)(v / 2.6 * env);
                }
                gain = 0.16f; send = 0.3f;
                break;
            }
            case "keys":
            case "piano":
            case "vibes":
            case "bell":
            case "marimba":
            {
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate, v;
                    switch (kind)
                    {
                        case "piano":
                            v = (Math.Sin(2 * Math.PI * hz * x) * Math.Exp(-x * 1.1) + 0.5 * Math.Sin(4 * Math.PI * hz * x * 1.001) * Math.Exp(-x * 2.2)
                                 + 0.25 * Math.Sin(6 * Math.PI * hz * x * 1.002) * Math.Exp(-x * 3.5) + 0.1 * Math.Sin(8 * Math.PI * hz * x) * Math.Exp(-x * 5));
                            if (x < 0.004) v += N(r) * 0.15 * (1 - x / 0.004);
                            break;
                        case "vibes":
                            v = (Math.Sin(2 * Math.PI * hz * x) + 0.25 * Math.Sin(2 * Math.PI * hz * 4 * x) * Math.Exp(-x * 8)) * Math.Exp(-x * 1.6) * (1 + 0.25 * Math.Sin(2 * Math.PI * 5 * x));
                            break;
                        case "bell":
                            v = Math.Sin(2 * Math.PI * hz * x + 2.2 * Math.Exp(-x * 4) * Math.Sin(2 * Math.PI * hz * 3.5 * x)) * Math.Exp(-x * 2.4);
                            break;
                        case "marimba":
                            v = (Math.Sin(2 * Math.PI * hz * x) + 0.35 * Math.Sin(2 * Math.PI * hz * 4 * x) * Math.Exp(-x * 25)) * Math.Exp(-x * 7);
                            break;
                        default: // e-piano
                            double idx = 1.4 * Math.Exp(-x * 3);
                            v = Math.Sin(2 * Math.PI * hz * x + idx * Math.Sin(2 * Math.PI * hz * x)) * Math.Exp(-x * 0.9) * (1 + 0.12 * Math.Sin(2 * Math.PI * 4.5 * x));
                            break;
                    }
                    double env = Math.Min(1, x / 0.003) * (x > len ? Math.Max(0, 1 - (x - len) / tail) : 1);
                    b[i] = (float)(v * env);
                }
                gain = kind switch { "piano" => 0.28f, "keys" => 0.3f, "vibes" => 0.22f, "bell" => 0.16f, _ => 0.26f };
                send = kind == "piano" ? 0.55f : 0.45f;
                dly = kind is "vibes" or "bell" ? 0.4f : 0;
                break;
            }
            case "pluck":
            case "harp":
            case "pizz":
            {
                int period = Math.Max(2, (int)(Dsp.Rate / hz));
                var line = new float[period];
                for (int i = 0; i < period; i++) line[i] = N(r);
                float damp = kind == "pizz" ? 0.985f : kind == "harp" ? 0.998f : 0.996f;
                int p = 0; float last = 0;
                for (int i = 0; i < n; i++)
                {
                    float cur = line[p];
                    line[p] = 0.5f * (cur + last) * damp;
                    last = cur;
                    b[i] = cur;
                    if (++p >= period) p = 0;
                }
                gain = kind == "harp" ? 0.18f : 0.16f;
                send = 0.6f; dly = kind == "pluck" ? 0.3f : 0;
                break;
            }
            case "square":
            case "pulse":
            {
                double ph = 0, duty = kind == "pulse" ? 0.25 : 0.5;
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    double vib = kind == "square" ? 1 + 0.006 * Math.Sin(2 * Math.PI * 6 * x) * Math.Min(1, x / 0.2) : 1;
                    ph += inc * vib;
                    double env = Math.Min(1, x / 0.002) * (x > len ? Math.Max(0, 1 - (x - len) / 0.03) : 1);
                    b[i] = (float)(((ph - Math.Floor(ph)) < duty ? 1 : -1) * env);
                }
                gain = kind == "pulse" ? 0.06f : 0.08f; send = 0.15f; dly = 0.3f;
                break;
            }
        }
        int at = Dsp.Frames(t);
        float g = gain * vel;
        (music ?? s.Dry).Mix(b, at, g, pan);
        if (send > 0) s.Rev.Mix(b, at, g * send, -pan);
        if (dly > 0) s.Dly.Mix(b, at, g * dly, pan);
    }
}
