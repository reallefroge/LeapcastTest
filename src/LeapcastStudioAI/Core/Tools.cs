using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LeapcastStudioAI;

public static class FfmpegLocator
{
    public static string? Find(string? configured)
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured.Trim().Trim('"'));
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "ffmpeg", "bin", exe));
        candidates.Add(Path.Combine(baseDir, "ffmpeg", exe));
        candidates.Add(Path.Combine(baseDir, exe));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), exe)); } catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<string?> VersionAsync(string ffmpeg)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi)!;
            _ = p.StandardError.ReadToEndAsync();
            string all = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var m = Regex.Match(all, @"ffmpeg version (\S+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }
}

public static class DeviceDiscovery
{
    public sealed record DshowDevice(string Name, string Kind);

    public static async Task<List<DshowDevice>> ListDshowAsync(string ffmpeg)
    {
        var (_, err) = await EncoderCatalog.RunQuietAsync(ffmpeg,
            new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" }, TimeSpan.FromSeconds(15), default);
        return ParseDshowList(err);
    }

    /// <summary>Parses "[dshow @ …] "Name" (video)" lines (ffmpeg ≥ 5) and the older two-section format.</summary>
    public static List<DshowDevice> ParseDshowList(string stderr)
    {
        var list = new List<DshowDevice>();
        string section = "";
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("DirectShow video devices", StringComparison.Ordinal)) { section = "video"; continue; }
            if (line.Contains("DirectShow audio devices", StringComparison.Ordinal)) { section = "audio"; continue; }
            if (line.Contains("Alternative name", StringComparison.Ordinal)) continue;
            var m = Regex.Match(line, "\"(?<name>[^\"]+)\"\\s*(\\((?<kind>video|audio|none|video,audio)\\))?");
            if (!m.Success) continue;
            string kind = m.Groups["kind"].Success ? m.Groups["kind"].Value : section;
            if (kind.Length == 0) continue;
            if (kind.Contains("video") && !list.Any(d => d.Name == m.Groups["name"].Value && d.Kind == "video"))
                list.Add(new DshowDevice(m.Groups["name"].Value, "video"));
            else if (kind == "audio") list.Add(new DshowDevice(m.Groups["name"].Value, "audio"));
        }
        return list;
    }
}

/// <summary>One capture mode a webcam offers (from DirectShow's option list).</summary>
public sealed record CameraMode(string Format, bool Compressed, int Width, int Height, double MaxFps)
{
    /// <summary>The ffmpeg dshow option that selects this format ("-vcodec mjpeg" or "-pixel_format yuyv422").</summary>
    public string Tag => (Compressed ? "vcodec=" : "pixel_format=") + Format;
    public override string ToString() => $"{Width}×{Height} @ {MaxFps:0.##} FPS ({Format})";
}

public static class CameraModes
{
    public static async Task<List<CameraMode>> ListAsync(string ffmpeg, string device)
    {
        var (_, err) = await EncoderCatalog.RunQuietAsync(ffmpeg,
            new[] { "-hide_banner", "-f", "dshow", "-list_options", "true", "-i", "video=" + device }, TimeSpan.FromSeconds(15), default);
        return Parse(err);
    }

    private static readonly Regex Line = new(
        @"(?<kind>vcodec|pixel_format)=(?<fmt>\S+)\s+min s=\d+x\d+ fps=[\d.]+\s+max s=(?<w>\d+)x(?<h>\d+) fps=(?<fps>[\d.]+)",
        RegexOptions.Compiled);

    /// <summary>Parses "vcodec=mjpeg  min s=1280x720 fps=5 max s=1280x720 fps=30" / "pixel_format=yuyv422 …" lines.</summary>
    public static List<CameraMode> Parse(string stderr)
    {
        var list = new List<CameraMode>();
        foreach (Match m in Line.Matches(stderr))
        {
            var mode = new CameraMode(m.Groups["fmt"].Value, m.Groups["kind"].Value == "vcodec",
                int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups["fps"].Value, CultureInfo.InvariantCulture));
            if (!list.Any(x => x.Tag == mode.Tag && x.Width == mode.Width && x.Height == mode.Height && Math.Abs(x.MaxFps - mode.MaxFps) < 0.01))
                list.Add(mode);
        }
        return list;
    }

    /// <summary>
    /// The lightest mode that still looks sharp where the camera is drawn and runs at the stream's frame rate
    /// (a 24 FPS camera in a 30 FPS stream repeats every 4th frame, which reads as stutter). Uncompressed formats
    /// beat MJPEG at the same size (no decoding). Null when the list is empty.
    /// </summary>
    public static CameraMode? Best(IReadOnlyList<CameraMode> modes, int neededWidth, int streamFps)
    {
        if (modes.Count == 0) return null;
        int minW = Math.Max(640, neededWidth);
        var fast = modes.Where(m => m.MaxFps >= streamFps - 0.5).ToList();
        var pool = fast.Count > 0 ? fast : modes.Where(m => m.MaxFps >= modes.Max(x => x.MaxFps) - 0.5).ToList();
        var bigEnough = pool.Where(m => m.Width >= minW).ToList();
        var pick = (bigEnough.Count > 0 ? bigEnough : pool)
            .OrderBy(m => bigEnough.Count > 0 ? m.Width * m.Height : -(m.Width * m.Height)) // smallest that's enough, else largest
            .ThenBy(m => m.Compressed ? 1 : 0)
            .ThenBy(m => Math.Abs(m.Width / (double)m.Height - 16.0 / 9))
            .First();
        return pick;
    }
}

public sealed record BandwidthResult(double MeasuredKbps, int TestedKbps, bool ReachedTestRate, string Detail);

/// <summary>
/// Twitch-safe upload test: pushes an incompressible test pattern at a fixed rate to Twitch with
/// ?bandwidthtest=true (Twitch accepts the data but never shows it to viewers) and measures what got through.
/// </summary>
public static class BandwidthTester
{
    public static async Task<BandwidthResult> RunAsync(string ffmpeg, string serverUrl, string streamKey,
        int testKbps = 9000, int seconds = 12, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        string url = Platforms.BuildUrl(serverUrl, streamKey) + "?bandwidthtest=true";
        string k = testKbps.ToString(CultureInfo.InvariantCulture) + "k";
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:2",
            "-re", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30,noise=alls=40:allf=t+u",
            "-re", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-t", seconds.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-b:v", k, "-minrate", k, "-maxrate", k, "-bufsize", k, "-x264-params", "nal-hrd=cbr", "-g", "60",
            "-c:a", "aac", "-b:a", "128k", "-f", "flv", url
        };
        return await MeasureAsync(ffmpeg, args, testKbps, seconds, progress, ct);
    }

    internal static async Task<BandwidthResult> MeasureAsync(string ffmpeg, IReadOnlyList<string> args, int testKbps, int seconds,
        IProgress<string>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync(ct);
        long firstBytes = -1, lastBytes = 0;
        var sw = Stopwatch.StartNew();
        double firstT = 0, lastT = 0;
        var errors = new List<string>();
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        string? line;
        while ((line = await p.StandardError.ReadLineAsync(ct)) is not null)
        {
            if (line.StartsWith("total_size=", StringComparison.Ordinal))
            {
                if (long.TryParse(line[11..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > 0)
                {
                    if (firstBytes < 0) { firstBytes = bytes; firstT = sw.Elapsed.TotalSeconds; }
                    lastBytes = bytes; lastT = sw.Elapsed.TotalSeconds;
                    double kbpsNow = lastT - firstT > 0.5 ? (lastBytes - firstBytes) * 8 / 1000.0 / (lastT - firstT) : 0;
                    progress?.Report(kbpsNow > 0 ? $"Measuring… {kbpsNow / 1000:0.0} Mbps" : "Connecting…");
                }
            }
            else if (!FfmpegRunner.IsProgressLine(line, line.IndexOf('='))) errors.Add(line);
        }
        await p.WaitForExitAsync(ct);
        double span = lastT - firstT;
        if (firstBytes < 0 || span < 2)
        {
            string why = errors.LastOrDefault() ?? "no data was sent";
            throw new InvalidOperationException("The test couldn't send data: " + StreamEngine.Explain(why));
        }
        double kbps = (lastBytes - firstBytes) * 8 / 1000.0 / span;
        bool reached = kbps >= testKbps * 0.95;
        string detail = reached
            ? $"Your upload handled the full {testKbps / 1000.0:0.#} Mbps test, so it's at least that fast."
            : $"Your upload sustained about {kbps / 1000:0.0} Mbps.";
        return new BandwidthResult(kbps, testKbps, reached, detail);
    }
}
