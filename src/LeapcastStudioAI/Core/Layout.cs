using System.Globalization;

namespace LeapcastStudioAI;

/// <summary>A placement on a canvas, in canvas pixels.</summary>
public readonly record struct LayoutRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
}

public enum CanvasKind { Horizontal, Vertical }

/// <summary>Which part of a source's frame the mouse grabbed on the preview.</summary>
public enum Grip { None, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

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
        if (anchor == Anchors.Fill) return FillRect(s, cw, ch, aspect);
        if (anchor == Anchors.Free)
        {
            var k = SourceTypes.KindOf(s.Type);
            if (s.W <= 0 && SourceTypes.IsGraphic(k) && naturalSize is { } nat)
                return new LayoutRect(s.X * cw, s.Y * ch, nat.w, nat.h); // free position, natural size (logos, text)
            double w = Math.Max(16, (s.W > 0 ? s.W : 0.25) * cw);
            return new LayoutRect(s.X * cw, s.Y * ch, w, s.H > 0 && s.W > 0 ? Math.Max(16, s.H * ch) : w / aspect);
        }
        double ow, oh;
        var kind = SourceTypes.KindOf(s.Type);
        if ((kind is SourceKind.Text or SourceKind.Timer || (kind is SourceKind.Image or SourceKind.Browser && s.SizePercent <= 0)) && naturalSize is { } n)
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
    public static bool ShownVertical(StudioSource s) => OnVerticalCanvas(s) && s.Shown;

    /// <summary>Drawn on the vertical canvas when visible (hidden sources stay in the pipeline, switched off).</summary>
    public static bool OnVerticalCanvas(StudioSource s)
    {
        if (!s.VShow) return false;
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
            return new LayoutRect(s.VX * cw, s.VY * ch, w, s.VH > 0 ? Math.Max(16, s.VH * ch) : w / aspect);
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

    /// <summary>Where a full-screen source lands: cropped to fill (may overhang the canvas), fitted, or stretched.</summary>
    public static LayoutRect FillRect(StudioSource s, int cw, int ch, double aspect)
    {
        aspect = SafeAspect(aspect);
        if (s.FillMode == Anchors.FillStretch) return new LayoutRect(0, 0, cw, ch);
        if (s.FillMode == Anchors.FillFit) return Fit(cw, ch, aspect);
        double w = cw, h = cw / aspect;
        if (h < ch) { h = ch; w = ch * aspect; }
        return new LayoutRect((cw - w) / 2, (ch - h) / 2, w, h);
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
        if (k is SourceKind.Text or SourceKind.Timer) return 6;
        if (k == SourceKind.Browser) return Math.Max(64, s.BrowserWidth) / (double)Math.Max(64, s.BrowserHeight);
        return 16.0 / 9;
    }

    private static double SafeAspect(double a) => double.IsFinite(a) && a > 0.05 && a < 20 ? a : 16.0 / 9;

    public static (int w, int h) VerticalSize(string? text) =>
        SmartTuner.TryParseResolution(text, out var w, out var h) && h > w ? (w, h) : (720, 1280);

    /// <summary>Snap a moving rectangle to canvas edges and centre lines (within <paramref name="threshold"/> px).</summary>
    /// <summary>
    /// Resizes <paramref name="start"/> by dragging <paramref name="grip"/> to (px, py). By default the picture keeps
    /// its shape and the opposite corner or edge stays put; with <paramref name="keepShape"/> false each dragged edge
    /// simply follows the mouse (stretching the picture). Edges that land within <paramref name="snap"/> of the
    /// canvas border snap to it (0 = no snapping).
    /// </summary>
    public static LayoutRect Resize(LayoutRect start, Grip grip, double px, double py, double cw, double ch, double snap,
        double minW = 32, bool keepShape = true)
    {
        if (start.W <= 0 || start.H <= 0) return start;
        if (!keepShape) return Stretch(start, grip, px, py, cw, ch, snap, minW);
        double aspect = start.W / start.H;
        // the point that stays fixed: the opposite corner, or the middle of the opposite edge
        double fx = grip switch
        {
            Grip.TopLeft or Grip.BottomLeft or Grip.Left => start.Right,
            Grip.TopRight or Grip.BottomRight or Grip.Right => start.X,
            _ => start.X + start.W / 2,
        };
        double fy = grip switch
        {
            Grip.TopLeft or Grip.TopRight or Grip.Top => start.Bottom,
            Grip.BottomLeft or Grip.BottomRight or Grip.Bottom => start.Y,
            _ => start.Y + start.H / 2,
        };
        bool horizontal = grip is not (Grip.Top or Grip.Bottom);
        bool vertical = grip is not (Grip.Left or Grip.Right);
        // snap the edge being dragged to the canvas border
        if (snap > 0)
        {
            if (horizontal && fx <= start.X + 0.5 && Math.Abs(px - cw) < snap) px = cw;
            if (horizontal && fx >= start.Right - 0.5 && Math.Abs(px) < snap) px = 0;
            if (vertical && fy <= start.Y + 0.5 && Math.Abs(py - ch) < snap) py = ch;
            if (vertical && fy >= start.Bottom - 0.5 && Math.Abs(py) < snap) py = 0;
        }
        double wFromX = Math.Abs(px - fx);
        double wFromY = Math.Abs(py - fy) * aspect;
        double w = grip switch
        {
            Grip.Left or Grip.Right => wFromX,
            Grip.Top or Grip.Bottom => wFromY,
            // corners: follow whichever direction the mouse moved further
            _ => Math.Abs(wFromX - start.W) >= Math.Abs(wFromY - start.W) ? wFromX : wFromY,
        };
        w = Math.Clamp(w, minW, Math.Max(minW, cw * 1.5));
        double h = w / aspect;
        double x = grip switch
        {
            Grip.TopLeft or Grip.BottomLeft or Grip.Left => fx - w,
            Grip.TopRight or Grip.BottomRight or Grip.Right => fx,
            _ => fx - w / 2,
        };
        double y = grip switch
        {
            Grip.TopLeft or Grip.TopRight or Grip.Top => fy - h,
            Grip.BottomLeft or Grip.BottomRight or Grip.Bottom => fy,
            _ => fy - h / 2,
        };
        return new LayoutRect(x, y, w, h);
    }

    private static bool MovesLeft(Grip g) => g is Grip.TopLeft or Grip.Left or Grip.BottomLeft;
    private static bool MovesRight(Grip g) => g is Grip.TopRight or Grip.Right or Grip.BottomRight;
    private static bool MovesTop(Grip g) => g is Grip.TopLeft or Grip.Top or Grip.TopRight;
    private static bool MovesBottom(Grip g) => g is Grip.BottomLeft or Grip.Bottom or Grip.BottomRight;

    private static double SnapTo(double v, double edge, double snap) => snap > 0 && Math.Abs(v - edge) < snap ? edge : v;

    /// <summary>Free resize: each dragged edge follows the mouse (the picture stretches).</summary>
    private static LayoutRect Stretch(LayoutRect r, Grip grip, double px, double py, double cw, double ch, double snap, double minW)
    {
        double x1 = r.X, x2 = r.Right, y1 = r.Y, y2 = r.Bottom, minH = Math.Max(16, minW / 2);
        if (MovesLeft(grip)) x1 = Math.Min(SnapTo(SnapTo(px, 0, snap), cw, snap), x2 - minW);
        if (MovesRight(grip)) x2 = Math.Max(SnapTo(SnapTo(px, cw, snap), 0, snap), x1 + minW);
        if (MovesTop(grip)) y1 = Math.Min(SnapTo(SnapTo(py, 0, snap), ch, snap), y2 - minH);
        if (MovesBottom(grip)) y2 = Math.Max(SnapTo(SnapTo(py, ch, snap), 0, snap), y1 + minH);
        return new LayoutRect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>
    /// Crop by dragging an edge or corner (Ctrl + Shift on the preview): the picture keeps its scale and the dragged
    /// edge cuts into it, in source pixels. <paramref name="crop"/> is the crop at the start of the drag (L, T, R, B)
    /// and <paramref name="source"/> the source's full size before cropping. Crops are even (clean for 4:2:0 video)
    /// and always leave at least 16 px.
    /// </summary>
    public static ((int L, int T, int R, int B) Crop, LayoutRect Rect) CropDrag(LayoutRect start, Grip grip, double px, double py,
        (int L, int T, int R, int B) crop, (int W, int H) source)
    {
        int visW = source.W - crop.L - crop.R, visH = source.H - crop.T - crop.B;
        if (visW <= 0 || visH <= 0 || start.W <= 0 || start.H <= 0) return (crop, start);
        double kx = start.W / visW, ky = start.H / visH; // canvas px per source px
        static int Even(double v) => 2 * (int)Math.Round(v / 2);
        var (l, t, r, b) = crop;
        if (MovesLeft(grip)) l = Math.Clamp(Even(crop.L + (px - start.X) / kx), 0, source.W - crop.R - 16);
        if (MovesRight(grip)) r = Math.Clamp(Even(crop.R + (start.Right - px) / kx), 0, source.W - l - 16);
        if (MovesTop(grip)) t = Math.Clamp(Even(crop.T + (py - start.Y) / ky), 0, source.H - crop.B - 16);
        if (MovesBottom(grip)) b = Math.Clamp(Even(crop.B + (start.Bottom - py) / ky), 0, source.H - t - 16);
        var rect = new LayoutRect(start.X + (l - crop.L) * kx, start.Y + (t - crop.T) * ky,
            (source.W - l - r) * kx, (source.H - t - b) * ky);
        return ((l, t, r, b), rect);
    }

    /// <summary>The resize grip under (px, py), within <paramref name="tolerance"/> canvas pixels; None if none.</summary>
    public static Grip GripAt(LayoutRect r, double px, double py, double tolerance)
    {
        bool nearL = Math.Abs(px - r.X) <= tolerance, nearR = Math.Abs(px - r.Right) <= tolerance;
        bool nearT = Math.Abs(py - r.Y) <= tolerance, nearB = Math.Abs(py - r.Bottom) <= tolerance;
        bool inX = px >= r.X - tolerance && px <= r.Right + tolerance, inY = py >= r.Y - tolerance && py <= r.Bottom + tolerance;
        if (nearT && nearL) return Grip.TopLeft;
        if (nearT && nearR) return Grip.TopRight;
        if (nearB && nearR) return Grip.BottomRight;
        if (nearB && nearL) return Grip.BottomLeft;
        double cx = r.X + r.W / 2, cy = r.Y + r.H / 2;
        if (nearT && Math.Abs(px - cx) <= tolerance) return Grip.Top;
        if (nearB && Math.Abs(px - cx) <= tolerance) return Grip.Bottom;
        if (nearL && Math.Abs(py - cy) <= tolerance) return Grip.Left;
        if (nearR && Math.Abs(py - cy) <= tolerance) return Grip.Right;
        // anywhere else along an edge still resizes (handles are small on a scaled-down preview)
        if (inY && nearR) return Grip.Right;
        if (inY && nearL) return Grip.Left;
        if (inX && nearB) return Grip.Bottom;
        if (inX && nearT) return Grip.Top;
        return Grip.None;
    }

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

/// <summary>
/// Multi-client: every open client of a game side by side. n clients are tiled in a grid (2 → side by side,
/// 3-4 → 2×2, 5-6 → 3×2…), each keeping its own shape, the grid centred on the canvas.
/// </summary>
public static class MultiClient
{
    /// <summary>Each tile as fractions of the canvas (X, Y, W; the height follows the picture's shape).</summary>
    public static List<(double X, double Y, double W)> Grid(int n, double tileAspect = 16.0 / 9, double canvasAspect = 16.0 / 9)
    {
        var tiles = new List<(double, double, double)>();
        if (n <= 0) return tiles;
        int cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (int)Math.Ceiling(n / (double)cols);
        double w = 1.0 / cols, h = w * canvasAspect / tileAspect;          // a tile's height, as a fraction of the canvas height
        if (h * rows > 1) { double k = 1 / (h * rows); w *= k; h *= k; }     // tall tiles: shrink so every row fits
        double y0 = (1 - h * rows) / 2;
        for (int i = 0; i < n; i++)
        {
            int row = i / cols, inRow = row == rows - 1 ? n - row * cols : cols;
            double x0 = (1 - inRow * w) / 2;                                  // a short last row is centred
            tiles.Add((x0 + (i % cols) * w, y0 + row * h, w));
        }
        return tiles;
    }

    /// <summary>
    /// Makes the scene show <paramref name="count"/> clients of the window titled <paramref name="title"/>: the
    /// window sources it already has for that title are reused (client 1, 2…), more are added, extra ones hidden,
    /// and all are tiled. Returns the sources, client 1 first.
    /// </summary>
    public static List<StudioSource> Arrange(StudioScene scene, string title, int count, double tileAspect = 16.0 / 9, double canvasAspect = 16.0 / 9)
    {
        var have = scene.Sources.Where(s => SourceTypes.KindOf(s.Type) == SourceKind.Window && string.Equals(s.WindowTitle.Trim(), title, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.WindowInstance).ToList();
        var result = new List<StudioSource>();
        var first = have.FirstOrDefault();
        for (int i = 0; i < count; i++)
        {
            var s = have.FirstOrDefault(x => x.WindowInstance == i && !result.Contains(x));
            if (s is null)
            {
                s = first?.Copy() ?? new StudioSource { Type = SourceTypes.Window, WindowTitle = title };
                s.Name = $"{title} client {i + 1}";
                s.WindowInstance = i;
                s.FolderId = first?.FolderId;
                // just above the last client, so overlays (camera, chat) stay on top
                int at = result.Count > 0 ? scene.Sources.IndexOf(result[^1]) + 1 : scene.Sources.Count;
                scene.Sources.Insert(Math.Clamp(at, 0, scene.Sources.Count), s);
            }
            s.Visible = true;
            result.Add(s);
        }
        foreach (var extra in have.Where(x => !result.Contains(x))) extra.Visible = false;
        var grid = Grid(count, tileAspect, canvasAspect);
        for (int i = 0; i < result.Count; i++)
        {
            var s = result[i];
            if (count == 1) { s.Anchor = Anchors.Fill; continue; }
            s.Anchor = Anchors.Free; s.FillMode = Anchors.FillFit;
            (s.X, s.Y, s.W) = grid[i]; s.H = 0;
        }
        return result;
    }
}
