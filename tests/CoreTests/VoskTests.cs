using LeapcastStudioAI;

/// <summary>
/// The voice-clip listener (Vosk) end to end: audio goes through the same 16 kHz stream the microphone feeds, and
/// what it hears is checked phrase by phrase. On Windows the phrases are spoken by Windows' own voices; elsewhere
/// only what needs no voice runs (plus recordings from LEAPCAST_VOICE_SAMPLES, printed for tuning).
/// </summary>
static class VoskTests
{
    const int Rate = SpeechPcmConverter.OutRate;

    public static readonly string[] Positives =
    {
        "Clip", "Clip that", "Clip it", "Clips", "Okay, let's clip that.", "Oh my god, clip that!", "That was insane. Clip it.",
    };

    /// <summary>Ordinary streaming talk, including words that sound like the trigger.</summary>
    public static readonly string[] Negatives =
    {
        "The weather is really nice today, isn't it?", "Let's take a quick trip to the shop.", "Flip the card over.",
        "I need to heal up before the boss.", "Click on the spell deck.", "My lip is a little dry.", "He gave me a really good tip.",
        "That was a close fight.", "Don't slip on the ice.", "Let me check the chat real quick.", "Thanks for the follow!",
        "We're going live tomorrow at noon.", "Hold on, I'm getting a drink.", "Cast the blade on the storm wizard.",
        "I'm heading back to Ravenwood.", "Use the shield, then attack.", "Let's farm some gold tonight.", "Nice critical hit!",
        "I got a lot of loot from that dungeon.", "What's up everybody, welcome to the stream.", "Can you hear me okay?",
        "I think my internet is lagging.", "That boss hits so hard.", "Okay, one more round and then we're done.",
        "Who wants to join my team?", "I'm going to grab some food, be right back.", "This quest is taking forever.",
        "Lip sync looks fine on my end.", "Quick question for chat.", "Let's flip a coin for it.", "He slipped right past me.",
        "I clicked the wrong button.", "Did you see that?", "It's a lot of fun.", "Keep it up!",
    };

    sealed record Piece(string Label, short[] Samples, bool ShouldTrigger);

    /// <summary>Feeds the pieces (with silence between) into a listener; returns the hits per piece and when each voice started.</summary>
    static (Dictionary<int, List<(string Text, double At)>> Hits, double[] VoiceStart, List<string> Log) Listen(IReadOnlyList<Piece> pieces, string sensitivity)
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var feed = new BlockingAudioStream(maxSeconds: 7200);
        var hits = new List<(string Text, DateTime At)>();
        var log = new List<string>();
        using var listener = new VoiceListener();
        listener.Heard += (t, c, at) => { lock (hits) hits.Add((t, at)); };
        listener.Debug += d => { lock (log) log.Add(d); };
        listener.Start("clip", feed, sensitivity);
        long written = 0;
        void Write(short[] s)
        {
            for (int i = 0; i < s.Length; i += 1600)
            {
                var chunk = s.AsSpan(i, Math.Min(1600, s.Length - i)).ToArray();
                written += chunk.Length;
                feed.Write(chunk, t0 + TimeSpan.FromSeconds(written / (double)Rate)); // the chunk's audio ends then
            }
        }
        var ranges = new (double From, double To)[pieces.Count];
        var voice = new double[pieces.Count];
        Write(new short[Rate]);
        for (int k = 0; k < pieces.Count; k++)
        {
            var s = pieces[k].Samples;
            int first = Array.FindIndex(s, v => Math.Abs((int)v) > 600);
            ranges[k].From = written / (double)Rate;
            voice[k] = (written + Math.Max(0, first)) / (double)Rate;
            Write(s);
            Write(new short[Rate * 7]); // past the cooldown, so each piece stands alone
            ranges[k].To = written / (double)Rate;
        }
        feed.Complete();
        listener.WaitUntilDone(TimeSpan.FromMinutes(3));
        var byPiece = new Dictionary<int, List<(string, double)>>();
        lock (hits)
            foreach (var (text, at) in hits)
            {
                double sec = (at - t0).TotalSeconds;
                int k = Array.FindIndex(ranges, r => sec >= r.From - 0.5 && sec < r.To);
                if (!byPiece.TryGetValue(k, out var l)) byPiece[k] = l = new();
                l.Add((text, sec));
            }
        return (byPiece, voice, log);
    }

    public static Task RunAsync(Action<bool, string> check, Func<string, int, short[]>? say, string? voiceName = null)
    {
        bool require = Environment.GetEnvironmentVariable("LEAPCAST_REQUIRE_VOSK") == "1";
        if (!VoiceListener.Available(out var model))
        {
            if (require) check(false, "the speech model is installed for the voice-clip tests (LEAPCAST_VOSK_MODEL)");
            else Console.WriteLine("    note: no speech model (set LEAPCAST_VOSK_MODEL); voice listener tests skipped");
            return Task.CompletedTask;
        }
        try { using var probe = new VoiceListener(); probe.Start("clip", new BlockingAudioStream(), "High"); }
        catch (Exception ex)
        {
            if (require) check(false, "the speech recognizer loads: " + ex.Message);
            else Console.WriteLine("    note: speech recognizer not loadable here (" + ex.Message + "); skipped");
            return Task.CompletedTask;
        }
        Console.WriteLine($"    speech model: {model}");

        // no voice at all: silence and steady noise never trigger, on any level
        var rnd = new Random(5);
        var noise = Enumerable.Range(0, Rate * 6).Select(_ => (short)(rnd.Next(-2500, 2500))).ToArray();
        var hum = Enumerable.Range(0, Rate * 6).Select(i => (short)(6000 * Math.Sin(2 * Math.PI * 120 * i / Rate))).ToArray();
        var quiet = Listen(new[] { new Piece("noise", noise, false), new Piece("hum", hum, false) }, "Max");
        check(quiet.Hits.Count == 0, "noise and hum never count as the word, even on Maximum");

        var samplesDir = Environment.GetEnvironmentVariable("LEAPCAST_VOICE_SAMPLES");
        if (!string.IsNullOrEmpty(samplesDir) && Directory.Exists(samplesDir))
            foreach (var level in new[] { "Normal", "High", "Max" })
            {
                var files = Directory.GetFiles(samplesDir, "*.raw").OrderBy(f => f).ToList();
                var pieces = files.Select(f => { var b = File.ReadAllBytes(f); var s = new short[b.Length / 2]; Buffer.BlockCopy(b, 0, s, 0, s.Length * 2); return new Piece(Path.GetFileName(f), s, true); }).ToList();
                var r = Listen(pieces, level);
                Console.WriteLine($"    recordings on {level}: " + string.Join("; ", pieces.Select((p, k) =>
                    $"{p.Label[..Math.Min(8, p.Label.Length)]}: {(r.Hits.TryGetValue(k, out var h) ? string.Join(", ", h.Select(x => $"{x.Text}@{x.At - r.VoiceStart[k]:0.0}")) : "nothing")}")));
                if (level == "Max") foreach (var line in r.Log.Take(30)) Console.WriteLine("      " + line);
            }

        if (say is null) return Task.CompletedTask;
        string who = voiceName is null ? "Windows' voice" : voiceName;
        var pos = Positives.Select(p => new Piece(p, say(p, 0), true)).ToList();
        var neg = Negatives.Select(p => new Piece(p, say(p, 0), false)).ToList();
        var all = pos.Concat(neg).ToList();
        foreach (var level in new[] { "Normal", "High", "Max" })
        {
            var r = Listen(all, level);
            var caught = pos.Select((p, k) => r.Hits.ContainsKey(k)).ToList();
            var falseHits = neg.Select((p, k) => (p.Label, Hit: r.Hits.TryGetValue(pos.Count + k, out var h) ? h[0].Text : null))
                               .Where(x => x.Hit is not null).ToList();
            string missed = string.Join(", ", pos.Where((p, k) => !caught[k]).Select(p => $"\"{p.Label}\""));
            string wrong = string.Join(", ", falseHits.Select(x => $"\"{x.Label}\" → {x.Hit}"));
            Console.WriteLine($"    {level}: {caught.Count(c => c)}/{pos.Count} said, {falseHits.Count}/{neg.Count} ordinary sentences set it off" +
                              (missed.Length > 0 ? $"; missed {missed}" : "") + (wrong.Length > 0 ? $"; false: {wrong}" : ""));
            if (level != "Normal" && caught.Count(c => c) < pos.Count || falseHits.Count > 0)
                foreach (var line in r.Log.Take(80)) Console.WriteLine("      " + line);
            if (level == "Max")
            {
                Console.WriteLine($"    note: Maximum, {who}: {falseHits.Count} false trigger(s) in {neg.Count} sentences (Maximum is allowed a few)");
                check(caught.All(c => c), $"Maximum hears every way of saying it ({who})");
                continue;
            }
            check(falseHits.Count == 0, $"{level}: ordinary talk never sets it off — {neg.Count} sentences incl. \"flip\", \"lip\", \"click\", \"trip\" ({who}{(wrong.Length > 0 ? ": " + wrong : "")})");
            if (level == "High")
            {
                check(caught.All(c => c), $"High hears every way of saying it: alone, \"clip that\", mid-sentence ({who}{(missed.Length > 0 ? "; missed " + missed : "")})");
                // timing: "Clip" said alone; the clip has to end before the word
                double at = r.Hits.TryGetValue(0, out var h0) ? h0[0].At - r.VoiceStart[0] : double.NaN;
                check(Math.Abs(at) < 0.3, $"…and knows when the word started, so the clip ends before it ({at * 1000:+0;-0} ms from the voice starting)");
                int sent = Array.IndexOf(Positives, "Okay, let's clip that.");
                double into = r.Hits.TryGetValue(sent, out var hs) ? hs[0].At - r.VoiceStart[sent] : double.NaN;
                check(into is > 0.3 and < 2.0, $"…inside a sentence, at the word, not the start of the sentence ({into:0.00}s in)");
            }
            // Normal only takes a word transcribed exactly: "clip that" and the word in a sentence always are; a lone "Clip"
            // can come out as "club" in some voices (High and Maximum take that)
            else check(Positives.Select((p, k) => (p, k)).Where(x => x.p.Contains(' ')).All(x => caught[x.k]),
                $"Normal hears \"clip that\", \"clip it\" and the word in a sentence ({caught.Count(c => c)}/{pos.Count}{(missed.Length > 0 ? "; missed " + missed : "")})");
        }
        // like a real microphone: 48 kHz with background noise, through the app's converter
        var conv = new SpeechPcmConverter();
        var nrnd = new Random(7);
        var at48 = say("Clip that", 48000).Select(v => v / 32768f + (float)(0.03 * (nrnd.NextDouble() * 2 - 1))).ToArray();
        var outs = new List<short>();
        for (int i = 0; i < at48.Length; i += 480) outs.AddRange(conv.Convert(at48.AsSpan(i, Math.Min(480, at48.Length - i)), 48000));
        var mic = Listen(new[] { new Piece("mic", outs.ToArray(), true) }, "High");
        check(mic.Hits.ContainsKey(0), $"…also through a noisy 48 kHz microphone ({who})");
        return Task.CompletedTask;
    }
}
