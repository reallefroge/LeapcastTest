namespace LeapcastStudioAI;

/// <summary>
/// Listens for the clip word: everything the microphone hears is transcribed by Vosk (offline), and a clip is asked
/// for only when the transcript has the word in it (how sure it must be: <see cref="VoiceClips.Sensitivity"/>).
/// Other talking is transcribed as what it is, so it can't set it off.
/// Fires at most once per <see cref="Cooldown"/> of speech (a clip already covers the moments before it).
/// </summary>
public sealed class VoiceListener : IDisposable
{
    private static readonly object ModelLock = new();
    private static VoskApi.Model? _model;
    private static string? _modelPath;

    private Thread? _thread;
    private BlockingAudioStream? _stream;
    private volatile bool _stop;
    private string _word = "clip";
    private VoiceClips.SensitivityLevel _level = VoiceClips.Sensitivity("High");
    private DateTime _lastSpoken = DateTime.MinValue;

    // the last ~30 s of what the recognizer heard, for the diagnostic recordings
    private const int RingSeconds = 30;
    private readonly short[] _ring = new short[RingSeconds * SpeechPcmConverter.OutRate];
    private long _fed; // samples fed so far

    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(6);

    /// <summary>The word was heard: (what was said, the recognizer's score, when the word started being said, UTC).</summary>
    public event Action<string, float, DateTime>? Heard;
    /// <summary>Every finished utterance's words (best reading), with when each was said: for clip captions.</summary>
    public event Action<IReadOnlyList<CaptionWord>>? Transcribed;
    /// <summary>What is being said right now (the unfinished sentence), refreshed a few times a second.</summary>
    public string Partial => _partial;
    private volatile string _partial = "";
    /// <summary>When the last finished sentence was handed out (words up to about then are final).</summary>
    public DateTime LastResultUtc { get; private set; }
    /// <summary>Only transcribe (captions): never fire <see cref="Heard"/>.</summary>
    public bool TranscribeOnly { get; set; }
    /// <summary>What the recognizer transcribed (diagnostics).</summary>
    public event Action<string>? Debug;

    /// <summary>When set, a few seconds of audio around every hit and every near miss go here as WAV (to see why a
    /// word did or didn't count). The oldest are deleted beyond <see cref="MaxRecordings"/>.</summary>
    public string? RecordingsFolder { get; set; }
    public const int MaxRecordings = 40;

    public bool Running => _thread is { IsAlive: true };

    /// <summary>Is the speech model installed with the app?</summary>
    public static bool Available(out string? modelPath)
    {
        modelPath = VoskApi.FindModel();
        return modelPath is not null;
    }

    private static VoskApi.Model LoadModel()
    {
        lock (ModelLock)
        {
            var path = VoskApi.FindModel() ?? throw new InvalidOperationException("The speech model is missing from the app folder (speech\\" + VoskApi.ModelName + "): reinstall Leapcast Studio AI.");
            if (_model is not null && _modelPath == path) return _model;
            _model?.Dispose();
            _model = new VoskApi.Model(path);
            _modelPath = path;
            return _model;
        }
    }

    /// <summary>Starts listening to <paramref name="feed"/> (16 kHz mono 16-bit).</summary>
    public void Start(string word, BlockingAudioStream feed, string? sensitivity = "High")
    {
        Stop();
        _level = VoiceClips.Sensitivity(sensitivity);
        _word = string.IsNullOrWhiteSpace(word) ? "clip" : word.Trim().ToLowerInvariant();
        var model = LoadModel();
        var rec = new VoskApi.Recognizer(model, _level.Alternatives > 1 ? _level.Alternatives : 0);
        _stream = feed;
        _stop = false;
        _fed = 0;
        _thread = new Thread(() => Run(rec, feed)) { IsBackground = true, Name = "voice clips", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Run(VoskApi.Recognizer rec, BlockingAudioStream feed)
    {
        var bytes = new byte[SpeechPcmConverter.OutRate / 10 * 2]; // 100 ms
        var samples = new short[bytes.Length / 2];
        try
        {
            while (!_stop)
            {
                int n = feed.Read(bytes, 0, bytes.Length);
                if (n <= 0) break;
                int count = n / 2;
                Buffer.BlockCopy(bytes, 0, samples, 0, count * 2);
                for (int i = 0; i < count; i++) _ring[(_fed + i) % _ring.Length] = samples[i];
                _fed += count;
                var json = rec.Accept(samples, count);
                if (json is not null) { _partial = ""; OnResult(json, feed); }
                else if (++_chunks % 3 == 0) _partial = PartialText(rec.Partial());
                if (n < bytes.Length) break; // the stream was completed
            }
            if (!_stop) OnResult(rec.Final(), feed);
        }
        catch (Exception ex) { Debug?.Invoke("recognizer stopped: " + ex.Message); }
        finally { rec.Dispose(); }
    }

    private int _chunks;

    private static string PartialText(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("partial", out var p) ? p.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private void OnResult(string json, BlockingAudioStream feed)
    {
        LastResultUtc = DateTime.UtcNow;
        var readings = VoskApi.Parse(json);
        if (readings.Count == 0 || readings[0].Words.Count == 0 && readings.Count == 1) return;
        double top = readings[0].Score;
        Debug?.Invoke("heard: " + string.Join("  |  ", readings.Take(3).Select(r =>
            (r.Words.Count == 0 ? "(nothing)" : r.Text) + (double.IsNaN(r.Score) ? "" : $" ({r.Score - top:+0.0;-0.0;0})"))));
        if (Transcribed is { } tr && readings[0].Words.Count > 0)
        {
            DateTime At(double sec) => feed.WallTimeAt(TimeSpan.FromSeconds(sec))
                                       ?? DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(0, _fed / (double)SpeechPcmConverter.OutRate - sec));
            try { tr(readings[0].Words.Select(x => new CaptionWord(x.Text, At(x.Start), At(x.End))).ToList()); } catch { }
        }
        if (TranscribeOnly) return;
        var hit = VoiceClips.FindWord(readings, _word, _level);
        if (hit is null)
        {
            // a near miss ("lip", "flip"): kept as a recording, to tune what counts
            if (readings.Take(3).Any(r => r.Words.Count <= 3 && r.Words.Any(w => VoiceClips.NearMatch(w.Text, _word, 1))))
                Record(readings[0], "near " + readings[0].Text);
            return;
        }
        var w = hit.Value;
        var spoken = feed.WallTimeAt(TimeSpan.FromSeconds(w.Start))
                     ?? DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(0, _fed / (double)SpeechPcmConverter.OutRate - w.Start));
        string text = readings.FirstOrDefault(r => r.Words.Contains(w))?.Text ?? w.Text;
        Record(readings[0], "heard " + text);
        if (spoken - _lastSpoken < Cooldown && spoken >= _lastSpoken) { Debug?.Invoke("  (a clip was just saved)"); return; }
        _lastSpoken = spoken;
        Heard?.Invoke(text, double.IsNaN(top) ? 1f : (float)top, spoken);
    }

    /// <summary>Writes the utterance's audio (from a second before it to now) to <see cref="RecordingsFolder"/>.</summary>
    private void Record(VoskApi.Hypothesis h, string what)
    {
        var folder = RecordingsFolder;
        if (folder is null || h.Words.Count == 0) return;
        try
        {
            int rate = SpeechPcmConverter.OutRate;
            long from = Math.Max(Math.Max(0, _fed - _ring.Length), (long)((h.Words[0].Start - 1.0) * rate));
            long to = _fed;
            if (to - from > 12L * rate) from = to - 12L * rate;
            if (to <= from) return;
            var pcm = new short[to - from];
            for (long i = from; i < to; i++) pcm[i - from] = _ring[i % _ring.Length];
            Directory.CreateDirectory(folder);
            string safe = new string(what.Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : '_').ToArray());
            if (safe.Length > 60) safe = safe[..60];
            string path = Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {safe.Trim()}.wav");
            WriteWav(path, pcm, rate);
            var old = new DirectoryInfo(folder).GetFiles("*.wav").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxRecordings);
            foreach (var f in old) try { f.Delete(); } catch { }
        }
        catch (Exception ex) { Debug?.Invoke("recording not saved: " + ex.Message); }
    }

    public static void WriteWav(string path, short[] pcm, int rate)
    {
        using var f = new BinaryWriter(File.Create(path));
        int data = pcm.Length * 2;
        f.Write("RIFF"u8); f.Write(36 + data); f.Write("WAVE"u8);
        f.Write("fmt "u8); f.Write(16); f.Write((short)1); f.Write((short)1); f.Write(rate); f.Write(rate * 2); f.Write((short)2); f.Write((short)16);
        f.Write("data"u8); f.Write(data);
        foreach (var s in pcm) f.Write(s);
    }

    /// <summary>Stops listening; what was said so far is still checked (the rest of the utterance in progress).</summary>
    public void Stop()
    {
        var t = _thread;
        _thread = null;
        _stream?.Complete();
        _stream = null;
        t?.Join(TimeSpan.FromSeconds(3));
    }

    /// <summary>For tests: waits until everything written to the stream has been heard (after <see cref="Stop"/>-free completion).</summary>
    public bool WaitUntilDone(TimeSpan timeout) => _thread?.Join(timeout) ?? true;

    public void Dispose()
    {
        _stop = true;
        Stop();
    }
}
