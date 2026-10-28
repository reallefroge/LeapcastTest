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

/// <summary>One row of the multistream panel.</summary>
public sealed class OutputRow : INotifyPropertyChanged
{
    private static readonly Brush Grey = Frozen(0x5A, 0x60, 0x70), Amber = Frozen(0xFF, 0xC9, 0x78), Green = Frozen(0x6F, 0xDB, 0x9A),
        Red = Frozen(0xFF, 0x7A, 0x7A), StartBg = Frozen(0x57, 0x48, 0xC7), StopBg = Frozen(0xB8, 0x36, 0x3B), Disabled = Frozen(0x2A, 0x2F, 0x3D);
    private static SolidColorBrush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    private string _name = "", _detail = "", _action = "";
    private Brush _dot = Grey, _actionBrush = StartBg;
    public Guid Id { get; init; }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public Brush DotBrush { get => _dot; private set => Set(ref _dot, value); }
    public string ActionText { get => _action; private set => Set(ref _action, value); }
    public Brush ActionBrush { get => _actionBrush; private set => Set(ref _actionBrush, value); }

    public void Update(StreamOutput o, OutputStatus? st)
    {
        Name = o.Name;
        string service = o.Service == o.Name ? "" : o.Service + " · ";
        string encode = o.OwnBitrateKbps > 0 ? $" · own encode {o.OwnBitrateKbps} kbps" : "";
        switch (st?.State)
        {
            case OutputState.Live:
                DotBrush = Green;
                Detail = $"LIVE {Fmt(st.LiveFor)} · {st.Kbps / 1000:0.0} Mbps" + (st.BufferedSeconds > 1.5 ? $" · {st.BufferedSeconds:0}s behind" : "");
                break;
            case OutputState.Starting:
                DotBrush = Amber; Detail = st.Message ?? "Connecting…"; break;
            case OutputState.Reconnecting:
                DotBrush = Amber; Detail = st.Message ?? "Reconnecting…"; break;
            case OutputState.Error:
                DotBrush = Red; Detail = st.Message ?? "Failed"; break;
            default:
                DotBrush = Grey;
                Detail = service + (o.Enabled ? (o.AskKeyEachTime ? "asks for the key when starting" : "ready") : "not in Start all") + encode;
                break;
        }
        bool active = st?.State is OutputState.Live or OutputState.Starting or OutputState.Reconnecting;
        ActionText = active ? "■  Stop" : "▶  Start";
        ActionBrush = active ? StopBg : (o.Enabled ? StartBg : Disabled);
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public partial class MainWindow : Window
{
    private readonly StudioState _state;
    private readonly WasapiAudioBridge _audio = new();
    private readonly StreamEngine _engine;
    private readonly ObservableCollection<MixerChannel> _mixer = new();
    private readonly ObservableCollection<string> _activity = new();
    private readonly ObservableCollection<OutputRow> _hOutputs = new(), _vOutputs = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _commitTimer;
    private WriteableBitmap? _hBitmap, _vBitmap;
    private readonly object _frameLock = new();
    private byte[]? _frame;
    private bool _frameDirty;
    private EngineStatus? _status;
    private bool _busy;
    private bool _closeConfirmed;
    private bool _ready;
    private readonly PreviewEditor _hEditor, _vEditor;
    private (int W, int H) _hCanvas = (1600, 900), _vCanvas = (720, 1280);
    private int _sizesSeen;
    private bool _syncingSelection;

    private StudioScene? SelectedScene => ScenesList.SelectedItem as StudioScene;

    public MainWindow()
    {
        InitializeComponent();
        _state = StateStore.Load();
        _engine = new StreamEngine(_audio, new WindowsPlatform(), StateStore.Folder)
        {
            KeyResolver = o => SecretStore.Unprotect(o.StreamKeyProtected),
        };

        _engine.FrameReady += OnFrame;
        _engine.StatusChanged += st => Dispatcher.BeginInvoke(() => ApplyStatus(st));
        _engine.Message += (_, e) => Dispatcher.BeginInvoke(() => Log(e.Text, e.IsError));
        _engine.ModesChanged += () => Dispatcher.BeginInvoke(() => ApplyStatus(_engine.Snapshot()));
        _audio.DeviceError += msg => Dispatcher.BeginInvoke(() => Log(msg, true));

        _hEditor = MakeEditor(HEditLayer, HViewbox, CanvasKind.Horizontal, () => _hCanvas);
        _vEditor = MakeEditor(VEditLayer, VViewbox, CanvasKind.Vertical, () => _vCanvas);

        ScenesList.ItemsSource = _state.Scenes;
        MixerList.ItemsSource = _mixer;
        ActivityList.ItemsSource = _activity;
        HorizontalOutputs.ItemsSource = _hOutputs;
        VerticalOutputs.ItemsSource = _vOutputs;
        var selected = _state.Scenes.FirstOrDefault(s => s.Id == _state.SelectedSceneId) ?? _state.Scenes.FirstOrDefault();
        if (selected is not null) ScenesList.SelectedItem = selected;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) }; // 30 FPS preview
        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();
        // keyboard nudges are saved once the keys go quiet
        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _commitTimer.Tick += (_, _) => { _commitTimer.Stop(); SceneEdited(); };

        HardwareLabel.Text = $"{Environment.ProcessorCount} CPU threads · detecting encoders…";
        // Small laptop screens: fit the work area instead of hanging off the edge.
        var area = SystemParameters.WorkArea;
        if (Width > area.Width || Height > area.Height)
        {
            Width = Math.Max(MinWidth, area.Width);
            Height = Math.Max(MinHeight, area.Height);
            if (Width > area.Width || Height > area.Height) WindowState = WindowState.Maximized;
        }
        RebuildOutputRows();
        UpdateCanvasLayout();
        UpdatePlanDisplay();
        SizeChanged += (_, _) => RedrawEditors();
    }

    private PreviewEditor MakeEditor(Canvas layer, FrameworkElement viewbox, CanvasKind kind, Func<(int, int)> size)
    {
        var ed = new PreviewEditor(layer, viewbox, kind)
        {
            Scene = () => SelectedScene,
            CanvasSize = size,
            Aspect = s => _engine.SourceAspect(s),
            NaturalSize = s => SourceTypes.KindOf(s.Type) is SourceKind.Text or SourceKind.Image ? _engine.SourceSize(s) : null,
        };
        ed.SelectionChanged += s =>
        {
            _syncingSelection = true;
            SourcesList.SelectedItem = s;
            _syncingSelection = false;
            (ed == _hEditor ? _vEditor : _hEditor).Select(s);
        };
        ed.Moving += () => { _engine.RefreshLayout(); if (ed == _hEditor) _vEditor.Redraw(); else _hEditor.Redraw(); };
        ed.Committed += () => { SceneEdited(); RedrawEditors(); };
        ed.ContextMenuRequested += (s, k) => ShowSourceMenu(s, k, layer);
        return ed;
    }

    // ------------------------------------------------------------------ startup / shutdown

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RedrawEditors();
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
        EngineStatus.Text = _state.Settings.Outputs.Count == 0
            ? "Ready. Add your Twitch, YouTube or TikTok in the Multistream panel, then start the preview or go live."
            : "Ready.";
        Log("Engine ready: " + string.Join(", ", names), false);
        UpdateCanvasLayout();
        UpdatePlanDisplay();
        // demo/automation: --preview starts the preview right away
        if (Environment.GetCommandLineArgs().Contains("--preview", StringComparer.OrdinalIgnoreCase))
        {
            try { await _engine.SetPreviewAsync(true); } catch (Exception ex) { Log(ex.Message, true); }
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        Save();
        if (_closeConfirmed) { _uiTimer.Stop(); _engine.Dispose(); return; }
        if (_engine.Live)
        {
            var answer = MessageBox.Show(this, "You're live. End the stream on every platform and quit?", "Leapcast Studio AI",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        if (_engine.Running || _engine.Recording || _engine.Live)
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
        return _engine.UpdateAsync(_state.Scenes.ToList(), scene, _state.Settings, restart);
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
            var pf = _engine.PreviewFrame;
            if (_frameDirty && _frame is not null && _frame.Length == pf.Width * pf.Height * 4)
            {
                if (_hBitmap is null || _hBitmap.PixelWidth != pf.HorizontalWidth || _hBitmap.PixelHeight != pf.Height)
                {
                    _hBitmap = new WriteableBitmap(pf.HorizontalWidth, pf.Height, 96, 96, PixelFormats.Bgra32, null);
                    HPreviewImage.Source = _hBitmap;
                }
                _hBitmap.WritePixels(new Int32Rect(0, 0, pf.HorizontalWidth, pf.Height), _frame, pf.Width * 4, 0);
                if (pf.VerticalWidth > 0)
                {
                    if (_vBitmap is null || _vBitmap.PixelWidth != pf.VerticalWidth || _vBitmap.PixelHeight != pf.Height)
                    {
                        _vBitmap = new WriteableBitmap(pf.VerticalWidth, pf.Height, 96, 96, PixelFormats.Bgra32, null);
                        VPreviewImage.Source = _vBitmap;
                    }
                    // the vertical canvas sits to the right of the horizontal one in the same frame
                    _vBitmap.WritePixels(new Int32Rect(0, 0, pf.VerticalWidth, pf.Height), _frame, pf.Width * 4, pf.HorizontalWidth * 4);
                }
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
        HPreviewImage.Visibility = st.Running ? Visibility.Visible : Visibility.Hidden;
        VPreviewImage.Visibility = st.Running && _engine.PreviewFrame.VerticalWidth > 0 ? Visibility.Visible : Visibility.Hidden;

        var enabled = _state.Settings.Outputs.Where(o => o.Enabled).ToList();
        bool allLive = enabled.Count > 0 && enabled.All(o => _engine.IsOutputActive(o.Id));
        GoLiveButton.Content = st.Live ? "■  End stream" : "▶  Go live";
        GoLiveButton.Background = st.Live ? (Brush)FindResource("LiveBrush") : (Brush)FindResource("StartBrush");
        StartAllButton.Content = allLive ? "Stop all" : "Start all";
        RecordButton.Content = st.Recording ? "■  Stop rec" : "●  Record";
        PreviewButton.Content = _engine.PreviewOn ? "■  Preview" : "▶  Preview";
        ReplayBufferButton.Content = st.ReplayBuffer ? "Replay buffer: on" : "Replay buffer";
        SaveReplayButton.IsEnabled = !_busy && encoding && st.Running;

        LiveTag.Visibility = st.Live ? Visibility.Visible : Visibility.Collapsed;
        int liveCount = st.Outputs.Count(o => o.State == OutputState.Live);
        LiveTagText.Text = st.Reconnecting ? "RECONNECTING" : $"LIVE  {Fmt(st.LiveFor)}  ·  {liveCount}/{st.Outputs.Count(o => o.State != OutputState.Error)}";
        RecTag.Visibility = st.Recording ? Visibility.Visible : Visibility.Collapsed;
        RecTagText.Text = $"● REC  {Fmt(st.RecordingFor)}";

        StatNow.Text = st.Live && st.UploadKbps > 0 ? $"↑ {st.UploadKbps / 1000:0.0} Mbps"
            : encoding && st.RecentKbps > 0 ? $"● {st.RecentKbps / 1000:0.0} Mbps" : "↑ —";
        StatAvg.Text = st.Live && st.AverageKbps > 0 ? $"{st.AverageKbps / 1000:0.00} Mbps" : "—";
        bool cbr = _engine.CurrentPlan?.Cbr ?? false;
        StatSaved.Text = st.Live && st.AverageKbps > 0 && st.CapKbps > 0 && !cbr
            ? $"{Math.Max(0, (1 - st.AverageKbps / (st.CapKbps + _state.Settings.AudioBitrateKbps)) * 100):0}%"
            : "—";
        StatFps.Text = st.Running && st.Fps > 0 ? $"{st.Fps:0} / {_engine.CurrentPlan?.Fps ?? 30} FPS" : "— FPS";
        StatHealth.Text = st.Running || st.Reconnecting ? st.Health : "Idle";
        StatHealth.Foreground = st.Health.StartsWith("Struggling") || st.Health.StartsWith("Upload") ? (Brush)FindResource("BadBrush")
            : st.Reconnecting ? (Brush)FindResource("WarnBrush")
            : st.Health.StartsWith("Excellent") ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("TextBrush");
        StatTime.Text = st.Live ? Fmt(st.LiveFor) : "—";
        UpdateOutputRows(st.Outputs);

        if (_ready && !_busy)
        {
            if (st.Live) SetBadge(st.Reconnecting ? "RECONNECTING" : "LIVE", "#4A1D20", "#FF9A9A");
            else if (st.Recording) SetBadge("RECORDING", "#3A2224", "#FF9A9A");
            else SetBadge("READY · " + EncoderCatalog.DisplayName((_engine.CurrentPlan ?? _engine.PlanFor(_state.Settings)).Encoder).ToUpperInvariant(), "#1E3328", "#6FDB9A");
        }
        if (st.Running)
        {
            UpdatePlanDisplay();
            UpdateCanvasLayout();
        }
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    private string? _shownPlan;

    private void UpdatePlanDisplay()
    {
        var plans = _engine.Running && _engine.CurrentPlans is not null ? _engine.CurrentPlans : _engine.PlansFor(_state.Settings);
        var plan = plans.Main;
        string key = plan.Summary + "|" + plan.ProfileName + "|" + string.Join("|", plan.Reasons) + "|" + plans.Vertical?.Summary + "|" + plans.UploadKbps;
        if (key == _shownPlan) return;
        _shownPlan = key;
        PlanTitle.Text = $"{plan.Width} × {plan.Height}  ·  {plan.Fps} FPS";
        PlanSubtitle.Text = $"{EncoderCatalog.DisplayName(plan.Encoder)} · {(plan.Cbr ? "constant" : "smart")} bitrate up to {plan.CapKbps} kbps · {plan.ProfileName}";
        bool vertical = _state.Settings.VerticalCanvas && plans.Vertical is not null;
        VerticalPlanText.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        if (vertical) VerticalPlanText.Text = $"Vertical: {plans.Vertical!.Width} × {plans.Vertical.Height} up to {plans.Vertical.CapKbps} kbps";
        ReasonsList.ItemsSource = plan.Reasons.Concat(plans.Vertical is not null && vertical ? plans.Vertical.Reasons : Array.Empty<string>()).ToList();
        int enabled = _state.Settings.Outputs.Count(o => o.Enabled);
        UploadPlanText.Text = enabled == 0 ? ""
            : $"Going live on {enabled} destination{(enabled == 1 ? "" : "s")} uploads up to {plans.UploadKbps / 1000.0:0.0} Mbps"
              + (_state.Settings.UploadMbps > 0 ? $" of your {_state.Settings.UploadMbps:0.#} Mbps." : ". Test your connection in Settings to fit it to your upload.");
    }

    /// <summary>Canvas sizes for the editors (the plan's size; the vertical canvas from settings).</summary>
    private void UpdateCanvasLayout()
    {
        var plan = _engine.Running && _engine.CurrentPlan is not null ? _engine.CurrentPlan : _engine.PlanFor(_state.Settings);
        var v = LayoutMath.VerticalSize(_state.Settings.VerticalResolution);
        bool changed = _hCanvas != (plan.Width, plan.Height) || _vCanvas != v;
        _hCanvas = (plan.Width, plan.Height);
        _vCanvas = v;
        HCanvasHost.Width = _hCanvas.W; HCanvasHost.Height = _hCanvas.H;
        VCanvasHost.Width = _vCanvas.W; VCanvasHost.Height = _vCanvas.H;
        HorizontalCaption.Text = $"HORIZONTAL · {_hCanvas.W}×{_hCanvas.H}";
        VerticalCaption.Text = $"VERTICAL · {_vCanvas.W}×{_vCanvas.H}";
        bool vOn = _state.Settings.VerticalCanvas;
        VerticalPanel.Visibility = vOn ? Visibility.Visible : Visibility.Collapsed;
        VerticalToggle.IsChecked = vOn;
        // redraw once real source sizes arrive from the running pipeline (exact outlines)
        int seen = SelectedScene?.Sources.Count(s => _engine.SourceSize(s) is not null) ?? 0;
        if (changed || seen != _sizesSeen) { _sizesSeen = seen; RedrawEditors(); }
    }

    private void RedrawEditors()
    {
        _hEditor.Redraw();
        if (_state.Settings.VerticalCanvas) _vEditor.Redraw();
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
        GoLiveButton.IsEnabled = StartAllButton.IsEnabled = RecordButton.IsEnabled = PreviewButton.IsEnabled = PreviewButtonCenter.IsEnabled =
            ReplayBufferButton.IsEnabled = SaveReplayButton.IsEnabled = HorizontalOutputs.IsEnabled = VerticalOutputs.IsEnabled = on;
    }

    // ------------------------------------------------------------------ multistream outputs

    private void RebuildOutputRows()
    {
        _hOutputs.Clear(); _vOutputs.Clear();
        foreach (var o in _state.Settings.Outputs)
            (o.IsVertical ? _vOutputs : _hOutputs).Add(new OutputRow { Id = o.Id });
        NoHorizontalHint.Visibility = _hOutputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoVerticalHint.Visibility = _vOutputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateOutputRows(_status?.Outputs ?? Array.Empty<OutputStatus>());
    }

    private void UpdateOutputRows(IReadOnlyList<OutputStatus> statuses)
    {
        foreach (var row in _hOutputs.Concat(_vOutputs))
        {
            var o = _state.Settings.Outputs.FirstOrDefault(x => x.Id == row.Id);
            if (o is null) continue;
            row.Update(o, statuses.FirstOrDefault(s => s.Id == row.Id));
        }
    }

    /// <summary>The key to use when starting this output: null = the saved one; "" = the user skipped it.</summary>
    private string? KeyFor(StreamOutput o)
    {
        string saved = SecretStore.Unprotect(o.StreamKeyProtected);
        if (!o.AskKeyEachTime && (saved.Length > 0 || o.Service == "Custom")) return null;
        return KeyPrompt.Ask(this, o.Name, Platforms.Get(o.Service).KeyHint) ?? "";
    }

    private async Task StartOutputsAsync(IReadOnlyList<StreamOutput> outputs)
    {
        if (outputs.Count == 0) return;
        if (outputs.Any(o => o.IsVertical) && !_state.Settings.VerticalCanvas)
        {
            // a TikTok output needs the 9:16 canvas: switch it on rather than refusing
            _state.Settings.VerticalCanvas = true;
            Save();
            UpdateCanvasLayout();
            await ApplyToEngineAsync(false);
            Log("Turned on the vertical canvas for " + string.Join(", ", outputs.Where(o => o.IsVertical).Select(o => o.Name)) + ".", false);
        }
        var requests = new List<(Guid, string?)>();
        foreach (var o in outputs)
        {
            var key = KeyFor(o);
            if (key == "") { Log($"{o.Name}: skipped (no key).", false); continue; }
            requests.Add((o.Id, key));
        }
        if (requests.Count == 0) return;
        string names = string.Join(", ", outputs.Where(o => requests.Any(r => r.Item1 == o.Id)).Select(o => o.Name));
        await Guarded("Going live on " + names + "…", async () =>
        {
            var problems = await _engine.StartOutputsAsync(requests);
            foreach (var p in problems) Log(p, true);
            if (problems.Count > 0)
                MessageBox.Show(this, string.Join(Environment.NewLine, problems), "Some outputs didn't start", MessageBoxButton.OK, MessageBoxImage.Information);
        }, "Connecting to " + names + "…");
    }

    private async void GoLive_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.Live)
        {
            var answer = MessageBox.Show(this, "End the stream on every platform?", "Leapcast Studio AI", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            await Guarded("Ending stream…", () => _engine.StopAllOutputsAsync(), "Stream ended.");
            return;
        }
        var enabled = _state.Settings.Outputs.Where(o => o.Enabled).ToList();
        if (enabled.Count == 0)
        {
            MessageBox.Show(this, "Add where you want to stream first (Twitch, YouTube, TikTok…).", "Go live", MessageBoxButton.OK, MessageBoxImage.Information);
            AddOutput_Click(sender, e);
            return;
        }
        await StartOutputsAsync(enabled);
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        var enabled = _state.Settings.Outputs.Where(o => o.Enabled).ToList();
        if (enabled.Count > 0 && enabled.All(o => _engine.IsOutputActive(o.Id)))
        {
            await Guarded("Stopping all outputs…", () => _engine.StopAllOutputsAsync(), "All outputs stopped.");
            return;
        }
        if (enabled.Count == 0) { AddOutput_Click(sender, e); return; }
        await StartOutputsAsync(enabled.Where(o => !_engine.IsOutputActive(o.Id)).ToList());
    }

    private async void ToggleOutput_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Guid id) return;
        var o = _state.Settings.Outputs.FirstOrDefault(x => x.Id == id);
        if (o is null) return;
        if (_engine.IsOutputActive(id))
        {
            await Guarded($"Stopping {o.Name}…", () => _engine.StopOutputAsync(id), $"{o.Name} stopped.");
            return;
        }
        _engine.ClearOutputError(id);
        await StartOutputsAsync(new[] { o });
    }

    private async void AddOutput_Click(object sender, RoutedEventArgs e)
    {
        var w = new OutputEditWindow(null, "") { Owner = this };
        if (w.ShowDialog() != true || w.Deleted) return;
        _state.Settings.Outputs.Add(w.Result);
        if (w.Result.IsVertical && !_state.Settings.VerticalCanvas) _state.Settings.VerticalCanvas = true;
        await OutputsEditedAsync();
        Status($"Added {w.Result.Name}.");
    }

    private async void EditOutput_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Guid id) return;
        int index = _state.Settings.Outputs.FindIndex(x => x.Id == id);
        if (index < 0) return;
        var o = _state.Settings.Outputs[index];
        var w = new OutputEditWindow(o, SecretStore.Unprotect(o.StreamKeyProtected)) { Owner = this };
        if (w.ShowDialog() != true) return;
        if (_engine.IsOutputActive(id) && w.Deleted) await Guarded($"Stopping {o.Name}…", () => _engine.StopOutputAsync(id));
        if (w.Deleted) _state.Settings.Outputs.RemoveAt(index);
        else
        {
            _state.Settings.Outputs[index] = w.Result;
            if (w.Result.IsVertical && !_state.Settings.VerticalCanvas) _state.Settings.VerticalCanvas = true;
            if (_engine.IsOutputActive(id)) Status($"{w.Result.Name} saved: the changes apply the next time it starts.");
        }
        _engine.ClearOutputError(id);
        await OutputsEditedAsync();
    }

    private async Task OutputsEditedAsync()
    {
        Save();
        RebuildOutputRows();
        UpdateCanvasLayout();
        _shownPlan = null;
        UpdatePlanDisplay();
        try { await ApplyToEngineAsync(false); } catch (Exception ex) { Log(ex.Message, true); }
    }

    private async void VerticalToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = VerticalToggle.IsChecked == true;
        if (!on && _engine.OutputStatuses().Any(o => o.Vertical && o.State != OutputState.Error))
        {
            MessageBox.Show(this, "Stop the vertical outputs before turning the vertical canvas off.", "Vertical canvas", MessageBoxButton.OK, MessageBoxImage.Information);
            VerticalToggle.IsChecked = true;
            return;
        }
        if (_engine.Live)
        {
            var answer = MessageBox.Show(this, (on ? "Add" : "Remove") + " the vertical canvas now? Your outputs will reconnect for a second.",
                "Vertical canvas", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) { VerticalToggle.IsChecked = !on; return; }
        }
        _state.Settings.VerticalCanvas = on;
        Save();
        UpdateCanvasLayout();
        _shownPlan = null;
        UpdatePlanDisplay();
        await ApplySceneSafelyAsync(false, on ? "Adding the vertical canvas…" : "Removing the vertical canvas…", on ? "Vertical canvas on." : "Vertical canvas off.");
    }

    private void ResetVertical_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScene is null) return;
        foreach (var s in SelectedScene.Sources) { s.VAuto = true; s.VShow = true; }
        SceneEdited();
        RedrawEditors();
    }

    private void ReasonsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = ReasonsList.Visibility != Visibility.Visible;
        ReasonsList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ReasonsToggle.Content = show ? "Why these settings ▴" : "Why these settings ▾";
    }

    // ------------------------------------------------------------------ preview / record / replay

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        bool on = !_engine.PreviewOn;
        await Guarded(on ? "Starting preview…" : "Stopping preview…", () => _engine.SetPreviewAsync(on), on ? "Previewing." : "Preview stopped.");
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
        var window = new SettingsWindow(_state.Settings, _engine) { Owner = this };
        if (window.ShowDialog() != true) return;
        bool ffmpegChanged = window.Result.FfmpegPath != _state.Settings.FfmpegPath;
        _state.Settings = window.Result;
        Save();
        _shownPlan = null;
        UpdatePlanDisplay();
        UpdateCanvasLayout();
        if (ffmpegChanged && !_engine.Running)
            MessageBox.Show(this, "Restart Leapcast to use the new FFmpeg location.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        if (_engine.Running)
        {
            if (_engine.Live)
            {
                var answer = MessageBox.Show(this, "Apply the new settings now? Your outputs will reconnect for a second.",
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

    // ------------------------------------------------------------------ preview editing

    private void ShowSourceMenu(StudioSource s, CanvasKind kind, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        void Item(string header, Action act, bool isChecked = false)
        {
            var mi = new MenuItem { Header = header, IsChecked = isChecked };
            mi.Click += (_, _) => { act(); SceneEdited(); RedrawEditors(); };
            menu.Items.Add(mi);
        }
        if (kind == CanvasKind.Horizontal)
        {
            var k = SourceTypes.KindOf(s.Type);
            bool canFill = k is SourceKind.Display or SourceKind.Window or SourceKind.Camera or SourceKind.Image;
            if (canFill) Item("Fill the screen", () => s.Anchor = Anchors.Fill, Anchors.Resolve(s) == Anchors.Fill);
            foreach (var a in new[] { Anchors.TopLeft, Anchors.TopRight, Anchors.BottomLeft, Anchors.BottomRight, Anchors.Center })
            {
                string anchor = a;
                Item("Snap: " + anchor.ToLowerInvariant(), () => AnchorKeepingSize(s, anchor), Anchors.Resolve(s) == anchor);
            }
            menu.Items.Add(new Separator());
            if (SourceTypes.IsVideo(k)) Item("Show on the vertical canvas", () => s.VShow = !s.VShow, s.VShow);
        }
        else
        {
            Item("Automatic vertical layout", () => s.VAuto = true, s.VAuto);
            Item("Hide on the vertical canvas", () => s.VShow = false);
        }
        menu.Items.Add(new Separator());
        var props = new MenuItem { Header = "Properties…" };
        props.Click += (_, _) => { SourcesList.SelectedItem = s; SourceProperties_Click(this, new RoutedEventArgs()); };
        menu.Items.Add(props);
        Item("Hide", () => s.Visible = false);
        menu.IsOpen = true;
    }

    /// <summary>Anchor to a corner with the size the source has now (so the change applies live, no rebuild).</summary>
    private static void AnchorKeepingSize(StudioSource s, string anchor)
    {
        if (Anchors.Resolve(s) == Anchors.Free)
        {
            var k = SourceTypes.KindOf(s.Type);
            if (s.W > 0) s.SizePercent = Math.Round(s.W * 100, 2);
            else if (k == SourceKind.Image) s.SizePercent = 0; // natural size
        }
        s.Anchor = anchor;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // only when the preview was clicked last (lists keep their own arrow-key navigation)
        if (Keyboard.FocusedElement is not Canvas focused) return;
        var ed = ReferenceEquals(focused, VEditLayer) ? _vEditor : _hEditor;
        if (ed.Selected is null) return;
        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        (int dx, int dy) = e.Key switch
        {
            Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0)
        };
        if (dx == 0 && dy == 0) return;
        if (ed.Nudge(dx, dy))
        {
            e.Handled = true;
            _commitTimer.Stop();
            _commitTimer.Start();
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
            RedrawEditors();
            return;
        }
        SourcesList.ItemsSource = SelectedScene.Sources;
        SelectedSceneLabel.Text = SelectedScene.Name;
        foreach (var s in SelectedScene.Sources.Where(s => s.Visible && SourceTypes.IsAudio(SourceTypes.KindOf(s.Type))))
            _mixer.Add(new MixerChannel { SourceId = s.Id, Name = s.Name });
        _state.SelectedSceneId = SelectedScene.Id;
        _hEditor.Select(null); _vEditor.Select(null);
        RedrawEditors();
        Save();
        if (_ready)
        {
            string how = _state.Settings.Transition == "Cut" ? "Cut" : "Fade";
            await ApplySceneSafelyAsync(restart: false, $"{how} to \"{SelectedScene.Name}\"…", "On air: " + SelectedScene.Name);
        }
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
        SourcesList.Items.Refresh();
        if (!_ready) return;
        // The engine rebuilds only if the edit changes what's captured/composited; moves are applied live.
        await ApplySceneSafelyAsync(restart: false, "Updating scene…", "Scene updated.");
    }

    private void RefreshMixerOnly()
    {
        _mixer.Clear();
        if (SelectedScene is null) return;
        foreach (var s in SelectedScene.Sources.Where(s => s.Visible && SourceTypes.IsAudio(SourceTypes.KindOf(s.Type))))
            _mixer.Add(new MixerChannel { SourceId = s.Id, Name = s.Name });
    }

    private void ScenesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshScene();

    private void SourcesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        var s = SourcesList.SelectedItem as StudioSource;
        _hEditor.Select(s);
        _vEditor.Select(s);
    }

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
        ScenesList.SelectedItem = scene; // selection change applies it (adding a scene while running rebuilds once)
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
        RedrawEditors();
    }

    private void SourceProperties_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not StudioSource source) return;
        var props = new SourcePropertiesWindow(source, _engine.FfmpegPath) { Owner = this };
        if (props.ShowDialog() != true) return;
        SourcesList.Items.Refresh();
        SceneEdited();
        RedrawEditors();
    }

    private void SourcesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SourceProperties_Click(sender, e);

    private void SourceVisible_Click(object sender, RoutedEventArgs e) { SceneEdited(); RedrawEditors(); }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScene is null || SourcesList.SelectedItem is not StudioSource source) return;
        SelectedScene.Sources.Remove(source);
        _hEditor.Select(null); _vEditor.Select(null);
        SceneEdited();
        RedrawEditors();
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
        RedrawEditors();
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
