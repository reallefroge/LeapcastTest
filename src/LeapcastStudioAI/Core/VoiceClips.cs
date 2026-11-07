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
    /// <param name="Alternatives">How many readings of each utterance the recognizer gives (best first).</param>
    /// <param name="Margin">A runner-up reading counts when its score is within this of the best one (0 = the best
    /// reading only). Scores are the recognizer's log-likelihoods: readings that differ in one short word ("lived" /
    /// "live" / "lip") are usually under 1 apart; a reading the audio doesn't support is several points down.</param>
    /// <param name="NearLetters">Also accept a word this many letters off ("lip", "flip", "klip"), but only when the
    /// utterance is short (at most <paramref name="NearMaxWords"/> words): a word said on its own, as a command.</param>
    public sealed record SensitivityLevel(int Alternatives, double Margin, int NearLetters, int NearMaxWords)
    {
        public bool Near => NearLetters > 0;
    }

    /// <summary>How readily the word counts, by the Settings choice.</summary>
    public static SensitivityLevel Sensitivity(string? level) => level switch
    {
        // Every level transcribes all speech into ordinary words, so talking about something else never turns into
        // the word; they differ in how much doubt is allowed when the recognizer isn't sure it heard the word.
        // Normal: the word is the recognizer's best reading
        "Normal" => new(1, 0, 0, 0),
        // High: or a close second reading ("lived" / "live" / "clip" for a quick, mumbled word), or a word one letter
        // off said entirely on its own ("lip", "flip": how the small model often hears a short "clip")
        "High" => new(3, 1.5, 1, 1),
        // Maximum: more doubt allowed, and one letter off in a two-word command too ("lip that")
        "Max" => new(5, 3.0, 1, 2),
        _ => Sensitivity("High"),
    };

    /// <summary>
    /// One transcribed word is the trigger word: the word or its plural ("clip", "clips"), also with a k ("klip").
    /// Not "clipped" / "clipping" ("my mic is clipping" isn't a request for a clip).
    /// </summary>
    public static bool IsWord(string? heard, string word)
    {
        if (string.IsNullOrWhiteSpace(heard) || string.IsNullOrWhiteSpace(word)) return false;
        string h = heard.Trim().ToLowerInvariant(), w = word.Trim().ToLowerInvariant();
        if (h == w || h == w + "s" || h == w + "es") return true;
        return w.Length > 1 && w[0] == 'c' && h.Length > 1 && h[0] == 'k' && IsWord("c" + h[1..], w);
    }

    /// <summary>
    /// The trigger word in one recognized utterance, by the sensitivity level: the first matching word of the best
    /// reading, else of a close runner-up. Null when the utterance isn't the word.
    /// </summary>
    public static VoskApi.Word? FindWord(IReadOnlyList<VoskApi.Hypothesis> readings, string word, SensitivityLevel level)
    {
        if (readings.Count == 0) return null;
        double best = readings[0].Score;
        for (int i = 0; i < readings.Count && i < Math.Max(1, level.Alternatives); i++)
        {
            var r = readings[i];
            if (i > 0 && !(double.IsNaN(best) || best - r.Score <= level.Margin)) break;
            foreach (var w in r.Words) if (IsWord(w.Text, word)) return w;
            if (level.Near && r.Words.Count <= level.NearMaxWords)
                foreach (var w in r.Words) if (NearMatch(w.Text, word, level.NearLetters) || SameStart(w.Text, word)) return w;
        }
        return null;
    }

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

    /// <summary>
    /// A word said on its own that starts like the trigger and is at most two letters off: "club" / "clubs" for
    /// "clip" (how the recognizer hears a short, clipped "clip" in some voices).
    /// </summary>
    public static bool SameStart(string? heard, string word)
    {
        if (string.IsNullOrWhiteSpace(heard) || string.IsNullOrWhiteSpace(word)) return false;
        string h = heard.Trim().ToLowerInvariant(), w = word.Trim().ToLowerInvariant();
        if (h.Length > 3 && h.EndsWith('s') && !w.EndsWith('s')) h = h[..^1];
        return w.Length >= 3 && h.Length >= 3 && h[..2] == w[..2] && Math.Abs(h.Length - w.Length) <= 1 && Distance(h, w) <= 2;
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
    public static string PathFor(string folder, DateTime when, string? label = null)
    {
        string stem = when.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture)
                      + (string.IsNullOrWhiteSpace(label) ? "" : " " + label.Trim());
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
            _lastWrite = writtenUtc;
            _chunks.Enqueue((bytes, writtenUtc));
            _queued += bytes.Length;
            while (_queued > _maxBytes && _chunks.Count > 1) _queued -= _chunks.Dequeue().Data.Length;
            Monitor.PulseAll(_chunks);
        }
    }

    public void Complete() { lock (_chunks) { _done = true; Monitor.PulseAll(_chunks); } }

    private DateTime _lastWrite;

    /// <summary>
    /// Desktop audio only arrives while something plays: fills the gaps with silence (call it a few times a second),
    /// so the recognizer hears the pause that ends a sentence instead of waiting for the next sound.
    /// </summary>
    public void PadSilence(DateTime nowUtc)
    {
        DateTime last;
        lock (_chunks) last = _lastWrite;
        if (last == default || nowUtc - last < TimeSpan.FromMilliseconds(150)) return;
        int n = (int)Math.Min(_bytesPerSecond, (nowUtc - last).TotalSeconds * _bytesPerSecond) / 2;
        if (n > 0) WriteAt(new short[n], nowUtc);
    }

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
