using System.Globalization;

namespace LeapcastStudioAI;

/// <summary>A placement on a canvas, in canvas pixels.</summary>
public readonly record struct LayoutRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
}

public enum CanvasKind { Horizontal, Vertical }

/// <summary>
/// Where every source sits on each canvas. Shared by the pipeline builder (what gets composited) and the preview
/// editor (what you drag), so the two can never disagree.
/// </summary>
public static class LayoutMath
{
    public static bool IsGame(SourceKind k) => k is SourceKind.Display or SourceKind.Window;

    /// <summary>The base layer is the bottom-most visible full-screen capture, drawn by padding instead of overlay.</summary>
    public static bool IsBaseLayer(StudioSource s, IReadOnlyList<StudioSource> visibleLayers) =>
        visibleLayers.Count > 0 && ReferenceEquals(visibleLayers[0], s)
        && Anchors.Resolve(s) == Anchors.Fill
        && SourceTypes.KindOf(s.Type) is SourceKind.Display or SourceKind.Window or SourceKind.Camera;

    /// <summary>Rectangle on the horizontal canvas. <paramref name="aspect"/> = source width / height.</summary>
    public static LayoutRect Horizontal(StudioSource s, int cw, int ch, double aspect, (int w, int h)? naturalSize = null)
    {
        aspect = SafeAspect(aspect);
        string anchor = Anchors.Resolve(s);
        if (anchor == Anchors.Fill) return Fit(cw, ch, aspect);
        if (anchor == Anchors.Free)
        {
            var k = SourceTypes.KindOf(s.Type);
            if (s.W <= 0 && k is SourceKind.Text or SourceKind.Image && naturalSize is { } nat)
                return new LayoutRect(s.X * cw, s.Y * ch, nat.w, nat.h); // free position, natural size (logos, text)
            double w = Math.Max(16, (s.W > 0 ? s.W : 0.25) * cw);
            return new LayoutRect(s.X * cw, s.Y * ch, w, w / aspect);
        }
        double ow, oh;
        var kind = SourceTypes.KindOf(s.Type);
        if ((kind == SourceKind.Text || (kind == SourceKind.Image && s.SizePercent <= 0)) && naturalSize is { } n)
        { ow = n.w; oh = n.h; }
        else
        {
            ow = OverlayWidth(s, cw);
            oh = ow / aspect;
        }
        double m = Math.Max(0, s.MarginPx);
        return anchor switch
        {
            Anchors.TopLeft => new LayoutRect(m, m, ow, oh),
            Anchors.TopRight => new LayoutRect(cw - ow - m, m, ow, oh),
            Anchors.BottomLeft => new LayoutRect(m, ch - oh - m, ow, oh),
            Anchors.BottomRight => new LayoutRect(cw - ow - m, ch - oh - m, ow, oh),
            _ => new LayoutRect((cw - ow) / 2, (ch - oh) / 2, ow, oh),
        };
    }

    public static int OverlayWidth(StudioSource s, int cw) =>
        Math.Max(16, SmartTuner.Even((int)Math.Round(cw * Math.Clamp(s.SizePercent, 1, 100) / 100.0)));

    /// <summary>Is the source shown on the vertical canvas?</summary>
    public static bool ShownVertical(StudioSource s)
    {
        if (!s.Visible || !s.VShow) return false;
        var k = SourceTypes.KindOf(s.Type);
        if (!SourceTypes.IsVideo(k)) return false;
        // automatic layout uses game + camera; overlays (images/text) are opt-in by dragging them in manually
        return !s.VAuto || IsGame(k) || k == SourceKind.Camera;
    }

    /// <summary>Rectangle on the vertical canvas.</summary>
    public static LayoutRect Vertical(StudioSource s, int cw, int ch, double aspect, bool sceneHasCamera)
    {
        aspect = SafeAspect(aspect);
        var k = SourceTypes.KindOf(s.Type);
        if (!s.VAuto)
        {
            double w = Math.Max(16, s.VW * cw);
            return new LayoutRect(s.VX * cw, s.VY * ch, w, w / aspect);
        }
        if (k == SourceKind.Camera)
        {
            // camera on top: 92% width, a little below the top edge
            double w = cw * 0.92, h = w / aspect;
            return new LayoutRect((cw - w) / 2, ch * 0.06, w, h);
        }
        if (IsGame(k))
        {
            // game full width; below the camera if there is one, otherwise centred
            double w = cw, h = w / aspect;
            double y = sceneHasCamera ? Math.Max(ch * 0.06 + cw * 0.92 / (16.0 / 9) + ch * 0.04, (ch - h) * 0.62) : (ch - h) / 2;
            return new LayoutRect(0, Math.Min(y, ch - h), w, h);
        }
        double ow = cw * 0.6;
        return new LayoutRect((cw - ow) / 2, ch * 0.75, ow, ow / aspect);
    }

    public static LayoutRect Fit(int cw, int ch, double aspect)
    {
        double w = cw, h = cw / aspect;
        if (h > ch) { h = ch; w = ch * aspect; }
        return new LayoutRect((cw - w) / 2, (ch - h) / 2, w, h);
    }

    /// <summary>Typical aspect before the real size is known (the engine reports real sizes once running).</summary>
    public static double EstimateAspect(StudioSource s)
    {
        var k = SourceTypes.KindOf(s.Type);
        if (k == SourceKind.Camera && SmartTuner.TryParseResolution(s.CameraResolution, out var w, out var h)) return w / (double)h;
        if (k == SourceKind.Text) return 6;
        return 16.0 / 9;
    }

    private static double SafeAspect(double a) => double.IsFinite(a) && a > 0.05 && a < 20 ? a : 16.0 / 9;

    public static (int w, int h) VerticalSize(string? text) =>
        SmartTuner.TryParseResolution(text, out var w, out var h) && h > w ? (w, h) : (720, 1280);

    /// <summary>Snap a moving rectangle to canvas edges and centre lines (within <paramref name="threshold"/> px).</summary>
    public static (double x, double y) Snap(LayoutRect r, int cw, int ch, double threshold)
    {
        double x = r.X, y = r.Y;
        foreach (var (target, edge) in new[] { (0.0, r.X), (cw - r.W, r.X), ((cw - r.W) / 2, r.X) })
            if (Math.Abs(edge - target) < threshold) { x = target; break; }
        foreach (var (target, edge) in new[] { (0.0, r.Y), (ch - r.H, r.Y), ((ch - r.H) / 2, r.Y) })
            if (Math.Abs(edge - target) < threshold) { y = target; break; }
        return (x, y);
    }

    internal static string Px(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
}
