namespace LeapcastStudioAI;

/// <summary>How a source's latest picture is laid out in memory.</summary>
public enum FramePixels { I420, Bgra }

/// <summary>A source's latest picture. A new array means a new picture (an unchanged one isn't uploaded again).</summary>
public sealed record GpuFrame(byte[] Data, int Width, int Height, FramePixels Format);

/// <summary>
/// One source as drawn on the canvas: where (canvas pixels), which part of its picture (0…1 texture coordinates,
/// for crop and fill), mirrored or not, with what colour correction (auto light) and opacity.
/// </summary>
public sealed record GpuLayer(string Key, LayoutRect Rect, double U0 = 0, double V0 = 0, double U1 = 1, double V1 = 1,
    bool Mirror = false, LightSettings? Light = null, double Opacity = 1);

public sealed record GpuScene(IReadOnlyList<GpuLayer> Layers);

/// <summary>
/// What the canvas shows: the scene on air, and during a fade the one it's fading from (the fade's progress is
/// worked out from the clock, so it's smooth whatever the frame rate).
/// </summary>
public sealed record GpuState(IReadOnlyList<GpuScene> Scenes, int Active, int? From = null, DateTime FadeStartUtc = default, double FadeSeconds = 0)
{
    public static readonly GpuState Empty = new(Array.Empty<GpuScene>(), 0);

    /// <summary>The fade's progress at <paramref name="now"/> (1 = done / no fade).</summary>
    public double Mix(DateTime now) => From is null || FadeSeconds <= 0 ? 1 : Math.Clamp((now - FadeStartUtc).TotalSeconds / FadeSeconds, 0, 1);
}

/// <summary>
/// The scene compositor on the graphics card: every source's latest picture is uploaded (only when it changed),
/// scaled, cropped, colour-corrected and layered by the GPU, and the finished canvas comes back once per frame
/// as YUV 4:2:0, ready for the encoder. Moving, resizing, showing, hiding and switching scenes are just a new
/// <see cref="GpuState"/>: nothing restarts.
/// </summary>
public interface IGpuCanvas : IDisposable
{
    int Width { get; }
    int Height { get; }
    int Fps { get; }
    /// <summary>The graphics card in use ("… (software)" when Windows' software renderer is used instead).</summary>
    string Adapter { get; }
    void SetState(GpuState state);
    /// <summary>The latest finished canvas (YUV 4:2:0, Width × Height), or null before the first.</summary>
    byte[]? Latest();
    long Frames { get; }
    /// <summary>Average time per canvas (ms): uploads, drawing and bringing the result back.</summary>
    double FrameMs { get; }
    /// <summary>Pictures uploaded so far (unchanged pictures aren't).</summary>
    long Uploads { get; }
    string? Error { get; }
}

public static class GpuCanvasMath
{
    /// <summary>A canvas pixel rectangle in Direct3D's −1…1 coordinates (x0, y0 top-left, x1, y1 bottom-right).</summary>
    public static (float X0, float Y0, float X1, float Y1) Ndc(LayoutRect r, int w, int h) =>
        ((float)(r.X / w * 2 - 1), (float)(1 - r.Y / h * 2), (float)((r.X + r.W) / w * 2 - 1), (float)(1 - (r.Y + r.H) / h * 2));

    /// <summary>
    /// The part of a picture shown when it fills <paramref name="dst"/> (aspect kept by cutting the edges, the
    /// way "crop to fill" works), after the source's own crop (pixels).
    /// </summary>
    public static (double U0, double V0, double U1, double V1) FillUv(int srcW, int srcH, (int L, int T, int R, int B) crop, double dstW, double dstH, string fill)
    {
        double l = Math.Max(0, crop.L), t = Math.Max(0, crop.T), r = Math.Max(0, crop.R), b = Math.Max(0, crop.B);
        double cw = Math.Max(1, srcW - l - r), ch = Math.Max(1, srcH - t - b);
        double u0 = l / srcW, v0 = t / srcH, u1 = (srcW - r) / srcW, v1 = (srcH - b) / srcH;
        if (fill != Anchors.FillCrop || dstW <= 0 || dstH <= 0) return (u0, v0, u1, v1);
        double target = dstW / dstH, src = cw / ch;
        if (src > target)
        {
            double keep = target / src, cut = (u1 - u0) * (1 - keep) / 2;
            return (u0 + cut, v0, u1 - cut, v1);
        }
        else
        {
            double keep = src / target, cut = (v1 - v0) * (1 - keep) / 2;
            return (u0, v0 + cut, u1, v1 - cut);
        }
    }
}
