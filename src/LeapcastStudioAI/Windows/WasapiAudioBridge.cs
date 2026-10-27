using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LeapcastStudioAI;

/// <summary>
/// Captures microphones (WASAPI) and desktop audio (WASAPI loopback, i.e. the game's sound) and serves each
/// one to ffmpeg as raw 48 kHz stereo float through a named pipe. Writes are paced by a real-time clock and
/// padded with silence, so ffmpeg always gets a continuous stream even when Windows delivers no loopback
/// packets (nothing playing).
/// </summary>
public sealed class WasapiAudioBridge : IAudioBridge
{
    private const int Rate = 48000;
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private sealed class Feed : IDisposable
    {
        public required Guid SourceId;
        public required string Key;
        public required string Name;
        public WasapiCapture? Capture;
        public BufferedWaveProvider? Buffer;
        public ISampleProvider? Stereo48k;
        public volatile float Peak;
        public long PeakTicks;
        public string? Error;

        public NamedPipeServerStream? Pipe;
        public Thread? Writer;

        public void Dispose()
        {
            try { Pipe?.Dispose(); } catch { }
            try { Capture?.StopRecording(); } catch { }
            try { Capture?.Dispose(); } catch { }
            Capture = null;
        }
    }

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Feed> _feeds = new();
    private CancellationTokenSource? _pipeCts;

    public event Action<string>? DeviceError;

    // ------------------------------------------------------------------ device lists (for the properties dialog)

    public sealed record AudioDevice(string Id, string Name);

    public static List<AudioDevice> ListDevices(bool capture)
    {
        // Core Audio COM objects must be used from an MTA thread.
        return Task.Run(() =>
        {
            var list = new List<AudioDevice>();
            try
            {
                using var en = new MMDeviceEnumerator();
                foreach (var d in en.EnumerateAudioEndPoints(capture ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
                {
                    try { list.Add(new AudioDevice(d.ID, d.FriendlyName)); } catch { }
                }
            }
            catch { }
            return list;
        }).GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------ IAudioBridge

    public void Configure(IReadOnlyList<StudioSource> audioSources)
    {
        Task.Run(() =>
        {
            lock (_lock)
            {
                var wanted = audioSources.ToDictionary(s => s.Id, s => (s, key: SourceTypes.KindOf(s.Type) + "|" + s.AudioDeviceId));
                foreach (var id in _feeds.Keys.ToList())
                {
                    if (!wanted.TryGetValue(id, out var w) || w.key != _feeds[id].Key)
                    {
                        _feeds[id].Dispose();
                        _feeds.Remove(id);
                    }
                }
                foreach (var (id, (src, key)) in wanted)
                {
                    if (_feeds.TryGetValue(id, out var existing))
                    {
                        if (existing.Capture is not null && existing.Error is null) continue;
                        existing.Dispose(); // failed or unplugged earlier: try again
                        _feeds.Remove(id);
                    }
                    var feed = new Feed { SourceId = id, Key = key, Name = src.Name };
                    try { StartCapture(feed, src); }
                    catch (Exception ex)
                    {
                        feed.Error = ex.Message;
                        feed.Dispose();
                        DeviceError?.Invoke($"\"{src.Name}\": couldn't open the audio device ({ex.Message}).");
                    }
                    _feeds[id] = feed;
                }
            }
        }).GetAwaiter().GetResult();
    }

    private void StartCapture(Feed feed, StudioSource src)
    {
        bool mic = SourceTypes.KindOf(src.Type) == SourceKind.Microphone;
        using var en = new MMDeviceEnumerator();
        MMDevice device = string.IsNullOrWhiteSpace(src.AudioDeviceId)
            ? en.GetDefaultAudioEndpoint(mic ? DataFlow.Capture : DataFlow.Render, Role.Multimedia)
            : en.GetDevice(src.AudioDeviceId);

        WasapiCapture capture = mic ? new WasapiCapture(device) : new WasapiLoopbackCapture(device);
        var format = capture.WaveFormat;
        var buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true, // silence when empty: keeps the stream continuous
        };
        ISampleProvider stereo = new StereoFloatProvider(buffer);
        if (format.SampleRate != Rate) stereo = new WdlResamplingSampleProvider(stereo, Rate);

        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0) return;
            buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            float peak = PeakOf(e.Buffer, e.BytesRecorded, format);
            if (peak > feed.Peak || Environment.TickCount64 - Interlocked.Read(ref feed.PeakTicks) > 120)
            {
                feed.Peak = peak;
                Interlocked.Exchange(ref feed.PeakTicks, Environment.TickCount64);
            }
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                feed.Error = e.Exception.Message;
                DeviceError?.Invoke($"\"{feed.Name}\" stopped: {e.Exception.Message}");
            }
        };
        feed.Capture = capture;
        feed.Buffer = buffer;
        feed.Stereo48k = stereo;
        capture.StartRecording();
    }

    public IReadOnlyDictionary<Guid, string> OpenPipes(double wallclockOrigin)
    {
        ClosePipes();
        var cts = new CancellationTokenSource();
        _pipeCts = cts;
        var map = new Dictionary<Guid, string>();
        lock (_lock)
        {
            foreach (var feed in _feeds.Values)
            {
                if (feed.Capture is null || feed.Stereo48k is null) continue;
                string name = $"leapcast_{feed.SourceId:N}_{Environment.TickCount64}";
                // Created synchronously so the pipe exists before ffmpeg tries to open it.
                var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 1 << 16);
                feed.Pipe = pipe;
                var f = feed;
                feed.Writer = new Thread(() => PipeWriter(f, pipe, wallclockOrigin, cts.Token))
                {
                    IsBackground = true, Name = "Leapcast audio " + feed.Name, Priority = ThreadPriority.AboveNormal
                };
                feed.Writer.Start();
                map[feed.SourceId] = @"\\.\pipe\" + name;
            }
        }
        return map;
    }

    private static double NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static void PipeWriter(Feed feed, NamedPipeServerStream pipe, double origin, CancellationToken ct)
    {
        const int chunk = 480; // 10 ms
        var samples = new float[chunk * 2];
        try
        {
            pipe.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
            feed.Buffer?.ClearBuffer(); // don't send audio that piled up while ffmpeg was starting

            // Sample 0 == the pipeline's time origin. One clock, anchored to wall time *before* padding, so the
            // sample count equals the timeline regardless of how long ffmpeg takes to read the padding.
            double wallAtStart = NowUnix();
            var clock = Stopwatch.StartNew();
            long written = 0;
            long lead = Math.Max(0, (long)((wallAtStart - origin) * Rate));
            lead -= lead % chunk;
            var silence = new byte[chunk * 2 * sizeof(float)];
            while (written < lead && !ct.IsCancellationRequested) { pipe.Write(silence); written += chunk; }
            // device audio captured while the padding was going out belongs right after it: keep it
            while (!ct.IsCancellationRequested)
            {
                // Never skip: if ffmpeg paused reading, the backlog is written later with the samples captured
                // meanwhile, so every sample keeps its true timestamp.
                long due = (long)((wallAtStart + clock.Elapsed.TotalSeconds - origin) * Rate);

                // In steady state, a growing device buffer means the device clock runs fast: trim it so latency
                // can't creep up over a long stream. (Not while catching up: that backlog is real, timed audio.)
                var buf = feed.Buffer;
                if (buf is not null && due - written <= chunk * 2 && buf.BufferedDuration > TimeSpan.FromMilliseconds(150))
                {
                    int drop = buf.WaveFormat.AverageBytesPerSecond / 20; // 50 ms
                    drop -= drop % buf.WaveFormat.BlockAlign;
                    var trash = new byte[drop];
                    buf.Read(trash, 0, drop);
                }

                while (written < due && !ct.IsCancellationRequested)
                {
                    int got = 0;
                    try { got = feed.Stereo48k!.Read(samples, 0, samples.Length); } catch { }
                    if (got < samples.Length) Array.Clear(samples, got, samples.Length - got);
                    pipe.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
                    written += chunk;
                }
                Thread.Sleep(4);
            }
        }
        catch { /* pipe closed by ffmpeg or by ClosePipes */ }
    }

    public void ClosePipes()
    {
        _pipeCts?.Cancel();
        _pipeCts = null;
        List<Feed> feeds;
        lock (_lock) feeds = _feeds.Values.ToList();
        foreach (var f in feeds)
        {
            try { f.Pipe?.Dispose(); } catch { }
            f.Writer?.Join(1000);
            f.Pipe = null;
            f.Writer = null;
        }
    }

    public double LevelDb(Guid sourceId)
    {
        Feed? f;
        lock (_lock) _feeds.TryGetValue(sourceId, out f);
        if (f is null || f.Capture is null) return -60;
        if (Environment.TickCount64 - Interlocked.Read(ref f.PeakTicks) > 400) return -60;
        double p = f.Peak;
        return p <= 0.001 ? -60 : Math.Max(-60, 20 * Math.Log10(p));
    }

    public string? ErrorFor(Guid sourceId)
    {
        lock (_lock) return _feeds.TryGetValue(sourceId, out var f) ? f.Error : null;
    }

    public void Dispose()
    {
        ClosePipes();
        Task.Run(() =>
        {
            lock (_lock)
            {
                foreach (var f in _feeds.Values) f.Dispose();
                _feeds.Clear();
            }
        }).Wait(3000);
    }

    // ------------------------------------------------------------------ format helpers

    private static bool IsFloat(WaveFormat f) =>
        f.Encoding == WaveFormatEncoding.IeeeFloat ||
        (f is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);

    private static float PeakOf(byte[] data, int count, WaveFormat f)
    {
        float peak = 0;
        if (IsFloat(f) && f.BitsPerSample == 32)
        {
            var span = MemoryMarshal.Cast<byte, float>(data.AsSpan(0, count - count % 4));
            for (int i = 0; i < span.Length; i += 2) { float v = Math.Abs(span[i]); if (v > peak) peak = v; }
        }
        else if (f.BitsPerSample == 16)
        {
            var span = MemoryMarshal.Cast<byte, short>(data.AsSpan(0, count - count % 2));
            for (int i = 0; i < span.Length; i += 2) { float v = Math.Abs(span[i] / 32768f); if (v > peak) peak = v; }
        }
        return Math.Min(1f, peak);
    }

    /// <summary>Converts whatever the device delivers (float/PCM, mono/stereo/5.1/7.1) to interleaved stereo float.</summary>
    private sealed class StereoFloatProvider : ISampleProvider
    {
        private readonly IWaveProvider _src;
        private readonly int _channels, _bytesPerSample;
        private readonly bool _float;
        private byte[] _raw = Array.Empty<byte>();

        public WaveFormat WaveFormat { get; }

        public StereoFloatProvider(IWaveProvider src)
        {
            _src = src;
            var f = src.WaveFormat;
            _channels = Math.Max(1, f.Channels);
            _bytesPerSample = Math.Max(1, f.BitsPerSample / 8);
            _float = IsFloat(f);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, 2);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int need = frames * _channels * _bytesPerSample;
            if (_raw.Length < need) _raw = new byte[need];
            int got = _src.Read(_raw, 0, need);
            int gotFrames = got / (_channels * _bytesPerSample);
            for (int i = 0; i < gotFrames; i++)
            {
                int bas = i * _channels * _bytesPerSample;
                float l = Sample(bas), r = _channels > 1 ? Sample(bas + _bytesPerSample) : l;
                if (_channels >= 3) // fold the centre channel (game dialogue) into L/R on surround setups
                {
                    float c = Sample(bas + 2 * _bytesPerSample) * 0.707f;
                    l += c; r += c;
                }
                buffer[offset + i * 2] = Math.Clamp(l, -1f, 1f);
                buffer[offset + i * 2 + 1] = Math.Clamp(r, -1f, 1f);
            }
            int produced = gotFrames * 2;
            if (produced < count) Array.Clear(buffer, offset + produced, count - produced);
            return count;
        }

        private float Sample(int at)
        {
            if (_float && _bytesPerSample == 4) return BitConverter.ToSingle(_raw, at);
            return _bytesPerSample switch
            {
                2 => BitConverter.ToInt16(_raw, at) / 32768f,
                3 => ((_raw[at] | (_raw[at + 1] << 8) | ((sbyte)_raw[at + 2] << 16))) / 8388608f,
                4 => BitConverter.ToInt32(_raw, at) / 2147483648f,
                _ => 0f
            };
        }
    }
}
