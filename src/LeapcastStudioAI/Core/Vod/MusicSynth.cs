namespace LeapcastStudioAI.Vod;

/// <summary>The feel of a music bed: picked per chapter from how intense it is.</summary>
public enum Mood { Chill, Hype, Tense, Epic }

/// <summary>
/// Original background music, composed and synthesized on the PC for each video (so there is nothing anyone can
/// claim): drums, bass, chords, a lead and space, arranged in 4-bar sections (intro, groove, full, breakdown) that
/// follow the requested length. Same seed = same track.
/// </summary>
public static class MusicSynth
{
    private static readonly int[] Minor = { 0, 2, 3, 5, 7, 8, 10 };

    private sealed class Ctx
    {
        public required Random Rng;
        public required double Bpm, Beat, Bar, Swing;
        public required int Key; // midi of the tonic (around C3)
        public required int[][] Prog; // chords: midi notes
    }

    /// <summary>A chord built on a scale degree: stacked thirds of the minor scale (3 notes, or 4 with sevenths).</summary>
    private static int[] Chord(int key, int degree, bool seventh)
    {
        var notes = new List<int>();
        for (int k = 0; k < (seventh ? 4 : 3); k++)
        {
            int d = degree + k * 2;
            notes.Add(key + Minor[d % 7] + 12 * (d / 7));
        }
        return notes.ToArray();
    }

    public static Dsp.Stereo Track(Mood mood, double seconds, int seed)
    {
        var rng = new Random(seed);
        double bpm = mood switch
        {
            Mood.Hype => 120 + rng.Next(0, 9),
            Mood.Chill => 80 + rng.Next(0, 11),
            Mood.Tense => 96 + rng.Next(0, 9),
            _ => 100 + rng.Next(0, 11),
        };
        int key = 45 + rng.Next(0, 10); // A2…F#3
        int[][] degrees = mood switch
        {
            Mood.Hype => new[] { new[] { 0, 5, 2, 6 }, new[] { 0, 6, 5, 6 }, new[] { 5, 2, 6, 0 }, new[] { 0, 3, 5, 4 } },
            Mood.Chill => new[] { new[] { 3, 6, 2, 5 }, new[] { 0, 3, 6, 2 }, new[] { 5, 4, 2, 0 } },
            Mood.Tense => new[] { new[] { 0, 0, 5, 4 }, new[] { 0, 5, 0, 1 }, new[] { 0, 3, 0, 4 } },
            _ => new[] { new[] { 5, 6, 0, 0 }, new[] { 0, 5, 6, 4 }, new[] { 0, 6, 5, 6 } },
        };
        var pick = degrees[rng.Next(degrees.Length)];
        var ctx = new Ctx
        {
            Rng = rng, Bpm = bpm, Beat = 60.0 / bpm, Bar = 240.0 / bpm, Swing = mood == Mood.Chill ? 0.58 : 0.5, Key = key,
            Prog = pick.Select(d => Chord(key, d, mood == Mood.Chill)).ToArray(),
        };

        int bars = Math.Max(4, (int)Math.Ceiling(seconds / ctx.Bar) + 1);
        var mix = new Dsp.Stereo(Dsp.Frames(bars * ctx.Bar + 3));
        var send = new Dsp.Stereo(mix.Length); // to the reverb
        var duck = new float[mix.Length];      // sidechain: 1 = no ducking
        Array.Fill(duck, 1f);

        // arrangement: 0 intro, 1 groove, 2 full, 3 breakdown; the last bar rings out
        int Section(int bar)
        {
            if (bar < 4) return 0;
            if (bar >= bars - 1) return 4;
            int b = (bar - 4) % 28;
            return b < 8 ? 1 : b < 20 ? 2 : b < 24 ? 3 : 2;
        }

        var drums = new Dsp.Stereo(mix.Length);
        var music = new Dsp.Stereo(mix.Length);
        for (int bar = 0; bar < bars; bar++)
        {
            int sec = Section(bar);
            double t0 = bar * ctx.Bar;
            var chord = ctx.Prog[bar % ctx.Prog.Length];
            bool lastOfPhrase = bar % 4 == 3;
            bool beforeChange = Section(bar + 1) != sec && Section(bar + 1) is 2 or 1;
            Drums(ctx, mood, sec, t0, drums, send, duck, lastOfPhrase, beforeChange);
            Harmony(ctx, mood, sec, t0, chord, music, send);
            if (beforeChange && sec != 4) Riser(t0 + ctx.Bar * 0.5, ctx.Bar * 0.5, music, mood == Mood.Chill ? 0.05f : 0.12f);
        }

        // the bass and chords breathe with the kick (sidechain), then everything gets a room
        for (int i = 0; i < mix.Length; i++) { music.L[i] *= duck[i]; music.R[i] *= duck[i]; }
        mix.Mix(drums, 0);
        mix.Mix(music, 0);
        var verb = Dsp.Reverb(send, mood == Mood.Epic ? 0.88f : 0.8f, 0.35f, mood == Mood.Chill ? 0.35f : 0.28f);
        mix.Mix(verb, 0);
        if (mood == Mood.Chill)
        {
            // warm, slightly dusty: low-pass and vinyl crackle
            var lpL = Dsp.Biquad.LowPass(6500); var lpR = Dsp.Biquad.LowPass(6500);
            for (int i = 0; i < mix.Length; i++)
            {
                float crackle = rng.NextDouble() < 0.0006 ? (float)(rng.NextDouble() - 0.5) * 0.25f : (float)(rng.NextDouble() - 0.5) * 0.004f;
                mix.L[i] = lpL.Process(mix.L[i]) + crackle;
                mix.R[i] = lpR.Process(mix.R[i]) + crackle;
            }
        }
        // a soft fade out over the last bar
        int fadeFrom = Dsp.Frames((bars - 1) * ctx.Bar);
        for (int i = fadeFrom; i < mix.Length; i++)
        {
            float g = 1f - (float)(i - fadeFrom) / (mix.Length - fadeFrom);
            mix.L[i] *= g * g; mix.R[i] *= g * g;
        }
        Dsp.Master(mix);
        return mix;
    }

    // ------------------------------------------------------------------ drums

    private static void Drums(Ctx c, Mood mood, int sec, double t0, Dsp.Stereo d, Dsp.Stereo send, float[] duck, bool fill, bool beforeChange)
    {
        if (sec == 0 && mood != Mood.Tense) return;      // intros are drumless (tense keeps a pulse)
        if (sec == 4) { Cymbal(c, t0, d, send, 0.35f); return; }
        bool full = sec == 2;
        double B = c.Beat;
        double Sw(int eighth) => eighth % 2 == 1 ? (eighth - 1) * B / 2 + B * c.Swing : eighth * B / 2;
        switch (mood)
        {
            case Mood.Hype:
                if (sec == 3) { for (int i = 0; i < 4; i++) Hat(c, t0 + i * B + B / 2, d, 0.10f, false); break; }
                for (int i = 0; i < 4; i++) { Kick(c, t0 + i * B, d, duck, 0.95f); }
                Clap(c, t0 + B, d, send, 0.5f); Clap(c, t0 + 3 * B, d, send, 0.5f);
                for (int i = 0; i < 8; i++) Hat(c, t0 + i * B / 2, d, i % 2 == 1 ? 0.16f : 0.07f, i % 2 == 1 && full);
                if (full) for (int i = 0; i < 16; i += 2) Hat(c, t0 + i * B / 4 + B / 4, d, 0.04f, false);
                if (fill || beforeChange) for (int i = 12; i < 16; i++) Snare(c, t0 + i * B / 4, d, send, 0.18f + 0.06f * (i - 12));
                break;
            case Mood.Chill:
                if (sec == 3) { for (int i = 0; i < 8; i++) Hat(c, t0 + Sw(i), d, 0.05f, false); break; }
                Kick(c, t0, d, duck, 0.7f); Kick(c, t0 + Sw(5), d, duck, 0.5f);
                if (c.Rng.NextDouble() < 0.4) Kick(c, t0 + Sw(3), d, duck, 0.35f);
                Snare(c, t0 + B, d, send, 0.32f); Snare(c, t0 + 3 * B, d, send, 0.32f);
                for (int i = 0; i < 8; i++) Hat(c, t0 + Sw(i), d, i % 2 == 0 ? 0.08f : 0.05f, false);
                break;
            case Mood.Tense:
                for (int i = 0; i < 8; i++) Tom(c, t0 + i * B / 2, d, i % 4 == 0 ? 0.55f : 0.22f, i % 4 == 0 ? 70 : 95);
                if (sec >= 1) for (int i = 0; i < 16; i++) Hat(c, t0 + i * B / 4, d, i % 4 == 2 ? 0.07f : 0.035f, false);
                if (full) { Kick(c, t0, d, duck, 0.9f); Kick(c, t0 + 2 * B, d, duck, 0.8f); Snare(c, t0 + 3 * B, d, send, 0.3f); }
                if (fill) for (int i = 0; i < 4; i++) Tom(c, t0 + 3 * B + i * B / 4, d, 0.45f, 120 - i * 12);
                break;
            default: // epic: half time, big snare
                if (sec == 3) { Tom(c, t0, d, 0.5f, 60); break; }
                Kick(c, t0, d, duck, 1f); Kick(c, t0 + 1.5 * B, d, duck, 0.6f);
                Snare(c, t0 + 2 * B, d, send, 0.6f);
                for (int i = 0; i < 4; i++) Hat(c, t0 + i * B, d, 0.06f, false);
                if (fill) for (int i = 0; i < 6; i++) Tom(c, t0 + 2.5 * B + i * B / 4, d, 0.4f, 130 - i * 14);
                if (full && c.Rng.NextDouble() < 0.25) Cymbal(c, t0, d, send, 0.22f);
                break;
        }
    }

    private static void Kick(Ctx c, double t, Dsp.Stereo d, float[] duck, float g)
    {
        int n = Dsp.Frames(0.45);
        var s = new float[n];
        double ph = 0;
        for (int i = 0; i < n; i++)
        {
            double x = i / (double)Dsp.Rate;
            double f = 48 + 120 * Math.Exp(-x * 32);
            ph += f / Dsp.Rate;
            float body = (float)(Math.Sin(2 * Math.PI * ph) * Math.Exp(-x * 7.5));
            float click = x < 0.003 ? (float)(c.Rng.NextDouble() - 0.5) * (float)(1 - x / 0.003) * 0.6f : 0;
            s[i] = MathF.Tanh((body + click) * 1.6f) * g;
        }
        int at = Dsp.Frames(t);
        d.Mix(s, at, 0.78f);
        // sidechain: the music dips under each kick and recovers over ~180 ms
        int rel = Dsp.Frames(0.18);
        for (int i = 0; i < rel && at + i < duck.Length; i++)
        {
            float k = 0.45f + 0.55f * (float)Math.Pow(i / (double)rel, 1.6);
            duck[at + i] = MathF.Min(duck[at + i], k);
        }
    }

    private static void Snare(Ctx c, double t, Dsp.Stereo d, Dsp.Stereo send, float g)
    {
        int n = Dsp.Frames(0.3);
        var s = new float[n];
        var bp = Dsp.Biquad.BandPass(1900, 0.8);
        var hp = Dsp.Biquad.HighPass(400);
        for (int i = 0; i < n; i++)
        {
            double x = i / (double)Dsp.Rate;
            float noise = hp.Process(bp.Process((float)(c.Rng.NextDouble() * 2 - 1))) * (float)Math.Exp(-x * 16) * 2.2f;
            float tone = (float)(Math.Sin(2 * Math.PI * 185 * x) * Math.Exp(-x * 28)) * 0.5f;
            s[i] = (noise + tone) * g;
        }
        int at = Dsp.Frames(t);
        float pan = (float)(c.Rng.NextDouble() - 0.5) * 0.1f;
        d.Mix(s, at, 1f, pan);
        send.Mix(s, at, 0.6f, pan);
    }

    private static void Clap(Ctx c, double t, Dsp.Stereo d, Dsp.Stereo send, float g)
    {
        int n = Dsp.Frames(0.35);
        var s = new float[n];
        var bp = Dsp.Biquad.BandPass(1300, 1.1);
        for (int i = 0; i < n; i++)
        {
            double x = i / (double)Dsp.Rate;
            // three quick hands, then the tail
            double env = x < 0.03 ? Math.Exp(-((x % 0.01) * 380)) : Math.Exp(-(x - 0.03) * 14);
            s[i] = bp.Process((float)(c.Rng.NextDouble() * 2 - 1)) * (float)env * 2.6f * g;
        }
        int at = Dsp.Frames(t);
        d.Mix(s, at);
        send.Mix(s, at, 0.8f);
    }

    private static void Hat(Ctx c, double t, Dsp.Stereo d, float g, bool open)
    {
        int n = Dsp.Frames(open ? 0.32 : 0.06);
        var s = new float[n];
        var hp = Dsp.Biquad.HighPass(7200, 0.9);
        double decay = open ? 11 : 70;
        for (int i = 0; i < n; i++)
            s[i] = hp.Process((float)(c.Rng.NextDouble() * 2 - 1)) * (float)Math.Exp(-i / (double)Dsp.Rate * decay) * g;
        d.Mix(s, Dsp.Frames(t), 1f, 0.25f);
    }

    private static void Tom(Ctx c, double t, Dsp.Stereo d, float g, double hz)
    {
        int n = Dsp.Frames(0.5);
        var s = new float[n];
        double ph = 0;
        for (int i = 0; i < n; i++)
        {
            double x = i / (double)Dsp.Rate;
            ph += hz * (1 + 0.6 * Math.Exp(-x * 20)) / Dsp.Rate;
            s[i] = (float)(Math.Sin(2 * Math.PI * ph) * Math.Exp(-x * 6)) * g;
        }
        d.Mix(s, Dsp.Frames(t), 1f, (float)(c.Rng.NextDouble() - 0.5) * 0.4f);
    }

    private static void Cymbal(Ctx c, double t, Dsp.Stereo d, Dsp.Stereo send, float g)
    {
        int n = Dsp.Frames(2.2);
        var s = new float[n];
        var hp = Dsp.Biquad.HighPass(5200, 0.7);
        for (int i = 0; i < n; i++)
            s[i] = hp.Process((float)(c.Rng.NextDouble() * 2 - 1)) * (float)Math.Exp(-i / (double)Dsp.Rate * 2.2) * g;
        int at = Dsp.Frames(t);
        d.Mix(s, at, 1f, -0.2f);
        send.Mix(s, at, 0.5f);
    }

    private static void Riser(double t, double len, Dsp.Stereo m, float g)
    {
        int n = Dsp.Frames(len);
        var s = new float[n];
        var bp = Dsp.Biquad.BandPass(400, 1.4);
        var rng = new Random(n);
        for (int i = 0; i < n; i++)
        {
            double p = i / (double)n;
            if (i % 64 == 0) bp.Tune(400 + 6000 * p * p);
            s[i] = bp.Process((float)(rng.NextDouble() * 2 - 1)) * (float)(p * p) * g * 2.5f;
        }
        m.Mix(s, Dsp.Frames(t), 1f, 0);
    }

    // ------------------------------------------------------------------ bass, chords, lead

    private static void Harmony(Ctx c, Mood mood, int sec, double t0, int[] chord, Dsp.Stereo m, Dsp.Stereo send)
    {
        double B = c.Beat, bar = c.Bar;
        int root = chord[0];
        if (sec == 4) { Pad(c, chord, t0, bar * 1.5, m, send, 0.16f, 900); return; }
        switch (mood)
        {
            case Mood.Hype:
                Pad(c, chord, t0, bar, m, send, sec == 3 ? 0.2f : 0.11f, sec == 3 ? 1200 : 2800);
                if (sec >= 1 && sec != 3)
                    for (int i = 0; i < 8; i++) Bass(root - 12, t0 + i * B / 2 + (i % 2 == 0 ? B / 4 : 0), B / 3, m, 0.34f, 900);
                if (sec == 2)
                {
                    // supersaw stabs: 3-3-2
                    foreach (var at in new[] { 0.0, 0.75, 1.5, 2.25, 3.0 }) Stab(c, chord, t0 + at * B, B * 0.45, m, send, 0.085f);
                    Arp(c, chord, t0, bar, B / 4, m, send, 0.055f, true);
                }
                else if (sec == 1) Arp(c, chord, t0, bar, B / 4, m, send, 0.06f, false);
                break;
            case Mood.Chill:
                Keys(c, chord, t0, bar, m, send, 0.2f);
                if (sec >= 1 && sec != 3) { Bass(root - 12, t0, B * 1.6, m, 0.36f, 300); Bass(root - 12, t0 + 2.5 * B, B * 1.2, m, 0.3f, 300); }
                if (sec == 2) for (int i = 0; i < 4; i++) if (c.Rng.NextDouble() < 0.55) Pluck(c, chord[c.Rng.Next(chord.Length)] + 12, t0 + i * B + (c.Rng.NextDouble() < 0.5 ? B / 2 : 0), m, send, 0.11f);
                break;
            case Mood.Tense:
                Pad(c, new[] { chord[0], chord[0] + 7, chord[1] + 12 }, t0, bar, m, send, 0.12f, 1100);
                for (int i = 0; i < 8; i++) Bass(root - 12, t0 + i * B / 2, B / 2.4, m, sec == 0 ? 0.2f : 0.32f, 500 + 900 * (i % 2));
                if (sec == 2) Arp(c, new[] { chord[0] + 12, chord[1] + 12, chord[0] + 24 }, t0, bar, B / 4, m, send, 0.04f, false);
                break;
            default: // epic
                Pad(c, chord, t0, bar, m, send, sec == 3 ? 0.22f : 0.15f, sec == 2 ? 3600 : 1800);
                Pad(c, chord.Select(n => n + 12).ToArray(), t0, bar, m, send, 0.06f, 4200);
                if (sec >= 1 && sec != 3) { Bass(root - 12, t0, B * 1.8, m, 0.36f, 600); Bass(root - 12, t0 + 2 * B, B * 1.8, m, 0.32f, 600); }
                if (sec == 2) Arp(c, chord, t0, bar, B / 2, m, send, 0.06f, true);
                break;
        }
    }

    private static void Bass(int midi, double t, double len, Dsp.Stereo m, float g, double cutoff)
    {
        g *= 0.8f;
        int n = Dsp.Frames(len + 0.05);
        var s = new float[n];
        double hz = Dsp.NoteHz(midi), inc = hz / Dsp.Rate, ph = 0;
        var lp = Dsp.Biquad.LowPass(cutoff, 0.9);
        for (int i = 0; i < n; i++)
        {
            double x = i / (double)Dsp.Rate;
            double env = Math.Min(1, x / 0.006) * (x > len ? Math.Max(0, 1 - (x - len) / 0.05) : 1);
            if (i % 32 == 0) lp.Tune(cutoff * (0.6 + 1.6 * Math.Exp(-x * 12)));
            float saw = Dsp.Saw(ph, inc);
            float sub = (float)Math.Sin(2 * Math.PI * ph);
            s[i] = (lp.Process(saw) * 0.6f + sub * 0.7f) * (float)env * g;
            ph += inc;
        }
        m.Mix(s, Dsp.Frames(t), 1f, 0);
    }

    /// <summary>Wide, soft chords: several detuned saws per note, filtered, spread left and right.</summary>
    /// <summary>Melodic parts sit ~4.5 dB up against the drums and bass (they carry the feel under a voice).</summary>
    private const float Melody = 1.7f;

    private static void Pad(Ctx c, int[] chord, double t, double len, Dsp.Stereo m, Dsp.Stereo send, float g, double cutoff)
    {
        g *= Melody;
        int n = Dsp.Frames(len + 0.6);
        var l = new float[n]; var r = new float[n];
        double[] det = { -0.11, -0.05, 0, 0.05, 0.11 };
        foreach (var note in chord)
            for (int v = 0; v < det.Length; v++)
            {
                double hz = Dsp.NoteHz(note + 12) * Math.Pow(2, det[v] / 12), inc = hz / Dsp.Rate, ph = c.Rng.NextDouble();
                var buf = v % 2 == 0 ? l : r;
                for (int i = 0; i < n; i++) { buf[i] += Dsp.Saw(ph, inc); ph += inc; }
            }
        var lpl = Dsp.Biquad.LowPass(cutoff); var lpr = Dsp.Biquad.LowPass(cutoff);
        float norm = g / (chord.Length * 2.2f);
        for (int i = 0; i < n; i++)
        {
            double x = i / (double)Dsp.Rate;
            double env = Math.Min(1, x / 0.25) * (x > len ? Math.Max(0, 1 - (x - len) / 0.6) : 1);
            l[i] = lpl.Process(l[i]) * (float)env * norm;
            r[i] = lpr.Process(r[i]) * (float)env * norm;
        }
        int at = Dsp.Frames(t);
        m.Mix(l, at, 1f, -0.7f); m.Mix(r, at, 1f, 0.7f);
        send.Mix(l, at, 0.5f, -0.7f); send.Mix(r, at, 0.5f, 0.7f);
    }

    private static void Stab(Ctx c, int[] chord, double t, double len, Dsp.Stereo m, Dsp.Stereo send, float g)
    {
        g *= Melody;
        int n = Dsp.Frames(len + 0.08);
        var s = new float[n];
        foreach (var note in chord)
            for (int v = -1; v <= 1; v++)
            {
                double hz = Dsp.NoteHz(note + 24) * Math.Pow(2, v * 0.08 / 12), inc = hz / Dsp.Rate, ph = c.Rng.NextDouble();
                for (int i = 0; i < n; i++) { s[i] += Dsp.Saw(ph, inc); ph += inc; }
            }
        var lp = Dsp.Biquad.LowPass(5200);
        for (int i = 0; i < n; i++)
        {
            double x = i / (double)Dsp.Rate;
            double env = Math.Min(1, x / 0.004) * Math.Exp(-x * 5) * (x > len ? Math.Max(0, 1 - (x - len) / 0.08) : 1);
            s[i] = lp.Process(s[i]) * (float)env * g / chord.Length;
        }
        int at = Dsp.Frames(t);
        m.Mix(s, at, 1f, 0.15f);
        send.Mix(s, at, 0.6f, -0.15f);
    }

    /// <summary>Plucked notes (Karplus-Strong): a little lead line that walks the chord.</summary>
    private static void Arp(Ctx c, int[] chord, double t, double len, double step, Dsp.Stereo m, Dsp.Stereo send, float g, bool upDown)
    {
        int steps = (int)Math.Round(len / step);
        var order = chord.Concat(new[] { chord[0] + 12 }).ToArray();
        for (int i = 0; i < steps; i++)
        {
            int k = upDown ? (i / order.Length % 2 == 0 ? i % order.Length : order.Length - 1 - i % order.Length) : i % order.Length;
            Pluck(c, order[k] + 12, t + i * step, m, send, g * (i % 4 == 0 ? 1.15f : 0.85f));
        }
    }

    private static void Pluck(Ctx c, int midi, double t, Dsp.Stereo m, Dsp.Stereo send, float g)
    {
        g *= Melody;
        double hz = Dsp.NoteHz(midi);
        int period = Math.Max(2, (int)(Dsp.Rate / hz));
        var line = new float[period];
        for (int i = 0; i < period; i++) line[i] = (float)(c.Rng.NextDouble() * 2 - 1);
        int n = Dsp.Frames(0.9);
        var s = new float[n];
        int p = 0;
        float last = 0;
        for (int i = 0; i < n; i++)
        {
            float cur = line[p];
            float next = 0.5f * (cur + last) * 0.996f;
            last = cur;
            line[p] = next;
            s[i] = cur * g;
            if (++p >= period) p = 0;
        }
        int at = Dsp.Frames(t);
        float pan = (float)(c.Rng.NextDouble() - 0.5) * 0.6f;
        m.Mix(s, at, 1f, pan);
        send.Mix(s, at, 0.7f, -pan);
    }

    /// <summary>Electric-piano chords (two-operator FM with a slow tremolo), slightly strummed.</summary>
    private static void Keys(Ctx c, int[] chord, double t, double len, Dsp.Stereo m, Dsp.Stereo send, float g)
    {
        g *= Melody;
        int n = Dsp.Frames(len + 0.8);
        var s = new float[n];
        for (int k = 0; k < chord.Length; k++)
        {
            double hz = Dsp.NoteHz(chord[k] + 12), off = k * 0.012;
            for (int i = 0; i < n; i++)
            {
                double x = i / (double)Dsp.Rate - off;
                if (x < 0) continue;
                double idx = 1.4 * Math.Exp(-x * 3);
                double env = Math.Min(1, x / 0.004) * Math.Exp(-x * 0.9) * (x > len ? Math.Max(0, 1 - (x - len) / 0.8) : 1);
                double v = Math.Sin(2 * Math.PI * hz * x + idx * Math.Sin(2 * Math.PI * hz * x));
                s[i] += (float)(v * env * (1 + 0.12 * Math.Sin(2 * Math.PI * 4.5 * x)));
            }
        }
        for (int i = 0; i < n; i++) s[i] *= g / chord.Length;
        int at = Dsp.Frames(t);
        m.Mix(s, at, 1f, -0.1f);
        send.Mix(s, at, 0.55f, 0.1f);
    }
}
