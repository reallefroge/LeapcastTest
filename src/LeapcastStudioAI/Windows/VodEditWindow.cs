using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LeapcastStudioAI.Vod;

namespace LeapcastStudioAI;

/// <summary>
/// Tools → "Edit a recording into a video…": pick a recording, a title and a length; the editor does the rest and
/// leaves the video, a thumbnail and a description (with chapters) in the VOD edits folder. Not modal: streaming
/// and everything else keep working while it runs (at low priority).
/// </summary>
public sealed class VodEditWindow : Window
{
    /// <summary>An edit is running (the stream health log notes it).</summary>
    public static volatile bool Busy;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private readonly StreamEngine _engine;
    private readonly StudioSettings _settings;
    private readonly Action _save;
    private readonly ListBox _list = new() { Height = 190 };
    private readonly TextBox _title = new(), _minutes = new() { Width = 60 };
    private readonly CheckBox _captions = new() { Content = "Captions", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _music = new() { Content = "Music", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _chapters = new() { Content = "Chapters", IsChecked = true, ToolTip = "Chapter cards on screen (\"MEETING 1\", \"ROUND 2\"…) and chapter times in the description" };
    private readonly ComboBox _mode = new() { MinWidth = 300, ItemsSource = new[] { "Fully automatic: make the whole video", "Review before rendering: see every cut and the editor's suggestions, then render" } };
    private readonly TextBox _channel = new() { Width = 260 };
    private static readonly string[] MusicSources = { "kit", "none" };
    private readonly ComboBox _musicSource = new() { Width = 260, ItemsSource = new[] { "Wizard101 kit (the game's own music)", "No music" } };
    private readonly CheckBox _hook = new() { Content = "Hook (your best moments first)", Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _endScreen = new() { Content = "End screen (previous video, watch next, subscribe)" };
    private readonly TextBlock _kitText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _learned = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _folder = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Height = 8, Maximum = 1, Margin = new Thickness(0, 12, 0, 6) };
    private readonly TextBlock _step = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _start = new() { Content = "Make the video", MinWidth = 150 };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 80, IsEnabled = false };
    private readonly Button _open = new() { Content = "Open folder", Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _cts;
    private string? _lastVideo;

    public VodEditWindow(StreamEngine engine, StudioSettings settings, Action save)
    {
        _engine = engine; _settings = settings; _save = save;
        Title = "Edit a recording into a video";
        Width = 600; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanMinimize;
        Background = Res("BgBrush"); Foreground = Res("TextBrush");
        _start.Style = (Style)Application.Current.FindResource("PrimaryButton");
        _title.Text = string.IsNullOrWhiteSpace(settings.RecapTitle) ? "Stream highlights" : settings.RecapTitle;
        _minutes.Text = settings.VodEditMinutes.ToString();
        _folder.Text = settings.VodEditFolder;
        _step.Foreground = Res("MutedBrush");

        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = "Turn a long stream into an edited video", FontSize = 16, FontWeight = FontWeights.SemiBold });
        p.Children.Add(new TextBlock { Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10), FontSize = 12,
            Text = "Finds the best moments (your reactions, clips you saved, fights, chat) and edits them into a story: a cold open, chapters, captions, zooms, original music and sound effects, and transitions when you want them (plain cuts otherwise). Among Us: meetings and gameplay, never the lobby, and VAR checks when you explain where you were. Recordings made in Leapcast edit best: what you said and did is logged while recording." });
        p.Children.Add(Label("Recording"));
        p.Children.Add(new Border { Child = _list, Background = Res("FieldBrush"), CornerRadius = new CornerRadius(8), BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1) });
        var browse = new Button { Content = "Another file…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        browse.Click += (_, _) => Browse();
        p.Children.Add(browse);
        p.Children.Add(Label("Video title"));
        p.Children.Add(_title);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(new TextBlock { Text = "Length (minutes)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(_minutes);
        p.Children.Add(row);
        var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        opts.Children.Add(_captions); opts.Children.Add(_music); opts.Children.Add(_chapters);
        _chapters.IsChecked = _settings.VodEditChapters;
        p.Children.Add(opts);
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        modeRow.Children.Add(new TextBlock { Text = "How", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Width = 40 });
        modeRow.Children.Add(_mode);
        _mode.SelectedIndex = _settings.VodEditReviewFirst ? 1 : 0;
        p.Children.Add(modeRow);
        // your channel: the intro and end screen (Wizard101-styled on every video), and the music
        p.Children.Add(Label("Your channel (the intro and end screen, the same on every video)"));
        var chRow = new StackPanel { Orientation = Orientation.Horizontal };
        chRow.Children.Add(new TextBlock { Text = "Channel name", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        chRow.Children.Add(_channel);
        p.Children.Add(chRow);
        var muRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        muRow.Children.Add(new TextBlock { Text = "Music", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        muRow.Children.Add(_musicSource);
        var openKit = new Button { Content = "Open the kit folder", Margin = new Thickness(8, 0, 0, 0) };
        openKit.Click += (_, _) => { VodKit.Ensure(); try { Process.Start(new ProcessStartInfo(VodKit.Folder) { UseShellExecute = true }); } catch { } ShowKit(); };
        muRow.Children.Add(openKit);
        p.Children.Add(muRow);
        p.Children.Add(_kitText);
        var hkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        hkRow.Children.Add(_hook); hkRow.Children.Add(_endScreen);
        p.Children.Add(hkRow);
        _kitText.Foreground = Res("MutedBrush");
        _channel.Text = _settings.VodChannel;
        _musicSource.SelectedIndex = Math.Max(0, Array.IndexOf(MusicSources, _settings.VodMusicSource));
        _hook.IsChecked = _settings.VodHook; _endScreen.IsChecked = _settings.VodEndScreen;
        _musicSource.SelectionChanged += (_, _) => ShowKit();
        VodEditor.BrandProvider = CurrentBrand;
        ShowKit();
        // what the editor has learned from your earlier edits (each new edit starts from it)
        var learnRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var forget = new Button { Content = "Forget what it learned", FontSize = 11, Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 4, 0, 0) };
        forget.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Start fresh? The editor forgets the choices it learned from your edits.", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { File.Delete(StylePath); } catch { }
            ShowLearned();
        };
        DockPanel.SetDock(forget, Dock.Right);
        learnRow.Children.Add(forget);
        learnRow.Children.Add(_learned);
        p.Children.Add(learnRow);
        _learned.Foreground = Res("MutedBrush");
        VodEditor.StylePath = StylePath;
        ShowLearned();
        var folderRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var change = new Button { Content = "Change…", Margin = new Thickness(8, 0, 0, 0) };
        change.Click += (_, _) => ChooseFolder();
        DockPanel.SetDock(change, Dock.Right);
        folderRow.Children.Add(change);
        folderRow.Children.Add(new TextBlock { Text = "Save to: ", VerticalAlignment = VerticalAlignment.Center, Foreground = Res("MutedBrush") });
        folderRow.Children.Add(_folder);
        p.Children.Add(folderRow);
        p.Children.Add(_bar);
        p.Children.Add(_step);
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_open, Dock.Left);
        DockPanel.SetDock(_start, Dock.Right);
        DockPanel.SetDock(_cancel, Dock.Right);
        var reviseOld = new Button { Content = "Revise an earlier edit…", Margin = new Thickness(8, 0, 0, 0) };
        reviseOld.Click += (_, _) => OpenEarlier();
        DockPanel.SetDock(reviseOld, Dock.Left);
        buttons.Children.Add(_open); buttons.Children.Add(reviseOld); buttons.Children.Add(_start); buttons.Children.Add(_cancel);
        p.Children.Add(buttons);
        p.Children.Add(BuildRevisePanel());
        Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height * 0.92 };

        _start.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _open.Click += (_, _) =>
        {
            try
            {
                if (_lastVideo is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastVideo}\"") { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo(_settings.VodEditFolder) { UseShellExecute = true });
            }
            catch { }
        };
        Closing += (_, e) =>
        {
            if (_cts is not null && MessageBox.Show(this, "Stop making the video?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true;
            else _cts?.Cancel();
        };
        LoadRecordings();
    }

    private static string StylePath => Path.Combine(StateStore.Folder, "vod-style.json");

    /// <summary>Your channel's look, from these settings and the kit as it is right now.</summary>
    private VodBrand CurrentBrand() => Dispatcher.Invoke(() => VodKit.Brand(_channel.Text.Trim(), MusicSources[Math.Max(0, _musicSource.SelectedIndex)],
        _hook.IsChecked == true, _endScreen.IsChecked == true));

    private void ShowKit()
    {
        bool kit = _musicSource.SelectedIndex <= 0;
        VodEditor.HarvestKit = kit;
        _kitText.Text = kit
            ? $"Your Wizard101 kit fills itself: each Wizard101 stream you edit adds stretches of the game's music (where nobody talks), and the first sets the intro clip and its music, which then stay the same on every video, Among Us included. Drop your own tracks, logo (PNG) or intro clip in any time. Now: {VodKit.Summary()}."
            : "No music: just the stream's own sound.";
    }

    private void ShowLearned()
    {
        var st = EditStyle.Load(StylePath);
        var lines = new[] { "Among Us", "other" }.Select(g => (g, st.Learned(g).Note, n: st.Count(g))).Where(x => x.n > 0).ToList();
        _learned.Text = lines.Count == 0
            ? "Learns as you go: every video you make or revise teaches it your taste, and new edits start from what you keep choosing."
            : string.Join(Environment.NewLine, lines.Select(x => x.Note.Length > 0 ? "Learned: " + x.Note
                : $"Learning from your {x.n} {(x.g == "other" ? "" : x.g + " ")}edit{(x.n == 1 ? "" : "s")} (a choice you make twice becomes the starting point)."));
    }

    // ------------------------------------------------------------------ review & revise

    private VodProject? _project;
    private readonly Border _revise = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    // feedback: one suggestion per line (Enter applies it and starts the next)
    private sealed class Suggestion
    {
        public required TextBox Box { get; init; }
        public required TextBlock Result { get; init; }
        public required FrameworkElement Row { get; init; }
        public string? Applied { get; set; }
    }
    private readonly List<Suggestion> _suggestions = new();
    private readonly StackPanel _suggestionRows = new();
    private readonly TextBlock _understood = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _rTitle = new(), _rMinutes = new() { Width = 60 };
    private readonly StackPanel _cards = new(), _moments = new();
    private readonly Slider _musicVol = new() { Minimum = 0, Maximum = 2, Width = 180, TickFrequency = 0.05, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _musicVolText = new() { Width = 46, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ComboBox _pace = new() { Width = 120, ItemsSource = new[] { "Tighter", "Normal", "Relaxed" } };
    private readonly ComboBox _zooms = new() { Width = 120, ItemsSource = new[] { "None", "Fewer", "Normal", "More" } };
    private readonly ComboBox _emphasis = new() { Width = 140, ItemsSource = new[] { "More action", "Balanced", "More funny" } };
    private readonly CheckBox _rSlow = new() { Content = "Slow motion", Margin = new Thickness(0, 0, 14, 0) }, _rFace = new() { Content = "Cuts to your face", Margin = new Thickness(0, 0, 14, 0) },
        _rCold = new() { Content = "Cold open", Margin = new Thickness(0, 0, 14, 0) }, _rCaptions = new() { Content = "Captions", Margin = new Thickness(0, 0, 14, 0) },
        _rMusic = new() { Content = "Music", Margin = new Thickness(0, 0, 14, 0) }, _rChapters = new() { Content = "Chapters", Margin = new Thickness(0, 0, 14, 0) },
        _rVar = new() { Content = "VAR checks (Among Us)", Margin = new Thickness(0, 0, 14, 0), ToolTip = "When you say where you were in a meeting (your mic only), a fast replay of your round plays over it" },
        _rTransitionSounds = new() { Content = "Card and title sounds", Margin = new Thickness(0, 0, 14, 0) },
        _rEven = new() { Content = "Even loudness between parts", Margin = new Thickness(0, 0, 14, 0) }, _rShake = new() { Content = "Screen shake", Margin = new Thickness(0, 0, 14, 0) },
        _rLetterbox = new() { Content = "Black bars on the cold open" };
    private readonly Slider _sfxVol = new() { Minimum = 0, Maximum = 2, Width = 180, TickFrequency = 0.05, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sfxVolText = new() { Width = 46, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ComboBox _versions = new() { MinWidth = 260 };
    private static readonly string[] StyleChoices = TransitionKinds.Styles.Append(TransitionKinds.Mixed).ToArray();
    private static readonly string[] SoundChoices = new[] { "auto" }.Concat(TransitionKinds.Sounds).Append(TransitionKinds.Mixed).ToArray();
    private readonly ComboBox _tStyle = new() { MinWidth = 260, ItemsSource = StyleChoices.Select(TransitionKinds.Name).ToArray() };
    private readonly ComboBox _tSound = new() { MinWidth = 260, ItemsSource = SoundChoices.Select(TransitionKinds.SoundName).ToArray() };
    private readonly StackPanel _cutRows = new();
    private readonly TextBlock _reviseHeader = new() { Text = "Revise this video", FontSize = 15, FontWeight = FontWeights.SemiBold };
    private bool _loadingVersions;
    private readonly Button _rerender = new() { Content = "Re-render with changes", MinWidth = 190 };

    // ---- the player: watch what you're giving feedback on
    private readonly MediaElement _player = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Volume = 1, ScrubbingEnabled = true };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
    private readonly TextBlock _time = new() { Width = 110, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _watching = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _playPause = new() { Content = "▶", Width = 38 };
    private readonly Button _previewButton = new() { Content = "Quick preview of the edit", Margin = new Thickness(8, 0, 0, 0) };
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private string? _watchFile;
    private bool _watchIsEdit, _playing, _seeking;
    private double? _stopAt;

    private FrameworkElement BuildPlayer()
    {
        var box = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        box.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(6), Height = 300, Child = _player, ClipToBounds = true });
        var bar = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(_playPause); DockPanel.SetDock(_playPause, Dock.Left);
        bar.Children.Add(_previewButton); DockPanel.SetDock(_previewButton, Dock.Right);
        var note = new Button { Content = "Note at this time", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Adds the time you're at to “What should change?”, then write what to change there" };
        bar.Children.Add(note); DockPanel.SetDock(note, Dock.Right);
        bar.Children.Add(_time); DockPanel.SetDock(_time, Dock.Right);
        _seek.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(_seek);
        box.Children.Add(bar);
        box.Children.Add(_watching);
        _watching.Foreground = Res("MutedBrush"); _time.Foreground = Res("MutedBrush");

        _playPause.Click += (_, _) => { if (_playing) Pause(); else Play(); };
        _player.MediaOpened += (_, _) =>
        {
            _seek.Maximum = _player.NaturalDuration.HasTimeSpan ? Math.Max(1, _player.NaturalDuration.TimeSpan.TotalSeconds) : 1;
            if (!_playing) { _player.Play(); _player.Pause(); } // the first frame shows
        };
        _player.MediaEnded += (_, _) => Pause();
        _player.MediaFailed += (_, e) =>
        {
            _playing = false; _playPause.Content = "▶";
            _watching.Text = $"This file can't play here ({e.ErrorException?.Message}). Press “Quick preview of the edit”: it makes a small version that plays here.";
        };
        _seek.PreviewMouseDown += (_, _) => _seeking = true;
        _seek.PreviewMouseUp += (_, _) => { _seeking = false; _stopAt = null; _player.Position = TimeSpan.FromSeconds(_seek.Value); };
        _tick.Tick += (_, _) =>
        {
            if (_watchFile is null) return;
            double at = _player.Position.TotalSeconds;
            if (!_seeking) _seek.Value = Math.Min(_seek.Maximum, at);
            _time.Text = $"{EditFeedback.Clock(at)} / {EditFeedback.Clock(_seek.Maximum)}";
            if (_stopAt is { } stop && at >= stop) { _stopAt = null; Pause(); }
        };
        _tick.Start();
        Closed += (_, _) => { _tick.Stop(); try { _player.Close(); } catch { } };
        note.Click += (_, _) =>
        {
            string at = EditFeedback.Clock(_player.Position.TotalSeconds) + (_watchIsEdit ? "" : " of the stream");
            string add = $"at {at}: ";
            var row = _suggestions.LastOrDefault(x => x.Box.Text.Trim().Length == 0) ?? AddSuggestion();
            row.Box.Text = add;
            row.Box.Focus(); row.Box.CaretIndex = row.Box.Text.Length;
            Pause();
        };
        _previewButton.Click += async (_, _) => await PreviewAsync();
        return box;
    }

    private void Play() { if (_watchFile is null) return; _player.Play(); _playing = true; _playPause.Content = "❚❚"; }
    private void Pause() { _player.Pause(); _playing = false; _playPause.Content = "▶"; }

    /// <summary>What the player shows: your video (or its quick preview), or the recording before there's either.</summary>
    private void Watch(string file, bool isEdit, string what)
    {
        _stopAt = null;
        if (!string.Equals(_watchFile, file, StringComparison.OrdinalIgnoreCase))
        {
            _playing = false; _playPause.Content = "▶";
            try { _player.Close(); } catch { }
            _watchFile = file; _watchIsEdit = isEdit;
            _player.Source = new Uri(file);
        }
        _watching.Text = what;
    }

    /// <summary>Plays a spot: in the edit when you're watching the edit, else in the recording (just that stretch).</summary>
    private void PlayAt(double videoAt, double sourceAt, double sourceLen)
    {
        if (_watchFile is null) return;
        if (_watchIsEdit) { _stopAt = null; _player.Position = TimeSpan.FromSeconds(Math.Max(0, videoAt)); }
        else { _player.Position = TimeSpan.FromSeconds(Math.Max(0, sourceAt)); _stopAt = sourceAt + sourceLen; }
        Play();
    }

    private Button PlayButton(Action play) => new()
    {
        Content = "▶", Width = 30, Height = 24, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Watch this", Tag = play,
    };

    private Button Watchable(Action play) { var b = PlayButton(play); b.Click += (_, _) => play(); return b; }

    /// <summary>The player's starting point for this project: the video, else the recording.</summary>
    private void WatchProject(VodProject pr)
    {
        if (pr.Video.Length > 0 && File.Exists(pr.Video))
            Watch(pr.Video, true, $"Watching your video ({Path.GetFileName(pr.Video)}). ▶ next to a cut or a moment jumps there; “Note at this time” puts the time in your feedback.");
        else if (File.Exists(pr.Recording))
            Watch(pr.Recording, false, "Watching the recording: ▶ next to a moment plays it, ▶ next to a cut plays the clip after it. To watch the edit itself (cuts, text, music), press “Quick preview of the edit”: a small, fast version made in a fraction of the render's time.");
    }

    private async Task PreviewAsync()
    {
        if (_project is not { } pr) return;
        if (_engine.FfmpegPath is not { } ffmpeg) { _step.Text = "FFmpeg isn't available."; return; }
        ApplyFeedback(); // lines typed but not applied yet
        ReadControls();
        Pause();
        try { _player.Close(); } catch { }
        _watchFile = null;
        _cts = new CancellationTokenSource();
        _start.IsEnabled = false; _rerender.IsEnabled = false; _previewButton.IsEnabled = false; _cancel.IsEnabled = true;
        var started = DateTime.Now;
        try
        {
            var progress = new Progress<VodEditor.Progress>(x => { _bar.Value = x.Fraction; _step.Text = x.Step; });
            VodRenderer.LogPath = Path.Combine(StateStore.Folder, "logs", "vod-edit.log");
            Busy = true;
            string file = await Task.Run(() => VodEditor.PreviewAsync(ffmpeg, pr, progress, _cts.Token)).ContinueWith(t => { Busy = false; return t; }).Unwrap();
            RefreshControls(); // the times now match the preview
            Watch(file, true, $"Watching a quick preview of the edit as it stands (small and fast, captions not final; made in {(DateTime.Now - started):m\\:ss}). ▶ next to a cut or moment jumps there. Change anything, preview again (only what changed is remade), then render.");
            _bar.Value = 0.3;
            _step.Text = "Preview ready: watch it above. " + VodRenderer.LoudnessCheck;
            Play();
        }
        catch (OperationCanceledException) { _step.Text = "Stopped."; _bar.Value = 0; WatchProject(pr); }
        catch (Exception ex) { _step.Text = "Couldn't make the preview: " + ex.Message + " (details in logs\\vod-edit.log)"; VodRenderer.Note("Preview failed: " + ex); WatchProject(pr); }
        finally { _cts = null; _start.IsEnabled = true; _rerender.IsEnabled = true; _previewButton.IsEnabled = true; _cancel.IsEnabled = false; }
    }

    private FrameworkElement BuildRevisePanel()
    {
        _revise.Background = Res("FieldBrush"); _revise.BorderBrush = Res("LineBrush");
        _rerender.Style = (Style)Application.Current.FindResource("PrimaryButton");
        var r = new StackPanel();
        r.Children.Add(_reviseHeader);
        // every version made from this recording can be the starting point (its own changes are loaded)
        var vrow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        vrow.Children.Add(new TextBlock { Text = "Start from", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        vrow.Children.Add(_versions);
        r.Children.Add(vrow);
        _versions.SelectionChanged += (_, _) =>
        {
            if (_loadingVersions || _versions.SelectedItem is not ComboBoxItem { Tag: string video } || _project?.Video == video) return;
            if (VodProject.Load(video) is not { } older) { _step.Text = "Couldn't read that version's edit info."; return; }
            older.Video = video;
            _lastVideo = video;
            _step.Text = "Revising from " + Path.GetFileName(video) + " (with the changes it was made with)";
            ShowRevise(older);
        };
        r.Children.Add(BuildPlayer());
        r.Children.Add(new TextBlock { Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 8),
            Text = "Say what to change, or change it below. The recording isn't watched again and unchanged parts are reused, so a revision is quick." });
        r.Children.Add(Label("What should change? (one suggestion per line: press Enter to apply it and start the next)"));
        r.Children.Add(_suggestionRows);
        r.Children.Add(new TextBlock { Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
            Text = "Write each however you like, e.g. “remove transitions” · “I don't want the transition sounds” · “the volume jumps between clips” · “music quieter by 50%” · “rename part 2 to Khrysalis boss fight” · “remove the part at 3:20” · “add the moment at 1:12:30 of the stream” · “too chaotic” · “make it 12 minutes”" });
        var apply = new Button { Content = "Apply all", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), MinWidth = 90 };
        apply.Click += (_, _) => ApplyFeedback();
        r.Children.Add(apply);
        r.Children.Add(_understood);

        r.Children.Add(Label("Video title"));
        r.Children.Add(_rTitle);
        r.Children.Add(Label("Chapter cards (the big line, then the line under it)"));
        r.Children.Add(_cards);

        var sound = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        sound.Children.Add(new TextBlock { Text = "Music volume", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        sound.Children.Add(_musicVol); sound.Children.Add(_musicVolText);
        _musicVol.ValueChanged += (_, e) => _musicVolText.Text = $"{e.NewValue:P0}";
        r.Children.Add(sound);
        var sfx = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        sfx.Children.Add(new TextBlock { Text = "Sound effects", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        sfx.Children.Add(_sfxVol); sfx.Children.Add(_sfxVolText);
        _sfxVol.ValueChanged += (_, e) => _sfxVolText.Text = $"{e.NewValue:P0}";
        r.Children.Add(sfx);
        r.Children.Add(Row("Transitions", _tStyle));
        r.Children.Add(Row("Their sound", _tSound));
        r.Children.Add(Label("Cuts between clips (the editor's suggestion for each is marked ★; set one to change just that cut)"));
        r.Children.Add(new Border { Child = new ScrollViewer { Content = _cutRows, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6) });
        r.Children.Add(Row("Pace", _pace));
        r.Children.Add(Row("Zooms", _zooms));
        r.Children.Add(Row("Focus on", _emphasis));
        r.Children.Add(Row("Length (minutes)", _rMinutes));
        var toggles = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var c in new[] { _rCold, _rSlow, _rFace, _rCaptions, _rMusic, _rChapters, _rTransitionSounds, _rShake, _rLetterbox, _rEven, _rVar })
        { c.Margin = new Thickness(0, 2, 14, 2); toggles.Children.Add(c); }
        r.Children.Add(toggles);
        r.Children.Add(Label("Moments in the video (untick to leave one out)"));
        r.Children.Add(new Border { Child = new ScrollViewer { Content = _moments, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6) });
        var bar = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_rerender, Dock.Right);
        bar.Children.Add(_rerender);
        r.Children.Add(bar);
        _rerender.Click += async (_, _) => await ReviseAsync();
        _revise.Child = r;
        return _revise;
    }

    private FrameworkElement Row(string label, FrameworkElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    private void ShowRevise(VodProject project, bool review = false)
    {
        _project = project;
        _reviseHeader.Text = review ? "Review the edit before it's rendered" : "Revise this video";
        _rerender.Content = review ? "Render" : "Re-render with changes";
        _revise.Visibility = Visibility.Visible;
        _suggestions.Clear(); _suggestionRows.Children.Clear();
        AddSuggestion();
        _understood.Text = "";
        RefreshVersions();
        RefreshControls();
        WatchProject(project);
    }

    /// <summary>Every version made from the same recording (the original and each revision), newest first.</summary>
    private void RefreshVersions()
    {
        if (_project is not { } pr) return;
        _loadingVersions = true;
        try
        {
            _versions.Items.Clear();
            foreach (var (video, made) in VodProject.Versions(pr))
            {
                var item = new ComboBoxItem { Content = $"{Path.GetFileNameWithoutExtension(video)}  ·  {made:MMM d, h:mm tt}", Tag = video };
                _versions.Items.Add(item);
                if (string.Equals(video, pr.Video, StringComparison.OrdinalIgnoreCase)) _versions.SelectedItem = item;
            }
            _versions.IsEnabled = _versions.Items.Count > 1;
        }
        finally { _loadingVersions = false; }
    }

    /// <summary>The controls show the project's current changes.</summary>
    private void RefreshControls()
    {
        if (_project is not { } pr) return;
        var t = pr.Tweaks;
        _rTitle.Text = t.Title ?? pr.Title;
        _cards.Children.Clear();
        foreach (var ch in pr.Chapters)
        {
            var card = t.Cards.FirstOrDefault(c => c.Chapter == ch.Index);
            var head = new TextBox { Width = 120, Text = card?.Heading ?? ch.Heading, Tag = ("h", ch.Index) };
            var sub = new TextBox { Text = card?.Sub ?? ch.Sub, Tag = ("s", ch.Index), Margin = new Thickness(8, 0, 0, 0) };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = EditFeedback.Clock(ch.At), Width = 46, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("MutedBrush") });
            DockPanel.SetDock(row.Children[0], Dock.Left);
            row.Children.Add(head); DockPanel.SetDock(head, Dock.Left);
            row.Children.Add(sub);
            _cards.Children.Add(row);
        }
        if (pr.Chapters.Count == 0) _cards.Children.Add(new TextBlock { Text = "This video is short enough to have no chapter cards.", Foreground = Res("MutedBrush"), FontSize = 12 });
        _musicVol.Value = t.Music ? t.MusicVolume : 0;
        _musicVolText.Text = $"{_musicVol.Value:P0}";
        _pace.SelectedIndex = t.Pace + 1;
        _zooms.SelectedIndex = Math.Clamp(t.Zooms, 0, 3);
        _emphasis.SelectedIndex = t.Emphasis + 1;
        _rMinutes.Text = ((t.Minutes ?? pr.TargetSeconds / 60)).ToString("0.#");
        _rSlow.IsChecked = t.SlowMotion; _rFace.IsChecked = t.FaceCam; _rCold.IsChecked = t.ColdOpen;
        _rCaptions.IsChecked = t.Captions && pr.Captions; _rMusic.IsChecked = t.Music && pr.Music; _rChapters.IsChecked = t.Chapters && pr.ChapterCards;
        _rTransitionSounds.IsChecked = t.TransitionSounds; _rEven.IsChecked = t.EvenLoudness; _rVar.IsChecked = t.VarChecks;
        _tStyle.SelectedIndex = Math.Max(0, Array.IndexOf(StyleChoices, t.TransitionStyle));
        _tSound.SelectedIndex = Math.Max(0, Array.IndexOf(SoundChoices, t.TransitionSound));
        // every cut: what it uses, the editor's suggestion first (★), why
        _cutRows.Children.Clear();
        foreach (var c in pr.Cuts)
        {
            var pick = t.Cuts.FirstOrDefault(x => Math.Abs(x.From - c.From) < 0.5);
            var styles = new[] { "(as the setting above)" }.Concat(c.Options.Select(o => (o == c.Suggested ? "★ " : "") + TransitionKinds.Name(o))).ToArray();
            var sc = new ComboBox { Width = 210, ItemsSource = styles, Tag = ("style", c.From, c.Options), SelectedIndex = pick?.Style is { } ps ? 1 + Math.Max(0, c.Options.ToList().IndexOf(ps)) : 0 };
            var sounds = new[] { "(as the setting above)" }.Concat(TransitionKinds.Sounds.Select(o => (o == c.SuggestedSound ? "★ " : "") + TransitionKinds.SoundName(o))).ToArray();
            var so = new ComboBox { Width = 150, ItemsSource = sounds, Tag = ("sound", c.From, (IReadOnlyList<string>)TransitionKinds.Sounds), Margin = new Thickness(6, 0, 0, 0),
                SelectedIndex = pick?.Sound is { } pso ? 1 + Math.Max(0, Array.IndexOf(TransitionKinds.Sounds, pso)) : 0 };
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var cut = c;
            line.Children.Add(Watchable(() => PlayAt(cut.At - 2.5, cut.From, 5)));
            line.Children.Add(new TextBlock { Text = EditFeedback.Clock(c.At), Width = 46, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("MutedBrush") });
            line.Children.Add(sc); line.Children.Add(so);
            row.Children.Add(line);
            row.Children.Add(new TextBlock { Text = $"now: {TransitionKinds.Name(c.Style)}, {TransitionKinds.SoundName(c.Sound).ToLowerInvariant()} · suggested: {TransitionKinds.Name(c.Suggested)} ({c.Why})",
                FontSize = 11, Foreground = Res("MutedBrush"), Margin = new Thickness(82, 1, 0, 0), TextWrapping = TextWrapping.Wrap });
            _cutRows.Children.Add(row);
        }
        if (pr.Cuts.Count == 0) _cutRows.Children.Add(new TextBlock { Text = "One clip: no cuts between clips.", Foreground = Res("MutedBrush"), FontSize = 12 });
        _rShake.IsChecked = t.Shake; _rLetterbox.IsChecked = t.Letterbox;
        _sfxVol.Value = t.SfxVolume; _sfxVolText.Text = $"{t.SfxVolume:P0}";
        _moments.Children.Clear();
        foreach (var m in pr.Moments)
        {
            bool removed = t.Remove.Any(r => r[0] <= m.From + 0.5 && r[1] >= m.To - 0.5);
            var mm = m;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            line.Children.Add(Watchable(() => PlayAt(mm.At, mm.From, mm.To - mm.From)));
            line.Children.Add(new CheckBox { IsChecked = !removed, Tag = m, VerticalAlignment = VerticalAlignment.Center,
                Content = $"{EditFeedback.Clock(m.At)}  ·  {(m.Quote.Length > 0 ? "“" + m.Quote + "”" : "(no words)")}  ·  from {EditFeedback.Clock(m.From)} of the recording" });
            _moments.Children.Add(line);
        }
    }

    /// <summary>The controls into the project's changes.</summary>
    private void ReadControls()
    {
        if (_project is not { } pr) return;
        var t = pr.Tweaks;
        string title = _rTitle.Text.Trim();
        t.Title = title.Length == 0 || title == pr.Title ? null : title;
        foreach (var row in _cards.Children.OfType<DockPanel>())
            foreach (var tb in row.Children.OfType<TextBox>())
                if (tb.Tag is ValueTuple<string, int> tag)
                {
                    var ch = pr.Chapters.First(c => c.Index == tag.Item2);
                    var card = t.Card(tag.Item2);
                    if (tag.Item1 == "h") card.Heading = tb.Text.Trim() == ch.Heading ? card.Heading : tb.Text.Trim();
                    else card.Sub = tb.Text.Trim() == ch.Sub ? card.Sub : tb.Text.Trim();
                }
        t.Music = _rMusic.IsChecked == true && _musicVol.Value > 0.001;
        if (t.Music) t.MusicVolume = _musicVol.Value;
        t.Pace = Math.Max(0, _pace.SelectedIndex) - 1;
        t.Zooms = Math.Max(0, _zooms.SelectedIndex);
        t.Emphasis = Math.Max(0, _emphasis.SelectedIndex) - 1;
        if (double.TryParse(_rMinutes.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var mins) && mins is >= 1 and <= 120)
            t.Minutes = Math.Abs(mins - pr.TargetSeconds / 60) < 0.01 ? null : mins;
        t.SlowMotion = _rSlow.IsChecked == true; t.FaceCam = _rFace.IsChecked == true; t.ColdOpen = _rCold.IsChecked == true;
        t.Captions = _rCaptions.IsChecked == true;
        t.Chapters = _rChapters.IsChecked == true;
        t.TransitionSounds = _rTransitionSounds.IsChecked == true; t.EvenLoudness = _rEven.IsChecked == true; t.VarChecks = _rVar.IsChecked == true;
        if (_tStyle.SelectedIndex >= 0) t.TransitionStyle = StyleChoices[_tStyle.SelectedIndex];
        if (_tSound.SelectedIndex >= 0) t.TransitionSound = SoundChoices[_tSound.SelectedIndex];
        foreach (var row in _cutRows.Children.OfType<StackPanel>())
            foreach (var cb in row.Children.OfType<StackPanel>().SelectMany(l => l.Children.OfType<ComboBox>()))
                if (cb.Tag is ValueTuple<string, double, IReadOnlyList<string>> tag)
                {
                    var (what, from, options) = tag;
                    string? chosen = cb.SelectedIndex <= 0 ? null : options[cb.SelectedIndex - 1];
                    if (chosen is null && t.Cuts.All(x => Math.Abs(x.From - from) >= 0.5)) continue;
                    var pick = t.Cut(from);
                    if (what == "style") pick.Style = chosen; else pick.Sound = chosen;
                }
        t.Cuts.RemoveAll(c => c.Style is null && c.Sound is null);
        t.Shake = _rShake.IsChecked == true; t.Letterbox = _rLetterbox.IsChecked == true;
        t.SfxVolume = _sfxVol.Value;
        if (t.Chapters && !pr.ChapterCards) pr.ChapterCards = true; // made without, wanted now
        foreach (var cb in _moments.Children.OfType<StackPanel>().SelectMany(l => l.Children.OfType<CheckBox>()))
            if (cb.Tag is EditMoment m)
            {
                t.Remove.RemoveAll(r => r[0] <= m.From + 0.5 && r[1] >= m.To - 0.5);
                if (cb.IsChecked != true) t.Remove.Add(new[] { m.From, m.To });
            }
    }

    /// <summary>A new suggestion line (after <paramref name="after"/>, else at the end).</summary>
    private Suggestion AddSuggestion(Suggestion? after = null)
    {
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 30, Padding = new Thickness(6, 4, 6, 4) };
        var num = new TextBlock { Width = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), Foreground = Res("MutedBrush"), FontSize = 12 };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(22, 2, 0, 0), Visibility = Visibility.Collapsed };
        var line = new DockPanel();
        line.Children.Add(num); DockPanel.SetDock(num, Dock.Left);
        line.Children.Add(box);
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(line); row.Children.Add(result);
        var sg = new Suggestion { Box = box, Result = result, Row = row };
        int at = after is null ? _suggestions.Count : _suggestions.IndexOf(after) + 1;
        _suggestions.Insert(at, sg);
        _suggestionRows.Children.Insert(at, row);
        Renumber();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                // Enter: this suggestion is applied, and the next line is ready
                e.Handled = true;
                ApplySuggestion(sg);
                int k = _suggestions.IndexOf(sg);
                var next = k + 1 < _suggestions.Count && _suggestions[k + 1].Box.Text.Trim().Length == 0 ? _suggestions[k + 1]
                    : sg.Box.Text.Trim().Length == 0 ? sg : AddSuggestion(sg);
                next.Box.Focus();
            }
            else if (e.Key == System.Windows.Input.Key.Back && box.Text.Length == 0 && _suggestions.Count > 1)
            {
                // Backspace on an empty line: the line goes, back to the one above
                e.Handled = true;
                int k = _suggestions.IndexOf(sg);
                _suggestions.RemoveAt(k); _suggestionRows.Children.Remove(row);
                Renumber();
                var prev = _suggestions[Math.Max(0, k - 1)];
                prev.Box.Focus(); prev.Box.CaretIndex = prev.Box.Text.Length;
            }
            else if (e.Key == System.Windows.Input.Key.Up && box.GetLineIndexFromCharacterIndex(box.CaretIndex) == 0 && _suggestions.IndexOf(sg) > 0)
            { e.Handled = true; _suggestions[_suggestions.IndexOf(sg) - 1].Box.Focus(); }
            else if (e.Key == System.Windows.Input.Key.Down && box.GetLineIndexFromCharacterIndex(box.CaretIndex) >= box.LineCount - 1 && _suggestions.IndexOf(sg) < _suggestions.Count - 1)
            { e.Handled = true; _suggestions[_suggestions.IndexOf(sg) + 1].Box.Focus(); }
        };
        // pasted lines become one suggestion each
        box.TextChanged += (_, _) =>
        {
            if (!box.Text.Contains('\n')) return;
            var lines = box.Text.Replace("\r", "").Split('\n');
            box.Text = lines[0];
            var last = sg;
            foreach (var l in lines.Skip(1).Where(l => l.Trim().Length > 0)) { last = AddSuggestion(last); last.Box.Text = l; }
            last.Box.Focus(); last.Box.CaretIndex = last.Box.Text.Length;
        };
        // an applied line that's edited shows it needs applying again
        box.TextChanged += (_, _) => { if (sg.Applied is not null && box.Text.Trim() != sg.Applied) sg.Result.Opacity = 0.45; else sg.Result.Opacity = 1; };
        return sg;
    }

    private void Renumber()
    {
        for (int k = 0; k < _suggestions.Count; k++)
            if (_suggestions[k].Row is StackPanel sp && sp.Children[0] is DockPanel dp && dp.Children[0] is TextBlock n) n.Text = $"{k + 1}.";
    }

    /// <summary>One suggestion → the changes it asks for, shown under it (never an error: what can't be changed is noted).</summary>
    private void ApplySuggestion(Suggestion sg)
    {
        if (_project is not { } pr) return;
        string text = sg.Box.Text.Trim();
        if (text.Length == 0 || text == sg.Applied) return;
        ReadControls(); // what's already been changed by hand stays
        var res = EditFeedback.Apply(text, pr);
        RefreshControls();
        sg.Applied = text;
        var parts = res.Understood.Select(x => "✓ " + x).ToList();
        if (res.NotUnderstood.Count > 0) parts.Add("Noted: " + string.Join("; ", res.NotUnderstood.Select(x => "“" + x + "”")) + " (see what the editor can change below)");
        sg.Result.Text = string.Join(Environment.NewLine, parts);
        sg.Result.Foreground = res.Understood.Count > 0 ? Res("TextBrush") : Res("MutedBrush");
        sg.Result.Opacity = 1;
        sg.Result.Visibility = Visibility.Visible;
        bool anyNoted = _suggestions.Any(x => x.Result.Text.Contains("Noted:"));
        _understood.Text = (anyNoted
            ? "The editor can change the music, sound effects, transitions, loudness between parts, captions, chapters and their text, zooms, slow motion, screen shake, " +
              "cuts to your face, the hook, the end screen, the cold open, pace, length, what it focuses on, the title, and which moments are in. Say which of those you'd like different, or adjust them below. "
            : "") + "Press “" + _rerender.Content + "” when you're ready, or “Quick preview of the edit” to watch the changes first.";
        _understood.Foreground = Res("MutedBrush");
    }

    /// <summary>Every line typed but not applied yet.</summary>
    private void ApplyFeedback()
    {
        foreach (var sg in _suggestions.ToList()) ApplySuggestion(sg);
        if (_suggestions.Count == 0 || _suggestions[^1].Box.Text.Trim().Length > 0) AddSuggestion();
    }

    private async Task ReviseAsync()
    {
        if (_project is not { } pr) return;
        if (_engine.FfmpegPath is not { } ffmpeg) { _step.Text = "FFmpeg isn't available."; return; }
        ApplyFeedback(); // lines typed but not applied yet
        ReadControls();
        bool firstRender = pr.Video.Length == 0;   // (a reviewed plan, not rendered yet)
        _cts = new CancellationTokenSource();
        _start.IsEnabled = false; _rerender.IsEnabled = false; _cancel.IsEnabled = true; _open.Visibility = Visibility.Collapsed;
        _bar.Value = 0;
        var started = DateTime.Now;
        try
        {
            var progress = new Progress<VodEditor.Progress>(x => { _bar.Value = x.Fraction; _step.Text = x.Step; });
            VodRenderer.LogPath = Path.Combine(StateStore.Folder, "logs", "vod-edit.log");
            Busy = true;
            var result = await Task.Run(() => VodEditor.ReviseAsync(ffmpeg, pr, _engine.VodVideoArgs, progress, _cts.Token))
                .ContinueWith(t => { Busy = false; return t; }).Unwrap();
            if (result.Unchanged)
            {
                _bar.Value = 0;
                _step.Text = "Not rendered again: nothing you asked for changes this video (it would come out the same file). Look under each suggestion for what it understood; " +
                             "say what you'd like different, e.g. “different clips”, “remove the part at 3:20”, “even out the loudness”, or untick moments below.";
                return;
            }
            _lastVideo = result.Video;
            _bar.Value = 1;
            _step.Text = (firstRender
                ? $"Rendered in {(DateTime.Now - started):h\\:mm\\:ss}: {Path.GetFileName(result.Video)} ({result.Length:m\\:ss}), with a thumbnail and a description next to it. You can keep revising it below."
                : $"Revised in {(DateTime.Now - started):h\\:mm\\:ss}: {Path.GetFileName(result.Video)} ({result.Length:m\\:ss}); {VodRenderer.Reused} of {pr.Pieces.Count} parts reused. The earlier version is still there (pick it under “Start from” to revise it instead).")
                + (pr.QualityNote.Length > 0 ? " " + pr.QualityNote : "");
            ShowLearned();
            _open.Visibility = Visibility.Visible;
            ShowRevise(pr);
        }
        catch (OperationCanceledException) { _step.Text = "Stopped."; _bar.Value = 0; }
        catch (Exception ex) { _step.Text = "Couldn't revise the video: " + ex.Message + " (details in logs\\vod-edit.log)"; VodRenderer.Note("Revise failed: " + ex); }
        finally { _cts = null; _start.IsEnabled = true; _rerender.IsEnabled = true; _cancel.IsEnabled = false; }
    }

    private void OpenEarlier()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Edited videos|*.mp4|All files|*.*", InitialDirectory = _settings.VodEditFolder, Title = "Pick an edited video to revise" };
        if (dlg.ShowDialog(this) != true) return;
        var pr = VodProject.Load(dlg.FileName);
        if (pr is null) { _step.Text = "That video wasn't made by this version of the editor (no revision info next to it). Make it again once and it can be revised from then on."; return; }
        if (!File.Exists(pr.Recording)) { _step.Text = "The recording that video came from isn't where it was: " + pr.Recording; return; }
        pr.Video = dlg.FileName;
        _lastVideo = dlg.FileName;
        _step.Text = "Revising " + Path.GetFileName(dlg.FileName);
        ShowRevise(pr);
    }

    private TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold, FontSize = 12 };

    private void LoadRecordings()
    {
        _list.Items.Clear();
        try
        {
            var files = new DirectoryInfo(_settings.RecordingFolder).Exists
                ? new DirectoryInfo(_settings.RecordingFolder).GetFiles("*.*").Where(f => f.Extension.ToLowerInvariant() is ".mp4" or ".mkv" or ".mov" or ".flv")
                    .Where(f => !f.Name.StartsWith("Replay ")).OrderByDescending(f => f.LastWriteTime).Take(40)
                : Enumerable.Empty<FileInfo>();
            foreach (var f in files) Add(f.FullName);
        }
        catch { }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        else _step.Text = "No recordings yet in " + _settings.RecordingFolder + ". Record a stream, or pick a file.";
    }

    private void Add(string path, bool select = false)
    {
        var f = new FileInfo(path);
        bool logged = File.Exists(VodSidecar.PathFor(path));
        var sp = new StackPanel { Margin = new Thickness(6, 3, 6, 3) };
        sp.Children.Add(new TextBlock { Text = f.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { FontSize = 11, Foreground = Res("MutedBrush"),
            Text = $"{f.LastWriteTime:ddd MMM d, h:mm tt} · {f.Length / 1048576.0 / 1024:0.0} GB{(logged ? " · session log ✓" : "")}" });
        var item = new ListBoxItem { Content = sp, Tag = path };
        if (select) { _list.Items.Insert(0, item); _list.SelectedItem = item; } else _list.Items.Add(item);
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Videos|*.mp4;*.mkv;*.mov;*.flv|All files|*.*", InitialDirectory = _settings.RecordingFolder };
        if (dlg.ShowDialog(this) == true) Add(dlg.FileName, true);
    }

    private void ChooseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _settings.VodEditFolder };
        if (dlg.ShowDialog(this) == true) { _settings.VodEditFolder = dlg.FolderName; _folder.Text = dlg.FolderName; _save(); }
    }

    private async Task RunAsync()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: string recording }) { _step.Text = "Pick a recording first."; return; }
        if (_engine.FfmpegPath is not { } ffmpeg) { _step.Text = "FFmpeg isn't available."; return; }
        if (!int.TryParse(_minutes.Text.Trim(), out int minutes) || minutes < 1 || minutes > 120) { _step.Text = "Length: 1 to 120 minutes."; return; }
        _settings.VodEditMinutes = minutes;
        _settings.VodEditChapters = _chapters.IsChecked == true;
        _settings.VodEditReviewFirst = _mode.SelectedIndex == 1;
        _settings.VodChannel = _channel.Text.Trim();
        _settings.VodMusicSource = MusicSources[Math.Max(0, _musicSource.SelectedIndex)];
        _settings.VodHook = _hook.IsChecked == true; _settings.VodEndScreen = _endScreen.IsChecked == true;
        _save();
        _cts = new CancellationTokenSource();
        _start.IsEnabled = false; _cancel.IsEnabled = true; _open.Visibility = Visibility.Collapsed;
        var started = DateTime.Now;
        try
        {
            var opts = new VodEditOptions(_title.Text.Trim().Length == 0 ? "Stream highlights" : _title.Text.Trim(), minutes * 60, _captions.IsChecked == true,
                _music.IsChecked == true, _settings.RecapSocials, _chapters.IsChecked == true, CurrentBrand());
            var progress = new Progress<VodEditor.Progress>(x => { _bar.Value = x.Fraction; _step.Text = x.Step; });
            VodRenderer.LogPath = Path.Combine(StateStore.Folder, "logs", "vod-edit.log");
            if (BackgroundWork.Live) _step.Text = "You're live: editing slowly in the background so the stream stays smooth (full speed once you stop).";
            Busy = true;
            var result = await Task.Run(() => VodEditor.MakeAsync(ffmpeg, recording, _settings.VodEditFolder, opts, _engine.VodVideoArgs,
                new Progress<VodEditor.Progress>(x => { ((IProgress<VodEditor.Progress>)progress).Report(BackgroundWork.Live ? x with { Step = x.Step + " (slowed while you're live)" } : x); }), _cts.Token,
                reviewFirst: _settings.VodEditReviewFirst))
                .ContinueWith(t => { Busy = false; return t; }).Unwrap();
            if (result is null)
            {
                // review first: the plan, every cut with the editor's suggestions; render when it's right
                if (VodEditor.LastProject is { } planned) ShowRevise(planned, review: true);
                _bar.Value = 0.3;
                _step.Text = $"Planned in {(DateTime.Now - started):m\\:ss}: {planned_summary()}. Press “Quick preview of the edit” to watch it (or ▶ any moment or cut), change anything below, then press “Render”."
                             + (VodEditor.LastProject?.LearnedNote is { Length: > 0 } ln ? " " + ln : "");
                return;
                string planned_summary() => VodEditor.LastProject is { } pp ? $"{pp.Moments.Count} moments, {pp.Cuts.Count} cuts, {TimeSpan.FromSeconds(pp.Duration):m\\:ss} long" : "";
            }
            _lastVideo = result.Video;
            ShowLearned();
            if (VodEditor.LastProject is { } made) ShowRevise(made);
            _bar.Value = 1;
            _step.Text = $"Done in {(DateTime.Now - started):h\\:mm\\:ss}: {Path.GetFileName(result.Video)} ({result.Length:m\\:ss}), with a thumbnail and a description with chapters next to it."
                         + (VodRenderer.Fallbacks > 0 ? $" ({VodRenderer.Fallbacks} effect(s) were simplified; details in logs\\vod-edit.log)" : "")
                         + (VodEditor.LastProject?.LearnedNote is { Length: > 0 } ln2 ? " " + ln2 : "")
                         + (VodEditor.LastProject?.QualityNote is { Length: > 0 } qn ? " " + qn : "");
            _open.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { _step.Text = "Stopped."; _bar.Value = 0; }
        catch (Exception ex)
        {
            _step.Text = "Couldn't make the video: " + ex.Message + " (details in logs\\vod-edit.log)";
            // the whole story goes to logs\vod-edit.log (the command, FFmpeg's full output, where it happened)
            try
            {
                string log = Path.Combine(StateStore.Folder, "logs", "vod-edit.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {recording}{Environment.NewLine}{ex}{Environment.NewLine}{VodRenderer.LastFailure}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }
        finally
        {
            _cts = null;
            _start.IsEnabled = true; _cancel.IsEnabled = false;
        }
    }
}
