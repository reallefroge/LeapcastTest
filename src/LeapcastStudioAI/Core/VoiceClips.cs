using System.Text.RegularExpressions;

namespace LeapcastStudioAI;

/// <summary>Voice clipping: what counts as saying the word, where clips go and what they're called.</summary>
public static class VoiceClips
{
    /// <summary>C:\Users\&lt;you&gt;\Videos\Leapcast\clips.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "Leapcast", "clips");

    /// <summary>
    /// Does <paramref name="heard"/> contain the trigger word on its own ("clip", "Clip!", "clip that", "let's clip
    /// it")? Its plain inflections count too, since a recognizer often hears one ("clips", "clipped", "clipping",
    /// "clip it" run together as "clipit"), and so does a k- spelling ("klip"). Other words that merely contain it
    /// ("clipboard", "eclipse") don't count, and neither do near misses for other speech ("click", "flip").
    /// </summary>
    public static bool Matches(string? heard, string word)
    {
        if (string.IsNullOrWhiteSpace(heard) || string.IsNullOrWhiteSpace(word)) return false;
        string t = word.Trim();
        string w = Regex.Escape(t);
        // "c" at the start of the word may be heard as "k" (klip)
        if (t.Length > 1 && char.ToLowerInvariant(t[0]) == 'c' && char.IsLetter(t[1])) w = "[ck]" + Regex.Escape(t[1..]);
        string last = char.IsLetter(t[^1]) ? Regex.Escape(t[^1].ToString()) + "?" : "";
        return Regex.IsMatch(heard, $@"(?<![\p{{L}}\p{{N}}]){w}(?:{last}(?:s|es|ed|ing|it|er|ers))?(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public const int MinSeconds = 15, MaxSeconds = 60;

    /// <summary>A clip length within the allowed range.</summary>
    public static int ClampSeconds(int seconds) => Math.Clamp(seconds, MinSeconds, MaxSeconds);

    /// <summary>A typed clip length ("45", "45 s"), or null when it isn't a whole number from 15 to 60.</summary>
    public static int? ParseSeconds(string? text)
    {
        var t = (text ?? "").Trim().TrimEnd('s', 'S').Trim();
        return int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)
               && v >= MinSeconds && v <= MaxSeconds ? v : null;
    }

    /// <summary>One sensitivity level: what the listener accepts.</summary>
    /// <param name="Alone">Lowest confidence for the word said on its own (or found by the sentence grammar).</param>
    /// <param name="InSentence">Lowest confidence for the word inside free dictation.</param>
    /// <param name="Rejection">The recognizer's own rejection bar (0–100; it drops results below it before we see them).</param>
    /// <param name="UseRejected">Also check what the recognizer rejected, and its runner-up guesses.</param>
    /// <param name="NearLetters">Accept words this many letters away from the word ("klip", "clap"); 0 = exact only.</param>
    /// <param name="DictationWeight">How much free dictation competes with the word (0 = not loaded: everything is heard
    /// as a possible "clip"). Lower = the word wins more often; dictation is what keeps other speech from triggering.</param>
    /// <param name="Filler">Instead of dictation, a list of everyday words soaks up other speech: lighter than
    /// dictation, so a word said over a noisy mic still wins.</param>
    public sealed record SensitivityLevel(float Alone, float InSentence, int Rejection, bool UseRejected, int NearLetters, float DictationWeight,
        bool Filler = false)
    {
        public bool Near => NearLetters > 0;
    }

    /// <summary>How readily the word counts, by the Settings choice.</summary>
    public static SensitivityLevel Sensitivity(string? level) => level switch
    {
        // cautious: clear "clip"s, rarely a false one
        "Normal" => new(0.40f, 0.30f, 30, true, 0, 0.6f),
        // the word wins over dictation more often, and a one-letter mishearing counts
        "High" => new(0.30f, 0.25f, 10, true, 1, 0f, Filler: true),
        // no dictation at all: every utterance is matched against the word; two-letter mishearings count
        "Max" => new(0.05f, 0.05f, 0, true, 2, 0f),
        _ => Sensitivity("High"),
    };

    /// <summary>Everyday words that soak up other speech on High (so it isn't forced onto the trigger word).</summary>
    public static readonly string[] FillerWords =
    {
        "a", "about", "after", "again", "all", "alright", "and", "any", "are", "as", "at", "back", "bad", "be", "because", "been",
        "before", "big", "but", "by", "can", "come", "could", "day", "did", "do", "does", "done", "dont", "down", "even", "first",
        "for", "from", "game", "get", "go", "going", "gonna", "good", "got", "guys", "had", "has", "have", "he", "hello", "her",
        "here", "hey", "him", "his", "how", "i", "if", "im", "in", "is", "it", "its", "just", "kill", "know", "last", "let", "lets",
        "like", "live", "look", "lot", "make", "man", "me", "more", "my", "need", "new", "nice", "no", "not", "now", "of", "oh", "ok",
        "okay", "on", "one", "only", "or", "our", "out", "over", "play", "really", "right", "see", "she", "so", "some", "still",
        "stream", "take", "than", "thank", "thanks", "that", "the", "their", "them", "then", "there", "they", "thing", "think",
        "this", "time", "to", "today", "too", "turn", "two", "up", "us", "very", "wait", "want", "was", "way", "we", "weather",
        "well", "were", "what", "when", "where", "which", "who", "why", "will", "win", "with", "wow", "yeah", "yes", "you", "your",
    };

    /// <summary>
    /// Maximum sensitivity: also a word one letter away from the trigger ("klip", "clap", "flip", "clit"), which is
    /// how a recognizer often mishears a short word in a noisy room.
    /// </summary>
    public static bool NearMatch(string? heard, string word, int letters = 1)
    {
        if (Matches(heard, word)) return true;
        if (string.IsNullOrWhiteSpace(heard) || string.IsNullOrWhiteSpace(word)) return false;
        string w = word.Trim().ToLowerInvariant();
        if (w.Length < 3) return false;
        foreach (Match m in Regex.Matches(heard.ToLowerInvariant(), @"[\p{L}]+"))
        {
            string t = m.Value;
            // two letters off only for words that still share the word's first or last letter ("click", "chip", "lip")
            int allowed = letters >= 2 && (t[0] == w[0] || t[^1] == w[^1] || t[0] == 'k' && w[0] == 'c') ? 2 : Math.Min(letters, 1);
            if (allowed == 0) continue;
            if (t.Length >= 3 && (Distance(t, w) <= allowed || t.EndsWith('s') && Distance(t[..^1], w) <= allowed)) return true;
        }
        return false;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>
    /// The clip's file: the day, then the time it was saved, e.g. "2026-11-04 15-57-32.mp4" (sorts in order;
    /// colons aren't allowed in Windows file names). Two clips in the same second get " (2)", " (3)"…
    /// </summary>
    public static string PathFor(string folder, DateTime when)
    {
        string stem = when.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        string path = Path.Combine(folder, stem + ".mp4");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}).mp4");
        return path;
    }
}

/// <summary>
/// Turns microphone packets (any rate, any channel count, float or 16-bit) into 16 kHz mono 16-bit PCM for the
/// speech recognizer. Linear interpolation: good enough for speech recognition, and cheap.
/// </summary>
public sealed class SpeechPcmConverter
{
    public const int OutRate = 16000;
    private double _pos;      // position of the next output sample, in input frames (relative to the packet)
    private float _last;      // last input frame of the previous packet (for interpolation across packets)
    private float _env;       // speech level follower (fast up, slow down)
    private float _noise;     // background level: follows the quiet moments down quickly, rises slowly
    private float _gain = 1f;

    /// <summary>
    /// Brings a quiet microphone up to a level the recognizer hears well (up to <see cref="MaxGain"/>, about +18 dB),
    /// without touching the stream's audio: this only shapes what the speech recognizer listens to.
    /// </summary>
    public bool AutoGain { get; set; } = true;
    public const float TargetLevel = 0.3f, MaxGain = 8f;
    /// <summary>Background noise is never boosted above this (RMS, about -36 dBFS): boosting a hissy mic into the
    /// recognizer drowns the word (heard as "Phyllis set").</summary>
    public const float NoiseCeiling = 0.015f;
    public float Gain => _gain;

    private float[]? _taps;
    private int _tapsRate;
    private float[] _history = Array.Empty<float>();

    /// <summary>
    /// Low-pass below 16 kHz's limit (about 7 kHz) before downsampling. Without it, everything between 8 and 24 kHz
    /// (hiss, sibilance, fan noise) folds back onto the speech band and the recognizer mishears words ("clip" as "but").
    /// </summary>
    private float[] AntiAlias(ReadOnlySpan<float> x, int rate)
    {
        if (rate <= OutRate * 1.05) return x.ToArray();
        if (_taps is null || _tapsRate != rate)
        {
            _taps = Design(rate, 7000, 63);
            _tapsRate = rate;
            _history = new float[_taps.Length - 1];
        }
        int n = x.Length, m = _taps.Length;
        var buf = new float[_history.Length + n];
        _history.CopyTo(buf, 0);
        x.CopyTo(buf.AsSpan(_history.Length));
        var y = new float[n];
        for (int i = 0; i < n; i++)
        {
            float acc = 0;
            for (int k = 0; k < m; k++) acc += _taps[k] * buf[i + k];
            y[i] = acc;
        }
        Array.Copy(buf, n, _history, 0, _history.Length);
        return y;
    }

    /// <summary>Windowed-sinc (Blackman) low-pass, unity gain at 0 Hz.</summary>
    private static float[] Design(int rate, double cutoffHz, int taps)
    {
        var h = new double[taps];
        double fc = cutoffHz / rate, c = (taps - 1) / 2.0, sum = 0;
        for (int k = 0; k < taps; k++)
        {
            double t = k - c;
            double sinc = t == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * t) / (Math.PI * t);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * k / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * k / (taps - 1));
            h[k] = sinc * w;
            sum += h[k];
        }
        return h.Select(v => (float)(v / sum)).ToArray();
    }

    /// <param name="mono">Mono input frames (already mixed down), at <paramref name="rate"/>.</param>
    public short[] Convert(ReadOnlySpan<float> input, int rate)
    {
        if (input.Length == 0 || rate <= 0) return Array.Empty<short>();
        ReadOnlySpan<float> mono = AntiAlias(input, rate);
        int n = mono.Length;
        if (n == 0 || rate <= 0) return Array.Empty<short>();
        double step = rate / (double)OutRate;
        var output = new List<short>((int)(n / step) + 2);
        float maxGain = MaxGain;
        if (AutoGain)
        {
            double sum = 0;
            for (int k = 0; k < n; k++) sum += mono[k] * mono[k];
            float rms = (float)Math.Sqrt(sum / n);
            _noise = _noise <= 0 ? rms : rms < _noise ? _noise + (rms - _noise) * 0.3f : _noise + (rms - _noise) * 0.002f;
            maxGain = Math.Clamp(NoiseCeiling / Math.Max(_noise, 1e-5f), 1f, MaxGain);
        }
        // input frame -1 is the previous packet's last frame, so interpolation runs on across packets
        while (_pos < n - 1)
        {
            int i = (int)Math.Floor(_pos);
            double frac = _pos - i;
            float a = i < 0 ? _last : mono[i];
            float b = mono[i + 1];
            float v = (float)(a + (b - a) * frac);
            if (AutoGain)
            {
                float lvl = Math.Abs(v);
                _env = lvl > _env ? _env + (lvl - _env) * 0.02f : _env * 0.99999f;  // rises in ms, holds for seconds between words
                float want = Math.Clamp(TargetLevel / Math.Max(_env, 1e-4f), 1f, maxGain);
                _gain += (want - _gain) * (want < _gain ? 0.02f : 0.0003f);          // turns down at once, up over ~0.2 s
                v *= _gain;
            }
            output.Add((short)Math.Clamp(v * 32767f, short.MinValue, short.MaxValue));
            _pos += step;
        }
        _pos -= n;
        _last = mono[n - 1];
        return output.ToArray();
    }

    /// <summary>Mixes interleaved frames down to mono.</summary>
    public static float[] Mono(ReadOnlySpan<float> interleaved, int channels)
    {
        channels = Math.Max(1, channels);
        var m = new float[interleaved.Length / channels];
        for (int f = 0; f < m.Length; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[f * channels + c];
            m[f] = sum / channels;
        }
        return m;
    }
}

/// <summary>
/// A read-blocking stream fed with PCM from the microphone capture: what the speech recognizer listens to.
/// Every read waits until it can return the full amount asked for (the recognizer runs on its own thread and treats
/// a short read as the end of the stream); after <see cref="Complete"/> reads return what's left, then 0, which
/// ends recognition. Holds at most a few seconds, dropping the oldest audio if the reader stalls.
/// </summary>
public sealed class BlockingAudioStream : Stream
{
    private readonly Queue<(byte[] Data, DateTime WrittenUtc)> _chunks = new();
    private byte[]? _current;
    /// <summary>Where recent chunks start in the read position, and when they arrived (their audio ends then).</summary>
    private readonly List<(long ReadStart, int Length, DateTime WrittenUtc)> _timeline = new();
    private readonly int _bytesPerSecond;
    private int _offset;
    private long _queued;
    private bool _done;
    private readonly long _maxBytes;
    private long _readTotal;

    /// <summary>What <see cref="Length"/> reports (the speech engine sizes the stream from it when it starts).</summary>
    public long ReportedLength { get; init; } = long.MaxValue;
    /// <summary>Diagnostics: every call the reader makes (Length, Seek, Read sizes).</summary>
    public Action<string>? Trace { get; set; }

    public BlockingAudioStream(int maxSeconds = 5, int bytesPerSecond = SpeechPcmConverter.OutRate * 2)
    {
        _maxBytes = (long)maxSeconds * bytesPerSecond;
        _bytesPerSecond = bytesPerSecond;
    }

    /// <summary>The time a position in what was read (e.g. where the recognizer heard a word start) was spoken.</summary>
    public DateTime? WallTimeAt(TimeSpan audioPosition)
    {
        long pos = (long)(audioPosition.TotalSeconds * _bytesPerSecond);
        lock (_chunks)
        {
            for (int i = _timeline.Count - 1; i >= 0; i--)
            {
                var (start, len, written) = _timeline[i];
                if (pos < start) continue;
                // a chunk's audio ends when it arrived; earlier bytes were spoken that much earlier
                double before = (start + len - pos) / (double)_bytesPerSecond;
                return written - TimeSpan.FromSeconds(Math.Max(0, before));
            }
        }
        return null;
    }

    /// <summary>For tests: as <see cref="Write(short[])"/>, as if the audio arrived at <paramref name="writtenUtc"/>.</summary>
    public void Write(short[] samples, DateTime writtenUtc) => WriteAt(samples, writtenUtc);

    public void Write(short[] samples) => WriteAt(samples, DateTime.UtcNow);

    private void WriteAt(short[] samples, DateTime writtenUtc)
    {
        if (samples.Length == 0) return;
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        lock (_chunks)
        {
            if (_done) return;
            _chunks.Enqueue((bytes, writtenUtc));
            _queued += bytes.Length;
            while (_queued > _maxBytes && _chunks.Count > 1) _queued -= _chunks.Dequeue().Data.Length;
            Monitor.PulseAll(_chunks);
        }
    }

    public void Complete() { lock (_chunks) { _done = true; Monitor.PulseAll(_chunks); } }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int copied = 0;
        if (count <= 0) return 0;
        lock (_chunks)
        {
            // fill the whole request: the speech engine takes a short read as the end of the stream
            while (copied < count)
            {
                if (_current is null || _offset >= _current.Length)
                {
                    while (_chunks.Count == 0 && !_done) Monitor.Wait(_chunks);
                    if (_chunks.Count == 0) break; // completed: hand back what's left, then 0
                    var (data, written) = _chunks.Dequeue();
                    _current = data;
                    _queued -= data.Length;
                    _offset = 0;
                    _timeline.Add((_readTotal + copied, data.Length, written));
                    if (_timeline.Count > 4000) _timeline.RemoveRange(0, 1000); // ~40 s of 10 ms packets
                }
                int n = Math.Min(count - copied, _current.Length - _offset);
                Buffer.BlockCopy(_current, _offset, buffer, offset + copied, n);
                _offset += n;
                copied += n;
            }
        }
        _readTotal += copied;
        Trace?.Invoke($"Read({count}) → {copied}");
        return copied;
    }

    protected override void Dispose(bool disposing) { Complete(); base.Dispose(disposing); }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    /// <summary>Endless: reported as <see cref="ReportedLength"/> (the speech recognizer asks when it starts).</summary>
    public override long Length { get { Trace?.Invoke($"Length → {ReportedLength}"); return ReportedLength; } }
    /// <summary>Bytes handed out so far (the speech engine tracks its position through it).</summary>
    public override long Position { get => _readTotal; set => Trace?.Invoke($"Position = {value}"); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Trace?.Invoke($"Seek({offset}, {origin})");
        return origin == SeekOrigin.Current && offset == 0 ? _readTotal : _readTotal;
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
