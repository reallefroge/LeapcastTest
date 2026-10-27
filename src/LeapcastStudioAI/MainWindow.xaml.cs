using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LeapcastStudioAI;

public sealed class MixerChannel : INotifyPropertyChanged
{
    private double _meter;
    private string _dbText = "-∞ dB";
    public Guid SourceId { get; init; }
    public string Name { get; init; } = "";
    public double Meter { get => _meter; private set { _meter = value; OnChanged(); } }
    public string DbText { get => _dbText; private set { _dbText = value; OnChanged(); } }

    public void Update(double db)
    {
        double target = Math.Clamp(db + 60, 0, 60);
        // fast attack, slow release, like a real meter
        Meter = target > _meter ? target : Math.Max(target, _meter - 2.5);
        DbText = db <= -59.5 ? "-∞ dB" : $"{db:0} dB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class MainWindow : Window
{
    private readonly StudioState _state;
    private readonly WasapiAudioBridge _audio = new();
    private readonly StreamEngine _engine;
    private readonly ObservableCollection<MixerChannel> _mixer = new();
    private readonly ObservableCollection<string> _activity = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly WriteableBitmap _bitmap;
    private readonly object _frameLock = new();
    private byte[]? _frame;
    private bool _frameDirty;
    private EngineStatus? _status;
    private bool _busy;
    private bool _closeConfirmed;
    private bool _ready;

    private StudioScene? SelectedScene => ScenesList.SelectedItem as StudioScene;
    private string StreamKey => SecretStore.Unprotect(_state.Settings.StreamKeyProtected);

    public MainWindow()
    {
        InitializeComponent();
        _state = StateStore.Load();
        _engine = new StreamEngine(_audio, new WindowsPlatform(), StateStore.Folder);
        _bitmap = new WriteableBitmap(_engine.PreviewWidth, _engine.PreviewHeight, 96, 96, PixelFormats.Bgra32, null);
        PreviewImage.Source = _bitmap;

        _engine.FrameReady += OnFrame;
        _engine.StatusChanged += st => Dispatcher.BeginInvoke(() => ApplyStatus(st));
        _engine.Message += (_, e) => Dispatcher.BeginInvoke(() => Log(e.Text, e.IsError));
        _engine.ModesChanged += () => Dispatcher.BeginInvoke(() => ApplyStatus(_engine.Snapshot()));
        _audio.DeviceError += msg => Dispatcher.BeginInvoke(() => Log(msg, true));

        ScenesList.ItemsSource = _state.Scenes;
        MixerList.ItemsSource = _mixer;
        ActivityList.ItemsSource = _activity;
        var selected = _state.Scenes.FirstOrDefault(s => s.Id == _state.SelectedSceneId) ?? _state.Scenes.FirstOrDefault();
        if (selected is not null) ScenesList.SelectedItem = selected;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(66) };
        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();
        HardwareLabel.Text = $"{Environment.ProcessorCount} CPU threads · detecting encoders…";
        UpdatePlanDisplay();
    }

    // ------------------------------------------------------------------ startup / shutdown

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var ffmpeg = FfmpegLocator.Find(_state.Settings.FfmpegPath);
        if (ffmpeg is null)
        {
            SetBadge("FFMPEG MISSING", "#3A2224", "#FF7A7A");
            EngineStatus.Text = "FFmpeg wasn't found. Reinstall Leapcast (the installer bundles it) or set its path in Settings → Advanced.";
            HardwareLabel.Text = $"{Environment.ProcessorCount} CPU threads · FFmpeg not found";
            return;
        }
        _engine.FfmpegPath = ffmpeg;
        SetBadge("DETECTING ENCODERS…", "#332F26", "#FFC978");
        EngineStatus.Text = "Testing which video encoders this PC has…";
        string? version = await FfmpegLocator.VersionAsync(ffmpeg);
        await _engine.ProbeEncodersAsync();
        var names = _engine.Encoders.Select(x => EncoderCatalog.DisplayName(x.Name) + (x.Tier == EncoderTier.Basic ? " (basic)" : ""));
        HardwareLabel.Text = $"{Environment.ProcessorCount} CPU threads · Encoders: {string.Join(", ", names)} · FFmpeg {version ?? "?"}";
        if (_engine.Encoders.Count == 0)
        {
            SetBadge("NO ENCODER", "#3A2224", "#FF7A7A");
            EngineStatus.Text = "No working H.264 encoder was found. Update your graphics driver, or check the FFmpeg build.";
            return;
        }
        _ready = true;
        await ApplyToEngineAsync(restart: false);
        SetBadge("READY · " + EncoderCatalog.DisplayName(_engine.PlanFor(_state.Settings).Encoder).ToUpperInvariant(), "#1E3328", "#6FDB9A");
        EngineStatus.Text = string.IsNullOrEmpty(StreamKey)
            ? "Ready. Add your stream key in Settings, then start the preview or go live."
            : "Ready.";
        Log("Engine ready: " + string.Join(", ", names), false);
        UpdatePlanDisplay();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        Save();
        if (_closeConfirmed) { _uiTimer.Stop(); _engine.Dispose(); return; }
        if (_engine.Live)
        {
            var answer = MessageBox.Show(this, "You're live. End the stream and quit?", "Leapcast Studio AI",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        if (_engine.Running || _engine.Recording)
        {
            e.Cancel = true;
            IsEnabled = false;
            EngineStatus.Text = _engine.Recording ? "Finishing your recording…" : "Stopping…";
            try { await _engine.StopAllAsync(); } catch { }
            _closeConfirmed = true;
            Close();
            return;
        }
        _closeConfirmed = true;
        _uiTimer.Stop();
        _engine.Dispose();
    }

    // ------------------------------------------------------------------ engine plumbing

    private Task ApplyToEngineAsync(bool restart)
    {
        var scene = SelectedScene;
        if (scene is null) return Task.CompletedTask;
        return _engine.UpdateAsync(scene, _state.Settings, StreamKey, restart);
    }

    private void OnFrame(byte[] frame)
    {
        lock (_frameLock)
        {
            if (_frame is null || _frame.Length != frame.Length) _frame = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, _frame, 0, frame.Length);
            _frameDirty = true;
        }
    }

    private void OnUiTick()
    {
        lock (_frameLock)
        {
            if (_frameDirty && _frame is not null)
            {
                _bitmap.WritePixels(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight), _frame, _bitmap.PixelWidth * 4, 0);
                _frameDirty = false;
            }
        }
        bool running = _engine.Running;
        foreach (var ch in _mixer) ch.Update(running ? _audio.LevelDb(ch.SourceId) : -60);
    }

    private void ApplyStatus(EngineStatus st)
    {
        _status = st;
        bool encoding = st.Live || st.Recording || st.ReplayBuffer;
        PreviewPlaceholder.Visibility = st.Running ? Visibility.Collapsed : Visibility.Visible;
        PreviewImage.Visibility = st.Running ? Visibility.Visible : Visibility.Hidden;

        StreamButton.Content = st.Live ? (st.Reconnecting ? "■  End stream (reconnecting…)" : "■  End stream") : "▶  Go live";
        StreamButton.Background = st.Live ? (Brush)FindResource("LiveBrush") : new SolidColorBrush(Color.FromRgb(0x57, 0x48, 0xC7));
        RecordButton.Content = st.Recording ? "■  Stop rec" : "●  Record";
        PreviewButton.Content = _engine.PreviewOn ? "Stop preview" : "Preview";
        ReplayBufferButton.Content = st.ReplayBuffer ? "Replay buffer: on" : "Replay buffer";
        SaveReplayButton.IsEnabled = encoding && st.Running;

        LiveTag.Visibility = st.Live ? Visibility.Visible : Visibility.Collapsed;
        LiveTagText.Text = st.Reconnecting ? "RECONNECTING" : $"LIVE  {Fmt(st.LiveFor)}";
        RecTag.Visibility = st.Recording ? Visibility.Visible : Visibility.Collapsed;
        RecTagText.Text = $"● REC  {Fmt(st.RecordingFor)}";
        ReplayTag.Visibility = st.ReplayBuffer ? Visibility.Visible : Visibility.Collapsed;

        StatNow.Text = encoding && st.RecentKbps > 0 ? $"{st.RecentKbps / 1000:0.00} Mbps" : "—";
        StatAvg.Text = st.Live && st.AverageKbps > 0 ? $"{st.AverageKbps / 1000:0.00} Mbps" : "—";
        bool cbr = _engine.CurrentPlan?.Cbr ?? false;
        StatSaved.Text = st.Live && st.AverageKbps > 0 && st.CapKbps > 0 && !cbr
            ? $"{Math.Max(0, (1 - st.AverageKbps / (st.CapKbps + _state.Settings.AudioBitrateKbps)) * 100):0}%"
            : "—";
        StatFps.Text = st.Running && st.Fps > 0 ? $"{st.Fps:0.0} / {_engine.CurrentPlan?.Fps ?? 30}" : "—";
        StatHealth.Text = st.Running || st.Reconnecting ? st.Health : "—";
        StatHealth.Foreground = st.Health.StartsWith("Struggling") || st.Reconnecting ? (Brush)FindResource("BadBrush")
            : st.Health.StartsWith("Excellent") ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("TextBrush");
        StatTime.Text = st.Live ? Fmt(st.LiveFor) : "—";

        if (_ready && !_busy)
        {
            if (st.Live) SetBadge(st.Reconnecting ? "RECONNECTING" : "LIVE", "#4A1D20", "#FF9A9A");
            else if (st.Recording) SetBadge("RECORDING", "#3A2224", "#FF9A9A");
            else SetBadge("READY · " + EncoderCatalog.DisplayName((_engine.CurrentPlan ?? _engine.PlanFor(_state.Settings)).Encoder).ToUpperInvariant(), "#1E3328", "#6FDB9A");
        }
        if (st.Running) UpdatePlanDisplay();
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    private string? _shownPlan;

    private void UpdatePlanDisplay()
    {
        var plan = _engine.Running && _engine.CurrentPlan is not null ? _engine.CurrentPlan : _engine.PlanFor(_state.Settings);
        string key = plan.Summary + "|" + plan.ProfileName + "|" + string.Join("|", plan.Reasons);
        if (key == _shownPlan) return;
        _shownPlan = key;
        PlanTitle.Text = $"{plan.Width} × {plan.Height}  ·  {plan.Fps} FPS";
        PlanSubtitle.Text = $"{EncoderCatalog.DisplayName(plan.Encoder)} · {(plan.Cbr ? "constant" : "smart")} bitrate up to {plan.CapKbps} kbps · {plan.ProfileName}";
        ReasonsList.ItemsSource = plan.Reasons;
    }

    private void SetBadge(string text, string bg, string fg)
    {
        EngineBadgeText.Text = text;
        EngineBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        EngineBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
    }

    private void Log(string text, bool error)
    {
        _activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (_activity.Count > 40) _activity.RemoveAt(_activity.Count - 1);
        if (error)
        {
            EngineStatus.Text = text;
            EngineStatus.Foreground = (Brush)FindResource("BadBrush");
        }
    }

    private void Status(string text)
    {
        EngineStatus.Text = text;
        EngineStatus.Foreground = (Brush)FindResource("WarnBrush");
    }

    /// <summary>Runs an engine action with buttons disabled and errors shown instead of crashing.</summary>
    private async Task Guarded(string working, Func<Task> action, string? done = null)
    {
        if (_busy) return;
        if (!_ready)
        {
            MessageBox.Show(this, EngineStatus.Text, "Engine not ready", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _busy = true;
        SetButtonsEnabled(false);
        Status(working);
        try
        {
            await action();
            if (done is not null) Status(done);
        }
        catch (Exception ex)
        {
            Log(ex.Message, true);
            MessageBox.Show(this, ex.Message, "Leapcast Studio AI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
            ApplyStatus(_engine.Snapshot());
        }
    }

    private void SetButtonsEnabled(bool on)
    {
        StreamButton.IsEnabled = RecordButton.IsEnabled = PreviewButton.IsEnabled = PreviewButtonCenter.IsEnabled =
            ReplayBufferButton.IsEnabled = SaveReplayButton.IsEnabled = on;
    }

    // ------------------------------------------------------------------ output buttons

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        bool on = !_engine.PreviewOn;
        await Guarded(on ? "Starting preview…" : "Stopping preview…", () => _engine.SetPreviewAsync(on), on ? "Previewing." : "Preview stopped.");
    }

    private async void Stream_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.Live)
        {
            if (string.IsNullOrWhiteSpace(StreamKey))
            {
                MessageBox.Show(this, "Add your stream key in Settings first.", "Go live", MessageBoxButton.OK, MessageBoxImage.Information);
                Settings_Click(sender, e);
                return;
            }
            await Guarded("Connecting to " + _state.Settings.Platform + "…", () => _engine.StartLiveAsync(), "You're live!");
        }
        else
        {
            var answer = MessageBox.Show(this, "End the stream?", "Leapcast Studio AI", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            await Guarded("Ending stream…", () => _engine.StopLiveAsync(), "Stream ended.");
        }
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.Recording)
        {
            await Guarded("Starting recording…", () => _engine.StartRecordingAsync(), "Recording.");
        }
        else
        {
            string? path = null;
            await Guarded("Saving recording…", async () => path = await _engine.StopRecordingAsync());
            if (!string.IsNullOrEmpty(path)) { Status("Saved " + Path.GetFileName(path)); Log("Recording saved: " + path, false); }
        }
    }

    private async void ReplayBuffer_Click(object sender, RoutedEventArgs e)
    {
        bool on = !_engine.ReplayBuffer;
        await Guarded(on ? "Starting replay buffer…" : "Stopping replay buffer…", () => _engine.SetReplayBufferAsync(on),
            on ? $"Replay buffer on: the last {_state.Settings.ReplaySeconds}s can be saved any time." : "Replay buffer off.");
    }

    private async void Replay_Click(object sender, RoutedEventArgs e)
    {
        // Saving doesn't interrupt anything, so don't block the other buttons.
        try
        {
            Status("Saving replay…");
            var path = await _engine.SaveReplayAsync();
            Status("Replay saved: " + Path.GetFileName(path));
            Log("Replay saved: " + path, false);
        }
        catch (Exception ex) { Log(ex.Message, true); }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_state.Settings, StreamKey, _engine) { Owner = this };
        if (window.ShowDialog() != true) return;
        bool ffmpegChanged = window.Result.FfmpegPath != _state.Settings.FfmpegPath;
        _state.Settings = window.Result;
        Save();
        UpdatePlanDisplay();
        if (ffmpegChanged && !_engine.Running)
        {
            MessageBox.Show(this, "Restart Leapcast to use the new FFmpeg location.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        if (_engine.Running)
        {
            if (_engine.Live)
            {
                var answer = MessageBox.Show(this, "Apply the new settings now? Your stream will blink for a second.",
                    "Settings", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) { await ApplyToEngineAsync(false); Status("Settings saved: they apply next time you go live."); return; }
            }
            await Guarded("Applying settings…", () => ApplyToEngineAsync(true), "Settings applied.");
        }
        else
        {
            await ApplyToEngineAsync(false);
            Status("Settings saved.");
        }
    }

    // ------------------------------------------------------------------ scenes & sources

    private void Save()
    {
        try { StateStore.Save(_state); }
        catch (Exception ex) { Status("Could not save settings: " + ex.Message); }
    }

    private async void RefreshScene()
    {
        _mixer.Clear();
        if (SelectedScene is null)
        {
            SourcesList.ItemsSource = null;
            SelectedSceneLabel.Text = "";
            return;
        }
        SourcesList.ItemsSource = SelectedScene.Sources;
        SelectedSceneLabel.Text = SelectedScene.Name;
        foreach (var s in SelectedScene.Sources.Where(s => s.Visible && SourceTypes.IsAudio(SourceTypes.KindOf(s.Type))))
            _mixer.Add(new MixerChannel { SourceId = s.Id, Name = s.Name });
        _state.SelectedSceneId = SelectedScene.Id;
        Save();
        if (_ready) await ApplySceneSafelyAsync(restart: false, "Switching scene…", "Scene: " + SelectedScene.Name);
    }

    /// <summary>Scene changes are queued by the engine's own lock, so they're never dropped while another action runs.</summary>
    private async Task ApplySceneSafelyAsync(bool restart, string working, string done)
    {
        bool running = _engine.Running;
        if (running) Status(working);
        try
        {
            await ApplyToEngineAsync(restart);
            if (running) Status(done);
        }
        catch (Exception ex) { Log(ex.Message, true); }
    }

    /// <summary>Call after any change to the current scene's sources.</summary>
    private async void SceneEdited()
    {
        Save();
        RefreshMixerOnly();
        if (!_ready) return;
        await ApplySceneSafelyAsync(restart: true, "Updating scene…", "Scene updated.");
    }

    private void RefreshMixerOnly()
    {
        _mixer.Clear();
        if (SelectedScene is null) return;
        foreach (var s in SelectedScene.Sources.Where(s => s.Visible && SourceTypes.IsAudio(SourceTypes.KindOf(s.Type))))
            _mixer.Add(new MixerChannel { SourceId = s.Id, Name = s.Name });
    }

    private void ScenesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshScene();

    private void AddScene_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPrompt.Ask(this, "Add scene", "Name the new scene:", $"Scene {_state.Scenes.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        var scene = new StudioScene { Name = name.Trim() };
        // Start new scenes with the same capture setup as the current one: handy for "Starting soon" / "BRB" variants.
        if (SelectedScene is not null)
        {
            foreach (var s in SelectedScene.Sources)
            {
                var copy = System.Text.Json.JsonSerializer.Deserialize<StudioSource>(System.Text.Json.JsonSerializer.Serialize(s))!;
                copy.Id = Guid.NewGuid();
                scene.Sources.Add(copy);
            }
        }
        _state.Scenes.Add(scene);
        ScenesList.SelectedItem = scene;
        Save();
    }

    private void RenameScene_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScene is null) return;
        var name = TextPrompt.Ask(this, "Rename scene", "Rename scene:", SelectedScene.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        SelectedScene.Name = name.Trim();
        ScenesList.Items.Refresh();
        SelectedSceneLabel.Text = SelectedScene.Name;
        Save();
    }

    private void DeleteScene_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScene is null) return;
        if (_state.Scenes.Count == 1)
        {
            MessageBox.Show(this, "Keep at least one scene in the collection.", "Leapcast Studio AI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Delete scene \"{SelectedScene.Name}\"?", "Delete scene", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var oldIndex = ScenesList.SelectedIndex;
        _state.Scenes.Remove(SelectedScene);
        ScenesList.SelectedIndex = Math.Clamp(oldIndex, 0, _state.Scenes.Count - 1);
    }

    private void AddSource_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScene is null) return;
        var picker = new SourcePickerWindow { Owner = this };
        if (picker.ShowDialog() != true || string.IsNullOrWhiteSpace(picker.SelectedType)) return;
        var source = new StudioSource { Type = picker.SelectedType, Name = picker.SelectedType };
        if (source.Type == SourceTypes.Camera) source.Name = "Webcam";
        if (source.Type == SourceTypes.Window) source.Name = "Wizard101 window";
        if (source.Type == SourceTypes.DesktopAudio) { source.Name = "Desktop audio"; source.NoiseCleanup = false; }
        if (source.Type == SourceTypes.Microphone) source.Name = "Microphone";

        var props = new SourcePropertiesWindow(source, _engine.FfmpegPath) { Owner = this };
        if (props.ShowDialog() != true) return;
        SelectedScene.Sources.Add(source);
        SourcesList.SelectedItem = source;
        SceneEdited();
    }

    private void SourceProperties_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not StudioSource source) return;
        var props = new SourcePropertiesWindow(source, _engine.FfmpegPath) { Owner = this };
        if (props.ShowDialog() != true) return;
        SourcesList.Items.Refresh();
        SceneEdited();
    }

    private void SourcesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SourceProperties_Click(sender, e);

    private void SourceVisible_Click(object sender, RoutedEventArgs e) => SceneEdited();

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScene is null || SourcesList.SelectedItem is not StudioSource source) return;
        SelectedScene.Sources.Remove(source);
        SceneEdited();
    }

    private void MoveSourceUp_Click(object sender, RoutedEventArgs e) => MoveSource(-1);
    private void MoveSourceDown_Click(object sender, RoutedEventArgs e) => MoveSource(+1);

    private void MoveSource(int delta)
    {
        if (SelectedScene is null || SourcesList.SelectedItem is not StudioSource source) return;
        int i = SelectedScene.Sources.IndexOf(source), j = i + delta;
        if (i < 0 || j < 0 || j >= SelectedScene.Sources.Count) return;
        SelectedScene.Sources.Move(i, j);
        SourcesList.SelectedItem = source;
        SceneEdited();
    }
}

/// <summary>Minimal themed text prompt (replaces the WinForms-only VB InputBox).</summary>
public static class TextPrompt
{
    public static string? Ask(Window owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 300 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Background = new SolidColorBrush(Color.FromRgb(0x57, 0x48, 0xC7)) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(3, 0, 3, 6) });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Owner = owner, Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.FindResource("BgBrush"),
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }
}
