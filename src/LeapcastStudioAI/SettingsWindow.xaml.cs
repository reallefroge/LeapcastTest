using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace LeapcastStudioAI;

public partial class SettingsWindow : Window
{
    private readonly StreamEngine _engine;
    private readonly bool _loaded;
    private CancellationTokenSource? _testCts;

    public StudioSettings Result { get; private set; }

    public SettingsWindow(StudioSettings current, string streamKey, StreamEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        Result = current.Clone();

        foreach (var p in Platforms.All) PlatformBox.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Name });
        SelectTag(PlatformBox, Platforms.Get(current.Platform).Name);
        ServerBox.Text = current.ServerUrl;
        KeyBox.Password = streamKey;
        UploadBox.Text = current.UploadMbps > 0 ? current.UploadMbps.ToString("0.#", CultureInfo.CurrentCulture) : "";
        if (current.UploadMeasuredUtc is { } when)
            TestResult.Text = $"Last measured {when.ToLocalTime():g}.";

        SelectTag(ContentBox, current.ContentType);
        SelectTag(ProfileBox, current.QualityProfile);
        ResolutionBox.Text = current.Resolution;
        SelectTag(FrameRateBox, current.FrameRate >= 45 ? "60" : "30");
        BitrateBox.Text = current.MaxBitrateKbps.ToString(CultureInfo.InvariantCulture);

        EncoderBox.Items.Add(new ComboBoxItem { Content = "Automatic (recommended)", Tag = "Auto" });
        foreach (var e in engine.Encoders)
            EncoderBox.Items.Add(new ComboBoxItem { Content = EncoderCatalog.DisplayName(e.Name) + (e.Tier == EncoderTier.Basic ? " (basic mode)" : ""), Tag = e.Name });
        if (!engine.EncodersProbed)
            EncoderBox.Items.Add(new ComboBoxItem { Content = "(still detecting encoders…)", Tag = "Auto", IsEnabled = false });
        SelectTag(EncoderBox, current.Encoder);

        SelectTag(RateBox, current.RateControl);
        SelectTag(AudioBox, current.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture));
        LowLatencyBox.IsChecked = current.LowLatencyMode;

        SelectTag(TransitionBox, current.Transition);
        SelectTag(TransitionMsBox, current.TransitionMs.ToString(CultureInfo.InvariantCulture));
        FolderBox.Text = current.RecordingFolder;
        SelectTag(ReplayBox, current.ReplaySeconds.ToString(CultureInfo.InvariantCulture));
        ReconnectBox.IsChecked = current.AutoReconnect;
        AdjustBox.IsChecked = current.AutoAdjustBitrate;
        FfmpegBox.Text = current.FfmpegPath;

        _loaded = true;
        UpdatePlatformUi(setServer: false);
        UpdatePlan();
        Closed += (_, _) => _testCts?.Cancel();
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string ?? item.Content as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0 && box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    /// <summary>Reads the form into a settings object (without touching Result until Save).</summary>
    private StudioSettings Collect()
    {
        var s = Result.Clone();
        s.Platform = TagOf(PlatformBox, "Custom");
        s.ServerUrl = ServerBox.Text.Trim();
        s.UploadMbps = double.TryParse(UploadBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var up) ? Math.Max(0, up) : 0;
        s.ContentType = TagOf(ContentBox, "LowMotion");
        s.QualityProfile = TagOf(ProfileBox, "Auto");
        s.Resolution = string.IsNullOrWhiteSpace(ResolutionBox.Text) ? "1920 × 1080" : ResolutionBox.Text.Trim();
        s.FrameRate = int.Parse(TagOf(FrameRateBox, "30"), CultureInfo.InvariantCulture);
        s.MaxBitrateKbps = int.TryParse(BitrateBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var br) ? Math.Clamp(br, 500, 50000) : 4500;
        s.Encoder = TagOf(EncoderBox, "Auto");
        s.RateControl = TagOf(RateBox, "SmartVbr");
        s.AudioBitrateKbps = int.Parse(TagOf(AudioBox, "160"), CultureInfo.InvariantCulture);
        s.LowLatencyMode = LowLatencyBox.IsChecked == true;
        s.Transition = TagOf(TransitionBox, "Fade");
        s.TransitionMs = int.Parse(TagOf(TransitionMsBox, "500"), CultureInfo.InvariantCulture);
        s.RecordingFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? new StudioSettings().RecordingFolder : FolderBox.Text.Trim();
        s.ReplaySeconds = int.Parse(TagOf(ReplayBox, "60"), CultureInfo.InvariantCulture);
        s.AutoReconnect = ReconnectBox.IsChecked == true;
        s.AutoAdjustBitrate = AdjustBox.IsChecked == true;
        s.FfmpegPath = FfmpegBox.Text.Trim();
        return s;
    }

    private void AnyChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded) UpdatePlan();
    }

    private void UpdatePlan()
    {
        CustomPanel.Visibility = TagOf(ProfileBox, "Auto") == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        var plan = _engine.PlanFor(Collect());
        PlanPreview.Text = plan.Summary;
        PlanReasons.Text = string.Join(Environment.NewLine, plan.Reasons.Select(r => "• " + r));
    }

    private void PlatformBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        UpdatePlatformUi(setServer: true);
        UpdatePlan();
    }

    private void UpdatePlatformUi(bool setServer)
    {
        var p = Platforms.Get(TagOf(PlatformBox, "Custom"));
        if (setServer && !string.IsNullOrEmpty(p.ServerUrl)) ServerBox.Text = p.ServerUrl;
        if (setServer && string.IsNullOrEmpty(p.ServerUrl) && Platforms.All.Any(x => x.ServerUrl == ServerBox.Text)) ServerBox.Text = "";
        KeyHint.Text = p.KeyHint;
        TestButton.IsEnabled = p.SupportsBandwidthTest;
        if (!p.SupportsBandwidthTest)
            TestResult.Text = "The connection test runs against Twitch's private test mode. For other platforms, type your upload speed below.";
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.FfmpegPath is null) { TestResult.Text = "FFmpeg not found."; return; }
        if (string.IsNullOrWhiteSpace(KeyBox.Password)) { TestResult.Text = "Paste your Twitch stream key first."; return; }
        if (_engine.Live) { TestResult.Text = "End your stream before testing (the test would compete for upload)."; return; }
        TestButton.IsEnabled = false;
        _testCts = new CancellationTokenSource();
        var progress = new Progress<string>(t => TestResult.Text = t);
        try
        {
            TestResult.Text = "Connecting to Twitch (test mode: nothing is shown to viewers)…";
            var r = await BandwidthTester.RunAsync(_engine.FfmpegPath, ServerBox.Text, KeyBox.Password, 9000, 12, progress, _testCts.Token);
            double mbps = r.MeasuredKbps / 1000.0;
            UploadBox.Text = mbps.ToString("0.#", CultureInfo.CurrentCulture);
            Result.UploadMeasuredUtc = DateTime.UtcNow;
            TestResult.Text = r.Detail;
            UpdatePlan();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { TestResult.Text = ex.Message; }
        finally { TestButton.IsEnabled = true; }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where recordings and replays are saved" };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Locate ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|Programs (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) == true) FfmpegBox.Text = dlg.FileName;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(StateStore.Folder, "logs");
        Directory.CreateDirectory(dir);
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); } catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = Collect();
        if (s.QualityProfile == "Custom" && !SmartTuner.TryParseResolution(s.Resolution, out _, out _))
        {
            MessageBox.Show(this, "Enter the resolution as width × height, e.g. 1600 × 900.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        s.StreamKeyProtected = SecretStore.Protect(KeyBox.Password.Trim());
        Result = s;
        _testCts?.Cancel();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();
        DialogResult = false;
    }
}
