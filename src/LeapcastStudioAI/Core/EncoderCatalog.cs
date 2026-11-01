using System.Diagnostics;
using System.Globalization;

namespace LeapcastStudioAI;

/// <summary>
/// Encoder argument sets. Each encoder has a "Rich" tier (look-ahead, adaptive quantization,
/// B-frames: the settings that buy quality per kbps) and a conservative "Basic" tier that works
/// on older GPUs/drivers. Detection test-encodes with both so we always use the best one that works.
/// </summary>
public static class EncoderCatalog
{
    public static readonly string[] Candidates = { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };

    public static string DisplayName(string encoder) => encoder switch
    {
        "h264_nvenc" => "NVIDIA NVENC",
        "h264_qsv" => "Intel Quick Sync",
        "h264_amf" => "AMD AMF",
        "libx264" => "x264 (CPU)",
        "Auto" => "Automatic",
        _ => encoder
    };

    private static string K(int kbps) => kbps.ToString(CultureInfo.InvariantCulture) + "k";

    public static List<string> BuildVideoArgs(EncodePlan plan)
    {
        int cap = plan.CapKbps;
        int target = (int)(cap * 0.6);       // average target for encoders without a quality-capped mode
        string gop = plan.GopFrames.ToString(CultureInfo.InvariantCulture);
        string q = plan.Quality.ToString(CultureInfo.InvariantCulture);
        string hwq = (plan.Quality + 2).ToString(CultureInfo.InvariantCulture); // hardware CQ scale ≈ x264 CRF + 2
        bool rich = plan.Tier == EncoderTier.Rich;
        bool light = plan.Light; // performance governor: trade a little efficiency for speed
        var a = new List<string> { "-c:v", plan.Encoder };

        switch (plan.Encoder)
        {
            case "h264_nvenc":
                a.AddRange(new[] { "-preset", light ? "p2" : rich ? "p5" : "p4", "-profile:v", "high" });
                if (rich) a.AddRange(new[] { "-tune", light ? "ll" : "hq" });
                if (plan.Cbr) a.AddRange(new[] { "-rc", "cbr", "-b:v", K(cap), "-maxrate", K(cap), "-bufsize", K(cap) });
                else a.AddRange(new[] { "-rc", "vbr", "-cq", hwq, "-b:v", "0", "-maxrate", K(cap), "-bufsize", K(cap) });
                if (rich && light)
                    a.AddRange(new[] { "-spatial-aq", "1", "-bf", "0", "-multipass", "disabled" }); // no look-ahead, no extra passes
                else if (rich)
                {
                    a.AddRange(new[] { "-spatial-aq", "1", "-temporal-aq", "1", "-aq-strength", "8",
                        "-rc-lookahead", plan.LowLatency ? "8" : "20",
                        "-bf", plan.LowLatency ? "0" : "2", "-multipass", "qres" });
                    if (!plan.LowLatency) a.AddRange(new[] { "-b_ref_mode", "middle" });
                }
                a.AddRange(new[] { "-g", gop, "-no-scenecut", "1", "-forced-idr", "1" });
                break;

            case "h264_qsv":
                a.AddRange(new[] { "-preset", light ? "veryfast" : rich ? "slower" : "medium", "-profile:v", "high" });
                if (plan.Cbr) a.AddRange(new[] { "-b:v", K(cap), "-maxrate", K(cap), "-bufsize", K(cap) });
                else
                {
                    if (rich) a.AddRange(new[] { "-global_quality", hwq }); // QVBR: quality target with a hard cap
                    a.AddRange(new[] { "-b:v", K(target), "-maxrate", K(cap), "-bufsize", K(cap) });
                }
                if (rich)
                {
                    a.AddRange(new[] { "-bf", plan.LowLatency || light ? "0" : "3" });
                    if (!plan.LowLatency && !light) a.AddRange(new[] { "-look_ahead_depth", "40", "-extbrc", "1" });
                }
                a.AddRange(new[] { "-g", gop, "-idr_interval", "0" });
                break;

            case "h264_amf":
                a.AddRange(new[] { "-usage", plan.LowLatency ? "lowlatency" : "transcoding",
                    "-quality", light ? "speed" : rich ? "quality" : "balanced", "-profile:v", "high" });
                if (plan.Cbr) a.AddRange(new[] { "-rc", "cbr", "-b:v", K(cap), "-maxrate", K(cap), "-bufsize", K(cap) });
                else a.AddRange(new[] { "-rc", "vbr_peak", "-b:v", K(target), "-maxrate", K(cap), "-bufsize", K(cap) });
                if (rich) a.AddRange(light ? new[] { "-vbaq", "1" } : new[] { "-vbaq", "1", "-preanalysis", "1" });
                a.AddRange(new[] { "-g", gop });
                break;

            default: // libx264
                string preset = light ? LighterX264(plan.X264Preset) : rich ? plan.X264Preset : "veryfast";
                a.AddRange(new[] { "-preset", preset, "-profile:v", "high" });
                string xp = $"aq-mode=3:rc-lookahead={(plan.LowLatency || light ? 10 : 40)}";
                if (plan.Cbr)
                {
                    a.AddRange(new[] { "-b:v", K(cap), "-minrate", K(cap), "-maxrate", K(cap), "-bufsize", K(cap) });
                    xp += ":nal-hrd=cbr";
                }
                else a.AddRange(new[] { "-crf", q, "-maxrate", K(cap), "-bufsize", K(cap) });
                if (rich) a.AddRange(new[] { "-x264-params", xp });
                a.AddRange(new[] { "-bf", plan.LowLatency || light ? "1" : "3", "-g", gop, "-keyint_min", gop, "-sc_threshold", "0" });
                break;
        }
        return a;
    }

    /// <summary>Two x264 presets faster (each step roughly halves the CPU time per frame).</summary>
    public static string LighterX264(string preset) => preset switch
    {
        "slow" or "medium" => "veryfast",
        "fast" or "faster" => "superfast",
        _ => "ultrafast",
    };

    /// <summary>Test-encodes a few frames with each candidate (rich, then basic) and returns what works.</summary>
    public static async Task<List<ProbedEncoder>> ProbeAsync(string ffmpeg, CancellationToken ct = default)
    {
        var found = new List<ProbedEncoder>();
        foreach (var enc in Candidates)
        {
            foreach (var tier in new[] { EncoderTier.Rich, EncoderTier.Basic })
            {
                var plan = new EncodePlan(1280, 720, 30, 3000, 22, enc, tier, false, false, "veryfast", "probe", Array.Empty<string>());
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                    "-i", "testsrc2=size=1280x720:rate=30", "-frames:v", "20", "-vf", "format=nv12" };
                args.AddRange(BuildVideoArgs(plan));
                args.AddRange(new[] { "-f", "null", "-" });
                var (code, _) = await RunQuietAsync(ffmpeg, args, TimeSpan.FromSeconds(20), ct);
                if (code != 0) continue;
                // the governor's lighter settings must work too (older drivers lack some options)
                var light = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                    "-i", "testsrc2=size=1280x720:rate=30", "-frames:v", "20", "-vf", "format=nv12" };
                light.AddRange(BuildVideoArgs(plan with { Light = true }));
                light.AddRange(new[] { "-f", "null", "-" });
                var (lightCode, _) = await RunQuietAsync(ffmpeg, light, TimeSpan.FromSeconds(20), ct);
                found.Add(new ProbedEncoder(enc, tier) { LightOk = lightCode == 0 });
                break;
            }
        }
        return found;
    }

    internal static async Task<(int code, string stderr)> RunQuietAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync(ct);
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
            string err = await errTask; string output = await outTask;
            return (p.ExitCode, err + output); // some ffmpeg output (e.g. -h) goes to stdout
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
