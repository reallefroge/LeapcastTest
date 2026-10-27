using System.Diagnostics;
using System.Globalization;
using LeapcastStudioAI;

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) failures++; }

string ffmpeg = FfmpegLocator.Find(null) ?? throw new Exception("ffmpeg missing");
string work = Path.Combine(Path.GetTempPath(), "leapcast-tests");
if (Directory.Exists(work)) Directory.Delete(work, true);
Directory.CreateDirectory(work);

// ---------------------------------------------------------------- SmartTuner
Console.WriteLine("SmartTuner");
var enc = new List<ProbedEncoder> { new("h264_nvenc", EncoderTier.Rich), new("libx264", EncoderTier.Rich) };
var s = new StudioSettings();
var plan = SmartTuner.Resolve(s, enc, 8);
Check(plan is { Width: 1600, Height: 900, Fps: 30, CapKbps: 3500, Encoder: "h264_nvenc" }, $"default (no test yet) → {plan.Summary}");
s.UploadMbps = 20; plan = SmartTuner.Resolve(s, enc, 8);
Check(plan is { Width: 1920, Height: 1080, CapKbps: 5000 }, $"20 Mbps → {plan.Summary}");
s.UploadMbps = 4; plan = SmartTuner.Resolve(s, enc, 8);
Check(plan.Width == 1280 && plan.CapKbps == 2500, $"4 Mbps → {plan.Summary}");
s.UploadMbps = 2; plan = SmartTuner.Resolve(s, enc, 8);
Check(plan.Width == 960 && plan.CapKbps <= 1340 && plan.CapKbps >= 800, $"2 Mbps → {plan.Summary}");
s.UploadMbps = 0; s.QualityProfile = "High"; s.Platform = "Twitch"; s.ContentType = "HighMotion"; plan = SmartTuner.Resolve(s, enc, 8);
Check(plan is { Fps: 60, CapKbps: 6000 }, $"high-motion High on Twitch → {plan.Summary}");
plan = SmartTuner.Resolve(new StudioSettings(), new List<ProbedEncoder> { new("h264_nvenc", EncoderTier.Basic), new("libx264", EncoderTier.Rich) }, 16);
Check(plan.Encoder == "libx264", "old NVENC + 16 threads → x264");
plan = SmartTuner.Resolve(new StudioSettings(), new List<ProbedEncoder> { new("h264_qsv", EncoderTier.Rich), new("libx264", EncoderTier.Rich) }, 8);
Check(plan.Encoder == "h264_qsv", "QSV + 8 threads → QSV");
Check(SmartTuner.ParseResolution("1664 × 936") == (1664, 936), "parse resolution with ×");

// ---------------------------------------------------------------- dshow parsing
Console.WriteLine("Device list parsing");
var dshow = DeviceDiscovery.ParseDshowList("""
[dshow @ 0000024c] "Logitech BRIO" (video)
[dshow @ 0000024c]   Alternative name "@device_pnp_\\?\usb#vid_046d"
[dshow @ 0000024c] "OBS Virtual Camera" (none)
[dshow @ 0000024c] "Microphone (Logitech BRIO)" (audio)
""");
Check(dshow.Count(d => d.Kind == "video") == 1 && dshow[0].Name == "Logitech BRIO", "parses camera names");
Check(PipelineBuilder.TeeEscape(@"C:\a|b'c") == @"C:\\a\|b\'c", "tee escaping");

// ---------------------------------------------------------------- encoder probe
Console.WriteLine("Encoder probe (this machine)");
var probed = await EncoderCatalog.ProbeAsync(ffmpeg);
Console.WriteLine("    found: " + string.Join(", ", probed.Select(p => $"{p.Name}/{p.Tier}")));
Check(probed.Any(p => p.Name == "libx264" && p.Tier == EncoderTier.Rich), "x264 rich argument set is valid");
foreach (var cbr in new[] { false, true })
{
    var x = new EncodePlan(1280, 720, 30, 3000, 22, "libx264", EncoderTier.Rich, cbr, true, "faster", "t", Array.Empty<string>());
    var (code, err) = await RunAsync(new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-frames:v", "10", "-vf", "format=nv12" }
        .Concat(EncoderCatalog.BuildVideoArgs(x)).Concat(new[] { "-f", "null", "-" }));
    Check(code == 0, $"x264 cbr={cbr} lowlatency args valid {err}");
}

if (OperatingSystem.IsWindows())
{
    var (dc, de) = await RunAsync(new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "ddagrab=output_idx=0:framerate=30,hwdownload,format=bgra", "-frames:v", "10", "-f", "null", "-" });
    Console.WriteLine(dc == 0 ? "    GPU desktop duplication (ddagrab) works on this machine" : "    note: ddagrab unavailable on this machine (" + de.Split('\n').FirstOrDefault() + ")");
    var ver = await FfmpegLocator.VersionAsync(ffmpeg);
    Console.WriteLine("    FFmpeg under test: " + ver);
}

// ---------------------------------------------------------------- full engine run
Console.WriteLine("Engine end-to-end (lavfi stand-ins for screen + camera, FIFO audio like the Windows pipes)");
string png = Path.Combine(work, "logo.png");
await RunAsync(new[] { "-y", "-f", "lavfi", "-i", "color=c=red@0.6:s=200x80,format=rgba", "-frames:v", "1", png });
// Windows CI: capture the real desktop with GDI (cloud VMs have no GPU for desktop duplication).
string testCapture = OperatingSystem.IsWindows() ? "GDI" : "Auto";
var scene = new StudioScene { Name = "W101" };
scene.Sources.Add(new StudioSource { Name = "Game", Type = SourceTypes.Display, CaptureMethod = testCapture });
scene.Sources.Add(new StudioSource { Name = "Cam", Type = SourceTypes.Camera, DeviceName = "x", Mirror = true });
scene.Sources.Add(new StudioSource { Name = "Logo", Type = SourceTypes.Image, FilePath = png, Anchor = Anchors.TopRight, SizePercent = 0 });
scene.Sources.Add(new StudioSource { Name = "Title", Type = SourceTypes.Text, Text = "hi" });
scene.Sources.Add(new StudioSource { Name = "Desktop", Type = SourceTypes.DesktopAudio });
scene.Sources.Add(new StudioSource { Name = "Mic", Type = SourceTypes.Microphone });

var audio = new FifoAudioBridge(work);
var engine = new StreamEngine(audio, new FakePlatform(png), Path.Combine(work, "data"))
{
    FfmpegPath = ffmpeg,
    // Real capture devices deliver frames in real time; emulate that with paced raw-video FIFOs.
    InputOverride = src => SourceTypes.KindOf(src.Type) switch
    {
        // On Windows CI the "game" is the real desktop via GDI capture; elsewhere a paced pipe.
        SourceKind.Display => OperatingSystem.IsWindows() ? null : VideoFifo.Input(work, 1280, 720, 30),
        SourceKind.Camera => VideoFifo.Input(work, 640, 360, 30),
        _ => null
    }
};
await engine.ProbeEncodersAsync();
int frames = 0; engine.FrameReady += f => { if (f.Length == 640 * 360 * 4) Interlocked.Increment(ref frames); };
var messages = new List<string>(); engine.Message += (_, e) => { lock (messages) messages.Add(e.Text); Console.WriteLine("    engine: " + e.Text); };
EngineStatus? last = null; engine.StatusChanged += st => last = st;

var settings = new StudioSettings { QualityProfile = "DataSaver", Encoder = "libx264", RecordingFolder = Path.Combine(work, "videos"),
    ServerUrl = Path.Combine(work, "live"), ReplaySeconds = 6 };
// The "server" is a local FLV file: Platforms.BuildUrl(server, key) = <work>/live/stream.flv
Directory.CreateDirectory(Path.Combine(work, "live"));
await engine.UpdateAsync(scene, settings, "stream.flv", false);

await engine.SetPreviewAsync(true);
await Task.Delay(3000);
Check(engine.Running && frames > 10, $"preview-only pipeline delivers frames ({frames} in 3 s)");

var clock = Stopwatch.StartNew();
await engine.StartRecordingAsync();      // restart #1 → encode + segments
await Task.Delay(5000);
await engine.StartLiveAsync();           // restart #2 → + RTMP(FLV) output; recording continues across it
await Task.Delay(9000);
Check(last is { Live: true, Recording: true }, $"live + recording status ({last?.Health}, {last?.RecentKbps:0} kbps recent, fps {last?.Fps:0.0})");
Check(last!.RecentKbps > 50 && last.RecentKbps <= 2500 * 1.25 + 200, $"bitrate measured from segments and respects the cap: {last.RecentKbps:0} kbps (cap {last.CapKbps})");
string replay = await engine.SaveReplayAsync();
double wallRecorded = clock.Elapsed.TotalSeconds;
string rec = await engine.StopRecordingAsync();
Console.WriteLine($"    wall-clock time between Record and Stop: {wallRecorded:0.0}s");
await engine.StopLiveAsync();
await engine.StopAllAsync();

var rp = await ProbeAsync(replay);
Check(rp.ok && rp.duration is > 4 and < 12, $"replay saved: {Path.GetFileName(replay)} {rp.duration:0.0}s, {rp.streams}");
var rr = await ProbeAsync(rec);
Check(rr.ok && rr.duration > wallRecorded - 9 && rr.duration < wallRecorded + 1, $"recording spans the restart: {rr.duration:0.0}s of {wallRecorded:0.0}s wall time (restart gaps excluded), {rr.streams}");
var lf = await ProbeAsync(Path.Combine(work, "live", "stream.flv"));
Check(lf.ok && lf.streams.Contains("h264") && lf.streams.Contains("aac") && lf.duration > 6, $"live FLV output valid: {lf.duration:0.0}s, {lf.streams}");
var sync = await StartTimesAsync(rec);
Check(Math.Abs(sync.v - sync.a) < 0.25, $"audio/video start aligned in recording (v {sync.v:0.000}s, a {sync.a:0.000}s)");
var dims = await RunCaptureAsync("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate", "-of", "csv=p=0", rec });
Check(dims.Trim().StartsWith("1280,720,"), $"output is 1280x720@30 ({dims.Trim()})");

// ---------------------------------------------------------------- smart VBR vs CBR on Wizard101-like content
Console.WriteLine("Bandwidth: Smart VBR vs CBR on mostly-static game-like content (cap 3500 kbps, 1600x900@30)");
// mostly static UI with a small animated region, like waiting for a turn in a duel
string staticSrc = "testsrc2=size=1600x900:rate=30,drawbox=x=0:y=0:w=1600:h=760:color=0x204060:t=fill";
foreach (var cbr in new[] { true, false })
{
    var p2 = new EncodePlan(1600, 900, 30, 3500, 22, "libx264", EncoderTier.Rich, cbr, false, "veryfast", "t", Array.Empty<string>());
    string outFile = Path.Combine(work, cbr ? "cbr.ts" : "vbr.ts");
    await RunAsync(new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", staticSrc, "-t", "10", "-vf", "format=nv12" }
        .Concat(EncoderCatalog.BuildVideoArgs(p2)).Concat(new[] { outFile }));
    double kbps = new FileInfo(outFile).Length * 8 / 1000.0 / 10;
    Console.WriteLine($"    {(cbr ? "CBR      " : "Smart VBR")}: {kbps,6:0} kbps");
    if (!cbr) Check(kbps < 3500 * 0.6, "smart VBR spends much less than the cap on static content");
}

// ---------------------------------------------------------------- bandwidth tester parsing (local file instead of Twitch)
Console.WriteLine("Bandwidth tester measurement loop");
var bwArgs = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:2", "-y",
    "-re", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30,noise=alls=40:allf=t+u", "-t", "5",
    "-c:v", "libx264", "-preset", "ultrafast", "-b:v", "4000k", "-minrate", "4000k", "-maxrate", "4000k", "-bufsize", "4000k",
    "-x264-params", "nal-hrd=cbr", "-f", "flv", Path.Combine(work, "bw.flv") };
var bw = await BandwidthTester.MeasureAsync(ffmpeg, bwArgs, 4000, 5, null, default);
Check(bw.MeasuredKbps > 3000 && bw.MeasuredKbps < 5500, $"measures ~4 Mbps test stream: {bw.MeasuredKbps:0} kbps — {bw.Detail}");

// ---------------------------------------------------------------- health heuristic on a light load
Console.WriteLine("Health monitor on a light scene (screen + mic)");
var light = new StudioScene { Name = "light" };
light.Sources.Add(new StudioSource { Name = "Game", Type = SourceTypes.Display, CaptureMethod = testCapture });
light.Sources.Add(new StudioSource { Name = "Mic", Type = SourceTypes.Microphone });
await engine.UpdateAsync(light, WithEncoder(settings, "libx264"), "stream.flv", false);
await engine.SetReplayBufferAsync(true);
await Task.Delay(12000);
Check(last is { Running: true } && !last.Health.StartsWith("Struggling"), $"no false 'struggling' alarm: {last?.Health}, fps {last?.Fps:0.0}, speed {last?.Speed:0.00}");
await engine.SetReplayBufferAsync(false);

Console.WriteLine("Seamless scene transitions (no restart, real crossfade)");
string redPng = Path.Combine(work, "red.png");
await RunAsync(new[] { "-y", "-f", "lavfi", "-i", "color=c=red:s=1280x720", "-frames:v", "1", redPng });
var sceneA = new StudioScene { Name = "Gameplay" };
// "Gameplay" is a paced synthetic feed here (known colours) so the crossfade can be measured on any machine.
var gameSrc = new StudioSource { Name = "Game", Type = SourceTypes.Camera, DeviceName = "synthetic", Anchor = Anchors.Fill, CameraDenoise = false };
sceneA.Sources.Add(gameSrc);
sceneA.Sources.Add(new StudioSource { Name = "Mic", Type = SourceTypes.Microphone });
var sceneB = new StudioScene { Name = "BRB" };
sceneB.Sources.Add(new StudioSource { Name = "BRB card", Type = SourceTypes.Image, FilePath = redPng, Anchor = Anchors.Fill });
sceneB.Sources.Add(new StudioSource { Name = "Mic", Type = SourceTypes.Microphone, VolumeDb = -12 }); // same device, quieter
var both = new List<StudioScene> { sceneA, sceneB };
var tset = WithEncoder(settings, "libx264"); tset.Transition = "Fade"; tset.TransitionMs = 600;
var samples = new List<(double t, int red)>();
var tclock = Stopwatch.StartNew();
engine.FrameReady += f =>
{
    // BGRA: redness = R - G at the frame centre
    int i = (180 * 640 + 320) * 4;
    lock (samples) samples.Add((tclock.Elapsed.TotalSeconds, f[i + 2] - f[i + 1]));
};
await engine.UpdateAsync(both, sceneA, tset, "stream.flv", false);
await engine.SetReplayBufferAsync(true);
await Task.Delay(5000);
int starts = engine.PipelineStarts;
double switchAt = tclock.Elapsed.TotalSeconds;
await engine.UpdateAsync(both, sceneB, tset, "stream.flv", false);
await Task.Delay(3500);
List<(double t, int red)> after;
lock (samples) after = samples.Where(x => x.t >= switchAt - 0.5).ToList();
int mids = after.Count(x => x.red is > 40 and < 215);
double maxGap = after.Zip(after.Skip(1), (a, b) => b.t - a.t).DefaultIfEmpty(0).Max();
Check(engine.PipelineStarts == starts && engine.Running, $"switching scenes did not restart the pipeline (starts {starts} → {engine.PipelineStarts})");
Check(after.First().red < 30 && after.Last().red > 220, $"preview went from gameplay (red {after.First().red}) to BRB (red {after.Last().red})");
Check(mids >= 3, $"crossfade has {mids} in-between frames (fade, not a cut)");
Check(maxGap < 0.4, $"no blank/frozen gap during the switch (largest frame gap {maxGap * 1000:0} ms)");
string ff = File.ReadAllText(Path.Combine(work, "data", "logs", "ffmpeg-last.log"));
Check(!ff.Contains("ret:-"), "ffmpeg accepted every transition command");

tset.Transition = "Cut";
switchAt = tclock.Elapsed.TotalSeconds;
await engine.UpdateAsync(both, sceneA, tset, "stream.flv", false);
await Task.Delay(3000);
lock (samples) after = samples.Where(x => x.t >= switchAt).ToList();
mids = after.Count(x => x.red is > 40 and < 215);
Check(engine.PipelineStarts == starts && after.Last().red < 30 && mids <= 1, $"cut transition back is instant ({mids} in-between frames) and still no restart");
var rep2 = await engine.SaveReplayAsync();
var rp2 = await ProbeAsync(rep2);
Check(rp2.ok && rp2.duration > 5, $"replay spanning both transitions is continuous: {rp2.duration:0.0}s");

gameSrc.CropLeft = 10; // editing a source changes the graph → rebuild
await engine.UpdateAsync(both, sceneA, tset, "stream.flv", false);
await Task.Delay(1500);
Check(engine.PipelineStarts == starts + 1, "editing a source rebuilds the pipeline (expected)");
await engine.SetReplayBufferAsync(false);
await engine.UpdateAsync(new[] { light }, light, WithEncoder(settings, "libx264"), "stream.flv", false);

Console.WriteLine("Auto-reconnect when the stream process dies mid-broadcast");
await engine.StartLiveAsync();
await Task.Delay(10000);
if (OperatingSystem.IsWindows()) foreach (var pr in Process.GetProcessesByName("ffmpeg")) { try { pr.Kill(); } catch { } }
else Process.Start("pkill", new[] { "-f", "-9", "f=flv:onfail=abort" }).WaitForExit();
await Task.Delay(1500);
bool sawReconnect; lock (messages) sawReconnect = messages.Any(m => m.Contains("Reconnecting"));
Check(sawReconnect && engine.Live, "drop detected, still live, reconnect scheduled");
await Task.Delay(7000);
Check(engine.Running && engine.Live && last is { Live: true, Running: true }, $"pipeline back up after reconnect ({last?.Health})");
await engine.StopAllAsync();
Check(!engine.Running && !engine.Live, "stop all leaves nothing running");

engine.Dispose();
Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------- helpers
static StudioSettings WithEncoder(StudioSettings s, string e) { var c = s.Clone(); c.Encoder = e; c.QualityProfile = "DataSaver"; return c; }
async Task<(int, string)> RunAsync(IEnumerable<string> args) => await RunToolAsync(ffmpeg, args);
async Task<(int, string)> RunToolAsync(string exe, IEnumerable<string> args)
{
    var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var e = p.StandardError.ReadToEndAsync(); var o = p.StandardOutput.ReadToEndAsync();
    await p.WaitForExitAsync();
    return (p.ExitCode, (await e).Trim());
}
async Task<string> RunCaptureAsync(string exe, IEnumerable<string> args)
{
    var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEndAsync(); _ = p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    return await o;
}
async Task<(bool ok, double duration, string streams)> ProbeAsync(string file)
{
    if (!File.Exists(file)) return (false, 0, "missing");
    string d = await RunCaptureAsync("ffprobe", new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file });
    string c = await RunCaptureAsync("ffprobe", new[] { "-v", "error", "-show_entries", "stream=codec_name", "-of", "csv=p=0", file });
    bool ok = double.TryParse(d.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur);
    return (ok, dur, string.Join("+", c.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())));
}
async Task<(double v, double a)> StartTimesAsync(string file)
{
    double St(string sel)
    {
        var t = RunCaptureAsync("ffprobe", new[] { "-v", "error", "-select_streams", sel, "-show_entries", "stream=start_time", "-of", "csv=p=0", file }).Result;
        return double.Parse(t.Trim(), CultureInfo.InvariantCulture);
    }
    return await Task.Run(() => (St("v:0"), St("a:0")));
}

/// <summary>
/// A pipe that a writer thread fills at a fixed real-time rate, like a capture device.
/// Linux/macOS: mkfifo. Windows: a named pipe (the same transport the app uses for audio).
/// </summary>
static class PacedPipe
{
    public static string Create(string dir, string ext, Func<long, byte[]> chunk, double chunksPerSecond, CancellationToken ct = default)
    {
        string name = $"leapcast_test_{Guid.NewGuid():N}";
        if (OperatingSystem.IsWindows())
        {
            var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.Out, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 0, 1 << 16);
            new Thread(() =>
            {
                try { server.WaitForConnection(); Pump(server, chunk, chunksPerSecond, ct); }
                catch { }
                finally { server.Dispose(); }
            }) { IsBackground = true }.Start();
            return @"\\.\pipe\" + name;
        }
        string path = Path.Combine(dir, name + ext);
        Process.Start("mkfifo", path).WaitForExit();
        new Thread(() =>
        {
            try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Write); Pump(fs, chunk, chunksPerSecond, ct); }
            catch { }
            finally { try { File.Delete(path); } catch { } }
        }) { IsBackground = true }.Start();
        return path;
    }

    private static void Pump(Stream s, Func<long, byte[]> chunk, double rate, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long n = 0;
        while (!ct.IsCancellationRequested)
        {
            long due = (long)(sw.Elapsed.TotalSeconds * rate);
            if (n <= due) { s.Write(chunk(n)); n++; }
            else Thread.Sleep(3);
        }
    }
}

static class VideoFifo
{
    /// <summary>Raw yuv420p frames (moving grey stripes) at exactly fps.</summary>
    public static InputSpec Input(string dir, int w, int h, int fps)
    {
        int size = w * h * 3 / 2;
        var frames = Enumerable.Range(0, 30).Select(i =>
        {
            var f = new byte[size];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    f[y * w + x] = (byte)(((x + i * 8) / 32 % 2 == 0) ? 200 : 40);
            Array.Fill(f, (byte)128, w * h, size - w * h);
            return f;
        }).ToArray();
        string path = PacedPipe.Create(dir, ".yuv", n => frames[n % frames.Length], fps);
        return new InputSpec(new[] { "-f", "rawvideo", "-pix_fmt", "yuv420p", "-video_size", $"{w}x{h}", "-framerate", fps.ToString(), "-thread_queue_size", "64", "-i", path }, true);
    }
}

sealed class FakePlatform(string png) : IPlatformServices
{
    public string? RenderText(StudioSource source, int outputHeight) => png;
    public (int X, int Y, int W, int H)? MonitorBounds(int index) => null;
}

/// <summary>Stand-in for the WASAPI bridge: paced 48 kHz stereo float tones over FIFOs / named pipes.</summary>
sealed class FifoAudioBridge(string dir) : IAudioBridge
{
    private List<StudioSource> _sources = new();
    private CancellationTokenSource? _cts;
    private readonly List<string> _paths = new();
    public void Configure(IReadOnlyList<StudioSource> audioSources) => _sources = audioSources.ToList();
    public IReadOnlyDictionary<Guid, string> OpenPipes()
    {
        _cts = new CancellationTokenSource();
        var map = new Dictionary<Guid, string>();
        int k = 0;
        foreach (var s in _sources)
        {
            double freq = 440 + 220 * k++;
            double phase = 0;
            var buf = new byte[480 * 8];
            map[s.Id] = PacedPipe.Create(dir, ".f32", _ =>
            {
                for (int i = 0; i < 480; i++)
                {
                    float v = (float)(0.2 * Math.Sin(phase)); phase += 2 * Math.PI * freq / 48000;
                    BitConverter.TryWriteBytes(buf.AsSpan(i * 8), v);
                    BitConverter.TryWriteBytes(buf.AsSpan(i * 8 + 4), v);
                }
                return buf;
            }, 100, _cts.Token);
        }
        return map;
    }
    public void ClosePipes()
    {
        _cts?.Cancel();
        foreach (var p in _paths) { try { File.Delete(p); } catch { } }
        _paths.Clear();
    }
    public double LevelDb(Guid sourceId) => -20;
    public void Dispose() => ClosePipes();
}
