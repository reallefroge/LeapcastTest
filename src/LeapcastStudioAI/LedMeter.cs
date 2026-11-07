using System.Windows;
using System.Windows.Media;

namespace LeapcastStudioAI;

/// <summary>
/// A vertical LED-style level meter: segments light from the bottom (green, then amber above -20 dBFS, red above
/// -9 dBFS), unlit segments glow faintly, and a bright line holds the recent peak.
/// <see cref="Level"/> and <see cref="Peak"/> are dB above -60 (0 = silence, 60 = 0 dBFS).
/// </summary>
public sealed class LedMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(LedMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(nameof(Peak), typeof(double), typeof(LedMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Peak { get => (double)GetValue(PeakProperty); set => SetValue(PeakProperty, value); }

    private const int Segments = 30; // 2 dB each

    private static readonly Brush Green = Make(0x3D, 0xF5, 0xA6), Amber = Make(0xFF, 0xC9, 0x58), Red = Make(0xFF, 0x4D, 0x6A),
        GreenDim = Make(0x3D, 0xF5, 0xA6, 0x22), AmberDim = Make(0xFF, 0xC9, 0x58, 0x22), RedDim = Make(0xFF, 0x4D, 0x6A, 0x26),
        Track = Make(0x05, 0x07, 0x0D, 0xE6), PeakBrush = Make(0xFF, 0xFF, 0xFF, 0xF0);

    private static SolidColorBrush Make(byte r, byte g, byte b, byte a = 0xFF)
    {
        var x = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        x.Freeze();
        return x;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), 3, 3);
        double pad = 2, gap = 1.5;
        double seg = (h - pad * 2 - gap * (Segments - 1)) / Segments;
        if (seg < 1) return;
        double level = Math.Clamp(Level, 0, 60), peak = Math.Clamp(Peak, 0, 60);
        for (int i = 0; i < Segments; i++)
        {
            double top = h - pad - (i + 1) * seg - i * gap;
            double dbAt = i * 2.0;                 // this segment's bottom, in dB above -60
            bool lit = level > dbAt + 0.5;
            Brush b = dbAt >= 51 ? (lit ? Red : RedDim) : dbAt >= 40 ? (lit ? Amber : AmberDim) : (lit ? Green : GreenDim);
            dc.DrawRectangle(b, null, new Rect(pad, top, w - pad * 2, seg));
        }
        if (peak > 1)
        {
            double y = h - pad - (h - pad * 2) * peak / 60.0;
            dc.DrawRectangle(PeakBrush, null, new Rect(pad, Math.Max(pad, y - 1), w - pad * 2, 2));
        }
    }
}
