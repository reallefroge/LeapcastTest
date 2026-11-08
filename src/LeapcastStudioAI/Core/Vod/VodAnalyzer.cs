using System.Diagnostics;
using System.Globalization;

namespace LeapcastStudioAI.Vod;

/// <summary>What the editor knows about a recording, second by second.</summary>
public sealed class VodSignals
{
    public const double LoudStep = 0.1, VideoStep = 0.5;
    public required double Duration { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double Fps { get; init; }
    /// <summary>Audio level (dBFS) every 0.1 s.</summary>
    public required float[] Loud { get; init; }
    /// <summary>How much the picture changes, every 0.5 s (0 still … 1 everything moved).</summary>
    public required float[] Motion { get; init; }
    /// <summary>Picture brightness every 0.5 s (0…1): loading screens and fades are dark or flat.</summary>
    public required float[] Brightness { get; init; }
    public required List<VodWord> Words { get; init; }
    public required List<VodMarker> Markers { get; init; }
    public ClipLayout? Layout { get; init; }
    public bool WordsFromMic { get; init; }

    public float LoudAt(double t) => Loud.Length == 0 ? -90 : Loud[Math.Clamp((int)(t / LoudStep), 0, Loud.Length - 1)];
    public float MotionAt(double t) => Motion.Length == 0 ? 0 : Motion[Math.Clamp((int)(t / VideoStep), 0, Motion.Length - 1)];
}

/// <summary>Reads a recording once for sound and once for pictures (in parallel, at low priority).</summary>
public static class VodAnalyzer
{
    public static async Task<(double Duration, int W, int H, double Fps)> ProbeAsync(string ffmpeg, string file, CancellationToken ct)
    {
        var (_, info) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-hide_banner", "-i", file }, TimeSpan.FromSeconds(30), ct);
        var p = await Captions.ProbeAsync(ffmpeg, file, ct) ?? throw new InvalidOperationException("That file isn't a video FFmpeg can read.");
        var fps = System.Text.RegularExpressions.Regex.Match(info, @"(\d+(?:\.\d+)?) fps");
        double f = fps.Success ? double.Parse(fps.Groups[1].Value, CultureInfo.InvariantCulture) : 30;
        return (p.Duration, p.W, p.H, f is > 5 and < 241 ? f : 30);
    }

    private static Process Start(string ffmpeg, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        _ = p.StandardError.ReadToEndAsync();
        return p;
    }

    public static async Task<VodSignals> AnalyzeAsync(string ffmpeg, string video, IProgress<double>? progress, CancellationToken ct)
    {
        var (dur, w, h, fps) = await ProbeAsync(ffmpeg, video, ct);
        var side = VodSidecar.Load(video);
        bool haveWords = side is { Words.Count: > 0 };
        double audioDone = 0, videoDone = 0;
        void Report() => progress?.Report(Math.Min(1, (audioDone + videoDone) / 2));

        // ---- sound: level every 0.1 s; words from the recognizer when there's no session log
        var audioTask = Task.Run(async () =>
        {
            var loud = new List<float>();
            var words = new List<VodWord>();
            using var p = Start(ffmpeg, new[] { "-v", "error", "-i", video, "-vn", "-ac", "1", "-ar", "16000", "-f", "s16le", "-" });
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            VoskApi.Recognizer? rec = null;
            try
            {
                if (!haveWords && VoiceListener.Available(out _))
                    rec = new VoskApi.Recognizer(VoiceListenerModel(), alternatives: 0);
            }
            catch { rec = null; }
            var buf = new byte[3200];
            var samples = new short[1600];
            long total = 0;
            var stream = p.StandardOutput.BaseStream;
            while (true)
            {
                int got = 0;
                while (got < buf.Length)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(got), ct);
                    if (n == 0) break;
                    got += n;
                }
                if (got < 2) break;
                int count = got / 2;
                Buffer.BlockCopy(buf, 0, samples, 0, count * 2);
                double sum = 0;
                for (int i = 0; i < count; i++) sum += samples[i] * (double)samples[i];
                loud.Add((float)(10 * Math.Log10(sum / count / (32768.0 * 32768.0) + 1e-9)));
                if (rec?.Accept(samples, count) is { } json) words.AddRange(Words(json));
                total += count;
                audioDone = total / 16000.0 / Math.Max(1, dur);
                if (loud.Count % 50 == 0) Report();
                if (got < buf.Length) break;
            }
            if (rec is not null) { words.AddRange(Words(rec.Final())); rec.Dispose(); }
            return (loud.ToArray(), words);
        }, ct);

        // ---- pictures: 2 frames a second, small, for motion, brightness and Wizard101's health globe
        var videoTask = Task.Run(async () =>
        {
            const int W = 320, H = 180;
            var motion = new List<float>();
            var bright = new List<float>();
            var deaths = new List<VodMarker>();
            var det = new DeathDetector();
            var t0 = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            det.Died += (start, at) => { lock (deaths) { deaths.Add(new VodMarker("fight", (start - t0).TotalSeconds)); deaths.Add(new VodMarker("death", (at - t0).TotalSeconds)); } };
            var layout = side?.Layout;
            LayoutRect game = layout?.Game is { } g && layout.CanvasW > 0
                ? new LayoutRect(g.X * W / layout.CanvasW, g.Y * H / layout.CanvasH, g.W * W / layout.CanvasW, g.H * H / layout.CanvasH)
                : new LayoutRect(0, 0, W, H);
            // the GPU decodes when it can (an hour of 1080p60 is a lot of frames); the CPU otherwise
            using var p = Start(ffmpeg, new[] { "-v", "error", "-hwaccel", "auto", "-i", video, "-an", "-vf", $"fps=2,scale={W}:{H}:flags=fast_bilinear,format=bgra", "-f", "rawvideo", "-" });
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            var frame = new byte[W * H * 4];
            byte[]? prev = null;
            var stream = p.StandardOutput.BaseStream;
            int index = 0;
            while (true)
            {
                int got = 0;
                while (got < frame.Length)
                {
                    int n = await stream.ReadAsync(frame.AsMemory(got), ct);
                    if (n == 0) break;
                    got += n;
                }
                if (got < frame.Length) break;
                long diff = 0, lum = 0;
                int samples = 0;
                for (int i = 0; i < frame.Length; i += 16)
                {
                    int y = (frame[i] + 2 * frame[i + 1] + frame[i + 2]) >> 2;
                    lum += y;
                    if (prev is not null) diff += Math.Abs(y - ((prev[i] + 2 * prev[i + 1] + prev[i + 2]) >> 2));
                    samples++;
                }
                motion.Add(prev is null ? 0 : Math.Min(1f, diff / (float)samples / 40f));
                bright.Add(lum / (float)samples / 255f);
                det.Feed(frame, W, H, game, t0 + TimeSpan.FromSeconds(index * 0.5));
                prev ??= new byte[frame.Length];
                Buffer.BlockCopy(frame, 0, prev, 0, frame.Length);
                index++;
                videoDone = index * 0.5 / Math.Max(1, dur);
                if (index % 20 == 0) Report();
            }
            return (motion.ToArray(), bright.ToArray(), deaths);
        }, ct);

        var (loud, heard) = await audioTask;
        var (mot, bri, found) = await videoTask;
        var markers = new List<VodMarker>(side?.Markers ?? new());
        // deaths seen in the video that the session log doesn't already have
        foreach (var m in found)
            if (!markers.Any(x => x.Kind == m.Kind && Math.Abs(x.At - m.At) < 10)) markers.Add(m);
        markers.Sort((a, b) => a.At.CompareTo(b.At));
        progress?.Report(1);
        return new VodSignals
        {
            Duration = dur, Width = w, Height = h, Fps = fps, Loud = loud, Motion = mot, Brightness = bri,
            Words = haveWords ? side!.Words : heard, Markers = markers, Layout = side?.Layout, WordsFromMic = haveWords,
        };
    }

    private static IEnumerable<VodWord> Words(string json) =>
        VoskApi.Parse(json).FirstOrDefault()?.Words.Select(x => new VodWord(x.Text, x.Start, x.End)) ?? Enumerable.Empty<VodWord>();

    private static VoskApi.Model VoiceListenerModel() => VoiceListener.SharedModel();
}
