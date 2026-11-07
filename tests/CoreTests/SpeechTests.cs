#if WINDOWS_SPEECH
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using LeapcastStudioAI;

/// <summary>
/// Windows only: Windows' own voices say "clip" (and ordinary sentences) into the voice-clip listener, through the
/// same 16 kHz audio stream the stream's microphone feeds.
/// </summary>
static class SpeechTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        List<string> voices;
        using (var probe = new SpeechSynthesizer())
            voices = probe.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en")
                          .Select(v => v.VoiceInfo.Name).Take(2).ToList();
        if (voices.Count == 0)
        {
            Console.WriteLine("    note: no English text-to-speech voice on this machine; only the voice-free listener tests run");
            await VoskTests.RunAsync(check, null);
            return;
        }
        Console.WriteLine("    voices: " + string.Join(", ", voices));
        foreach (var name in voices)
        {
            short[] Say(string text, int rate)
            {
                using var synth = new SpeechSynthesizer();
                synth.SelectVoice(name);
                var ms = new MemoryStream();
                synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(rate == 0 ? SpeechPcmConverter.OutRate : rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                synth.Speak(text);
                var b = ms.ToArray();
                var s = new short[b.Length / 2];
                Buffer.BlockCopy(b, 0, s, 0, s.Length * 2);
                return s;
            }
            await VoskTests.RunAsync(check, Say, name);
        }
    }
}
#endif
