using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LeapcastStudioAI;

/// <summary>
/// One mixer row: an audio source of the current scene (or the master "Stream output"), with a dB fader, a mute
/// button and a post-fader meter. Changes are reported through <see cref="Changed"/> and applied live.
/// </summary>
public sealed class MixerChannel : INotifyPropertyChanged
{
    private double _meter, _db, _peak;
    private long _peakAt;
    private bool _muted;
    private string _dbText = "-∞ dB";
    /// <summary>The source (Guid.Empty for the master).</summary>
    public Guid SourceId { get; init; }
    /// <summary>Which audio feed's meter to read (copies of a device share one capture).</summary>
    public Guid MeterId { get; init; }
    public string Name { get; init; } = "";
    public bool IsMaster => SourceId == Guid.Empty;
    public FontWeight NameWeight => IsMaster ? FontWeights.SemiBold : FontWeights.Normal;
    public double Meter { get => _meter; private set { _meter = value; OnChanged(); } }
    /// <summary>The recent peak (same scale as <see cref="Meter"/>): held 1.5 s, then falls.</summary>
    public double Peak { get => _peak; private set { if (Math.Abs(_peak - value) < 0.01) return; _peak = value; OnChanged(); } }
    public string DbText { get => _dbText; private set { _dbText = value; OnChanged(); } }
    public Action<MixerChannel>? Changed;

    /// <summary>Fader level in dB (−60 … +20).</summary>
    public double Db
    {
        get => _db;
        set
        {
            value = Math.Clamp(Math.Round(value * 2) / 2, -60, 20);
            if (Math.Abs(value - _db) < 1e-9) return;
            _db = value; OnChanged(); OnChanged(nameof(GainText)); Changed?.Invoke(this);
        }
    }

    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; OnChanged(); Changed?.Invoke(this); }
    }

    public string GainText => _db <= -59.9 ? "-∞ dB" : $"{(_db > 0 ? "+" : "")}{_db:0.0} dB";

    /// <summary>Sets the fader without reporting a change (loading the scene's values).</summary>
    public void Load(double db, bool muted) { _db = Math.Clamp(db, -60, 20); _muted = muted; OnChanged(nameof(Db)); OnChanged(nameof(Muted)); OnChanged(nameof(GainText)); }

    /// <param name="db">Input level in dBFS (before the fader).</param>
    /// <param name="after">Gain after this channel's fader (the master's), in dB.</param>
    public void Update(double db, double after = 0)
    {
        double post = _muted || db <= -59.5 ? -60 : db + _db + after;
        double target = Math.Clamp(post + 60, 0, 60);
        // fast attack, slow release, like a real meter
        Meter = target > _meter ? target : Math.Max(target, _meter - 2.5);
        long now = Environment.TickCount64;
        if (target >= _peak) { Peak = target; _peakAt = now; }
        else if (now - _peakAt > 1500) Peak = Math.Max(target, _peak - 1.0);
        DbText = post <= -59.5 ? "-∞ dB" : $"{post:0} dBFS";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>One row of the multistream panel.</summary>
public sealed class OutputRow : INotifyPropertyChanged
{
    private static readonly Brush Grey = Frozen(0x5A, 0x60, 0x70), Amber = Frozen(0xFF, 0xC9, 0x78), Green = Frozen(0x6F, 0xDB, 0x9A),
        Red = Frozen(0xFF, 0x6B, 0x7A), StartBg = Gradient(0x73, 0x56, 0xFF, 0x4A, 0x7B, 0xFF), StopBg = Gradient(0xD1, 0x3C, 0x4A, 0xA8, 0x2C, 0x5C),
        Disabled = Frozen(0x1C, 0x21, 0x30);
    private static SolidColorBrush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    private static Brush Gradient(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var x = new LinearGradientBrush(Color.FromRgb(r1, g1, b1), Color.FromRgb(r2, g2, b2), new Point(0, 0), new Point(1, 1));
        x.Freeze();
        return x;
    }

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
                Detail = $"LIVE {Fmt(st.LiveFor)} · {st.Kbps:N0} kbps" + (st.BufferedSeconds > 1.5 ? $" · {st.BufferedSeconds:0}s behind" : "");
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
    private readonly PreviewEditor _hEditor, _vEditor, _sEditor;
    private IEnumerable<PreviewEditor> Editors => new[] { _hEditor, _vEditor, _sEditor };
    /// <summary>Studio mode: the scene list picks what's staged in the Preview; this is what's on air.</summary>
    private bool _studio;
    private StudioScene? _programScene;
    private WriteableBitmap? _sBitmap;
    private (int W, int H) _hCanvas = (1600, 900), _vCanvas = (720, 1280);
    private int _sizesSeen;
    private bool _syncingSelection;
    private bool _struggleTipShown;

    private StudioScene? SelectedScene => ScenesList.SelectedItem as StudioScene;

    public MainWindow()
    {
        InitializeComponent();
        _death.Died += (start, at) => Dispatcher.BeginInvoke(() => SaveDeathClip(start, at));
        _chat.Command += m => Dispatcher.BeginInvoke(() => SaveChatClip(m));
        _chat.Debug += VoiceLog;
        _death.Debug += VoiceLog;
        // CPU/RAM of Leapcast and everything it runs (encoder, capture, destinations, browser sources), every 2 s
        var resTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        resTimer.Tick += (_, _) =>
        {
            var usage = ResourceMonitor.Sample();
            SbResText.Text = $"CPU {usage.Sum(u => u.CpuPercent):0}% · RAM {usage.Sum(u => u.MemoryBytes) / 1048576.0:N0} MB";
            SbRes.ToolTip = ResourceMonitor.Describe(usage);
        };
        resTimer.Start();
        var ver = typeof(MainWindow).Assembly.GetName().Version;
        VersionLabel.Text = ver is null ? "" : $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        _state = StateStore.Load();
        CaptureExclusion.Enabled = _state.Settings.HideFromCapture;
        _engine = new StreamEngine(_audio, new WindowsPlatform(), StateStore.Folder)
        {
            KeyResolver = o => SecretStore.Unprotect(o.StreamKeyProtected),
        };

        _engine.FrameReady += OnFrame;
        _engine.StatusChanged += st => Dispatcher.BeginInvoke(() => ApplyStatus(st));
        _engine.Message += (_, e) => Dispatcher.BeginInvoke(() => Log(e.Text, e.IsError));
        _engine.ModesChanged += () => Dispatcher.BeginInvoke(() => ApplyStatus(_engine.Snapshot()));
        _engine.PerformanceChanged += _ => Dispatcher.BeginInvoke(() => { _shownPlan = null; UpdatePlanDisplay(); UpdateCanvasLayout(); });
        _audio.DeviceError += msg => Dispatcher.BeginInvoke(() => Log(msg, true));

        // in studio mode the Program isn't edited (it's on air); the staged scene is edited in the Preview
        _hEditor = MakeEditor(HEditLayer, HViewbox, CanvasKind.Horizontal, () => _hCanvas, () => _studio ? null : SelectedScene);
        _vEditor = MakeEditor(VEditLayer, VViewbox, CanvasKind.Vertical, () => _vCanvas, () => _studio ? null : SelectedScene);
        _sEditor = MakeEditor(SEditLayer, SViewbox, CanvasKind.Horizontal, () => _hCanvas, () => _studio ? SelectedScene : null);
        SelectTag(TransitionKindBox, _state.Settings.Transition == "Cut" ? "Cut|0"
            : $"Fade|{new[] { 300, 600, 1000, 2000 }.MinBy(ms => Math.Abs(ms - _state.Settings.TransitionMs))}");

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

        VersionLabel.ToolTip = $"{Environment.ProcessorCount} CPU threads · detecting encoders…";
        // Small laptop screens: fit the work area instead of hanging off the edge.
        var area = SystemParameters.WorkArea;
        if (Width > area.Width || Height > area.Height)
        {
            Width = Math.Max(MinWidth, area.Width);
            Height = Math.Max(MinHeight, area.Height);
            if (Width > area.Width || Height > area.Height) WindowState = WindowState.Maximized;
        }
        RebuildOutputRows();
        ApplyDocks();
        UpdateCanvasLayout();
        UpdatePlanDisplay();
        SizeChanged += (_, _) => RedrawEditors();
    }

    private PreviewEditor MakeEditor(Canvas layer, FrameworkElement viewbox, CanvasKind kind, Func<(int, int)> size, Func<StudioScene?> scene)
    {
        var ed = new PreviewEditor(layer, viewbox, kind)
        {
            Scene = scene,
            CanvasSize = size,
            Aspect = s => _engine.SourceAspect(s),
            NaturalSize = s => SourceTypes.KindOf(s.Type) is SourceKind.Text or SourceKind.Image ? _engine.SourceSize(s) : null,
            SourcePixels = s => _engine.CaptureSize(s),
        };
        ed.Hint += Status;
        ed.SelectionChanged += s =>
        {
            if (s is not null && SelectedScene?.FolderOf(s) is { Collapsed: true } f) { f.Collapsed = false; RefreshSourceRows(); }
            _syncingSelection = true;
            SourcesList.SelectedItem = s;
            _syncingSelection = false;
            foreach (var other in Editors.Where(o => o != ed)) other.Select(s);
        };
        ed.Moving += () => { _engine.RefreshLayout(); foreach (var other in Editors.Where(o => o != ed)) other.Redraw(); };
        ed.Committed += () => { SceneEdited(); RedrawEditors(); };
        ed.ContextMenuRequested += (s, k) => ShowSourceMenu(s, k, layer);
        ed.BackgroundMenuRequested += k => ShowPreviewMenu(k, layer);
        return ed;
    }

    // ------------------------------------------------------------------ startup / shutdown

    // ------------------------------------------------------------------ hotkeys (show / hide sources)

    private GlobalHotkeys? _hotkeys;
    private string _registeredHotkeys = "";

    /// <summary>Every hotkey in this profile: studio actions, scene switches, source show/hide.</summary>
    private Dictionary<string, Action> HotkeyTargets()
    {
        var map = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
        void Add(string? hotkey, Action act)
        {
            var k = HotkeyText.Normalize(hotkey);
            if (k.Length > 0) map.TryAdd(k, act); // the first one wins (Settings → Hotkeys warns about doubles)
        }
        var none = new RoutedEventArgs();
        foreach (var (id, hk) in _state.Settings.Hotkeys)
            Add(hk, id switch
            {
                HotkeyActions.GoLive => () => GoLive_Click(this, none),
                HotkeyActions.Record => () => Record_Click(this, none),
                HotkeyActions.SaveReplay => () => Replay_Click(this, none),
                HotkeyActions.Preview => () => Preview_Click(this, none),
                HotkeyActions.StudioMode => () => StudioMode_Click(this, none),
                HotkeyActions.Transition => () => Transition_Click(this, none),
                _ => () => { },
            });
        foreach (var sc in _state.Scenes)
        {
            var scene = sc;
            Add(sc.Hotkey, () => { ScenesList.SelectedItem = scene; }); // switches (or stages, in studio mode)
        }
        foreach (var (hk, group) in HotkeyText.Map(_state.Scenes))
            Add(hk, () =>
            {
                bool shown = HotkeyText.Toggle(group);
                Status($"{group[0].Name} {(shown ? "shown" : "hidden")} ({hk}).");
                RedrawEditors();
                SceneEdited(); // live: no rebuild, the stream doesn't notice
            });
        return map;
    }

    /// <summary>(Re)registers every hotkey when the set changed. Called on every save.</summary>
    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        var keys = HotkeyTargets().Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        string joined = string.Join("|", keys);
        if (joined == _registeredHotkeys) return;
        _registeredHotkeys = joined;
        var failed = _hotkeys.Register(keys);
        if (failed.Count > 0) Status($"Hotkey {string.Join(", ", failed)} is already used by another program; pick a different one.");
    }

    private void OnHotkey(string hotkey)
    {
        if (HotkeyTargets().TryGetValue(hotkey, out var act)) act();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _hotkeys = new GlobalHotkeys(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            _hotkeys.Pressed += OnHotkey;
            RegisterHotkeys();
        }
        catch { /* hotkeys are a convenience */ }
        LoadPanelLayout();
        RedrawEditors();
        var ffmpeg = FfmpegLocator.Find(_state.Settings.FfmpegPath);
        if (ffmpeg is null)
        {
            SetBadge("FFMPEG MISSING", "#3A2224", "#FF7A7A");
            EngineStatus.Text = "FFmpeg wasn't found. Reinstall Leapcast (the installer bundles it) or set its path in Settings → Advanced.";
            VersionLabel.ToolTip = $"{Environment.ProcessorCount} CPU threads · FFmpeg not found";
            return;
        }
        _engine.FfmpegPath = ffmpeg;
        SetBadge("DETECTING ENCODERS…", "#332F26", "#FFC978");
        EngineStatus.Text = "Testing which video encoders this PC has…";
        string? version = await FfmpegLocator.VersionAsync(ffmpeg);
        await _engine.ProbeEncodersAsync();
        var names = _engine.Encoders.Select(x => EncoderCatalog.DisplayName(x.Name) + (x.Tier == EncoderTier.Basic ? " (basic)" : ""));
        VersionLabel.ToolTip = $"{Environment.ProcessorCount} CPU threads\nEncoders: {string.Join(", ", names)}\nFFmpeg {version ?? "?"}";
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
        if (!_state.Settings.AutoConfigOffered && !Environment.GetCommandLineArgs().Contains("--preview", StringComparer.OrdinalIgnoreCase))
        {
            _state.Settings.AutoConfigOffered = true;
            Save();
            if (MessageBox.Show(this, "Run the auto-configuration wizard? It takes about a minute. Keep the game open.",
                    "Auto-config", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                AutoConfig_Click(this, new RoutedEventArgs());
        }
        // demo/automation: --demo-menu opens a menu (CI screenshot of the menu styling)
        if (Environment.GetCommandLineArgs().Contains("--demo-menu", StringComparer.OrdinalIgnoreCase) && MainMenu.Items.Count > 0)
        {
            await Task.Delay(1500);
            if (MainMenu.Items[0] is MenuItem first) first.IsSubmenuOpen = true;
        }
        // demo/automation: --preview starts the preview right away
        if (Environment.GetCommandLineArgs().Contains("--preview", StringComparer.OrdinalIgnoreCase))
        {
            try { await _engine.SetPreviewAsync(true); } catch (Exception ex) { Log(ex.Message, true); }
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        Save();
        SavePanelLayout();
        if (_closeConfirmed) { _uiTimer.Stop(); _hotkeys?.Dispose(); StopVoice(); _engine.Dispose(); return; }
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
        if (_studio && (_programScene is null || !_state.Scenes.Contains(_programScene))) _programScene = SelectedScene;
        var scene = _studio ? _programScene : SelectedScene;
        if (scene is null) return Task.CompletedTask;
        return _engine.UpdateAsync(_state.Scenes.ToList(), scene, _state.Settings, restart);
    }

    private void OnFrame(byte[] frame)
    {
        WatchForDeath(frame);
        lock (_frameLock)
        {
            if (_frame is null || _frame.Length != frame.Length) _frame = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, _frame, 0, frame.Length);
            _frameDirty = true;
        }
    }

    private void OnUiTick()
    {
        // minimized: don't spend CPU drawing a preview nobody sees
        if (WindowState == WindowState.Minimized && _fullscreens == 0) { lock (_frameLock) _frameDirty = false; return; }
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
                // frame layout: [staged scene (studio mode) | program | vertical]
                _hBitmap.WritePixels(new Int32Rect(0, 0, pf.HorizontalWidth, pf.Height), _frame, pf.Width * 4, pf.StagedWidth * 4);
                if (pf.StagedWidth > 0)
                {
                    if (_sBitmap is null || _sBitmap.PixelWidth != pf.StagedWidth || _sBitmap.PixelHeight != pf.Height)
                    {
                        _sBitmap = new WriteableBitmap(pf.StagedWidth, pf.Height, 96, 96, PixelFormats.Bgra32, null);
                        SPreviewImage.Source = _sBitmap;
                    }
                    _sBitmap.WritePixels(new Int32Rect(0, 0, pf.StagedWidth, pf.Height), _frame, pf.Width * 4, 0);
                }
                if (pf.VerticalWidth > 0)
                {
                    if (_vBitmap is null || _vBitmap.PixelWidth != pf.VerticalWidth || _vBitmap.PixelHeight != pf.Height)
                    {
                        _vBitmap = new WriteableBitmap(pf.VerticalWidth, pf.Height, 96, 96, PixelFormats.Bgra32, null);
                        VPreviewImage.Source = _vBitmap;
                    }
                    // the vertical canvas sits to the right of the horizontal one in the same frame
                    _vBitmap.WritePixels(new Int32Rect(0, 0, pf.VerticalWidth, pf.Height), _frame, pf.Width * 4, (pf.StagedWidth + pf.HorizontalWidth) * 4);
                }
                _frameDirty = false;
            }
        }
        bool running = _engine.Running;
        // master meter: the loudest input after its fader
        MixerChannel? masterCh = null;
        double loudest = -60;
        foreach (var ch in _mixer)
        {
            if (ch.IsMaster) { masterCh = ch; continue; }
            double inDb = running ? _audio.LevelDb(ch.MeterId) : -60;
            ch.Update(inDb);
            if (!ch.Muted && inDb > -59.5) loudest = Math.Max(loudest, inDb + ch.Db);
        }
        masterCh?.Update(loudest);
    }

    // ------------------------------------------------------------------ voice clips ("Clip")

    private VoiceListener? _voice;
    private IDisposable? _voiceMic;          // Windows' default mic, when the scene has none
    private string _voiceKey = "";           // what the running listener was started with (word | mic)
    private DateTime _voiceRetryAfter = DateTime.MinValue;
    private bool _voiceSaving;

    /// <summary>
    /// Listens for the clip word only while there's something to clip (live, recording or replay buffer), on the
    /// stream's own microphone (falls back to Windows' default mic when the scene has none).
    /// </summary>
    private void UpdateVoiceClips()
    {
        var s = _state.Settings;
        // the mic is transcribed for voice clips ("Clip") and for captions on clips
        bool want = (s.VoiceClipEnabled || s.ClipCaptions) && _engine.CanClip;
        // the mic in the scene on air; its capture is shared by every copy of it, keyed by the first one
        var scene = (_studio ? _programScene : SelectedScene) ?? SelectedScene;
        var mic = scene?.Sources.FirstOrDefault(x => SourceTypes.KindOf(x.Type) == SourceKind.Microphone && x.Shown);
        Guid? micId = null;
        if (mic is not null)
        {
            string key = PipelineBuilder.InputKey(mic);
            var rep = PipelineBuilder.AudioRepresentatives(_state.Scenes).FirstOrDefault(r => PipelineBuilder.InputKey(r) == key);
            if (rep is not null && _audio.IsCapturing(rep.Id)) micId = rep.Id;
        }
        // the desktop audio (game, Discord) is transcribed too, so words leaking from the speakers into the mic are dropped
        var desktop = scene?.Sources.FirstOrDefault(x => SourceTypes.KindOf(x.Type) == SourceKind.DesktopAudio);
        Guid? deskId = null;
        if (desktop is not null)
        {
            string key = PipelineBuilder.InputKey(desktop);
            var rep = PipelineBuilder.AudioRepresentatives(_state.Scenes).FirstOrDefault(r => PipelineBuilder.InputKey(r) == key);
            if (rep is not null && _audio.IsCapturing(rep.Id)) deskId = rep.Id;
        }
        string wantKey = want ? $"{s.VoiceClipWord.Trim().ToLowerInvariant()}|{micId}|{deskId}|{s.VoiceClipSensitivity}|{s.VoiceClipEnabled}" : "";
        if (wantKey == _voiceKey) { VoiceTag.Visibility = _voice?.Running == true && s.VoiceClipEnabled ? Visibility.Visible : Visibility.Collapsed; return; }
        StopVoice();
        if (!want) return;
        if (DateTime.UtcNow < _voiceRetryAfter) return;
        try
        {
            var listener = new VoiceListener
            {
                RecordingsFolder = Path.Combine(StateStore.Folder, "logs", "voice-audio"),
                TranscribeOnly = !s.VoiceClipEnabled,
            };
            listener.Transcribed += _transcript.Add;
            var feed = new BlockingAudioStream();
            if (micId is not null) _audio.SetSpeechTap(micId, feed.Write);
            listener.Heard += (text, conf, spokenUtc) =>
            {
                VoiceLog($"HEARD “{text}”, said at {spokenUtc.ToLocalTime():HH:mm:ss.fff}: saving a clip");
                Dispatcher.BeginInvoke(() => SaveVoiceClip(text, spokenUtc));
            };
            listener.Debug += VoiceLog;
            VoiceLog(s.VoiceClipEnabled
                ? $"listening for “{s.VoiceClipWord}” on {(micId is null ? "Windows' default microphone" : "the scene's microphone")}, sensitivity {s.VoiceClipSensitivity}"
                : $"transcribing {(micId is null ? "Windows' default microphone" : "the scene's microphone")} for clip captions");
            listener.Start(s.VoiceClipWord, feed, s.VoiceClipSensitivity);
            if (micId is null) _voiceMic = WasapiAudioBridge.CaptureDefaultMicForSpeech(feed.Write);
            StartDesktopTranscript(deskId);
            _voice = listener;
            _voiceKey = wantKey;
            VoiceTagText.Text = $"🎙 Say “{CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.VoiceClipWord.Trim())}”";
            VoiceTag.Visibility = s.VoiceClipEnabled ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StopVoice();
            _voiceRetryAfter = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            Log("Voice clips: " + ex.Message, true);
        }
    }

    /// <summary>Switches the voice-clip listener on/off and saves it straight away (the Settings checkbox).</summary>
    private async Task SetVoiceClipsAsync(bool on)
    {
        if (_state.Settings.VoiceClipEnabled == on) return;
        _state.Settings.VoiceClipEnabled = on;
        Save();
        VoiceLog(on ? "turned on in Settings" : "turned off in Settings");
        UpdateVoiceClips();
        try { await ApplyToEngineAsync(false); } catch (Exception ex) { Log("Voice clips: " + ex.Message, true); }
        UpdateVoiceClips();
        Status(on ? $"Voice clips on."
                  : "Voice clips off.");
    }

    private static readonly object VoiceLogLock = new();

    /// <summary>What the speech recognizer heard (logs\voice.log), to see why a word did or didn't count.</summary>
    private static void VoiceLog(string line)
    {
        try
        {
            string path = Path.Combine(StateStore.Folder, "logs", "voice.log");
            lock (VoiceLogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ desktop audio transcript (leak filter)

    private VoiceListener? _deskVoice;
    private IDisposable? _deskCapture;
    private BlockingAudioStream? _deskFeed;
    private System.Windows.Threading.DispatcherTimer? _deskPad;
    private readonly Transcript _deskTranscript = new();

    private void StartDesktopTranscript(Guid? deskId)
    {
        try
        {
            var feed = new BlockingAudioStream();
            var listener = new VoiceListener { TranscribeOnly = true };
            listener.Transcribed += _deskTranscript.Add;
            listener.Start("clip", feed, "Normal");
            if (deskId is not null) _audio.SetDesktopSpeechTap(deskId, feed.Write);
            else _deskCapture = WasapiAudioBridge.CaptureDefaultOutputForSpeech(feed.Write);
            // desktop audio only arrives while something plays: pad the gaps so sentences end on time
            _deskPad = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _deskPad.Tick += (_, _) => feed.PadSilence(DateTime.UtcNow);
            _deskPad.Start();
            feed.Write(new short[1600]); // starts the pad clock
            _deskVoice = listener;
            _deskFeed = feed;
            VoiceLog($"transcribing {(deskId is null ? "Windows' default output" : "the scene's desktop audio")} to ignore speaker sound leaking into the mic");
        }
        catch (Exception ex) { VoiceLog("desktop audio transcript unavailable: " + ex.Message); StopDesktopTranscript(); }
    }

    private void StopDesktopTranscript()
    {
        _audio.SetDesktopSpeechTap(null, null);
        _deskPad?.Stop();
        _deskPad = null;
        _deskCapture?.Dispose();
        _deskCapture = null;
        _deskVoice?.Dispose();
        _deskVoice = null;
        _deskFeed = null;
    }

    /// <summary>Waits (a few seconds at most) until the desktop transcript has caught up with <paramref name="utc"/>.</summary>
    private async Task DesktopCaughtUpAsync(DateTime utc, TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (_deskVoice is { } d && d.LastResultUtc < utc + Echo.Window && string.IsNullOrEmpty(d.Partial) is false && DateTime.UtcNow < until)
            await Task.Delay(250);
    }

    private void StopVoice()
    {
        StopDesktopTranscript();
        _audio.SetSpeechTap(null, null);
        _voiceMic?.Dispose();
        _voiceMic = null;
        _voice?.Dispose();
        _voice = null;
        _voiceKey = "";
        VoiceTag.Visibility = Visibility.Collapsed;
    }

    /// <summary>How far before the word the clip ends, so none of it is heard.</summary>
    private static readonly TimeSpan BeforeWord = TimeSpan.FromSeconds(0.25);

    private async void SaveVoiceClip(string heard, DateTime spokenUtc)
    {
        if (_voiceSaving) return;
        _voiceSaving = true;
        var s = _state.Settings;
        try
        {
            if (_deskVoice is { } desk)
            {
                // a "clip" from the game or a friend on Discord, leaking from the speakers into the mic, isn't yours
                await DesktopCaughtUpAsync(spokenUtc, TimeSpan.FromSeconds(2));
                if (Echo.FromDesktop(s.VoiceClipWord, spokenUtc, _deskTranscript.Between(spokenUtc - TimeSpan.FromSeconds(3), spokenUtc + TimeSpan.FromSeconds(3)), desk.Partial))
                {
                    VoiceLog($"ignored “{heard}”: the desktop audio said it too (speaker sound in the mic)");
                    return;
                }
            }
            int seconds = VoiceClips.ClampSeconds(s.VoiceClipSeconds);
            Status($"Saving clip ({seconds} s)…");
            var layout = _engine.CurrentClipLayout();
            var (path, vertical) = await _engine.SaveClipAsync(seconds, ClipFolder(), s.VerticalClips, spokenUtc - BeforeWord);
            ClipSaved(path, vertical ? null : layout, spokenUtc - BeforeWord);
        }
        catch (Exception ex) { Log("Clip: " + ex.Message, true); }
        finally { _voiceSaving = false; }
    }

    private string ClipFolder() =>
        string.IsNullOrWhiteSpace(_state.Settings.VoiceClipFolder) ? VoiceClips.DefaultFolder : _state.Settings.VoiceClipFolder;

    /// <summary>
    /// Every clip: turned into 9:16 for TikTok / Shorts when that's on (only the 9:16 file is kept, under the clip's
    /// name), logged, and added to this session's recap.
    /// </summary>
    private readonly Transcript _transcript = new();

    private async void ClipSaved(string path, ClipLayout? layout, DateTime? endUtc = null)
    {
        var savedUtc = DateTime.UtcNow;
        if (_state.Settings.VerticalClips && layout is not null)
        {
            Status($"Making the 9:16 clip…");
            string temp = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " (9x16).mp4");
            try
            {
                temp = await _engine.SaveVerticalAsync(path, layout);
                File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                try { File.Delete(temp); } catch { }
                Log("9:16 clip: " + ex.Message + " (kept the 16:9 clip)", true);
                VoiceLog("9:16 failed: " + ex.Message);
            }
        }
        if (_state.Settings.ClipCaptions) await AddCaptionsAsync(path, endUtc ?? savedUtc);
        VoiceLog("saved " + path);
        Log($"Clip saved: {Path.GetFileName(path)}", false);
        Status($"Clip saved: {Path.GetFileName(path)}");
        _sessionClips.Add(path);
    }

    /// <summary>Burns what was said during the clip into it (the clip ends at <paramref name="endUtc"/>).</summary>
    private async Task AddCaptionsAsync(string path, DateTime endUtc)
    {
        try
        {
            if (_engine.FfmpegPath is not { } ffmpeg) return;
            await Task.Delay(TimeSpan.FromSeconds(1.5)); // the recognizer finishes the sentence in progress
            await DesktopCaughtUpAsync(endUtc, TimeSpan.FromSeconds(6));
            if (await Captions.ProbeAsync(ffmpeg, path) is not { } info) return;
            var start = endUtc - TimeSpan.FromSeconds(info.Duration);
            var mine = _transcript.Between(start, endUtc);
            // only what you said: words the game / Discord said at the same moment leaked in through the speakers
            var spoken = Echo.Remove(mine, _deskTranscript.Between(start - Echo.Window, endUtc + Echo.Window));
            if (spoken.Count < mine.Count) VoiceLog($"captions: left out {mine.Count - spoken.Count} word(s) that came from the desktop audio");
            var captions = Captions.Build(spoken, start, info.Duration);
            VoiceLog($"captions: {captions.Count} line{(captions.Count == 1 ? "" : "s")} for {Path.GetFileName(path)}");
            if (captions.Count == 0) return;
            Status("Adding captions…");
            string temp = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " (captions).mp4");
            await Captions.BurnAsync(ffmpeg, path, temp, captions, info.W, info.H, _engine.ClipVideoArgs(info.W, info.H), info.Start);
            File.Delete(path);
            File.Move(temp, path);
        }
        catch (Exception ex) { Log("Captions: " + ex.Message + " (the clip was kept without them)", true); VoiceLog("captions failed: " + ex.Message); }
    }

    private readonly List<string> _sessionClips = new();

    // ------------------------------------------------------------------ !clip from Twitch chat

    private readonly TwitchChat _chat = new();
    private string _chatKey = "";
    private DateTime _lastChatClip = DateTime.MinValue;

    /// <summary>The Twitch channel to read: the setting, or the one a Twitch chat browser source shows.</summary>
    private string? ChatChannel() =>
        TwitchChat.ChannelFrom(_state.Settings.ChatClipChannel)
        ?? _state.Scenes.SelectMany(sc => sc.Sources)
            .Where(x => SourceTypes.KindOf(x.Type) == SourceKind.Browser && (x.Url ?? "").Contains("twitch.tv", StringComparison.OrdinalIgnoreCase))
            .Select(x => TwitchChat.ChannelFrom(x.Url)).FirstOrDefault(c => c is not null);

    /// <summary>Reads chat while clips can be taken (live or recording) and chat clips are on.</summary>
    private void UpdateChatClips()
    {
        var s = _state.Settings;
        string? channel = s.ChatClips && _engine.CanClip ? ChatChannel() : null;
        string key = channel is null ? "" : $"{channel}|{s.ChatClipCommand}|{s.ChatClipWho}";
        if (key == _chatKey) return;
        _chatKey = key;
        if (channel is null) { _chat.Stop(); return; }
        _chat.Start(channel, s.ChatClipCommand, s.ChatClipWho);
    }

    private async void SaveChatClip(ChatMessage msg)
    {
        if (DateTime.UtcNow - _lastChatClip < TimeSpan.FromSeconds(10) || !_engine.CanClip) return;
        _lastChatClip = DateTime.UtcNow;
        try
        {
            var s = _state.Settings;
            int seconds = VoiceClips.ClampSeconds(s.VoiceClipSeconds);
            Status($"{msg.User} clipped it: saving {seconds} s…");
            var layout = _engine.CurrentClipLayout();
            // they reacted to what they saw a few seconds ago (viewers watch with a delay)
            var end = msg.ReceivedUtc - TimeSpan.FromSeconds(Math.Clamp(s.ChatClipDelaySeconds, 0, 20));
            var (path, vertical) = await _engine.SaveClipAsync(seconds, ClipFolder(), s.VerticalClips, end, "chat");
            ClipSaved(path, vertical ? null : layout, end);
        }
        catch (Exception ex) { Log("Chat clip: " + ex.Message, true); }
    }

    // ------------------------------------------------------------------ post-stream recap

    private bool _wasLive;
    private DateTime _liveSinceLocal;

    /// <summary>When a stream ends with clips taken: the recap pack, built in the background.</summary>
    private async void UpdateRecap(EngineStatus st)
    {
        if (st.Live && !_wasLive) { _liveSinceLocal = DateTime.Now; _sessionClips.Clear(); }
        bool ended = _wasLive && !st.Live;
        _wasLive = st.Live;
        if (!ended || !_state.Settings.RecapEnabled || _sessionClips.Count == 0) return;
        var clips = _sessionClips.ToList();
        _sessionClips.Clear();
        var s = _state.Settings;
        string folder = Recap.FolderFor(ClipFolder(), _liveSinceLocal);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15)); // let any clip still saving finish
            Status($"Building the stream recap ({clips.Count} clip{(clips.Count == 1 ? "" : "s")})…");
            var result = await Task.Run(() => Recap.BuildAsync(_engine.FfmpegPath ?? throw new InvalidOperationException("FFmpeg not found"), clips, folder, s.RecapTitle, s.RecapSocials));
            Log($"Recap ready: {result.Folder}", false);
            Status($"Recap ready: {Path.GetFileName(result.Folder)}");
        }
        catch (Exception ex) { Log("Recap: " + ex.Message, true); }
    }

    // ------------------------------------------------------------------ Wizard101 death clips

    private readonly DeathDetector _death = new();
    private volatile ClipLayout? _deathLayout;
    private bool _deathSaving;

    /// <summary>Program frames (reader thread): the detector reads the game's health globe a few times a second.</summary>
    private void WatchForDeath(byte[] frame)
    {
        if (!_state.Settings.DeathClips || _deathLayout is not { Game: { } g } layout) return;
        var pf = _engine.PreviewFrame;
        if (frame.Length != pf.Width * pf.Height * 4 || layout.CanvasW <= 0) return;
        double sx = pf.HorizontalWidth / (double)layout.CanvasW, sy = pf.Height / (double)layout.CanvasH;
        var rect = new LayoutRect(pf.StagedWidth + g.X * sx, g.Y * sy, g.W * sx, g.H * sy);
        _death.Feed(frame, pf.Width, pf.Height, rect, DateTime.UtcNow);
    }

    private async void SaveDeathClip(DateTime fightStartUtc, DateTime deathUtc)
    {
        if (_deathSaving || !_engine.CanClip) return;
        _deathSaving = true;
        try
        {
            Status("You died: saving the fight…");
            await Task.Delay(TimeSpan.FromSeconds(3)); // a moment after the globe empties
            var end = deathUtc + TimeSpan.FromSeconds(3);
            int seconds = (int)Math.Ceiling((end - fightStartUtc).TotalSeconds);
            var layout = _engine.CurrentClipLayout();
            var (path, vertical) = await _engine.SaveClipAsync(seconds, ClipFolder(), _state.Settings.VerticalClips, end, "death");
            ClipSaved(path, vertical ? null : layout, end);
        }
        catch (Exception ex) { Log("Death clip: " + ex.Message, true); }
        finally { _deathSaving = false; }
    }

    private void ApplyStatus(EngineStatus st)
    {
        _status = st;
        // the wizard rebuilds the encoder: not while live
        AutoConfigMenu.IsEnabled = !st.Live;
        AutoConfigMenu.Header = st.Live ? "Auto-configuration wizard (" + SettingsWindow.LiveLockText + ")" : "Auto-configuration wizard…";
        UpdateVoiceClips();
        _deathLayout = _state.Settings.DeathClips && _engine.CanClip ? _engine.CurrentClipLayout() : null;
        UpdateChatClips();
        UpdateRecap(st);
        bool encoding = st.Live || st.Recording || st.ReplayBuffer;
        PreviewPlaceholder.Visibility = st.Running ? Visibility.Collapsed : Visibility.Visible;
        HPreviewImage.Visibility = st.Running ? Visibility.Visible : Visibility.Hidden;
        SPreviewImage.Visibility = st.Running && _engine.PreviewFrame.StagedWidth > 0 ? Visibility.Visible : Visibility.Hidden;
        VPreviewImage.Visibility = st.Running && _engine.PreviewFrame.VerticalWidth > 0 ? Visibility.Visible : Visibility.Hidden;

        var enabled = _state.Settings.Outputs.Where(o => o.Enabled).ToList();
        bool allLive = enabled.Count > 0 && enabled.All(o => _engine.IsOutputActive(o.Id));
        GoLiveButton.Content = st.Live ? "■  End stream" : "▶  Go live";
        GoLiveButton.Background = st.Live ? (Brush)FindResource("LiveBrush") : (Brush)FindResource("StartBrush");
        StartAllButton.Content = allLive ? "Stop all" : "Start all";
        RecordButton.Content = st.Recording ? "■  Stop rec" : "●  Record";
        PreviewButton.Content = _engine.PreviewOn ? "■  Preview" : "▶  Preview";
        VoiceClipsButton.Content = _state.Settings.VoiceClipEnabled ? "🎙  Voice clips: on" : "🎙  Voice clips: off";
        VoiceClipsButton.Opacity = _state.Settings.VoiceClipEnabled ? 1 : 0.6;

        LiveTag.Visibility = st.Live ? Visibility.Visible : Visibility.Collapsed;
        int liveCount = st.Outputs.Count(o => o.State == OutputState.Live);
        LiveTagText.Text = st.Reconnecting ? "RECONNECTING" : $"LIVE  {Fmt(st.LiveFor)}  ·  {liveCount}/{st.Outputs.Count(o => o.State != OutputState.Error)}";
        RecTag.Visibility = st.Recording ? Visibility.Visible : Visibility.Collapsed;
        RecTagText.Text = $"● REC  {Fmt(st.RecordingFor)}";

        StatNow.Text = st.Live && st.UploadKbps > 0 ? $"↑ {st.UploadKbps:N0} kbps"
            : encoding && st.RecentKbps > 0 ? $"● {st.RecentKbps:N0} kbps" : "↑ —";
        StatAvg.Text = st.Live && st.AverageKbps > 0 ? $"{st.AverageKbps:N0} kbps" : "—";
        bool cbr = _engine.CurrentPlan?.Cbr ?? false;
        StatSaved.Text = st.Live && st.AverageKbps > 0 && st.CapKbps > 0 && !cbr
            ? $"{Math.Max(0, (1 - st.AverageKbps / (st.CapKbps + _state.Settings.AudioBitrateKbps)) * 100):0}%"
            : "—";
        StatFps.Text = st.Running && st.Fps > 0 ? $"{st.Fps:0} / {_engine.CurrentPlan?.Fps ?? 30} FPS" : "— FPS";
        StatHealth.Text = st.Running || st.Reconnecting ? st.Health : "Idle";
        if (st.Health.StartsWith("Struggling") && !_struggleTipShown)
        {
            _struggleTipShown = true;
            Log("Encoder overloaded. Run Tools → Auto-configuration wizard to find lighter settings.", true);
        }
        StatHealth.Foreground = st.Health.StartsWith("Struggling") || st.Health.StartsWith("Upload") ? (Brush)FindResource("BadBrush")
            : st.Reconnecting ? (Brush)FindResource("WarnBrush")
            : st.Health.StartsWith("Excellent") ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("TextBrush");
        StatTime.Text = st.Live ? Fmt(st.LiveFor) : "—";
        UpdateStatusBar(st);
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

    private void UpdateStatusBar(EngineStatus st)
    {
        int fps = _engine.CurrentPlan?.Fps ?? _state.Settings.FrameRate;
        void Show(FrameworkElement panel, System.Windows.Shapes.Ellipse dot, TextBlock text, StatusItem item)
        {
            text.Text = item.Text;
            panel.ToolTip = string.IsNullOrEmpty(item.Tip) ? null : item.Tip;
            var brush = (Brush)FindResource(item.Level switch
            {
                StatusLevel.Good => "GoodBrush", StatusLevel.Warn => "WarnBrush", StatusLevel.Bad => "BadBrush", _ => "MutedBrush",
            });
            dot.Fill = brush;
            dot.Opacity = item.Level == StatusLevel.Off ? 0.4 : 1;
            text.Foreground = (Brush)FindResource(item.Level == StatusLevel.Off ? "MutedBrush" : "TextBrush");
        }
        Show(SbHealth, SbHealthDot, SbHealthText, StatusSummary.StreamHealth(st));
        Show(SbBitrate, SbBitrateDot, SbBitrateText, StatusSummary.Bitrate(st));
        Show(SbEncoder, SbEncoderDot, SbEncoderText, StatusSummary.Encoder(st, fps));
        Show(SbFps, SbFpsDot, SbFpsText, StatusSummary.Fps(st, fps));
        Show(SbLive, SbLiveDot, SbLiveText, StatusSummary.Live(st));
        Show(SbRec, SbRecDot, SbRecText, StatusSummary.Recording(st));
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    private string? _shownPlan;

    private void UpdatePlanDisplay()
    {
        var plans = _engine.Running && _engine.CurrentPlans is not null ? _engine.CurrentPlans : _engine.PlansFor(_state.Settings);
        var plan = plans.Main;
        string key = plan.Summary + "|" + plan.ProfileName + "|" + string.Join("|", plan.Reasons) + "|" + plans.Vertical?.Summary + "|" + plans.UploadKbps
                     + "|" + _engine.Performance.Index + "|" + _engine.Tier + "|" + _state.Settings.AdaptivePerformance;
        if (key == _shownPlan) return;
        _shownPlan = key;
        PlanTitle.Text = $"{plan.Width} × {plan.Height}  ·  {plan.Fps} FPS";
        PlanSubtitle.Text = $"{EncoderCatalog.DisplayName(plan.Encoder)} · {(plan.Cbr ? "constant" : "smart")} bitrate up to {plan.CapKbps} kbps · {plan.ProfileName}";
        bool vertical = _state.Settings.VerticalCanvas && plans.Vertical is not null;
        VerticalPlanText.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        if (vertical) VerticalPlanText.Text = $"Vertical: {plans.Vertical!.Width} × {plans.Vertical.Height} up to {plans.Vertical.CapKbps} kbps";
        var rung = _engine.Performance;
        string tier = _engine.EncodersProbed ? $"{_engine.Tier.ToString().ToLowerInvariant()}-end PC" : "this PC";
        PerformanceText.Text = !_state.Settings.AdaptivePerformance ? "Adaptive performance: off"
            : rung.Index == 0 ? $"Adaptive performance: full quality ({tier})"
            : $"Adaptive performance: {rung.Name.ToLowerInvariant()} ({tier})";
        ReasonsList.ItemsSource = plan.Reasons.Concat(plans.Vertical is not null && vertical ? plans.Vertical.Reasons : Array.Empty<string>()).ToList();
        int enabled = _state.Settings.Outputs.Count(o => o.Enabled);
        int up = _state.Settings.UploadKbps;
        bool tooMuch = up > 0 && plans.UploadKbps > up * 0.75;
        UploadPlanText.Text = enabled == 0 ? ""
            : $"Total upload: {plans.UploadKbps:N0} kbps across {enabled} destination{(enabled == 1 ? "" : "s")}"
              + (up > 0 ? $" of {up:N0} kbps available{(tooMuch ? ". Too high: lower the bitrate or use fewer destinations." : ".")}"
                        : ". Upload speed not measured (Tools → Connection test).");
        UploadPlanText.Foreground = tooMuch ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("MutedBrush");
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
        SCanvasHost.Width = _hCanvas.W; SCanvasHost.Height = _hCanvas.H;
        VCanvasHost.Width = _vCanvas.W; VCanvasHost.Height = _vCanvas.H;
        HorizontalCaption.Text = $"{(_studio ? "PROGRAM" : "HORIZONTAL")} · {_hCanvas.W}×{_hCanvas.H}";
        VerticalCaption.Text = $"VERTICAL · {_vCanvas.W}×{_vCanvas.H}";
        bool vOn = _state.Settings.VerticalCanvas && !_studio; // studio mode needs the room (the vertical stream keeps running)
        VerticalPanel.Visibility = vOn ? Visibility.Visible : Visibility.Collapsed;
        VerticalColumn.Width = vOn ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        VerticalToggle.IsChecked = vOn;
        // redraw once real source sizes arrive from the running pipeline (exact outlines)
        int seen = SelectedScene?.Sources.Count(s => _engine.SourceSize(s) is not null) ?? 0;
        if (changed || seen != _sizesSeen) { _sizesSeen = seen; RedrawEditors(); }
    }

    private void RedrawEditors()
    {
        _hEditor.Redraw();
        _sEditor.Redraw();
        if (_state.Settings.VerticalCanvas) _vEditor.Redraw();
    }

    // ------------------------------------------------------------------ fullscreen preview (right-click the preview)

    /// <summary>Right-click on an empty part of a preview.</summary>
    private int _fullscreens;

    private void ShowPreviewMenu(CanvasKind kind, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        menu.Items.Add(FullscreenMenu(kind, target));
        menu.IsOpen = true;
    }

    /// <summary>"Fullscreen preview", with one entry per monitor when there are several.</summary>
    private MenuItem FullscreenMenu(CanvasKind kind, FrameworkElement target)
    {
        bool staged = ReferenceEquals(target, SEditLayer);
        string what = kind == CanvasKind.Vertical ? "vertical canvas" : staged ? "studio-mode Preview" : _studio ? "Program" : "preview";
        var root = new MenuItem { Header = $"Fullscreen {what}" };
        var monitors = WindowsPlatform.Monitors();
        if (monitors.Count <= 1)
        {
            root.Click += (_, _) => OpenFullscreen(kind, staged, monitors.Count == 1 ? monitors[0] : null);
            return root;
        }
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var item = new MenuItem { Header = $"Monitor {i + 1}{(i == 0 ? " (primary)" : "")}: {m.W}×{m.H}" };
            item.Click += (_, _) => OpenFullscreen(kind, staged, m);
            root.Items.Add(item);
        }
        return root;
    }

    private void OpenFullscreen(CanvasKind kind, bool staged, (int X, int Y, int W, int H)? monitor)
    {
        Func<BitmapSource?> picture = kind == CanvasKind.Vertical ? () => _vBitmap : staged ? () => _sBitmap : () => _hBitmap;
        // not owned: it must stay up when the studio window is minimized on the other monitor
        var w = new FullscreenPreviewWindow(picture, monitor);
        _fullscreens++;
        w.Closed += (_, _) => _fullscreens--;
        Closed += (_, _) => { try { w.Close(); } catch { } };
        w.Show();
        Status(_engine.Running ? "Fullscreen preview: press Esc (or double-click) to leave it." : "Start the preview first. Press Esc to exit full screen.");
    }

    // ------------------------------------------------------------------ docks (show / hide panels)

    // ------------------------------------------------------------------ dockable panels

    /// <summary>The panels that can be moved, popped out, docked elsewhere or hidden.</summary>
    private IEnumerable<LayoutAnchorable> Panels() => DockManager.Layout.Descendents().OfType<LayoutAnchorable>();
    private LayoutAnchorable? Panel(string id) => Panels().FirstOrDefault(p => p.ContentId == id);

    private string LayoutPath => Path.Combine(StateStore.Folder, "panels-layout.xml");
    private string? _defaultLayout;

    /// <summary>Each panel's content and title, as designed (so a saved layout can always be filled back in).</summary>
    private readonly Dictionary<string, (object Content, string Title, bool Document)> _panelContents = new();

    private XmlLayoutSerializer Serializer()
    {
        var ser = new XmlLayoutSerializer(DockManager);
        ser.LayoutSerializationCallback += (_, e) =>
        {
            if (e.Model.ContentId is { } id && _panelContents.TryGetValue(id, out var c)) e.Content = c.Content;
            else e.Cancel = true;
        };
        return ser;
    }

    /// <summary>Restores where the panels were (Window_Loaded); remembers the built-in layout for "Reset layout".</summary>
    private void LoadPanelLayout()
    {
        DockStyle();
        foreach (var c in DockManager.Layout.Descendents().OfType<LayoutContent>())
            if (c.ContentId is { } id && c.Content is not null) _panelContents[id] = (c.Content, c.Title, c is LayoutDocument);
        try
        {
            var sw = new StringWriter();
            new XmlLayoutSerializer(DockManager).Serialize(sw);
            _defaultLayout = sw.ToString();
        }
        catch { }
        try
        {
            if (File.Exists(LayoutPath))
            {
                using var r = new StreamReader(LayoutPath);
                Serializer().Deserialize(r);
            }
        }
        catch (Exception ex) { Log("Panel layout couldn't be restored (using the default): " + ex.Message, true); RestoreDefaultLayout(); }
        AfterLayout();
        // panels hidden from the Docks menu before panels could move
        foreach (var id in _state.Settings.HiddenDocks) if (Panel(id) is { IsHidden: false } p) p.Hide();
    }

    /// <summary>After a layout is loaded: panels new since it was saved are added back, and change tracking is hooked up.</summary>
    private void AfterLayout()
    {
        foreach (var (id, c) in _panelContents)
        {
            if (c.Document || DockManager.Layout.Descendents().OfType<LayoutContent>().Any(x => x.ContentId == id)) continue;
            var a = new LayoutAnchorable { ContentId = id, Title = c.Title, Content = c.Content, CanClose = false, CanHide = true };
            a.AddToLayout(DockManager, AnchorableShowStrategy.Right);
        }
        foreach (var p in Panels())
        {
            p.PropertyChanged += (_, e) => { if (e.PropertyName is "IsVisible" or "IsHidden") SyncHiddenDocks(); };
            if (p.ContentId == "StreamInfo")
            {
                var si = p;
                si.IsSelectedChanged += (_, _) => { if (si.IsSelected) EnsureStreamInfo(); };
                si.IsActiveChanged += (_, _) => { if (si.IsActive) EnsureStreamInfo(); };
                if (si.IsSelected && !si.IsHidden) Dispatcher.InvokeAsync(EnsureStreamInfo, DispatcherPriority.Background);
            }
        }
    }

    private void SyncHiddenDocks()
    {
        var hidden = _state.Settings.HiddenDocks;
        foreach (var p in Panels())
        {
            hidden.Remove(p.ContentId);
            if (p.IsHidden) hidden.Add(p.ContentId);
        }
    }

    private void SavePanelLayout()
    {
        try
        {
            using var w = new StreamWriter(LayoutPath);
            new XmlLayoutSerializer(DockManager).Serialize(w);
        }
        catch { }
    }


    private void RestoreDefaultLayout()
    {
        if (_defaultLayout is null) return;
        try { Serializer().Deserialize(new StringReader(_defaultLayout)); } catch { }
    }

    /// <summary>The dock frames in the studio's colours (the theme's own greys don't match the rest of the app).</summary>
    private void DockStyle()
    {
        var keys = DockManager.Theme?.GetType().Assembly.GetTypes().FirstOrDefault(t => t.Name == "ResourceKeys");
        if (keys is null) return;
        SolidColorBrush B(uint argb) { var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)); b.Freeze(); return b; }
        var map = new Dictionary<string, Brush>
        {
            ["Background"] = B(0x00000000), ["PanelBorderBrush"] = B(0xFF1C2233), ["TabBackground"] = B(0xFF0E1220),
            ["ToolWindowBackground"] = B(0xE6121623), ["ToolWindowCaptionInactiveBackground"] = B(0xFF0E1220),
            ["ToolWindowCaptionActiveBackground"] = B(0xFF1D2340), ["ToolWindowCaptionActiveText"] = B(0xFFE9ECF8),
            ["ToolWindowCaptionInactiveText"] = B(0xFF9AA2BC), ["DocumentWellTabSelectedActiveBackground"] = B(0xFF2A2F55),
            ["DocumentWellTabSelectedInactiveBackground"] = B(0xFF1D2340), ["DocumentWellBackground"] = B(0x00000000),
            ["FloatingDocumentWindowBackground"] = B(0xFF0B0E18), ["FloatingToolWindowBackground"] = B(0xFF0B0E18),
            ["ToolWindowTabSelectedActiveBackground"] = B(0xFF1D2340), ["ToolWindowTabSelectedInactiveBackground"] = B(0xFF161B2E),
            ["ToolWindowTabUnselectedHoveredBackground"] = B(0xFF161B2E), ["ToolWindowTabSelectedActiveText"] = B(0xFFE9ECF8),
            ["ToolWindowTabSelectedInactiveText"] = B(0xFFC9CEE0), ["ToolWindowTabUnselectedText"] = B(0xFF8D95B0),
        };
        foreach (var (name, brush) in map)
        {
            object? key = keys.GetField(name)?.GetValue(null) ?? keys.GetProperty(name)?.GetValue(null);
            if (key is not null) DockManager.Resources[key] = brush;
        }
    }

    private void DocksMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        foreach (var item in DocksMenu.Items.OfType<MenuItem>())
            if (item.Tag is string tag)
                item.IsChecked = Panel(tag) is { } p ? !p.IsHidden : !_state.Settings.HiddenDocks.Contains(tag);
    }

    private void DockToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } item) return;
        _state.Settings.HiddenDocks.Remove(tag);
        if (!item.IsChecked) _state.Settings.HiddenDocks.Add(tag);
        if (Panel(tag) is { } p)
        {
            if (item.IsChecked) { p.Show(); p.IsActive = true; if (tag == "StreamInfo") EnsureStreamInfo(); }
            else p.Hide();
        }
        ApplyDocks();
        Save();
    }

    private void ResetDocks_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.HiddenDocks.Clear();
        foreach (var p in Panels().ToList()) if (p.IsHidden) p.Show();
        ApplyDocks();
        Save();
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.HiddenDocks.Clear();
        RestoreDefaultLayout();
        AfterLayout();
        try { File.Delete(LayoutPath); } catch { }
        ApplyDocks();
        Save();
    }

    /// <summary>The top bar's buttons follow the Docks menu (the panels hide and show themselves).</summary>
    private void ApplyDocks()
    {
        var hidden = _state.Settings.HiddenDocks;
        Visibility V(string name) => hidden.Contains(name) ? Visibility.Collapsed : Visibility.Visible;
        GoLiveButton.Visibility = V("GoLive");
        StudioModeButton.Visibility = V("StudioButton");
        RedrawEditors();
    }

    // ------------------------------------------------------------------ stream info (Twitch / YouTube web panels)

    private Microsoft.Web.WebView2.Wpf.WebView2? _infoView;
    private bool _infoStarting;

    private string StreamInfoUrl() => StreamInfoYouTube.IsChecked == true
        ? "https://studio.youtube.com/channel/UC/livestreaming"
        : ChatChannel() is { } ch ? $"https://dashboard.twitch.tv/popout/u/{ch}/stream-manager/edit-stream-info" : "https://dashboard.twitch.tv/stream-manager";

    /// <summary>Opens the web panel the first time it's shown (no browser running until then). Sign-ins are kept.</summary>
    private async void EnsureStreamInfo()
    {
        if (_infoView is not null || _infoStarting) return;
        _infoStarting = true;
        try
        {
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, Path.Combine(StateStore.Folder, "webview2-panels"));
            var view = new Microsoft.Web.WebView2.Wpf.WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 5, 7, 13) };
            StreamInfoHost.Child = view;
            await view.EnsureCoreWebView2Async(env);
            view.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; view.CoreWebView2.Navigate(e.Uri); };
            view.CoreWebView2.Navigate(StreamInfoUrl());
            _infoView = view;
        }
        catch (Exception ex)
        {
            StreamInfoHint.Text = "The web panel couldn't start (" + ex.Message + "). Use \"Open in your browser\" instead.";
        }
        finally { _infoStarting = false; }
    }

    private void StreamInfoSite_Click(object sender, RoutedEventArgs e)
    {
        if (_infoView?.CoreWebView2 is { } w) w.Navigate(StreamInfoUrl()); else EnsureStreamInfo();
    }

    private void StreamInfoReload_Click(object sender, RoutedEventArgs e)
    {
        if (_infoView?.CoreWebView2 is { } w) w.Reload(); else EnsureStreamInfo();
    }

    private void StreamInfoBrowser_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_infoView?.Source?.ToString() ?? StreamInfoUrl()) { UseShellExecute = true }); } catch { }
    }

    // ------------------------------------------------------------------ profiles

    private void ProfileMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ProfileMenu)) return;
        ProfileMenu.Items.Clear();
        var store = ProfileStore.Default;
        string active = store.Active;
        foreach (var name in store.List())
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = string.Equals(name, active, StringComparison.OrdinalIgnoreCase) };
            string n = name;
            item.Click += (_, _) => SwitchProfile(n);
            ProfileMenu.Items.Add(item);
        }
        ProfileMenu.Items.Add(new Separator());
        void Add(string header, Action act) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => act(); ProfileMenu.Items.Add(mi); }
        Add("New profile (fresh setup)…", () => NewProfile(copy: false));
        Add("Duplicate this profile…", () => NewProfile(copy: true));
        Add("Rename this profile…", RenameProfile);
        Add("Delete a profile…", DeleteProfile);
        ProfileMenu.Items.Add(new Separator());
        Add("Export this profile to a file…", ExportProfile);
        Add("Import a profile file…", ImportProfile);
        Add("Open profiles folder", () => OpenFolder(store.ProfilesFolder));
    }

    private bool BusyForProfiles()
    {
        if (!(_engine.Live || _engine.Recording)) return false;
        MessageBox.Show(this, "Stop streaming and recording before switching profiles.", "Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>Profiles swap every scene, output and setting at once: Leapcast restarts into the other one (a second).</summary>
    private void SwitchProfile(string name)
    {
        var store = ProfileStore.Default;
        if (string.Equals(name, store.Active, StringComparison.OrdinalIgnoreCase) || BusyForProfiles()) return;
        Save();
        store.SetActive(name);
        RestartApp();
    }

    private void NewProfile(bool copy)
    {
        if (BusyForProfiles()) return;
        var store = ProfileStore.Default;
        var name = TextPrompt.Ask(this, copy ? "Duplicate profile" : "New profile", "Name the profile:",
            copy ? store.Active + " copy" : $"Profile {store.List().Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Save();
            store.Create(name.Trim(), copy ? _state : null);
            store.SetActive(name.Trim());
            RestartApp();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Profiles", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void RenameProfile()
    {
        var store = ProfileStore.Default;
        string old = store.Active;
        var name = TextPrompt.Ask(this, "Rename profile", "New name:", old);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == old) return;
        try
        {
            Save();
            string oldAssets = store.AssetsFolder(old);
            store.Rename(old, name.Trim());
            // images imported with the profile live in its folder, which just moved
            string newAssets = store.AssetsFolder(name.Trim());
            foreach (var s in _state.Scenes.SelectMany(sc => sc.Sources))
                if (s.FilePath.StartsWith(oldAssets, StringComparison.OrdinalIgnoreCase)) s.FilePath = newAssets + s.FilePath[oldAssets.Length..];
            Save();
            Status($"Profile renamed to \"{name.Trim()}\".");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Profiles", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void DeleteProfile()
    {
        var store = ProfileStore.Default;
        var others = store.List().Where(n => !string.Equals(n, store.Active, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count == 0) { MessageBox.Show(this, "This is your only profile.", "Profiles"); return; }
        var name = TextPrompt.Ask(this, "Delete profile", "Type the name of the profile to delete (" + string.Join(", ", others) + "):", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var match = others.FirstOrDefault(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) { MessageBox.Show(this, "No other profile has that name.", "Profiles"); return; }
        if (MessageBox.Show(this, $"Delete the profile \"{match}\" with its scenes and settings? This can't be undone.", "Profiles",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { store.Delete(match); Status($"Profile \"{match}\" deleted."); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Profiles"); }
    }

    private void ExportProfile()
    {
        Save();
        var store = ProfileStore.Default;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export profile", FileName = ProfileStore.Safe(store.Active) + ProfilePackage.Extension,
            Filter = "Leapcast profile|*" + ProfilePackage.Extension, DefaultExt = ProfilePackage.Extension,
        };
        if (dlg.ShowDialog(this) != true) return;
        string? password = null;
        if (_state.Settings.Outputs.Any(o => SecretStore.Unprotect(o.StreamKeyProtected).Length > 0))
        {
            var pw = TextPrompt.Ask(this, "Stream keys",
                "Include your stream keys? Type a password to protect them (you'll need it when importing). Leave it empty to export everything except the keys.", "", secret: true);
            if (pw is null) return;
            password = pw.Length > 0 ? pw : null;
        }
        try
        {
            ProfilePackage.Export(_state, store.Active, dlg.FileName, SecretStore.Unprotect, password, AppVersion);
            Status($"Profile exported to {dlg.FileName}{(password is null ? " (without stream keys)" : " (stream keys password-protected)")}.");
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't export the profile: " + ex.Message, "Profiles", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ImportProfile()
    {
        if (BusyForProfiles()) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import profile", Filter = "Leapcast profile|*" + ProfilePackage.Extension };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var info = ProfilePackage.Inspect(dlg.FileName);
            var store = ProfileStore.Default;
            string name = info.Name;
            for (int i = 2; store.Exists(name); i++) name = $"{info.Name} ({i})";
            string? password = null;
            if (info.HasKeys)
            {
                password = TextPrompt.Ask(this, "Stream keys", "This profile includes stream keys. Type the password it was exported with (or leave it empty to import everything except the keys):", "", secret: true);
                if (password is null) return;
                if (password.Length == 0) password = null;
            }
            StudioState state;
            while (true)
            {
                try { state = ProfilePackage.Import(dlg.FileName, store.AssetsFolder(name), SecretStore.Protect, password); break; }
                catch (ProfilePasswordException ex)
                {
                    password = TextPrompt.Ask(this, "Stream keys", ex.Message + " Try again (or leave it empty to skip the keys):", "", secret: true);
                    if (password is null) return;
                    if (password.Length == 0) password = null;
                }
            }
            store.Write(name, state);
            if (MessageBox.Show(this, $"Imported \"{name}\" ({info.Scenes} scenes, {info.Sources} sources). Switch to it now?", "Profiles",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                SwitchProfile(name);
            else Status($"Profile \"{name}\" imported: pick it in the Profile menu.");
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't import the profile: " + ex.Message, "Profiles", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string AppVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";

    private void RestartApp()
    {
        _closeConfirmed = true;
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch { }
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ tools

    private void OpenRecordings_Click(object sender, RoutedEventArgs e) => OpenFolder(_state.Settings.RecordingFolder);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(StateStore.Folder, "logs"));

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ------------------------------------------------------------------ studio mode

    private async void StudioMode_Click(object sender, RoutedEventArgs e)
    {
        _studio = !_studio;
        if (_studio) _programScene = SelectedScene;
        else if (_programScene is not null && _state.Scenes.Contains(_programScene) && !ReferenceEquals(_programScene, SelectedScene))
        {
            // leaving studio mode: the list selection follows what's on air again
            _studio = true; // so the selection change below only stages, it doesn't switch scenes
            ScenesList.SelectedItem = _programScene;
            _studio = false;
        }
        ApplyStudioLayout();
        try { await _engine.SetStudioModeAsync(_studio, SelectedScene); }
        catch (Exception ex) { Log(ex.Message, true); }
        Status(_studio ? "Studio mode on. Pick a scene to edit, then click Transition." : "Studio mode off.");
    }

    private void ApplyStudioLayout()
    {
        StagePanel.Visibility = TransitionPanel.Visibility = _studio ? Visibility.Visible : Visibility.Collapsed;
        StageColumn.Width = _studio ? new GridLength(3.2, GridUnitType.Star) : new GridLength(0);
        StudioModeButton.Content = _studio ? "■  Studio mode" : "Studio mode";
        StudioModeMenu.IsChecked = _studio;
        UpdateCanvasLayout();
        UpdateStudioLabels();
        foreach (var ed in Editors) ed.Select(null);
        RedrawEditors();
    }

    private void UpdateStudioLabels()
    {
        StageSceneLabel.Text = SelectedScene?.Name ?? "";
        SelectedSceneLabel.Text = (_studio ? _programScene : SelectedScene)?.Name ?? "";
        HorizontalCaption.Text = $"{(_studio ? "PROGRAM" : "HORIZONTAL")} · {_hCanvas.W}×{_hCanvas.H}";
    }

    private async void Transition_Click(object sender, RoutedEventArgs e)
    {
        if (!_studio || SelectedScene is null) return;
        if (ReferenceEquals(_programScene, SelectedScene)) { Status($"\"{SelectedScene.Name}\" is already on air."); return; }
        _programScene = SelectedScene;
        UpdateStudioLabels();
        string how = _state.Settings.Transition == "Cut" ? "Cut" : "Fade";
        await ApplySceneSafelyAsync(restart: false, $"{how} to \"{SelectedScene.Name}\"…", "On air: " + SelectedScene.Name);
    }

    private void TransitionKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TransitionKindBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return; // not while restoring it
        var parts = tag.Split('|');
        _state.Settings.Transition = parts[0];
        if (int.TryParse(parts[1], out var ms) && ms > 0) _state.Settings.TransitionMs = ms;
        Save();
        if (_ready) _ = ApplyToEngineAsync(restart: false); // the engine reads the transition from its settings
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem ci && (ci.Tag as string) == tag) { box.SelectedItem = ci; return; }
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
        GoLiveButton.IsEnabled = AutoConfigMenu.IsEnabled = StartAllButton.IsEnabled = RecordButton.IsEnabled = PreviewButton.IsEnabled = PreviewButtonCenter.IsEnabled =
            HorizontalOutputs.IsEnabled = VerticalOutputs.IsEnabled = on;
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

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The YouTube API when the broadcast picker is on and a Google client is set up (Settings → Connection).</summary>
    private YouTubeApi? YouTube()
    {
        var s = _state.Settings;
        if (!s.YouTubePickBroadcast || string.IsNullOrWhiteSpace(s.YouTubeClientId)) return null;
        return new YouTubeApi(Http, s.YouTubeClientId, SecretStore.Unprotect(s.YouTubeClientSecretProtected), SecretStore.Unprotect(s.YouTubeRefreshProtected))
        {
            RefreshTokenChanged = r => Dispatcher.BeginInvoke(() => { _state.Settings.YouTubeRefreshProtected = SecretStore.Protect(r); Save(); }),
        };
    }

    /// <summary>Once the video reaches YouTube, a broadcast that doesn't start by itself is switched to live.</summary>
    private async Task MakeBroadcastLiveAsync(StreamOutput o, YouTubePick pick)
    {
        if (pick is not { Broadcast: { } b, Ingest: { } ing } || b.AutoStart || b.LifeCycle == "live") return;
        try
        {
            var api = YouTube();
            if (api is null) return;
            string what = await api.GoLiveWhenReadyAsync(b, ing.StreamId, TimeSpan.FromSeconds(90), CancellationToken.None);
            Log($"{o.Name}: \"{b.Title}\" {what}.", false);
        }
        catch (Exception ex) { Log($"{o.Name}: couldn't switch \"{b.Title}\" to live ({ex.Message}). Start it in YouTube Studio.", true); }
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
        var picks = new List<(StreamOutput Output, YouTubePick Pick)>();
        foreach (var o in outputs)
        {
            if (o.Service == "YouTube" && YouTube() is { } api)
            {
                // which broadcast: a scheduled one, the one already live, or a new one
                var pick = YouTubePicker.Show(this, api, o.Name);
                if (pick is null) { Log($"{o.Name}: not started (no broadcast chosen).", false); continue; }
                if (pick is { Ingest: { } ing, Broadcast: { } bc })
                {
                    if (!string.Equals(o.ServerUrl.Trim().TrimEnd('/'), ing.ServerUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    {
                        o.ServerUrl = ing.ServerUrl;
                        Save();
                    }
                    requests.Add((o.Id, ing.StreamKey));
                    picks.Add((o, pick));
                    Log($"{o.Name}: streaming to \"{bc.Title}\" ({bc.When}).", false);
                    continue;
                }
            }
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
            foreach (var (o, pick) in picks) _ = MakeBroadcastLiveAsync(o, pick);
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

    /// <summary>The one switch for clipping: recent footage is always kept while live, so there's no buffer to start.</summary>
    private async void VoiceClipsButton_Click(object sender, RoutedEventArgs e)
    {
        await SetVoiceClipsAsync(!_state.Settings.VoiceClipEnabled);
        VoiceClipsButton.Content = _state.Settings.VoiceClipEnabled ? "🎙  Voice clips: on" : "🎙  Voice clips: off";
        VoiceClipsButton.Opacity = _state.Settings.VoiceClipEnabled ? 1 : 0.6;
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

    private async void AutoConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) { MessageBox.Show(this, EngineStatus.Text, "Engine not ready", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_engine.Live)
        {
            MessageBox.Show(this, "End the stream first: the test needs your PC to itself for about a minute.", "Auto-config", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var w = new AutoConfigWindow(_engine, _state) { Owner = this };
        bool ok = w.ShowDialog() == true && w.Result is not null;
        if (ok) _state.Settings = w.Result!;
        Save();
        _shownPlan = null;
        UpdatePlanDisplay();
        UpdateCanvasLayout();
        RedrawEditors();
        RefreshSourceRows();
        try { await ApplyToEngineAsync(true); } catch (Exception ex) { Log(ex.Message, true); }
        if (ok) { Status("Auto-config applied: " + (_engine.CurrentPlan ?? _engine.PlanFor(_state.Settings)).Summary); Log("Auto-config applied.", false); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(null);

    private void SettingsMenu_Click(object sender, RoutedEventArgs e) => OpenSettings((sender as FrameworkElement)?.Tag as string);

    private async void OpenSettings(string? tab)
    {
        var window = new SettingsWindow(_state.Settings, _engine, _state.Scenes, tab) { Owner = this };
        window.VoiceClipToggled += on => _ = SetVoiceClipsAsync(on);
        if (window.ShowDialog() != true) return;
        bool ffmpegChanged = window.Result.FfmpegPath != _state.Settings.FfmpegPath;
        window.ApplySceneHotkeys(); // scene and source hotkeys live on the scenes
        _state.Settings = window.Result;
        CaptureExclusion.Enabled = _state.Settings.HideFromCapture;
        ApplyDocks();
        Save();
        _shownPlan = null;
        UpdatePlanDisplay();
        UpdateCanvasLayout();
        if (ffmpegChanged && !_engine.Running)
            MessageBox.Show(this, "Restart Leapcast to use the new FFmpeg location.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        if (_engine.Live)
        {
            // what would restart the stream was locked in the dialog: the rest applies live, the stream isn't touched
            await Guarded("Applying settings…", () => ApplyToEngineAsync(false), "Settings applied.");
        }
        else if (_engine.Running)
        {
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
            mi.Click += (_, _) => { act(); SceneEdited(); RedrawEditors(); RefreshSourceRows(); };
            menu.Items.Add(mi);
        }
        Item(s.Locked ? "Unlock" : "Lock", () => s.Locked = !s.Locked, s.Locked);
        if (SelectedScene is { } scene)
        {
            var folders = scene.Sources.Where(x => x.IsFolder).ToList();
            var current = scene.FolderOf(s);
            if (folders.Any(f => !ReferenceEquals(f, current)))
            {
                var into = new MenuItem { Header = "Move to folder" };
                foreach (var f in folders.Where(f => !ReferenceEquals(f, current)))
                {
                    var dest = f;
                    var mi = new MenuItem { Header = dest.Name };
                    mi.Click += (_, _) => { scene.MoveToFolder(s, dest); RefreshSourceRows(); SceneEdited(); RedrawEditors(); };
                    into.Items.Add(mi);
                }
                menu.Items.Add(into);
            }
            if (current is not null) Item($"Remove from \"{current.Name}\"", () => scene.MoveToFolder(s, null));
        }
        menu.Items.Add(new Separator());
        if (!SourceTypes.IsVideo(SourceTypes.KindOf(s.Type)))
        {
            // audio: nothing to place on a canvas
            var p = new MenuItem { Header = "Properties…" };
            p.Click += (_, _) => { SourcesList.SelectedItem = s; SourceProperties_Click(this, new RoutedEventArgs()); };
            menu.Items.Add(p);
            Item(s.Visible ? "Mute (hide)" : "Unmute (show)", () => s.Visible = !s.Visible);
            menu.IsOpen = true;
            return;
        }
        if (kind == CanvasKind.Horizontal)
        {
            var k = SourceTypes.KindOf(s.Type);
            bool canFill = k is SourceKind.Display or SourceKind.Window or SourceKind.Camera or SourceKind.Image;
            if (canFill) Item("Fill the screen", () => s.Anchor = Anchors.Fill, Anchors.Resolve(s) == Anchors.Fill);
            foreach (var a in new[] { Anchors.TopLeft, Anchors.TopRight, Anchors.BottomLeft, Anchors.BottomRight, Anchors.Center })
            {
                string anchor = a;
                Item("Align " + anchor.ToLowerInvariant(), () => AnchorKeepingSize(s, anchor), Anchors.Resolve(s) == anchor);
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
        if (s.CropLeft + s.CropTop + s.CropRight + s.CropBottom > 0)
            Item("Reset crop", () => s.CropLeft = s.CropTop = s.CropRight = s.CropBottom = 0);
        if (kind == CanvasKind.Horizontal ? s.H > 0 : s.VH > 0)
            Item("Reset shape", () => { if (kind == CanvasKind.Horizontal) s.H = 0; else s.VH = 0; });
        menu.Items.Add(FullscreenMenu(kind, target));
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
        var ed = ReferenceEquals(focused, VEditLayer) ? _vEditor : ReferenceEquals(focused, SEditLayer) ? _sEditor : _hEditor;
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
        RegisterHotkeys();
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
        RefreshSourceRows();
        SelectedSceneLabel.Text = SelectedScene.Name;
        RebuildMixer();
        _state.SelectedSceneId = SelectedScene.Id;
        foreach (var ed in Editors) ed.Select(null);
        RedrawEditors();
        Save();
        if (_studio)
        {
            // studio mode: the selected scene is staged in the Preview; nothing changes on air
            UpdateStudioLabels();
            if (_ready) _engine.StageScene(SelectedScene);
            return;
        }
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
        RefreshSourceRows();
        if (!_ready) return;
        // The engine rebuilds only if the edit changes what's captured/composited; moves are applied live.
        await ApplySceneSafelyAsync(restart: false, "Updating scene…", "Scene updated.");
    }

    private void RefreshMixerOnly() => RebuildMixer();

    // ------------------------------------------------------------------ audio mixer (dB faders, live)

    private DispatcherTimer? _mixerSave;

    /// <summary>
    /// One row per audio source of the selected scene (muted ones too) plus the master "Stream output". Rebuilt only
    /// when the set of sources changes, so a fader being dragged is never replaced under the mouse.
    /// </summary>
    private void RebuildMixer()
    {
        var sources = SelectedScene?.Sources.Where(s => SourceTypes.IsAudio(SourceTypes.KindOf(s.Type))).ToList() ?? new();
        var ids = sources.Select(s => s.Id).Append(Guid.Empty).ToList();
        if (_mixer.Select(c => c.SourceId).SequenceEqual(ids))
        {
            foreach (var ch in _mixer)
            {
                if (ch.IsMaster) ch.Load(_state.Settings.MasterVolumeDb, false);
                else if (sources.FirstOrDefault(s => s.Id == ch.SourceId) is { } src) ch.Load(src.VolumeDb, !src.Shown);
            }
            return;
        }
        _mixer.Clear();
        var reps = PipelineBuilder.AudioRepresentatives(_state.Scenes);
        foreach (var s in sources)
        {
            string key = PipelineBuilder.InputKey(s);
            var ch = new MixerChannel { SourceId = s.Id, Name = s.Name, MeterId = reps.FirstOrDefault(r => PipelineBuilder.InputKey(r) == key)?.Id ?? s.Id };
            ch.Load(s.VolumeDb, !s.Shown);
            ch.Changed = OnMixerChanged;
            _mixer.Add(ch);
        }
        if (sources.Count > 0)
        {
            var master = new MixerChannel { SourceId = Guid.Empty, Name = "Stream output" };
            master.Load(_state.Settings.MasterVolumeDb, false);
            master.Changed = OnMixerChanged;
            _mixer.Add(master);
        }
    }

    /// <summary>A fader or mute button moved: apply it to the running stream right away, save a moment later.</summary>
    private void OnMixerChanged(MixerChannel ch)
    {
        if (ch.IsMaster) _state.Settings.MasterVolumeDb = ch.Db;
        else if (SelectedScene?.Sources.FirstOrDefault(s => s.Id == ch.SourceId) is { } src)
        {
            src.VolumeDb = ch.Db;
            if (src.Visible == ch.Muted) { src.Visible = !ch.Muted; RefreshSourceRows(); }
        }
        _engine.SetAudioLevels(_state.Settings.MasterVolumeDb); // live: gain commands, no rebuild
        if (_mixerSave is null)
        {
            _mixerSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _mixerSave.Tick += (_, _) => { _mixerSave.Stop(); Save(); };
        }
        _mixerSave.Stop();
        _mixerSave.Start();
    }

    private void MixerMute_Click(object sender, RoutedEventArgs e) { /* the binding updates the channel; OnMixerChanged applies it */ }

    private void MixerFader_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MixerChannel ch }) ch.Db = 0;
    }

    private void ScenesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshScene();

    private void SourcesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        var s = SourcesList.SelectedItem as StudioSource;
        foreach (var ed in Editors) ed.Select(s);
    }

    private void AddScene_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPrompt.Ask(this, "Add scene", "Name the new scene:", $"Scene {_state.Scenes.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        var scene = new StudioScene { Name = name.Trim() };
        // Start new scenes with the same capture setup as the current one: handy for "Starting soon" / "BRB" variants.
        if (SelectedScene is not null)
        {
            foreach (var copy in StudioScene.CopyAll(SelectedScene.Sources)) scene.Sources.Add(copy);
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
        var picker = new SourcePickerWindow(_state.Scenes, SelectedScene) { Owner = this };
        if (picker.ShowDialog() != true) return;
        var into = TargetFolder();
        if (picker.SelectedExisting is { } existing)
        {
            // a copy with every setting (a folder with everything in it): no properties dialog, it's ready to go
            var from = _state.Scenes.FirstOrDefault(sc => sc.Sources.Contains(existing));
            var originals = existing.IsFolder && from is not null ? from.ChildrenOf(existing).Append(existing) : new[] { existing };
            var copies = StudioScene.CopyAll(originals);
            var head = copies[^1];
            foreach (var c in copies) SelectedScene.Sources.Add(c);
            if (!head.IsFolder && into is not null) SelectedScene.MoveToFolder(head, into);
            RefreshSourceRows();
            SourcesList.SelectedItem = head;
            Status($"Added {head.Name}.");
            SceneEdited();
            RedrawEditors();
            return;
        }
        if (string.IsNullOrWhiteSpace(picker.SelectedType)) return;
        if (picker.SelectedType == SourceTypes.Folder) { AddFolder(); return; }
        var source = new StudioSource { Type = picker.SelectedType, Name = picker.SelectedType };
        if (source.Type == SourceTypes.Camera) source.Name = "Webcam";
        if (source.Type == SourceTypes.Window) source.Name = "Wizard101 window";
        if (source.Type == SourceTypes.DesktopAudio) { source.Name = "Desktop audio"; source.NoiseCleanup = false; }
        if (source.Type == SourceTypes.Microphone) source.Name = "Microphone";
        if (source.Type == SourceTypes.Browser) { source.Name = "Chat"; source.Anchor = Anchors.TopRight; source.SizePercent = 0; }

        var props = new SourcePropertiesWindow(source, _engine.FfmpegPath) { Owner = this };
        if (props.ShowDialog() != true) return;
        SelectedScene.Sources.Add(source);
        if (into is not null) SelectedScene.MoveToFolder(source, into);
        RefreshSourceRows();
        SourcesList.SelectedItem = source;
        SceneEdited();
        RedrawEditors();
    }

    private void SourceProperties_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not StudioSource source) return;
        if (source.IsFolder) { RenameFolder(source); return; }
        var props = new SourcePropertiesWindow(source, _engine.FfmpegPath) { Owner = this };
        if (props.ShowDialog() != true) return;
        RefreshSourceRows();
        SceneEdited();
        RedrawEditors();
    }

    private void SourcesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SourceProperties_Click(sender, e);

    private void SourceVisible_Click(object sender, RoutedEventArgs e)
    {
        SelectedScene?.ApplyFolders();
        SceneEdited();
        RedrawEditors();
    }

    private void SourceLock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StudioSource s }) Status(s.Locked ? $"{s.Name} locked." : $"{s.Name} unlocked.");
        Save();
        RedrawEditors();
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScene is null || SourcesList.SelectedItem is not StudioSource source) return;
        if (source.IsFolder)
        {
            int n = SelectedScene.ChildrenOf(source).Count;
            if (n > 0)
            {
                var answer = MessageBox.Show(this, $"Also delete the {(n == 1 ? "source" : $"{n} sources")} in \"{source.Name}\"?\n\nYes: delete the folder and its contents.\nNo: delete only the folder.",
                    "Delete folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return;
                SelectedScene.RemoveFolder(source, keepContents: answer == MessageBoxResult.No);
            }
            else SelectedScene.RemoveFolder(source, keepContents: true);
        }
        else SelectedScene.Sources.Remove(source);
        _hEditor.Select(null); _vEditor.Select(null);
        SceneEdited();
        RedrawEditors();
    }

    private void MoveSourceUp_Click(object sender, RoutedEventArgs e) => MoveSource(up: true);
    private void MoveSourceDown_Click(object sender, RoutedEventArgs e) => MoveSource(up: false);

    /// <summary>Up = toward the front (the top of the list is drawn on top).</summary>
    private void MoveSource(bool up)
    {
        if (SelectedScene is null || SourcesList.SelectedItem is not StudioSource source) return;
        SelectedScene.MoveInList(source, up);
        RefreshSourceRows();
        SourcesList.SelectedItem = source;
        SceneEdited();
        RedrawEditors();
    }

    // ------------------------------------------------------------------ sources list: rows, folders, drag and drop

    /// <summary>
    /// The list shows the front first (the reverse of the drawing order), each folder followed by its contents,
    /// indented (unless it's folded).
    /// </summary>
    private void RefreshSourceRows()
    {
        var scene = SelectedScene;
        if (scene is null) { SourcesList.ItemsSource = null; return; }
        scene.ApplyFolders();
        var rows = new List<StudioSource>();
        foreach (var block in Enumerable.Reverse(scene.Blocks()))
        {
            var head = block[^1];
            head.ListIndent = 0;
            if (head.IsFolder)
            {
                var kids = block.Take(block.Count - 1).ToList();
                head.ListDetail = kids.Count == 0 ? "Empty folder" : kids.Count == 1 ? "1 source" : $"{kids.Count} sources";
                rows.Add(head);
                if (head.Collapsed) continue;
                foreach (var k in Enumerable.Reverse(kids)) { k.ListIndent = 22; k.ListDetail = k.Type; rows.Add(k); }
            }
            else { head.ListDetail = head.Type; rows.Add(head); }
        }
        var selected = SourcesList.SelectedItem;
        _syncingSelection = true;
        SourcesList.ItemsSource = rows;
        if (selected is StudioSource sel && rows.Contains(sel)) SourcesList.SelectedItem = sel;
        _syncingSelection = false;
    }

    /// <summary>Where a new source goes: the selected folder, or the folder of the selected source.</summary>
    private StudioSource? TargetFolder() =>
        SourcesList.SelectedItem is StudioSource s && SelectedScene is { } sc ? (s.IsFolder ? s : sc.FolderOf(s)) : null;

    private void AddFolder_Click(object sender, RoutedEventArgs e) => AddFolder();

    private void AddFolder()
    {
        if (SelectedScene is null) return;
        int n = 1;
        while (SelectedScene.Sources.Any(x => x.IsFolder && x.Name == $"Folder {n}")) n++;
        var name = TextPrompt.Ask(this, "New folder", "Folder name:", $"Folder {n}");
        if (string.IsNullOrWhiteSpace(name)) return;
        var folder = new StudioSource { Type = SourceTypes.Folder, Name = name.Trim() };
        SelectedScene.Sources.Add(folder); // top of the list
        RefreshSourceRows();
        SourcesList.SelectedItem = folder;
        Status($"Added folder \"{folder.Name}\". Drag sources onto it.");
        SceneEdited();
        RedrawEditors();
    }

    private void RenameFolder(StudioSource folder)
    {
        var name = TextPrompt.Ask(this, "Rename folder", "Folder name:", folder.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        folder.Name = name.Trim();
        RefreshSourceRows();
        Save();
        RedrawEditors();
    }

    private void FolderCollapse_Click(object sender, RoutedEventArgs e)
    {
        Save();
        RefreshSourceRows();
    }

    private Point _dragStart;
    private StudioSource? _dragRow;

    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private StudioSource? RowAt(DependencyObject? d) => Ancestor<ListBoxItem>(d)?.DataContext as StudioSource;

    private void SourcesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // the eye, lock and fold buttons are clicks, not drags
        _dragRow = Ancestor<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource as DependencyObject) is null ? RowAt(e.OriginalSource as DependencyObject) : null;
        _dragStart = e.GetPosition(SourcesList);
    }

    private void SourcesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(SourcesList);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow;
        _dragRow = null;
        DragDrop.DoDragDrop(SourcesList, new DataObject(typeof(StudioSource), row), DragDropEffects.Move);
    }

    private void SourcesList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(StudioSource)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropped onto a folder: into it. Onto a source: directly above it (its folder, if it's in one). Below the rows: to the bottom.</summary>
    private void SourcesList_Drop(object sender, DragEventArgs e)
    {
        if (SelectedScene is not { } scene || e.Data.GetData(typeof(StudioSource)) is not StudioSource moving) return;
        var target = RowAt(e.OriginalSource as DependencyObject);
        if (ReferenceEquals(target, moving)) return;
        if (target is null) scene.PlaceAtBottom(moving);
        else if (target.IsFolder && !moving.IsFolder) scene.MoveToFolder(moving, target);
        else scene.PlaceAbove(moving, target);
        RefreshSourceRows();
        SourcesList.SelectedItem = moving;
        SceneEdited();
        RedrawEditors();
    }

    private void SourcesList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource as DependencyObject) is not { } s) return;
        SourcesList.SelectedItem = s;
        if (s.IsFolder) ShowFolderMenu(s, SourcesList);
        else ShowSourceMenu(s, CanvasKind.Horizontal, SourcesList);
        e.Handled = true;
    }

    private void ShowFolderMenu(StudioSource folder, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        void Item(string header, Action act, bool isChecked = false)
        {
            var mi = new MenuItem { Header = header, IsChecked = isChecked };
            mi.Click += (_, _) => { act(); SelectedScene?.ApplyFolders(); SceneEdited(); RedrawEditors(); };
            menu.Items.Add(mi);
        }
        Item("Rename…", () => RenameFolder(folder));
        Item(folder.Visible ? "Hide contents" : "Show contents", () => folder.Visible = !folder.Visible);
        Item(folder.Locked ? "Unlock" : "Lock", () => folder.Locked = !folder.Locked, folder.Locked);
        menu.Items.Add(new Separator());
        Item("Ungroup (keep sources)", () => SelectedScene?.RemoveFolder(folder, keepContents: true));
        var del = new MenuItem { Header = "Delete…" };
        del.Click += (_, _) => { SourcesList.SelectedItem = folder; RemoveSource_Click(this, new RoutedEventArgs()); };
        menu.Items.Add(del);
        menu.IsOpen = true;
    }
}

/// <summary>Minimal themed text prompt (replaces the WinForms-only VB InputBox).</summary>
public static class TextPrompt
{
    /// <param name="secret">A password: typed into a box that hides it.</param>
    public static string? Ask(Window owner, string title, string label, string initial, bool secret = false)
    {
        var box = new TextBox { Text = initial, MinWidth = 300 };
        var pw = new PasswordBox { MinWidth = 300, Password = initial };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(3, 0, 3, 6), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
        panel.Children.Add(secret ? pw : box);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Owner = owner, Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.FindResource("BgBrush"),
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { if (secret) pw.Focus(); else { box.Focus(); box.SelectAll(); } };
        return w.ShowDialog() == true ? (secret ? pw.Password : box.Text) : null;
    }
}
