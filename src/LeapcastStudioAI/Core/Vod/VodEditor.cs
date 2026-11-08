using System.Net.Http;

namespace LeapcastStudioAI.Vod;

/// <summary>
/// The on-demand VOD editor: a long recording in, an edited video out (about 15 minutes by default), with a
/// thumbnail and a description with chapters next to it. Runs at low priority so a stream can keep going.
/// </summary>
public static class VodEditor
{
    public sealed record Progress(double Fraction, string Step);

    public static async Task<VodRenderer.Result> MakeAsync(string ffmpeg, string recording, string outputFolder, VodEditOptions options,
        Func<int, int, double, IReadOnlyList<string>> videoArgs, IProgress<Progress>? progress, CancellationToken ct)
    {
        progress?.Report(new Progress(0, "Watching the recording"));
        // the caption model, once (≈148 MB)
        if (Whisper.Installed && !Whisper.Ready)
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
                await Whisper.EnsureModelAsync(http, new Progress<double>(f => progress?.Report(new Progress(0, $"Downloading the caption model, once ({f:P0})"))), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* captions from the live recognizer instead */ }
        var signals = await VodAnalyzer.AnalyzeAsync(ffmpeg, recording,
            new Progress<double>(f => progress?.Report(new Progress(0.3 * f, $"Watching the recording ({f:P0})"))), ct);
        progress?.Report(new Progress(0.3, "Finding the best moments"));
        var plan = VodPlanner.Plan(signals, options);
        string baseName = $"{Safe(options.Title)} - {DateTime.Now:yyyy-MM-dd HH-mm}";
        return await VodRenderer.RenderAsync(ffmpeg, recording, plan, outputFolder, baseName, videoArgs(plan.Width, plan.Height, plan.Fps), signals,
            new Progress<(double, string)>(x => progress?.Report(new Progress(0.3 + 0.7 * x.Item1, x.Item2))), ct);
    }

    private static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var t = new string(s.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim();
        return t.Length == 0 ? "Stream highlights" : t.Length > 60 ? t[..60].Trim() : t;
    }
}
