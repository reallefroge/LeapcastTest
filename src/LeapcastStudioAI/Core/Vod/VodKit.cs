using System.Diagnostics;
using System.Globalization;

namespace LeapcastStudioAI.Vod;

/// <summary>
/// Your Wizard101 kit: one folder (Videos\Leapcast\Wizard101 kit) the editor takes your channel's look and sound from.
/// It fills itself from your own Wizard101 streams: every Wizard101 recording you edit gives it stretches of the game's
/// music (where nobody talks), and the first one gives it the intro clip and the intro's music, which then stay the
/// same on every video. Anything you drop in yourself is used too: tracks in "music", a logo (PNG) in the folder,
/// your own "intro clip.mp4" / "intro music.*".
/// </summary>
public static class VodKit
{
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Leapcast", "Wizard101 kit");
    public static string MusicFolder => Path.Combine(Folder, "music");
    public static string IntroClipPath => Path.Combine(Folder, "intro clip.mp4");

    /// <summary>At most this many tracks are taken from your streams (your own files don't count).</summary>
    public const int MaxHarvested = 12;

    public static void Ensure()
    {
        try
        {
            Directory.CreateDirectory(MusicFolder);
            string readme = Path.Combine(Folder, "README.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme,
                    "Leapcast's Wizard101 kit: your edited videos take their look and sound from here.\r\n\r\n" +
                    "It fills itself: every Wizard101 recording you edit adds a few stretches of the game's music (where nobody talks)\r\n" +
                    "to \"music\", and the first one sets the intro clip and the intro music, which then stay the same on every video.\r\n\r\n" +
                    "Add your own any time:\r\n" +
                    "  music\\            any audio files (mp3, ogg, wav, flac, m4a): used for the chapters\r\n" +
                    "  intro music.*     the intro's music (the same on every video)\r\n" +
                    "  intro clip.mp4    the footage behind the intro\r\n" +
                    "  any .png / .jpg   your logo (shown in the intro)\r\n\r\n" +
                    "Delete a file and it's simply not used (an auto one may be taken again from your next stream).\r\n");
        }
        catch { }
    }

    public static string? Logo()
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
        }
        catch { return null; }
    }

    public static string? IntroMusic()
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder, "intro music.*").FirstOrDefault(f => MusicLibrary.Tracks(Folder).Contains(f))
                : null;
        }
        catch { return null; }
    }

    public static string? IntroClip() => File.Exists(IntroClipPath) ? IntroClipPath : null;

    /// <summary>Your channel's look from the kit (what's in it right now).</summary>
    public static VodBrand Brand(string channel, string musicSource, bool hook, bool endScreen) =>
        new(channel, Logo(), musicSource == "kit" ? MusicFolder : null, IntroMusic(), IntroClip(),
            musicSource == "kit" ? "folder" : musicSource, endScreen, hook);

    /// <summary>What's in the kit, in a line.</summary>
    public static string Summary()
    {
        int tracks = MusicLibrary.Tracks(MusicFolder).Count;
        return $"{tracks} music track{(tracks == 1 ? "" : "s")}, intro clip {(IntroClip() is null ? "not yet" : "✓")}, " +
               $"intro music {(IntroMusic() is null ? (tracks > 0 ? "from the first track" : "not yet") : "✓")}, logo {(Logo() is null ? "none (drop a PNG in)" : "✓")}";
    }

    /// <summary>
    /// Takes from a Wizard101 recording what the kit is missing: stretches of the game's music (60 s, nobody talking on
    /// either track, the sound steady: music, not a fight), and, the first time, a lively 4 s for the intro and 7 s of
    /// music for it. Returns what was added.
    /// </summary>
    public static async Task<List<string>> HarvestAsync(string ffmpeg, string recording, VodSignals s, CancellationToken ct)
    {
        var added = new List<string>();
        Ensure();
        string stamp = Path.GetFileNameWithoutExtension(recording).Replace("Leapcast ", "");
        var existing = MusicLibrary.Tracks(MusicFolder);
        int harvested = existing.Count(f => Path.GetFileName(f).StartsWith("from stream ", StringComparison.OrdinalIgnoreCase));
        bool thisOneDone = existing.Any(f => Path.GetFileName(f).Contains(stamp, StringComparison.OrdinalIgnoreCase));

        // ---- the game's music: quiet of words, steady in level
        if (!thisOneDone && harvested < MaxHarvested && s.HasAudio && s.Loud.Length > 0)
        {
            var talk = new bool[(int)(s.Duration) + 2];
            foreach (var w in s.Words.Concat(s.Others))
                for (int k = (int)Math.Max(0, w.Start - 1); k <= Math.Min(talk.Length - 1, w.End + 1); k++) talk[k] = true;
            var picks = new List<(double At, double Score)>();
            const double Len = 60;
            for (double a = 30; a + Len < s.Duration - 30; a += 5)
            {
                if (Enumerable.Range((int)a, (int)Len).Any(k => k < talk.Length && talk[k])) continue;
                if (s.Markers.Any(m => m.At >= a && m.At <= a + Len)) continue;
                var lv = new List<double>();
                for (double t = a; t < a + Len; t += 0.5) lv.Add(s.LoudAt(t));
                double mean = lv.Average(), sd = Math.Sqrt(lv.Average(x => (x - mean) * (x - mean)));
                if (mean < -42 || mean > -14 || sd > 7) continue;
                picks.Add((a, sd));
            }
            var chosen = new List<double>();
            foreach (var (at, _) in picks.OrderBy(p => p.Score))
            {
                if (chosen.Count >= Math.Min(3, MaxHarvested - harvested)) break;
                if (chosen.Any(c => Math.Abs(c - at) < 300)) continue;   // different places in the stream: different music
                chosen.Add(at);
            }
            int n = 0;
            foreach (var at in chosen.OrderBy(x => x))
            {
                string file = Path.Combine(MusicFolder, $"from stream {stamp} {++n}.m4a");
                if (await RunAsync(ffmpeg, ct, "-y", "-v", "error", "-ss", I(at), "-t", I(Len), "-i", recording, "-vn",
                        "-af", $"afade=t=in:d=1.5,afade=t=out:st={I(Len - 2)}:d=2", "-ac", "2", "-ar", "48000", "-c:a", "aac", "-b:a", "192k", file))
                    added.Add(Path.GetFileName(file));
            }
        }

        // ---- the intro (once, then it stays): a lively 4 s, and the music under it
        if (IntroClip() is null && s.Motion.Length > 0)
        {
            double best = -1, bestAt = -1;
            for (double a = 20; a + 4 < s.Duration - 20; a += 1)
            {
                if (s.Markers.Any(m => m.At >= a - 2 && m.At <= a + 6)) continue;
                double sc = 0;
                for (double t = a; t < a + 4; t += 0.5) sc += s.MotionAt(t) + 2 * s.NoveltyAt(t);
                if (sc > best) { best = sc; bestAt = a; }
            }
            if (bestAt >= 0 && await RunAsync(ffmpeg, ct, "-y", "-v", "error", "-ss", I(bestAt), "-t", "4.5", "-i", recording, "-an",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", IntroClipPath))
                added.Add("intro clip.mp4");
        }
        if (IntroMusic() is null && MusicLibrary.Tracks(MusicFolder).FirstOrDefault() is { } first)
        {
            string im = Path.Combine(Folder, "intro music.m4a");
            if (await RunAsync(ffmpeg, ct, "-y", "-v", "error", "-ss", "3", "-t", "7", "-i", first, "-af", "afade=t=in:d=0.2,afade=t=out:st=5.5:d=1.5",
                    "-c:a", "aac", "-b:a", "192k", im))
                added.Add("intro music.m4a");
        }
        return added;
    }

    private static string I(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static async Task<bool> RunAsync(string ffmpeg, CancellationToken ct, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            BackgroundWork.Track(p);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 && File.Exists(args[^1]) && new FileInfo(args[^1]).Length > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
}
