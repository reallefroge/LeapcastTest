using System.Diagnostics;
using System.Net.Http;

namespace LeapcastStudioAI.Vod;

/// <summary>
/// The on-demand VOD editor: a long recording in, an edited video out (about 15 minutes by default), with a
/// thumbnail and a description with chapters next to it. Full speed when you're not live; while you stream or
/// record it slows itself right down (idle priority, few threads) so the stream never stutters.
/// </summary>
public static class VodEditor
{
    public sealed record Progress(double Fraction, string Step);

    /// <summary>The edit just made or revised (for the review panel).</summary>
    public static VodProject? LastProject { get; private set; }

    /// <param name="reviewFirst">Plan the edit and stop (returns null): the review panel shows it, with the editor's
    /// suggestion for every cut, and you render it when it's how you want it.</param>
    public static Task<VodRenderer.Result?> MakeAsync(string ffmpeg, string recording, string outputFolder, VodEditOptions options,
        Func<int, int, double, IReadOnlyList<string>> videoArgs, IProgress<Progress>? progress, CancellationToken ct, bool reviewFirst = false)
    {
        var project = new VodProject
        {
            Recording = recording, OutputFolder = outputFolder, Title = options.Title, TargetSeconds = options.TargetSeconds,
            Captions = options.Captions, Music = options.Music, Socials = options.Socials, ChapterCards = options.Chapters,
            Brand = options.Brand ?? new VodBrand(),
        };
        CleanOldWork(outputFolder);
        return RunAsync(ffmpeg, project, videoArgs, progress, ct, revise: false, planOnly: reviewFirst);
    }

    /// <summary>Your channel's look as it is right now (the Wizard101 kit can grow during an edit); set by the app.</summary>
    public static Func<VodBrand>? BrandProvider { get; set; }
    /// <summary>Wizard101 recordings feed the kit (music stretches, the intro) as they're edited.</summary>
    public static bool HarvestKit { get; set; }

    /// <summary>Where what the editor learns about your taste is kept (set by the app; null = nothing learned or used).</summary>
    public static string? StylePath { get; set; }

    /// <summary>
    /// Makes the edit again with your changes (<see cref="VodProject.Tweaks"/>): nothing is watched again, captions
    /// already written are kept, and pieces that didn't change are reused, so a revision takes a fraction of the time.
    /// </summary>
    public static async Task<VodRenderer.Result> ReviseAsync(string ffmpeg, VodProject project,
        Func<int, int, double, IReadOnlyList<string>> videoArgs, IProgress<Progress>? progress, CancellationToken ct) =>
        (await RunAsync(ffmpeg, project, videoArgs, progress, ct, revise: true))!;

    private static async Task<VodRenderer.Result?> RunAsync(string ffmpeg, VodProject project,
        Func<int, int, double, IReadOnlyList<string>> videoArgs, IProgress<Progress>? progress, CancellationToken ct, bool revise, bool planOnly = false)
    {
        var total = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();
        void Took(string what) { VodRenderer.Note($"{what}: {step.Elapsed:mm\\:ss}{(BackgroundWork.Live ? " (live: slowed down)" : "")}"); step.Restart(); }
        string recording = project.Recording;
        var options = project.Options();
        VodRenderer.Note($"{(revise ? "Revising" : "Editing")} {Path.GetFileName(recording)} ({Environment.ProcessorCount} CPU threads)");

        // 1. watch and listen (once: a revision uses what was learned the first time)
        var signals = revise ? project.LoadSignals() : null;
        if (signals is null)
        {
            progress?.Report(new Progress(0, "Watching the recording"));
            // the caption model, once (≈148 MB)
            if (options.Captions && Whisper.Installed && !Whisper.Ready)
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
                    await Whisper.EnsureModelAsync(http, new Progress<double>(f => progress?.Report(new Progress(0, $"Downloading the caption model, once ({f:P0})"))), ct);
                    Took("caption model download");
                }
                catch (OperationCanceledException) { throw; }
                catch { /* captions from the live recognizer instead */ }
            // the quick recognizer finds the moments; Whisper only writes the captions later
            signals = await VodAnalyzer.AnalyzeAsync(ffmpeg, recording,
                new Progress<double>(f => progress?.Report(new Progress(0.25 * f, $"Watching the recording ({f:P0})"))), ct, useWhisper: false);
            try { project.SaveSignals(signals); } catch { }
            Took($"watching {TimeSpan.FromSeconds(signals.Duration):h\\:mm\\:ss} of recording");
        }

        // the game, and (on a new edit) what you've liked in your edits of it before
        VodPlanner.GamePhases(signals, Math.Max(1, (int)Math.Ceiling(signals.Duration / VodPlanner.Bin)), out bool amongUs);
        project.Game = amongUs ? "Among Us" : "other";
        // a Wizard101 stream feeds your kit (the game's music where nobody talks; the intro, the first time)
        if (!amongUs && HarvestKit)
        {
            progress?.Report(new Progress(0.24, "Adding this stream's music to your Wizard101 kit"));
            try
            {
                var added = await VodKit.HarvestAsync(ffmpeg, recording, signals, ct);
                if (added.Count > 0) VodRenderer.Note("Wizard101 kit: added " + string.Join(", ", added));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { VodRenderer.Note("Wizard101 kit: " + ex.Message); }
        }
        if (BrandProvider is not null) project.Brand = BrandProvider();
        if (!revise && StylePath is { } sp)
        {
            var (learned, note) = EditStyle.Load(sp).Learned(project.Game);
            project.Tweaks = learned;
            project.LearnedNote = note;
            if (note.Length > 0) VodRenderer.Note("learned: " + note);
        }

        // 2. the edit
        progress?.Report(new Progress(0.25, revise ? "Re-planning with your changes" : "Finding the best moments"));
        var plan = VodPlanner.Plan(signals, options, project.Tweaks);
        Took($"planning ({plan.Pieces.Count} pieces, {TimeSpan.FromSeconds(plan.Duration):mm\\:ss} long)");

        // a revision whose changes don't change the video isn't rendered again (it would be the same file)
        string print = PlanPrint(plan, project.Tweaks);
        if (revise && !planOnly && project.Video.Length > 0 && File.Exists(project.Video) && project.PlanPrint == print)
        {
            VodRenderer.Note("Revision: nothing in the video changes; not rendered again");
            LastProject = project;
            return new VodRenderer.Result(project.Video, Path.ChangeExtension(project.Video, ".jpg"), Path.ChangeExtension(project.Video, ".txt"),
                TimeSpan.FromSeconds(plan.Duration)) { Unchanged = true };
        }

        if (planOnly)
        {
            // review first: nothing rendered yet; the review panel shows the plan and its suggestions
            project.Remember(plan);
            LastProject = project;
            VodRenderer.Note($"Planned for review in {total.Elapsed:h\\:mm\\:ss}");
            return null;
        }

        // 3 + 4. precise captions (only for what made the cut, only what isn't written already) while the pieces render
        // (pieces don't need them; the music and the text at the end do)
        Task? captions = null;
        if (options.Captions && Whisper.Ready)
        {
            var capWatch = Stopwatch.StartNew();
            captions = Task.Run(async () =>
            {
                await CaptionAsync(ffmpeg, project, plan, signals, new Progress<double>(_ => { }), ct);
                VodRenderer.Note($"captions (Whisper, alongside the pieces): {capWatch.Elapsed:mm\\:ss}");
            }, ct);
        }
        // the clips are watched before the video is made from them: one that's frozen, black or silent is swapped
        // for the next best moment (twice at most), and the captions follow the final choice
        string pieceCache = Path.Combine(project.WorkDir, "pieces");
        var swapped = new List<string>();
        for (int round = 0; round < 3; round++)
        {
            progress?.Report(new Progress(0.3, round == 0 ? "Making the clips" : "Making the replacement clips"));
            var files = await VodRenderer.PiecesAsync(ffmpeg, recording, plan, videoArgs(plan.Width, plan.Height, plan.Fps),
                new Progress<(double, string)>(x => progress?.Report(new Progress(0.3 + 0.45 * x.Item1, x.Item2))), ct, pieceCache, project.WorkDir);
            progress?.Report(new Progress(0.66, "Watching the clips back (frozen, black or silent ones are replaced)"));
            var bad = await VodRenderer.BadPiecesAsync(ffmpeg, plan, files, ct);
            if (bad.Count == 0 || round == 2) break;
            foreach (var (i, why) in bad)
            {
                var p = plan.Pieces[i];
                project.Tweaks.Remove.Add(new[] { p.In - 0.5, p.Out + 0.5 });
                swapped.Add($"{EditFeedback.Clock(p.At)} ({why})");
            }
            VodRenderer.Note("clip check: replaced " + string.Join(", ", bad.Select(b => $"piece {b.Index + 1} ({b.Why})")));
            plan = VodPlanner.Plan(signals, options, project.Tweaks);
            if (captions is not null) { await captions; captions = CaptionAsync(ffmpeg, project, plan, signals, new Progress<double>(_ => { }), ct); }
        }
        print = PlanPrint(plan, project.Tweaks);
        string baseName = revise && project.Video.Length > 0 ? NextVersion(project.Video) : $"{Safe(options.Title)} - {DateTime.Now:yyyy-MM-dd HH-mm}";
        var result = await VodRenderer.RenderAsync(ffmpeg, recording, plan, project.OutputFolder, baseName, videoArgs(plan.Width, plan.Height, plan.Fps), signals,
            new Progress<(double, string)>(x => progress?.Report(new Progress(0.3 + 0.7 * x.Item1, x.Item2))), ct, Path.Combine(project.WorkDir, "pieces"), captions);
        Took($"rendering ({VodRenderer.Fallbacks} fallback(s), {VodRenderer.Reused} of {plan.Pieces.Count} pieces reused, pieces encoded with {VodRenderer.PieceEncoder})");
        VodRenderer.Note($"Done in {total.Elapsed:h\\:mm\\:ss}");
        project.Video = result.Video;
        project.Remember(plan);
        project.PlanPrint = print;
        project.QualityNote = "Checked before saving: " + (swapped.Count > 0 ? $"{swapped.Count} clip(s) swapped for better ones ({string.Join(", ", swapped)}); " : "every clip watched back (none frozen, black or silent); ")
                              + VodRenderer.LoudnessCheck + ".";
        VodRenderer.Note(project.QualityNote);
        try { project.Save(); } catch { }
        // every video you make or revise teaches the editor your taste (the last version of each edit counts)
        if (StylePath is { } stylePath) try { var st = EditStyle.Load(stylePath); st.Record(project); st.Save(stylePath); } catch { }
        LastProject = project;
        return result;
    }

    /// <summary>
    /// A quick, small draft of the edit as it stands (640×360, 30 fps, fast encode, captions as heard live): to watch
    /// it through and say what to change before the real render. Its pieces are kept, so a second preview after a
    /// change only makes what changed. Returns the preview file.
    /// </summary>
    public static async Task<string> PreviewAsync(string ffmpeg, VodProject project, IProgress<Progress>? progress, CancellationToken ct)
    {
        var signals = project.LoadSignals() ?? throw new InvalidOperationException("The recording's analysis isn't there any more: make the edit again.");
        if (BrandProvider is not null) project.Brand = BrandProvider();
        var plan = VodPlanner.Plan(signals, project.Options(), project.Tweaks);
        // the clips watched back first, as for the real video (a frozen, black or silent one is swapped)
        string cache = Path.Combine(project.WorkDir, "preview-pieces");
        var draftArgs = new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-pix_fmt", "yuv420p" };
        for (int round = 0; round < 2; round++)
        {
            var s0 = plan.Resized(640, 360, 30);
            var files = await VodRenderer.PiecesAsync(ffmpeg, project.Recording, s0, draftArgs,
                new Progress<(double, string)>(x => progress?.Report(new Progress(0.6 * x.Item1, "Preview: " + x.Item2))), ct, cache, project.WorkDir);
            progress?.Report(new Progress(0.6, "Preview: watching the clips back"));
            var bad = await VodRenderer.BadPiecesAsync(ffmpeg, s0, files, ct);
            if (bad.Count == 0) break;
            foreach (var (i, _) in bad) project.Tweaks.Remove.Add(new[] { plan.Pieces[i].In - 0.5, plan.Pieces[i].Out + 0.5 });
            plan = VodPlanner.Plan(signals, project.Options(), project.Tweaks);
        }
        project.Remember(plan);
        var small = plan.Resized(640, 360, 30);
        string folder = Path.Combine(project.WorkDir, "preview");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder, "*.mp4")) try { File.Delete(old); } catch { } // (one playing can't go: it does next time)
        var result = await VodRenderer.RenderAsync(ffmpeg, project.Recording, small, folder, $"preview {DateTime.Now:HHmmss}",
            draftArgs, signals,
            new Progress<(double, string)>(x => progress?.Report(new Progress(x.Item1, "Preview: " + x.Item2))), ct,
            Path.Combine(project.WorkDir, "preview-pieces"), draft: true);
        return result.Video;
    }

    /// <summary>Everything that decides what the video looks and sounds like (two plans with the same print make the same video).</summary>
    public static string PlanPrint(EditPlan plan, EditTweaks tw)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            plan.Width, plan.Height, plan.Fps, plan.Title, plan.Pieces, plan.Texts, plan.Sfx, plan.Music, plan.MusicVolume, plan.SfxVolume, plan.Brand, plan.EvenLoudness,
            Chapters = plan.Chapters.Select(c => $"{c.At:0.###} {c.Title}").ToList(), CaptionsOn = tw.Captions, ChaptersOn = tw.Chapters,
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))[..24];
    }

    /// <summary>"Title - date.mp4" → "Title - date (2)", then (3)…</summary>
    private static string NextVersion(string video)
    {
        string name = Path.GetFileNameWithoutExtension(video), dir = Path.GetDirectoryName(video) ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*) \((\d+)\)$");
        string stem = m.Success ? m.Groups[1].Value : name;
        int v = m.Success ? int.Parse(m.Groups[2].Value) + 1 : 2;
        while (File.Exists(Path.Combine(dir, $"{stem} ({v}).mp4"))) v++;
        return $"{stem} ({v})";
    }

    /// <summary>Revision caches older than two weeks go (the pieces take room).</summary>
    private static void CleanOldWork(string outputFolder)
    {
        try
        {
            var root = new DirectoryInfo(Path.Combine(outputFolder, ".leapcast-work"));
            if (!root.Exists) return;
            foreach (var d in root.GetDirectories().Where(d => d.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14))) d.Delete(true);
        }
        catch { }
    }

    /// <summary>
    /// Whisper on just the stretches in the edit (from your mic's own track when there is one), timed by the
    /// recognizer's words; a stretch Whisper can't do keeps the recognizer's words.
    /// </summary>
    private static async Task CaptionAsync(string ffmpeg, VodProject project, EditPlan plan, VodSignals signals, IProgress<double> progress, CancellationToken ct)
    {
        string recording = project.Recording;
        bool Covered(double a, double b) => project.CaptionCovered.Any(c => c[0] <= a + 0.01 && c[1] >= b - 0.01);
        var side = VodSidecar.Load(recording);
        string? mic = side?.MicFile is { } mf ? Path.Combine(Path.GetDirectoryName(recording) ?? "", mf) : null;
        bool fromMic = mic is not null && File.Exists(mic);
        double off = fromMic ? side!.MicOffset : 0;

        // the captioned stretches, a little padded, joined when they nearly touch
        var ranges = new List<(double A, double B)>();
        foreach (var (a, b) in VodPlanner.CaptionedPieces(plan).Select(p => (Math.Max(0, p.In - 0.6), Math.Min(signals.Duration, p.Out + 0.6))).OrderBy(r => r.Item1))
        {
            if (ranges.Count > 0 && a <= ranges[^1].B + 1.5) ranges[^1] = (ranges[^1].A, Math.Max(ranges[^1].B, b));
            else ranges.Add((a, b));
        }
        // at most ~28 s per Whisper pass: it listens to the list of names (Khrysalis…) only at the start of each 30 s
        // window, so a short pass keeps it in mind the whole way. Cut where nobody's talking.
        var chunks = new List<(double A, double B)>();
        foreach (var (ra, rb) in ranges)
        {
            double cur = ra;
            while (rb - cur > 28)
            {
                double cut = cur + 28, gapBest = -1;
                var near = signals.Words.Where(w => w.End > cur + 18 && w.Start < cur + 28).OrderBy(w => w.Start).ToList();
                for (int k = 0; k + 1 < near.Count; k++)
                {
                    double g = near[k + 1].Start - near[k].End;
                    if (g > gapBest) { gapBest = g; cut = (near[k].End + near[k + 1].Start) / 2; }
                }
                chunks.Add((cur, cut));
                cur = cut;
            }
            chunks.Add((cur, rb));
        }
        ranges = chunks;
        double totalLen = Math.Max(1, ranges.Sum(r => r.B - r.A)), doneLen = 0;
        var words = new List<VodWord>();
        // already written (a revision): those words, no Whisper
        foreach (var (a, b) in ranges.Where(r => Covered(r.A, r.B)))
            words.AddRange(project.CaptionWords.Where(w => w.Start >= a && w.Start < b));
        ranges = ranges.Where(r => !Covered(r.A, r.B)).ToList();
        totalLen = Math.Max(1, ranges.Sum(r => r.B - r.A));
        var fresh = new List<VodWord>();
        foreach (var (a, b) in ranges)
        {
            ct.ThrowIfCancellationRequested();
            double fileFrom = Math.Max(0, a - off), len = b - a;
            var inRange = signals.Words.Where(w => w.Start >= a && w.Start < b).ToList();
            try
            {
                // times inside the stretch's own audio
                var anchors = inRange.Select(w => w with { Start = w.Start - off - fileFrom, End = w.End - off - fileFrom }).ToList();
                double before = doneLen;
                var got = await Whisper.TranscribeAsync(ffmpeg, fromMic ? mic! : recording,
                    new Progress<double>(f => progress.Report((before + f * len) / totalLen)), ct, fileFrom, len, anchors, fast: true);
                var placed = got.Select(w => w with { Start = w.Start + fileFrom + off, End = w.End + fileFrom + off }).ToList();
                fresh.AddRange(placed);
                project.CaptionCovered.Add(new[] { a, b });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                VodRenderer.Note($"captions {a:0}–{b:0} s: Whisper failed ({ex.Message}); the recognizer's words are used");
                words.AddRange(inRange);
            }
            doneLen += len;
            progress.Report(doneLen / totalLen);
        }
        // anything the game said through your speakers that leaked into the mic
        if (side is { Desktop.Count: > 0 })
        {
            var t0 = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            CaptionWord C(VodWord w) => new(w.Text, t0.AddSeconds(w.Start), t0.AddSeconds(w.End));
            fresh = Echo.Remove(fresh.Select(C).ToList(), side.Desktop.Select(C).ToList())
                .Select(w => new VodWord(w.Text, (w.StartUtc - t0).TotalSeconds, (w.EndUtc - t0).TotalSeconds)).ToList();
        }
        fresh = Vocabulary.Fix(fresh);
        project.CaptionWords.AddRange(fresh);
        words.AddRange(fresh);
        words.Sort((x, y) => x.Start.CompareTo(y.Start));
        VodPlanner.PlaceCaptions(plan, Vocabulary.Fix(words));
    }

    private static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var t = new string(s.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim();
        return t.Length == 0 ? "Stream highlights" : t.Length > 60 ? t[..60].Trim() : t;
    }
}
