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

    /// <summary>How readily the word counts, by the Settings choice.</summary>
    /// <returns>Lowest confidence for the word said alone / inside a sentence, the recognizer's own rejection
    /// threshold (0–100, -1 = leave it), and whether results the recognizer rejected are still checked.</returns>
    public static (float Alone, float InSentence, int Rejection, bool UseRejected) Sensitivity(string? level) => level switch
    {
        "Normal" => (0.55f, 0.35f, -1, false),
        "Max" => (0.12f, 0.08f, 5, true),
        _ => (0.30f, 0.18f, 20, true), // High
    };

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
    private float _env;       // level follower (fast up, slow down)
    private float _gain = 1f;

    /// <summary>
    /// Brings a quiet microphone up to a level the recognizer hears well (up to <see cref="MaxGain"/>, about +18 dB),
    /// without touching the stream's audio: this only shapes what the speech recognizer listens to.
    /// </summary>
    public bool AutoGain { get; set; } = true;
    public const float TargetLevel = 0.3f, MaxGain = 8f;
    public float Gain => _gain;

    /// <param name="mono">Mono input frames (already mixed down), at <paramref name="rate"/>.</param>
    public short[] Convert(ReadOnlySpan<float> mono, int rate)
    {
        int n = mono.Length;
        if (n == 0 || rate <= 0) return Array.Empty<short>();
        double step = rate / (double)OutRate;
        var output = new List<short>((int)(n / step) + 2);
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
                _env = lvl > _env ? _env + (lvl - _env) * 0.02f : _env * 0.99996f;  // rises in ms, falls over ~1.5 s
                float want = Math.Clamp(TargetLevel / Math.Max(_env, 1e-4f), 1f, MaxGain);
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
    private readonly Queue<byte[]> _chunks = new();
    private byte[]? _current;
    private int _offset;
    private long _queued;
    private bool _done;
    private readonly long _maxBytes;
    private long _readTotal;

    /// <summary>What <see cref="Length"/> reports (the speech engine sizes the stream from it when it starts).</summary>
    public long ReportedLength { get; init; } = long.MaxValue;
    /// <summary>Diagnostics: every call the reader makes (Length, Seek, Read sizes).</summary>
    public Action<string>? Trace { get; set; }

    public BlockingAudioStream(int maxSeconds = 5, int bytesPerSecond = SpeechPcmConverter.OutRate * 2) => _maxBytes = (long)maxSeconds * bytesPerSecond;

    public void Write(short[] samples)
    {
        if (samples.Length == 0) return;
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        lock (_chunks)
        {
            if (_done) return;
            _chunks.Enqueue(bytes);
            _queued += bytes.Length;
            while (_queued > _maxBytes && _chunks.Count > 1) _queued -= _chunks.Dequeue().Length;
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
                    _current = _chunks.Dequeue();
                    _queued -= _current.Length;
                    _offset = 0;
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
