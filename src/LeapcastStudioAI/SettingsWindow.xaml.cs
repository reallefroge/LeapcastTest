using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LeapcastStudioAI;

public partial class SettingsWindow : Window
{
    private readonly StreamEngine _engine;
    private readonly bool _loaded;
    private CancellationTokenSource? _testCts;

    public StudioSettings Result { get; private set; }

    /// <summary>The voice-clip listener was switched on/off: applied and saved at once, not only on OK.</summary>
    public event Action<bool>? VoiceClipToggled;

    /// <summary>The Twitch output used for the connection test (Twitch has a private bandwidth-test mode).</summary>
    private readonly StreamOutput? _twitch;

    private readonly IReadOnlyList<StudioScene> _scenes;
    /// <summary>Hotkey boxes: studio actions by id, scenes and sources by object (applied on save).</summary>
    private readonly Dictionary<string, TextBox> _actionKeys = new();
    private readonly List<(StudioScene Scene, TextBox Box)> _sceneKeys = new();
    private readonly List<(StudioSource Source, TextBox Box)> _sourceKeys = new();

    /// <param name="scenes">For the Hotkeys tab (scene switch and source show/hide hotkeys).</param>
    /// <param name="tab">Tab to open on: Stream, Hotkeys, Canvas, Connection, Recording or Advanced.</param>
    public const string LiveLockText = "Unable to Change while Live";
    /// <summary>Shown under Chat &amp; alerts: the overlay links (set by the main window).</summary>
    public static string? OverlayLinks { get; set; }

    private void UpdateYouTubeSignedIn(StudioSettings s)
    {
        bool signedIn = s.YouTubeRefreshProtected.Length > 0;
        YouTubeSignOutButton.IsEnabled = signedIn;
        YouTubeSignedInText.Text = signedIn ? "Signed in with Google." : "Not signed in yet.";
    }

    private void YouTubeSignOut_Click(object sender, RoutedEventArgs e)
    {
        Result.YouTubeRefreshProtected = "";
        UpdateYouTubeSignedIn(Result);
    }

    /// <summary>
    /// While live, everything that would restart the encoder (and so drop or pause the stream) is greyed out and
    /// can't be clicked; everything else still applies at once, without touching the stream.
    /// </summary>
    /// <summary>The slur filter only works with the delay: say so next to it.</summary>
    private void SafetyChanged(object? sender, RoutedEventArgs? e)
    {
        if (SlurHint is null || SafetyDelayBox is null) return;
        bool on = SafetyDelayBox.IsChecked == true;
        SlurHint.Text = "Racial, ethnic and anti-LGBTQ slurs are built in. Desktop audio is transcribed on this PC as it plays; a filtered word is cut out (silence) before it reaches the stream. "
                        + "Ordinary words that sound similar (bigger, trigger, Niagara) are never cut. No speech recognizer hears every word, so a slur that's mumbled, shouted over or drowned out by game sound can still get through."
                        + (on ? "" : "  ⚠ Turn on the safety delay above for this to work: without it, a word is on stream before it can be heard.");
        SlurHint.Foreground = (Brush)FindResource(on ? "MutedBrush" : "WarnBrush");
    }

    private void LockWhileLive()
    {
        var locked = new FrameworkElement[]
        {
            ContentBox, ProfileBox, ResolutionBox, FrameRateBox, BitrateBox, EncoderBox, PresetBox, RateBox, AudioBox, LowLatencyBox,
            VerticalBox, VerticalResBox, VerticalBitrateBox, UploadBox, TestButton, VerticalClipsBox, InstantSwitchBox,
            SafetyDelayBox, SafetyDelaySecondsBox, FilterMicBox,
        };
        var bannered = new HashSet<Panel>();
        foreach (var c in locked)
        {
            c.IsEnabled = false;
            c.ToolTip = LiveLockText;
            ToolTipService.SetShowOnDisabled(c, true);
            if (c.Parent is not Panel parent) continue;
            int at = parent.Children.IndexOf(c);
            // the field's label goes grey with it
            if (at > 0 && parent.Children[at - 1] is TextBlock label && label.Style == (Style)FindResource("FieldLabel")) { label.Opacity = 0.45; at--; }
            if (!bannered.Add(parent)) continue;
            parent.Children.Insert(at, new TextBlock
            {
                Text = "🔒 " + LiveLockText, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 6, 2, 4),
                Foreground = (Brush)FindResource("WarnBrush"),
            });
        }
    }

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

        UploadBox.Text = current.UploadKbps > 0 ? current.UploadKbps.ToString(CultureInfo.InvariantCulture) : "";
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
        VoiceClipBox.IsChecked = current.VoiceClipEnabled;
        VoiceWordBox.Text = current.VoiceClipWord;
        VoiceSecondsBox.Text = VoiceClips.ClampSeconds(current.VoiceClipSeconds).ToString(CultureInfo.InvariantCulture);
        VoiceFolderBox.Text = current.VoiceClipFolder;
        InstantSwitchBox.IsChecked = current.InstantSwitching;
        PreviewBackgroundBox.IsChecked = current.PreviewLiveInBackground;
        GpuCompositingBox.IsChecked = current.GpuCompositing;
        YouTubePickBox.IsChecked = current.YouTubePickBroadcast;
        YouTubeClientIdBox.Text = current.YouTubeClientId;
        YouTubeSecretBox.Password = SecretStore.Unprotect(current.YouTubeClientSecretProtected);
        UpdateYouTubeSignedIn(current);
        HideFromCaptureBox.IsChecked = current.HideFromCapture;
        VerticalClipsBox.IsChecked = current.VerticalClips;
        ClipCaptionsBox.IsChecked = current.ClipCaptions;
        CaptionSpellingsBox.Text = current.CaptionSpellings ?? "";
        SafetyDelayBox.IsChecked = current.SafetyDelay;
        string sec = current.SafetyDelaySeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        SafetyDelaySecondsBox.SelectedItem = SafetyDelaySecondsBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == sec)
                                            ?? SafetyDelaySecondsBox.Items[0]; // 2 s
        MuteSlursBox.IsChecked = current.MuteSlurs;
        MuteAbleistBox.IsChecked = current.MuteAbleist;
        MuteWordsBox.Text = current.MuteWords ?? "";
        LoudGuardBox.IsChecked = current.LoudGuard;
        MuteCodesBox.IsChecked = current.MuteCodes;
        FilterMicBox.IsChecked = current.FilterMic;
        SafetyChanged(null, null);
        ChatTwitchBox.Text = current.ChatTwitchChannel.Length > 0 ? current.ChatTwitchChannel : current.ChatClipChannel;
        ChatYouTubeBox.Text = current.ChatYouTube;
        StreamlabsTokenBox.Password = SecretStore.Unprotect(current.StreamlabsTokenProtected);
        AlertFollowsBox.IsChecked = current.AlertFollows; AlertSubsBox.IsChecked = current.AlertSubs; AlertGiftsBox.IsChecked = current.AlertGifts;
        AlertRaidsBox.IsChecked = current.AlertRaids; AlertSuperChatsBox.IsChecked = current.AlertSuperChats; AlertDonationsBox.IsChecked = current.AlertDonations;
        AlertBitsBox.IsChecked = current.AlertBits; AlertMinBitsBox.Text = Math.Max(1, current.AlertMinBits).ToString();
        string asec = Math.Round(current.AlertSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        AlertSecondsBox.SelectedItem = AlertSecondsBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == asec) ?? AlertSecondsBox.Items[1];
        AlertSoundBox.IsChecked = current.AlertSound;
        if (OverlayLinks is { } links) OverlayLinksText.Text = links;
        ClipHandleBox.Text = current.ClipHandle;
        DeathClipsBox.IsChecked = current.DeathClips;
        ChatClipsBox.IsChecked = current.ChatClips;
        ChatCommandBox.Text = current.ChatClipCommand;
        SelectTag(ChatWhoBox, current.ChatClipWho.ToString());
        ChatChannelBox.Text = current.ChatClipChannel;
        ChatDelayBox.Text = current.ChatClipDelaySeconds.ToString(CultureInfo.InvariantCulture);
        RecapBox.IsChecked = current.RecapEnabled;
        RecapTitleBox.Text = current.RecapTitle;
        RecapSocialsBox.Text = current.RecapSocials;
        SelectTag(VoiceSensitivityBox, current.VoiceClipSensitivity);
        ReconnectBox.IsChecked = current.AutoReconnect;
        AdjustBox.IsChecked = current.AutoAdjustBitrate;
        AdaptBox.IsChecked = current.AdaptivePerformance;
        FfmpegBox.Text = current.FfmpegPath;
        VerticalBox.IsChecked = current.VerticalCanvas;
        VerticalBitrateBox.Text = current.VerticalBitrateKbps.ToString(CultureInfo.InvariantCulture);
        SelectTag(VerticalResBox, current.VerticalResolution.Replace(" ", "").Replace("×", "x"));

        if (_twitch is null && current.UploadMeasuredUtc is null)
            TestResult.Text = "Measures with an upload test over the internet (add a Twitch output with its key to use Twitch's own test mode).";

        _loaded = true;
        UpdatePlan();
        Closed += (_, _) => _testCts?.Cancel();
        if (engine.Live) LockWhileLive();
    }

    private void VoiceSecondsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool ok = VoiceClips.ParseSeconds(VoiceSecondsBox.Text) is not null;
        VoiceSecondsHint.Text = ok ? "" : $"Enter a whole number from {VoiceClips.MinSeconds} to {VoiceClips.MaxSeconds}.";
        VoiceSecondsHint.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private void VoiceClipBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        bool on = VoiceClipBox.IsChecked == true;
        Result.VoiceClipEnabled = on;
        VoiceClipToggled?.Invoke(on);
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
        s.UploadKbps = int.TryParse(UploadBox.Text.Replace(",", "").Replace(" ", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var up) ? Math.Clamp(up, 0, 10_000_000) : 0;
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
        s.VoiceClipEnabled = VoiceClipBox.IsChecked == true;
        s.VoiceClipWord = string.IsNullOrWhiteSpace(VoiceWordBox.Text) ? "clip" : VoiceWordBox.Text.Trim().Split(' ')[0];
        s.VoiceClipSeconds = VoiceClips.ParseSeconds(VoiceSecondsBox.Text) ?? Result.VoiceClipSeconds;
        s.VoiceClipFolder = string.IsNullOrWhiteSpace(VoiceFolderBox.Text) ? VoiceClips.DefaultFolder : VoiceFolderBox.Text.Trim();
        s.VoiceClipSensitivity = TagOf(VoiceSensitivityBox, "High");
        s.InstantSwitching = InstantSwitchBox.IsChecked == true;
        s.PreviewLiveInBackground = PreviewBackgroundBox.IsChecked == true;
        s.GpuCompositing = GpuCompositingBox.IsChecked == true;
        s.YouTubePickBroadcast = YouTubePickBox.IsChecked == true;
        if (s.YouTubeClientId != YouTubeClientIdBox.Text.Trim()) s.YouTubeRefreshProtected = ""; // a different app: sign in again
        s.YouTubeClientId = YouTubeClientIdBox.Text.Trim();
        s.YouTubeClientSecretProtected = SecretStore.Protect(YouTubeSecretBox.Password.Trim());
        s.HideFromCapture = HideFromCaptureBox.IsChecked == true;
        s.VerticalClips = VerticalClipsBox.IsChecked == true;
        s.ClipCaptions = ClipCaptionsBox.IsChecked == true;
        s.CaptionSpellings = CaptionSpellingsBox.Text.Trim();
        s.SafetyDelay = SafetyDelayBox.IsChecked == true;
        if (SafetyDelaySecondsBox.SelectedItem is ComboBoxItem si &&
            double.TryParse((string)si.Content, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs))
            s.SafetyDelaySeconds = secs;
        s.MuteSlurs = MuteSlursBox.IsChecked == true;
        s.MuteAbleist = MuteAbleistBox.IsChecked == true;
        s.MuteWords = MuteWordsBox.Text.Trim();
        s.LoudGuard = LoudGuardBox.IsChecked == true;
        s.MuteCodes = MuteCodesBox.IsChecked == true;
        s.FilterMic = FilterMicBox.IsChecked == true;
        s.ChatTwitchChannel = ChatTwitchBox.Text.Trim();
        s.ChatYouTube = ChatYouTubeBox.Text.Trim();
        s.StreamlabsTokenProtected = StreamlabsTokenBox.Password.Trim().Length > 0 ? SecretStore.Protect(StreamlabsTokenBox.Password.Trim()) : "";
        s.AlertFollows = AlertFollowsBox.IsChecked == true; s.AlertSubs = AlertSubsBox.IsChecked == true; s.AlertGifts = AlertGiftsBox.IsChecked == true;
        s.AlertRaids = AlertRaidsBox.IsChecked == true; s.AlertSuperChats = AlertSuperChatsBox.IsChecked == true; s.AlertDonations = AlertDonationsBox.IsChecked == true;
        s.AlertBits = AlertBitsBox.IsChecked == true;
        if (int.TryParse(AlertMinBitsBox.Text.Trim(), out int mb)) s.AlertMinBits = Math.Max(1, mb);
        if (AlertSecondsBox.SelectedItem is ComboBoxItem ai && double.TryParse((string)ai.Content, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var asecs)) s.AlertSeconds = asecs;
        s.AlertSound = AlertSoundBox.IsChecked == true;
        s.ClipHandle = ClipHandleBox.Text.Trim();
        s.DeathClips = DeathClipsBox.IsChecked == true;
        s.ChatClips = ChatClipsBox.IsChecked == true;
        s.ChatClipCommand = string.IsNullOrWhiteSpace(ChatCommandBox.Text) ? "!clip" : ChatCommandBox.Text.Trim();
        s.ChatClipWho = Enum.TryParse<ChatClipWho>(TagOf(ChatWhoBox, "Mods"), out var who) ? who : ChatClipWho.Mods;
        s.ChatClipChannel = ChatChannelBox.Text.Trim();
        s.ChatClipDelaySeconds = int.TryParse(ChatDelayBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? Math.Clamp(d, 0, 20) : 4;
        s.RecapEnabled = RecapBox.IsChecked == true;
        s.RecapTitle = string.IsNullOrWhiteSpace(RecapTitleBox.Text) ? "Stream highlights" : RecapTitleBox.Text.Trim();
        s.RecapSocials = RecapSocialsBox.Text.Replace("\r\n", "\n").Trim();
        s.AutoReconnect = ReconnectBox.IsChecked == true;
        s.AutoAdjustBitrate = AdjustBox.IsChecked == true;
        s.AdaptivePerformance = AdaptBox.IsChecked == true;
        s.FfmpegPath = FfmpegBox.Text.Trim();
        s.VerticalCanvas = VerticalBox.IsChecked == true;
        s.VerticalBitrateKbps = int.TryParse(VerticalBitrateBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vbr) && vbr > 0 ? Math.Clamp(vbr, 500, 20000) : 0;
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
        if (_engine.Live) { TestResult.Text = "End your stream before testing (the test would compete for upload)."; return; }
        TestButton.IsEnabled = false;
        _testCts = new CancellationTokenSource();
        var progress = new Progress<string>(t => TestResult.Text = t);
        try
        {
            var r = await UploadScan.RunAsync(Result, _engine.FfmpegPath, progress, _testCts.Token);
            UploadBox.Text = ((int)Math.Round(r.MeasuredKbps)).ToString(CultureInfo.InvariantCulture);
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

    private void BrowseClipFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where voice clips are saved" };
        if (Directory.Exists(VoiceFolderBox.Text)) dlg.InitialDirectory = VoiceFolderBox.Text;
        if (dlg.ShowDialog(this) == true) VoiceFolderBox.Text = dlg.FolderName;
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
        if (VoiceClips.ParseSeconds(VoiceSecondsBox.Text) is null)
        {
            Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().First(t => (t.Tag as string) == "Recording");
            MessageBox.Show(this, $"Clip length must be a whole number from {VoiceClips.MinSeconds} to {VoiceClips.MaxSeconds} seconds.", "Voice clips",
                MessageBoxButton.OK, MessageBoxImage.Information);
            VoiceSecondsBox.Focus();
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
