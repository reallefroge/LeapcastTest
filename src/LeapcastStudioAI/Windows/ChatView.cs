using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LeapcastStudioAI;

/// <summary>
/// Twitch + YouTube chat in one list (names in their colours, badges, emotes), with alerts highlighted. Used by the
/// Chat panel and by the pop-out chat window.
/// </summary>
public sealed class ChatView : DockPanel
{
    private readonly ChatHub _hub;
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private const int Max = 250;

    public event Action? PopOutRequested;
    public event Action? AddAlertsRequested;
    public event Action? AddChatOverlayRequested;
    public event Action? SettingsRequested;
    public double FontScale { get; set; } = 1;

    public ChatView(ChatHub hub, bool docked)
    {
        _hub = hub;
        LastChildFill = true;
        // ---- top: who's connected + actions
        var bar = new DockPanel { Margin = new Thickness(4, 2, 4, 6) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        if (docked) actions.Children.Add(IconButton("", "Pop out (pin it on top of your game)", () => PopOutRequested?.Invoke()));
        actions.Children.Add(MenuButton("", "Test an alert", new (string, Action)[]
        {
            ("Follow / subscriber", () => _hub.Test(AlertKind.Follow)), ("Sub", () => _hub.Test(AlertKind.Sub)), ("Resub", () => _hub.Test(AlertKind.Resub)),
            ("Gifted subs", () => _hub.Test(AlertKind.GiftSub)), ("Bits", () => _hub.Test(AlertKind.Bits)), ("Raid", () => _hub.Test(AlertKind.Raid)),
            ("Super Chat", () => _hub.Test(AlertKind.SuperChat)), ("New member", () => _hub.Test(AlertKind.Member)), ("Donation", () => _hub.Test(AlertKind.Donation)),
        }));
        if (docked)
            actions.Children.Add(MenuButton("", "Add an overlay to the scene", new (string, Action)[]
            {
                ("Add alerts to the scene", () => AddAlertsRequested?.Invoke()),
                ("Add chat to the scene (on stream)", () => AddChatOverlayRequested?.Invoke()),
            }));
        actions.Children.Add(IconButton("", "Chat & alerts settings", () => SettingsRequested?.Invoke()));
        bar.Children.Add(actions);
        bar.Children.Add(_status);
        DockPanel.SetDock(bar, Dock.Top);
        Children.Add(bar);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _list };
        Children.Add(_scroll);

        foreach (var it in hub.Recent().TakeLast(80)) _list.Children.Add(Render(it));
        Loaded += (_, _) => { hub.ItemAdded += OnItem; hub.StateChanged += OnState; OnState(); _scroll.ScrollToEnd(); };
        Unloaded += (_, _) => { hub.ItemAdded -= OnItem; hub.StateChanged -= OnState; };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => OnState();
        Loaded += (_, _) => timer.Start();
        Unloaded += (_, _) => timer.Stop();
    }

    private void OnState() => Dispatcher.BeginInvoke(() =>
    {
        var bits = new List<string>();
        if (_hub.TwitchChannel.Length > 0) bits.Add($"{(_hub.TwitchConnected ? "●" : "○")} Twitch #{_hub.TwitchChannel}");
        if (_hub.YouTubeInput.Length > 0) bits.Add($"{(_hub.YouTubeState == "Live" ? "●" : "○")} YouTube {(_hub.YouTubeState == "Live" ? "live" : _hub.YouTubeState == "Waiting" ? "(not live)" : _hub.YouTubeState.StartsWith("Error") ? "(can't reach)" : "")}".TrimEnd());
        if (_hub.StreamlabsOn) bits.Add($"{(_hub.StreamlabsConnected ? "●" : "○")} Streamlabs");
        _status.Text = bits.Count == 0 ? "Add your Twitch / YouTube channel in Settings → Chat & alerts" : string.Join("   ", bits);
        _status.Foreground = new SolidColorBrush(bits.Count == 0 ? Color.FromRgb(0xF5, 0xC2, 0x6B) : Color.FromRgb(0x9A, 0xA4, 0xB8));
    });

    private void OnItem(ChatItem it) => Dispatcher.BeginInvoke(() =>
    {
        bool atEnd = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 8;
        _list.Children.Add(Render(it));
        if (_list.Children.Count > Max) _list.Children.RemoveRange(0, _list.Children.Count - Max);
        if (atEnd) _scroll.ScrollToEnd();
    });

    private static readonly Dictionary<string, (string Label, Color Back)> BadgeLook = new()
    {
        ["broadcaster"] = ("HOST", Color.FromRgb(0xE9, 0x19, 0x16)), ["moderator"] = ("MOD", Color.FromRgb(0x00, 0xAD, 0x03)),
        ["vip"] = ("VIP", Color.FromRgb(0xE0, 0x05, 0xB9)), ["subscriber"] = ("SUB", Color.FromRgb(0x64, 0x41, 0xA5)),
        ["member"] = ("MEMBER", Color.FromRgb(0x2B, 0xA6, 0x40)), ["verified"] = ("✓", Color.FromRgb(0x55, 0x55, 0x55)),
    };

    private FrameworkElement Render(ChatItem it)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5 * FontScale, Foreground = Brushes.White, LineHeight = 20 * FontScale };
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 1),
            Fill = new SolidColorBrush(it.Platform == ChatPlatform.Twitch ? Color.FromRgb(0x91, 0x46, 0xFF) : Color.FromRgb(0xFF, 0x2D, 0x55)),
            ToolTip = it.Platform.ToString() };
        tb.Inlines.Add(new InlineUIContainer(dot) { BaselineAlignment = BaselineAlignment.Center });
        foreach (var b in it.Badges)
            if (BadgeLook.TryGetValue(b, out var look))
                tb.Inlines.Add(new InlineUIContainer(new Border
                {
                    Background = new SolidColorBrush(look.Back), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(0, 0, 4, 0),
                    Child = new TextBlock { Text = look.Label, FontSize = 9.5 * FontScale, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
                }) { BaselineAlignment = BaselineAlignment.Center });
        tb.Inlines.Add(new Run(it.User) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(it.NameColor)) });
        tb.Inlines.Add(new Run(": ") { Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB8)) });
        foreach (var p in it.Parts)
        {
            if (p.ImageUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                try
                {
                    var img = new Image { Height = 22 * FontScale, Margin = new Thickness(1, 0, 1, -4), ToolTip = p.Text, Source = new BitmapImage(uri) };
                    tb.Inlines.Add(new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Center });
                    continue;
                }
                catch { }
            }
            tb.Inlines.Add(new Run(p.Text));
        }
        if (it.Alert is null) return new Border { Child = tb, Padding = new Thickness(6, 3, 6, 3) };
        return new Border
        {
            Child = tb, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(2, 3, 2, 3), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xD9, 0x3D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x3D)), BorderThickness = new Thickness(3, 0, 0, 0),
        };
    }

    private static Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, ToolTip = tip, Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(3, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button MenuButton(string glyph, string tip, (string, Action)[] items)
    {
        var b = IconButton(glyph, tip, () => { });
        var menu = new ContextMenu();
        foreach (var (text, act) in items) { var mi = new MenuItem { Header = text }; mi.Click += (_, _) => act(); menu.Items.Add(mi); }
        b.ContextMenu = menu;
        b.Click += (_, _) => { menu.PlacementTarget = b; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
        return b;
    }
}

/// <summary>
/// The pop-out chat: a slim window that can stay pinned on top of the game, with see-through background (the
/// slider) so the game shows behind it. Hidden from display capture with the rest of Leapcast when that's on.
/// </summary>
public sealed class ChatWindow : Window
{
    private readonly StudioSettings _settings;
    private readonly Border _frame;
    private readonly ToggleButton _pin;

    public ChatView View { get; }

    public ChatWindow(ChatHub hub, StudioSettings settings)
    {
        _settings = settings;
        Title = "Leapcast chat";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = true;
        MinWidth = 220; MinHeight = 160;
        Width = settings.ChatPopWidth > 0 ? settings.ChatPopWidth : 360;
        Height = settings.ChatPopHeight > 0 ? settings.ChatPopHeight : 560;
        if (settings.ChatPopLeft is double l && settings.ChatPopTop is double t && OnScreen(l, t)) { Left = l; Top = t; WindowStartupLocation = WindowStartupLocation.Manual; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = settings.ChatPopPinned;

        View = new ChatView(hub, docked: false) { Margin = new Thickness(4, 0, 4, 4) };
        var header = new DockPanel { Margin = new Thickness(8, 4, 4, 2), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var close = new Button { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Padding = new Thickness(8, 4, 8, 4),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, ToolTip = "Close", Cursor = Cursors.Hand };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        _pin = new ToggleButton { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Padding = new Thickness(8, 4, 8, 4),
            IsChecked = Topmost, ToolTip = "Keep on top of everything (your game too)", Cursor = Cursors.Hand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White };
        _pin.Click += (_, _) => { Topmost = _pin.IsChecked == true; _settings.ChatPopPinned = Topmost; Paint(); };
        DockPanel.SetDock(_pin, Dock.Right);
        var opacity = new Slider { Minimum = 0.15, Maximum = 1, Value = settings.ChatPopOpacity is > 0 and <= 1 ? settings.ChatPopOpacity : 0.85, Width = 80,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = "Background see-through", Margin = new Thickness(6, 0, 6, 0), Cursor = Cursors.Arrow };
        opacity.ValueChanged += (_, e) => { _settings.ChatPopOpacity = e.NewValue; Paint(); };
        DockPanel.SetDock(opacity, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(_pin);
        header.Children.Add(opacity);
        header.Children.Add(new TextBlock { Text = "💬 Chat", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(View);
        _frame = new Border { CornerRadius = new CornerRadius(10), Child = root, BorderThickness = new Thickness(1) };
        Content = _frame;
        Paint();
        Closing += (_, _) =>
        {
            _settings.ChatPopLeft = Left; _settings.ChatPopTop = Top; _settings.ChatPopWidth = ActualWidth; _settings.ChatPopHeight = ActualHeight;
        };
    }

    private void Paint()
    {
        byte a = (byte)Math.Round(255 * Math.Clamp(_settings.ChatPopOpacity is > 0 and <= 1 ? _settings.ChatPopOpacity : 0.85, 0.05, 1));
        _frame.Background = new SolidColorBrush(Color.FromArgb(a, 0x0E, 0x12, 0x20));
        _frame.BorderBrush = new SolidColorBrush(Topmost ? Color.FromArgb(0xAA, 0x3D, 0xD6, 0xF5) : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        _pin.Foreground = Topmost ? new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0xF5)) : Brushes.White;
    }

    private static bool OnScreen(double l, double t) =>
        l > SystemParameters.VirtualScreenLeft - 50 && t > SystemParameters.VirtualScreenTop - 20 &&
        l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
}

/// <summary>The alert sound: a short bright chime, made here (nothing to download), played on Windows' output.</summary>
public static class AlertChime
{
    private static byte[]? _wav;

    public static void Play()
    {
        try
        {
            _wav ??= Make();
            var player = new System.Media.SoundPlayer(new MemoryStream(_wav));
            player.Play();
        }
        catch { }
    }

    private static byte[] Make()
    {
        const int rate = 44100;
        double[] notes = { 783.99, 1046.5, 1318.5, 1567.98 }; // G5 C6 E6 G6
        int n = (int)(rate * 1.4);
        var pcm = new short[n];
        for (int k = 0; k < notes.Length; k++)
        {
            int start = (int)(k * 0.09 * rate);
            for (int i = start; i < n; i++)
            {
                double t = (i - start) / (double)rate;
                double env = Math.Exp(-t * 3.2) * Math.Min(1, t * 400);
                double v = Math.Sin(2 * Math.PI * notes[k] * t) + 0.35 * Math.Sin(2 * Math.PI * notes[k] * 2.01 * t) * Math.Exp(-t * 6);
                pcm[i] = (short)Math.Clamp(pcm[i] + v * env * 0.16 * 32767, -32768, 32767);
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(n * 2);
        foreach (var s in pcm) w.Write(s);
        return ms.ToArray();
    }
}
