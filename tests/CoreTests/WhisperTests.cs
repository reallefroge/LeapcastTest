using LeapcastStudioAI;
using LeapcastStudioAI.Vod;

/// <summary>Whisper captions: reading whisper-cli's JSON (everywhere), and real speech in → words and times out (when Whisper is installed).</summary>
static class WhisperTests
{
    public static async Task RunAsync(Action<bool, string> check, Func<string, int, short[]>? say = null)
    {
        // ---- the JSON: tokens → words, DTW times, sound tags and unsure tokens out
        const string json = """
        {"transcription":[{"offsets":{"from":0,"to":4000},"text":" Bottom feeder was, hiding","tokens":[
          {"text":"[_BEG_]","offsets":{"from":0,"to":0},"p":0.9,"t_dtw":-1},
          {"text":" Bottom","offsets":{"from":200,"to":600},"p":0.95,"t_dtw":31},
          {"text":" feed","offsets":{"from":600,"to":800},"p":0.9,"t_dtw":66},
          {"text":"er","offsets":{"from":800,"to":1000},"p":0.88,"t_dtw":80},
          {"text":" was","offsets":{"from":1000,"to":1300},"p":0.97,"t_dtw":104},
          {"text":",","offsets":{"from":1300,"to":1300},"p":0.4,"t_dtw":120},
          {"text":" [","offsets":{"from":1300,"to":1400},"p":0.9,"t_dtw":125},
          {"text":"Music","offsets":{"from":1400,"to":1700},"p":0.9,"t_dtw":130},
          {"text":"]","offsets":{"from":1700,"to":1800},"p":0.9,"t_dtw":150},
          {"text":" mumble","offsets":{"from":1800,"to":2000},"p":0.05,"t_dtw":181},
          {"text":" it's","offsets":{"from":2000,"to":2300},"p":0.9,"t_dtw":205},
          {"text":" hiding","offsets":{"from":2300,"to":2700},"p":0.9,"t_dtw":-1},
          {"text":"[_TT_200]","offsets":{"from":4000,"to":4000},"p":0.9,"t_dtw":-1}]}]}
        """;
        var w = Whisper.Parse(json);
        check(string.Join(" ", w.Select(x => x.Text)) == "Bottom feeder was it's hiding",
            $"Whisper JSON: tokens join into words; [Music], special tokens and unsure words are left out ({string.Join(" ", w.Select(x => x.Text))})");
        check(w.Count == 5 && Math.Abs(w[0].Start - 0.31) < 1e-6 && Math.Abs(w[1].Start - 0.66) < 1e-6 && Math.Abs(w[4].Start - 2.3) < 1e-6,
            "Whisper JSON: words start at the DTW alignment, or the token time when there is none");
        check(w.Zip(w.Skip(1)).All(p => p.First.End <= p.Second.Start + 1e-9 && p.First.End > p.First.Start),
            "Whisper JSON: every word ends before the next one starts");

        // ---- timing: the recognizer's exact times where it agrees, the voice's level between, silence dropped
        var env = Enumerable.Repeat(-75f, 240).ToArray();
        for (int i = 40; i < 100; i++) env[i] = -22f;  // voice 2.0–5.0 s
        var raw = new List<(VodWord, int, double, double)>
        {
            (new("the", 0.0, 1.7), 0, 0, 30), (new("bottom", 2.1, 5.2), 0, 0, 30), (new("feeder", 5.2, 7.0), 0, 0, 30),
            (new("was", 7.0, 7.5), 0, 0, 30), (new("hiding", 7.5, 9.0), 0, 0, 30),
            (new("thank", 9.5, 9.8), 1, 9.0, 12.0), (new("you", 9.8, 10.0), 1, 9.0, 12.0),
        };
        var timed = Whisper.Retime(raw, null, env);
        check(timed.Count == 5 && Math.Abs(timed[0].Start - 2.0) < 0.06 && timed[^1].End <= 5.01 && timed.Zip(timed.Skip(1)).All(p => p.First.End <= p.Second.Start + 1e-9),
            $"Whisper timing: words spread over where the voice actually is (2–5 s), the silent \"thank you\" left out ({string.Join(" ", timed.Select(x => $"{x.Text}@{x.Start:0.00}"))})");
        var anchored = Whisper.Retime(raw, new List<VodWord> { new("bottom", 2.40, 2.80), new("feeder", 2.85, 3.30), new("hiding", 3.9, 4.5) }, env);
        check(anchored.Count == 5 && Math.Abs(anchored[1].Start - 2.40) < 1e-6 && Math.Abs(anchored[2].Start - 2.85) < 1e-6 && Math.Abs(anchored[4].Start - 3.9) < 1e-6
              && anchored[0].Start >= 1.99 && anchored[0].End <= 2.40 + 1e-6 && anchored[3].Start >= 3.30 - 1e-6 && anchored[3].End <= 3.9 + 1e-6,
            $"Whisper timing: words the live recognizer also heard take its exact times, the rest fit between ({string.Join(" ", anchored.Select(x => $"{x.Text}@{x.Start:0.00}"))})");

        // the level is read from the samples, not the file's header (FFmpeg writes extra chunks before them)
        string hw = Path.Combine(Path.GetTempPath(), $"leapcast-hdr-{Guid.NewGuid():N}.wav");
        var ffw = FfmpegLocator.Find(null);
        if (ffw is not null)
        {
            await EncoderCatalog.RunQuietAsync(ffw, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "1", "-c:a", "pcm_s16le", "-metadata", "title=a long title to make the header bigger", hw }, TimeSpan.FromSeconds(30), CancellationToken.None);
            var silentEnv = Whisper.Envelope(hw);
            check(silentEnv.Length >= 19 && silentEnv.All(x => x < -90), $"Whisper timing: a silent WAV reads as silent from its first moment ({(silentEnv.Length > 0 ? silentEnv[0] : 0):0} dB)");
            try { File.Delete(hw); } catch { }
        }

        // ---- names spelled the game's way
        Vocabulary.SetUser("");
        string F(string t) => Vocabulary.FixText(t);
        check(F("we're going to Chrysalis today") == "we're going to Khrysalis today" && F("Crysalis, here we go") == "Khrysalis, here we go"
              && F("the Chrysalis's bosses") == "the Khrysalis's bosses",
            $"Captions: Chrysalis / Crysalis become Khrysalis ({F("the Chrysalis's bosses")})");
        check(F("back to crys") == "back to Khrys" && F("chris is here") == "chris is here",
            "Captions: Crys becomes Khrys, but the name Chris stays");
        check(F("dragon spire then moo shu and croc o topia") == "Dragonspyre then MooShu and Krokotopia" && F("wizard 101 rocks") == "Wizard101 rocks",
            $"Captions: names heard as several words join up ({F("dragon spire then moo shu and croc o topia")})");
        var t0w = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fixedWords = Vocabulary.Fix(new List<CaptionWord> { new("dragon", t0w, t0w.AddSeconds(0.3)), new("spire!", t0w.AddSeconds(0.35), t0w.AddSeconds(0.7)) });
        check(fixedWords.Count == 1 && fixedWords[0].Text == "Dragonspyre!" && fixedWords[0].EndUtc == t0w.AddSeconds(0.7),
            "Captions: a joined name takes the first word's start and the last word's end");
        Vocabulary.SetUser("Leapfrog = leap frog, leapfrogg\nKhrysalis = crisis alice");
        check(F("hi leap frog") == "hi Leapfrog" && F("crisis alice") == "Khrysalis" && Vocabulary.Prompt().Contains("Leapfrog") && Vocabulary.Prompt().Contains("Khrysalis"),
            $"Captions: your own spellings from Settings work and reach Whisper's prompt ({Vocabulary.Prompt().Length} chars)");
        Vocabulary.SetUser("");

        // ---- real speech (Windows CI: the installer's whisper-cli and the base.en model)
        if (!Whisper.Ready || say is null)
        {
            Console.WriteLine("    note: Whisper isn't installed here (or no voice to speak): live transcription test skipped");
            if (Environment.GetEnvironmentVariable("LEAPCAST_REQUIRE_WHISPER") == "1") check(false, "Whisper should be installed on this machine (LEAPCAST_REQUIRE_WHISPER=1)");
            return;
        }
        string ffmpeg = FfmpegLocator.Find(null) ?? throw new Exception("ffmpeg missing");
        const string line = "the bottom feeder was hiding behind the tower in chrysalis";
        var speech = say(line, 0);
        string wav = Path.Combine(Path.GetTempPath(), $"leapcast-whispertest-{Guid.NewGuid():N}.wav");
        var t0 = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var mt = new MicTrack(wav))
        {
            mt.Write(new short[16000], t0.AddSeconds(1));               // 1 s of silence
            mt.Write(new short[16000], t0.AddSeconds(2));               // another (2 s in all)
            mt.Write(speech, t0.AddSeconds(2 + speech.Length / 16000.0)); // then the line
            mt.Write(new short[16000], t0.AddSeconds(3 + speech.Length / 16000.0));
            mt.Finish();
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var heard = await Whisper.TranscribeAsync(ffmpeg, wav, null, CancellationToken.None);
        try { File.Delete(wav); } catch { }
        var said = line.Split(' ').Where(x => x != "chrysalis").ToArray();
        string got = string.Join(" ", heard.Select(x => x.Text.ToLowerInvariant()));
        int hits = said.Count(s => heard.Any(h => string.Equals(h.Text, s, StringComparison.OrdinalIgnoreCase)));
        bool good = hits >= said.Length - 1 && heard.Count > 0 && Math.Abs(heard[0].Start - 2.0) < 0.6 && heard.Any(h => h.Text == "Khrysalis");
        if (!good) await DiagnoseAsync(ffmpeg, wav2: speech, t0);
        check(hits >= said.Length - 1, $"Whisper heard the line ({hits}/{said.Length} words in {sw.ElapsedMilliseconds} ms): “{got}”");
        check(heard.Any(h => h.Text == "Khrysalis"), "Whisper + the name list write the world as Khrysalis (with a K)");
        check(heard.Count > 0 && Math.Abs(heard[0].Start - 2.0) < 0.6,
            $"Whisper places the first word where it's said (2.0 s; got {(heard.Count > 0 ? heard[0].Start : -1):0.00} s)");
        check(heard.Zip(heard.Skip(1)).All(p => p.Second.Start >= p.First.Start) && heard.Count > 0 && heard[^1].End <= 3 + speech.Length / 16000.0,
            "Whisper's word times rise and stay inside the speech");
    }

    /// <summary>When the live test fails: the same speech with each setting changed, to see which one is at fault.</summary>
    private static async Task DiagnoseAsync(string ffmpeg, short[] wav2, DateTime t0)
    {
        string wav = Path.Combine(Path.GetTempPath(), $"leapcast-whisperdiag-{Guid.NewGuid():N}.wav");
        using (var mt = new MicTrack(wav))
        {
            mt.Write(new short[32000], t0.AddSeconds(2));
            mt.Write(wav2, t0.AddSeconds(2 + wav2.Length / 16000.0));
            mt.Write(new short[16000], t0.AddSeconds(3 + wav2.Length / 16000.0));
            mt.Finish();
        }
        List<string> Without(List<string> a, string flag, bool hasValue) { int i = a.IndexOf(flag); if (i >= 0) a.RemoveRange(i, hasValue ? 2 : 1); return a; }
        var variants = new (string Name, Func<List<string>, List<string>> Hook)[]
        {
            ("as shipped", a => a),
            ("no prompt", a => Without(a, "--prompt", true)),
            ("no -sns/-nth", a => Without(Without(a, "-sns", false), "-nth", true)),
        };
        foreach (var (name, hook) in variants)
        {
            Whisper.ArgsHook = hook;
            try
            {
                var w = await Whisper.TranscribeAsync(ffmpeg, wav, null, CancellationToken.None);
                Console.WriteLine($"    Whisper diag [{name}]: " + string.Join(" ", w.Select(x => $"{x.Text}@{x.Start:0.00}")));
                var raw = System.Text.RegularExpressions.Regex.Matches(Whisper.LastJson ?? "", "\"text\":\\s*\"([^\"]*)\"").Select(m => m.Groups[1].Value).FirstOrDefault() ?? "";
                Console.WriteLine($"    Whisper diag [{name}] segment text: {raw}");
            }
            catch (Exception ex) { Console.WriteLine($"    Whisper diag [{name}]: failed {ex.Message}"); }
            finally { Whisper.ArgsHook = null; }
        }
        // the shipped run's JSON, trimmed
        Whisper.ArgsHook = null;
        await Whisper.TranscribeAsync(ffmpeg, wav, null, CancellationToken.None);
        string j = Whisper.LastJson ?? "";
        Console.WriteLine("    Whisper diag json: " + System.Text.RegularExpressions.Regex.Replace(j, "\\s+", " ")[..Math.Min(3000, j.Length)]);
        try { File.Delete(wav); } catch { }
    }
}
