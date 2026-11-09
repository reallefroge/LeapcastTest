using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LeapcastStudioAI;

/// <summary>
/// Image and text sources for the GPU canvas: each file decoded once to BGRA (by FFmpeg, in the background), and
/// again only when the file changes. Until it's ready the layer is simply not drawn yet.
/// </summary>
public sealed class GpuImages
{
    private readonly Func<string?> _ffmpeg;
    private readonly Dictionary<string, (DateTime Stamp, GpuFrame? Frame, bool Loading)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GpuImages(Func<string?> ffmpeg) => _ffmpeg = ffmpeg;

    public GpuFrame? Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); } catch { return null; }
        lock (_cache)
        {
            if (_cache.TryGetValue(path, out var e) && (e.Stamp == stamp || e.Loading)) return e.Frame;
            _cache[path] = (stamp, e.Frame, true);
        }
        _ = Task.Run(async () =>
        {
            GpuFrame? f = null;
            try { f = await DecodeAsync(path); } catch { }
            lock (_cache) _cache[path] = (stamp, f, false);
        });
        lock (_cache) return _cache[path].Frame;
    }

    /// <summary>The image's first frame as BGRA (straight alpha), with its size.</summary>
    private async Task<GpuFrame?> DecodeAsync(string path)
    {
        if (_ffmpeg() is not { } ff || !File.Exists(path)) return null;
        // its size, from FFmpeg's description of the file
        var probe = new ProcessStartInfo(ff) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-i", path }) probe.ArgumentList.Add(a);
        string info;
        using (var p = Process.Start(probe)!) { info = await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); }
        var m = Regex.Match(info, @"Video:.*?(\d{2,5})x(\d{2,5})");
        if (!m.Success) return null;
        int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);
        if (w < 1 || h < 1 || (long)w * h > 8192L * 8192) return null;
        var dec = new ProcessStartInfo(ff) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }) dec.ArgumentList.Add(a);
        using var d = Process.Start(dec)!;
        var buf = new byte[w * h * 4];
        int got = 0;
        var s = d.StandardOutput.BaseStream;
        while (got < buf.Length) { int n = await s.ReadAsync(buf.AsMemory(got)); if (n <= 0) break; got += n; }
        await d.WaitForExitAsync();
        return got == buf.Length ? new GpuFrame(buf, w, h, FramePixels.Bgra) : null;
    }
}
