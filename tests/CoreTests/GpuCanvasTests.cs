namespace LeapcastStudioAI;

/// <summary>
/// The GPU canvas (Windows, Direct3D 11 on the software renderer: the test machine has no graphics card): layers
/// land where they're placed with the right colours, scale/crop/mirror/opacity work, a fade mixes two scenes, a
/// new state applies at once, and a picture that didn't change isn't uploaded again.
/// </summary>
public static class GpuCanvasTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        // pure maths (every platform)
        var ndc = GpuCanvasMath.Ndc(new LayoutRect(0, 0, 320, 180), 320, 180);
        check(ndc == (-1f, 1f, 1f, -1f), $"GPU canvas: a full-canvas layer covers −1…1 ({ndc})");
        var uv = GpuCanvasMath.FillUv(400, 100, (0, 0, 0, 0), 200, 100, Anchors.FillCrop);
        check(Math.Abs(uv.U0 - 0.25) < 1e-9 && Math.Abs(uv.U1 - 0.75) < 1e-9 && uv.V0 == 0 && uv.V1 == 1, $"GPU canvas: crop-to-fill shows the middle of a wider picture ({uv})");
        var uvc = GpuCanvasMath.FillUv(100, 100, (10, 0, 30, 0), 60, 100, Anchors.FillStretch);
        check(Math.Abs(uvc.U0 - 0.1) < 1e-9 && Math.Abs(uvc.U1 - 0.7) < 1e-9, $"GPU canvas: a source's own crop ({uvc})");

#if WINDOWS_GPU
        const int W = 320, H = 180;
        // sources: a red page (BGRA), a half-transparent blue page, an I420 picture: left half black, right half white
        byte[] Bgra(int w, int h, byte b, byte g, byte r, byte a) { var o = new byte[w * h * 4]; for (int i = 0; i < w * h; i++) { o[i * 4] = b; o[i * 4 + 1] = g; o[i * 4 + 2] = r; o[i * 4 + 3] = a; } return o; }
        var red = new GpuFrame(Bgra(64, 36, 0, 0, 255, 255), 64, 36, FramePixels.Bgra);
        var blue = new GpuFrame(Bgra(32, 32, 255, 0, 0, 128), 32, 32, FramePixels.Bgra);
        byte[] Split(int w, int h)
        {
            var o = new byte[w * h * 3 / 2];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) o[y * w + x] = (byte)(x < w / 2 ? 16 : 235);
            Array.Fill(o, (byte)128, w * h, w * h / 2);
            return o;
        }
        var cam = new GpuFrame(Split(64, 36), 64, 36, FramePixels.I420);
        var frames = new Dictionary<string, GpuFrame> { ["red"] = red, ["blue"] = blue, ["cam"] = cam };
        IGpuCanvas canvas;
        try { canvas = new D3DCanvas(W, H, 30, k => frames.TryGetValue(k, out var f) ? f : null, software: true); }
        catch (Exception ex) { check(false, $"GPU canvas: Direct3D 11 starts ({ex.GetType().Name}: {ex.Message})"); return; }
        using (canvas)
        {
            Console.WriteLine($"    GPU canvas on: {canvas.Adapter}");
            // read a pixel back as RGB (from the canvas's YUV 4:2:0)
            (int R, int G, int B) Px(byte[] f, int x, int y)
            {
                double yy = (f[y * W + x] - 16) * 255.0 / 219, u = f[W * H + (y / 2) * (W / 2) + x / 2] - 128.0, v = f[W * H + W * H / 4 + (y / 2) * (W / 2) + x / 2] - 128.0;
                u *= 255.0 / 224; v *= 255.0 / 224;
                int C(double c) => (int)Math.Clamp(Math.Round(c), 0, 255);
                return (C(yy + 1.402 * v), C(yy - 0.344136 * u - 0.714136 * v), C(yy + 1.772 * u));
            }
            async Task<byte[]> NextFrame()
            {
                long n = canvas.Frames;
                for (int i = 0; i < 200 && canvas.Frames < n + 2; i++) await Task.Delay(10);
                return canvas.Latest() ?? new byte[W * H * 3 / 2];
            }
            bool Near((int R, int G, int B) a, (int R, int G, int B) b, int tol = 12) => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

            // 1. the red page fills the canvas, the camera in the bottom right quarter
            var scene1 = new GpuScene(new[]
            {
                new GpuLayer("red", new LayoutRect(0, 0, W, H)),
                new GpuLayer("cam", new LayoutRect(160, 90, 160, 90)),
            });
            canvas.SetState(new GpuState(new[] { scene1 }, 0));
            var f1 = await NextFrame();
            var bg = Px(f1, 40, 40); var camL = Px(f1, 180, 135); var camR = Px(f1, 300, 135);
            check(canvas.Error is null && Near(bg, (255, 0, 0)) && Near(camL, (0, 0, 0)) && Near(camR, (255, 255, 255)),
                $"GPU canvas: layers drawn where placed, in the right colours (red {bg}, camera left {camL}, right {camR}){(canvas.Error is { } e1 ? " — " + e1 : "")}");
            long uploads = canvas.Uploads;
            await NextFrame(); await NextFrame();
            check(canvas.Uploads == uploads, $"GPU canvas: an unchanged picture isn't uploaded again ({uploads} → {canvas.Uploads})");

            // 2. mirrored, half transparent, cropped to its right half: all just a new state, applied at once
            var scene2 = new GpuScene(new[]
            {
                new GpuLayer("red", new LayoutRect(0, 0, W, H)),
                new GpuLayer("cam", new LayoutRect(160, 90, 160, 90), Mirror: true),
                new GpuLayer("blue", new LayoutRect(0, 0, 80, 80)),
                new GpuLayer("cam", new LayoutRect(0, 100, 80, 80), U0: 0.5, U1: 1),
            });
            canvas.SetState(new GpuState(new[] { scene2 }, 0));
            var f2 = await NextFrame();
            var mL = Px(f2, 180, 135); var mR = Px(f2, 300, 135); var mix = Px(f2, 40, 40); var crop = Px(f2, 40, 140);
            check(Near(mL, (255, 255, 255)) && Near(mR, (0, 0, 0)), $"GPU canvas: mirrored ({mL} | {mR})");
            check(Near(mix, (127, 0, 128), 16), $"GPU canvas: a half-transparent page blends over what's under it ({mix})");
            check(Near(crop, (255, 255, 255)), $"GPU canvas: a cropped source shows only its part ({crop})");

            // 3. a 1 s fade from scene 1 to an empty scene (background), half way ≈ half each
            var empty = new GpuScene(Array.Empty<GpuLayer>());
            canvas.SetState(new GpuState(new[] { scene1, empty }, 1, From: 0, FadeStartUtc: DateTime.UtcNow, FadeSeconds: 1.0));
            await Task.Delay(500);
            var f3 = await NextFrame();
            var half = Px(f3, 40, 40);
            await Task.Delay(800);
            var f4 = await NextFrame();
            var done = Px(f4, 40, 40);
            check(half.R is > 70 and < 200 && Near(done, (0x0E, 0x10, 0x16), 10), $"GPU canvas: a fade mixes the two scenes (half way {half}, then the next scene {done})");

            // 4. a new picture from the source shows up (and is uploaded once)
            frames["red"] = new GpuFrame(Bgra(64, 36, 0, 255, 0, 255), 64, 36, FramePixels.Bgra);
            canvas.SetState(new GpuState(new[] { scene1 }, 0));
            var f5 = await NextFrame();
            check(Near(Px(f5, 40, 40), (0, 255, 0)), $"GPU canvas: a source's new picture shows up ({Px(f5, 40, 40)})");

            // 5. speed: frames keep coming at the frame rate (software renderer: the real GPU is far faster)
            long a = canvas.Frames; await Task.Delay(1000); long rate = canvas.Frames - a;
            check(rate >= 20, $"GPU canvas: keeps the frame rate on the software renderer ({rate} fps, {canvas.FrameMs:0.0} ms a frame)");
        }
#else
        await Task.CompletedTask;
        Console.WriteLine("    note: the GPU canvas itself is tested on Windows");
#endif
    }
}
