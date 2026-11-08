using System.Net.Http;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeapcastStudioAI.Vod;

namespace LeapcastStudioAI;

/// <summary>
/// Whisper (whisper.cpp, offline) for captions: far more accurate than the live "clip" listener, with a time for
/// every word. The program ships with the app (speech\whisper); the model (≈148 MB) is downloaded once, from
/// whisper.cpp's own model page, checked against its SHA-256, and kept in the app's data folder.
/// </summary>
public static class Whisper
{
    public const string ModelFile = "ggml-base.en.bin";
    public const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";
    /// <summary>SHA-256 of <see cref="ModelFile"/> (pinned: .github/workflows/fetch-whisper.yml).</summary>
    public static string ModelSha256 = "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002";
    public const string VadFile = "ggml-silero-v5.1.2.bin";

    private static string AppDir => Path.Combine(AppContext.BaseDirectory, "speech", "whisper");

    /// <summary>whisper-cli (LEAPCAST_WHISPER_CLI, else speech\whisper next to the app).</summary>
    public static string? Cli()
    {
        var env = Environment.GetEnvironmentVariable("LEAPCAST_WHISPER_CLI");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var name in new[] { "whisper-cli.exe", "whisper-cli" })
        {
            var p = Path.Combine(AppDir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Where the model is kept (LEAPCAST_WHISPER_MODEL overrides).</summary>
    public static string ModelPath => Environment.GetEnvironmentVariable("LEAPCAST_WHISPER_MODEL") is { Length: > 0 } m
        ? m : Path.Combine(StateStore.Folder, "models", ModelFile);

    private static string? Vad()
    {
        var env = Environment.GetEnvironmentVariable("LEAPCAST_WHISPER_VAD");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var dir in new[] { Path.GetDirectoryName(Cli() ?? "") ?? "", AppDir })
        {
            var p = Path.Combine(dir, VadFile);
            if (dir.Length > 0 && File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Whisper can run now (the program is installed and the model downloaded).</summary>
    public static bool Ready => Cli() is not null && File.Exists(ModelPath);
    public static bool Installed => Cli() is not null;

    private static readonly SemaphoreSlim DownloadLock = new(1, 1);

    /// <summary>Downloads the model once (with a checksum). Returns false when it couldn't.</summary>
    public static async Task<bool> EnsureModelAsync(HttpClient http, IProgress<double>? progress, CancellationToken ct)
    {
        if (File.Exists(ModelPath)) return true;
        if (!Installed) return false;
        await DownloadLock.WaitAsync(ct);
        try
        {
            if (File.Exists(ModelPath)) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            string part = ModelPath + ".part";
            using (var resp = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 148_000_000;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(part);
                var buf = new byte[1 << 16];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    progress?.Report(Math.Min(1, got / (double)total));
                }
            }
            string hash;
            await using (var f = File.OpenRead(part)) hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
            if (hash != ModelSha256) { File.Delete(part); throw new InvalidOperationException("The caption model download was damaged (checksum mismatch)."); }
            File.Move(part, ModelPath, true);
            return true;
        }
        finally { DownloadLock.Release(); }
    }

    /// <summary>
    /// Transcribes a file (any audio or video FFmpeg reads; a 16 kHz mono WAV is used as is): words with their
    /// times in seconds from the start of the file. <paramref name="progress"/> gets 0…1.
    /// </summary>
    public static async Task<List<VodWord>> TranscribeAsync(string ffmpeg, string input, IProgress<double>? progress, CancellationToken ct,
        double from = 0, double? length = null)
    {
        string cli = Cli() ?? throw new InvalidOperationException("Whisper isn't installed.");
        if (!File.Exists(ModelPath)) throw new InvalidOperationException("The caption model hasn't been downloaded yet.");
        string dir = Path.Combine(Path.GetTempPath(), "leapcast-whisper-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string wav = Path.Combine(dir, "audio.wav");
            var conv = new List<string> { "-hide_banner", "-v", "error", "-y" };
            if (from > 0) conv.AddRange(new[] { "-ss", from.ToString("0.###", CultureInfo.InvariantCulture) });
            conv.AddRange(new[] { "-i", input });
            if (length is { } l) conv.AddRange(new[] { "-t", l.ToString("0.###", CultureInfo.InvariantCulture) });
            conv.AddRange(new[] { "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav });
            var (code, err) = await EncoderCatalog.RunQuietAsync(ffmpeg, conv, TimeSpan.FromMinutes(30), ct);
            if (code != 0 || !File.Exists(wav)) throw new InvalidOperationException("Couldn't read the sound: " + err.Trim());

            string outBase = Path.Combine(dir, "words");
            var args = new List<string> { "-m", ModelPath, "-f", wav, "-l", "en", "-ojf", "-of", outBase, "-np", "-pp",
                "-t", Math.Clamp(Environment.ProcessorCount - 2, 2, 8).ToString(CultureInfo.InvariantCulture),
                "-sns", "-nth", "0.6", "-dtw", "base.en", "-bs", "5", "-bo", "5" };
            // the names to expect (Khrysalis, Krokotopia, yours…), so they come out spelled right
            args.AddRange(new[] { "--prompt", Vocabulary.Prompt() });
            if (Vad() is { } vad) args.AddRange(new[] { "--vad", "-vm", vad, "-vp", "250" });
            var psi = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var errText = new System.Text.StringBuilder();
            var reader = Task.Run(async () =>
            {
                string? line;
                while ((line = await p.StandardError.ReadLineAsync(ct)) is not null)
                {
                    errText.AppendLine(line);
                    var m = Regex.Match(line, @"progress\s*=\s*(\d+)%");
                    if (m.Success) progress?.Report(int.Parse(m.Groups[1].Value) / 100.0);
                }
            }, ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            using (ct.Register(() => { try { p.Kill(true); } catch { } })) await p.WaitForExitAsync(ct);
            try { await reader; } catch { }
            if (p.ExitCode != 0 || !File.Exists(outBase + ".json"))
                throw new InvalidOperationException($"Whisper failed (exit code {p.ExitCode}): " + Tail(errText.ToString()));
            progress?.Report(1);
            return Vocabulary.Fix(Parse(await File.ReadAllTextAsync(outBase + ".json", ct)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string Tail(string s) { s = s.Trim(); return s.Length > 400 ? s[^400..] : s; }

    /// <summary>
    /// Words from whisper-cli's full JSON: tokens joined into words (a token starting with a space starts a word),
    /// timed by the DTW alignment when present (closest to when the word is actually said), else the token times.
    /// Special tokens, sound tags ("[Music]", "(laughs)") and very unsure tokens are left out.
    /// </summary>
    public static List<VodWord> Parse(string json)
    {
        var words = new List<VodWord>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("transcription", out var segs)) return words;
        foreach (var seg in segs.EnumerateArray())
        {
            if (!seg.TryGetProperty("tokens", out var toks)) continue;
            string cur = "";
            double start = -1, end = -1, minP = 1;
            void Flush()
            {
                string w = Regex.Replace(cur.Trim(), @"^[^\w']+|[^\w'.,!?-]+$", "");
                if (w.Length > 0 && start >= 0 && minP >= 0.15 && !Regex.IsMatch(w, @"^[\[(].*[\])]$"))
                    words.Add(new VodWord(w.TrimEnd('.', ',', '!', '?'), start, Math.Max(start + 0.08, end)));
                cur = ""; start = -1; end = -1; minP = 1;
            }
            bool inTag = false;
            foreach (var t in toks.EnumerateArray())
            {
                string text = t.GetProperty("text").GetString() ?? "";
                if (text.StartsWith("[_") || text.StartsWith("<|")) continue;
                // sound tags spread over several tokens: [ Music ]
                if (text.TrimStart().StartsWith("[") || text.TrimStart().StartsWith("(")) { Flush(); inTag = true; }
                if (inTag) { if (text.Contains(']') || text.Contains(')')) inTag = false; continue; }
                double p = t.TryGetProperty("p", out var pp) ? pp.GetDouble() : 1;
                double from = -1, to = -1;
                if (t.TryGetProperty("offsets", out var off)) { from = off.GetProperty("from").GetDouble() / 1000.0; to = off.GetProperty("to").GetDouble() / 1000.0; }
                double dtw = t.TryGetProperty("t_dtw", out var d) ? d.GetDouble() : -1;
                if (text.StartsWith(' ') && cur.Trim().Length > 0) Flush();
                if (cur.Trim().Length == 0) start = dtw >= 0 ? dtw / 100.0 : from;
                cur += text;
                end = to >= 0 ? to : end;
                if (Regex.IsMatch(text, @"\w")) minP = Math.Min(minP, p); // punctuation's sureness doesn't count
            }
            Flush();
        }
        // DTW times can run a hair out of order: keep them rising, and each word ending before the next starts
        words.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 0; i + 1 < words.Count; i++)
            if (words[i].End > words[i + 1].Start) words[i] = words[i] with { End = Math.Max(words[i].Start + 0.06, words[i + 1].Start) };
        return words;
    }
}
