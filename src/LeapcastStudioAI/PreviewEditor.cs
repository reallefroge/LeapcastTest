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
/// Every corner and edge of the selected source is a grip: drag = resize keeping the picture's shape (the opposite
/// corner or edge stays put), Shift + drag = stretch freely, Ctrl + Shift + drag = crop. While moving, Shift turns
/// snapping off. Locked sources can be selected but not moved, resized, cropped or nudged.
/// </summary>
public sealed class PreviewEditor
{
    private readonly Canvas _layer;
    private readonly FrameworkElement _viewbox;
    private readonly CanvasKind _kind;
    private StudioSource? _drag;
    private Grip _grip;
    private enum EditMode { Move, Resize, Stretch, Crop }
    private EditMode _mode;
    private (int L, int T, int R, int B) _startCrop;
    private (int W, int H)? _sourcePixels;
    private Point _grabOffset;
    private LayoutRect _startRect;
    private bool _changedSize;
    private bool _moved;

    public Func<StudioScene?> Scene { get; init; } = () => null;
    public Func<(int W, int H)> CanvasSize { get; init; } = () => (1920, 1080);
    public Func<StudioSource, double> Aspect { get; init; } = LayoutMath.EstimateAspect;
    public Func<StudioSource, (int W, int H)?> NaturalSize { get; init; } = _ => null;
    /// <summary>The source's full picture size before cropping (null until the preview has run once).</summary>
    public Func<StudioSource, (int W, int H)?> SourcePixels { get; init; } = _ => null;
    /// <summary>A short line for the status bar (crop amounts while cropping, why cropping isn't possible yet).</summary>
    public event Action<string>? Hint;

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

    private StudioSource? HitTestUnlocked(Point p, IReadOnlyList<StudioSource> layers)
    {
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            if (layers[i].Locked) continue;
            var r = RectOf(layers[i], layers);
            if (p.X >= r.X && p.X <= r.Right && p.Y >= r.Y && p.Y <= r.Bottom) return layers[i];
        }
        return null;
    }

    /// <summary>The selected (unlocked) source's resize grip under the mouse, if any.</summary>
    private Grip GripUnder(Point p, IReadOnlyList<StudioSource> layers)
    {
        if (Selected is null || Selected.Locked || !layers.Contains(Selected)) return Grip.None;
        return LayoutMath.GripAt(RectOf(Selected, layers), p.X, p.Y, 8 / Scale);
    }

    private static Cursor CursorFor(Grip g) => g switch
    {
        Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
        Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
        Grip.Left or Grip.Right => Cursors.SizeWE,
        Grip.Top or Grip.Bottom => Cursors.SizeNS,
        Grip.Move => Cursors.SizeAll,
        _ => Cursors.Arrow,
    };

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_layer);
        _layer.Focus();
        var layers = Layers();
        // the selected source's resize grips win even where another source overlaps them
        var grip = GripUnder(p, layers);
        if (grip != Grip.None)
        {
            BeginDrag(Selected!, p, layers, grip);
            e.Handled = true;
            return;
        }
        // locked sources are see-through for dragging: grab the unlocked source under them, else just select
        var hit = HitTestUnlocked(p, layers) ?? HitTest(p, layers);
        if (!ReferenceEquals(hit, Selected))
        {
            Selected = hit;
            SelectionChanged?.Invoke(hit);
        }
        if (hit is { Locked: false })
        {
            BeginDrag(hit, p, layers, Grip.Move);
            e.Handled = true;
        }
        Redraw();
    }

    private void BeginDrag(StudioSource s, Point p, IReadOnlyList<StudioSource> layers, Grip grip)
    {
        _startRect = RectOf(s, layers);
        _drag = s;
        _grip = grip;
        _startCrop = (Math.Max(0, s.CropLeft), Math.Max(0, s.CropTop), Math.Max(0, s.CropRight), Math.Max(0, s.CropBottom));
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
            s.H = 0;
            s.Anchor = Anchors.Free;
        }
        else
        {
            if (!s.VAuto) return;
            s.VX = _startRect.X / cw;
            s.VY = _startRect.Y / ch;
            s.VW = _startRect.W / cw;
            s.VH = 0;
            s.VAuto = false;
        }
    }

    private bool Stretched(StudioSource s) => _kind == CanvasKind.Horizontal ? s.H > 0 && s.W > 0 : s.VH > 0;

    /// <summary>Writes a rectangle back to the source (free placement); the height only when it's stretched.</summary>
    private void Place(StudioSource s, LayoutRect r, bool stretched)
    {
        var (cw, ch) = CanvasSize();
        if (_kind == CanvasKind.Horizontal) { s.X = r.X / cw; s.Y = r.Y / ch; s.W = r.W / cw; s.H = stretched ? r.H / ch : 0; }
        else { s.VX = r.X / cw; s.VY = r.Y / ch; s.VW = r.W / cw; s.VH = stretched ? r.H / ch : 0; }
    }

    private EditMode ModeNow()
    {
        if (_grip == Grip.Move) return EditMode.Move;
        var m = Keyboard.Modifiers;
        bool shift = (m & ModifierKeys.Shift) != 0, ctrl = (m & ModifierKeys.Control) != 0;
        return shift && ctrl ? EditMode.Crop : shift ? EditMode.Stretch : EditMode.Resize;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_layer);
        if (_drag is null)
        {
            // cursor feedback
            var layers = Layers();
            var g = GripUnder(p, layers);
            _layer.Cursor = CursorFor(g != Grip.None ? g : HitTestUnlocked(p, layers) is not null ? Grip.Move : Grip.None);
            return;
        }
        var (cw, ch) = CanvasSize();
        if (!_moved)
        {
            // ignore tiny jitter on click
            double dx = p.X - (_startRect.X + _grabOffset.X), dy = p.Y - (_startRect.Y + _grabOffset.Y);
            if (Math.Abs(dx) * Scale < 3 && Math.Abs(dy) * Scale < 3) return;
            _moved = true;
            _mode = ModeNow(); // decided once per drag, so a key let go halfway doesn't mix crop and resize
            if (_mode == EditMode.Crop)
            {
                _sourcePixels = SourcePixels(_drag);
                if (_sourcePixels is null)
                {
                    Hint?.Invoke("Start the preview once so Leapcast knows this source's size, then Ctrl + Shift + drag to crop.");
                    _mode = EditMode.Resize;
                }
            }
            // a full-screen source stays full-screen while it's cropped (the rest of the picture refills the canvas)
            if (!(_mode == EditMode.Crop && _kind == CanvasKind.Horizontal && Anchors.Resolve(_drag) == Anchors.Fill)) MakeFree(_drag);
        }
        bool snap = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
        if (_mode == EditMode.Crop)
        {
            var (crop, r) = LayoutMath.CropDrag(_startRect, _grip, p.X, p.Y, _startCrop, _sourcePixels!.Value);
            (_drag.CropLeft, _drag.CropTop, _drag.CropRight, _drag.CropBottom) = crop;
            bool fill = _kind == CanvasKind.Horizontal && Anchors.Resolve(_drag) == Anchors.Fill;
            if (!fill) Place(_drag, r, Stretched(_drag));
            Hint?.Invoke($"Cropping {_drag.Name}: left {crop.L}, top {crop.T}, right {crop.R}, bottom {crop.B} px (applies when you let go).");
            _changedSize = true;
        }
        else if (_mode != EditMode.Move)
        {
            // Shift: stretch freely. Plain drag: keep the shape (a source stretched earlier keeps its new shape)
            bool stretch = _mode == EditMode.Stretch;
            var r = LayoutMath.Resize(_startRect, _grip, p.X, p.Y, cw, ch, 12 / Scale, keepShape: !stretch);
            Place(_drag, r, stretch || Stretched(_drag));
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
        if (s is null || s.Locked || !Layers().Contains(s)) return false;
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
    private static readonly Brush LockedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x78));
    private static readonly Grip[] Handles =
        { Grip.TopLeft, Grip.Top, Grip.TopRight, Grip.Right, Grip.BottomRight, Grip.Bottom, Grip.BottomLeft, Grip.Left };

    /// <summary>Outlines for every source; the selected one gets a highlighted frame and eight resize handles
    /// (or a lock badge when it's locked).</summary>
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
                Stroke = sel ? (s.Locked ? LockedBrush : Accent) : Faint, StrokeThickness = sel ? thick : thin,
                StrokeDashArray = sel ? null : new DoubleCollection { 4, 3 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, r.X); Canvas.SetTop(box, r.Y);
            _layer.Children.Add(box);
            if (!sel) continue;
            if (!s.Locked)
            {
                double hs = 10 / scale;
                foreach (var g in Handles)
                {
                    double hx = g switch { Grip.TopLeft or Grip.Left or Grip.BottomLeft => r.X, Grip.Top or Grip.Bottom => r.X + r.W / 2, _ => r.Right };
                    double hy = g switch { Grip.TopLeft or Grip.Top or Grip.TopRight => r.Y, Grip.Left or Grip.Right => r.Y + r.H / 2, _ => r.Bottom };
                    var handle = new Rectangle { Width = hs, Height = hs, Fill = Brushes.White, Stroke = Accent, StrokeThickness = thin * 1.5, IsHitTestVisible = false };
                    Canvas.SetLeft(handle, hx - hs / 2); Canvas.SetTop(handle, hy - hs / 2);
                    _layer.Children.Add(handle);
                }
            }
            var label = new Border
            {
                Background = LabelBg, CornerRadius = new CornerRadius(3 / scale), Padding = new Thickness(5 / scale, 2 / scale, 5 / scale, 2 / scale),
                Child = new TextBlock { Text = s.Locked ? "🔒 " + s.Name + " (locked)" : s.Name, Foreground = Brushes.White, FontSize = 12 / scale },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, Math.Max(0, r.X));
            Canvas.SetTop(label, Math.Max(0, r.Y - 20 / scale));
            _layer.Children.Add(label);
        }
    }
}
