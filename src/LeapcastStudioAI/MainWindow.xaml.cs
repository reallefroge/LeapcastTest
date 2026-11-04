using System.Collections.ObjectModel;
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
    private double _meter, _db;
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
        DbText = post <= -59.5 ? "-∞ dB" : $"{post:0} dBFS";
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
        _state = StateStore.Load();
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
        if (!_state.Settings.AutoConfigOffered && !Environment.GetCommandLineArgs().Contains("--preview", StringComparer.OrdinalIgnoreCase))
        {
            _state.Settings.AutoConfigOffered = true;
            Save();
            if (MessageBox.Show(this, "Let Leapcast test this PC with your scene and pick the smoothest settings? It takes about a minute (keep your game open).",
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

    private VoiceCommandListener? _voice;
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
        bool want = s.VoiceClipEnabled && _engine.CanClip;
        // the mic in the scene on air; its capture is shared by every copy of it, keyed by the first one
        var scene = (_studio ? _programScene : SelectedScene) ?? SelectedScene;
        var mic = scene?.Sources.FirstOrDefault(x => SourceTypes.KindOf(x.Type) == SourceKind.Microphone && x.Visible);
        Guid? micId = null;
        if (mic is not null)
        {
            string key = PipelineBuilder.InputKey(mic);
            var rep = PipelineBuilder.AudioRepresentatives(_state.Scenes).FirstOrDefault(r => PipelineBuilder.InputKey(r) == key);
            if (rep is not null && _audio.IsCapturing(rep.Id)) micId = rep.Id;
        }
        string wantKey = want ? $"{s.VoiceClipWord.Trim().ToLowerInvariant()}|{micId}" : "";
        if (wantKey == _voiceKey) { VoiceTag.Visibility = _voice?.Running == true ? Visibility.Visible : Visibility.Collapsed; return; }
        StopVoice();
        if (!want) return;
        if (DateTime.UtcNow < _voiceRetryAfter) return;
        try
        {
            var listener = new VoiceCommandListener();
            BlockingAudioStream? feed = micId is null ? null : new BlockingAudioStream();
            if (feed is not null) _audio.SetSpeechTap(micId, feed.Write);
            listener.Heard += (text, conf) => Dispatcher.BeginInvoke(() => SaveVoiceClip(text));
            listener.Start(s.VoiceClipWord, feed);
            _voice = listener;
            _voiceKey = wantKey;
            VoiceTagText.Text = $"🎙 Say “{CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.VoiceClipWord.Trim())}”";
            VoiceTag.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            StopVoice();
            _voiceRetryAfter = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            Log("Voice clips: " + ex.Message, true);
        }
    }

    private void StopVoice()
    {
        _audio.SetSpeechTap(null, null);
        _voice?.Dispose();
        _voice = null;
        _voiceKey = "";
        VoiceTag.Visibility = Visibility.Collapsed;
    }

    private async void SaveVoiceClip(string heard)
    {
        if (_voiceSaving) return;
        _voiceSaving = true;
        var s = _state.Settings;
        try
        {
            Status($"Heard “{heard}”: saving the last {s.VoiceClipSeconds} seconds…");
            string path = await _engine.SaveClipAsync(s.VoiceClipSeconds, string.IsNullOrWhiteSpace(s.VoiceClipFolder) ? VoiceClips.DefaultFolder : s.VoiceClipFolder);
            Log($"Clip saved: {Path.GetFileName(path)}", false);
            Status($"Clip saved: {path}");
        }
        catch (Exception ex) { Log("Clip: " + ex.Message, true); }
        finally { _voiceSaving = false; }
    }

    private void ApplyStatus(EngineStatus st)
    {
        _status = st;
        UpdateVoiceClips();
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
        ReplayBufferButton.Content = st.ReplayBuffer ? "Replay buffer: on" : "Replay buffer";
        SaveReplayButton.IsEnabled = !_busy && encoding && st.Running;

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
            Log("Your PC is falling behind. Click Auto-config (top right) to find lighter settings that stay smooth.", true);
        }
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
        PerformanceText.Text = !_state.Settings.AdaptivePerformance ? "Adaptive performance is off (Settings → Connection)."
            : rung.Index == 0 ? $"Adaptive performance: full quality ({tier})."
            : $"Adaptive performance: {rung.Name.ToLowerInvariant()} ({tier}): {rung.Explanation}.";
        ReasonsList.ItemsSource = plan.Reasons.Concat(plans.Vertical is not null && vertical ? plans.Vertical.Reasons : Array.Empty<string>()).ToList();
        int enabled = _state.Settings.Outputs.Count(o => o.Enabled);
        int up = _state.Settings.UploadKbps;
        bool tooMuch = up > 0 && plans.UploadKbps > up * 0.75;
        UploadPlanText.Text = enabled == 0 ? ""
            : $"Each destination gets the full bitrate, and extra destinations cost no extra encoding (one encode, copied to each). Going live on {enabled} destination{(enabled == 1 ? "" : "s")} uploads up to {plans.UploadKbps:N0} kbps in total"
              + (up > 0 ? $" of your {up:N0} kbps{(tooMuch ? ": that's more than is safe, so lower the bitrate (Settings → Stream) or stream to fewer places." : ".")}"
                        : ". Scan your upload (Tools → Auto-config or Settings → Connection) to check it fits.");
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
        Status(_engine.Running ? "Fullscreen preview: press Esc (or double-click) to leave it." : "Start the preview to see the picture full screen. Press Esc to leave full screen.");
    }

    // ------------------------------------------------------------------ docks (show / hide panels)

    private void DocksMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        foreach (var item in DocksMenu.Items.OfType<MenuItem>())
            if (item.Tag is string tag) item.IsChecked = !_state.Settings.HiddenDocks.Contains(tag);
    }

    private void DockToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } item) return;
        _state.Settings.HiddenDocks.Remove(tag);
        if (!item.IsChecked) _state.Settings.HiddenDocks.Add(tag);
        ApplyDocks();
        Save();
    }

    private void ResetDocks_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.HiddenDocks.Clear();
        ApplyDocks();
        Save();
    }

    /// <summary>Shows/hides every panel per the Docks menu; empty side columns collapse so the preview gets the room.</summary>
    private void ApplyDocks()
    {
        var hidden = _state.Settings.HiddenDocks;
        Visibility V(string name) => hidden.Contains(name) ? Visibility.Collapsed : Visibility.Visible;
        ScenesDock.Visibility = V("Scenes");
        SourcesDock.Visibility = V("Sources");
        ControlsDock.Visibility = V("Controls");
        MixerDock.Visibility = V("Mixer");
        MultistreamDock.Visibility = V("Multistream");
        SmartStreamDock.Visibility = V("SmartStream");
        ActivityDock.Visibility = V("Activity");
        GoLiveButton.Visibility = V("GoLive");
        StudioModeButton.Visibility = V("StudioButton");
        bool scenes = !hidden.Contains("Scenes"), sources = !hidden.Contains("Sources");
        ScenesRow.Height = scenes ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        SourcesRow.Height = sources ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        ScenesDock.Margin = new Thickness(0, 0, 0, scenes && sources ? 10 : 0);
        LeftColumn.Width = scenes || sources ? new GridLength(244) : new GridLength(0);
        LeftPanel.Margin = new Thickness(0, 0, scenes || sources ? 10 : 0, 0);
        bool right = new[] { "Multistream", "SmartStream", "Activity" }.Any(d => !hidden.Contains(d));
        RightColumn.Width = right ? new GridLength(310) : new GridLength(0);
        RightPanel.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        RedrawEditors();
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
        MessageBox.Show(this, "End the stream and the recording first: switching profiles restarts Leapcast.", "Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
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
        Status(_studio ? "Studio mode: pick a scene to preview and edit it, then click Transition to put it on air." : "Studio mode off.");
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
        SourcesList.Items.Refresh();
        try { await ApplyToEngineAsync(true); } catch (Exception ex) { Log(ex.Message, true); }
        if (ok) { Status("Auto-config applied: " + (_engine.CurrentPlan ?? _engine.PlanFor(_state.Settings)).Summary); Log("Auto-config applied.", false); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(null);

    private void SettingsMenu_Click(object sender, RoutedEventArgs e) => OpenSettings((sender as FrameworkElement)?.Tag as string);

    private async void OpenSettings(string? tab)
    {
        var window = new SettingsWindow(_state.Settings, _engine, _state.Scenes, tab) { Owner = this };
        if (window.ShowDialog() != true) return;
        bool ffmpegChanged = window.Result.FfmpegPath != _state.Settings.FfmpegPath;
        window.ApplySceneHotkeys(); // scene and source hotkeys live on the scenes
        _state.Settings = window.Result;
        ApplyDocks();
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
                var answer = MessageBox.Show(this, "Apply the new settings now? Your destinations stay connected and viewers see a short pause (only a new resolution makes them reconnect for a second).",
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
            mi.Click += (_, _) => { act(); SceneEdited(); RedrawEditors(); SourcesList.Items.Refresh(); };
            menu.Items.Add(mi);
        }
        Item(s.Locked ? "Unlock (allow moving and resizing)" : "Lock (prevent moving and resizing)", () => s.Locked = !s.Locked, s.Locked);
        menu.Items.Add(new Separator());
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
        if (s.CropLeft + s.CropTop + s.CropRight + s.CropBottom > 0)
            Item("Reset crop", () => s.CropLeft = s.CropTop = s.CropRight = s.CropBottom = 0);
        if (kind == CanvasKind.Horizontal ? s.H > 0 : s.VH > 0)
            Item("Reset shape (undo stretch)", () => { if (kind == CanvasKind.Horizontal) s.H = 0; else s.VH = 0; });
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
        SourcesList.ItemsSource = SelectedScene.Sources;
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
        SourcesList.Items.Refresh();
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
                else if (sources.FirstOrDefault(s => s.Id == ch.SourceId) is { } src) ch.Load(src.VolumeDb, !src.Visible);
            }
            return;
        }
        _mixer.Clear();
        var reps = PipelineBuilder.AudioRepresentatives(_state.Scenes);
        foreach (var s in sources)
        {
            string key = PipelineBuilder.InputKey(s);
            var ch = new MixerChannel { SourceId = s.Id, Name = s.Name, MeterId = reps.FirstOrDefault(r => PipelineBuilder.InputKey(r) == key)?.Id ?? s.Id };
            ch.Load(s.VolumeDb, !s.Visible);
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
            if (src.Visible == ch.Muted) { src.Visible = !ch.Muted; SourcesList.Items.Refresh(); }
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
            foreach (var s in SelectedScene.Sources)
            {
                scene.Sources.Add(s.Copy());
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
        var picker = new SourcePickerWindow(_state.Scenes, SelectedScene) { Owner = this };
        if (picker.ShowDialog() != true) return;
        if (picker.SelectedExisting is { } existing)
        {
            // a copy with every setting: no properties dialog, it's ready to go
            var copy = existing.Copy();
            SelectedScene.Sources.Add(copy);
            SourcesList.SelectedItem = copy;
            Status($"Added {copy.Name} with the same settings as in the other scene.");
            SceneEdited();
            RedrawEditors();
            return;
        }
        if (string.IsNullOrWhiteSpace(picker.SelectedType)) return;
        var source = new StudioSource { Type = picker.SelectedType, Name = picker.SelectedType };
        if (source.Type == SourceTypes.Camera) source.Name = "Webcam";
        if (source.Type == SourceTypes.Window) source.Name = "Wizard101 window";
        if (source.Type == SourceTypes.DesktopAudio) { source.Name = "Desktop audio"; source.NoiseCleanup = false; }
        if (source.Type == SourceTypes.Microphone) source.Name = "Microphone";
        if (source.Type == SourceTypes.Browser) { source.Name = "Chat"; source.Anchor = Anchors.TopRight; source.SizePercent = 0; }

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

    private void SourceLock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StudioSource s }) Status(s.Locked ? $"{s.Name} is locked in place." : $"{s.Name} can be moved again.");
        Save();
        RedrawEditors();
    }

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
    /// <param name="secret">A password: typed into a box that hides it.</param>
    public static string? Ask(Window owner, string title, string label, string initial, bool secret = false)
    {
        var box = new TextBox { Text = initial, MinWidth = 300 };
        var pw = new PasswordBox { MinWidth = 300, Password = initial };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Background = new SolidColorBrush(Color.FromRgb(0x57, 0x48, 0xC7)) };
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
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { if (secret) pw.Focus(); else { box.Focus(); box.SelectAll(); } };
        return w.ShowDialog() == true ? (secret ? pw.Password : box.Text) : null;
    }
}
