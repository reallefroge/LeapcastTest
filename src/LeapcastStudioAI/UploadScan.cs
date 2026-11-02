namespace LeapcastStudioAI;

/// <summary>
/// "Scan my upload": Twitch's invisible bandwidth-test mode when a Twitch output with a key exists (a real stream
/// to the real ingest, the most accurate), otherwise an HTTPS upload test. Result in kbps.
/// </summary>
public static class UploadScan
{
    public static async Task<BandwidthResult> RunAsync(StudioSettings settings, string? ffmpeg, IProgress<string> progress, CancellationToken ct)
    {
        var twitch = settings.Outputs.FirstOrDefault(o => o.Service == "Twitch" && SecretStore.Unprotect(o.StreamKeyProtected).Length > 0);
        if (twitch is not null && ffmpeg is not null)
        {
            try
            {
                progress.Report("Connecting to Twitch (test mode: nothing is shown to viewers)…");
                return await BandwidthTester.RunAsync(ffmpeg, twitch.ServerUrl, SecretStore.Unprotect(twitch.StreamKeyProtected), 12000, 12, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { progress.Report("Twitch test unavailable: using the general upload test…"); }
        }
        return await UploadSpeedTest.RunAsync(progress, ct);
    }
}
