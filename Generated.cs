namespace LeapcastStudioAI { public partial class SourcePropertiesWindow {
    internal System.Windows.Controls.TextBlock HeaderText = null!;
    internal System.Windows.Controls.TextBlock TypeText = null!;
    internal System.Windows.Controls.TextBox NameBox = null!;
    internal System.Windows.Controls.StackPanel DisplayGroup = null!;
    internal System.Windows.Controls.ComboBox MonitorBox = null!;
    internal System.Windows.Controls.StackPanel WindowGroup = null!;
    internal System.Windows.Controls.ComboBox WindowBox = null!;
    internal System.Windows.Controls.StackPanel CaptureGroup = null!;
    internal System.Windows.Controls.ComboBox MethodBox = null!;
    internal System.Windows.Controls.CheckBox CursorBox = null!;
    internal System.Windows.Controls.TextBox CropL = null!;
    internal System.Windows.Controls.TextBox CropT = null!;
    internal System.Windows.Controls.TextBox CropR = null!;
    internal System.Windows.Controls.TextBox CropB = null!;
    internal System.Windows.Controls.StackPanel CameraGroup = null!;
    internal System.Windows.Controls.ComboBox CameraBox = null!;
    internal System.Windows.Controls.TextBlock CameraHint = null!;
    internal System.Windows.Controls.ComboBox CamResBox = null!;
    internal System.Windows.Controls.ComboBox CamFpsBox = null!;
    internal System.Windows.Controls.CheckBox DenoiseBox = null!;
    internal System.Windows.Controls.CheckBox MirrorBox = null!;
    internal System.Windows.Controls.Button ClapButton = null!;
    internal System.Windows.Controls.TextBox SyncOffsetBox = null!;
    internal System.Windows.Controls.TextBlock ClapHint = null!;
    internal System.Windows.Controls.StackPanel ImageGroup = null!;
    internal System.Windows.Controls.TextBox ImageBox = null!;
    internal System.Windows.Controls.StackPanel BrowserGroup = null!;
    internal System.Windows.Controls.TextBox UrlBox = null!;
    internal System.Windows.Controls.TextBox BrowserWBox = null!;
    internal System.Windows.Controls.TextBox BrowserHBox = null!;
    internal System.Windows.Controls.ComboBox BrowserFpsBox = null!;
    internal System.Windows.Controls.TextBox CssBox = null!;
    internal System.Windows.Controls.StackPanel TextGroup = null!;
    internal System.Windows.Controls.TextBox TextContentBox = null!;
    internal System.Windows.Controls.TextBox FontSizeBox = null!;
    internal System.Windows.Controls.TextBox ColorBox = null!;
    internal System.Windows.Controls.StackPanel PlacementGroup = null!;
    internal System.Windows.Controls.ComboBox AnchorBox = null!;
    internal System.Windows.Controls.StackPanel FillGroup = null!;
    internal System.Windows.Controls.ComboBox FillBox = null!;
    internal System.Windows.Controls.TextBlock SizeLabel = null!;
    internal System.Windows.Controls.TextBlock SizeValue = null!;
    internal System.Windows.Controls.Slider SizeSlider = null!;
    internal System.Windows.Controls.TextBox MarginBox = null!;
    internal System.Windows.Controls.StackPanel AudioGroup = null!;
    internal System.Windows.Controls.ComboBox AudioDeviceBox = null!;
    internal System.Windows.Controls.TextBox VolumeBox = null!;
    internal System.Windows.Controls.CheckBox NoiseBox = null!;
    // handler SizeSlider_ValueChanged present
    // handler RefreshWindows_Click present
    // handler Ok_Click present
    // handler ClapTest_Click present
    // handler BrowseImage_Click present
    // handler Cancel_Click present
    // handler RefreshCameras_Click present
    private void InitializeComponent() { }
} }
namespace LeapcastStudioAI { public partial class OutputEditWindow {
    internal System.Windows.Controls.TextBlock HeaderText = null!;
    internal System.Windows.Controls.ComboBox ServiceBox = null!;
    internal System.Windows.Controls.TextBox NameBox = null!;
    internal System.Windows.Controls.TextBox ServerBox = null!;
    internal System.Windows.Controls.PasswordBox KeyBox = null!;
    internal System.Windows.Controls.TextBlock KeyHint = null!;
    internal System.Windows.Controls.CheckBox AskKeyBox = null!;
    internal System.Windows.Controls.ComboBox CanvasBox = null!;
    internal System.Windows.Controls.ComboBox EncodeBox = null!;
    internal System.Windows.Controls.StackPanel OwnPanel = null!;
    internal System.Windows.Controls.TextBox OwnBitrateBox = null!;
    internal System.Windows.Controls.CheckBox EnabledBox = null!;
    internal System.Windows.Controls.Button DeleteButton = null!;
    // handler Save_Click present
    // handler ServiceBox_SelectionChanged present
    // handler Delete_Click present
    // handler EncodeBox_SelectionChanged present
    private void InitializeComponent() { }
} }
namespace LeapcastStudioAI { public partial class SourcePickerWindow {
    internal System.Windows.Controls.ListBox SourceType = null!;
    // handler SourceType_MouseDoubleClick present
    // handler Cancel_Click present
    // handler Add_Click present
    private void InitializeComponent() { }
} }
namespace LeapcastStudioAI { public partial class MainWindow {
    internal System.Windows.Controls.Border EngineBadge = null!;
    internal System.Windows.Controls.TextBlock EngineBadgeText = null!;
    internal System.Windows.Controls.Border LiveTag = null!;
    internal System.Windows.Controls.TextBlock LiveTagText = null!;
    internal System.Windows.Controls.Border RecTag = null!;
    internal System.Windows.Controls.TextBlock RecTagText = null!;
    internal System.Windows.Controls.Button AutoConfigButton = null!;
    internal System.Windows.Controls.Button GoLiveButton = null!;
    internal System.Windows.Controls.ListBox ScenesList = null!;
    internal System.Windows.Controls.ListBox SourcesList = null!;
    internal System.Windows.Controls.ColumnDefinition VerticalColumn = null!;
    internal System.Windows.Controls.TextBlock SelectedSceneLabel = null!;
    internal System.Windows.Controls.TextBlock HorizontalCaption = null!;
    internal System.Windows.Controls.Viewbox HViewbox = null!;
    internal System.Windows.Controls.Grid HCanvasHost = null!;
    internal System.Windows.Controls.Image HPreviewImage = null!;
    internal System.Windows.Controls.Canvas HEditLayer = null!;
    internal System.Windows.Controls.StackPanel PreviewPlaceholder = null!;
    internal System.Windows.Controls.TextBlock PreviewHint = null!;
    internal System.Windows.Controls.Button PreviewButtonCenter = null!;
    internal System.Windows.Controls.DockPanel VerticalPanel = null!;
    internal System.Windows.Controls.TextBlock VerticalCaption = null!;
    internal System.Windows.Controls.Viewbox VViewbox = null!;
    internal System.Windows.Controls.Grid VCanvasHost = null!;
    internal System.Windows.Controls.Image VPreviewImage = null!;
    internal System.Windows.Controls.Canvas VEditLayer = null!;
    internal System.Windows.Controls.Button PreviewButton = null!;
    internal System.Windows.Controls.Button RecordButton = null!;
    internal System.Windows.Controls.Button ReplayBufferButton = null!;
    internal System.Windows.Controls.Button SaveReplayButton = null!;
    internal System.Windows.Controls.TextBlock StatFps = null!;
    internal System.Windows.Controls.TextBlock StatNow = null!;
    internal System.Windows.Controls.TextBlock StatHealth = null!;
    internal System.Windows.Controls.TextBlock MixerHint = null!;
    internal System.Windows.Controls.ItemsControl MixerList = null!;
    internal System.Windows.Controls.Button StartAllButton = null!;
    internal System.Windows.Controls.ItemsControl HorizontalOutputs = null!;
    internal System.Windows.Controls.TextBlock NoHorizontalHint = null!;
    internal System.Windows.Controls.CheckBox VerticalToggle = null!;
    internal System.Windows.Controls.ItemsControl VerticalOutputs = null!;
    internal System.Windows.Controls.TextBlock NoVerticalHint = null!;
    internal System.Windows.Controls.TextBlock UploadPlanText = null!;
    internal System.Windows.Controls.TextBlock PlanTitle = null!;
    internal System.Windows.Controls.TextBlock PlanSubtitle = null!;
    internal System.Windows.Controls.TextBlock VerticalPlanText = null!;
    internal System.Windows.Controls.TextBlock PerformanceText = null!;
    internal System.Windows.Controls.TextBlock StatAvg = null!;
    internal System.Windows.Controls.TextBlock StatSaved = null!;
    internal System.Windows.Controls.TextBlock StatTime = null!;
    internal System.Windows.Controls.Button ReasonsToggle = null!;
    internal System.Windows.Controls.ItemsControl ReasonsList = null!;
    internal System.Windows.Controls.ItemsControl ActivityList = null!;
    internal System.Windows.Controls.TextBlock HardwareLabel = null!;
    internal System.Windows.Controls.TextBlock EngineStatus = null!;
    // handler AddScene_Click present
    // handler SourceProperties_Click present
    // handler SourceVisible_Click present
    // handler ResetVertical_Click present
    // handler Window_PreviewKeyDown present
    // handler DeleteScene_Click present
    // handler ReasonsToggle_Click present
    // handler Window_Closing present
    // handler MoveSourceUp_Click present
    // handler StartAll_Click present
    // handler GoLive_Click present
    // handler EditOutput_Click present
    // handler Settings_Click present
    // handler Record_Click present
    // handler SourcesList_MouseDoubleClick present
    // handler MoveSourceDown_Click present
    // handler ReplayBuffer_Click present
    // handler SourceLock_Click present
    // handler AddOutput_Click present
    // handler Window_Loaded present
    // handler ToggleOutput_Click present
    // handler RemoveSource_Click present
    // handler RenameScene_Click present
    // handler AutoConfig_Click present
    // handler ScenesList_SelectionChanged present
    // handler Replay_Click present
    // handler SourcesList_SelectionChanged present
    // handler AddSource_Click present
    // handler VerticalToggle_Click present
    // handler Preview_Click present
    private void InitializeComponent() { }
} }
namespace LeapcastStudioAI { public partial class AutoConfigWindow {
    internal System.Windows.Controls.TextBlock Subtitle = null!;
    internal System.Windows.Controls.Button CancelButton = null!;
    internal System.Windows.Controls.Button ClapButton = null!;
    internal System.Windows.Controls.Button PrimaryButton = null!;
    internal System.Windows.Controls.ScrollViewer QuestionsPage = null!;
    internal System.Windows.Controls.ComboBox ContentBox = null!;
    internal System.Windows.Controls.ComboBox PriorityBox = null!;
    internal System.Windows.Controls.TextBox UploadBox = null!;
    internal System.Windows.Controls.TextBlock UploadHint = null!;
    internal System.Windows.Controls.CheckBox FillGameBox = null!;
    internal System.Windows.Controls.ScrollViewer ResultsPage = null!;
    internal System.Windows.Controls.ItemsControl StepsList = null!;
    internal System.Windows.Controls.Border SummaryCard = null!;
    internal System.Windows.Controls.TextBlock SummaryTitle = null!;
    internal System.Windows.Controls.ItemsControl SummaryList = null!;
    // handler Clap_Click present
    // handler Cancel_Click present
    // handler Primary_Click present
    private void InitializeComponent() { }
} }
namespace LeapcastStudioAI { public partial class SettingsWindow {
    internal System.Windows.Controls.ComboBox ContentBox = null!;
    internal System.Windows.Controls.ComboBox ProfileBox = null!;
    internal System.Windows.Controls.StackPanel CustomPanel = null!;
    internal System.Windows.Controls.ComboBox ResolutionBox = null!;
    internal System.Windows.Controls.ComboBox FrameRateBox = null!;
    internal System.Windows.Controls.TextBox BitrateBox = null!;
    internal System.Windows.Controls.ComboBox EncoderBox = null!;
    internal System.Windows.Controls.ComboBox RateBox = null!;
    internal System.Windows.Controls.ComboBox AudioBox = null!;
    internal System.Windows.Controls.CheckBox LowLatencyBox = null!;
    internal System.Windows.Controls.TextBlock PlanPreview = null!;
    internal System.Windows.Controls.TextBlock PlanReasons = null!;
    internal System.Windows.Controls.CheckBox VerticalBox = null!;
    internal System.Windows.Controls.ComboBox VerticalResBox = null!;
    internal System.Windows.Controls.ComboBox TransitionBox = null!;
    internal System.Windows.Controls.ComboBox TransitionMsBox = null!;
    internal System.Windows.Controls.TextBox UploadBox = null!;
    internal System.Windows.Controls.Button TestButton = null!;
    internal System.Windows.Controls.TextBlock TestResult = null!;
    internal System.Windows.Controls.CheckBox ReconnectBox = null!;
    internal System.Windows.Controls.CheckBox AdjustBox = null!;
    internal System.Windows.Controls.CheckBox AdaptBox = null!;
    internal System.Windows.Controls.TextBox FolderBox = null!;
    internal System.Windows.Controls.ComboBox ReplayBox = null!;
    internal System.Windows.Controls.TextBox FfmpegBox = null!;
    // handler BrowseFolder_Click present
    // handler BrowseFfmpeg_Click present
    // handler Save_Click present
    // handler OpenLogs_Click present
    // handler AnyChanged present
    // handler Test_Click present
    // handler Cancel_Click present
    private void InitializeComponent() { }
} }