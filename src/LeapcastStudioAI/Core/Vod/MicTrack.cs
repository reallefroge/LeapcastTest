namespace LeapcastStudioAI.Vod;

/// <summary>
/// Your microphone on its own (16 kHz mono WAV) while recording: captions are transcribed from it, so they're only
/// your voice, never the game. Gaps (the mic delivering nothing) are filled with silence by the clock, so a second
/// in the file is a second of the recording.
/// </summary>
public sealed class MicTrack : IDisposable
{
    private const int Rate = SpeechPcmConverter.OutRate;
    private readonly object _lock = new();
    private FileStream? _file;
    private long _samples;
    public string Path { get; }
    public DateTime? StartUtc { get; private set; }

    public MicTrack(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _file = File.Create(path);
        _file.Write(new byte[44]); // header, written on Finish
    }

    /// <summary>Adds audio that just arrived (its last sample is "now").</summary>
    public void Write(short[] pcm, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_file is null || pcm.Length == 0) return;
            var first = nowUtc - TimeSpan.FromSeconds(pcm.Length / (double)Rate);
            StartUtc ??= first;
            // fill a gap with silence (more than 80 ms missing)
            long expected = (long)((first - StartUtc.Value).TotalSeconds * Rate);
            if (expected - _samples > Rate / 12 && expected - _samples < Rate * 600)
            {
                var zeros = new byte[(expected - _samples) * 2];
                _file.Write(zeros);
                _samples = expected;
            }
            var bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            _file.Write(bytes);
            _samples += pcm.Length;
        }
    }

    /// <summary>Closes the file (with its WAV header). Returns false when nothing was recorded.</summary>
    public bool Finish()
    {
        lock (_lock)
        {
            if (_file is null) return false;
            int data = (int)Math.Min(int.MaxValue - 44, _samples * 2);
            _file.Position = 0;
            using (var w = new BinaryWriter(_file, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
                w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write("data"u8); w.Write(data);
            }
            _file.Dispose();
            _file = null;
            return _samples > 0;
        }
    }

    public void Dispose() => Finish();
}

/// <summary>The last few minutes of your microphone (16 kHz), in memory: clip captions are transcribed from it.</summary>
public sealed class MicRing
{
    private const int Rate = SpeechPcmConverter.OutRate;
    private readonly short[] _buf;
    private long _written;
    private DateTime _lastUtc;
    private readonly object _lock = new();

    public MicRing(int seconds = 240) { _buf = new short[seconds * Rate]; }

    public void Write(short[] pcm, DateTime nowUtc)
    {
        lock (_lock)
        {
            // a gap: silence for the missing time, so positions stay on the clock
            if (_written > 0)
            {
                long missing = (long)((nowUtc - _lastUtc).TotalSeconds * Rate) - pcm.Length;
                if (missing > Rate / 12 && missing < _buf.Length) for (long i = 0; i < missing; i++) _buf[(_written++) % _buf.Length] = 0;
            }
            foreach (var s in pcm) _buf[(_written++) % _buf.Length] = s;
            _lastUtc = nowUtc;
        }
    }

    /// <summary>The audio between two moments (null when it's no longer, or not yet, in the buffer).</summary>
    public short[]? Between(DateTime fromUtc, DateTime toUtc)
    {
        lock (_lock)
        {
            if (_written == 0) return null;
            long end = _written - (long)Math.Max(0, (_lastUtc - toUtc).TotalSeconds * Rate);
            long start = end - (long)((toUtc - fromUtc).TotalSeconds * Rate);
            start = Math.Max(start, Math.Max(0, _written - _buf.Length));
            if (end <= start) return null;
            var o = new short[end - start];
            for (long i = start; i < end; i++) o[i - start] = _buf[i % _buf.Length];
            return o;
        }
    }
}
