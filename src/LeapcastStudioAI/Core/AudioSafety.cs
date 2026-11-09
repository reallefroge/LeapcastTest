using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeapcastStudioAI;

/// <summary>What stream safety does to desktop audio (the game, Discord…), set before a pipeline starts.</summary>
public sealed record AudioSafetyConfig(
    double DelaySeconds,      // 0 = off: desktop audio (and the encoded stream with it) runs this far behind
    bool MuteSlurs,           // needs the delay: a word has to be heard before it can be taken out
    bool MuteAbleist,         // the r-word too
    string CustomWords,       // more words to mute (one per line or comma-separated)
    bool LoudGuard,           // turn sudden, very loud audio right down (ear-rape protection)
    bool MuteCodes = false,   // letters read out (game lobby codes) are hidden too
    bool FilterMic = false)   // your microphone goes through the same filter (codes and words you say)
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

    /// <summary>All the words the filter takes out (for the keyword listener).</summary>
    public static IEnumerable<string> All(bool ableist, IReadOnlySet<string>? custom) =>
        BuiltIn.Value.Concat(ableist ? Ableist.Value : Enumerable.Empty<string>()).Concat(custom ?? (IEnumerable<string>)Array.Empty<string>()).Distinct();

    /// <summary>
    /// How a word sounds, roughly: consonants kept in order (soft and hard spellings of the same sound made equal),
    /// the first vowel kept, every later run of vowels one "a", endings that only make a plural or a casual form
    /// dropped (and whether one was).
    /// </summary>
    public static (string Key, bool Ending) Sound(string word)
    {
        string w = Norm(word);
        if (w.Length < 3) return (w, false);
        w = w.Replace("ph", "f").Replace("ck", "k").Replace("qu", "kw").Replace("q", "k").Replace("x", "ks");
        w = Regex.Replace(w, "c(?=[eiy])", "s").Replace('c', 'k');
        w = Regex.Replace(w, "(.)\\1+", "$1");              // gg → g
        w = Regex.Replace(w, "(?<=.)h", "");                  // silent h after a letter
        var m = Regex.Match(w, "[aeiouy]+");                  // the first vowel stays itself (nag ≠ n?g)
        if (m.Success) w = w[..m.Index] + char.ToUpperInvariant(m.Value[0] == 'y' ? 'i' : m.Value[0]) + w[(m.Index + m.Length)..];
        w = Regex.Replace(w, "[aeiouy]+", "a");               // later vowels → a
        string w2 = Regex.Replace(w, "(as|az|s|z)$", "");     // plural endings
        string w3 = Regex.Replace(w2, "(ar|a)$", "");         // -er / -a / -ah / -uh
        return (w3, w3 != w);
    }

    public static string SoundKey(string word) => Sound(word).Key;

    // the words a near-miss is caught for: only these, because checked against 200,000 English words their
    // sound-alikes are only themselves said differently (others' sound-alikes include "speaker", "decay", "cafe")
    private static readonly string[] EncodedNear = { "avttre", "snttbg" };
    private static readonly Lazy<HashSet<string>> NearKeys = new(() => new HashSet<string>(EncodedNear.Select(e => SoundKey(Rot13(e)))));
    private static readonly Lazy<string> AbleistKey = new(() => SoundKey(Rot13("ergneq")));

    /// <summary>
    /// A filtered word, or one that sounds just like the n-word or the f-word with only its ending changed (said
    /// casually, cut short, misheard: "-a", "-ah", "-uh", "-az", "-er"). Ordinary words never match: a different
    /// first vowel or any different consonant (bigger, trigger, nag, nigh, neighbor, Niagara) is a different word.
    /// </summary>
    public static bool Near(string word, bool ableist, IReadOnlySet<string>? custom)
    {
        if (Is(word, ableist, custom)) return true;
        string w = Norm(word);
        if (w.Length < 4) return false;
        var (k, ending) = Sound(w);
        if (!ending && k.Length < 5) return false;
        return NearKeys.Value.Contains(k) || (ableist && k == AbleistKey.Value);
    }

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
    // mutes: a confirmed mute (−1) or a tentative one (its id, which can be released); 0 = none
    private readonly long[] _muteSlot = new long[Size];
    private readonly int[] _mute = new int[Size];
    private readonly object _lock = new();

    public ContentGains() { Array.Fill(_slot, long.MinValue); Array.Fill(_muteSlot, long.MinValue); }

    private static int Index(long slot) => (int)(((slot % Size) + Size) % Size);

    public void Limit(long slot, float gain)
    {
        lock (_lock)
        {
            int i = Index(slot);
            if (_slot[i] != slot) { _slot[i] = slot; _gain[i] = 1; }
            _gain[i] = Math.Min(_gain[i], gain);
        }
    }

    /// <summary>Mutes the timeline between two moments (seconds). <paramref name="tentative"/>: an id the mute can be released by.</summary>
    public void Mute(double fromSeconds, double toSeconds, int tentative = 0)
    {
        long a = (long)Math.Floor(fromSeconds * 100), b = (long)Math.Ceiling(toSeconds * 100);
        lock (_lock)
            for (long s = Math.Max(0, a); s <= b; s++)
            {
                int i = Index(s);
                if (_muteSlot[i] != s) { _muteSlot[i] = s; _mute[i] = 0; }
                // a confirmed mute is never downgraded to a tentative one
                if (_mute[i] != -1) _mute[i] = tentative == 0 ? -1 : _mute[i] == 0 ? tentative : _mute[i];
            }
    }

    /// <summary>Lifts a tentative mute (only its own slots: a confirmed or another mute stays).</summary>
    public void Release(int tentative, double fromSeconds, double toSeconds)
    {
        long a = (long)Math.Floor(fromSeconds * 100), b = (long)Math.Ceiling(toSeconds * 100);
        lock (_lock)
            for (long s = Math.Max(0, a); s <= b; s++)
            {
                int i = Index(s);
                if (_muteSlot[i] == s && _mute[i] == tentative) _mute[i] = 0;
            }
    }

    public bool Muted(long slot) { lock (_lock) { int i = Index(slot); return _muteSlot[i] == slot && _mute[i] != 0; } }

    public float this[long slot]
    {
        get
        {
            lock (_lock)
            {
                int i = Index(slot);
                if (_muteSlot[i] == slot && _mute[i] != 0) return 0;
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

/// <summary>A word the recognizer heard, with when (seconds on the stream's timeline); Estimated: from its running guess.</summary>
public readonly record struct HeardWord(string Text, double Start, double End, bool Estimated = false);

/// <summary>
/// Times for the words of a recognizer's running guess, taken from when each first showed up in it: a word
/// appears about <see cref="Lag"/> after it ends (the guess with real word times comes ~1.5 s later, too late
/// for a 2 s delay). A word the guess revises keeps its place; finished sentences bring exact times.
/// </summary>
public sealed class GuessTimes
{
    public const double Lag = 0.25;
    public const double MaxWord = 0.75;
    private readonly List<HeardWord> _est = new();
    private double _floor = double.NegativeInfinity;   // where the current sentence can start (the last one's end)

    public void Finished(double end) { _est.Clear(); _floor = end; }

    public IReadOnlyList<HeardWord> Update(string text, double now)
    {
        var cur = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var old = _est.ToList();
        _est.Clear();
        // words already there keep their times (a revised word keeps its place)
        for (int i = 0; i < Math.Min(cur.Length, old.Count); i++) _est.Add(old[i] with { Text = cur[i] });
        int fresh = cur.Length - _est.Count;
        double baseEnd = _est.Count > 0 ? _est[^1].End : _floor;
        double target = now - Lag;
        for (int j = 0; j < fresh; j++)
        {
            // new since the last look: said since the last known word, the last of them just now
            double end = double.IsInfinity(baseEnd) || baseEnd >= target
                ? target - (fresh - 1 - j) * 0.12   // several at once: assume quick speech (never makes a code out of talk)
                : Math.Max(baseEnd + (target - baseEnd) * (j + 1) / fresh, target - (fresh - 1 - j) * 0.12);
            double prev = _est.Count > 0 ? _est[^1].End : _floor;
            double start = Math.Max(double.IsInfinity(prev) ? double.MinValue : prev, end - MaxWord);
            _est.Add(new HeardWord(cur[_est.Count], Math.Min(start, end - 0.08), end, Estimated: true));
        }
        return _est.ToList();
    }
}

/// <summary>
/// Letters said out loud, the way a game code (an Among Us lobby code) is read: "C, E, U, W, Y, B", "see e you
/// double-you why bee", "charlie echo…". Strong letters are words people only say when spelling (a bare "e", "w");
/// weak ones are also ordinary words ("see", "you", "why", "are", "I").
/// </summary>
public static class CodeLetters
{
    private static readonly HashSet<string> Weak = new()
    {
        "a", "i", "o", "see", "sea", "be", "bee", "you", "yew", "why", "wye", "are", "oh", "owe", "eye", "aye", "tea", "tee", "pea", "pee",
        "queue", "cue", "jay", "kay", "gee", "dee", "ex", "em", "en", "ay", "acts", "ax", "axe", "eff", "elle",
    };
    private static readonly HashSet<string> StrongNames = new() { "ef", "el", "ell", "ess", "vee", "zee", "zed", "aitch", "que", "kew", "dubya" };
    private static readonly HashSet<string> Nato = new()
    {
        "alpha", "alfa", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "juliett", "kilo", "lima",
        "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "whisky", "yankee", "zulu",
    };
    /// <summary>Words that make what comes next a code.</summary>
    public static readonly HashSet<string> Arming = new() { "code", "codes", "lobby", "lobbies" };
    /// <summary>Sounds people make between letters (they don't end the run).</summary>
    public static readonly HashSet<string> Fillers = new() { "uh", "um", "er", "ah", "hmm", "huh", "mm" };
    /// <summary>Words between "code" and the letters ("the code is…", "lobby code's…").</summary>
    private static readonly HashSet<string> Lead = new() { "is", "its", "it's", "the", "was", "code's", "codes", "code", "lobby", "new", "our", "my", "it", "so", "okay", "ok", "now", "here", "for" };

    public enum Kind { None, Weak, Strong, Nato, Double }

    public static Kind Of(string word)
    {
        string w = SlurWords.Norm(word);
        if (w.Length == 0) return Kind.None;
        if (w == "double") return Kind.Double;
        if (w.Length == 1) return w is "a" or "i" or "o" ? Kind.Weak : Kind.Strong;
        // "b's", "c's" (the recognizer's plural of a letter)
        if (w.Length == 2 && w[1] == 's' && word.Contains('\'')) return Kind.Strong;
        if (StrongNames.Contains(w)) return Kind.Strong;
        if (Weak.Contains(w)) return Kind.Weak;
        if (Nato.Contains(w)) return Kind.Nato;
        return Kind.None;
    }

    /// <summary>A run of letters said one after another.</summary>
    public sealed record Run(IReadOnlyList<HeardWord> Letters, int Strong, int NatoCount, bool Closed, bool Armed)
    {
        public int Count => Letters.Count;
        public double From => Letters[0].Start;
        public double To => Letters[^1].End;
        /// <summary>Mean time from one letter's start to the next (s): reading letters out is slow and even.</summary>
        public double Pace => Count > 1 ? (Letters[^1].End - Letters[0].End) / (Count - 1) : 0;
        public int StrongCount => Strong + (NatoCount >= 3 ? NatoCount : 0);

        /// <summary>
        /// A code: after "code"/"lobby" any two letters; otherwise (the fallback) four or more letters in a row,
        /// read at the pace of someone spelling or mostly letters nobody says except when spelling.
        /// </summary>
        public bool IsCode => (Armed && Count >= 2)
            || (Count >= 4 && (StrongCount >= 2 || (StrongCount >= 1 && Pace >= 0.3) || Pace >= 0.38));

        /// <summary>Could still become a code: hold it back until it's decided.</summary>
        public bool Suspect => !Closed && (Armed || StrongCount >= 1 || (Count >= 3 && Pace >= 0.33) || (Count >= 2 && Pace >= 0.45));

        /// <summary>
        /// Someone is (probably) reading a code right now: hold back everything from its start until it's over, so
        /// letters the recognizer hasn't written down yet can't slip out.
        /// </summary>
        public bool Reading => !Closed && (Armed || IsCode || StrongCount >= 2 || (Count >= 3 && Pace >= 0.33));
    }

    public const double MaxGap = 1.3;   // longer than this between letters: a new run

    /// <summary>
    /// The runs of letters in what was heard (time order). <paramref name="frontier"/>: how far the words heard are
    /// known to be complete (a run that ended more than <see cref="MaxGap"/> before it is over).
    /// </summary>
    public static List<Run> Runs(IReadOnlyList<HeardWord> words, double frontier)
    {
        var runs = new List<Run>();
        var cur = new List<HeardWord>();
        int strong = 0, nato = 0, bridged = 0;
        double lastArm = double.NegativeInfinity;
        bool armed = false;
        void End(bool closedByWord)
        {
            if (cur.Count > 0)
                runs.Add(new Run(cur.ToList(), strong, nato, closedByWord || frontier - cur[^1].End > MaxGap, armed));
            cur.Clear(); strong = 0; nato = 0; bridged = 0;
        }
        bool LetterAt(int j) => j < words.Count && Of(words[j].Text) != Kind.None;
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var k = Of(w.Text);
            string n = SlurWords.Norm(w.Text);
            if (cur.Count > 0 && w.Start - cur[^1].End > MaxGap) End(false);
            if (k == Kind.Double)
            {
                // "double you" is W; "double e" is two e's
                if (i + 1 < words.Count && Of(words[i + 1].Text) is Kind.Strong or Kind.Weak && words[i + 1].Start - w.End < 0.8)
                {
                    string next = SlurWords.Norm(words[i + 1].Text);
                    if (cur.Count == 0) armed = w.Start - lastArm < 10;
                    if (next is "you" or "u") { cur.Add(new HeardWord("w", w.Start, words[i + 1].End)); strong++; }
                    else { cur.Add(w with { Text = next }); cur.Add(words[i + 1]); strong += 2; }
                    i++;
                    continue;
                }
                End(true);
                continue;
            }
            if (k == Kind.None)
            {
                if (Fillers.Contains(n)) continue;
                // a letter the recognizer wrote as a word ("Z" heard as "the") in the middle of a code being read
                if (cur.Count > 0 && (strong >= 2 || armed) && bridged < 2 && (w.Estimated || w.End - w.Start < 0.6) && LetterAt(i + 1)
                    && w.Start - cur[^1].End <= MaxGap && words[i + 1].Start - w.End <= MaxGap && !Arming.Contains(n))
                {
                    cur.Add(w); bridged++;
                    continue;
                }
                // the last word written so far, after letters being read out: the next one decides (a misheard
                // letter in the running guess mustn't let the rest of the code out)
                if (i == words.Count - 1 && cur.Count > 0 && (armed || strong >= 1 || cur.Count >= 3)) continue;
                End(true);
                if (Arming.Contains(n)) lastArm = w.End;
                continue;
            }
            if (cur.Count == 0) armed = w.Start - lastArm < 10;
            cur.Add(w);
            if (k == Kind.Strong) strong++;
            if (k == Kind.Nato) nato++;
        }
        End(false);
        return runs;
    }

    /// <summary>
    /// "…the code is" with nothing after it yet: where the letters would start (the end of "code"), or null. Held
    /// back so the first letter is covered even before the recognizer writes it down.
    /// </summary>
    public static double? OpenArm(IReadOnlyList<HeardWord> words, double now)
    {
        for (int i = words.Count - 1; i >= 0; i--)
        {
            string n = SlurWords.Norm(words[i].Text);
            if (Arming.Contains(n))
            {
                if (now - words[i].End > 4.5) return null;
                for (int j = i + 1; j < words.Count; j++)
                {
                    string m = SlurWords.Norm(words[j].Text);
                    if (Lead.Contains(m) || Fillers.Contains(m)) continue;
                    if (Of(words[j].Text) != Kind.None) return null;   // letters: their run holds them
                    if (j == words.Count - 1) break;                   // one word so far: could be a misheard letter
                    return null;                                       // ordinary talk: not a code
                }
                return words[i].End;
            }
        }
        return null;
    }
}

/// <summary>
/// Decides what's muted. Two ears report to it: a quick keyword listener that only knows the filtered words (it
/// says "maybe" early, and that stretch is held back at once) and the full recognizer, which knows ~100,000 words
/// and has the last say: a filtered word, or one that sounds just like it, is muted for good; an ordinary word
/// ("bigger", "trigger") lets the stretch go out untouched. Until the full recognizer has written down what was
/// said there, it stays held. Spoken codes are held back the same way while someone may be reading one out.
/// </summary>
public sealed class SafetyJudge
{
    private sealed class Hold { public int Id; public double From, To, CoreFrom, CoreTo, Since; }
    private readonly ContentGains _gains;
    private readonly object _lock = new();
    private int _nextId = 1;
    private readonly List<Hold> _slurHolds = new(), _codeHolds = new();
    private readonly List<HeardWord> _history = new();     // finished words, last ~20 s (a code can span two sentences)
    private double _finalTo = double.NegativeInfinity;      // audio the last finished sentence covered (its words are settled)
    private readonly HashSet<long> _done = new();           // confirmed mutes already reported
    private readonly List<(double From, double To)> _codesMuted = new();

    public Func<string, bool> IsSlur { get; set; }
    public bool Codes { get; set; }
    /// <summary>A stretch muted for good: (from, to, what: "word" or "code").</summary>
    public event Action<double, double, string>? Muted;
    public int Held { get; private set; }
    public int Released { get; private set; }

    public SafetyJudge(ContentGains gains, Func<string, bool> isSlur, bool codes) { _gains = gains; IsSlur = isSlur; Codes = codes; }

    /// <summary>The keyword listener thinks it heard a filtered word in [from, to]: held back until the full recognizer decides.</summary>
    public void Suspect(double from, double to, string word, double now)
    {
        lock (_lock)
        {
            double a = from - 0.12, b = to + 0.12;
            if (_slurHolds.FirstOrDefault(h => h.From < b && h.To > a) is { } h)
            {
                h.CoreFrom = Math.Min(h.CoreFrom, from); h.CoreTo = Math.Max(h.CoreTo, to);
                if (a < h.From || b > h.To) { h.From = Math.Min(h.From, a); h.To = Math.Max(h.To, b); _gains.Mute(h.From, h.To, h.Id); }
                return;
            }
            var hold = new Hold { Id = _nextId++, From = a, To = b, CoreFrom = from, CoreTo = to, Since = now };
            _slurHolds.Add(hold);
            _gains.Mute(a, b, hold.Id);
            Held++;
        }
    }

    /// <summary>
    /// The full recognizer's words: <paramref name="final"/> = a finished sentence (otherwise its running guess).
    /// <paramref name="now"/>: how far it has listened (s on the timeline).
    /// </summary>
    public void Heard(IReadOnlyList<HeardWord> words, double now, bool final)
    {
        lock (_lock)
        {
            // 1. filtered words: muted for good
            foreach (var w in words)
                if (IsSlur(w.Text))
                {
                    // a time from the running guess is rough: wider (the finished sentence brings the exact one)
                    // (a word can show up before it's finished: the end gets a little more room)
                    if (w.Estimated) Confirm(w.Start - 0.25, w.End + 0.22, "word");
                    else Confirm(w.Start - 0.1, w.End + 0.1, "word");
                }
            if (final) _finalTo = Math.Max(_finalTo, now);

            // 2. what the keyword listener held back: decided once the full recognizer has written down what was said there
            double lastStart = words.Count > 0 ? words[^1].Start : double.NegativeInfinity;
            for (int i = _slurHolds.Count - 1; i >= 0; i--)
            {
                var h = _slurHolds[i];
                double core = Math.Max(0.05, h.CoreTo - h.CoreFrom);
                var over = words.Concat(_history).Where(w =>
                    Math.Min(w.End, h.CoreTo) - Math.Max(w.Start, h.CoreFrom) >= 0.5 * Math.Min(core, Math.Max(0.05, w.End - w.Start))).ToList();
                bool settled = _finalTo > h.CoreTo + 0.1 || lastStart >= h.CoreTo - 0.05;
                // the full recognizer has written down what was said there: a filtered word in it is already muted
                // for good (step 1), everything else there goes out
                if (settled && (over.Count > 0 || _finalTo > h.CoreTo + 0.3)) { Release(h); _slurHolds.RemoveAt(i); continue; }
                // gone out long ago: forget it (still undecided: it stayed muted)
                if (now - h.Since > 15) _slurHolds.RemoveAt(i);
            }

            if (final)
            {
                _history.AddRange(words);
                _history.RemoveAll(w => w.End < now - 20);
            }

            // 3. spoken codes
            if (Codes) Codes_(words, now, final);
            else foreach (var h in _codeHolds.ToList()) { Release(h); _codeHolds.Remove(h); }

            if (_done.Count > 5000) _done.Clear();
            _codesMuted.RemoveAll(c => c.To < now - 30);
        }
    }

    private void Codes_(IReadOnlyList<HeardWord> words, double now, bool final)
    {
        // what's known so far: finished sentences, then the running guess (which runs behind the audio a little)
        var all = final ? _history.ToList()
            : _history.Where(w => words.Count == 0 || w.End <= words[0].Start + 0.05).Concat(words).ToList();
        double frontier = final ? now : now - 0.6;
        var want = new List<(double A, double B)>();
        foreach (var r in CodeLetters.Runs(all, frontier))
        {
            double a = r.From - 0.1, b = r.To + 0.12;
            // the last letter's end is only estimated (running guess): muted for good up to it, the bit after held
            // until the finished sentence says exactly where it ends (so the next word isn't clipped)
            if (r.IsCode && r.Letters[^1].Estimated) { want.Add((r.To - 0.05, b + 0.1)); b = r.To - 0.05; }
            if (r.IsCode)
            {
                if (!_codesMuted.Any(c => c.From < b && c.To > a)) Muted?.Invoke(a, b, "code");
                var merged = (From: Math.Min(a, _codesMuted.Where(c => c.From < b && c.To > a).Select(c => c.From).DefaultIfEmpty(a).Min()), To: b);
                _codesMuted.RemoveAll(c => c.From < b && c.To > a);
                _codesMuted.Add(merged);
                _gains.Mute(a, b);
            }
            if (r.Reading) want.Add((a, Math.Max(b, now + 1.0)));       // still being read: everything from its start
            else if (r.Suspect && !r.IsCode) want.Add((a, b));          // a letter or two: just those, until it's clear
        }
        if (CodeLetters.OpenArm(all, now) is double armEnd) want.Add((armEnd, now + 1.0));
        // join what overlaps
        want.Sort((x, y) => x.A.CompareTo(y.A));
        var joined = new List<(double A, double B)>();
        foreach (var w in want)
            if (joined.Count > 0 && w.A <= joined[^1].B) joined[^1] = (joined[^1].A, Math.Max(joined[^1].B, w.B));
            else joined.Add(w);

        // the held stretches become exactly these: grown, shrunk (the end of a code read out: the rest goes out), new or let go
        var used = new HashSet<Hold>();
        foreach (var (a, b) in joined)
        {
            var touching = _codeHolds.Where(h => !used.Contains(h) && h.From < b && h.To > a).ToList();
            var h = touching.FirstOrDefault();
            foreach (var o in touching.Skip(1)) { Release(o); _codeHolds.Remove(o); }
            if (h is null) { h = new Hold { Id = _nextId++, From = a, To = b, Since = now }; _codeHolds.Add(h); Held++; }
            else
            {
                if (h.To > b) _gains.Release(h.Id, b, h.To);
                if (h.From < a) _gains.Release(h.Id, h.From, a);
                h.From = a; h.To = b;
            }
            _gains.Mute(a, b, h.Id);
            used.Add(h);
        }
        foreach (var h in _codeHolds.Where(h => !used.Contains(h)).ToList()) { Release(h); _codeHolds.Remove(h); }
    }

    private void Confirm(double from, double to, string what)
    {
        _gains.Mute(from, to);
        if (_done.Add((long)Math.Round(from * 5))) Muted?.Invoke(from, to, what);
    }

    private void Release(Hold h) { _gains.Release(h.Id, h.From, h.To); Released++; }
}

/// <summary>
/// The safety filter's ears: transcribes the audio (16 kHz) as it comes in with two recognizers, a quick keyword
/// listener (only the filtered words) and the full one, and tells the <see cref="SafetyJudge"/> what each heard and
/// when on the stream's timeline, as early as they suspect it (their running guesses, not only finished sentences).
/// </summary>
public sealed class SpeechListener : IDisposable
{
    private sealed class Ear
    {
        // audio to transcribe, or (Pcm null) a stretch of silence that was skipped
        public readonly System.Collections.Concurrent.BlockingCollection<(short[]? Pcm, int Skip)> Queue = new(boundedCapacity: 400);
        public Thread? Thread;
        public long Fed;            // samples the recognizer was given
        public long Skipped;        // samples of silence not given to it (still on the timeline)
        public readonly List<(long At, long Total)> Skips = new();   // recognizer position → silence skipped before it
        /// <summary>Recognizer time (s since its start) → timeline seconds.</summary>
        public double Timeline(double t0, double recSeconds)
        {
            long at = (long)(recSeconds * 16000), skipped = 0;
            lock (Skips) foreach (var (a, total) in Skips) { if (a <= at) skipped = total; else break; }
            return t0 + recSeconds + skipped / 16000.0;
        }
        public double Now(double t0) => t0 + (Interlocked.Read(ref Fed) + Interlocked.Read(ref Skipped)) / 16000.0;
        public void Skip(int n)
        {
            long total = Interlocked.Add(ref Skipped, n);
            lock (Skips) Skips.Add((Interlocked.Read(ref Fed), total));
        }
    }
    private readonly Ear _main = new();
    private readonly Ear? _keys;
    private readonly SafetyJudge _judge;
    private readonly double _t0;
    private readonly VoskApi.Model _model;
    private volatile bool _stop;

    /// <param name="timelineStart">Where the first audio fed sits on the stream's timeline (seconds).</param>
    /// <param name="keywords">The filtered words for the quick listener (null: none, e.g. only codes are filtered).</param>
    public SpeechListener(VoskApi.Model model, double timelineStart, SafetyJudge judge, IReadOnlyCollection<string>? keywords)
    {
        _t0 = timelineStart; _judge = judge; _model = model;
        var rec = new VoskApi.Recognizer(model, alternatives: 0);
        _main.Thread = new Thread(() => RunMain(rec)) { IsBackground = true, Name = "Leapcast safety (words)", Priority = ThreadPriority.AboveNormal };
        _main.Thread.Start();
        if (keywords is { Count: > 0 })
            try
            {
                var kr = VoskApi.Recognizer.Keywords(model, keywords);
                _keys = new Ear();
                _keys.Thread = new Thread(() => RunKeys(kr)) { IsBackground = true, Name = "Leapcast safety (keywords)", Priority = ThreadPriority.AboveNormal };
                _keys.Thread.Start();
            }
            catch { _keys = null; } // an older libvosk without grammars: the full recognizer alone
    }

    /// <summary>Diagnostics: what the recognizers heard and when (tests).</summary>
    public Action<string>? Trace { get; set; }
    public bool HasKeywordListener => _keys is not null;

    /// <summary>Where the full recognizer has got to on the timeline (s).</summary>
    public double HeardUntil => _main.Now(_t0);

    /// <summary>What the full recognizer is hearing right now (its unfinished sentence).</summary>
    public string Partial => _partial;
    private volatile string _partial = "";
    /// <summary>Every finished sentence (timeline seconds): the desktop transcript comes from here too.</summary>
    public event Action<IReadOnlyList<HeardWord>>? Finished;
    /// <summary>Silence isn't transcribed (it costs the same work as speech): how much was skipped (s).</summary>
    public double SkippedSeconds => Interlocked.Read(ref _main.Skipped) / 16000.0;
    private int _quiet;   // 100 ms pieces of silence in a row

    /// <summary>Audio in (16 kHz mono); never blocks the audio thread (drops if a recognizer is far behind).</summary>
    public void Feed(short[] pcm)
    {
        if (_stop) return;
        // silence (nothing playing): after the recognizer has had ~1.2 s of it (enough to finish the sentence),
        // the rest isn't given to it; the timeline still moves on
        long sum = 0;
        for (int i = 0; i < pcm.Length; i += 4) { int v = pcm[i]; sum += v * v; }
        bool quiet = sum / Math.Max(1, (pcm.Length + 3) / 4) < 40 * 40;   // under about -58 dBFS
        _quiet = quiet ? _quiet + 1 : 0;
        if (_quiet > 12)
        {
            _main.Queue.TryAdd((null, pcm.Length));
            _keys?.Queue.TryAdd((null, pcm.Length));
            return;
        }
        _main.Queue.TryAdd((pcm, 0));
        _keys?.Queue.TryAdd((pcm, 0));
    }

    private void WarmUp()
    {
        // a throwaway stream first: a recognizer's first second of decoding is slow, and a slow start is exactly
        // what the delay can't absorb
        try
        {
            using var warm = new VoskApi.Recognizer(_model, alternatives: 0);
            var quiet = new short[1600];
            for (int i = 0; i < 20; i++) { quiet[i % quiet.Length] = (short)((i * 37) % 200 - 100); warm.Accept(quiet, quiet.Length); warm.Partial(); }
            warm.Final();
        }
        catch { }
    }

    private void RunMain(VoskApi.Recognizer rec)
    {
        WarmUp();
        int maxBacklog = 0;
        string lastTrace = "";
        var times = new GuessTimes();
        try
        {
            foreach (var (pcm, skip) in _main.Queue.GetConsumingEnumerable())
            {
                if (_stop) break;
                if (pcm is null) { _main.Skip(skip); _partial = ""; continue; }
                var json = rec.Accept(pcm, pcm.Length);
                Interlocked.Add(ref _main.Fed, pcm.Length);
                double now = _main.Now(_t0);
                if (json is not null)
                {
                    var words = Words(json, "result", _main);
                    _partial = "";
                    try { Finished?.Invoke(words); } catch { }
                    Trace?.Invoke($"{now:0.00}s result: {string.Join(" ", words.Select(w => $"{Show(w.Text)}@{w.Start:0.00}"))}");
                    _judge.Heard(words, now, final: true);
                    times.Finished(words.Count > 0 ? words[^1].End : now);
                    continue;
                }
                // the running guess costs time: only when caught up (a slow PC must not fall behind the delay)
                if (_main.Queue.Count > 0)
                {
                    if (Trace is { } tb && _main.Queue.Count > maxBacklog) { maxBacklog = _main.Queue.Count; tb($"{now:0.00}s behind by {_main.Queue.Count * 0.1:0.0} s"); }
                    continue;
                }
                // the guess as text (asking for its word times makes it ~1.5 s later): times from when each word showed up
                string text = Text(rec.Partial());
                _partial = text;
                var guess = times.Update(text, now);
                if (Trace is { } tr && text != lastTrace) { lastTrace = text; tr($"{now:0.00}s partial: {string.Join(" ", guess.Select(w => $"{Show(w.Text)}~{w.Start:0.00}"))}"); }
                _judge.Heard(guess, now, final: false);
            }
        }
        catch { }
        finally { rec.Dispose(); }
    }

    private void RunKeys(VoskApi.Recognizer rec)
    {
        WarmUp();
        var times = new GuessTimes();
        int reported = 0;     // words of the current guess already reported
        try
        {
            foreach (var (pcm, skip) in _keys!.Queue.GetConsumingEnumerable())
            {
                if (_stop) break;
                if (pcm is null) { _keys.Skip(skip); continue; }
                var json = rec.Accept(pcm, pcm.Length);
                Interlocked.Add(ref _keys.Fed, pcm.Length);
                double now = _keys.Now(_t0);
                if (json is not null)
                {
                    var words = Words(json, "result", _keys).Where(w => w.Text != "[unk]" && w.Text.Length > 0).ToList();
                    foreach (var w in words) _judge.Suspect(w.Start, w.End, w.Text, now);
                    if (words.Count > 0) Trace?.Invoke($"{now:0.00}s keyword result: {string.Join(" ", words.Select(w => $"{SlurWords.Rot13(w.Text)}@{w.Start:0.00}"))}");
                    times.Finished(now); reported = 0;
                    continue;
                }
                if (_keys.Queue.Count > 0) continue;
                var guess = times.Update(Text(rec.Partial()), now);
                if (reported > guess.Count) reported = guess.Count;
                for (int i = reported; i < guess.Count; i++)
                {
                    var w = guess[i];
                    if (w.Text == "[unk]" || w.Text.Length == 0) continue;
                    // just showed up: said in about the last second
                    _judge.Suspect(Math.Min(w.Start, now - 0.95), Math.Max(w.End, now - 0.15), w.Text, now);
                    Trace?.Invoke($"{now:0.00}s keyword: {SlurWords.Rot13(w.Text)}~{w.Start:0.00}");
                }
                reported = guess.Count;
            }
        }
        catch { }
        finally { rec.Dispose(); }
    }

    /// <summary>Filtered words appear in logs encoded (#…, ROT13), never spelled out.</summary>
    private string Show(string w) => _judge.IsSlur(w) ? "#" + SlurWords.Rot13(w) : w;

    private static string Text(string json)
    {
        try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty("partial", out var p) ? p.GetString() ?? "" : ""; }
        catch { return ""; }
    }

    private List<HeardWord> Words(string json, string prop, Ear ear)
    {
        var o = new List<HeardWord>();
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var w in arr.EnumerateArray())
                    o.Add(new HeardWord(w.GetProperty("word").GetString() ?? "",
                        ear.Timeline(_t0, w.TryGetProperty("start", out var s) ? s.GetDouble() : 0),
                        ear.Timeline(_t0, w.TryGetProperty("end", out var e) ? e.GetDouble() : 0)));
        }
        catch { }
        return o;
    }

    public void Dispose()
    {
        _stop = true;
        _main.Queue.CompleteAdding();
        _keys?.Queue.CompleteAdding();
        _main.Thread?.Join(2000);
        _keys?.Thread?.Join(2000);
    }
}

/// <summary>
/// One audio feed's safety stage, between the device and the stream (48 kHz stereo, 10 ms at a time, in timeline
/// order): the loud-audio guard and the listener look at the audio as it arrives; it then waits in a delay line
/// for <see cref="AudioSafetyConfig.DelaySeconds"/> and goes out with their decisions applied.
/// </summary>
public sealed class DesktopSafety : IDisposable
{
    private AudioSafetyConfig _cfg;
    private readonly ContentGains _gains = new();
    internal ContentGains Gains => _gains;
    private LoudGuard? _guard;
    private SpeechListener? _listener;
    private readonly SafetyJudge _judge;
    private readonly bool _mic;
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
    private string _listenKey = "";
    /// <summary>The last muted stretch's margin: how long before it would have gone out it was decided (s).</summary>
    public double LastMargin { get; private set; } = double.NaN;
    private HashSet<string> _custom;
    public event Action<string>? Log;
    public int Muted { get; private set; }
    public int CodesMuted { get; private set; }
    public int LoudTriggers => _guard?.Triggers ?? 0;
    public double DelaySeconds => _delaySlots / 100.0;
    public SafetyJudge Judge => _judge;
    /// <summary>Diagnostics (tests): the recognizers' running results.</summary>
    public Action<string>? Trace { get; set; }
    public bool KeywordListener => _listener?.HasKeywordListener ?? false;
    /// <summary>Also run a separate keyword-only recognizer (twice the work; no faster with the running guess).</summary>
    public static bool UseKeywordListener { get; set; }
    /// <summary>The recognizer is running on this feed (it then also provides the feed's transcript).</summary>
    public bool Hearing => _listener is not null;
    /// <summary>What's being said right now on this feed.</summary>
    public string Partial => _listener?.Partial ?? "";
    /// <summary>When the last finished sentence came (UTC).</summary>
    public DateTime LastResultUtc { get; private set; }
    /// <summary>Silence not transcribed so far (s): work saved.</summary>
    public double SkippedSeconds => _listener?.SkippedSeconds ?? 0;
    /// <summary>Every finished sentence, with real times (UTC): this feed's transcript.</summary>
    public event Action<IReadOnlyList<CaptionWord>>? Transcribed;

    private void OnFinished(IReadOnlyList<HeardWord> words)
    {
        LastResultUtc = DateTime.UtcNow;
        if (words.Count == 0 || Transcribed is null) return;
        // timeline seconds → UTC: the audio arrives as it plays, so the newest sample is about now
        double latest = Interlocked.Read(ref _latest) / 48000.0;
        var nowUtc = DateTime.UtcNow;
        DateTime U(double t) => nowUtc - TimeSpan.FromSeconds(Math.Max(0, latest - t));
        try { Transcribed(words.Select(w => new CaptionWord(w.Text, U(w.Start), U(w.End))).ToList()); } catch { }
    }

    private bool Listening => (_cfg.MuteSlurs || _cfg.MuteCodes) && _delaySlots > 0;

    /// <summary>Live changes (word list, filters and guard on/off); the delay itself stays as built.</summary>
    public void Update(AudioSafetyConfig cfg)
    {
        _custom = SlurWords.Custom(cfg.CustomWords);
        bool guard = cfg.LoudGuard && !_mic;
        if (guard && _guard is null) _guard = new LoudGuard();
        if (!guard) _guard = null;
        _cfg = cfg with { DelaySeconds = _delaySlots / 100.0, MuteSlurs = cfg.MuteSlurs && _delaySlots > 0, MuteCodes = cfg.MuteCodes && _delaySlots > 0 };
        _judge.Codes = _cfg.MuteCodes;
        // the keyword listener knows a fixed list: a changed list (or filter off) starts the ears afresh
        if (_listener is not null && (!Listening || ListenKey() != _listenKey)) { _listener.Dispose(); _listener = null; }
    }

    public DesktopSafety(AudioSafetyConfig cfg, Func<VoskApi.Model?>? model, bool mic = false)
    {
        _cfg = cfg; _model = model; _mic = mic;
        _custom = SlurWords.Custom(cfg.CustomWords);
        if (cfg.LoudGuard && !mic) _guard = new LoudGuard();
        _delaySlots = (int)Math.Round(Math.Max(0, cfg.DelaySeconds) * 100);
        if (_delaySlots > 0) _line = new float[_delaySlots * ContentGains.SlotSamples * 2];
        _judge = new SafetyJudge(_gains, w => _cfg.MuteSlurs && SlurWords.Near(w, _cfg.MuteAbleist, _custom), cfg.MuteCodes && _delaySlots > 0);
        _judge.Muted += (from, to, what) =>
        {
            if (what == "code") CodesMuted++; else Muted++;
            // it goes out DelaySeconds after it arrived: how much of that was left when it was decided
            LastMargin = from + _delaySlots / 100.0 - Interlocked.Read(ref _latest) / 48000.0;
            string feed = _mic ? "your microphone" : "desktop audio";
            Log?.Invoke(what == "code"
                ? $"Stream safety: hid a spoken code at {TimeSpan.FromSeconds(Math.Max(0, from)):hh\\:mm\\:ss} ({feed})."
                : $"Stream safety: muted a filtered word at {TimeSpan.FromSeconds(Math.Max(0, from)):hh\\:mm\\:ss} ({feed}).");
        };
        // load the recognizer now (off the audio thread), so it's ready and warm before the first word
        if (Listening && model is not null) _modelTask = Task.Run(() => { try { return model(); } catch { return null; } });
    }

    private string ListenKey() => $"{_cfg.MuteSlurs}|{_cfg.MuteAbleist}|{string.Join(",", _custom.OrderBy(x => x))}";

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
        if (Listening)
        {
            // the recognizer's model loads off the audio thread (the first time takes a moment)
            if (_listener is null && _model is not null) _modelTask ??= Task.Run(() => { try { return _model(); } catch { return null; } });
            if (_listener is null && _modelTask is { IsCompletedSuccessfully: true, Result: { } m })
            {
                _listenKey = ListenKey();
                // the recognizer's quick running guess (~0.25 s behind the voice) is as early as a separate keyword
                // listener would be, at half the work: one recognizer per audio source
                var keywords = _cfg.MuteSlurs && UseKeywordListener ? SlurWords.All(_cfg.MuteAbleist, _custom).ToList() : null;
                _listener = new SpeechListener(m, index / 48000.0, _judge, keywords);
                _listener.Finished += OnFinished;
                _pending.Clear();
            }
            if (_listener is not null && Trace is not null) _listener.Trace = Trace;
            if (_listener is not null)
            {
                // the recognizers get 100 ms at a time (10 ms pieces would cost them far more work)
                _pending.AddRange(_down.Process(stereo));
                if (_pending.Count >= 1600) { _listener.Feed(_pending.ToArray()); _pending.Clear(); }
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

    public void Dispose() { _listener?.Dispose(); _listener = null; }

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
