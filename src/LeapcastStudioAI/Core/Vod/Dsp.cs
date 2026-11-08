namespace LeapcastStudioAI.Vod;

/// <summary>Small audio building blocks for the generated music and sound effects (48 kHz, float).</summary>
public static class Dsp
{
    public const int Rate = 48000;

    /// <summary>A stereo buffer: left and right channels.</summary>
    public sealed class Stereo
    {
        public readonly float[] L, R;
        public Stereo(int frames) { L = new float[frames]; R = new float[frames]; }
        public int Length => L.Length;

        /// <summary>Adds <paramref name="src"/> (mono) at <paramref name="at"/> with gain and pan (-1 left … 1 right).</summary>
        public void Mix(float[] src, int at, float gain = 1, float pan = 0)
        {
            float gl = gain * MathF.Sqrt(0.5f * (1 - pan)), gr = gain * MathF.Sqrt(0.5f * (1 + pan));
            int n = Math.Min(src.Length, Length - at);
            for (int i = Math.Max(0, -at); i < n; i++) { L[at + i] += src[i] * gl; R[at + i] += src[i] * gr; }
        }

        public void Mix(Stereo src, int at, float gain = 1)
        {
            int n = Math.Min(src.Length, Length - at);
            for (int i = Math.Max(0, -at); i < n; i++) { L[at + i] += src.L[i] * gain; R[at + i] += src.R[i] * gain; }
        }

        public float Peak()
        {
            float p = 0;
            for (int i = 0; i < Length; i++) p = MathF.Max(p, MathF.Max(MathF.Abs(L[i]), MathF.Abs(R[i])));
            return p;
        }
    }

    public static int Frames(double seconds) => (int)Math.Round(seconds * Rate);

    /// <summary>A biquad filter (RBJ cookbook).</summary>
    public sealed class Biquad
    {
        private double b0, b1, b2, a1, a2, z1, z2;
        public static Biquad LowPass(double f, double q = 0.707) => new Biquad().Set(0, f, q);
        public static Biquad HighPass(double f, double q = 0.707) => new Biquad().Set(1, f, q);
        public static Biquad BandPass(double f, double q = 1.0) => new Biquad().Set(2, f, q);

        public Biquad Set(int kind, double f, double q)
        {
            f = Math.Clamp(f, 10, Rate * 0.45);
            double w = 2 * Math.PI * f / Rate, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q);
            double a0;
            switch (kind)
            {
                case 0: b0 = (1 - cs) / 2; b1 = 1 - cs; b2 = (1 - cs) / 2; break;
                case 1: b0 = (1 + cs) / 2; b1 = -(1 + cs); b2 = (1 + cs) / 2; break;
                default: b0 = al; b1 = 0; b2 = -al; break;
            }
            a0 = 1 + al; a1 = -2 * cs; a2 = 1 - al;
            b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
            _kind = kind; _q = q;
            return this;
        }
        private int _kind; private double _q;
        /// <summary>Moves the cutoff without resetting the filter's state (sweeps).</summary>
        public void Tune(double f) { double s1 = z1, s2 = z2; Set(_kind, f, _q); z1 = s1; z2 = s2; }

        public float Process(float x)
        {
            double y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return (float)y;
        }
    }

    /// <summary>Band-limited sawtooth (polyBLEP), phase in cycles.</summary>
    public static float Saw(double phase, double inc)
    {
        double t = phase - Math.Floor(phase);
        double v = 2 * t - 1;
        if (t < inc) { double x = t / inc; v -= x + x - x * x - 1; }
        else if (t > 1 - inc) { double x = (t - 1) / inc; v -= x * x + x + x + 1; }
        return (float)v;
    }

    public static double NoteHz(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

    /// <summary>Freeverb-style stereo reverb (8 combs + 4 allpasses per side).</summary>
    public static Stereo Reverb(Stereo dry, float room = 0.82f, float damp = 0.35f, float wet = 0.3f)
    {
        int[] combs = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        int[] alls = { 556, 441, 341, 225 };
        var outp = new Stereo(dry.Length);
        for (int ch = 0; ch < 2; ch++)
        {
            int spread = ch == 0 ? 0 : 23;
            var src = ch == 0 ? dry.L : dry.R;
            var dst = ch == 0 ? outp.L : outp.R;
            var cb = combs.Select(c => new float[(int)((c + spread) * Rate / 44100.0)]).ToArray();
            var cf = new float[cb.Length];
            var ci = new int[cb.Length];
            var ab = alls.Select(a => new float[(int)((a + spread) * Rate / 44100.0)]).ToArray();
            var ai = new int[ab.Length];
            for (int i = 0; i < src.Length; i++)
            {
                float input = src[i] * 0.015f, acc = 0;
                for (int c = 0; c < cb.Length; c++)
                {
                    var buf = cb[c];
                    float o = buf[ci[c]];
                    cf[c] = o * (1 - damp) + cf[c] * damp;
                    buf[ci[c]] = input + cf[c] * room;
                    if (++ci[c] >= buf.Length) ci[c] = 0;
                    acc += o;
                }
                for (int a = 0; a < ab.Length; a++)
                {
                    var buf = ab[a];
                    float b = buf[ai[a]];
                    buf[ai[a]] = acc + b * 0.5f;
                    acc = b - acc;
                    if (++ai[a] >= buf.Length) ai[a] = 0;
                }
                dst[i] = acc * wet * 3f;
            }
        }
        return outp;
    }

    /// <summary>Gentle saturation and a peak ceiling.</summary>
    public static void Master(Stereo s, float ceiling = 0.89f)
    {
        float peak = s.Peak();
        if (peak <= 0) return;
        float drive = 1.15f / peak;
        for (int i = 0; i < s.Length; i++)
        {
            s.L[i] = MathF.Tanh(s.L[i] * drive) * ceiling;
            s.R[i] = MathF.Tanh(s.R[i] * drive) * ceiling;
        }
    }

    /// <summary>Writes 16-bit stereo PCM WAV.</summary>
    public static void WriteWav(string path, Stereo s)
    {
        using var f = new BinaryWriter(File.Create(path));
        int data = s.Length * 4;
        f.Write("RIFF"u8); f.Write(36 + data); f.Write("WAVE"u8);
        f.Write("fmt "u8); f.Write(16); f.Write((short)1); f.Write((short)2); f.Write(Rate); f.Write(Rate * 4); f.Write((short)4); f.Write((short)16);
        f.Write("data"u8); f.Write(data);
        var buf = new byte[Math.Min(s.Length, 65536) * 4];
        for (int start = 0; start < s.Length; start += 65536)
        {
            int n = Math.Min(65536, s.Length - start);
            for (int i = 0; i < n; i++)
            {
                short l = (short)Math.Clamp(s.L[start + i] * 32767f, -32768f, 32767f), r = (short)Math.Clamp(s.R[start + i] * 32767f, -32768f, 32767f);
                buf[i * 4] = (byte)l; buf[i * 4 + 1] = (byte)(l >> 8); buf[i * 4 + 2] = (byte)r; buf[i * 4 + 3] = (byte)(r >> 8);
            }
            f.Write(buf, 0, n * 4);
        }
    }
}
