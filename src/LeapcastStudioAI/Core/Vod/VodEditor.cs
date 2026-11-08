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

    public static async Task<VodRenderer.Result> MakeAsync(string ffmpeg, string recording, string outputFolder, VodEditOptions options,
        Func<int, int, double, IReadOnlyList<string>> videoArgs, IProgress<Progress>? progress, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();
        void Took(string what) { VodRenderer.Note($"{what}: {step.Elapsed:mm\\:ss}{(BackgroundWork.Live ? " (live: slowed down)" : "")}"); step.Restart(); }
        VodRenderer.Note($"Editing {Path.GetFileName(recording)} ({Environment.ProcessorCount} CPU threads)");

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

        // 1. watch and listen (the quick recognizer finds the moments; Whisper only writes the captions later)
        var signals = await VodAnalyzer.AnalyzeAsync(ffmpeg, recording,
            new Progress<double>(f => progress?.Report(new Progress(0.25 * f, $"Watching the recording ({f:P0})"))), ct, useWhisper: false);
        Took($"watching {TimeSpan.FromSeconds(signals.Duration):h\\:mm\\:ss} of recording");

        // 2. the edit
        progress?.Report(new Progress(0.25, "Finding the best moments"));
        var plan = VodPlanner.Plan(signals, options);
        Took($"planning ({plan.Pieces.Count} pieces, {TimeSpan.FromSeconds(plan.Duration):mm\\:ss} long)");

        // 3. precise captions, only for what made the cut
        if (options.Captions && Whisper.Ready)
        {
            await CaptionAsync(ffmpeg, recording, plan, signals,
                new Progress<double>(f => progress?.Report(new Progress(0.25 + 0.1 * f, $"Writing the captions ({f:P0})"))), ct);
            Took("captions (Whisper)");
        }

        // 4. render
        string baseName = $"{Safe(options.Title)} - {DateTime.Now:yyyy-MM-dd HH-mm}";
        var result = await VodRenderer.RenderAsync(ffmpeg, recording, plan, outputFolder, baseName, videoArgs(plan.Width, plan.Height, plan.Fps), signals,
            new Progress<(double, string)>(x => progress?.Report(new Progress(0.35 + 0.65 * x.Item1, x.Item2))), ct);
        Took($"rendering ({VodRenderer.Fallbacks} fallback(s))");
        VodRenderer.Note($"Done in {total.Elapsed:h\\:mm\\:ss}");
        return result;
    }

    /// <summary>
    /// Whisper on just the stretches in the edit (from your mic's own track when there is one), timed by the
    /// recognizer's words; a stretch Whisper can't do keeps the recognizer's words.
    /// </summary>
    private static async Task CaptionAsync(string ffmpeg, string recording, EditPlan plan, VodSignals signals, IProgress<double> progress, CancellationToken ct)
    {
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
        double totalLen = Math.Max(1, ranges.Sum(r => r.B - r.A)), doneLen = 0;
        var words = new List<VodWord>();
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
                words.AddRange(got.Select(w => w with { Start = w.Start + fileFrom + off, End = w.End + fileFrom + off }));
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
            words = Echo.Remove(words.Select(C).ToList(), side.Desktop.Select(C).ToList())
                .Select(w => new VodWord(w.Text, (w.StartUtc - t0).TotalSeconds, (w.EndUtc - t0).TotalSeconds)).ToList();
        }
        VodPlanner.PlaceCaptions(plan, Vocabulary.Fix(words));
    }

    private static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var t = new string(s.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim();
        return t.Length == 0 ? "Stream highlights" : t.Length > 60 ? t[..60].Trim() : t;
    }
}
