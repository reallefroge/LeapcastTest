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
        byte[] Say(string text, int rate = 16000)
        {
            using var synth = new SpeechSynthesizer();
            var ms = new MemoryStream();
            synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(text);
            return ms.ToArray();
        }
        var spoken = new List<DateTime>();
        DateTime voiceStart = default;
        // mic48k: like a real microphone, 48 kHz into the same converter the app uses (downsampled to 16 kHz)
        async Task<List<string>> Listen(string[] phrases, long length = long.MaxValue, bool trace = false, bool mic48k = false, double leadIn = 6,
            string sensitivity = "High", double noise = 0)
        {
            var heard = new List<string>();
            lock (spoken) spoken.Clear();
            voiceStart = default;
            var calls = new List<string>();
            var feed = new BlockingAudioStream { ReportedLength = length, Trace = trace ? c => { lock (calls) calls.Add(c); } : null };
            using var listener = new VoiceCommandListener();
            listener.Heard += (t, c, at) => { lock (heard) heard.Add($"{t} ({c:0.00})"); lock (spoken) spoken.Add(at); };
            listener.Debug += d => Console.WriteLine("      speech: " + d);
            listener.Start("clip", feed, sensitivity);
            Console.WriteLine($"      sensitivity {sensitivity}{(noise > 0 ? $", mic noise {20 * Math.Log10(noise):0} dBFS" : "")}");
            var silence = new short[16000 / 2];
            // silence first: a word's position has to be right well into the stream, not only at its start
            for (double t = 0; t < leadIn; t += 0.5) { feed.Write(silence); await Task.Delay(500); }
            var conv = new SpeechPcmConverter();
            foreach (var p in phrases)
            {
                var pcm = Say(p, mic48k ? 48000 : 16000);
                Console.WriteLine($"      said \"{p}\"{(mic48k ? " at 48 kHz" : "")}: {pcm.Length / (mic48k ? 96000.0 : 32000.0):0.0}s of audio, peak {PeakOf(pcm):0.00}");
                var samples = new short[pcm.Length / 2];
                Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);
                if (mic48k)
                {
                    var rnd = new Random(7);
                    var f = samples.Select(v => v / 32768f + (float)(noise * (rnd.NextDouble() * 2 - 1))).ToArray();
                    var outs = new List<short>();
                    for (int i = 0; i < f.Length; i += 480) outs.AddRange(conv.Convert(f.AsSpan(i, Math.Min(480, f.Length - i)), 48000));
                    samples = outs.ToArray();
                }
                // real-time pace, 100 ms at a time, like the microphone
                int firstVoice = Array.FindIndex(samples, v => Math.Abs((int)v) > 600);
                for (int i = 0; i < samples.Length; i += 1600)
                {
                    var chunk = samples.Skip(i).Take(1600).ToArray();
                    var now = DateTime.UtcNow;
                    // the first audible sample: when the voice really starts (the chunk's audio ends "now")
                    if (voiceStart == default && firstVoice >= i && firstVoice < i + chunk.Length)
                        voiceStart = now - TimeSpan.FromSeconds((i + chunk.Length - firstVoice) / 16000.0);
                    feed.Write(chunk, now);
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
        double alone = spoken.Count == 1 ? (spoken[0] - voiceStart).TotalSeconds : double.NaN;
        check(Math.Abs(alone) < 0.35, $"…and Leapcast knows when it started, so the clip ends before it ({alone * 1000:+0;-0} ms from the voice starting)");
        var inSentence = await Listen(new[] { "Okay, let's clip that." });
        check(inSentence.Count == 1, $"…and inside a sentence ({string.Join(", ", inSentence)})");
        double into = spoken.Count == 1 ? (spoken[0] - voiceStart).TotalSeconds : double.NaN;
        check(into is > 0.3 and < 2.0, $"…where the clip ends at the word, not at the start of the sentence ({into:0.00}s into the sentence)");
        var viaMic = await Listen(new[] { "Clip" }, mic48k: true);
        double micAt = spoken.Count == 1 ? (spoken[0] - voiceStart).TotalSeconds : double.NaN;
        check(viaMic.Count == 1 && Math.Abs(micAt) < 0.35,
            $"…also through the microphone path (48 kHz, downsampled like a real mic): {string.Join(", ", viaMic)}, {micAt * 1000:+0;-0} ms");
        var noisy = await Listen(new[] { "Clip" }, mic48k: true, noise: 0.03);
        check(noisy.Count == 1, $"…with a noisy microphone too, on High ({string.Join(", ", noisy)})");
        var normal = await Listen(new[] { "Clip" }, sensitivity: "Normal");
        check(normal.Count == 1, $"Normal hears a clear \"Clip\" ({string.Join(", ", normal)})");
        var max = await Listen(new[] { "Clip" }, mic48k: true, noise: 0.03, sensitivity: "Max");
        check(max.Count == 1, $"Maximum hears it ({string.Join(", ", max)})");
        var normalChat = await Listen(new[] { "The weather is really nice today, isn't it?" }, sensitivity: "Normal");
        check(normalChat.Count == 0, $"Normal ignores an ordinary sentence ({(normalChat.Count == 0 ? "nothing" : string.Join(", ", normalChat))})");
        var chat = await Listen(new[] { "The weather is really nice today, isn't it?" });
        check(chat.Count == 0, $"High ignores an ordinary sentence too ({(chat.Count == 0 ? "nothing" : string.Join(", ", chat))})");
        var near = await Listen(new[] { "Let's take a quick trip to the shop." });
        check(near.Count == 0, $"…and one with a similar-sounding word ({(near.Count == 0 ? "nothing" : string.Join(", ", near))})");
        var maxChat = await Listen(new[] { "The weather is really nice today, isn't it?" }, sensitivity: "Max");
        Console.WriteLine($"    note: Maximum on an ordinary sentence: {(maxChat.Count == 0 ? "nothing" : string.Join(", ", maxChat))} (Maximum may trigger by accident)");
    }
}
#endif
