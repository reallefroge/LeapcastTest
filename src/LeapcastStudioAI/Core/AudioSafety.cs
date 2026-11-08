using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeapcastStudioAI;

/// <summary>What stream safety does to desktop audio (the game, Discord…), set before a pipeline starts.</summary>
public sealed record AudioSafetyConfig(
    double DelaySeconds,      // 0 = off: desktop audio (and the encoded stream with it) runs this far behind
    bool MuteSlurs,           // needs the delay: a word has to be heard before it can be taken out
    bool MuteAbleist,         // the r-word too
    string CustomWords,       // more words to mute (one per line or comma-separated)
    bool LoudGuard)           // turn sudden, very loud audio right down (ear-rape protection)
{
    public static readonly AudioSafetyConfig Off = new(0, false, false, "", false);
    public bool Any => DelaySeconds > 0 || LoudGuard;
}

/// <summary>
/// The words the slur filter takes out. Kept ROT13-encoded so the source isn't a list of slurs; matched on whole
/// words (with plural / possessive endings), never inside other words.
/// </summary>
public static class SlurWords
{
    private static readonly string[] Encoded =
    {
        "avttre", "avttref", "avttn", "avttnf", "avttnm", "avttnu", "avtte", "avtref", "avtnn", "fnaqavttre", "fnaqavttref",
        "snttbg", "snttbgf", "snttbgl", "snt", "sntf", "snttl", "qlxr", "qlxrf", "genaal", "genaavrf",
        "puvax", "puvaxf", "tbbx", "tbbxf", "fcvp", "fcvpf", "fcvpx", "fcvpxf", "xvxr", "xvxrf", "jrgonpx", "jrgonpxf",
        "ornare", "ornaref", "enturnq", "enturnqf", "gbjryurnq", "gbjryurnqf", "cnxv", "cnxvf", "wvtnobb", "wvtnobbf",
        "erqfxva", "erqfxvaf", "fdhnj", "fdhnjf", "puvanzna", "qnexvr", "qnexvrf", "xnssve", "xnssvef", "ubaxl", "ubaxvrf",
        "mvccreurnq", "mvccreurnqf", "tbyyvjbt", "jbc", "jbcf", "qntb", "qntbf",
    };
    private static readonly string[] EncodedAbleist = { "ergneq", "ergneqf", "ergneqrq" };

    private static readonly Lazy<HashSet<string>> BuiltIn = new(() => new HashSet<string>(Encoded.Select(Rot13)));
    private static readonly Lazy<HashSet<string>> Ableist = new(() => new HashSet<string>(EncodedAbleist.Select(Rot13)));

    public static string Rot13(string s) => new(s.Select(c =>
        c is >= 'a' and <= 'z' ? (char)('a' + (c - 'a' + 13) % 26) : c is >= 'A' and <= 'Z' ? (char)('A' + (c - 'A' + 13) % 26) : c).ToArray());

    public static string Norm(string w) => Regex.Replace(w.ToLowerInvariant(), "[^a-z]", "");

    public static HashSet<string> Custom(string? text) =>
        new((text ?? "").Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(Norm).Where(x => x.Length > 1));

    /// <summary>Is this one recognized word one to take out?</summary>
    public static bool Is(string word, bool ableist, IReadOnlySet<string>? custom)
    {
        string w = Norm(word);
        if (w.Length < 2) return false;
        bool Hit(string x) => BuiltIn.Value.Contains(x) || (ableist && Ableist.Value.Contains(x)) || (custom?.Contains(x) ?? false);
        if (Hit(w)) return true;
        foreach (var end in new[] { "s", "es", "z" })
            if (w.Length > end.Length + 2 && w.EndsWith(end) && Hit(w[..^end.Length])) return true;
        return false;
    }
}

/// <summary>
/// Gain per 10 ms of desktop audio, keyed by its place on the stream's timeline (slot = sample index / 480):
/// 0 where a word is muted, below 1 where the loud-audio guard turned it down. Written by the detectors,
/// read by the pipe writer when that audio goes out. Remembers the last ~40 s.
/// </summary>
public sealed class ContentGains
{
    public const int SlotSamples = 480; // 10 ms at 48 kHz
    private const int Size = 4096;
    private readonly long[] _slot = new long[Size];
    private readonly float[] _gain = new float[Size];
    private readonly object _lock = new();

    public ContentGains() { Array.Fill(_slot, long.MinValue); }

    public void Limit(long slot, float gain)
    {
        lock (_lock)
        {
            int i = (int)(((slot % Size) + Size) % Size);
            if (_slot[i] != slot) { _slot[i] = slot; _gain[i] = 1; }
            _gain[i] = Math.Min(_gain[i], gain);
        }
    }

    /// <summary>Mutes the timeline between two moments (seconds), with a little room either side.</summary>
    public void Mute(double fromSeconds, double toSeconds)
    {
        long a = (long)Math.Floor(fromSeconds * 100), b = (long)Math.Ceiling(toSeconds * 100);
        for (long s = Math.Max(0, a); s <= b; s++) Limit(s, 0);
    }

    public float this[long slot]
    {
        get
        {
            lock (_lock)
            {
                int i = (int)(((slot % Size) + Size) % Size);
                return _slot[i] == slot ? _gain[i] : 1;
            }
        }
    }
}

/// <summary>
/// Ear-rape protection: watches desktop audio 10 ms at a time. When it suddenly gets far louder than it has been
/// (a blown-out mic on Discord, a screamer, a soundboard) or plain dangerously loud, it's turned right down to
/// a comfortable level, instantly, and eased back up once it calms down. Ordinary loud game moments barely move.
/// </summary>
public sealed class LoudGuard
{
    public const float AbsoluteDb = -15f;   // short-term level that's too loud whatever came before
    public const float JumpDb = 14f;        // this far above the usual level is "sudden"
    private readonly float[] _recent = new float[10]; // last 100 ms of 10 ms powers
    private int _n;
    private float _baseDb = -30f;
    private float _gainDb;
    private int _hold;
    public bool Active => _gainDb < -3;
    /// <summary>Times it stepped in (for the log).</summary>
    public int Triggers { get; private set; }

    /// <summary>Feeds 10 ms (stereo interleaved); returns the gain for it (1 = untouched).</summary>
    public float Feed(ReadOnlySpan<float> stereo)
    {
        double sum = 0; float peak = 0;
        foreach (var v in stereo) { sum += v * v; float a = Math.Abs(v); if (a > peak) peak = a; }
        float p = (float)(sum / Math.Max(1, stereo.Length));
        _recent[_n++ % _recent.Length] = p;
        float st = 0; int k = Math.Min(_n, _recent.Length);
        for (int i = 0; i < k; i++) st += _recent[i];
        float stDb = 10 * MathF.Log10(st / k + 1e-12f);

        bool loud = stDb > AbsoluteDb || (stDb > _baseDb + JumpDb && stDb > -28);
        float target = 0;
        if (loud)
        {
            float comfortable = Math.Min(-22, Math.Max(_baseDb + 4, -30));
            target = Math.Clamp(comfortable - stDb, -40, 0);
            if (!Active && target < -3) Triggers++;
            _hold = 40; // keep it down 400 ms after it stops
            // a jump that stays (quiet menu → loud battle) becomes the new normal within a few seconds;
            // dangerously loud never does
            if (stDb < AbsoluteDb) _baseDb += (stDb - _baseDb) * 0.003f;
        }
        else if (stDb > -60)
        {
            // the usual level: learns slowly (rises slower than it falls, so a long scream doesn't become "usual")
            _baseDb += (stDb - _baseDb) * (stDb > _baseDb ? 0.0015f : 0.006f);
        }

        if (target < _gainDb) _gainDb = target;                // down at once
        else if (_hold > 0) _hold--;
        else _gainDb = Math.Min(0, _gainDb + 0.3f);              // back up at 30 dB/s
        float g = MathF.Pow(10, _gainDb / 20);
        // and never let a peak out above −4 dBFS
        if (peak * g > 0.63f) g = 0.63f / peak;
        return g;
    }
}

/// <summary>
/// The slur filter's ears: transcribes desktop audio (16 kHz) as it comes in and reports every filtered word
/// with when it was said on the stream's timeline, as early as the recognizer suspects it (its running guess,
/// not only finished sentences), so it can be muted before the delayed audio goes out.
/// </summary>
public sealed class SlurSpotter : IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<short[]> _queue = new(boundedCapacity: 400);
    private readonly Thread _thread;
    private readonly Func<string, bool> _isSlur;
    private readonly Action<double, double, string> _found;
    private readonly double _t0;
    private long _fed;
    private volatile bool _stop;

    /// <param name="timelineStart">Where the first audio fed sits on the stream's timeline (seconds).</param>
    /// <param name="found">(from, to, word) on the timeline, in seconds.</param>
    public SlurSpotter(VoskApi.Model model, double timelineStart, Func<string, bool> isSlur, Action<double, double, string> found)
    {
        _t0 = timelineStart; _isSlur = isSlur; _found = found;
        var rec = new VoskApi.Recognizer(model, alternatives: 0);
        bool partialWords = rec.TryPartialWords();
        _thread = new Thread(() => Run(rec, partialWords)) { IsBackground = true, Name = "Leapcast slur filter", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Diagnostics: what the recognizer heard and when (tests).</summary>
    public Action<string>? Trace { get; set; }
    private string _lastTrace = "";

    /// <summary>Where the recognizer has got to on the timeline (s).</summary>
    public double HeardUntil => _t0 + Interlocked.Read(ref _fed) / 16000.0;

    /// <summary>Audio in (16 kHz mono); never blocks the audio thread (drops if the recognizer is far behind).</summary>
    public void Feed(short[] pcm) { if (!_stop) _queue.TryAdd(pcm); }

    private void Run(VoskApi.Recognizer rec, bool partialWords)
    {
        var reported = new HashSet<long>(); // (start in 10 ms) already muted
        string lastPartial = "";
        try
        {
            foreach (var pcm in _queue.GetConsumingEnumerable())
            {
                if (_stop) break;
                var json = rec.Accept(pcm, pcm.Length);
                _fed += pcm.Length;
                double now = _t0 + _fed / 16000.0;
                if (json is not null) { Trace?.Invoke($"{now:0.00}s result: {string.Join(" ", Words(json, "result").Select(w => w.Word))}"); Check(json, "result", now); lastPartial = ""; continue; }
                // the running guess costs time: only when caught up (a slow PC must not fall behind the delay)
                if (_queue.Count > 0) continue;
                var partial = rec.Partial();
                // word times in the running guess when this libvosk gives them; otherwise (or when it leaves them
                // out) the running text itself: a new filtered word in it was said within the last moment
                var timed = partialWords ? Words(partial, "partial_result") : new();
                if (Trace is { } tr) { string tx = Text(partial); if (tx != _lastTrace) { _lastTrace = tx; tr($"{now:0.00}s q{_queue.Count} partial{(timed.Count > 0 ? "+times" : "")}: {tx}"); } }
                if (timed.Count > 0) Check(partial, "partial_result", now);
                else
                {
                    // no word times in the running guess: a new slur in it was said within the last moment or so
                    string text = Text(partial);
                    if (text != lastPartial)
                    {
                        var before = lastPartial.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var after = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < after.Length; i++)
                            if ((i >= before.Length || before[i] != after[i]) && _isSlur(after[i]))
                            {
                                long key = (long)(now * 2);
                                if (reported.Add(-key)) _found(now - 1.6, now + 0.25, after[i]);
                            }
                        lastPartial = text;
                    }
                }
            }
        }
        catch { }
        finally { rec.Dispose(); }

        void Check(string js, string prop, double now)
        {
            foreach (var (w, s, e) in Words(js, prop))
            {
                if (!_isSlur(w)) continue;
                double from = _t0 + s, to = _t0 + e;
                if (to < from || to > now + 1) { from = now - 1.6; to = now + 0.25; } // odd times: be generous
                if (reported.Add((long)(from * 100))) _found(from - 0.15, to + 0.15, w);
            }
            if (reported.Count > 5000) reported.Clear();
        }
    }

    private static string Text(string json)
    {
        try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty("partial", out var p) ? p.GetString() ?? "" : ""; }
        catch { return ""; }
    }

    internal static List<(string Word, double Start, double End)> Words(string json, string prop)
    {
        var o = new List<(string, double, double)>();
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var w in arr.EnumerateArray())
                    o.Add((w.GetProperty("word").GetString() ?? "", w.TryGetProperty("start", out var s) ? s.GetDouble() : 0,
                        w.TryGetProperty("end", out var e) ? e.GetDouble() : 0));
        }
        catch { }
        return o;
    }

    public void Dispose()
    {
        _stop = true;
        _queue.CompleteAdding();
        _thread.Join(2000);
    }
}

/// <summary>
/// One desktop audio feed's safety stage, between the device and the stream (48 kHz stereo, 10 ms at a time, in
/// timeline order): the loud-audio guard and the slur filter look at the audio as it arrives; it then waits in a
/// delay line for <see cref="AudioSafetyConfig.DelaySeconds"/> and goes out with their decisions applied.
/// </summary>
public sealed class DesktopSafety : IDisposable
{
    private AudioSafetyConfig _cfg;
    private readonly ContentGains _gains = new();
    internal ContentGains Gains => _gains;
    private LoudGuard? _guard;
    private SlurSpotter? _spotter;
    private readonly Func<VoskApi.Model?>? _model;
    private Task<VoskApi.Model?>? _modelTask;
    private readonly float[]? _line;     // delay line (stereo interleaved)
    private long _lineAt;
    private float[] _tmp = Array.Empty<float>();
    private readonly int _delaySlots;
    private float _lastGain = 1;
    private readonly Downsampler _down = new();
    private readonly List<short> _pending = new();
    private long _latest;
    /// <summary>The last filtered word's margin: how long before it would have gone out it was muted (s).</summary>
    public double LastMargin { get; private set; } = double.NaN;
    private HashSet<string> _custom;
    public event Action<string>? Log;
    public int Muted { get; private set; }
    public int LoudTriggers => _guard?.Triggers ?? 0;
    public double DelaySeconds => _delaySlots / 100.0;
    /// <summary>Diagnostics (tests): the recognizer's running results.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Live changes (word list, filter and guard on/off); the delay itself stays as built.</summary>
    public void Update(AudioSafetyConfig cfg)
    {
        _custom = SlurWords.Custom(cfg.CustomWords);
        if (cfg.LoudGuard && _guard is null) _guard = new LoudGuard();
        if (!cfg.LoudGuard) _guard = null;
        if (!cfg.MuteSlurs && _spotter is not null) { _spotter.Dispose(); _spotter = null; }
        _cfg = cfg with { DelaySeconds = _delaySlots / 100.0, MuteSlurs = cfg.MuteSlurs && _delaySlots > 0 };
    }

    public DesktopSafety(AudioSafetyConfig cfg, Func<VoskApi.Model?>? model)
    {
        _cfg = cfg; _model = model;
        _custom = SlurWords.Custom(cfg.CustomWords);
        if (cfg.LoudGuard) _guard = new LoudGuard();
        _delaySlots = (int)Math.Round(Math.Max(0, cfg.DelaySeconds) * 100);
        if (_delaySlots > 0) _line = new float[_delaySlots * ContentGains.SlotSamples * 2];
    }

    /// <summary>Takes 10 ms that just arrived (sample index <paramref name="index"/> on the timeline) and returns the 10 ms to send now, in place.</summary>
    public void Process(float[] stereo, long index)
    {
        long slot = index / ContentGains.SlotSamples;
        Interlocked.Exchange(ref _latest, index);
        // ---- look at what just arrived
        if (_guard is { } guard)
        {
            float g = guard.Feed(stereo);
            if (g < 0.999f)
            {
                _gains.Limit(slot, g);
                // with a delay there's time to start turning it down a moment before it hits
                if (_delaySlots > 3) for (int k = 1; k <= 3; k++) _gains.Limit(slot - k, Math.Min(1, g * (1 + k * 0.6f)));
            }
        }
        if (_cfg.MuteSlurs && _delaySlots > 0)
        {
            // the recognizer's model loads off the audio thread (the first time takes a moment)
            if (_spotter is null && _model is not null) _modelTask ??= Task.Run(() => { try { return _model(); } catch { return null; } });
            if (_spotter is null && _modelTask is { IsCompletedSuccessfully: true, Result: { } m })
                _spotter = new SlurSpotter(m, index / 48000.0, w => SlurWords.Is(w, _cfg.MuteAbleist, _custom), (from, to, word) =>
                {
                    _gains.Mute(from, to);
                    Muted++;
                    // it goes out DelaySeconds after it arrived: how much of that was left when it was caught
                    LastMargin = from + _delaySlots / 100.0 - Interlocked.Read(ref _latest) / 48000.0;
                    Log?.Invoke($"Stream safety: muted a filtered word at {TimeSpan.FromSeconds(Math.Max(0, from)):hh\\:mm\\:ss} (desktop audio).");
                });
            if (_spotter is not null && Trace is not null) _spotter.Trace = Trace;
            if (_spotter is not null)
            {
                // the recognizer gets 100 ms at a time (10 ms pieces would cost it far more work)
                _pending.AddRange(_down.Process(stereo));
                if (_pending.Count >= 1600) { _spotter.Feed(_pending.ToArray()); _pending.Clear(); }
            }
        }

        // ---- the delay line: what goes out now arrived DelaySeconds ago
        long outSlot = slot;
        if (_line is not null)
        {
            int n = stereo.Length, len = _line.Length;
            int at = (int)(_lineAt % len);
            if (_tmp.Length != n) _tmp = new float[n];
            Array.Copy(_line, at, _tmp, 0, n);
            Array.Copy(stereo, 0, _line, at, n);
            Array.Copy(_tmp, stereo, n);
            _lineAt += n;
            outSlot = slot - _delaySlots;
        }

        // ---- apply the decisions, ramped across the 10 ms (no clicks)
        float target = outSlot < 0 ? 1 : _gains[outSlot];
        if (target < 0.999f || _lastGain < 0.999f)
        {
            int frames = stereo.Length / 2;
            for (int i = 0; i < frames; i++)
            {
                float g = _lastGain + (target - _lastGain) * (i + 1) / frames;
                stereo[2 * i] *= g; stereo[2 * i + 1] *= g;
            }
        }
        _lastGain = target;
    }

    public void Dispose() { _spotter?.Dispose(); _spotter = null; }

    /// <summary>48 kHz stereo float → 16 kHz mono 16-bit for the recognizer (low-passed so nothing folds back).</summary>
    private sealed class Downsampler
    {
        private static readonly float[] Taps = MakeTaps();
        private readonly float[] _hist = new float[Taps.Length];
        private int _phase;
        private static float[] MakeTaps()
        {
            const int N = 31; var t = new float[N]; double fc = 7000.0 / 48000, sum = 0;
            for (int i = 0; i < N; i++)
            {
                double x = i - (N - 1) / 2.0;
                double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
                double w = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (N - 1));
                t[i] = (float)(sinc * w); sum += t[i];
            }
            for (int i = 0; i < N; i++) t[i] /= (float)sum;
            return t;
        }
        public short[] Process(float[] stereo)
        {
            int frames = stereo.Length / 2;
            var o = new List<short>(frames / 3 + 1);
            for (int i = 0; i < frames; i++)
            {
                Array.Copy(_hist, 1, _hist, 0, _hist.Length - 1);
                _hist[^1] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f;
                if (++_phase % 3 != 0) continue;
                float acc = 0;
                for (int k = 0; k < Taps.Length; k++) acc += Taps[k] * _hist[k];
                o.Add((short)Math.Clamp(acc * 32767f, -32768, 32767));
            }
            return o.ToArray();
        }
    }
}
