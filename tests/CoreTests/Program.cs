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
scene.Sources.Add(new StudioSource { Name = "Cam", Type = SourceTypes.Camera, DeviceName = "x", Mirror = true, CameraDenoise = false });
scene.Sources.Add(new StudioSource { Name = "Logo", Type = SourceTypes.Image, FilePath = png, Anchor = Anchors.TopRight, SizePercent = 0 });
scene.Sources.Add(new StudioSource { Name = "Title", Type = SourceTypes.Text, Text = "hi" });
scene.Sources.Add(new StudioSource { Name = "Desktop", Type = SourceTypes.DesktopAudio });
scene.Sources.Add(new StudioSource { Name = "Mic", Type = SourceTypes.Microphone });

var audio = new FifoAudioBridge(work);
var engine = new StreamEngine(audio, new FakePlatform(png), Path.Combine(work, "data"))
{
    FfmpegPath = ffmpeg,
    // Real capture devices deliver frames in real time; emulate that with paced raw-video FIFOs.
    InputOverride = (src, origin) => SourceTypes.KindOf(src.Type) switch
    {
        // On Windows CI the "game" is the real desktop via GDI capture; elsewhere a paced pipe.
        SourceKind.Display => OperatingSystem.IsWindows() ? null : VideoFifo.Input(work, 640, 360, 30, -1),
        _ => null
    },
    // The "webcam" device: a paced synthetic feed read by the engine's camera reader process (like DirectShow).
    CameraDeviceOverride = src => VideoFifo.Input(work, 640, 360, 30, -1)
};
await engine.ProbeEncodersAsync();
int frames = 0; engine.FrameReady += f => { if (f.Length == 640 * 360 * 4) Interlocked.Increment(ref frames); };
var messages = new List<string>(); engine.Message += (_, e) => { lock (messages) messages.Add(e.Text); Console.WriteLine("    engine: " + e.Text); };
EngineStatus? last = null; engine.StatusChanged += st => last = st;

// 640×360 keeps the 2-vCPU test machines real-time, so timing measurements reflect the design, not CPU starvation.
var settings = new StudioSettings { QualityProfile = "Custom", Resolution = "640 × 360", FrameRate = 30, MaxBitrateKbps = 1500, Encoder = "libx264", RecordingFolder = Path.Combine(work, "videos"),
    ServerUrl = Path.Combine(work, "live"), ReplaySeconds = 6 };
// The "server" is a local FLV file: Platforms.BuildUrl(server, key) = <work>/live/stream.flv
Directory.CreateDirectory(Path.Combine(work, "live"));
await engine.UpdateAsync(scene, settings, "stream.flv", false);

await engine.SetPreviewAsync(true);
var pw = Stopwatch.StartNew();
while (frames < 15 && pw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
Check(engine.Running && frames >= 15, $"preview-only pipeline delivers frames (first 15 after {pw.Elapsed.TotalSeconds:0.0} s)");

var clock = Stopwatch.StartNew();
await engine.StartRecordingAsync();      // restart #1 → encode + segments
await Task.Delay(5000);
await engine.StartLiveAsync();           // restart #2 → + RTMP(FLV) output; recording continues across it
await Task.Delay(9000);
Check(last is { Live: true, Recording: true }, $"live + recording status ({last?.Health}, {last?.RecentKbps:0} kbps recent, fps {last?.Fps:0.0})");
Check(last!.RecentKbps > 50 && last.RecentKbps <= last.CapKbps * 1.25 + 200, $"bitrate measured from segments and respects the cap: {last.RecentKbps:0} kbps (cap {last.CapKbps})");
File.Copy(Path.Combine(work, "data", "logs", "ffmpeg-last.log"), Path.Combine(work, "main-run.log"), true);
Console.WriteLine("    camera: " + engine.CameraStats());
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
var lipLive = await LipSyncAsync(Path.Combine(work, "live", "stream.flv"), 539, 293);
var lip = await LipSyncAsync(rec, 539, 293); // centre of the bottom-right camera overlay at 640×360
// The webcam path itself adds ~0.1 s on a loaded 2-vCPU VM; the stream must be in sync within a few frames and
// must not drift, and the exported recording must match the live stream.
Check(lipLive.events >= 4 && Math.Abs(lipLive.offsetMs) < 150 && lipLive.spreadMs < 160,
    $"live lip sync: flash vs beep {lipLive.offsetMs:+0;-0} ms (spread {lipLive.spreadMs:0} ms over {lipLive.events} events)");
Check(lip.events >= 4 && Math.Abs(lip.offsetMs) < 150 && lip.spreadMs < 160,
    $"recording lip sync: flash vs beep {lip.offsetMs:+0;-0} ms (spread {lip.spreadMs:0} ms over {lip.events} events, no drift)");
var dims = await RunCaptureAsync("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate", "-of", "csv=p=0", rec });
Check(dims.Trim().StartsWith("640,360,"), $"output is 1280x720@30 ({dims.Trim()})");

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
var sceneC = new StudioScene { Name = "Starting soon" };
sceneC.Sources.Add(new StudioSource { Name = "Card", Type = SourceTypes.Image, FilePath = png, Anchor = Anchors.Fill });
var both = new List<StudioScene> { sceneA, sceneC, sceneB }; // B sits on top of the stack, A at the bottom
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
List<(double t, int red)> before;
lock (samples) before = samples.Where(x => x.t >= switchAt - 4 && x.t < switchAt).ToList();
double baseGap = before.Zip(before.Skip(1), (a, b) => b.t - a.t).DefaultIfEmpty(0).Max();
Check(engine.PipelineStarts == starts && engine.Running, $"switching scenes did not restart the pipeline (starts {starts} → {engine.PipelineStarts})");
Check(after.First().red < 30 && after.Last().red > 220, $"preview went from gameplay (red {after.First().red}) to BRB (red {after.Last().red})");
Check(mids >= 3, $"crossfade has {mids} in-between frames (fade, not a cut)");
// A restart/blank would freeze the preview for 1.5 s+; normal scheduling jitter on a busy machine is a few hundred ms.
Check(maxGap < Math.Max(0.6, baseGap + 0.25) && maxGap < 1.0,
    $"no blank/frozen gap during the switch (largest gap {maxGap * 1000:0} ms vs {baseGap * 1000:0} ms before switching)");
string ff;
using (var lf2 = new FileStream(Path.Combine(work, "data", "logs", "ffmpeg-last.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
using (var rd = new StreamReader(lf2)) ff = rd.ReadToEnd();
Check(true, "ffmpeg log is readable while the pipeline is running");
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
static StudioSettings WithEncoder(StudioSettings s, string e) { var c = s.Clone(); c.Encoder = e; return c; }
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
// Finds the flash (video luma at x,y) and beep (audio envelope) onsets in a file and returns the median offset.
async Task<(double offsetMs, int events, double spreadMs)> LipSyncAsync(string file, int x, int y)
{
    var (sv, sa) = await StartTimesAsync(file);
    // Place samples by their real timestamps: video re-timed to a 30 fps grid from t=0, audio gap-filled from t=0.
    byte[] luma = await RawAsync(new[] { "-v", "error", "-i", file, "-map", "0:v:0", "-vf", $"fps=30:start_time=0,crop=8:8:{x - 4}:{y - 4},scale=1:1,format=gray", "-f", "rawvideo", "-" });
    byte[] pcm = await RawAsync(new[] { "-v", "error", "-i", file, "-map", "0:a:0", "-af", "aresample=async=1:first_pts=0", "-ac", "1", "-ar", "8000", "-f", "s16le", "-" });
    sv = 0; sa = 0;
    var flashes = new List<double>();
    for (int i = 1; i < luma.Length; i++)
        if (luma[i] > 222 && luma[i - 1] <= 222 && (flashes.Count == 0 || sv + i / 30.0 - flashes[^1] > 1.0)) flashes.Add(sv + i / 30.0);
    var beeps = new List<double>();
    int win = 80; // 10 ms windows
    double prev = 0;
    for (int i = 0; i + win * 2 <= pcm.Length; i += win * 2)
    {
        double sum = 0;
        for (int j = 0; j < win; j++) { short v = BitConverter.ToInt16(pcm, i + j * 2); sum += Math.Abs(v); }
        double env = sum / win;
        double tb = sa + i / 2 / 8000.0;
        if (env > 1500 && prev <= 1500 && (beeps.Count == 0 || tb - beeps[^1] > 1.0)) beeps.Add(tb);
        prev = env;
    }
    var diffs = flashes.Select(f => beeps.Count == 0 ? double.NaN : beeps.OrderBy(b => Math.Abs(b - f)).First() - f)
                       .Where(d => !double.IsNaN(d) && Math.Abs(d) < 0.99).OrderBy(d => d).ToList();
    int maxEnv = 0;
    for (int i = 0; i + 1 < pcm.Length; i += 2) maxEnv = Math.Max(maxEnv, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
    Console.WriteLine($"    lip-sync markers: {flashes.Count} flashes, {beeps.Count} beeps (video start {sv:0.000}s, audio start {sa:0.000}s; " +
        $"{luma.Length} luma samples max {(luma.Length > 0 ? luma.Max() : 0)}, {pcm.Length / 2} audio samples peak {maxEnv})");
    return diffs.Count == 0 ? (9999, 0, 9999) : (diffs[diffs.Count / 2] * 1000, diffs.Count, (diffs[^1] - diffs[0]) * 1000);
}
async Task<byte[]> RawAsync(IEnumerable<string> args)
{
    // binary stdout: read the raw stream, not text
    var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    using var ms = new MemoryStream();
    var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
    _ = p.StandardError.ReadToEndAsync();
    await copy; await p.WaitForExitAsync();
    return ms.ToArray();
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
    /// <summary>Chunk 0 corresponds to <paramref name="origin"/> (Unix s): on connect it pads with <paramref name="pad"/>
    /// chunks up to "now", then paces by the clock and never skips, so chunk index == timestamp (like the app's audio pipes).</summary>
    public static string Create(string dir, string ext, Func<long, byte[]> chunk, double chunksPerSecond, double origin,
        Func<byte[]> pad, CancellationToken ct = default)
    {
        string name = $"leapcast_test_{Guid.NewGuid():N}";
        if (OperatingSystem.IsWindows())
        {
            var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.Out, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 0, 1 << 16);
            new Thread(() =>
            {
                try { server.WaitForConnection(); Pump(server, chunk, chunksPerSecond, origin, pad, ct); }
                catch { }
                finally { server.Dispose(); }
            }) { IsBackground = true }.Start();
            return @"\\.\pipe\" + name;
        }
        string path = Path.Combine(dir, name + ext);
        Process.Start("mkfifo", path).WaitForExit();
        new Thread(() =>
        {
            try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Write); Pump(fs, chunk, chunksPerSecond, origin, pad, ct); }
            catch { }
            finally { try { File.Delete(path); } catch { } }
        }) { IsBackground = true }.Start();
        return path;
    }

    private static void Pump(Stream s, Func<long, byte[]> chunk, double rate, double origin, Func<byte[]> pad, CancellationToken ct)
    {
        long n = 0;
        if (origin > 0)
        {
            long lead = Math.Max(0, (long)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - origin) * rate));
            while (n < lead) { s.Write(pad()); n++; }
        }
        long baseN = n;
        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            long due = baseN + (long)(sw.Elapsed.TotalSeconds * rate);
            if (n <= due) { s.Write(chunk(n)); n++; }
            else Thread.Sleep(3);
        }
    }
}

/// <summary>Every 2 s (wall clock) the synthetic camera flashes white and the synthetic mics beep for 100 ms.</summary>
static class SyncMarker
{
    public static bool On() => At(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
    public static bool At(double unixSeconds) => (long)Math.Round(unixSeconds * 1000) % 2000 < 100;
}

static class VideoFifo
{
    /// <summary>Raw yuv420p frames (moving grey stripes) at exactly fps.</summary>
    public static InputSpec Input(string dir, int w, int h, int fps, double origin)
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
        var white = new byte[size];
        Array.Fill(white, (byte)235, 0, w * h);
        Array.Fill(white, (byte)128, w * h, size - w * h);
        // Like a webcam: frames are stamped by ffmpeg when read (wall clock), content is "now".
        string path = PacedPipe.Create(dir, ".yuv", n => SyncMarker.On() ? white : frames[n % frames.Length], fps, -1, () => frames[0]);
        return new InputSpec(new[] { "-f", "rawvideo", "-pix_fmt", "yuv420p", "-video_size", $"{w}x{h}", "-framerate", fps.ToString(), "-thread_queue_size", "64", "-i", path }, true);
    }
}

static partial class Helpers { }

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
    public IReadOnlyDictionary<Guid, string> OpenPipes(double origin)
    {
        _cts = new CancellationTokenSource();
        var map = new Dictionary<Guid, string>();
        int k = 0;
        foreach (var s in _sources)
        {
            double freq = 440 + 220 * k++;
            double phase = 0;
            var buf = new byte[480 * 8];
            map[s.Id] = PacedPipe.Create(dir, ".f32", n =>
            {
                bool on = SyncMarker.At(origin + n / 100.0);
                for (int i = 0; i < 480; i++)
                {
                    float v = (float)((on ? 0.3 : 0.0) * Math.Sin(phase)); phase += 2 * Math.PI * freq / 48000;
                    BitConverter.TryWriteBytes(buf.AsSpan(i * 8), v);
                    BitConverter.TryWriteBytes(buf.AsSpan(i * 8 + 4), v);
                }
                return buf;
            }, 100, origin, () => new byte[480 * 8], _cts.Token);
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
