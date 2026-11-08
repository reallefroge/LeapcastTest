using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeapcastStudioAI.Vod;

/// <summary>A chapter card's text: the big line ("PART 2") and the line under it (the chapter's name).</summary>
public sealed class CardText
{
    public int Chapter { get; set; }
    public string? Heading { get; set; }
    public string? Sub { get; set; }
}

/// <summary>What you asked to change about an edit (from the feedback box or the controls).</summary>
public sealed class EditTweaks
{
    public string? Title { get; set; }
    public List<CardText> Cards { get; set; } = new();
    /// <summary>Music level (1 = as made; 0.5 = half).</summary>
    public double MusicVolume { get; set; } = 1;
    public bool Music { get; set; } = true;
    public bool Captions { get; set; } = true;
    public double SfxVolume { get; set; } = 1;
    /// <summary>−1 tighter (more cuts, shorter moments), 0, +1 relaxed (longer, fewer cuts).</summary>
    public int Pace { get; set; }
    /// <summary>0 none, 1 fewer, 2 normal, 3 more.</summary>
    public int Zooms { get; set; } = 2;
    public bool SlowMotion { get; set; } = true;
    public bool FaceCam { get; set; } = true;
    public bool ColdOpen { get; set; } = true;
    /// <summary>−1 more action, 0 balanced, +1 more funny moments.</summary>
    public int Emphasis { get; set; }
    public double? Minutes { get; set; }
    /// <summary>Stretches of the recording to leave out (source seconds, [from, to]).</summary>
    public List<double[]> Remove { get; set; } = new();
    /// <summary>Moments of the recording that must be in (source seconds).</summary>
    public List<double> Include { get; set; } = new();

    public CardText Card(int chapter)
    {
        var c = Cards.FirstOrDefault(x => x.Chapter == chapter);
        if (c is null) { c = new CardText { Chapter = chapter }; Cards.Add(c); }
        return c;
    }
}

/// <summary>A moment in the finished edit, for the review list.</summary>
public sealed record EditMoment(double From, double To, double At, string Quote);

/// <summary>
/// An edit you can revise: the recording, what the editor learned about it (so it never has to watch it again), the
/// captions Whisper already wrote, the plan it made, and your changes. Saved next to the video.
/// </summary>
public sealed class VodProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Recording { get; set; } = "";
    public string Video { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string Title { get; set; } = "";
    public double TargetSeconds { get; set; } = 900;
    public bool Captions { get; set; } = true;
    public bool Music { get; set; } = true;
    public string Socials { get; set; } = "";
    public EditTweaks Tweaks { get; set; } = new();
    /// <summary>Whisper's words already written (source seconds) and the stretches they cover.</summary>
    public List<VodWord> CaptionWords { get; set; } = new();
    public List<double[]> CaptionCovered { get; set; } = new();
    // the last plan, for the review panel and for reading "at 3:20" in feedback
    public List<PieceInfo> Pieces { get; set; } = new();
    public List<ChapterInfo> Chapters { get; set; } = new();
    public List<EditMoment> Moments { get; set; } = new();
    public double Duration { get; set; }

    public sealed record PieceInfo(string Role, double In, double Out, double At, double Length, double Speed);
    public sealed record ChapterInfo(int Index, double At, string Heading, string Sub);

    public string WorkDir => Path.Combine(OutputFolder, ".leapcast-work", Id);
    public static string PathFor(string video) => Path.Combine(Path.GetDirectoryName(video) ?? "", Path.GetFileNameWithoutExtension(video) + ".edit.json");
    private string SignalsPath => Path.Combine(WorkDir, "signals.json.gz");

    public VodEditOptions Options() => new(Tweaks.Title ?? Title, (Tweaks.Minutes ?? TargetSeconds / 60) * 60, Captions && Tweaks.Captions, Music && Tweaks.Music, Socials);

    /// <summary>Where a moment of the finished video comes from in the recording (null: a title or end card).</summary>
    public double? SourceAt(double editSeconds)
    {
        var p = Pieces.LastOrDefault(x => x.At <= editSeconds + 0.01);
        if (p is null || p.Role is "title" or "end") return null;
        return p.In + Math.Min(p.Length, editSeconds - p.At) * p.Speed;
    }

    public void Remember(EditPlan plan)
    {
        Pieces = plan.Pieces.Select(p => new PieceInfo(p.Role, p.In, p.Out, p.At, p.Length, p.Speed)).ToList();
        Chapters = plan.Texts.Where(t => t.Kind == "chapter").Select((t, i) => new ChapterInfo(i + 2, t.At, t.Text, t.Sub ?? "")).ToList();
        Moments = plan.Moments.ToList();
        Duration = plan.Duration;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathFor(Video))!);
        File.WriteAllText(PathFor(Video), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static VodProject? Load(string video)
    {
        try { return File.Exists(PathFor(video)) ? JsonSerializer.Deserialize<VodProject>(File.ReadAllText(PathFor(video))) : null; }
        catch { return null; }
    }

    // ---- what the editor learned about the recording (sound, motion, words, events)
    private sealed record SignalsFile(double Duration, int Width, int Height, double Fps, float[] Loud, float[] Motion, float[] Brightness,
        List<VodWord> Words, List<VodMarker> Markers, ClipLayout? Layout, bool WordsFromMic, bool HasAudio, long RecordingSize, DateTime RecordingTime);

    public void SaveSignals(VodSignals s)
    {
        Directory.CreateDirectory(WorkDir);
        var fi = new FileInfo(Recording);
        var f = new SignalsFile(s.Duration, s.Width, s.Height, s.Fps, s.Loud, s.Motion, s.Brightness, s.Words, s.Markers, s.Layout, s.WordsFromMic, s.HasAudio,
            fi.Exists ? fi.Length : 0, fi.Exists ? fi.LastWriteTimeUtc : default);
        using var fs = File.Create(SignalsPath);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        JsonSerializer.Serialize(gz, f);
    }

    /// <summary>The cached analysis, if the recording hasn't changed since.</summary>
    public VodSignals? LoadSignals()
    {
        try
        {
            if (!File.Exists(SignalsPath)) return null;
            using var fs = File.OpenRead(SignalsPath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var f = JsonSerializer.Deserialize<SignalsFile>(gz);
            var fi = new FileInfo(Recording);
            if (f is null || !fi.Exists || fi.Length != f.RecordingSize) return null;
            return new VodSignals
            {
                Duration = f.Duration, Width = f.Width, Height = f.Height, Fps = f.Fps, Loud = f.Loud, Motion = f.Motion, Brightness = f.Brightness,
                Words = f.Words, Markers = f.Markers, Layout = f.Layout, WordsFromMic = f.WordsFromMic, HasAudio = f.HasAudio,
            };
        }
        catch { return null; }
    }
}

/// <summary>
/// Reads what you'd like changed, in your own words ("music quieter", "rename part 2 to Khrysalis", "remove the part at
/// 3:20", "more funny moments", "make it 12 minutes"), into changes to the edit, and says back what it understood.
/// </summary>
public static class EditFeedback
{
    public sealed record Result(List<string> Understood, List<string> NotUnderstood);

    public static Result Apply(string feedback, VodProject project)
    {
        var t = project.Tweaks;
        var ok = new List<string>();
        var not = new List<string>();
        // one request per sentence / line / "and also"
        var parts = Regex.Split(feedback, @"(?<=[.!?;\n])\s+|\n|,\s*(?:and\s+)?also\s+|\s+and\s+(?=(?:make|remove|cut|drop|add|include|rename|call|change|more|less|fewer|too|no|turn|put|keep|the music|music|captions|zoom|slow|face))", RegexOptions.IgnoreCase)
            .Select(x => x.Trim().TrimEnd('.', '!', ';')).Where(x => x.Length > 1).ToList();
        foreach (var raw in parts)
        {
            string s = raw.ToLowerInvariant();
            int before = ok.Count;

            // ---- names and text (keep the user's own capitals for the new text)
            var rename = Regex.Match(raw, @"(?:rename|call|name|change|retitle)\s+(?:the\s+)?(?:part|chapter|card)\s*(\d+)(?:'s)?\s*(?:card|title|text|name)?\s*(?:to|as|into)\s*[""“']?(.+?)[""”']?\s*$", RegexOptions.IgnoreCase);
            var rename2 = Regex.Match(raw, @"(?:part|chapter|card)\s*(\d+)(?:'s)?\s*(?:card|title|text|name)?\s*(?:should\s+(?:say|read|be)|says?|=|:)\s*[""“']?(.+?)[""”']?\s*$", RegexOptions.IgnoreCase);
            var rm = rename.Success ? rename : rename2;
            if (rm.Success)
            {
                int ch = int.Parse(rm.Groups[1].Value);
                if (ch < 2 || project.Chapters.All(c => c.Index != ch)) { not.Add($"{raw} (there's no part {ch} card: the cards are {string.Join(", ", project.Chapters.Select(c => c.Index))})"); continue; }
                t.Card(ch).Sub = rm.Groups[2].Value.Trim();
                ok.Add($"Part {ch} card: “{t.Card(ch).Sub}”");
                continue;
            }
            var heading = Regex.Match(raw, @"(?:part|chapter|card)\s*(\d+)\s*heading\s*(?:to|should\s+(?:say|be)|=|:)\s*[""“']?(.+?)[""”']?\s*$", RegexOptions.IgnoreCase);
            if (heading.Success)
            {
                int ch = int.Parse(heading.Groups[1].Value);
                t.Card(ch).Heading = heading.Groups[2].Value.Trim();
                ok.Add($"Part {ch} card heading: “{t.Card(ch).Heading}”");
                continue;
            }
            var title = Regex.Match(raw, @"(?:(?:video\s+)?title\s*(?:should\s+(?:say|be)|to|=|:)|(?:rename|call)\s+(?:the\s+)?(?:video|it)\s*(?:to)?)\s*[""“']?(.+?)[""”']?\s*$", RegexOptions.IgnoreCase);
            if (title.Success && !s.Contains("part ") && !s.Contains("chapter"))
            {
                t.Title = title.Groups[1].Value.Trim();
                ok.Add($"Title: “{t.Title}”");
                continue;
            }

            // ---- music and sound
            if (Regex.IsMatch(s, @"\b(no|remove|without|turn off|mute)\b.*\bmusic\b|\bmusic\b.*\b(off|gone)\b")) { t.Music = false; ok.Add("No music"); }
            else if (s.Contains("music"))
            {
                var pct = Regex.Match(s, @"(\d{1,3})\s*%");
                double f = pct.Success ? int.Parse(pct.Groups[1].Value) / 100.0 : s.Contains("half") ? 0.5 : s.Contains("a lot") || s.Contains("way ") || s.Contains("much") ? 0.5 : s.Contains("bit") || s.Contains("little") || s.Contains("slightly") ? 0.8 : 0.65;
                bool louder = s.Contains("too quiet") || s.Contains("too soft")
                              || (Regex.IsMatch(s, @"\b(louder|turn (it )?up|increase|raise|boost|more music)\b")
                                  && !Regex.IsMatch(s, @"\b(too loud|quieter|lower|softer|down|less|reduce|cut|decrease)\b"));
                if (louder)
                {
                    double up = pct.Success ? 1 + f : f < 0.6 ? 1.6 : f < 0.7 ? 1.4 : 1.2;
                    t.MusicVolume = Math.Min(2, t.MusicVolume * up);
                    t.Music = true;
                    ok.Add($"Music louder ({t.MusicVolume:P0} of the original level)");
                }
                else
                {
                    // "cut it back 50%": by that much; "to 50%": to that much
                    double by = pct.Success && Regex.IsMatch(s, @"\bto\s+\d") ? f : pct.Success ? 1 - f : f;
                    t.MusicVolume = Math.Max(0.05, t.MusicVolume * by);
                    ok.Add($"Music quieter ({t.MusicVolume:P0} of the original level)");
                }
            }
            if (Regex.IsMatch(s, @"\b(sound effects?|sfx|whoosh(es)?)\b"))
            {
                if (Regex.IsMatch(s, @"\b(no|remove|without|off)\b")) { t.SfxVolume = 0; ok.Add("No sound effects"); }
                else if (Regex.IsMatch(s, @"\b(louder|more)\b")) { t.SfxVolume = Math.Min(2, t.SfxVolume * 1.4); ok.Add("Sound effects louder"); }
                else { t.SfxVolume *= 0.6; ok.Add("Sound effects quieter"); }
            }

            // ---- captions
            if (Regex.IsMatch(s, @"\bcaptions?|subtitles?\b"))
            {
                if (Regex.IsMatch(s, @"\b(no|remove|without|off|turn off)\b")) { t.Captions = false; ok.Add("No captions"); }
                else if (Regex.IsMatch(s, @"\b(add|on|turn on|want|with)\b")) { t.Captions = true; ok.Add("Captions on"); }
            }

            // ---- length and pace
            var mins = Regex.Match(s, @"(\d{1,2}(?:\.\d)?)\s*(?:-|\s)?\s*min");
            if (mins.Success && Regex.IsMatch(s, @"\b(make|long|length|be|minutes?|under|about|around)\b"))
            {
                t.Minutes = Math.Clamp(double.Parse(mins.Groups[1].Value, CultureInfo.InvariantCulture), 1, 60);
                ok.Add($"About {t.Minutes:0.#} minutes long");
            }
            else if (Regex.IsMatch(s, @"\b(shorter|too long|cut it down)\b")) { t.Minutes = Math.Max(2, (t.Minutes ?? project.TargetSeconds / 60) - 3); ok.Add($"Shorter: about {t.Minutes:0} minutes"); }
            else if (Regex.IsMatch(s, @"\b(longer|too short)\b")) { t.Minutes = (t.Minutes ?? project.TargetSeconds / 60) + 3; ok.Add($"Longer: about {t.Minutes:0} minutes"); }
            if (Regex.IsMatch(s, @"\b(faster|tighter|more cuts|drags|too slow|boring|snappier|quicker)\b")) { t.Pace = Math.Max(-1, t.Pace - 1); ok.Add("Tighter pace (more cuts, shorter moments)"); }
            else if (Regex.IsMatch(s, @"\b(slower|relaxed|too many cuts|too choppy|choppy|too fast|breathe|less cuts|fewer cuts)\b")) { t.Pace = Math.Min(1, t.Pace + 1); ok.Add("More relaxed pace (longer moments, fewer cuts)"); }

            // ---- effects
            if (Regex.IsMatch(s, @"\bzoom"))
            {
                if (Regex.IsMatch(s, @"\b(too many|too much|less|fewer|tone down|calmer)\b")) { t.Zooms = Math.Max(0, t.Zooms - 1); ok.Add("Fewer zooms"); }
                else if (Regex.IsMatch(s, @"\b(no|remove|without|stop)\b.*zoom")) { t.Zooms = 0; ok.Add("No zooms"); }
                else if (Regex.IsMatch(s, @"\b(more|add)\b")) { t.Zooms = Math.Min(3, t.Zooms + 1); ok.Add("More zooms"); }
            }
            if (Regex.IsMatch(s, @"slow[\s-]?mo(tion)?"))
            {
                bool off = Regex.IsMatch(s, @"\b(no|remove|without|stop|off)\b");
                t.SlowMotion = !off; ok.Add(off ? "No slow motion" : "Slow motion on");
            }
            if (Regex.IsMatch(s, @"\b(face ?cam|webcam|my face|face zooms?|camera)\b"))
            {
                bool off = Regex.IsMatch(s, @"\b(no|remove|without|stop|less|off)\b");
                t.FaceCam = !off; ok.Add(off ? "No cuts to your face" : "Cuts to your face on");
            }
            if (Regex.IsMatch(s, @"cold open|teaser|intro clip|the hook"))
            {
                bool off = Regex.IsMatch(s, @"\b(no|remove|without|skip|cut)\b");
                t.ColdOpen = !off; ok.Add(off ? "No cold open" : "Cold open on");
            }
            if (Regex.IsMatch(s, @"\b(funnier|more funny|more laughs|more jokes|funny moments)\b")) { t.Emphasis = 1; ok.Add("More of the funny moments"); }
            else if (Regex.IsMatch(s, @"\b(more action|more fights?|more combat|more gameplay|more boss)\b")) { t.Emphasis = -1; ok.Add("More of the action"); }

            // ---- moments: remove (times in the finished video), add (times in the recording)
            var times = Regex.Matches(s, @"\b(\d{1,2}):(\d{2})(?::(\d{2}))?\b").Select(m =>
                m.Groups[3].Success ? int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value)
                                    : int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value)).Select(x => (double)x).ToList();
            bool inRecording = Regex.IsMatch(s, @"\b(stream|recording|vod|original|raw)\b");
            if (times.Count > 0 && Regex.IsMatch(s, @"\b(remove|cut|drop|delete|get rid of|lose|skip)\b"))
            {
                foreach (var tm in times)
                {
                    double? src = inRecording ? tm : project.SourceAt(tm);
                    var m = project.Moments.FirstOrDefault(x => src is { } v && v >= x.From - 0.5 && v <= x.To + 0.5);
                    if (m is not null) { t.Remove.Add(new[] { m.From, m.To }); ok.Add($"Remove the moment at {Clock(m.At)} (“{Short(m.Quote)}”)"); }
                    else if (src is { } v2) { t.Remove.Add(new[] { v2 - 8, v2 + 8 }); ok.Add($"Leave out the recording around {Clock(v2)}"); }
                    else not.Add($"{raw} (nothing from the recording plays at {Clock(tm)}: that's a title or end card)");
                }
            }
            else if (times.Count > 0 && Regex.IsMatch(s, @"\b(add|include|keep|put in|use|want)\b"))
            {
                foreach (var tm in times)
                {
                    // without "of the stream", a time past the end of the edit can only mean the recording
                    double src = inRecording || tm > project.Duration ? tm : project.SourceAt(tm) ?? tm;
                    t.Include.Add(src);
                    ok.Add($"Include the moment at {Clock(src)} of the recording");
                }
            }

            if (ok.Count == before) not.Add(raw);
        }
        return new Result(ok, not);
    }

    public static string Clock(double s) => s >= 3600 ? TimeSpan.FromSeconds(s).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"m\:ss");
    private static string Short(string q) => q.Length > 40 ? q[..40] + "…" : q;
}
