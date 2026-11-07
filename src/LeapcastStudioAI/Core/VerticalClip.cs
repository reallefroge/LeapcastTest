using System.Diagnostics;
using System.Globalization;

namespace LeapcastStudioAI;

/// <summary>Where the camera and the game are on the horizontal canvas when a clip is saved (canvas pixels).</summary>
public sealed record ClipLayout(int CanvasW, int CanvasH, LayoutRect? Game, LayoutRect? Camera);

/// <summary>
/// Turns a 16:9 clip into a 9:16 one for TikTok / Shorts: a small camera near the top (rounded corners), the game
/// big below it (the middle of the picture, where the action is, filling the width), a blurred, darkened copy of the
/// game behind everything, and an optional handle line at the bottom. Where the camera sat over the game, the game
/// copy is blurred so the camera doesn't appear twice.
/// </summary>
public static class VerticalClip
{
    /// <param name="Game">The game on the canvas (the background is made from all of it).</param>
    /// <param name="GameCrop">The part of the game shown in the panel (its middle, as wide as fits the panel's shape).</param>
    public sealed record Plan(int OutW, int OutH, LayoutRect Game, LayoutRect GameCrop, LayoutRect? Camera, (int X, int Y, int W, int H) GameBox,
        (int X, int Y, int W, int H)? CamBox, (int X, int Y, int W, int H)? CoverInGame);

    /// <summary>The game panel's shape (width / height): square, so the game fills the width and over half the height.
    /// A 16:9 game keeps its middle 56%: the character, the battle circle and the spell cards.</summary>
    public const double GameAspect = 1.0;
    /// <summary>The camera: at most this share of the height (and 4:3 at most: a wide webcam's sides are cut).</summary>
    public const double CamHeight = 0.2, CamMaxAspect = 4 / 3.0;

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    /// <summary>Output size: 1080 wide for 1080p canvases, 720 for 540p (never upscaled past the source by much).</summary>
    public static (int W, int H) OutputSize(int canvasH)
    {
        int w = Math.Clamp(Even(canvasH * 4 / 3.0 / 8) * 8, 720, 1080);
        return (w, w * 16 / 9);
    }

    /// <summary>The middle of <paramref name="r"/> at most <paramref name="aspect"/> wide (w/h).</summary>
    private static LayoutRect Middle(LayoutRect r, double aspect)
    {
        if (r.H <= 0 || r.W / r.H <= aspect) return r;
        double w = r.H * aspect;
        return new LayoutRect(r.X + (r.W - w) / 2, r.Y, w, r.H);
    }

    public static Plan PlanFor(ClipLayout layout, (int W, int H)? size = null)
    {
        var (ow, oh) = size ?? OutputSize(layout.CanvasH);
        var canvas = new LayoutRect(0, 0, layout.CanvasW, layout.CanvasH);
        var game = Clamp(layout.Game ?? canvas, canvas);
        if (game.W < 8 || game.H < 8) game = canvas;
        LayoutRect? cam = layout.Camera is { } c ? Clamp(c, canvas) : null;
        if (cam is { } cc && (cc.W < 8 || cc.H < 8)) cam = null;
        if (cam is { } wide) cam = Middle(wide, CamMaxAspect);

        // the game: the full width, its middle cropped to the panel's shape
        var crop = Middle(game, GameAspect);
        int gw = ow, gh = Even(ow * crop.H / crop.W);
        int margin = Even(oh * 0.05), gap = Even(oh * 0.02);
        (int X, int Y, int W, int H)? camBox = null;
        int gy;
        if (cam is { } k)
        {
            // the camera near the top, small and centred; the game right under it
            int ch = Even(oh * CamHeight);
            int cw = Even(ch * k.W / k.H);
            if (cw > ow * 0.7) { cw = Even(ow * 0.7); ch = Even(cw * k.H / k.W); }
            camBox = ((ow - cw) / 2 / 2 * 2, margin, cw, ch);
            gy = margin + ch + gap;
            int maxH = oh - gy - Even(oh * 0.08); // room for the handle line
            if (gh > maxH) { gh = maxH; crop = Middle(game, gw / (double)gh); }
        }
        else
        {
            if (gh > oh * 0.7) gh = Even(oh * 0.7);
            crop = Middle(game, gw / (double)gh);
            gy = Even((oh - gh) / 2.0 - oh * 0.03);
        }
        var gameBox = ((ow - gw) / 2 / 2 * 2, gy, gw, gh);

        // where the camera covered the game, in the shown crop's own pixels
        (int X, int Y, int W, int H)? cover = null;
        var camOnCanvas = layout.Camera is { } lc ? Clamp(lc, canvas) : (LayoutRect?)null;
        if (camOnCanvas is { } kc && Intersect(kc, crop) is { } hit && hit.W > 4 && hit.H > 4)
        {
            int gx = Even(crop.W), gyh = Even(crop.H);
            int cx = Math.Min(Even(hit.X - crop.X), gx - 4), cy = Math.Min(Even(hit.Y - crop.Y), gyh - 4);
            cover = (cx, cy, Math.Min(Even(hit.W), gx - cx), Math.Min(Even(hit.H), gyh - cy));
        }
        return new Plan(ow, oh, game, crop, cam, gameBox, camBox, cover);
    }

    /// <summary>
    /// The live 9:16 clip canvas (<paramref name="w"/>×<paramref name="h"/>): where the game panel and the camera go,
    /// for a game picture of <paramref name="gameAspect"/> and a camera of <paramref name="camAspect"/> (null = none).
    /// Same layout as <see cref="PlanFor"/>, but built from the raw sources, so nothing needs covering.
    /// </summary>
    public static Plan LivePlan(int w, int h, double gameAspect, double? camAspect)
    {
        const int u = 1080; // a nominal canvas: only the shapes matter
        int gw = Math.Max(16, (int)Math.Round(u * Math.Max(0.2, gameAspect)));
        LayoutRect? cam = camAspect is { } ca ? new LayoutRect(0, 0, Math.Max(8, Math.Min(gw, 400 * Math.Max(0.2, ca))), 400) : null;
        var p = PlanFor(new ClipLayout(gw, u, new LayoutRect(0, 0, gw, u), cam), (w, h));
        return p with { CoverInGame = null };
    }

    /// <summary>The ffmpeg filter graph: input 0 = the 16:9 clip, input 1 = the rounded-corner mask (when there's a camera).</summary>
    public static string Filter(Plan p, string? handle, string? fontFile)
    {
        string R(LayoutRect r) => $"{Even(r.W)}:{Even(r.H)}:{Even(r.X)}:{Even(r.Y)}";
        var g = new List<string>();
        int splits = p.Camera is null ? 2 : 3;
        g.Add($"[0:v]fps=30,split={splits}[bgsrc][gamesrc]{(p.Camera is null ? "" : "[camsrc]")}");
        // background: the game, covered and blurred small (cheap), darkened
        int bw = p.OutW / 4, bh = p.OutH / 4;
        g.Add($"[bgsrc]crop={R(p.Game)},scale={bw}:{bh}:force_original_aspect_ratio=increase,crop={bw}:{bh},boxblur=10:2," +
              $"eq=brightness=-0.12:saturation=0.9,scale={p.OutW}:{p.OutH}:flags=bilinear,setsar=1[bg]");
        // the game panel: the whole game; the spot under the camera blurred
        if (p.CoverInGame is { } cv)
        {
            g.Add($"[gamesrc]crop={R(p.GameCrop)},split[gm][gc]");
            // frosted: blurred, desaturated and darkened, so it reads as a smudge rather than a second camera
            g.Add($"[gc]crop={cv.W}:{cv.H}:{cv.X}:{cv.Y},boxblur=12:2,eq=brightness=-0.3:saturation=0.25[gcb]");
            g.Add($"[gm][gcb]overlay={cv.X}:{cv.Y},scale={p.GameBox.W}:{p.GameBox.H}:flags=lanczos,setsar=1[game]");
        }
        else g.Add($"[gamesrc]crop={R(p.GameCrop)},scale={p.GameBox.W}:{p.GameBox.H}:flags=lanczos,setsar=1[game]");
        g.Add($"[bg][game]overlay={p.GameBox.X}:{p.GameBox.Y}[v1]");
        string last = "v1";
        if (p.Camera is { } cam && p.CamBox is { } cb)
        {
            g.Add($"[camsrc]crop={R(cam)},scale={cb.W}:{cb.H}:flags=lanczos,format=rgba[cam]");
            g.Add($"[1:v]scale={cb.W}:{cb.H},format=gray[mask]");
            g.Add("[cam][mask]alphamerge[camr]");
            g.Add($"[v1][camr]overlay={cb.X}:{cb.Y}[v2]");
            last = "v2";
        }
        if (!string.IsNullOrWhiteSpace(handle) && !string.IsNullOrWhiteSpace(fontFile))
        {
            int fs = Even(p.OutW * 0.045);
            g.Add($"[{last}]drawtext=fontfile='{EscapeFilter(fontFile)}':text='{EscapeFilter(handle.Trim())}':fontcolor=white:fontsize={fs}:" +
                  $"x=(w-text_w)/2:y=h-{Even(p.OutH * 0.07)}-text_h:shadowcolor=black@0.6:shadowx=2:shadowy=2[v3]");
            last = "v3";
        }
        g.Add($"[{last}]format=yuv420p[vout]");
        return string.Join(";", g);
    }

    /// <summary>Escapes a value for an ffmpeg filter option in single quotes (paths with ':' and '\').</summary>
    public static string EscapeFilter(string s) => s.Replace("\\", "/").Replace(":", "\\:").Replace("'", "\u2019");

    /// <summary>A white rounded rectangle on black (alpha mask for the camera's corners).</summary>
    public static async Task MakeMaskAsync(string ffmpeg, int w, int h, string path, CancellationToken ct)
    {
        int r = Math.Max(4, Math.Min(w, h) / 9);
        string geq = $"lum='if(lte(hypot(max(0,abs(X-W/2)-(W/2-{r})),max(0,abs(Y-H/2)-(H/2-{r}))),{r}),255,0)'";
        var (code, err) = await EncoderCatalog.RunQuietAsync(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
            "-i", $"color=c=black:s={w}x{h}:d=1,format=gray,geq={geq}", "-frames:v", "1", path }, TimeSpan.FromSeconds(20), ct);
        if (code != 0) throw new InvalidOperationException("mask: " + err.Trim());
    }

    /// <summary>Renders <paramref name="input"/> (16:9) to <paramref name="output"/> (9:16), at low priority so the stream isn't disturbed.</summary>
    public static async Task RenderAsync(string ffmpeg, string input, string output, ClipLayout layout, IReadOnlyList<string> videoArgs,
        string? handle, CancellationToken ct = default)
    {
        var plan = PlanFor(layout);
        string? mask = null;
        try
        {
            if (plan.CamBox is { } cb)
            {
                mask = Path.Combine(Path.GetTempPath(), $"leapcast-mask-{cb.W}x{cb.H}.png");
                if (!File.Exists(mask)) await MakeMaskAsync(ffmpeg, cb.W, cb.H, mask, ct);
            }
            string? font = new[] { @"C:\Windows\Fonts\segoeuib.ttf", @"C:\Windows\Fonts\arialbd.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf" }
                .FirstOrDefault(File.Exists);
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", input };
            if (mask is not null) args.AddRange(new[] { "-loop", "1", "-i", mask });
            args.AddRange(new[] { "-filter_complex", Filter(plan, handle, font), "-map", "[vout]", "-map", "0:a?", "-shortest" });
            args.AddRange(videoArgs);
            args.AddRange(new[] { "-c:a", "copy", "-movflags", "+faststart", output });
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var errTask = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            using (ct.Register(() => { try { p.Kill(true); } catch { } }))
                await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new InvalidOperationException("9:16 clip: " + (await errTask).Trim());
        }
        catch { try { File.Delete(output); } catch { } throw; }
    }

    private static LayoutRect Clamp(LayoutRect r, LayoutRect bounds)
    {
        double x = Math.Max(bounds.X, r.X), y = Math.Max(bounds.Y, r.Y);
        double right = Math.Min(bounds.X + bounds.W, r.X + r.W), bottom = Math.Min(bounds.Y + bounds.H, r.Y + r.H);
        return new LayoutRect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static LayoutRect? Intersect(LayoutRect a, LayoutRect b)
    {
        double x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        double r = Math.Min(a.X + a.W, b.X + b.W), btm = Math.Min(a.Y + a.H, b.Y + b.H);
        return r > x && btm > y ? new LayoutRect(x, y, r - x, btm - y) : null;
    }
}
