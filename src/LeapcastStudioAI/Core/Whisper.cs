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


    /// <summary>Whisper can run now (the program is installed and the model downloaded).</summary>
    public static bool Ready => !Off && Cli() is not null && File.Exists(ModelPath);
    /// <summary>Tests: use the live recognizer's words instead.</summary>
    internal static bool Off;
    /// <summary>Tests: change whisper-cli's arguments (diagnostics).</summary>
    internal static Func<List<string>, List<string>>? ArgsHook;
    /// <summary>The last run's raw JSON (diagnostics).</summary>
    internal static string? LastJson;
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
        double from = 0, double? length = null, IReadOnlyList<VodWord>? anchors = null, bool fast = false)
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
            conv.AddRange(new[] { "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-map_metadata", "-1", "-bitexact", wav });
            var (code, err) = await EncoderCatalog.RunQuietAsync(ffmpeg, conv, TimeSpan.FromMinutes(30), ct);
            if (code != 0 || !File.Exists(wav)) throw new InvalidOperationException("Couldn't read the sound: " + err.Trim());

            string outBase = Path.Combine(dir, "words");
            var args = new List<string> { "-m", ModelPath, "-f", wav, "-l", "en", "-ojf", "-of", outBase, "-np", "-pp",
                "-t", Math.Min(8, BackgroundWork.Threads).ToString(CultureInfo.InvariantCulture),
                // fast (the VOD editor, minutes of speech): one best guess per word instead of five side by side
                "-sns", "-nth", "0.6", "-bs", fast ? "1" : "5", "-bo", fast ? "1" : "5" };
            // the names to expect (Khrysalis, Krokotopia, yours…), so they come out spelled right
            args.AddRange(new[] { "--prompt", Vocabulary.Prompt() });
            // (whisper.cpp's own --vad is left off: in this build it times words as if the silences weren't there.
            // Words Whisper "hears" in silence are dropped below instead, by the sound's own level.)
            if (ArgsHook is { } hook) args = hook(args);
            var psi = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            BackgroundWork.Track(p);
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
            LastJson = await File.ReadAllTextAsync(outBase + ".json", ct);
            // Whisper's words, timed by the live recognizer's words (exact) and your voice's own level (between them)
            var words = Retime(ParseRaw(LastJson), anchors, Envelope(wav));
            return Vocabulary.Fix(words);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Where a WAV's samples start (after "data"; FFmpeg's headers carry extra chunks, so not always byte 44).</summary>
    private static long DataOffset(FileStream f)
    {
        var r = new BinaryReader(f, System.Text.Encoding.ASCII, leaveOpen: true);
        f.Position = 12;
        while (f.Position + 8 <= f.Length)
        {
            string id = new string(r.ReadChars(4));
            uint size = r.ReadUInt32();
            if (id == "data") return f.Position;
            f.Position += size + (size & 1);
        }
        return 44;
    }

    /// <summary>The sound level (dBFS) every 50 ms of a 16 kHz mono WAV.</summary>
    internal static float[] Envelope(string wav)
    {
        var o = new List<float>();
        using var f = File.OpenRead(wav);
        f.Position = DataOffset(f);
        var buf = new byte[1600]; // 50 ms
        int got;
        while ((got = f.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false)) >= 2)
        {
            double sum = 0; int n = got / 2;
            for (int i = 0; i < n; i++) { double v = BitConverter.ToInt16(buf, i * 2) / 32768.0; sum += v * v; }
            o.Add((float)(10 * Math.Log10(sum / n + 1e-10)));
            if (got < buf.Length) break;
        }
        return o.ToArray();
    }

    /// <summary>
    /// Times Whisper's words. Whisper is right about WHAT was said but this build's word times can be seconds off,
    /// so: (1) words the live recognizer (Vosk) also heard take its times, which are exact; (2) the words between
    /// those are laid over the moments your voice is actually sounding (the level above its quiet floor), in
    /// proportion to their length; (3) words with no sound under them at all (Whisper "hearing" a "thank you" in
    /// silence) are left out.
    /// </summary>
    internal static List<VodWord> Retime(List<(VodWord Word, int Seg, double SegFrom, double SegTo)> raw,
        IReadOnlyList<VodWord>? anchors, float[] env)
    {
        int n = raw.Count;
        if (n == 0) return new();
        const double Step = 0.05;
        double total = env.Length * Step;
        // the voice's level: what's clearly above the quiet floor is "sounding"
        bool[] on = new bool[env.Length];
        if (env.Length > 0)
        {
            var sorted = env.OrderBy(x => x).ToArray();
            float floor = sorted[(int)(sorted.Length * 0.1)], loudest = sorted[^1];
            float gate = loudest - floor < 12 ? float.NegativeInfinity : Math.Max(floor + 9, -60);
            for (int i = 0; i < env.Length; i++) on[i] = env[i] > gate;
            // drop lone clicks (under 100 ms), then close tiny gaps between syllables (≤ 150 ms)
            for (int i = 0; i < on.Length; i++)
                if (on[i])
                {
                    int k = i; while (k < on.Length && on[k]) k++;
                    if (k - i < 2) for (int q = i; q < k; q++) on[q] = false;
                    i = k;
                }
            for (int i = 0; i < on.Length; i++)
                if (!on[i])
                {
                    int k = i; while (k < on.Length && !on[k]) k++;
                    if (i > 0 && k < on.Length && k - i <= 3) for (int q = i; q < k; q++) on[q] = true;
                    i = k;
                }
        }

        var start = new double[n]; var end = new double[n]; var known = new bool[n];
        // (1) match Whisper's words to the recognizer's, in order, a chunk at a time (both lists are in time order)
        if (anchors is { Count: > 0 })
        {
            var a = anchors.OrderBy(x => x.Start).ToList();
            int aFrom = 0;
            for (int c = 0; c < n; c += 60)
            {
                int cEnd = Math.Min(n, c + 60);
                double lo = raw[c].Word.Start - 20, hi = raw[cEnd - 1].Word.End + 20;
                lo = Math.Min(lo, raw[c].SegFrom - 5); hi = Math.Max(hi, raw[cEnd - 1].SegTo + 5);
                int a0 = aFrom; while (a0 < a.Count && a[a0].Start < lo) a0++;
                int a1 = a0; while (a1 < a.Count && a[a1].Start <= hi) a1++;
                int m = cEnd - c, k = a1 - a0;
                if (k == 0) continue;
                var dp = new int[m + 1, k + 1];
                for (int i = m - 1; i >= 0; i--)
                    for (int j = k - 1; j >= 0; j--)
                        dp[i, j] = Same(raw[c + i].Word.Text, a[a0 + j].Text) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
                int ii = 0, jj = 0, lastJ = -1;
                while (ii < m && jj < k)
                {
                    if (Same(raw[c + ii].Word.Text, a[a0 + jj].Text) && dp[ii, jj] == dp[ii + 1, jj + 1] + 1)
                    {
                        start[c + ii] = a[a0 + jj].Start; end[c + ii] = a[a0 + jj].End; known[c + ii] = true;
                        lastJ = jj; ii++; jj++;
                    }
                    else if (dp[ii + 1, jj] >= dp[ii, jj + 1]) ii++;
                    else jj++;
                }
                if (lastJ >= 0) aFrom = a0 + lastJ + 1;
            }
            // a lone match far from both neighbours is likely a coincidence ("the", "a"): only trust short words in a run
            for (int i = 0; i < n; i++)
                if (known[i] && Norm(raw[i].Word.Text).Length <= 3)
                {
                    bool left = i > 0 && known[i - 1], right = i + 1 < n && known[i + 1];
                    if (!left && !right) known[i] = false;
                }
        }

        // (2) the rest: runs of unmatched words in one sentence, laid over the sounding moments around them
        var keep = new bool[n];
        for (int i = 0; i < n; i++) keep[i] = known[i];
        for (int i = 0; i < n;)
        {
            if (known[i]) { i++; continue; }
            int j = i; while (j < n && !known[j] && raw[j].Seg == raw[i].Seg) j++;
            double prevEnd = 0; for (int q = i - 1; q >= 0; q--) if (known[q]) { prevEnd = end[q]; break; }
            double nextStart = total > 0 ? total : double.MaxValue; for (int q = j; q < n; q++) if (known[q]) { nextStart = start[q]; break; }
            double lo = Math.Max(prevEnd, raw[i].SegFrom - 0.5), hi = Math.Min(nextStart, raw[j - 1].SegTo + 0.5);
            if (hi - lo < 0.1) { lo = prevEnd; hi = Math.Max(prevEnd + 0.1, nextStart); }
            // the sounding moments in the window
            var spans = new List<(double A, double B)>();
            if (env.Length > 0)
            {
                int fa = Math.Max(0, (int)(lo / Step)), fb = Math.Min(env.Length, (int)Math.Ceiling(hi / Step));
                for (int f = fa; f < fb; f++)
                {
                    if (!on[f]) continue;
                    int g = f; while (g < fb && on[g]) g++;
                    spans.Add((Math.Max(lo, f * Step), Math.Min(hi, g * Step)));
                    f = g;
                }
            }
            else spans.Add((lo, hi));
            double sounding = spans.Sum(x => x.B - x.A);
            if (sounding >= 0.12)
            {
                double weightSum = 0;
                var weight = new double[j - i];
                for (int q = i; q < j; q++) { weight[q - i] = Norm(raw[q].Word.Text).Length + 1.5; weightSum += weight[q - i]; }
                double At(double x) // x seconds into the sounding time → clock time
                {
                    foreach (var (A, B) in spans) { if (x <= B - A) return A + x; x -= B - A; }
                    return spans[^1].B;
                }
                double acc = 0;
                for (int q = i; q < j; q++)
                {
                    double w = weight[q - i] / weightSum * sounding;
                    start[q] = At(acc + 0.001); end[q] = At(acc + w); acc += w;
                    keep[q] = true;
                }
            }
            i = j;
        }

        var o = new List<VodWord>(n);
        for (int i = 0; i < n; i++)
            if (keep[i]) o.Add(raw[i].Word with { Start = start[i], End = Math.Max(start[i] + 0.06, end[i]) });
        o.Sort((x, y) => x.Start.CompareTo(y.Start));
        for (int i = 0; i + 1 < o.Count; i++)
            if (o[i].End > o[i + 1].Start) o[i] = o[i] with { End = Math.Max(o[i].Start + 0.04, o[i + 1].Start) };
        return o;
    }

    private static string Norm(string w) => Regex.Replace(w.ToLowerInvariant(), @"[^a-z0-9']", "");

    /// <summary>The same word, allowing a letter or two of difference in longer words (recognizers spell names differently).</summary>
    private static bool Same(string x, string y)
    {
        x = Norm(x); y = Norm(y);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        int longer = Math.Max(x.Length, y.Length);
        if (longer < 5) return false;
        int allowed = longer >= 8 ? 2 : 1;
        if (Math.Abs(x.Length - y.Length) > allowed) return false;
        var d = new int[y.Length + 1];
        for (int j = 0; j <= y.Length; j++) d[j] = j;
        for (int i = 1; i <= x.Length; i++)
        {
            int prev = d[0]; d[0] = i;
            for (int j = 1; j <= y.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (x[i - 1] == y[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return d[y.Length] <= allowed;
    }

    private static string Tail(string s) { s = s.Trim(); return s.Length > 400 ? s[^400..] : s; }

    /// <summary>
    /// Words from whisper-cli's full JSON: tokens joined into words (a token starting with a space starts a word),
    /// timed by the DTW alignment when present (closest to when the word is actually said), else the token times.
    /// Special tokens, sound tags ("[Music]", "(laughs)") and very unsure tokens are left out.
    /// </summary>
    public static List<VodWord> Parse(string json)
    {
        var words = ParseRaw(json).Select(x => x.Word).ToList();
        // times can run a hair out of order: keep them rising, and each word ending before the next starts
        words.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 0; i + 1 < words.Count; i++)
            if (words[i].End > words[i + 1].Start) words[i] = words[i] with { End = Math.Max(words[i].Start + 0.06, words[i + 1].Start) };
        return words;
    }

    /// <summary>Words in Whisper's order with the segment (sentence window) each came from, in seconds.</summary>
    internal static List<(VodWord Word, int Seg, double SegFrom, double SegTo)> ParseRaw(string json)
    {
        var words = new List<(VodWord Word, int Seg, double SegFrom, double SegTo)>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("transcription", out var segs)) return words;
        int segIndex = -1;
        foreach (var seg in segs.EnumerateArray())
        {
            segIndex++;
            double segFrom = 0, segTo = 0;
            if (seg.TryGetProperty("offsets", out var so)) { segFrom = so.GetProperty("from").GetDouble() / 1000.0; segTo = so.GetProperty("to").GetDouble() / 1000.0; }
            if (!seg.TryGetProperty("tokens", out var toks)) continue;
            string cur = "";
            double start = -1, end = -1, minP = 1;
            void Flush()
            {
                string w = Regex.Replace(cur.Trim(), @"^[^\w']+|[^\w'.,!?-]+$", "");
                if (w.Length > 0 && start >= 0 && minP >= 0.15 && !Regex.IsMatch(w, @"^[\[(].*[\])]$"))
                    words.Add((new VodWord(w.TrimEnd('.', ',', '!', '?'), start, Math.Max(start + 0.08, end)), segIndex, segFrom, Math.Max(segTo, segFrom)));
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
        return words;
    }
}
