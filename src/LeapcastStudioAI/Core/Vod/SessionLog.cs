using System.Text.Json;

namespace LeapcastStudioAI.Vod;

/// <summary>A word in a recording: seconds from the recording's first frame.</summary>
public sealed record VodWord(string Text, double Start, double End);

/// <summary>Something that happened during a recording: "clip" (a clip was saved: voice, chat or death), "death",
/// "fight" (a fight started), "chat" (a chat message). <see cref="At"/> is seconds into the recording.</summary>
public sealed record VodMarker(string Kind, double At, string? Text = null);

/// <summary>What Leapcast knew while recording, saved next to the recording ("… .leapcast.json") for the VOD editor:
/// your mic transcript (clean, not the game), the desktop transcript (to drop leaks), saved clips, deaths, chat.</summary>
public sealed class VodSidecar
{
    public int Version { get; set; } = 1;
    public ClipLayout? Layout { get; set; }
    public List<VodWord> Words { get; set; } = new();
    public List<VodWord> Desktop { get; set; } = new();
    public List<VodMarker> Markers { get; set; } = new();
    /// <summary>Your mic on its own, next to the recording (16 kHz WAV), and where it starts in the recording (s).</summary>
    public string? MicFile { get; set; }
    public double MicOffset { get; set; }

    public static string MicPathFor(string video) => Path.Combine(Path.GetDirectoryName(video) ?? "", Path.GetFileNameWithoutExtension(video) + ".mic.wav");

    public static string PathFor(string video) => Path.Combine(Path.GetDirectoryName(video) ?? "", Path.GetFileNameWithoutExtension(video) + ".leapcast.json");

    public static VodSidecar? Load(string video)
    {
        try
        {
            string p = PathFor(video);
            return File.Exists(p) ? JsonSerializer.Deserialize<VodSidecar>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    public void Save(string video) => File.WriteAllText(PathFor(video), JsonSerializer.Serialize(this));
}

/// <summary>Collects a recording's transcript and events as they happen (UTC), then writes them as a sidecar.</summary>
public sealed class SessionLog
{
    private readonly object _lock = new();
    private readonly List<CaptionWord> _words = new(), _desk = new();
    private readonly List<(string Kind, DateTime Utc, string? Text)> _events = new();
    public ClipLayout? Layout { get; set; }

    public void AddWords(IEnumerable<CaptionWord> w) { lock (_lock) _words.AddRange(w); }
    public void AddDesktop(IEnumerable<CaptionWord> w) { lock (_lock) _desk.AddRange(w); }
    public void Mark(string kind, DateTime utc, string? text = null) { lock (_lock) _events.Add((kind, utc, text)); }

    /// <summary>The sidecar for a recording whose first frame was captured at <paramref name="startUtc"/>.</summary>
    public VodSidecar ToSidecar(DateTime startUtc, double? lengthSeconds = null)
    {
        double S(DateTime t) => (t - startUtc).TotalSeconds;
        bool In(double s) => s >= -1 && (lengthSeconds is not { } l || s <= l + 1);
        lock (_lock)
        {
            var side = new VodSidecar { Layout = Layout };
            side.Words.AddRange(Echo.Remove(_words, _desk).Select(w => new VodWord(w.Text, S(w.StartUtc), S(w.EndUtc))).Where(w => In(w.Start)));
            side.Desktop.AddRange(_desk.Select(w => new VodWord(w.Text, S(w.StartUtc), S(w.EndUtc))).Where(w => In(w.Start)));
            side.Markers.AddRange(_events.Select(e => new VodMarker(e.Kind, S(e.Utc), e.Text)).Where(m => In(m.At)));
            return side;
        }
    }
}
