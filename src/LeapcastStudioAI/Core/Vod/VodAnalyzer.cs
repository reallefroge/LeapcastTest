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
    /// <summary>The words came from Whisper (accurate), not the live recognizer.</summary>
    public bool WordsFromWhisper { get; init; }
    /// <summary>The recording has a sound track (a silent one is scored with music only).</summary>
    public bool HasAudio { get; init; } = true;

    /// <summary>How different the game picture is from 8 s before, every 0.5 s: moving around a map is high, a lobby or menu low.</summary>
    public float[] Novelty { get; init; } = Array.Empty<float>();
    /// <summary>Among Us: how much of the meeting tablet's green frame is on screen, every 0.5 s (0…1).</summary>
    public float[] MeetingUi { get; init; } = Array.Empty<float>();
    /// <summary>Among Us: the lobby is on screen ("WAITING FOR HOST" / the room settings panel), every 0.5 s (0 or 1).</summary>
    public float[] Lobby { get; init; } = Array.Empty<float>();
    public bool LobbyAt(double t) => Lobby.Length > 0 && Lobby[Math.Clamp((int)(t / VideoStep), 0, Lobby.Length - 1)] > 0.5f;
    /// <summary>What other people said (your desktop audio: friends on Discord, the game), when it was transcribed.</summary>
    public List<VodWord> Others { get; init; } = new();

    public float LoudAt(double t) => Loud.Length == 0 ? -90 : Loud[Math.Clamp((int)(t / LoudStep), 0, Loud.Length - 1)];
    public float NoveltyAt(double t) => Novelty.Length == 0 ? -1 : Novelty[Math.Clamp((int)(t / VideoStep), 0, Novelty.Length - 1)];
    public float MeetingAt(double t) => MeetingUi.Length == 0 ? 0 : MeetingUi[Math.Clamp((int)(t / VideoStep), 0, MeetingUi.Length - 1)];
    public float MotionAt(double t) => Motion.Length == 0 ? 0 : Motion[Math.Clamp((int)(t / VideoStep), 0, Motion.Length - 1)];
}

/// <summary>Reads a recording once for sound and once for pictures (in parallel, at low priority).</summary>
public static class VodAnalyzer
{
    public static async Task<(double Duration, int W, int H, double Fps, bool Audio)> ProbeAsync(string ffmpeg, string file, CancellationToken ct)
    {
        var (_, info) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-hide_banner", "-i", file }, TimeSpan.FromSeconds(30), ct);
        var p = await Captions.ProbeAsync(ffmpeg, file, ct) ?? throw new InvalidOperationException("That file isn't a video FFmpeg can read.");
        var fps = System.Text.RegularExpressions.Regex.Match(info, @"(\d+(?:\.\d+)?) fps");
        double f = fps.Success ? double.Parse(fps.Groups[1].Value, CultureInfo.InvariantCulture) : 30;
        return (p.Duration, p.W, p.H, f is > 5 and < 241 ? f : 30, info.Contains("Audio:"));
    }

    private static Process Start(string ffmpeg, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        BackgroundWork.Track(p);
        _ = p.StandardError.ReadToEndAsync();
        return p;
    }

    public static async Task<VodSignals> AnalyzeAsync(string ffmpeg, string video, IProgress<double>? progress, CancellationToken ct, bool useWhisper = true)
    {
        var (dur, w, h, fps, hasAudio) = await ProbeAsync(ffmpeg, video, ct);
        var side = VodSidecar.Load(video);
        bool haveWords = side is { Words.Count: > 0 };
        double audioDone = 0, videoDone = 0;
        // Whisper (when installed) writes the captions: most of the time goes there
        bool whisper = useWhisper && Whisper.Ready;
        double whisperDone = 0;
        void Report() => progress?.Report(Math.Min(1, whisper ? (audioDone + videoDone) / 2 * 0.35 + whisperDone * 0.65 : (audioDone + videoDone) / 2));

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
                // (with Whisper too: its words take the recognizer's exact times)
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
            var novelty = new List<float>();
            var meetingUi = new List<float>();
            var lobby = new List<float>();
            var thumbs = new List<float[]>();
            var deaths = new List<VodMarker>();
            var det = new DeathDetector();
            var t0 = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            det.Died += (start, at) => { lock (deaths) { deaths.Add(new VodMarker("fight", (start - t0).TotalSeconds)); deaths.Add(new VodMarker("death", (at - t0).TotalSeconds)); } };
            var layout = side?.Layout;
            LayoutRect game = layout?.Game is { } g && layout.CanvasW > 0
                ? new LayoutRect(g.X * W / layout.CanvasW, g.Y * H / layout.CanvasH, g.W * W / layout.CanvasW, g.H * H / layout.CanvasH)
                : new LayoutRect(0, 0, W, H);
            // the game's own picture on the frame (a layout may run it past the edges), and the webcam's spot (left out)
            int gx0 = Math.Clamp((int)game.X, 0, W - 1), gy0 = Math.Clamp((int)game.Y, 0, H - 1);
            int gx1 = Math.Clamp((int)(game.X + game.W), gx0 + 1, W), gy1 = Math.Clamp((int)(game.Y + game.H), gy0 + 1, H);
            LayoutRect? camRect = layout?.Camera is { } cr && layout.CanvasW > 0
                ? new LayoutRect(cr.X * W / layout.CanvasW, cr.Y * H / layout.CanvasH, cr.W * W / layout.CanvasW, cr.H * H / layout.CanvasH) : null;
            bool InCam(int x, int y) => camRect is { } c && x >= c.X && x < c.X + c.W && y >= c.Y && y < c.Y + c.H;
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
                // a 16×9 colour thumbnail of the game picture (webcam left out): compared with 8 s ago
                var th = new float[16 * 9 * 3];
                var cnt = new int[16 * 9];
                for (int y = gy0; y < gy1; y += 2)
                    for (int x = gx0; x < gx1; x += 2)
                    {
                        if (InCam(x, y)) continue;
                        int cell = Math.Min(8, (y - gy0) * 9 / (gy1 - gy0)) * 16 + Math.Min(15, (x - gx0) * 16 / (gx1 - gx0));
                        int i = (y * W + x) * 4;
                        th[cell * 3] += frame[i]; th[cell * 3 + 1] += frame[i + 1]; th[cell * 3 + 2] += frame[i + 2];
                        cnt[cell]++;
                    }
                for (int c = 0; c < cnt.Length; c++) if (cnt[c] > 0) { th[c * 3] /= cnt[c] * 255f; th[c * 3 + 1] /= cnt[c] * 255f; th[c * 3 + 2] /= cnt[c] * 255f; }
                thumbs.Add(th);
                if (thumbs.Count > 16)
                {
                    var old = thumbs[^17];
                    float d = 0; int used = 0;
                    for (int c = 0; c < cnt.Length; c++) if (cnt[c] > 0) { d += Math.Abs(th[c * 3] - old[c * 3]) + Math.Abs(th[c * 3 + 1] - old[c * 3 + 1]) + Math.Abs(th[c * 3 + 2] - old[c * 3 + 2]); used++; }
                    novelty.Add(used == 0 ? 0 : d / (used * 3));
                    thumbs.RemoveAt(0);
                }
                else novelty.Add(novelty.Count > 0 ? novelty[^1] : 0.1f);
                // Among Us: the meeting tablet's bright green frame down the left and right edges of the game picture
                float Strip(double fx0, double fx1)
                {
                    int x0 = gx0 + (int)((gx1 - gx0) * fx0), x1 = Math.Max(x0 + 1, gx0 + (int)((gx1 - gx0) * fx1));
                    int y0 = gy0 + (gy1 - gy0) / 10, y1 = gy0 + (gy1 - gy0) * 6 / 10;
                    int green = 0, all = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1 && x < W; x++)
                        {
                            if (InCam(x, y)) continue;
                            int i = (y * W + x) * 4;
                            if (frame[i + 1] > 140 && frame[i + 2] < 120 && frame[i] < 120) green++;
                            all++;
                        }
                    return all == 0 ? 0 : green / (float)all;
                }
                meetingUi.Add(Math.Max(Strip(0.003, 0.03), Strip(0.97, 0.997)));
                lobby.Add(AmongUsLobby(frame, W, H, gx0, gy0, gx1, gy1) ? 1 : 0);
                det.Feed(frame, W, H, game, t0 + TimeSpan.FromSeconds(index * 0.5));
                prev ??= new byte[frame.Length];
                Buffer.BlockCopy(frame, 0, prev, 0, frame.Length);
                index++;
                videoDone = index * 0.5 / Math.Max(1, dur);
                if (index % 20 == 0) Report();
            }
            return (motion.ToArray(), bright.ToArray(), deaths, novelty.ToArray(), meetingUi.ToArray(), lobby.ToArray());
        }, ct);

        // the words: Whisper on your mic track (only your voice), else on the recording's sound; then speaker leaks out
        var whisperTask = !whisper ? Task.FromResult<List<VodWord>?>(null) : Task.Run(async () =>
        {
            try
            {
                string? mic = side?.MicFile is { } mf ? Path.Combine(Path.GetDirectoryName(video) ?? "", mf) : null;
                bool fromMic = mic is not null && File.Exists(mic);
                // the live recognizer's words time Whisper's: the session log's (your mic, while recording), else this file's
                IReadOnlyList<VodWord> anchors = haveWords ? side!.Words : (await audioTask).Item2;
                if (fromMic && side!.MicOffset != 0) anchors = anchors.Select(w => w with { Start = w.Start - side.MicOffset, End = w.End - side.MicOffset }).ToList();
                var list = await Whisper.TranscribeAsync(ffmpeg, fromMic ? mic! : video, new Progress<double>(f => { whisperDone = f; Report(); }), ct, anchors: anchors);
                if (fromMic && side!.MicOffset != 0) list = list.Select(w => w with { Start = w.Start + side.MicOffset, End = w.End + side.MicOffset }).ToList();
                if (side is { Desktop.Count: > 0 })
                {
                    var t0 = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    CaptionWord C(VodWord w) => new(w.Text, t0.AddSeconds(w.Start), t0.AddSeconds(w.End));
                    list = Echo.Remove(list.Select(C).ToList(), side.Desktop.Select(C).ToList())
                        .Select(w => new VodWord(w.Text, (w.StartUtc - t0).TotalSeconds, (w.EndUtc - t0).TotalSeconds)).ToList();
                }
                return (List<VodWord>?)list;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; } // the recognizer's words are still there
        }, ct);
        var (loud, heard) = await audioTask;
        var whispered = await whisperTask;
        var (mot, bri, found, nov, meet, lob) = await videoTask;
        var markers = new List<VodMarker>(side?.Markers ?? new());
        // deaths seen in the video that the session log doesn't already have
        foreach (var m in found)
            if (!markers.Any(x => x.Kind == m.Kind && Math.Abs(x.At - m.At) < 10)) markers.Add(m);
        markers.Sort((a, b) => a.At.CompareTo(b.At));
        progress?.Report(1);
        return new VodSignals
        {
            Duration = dur, Width = w, Height = h, Fps = fps, HasAudio = hasAudio, Loud = loud, Motion = mot, Brightness = bri,
            Words = Vocabulary.Fix(whispered is { Count: > 0 } ? whispered : haveWords ? side!.Words : heard), Markers = markers, Layout = side?.Layout,
            WordsFromMic = haveWords || side?.MicFile is not null, WordsFromWhisper = whispered is { Count: > 0 },
            Novelty = nov, MeetingUi = meet, Lobby = lob, Others = side?.Desktop?.ToList() ?? new(),
        };
    }

    /// <summary>
    /// The Among Us lobby: the "WAITING FOR HOST" box under the crew (two thin grey lines with flat dark grey between,
    /// in the bottom middle of the game), or the room-settings panel on the right (dark, with the teal room code and
    /// "VIEW" button). BGRA frame; the game's picture is (x0, y0)–(x1, y1).
    /// </summary>
    public static bool AmongUsLobby(byte[] f, int w, int h, int x0, int y0, int x1, int y1)
    {
        int gw = x1 - x0, gh = y1 - y0;
        if (gw < 40 || gh < 40) return false;
        int bx0 = x0 + gw * 39 / 100, bx1 = x0 + gw * 59 / 100, by0 = y0 + gh * 70 / 100, by1 = Math.Min(h, y1);
        int rows = by1 - by0, cols = bx1 - bx0;
        double Lum(int x, int y) { int i = (y * w + x) * 4; return (f[i] + f[i + 1] + f[i + 2]) / 3.0; }
        int Sat(int x, int y) { int i = (y * w + x) * 4; return Math.Max(f[i], Math.Max(f[i + 1], f[i + 2])) - Math.Min(f[i], Math.Min(f[i + 1], f[i + 2])); }
        var lines = new List<int>();
        for (int y = by0 + 2; y < by1 - 2; y++)
        {
            int hit = 0;
            for (int x = bx0; x < bx1; x++)
            {
                double l = Lum(x, y);
                if (l > 85 && l < 210 && Sat(x, y) < 24 && (Lum(x, y - 2) < l - 18 || Lum(x, y + 2) < l - 18)) hit++;
            }
            if (hit > cols * 0.75) lines.Add(y);
        }
        foreach (int a in lines)
            foreach (int b in lines)
            {
                int g = b - a;
                if (g < gh * 0.08 || g > gh * 0.15) continue;
                int dark = 0, flat = 0, all = 0;
                for (int y = a + 2; y < b - 1; y++)
                    for (int x = bx0; x < bx1; x++) { all++; if (Lum(x, y) < 110) dark++; if (Sat(x, y) < 25) flat++; }
                if (all > 0 && dark > all * 0.6 && flat > all * 0.85) return true;
            }
        // the room settings panel (top right of the game)
        int px0 = x0 + gw * 77 / 100, px1 = x0 + gw * 99 / 100, py0 = y0 + gh * 12 / 100, py1 = Math.Min(h, y0 + gh * 62 / 100);
        int pd = 0, teal = 0, pall = 0;
        for (int y = Math.Max(0, py0); y < py1; y++)
            for (int x = px0; x < px1 && x < w; x++)
            {
                int i = (y * w + x) * 4;
                pall++;
                if ((f[i] + f[i + 1] + f[i + 2]) / 3 < 45) pd++;
                if (f[i + 1] > 170 && f[i] > 170 && f[i + 2] < 130) teal++; // BGRA: B high, G high, R low
            }
        return pall > 0 && pd > pall * 0.45 && teal > pall * 0.002;
    }

    private static IEnumerable<VodWord> Words(string json) =>
        VoskApi.Parse(json).FirstOrDefault()?.Words.Select(x => new VodWord(x.Text, x.Start, x.End)) ?? Enumerable.Empty<VodWord>();

    private static VoskApi.Model VoiceListenerModel() => VoiceListener.SharedModel();
}
