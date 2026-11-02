using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LeapcastStudioAI;

/// <summary>Edits one source. Changes are written back to the source only when OK is pressed.</summary>
public partial class SourcePropertiesWindow : Window
{
    private readonly StudioSource _source;
    private readonly SourceKind _kind;
    private readonly string? _ffmpeg;
    private readonly Func<StudioSource, Task<SyncAnalyzer.Result?>>? _clapTest;

    /// <param name="clapTest">Runs the camera clap test on the live pipeline (null when the camera isn't in a scene yet).</param>
    public SourcePropertiesWindow(StudioSource source, string? ffmpegPath, Func<StudioSource, Task<SyncAnalyzer.Result?>>? clapTest = null)
    {
        InitializeComponent();
        _source = source;
        _clapTest = clapTest;
        _kind = SourceTypes.KindOf(source.Type);
        _ffmpeg = ffmpegPath;

        HeaderText.Text = source.Name;
        TypeText.Text = source.Type;
        NameBox.Text = source.Name;
        HotkeyBox.Text = HotkeyText.Normalize(source.Hotkey);

        bool video = SourceTypes.IsVideo(_kind);
        Toggle(DisplayGroup, _kind == SourceKind.Display);
        Toggle(WindowGroup, _kind == SourceKind.Window);
        Toggle(CaptureGroup, _kind is SourceKind.Display or SourceKind.Window);
        Toggle(CameraGroup, _kind == SourceKind.Camera);
        Toggle(ImageGroup, _kind == SourceKind.Image);
        Toggle(TextGroup, _kind == SourceKind.Text);
        Toggle(BrowserGroup, _kind == SourceKind.Browser);
        Toggle(FillGroup, _kind is SourceKind.Display or SourceKind.Window or SourceKind.Camera or SourceKind.Image);
        ClapButton.IsEnabled = clapTest is not null;
        if (clapTest is null && _kind == SourceKind.Camera)
            ClapHint.Text = "Add the camera to a scene first, then open its properties again to run the clap test.";
        Toggle(PlacementGroup, video);
        Toggle(AudioGroup, SourceTypes.IsAudio(_kind));
        Toggle(NoiseBox, _kind == SourceKind.Microphone);

        // Display
        var monitors = WindowsPlatform.Monitors();
        for (int i = 0; i < Math.Max(1, monitors.Count); i++)
        {
            string label = i < monitors.Count ? $"Monitor {i + 1}{(i == 0 ? " (primary)" : "")}: {monitors[i].W}×{monitors[i].H}" : $"Monitor {i + 1}";
            MonitorBox.Items.Add(new ComboBoxItem { Content = label, Tag = i.ToString(CultureInfo.InvariantCulture) });
        }
        SelectTag(MonitorBox, Math.Clamp(source.MonitorIndex, 0, MonitorBox.Items.Count - 1).ToString(CultureInfo.InvariantCulture));
        SelectTag(MethodBox, source.CaptureMethod == "GDI" ? "GDI" : "Auto");
        CursorBox.IsChecked = source.CaptureCursor;
        CropL.Text = source.CropLeft.ToString(CultureInfo.InvariantCulture);
        CropT.Text = source.CropTop.ToString(CultureInfo.InvariantCulture);
        CropR.Text = source.CropRight.ToString(CultureInfo.InvariantCulture);
        CropB.Text = source.CropBottom.ToString(CultureInfo.InvariantCulture);

        // Window
        if (_kind == SourceKind.Window) LoadWindows();
        WindowBox.Text = source.WindowTitle;

        // Camera
        CameraBox.Text = source.DeviceName;
        SelectTag(CamResBox, string.IsNullOrWhiteSpace(source.CameraResolution) ? "Default" : source.CameraResolution);
        SelectTag(CamFpsBox, source.CameraFps.ToString(CultureInfo.InvariantCulture));
        DenoiseBox.IsChecked = source.CameraDenoise;
        MirrorBox.IsChecked = source.Mirror;
        SyncOffsetBox.Text = source.SyncOffsetMs.ToString(CultureInfo.InvariantCulture);
        if (_kind == SourceKind.Camera) _ = LoadCamerasAsync();

        // Browser
        UrlBox.Text = source.Url;
        BrowserWBox.Text = source.BrowserWidth.ToString(CultureInfo.InvariantCulture);
        BrowserHBox.Text = source.BrowserHeight.ToString(CultureInfo.InvariantCulture);
        SelectTag(BrowserFpsBox, source.BrowserFps <= 10 ? "10" : source.BrowserFps <= 15 ? "15" : "30");
        CssBox.Text = source.BrowserCss;
        SelectTag(FillBox, source.FillMode);

        // Image / text
        ImageBox.Text = source.FilePath;
        TextContentBox.Text = source.Text;
        FontSizeBox.Text = source.FontSize.ToString("0", CultureInfo.InvariantCulture);
        ColorBox.Text = source.TextColor;

        // Placement
        foreach (var a in Anchors.All)
            AnchorBox.Items.Add(new ComboBoxItem { Content = a == Anchors.Auto ? $"Auto ({Anchors.Resolve(new StudioSource { Type = source.Type })})" : a, Tag = a });
        SelectTag(AnchorBox, string.IsNullOrWhiteSpace(source.Anchor) ? Anchors.Auto : source.Anchor);
        SizeSlider.Value = Math.Clamp(source.SizePercent <= 0 ? 100 : source.SizePercent, 5, 100);
        MarginBox.Text = source.MarginPx.ToString(CultureInfo.InvariantCulture);
        if (_kind == SourceKind.Text) { SizeLabel.Visibility = Visibility.Collapsed; SizeSlider.Visibility = Visibility.Collapsed; SizeValue.Visibility = Visibility.Collapsed; }
        if (_kind == SourceKind.Image) SizeLabel.Text = "Width (% of the stream width; 100 = full width)";
        if (_kind == SourceKind.Browser) SizeLabel.Text = "Width (% of the stream width) for corner positions";

        // Audio
        if (SourceTypes.IsAudio(_kind))
        {
            AudioDeviceBox.Items.Add(new ComboBoxItem { Content = "Windows default device", Tag = "" });
            foreach (var d in WasapiAudioBridge.ListDevices(_kind == SourceKind.Microphone))
                AudioDeviceBox.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
            SelectTag(AudioDeviceBox, source.AudioDeviceId);
        }
        VolumeBox.Text = source.VolumeDb.ToString("0.#", CultureInfo.InvariantCulture);
        NoiseBox.IsChecked = source.NoiseCleanup;
    }

    private static void Toggle(UIElement e, bool on) => e.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    private static void SelectTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = item; return; }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private void LoadWindows()
    {
        string current = WindowBox.Text;
        WindowBox.Items.Clear();
        foreach (var t in WindowsPlatform.WindowTitles()) WindowBox.Items.Add(t);
        WindowBox.Text = string.IsNullOrWhiteSpace(current) ? _source.WindowTitle : current;
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => LoadWindows();

    private async Task LoadCamerasAsync()
    {
        if (_ffmpeg is null) { CameraHint.Text = "FFmpeg not found, so cameras can't be listed. Type the camera name."; return; }
        CameraHint.Text = "Looking for cameras…";
        string current = CameraBox.Text;
        try
        {
            var devices = await DeviceDiscovery.ListDshowAsync(_ffmpeg);
            CameraBox.Items.Clear();
            foreach (var d in devices.Where(d => d.Kind == "video")) CameraBox.Items.Add(d.Name);
            if (string.IsNullOrWhiteSpace(current) && CameraBox.Items.Count > 0) CameraBox.SelectedIndex = 0;
            else CameraBox.Text = current;
            CameraHint.Text = CameraBox.Items.Count == 0
                ? "No cameras found. Is it plugged in, and allowed in Windows Settings → Privacy → Camera?"
                : "Close other apps using the camera (Discord, Zoom, OBS) before going live.";
        }
        catch (Exception ex) { CameraHint.Text = "Couldn't list cameras: " + ex.Message; }
    }

    private void RefreshCameras_Click(object sender, RoutedEventArgs e) => _ = LoadCamerasAsync();

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*" };
        if (dlg.ShowDialog(this) == true) ImageBox.Text = dlg.FileName;
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SizeValue is not null) SizeValue.Text = $"{e.NewValue:0}%";
    }

    private static int Int(string text, int fallback, int min, int max) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : fallback;

    private static double Dbl(string text, double fallback, double min, double max) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : fallback;

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => HotkeyCapture.Handle(HotkeyBox, e);

    private void ClearHotkey_Click(object sender, RoutedEventArgs e) => HotkeyBox.Text = "";

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_kind == SourceKind.Camera && string.IsNullOrWhiteSpace(CameraBox.Text))
        {
            MessageBox.Show(this, "Choose a camera.", "Source properties", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_kind == SourceKind.Image && !File.Exists(ImageBox.Text.Trim()))
        {
            MessageBox.Show(this, "Choose an image file that exists.", "Source properties", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_kind == SourceKind.Text)
        {
            try { _ = (Color)ColorConverter.ConvertFromString(ColorBox.Text.Trim()); }
            catch
            {
                MessageBox.Show(this, "Colour must look like #FFFFFF.", "Source properties", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        if (_kind == SourceKind.Browser && string.IsNullOrWhiteSpace(UrlBox.Text))
        {
            MessageBox.Show(this, "Enter the web address to show.", "Source properties", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var s = _source;
        s.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? s.Type : NameBox.Text.Trim();
        s.Hotkey = HotkeyText.Normalize(HotkeyBox.Text);
        s.MonitorIndex = Int(TagOf(MonitorBox, "0"), 0, 0, 16);
        s.CaptureMethod = TagOf(MethodBox, "Auto");
        s.CaptureCursor = CursorBox.IsChecked == true;
        s.CropLeft = Int(CropL.Text, 0, 0, 8000);
        s.CropTop = Int(CropT.Text, 0, 0, 8000);
        s.CropRight = Int(CropR.Text, 0, 0, 8000);
        s.CropBottom = Int(CropB.Text, 0, 0, 8000);
        s.WindowTitle = WindowBox.Text.Trim();
        s.DeviceName = CameraBox.Text.Trim();
        s.CameraResolution = TagOf(CamResBox, "Default");
        s.CameraFps = Int(TagOf(CamFpsBox, "0"), 0, 0, 120);
        s.CameraDenoise = DenoiseBox.IsChecked == true;
        s.Mirror = MirrorBox.IsChecked == true;
        s.SyncOffsetMs = Int(SyncOffsetBox.Text, 0, -1000, 2000);
        s.FilePath = ImageBox.Text.Trim();
        s.Text = TextContentBox.Text;
        s.FontSize = Dbl(FontSizeBox.Text, 44, 8, 300);
        s.TextColor = string.IsNullOrWhiteSpace(ColorBox.Text) ? "#FFFFFF" : ColorBox.Text.Trim();
        s.Anchor = TagOf(AnchorBox, Anchors.Auto);
        s.SizePercent = SizeSlider.Value;
        s.MarginPx = Int(MarginBox.Text, 24, 0, 2000);
        s.AudioDeviceId = TagOf(AudioDeviceBox, "");
        s.VolumeDb = Dbl(VolumeBox.Text, 0, -40, 20);
        s.NoiseCleanup = NoiseBox.IsChecked == true;
        s.Url = UrlBox.Text.Trim();
        s.BrowserWidth = Int(BrowserWBox.Text, 400, 64, 3840);
        s.BrowserHeight = Int(BrowserHBox.Text, 600, 64, 2160);
        s.BrowserFps = Int(TagOf(BrowserFpsBox, "15"), 15, 1, 60);
        s.BrowserCss = CssBox.Text;
        s.FillMode = TagOf(FillBox, Anchors.FillCrop);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void ClapTest_Click(object sender, RoutedEventArgs e)
    {
        if (_clapTest is null) return;
        ClapButton.IsEnabled = false;
        try
        {
            ClapHint.Text = "Listening… clap 4–5 times, about once a second, where the camera can see your hands.";
            var r = await _clapTest(_source);
            int current = Int(SyncOffsetBox.Text, 0, -1000, 2000);
            if (r is not { Reliable: true })
            {
                ClapHint.Text = r is null
                    ? "Couldn't hear or see clear claps. Try again: clap loudly, close to the mic, with your hands in view."
                    : $"The claps didn't line up consistently ({r.Events} found). Try again with sharper claps.";
                return;
            }
            int fixedMs = Math.Clamp(current + (int)Math.Round(r.OffsetMs), -1000, 2000);
            SyncOffsetBox.Text = fixedMs.ToString(CultureInfo.InvariantCulture);
            ClapHint.Text = Math.Abs(r.OffsetMs) < 34
                ? $"Already in sync ({r.OffsetMs:+0;-0} ms over {r.Events} claps). Press OK."
                : $"Your camera was {Math.Abs(r.OffsetMs):0} ms {(r.OffsetMs > 0 ? "behind" : "ahead of")} your voice ({r.Events} claps). " +
                  $"Sync offset set to {fixedMs} ms: press OK to apply.";
        }
        catch (Exception ex) { ClapHint.Text = ex.Message; }
        finally { ClapButton.IsEnabled = true; }
    }
}
