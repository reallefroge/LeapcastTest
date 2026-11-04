#if WINDOWS_SPEECH
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using LeapcastStudioAI;

/// <summary>
/// Windows only: Windows' own voice says "clip" (and an ordinary sentence) into the voice-clip listener through the
/// same 16 kHz audio stream the stream's microphone feeds, at real-time pace.
/// </summary>
static class SpeechTests
{
    static double PeakOf(byte[] pcm)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
        return peak / 32768.0;
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        if (!VoiceCommandListener.Available(out var culture))
        {
            Console.WriteLine("    note: no speech recognizer is installed on this machine (Windows Server images usually have none); skipped");
            return;
        }
        Console.WriteLine($"    speech recognizer: {culture}");
        byte[] Say(string text)
        {
            using var synth = new SpeechSynthesizer();
            var ms = new MemoryStream();
            synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(text);
            return ms.ToArray();
        }
        async Task<List<string>> Listen(string[] phrases, long length = long.MaxValue, bool trace = false)
        {
            var heard = new List<string>();
            var calls = new List<string>();
            var feed = new BlockingAudioStream { ReportedLength = length, Trace = trace ? c => { lock (calls) calls.Add(c); } : null };
            using var listener = new VoiceCommandListener();
            listener.Heard += (t, c) => { lock (heard) heard.Add($"{t} ({c:0.00})"); };
            listener.Debug += d => Console.WriteLine("      speech: " + d);
            listener.Start("clip", feed);
            var silence = new short[16000 / 2];
            foreach (var p in phrases)
            {
                var pcm = Say(p);
                Console.WriteLine($"      said \"{p}\": {pcm.Length / 32000.0:0.0}s of audio, peak {PeakOf(pcm):0.00}");
                var samples = new short[pcm.Length / 2];
                Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);
                // real-time pace, 100 ms at a time, like the microphone
                for (int i = 0; i < samples.Length; i += 1600)
                {
                    feed.Write(samples.Skip(i).Take(1600).ToArray());
                    await Task.Delay(100);
                }
                for (int k = 0; k < 3; k++) { feed.Write(silence); await Task.Delay(500); }
            }
            await Task.Delay(1500);
            if (trace) lock (calls) Console.WriteLine("      stream calls: " + string.Join("; ", calls.Take(12)) + (calls.Count > 12 ? $" … ({calls.Count} calls)" : ""));
            lock (heard) return heard.ToList();
        }
        var said = await Listen(new[] { "Clip" }, trace: true);
        check(said.Count == 1, $"Windows' voice saying \"Clip\" is heard ({string.Join(", ", said)})");
        var inSentence = await Listen(new[] { "Okay, let's clip that." });
        check(inSentence.Count == 1, $"…and inside a sentence ({string.Join(", ", inSentence)})");
        var chat = await Listen(new[] { "The weather is really nice today, isn't it?" });
        check(chat.Count == 0, $"an ordinary sentence doesn't trigger a clip ({(chat.Count == 0 ? "nothing" : string.Join(", ", chat))})");
    }
}
#endif
