using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LeapcastStudioAI.Vod;

/// <summary>
/// Your camera's look in the edited video (the recording has it on top of the game, where the stream showed it):
/// its shape (as recorded, rounded, oval or circle, with a thin gold ring), and the background behind you (as
/// recorded, blurred, or removed: a person segmentation model cuts you out frame by frame).
/// A shape is drawn big enough to cover the camera's old square completely, so no corner of it shows.
/// </summary>
public sealed record CameraLook(string Shape = "", string Background = "", bool Ring = true)
{
    public const string Rounded = "rounded", Oval = "oval", Circle = "circle", Blurred = "blurred", Removed = "removed";
    public static readonly string[] Shapes = { "", Rounded, Oval, Circle };
    public static readonly string[] Backgrounds = { "", Blurred, Removed };
    public bool Any => Shape.Length > 0 || Background.Length > 0;
    public bool NeedsMask => Background is Blurred or Removed;

    public static string ShapeName(string s) => s switch { Rounded => "Rounded corners", Oval => "Oval", Circle => "Circle", _ => "As recorded (square)" };
    public static string BackgroundName(string s) => s switch { Blurred => "Blurred", Removed => "Removed", _ => "As recorded" };

    /// <summary>Finds people in a picture: <see cref="Size"/>×<see cref="Size"/> RGB in, how likely each pixel is you (0…1) out.</summary>
    public interface ISegmenter
    {
        int Size { get; }
        float[] Run(byte[] rgb);
    }

    /// <summary>The person segmentation model (set when the app starts, if the model is there).</summary>
    public static ISegmenter? Segmenter { get; set; }

    /// <summary>
    /// Where the camera goes in the video (pixels of the video) and its size, for a shape that covers the camera's
    /// old square (cx, cy: the square's middle).
    /// </summary>
    public static (double X, double Y, double W, double H) Box(LayoutRect cam, string shape)
    {
        double cx = cam.X + cam.W / 2, cy = cam.Y + cam.H / 2;
        double w = cam.W, h = cam.H;
        switch (shape)
        {
            case Oval: w *= Math.Sqrt(2); h *= Math.Sqrt(2); break;                    // an ellipse through the square's corners
            case Circle: w = h = Math.Sqrt(cam.W * cam.W + cam.H * cam.H); break;       // a circle through them
            case Rounded:
                double r = 0.14 * Math.Min(cam.W, cam.H), d = 0.3 * r + 2;             // the corners' curve clears the old corners
                w += 2 * d; h += 2 * d; break;
        }
        int W = Even(w), H = Even(h);
        return (Math.Round(cx - W / 2.0), Math.Round(cy - H / 2.0), W, H);
    }

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    /// <summary>The shape as a mask (gray: white inside, a soft 1.5 px edge), w×h, as a PGM file.</summary>
    public static void WriteShape(string path, int w, int h, string shape)
    {
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = (byte)Math.Round(255 * Inside(x + 0.5, y + 0.5, w, h, shape));
        using var f = File.Create(path);
        var head = Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n");
        f.Write(head); f.Write(px);
    }

    /// <summary>A thin gold ring along the shape's edge (RGBA, as a PAM file), with a soft dark shadow inside it.</summary>
    public static void WriteRing(string path, int w, int h, string shape)
    {
        double t = Math.Max(2.5, Math.Min(w, h) * 0.022);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double d = Distance(x + 0.5, y + 0.5, w, h, shape);   // px from the edge, + inside
                // from the shape's edge to t inside it, each side softened over 1.5 px
                double ring = Math.Clamp(d + 0.75, 0, 1.5) / 1.5 * (Math.Clamp(t - d + 0.75, 0, 1.5) / 1.5);
                double shade = d > t && d < t * 3 ? 0.35 * (1 - (d - t) / (2 * t)) : 0;
                int i = (y * w + x) * 4;
                if (ring > 0.01)
                {
                    // gold, lighter at the top (lit from above)
                    double light = 1.08 - 0.25 * y / h;
                    px[i] = (byte)Math.Clamp(0xD9 * light, 0, 255); px[i + 1] = (byte)Math.Clamp(0xB0 * light, 0, 255); px[i + 2] = (byte)Math.Clamp(0x4A * light, 0, 255);
                    px[i + 3] = (byte)Math.Round(255 * ring);
                }
                else if (shade > 0) { px[i] = px[i + 1] = px[i + 2] = 0; px[i + 3] = (byte)Math.Round(255 * shade); }
            }
        using var f = File.Create(path);
        f.Write(Encoding.ASCII.GetBytes($"P7\nWIDTH {w}\nHEIGHT {h}\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n"));
        f.Write(px);
    }

    private static double Inside(double x, double y, int w, int h, string shape) => Math.Clamp(Distance(x, y, w, h, shape) + 0.75, 0, 1.5) / 1.5;

    /// <summary>How far (pixels) a point is inside the shape's edge (negative: outside).</summary>
    private static double Distance(double x, double y, int w, int h, string shape)
    {
        double ax = w / 2.0, ay = h / 2.0, dx = x - ax, dy = y - ay;
        switch (shape)
        {
            case Oval or Circle:
            {
                // (an ellipse's distance, near enough at its edge)
                double k = Math.Sqrt(dx * dx / (ax * ax) + dy * dy / (ay * ay));
                if (k < 1e-9) return Math.Min(ax, ay);
                double gx = dx / (ax * ax), gy = dy / (ay * ay);
                double grad = Math.Sqrt(gx * gx + gy * gy) / k;
                return (1 - k) / Math.Max(1e-9, grad);
            }
            case Rounded:
            {
                double r = 0.14 * Math.Min(w, h) / 1.0;
                double qx = Math.Abs(dx) - (ax - r), qy = Math.Abs(dy) - (ay - r);
                double outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - r;
                return -outside;
            }
            default: return Math.Min(ax - Math.Abs(dx), ay - Math.Abs(dy));
        }
    }

    // ------------------------------------------------------------------ the mask of you (person segmentation)

    /// <summary>
    /// A video of where you are in the camera (white: you), for a stretch of the recording at the edit's frame rate:
    /// the camera's square is read from the recording, every frame goes through the segmentation model, the result is
    /// steadied over time (no flicker) and written as a small gray video.
    /// </summary>
    public static async Task<bool> MaskAsync(string ffmpeg, string source, double from, double length, double fps, LayoutRect cam, string output, CancellationToken ct)
    {
        if (Segmenter is not { } seg) return false;
        int n = seg.Size;
        string I(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var read = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-ss", I(Math.Max(0, from)), "-t", I(length), "-i", source, "-an",
                     "-vf", $"crop={(int)cam.W}:{(int)cam.H}:{(int)cam.X}:{(int)cam.Y},fps={I(fps)},scale={n}:{n}:flags=bilinear,format=rgb24",
                     "-f", "rawvideo", "-" })
            read.ArgumentList.Add(a);
        string partial = output + ".part.mkv";
        var write = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{n}x{n}", "-r", I(fps), "-i", "-",
                     "-c:v", "ffv1", partial })
            write.ArgumentList.Add(a);
        using var r = Process.Start(read)!;
        using var w = Process.Start(write)!;
        BackgroundWork.Track(r); BackgroundWork.Track(w);
        _ = r.StandardError.ReadToEndAsync(ct);
        var werr = w.StandardError.ReadToEndAsync(ct);
        var frame = new byte[n * n * 3];
        var outFrame = new byte[n * n];
        float[]? steady = null;
        int frames = 0;
        try
        {
            var input = r.StandardOutput.BaseStream;
            var sink = w.StandardInput.BaseStream;
            while (true)
            {
                int got = 0;
                while (got < frame.Length)
                {
                    int k = await input.ReadAsync(frame.AsMemory(got), ct);
                    if (k == 0) break;
                    got += k;
                }
                if (got < frame.Length) break;
                var p = seg.Run(frame);
                // steadier over time (no flicker at the edges), then a firm but soft edge
                if (steady is null) steady = (float[])p.Clone();
                else for (int i = 0; i < p.Length; i++) steady[i] = 0.55f * p[i] + 0.45f * steady[i];
                for (int i = 0; i < outFrame.Length; i++)
                    outFrame[i] = (byte)Math.Round(255 * Math.Clamp((steady[i] - 0.3f) / 0.4f, 0f, 1f));
                await sink.WriteAsync(outFrame, ct);
                frames++;
            }
            sink.Close();
            await r.WaitForExitAsync(ct);
            await w.WaitForExitAsync(ct);
            await werr;
            if (w.ExitCode != 0 || frames == 0) { try { File.Delete(partial); } catch { } return false; }
            File.Move(partial, output, true);
            return true;
        }
        catch
        {
            try { if (!r.HasExited) r.Kill(); } catch { }
            try { if (!w.HasExited) w.Kill(); } catch { }
            try { File.Delete(partial); } catch { }
            throw;
        }
    }

    /// <summary>The cached mask file for a piece (named after exactly what it covers).</summary>
    public static string MaskPath(string folder, string source, double from, double length, double fps, LayoutRect cam)
    {
        string key = $"{source}|{new FileInfo(source).Length}|{from:0.###}|{length:0.###}|{fps:0.###}|{cam.X:0}|{cam.Y:0}|{cam.W:0}|{cam.H:0}";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
        return Path.Combine(folder, "m-" + hash + ".mkv");
    }

    /// <summary>The shape and ring pictures for a size (made once).</summary>
    public static (string Shape, string Ring) Pictures(string folder, int w, int h, string shape)
    {
        Directory.CreateDirectory(folder);
        string s = Path.Combine(folder, $"shape-{shape}-{w}x{h}.pgm"), r = Path.Combine(folder, $"ring-{shape}-{w}x{h}.pam");
        if (!File.Exists(s)) WriteShape(s, w, h, shape);
        if (!File.Exists(r)) WriteRing(r, w, h, shape);
        return (s, r);
    }
}
