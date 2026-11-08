namespace LeapcastStudioAI.Vod;

/// <summary>The editor's sound effects, synthesized (no sample packs): each call is slightly different, like a human
/// editor never dropping the exact same whoosh twice.</summary>
public enum SfxKind { Whoosh, SoftWhoosh, Impact, Riser, Pop, SubDrop, Shimmer }

public static class Sfx
{
    public static Dsp.Stereo Make(SfxKind kind, int seed)
    {
        var rng = new Random(seed);
        float Noise() => (float)(rng.NextDouble() * 2 - 1);
        switch (kind)
        {
            case SfxKind.Whoosh:
            case SfxKind.SoftWhoosh:
            {
                bool soft = kind == SfxKind.SoftWhoosh;
                double len = soft ? 0.32 : 0.48 + rng.NextDouble() * 0.12;
                int n = Dsp.Frames(len);
                var s = new Dsp.Stereo(n);
                var bpL = Dsp.Biquad.BandPass(300, 0.9); var bpR = Dsp.Biquad.BandPass(330, 0.9);
                double peakAt = 0.55 + rng.NextDouble() * 0.1;
                bool leftToRight = rng.NextDouble() < 0.5;
                for (int i = 0; i < n; i++)
                {
                    double p = i / (double)n;
                    // the air rushes up to the peak and away: band sweeps up then down, loudness follows
                    double sweep = p < peakAt ? p / peakAt : 1 - (p - peakAt) / (1 - peakAt);
                    if (i % 48 == 0) { double f = 250 + (soft ? 2600 : 3800) * sweep * sweep; bpL.Tune(f); bpR.Tune(f * 1.07); }
                    double env = Math.Pow(Math.Sin(Math.PI * Math.Min(1, p)), 1.6) * (p < peakAt ? 1 : 1 - 0.4 * (p - peakAt));
                    float pan = (float)((leftToRight ? -1 : 1) * (1 - 2 * p) * 0.8);
                    float a = bpL.Process(Noise()), b = bpR.Process(Noise());
                    s.L[i] = a * (float)env * (1 - pan) * 1.6f;
                    s.R[i] = b * (float)env * (1 + pan) * 1.6f;
                }
                Normalize(s, soft ? 0.32f : 0.55f);
                return s;
            }
            case SfxKind.Impact:
            {
                int n = Dsp.Frames(1.6);
                var dry = new Dsp.Stereo(n);
                var lp = Dsp.Biquad.LowPass(2400);
                double ph = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    double f = 38 + 70 * Math.Exp(-x * 9);
                    ph += f / Dsp.Rate;
                    float boom = (float)(Math.Sin(2 * Math.PI * ph) * Math.Exp(-x * 2.4));
                    float crack = lp.Process(Noise()) * (float)Math.Exp(-x * 22) * 0.9f;
                    float v = MathF.Tanh((boom * 1.4f + crack) * 1.8f);
                    dry.L[i] = v; dry.R[i] = v;
                }
                var verb = Dsp.Reverb(dry, 0.86f, 0.4f, 0.25f);
                dry.Mix(verb, 0);
                Normalize(dry, 0.8f);
                return dry;
            }
            case SfxKind.Riser:
            {
                int n = Dsp.Frames(1.4);
                var s = new Dsp.Stereo(n);
                var bp = Dsp.Biquad.BandPass(300, 1.2);
                double ph = 0;
                for (int i = 0; i < n; i++)
                {
                    double p = i / (double)n;
                    if (i % 48 == 0) bp.Tune(300 + 7000 * p * p);
                    double hz = 180 * Math.Pow(2, p * 2.5);
                    ph += hz / Dsp.Rate;
                    float tone = Dsp.Saw(ph, hz / Dsp.Rate) * 0.18f;
                    float v = (bp.Process(Noise()) * 1.4f + tone) * (float)(p * p * p);
                    s.L[i] = v; s.R[i] = v * 0.92f + bp.Process(Noise()) * (float)(p * p) * 0.1f;
                }
                Normalize(s, 0.5f);
                return s;
            }
            case SfxKind.Pop:
            {
                int n = Dsp.Frames(0.09);
                var s = new Dsp.Stereo(n);
                double ph = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    double f = 1100 - 4500 * x;
                    ph += f / Dsp.Rate;
                    float v = (float)(Math.Sin(2 * Math.PI * ph) * Math.Exp(-x * 45));
                    s.L[i] = v; s.R[i] = v;
                }
                Normalize(s, 0.3f);
                return s;
            }
            case SfxKind.SubDrop:
            {
                int n = Dsp.Frames(1.1);
                var s = new Dsp.Stereo(n);
                double ph = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate;
                    double f = 90 * Math.Exp(-x * 1.6) + 28;
                    ph += f / Dsp.Rate;
                    float v = (float)(Math.Sin(2 * Math.PI * ph) * Math.Min(1, x / 0.01) * Math.Exp(-x * 2.2));
                    s.L[i] = v; s.R[i] = v;
                }
                Normalize(s, 0.75f);
                return s;
            }
            default: // shimmer: a bright bell for titles and chapter cards
            {
                int n = Dsp.Frames(1.8);
                var dry = new Dsp.Stereo(n);
                double[] partials = { 1, 2.76, 5.4, 8.93 };
                double baseHz = 880 * Math.Pow(2, rng.Next(0, 5) / 12.0);
                for (int i = 0; i < n; i++)
                {
                    double x = i / (double)Dsp.Rate, v = 0;
                    for (int k = 0; k < partials.Length; k++) v += Math.Sin(2 * Math.PI * baseHz * partials[k] * x) * Math.Exp(-x * (2 + k * 2.5)) / (k + 1);
                    dry.L[i] = (float)v; dry.R[i] = (float)(v * 0.95);
                }
                dry.Mix(Dsp.Reverb(dry, 0.9f, 0.2f, 0.35f), 0);
                Normalize(dry, 0.28f);
                return dry;
            }
        }
    }

    private static void Normalize(Dsp.Stereo s, float to)
    {
        float p = s.Peak();
        if (p <= 0) return;
        float g = to / p;
        for (int i = 0; i < s.Length; i++) { s.L[i] *= g; s.R[i] *= g; }
    }

    /// <summary>The music slows to a stop like a turntable losing power (used when a fight is lost).</summary>
    public static void TapeStop(Dsp.Stereo music, int at, double seconds)
    {
        int n = Dsp.Frames(seconds);
        if (at < 0 || at + n >= music.Length) return;
        int take = Math.Min(n * 2, music.Length - at);
        var srcL = music.L.AsSpan(at, take).ToArray();
        var srcR = music.R.AsSpan(at, take).ToArray();
        double pos = 0;
        for (int i = 0; i < n; i++)
        {
            double speed = 1 - i / (double)n;
            int k = (int)pos;
            float f = (float)(pos - k);
            float l = k + 1 < srcL.Length ? srcL[k] * (1 - f) + srcL[k + 1] * f : 0;
            float r = k + 1 < srcR.Length ? srcR[k] * (1 - f) + srcR[k + 1] * f : 0;
            float fade = (float)speed;
            music.L[at + i] = l * fade; music.R[at + i] = r * fade;
            pos += speed * speed;
        }
    }
}
