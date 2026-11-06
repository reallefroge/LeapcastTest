using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LeapcastStudioAI;

/// <summary>
/// After a stream: the session's clips (and their 9:16 copies) gathered in one folder, a highlight reel (title card,
/// the last seconds of each clip, an outro card with your socials) and a thumbnail frame from each clip.
/// Runs after the stream at low priority.
/// </summary>
public static class Recap
{
    public const int SecondsPerClip = 20, IntroSeconds = 3, OutroSeconds = 5;

    public sealed record Result(string Folder, string? Reel, IReadOnlyList<string> Clips, IReadOnlyList<string> Thumbnails);

    public static string FolderFor(string clipsFolder, DateTime streamStartLocal) =>
        Path.Combine(clipsFolder, "Recap " + streamStartLocal.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture));

    public static async Task<Result> BuildAsync(string ffmpeg, IReadOnlyList<string> clips, string folder, string title, string socials,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        // 1) the clips (and their 9:16 copies) move into the recap folder
        var moved = new List<string>();
        foreach (var clip in clips.Where(File.Exists))
        {
            string dest = Path.Combine(folder, Path.GetFileName(clip));
            if (!string.Equals(Path.GetFullPath(clip), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) File.Move(clip, dest, true);
            moved.Add(dest);
            string vertical = Path.Combine(Path.GetDirectoryName(clip)!, Path.GetFileNameWithoutExtension(clip) + " (9x16).mp4");
            if (File.Exists(vertical)) File.Move(vertical, Path.Combine(folder, Path.GetFileName(vertical)), true);
        }
        if (moved.Count == 0) return new Result(folder, null, moved, Array.Empty<string>());

        // 2) a thumbnail from each clip (two seconds before its end: the moment itself)
        var thumbs = new List<string>();
        for (int i = 0; i < moved.Count; i++)
        {
            string t = Path.Combine(folder, $"Thumbnail {i + 1:00}.jpg");
            var (code, _) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-sseof", "-2", "-i", moved[i],
                "-frames:v", "1", "-q:v", "2", t }, TimeSpan.FromSeconds(30), ct);
            if (code == 0 && File.Exists(t)) thumbs.Add(t);
        }

        // 3) the highlight reel
        var (w, h) = await SizeOfAsync(ffmpeg, moved[0], ct) ?? (1280, 720);
        string reel = Path.Combine(folder, "Highlights.mp4");
        var args = ReelArgs(moved, w, h, title, socials, FontFile(), reel);
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var err = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        using (ct.Register(() => { try { p.Kill(true); } catch { } })) await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException("highlight reel: " + (await err).Trim());
        return new Result(folder, reel, moved, thumbs);
    }

    public static string? FontFile() =>
        new[] { @"C:\Windows\Fonts\segoeuib.ttf", @"C:\Windows\Fonts\arialbd.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf" }.FirstOrDefault(File.Exists);

    /// <summary>The ffmpeg command for the reel: intro card, the last seconds of each clip, outro card, all at w×h, 30 FPS.</summary>
    public static List<string> ReelArgs(IReadOnlyList<string> clips, int w, int h, string title, string socials, string? font, string output)
    {
        string inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var c in clips) a.AddRange(new[] { "-sseof", "-" + SecondsPerClip, "-i", c });
        var g = new List<string>();
        string Card(string name, int seconds, IEnumerable<(string Text, double Size, double Y)> lines)
        {
            string chain = $"color=c=0x0B0D18:s={w}x{h}:r=30:d={seconds},format=yuv420p";
            // a soft violet glow behind the text
            chain += ",drawbox=x=0:y=ih*0.42:w=iw:h=ih*0.16:color=0x7C5CFF@0.18:t=fill";
            if (font is not null)
                foreach (var (text, size, y) in lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)))
                    chain += $",drawtext=fontfile='{VerticalClip.EscapeFilter(font)}':text='{VerticalClip.EscapeFilter(text)}':fontcolor=white:" +
                             $"fontsize={(int)(h * size)}:x=(w-text_w)/2:y=h*{inv(y)}-text_h/2";
            chain += $",fade=t=in:st=0:d=0.5,fade=t=out:st={inv(seconds - 0.5)}:d=0.5[{name}v]";
            g.Add(chain);
            g.Add($"anullsrc=r=48000:cl=stereo,atrim=0:{seconds}[{name}a]");
            return name;
        }
        Card("intro", IntroSeconds, new[] { (title, 0.085, 0.47), (DateTime.Now.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture), 0.04, 0.6) });
        var socialLines = (socials ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4).ToList();
        var outroLines = new List<(string, double, double)> { ("Thanks for watching!", 0.075, 0.3) };
        for (int i = 0; i < socialLines.Count; i++) outroLines.Add((socialLines[i], 0.05, 0.47 + i * 0.09));
        Card("outro", OutroSeconds, outroLines);
        for (int i = 0; i < clips.Count; i++)
        {
            g.Add($"[{i}:v]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,fps=30,format=yuv420p,setsar=1," +
                  $"fade=t=in:st=0:d=0.4[c{i}v]");
            g.Add($"[{i}:a]aresample=48000,aformat=channel_layouts=stereo[c{i}a]");
        }
        var order = new List<string> { "intro" };
        order.AddRange(Enumerable.Range(0, clips.Count).Select(i => $"c{i}"));
        order.Add("outro");
        g.Add(string.Concat(order.Select(n => $"[{n}v][{n}a]")) + $"concat=n={order.Count}:v=1:a=1[v][a]");
        a.AddRange(new[] { "-filter_complex", string.Join(";", g), "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", output });
        return a;
    }

    /// <summary>A video's picture size from ffmpeg's own report.</summary>
    public static async Task<(int W, int H)?> SizeOfAsync(string ffmpeg, string file, CancellationToken ct)
    {
        var (_, info) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-hide_banner", "-i", file }, TimeSpan.FromSeconds(20), ct);
        var m = Regex.Match(info, @"Video:.*?(\d{2,5})x(\d{2,5})");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }
}
