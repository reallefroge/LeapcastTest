using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LeapcastStudioAI;

/// <summary>One transcribed word and when it was said.</summary>
public readonly record struct CaptionWord(string Text, DateTime StartUtc, DateTime EndUtc);

/// <summary>One caption on a clip: its text and when it shows (seconds into the clip).</summary>
public readonly record struct Caption(string Text, double Start, double End);

/// <summary>
/// What was said on the mic over the last minutes (kept in memory only), so a clip can be captioned with the words
/// spoken during it.
/// </summary>
public sealed class Transcript
{
    private readonly List<CaptionWord> _words = new();
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(15);

    public void Add(IEnumerable<CaptionWord> words)
    {
        lock (_words)
        {
            _words.AddRange(words);
            _words.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            var cutoff = DateTime.UtcNow - Keep;
            int old = _words.FindIndex(w => w.EndUtc >= cutoff);
            if (old > 0) _words.RemoveRange(0, old);
            else if (old < 0 && _words.Count > 0 && _words[^1].EndUtc < cutoff) _words.Clear();
        }
    }

    /// <summary>The words said between two moments (overlapping them).</summary>
    public List<CaptionWord> Between(DateTime fromUtc, DateTime toUtc)
    {
        lock (_words) return _words.Where(w => w.EndUtc > fromUtc && w.StartUtc < toUtc).ToList();
    }

    public void Clear() { lock (_words) _words.Clear(); }
}

/// <summary>
/// Game sound (or a friend on Discord) coming out of the speakers leaks into the mic. The desktop audio is
/// transcribed too, and a word the mic heard that the desktop said at the same moment is the leak, not you.
/// </summary>
public static class Echo
{
    /// <summary>How far apart the same word may be in the two transcripts (speaker → room → mic, plus recognizer timing).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1.2);

    public static bool Similar(string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        if (a == b) return true;
        if (a.Length < 3 || b.Length < 3) return false;
        // the mic hears the leak muffled: a letter off ("hits" / "hit") is the same word
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length] <= 1;
    }

    private static bool Matches(CaptionWord w, IReadOnlyList<CaptionWord> desktop) =>
        desktop.Any(d => (d.StartUtc - w.StartUtc).Duration() <= Window && Similar(d.Text, w.Text));

    /// <summary>
    /// The mic's words without the ones that came from the speakers. A short word ("the", "a") only counts as a
    /// leak next to another leaked word, so you saying "the" while the game talks isn't dropped by chance.
    /// </summary>
    public static List<CaptionWord> Remove(IReadOnlyList<CaptionWord> mic, IReadOnlyList<CaptionWord> desktop)
    {
        if (desktop.Count == 0) return mic.ToList();
        var leaked = mic.Select(w => Matches(w, desktop)).ToArray();
        var keep = new List<CaptionWord>();
        for (int i = 0; i < mic.Count; i++)
        {
            bool neighbour = i > 0 && leaked[i - 1] || i + 1 < mic.Count && leaked[i + 1];
            bool echo = leaked[i] && (mic[i].Text.Length >= 4 || neighbour);
            if (!echo) keep.Add(mic[i]);
        }
        return keep;
    }

    /// <summary>Did the desktop audio say the trigger word around <paramref name="spokenUtc"/> (finished, or being said now)?</summary>
    public static bool FromDesktop(string word, DateTime spokenUtc, IReadOnlyList<CaptionWord> desktop, string desktopPartial = "")
    {
        bool Is(string t) => VoiceClips.IsWord(t, word) || VoiceClips.SameStart(t, word);
        if (desktop.Any(d => (d.StartUtc - spokenUtc).Duration() <= Window && Is(d.Text))) return true;
        return desktopPartial.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(Is);
    }
}

/// <summary>Turns transcribed words into short TikTok-style captions and burns them into a clip.</summary>
public static class Captions
{
    public const int MaxWords = 4, MaxChars = 22;
    /// <summary>A pause longer than this starts a new caption.</summary>
    public const double Pause = 0.7;
    /// <summary>A caption stays up this long after its last word (unless the next one starts).</summary>
    public const double Hold = 0.6;

    /// <summary>
    /// Groups the words said during a clip (starting at <paramref name="clipStartUtc"/>, <paramref name="clipSeconds"/>
    /// long) into captions of a few words, timed to the clip.
    /// </summary>
    public static List<Caption> Build(IReadOnlyList<CaptionWord> words, DateTime clipStartUtc, double clipSeconds)
    {
        var groups = new List<List<CaptionWord>>();
        foreach (var w in words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Text != "[unk]").OrderBy(w => w.StartUtc))
        {
            var cur = groups.Count > 0 ? groups[^1] : null;
            bool fresh = cur is null || cur.Count >= MaxWords
                         || (w.StartUtc - cur[^1].EndUtc).TotalSeconds > Pause
                         || string.Join(' ', cur.Select(x => x.Text)).Length + 1 + w.Text.Length > MaxChars;
            if (fresh) groups.Add(new List<CaptionWord> { w }); else cur!.Add(w);
        }
        var list = new List<Caption>();
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            double start = (g[0].StartUtc - clipStartUtc).TotalSeconds;
            double end = (g[^1].EndUtc - clipStartUtc).TotalSeconds + Hold;
            if (i + 1 < groups.Count) end = Math.Min(end, (groups[i + 1][0].StartUtc - clipStartUtc).TotalSeconds);
            start = Math.Max(0, start);
            end = Math.Min(clipSeconds, end);
            if (end - start < 0.2) continue;
            list.Add(new Caption(Tidy(string.Join(' ', g.Select(x => x.Text))), start, end));
        }
        return list;
    }

    /// <summary>The recognizer writes lowercase without punctuation: "i'm" → "I'm", first letter capital.</summary>
    public static string Tidy(string text)
    {
        var t = Regex.Replace(text.Trim(), @"\bi\b", "I");
        return t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..];
    }

    /// <summary>Where the captions go: on a 9:16 clip just under the game, on a 16:9 clip near the bottom.</summary>
    public static (int FontSize, int Y) Placement(int w, int h)
    {
        if (h > w)
        {
            var plan = VerticalClip.LivePlan(w, h, 16 / 9.0, 4 / 3.0);
            int fs = SmartTuner.Even((int)(w * 0.058));
            return (fs, plan.GameBox.Y + plan.GameBox.H + SmartTuner.Even((int)(h * 0.02)));
        }
        int f = SmartTuner.Even((int)(h * 0.065));
        return (f, (int)(h * 0.80));
    }

    /// <summary>The drawtext chain for the captions (each caption's text in its own file: no escaping trouble).</summary>
    /// <param name="offset">The video's first timestamp (captions are timed from the start of the clip).</param>
    public static string Filter(IReadOnlyList<Caption> captions, int w, int h, string fontFile, string textDir, double offset = 0)
    {
        Directory.CreateDirectory(textDir);
        var (fs, y) = Placement(w, h);
        string inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var parts = new List<string>();
        for (int i = 0; i < captions.Count; i++)
        {
            var c = captions[i];
            string file = Path.Combine(textDir, $"caption{i:000}.txt");
            File.WriteAllText(file, c.Text);
            parts.Add($"drawtext=fontfile='{VerticalClip.EscapeFilter(fontFile)}':textfile='{VerticalClip.EscapeFilter(file)}':" +
                      $"fontcolor=white:fontsize={fs}:borderw={Math.Max(2, fs / 12)}:bordercolor=black@0.9:" +
                      $"shadowcolor=black@0.5:shadowx=0:shadowy={Math.Max(2, fs / 16)}:x=(w-text_w)/2:y={y}:" +
                      $"enable='between(t,{inv(c.Start + offset)},{inv(c.End + offset)})'");
        }
        return string.Join(",", parts);
    }

    /// <summary>A video file's length, first timestamp and picture size, from ffmpeg's own report.</summary>
    public static async Task<(double Duration, double Start, int W, int H)?> ProbeAsync(string ffmpeg, string file, CancellationToken ct = default)
    {
        var (_, info) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-hide_banner", "-i", file }, TimeSpan.FromSeconds(20), ct);
        var m = Regex.Match(info, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        var st = Regex.Match(info, @"start:\s*(-?\d+(?:\.\d+)?)");
        var sz = Regex.Match(info, @"Video:.*?(\d{2,5})x(\d{2,5})");
        if (!m.Success || !sz.Success) return null;
        double d = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        double start = st.Success ? double.Parse(st.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return (d, start, int.Parse(sz.Groups[1].Value), int.Parse(sz.Groups[2].Value));
    }

    /// <summary>Burns <paramref name="captions"/> into <paramref name="input"/> (re-encoded with <paramref name="videoArgs"/>, audio copied), at low priority.</summary>
    public static async Task BurnAsync(string ffmpeg, string input, string output, IReadOnlyList<Caption> captions, int w, int h,
        IReadOnlyList<string> videoArgs, double offset = 0, CancellationToken ct = default)
    {
        string font = Recap.FontFile() ?? throw new InvalidOperationException("No font for captions.");
        string dir = Path.Combine(Path.GetTempPath(), "leapcast-captions-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-vf", Filter(captions, w, h, font, dir, offset) + ",format=yuv420p",
                "-map", "0:v", "-map", "0:a?" };
            args.AddRange(videoArgs);
            args.AddRange(new[] { "-c:a", "copy", "-movflags", "+faststart", output });
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var err = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            using (ct.Register(() => { try { p.Kill(true); } catch { } })) await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new InvalidOperationException("captions: " + (await err).Trim());
        }
        catch { try { File.Delete(output); } catch { } throw; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
