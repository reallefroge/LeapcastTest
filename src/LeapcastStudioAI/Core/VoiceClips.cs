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
    /// it")? Other words that merely contain it ("clipboard", "eclipse") don't count, and neither do the near
    /// misses a recognizer produces for other speech ("click", "flip").
    /// </summary>
    public static bool Matches(string? heard, string word)
    {
        if (string.IsNullOrWhiteSpace(heard) || string.IsNullOrWhiteSpace(word)) return false;
        var w = Regex.Escape(word.Trim());
        return Regex.IsMatch(heard, $@"(?<![\p{{L}}\p{{N}}]){w}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
/// Reads wait for audio (the recognizer runs on its own thread); after <see cref="Complete"/> they return 0, which
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
        lock (_chunks)
        {
            while (copied == 0)
            {
                if (_current is null || _offset >= _current.Length)
                {
                    while (_chunks.Count == 0 && !_done) Monitor.Wait(_chunks);
                    if (_chunks.Count == 0) return 0; // completed
                    _current = _chunks.Dequeue();
                    _queued -= _current.Length;
                    _offset = 0;
                }
                int n = Math.Min(count - copied, _current.Length - _offset);
                Buffer.BlockCopy(_current, _offset, buffer, offset + copied, n);
                _offset += n;
                copied += n;
                // hand back what's there rather than waiting to fill the whole buffer: keeps recognition prompt
                if (_offset >= _current.Length && _chunks.Count == 0) break;
            }
        }
        return copied;
    }

    protected override void Dispose(bool disposing) { Complete(); base.Dispose(disposing); }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set { } }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => 0;
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
