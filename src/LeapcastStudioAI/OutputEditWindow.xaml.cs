using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace LeapcastStudioAI;

/// <summary>Add / edit one multistream destination.</summary>
public partial class OutputEditWindow : Window
{
    private readonly bool _loaded;
    private readonly StreamOutput _original;

    public StreamOutput Result { get; private set; }
    /// <summary>The user chose "Delete output".</summary>
    public bool Deleted { get; private set; }

    public OutputEditWindow(StreamOutput? existing, string savedKey)
    {
        InitializeComponent();
        bool isNew = existing is null;
        _original = existing ?? new StreamOutput { Name = "", Service = "Twitch", ServerUrl = Platforms.Get("Twitch").ServerUrl };
        Result = Clone(_original);
        HeaderText.Text = isNew ? "Add output" : "Edit output";
        Title = isNew ? "Add output" : "Edit output · " + _original.Name;
        DeleteButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;

        foreach (var p in Platforms.All) ServiceBox.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Name });
        SelectTag(ServiceBox, Platforms.Get(_original.Service).Name);
        NameBox.Text = _original.Name;
        ServerBox.Text = _original.ServerUrl;
        KeyBox.Password = savedKey;
        AskKeyBox.IsChecked = _original.AskKeyEachTime;
        SelectTag(CanvasBox, _original.IsVertical ? "Vertical" : "Horizontal");
        SelectTag(EncodeBox, _original.OwnBitrateKbps > 0 ? "Own" : "Shared");
        OwnBitrateBox.Text = (_original.OwnBitrateKbps > 0 ? _original.OwnBitrateKbps : Platforms.Get(_original.Service).MaxKbps).ToString(CultureInfo.InvariantCulture);
        EnabledBox.IsChecked = _original.Enabled;
        _loaded = true;
        UpdateServiceUi(applyDefaults: isNew);
        UpdateOwnUi();
    }

    private static StreamOutput Clone(StreamOutput o) =>
        System.Text.Json.JsonSerializer.Deserialize<StreamOutput>(System.Text.Json.JsonSerializer.Serialize(o))!;

    private static void SelectTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = item; return; }
        if (box.Items.Count > 0 && box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private void ServiceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) UpdateServiceUi(applyDefaults: true);
    }

    private void EncodeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) UpdateOwnUi();
    }

    private void UpdateOwnUi() => OwnPanel.Visibility = TagOf(EncodeBox, "Shared") == "Own" ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Fills in the right server and sensible defaults for the chosen service.</summary>
    private void UpdateServiceUi(bool applyDefaults)
    {
        var p = Platforms.Get(TagOf(ServiceBox, "Custom"));
        KeyHint.Text = p.KeyHint;
        if (!applyDefaults) return;
        bool nameIsDefault = string.IsNullOrWhiteSpace(NameBox.Text) || Platforms.All.Any(x => x.Name == NameBox.Text.Trim());
        if (nameIsDefault) NameBox.Text = p.Name;
        bool serverIsPreset = string.IsNullOrWhiteSpace(ServerBox.Text) || Platforms.All.Any(x => x.ServerUrl.Length > 0 && x.ServerUrl == ServerBox.Text.Trim());
        if (serverIsPreset) ServerBox.Text = p.ServerUrl;
        if (p.Name == "TikTok")
        {
            // TikTok LIVE is vertical, and LIVE Producer issues a new key for (almost) every stream
            SelectTag(CanvasBox, "Vertical");
            AskKeyBox.IsChecked = true;
        }
        else if (Platforms.All.Any(x => x.Name == p.Name && x.Name != "Custom"))
        {
            SelectTag(CanvasBox, "Horizontal");
            AskKeyBox.IsChecked = false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var r = Clone(_original);
        r.Service = TagOf(ServiceBox, "Custom");
        r.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? r.Service : NameBox.Text.Trim();
        r.ServerUrl = ServerBox.Text.Trim();
        r.AskKeyEachTime = AskKeyBox.IsChecked == true;
        r.Canvas = TagOf(CanvasBox, "Horizontal");
        r.Enabled = EnabledBox.IsChecked == true;
        r.OwnBitrateKbps = 0;
        if (TagOf(EncodeBox, "Shared") == "Own")
        {
            if (!int.TryParse(OwnBitrateBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kbps) || kbps < 500 || kbps > 50000)
            {
                MessageBox.Show(this, "Enter a bitrate between 500 and 50000 kbps.", "Output", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            r.OwnBitrateKbps = kbps;
        }
        if (r.ServerUrl.Length > 0 && !r.ServerUrl.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)
            && !r.ServerUrl.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "The server URL must start with rtmp:// or rtmps://.", "Output", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string key = KeyBox.Password.Trim();
        r.StreamKeyProtected = key.Length == 0 ? "" : SecretStore.Protect(key);
        Result = r;
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Delete the output \"{_original.Name}\"?", "Delete output", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Deleted = true;
        DialogResult = true;
    }
}

/// <summary>Asks for a stream key without showing it on screen (it might be visible on stream).</summary>
public static class KeyPrompt
{
    public static string? Ask(Window owner, string outputName, string hint)
    {
        var box = new PasswordBox { MinWidth = 340 };
        var ok = new Button { Content = "Go live", IsDefault = true, MinWidth = 90, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Skip", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = $"Stream key for {outputName}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(3, 0, 3, 6) });
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, FontSize = 11,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MutedBrush"), Margin = new Thickness(3, 6, 3, 0) });
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = outputName, Owner = owner, Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgBrush"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush"),
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => box.Focus();
        return w.ShowDialog() == true && box.Password.Trim().Length > 0 ? box.Password.Trim() : null;
    }
}
