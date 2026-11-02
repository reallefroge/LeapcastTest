using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LeapcastStudioAI;

/// <summary>
/// A borderless full-screen view of a preview (right-click the preview → Fullscreen…), like OBS's fullscreen
/// projector: for a second monitor, a capture card loop, or just a big look at the stream. It shows the very same
/// picture the main window draws (no extra work for the encoder). Esc, double-click or right-click → Exit closes it.
/// </summary>
public sealed class FullscreenPreviewWindow : Window
{
    private readonly Func<BitmapSource?> _picture;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly DispatcherTimer _timer;

    /// <param name="picture">The preview bitmap (it's replaced when the preview size changes, so it's re-read).</param>
    /// <param name="monitor">Monitor bounds in screen pixels; null = the monitor the main window is on.</param>
    public FullscreenPreviewWindow(Func<BitmapSource?> picture, (int X, int Y, int W, int H)? monitor)
    {
        _picture = picture;
        Title = "Leapcast Studio AI · Fullscreen preview";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        ShowInTaskbar = true;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        var hint = new TextBlock
        {
            Text = "Start the preview to see the picture here · Esc to exit", Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA6)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16,
        };
        var grid = new Grid();
        grid.Children.Add(hint);
        grid.Children.Add(_image);
        Content = grid;

        var exit = new MenuItem { Header = "Exit fullscreen" };
        exit.Click += (_, _) => Close();
        ContextMenu = new ContextMenu();
        ContextMenu.Items.Add(exit);

        if (monitor is { } m)
        {
            // place it on that monitor first (screen pixels → WPF units), then maximize there
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (_, _) =>
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                Left = m.X / dpi.DpiScaleX + 20; Top = m.Y / dpi.DpiScaleY + 20;
                WindowState = WindowState.Maximized;
            };
            Left = m.X + 20; Top = m.Y + 20;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Loaded += (_, _) => WindowState = WindowState.Maximized;
        }

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseDoubleClick += (_, _) => Close();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            var p = _picture();
            if (!ReferenceEquals(_image.Source, p)) _image.Source = p;
            hint.Visibility = p is null ? Visibility.Visible : Visibility.Collapsed;
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Cursor = Cursors.None;
        MouseMove += (_, _) => { Cursor = null; _hideCursor.Stop(); _hideCursor.Start(); };
        _hideCursor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hideCursor.Tick += (_, _) => { _hideCursor.Stop(); Cursor = Cursors.None; };
    }

    private readonly DispatcherTimer _hideCursor;
}
