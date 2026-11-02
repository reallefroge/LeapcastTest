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

    /// <summary>The Twitch output used for the connection test (Twitch has a private bandwidth-test mode).</summary>
    private readonly StreamOutput? _twitch;

    private readonly IReadOnlyList<StudioScene> _scenes;
    /// <summary>Hotkey boxes: studio actions by id, scenes and sources by object (applied on save).</summary>
    private readonly Dictionary<string, TextBox> _actionKeys = new();
    private readonly List<(StudioScene Scene, TextBox Box)> _sceneKeys = new();
    private readonly List<(StudioSource Source, TextBox Box)> _sourceKeys = new();

    /// <param name="scenes">For the Hotkeys tab (scene switch and source show/hide hotkeys).</param>
    /// <param name="tab">Tab to open on: Stream, Hotkeys, Canvas, Connection, Recording or Advanced.</param>
    public SettingsWindow(StudioSettings current, StreamEngine engine, IReadOnlyList<StudioScene>? scenes = null, string? tab = null)
    {
        InitializeComponent();
        _engine = engine;
        _scenes = scenes ?? Array.Empty<StudioScene>();
        Result = current.Clone();
        BuildHotkeys(current);
        if (tab is not null)
            foreach (var item in Tabs.Items.OfType<TabItem>())
                if (string.Equals(item.Tag as string, tab, StringComparison.OrdinalIgnoreCase)) Tabs.SelectedItem = item;
        _twitch = current.Outputs.FirstOrDefault(o => o.Service == "Twitch" && SecretStore.Unprotect(o.StreamKeyProtected).Length > 0);

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

        SelectTag(PresetBox, current.EncoderPreset);
        SelectTag(RateBox, current.RateControl);
        SelectTag(AudioBox, current.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture));
        LowLatencyBox.IsChecked = current.LowLatencyMode;

        SelectTag(TransitionBox, current.Transition);
        SelectTag(TransitionMsBox, current.TransitionMs.ToString(CultureInfo.InvariantCulture));
        FolderBox.Text = current.RecordingFolder;
        SelectTag(ReplayBox, current.ReplaySeconds.ToString(CultureInfo.InvariantCulture));
        ReconnectBox.IsChecked = current.AutoReconnect;
        AdjustBox.IsChecked = current.AutoAdjustBitrate;
        AdaptBox.IsChecked = current.AdaptivePerformance;
        FfmpegBox.Text = current.FfmpegPath;
        VerticalBox.IsChecked = current.VerticalCanvas;
        SelectTag(VerticalResBox, current.VerticalResolution.Replace(" ", "").Replace("×", "x"));

        TestButton.IsEnabled = _twitch is not null;
        if (_twitch is null && current.UploadMeasuredUtc is null)
            TestResult.Text = "Add a Twitch output with its key to measure your upload (Twitch's private test mode, nothing is shown to viewers).";

        _loaded = true;
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
        s.UploadMbps = double.TryParse(UploadBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var up) ? Math.Max(0, up) : 0;
        s.ContentType = TagOf(ContentBox, "LowMotion");
        s.QualityProfile = TagOf(ProfileBox, "Auto");
        s.Resolution = string.IsNullOrWhiteSpace(ResolutionBox.Text) ? "1920 × 1080" : ResolutionBox.Text.Trim();
        s.FrameRate = int.Parse(TagOf(FrameRateBox, "30"), CultureInfo.InvariantCulture);
        s.MaxBitrateKbps = int.TryParse(BitrateBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var br) ? Math.Clamp(br, 500, 50000) : 4500;
        s.Encoder = TagOf(EncoderBox, "Auto");
        s.RateControl = TagOf(RateBox, "SmartVbr");
        s.EncoderPreset = TagOf(PresetBox, "Balanced");
        s.Hotkeys = _actionKeys.Where(kv => HotkeyText.Normalize(kv.Value.Text).Length > 0)
            .ToDictionary(kv => kv.Key, kv => HotkeyText.Normalize(kv.Value.Text));
        s.AudioBitrateKbps = int.Parse(TagOf(AudioBox, "160"), CultureInfo.InvariantCulture);
        s.LowLatencyMode = LowLatencyBox.IsChecked == true;
        s.Transition = TagOf(TransitionBox, "Fade");
        s.TransitionMs = int.Parse(TagOf(TransitionMsBox, "500"), CultureInfo.InvariantCulture);
        s.RecordingFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? new StudioSettings().RecordingFolder : FolderBox.Text.Trim();
        s.ReplaySeconds = int.Parse(TagOf(ReplayBox, "60"), CultureInfo.InvariantCulture);
        s.AutoReconnect = ReconnectBox.IsChecked == true;
        s.AutoAdjustBitrate = AdjustBox.IsChecked == true;
        s.AdaptivePerformance = AdaptBox.IsChecked == true;
        s.FfmpegPath = FfmpegBox.Text.Trim();
        s.VerticalCanvas = VerticalBox.IsChecked == true;
        s.VerticalResolution = TagOf(VerticalResBox, "720x1280");
        return s;
    }

    private void AnyChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded) UpdatePlan();
    }

    private void UpdatePlan()
    {
        CustomPanel.Visibility = TagOf(ProfileBox, "Auto") == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        var plans = _engine.PlansFor(Collect());
        PlanPreview.Text = plans.Main.Summary + (plans.Vertical is not null && VerticalBox.IsChecked == true ? $"   +   vertical {plans.Vertical.Width}×{plans.Vertical.Height} ≤ {plans.Vertical.CapKbps} kbps" : "");
        PlanReasons.Text = string.Join(Environment.NewLine, plans.Main.Reasons.Select(r => "• " + r));
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.FfmpegPath is null) { TestResult.Text = "FFmpeg not found."; return; }
        if (_twitch is null) { TestResult.Text = "Add a Twitch output with its stream key first."; return; }
        if (_engine.Live) { TestResult.Text = "End your stream before testing (the test would compete for upload)."; return; }
        TestButton.IsEnabled = false;
        _testCts = new CancellationTokenSource();
        var progress = new Progress<string>(t => TestResult.Text = t);
        try
        {
            TestResult.Text = "Connecting to Twitch (test mode: nothing is shown to viewers)…";
            var r = await BandwidthTester.RunAsync(_engine.FfmpegPath, _twitch.ServerUrl, SecretStore.Unprotect(_twitch.StreamKeyProtected), 9000, 12, progress, _testCts.Token);
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

    // ---------------------------------------------------------------- hotkeys tab

    private void BuildHotkeys(StudioSettings current)
    {
        void Header(string text) => HotkeysPanel.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("SectionHeader"), Margin = new Thickness(3, 12, 3, 4) });
        void Row(string label, TextBox box)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(3, 0, 8, 0) };
            Grid.SetColumn(box, 1);
            g.Children.Add(t); g.Children.Add(box);
            HotkeysPanel.Children.Add(g);
        }
        Header("STUDIO");
        foreach (var (id, label) in HotkeyActions.All)
        {
            var box = HotkeyCapture.Box(current.Hotkeys.TryGetValue(id, out var hk) ? hk : "");
            _actionKeys[id] = box;
            Row(label, box);
        }
        if (_scenes.Count == 0) return;
        Header("SWITCH TO SCENE");
        foreach (var sc in _scenes)
        {
            var box = HotkeyCapture.Box(sc.Hotkey);
            _sceneKeys.Add((sc, box));
            Row(sc.Name, box);
        }
        foreach (var sc in _scenes)
        {
            var sources = sc.Sources.Where(s => SourceTypes.KindOf(s.Type) != SourceKind.Unsupported).ToList();
            if (sources.Count == 0) continue;
            Header("SHOW / HIDE IN \"" + sc.Name.ToUpperInvariant() + "\"");
            foreach (var s in sources)
            {
                var box = HotkeyCapture.Box(s.Hotkey);
                _sourceKeys.Add((s, box));
                Row($"{s.Name}  ·  {s.Type}", box);
            }
        }
    }

    /// <summary>Writes the scene and source hotkeys back (call after the dialog returned true).</summary>
    public void ApplySceneHotkeys()
    {
        foreach (var (sc, box) in _sceneKeys) sc.Hotkey = HotkeyText.Normalize(box.Text);
        foreach (var (s, box) in _sourceKeys) s.Hotkey = HotkeyText.Normalize(box.Text);
    }

    /// <summary>Combinations used twice in the form (copies of one source in several scenes may share one).</summary>
    private List<string> HotkeyConflicts(StudioSettings s)
    {
        var probe = s.Clone();
        var scenes = _scenes.Select(sc => new StudioScene
        {
            Id = sc.Id, Name = sc.Name, Hotkey = _sceneKeys.FirstOrDefault(x => ReferenceEquals(x.Scene, sc)).Box?.Text ?? sc.Hotkey,
            Sources = new System.Collections.ObjectModel.ObservableCollection<StudioSource>(sc.Sources.Select(src => new StudioSource
            {
                Name = src.Name, Type = src.Type, Hotkey = _sourceKeys.FirstOrDefault(x => ReferenceEquals(x.Source, src)).Box?.Text ?? src.Hotkey,
            })),
        }).ToList();
        return HotkeyActions.Conflicts(probe, scenes);
    }

    private void OpenRecordings_Click(object sender, RoutedEventArgs e)
    {
        var dir = string.IsNullOrWhiteSpace(FolderBox.Text) ? new StudioSettings().RecordingFolder : FolderBox.Text.Trim();
        try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); } catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = Collect();
        if (HotkeyConflicts(s) is { Count: > 0 } doubles)
        {
            Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().First(t => (t.Tag as string) == "Hotkeys");
            MessageBox.Show(this, $"{string.Join(", ", doubles)} is used for more than one thing. Give each its own combination.", "Hotkeys",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (s.QualityProfile == "Custom" && !SmartTuner.TryParseResolution(s.Resolution, out _, out _))
        {
            MessageBox.Show(this, "Enter the resolution as width × height, e.g. 1600 × 900.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
