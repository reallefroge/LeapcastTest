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
    /// <summary>Chapter cards on screen and chapters in the description.</summary>
    public bool Chapters { get; set; } = true;
    public double SfxVolume { get; set; } = 1;
    /// <summary>The whooshes, pops and risers on cuts, chapter cards and the title (slow-motion hits stay).</summary>
    public bool TransitionSounds { get; set; } = true;
    /// <summary>The transition between clips: "none" (plain cuts, the default), a style from
    /// <see cref="TransitionKinds.Styles"/>, or "mixed" (the editor's suggestion at each cut).</summary>
    public string TransitionStyle { get; set; } = TransitionKinds.None;
    /// <summary>The sound on those transitions: "auto" (one that suits the transition), "none", a sound, or "mixed".</summary>
    public string TransitionSound { get; set; } = "auto";
    /// <summary>Your picks for single cuts (they win over the style above).</summary>
    public List<CutPick> Cuts { get; set; } = new();
    /// <summary>The teaser up front (your best moments, fast) before the intro.</summary>
    public bool Hook { get; set; } = true;
    /// <summary>The last 20 s laid out for YouTube's end-screen elements (previous video, next video, subscribe).</summary>
    public bool EndScreen { get; set; } = true;
    /// <summary>Among Us: when you describe where you were in a meeting (on your mic), a fast replay of your round over it.</summary>
    public bool VarChecks { get; set; } = true;

    public CutPick Cut(double from)
    {
        var c = Cuts.FirstOrDefault(x => Math.Abs(x.From - from) < 0.5);
        if (c is null) { c = new CutPick { From = from }; Cuts.Add(c); }
        return c;
    }
    /// <summary>Every part brought to the same loudness (and big swings inside a part smoothed).</summary>
    public bool EvenLoudness { get; set; }
    /// <summary>Screen shake on slow-motion moments.</summary>
    public bool Shake { get; set; } = true;
    /// <summary>Black bars on the cold open.</summary>
    public bool Letterbox { get; set; } = true;
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

/// <summary>
/// What the editor learns from you: the final settings of every edit you make or revise (per game, the last version of
/// each counts). A new edit starts from what you've chosen consistently: two or more of your recent edits agreeing,
/// and the most recent ones counting most. Nothing here is a guess about you, only your own choices.
/// </summary>
public sealed class EditStyle
{
    public sealed class Sample
    {
        public string Project { get; set; } = "";
        public DateTime When { get; set; }
        public EditTweaks Tweaks { get; set; } = new();
    }

    public Dictionary<string, List<Sample>> Games { get; set; } = new();

    public static EditStyle Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<EditStyle>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Your edits' count per game.</summary>
    public int Count(string game) => Games.TryGetValue(game, out var l) ? l.Count : 0;

    public void Record(VodProject project)
    {
        if (!Games.TryGetValue(project.Game, out var list)) Games[project.Game] = list = new();
        list.RemoveAll(x => x.Project == project.Id);
        // (a copy, without what only makes sense for that one video)
        var t = JsonSerializer.Deserialize<EditTweaks>(JsonSerializer.Serialize(project.Tweaks))!;
        t.Title = null; t.Cards.Clear(); t.Remove.Clear(); t.Include.Clear(); t.Cuts.Clear(); t.Minutes = null;
        list.Add(new Sample { Project = project.Id, When = DateTime.UtcNow, Tweaks = t });
        if (list.Count > 12) list.RemoveRange(0, list.Count - 12);
    }

    /// <summary>The settings a new edit of this game starts from, and a line saying what was learned.</summary>
    public (EditTweaks Tweaks, string Note) Learned(string game)
    {
        var t = new EditTweaks();
        if (!Games.TryGetValue(game, out var all) || all.Count < 2) return (t, "");
        var recent = all.OrderByDescending(x => x.When).Take(8).Select(x => x.Tweaks).ToList();
        var notes = new List<string>();
        var d = new EditTweaks();
        // a choice you made in at least two edits and in most recent ones (the latest counting double)
        T? Pick<T>(Func<EditTweaks, T> get, string label, Func<T, string>? say = null)
        {
            T def = get(d);
            var votes = recent.Select((x, i) => (V: get(x), W: i == 0 ? 2.0 : 1.0)).ToList();
            var best = votes.GroupBy(v => v.V).Select(g => (g.Key, W: g.Sum(v => v.W), N: g.Count())).OrderByDescending(g => g.W).First();
            if (EqualityComparer<T>.Default.Equals(best.Key, def) || best.N < 2 || best.W <= votes.Sum(v => v.W) / 2) return def;
            notes.Add(say is null ? label : $"{label} {say(best.Key)}");
            return best.Key;
        }
        double Level(Func<EditTweaks, double> get, string label)
        {
            var changed = recent.Select(get).Where(v => Math.Abs(v - 1) > 0.05).ToList();
            if (changed.Count < 2 || changed.Count * 2 < recent.Count) return 1;
            changed.Sort();
            double m = changed[changed.Count / 2];
            notes.Add($"{label} at {m:P0}");
            return m;
        }
        t.Music = Pick(x => x.Music, "no music") ; if (t.Music) t.MusicVolume = Level(x => x.Music ? x.MusicVolume : 1, "music");
        t.SfxVolume = Level(x => x.SfxVolume, "sound effects");
        t.TransitionStyle = Pick(x => x.TransitionStyle, "transitions:", v => TransitionKinds.Name(v).ToLowerInvariant())!;
        t.TransitionSound = Pick(x => x.TransitionSound, "transition sound:", v => TransitionKinds.SoundName(v).ToLowerInvariant())!;
        t.TransitionSounds = Pick(x => x.TransitionSounds, "no transition sounds");
        t.EvenLoudness = Pick(x => x.EvenLoudness, "even loudness between parts");
        t.Shake = Pick(x => x.Shake, "no screen shake");
        t.Letterbox = Pick(x => x.Letterbox, "no black bars");
        t.SlowMotion = Pick(x => x.SlowMotion, "no slow motion");
        t.FaceCam = Pick(x => x.FaceCam, "no cuts to your face");
        t.ColdOpen = Pick(x => x.ColdOpen, "no cold open");
        t.Hook = Pick(x => x.Hook, "no hook");
        t.EndScreen = Pick(x => x.EndScreen, "no end screen");
        t.Captions = Pick(x => x.Captions, "no captions");
        t.Chapters = Pick(x => x.Chapters, "no chapters");
        t.VarChecks = Pick(x => x.VarChecks, "no VAR checks");
        t.Pace = Pick(x => x.Pace, "pace:", v => v < 0 ? "tighter" : "relaxed");
        t.Zooms = Pick(x => x.Zooms, "zooms:", v => v switch { 0 => "none", 1 => "fewer", 3 => "more", _ => "normal" });
        t.Emphasis = Pick(x => x.Emphasis, "focus:", v => v < 0 ? "more action" : "more funny moments");
        string note = notes.Count == 0 ? "" : $"From your last {recent.Count} {(game == "other" ? "" : game + " ")}edits: {string.Join(", ", notes)}.";
        return (t, note);
    }
}

/// <summary>Your choice for one cut (by where the incoming clip starts in the recording); null = as the edit decides.</summary>
public sealed class CutPick
{
    public double From { get; set; }
    public string? Style { get; set; }
    public string? Sound { get; set; }
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
    /// <summary>Made with chapter cards (and description chapters).</summary>
    public bool ChapterCards { get; set; } = true;
    public string Socials { get; set; } = "";
    public EditTweaks Tweaks { get; set; } = new();
    /// <summary>Whisper's words already written (source seconds) and the stretches they cover.</summary>
    public List<VodWord> CaptionWords { get; set; } = new();
    public List<double[]> CaptionCovered { get; set; } = new();
    // the last plan, for the review panel and for reading "at 3:20" in feedback
    public List<PieceInfo> Pieces { get; set; } = new();
    public List<ChapterInfo> Chapters { get; set; } = new();
    /// <summary>"Among Us" or "other": what the editor learns is kept per game.</summary>
    public string Game { get; set; } = "other";
    /// <summary>What this edit started from, learned from your earlier edits ("" = nothing yet).</summary>
    public string LearnedNote { get; set; } = "";
    /// <summary>The cuts between clips in the last plan (what each uses, what the editor suggests).</summary>
    public List<CutInfo> Cuts { get; set; } = new();
    public List<EditMoment> Moments { get; set; } = new();
    public double Duration { get; set; }

    public sealed record PieceInfo(string Role, double In, double Out, double At, double Length, double Speed);
    public sealed record ChapterInfo(int Index, double At, string Heading, string Sub);

    public string WorkDir => Path.Combine(OutputFolder, ".leapcast-work", Id);
    public static string PathFor(string video) => Path.Combine(Path.GetDirectoryName(video) ?? "", Path.GetFileNameWithoutExtension(video) + ".edit.json");
    private string SignalsPath => Path.Combine(WorkDir, "signals.json.gz");

    public VodEditOptions Options() => new(Tweaks.Title ?? Title, (Tweaks.Minutes ?? TargetSeconds / 60) * 60, Captions && Tweaks.Captions, Music && Tweaks.Music, Socials,
        ChapterCards && Tweaks.Chapters, Brand);

    /// <summary>Your channel's look when this edit was made (refreshed from your settings on a revision).</summary>
    public VodBrand Brand { get; set; } = new();

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
        Cuts = plan.Cuts.ToList();
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
    // Format 2: + the game's own signals (Among Us meetings and lobby, picture changes, the other players' words).
    // An older cache lacks them, and a revision from it lost the meetings/no-lobby rules: it's watched again instead.
    private const int SignalsFormat = 2;
    private sealed record SignalsFile(double Duration, int Width, int Height, double Fps, float[] Loud, float[] Motion, float[] Brightness,
        List<VodWord> Words, List<VodMarker> Markers, ClipLayout? Layout, bool WordsFromMic, bool HasAudio, long RecordingSize, DateTime RecordingTime,
        int Format = 0, float[]? Novelty = null, float[]? MeetingUi = null, float[]? Lobby = null, List<VodWord>? Others = null);

    /// <summary>Every version made from the same recording (this edit's original and its revisions): video and when it was made, newest first.</summary>
    public static List<(string Video, DateTime Made)> Versions(VodProject project)
    {
        var found = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var folders = new[] { project.OutputFolder, Path.GetDirectoryName(project.Video) ?? "" }.Where(f => f.Length > 0 && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
            foreach (var json in Directory.EnumerateFiles(folder, "*.edit.json"))
            {
                try
                {
                    string video = Path.Combine(folder, Path.GetFileName(json)[..^".edit.json".Length] + ".mp4");
                    if (!File.Exists(video)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(json));
                    if (doc.RootElement.TryGetProperty("Id", out var id) && id.GetString() == project.Id)
                        found[video] = File.GetLastWriteTime(video);
                }
                catch { }
            }
        if (project.Video.Length > 0 && File.Exists(project.Video)) found[project.Video] = File.GetLastWriteTime(project.Video);
        return found.OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).ToList();
    }

    public void SaveSignals(VodSignals s)
    {
        Directory.CreateDirectory(WorkDir);
        var fi = new FileInfo(Recording);
        var f = new SignalsFile(s.Duration, s.Width, s.Height, s.Fps, s.Loud, s.Motion, s.Brightness, s.Words, s.Markers, s.Layout, s.WordsFromMic, s.HasAudio,
            fi.Exists ? fi.Length : 0, fi.Exists ? fi.LastWriteTimeUtc : default, SignalsFormat, s.Novelty, s.MeetingUi, s.Lobby, s.Others);
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
            if (f is null || !fi.Exists || fi.Length != f.RecordingSize || f.Format < SignalsFormat) return null;
            return new VodSignals
            {
                Duration = f.Duration, Width = f.Width, Height = f.Height, Fps = f.Fps, Loud = f.Loud, Motion = f.Motion, Brightness = f.Brightness,
                Words = f.Words, Markers = f.Markers, Layout = f.Layout, WordsFromMic = f.WordsFromMic, HasAudio = f.HasAudio,
                Novelty = f.Novelty ?? Array.Empty<float>(), MeetingUi = f.MeetingUi ?? Array.Empty<float>(), Lobby = f.Lobby ?? Array.Empty<float>(),
                Others = f.Others ?? new(),
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
        var parts = Regex.Split(feedback, @"(?<=[.!?;\n])\s+|\n|,\s*(?:and\s+|but\s+)?also\s+|\s+(?:and|but|also|plus|then)\s+(?=(?:make|remove|cut|drop|add|include|rename|call|change|more|less|fewer|too|no|not|turn|put|keep|take|get|lose|kill|stop|fix|even|balance|normali[sz]e|lower|raise|boost|i|it|the|music|captions?|subtitles?|zooms?|slow|face|transitions?|sound|audio|volume|chapters?|pace|shake|whoosh))", RegexOptions.IgnoreCase)
            .Select(x => x.Trim().TrimEnd('.', '!', ';')).Where(x => x.Length > 1).ToList();
        foreach (var raw in parts)
        {
            string s = Normalize(raw);
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

            // ---- everything you can turn on/off, up/down: by what it's about and which way you want it
            Settings(s, t, project, ok);

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

    // ------------------------------------------------------------------ understanding what you mean

    /// <summary>Lower case, contractions spelled out, common typos and other words for the same thing mapped to the
    /// words the editor knows ("dont want the transistions sfx" → "do not want the transitions sound effects").</summary>
    public static string Normalize(string raw)
    {
        string s = " " + raw.ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'') + " ";
        s = Regex.Replace(s, @"\b(do|does|did|is|are|was|were|should|could|would|ca|wo|has|have|had)n'?t\b", m => m.Groups[1].Value switch { "ca" => "can not", "wo" => "will not", var x => x + " not" });
        s = Regex.Replace(s, @"\b(i|you|we|they|it|that|there)'(m|re|s|ve|ll|d)\b", "$1 $2");
        s = Regex.Replace(s, @"[^a-z0-9:%.\s'-]", " ");
        // fix typos word by word against the editor's vocabulary
        s = Regex.Replace(s, @"[a-z]{5,}", m => Fix(m.Value));
        s = Regex.Replace(s, @"\b(sfx|fx|swoosh(es)?|whoosh(es)?|woosh(es)?)\b", m => m.Value.StartsWith("s") && m.Value != "sfx" || m.Value.Contains("oosh") ? "whoosh" : "sound effects");
        s = Regex.Replace(s, @"\bsubs\b|\bsubtitles?\b|\bcc\b", "captions");
        s = Regex.Replace(s, @"\bbgm\b|\bsoundtrack\b|\bbackground (audio|track|song)s?\b|\bsongs?\b|\bbeats?\b", "music");
        s = Regex.Replace(s, @"\bclips?\b|\bsegments?\b|\bsections?\b|\bscenes?\b|\bbits\b|\bcuts\b(?=.*\b(loud|quiet|volume|level))", "parts");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static readonly string[] Vocabulary =
    {
        "transitions", "transition", "music", "captions", "chapters", "chapter", "zooms", "zooming", "slow", "motion", "loudness", "volume",
        "louder", "quieter", "sound", "sounds", "effects", "audio", "remove", "without", "shake", "shaking", "letterbox", "intro",
        "faster", "slower", "shorter", "longer", "funny", "action", "minutes", "include", "mismatch", "mismatched", "mismatching", "uneven",
        "inconsistent", "normalize", "balance", "levels", "webcam", "facecam", "camera", "teaser", "whoosh", "subtitles", "remove", "delete",
        "voices", "voice", "different", "louder", "gameplay", "meetings", "transitions", "cards", "title", "rename", "choppy", "jarring",
    };

    private static string Fix(string w)
    {
        if (Vocabulary.Contains(w)) return w;
        string? best = null; int bestD = int.MaxValue;
        foreach (var v in Vocabulary.Distinct())
        {
            if (Math.Abs(v.Length - w.Length) > 2) continue;
            int d = Distance(w, v);
            if (d < bestD) { bestD = d; best = v; }
        }
        int allowed = w.Length >= 8 ? 2 : 1;
        return best is not null && bestD <= allowed ? best : w;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    private static bool Has(string s, string pattern) => Regex.IsMatch(s, pattern);

    /// <summary>Wants it gone / off / less.</summary>
    private static bool Off(string s) => Has(s, @"\b(no|not|none|remove|removed|without|off|disable|mute|muted|kill|drop|lose|stop|get rid|take (out|away|off)|cut (out|the)|delete|ditch|hate|dislike|annoying|unnecessary|pointless|distracting|too much|too many|less|fewer|tone down|turn (it |them )?off|skip|nix|scrap|away with)\b");
    /// <summary>Wants it there / on / more.</summary>
    private static bool On(string s) => Has(s, @"\b(add|want|keep|bring back|turn (it |them )?on|enable|need|include|put|more|use|with|should have|love|like)\b") && !Off(s);

    /// <summary>The settings you can change, by what the sentence is about and which way you want it.</summary>
    private static void Settings(string s, EditTweaks t, VodProject project, List<string> ok)
    {
        bool lessWords = Has(s, @"\b(too (loud|much|many|strong|intense|often|frequent)|less|fewer|quieter|softer|lower|down|reduce|decrease|tone down|calm)\b");
        bool moreWords = Has(s, @"\b(too (quiet|soft|weak|subtle)|more|louder|higher|up|raise|increase|boost|stronger)\b") && !lessWords;

        // ---- loudness between parts ("the volume is all over the place", "some clips are louder than others")
        bool aboutLevels = Has(s, @"\b(loudness|volume|levels?|audio|sound|loud|quiet)\b");
        bool levelsRule = false;
        if ((aboutLevels && Has(s, @"\b(mismatch\w*|uneven|inconsistent|different|differ|varies|vary|varying|jumps?|jumping|all over|balance\w*|even( it| them)? out|evenly|normali[sz]\w*|match\w*|consistent|same level|same volume|equali[sz]\w*|spikes?|jarring|fluctuat\w*)\b"))
            || Has(s, @"\bsome parts\b.*\b(louder|quieter|loud|quiet)\b|\b(louder|quieter) than (the )?(others?|rest)\b|\bnormali[sz]e\b"))
        {
            bool off = Has(s, @"\b(do not|no|stop|without)\b.*\b(even|balanc|normali|match)");
            t.EvenLoudness = !off; levelsRule = true;
            ok.Add(off ? "Loudness left as recorded" : "Every part at the same loudness (loud parts brought down, quiet parts up, big jumps smoothed)");
        }
        // ---- "can't hear me / the voices over the music"
        bool cantHear = Has(s, @"\b(can not|hard to|barely|can t) hear\b|\bdrowns?\b|\bover the music\b") && !Has(s, @"\bmusic\b.*\b(too quiet|louder)\b");
        if (cantHear && !levelsRule)
        {
            t.MusicVolume = Math.Max(0.05, t.MusicVolume * 0.6);
            ok.Add($"Music quieter so the voices come through ({t.MusicVolume:P0} of the original level)");
        }

        // ---- transitions between clips: which look, which sound (or none), for all cuts or one ("a fade at 3:20")
        string? styleReq = Has(s, @"\btime ?lapses?\b|\btimelaps\w*|\bfast ?forward\w*|\bsped up\b|\bspeed(ing)? (it |the footage |the gameplay |things )?up\b") ? TransitionKinds.Timelapse
            : Has(s, @"\bglitch\w*") ? TransitionKinds.Glitch
            : Has(s, @"\bwhip\w*|\bswipes?\b|\bpans?\b") ? TransitionKinds.Whip
            : Has(s, @"\b(cross ?fades?|dissolves?|fades?|fading|dip to black)\b") && !Has(s, @"\bmusic\b") ? TransitionKinds.Fade
            : Has(s, @"\b(flash\w*|white)\b") && !Has(s, @"\bred flash") ? TransitionKinds.Flash
            : Has(s, @"\bblur\w*") ? TransitionKinds.Blur
            : Has(s, @"\bzoom\w*\b.*\btransitions?\b|\btransitions?\b.*\bzoom\w*|\bpunch\w*") ? TransitionKinds.Zoom
            : Has(s, @"\b(plain|hard|straight|simple|regular) cuts?\b|\bjust cut\b") ? TransitionKinds.None
            : null;
        bool variety = Has(s, @"\b(mix\w*|vary|varied|variety|variation\w*|different (ones|kinds|types|transitions|sounds)|random\w*|switch (it |them )?up|change (it |them )?up)\b");
        bool transitions = styleReq is not null && (styleReq != TransitionKinds.Zoom || Has(s, @"\btransition")) || Has(s, @"\btransitions?\b|\bwhoosh\b");
        if (styleReq == TransitionKinds.Blur && !Has(s, @"\btransition|\bbetween|\bcuts?\b")) transitions = Has(s, @"\btransitions?\b");
        bool soundWords = Has(s, @"\b(audio|sound|sounds|noise|noises|whoosh|sound effects|sfx)\b");
        string? soundReq = Has(s, @"\bsoft (whoosh|sound)") ? "soft" : Has(s, @"\bwhoosh") ? "whoosh" : Has(s, @"\bpops?\b") ? "pop"
            : Has(s, @"\b(impacts?|booms?|hits?|thuds?)\b") ? "impact" : Has(s, @"\brisers?\b|\brising\b") ? "riser" : null;
        if (transitions)
        {
            bool off = Off(s) && !Has(s, @"\bless (often|frequent)");
            bool on = !off && (On(s) || moreWords || styleReq is not null || variety || soundReq is not null);
            // a single cut: "use a fade at 3:20", "no transition at 5:02"
            var at = Regex.Matches(s, @"\b(\d{1,2}):(\d{2})\b").Select(m => int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value)).ToList();
            if (at.Count > 0 && project.Cuts.Count > 0)
            {
                foreach (var tm in at)
                {
                    var cut = project.Cuts.OrderBy(c => Math.Abs(c.At - tm)).First();
                    if (Math.Abs(cut.At - tm) > 6) continue;
                    var pick = t.Cut(cut.From);
                    if (soundWords && !Has(s, @"\btransitions?\b(?!.*\bsound)")) pick.Sound = off ? "none" : soundReq ?? TransitionKinds.SoundFor(styleReq ?? cut.Style);
                    else pick.Style = off ? TransitionKinds.None : styleReq ?? cut.Suggested;
                    ok.Add($"Cut at {Clock(cut.At)}: {(pick.Style is { } ps ? TransitionKinds.Name(ps) : TransitionKinds.Name(cut.Style))}{(pick.Sound is { } pso ? ", " + TransitionKinds.SoundName(pso).ToLowerInvariant() : "")}");
                }
                if (ok.Count > 0 && ok[^1].StartsWith("Cut at")) return;
            }
            if (soundWords && (styleReq is null || Has(s, @"\b(sound|audio|noise)s?\b.*\b(of|on|for|in) (the )?transitions?|\btransitions?\b.*\b(sound|audio|noise|whoosh)")))
            {
                if (off) { t.TransitionSound = "none"; t.TransitionSounds = false; ok.Add("No transition sounds (no whooshes, pops or risers on cuts, cards or the title)"); }
                else if (soundReq is not null) { t.TransitionSound = soundReq; t.TransitionSounds = true; ok.Add($"Transition sound: {TransitionKinds.SoundName(soundReq).ToLowerInvariant()}"); }
                else if (variety) { t.TransitionSound = TransitionKinds.Mixed; t.TransitionSounds = true; ok.Add("Transition sounds: varied"); }
                else if (on) { t.TransitionSound = "auto"; t.TransitionSounds = true; ok.Add("Transition sounds on (each one suits its transition)"); }
                if (styleReq is null || off) return;
            }
            if (off && (styleReq is null || styleReq == t.TransitionStyle || t.TransitionStyle == TransitionKinds.Mixed))
            {
                t.TransitionStyle = TransitionKinds.None; t.Cuts.ForEach(c => c.Style = null);
                ok.Add("No transitions: plain cuts between clips");
            }
            else if (off) ok.Add($"No {TransitionKinds.Name(styleReq!).ToLowerInvariant()} transitions (there weren't any)");
            else if (variety && styleReq is null) { t.TransitionStyle = TransitionKinds.Mixed; ok.Add("Varied transitions: the editor picks what suits each cut, never the same twice in a row"); }
            else if (styleReq is not null)
            {
                t.TransitionStyle = styleReq;
                ok.Add(styleReq == TransitionKinds.Timelapse
                    ? "Timelapse between clips: the time in between plays fast-forwarded (2–6 s, with the speed shown), so the whole game tells its story (never through a lobby)"
                    : styleReq == TransitionKinds.None ? "Plain cuts between clips"
                    : $"{TransitionKinds.Name(styleReq)} between clips" + (t.TransitionSound == "auto" && t.TransitionSounds ? $" (with a {TransitionKinds.SoundName(TransitionKinds.SoundFor(styleReq)).ToLowerInvariant()}; say “no transition sounds” to drop it)" : ""));
            }
            else if (on) { t.TransitionStyle = TransitionKinds.Mixed; ok.Add("Transitions on: the editor picks what suits each cut (varied)"); }
            return;
        }
        // ---- VAR checks (Among Us)
        if (Has(s, @"\bvar\b|\breplays?\b|\bshow (what i did|where i was|my path\w*)|\brecall\w*|\bpathing\b|\bproof\b"))
        {
            bool off = Off(s);
            t.VarChecks = !off;
            ok.Add(off ? "No VAR checks" : "VAR checks: when you say where you were in a meeting (on your mic), a fast replay of your round plays over it");
        }
        // ---- sound effects in general
        if (Has(s, @"\b(sound effects|whoosh|pops?|risers?|impacts?|booms?|dings?)\b"))
        {
            if (Off(s) && !lessWords) { t.SfxVolume = 0; ok.Add("No sound effects"); }
            else if (lessWords) { t.SfxVolume *= 0.6; ok.Add($"Sound effects quieter ({t.SfxVolume:P0})"); }
            else if (moreWords) { t.SfxVolume = Math.Min(2, Math.Max(0.6, t.SfxVolume) * 1.4); t.TransitionSounds = true; ok.Add($"Sound effects louder ({t.SfxVolume:P0})"); }
            else if (On(s)) { t.SfxVolume = Math.Max(1, t.SfxVolume); t.TransitionSounds = true; ok.Add("Sound effects on"); }
        }

        // ---- music
        if (Has(s, @"\bmusic\b") && !cantHear)
        {
            var pct = Regex.Match(s, @"(\d{1,3})\s*%");
            bool off = Off(s) && !lessWords && !Has(s, @"\bless music\b");
            if (off) { t.Music = false; ok.Add("No music"); }
            else if (moreWords || Has(s, @"\bmore music\b"))
            {
                double up = pct.Success ? 1 + int.Parse(pct.Groups[1].Value) / 100.0 : Has(s, @"\b(bit|little|slightly)\b") ? 1.2 : Has(s, @"\b(lot|much|way)\b") ? 1.6 : 1.4;
                t.MusicVolume = Math.Min(2, t.MusicVolume * up); t.Music = true;
                ok.Add($"Music louder ({t.MusicVolume:P0} of the original level)");
            }
            else if (lessWords || Has(s, @"\b(loud|overpower\w*|blasting|annoying|distracting)\b"))
            {
                double f = pct.Success ? int.Parse(pct.Groups[1].Value) / 100.0 : 0;
                double by = pct.Success ? (Has(s, @"\bto\s+\d") ? f : 1 - f)
                    : Has(s, @"\b(half)\b") ? 0.5 : Has(s, @"\b(lot|much|way|very)\b") ? 0.5 : Has(s, @"\b(bit|little|slightly)\b") ? 0.8 : 0.65;
                t.MusicVolume = Math.Max(0.05, t.MusicVolume * by);
                ok.Add($"Music quieter ({t.MusicVolume:P0} of the original level)");
            }
            else if (On(s)) { t.Music = true; ok.Add("Music on"); }
        }

        // ---- captions
        if (Has(s, @"\bcaptions?\b|\btext on screen\b|\bwords on screen\b"))
        {
            if (Off(s)) { t.Captions = false; ok.Add("No captions"); }
            else { t.Captions = true; ok.Add("Captions on"); }
        }
        // ---- chapters
        if (Has(s, @"\bchapters?\b|\bchapter cards?\b|\bpart cards?\b|\btitle cards\b") && !Has(s, @"(part|chapter|card)\s*\d"))
        {
            if (Off(s)) { t.Chapters = false; ok.Add("No chapters"); }
            else { t.Chapters = true; ok.Add("Chapters on"); }
        }

        // ---- length and pace
        var mins = Regex.Match(s, @"(\d{1,2}(?:\.\d)?)\s*(?:-|\s)?\s*min");
        if (mins.Success)
        {
            t.Minutes = Math.Clamp(double.Parse(mins.Groups[1].Value, CultureInfo.InvariantCulture), 1, 60);
            ok.Add($"About {t.Minutes:0.#} minutes long");
        }
        else if (Has(s, @"\b(shorter|too long|cut it down|trim it|condense)\b")) { t.Minutes = Math.Max(2, (t.Minutes ?? project.TargetSeconds / 60) - 3); ok.Add($"Shorter: about {t.Minutes:0} minutes"); }
        else if (Has(s, @"\b(longer|too short|extend)\b")) { t.Minutes = (t.Minutes ?? project.TargetSeconds / 60) + 3; ok.Add($"Longer: about {t.Minutes:0} minutes"); }
        if (Has(s, @"\b(faster|tighter|more cuts|drags?|dragging|too slow|boring|snappier|quicker|slow paced)\b")) { t.Pace = Math.Max(-1, t.Pace - 1); ok.Add("Tighter pace (more cuts, shorter moments)"); }
        else if (Has(s, @"\b(slower|relaxed|too many cuts|choppy|too fast|breathe|less cuts|fewer cuts|jumpy|rushed|hectic)\b") && !transitions) { t.Pace = Math.Min(1, t.Pace + 1); ok.Add("More relaxed pace (longer moments, fewer cuts)"); }

        // ---- effects
        if (Has(s, @"\bzoom"))
        {
            if (Has(s, @"\b(no|without|stop|remove|disable|off|none)\b") && !lessWords) { t.Zooms = 0; ok.Add("No zooms"); }
            else if (lessWords || Off(s)) { t.Zooms = Math.Max(0, t.Zooms - 1); ok.Add(t.Zooms == 0 ? "No zooms" : "Fewer zooms"); }
            else { t.Zooms = Math.Min(3, t.Zooms + 1); ok.Add("More zooms"); }
        }
        if (Has(s, @"slow[\s-]?mo(tion)?|\bslow down\b"))
        {
            bool off = Off(s);
            t.SlowMotion = !off; ok.Add(off ? "No slow motion" : "Slow motion on");
        }
        if (Has(s, @"\bshak(e|ing|y)\b|\bscreen shake\b|\bjitter"))
        {
            bool off = Off(s) || Has(s, @"\bshaky\b");
            t.Shake = !off; ok.Add(off ? "No screen shake" : "Screen shake on");
        }
        if (Has(s, @"\b(black bars|letterbox\w*|bars)\b"))
        {
            bool off = Off(s);
            t.Letterbox = !off; ok.Add(off ? "No black bars on the cold open" : "Black bars on the cold open");
        }
        if (Has(s, @"\b(face ?cam|webcam|my face|face zooms?|camera|cuts? to me)\b"))
        {
            bool off = Off(s);
            t.FaceCam = !off; ok.Add(off ? "No cuts to your face" : "Cuts to your face on");
        }
        if (Has(s, @"\bhooks?\b|teaser|attention grab"))
        {
            bool off = Off(s);
            t.Hook = !off; if (!off) t.ColdOpen = true;
            ok.Add(off ? "No hook at the start" : "Hook at the start (your best moments, fast)");
        }
        else if (Has(s, @"cold open|preview at the start"))
        {
            bool off = Off(s);
            t.ColdOpen = !off; ok.Add(off ? "No cold open" : "Cold open on");
        }
        if (Has(s, @"end ?screen|outro|end card|subscribe (screen|section|button)|(previous|recommended|next) video"))
        {
            bool off = Off(s);
            t.EndScreen = !off; ok.Add(off ? "Short ending (no end screen)" : "End screen on (20 s for YouTube's previous video, watch next and subscribe)");
        }
        if (Has(s, @"\b(chaotic|overwhelming|too much going on|too busy|over the top|overedited|over edited|too flashy|calmer|cleaner|simpler|subtle)\b"))
        {
            t.Zooms = Math.Max(0, t.Zooms - 1); t.Shake = false; t.SfxVolume = Math.Min(t.SfxVolume, 0.6);
            ok.Add("Calmer edit: fewer zooms, no screen shake, softer sound effects");
        }
        if (Has(s, @"\b(funnier|more funny|more laughs|more jokes|funny moments|more banter|more talking)\b")) { t.Emphasis = 1; ok.Add("More of the funny moments"); }
        else if (Has(s, @"\b(more action|more fights?|more combat|more gameplay|more boss|more kills|more of the game|more playing)\b")) { t.Emphasis = -1; ok.Add("More of the action"); }
        if (Has(s, @"\bmore (meetings|discussion|arguing|accusations)\b")) { t.Emphasis = 1; ok.Add("More of the meetings"); }
    }

    public static string Clock(double s) => s >= 3600 ? TimeSpan.FromSeconds(s).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"m\:ss");
    private static string Short(string q) => q.Length > 40 ? q[..40] + "…" : q;
}
