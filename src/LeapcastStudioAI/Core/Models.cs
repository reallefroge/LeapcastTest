using System.Collections.ObjectModel;
using System.Text.Json;

namespace LeapcastStudioAI;

/// <summary>What the engine actually does with a source. The persisted <see cref="StudioSource.Type"/> string maps onto this.</summary>
public enum SourceKind { Display, Window, Camera, Image, Text, Browser, Microphone, DesktopAudio, Unsupported }

public static class SourceTypes
{
    public const string Display = "Display Capture";
    public const string Window = "Window Capture";
    public const string Camera = "Video Capture Device";
    public const string Image = "Image";
    public const string Text = "Text";
    public const string Microphone = "Audio Input";
    public const string DesktopAudio = "Audio Output";
    public const string Browser = "Browser";

    /// <summary>Types offered in the "Add source" picker.</summary>
    public static readonly string[] Pickable = { Window, Display, Camera, Browser, Image, Text, Microphone, DesktopAudio };

    public static SourceKind KindOf(string? type) => type switch
    {
        Display => SourceKind.Display,
        "Game Capture" => SourceKind.Display, // older saved scenes: display capture is the efficient GPU path
        Window => SourceKind.Window,
        Camera => SourceKind.Camera,
        Image => SourceKind.Image,
        Text => SourceKind.Text,
        Browser => SourceKind.Browser,
        Microphone => SourceKind.Microphone,
        DesktopAudio => SourceKind.DesktopAudio,
        _ => SourceKind.Unsupported
    };

    public static bool IsVideo(SourceKind k) => k is SourceKind.Display or SourceKind.Window or SourceKind.Camera or SourceKind.Image or SourceKind.Text or SourceKind.Browser;
    /// <summary>Has transparency and a natural pixel size (drawn as an overlay, never as the opaque base).</summary>
    public static bool IsGraphic(SourceKind k) => k is SourceKind.Image or SourceKind.Text or SourceKind.Browser;
    public static bool IsAudio(SourceKind k) => k is SourceKind.Microphone or SourceKind.DesktopAudio;
}

public static class Anchors
{
    public const string Auto = "Auto";
    public const string Fill = "Fill";
    public const string TopLeft = "Top left";
    public const string TopRight = "Top right";
    public const string BottomLeft = "Bottom left";
    public const string BottomRight = "Bottom right";
    public const string Center = "Center";
    /// <summary>Positioned by dragging on the preview (X/Y/W fractions of the canvas).</summary>
    public const string Free = "Free";
    /// <summary>How a full-screen source fills a canvas of a different shape.</summary>
    public const string FillCrop = "Crop", FillFit = "Fit", FillStretch = "Stretch";
    public static readonly string[] All = { Auto, Fill, TopLeft, TopRight, BottomLeft, BottomRight, Center, Free };

    public static string Resolve(StudioSource s)
    {
        if (!string.IsNullOrWhiteSpace(s.Anchor) && s.Anchor != Auto) return s.Anchor;
        return SourceTypes.KindOf(s.Type) switch
        {
            SourceKind.Display or SourceKind.Window => Fill,
            SourceKind.Camera => BottomRight,
            _ => TopLeft
        };
    }
}

public sealed class StudioSource
{
    /// <summary>An independent copy with every setting (device, crop, size, position, filters, hotkey) and a new
    /// id: "add existing source" into another scene without setting it up again.</summary>
    public StudioSource Copy()
    {
        var c = JsonSerializer.Deserialize<StudioSource>(JsonSerializer.Serialize(this))!;
        c.Id = Guid.NewGuid();
        return c;
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = SourceTypes.Display;
    public string Type { get; set; } = SourceTypes.Display;
    public bool Visible { get; set; } = true;
    /// <summary>Locked sources can't be moved or resized on the preview (Properties still edits them).</summary>
    public bool Locked { get; set; }
    /// <summary>Global hotkey that shows/hides this source (e.g. "Ctrl+Shift+F1"); works while the game has focus.
    /// Every source with the same hotkey (copies in other scenes) toggles together.</summary>
    public string Hotkey { get; set; } = "";

    // Placement (video sources). Sources later in the scene list are drawn on top.
    public string Anchor { get; set; } = Anchors.Auto;
    /// <summary>Width as a percentage of the output width (overlays only).</summary>
    public double SizePercent { get; set; } = 24;
    public int MarginPx { get; set; } = 24;
    /// <summary>Free placement on the horizontal canvas (fractions of canvas width/height), used when Anchor is Free.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 0.25;
    /// <summary>Free placement height (fraction of the canvas height). 0 = follow the picture's shape; set when
    /// the user stretches the source (Shift + drag on the preview).</summary>
    public double H { get; set; }
    /// <summary>"Crop" (fill the canvas, trim the edges: no bars, no stretching), "Fit" (bars) or "Stretch".</summary>
    public string FillMode { get; set; } = Anchors.FillCrop;

    // Vertical (9:16) canvas placement
    /// <summary>Shown on the vertical canvas.</summary>
    public bool VShow { get; set; } = true;
    /// <summary>Automatic vertical layout (camera on top, game below over a blurred copy of itself).</summary>
    public bool VAuto { get; set; } = true;
    public double VX { get; set; }
    public double VY { get; set; }
    public double VW { get; set; } = 1;
    /// <summary>Manual vertical-canvas height (fraction); 0 = follow the picture's shape.</summary>
    public double VH { get; set; }

    // Display / window capture
    public int MonitorIndex { get; set; }
    /// <summary>"Auto" = GPU desktop duplication (fast, works with fullscreen games); "GDI" = legacy compatible capture.</summary>
    public string CaptureMethod { get; set; } = "Auto";
    public string WindowTitle { get; set; } = "Wizard101";
    public bool CaptureCursor { get; set; } = true;
    public int CropLeft { get; set; }
    public int CropTop { get; set; }
    public int CropRight { get; set; }
    public int CropBottom { get; set; }

    // Camera
    public string DeviceName { get; set; } = "";
    public string CameraResolution { get; set; } = "1280x720";
    public int CameraFps { get; set; } = 30;
    /// <summary>"" = let the camera choose, else a dshow format tag: "vcodec=mjpeg" or "pixel_format=yuyv422".</summary>
    public string CameraFormat { get; set; } = "";
    /// <summary>Temporal denoise before encoding. Webcam sensor noise is one of the biggest bitrate wasters.</summary>
    public bool CameraDenoise { get; set; } = true;
    public bool Mirror { get; set; }
    /// <summary>Extra camera delay compensation in ms (positive = camera shown earlier), like OBS's sync offset.</summary>
    public int SyncOffsetMs { get; set; }

    // Image / text
    public string FilePath { get; set; } = "";
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 44;
    public string TextColor { get; set; } = "#FFFFFF";

    // Browser
    public string Url { get; set; } = "";
    public int BrowserWidth { get; set; } = 400;
    public int BrowserHeight { get; set; } = 600;
    public int BrowserFps { get; set; } = 15;
    /// <summary>Extra CSS injected into the page (e.g. to make a chat background transparent).</summary>
    public string BrowserCss { get; set; } = "body { background-color: rgba(0,0,0,0) !important; margin: 0; overflow: hidden; }";

    // Audio
    /// <summary>WASAPI endpoint id; empty = Windows default device.</summary>
    public string AudioDeviceId { get; set; } = "";
    public double VolumeDb { get; set; }
    public bool NoiseCleanup { get; set; } = true;
}

public sealed class StudioScene
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Scene";
    /// <summary>Global hotkey that switches to this scene (stages it in studio mode).</summary>
    public string Hotkey { get; set; } = "";
    public ObservableCollection<StudioSource> Sources { get; set; } = new();
}

/// <summary>One multistream destination (Aitum-style output row).</summary>
public sealed class StreamOutput
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Twitch";
    /// <summary>Twitch | YouTube | TikTok | Kick | Custom</summary>
    public string Service { get; set; } = "Twitch";
    public string ServerUrl { get; set; } = "rtmp://live.twitch.tv/app";
    /// <summary>Stream key encrypted for the current Windows user (DPAPI).</summary>
    public string StreamKeyProtected { get; set; } = "";
    /// <summary>"Horizontal" (main canvas) or "Vertical" (9:16 canvas).</summary>
    public string Canvas { get; set; } = "Horizontal";
    /// <summary>Included in "Start all".</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Prompt for a fresh key when starting (TikTok issues a new key for most sessions).</summary>
    public bool AskKeyEachTime { get; set; }
    /// <summary>0 = share the canvas encode (no extra GPU/CPU). Otherwise this output gets its own encode at this bitrate.</summary>
    public int OwnBitrateKbps { get; set; }

    public bool IsVertical => string.Equals(Canvas, "Vertical", StringComparison.OrdinalIgnoreCase);
}

public sealed class StudioSettings
{
    // Multistream outputs (replaces the single destination below, which is kept only to migrate old settings)
    public List<StreamOutput> Outputs { get; set; } = new();
    /// <summary>Second, 9:16 canvas for TikTok / Shorts / Reels outputs.</summary>
    public bool VerticalCanvas { get; set; }
    /// <summary>"720x1280" or "1080x1920".</summary>
    public string VerticalResolution { get; set; } = "720x1280";

    // Legacy single destination (migrated into Outputs on load)
    public string Platform { get; set; } = "Twitch";
    public string ServerUrl { get; set; } = "rtmp://live.twitch.tv/app";
    /// <summary>Stream key encrypted for the current Windows user (DPAPI). Never stored in plain text.</summary>
    public string StreamKeyProtected { get; set; } = "";

    // Smart quality
    /// <summary>"LowMotion" (Wizard101, card/turn-based games) or "HighMotion" (shooters, racing).</summary>
    public string ContentType { get; set; } = "LowMotion";
    /// <summary>Auto | DataSaver | Balanced | High | Custom</summary>
    public string QualityProfile { get; set; } = "Auto";
    public string Resolution { get; set; } = "1920 × 1080"; // Custom profile only
    public int FrameRate { get; set; } = 30;                // Custom profile only
    public int MaxBitrateKbps { get; set; } = 4500;         // Custom profile only
    /// <summary>"Auto" or an ffmpeg encoder name (h264_nvenc, h264_qsv, h264_amf, libx264).</summary>
    public string Encoder { get; set; } = "Auto";
    /// <summary>"SmartVbr" (quality-targeted, capped) or "Cbr" (constant, legacy).</summary>
    public string RateControl { get; set; } = "SmartVbr";
    public double UploadMbps { get; set; }
    public DateTime? UploadMeasuredUtc { get; set; }
    public int AudioBitrateKbps { get; set; } = 160;
    /// <summary>Quality (slower encoder settings), Balanced (default) or Performance (fastest settings).</summary>
    public string EncoderPreset { get; set; } = "Balanced";
    /// <summary>Studio-wide hotkeys: action name (<see cref="HotkeyActions"/>) → key combination.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();
    /// <summary>Panels the user switched off in the Docks menu.</summary>
    public List<string> HiddenDocks { get; set; } = new();
    public bool LowLatencyMode { get; set; }

    // Scene transitions
    /// <summary>"Fade" (crossfade) or "Cut" (instant, still no restart/blank).</summary>
    public string Transition { get; set; } = "Fade";
    public int TransitionMs { get; set; } = 500;

    // Recording / replay
    public string RecordingFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Leapcast");
    public int ReplaySeconds { get; set; } = 60;

    // Reliability
    public bool AutoReconnect { get; set; } = true;
    public bool AutoAdjustBitrate { get; set; } = true;
    /// <summary>Step to lighter settings when this PC falls behind real time (and back up when it has headroom).</summary>
    public bool AdaptivePerformance { get; set; } = true;
    /// <summary>The auto-config wizard has been offered once (first launch).</summary>
    public bool AutoConfigOffered { get; set; }
    public string FfmpegPath { get; set; } = "";

    public StudioSettings Clone() => JsonSerializer.Deserialize<StudioSettings>(JsonSerializer.Serialize(this))!;
}

public sealed class StudioState
{
    public ObservableCollection<StudioScene> Scenes { get; set; } = new();
    public StudioSettings Settings { get; set; } = new();
    public Guid? SelectedSceneId { get; set; }
}

public static class StateStore
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeapcastStudioAI");
    /// <summary>The active profile's state file (profiles/&lt;name&gt;/studio-state.json).</summary>
    private static string FilePath => ProfileStore.Default.StatePath(ProfileStore.Default.Active);

    public static StudioState Load()
    {
        try
        {
            ProfileStore.Default.MigrateLegacy();
            if (File.Exists(FilePath))
            {
                var state = JsonSerializer.Deserialize<StudioState>(File.ReadAllText(FilePath));
                if (state is not null && state.Scenes.Count > 0)
                {
                    Migrate(state);
                    return state;
                }
            }
        }
        catch { /* Corrupt settings should never prevent launch. */ }

        return ProfileStore.NewDefaultState();
    }

    /// <summary>Older versions had one destination: turn it into the first output.</summary>
    public static void Migrate(StudioState state)
    {
        var s = state.Settings;
        if (s.Outputs.Count == 0 && !string.IsNullOrEmpty(s.StreamKeyProtected))
        {
            s.Outputs.Add(new StreamOutput
            {
                Name = s.Platform, Service = Platforms.Get(s.Platform).Name, ServerUrl = s.ServerUrl,
                StreamKeyProtected = s.StreamKeyProtected, Canvas = "Horizontal", Enabled = true
            });
            s.StreamKeyProtected = "";
        }
    }

    public static void Save(StudioState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
