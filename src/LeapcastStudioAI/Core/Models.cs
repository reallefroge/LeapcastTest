using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeapcastStudioAI;

/// <summary>What the engine actually does with a source. The persisted <see cref="StudioSource.Type"/> string maps onto this.</summary>
public enum SourceKind { Display, Window, Camera, Image, Text, Browser, Microphone, DesktopAudio, Folder, Unsupported }

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
    /// <summary>Groups sources in the list: one eye and lock for all of them, moved together on the preview.</summary>
    public const string Folder = "Folder";

    /// <summary>Types offered in the "Add source" picker.</summary>
    public static readonly string[] Pickable = { Window, Display, Camera, Browser, Image, Text, Microphone, DesktopAudio, Folder };

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
        Folder => SourceKind.Folder,
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

    /// <summary>The folder this source is in (a <see cref="SourceTypes.Folder"/> source in the same scene), if any.</summary>
    public Guid? FolderId { get; set; }
    /// <summary>A folder whose contents are folded away in the sources list.</summary>
    public bool Collapsed { get; set; }
    [JsonIgnore] public bool IsFolder => Type == SourceTypes.Folder;
    /// <summary>Its folder is hidden (set by <see cref="StudioScene.ApplyFolders"/>).</summary>
    [JsonIgnore] public bool HiddenByFolder { get; set; }
    /// <summary>On the picture / heard: its own eye and its folder's eye are both on. What the pipeline uses.</summary>
    [JsonIgnore] public bool Shown => Visible && !HiddenByFolder;
    /// <summary>Sources list row: indent (inside a folder) and the second line.</summary>
    [JsonIgnore] public double ListIndent { get; set; }
    [JsonIgnore] public string ListDetail { get; set; } = "";

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

    // ---------------- folders ----------------
    // A folder is a source of type Folder; the sources in it carry its id and sit directly behind it in Sources
    // (Sources runs back → front, so in the list, which shows front first, they appear right under the folder).

    public StudioSource? FolderOf(StudioSource s) =>
        s.FolderId is Guid id ? Sources.FirstOrDefault(x => x.Id == id && x.IsFolder) : null;

    public List<StudioSource> ChildrenOf(StudioSource folder) => Sources.Where(x => x.FolderId == folder.Id && !x.IsFolder).ToList();

    /// <summary>Locked itself, or inside a locked folder.</summary>
    public bool IsLocked(StudioSource s) => s.Locked || FolderOf(s)?.Locked == true;

    /// <summary>Updates what each folder's eye means for its sources, and fixes up the order (call after edits).</summary>
    public void ApplyFolders()
    {
        Normalize();
        ApplyFolderVisibility();
    }

    /// <summary>Only what each folder's eye means for its sources (no reordering).</summary>
    public void ApplyFolderVisibility()
    {
        foreach (var s in Sources) s.HiddenByFolder = FolderOf(s) is { } f && !f.Visible;
    }

    /// <summary>The list split into blocks, back → front: a folder with its sources (folder last), or one source.</summary>
    public List<List<StudioSource>> Blocks()
    {
        var blocks = new List<List<StudioSource>>();
        var placed = new HashSet<StudioSource>();
        foreach (var s in Sources)
        {
            if (placed.Contains(s)) continue;
            if (s.IsFolder)
            {
                var block = ChildrenOf(s).Where(c => !placed.Contains(c)).ToList();
                block.Add(s);
                foreach (var b in block) placed.Add(b);
                blocks.Add(block);
            }
            else if (FolderOf(s) is { } f)
            {
                // a folder's source comes before the folder itself: placed with the folder's block
                if (!Sources.Contains(f)) { blocks.Add(new() { s }); placed.Add(s); }
            }
            else { blocks.Add(new() { s }); placed.Add(s); }
        }
        // anything a folder block didn't pick up (shouldn't happen)
        foreach (var s in Sources.Where(x => !placed.Contains(x))) blocks.Add(new() { s });
        return blocks;
    }

    /// <summary>Keeps every folder's sources together, directly behind the folder; drops links to missing folders.</summary>
    public void Normalize()
    {
        foreach (var s in Sources)
            if (s.FolderId is not null && (s.IsFolder || FolderOf(s) is null)) s.FolderId = null;
        SetOrder(Blocks().SelectMany(b => b).ToList());
    }

    /// <summary>Reorders Sources to exactly this order (moves only what changed: the list stays bound).</summary>
    public void SetOrder(IReadOnlyList<StudioSource> order)
    {
        for (int i = 0; i < order.Count; i++)
        {
            int at = Sources.IndexOf(order[i]);
            if (at >= 0 && at != i) Sources.Move(at, i);
        }
    }

    /// <summary>The block a source belongs to (its folder's block when it's in one).</summary>
    private int BlockIndex(List<List<StudioSource>> blocks, StudioSource s) => blocks.FindIndex(b => b.Contains(s));

    /// <summary>
    /// One step up (toward the front) or down in the sources list. A folder moves with everything in it; a source in
    /// a folder moves within it, and stepping past the folder's first or last place takes it out of the folder.
    /// </summary>
    public void MoveInList(StudioSource s, bool up)
    {
        Normalize();
        var blocks = Blocks();
        int bi = BlockIndex(blocks, s);
        if (bi < 0) return;
        if (FolderOf(s) is { } folder)
        {
            var block = blocks[bi];
            var kids = block.Take(block.Count - 1).ToList(); // back → front
            int i = kids.IndexOf(s);
            if (up && i < kids.Count - 1) (kids[i], kids[i + 1]) = (kids[i + 1], kids[i]);
            else if (!up && i > 0) (kids[i], kids[i - 1]) = (kids[i - 1], kids[i]);
            else
            {
                // out of the folder: just above it (in front) or just below it (behind)
                kids.Remove(s);
                s.FolderId = null;
                blocks[bi] = kids.Append(folder).ToList();
                blocks.Insert(up ? bi + 1 : bi, new List<StudioSource> { s });
                SetOrder(blocks.SelectMany(b => b).ToList());
                return;
            }
            blocks[bi] = kids.Append(folder).ToList();
        }
        else
        {
            int j = up ? bi + 1 : bi - 1;
            if (j < 0 || j >= blocks.Count) return;
            (blocks[bi], blocks[j]) = (blocks[j], blocks[bi]);
        }
        SetOrder(blocks.SelectMany(b => b).ToList());
    }

    /// <summary>Puts a source into a folder as its top item, or (folder null) out of its folder, just above it.</summary>
    public void MoveToFolder(StudioSource s, StudioSource? folder)
    {
        if (s.IsFolder || ReferenceEquals(s, folder)) return;
        Normalize();
        var current = FolderOf(s);
        if (folder is null)
        {
            if (current is null) return;
            s.FolderId = null;
            Sources.Remove(s);
            Sources.Insert(Sources.IndexOf(current) + 1, s);
        }
        else
        {
            s.FolderId = folder.Id;
            Sources.Remove(s);
            Sources.Insert(Sources.IndexOf(folder), s); // directly behind the folder entry = top of its contents
        }
        Normalize();
    }

    /// <summary>
    /// Drag and drop in the list: <paramref name="s"/> goes directly above <paramref name="target"/> (in front of it),
    /// joining the target's folder when the target is inside one. A folder moves with everything in it, and never
    /// into another folder.
    /// </summary>
    public void PlaceAbove(StudioSource s, StudioSource target)
    {
        if (ReferenceEquals(s, target) || s.IsFolder && ReferenceEquals(FolderOf(target), s)) return;
        Normalize();
        if (s.IsFolder)
        {
            var blocks = Blocks();
            int from = BlockIndex(blocks, s), to = BlockIndex(blocks, target);
            if (from < 0 || to < 0 || from == to) return;
            var moving = blocks[from];
            blocks.RemoveAt(from);
            to = BlockIndex(blocks, target);
            blocks.Insert(to + 1, moving);
            SetOrder(blocks.SelectMany(b => b).ToList());
            return;
        }
        s.FolderId = target.IsFolder ? null : FolderOf(target)?.Id;
        Sources.Remove(s);
        Sources.Insert(Sources.IndexOf(target) + 1, s);
        Normalize();
    }

    /// <summary>Drag and drop below the last row: to the very bottom of the list (behind everything).</summary>
    public void PlaceAtBottom(StudioSource s)
    {
        Normalize();
        var blocks = Blocks();
        int from = BlockIndex(blocks, s);
        if (from < 0) return;
        List<StudioSource> moving;
        if (s.IsFolder || FolderOf(s) is null) { moving = blocks[from]; blocks.RemoveAt(from); }
        else { s.FolderId = null; blocks[from].Remove(s); moving = new() { s }; }
        blocks.Insert(0, moving);
        SetOrder(blocks.SelectMany(b => b).ToList());
    }

    /// <summary>Deletes a folder; its sources are deleted too, or (<paramref name="keepContents"/>) stay in the list.</summary>
    public void RemoveFolder(StudioSource folder, bool keepContents)
    {
        foreach (var c in ChildrenOf(folder))
        {
            if (keepContents) c.FolderId = null;
            else Sources.Remove(c);
        }
        Sources.Remove(folder);
        Normalize();
    }

    /// <summary>Copies of these sources with new ids; folder links are kept within the copies.</summary>
    public static List<StudioSource> CopyAll(IEnumerable<StudioSource> sources)
    {
        var list = sources.ToList();
        var map = new Dictionary<Guid, Guid>();
        var copies = list.Select(s => { var c = s.Copy(); map[s.Id] = c.Id; return c; }).ToList();
        foreach (var c in copies)
            if (c.FolderId is Guid f) c.FolderId = map.TryGetValue(f, out var nf) ? nf : null;
        return copies;
    }
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
    /// <summary>Measured or typed upload speed in kbps (0 = unknown).</summary>
    public int UploadKbps { get; set; }
    /// <summary>Older versions stored Mbps: read into <see cref="UploadKbps"/>, never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double UploadMbps { get => 0; set => UploadKbps = (int)Math.Round(Math.Max(0, value) * 1000); }
    public DateTime? UploadMeasuredUtc { get; set; }
    public int AudioBitrateKbps { get; set; } = 160;
    /// <summary>Master fader for the stream/recording audio (dB).</summary>
    public double MasterVolumeDb { get; set; }
    /// <summary>Vertical canvas bitrate (kbps); 0 = automatic from its resolution.</summary>
    public int VerticalBitrateKbps { get; set; }
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
    /// <summary>Say the word (default "clip") while live, recording or with the replay buffer on: the last
    /// <see cref="VoiceClipSeconds"/> seconds are saved to <see cref="VoiceClipFolder"/>.</summary>
    public bool VoiceClipEnabled { get; set; } = true;
    public string VoiceClipWord { get; set; } = "clip";
    public int VoiceClipSeconds { get; set; } = 50;
    public string VoiceClipFolder { get; set; } = VoiceClips.DefaultFolder;
    /// <summary>
    /// Keep every scene and hidden source running so switching and showing are instant (more CPU/RAM). Off = light
    /// mode: only what's on screen is captured; showing a source or switching scenes rebuilds (about a second's pause,
    /// destinations stay connected).
    /// </summary>
    public bool InstantSwitching { get; set; }
    /// <summary>YouTube: pick the broadcast (scheduled or new) when going live, instead of only using the stream key.</summary>
    public bool YouTubePickBroadcast { get; set; } = true;
    /// <summary>Google OAuth "Desktop app" client (Google Cloud console) for the YouTube Data API.</summary>
    public string YouTubeClientId { get; set; } = "";
    public string YouTubeClientSecretProtected { get; set; } = "";
    /// <summary>The Google sign-in (refresh token), DPAPI-encrypted.</summary>
    public string YouTubeRefreshProtected { get; set; } = "";
    /// <summary>Where the VOD editor puts finished videos.</summary>
    public string VodEditFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Leapcast", "VODEdits");
    /// <summary>The VOD editor's target length (minutes).</summary>
    public int VodEditMinutes { get; set; } = 15;
    /// <summary>Leave Leapcast's own windows out of display captures (they show what's behind them).</summary>
    public bool HideFromCapture { get; set; }
    /// <summary>Clips are saved as 9:16 (TikTok / Shorts): small camera on top, the game big below.</summary>
    public bool VerticalClips { get; set; } = true;
    /// <summary>Captions on clips: what you said (transcribed from your mic, on this PC) burned into the clip.</summary>
    public bool ClipCaptions { get; set; } = true;
    /// <summary>Stream safety: desktop audio (and so the stream, recordings and clips) runs this far behind, so a
    /// filtered word can be muted before it goes out. Off: no delay (the slur filter can't work).</summary>
    public bool SafetyDelay { get; set; }
    public double SafetyDelaySeconds { get; set; } = 2;
    /// <summary>Mute slurs heard in desktop audio (needs the safety delay).</summary>
    public bool MuteSlurs { get; set; } = true;
    public bool MuteAbleist { get; set; } = true;
    /// <summary>More words to mute, one per line.</summary>
    public string MuteWords { get; set; } = "";
    /// <summary>Turn sudden, very loud desktop audio right down (ear-rape protection). Works with or without the delay.</summary>
    public bool LoudGuard { get; set; } = true;

    // ---- chat panel + alerts
    /// <summary>Twitch channel for the chat panel (blank: the chat-clips channel).</summary>
    public string ChatTwitchChannel { get; set; } = "";
    /// <summary>YouTube channel (@handle or link) or a live video link for the chat panel.</summary>
    public string ChatYouTube { get; set; } = "";
    /// <summary>Streamlabs Socket API token (DPAPI-protected): follows, donations, YouTube subscribers.</summary>
    public string StreamlabsTokenProtected { get; set; } = "";
    public bool AlertFollows { get; set; } = true;
    public bool AlertSubs { get; set; } = true;
    public bool AlertGifts { get; set; } = true;
    public bool AlertBits { get; set; } = true;
    public int AlertMinBits { get; set; } = 1;
    public bool AlertRaids { get; set; } = true;
    public bool AlertSuperChats { get; set; } = true;
    public bool AlertDonations { get; set; } = true;
    public double AlertSeconds { get; set; } = 6;
    public bool AlertSound { get; set; } = true;
    public double? ChatPopLeft { get; set; }
    public double? ChatPopTop { get; set; }
    public double ChatPopWidth { get; set; }
    public double ChatPopHeight { get; set; }
    public bool ChatPopPinned { get; set; } = true;
    public double ChatPopOpacity { get; set; } = 0.85;

    public AlertOptions AlertOptions() => new(AlertFollows, AlertSubs, AlertGifts, AlertBits, Math.Max(1, AlertMinBits), AlertRaids,
        AlertSuperChats, AlertDonations, Math.Clamp(AlertSeconds, 3, 20), AlertSound);

    public AudioSafetyConfig SafetyConfig() => new(
        SafetyDelay ? Math.Clamp(SafetyDelaySeconds, 1, 5) : 0, MuteSlurs && SafetyDelay, MuteAbleist, MuteWords ?? "", LoudGuard);
    /// <summary>Names captions spell your way, one per line: "Khrysalis = Chrysalis, Crysalis" (Wizard101's are built in).</summary>
    public string CaptionSpellings { get; set; } = "";
    /// <summary>Wizard101: when the health globe empties (HP 0), save the fight that led to it.</summary>
    public bool DeathClips { get; set; } = true;
    /// <summary>Clip from Twitch chat: someone allowed types the command.</summary>
    public bool ChatClips { get; set; } = true;
    /// <summary>The Twitch channel to read; empty = taken from a Twitch chat browser source.</summary>
    public string ChatClipChannel { get; set; } = "";
    public string ChatClipCommand { get; set; } = "!clip";
    public ChatClipWho ChatClipWho { get; set; } = ChatClipWho.Mods;
    /// <summary>Viewers see the stream this many seconds late: a chat clip ends this long before the message.</summary>
    public int ChatClipDelaySeconds { get; set; } = 4;
    /// <summary>When a stream ends: its clips in one folder, a highlight reel and thumbnails.</summary>
    public bool RecapEnabled { get; set; } = true;
    public string RecapTitle { get; set; } = "Stream highlights";
    /// <summary>Lines for the reel's outro card (e.g. your handles), one per line.</summary>
    public string RecapSocials { get; set; } = "";
    /// <summary>Text at the bottom of 9:16 clips (e.g. your handle); empty = none.</summary>
    public string ClipHandle { get; set; } = "";
    /// <summary>How readily the word counts: "Normal", "High" (default) or "Max" (<see cref="VoiceClips.Sensitivity"/>).</summary>
    public string VoiceClipSensitivity { get; set; } = "High";

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
