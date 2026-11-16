namespace LeapcastStudioAI;

/// <summary>
/// Finds fader levels from a short test instead of by trial and error: you talk for a few seconds, then let the
/// game play for a few, and the faders are set so your voice sits at a healthy streaming level with the game
/// clearly under it.
/// </summary>
public static class AudioBalance
{
    /// <summary>Where your voice's loud moments should peak on the stream (dBFS), and how far under it the game sits.</summary>
    public const double VoicePeakDb = -6, GameUnderVoiceDb = 12;

    public sealed record Result(double MicDb, double GameDb, double MicHeardDb, double GameHeardDb, string? MicNote, string? GameNote);

    /// <summary>The level of the loud moments in a run of meter readings (dBFS peaks): the 90th percentile of what isn't silence.</summary>
    public static double? Loud(IEnumerable<double> peaksDb)
    {
        var heard = peaksDb.Where(d => d > -50).OrderBy(d => d).ToList();
        return heard.Count < 5 ? null : heard[(int)Math.Min(heard.Count - 1, Math.Floor(heard.Count * 0.9))];
    }

    /// <param name="mic">Meter readings while you talked (before the fader).</param>
    /// <param name="game">Meter readings while the game played (before the fader).</param>
    public static Result Suggest(IReadOnlyCollection<double> mic, IReadOnlyCollection<double> game, double micNow, double gameNow)
    {
        double? m = Loud(mic), g = Loud(game);
        double micDb = micNow, gameDb = gameNow;
        string? micNote = null, gameNote = null;
        if (m is { } ml)
        {
            double want = VoicePeakDb - ml;
            micDb = Math.Round(Math.Clamp(want, -40, 20) * 2) / 2;
            if (want > 20) micNote = "Your microphone is very quiet even at the top of its fader: raise its level in Windows sound settings (or move closer), then run this again.";
        }
        else micNote = "Leapcast didn't hear you: check the microphone is the right device and not muted, then run this again.";
        if (g is { } gl)
        {
            double want = VoicePeakDb - GameUnderVoiceDb - gl;
            gameDb = Math.Round(Math.Clamp(want, -40, 20) * 2) / 2;
        }
        else gameNote = "Leapcast didn't hear the game: make sure it's making sound during the test (its level was left as it was).";
        return new Result(micDb, gameDb, m ?? -60, g ?? -60, micNote, gameNote);
    }
}
