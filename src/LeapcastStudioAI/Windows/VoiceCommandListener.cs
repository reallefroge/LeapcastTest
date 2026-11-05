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
    private VoiceClips.SensitivityLevel _level = VoiceClips.Sensitivity("High");

    /// <summary>The word was heard: (what was recognized, confidence, when the word started being said, UTC).</summary>
    public event Action<string, float, DateTime>? Heard;
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
    public void Start(string word, BlockingAudioStream? feed, string? sensitivity = "High")
    {
        Stop();
        _level = VoiceClips.Sensitivity(sensitivity);
        _word = string.IsNullOrWhiteSpace(word) ? "clip" : word.Trim().ToLowerInvariant();
        if (!Available(out var culture) || culture is null)
            throw new InvalidOperationException("Windows has no speech recognizer installed. Add one in Windows Settings → Time & language → Speech (English).");
        var engine = new SpeechRecognitionEngine(new CultureInfo(culture));
        try
        {
            var forms = new[] { _word, _word + "s", _word + " it", _word + " that", _word + " this", _word + " it now", _word + " that now" };
            var command = new Grammar(new GrammarBuilder(new Choices(forms))) { Name = "command", Priority = 10, Weight = 1f };
            engine.LoadGrammar(command);
            // the word with other speech around it ("okay let's clip that"): any words, the word, any words
            var around = new GrammarBuilder();
            around.AppendWildcard();
            around.Append(new Choices(_word, _word + "s", _word + " it", _word + " that"));
            var before = new GrammarBuilder();
            before.AppendWildcard();
            before.Append(new Choices(_word, _word + "s"));
            engine.LoadGrammar(new Grammar(new GrammarBuilder(new Choices(around, before))) { Name = "sentence", Priority = 8, Weight = 0.9f });
            // free dictation, weighted down: it recognizes other speech as what it is (so it doesn't trigger), but
            // shouldn't out-vote the word ("clip" heard as "but", "by", "let")
            if (_level.Filler)
            {
                // one to eight everyday words in a row: other speech lands here instead of on the word
                var filler = VoiceClips.FillerWords.Where(w => !VoiceClips.NearMatch(w, _word, 1)).ToArray();
                engine.LoadGrammar(new Grammar(new GrammarBuilder(new Choices(filler), 1, 8)) { Name = "filler", Priority = 0, Weight = 0.5f });
            }
            if (_level.DictationWeight > 0)
                try { engine.LoadGrammar(new DictationGrammar { Name = "dictation", Priority = 0, Weight = _level.DictationWeight }); }
                catch { /* some recognizers have no dictation: the word alone still works */ }
            if (feed is not null) engine.SetInputToAudioStream(feed, new SpeechAudioFormatInfo(SpeechPcmConverter.OutRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            else engine.SetInputToDefaultAudioDevice();
            // the recognizer drops results below its own confidence bar before we see them: lower it
            if (_level.Rejection >= 0)
                try { engine.UpdateRecognizerSetting("CFGConfidenceRejectionThreshold", _level.Rejection); }
                catch (Exception ex) { Debug?.Invoke("rejection threshold not set: " + ex.Message); }
            engine.SpeechRecognized += OnRecognized;
            engine.SpeechDetected += (_, e) => Debug?.Invoke($"speech detected at {e.AudioPosition}");
            engine.SpeechRecognitionRejected += OnRejected;
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
        if (r is null) return;
        Debug?.Invoke($"recognized: {r.Text} ({r.Confidence:0.00}, {r.Grammar?.Name}) words: " +
                      string.Join(" ", r.Words.Select(w => $"{w.Text}={w.Confidence:0.00}")));
        if (Accept(r, r)) return;
        // the best guess isn't the word, but a close second may be ("clip" vs "click" in a noisy room)
        foreach (var alt in r.Alternates.Skip(1).Take(4)) if (Accept(alt, r)) return;
    }

    private void OnRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        var alts = e.Result?.Alternates.Take(5).ToList() ?? new List<RecognizedPhrase>();
        Debug?.Invoke("rejected: " + string.Join(" | ", alts.Select(a => $"{a.Text} ({a.Confidence:0.00}, {a.Grammar?.Name})")));
        if (!_level.UseRejected || e.Result is null) return;
        foreach (var alt in alts) if (Accept(alt, e.Result)) return;
    }

    /// <summary>
    /// When the word started being said: the word's own audio position when the recognizer gives one (else the
    /// phrase's start), turned into a time through the audio stream's arrival times. Without a stream (Windows'
    /// default mic), counted back from now by the phrase's length, plus the recognizer's end-of-speech wait.
    /// </summary>
    private DateTime SpokenAt(RecognizedPhrase p, RecognitionResult result)
    {
        var now = DateTime.UtcNow;
        DateTime estimate = now - TimeSpan.FromSeconds(1.2); // the word took at least this long to say and recognize
        try
        {
            if (result.Audio is not { } phrase) return estimate;
            // A word's audio (GetAudioForWordRange) is positioned relative to the phrase on some recognizers and to
            // the whole stream on others: take whichever reading lands inside the phrase.
            var offset = TimeSpan.Zero;
            if (ReferenceEquals(p, result))
            {
                var unit = result.Words.FirstOrDefault(w => VoiceClips.Matches(w.Text, _word))
                           ?? (_level.Near ? result.Words.FirstOrDefault(w => VoiceClips.NearMatch(w.Text, _word, _level.NearLetters)) : null);
                if (unit is not null && result.GetAudioForWordRange(unit, unit) is { } word)
                {
                    var wp = word.AudioPosition;
                    offset = wp >= phrase.AudioPosition && wp - phrase.AudioPosition <= phrase.Duration ? wp - phrase.AudioPosition : wp;
                    if (offset < TimeSpan.Zero || offset > phrase.Duration) offset = TimeSpan.Zero;
                }
            }
            // counted back from now: the phrase ended a moment ago (the recognizer waits for a short silence)
            var fromNow = now - phrase.Duration + offset - TimeSpan.FromSeconds(0.4);
            if (_stream?.WallTimeAt(phrase.AudioPosition + offset) is { } wall && Math.Abs((wall - fromNow).TotalSeconds) < 3) return wall;
            return fromNow;
        }
        catch (Exception ex) { Debug?.Invoke("word timing: " + ex.Message); }
        return estimate;
    }

    private bool Accept(RecognizedPhrase p, RecognitionResult result)
    {
        if (!(_level.Near ? VoiceClips.NearMatch(p.Text, _word, _level.NearLetters) : VoiceClips.Matches(p.Text, _word))) return false;
        // the word on its own, or in a sentence (the sentence grammar is held to the stricter bar: it hunts for the word)
        bool alone = p.Grammar?.Name is "command" or "sentence";
        if (p.Confidence < (alone ? _level.Alone : _level.InSentence)) return false;
        var now = DateTime.UtcNow;
        if (now - _last < Cooldown) return true; // heard, but a clip was just saved
        _last = now;
        Heard?.Invoke(p.Text, p.Confidence, SpokenAt(p, result));
        return true;
    }

    public void Stop()
    {
        var engine = _engine;
        _engine = null;
        _stream?.Complete();
        _stream = null;
        if (engine is null) return;
        try { engine.SpeechRecognized -= OnRecognized; engine.SpeechRecognitionRejected -= OnRejected; engine.RecognizeAsyncCancel(); } catch { }
        try { engine.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
