using System.Globalization;

namespace LeapcastStudioAI;

/// <summary>An exposure correction for a camera: what the eq filter on it is set to.</summary>
public readonly record struct LightSettings(double Gamma, double Contrast, double Saturation, double Brightness)
{
    public static readonly LightSettings Neutral = new(1, 1, 1, 0);
    public string Filter(string name) => FormattableString.Invariant($"eq@{name}=gamma={Gamma:0.###}:contrast={Contrast:0.###}:saturation={Saturation:0.###}:brightness={Brightness:0.###}");
    public IEnumerable<string> Commands(string name) => new[]
    {
        FormattableString.Invariant($"eq@{name} -1 gamma {Gamma:0.###}"), FormattableString.Invariant($"eq@{name} -1 contrast {Contrast:0.###}"),
        FormattableString.Invariant($"eq@{name} -1 saturation {Saturation:0.###}"), FormattableString.Invariant($"eq@{name} -1 brightness {Brightness:0.###}"),
    };
}

/// <summary>
/// Auto light for a webcam: once a second it looks at ~2,000 pixels of the latest frame (more weight in the middle,
/// where your face usually is) and nudges a gamma / contrast / colour correction toward a well-lit picture: a dark
/// room is lifted (highlights protected, colour and contrast brought back), a blown-out one is calmed down. The
/// correction runs on the camera after it's scaled to its size on screen, and only changes when you'd see it, so it
/// costs next to nothing.
/// </summary>
public sealed class CameraAutoLight
{
    private const double Target = 0.46;           // mid-tone the face should sit at (0…1 luma)
    private LightSettings _current = LightSettings.Neutral, _sent = LightSettings.Neutral;
    private double _smoothGamma = 1;
    private bool _first = true;

    public LightSettings Current => _current;
    public double LastMean { get; private set; } = double.NaN;

    public CameraAutoLight(LightSettings? start = null) { if (start is { } s) { _current = _sent = s; _smoothGamma = s.Gamma; _first = false; } }

    /// <summary>Center-weighted mean and the 95th percentile of luma (0…1) from a YUV 4:2:0 frame (Y plane first).</summary>
    public static (double Mean, double P95) Measure(byte[] yuv, int w, int h)
    {
        if (w < 8 || h < 8 || yuv.Length < w * h) return (double.NaN, double.NaN);
        const int GX = 56, GY = 36;
        var hist = new int[256];
        double sum = 0, wsum = 0;
        int n = 0;
        for (int gy = 0; gy < GY; gy++)
        {
            int y = (int)((gy + 0.5) * h / GY);
            double dy = (y - h / 2.0) / (h / 2.0);
            for (int gx = 0; gx < GX; gx++)
            {
                int x = (int)((gx + 0.5) * w / GX);
                double dx = (x - w / 2.0) / (w / 2.0);
                byte v = yuv[y * w + x];
                double weight = 1 + 2 * Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy) / 0.7); // the middle counts ~3×
                // limited-range video: 16…235
                double l = Math.Clamp((v - 16) / 219.0, 0, 1);
                sum += l * weight; wsum += weight;
                hist[v]++; n++;
            }
        }
        int acc = 0, p95 = 255;
        for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= n * 0.95) { p95 = i; break; } }
        return (sum / wsum, Math.Clamp((p95 - 16) / 219.0, 0, 1));
    }

    /// <summary>
    /// Takes a new measurement; returns the settings to send when they changed enough to see (else null).
    /// <paramref name="seconds"/>: time since the last call (the correction eases in over a few seconds).
    /// </summary>
    public LightSettings? Update(double mean, double p95, double seconds = 1)
    {
        if (double.IsNaN(mean)) return null;
        LastMean = mean;
        // a black frame (lens covered, camera starting) isn't a dark room: leave it
        if (mean < 0.02) return null;
        // gamma that would put the middle at the target: mean^(1/g) = target
        double want = Math.Log(Math.Clamp(mean, 0.03, 0.97)) / Math.Log(Target);
        // close enough: no correction (a well-lit picture stays exactly as the camera made it)
        if (Math.Abs(mean - Target) < 0.06) want = 1;
        want = Math.Clamp(want, 0.72, 2.3);
        // don't push the brightest parts (a window, a lamp) into white; when they're white already (a window
        // behind you), the room is still lifted, just less
        if (want > 1 && p95 >= 0.93) want = Math.Min(want, 1.5);
        else if (want > 1 && p95 > 0.02)
        {
            double maxForHighlights = Math.Log(Math.Clamp(p95, 0.05, 0.999)) / Math.Log(0.97);
            want = Math.Min(want, Math.Max(1, maxForHighlights));
        }
        // ease toward it (about 3 s), straight there the first time
        double k = _first ? 1 : 1 - Math.Exp(-seconds / 3.0);
        _first = false;
        _smoothGamma += (want - _smoothGamma) * k;
        double g = _smoothGamma;
        // a lifted dark picture looks grey and washed: a little contrast and colour back; a calmed bright one, a touch more colour
        double lift = Math.Max(0, g - 1);
        var next = new LightSettings(
            Math.Round(g, 3),
            Math.Round(1 + Math.Min(0.12, lift * 0.1), 3),
            Math.Round(1 + Math.Min(0.25, lift * 0.2) + (g < 1 ? 0.06 : 0), 3),
            Math.Round(lift > 0.4 ? -0.015 : 0, 3)); // lifted blacks back down a hair
        _current = next;
        // only when you'd see it
        if (Math.Abs(next.Gamma - _sent.Gamma) < 0.03 && Math.Abs(next.Saturation - _sent.Saturation) < 0.03) return null;
        _sent = next;
        return next;
    }
}
