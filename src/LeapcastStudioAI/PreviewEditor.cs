using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LeapcastStudioAI;

/// <summary>
/// Drag / resize editing of sources directly on a canvas preview (horizontal or vertical). Works on canvas pixels:
/// the host grid is exactly canvas-sized inside a Viewbox, so mouse positions need no conversion.
/// Moves are pushed to the running pipeline live (overlay commands); resizes apply when the mouse is released.
/// </summary>
public sealed class PreviewEditor
{
    private readonly Canvas _layer;
    private readonly FrameworkElement _viewbox;
    private readonly CanvasKind _kind;
    private StudioSource? _drag;
    private bool _resizing;
    private Point _grabOffset;
    private LayoutRect _startRect;
    private bool _changedSize;
    private bool _moved;

    public Func<StudioScene?> Scene { get; init; } = () => null;
    public Func<(int W, int H)> CanvasSize { get; init; } = () => (1920, 1080);
    public Func<StudioSource, double> Aspect { get; init; } = LayoutMath.EstimateAspect;
    public Func<StudioSource, (int W, int H)?> NaturalSize { get; init; } = _ => null;

    public StudioSource? Selected { get; private set; }
    /// <summary>The user picked a source by clicking it on the canvas.</summary>
    public event Action<StudioSource?>? SelectionChanged;
    /// <summary>A position changed during a drag: push it to the running pipeline (cheap, call often).</summary>
    public event Action? Moving;
    /// <summary>The mouse was released after a change: save + apply (resizes rebuild the pipeline).</summary>
    public event Action? Committed;
    public event Action<StudioSource, CanvasKind>? ContextMenuRequested;

    public PreviewEditor(Canvas layer, FrameworkElement viewbox, CanvasKind kind)
    {
        _layer = layer;
        _viewbox = viewbox;
        _kind = kind;
        _layer.MouseLeftButtonDown += OnDown;
        _layer.MouseMove += OnMove;
        _layer.MouseLeftButtonUp += OnUp;
        _layer.MouseRightButtonUp += OnRightUp;
        _layer.LostMouseCapture += (_, _) => EndDrag();
        _layer.Focusable = true; // so arrow keys nudge only after clicking the preview
    }

    public void Select(StudioSource? s)
    {
        Selected = s;
        Redraw();
    }

    /// <summary>Display pixels per canvas pixel (handles and snapping stay the same size on screen).</summary>
    private double Scale
    {
        get
        {
            var (w, _) = CanvasSize();
            return _viewbox.ActualWidth > 0 && w > 0 ? Math.Max(0.05, _viewbox.ActualWidth / w) : 0.5;
        }
    }

    /// <summary>Sources drawn on this canvas, bottom → top.</summary>
    public List<StudioSource> Layers()
    {
        var scene = Scene();
        if (scene is null) return new();
        var sources = scene.Sources.ToList();
        return _kind == CanvasKind.Horizontal
            ? PipelineBuilder.VisibleLayers(sources).Where(x => SourceTypes.IsVideo(SourceTypes.KindOf(x.Type))).ToList()
            : PipelineBuilder.VerticalLayers(sources);
    }

    public LayoutRect RectOf(StudioSource s, IReadOnlyList<StudioSource>? layers = null)
    {
        var (cw, ch) = CanvasSize();
        if (_kind == CanvasKind.Horizontal)
        {
            layers ??= Layers();
            if (LayoutMath.IsBaseLayer(s, layers)) return LayoutMath.FillRect(s, cw, ch, Aspect(s));
            return LayoutMath.Horizontal(s, cw, ch, Aspect(s), NaturalSize(s));
        }
        layers ??= Layers();
        bool hasCam = layers.Any(x => SourceTypes.KindOf(x.Type) == SourceKind.Camera && x.VAuto);
        return LayoutMath.Vertical(s, cw, ch, Aspect(s), hasCam);
    }

    private StudioSource? HitTest(Point p, IReadOnlyList<StudioSource> layers)
    {
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            var r = RectOf(layers[i], layers);
            if (p.X >= r.X && p.X <= r.Right && p.Y >= r.Y && p.Y <= r.Bottom) return layers[i];
        }
        return null;
    }

    private bool OnHandle(Point p, LayoutRect r)
    {
        double h = 16 / Scale;
        return Math.Abs(p.X - r.Right) <= h && Math.Abs(p.Y - r.Bottom) <= h;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_layer);
        _layer.Focus();
        var layers = Layers();
        // the selected source's resize handle wins even where another source overlaps it
        if (Selected is not null && layers.Contains(Selected) && OnHandle(p, RectOf(Selected, layers)))
        {
            BeginDrag(Selected, p, layers, resize: true);
            e.Handled = true;
            return;
        }
        var hit = HitTest(p, layers);
        if (!ReferenceEquals(hit, Selected))
        {
            Selected = hit;
            SelectionChanged?.Invoke(hit);
        }
        if (hit is not null)
        {
            BeginDrag(hit, p, layers, resize: OnHandle(p, RectOf(hit, layers)));
            e.Handled = true;
        }
        Redraw();
    }

    private void BeginDrag(StudioSource s, Point p, IReadOnlyList<StudioSource> layers, bool resize)
    {
        _startRect = RectOf(s, layers);
        _drag = s;
        _resizing = resize;
        _moved = false;
        _changedSize = false;
        _grabOffset = new Point(p.X - _startRect.X, p.Y - _startRect.Y);
        _layer.CaptureMouse();
    }

    /// <summary>Switch the source to free placement at its current spot (same size), so it can follow the mouse.</summary>
    private void MakeFree(StudioSource s)
    {
        var (cw, ch) = CanvasSize();
        if (_kind == CanvasKind.Horizontal)
        {
            if (Anchors.Resolve(s) == Anchors.Free) return;
            var k = SourceTypes.KindOf(s.Type);
            bool natural = k == SourceKind.Text || (k == SourceKind.Image && s.SizePercent <= 0);
            s.X = _startRect.X / cw;
            s.Y = _startRect.Y / ch;
            s.W = natural ? 0 : _startRect.W / cw; // natural-size logos/text keep their size (no rebuild)
            s.Anchor = Anchors.Free;
        }
        else
        {
            if (!s.VAuto) return;
            s.VX = _startRect.X / cw;
            s.VY = _startRect.Y / ch;
            s.VW = _startRect.W / cw;
            s.VAuto = false;
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_layer);
        if (_drag is null)
        {
            // cursor feedback
            var layers = Layers();
            bool handle = Selected is not null && layers.Contains(Selected) && OnHandle(p, RectOf(Selected, layers));
            _layer.Cursor = handle ? Cursors.SizeNWSE : HitTest(p, layers) is not null ? Cursors.SizeAll : Cursors.Arrow;
            return;
        }
        var (cw, ch) = CanvasSize();
        if (!_moved)
        {
            // ignore tiny jitter on click
            double dx = p.X - (_startRect.X + _grabOffset.X), dy = p.Y - (_startRect.Y + _grabOffset.Y);
            if (Math.Abs(dx) * Scale < 3 && Math.Abs(dy) * Scale < 3) return;
            _moved = true;
            MakeFree(_drag);
        }
        bool snap = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
        if (_resizing)
        {
            double w = Math.Clamp(p.X - _startRect.X, 32, cw * 1.5);
            if (snap && Math.Abs(_startRect.X + w - cw) < 12 / Scale) w = cw - _startRect.X; // snap to the right edge
            if (_kind == CanvasKind.Horizontal) _drag.W = w / cw; else _drag.VW = w / cw;
            _changedSize = true;
        }
        else
        {
            double x = p.X - _grabOffset.X, y = p.Y - _grabOffset.Y;
            var r = new LayoutRect(x, y, _startRect.W, _startRect.H);
            if (snap) (x, y) = LayoutMath.Snap(r, cw, ch, 12 / Scale);
            if (_kind == CanvasKind.Horizontal) { _drag.X = x / cw; _drag.Y = y / ch; }
            else { _drag.VX = x / cw; _drag.VY = y / ch; }
            Moving?.Invoke();
        }
        Redraw();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        bool changed = _drag is not null && _moved;
        EndDrag();
        if (changed) Committed?.Invoke();
    }

    private void EndDrag()
    {
        if (_drag is null) return;
        _drag = null;
        if (_layer.IsMouseCaptured) _layer.ReleaseMouseCapture();
        Redraw();
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_layer);
        var hit = HitTest(p, Layers());
        if (hit is null) return;
        if (!ReferenceEquals(hit, Selected)) { Selected = hit; SelectionChanged?.Invoke(hit); Redraw(); }
        ContextMenuRequested?.Invoke(hit, _kind);
        e.Handled = true;
    }

    /// <summary>Moves the selected source by a few pixels (arrow keys).</summary>
    public bool Nudge(int dx, int dy)
    {
        var s = Selected;
        if (s is null || !Layers().Contains(s)) return false;
        var (cw, ch) = CanvasSize();
        _startRect = RectOf(s);
        MakeFree(s);
        if (_kind == CanvasKind.Horizontal) { s.X += dx / (double)cw; s.Y += dy / (double)ch; }
        else { s.VX += dx / (double)cw; s.VY += dy / (double)ch; }
        Redraw();
        Moving?.Invoke();
        return true;
    }

    public bool ChangedSizeLastDrag => _changedSize;

    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF));
    private static readonly Brush Faint = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LabelBg = new SolidColorBrush(Color.FromArgb(0xCC, 0x2C, 0x27, 0x66));

    /// <summary>Outlines for every source, a highlighted frame + resize handle for the selected one.</summary>
    public void Redraw()
    {
        _layer.Children.Clear();
        var layers = Layers();
        double scale = Scale;
        double thin = 1 / scale, thick = 2 / scale;
        foreach (var s in layers)
        {
            var r = RectOf(s, layers);
            bool sel = ReferenceEquals(s, Selected);
            var box = new Rectangle
            {
                Width = Math.Max(1, r.W), Height = Math.Max(1, r.H),
                Stroke = sel ? Accent : Faint, StrokeThickness = sel ? thick : thin,
                StrokeDashArray = sel ? null : new DoubleCollection { 4, 3 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, r.X); Canvas.SetTop(box, r.Y);
            _layer.Children.Add(box);
            if (!sel) continue;
            double hs = 12 / scale;
            var handle = new Rectangle { Width = hs, Height = hs, Fill = Accent, IsHitTestVisible = false };
            Canvas.SetLeft(handle, r.Right - hs / 2); Canvas.SetTop(handle, r.Bottom - hs / 2);
            _layer.Children.Add(handle);
            var label = new Border
            {
                Background = LabelBg, CornerRadius = new CornerRadius(3 / scale), Padding = new Thickness(5 / scale, 2 / scale, 5 / scale, 2 / scale),
                Child = new TextBlock { Text = s.Name, Foreground = Brushes.White, FontSize = 12 / scale },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, Math.Max(0, r.X));
            Canvas.SetTop(label, Math.Max(0, r.Y - 20 / scale));
            _layer.Children.Add(label);
        }
    }
}
