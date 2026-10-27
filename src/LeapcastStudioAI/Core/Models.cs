using System.Collections.ObjectModel;
using System.Text.Json;

namespace LeapcastStudioAI;

/// <summary>What the engine actually does with a source. The persisted <see cref="StudioSource.Type"/> string maps onto this.</summary>
public enum SourceKind { Display, Window, Camera, Image, Text, Microphone, DesktopAudio, Unsupported }

public static class SourceTypes
{
    public const string Display = "Display Capture";
    public const string Window = "Window Capture";
    public const string Camera = "Video Capture Device";
    public const string Image = "Image";
    public const string Text = "Text";
    public const string Microphone = "Audio Input";
    public const string DesktopAudio = "Audio Output";

    /// <summary>Types offered in the "Add source" picker.</summary>
    public static readonly string[] Pickable = { Display, Window, Camera, Image, Text, Microphone, DesktopAudio };

    public static SourceKind KindOf(string? type) => type switch
    {
        Display => SourceKind.Display,
        "Game Capture" => SourceKind.Display, // older saved scenes: display capture is the efficient GPU path
        Window => SourceKind.Window,
        Camera => SourceKind.Camera,
        Image => SourceKind.Image,
        Text => SourceKind.Text,
        Microphone => SourceKind.Microphone,
        DesktopAudio => SourceKind.DesktopAudio,
        _ => SourceKind.Unsupported
    };

    public static bool IsVideo(SourceKind k) => k is SourceKind.Display or SourceKind.Window or SourceKind.Camera or SourceKind.Image or SourceKind.Text;
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
    public static readonly string[] All = { Auto, Fill, TopLeft, TopRight, BottomLeft, BottomRight, Center };

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
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = SourceTypes.Display;
    public string Type { get; set; } = SourceTypes.Display;
    public bool Visible { get; set; } = true;

    // Placement (video sources). Sources later in the scene list are drawn on top.
    public string Anchor { get; set; } = Anchors.Auto;
    /// <summary>Width as a percentage of the output width (overlays only).</summary>
    public double SizePercent { get; set; } = 24;
    public int MarginPx { get; set; } = 24;

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
    public ObservableCollection<StudioSource> Sources { get; set; } = new();
}

public sealed class StudioSettings
{
    // Destination
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
    private static readonly string FilePath = Path.Combine(Folder, "studio-state.json");

    public static StudioState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var state = JsonSerializer.Deserialize<StudioState>(File.ReadAllText(FilePath));
                if (state is not null && state.Scenes.Count > 0)
                    return state;
            }
        }
        catch { /* Corrupt settings should never prevent launch. */ }

        var initial = new StudioState();
        var scene = new StudioScene { Name = "Wizard101" };
        scene.Sources.Add(new StudioSource { Name = "Wizard101 (screen)", Type = SourceTypes.Display });
        scene.Sources.Add(new StudioSource { Name = "Desktop audio", Type = SourceTypes.DesktopAudio, NoiseCleanup = false });
        scene.Sources.Add(new StudioSource { Name = "Microphone", Type = SourceTypes.Microphone });
        initial.Scenes.Add(scene);
        initial.SelectedSceneId = scene.Id;
        return initial;
    }

    public static void Save(StudioState state)
    {
        Directory.CreateDirectory(Folder);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
