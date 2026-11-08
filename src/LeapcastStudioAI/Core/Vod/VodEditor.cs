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

    public static Task<VodRenderer.Result> MakeAsync(string ffmpeg, string recording, string outputFolder, VodEditOptions options,
        Func<int, int, double, IReadOnlyList<string>> videoArgs, IProgress<Progress>? progress, CancellationToken ct)
    {
        var project = new VodProject
        {
            Recording = recording, OutputFolder = outputFolder, Title = options.Title, TargetSeconds = options.TargetSeconds,
            Captions = options.Captions, Music = options.Music, Socials = options.Socials,
        };
        CleanOldWork(outputFolder);
        return RunAsync(ffmpeg, project, videoArgs, progress, ct, revise: false);
    }

    /// <summary>
    /// Makes the edit again with your changes (<see cref="VodProject.Tweaks"/>): nothing is watched again, captions
    /// already written are kept, and pieces that didn't change are reused, so a revision takes a fraction of the time.
    /// </summary>
    public static Task<VodRenderer.Result> ReviseAsync(string ffmpeg, VodProject project,
        Func<int, int, double, IReadOnlyList<string>> videoArgs, IProgress<Progress>? progress, CancellationToken ct) =>
        RunAsync(ffmpeg, project, videoArgs, progress, ct, revise: true);

    private static async Task<VodRenderer.Result> RunAsync(string ffmpeg, VodProject project,
        Func<int, int, double, IReadOnlyList<string>> videoArgs, IProgress<Progress>? progress, CancellationToken ct, bool revise)
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

        // 2. the edit
        progress?.Report(new Progress(0.25, revise ? "Re-planning with your changes" : "Finding the best moments"));
        var plan = VodPlanner.Plan(signals, options, project.Tweaks);
        Took($"planning ({plan.Pieces.Count} pieces, {TimeSpan.FromSeconds(plan.Duration):mm\\:ss} long)");

        // 3. precise captions, only for what made the cut (and only what isn't written already)
        if (options.Captions && Whisper.Ready)
        {
            await CaptionAsync(ffmpeg, project, plan, signals,
                new Progress<double>(f => progress?.Report(new Progress(0.25 + 0.1 * f, $"Writing the captions ({f:P0})"))), ct);
            Took("captions (Whisper)");
        }

        // 4. render (pieces that didn't change are reused)
        string baseName = revise && project.Video.Length > 0 ? NextVersion(project.Video) : $"{Safe(options.Title)} - {DateTime.Now:yyyy-MM-dd HH-mm}";
        var result = await VodRenderer.RenderAsync(ffmpeg, recording, plan, project.OutputFolder, baseName, videoArgs(plan.Width, plan.Height, plan.Fps), signals,
            new Progress<(double, string)>(x => progress?.Report(new Progress(0.35 + 0.65 * x.Item1, x.Item2))), ct, Path.Combine(project.WorkDir, "pieces"));
        Took($"rendering ({VodRenderer.Fallbacks} fallback(s), {VodRenderer.Reused} of {plan.Pieces.Count} pieces reused)");
        VodRenderer.Note($"Done in {total.Elapsed:h\\:mm\\:ss}");
        project.Video = result.Video;
        project.Remember(plan);
        try { project.Save(); } catch { }
        LastProject = project;
        return result;
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
