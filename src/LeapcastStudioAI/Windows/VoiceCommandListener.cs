using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;

namespace LeapcastStudioAI;

/// <summary>
/// Listens for one spoken word (voice clips: "clip") with Windows' built-in offline speech recognizer: nothing
/// leaves the PC. Two grammars run together:
/// <list type="bullet">
/// <item>the word on its own ("clip", "clip it", "clip that"), the most reliable way to trigger;</item>
/// <item>free dictation, so the word also counts inside a sentence ("ok let's clip that"), and other speech is
/// recognized as what it is instead of being forced onto the word.</item>
/// </list>
/// Fires at most once per <see cref="Cooldown"/> (a clip already covers the moments before it).
/// </summary>
public sealed class VoiceCommandListener : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private BlockingAudioStream? _stream;
    private DateTime _last = DateTime.MinValue;
    private string _word = "clip";

    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(6);
    /// <summary>Lowest confidence accepted for the word alone / within dictation.</summary>
    public const float CommandConfidence = 0.55f, DictationConfidence = 0.35f;

    /// <summary>The word was heard: (what was recognized, confidence).</summary>
    public event Action<string, float>? Heard;
    /// <summary>Everything the recognizer reports (diagnostics: speech detected, recognized, rejected).</summary>
    public event Action<string>? Debug;

    public bool Running => _engine is not null;

    /// <summary>Is a speech recognizer installed (Windows Settings → Time &amp; language → Speech)?</summary>
    public static bool Available(out string? culture)
    {
        culture = null;
        try
        {
            var all = SpeechRecognitionEngine.InstalledRecognizers();
            var pick = all.FirstOrDefault(r => r.Culture.Equals(CultureInfo.CurrentUICulture))
                       ?? all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en") ?? all.FirstOrDefault();
            culture = pick?.Culture.Name;
            return pick is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Starts listening. With <paramref name="feed"/> the recognizer hears that audio (16 kHz mono 16-bit, e.g. the
    /// stream's own microphone); without it, Windows' default microphone.
    /// </summary>
    public void Start(string word, BlockingAudioStream? feed)
    {
        Stop();
        _word = string.IsNullOrWhiteSpace(word) ? "clip" : word.Trim().ToLowerInvariant();
        if (!Available(out var culture) || culture is null)
            throw new InvalidOperationException("Windows has no speech recognizer installed. Add one in Windows Settings → Time & language → Speech (English).");
        var engine = new SpeechRecognitionEngine(new CultureInfo(culture));
        try
        {
            var command = new Grammar(new GrammarBuilder(new Choices(_word, _word + " it", _word + " that", _word + " this"))) { Name = "command", Priority = 10 };
            engine.LoadGrammar(command);
            try { engine.LoadGrammar(new DictationGrammar { Name = "dictation", Priority = 0 }); }
            catch { /* some recognizers have no dictation: the word alone still works */ }
            if (feed is not null) engine.SetInputToAudioStream(feed, new SpeechAudioFormatInfo(SpeechPcmConverter.OutRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            else engine.SetInputToDefaultAudioDevice();
            engine.SpeechRecognized += OnRecognized;
            engine.SpeechDetected += (_, e) => Debug?.Invoke($"speech detected at {e.AudioPosition}");
            engine.SpeechRecognitionRejected += (_, e) =>
                Debug?.Invoke("rejected: " + string.Join(" | ", e.Result?.Alternates.Take(3).Select(a => $"{a.Text} ({a.Confidence:0.00}, {a.Grammar?.Name})") ?? Array.Empty<string>()));
            engine.RecognizeCompleted += (_, e) => Debug?.Invoke($"recognition ended{(e.Error is null ? "" : ": " + e.Error.Message)}");
            engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
        _engine = engine;
        _stream = feed;
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var r = e.Result;
        if (r is not null) Debug?.Invoke($"recognized: {r.Text} ({r.Confidence:0.00}, {r.Grammar?.Name})");
        if (r is null || !VoiceClips.Matches(r.Text, _word)) return;
        bool alone = r.Grammar?.Name == "command";
        if (r.Confidence < (alone ? CommandConfidence : DictationConfidence)) return;
        var now = DateTime.UtcNow;
        if (now - _last < Cooldown) return;
        _last = now;
        Heard?.Invoke(r.Text, r.Confidence);
    }

    public void Stop()
    {
        var engine = _engine;
        _engine = null;
        _stream?.Complete();
        _stream = null;
        if (engine is null) return;
        try { engine.SpeechRecognized -= OnRecognized; engine.RecognizeAsyncCancel(); } catch { }
        try { engine.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
