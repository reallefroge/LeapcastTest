using System.Diagnostics;
using System.Globalization;

namespace LeapcastStudioAI;

/// <summary>
/// Measures how late a camera picture is compared with the sound, from a short recording of a few claps (or any
/// sharp sound with matching motion): each clap is a sudden sound and a burst of motion in the camera's area.
/// </summary>
public static class SyncAnalyzer
{
    public sealed record Result(double OffsetMs, int Events, double SpreadMs)
    {
        /// <summary>Positive: the picture is late (the camera needs to be shown earlier by this much).</summary>
        public bool Reliable => Events >= 2 && SpreadMs < 160;
    }

    /// <param name="rect">The camera's area in the video, in video pixels.</param>
    public static async Task<Result?> MeasureAsync(string ffmpeg, string file, LayoutRect rect, int videoW, int videoH, CancellationToken ct = default)
    {
        int x = Math.Clamp((int)rect.X, 0, videoW - 16), y = Math.Clamp((int)rect.Y, 0, videoH - 16);
        int w = Math.Clamp((int)rect.W, 16, videoW - x), h = Math.Clamp((int)rect.H, 16, videoH - y);
        // keep a margin away from the edges (frames / borders don't move)
        int mx = w / 10, my = h / 10;
        const int gw = 32, gh = 18, fps = 30;
        byte[] luma = await RawAsync(ffmpeg, new[] { "-v", "error", "-i", file, "-map", "0:v:0",
            "-vf", $"fps={fps}:start_time=0,crop={w - 2 * mx}:{h - 2 * my}:{x + mx}:{y + my},scale={gw}:{gh}:flags=area,format=gray",
            "-f", "rawvideo", "-" }, ct);
        byte[] pcm = await RawAsync(ffmpeg, new[] { "-v", "error", "-i", file, "-map", "0:a:0",
            "-af", "aresample=async=1:first_pts=0", "-ac", "1", "-ar", "8000", "-f", "s16le", "-" }, ct);
        return Analyze(luma, gw * gh, fps, pcm, 8000);
    }

    /// <summary>Pure analysis (unit-testable): grey frames of <paramref name="frameSize"/> bytes, mono s16 audio.</summary>
    public static Result? Analyze(byte[] luma, int frameSize, double fps, byte[] pcm, int rate)
    {
        int frames = luma.Length / frameSize;
        if (frames < 10 || pcm.Length < rate) return null;
        // motion per frame: mean absolute change from the previous frame
        var motion = new double[frames];
        for (int i = 1; i < frames; i++)
        {
            long sum = 0;
            int a = (i - 1) * frameSize, b = i * frameSize;
            for (int j = 0; j < frameSize; j++) sum += Math.Abs(luma[b + j] - luma[a + j]);
            motion[i] = sum / (double)frameSize;
        }
        // audio envelope in 10 ms windows
        int win = rate / 100;
        int windows = pcm.Length / 2 / win;
        var env = new double[windows];
        for (int k = 0; k < windows; k++)
        {
            double sum = 0;
            for (int j = 0; j < win; j++) sum += Math.Abs((int)BitConverter.ToInt16(pcm, (k * win + j) * 2));
            env[k] = sum / win;
        }
        double envMedian = Median(env), motionMedian = Median(motion.Skip(1));
        double envPeak = env.Max();
        double threshold = Math.Max(envMedian * 6, envPeak * 0.3);
        if (envPeak < 600) return null; // nothing loud enough

        // onsets: the envelope crosses the threshold after ≥ 0.25 s below it
        var onsets = new List<double>();
        double lastLoud = -10;
        for (int k = 1; k < windows; k++)
        {
            double t = k / 100.0;
            if (env[k] >= threshold && env[k - 1] < threshold && t - lastLoud > 0.25) onsets.Add(t);
            if (env[k] >= threshold * 0.5) lastLoud = t;
        }

        // sudden motion: how much a frame changed beyond the usual movement of the moment before it
        // (a clap is a burst against a calm ~0.3 s; steady movement such as talking doesn't count)
        var novelty = new double[frames];
        for (int i = 1; i < frames; i++)
        {
            var before = motion.Skip(Math.Max(1, i - 10)).Take(Math.Min(10, i - 1)).ToList();
            novelty[i] = Math.Max(0, motion[i] - (before.Count == 0 ? 0 : Median(before)));
        }
        double noveltyMedian = Median(novelty.Skip(1));

        // For each sound: the moment the picture "lands". A clap is fast motion that stops dead when the hands meet
        // (that frame is when it's heard); a flash is a sudden change followed by calm. Both look like a frame that
        // changed a lot — far more than the moment before it — followed by a sharp drop in change. Scoring the drop
        // ignores slow build-ups (hands approaching) and the gentle return to normal afterwards.
        var diffs = new List<double>();
        foreach (var a in onsets)
        {
            int from = Math.Max(1, (int)Math.Floor((a - 0.4) * fps)), to = Math.Min(frames - 3, (int)Math.Ceiling((a + 0.7) * fps));
            int best = -1; double bestScore = 0;
            for (int i = from; i <= to; i++)
            {
                if (novelty[i] < Math.Max(2.0, noveltyMedian * 4 + motionMedian * 0.5)) continue;
                double after = (motion[i + 1] + motion[i + 2]) / 2;
                double score = Math.Min(novelty[i], motion[i] - after);
                if (score > bestScore * 1.15) { bestScore = score; best = i; }
            }
            if (best < 0 || bestScore < 2.0) continue;
            diffs.Add(best / fps - a);
        }
        if (diffs.Count == 0) return null;
        diffs.Sort();
        // trim one outlier at each end when there are enough events
        var core = diffs.Count >= 5 ? diffs.Skip(1).Take(diffs.Count - 2).ToList() : diffs;
        return new Result(core[core.Count / 2] * 1000, diffs.Count, (core[^1] - core[0]) * 1000);
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    private static async Task<byte[]> RawAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        await copy;
        await p.WaitForExitAsync(ct);
        return ms.ToArray();
    }
}
