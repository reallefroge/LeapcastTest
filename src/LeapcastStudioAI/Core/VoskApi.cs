using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LeapcastStudioAI;

/// <summary>
/// Vosk (Kaldi) offline speech recognition: libvosk plus a model folder shipped with the app. Nothing leaves the PC.
/// Unlike a word-list recognizer, it transcribes everything said into ordinary words, so speech that isn't the
/// trigger word comes out as what it is ("flip the card", "nice weather") instead of being forced onto the word.
/// </summary>
public static class VoskApi
{
    public const string ModelName = "vosk-model-small-en-us-0.15";

    private const string Lib = "libvosk";

    static VoskApi()
    {
        // tests (and dev builds) can point at a libvosk elsewhere
        NativeLibrary.SetDllImportResolver(typeof(VoskApi).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            var custom = Environment.GetEnvironmentVariable("LEAPCAST_VOSK_LIB");
            if (!string.IsNullOrEmpty(custom) && NativeLibrary.TryLoad(custom, out var h)) return h;
            foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "speech") })
                foreach (var file in new[] { "libvosk.dll", "libvosk.so", "libvosk.dylib" })
                    if (File.Exists(Path.Combine(dir, file)) && NativeLibrary.TryLoad(Path.Combine(dir, file), out h)) return h;
            return IntPtr.Zero;
        });
    }

    /// <summary>Where the model is: LEAPCAST_VOSK_MODEL, else "speech\&lt;model&gt;" next to the app.</summary>
    public static string? FindModel()
    {
        var env = Environment.GetEnvironmentVariable("LEAPCAST_VOSK_MODEL");
        foreach (var dir in new[] { env, Path.Combine(AppContext.BaseDirectory, "speech", ModelName) })
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "am", "final.mdl"))) return dir;
        return null;
    }

    [DllImport(Lib)] internal static extern void vosk_set_log_level(int level);
    [DllImport(Lib)] internal static extern IntPtr vosk_model_new([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Lib)] internal static extern void vosk_model_free(IntPtr model);
    [DllImport(Lib)] internal static extern IntPtr vosk_recognizer_new(IntPtr model, float sampleRate);
    [DllImport(Lib)] internal static extern void vosk_recognizer_set_max_alternatives(IntPtr rec, int max);
    [DllImport(Lib)] internal static extern IntPtr vosk_recognizer_new_grm(IntPtr model, float sampleRate, [MarshalAs(UnmanagedType.LPUTF8Str)] string grammar);
    [DllImport(Lib)] internal static extern void vosk_recognizer_set_words(IntPtr rec, int words);
    [DllImport(Lib)] internal static extern int vosk_recognizer_accept_waveform_s(IntPtr rec, short[] data, int length);
    [DllImport(Lib)] internal static extern IntPtr vosk_recognizer_result(IntPtr rec);
    [DllImport(Lib)] internal static extern IntPtr vosk_recognizer_partial_result(IntPtr rec);
    [DllImport(Lib)] internal static extern IntPtr vosk_recognizer_final_result(IntPtr rec);
    [DllImport(Lib)] internal static extern void vosk_recognizer_free(IntPtr rec);
    [DllImport(Lib)] internal static extern void vosk_recognizer_set_partial_words(IntPtr rec, int words);

    /// <summary>A loaded model (about 70 MB of memory for the small English one). Load once, share between recognizers.</summary>
    public sealed class Model : IDisposable
    {
        internal IntPtr Handle;
        public Model(string path)
        {
            try { vosk_set_log_level(-1); }
            catch (DllNotFoundException ex) { throw new InvalidOperationException("The speech recognizer (libvosk) is missing from the app folder: reinstall Leapcast Studio AI.", ex); }
            Handle = vosk_model_new(path);
            if (Handle == IntPtr.Zero) throw new InvalidOperationException($"The speech model couldn't be loaded from {path}.");
        }
        public void Dispose() { if (Handle != IntPtr.Zero) { vosk_model_free(Handle); Handle = IntPtr.Zero; } }
    }

    /// <summary>One audio stream being transcribed (16 kHz mono 16-bit). Not thread-safe: feed it from one thread.</summary>
    public sealed class Recognizer : IDisposable
    {
        private IntPtr _h;
        /// <summary>
        /// A keyword listener: only these words (anything else comes out as "[unk]"). Fast, and it hears a listed word
        /// even said quickly or half-swallowed; words not in the model's vocabulary are left out of the list.
        /// </summary>
        public static Recognizer Keywords(Model model, IEnumerable<string> words, float rate = SpeechPcmConverter.OutRate)
        {
            string grammar = JsonSerializer.Serialize(words.Distinct().Append("[unk]").ToArray());
            var h = vosk_recognizer_new_grm(model.Handle, rate, grammar);
            if (h == IntPtr.Zero) throw new InvalidOperationException("The keyword listener couldn't start.");
            return new Recognizer(h);
        }

        private Recognizer(IntPtr handle) { _h = handle; vosk_recognizer_set_words(_h, 1); }

        public Recognizer(Model model, int alternatives = 3, float rate = SpeechPcmConverter.OutRate)
        {
            _h = vosk_recognizer_new(model.Handle, rate);
            if (_h == IntPtr.Zero) throw new InvalidOperationException("The speech recognizer couldn't start.");
            vosk_recognizer_set_words(_h, 1);
            if (alternatives > 0) vosk_recognizer_set_max_alternatives(_h, alternatives);
        }

        /// <summary>Feeds audio; returns a finished utterance's result JSON when one just ended (a pause), else null.</summary>
        public string? Accept(short[] samples, int count)
        {
            if (count <= 0) return null;
            return vosk_recognizer_accept_waveform_s(_h, samples, count) != 0 ? Str(vosk_recognizer_result(_h)) : null;
        }

        /// <summary>Word times in the running guess too (newer libvosk); false when this build can't.</summary>
        public bool TryPartialWords()
        {
            try { vosk_recognizer_set_partial_words(_h, 1); return true; }
            catch (EntryPointNotFoundException) { return false; }
        }

        /// <summary>What is being said right now (unfinished utterance).</summary>
        public string Partial() => Str(vosk_recognizer_partial_result(_h));

        /// <summary>Ends the current utterance and returns its result.</summary>
        public string Final() => Str(vosk_recognizer_final_result(_h));

        private static string Str(IntPtr p) => p == IntPtr.Zero ? "{}" : Marshal.PtrToStringUTF8(p) ?? "{}";

        public void Dispose() { if (_h != IntPtr.Zero) { vosk_recognizer_free(_h); _h = IntPtr.Zero; } }
    }

    /// <summary>One recognized word: when it was said (seconds since the recognizer started).</summary>
    public readonly record struct Word(string Text, double Start, double End);

    /// <summary>One reading of an utterance: its words and the recognizer's score for it (higher = likelier).</summary>
    public sealed record Hypothesis(double Score, IReadOnlyList<Word> Words)
    {
        public string Text => string.Join(' ', Words.Select(w => w.Text));
    }

    /// <summary>The readings in a result (best first). Handles both the plain and the n-best JSON shapes.</summary>
    public static List<Hypothesis> Parse(string? json)
    {
        var list = new List<Hypothesis>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("alternatives", out var alts) && alts.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in alts.EnumerateArray())
                    list.Add(new Hypothesis(a.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0, Words(a)));
            }
            else list.Add(new Hypothesis(double.NaN, Words(root)));
        }
        catch (JsonException) { }
        return list;

        static List<Word> Words(JsonElement e)
        {
            var words = new List<Word>();
            if (e.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Array)
                foreach (var w in r.EnumerateArray())
                    words.Add(new Word(w.GetProperty("word").GetString() ?? "",
                        w.TryGetProperty("start", out var s) ? s.GetDouble() : 0, w.TryGetProperty("end", out var en) ? en.GetDouble() : 0));
            return words;
        }
    }
}
