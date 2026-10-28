using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
plan = SmartTuner.Resolve(new StudioSettings(), new List<ProbedEncoder> { new("h264_qsv", EncoderTier.Rich), new("libx264", EncoderTier.Rich) }, 12);
Check(plan.Encoder == "h264_qsv", "laptop with Quick Sync + 12 threads → Quick Sync (game keeps the CPU)");
Check(SmartTuner.ParseResolution("1664 × 936") == (1664, 936), "parse resolution with ×");

// ---------------------------------------------------------------- multistream budget + layout math
Console.WriteLine("Multistream budget and layout");
var mset = new StudioSettings { UploadMbps = 10 };
var twitchO = new StreamOutput { Name = "Twitch", Service = "Twitch" };
var ytO = new StreamOutput { Name = "YouTube", Service = "YouTube" };
var ttO = new StreamOutput { Name = "TikTok", Service = "TikTok", Canvas = "Vertical" };
mset.Outputs = new() { twitchO, ytO, ttO };
var ps = OutputBudget.Resolve(mset, mset.Outputs, true, enc, 8);
Check(ps.UploadKbps <= 10000 * 0.75 + 50 && ps.Main.CapKbps <= 6000 && ps.Vertical is { Width: 720, Height: 1280 },
    $"3 destinations on 10 Mbps fit 75% of the upload: main {ps.Main.CapKbps} kbps, vertical {ps.Vertical?.CapKbps} kbps, total {ps.UploadKbps} kbps");
var solo = OutputBudget.Resolve(mset, new[] { twitchO }, false, enc, 8);
Check(solo.Main.CapKbps > ps.Main.CapKbps, $"a single destination gets more bitrate ({solo.Main.CapKbps} vs {ps.Main.CapKbps} kbps)");
var noTest = OutputBudget.Resolve(new StudioSettings(), new[] { twitchO, ytO }, false, enc, 8);
Check(noTest.Main.CapKbps <= 6000, $"Twitch + YouTube share an encode capped by Twitch's limit ({noTest.Main.CapKbps} kbps)");
var ownO = new StreamOutput { Name = "YouTube HQ", Service = "YouTube", OwnBitrateKbps = 9000 };
var withOwn = OutputBudget.Resolve(new StudioSettings(), new[] { twitchO, ownO }, false, enc, 8);
Check(withOwn.Own.TryGetValue(ownO.Id, out var ownPlan) && ownPlan.CapKbps == 9000, "an output with its own encode gets its own bitrate");

var lcam = new StudioSource { Type = SourceTypes.Camera };
var lgame = new StudioSource { Type = SourceTypes.Display };
var camR = LayoutMath.Vertical(lcam, 720, 1280, 16.0 / 9, true);
var gameR = LayoutMath.Vertical(lgame, 720, 1280, 16.0 / 9, true);
Check(camR.Bottom <= gameR.Y && gameR.Bottom <= 1280 && camR.Y >= 0 && Math.Abs(gameR.W - 720) < 1,
    $"vertical auto layout: camera on top ({camR.Y:0}-{camR.Bottom:0}), game full width below ({gameR.Y:0}-{gameR.Bottom:0})");
var fpScene = new StudioScene();
var fpCard = new StudioSource { Type = SourceTypes.Image, FilePath = "x.png", Anchor = Anchors.TopRight, SizePercent = 25 };
fpScene.Sources.Add(new StudioSource { Type = SourceTypes.Display });
fpScene.Sources.Add(fpCard);
string fp0 = PipelineBuilder.LayoutFingerprint(new[] { fpScene }, 1600, 900, (720, 1280));
var r0 = LayoutMath.Horizontal(fpCard, 1600, 900, 2.0);
fpCard.Anchor = Anchors.Free; fpCard.X = r0.X / 1600; fpCard.Y = r0.Y / 900; fpCard.W = r0.W / 1600; // what dragging does
string fp1 = PipelineBuilder.LayoutFingerprint(new[] { fpScene }, 1600, 900, (720, 1280));
fpCard.X = 0.1; fpCard.Y = 0.7;
var fpGame = fpScene.Sources[0]; fpGame.VAuto = false; fpGame.VX = 0; fpGame.VY = 0.3; fpGame.VW = 1; // dragged on the vertical canvas
string fp2 = PipelineBuilder.LayoutFingerprint(new[] { fpScene }, 1600, 900, (720, 1280));
fpCard.W = 0.4;
string fp3 = PipelineBuilder.LayoutFingerprint(new[] { fpScene }, 1600, 900, (720, 1280));
Check(fp0 == fp1 && fp1 == fp2 && fp2 != fp3, $"moving (or un-anchoring at the same size) doesn't rebuild; resizing does ({fp0 == fp1}, {fp1 == fp2}, {fp2 != fp3})");
var dupA = new StudioScene { Name = "Game" };
var dupB = new StudioScene { Name = "BRB" };
foreach (var sc in new[] { dupA, dupB })
{
    sc.Sources.Add(new StudioSource { Type = SourceTypes.Display, MonitorIndex = 0 });
    sc.Sources.Add(new StudioSource { Type = SourceTypes.Camera, DeviceName = "cam" });
}
var dupSpec = PipelineBuilder.Build(new PipelineRequest
{
    Scenes = new[] { dupA, dupB }, ActiveSceneIndex = 0, Plan = new EncodePlan(1600, 900, 30, 3500, 22, "libx264", EncoderTier.Rich, false, false, "veryfast", "t", Array.Empty<string>()),
    InputOverride = (src, o) => new InputSpec(new[] { "-f", "lavfi", "-i", "testsrc2=s=800x500:r=30" }, false), IsWindows = true,
});
int fillScales = System.Text.RegularExpressions.Regex.Matches(dupSpec.FilterGraph, "force_original_aspect_ratio=increase").Count;
Check(fillScales == 1, $"a game used by two scenes is scaled once and shared ({fillScales} scale chain(s))");
Check(dupSpec.FilterGraph.Contains("overlay@stk1=format=yuv420:eof_action=repeat:enable=0") && dupSpec.SceneOverlays[1].Contains("overlay@stk1"),
    "the hidden scene isn't blended until it's switched to");
var tcmds = PipelineBuilder.TransitionCommands(dupSpec, 0, 1, 10, 0.5, out int tcrit);
Check(tcmds[0] == "overlay@stk1 -1 enable 1" && tcrit < tcmds.Count + 1, $"switching turns the incoming scene on first ({string.Join(" | ", tcmds)})");
var tback = PipelineBuilder.TransitionCommands(dupSpec, 1, 0, 20, 0.5, out int tcrit2);
Check(tback.Last().StartsWith("overlay@stk1 -1 enable lt(t,20.6") && tcrit2 == tback.Count - 1, "the outgoing scene is switched off after its fade");
Check(PipelineBuilder.FillChain(new StudioSource(), 1920, 1080, 30, true).Contains("increase") && LayoutMath.FillRect(new StudioSource(), 1920, 1080, 1.6) is { X: 0, W: 1920 } fr && fr.H > 1080,
    "fill mode Crop covers the canvas (16:10 game: full width, edges trimmed top/bottom)");

// the user's own layout: a game window dragged to free placement over the blank canvas
var freeScene = new StudioScene { Name = "free" };
freeScene.Sources.Add(new StudioSource { Type = SourceTypes.Window, WindowTitle = "Wizard101", Anchor = Anchors.Free, W = 0.9875, Y = -0.05 });
var freeSpec = PipelineBuilder.Build(new PipelineRequest
{
    Scenes = new[] { freeScene }, Plan = new EncodePlan(1920, 1080, 30, 3500, 22, "libx264", EncoderTier.Rich, false, false, "veryfast", "t", Array.Empty<string>()),
    InputOverride = (src, o) => new InputSpec(new[] { "-f", "lavfi", "-i", "testsrc2=s=1280x800:r=30" }, true), IsWindows = true,
});
Check(!string.Join(" ", freeSpec.Args).Contains("-re -f lavfi -i color") && freeSpec.FilterGraph.Contains("color=c=0x0E1016:s=1920x1080"),
    "the blank canvas behind a live game is generated in-graph (no rate-limited input that lags after hiccups)");

var snapped = LayoutMath.Snap(new LayoutRect(745, 7, 100, 50), 1600, 900, 12);
Check(snapped == (750, 0), $"snaps to the centre line and the top edge ({snapped})");

// ---------------------------------------------------------------- webcam modes
var camModes = CameraModes.Parse("""
[dshow @ 000001] vcodec=mjpeg  min s=1280x720 fps=5 max s=1280x720 fps=30
[dshow @ 000001] vcodec=mjpeg  min s=640x480 fps=5 max s=640x480 fps=30
[dshow @ 000001]   pixel_format=yuyv422  min s=1280x720 fps=5 max s=1280x720 fps=10 (tv, bt470bg/bt709/unknown, topleft)
[dshow @ 000001]   pixel_format=yuyv422  min s=640x360 fps=5 max s=640x360 fps=30
[dshow @ 000001]   pixel_format=yuyv422  min s=640x480 fps=5 max s=640x480 fps=30
""");
var smallCam = CameraModes.Best(camModes, 460, 30);
var bigCam = CameraModes.Best(camModes, 662, 30);
Check(camModes.Count == 5 && smallCam is { Width: 640, Height: 360, Compressed: false } && bigCam is { Width: 1280, Format: "mjpeg" },
    $"webcam mode: smallest sharp-enough mode at the stream's 30 FPS ({smallCam}; {bigCam} when the vertical canvas needs it bigger)");
var slowCam = CameraModes.Best(CameraModes.Parse("vcodec=mjpeg  min s=1280x720 fps=5 max s=1280x720 fps=24\npixel_format=yuyv422  min s=640x480 fps=5 max s=640x480 fps=30"), 460, 30);
Check(slowCam is { Width: 640, MaxFps: 30 }, $"a camera that only does 24 FPS at 720p runs at 30 FPS in a smaller mode instead ({slowCam})");

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
int frames = 0; engine.FrameReady += f => { var pf = engine.PreviewFrame; if (f.Length == pf.Width * pf.Height * 4) Interlocked.Increment(ref frames); };
var messages = new List<string>(); engine.Message += (_, e) => { lock (messages) messages.Add(e.Text); Console.WriteLine("    engine: " + e.Text); };
EngineStatus? last = null; engine.StatusChanged += st => last = st;

// Multistream destinations are local RTMP servers (ffmpeg -listen), one per output, like Twitch + YouTube.
int portA = RtmpServer.FreePort(), portB = RtmpServer.FreePort(), portV = RtmpServer.FreePort();
var outA = new StreamOutput { Name = "Twitch test", Service = "Custom", ServerUrl = $"rtmp://127.0.0.1:{portA}/live" };
var outB = new StreamOutput { Name = "YouTube test", Service = "Custom", ServerUrl = $"rtmp://127.0.0.1:{portB}/live" };
var outV = new StreamOutput { Name = "TikTok test", Service = "Custom", ServerUrl = $"rtmp://127.0.0.1:{portV}/live", Canvas = "Vertical", Enabled = false };
var keys = new Dictionary<Guid, string> { [outA.Id] = "a", [outB.Id] = "b", [outV.Id] = "v" };
engine.KeyResolver = o => keys.TryGetValue(o.Id, out var k) ? k : "";
OutputStatus? Out(StreamOutput o) => engine.OutputStatuses().FirstOrDefault(x => x.Id == o.Id);
string liveDir = Path.Combine(work, "live");
Directory.CreateDirectory(liveDir);

// 640×360 keeps the 2-vCPU test machines real-time, so timing measurements reflect the design, not CPU starvation.
var settings = new StudioSettings { QualityProfile = "Custom", Resolution = "640 × 360", FrameRate = 30, MaxBitrateKbps = 1500, Encoder = "libx264", RecordingFolder = Path.Combine(work, "videos"),
    ReplaySeconds = 6, Outputs = new() { outA, outB, outV } };
await engine.UpdateAsync(scene, settings, false);
async Task SyncSection()
{
    Console.WriteLine("Camera delay: the clap test measures it and the offset fixes it");
    var sscene = new StudioScene { Name = "sync" };
    sscene.Sources.Add(new StudioSource { Name = "Game", Type = SourceTypes.Display, CaptureMethod = testCapture });
    var lagCam = new StudioSource { Name = "Slow cam", Type = SourceTypes.Camera, DeviceName = "slow", CameraDenoise = false, SizePercent = 30 };
    sscene.Sources.Add(lagCam);
    sscene.Sources.Add(new StudioSource { Name = "Mic", Type = SourceTypes.Microphone });
    var oldCam = engine.CameraDeviceOverride;
    engine.CameraDeviceOverride = src => VideoFifo.Input(work, 640, 360, 30, -1, lagSeconds: 0.25); // a webcam 250 ms slower than ours
    await engine.UpdateAsync(new[] { sscene }, sscene, WithEncoder(settings, "libx264"), false);
    await engine.SetPreviewAsync(true);
    await Task.Delay(1500);
    var m1 = await engine.MeasureCameraSyncAsync(lagCam, 9);
    Console.WriteLine($"    measured: {m1?.OffsetMs:0} ms over {m1?.Events} events (spread {m1?.SpreadMs:0} ms)");
    Check(m1 is { Reliable: true } && m1.OffsetMs is > 150 and < 350, $"a 250 ms slower camera is measured as late ({m1?.OffsetMs:0} ms)");
    if (m1 is not null) lagCam.SyncOffsetMs += (int)Math.Round(m1.OffsetMs);
    await engine.UpdateAsync(new[] { sscene }, sscene, WithEncoder(settings, "libx264"), false);
    await Task.Delay(1500);
    var m2 = await engine.MeasureCameraSyncAsync(lagCam, 9);
    Console.WriteLine($"    after applying {lagCam.SyncOffsetMs} ms: {m2?.OffsetMs:0} ms over {m2?.Events} events");
    Check(m2 is { Reliable: true } && Math.Abs(m2.OffsetMs) < 90, $"after the fix the camera matches the sound ({m2?.OffsetMs:0} ms)");
    await engine.SetPreviewAsync(false);
    engine.CameraDeviceOverride = oldCam;
}

if (Environment.GetEnvironmentVariable("LEAPCAST_ONLY_SYNC") == "1")
{
    await SyncSection();
    await engine.StopAllAsync();
    Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} FAILURE(S)");
    return failures == 0 ? 0 : 1;
}

await engine.SetPreviewAsync(true);
var pw = Stopwatch.StartNew();
while (frames < 15 && pw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
Check(engine.Running && frames >= 15, $"preview-only pipeline delivers frames (first 15 after {pw.Elapsed.TotalSeconds:0.0} s, {engine.PreviewFrame.Width}x{engine.PreviewFrame.Height})");

var clock = Stopwatch.StartNew();
await engine.StartRecordingAsync();      // restart #1 → encode + segments
await Task.Delay(5000);
var srvA = RtmpServer.Start(ffmpeg, portA, "a", Path.Combine(liveDir, "a.flv"));
var srvB = RtmpServer.Start(ffmpeg, portB, "b", Path.Combine(liveDir, "b.flv"));
var problems = await engine.StartOutputAsync(outA.Id);   // restart #2 → + shared encode on a bus; recording continues
Check(problems.Count == 0, "first output starts" + string.Concat(problems.Select(x => ": " + x)));
await Task.Delay(8000);
int startsLive = engine.PipelineStarts;
Check(Out(outA)?.State == OutputState.Live, $"first destination is live ({Out(outA)?.State}, {Out(outA)?.Kbps:0} kbps)");
await engine.StartOutputAsync(outB.Id);                    // joins the running encode mid-stream
await Task.Delay(8000);
Check(engine.PipelineStarts == startsLive, $"second destination joined without restarting the encoder (starts {startsLive} → {engine.PipelineStarts})");
Check(Out(outA)?.State == OutputState.Live && Out(outB)?.State == OutputState.Live,
    $"both destinations live: A {Out(outA)?.State} {Out(outA)?.Kbps:0} kbps, B {Out(outB)?.State} {Out(outB)?.Kbps:0} kbps");
Check(last is { Live: true, Recording: true }, $"live + recording status ({last?.Health}, {last?.RecentKbps:0} kbps recent, fps {last?.Fps:0.0})");
Check(last!.RecentKbps > 50 && last.RecentKbps <= last.CapKbps * 1.25 + 200, $"bitrate measured from segments and respects the cap: {last.RecentKbps:0} kbps (cap {last.CapKbps})");
Check(last.UploadKbps > 2 * 50, $"upload is reported per destination: total {last.UploadKbps:0} kbps across {last.Outputs.Count} outputs");
File.WriteAllText(Path.Combine(work, "main-run.log"), ReadShared(Path.Combine(work, "data", "logs", "ffmpeg-last.log")));
Console.WriteLine("    camera: " + engine.CameraStats());
string replay = await engine.SaveReplayAsync();
await engine.StopOutputAsync(outA.Id);                     // B must not notice
await Task.Delay(3000);
Check(!engine.IsOutputActive(outA.Id) && Out(outB)?.State == OutputState.Live && engine.PipelineStarts == startsLive,
    $"stopping one destination leaves the other live, no restart (B {Out(outB)?.State}, starts {engine.PipelineStarts})");
double wallRecorded = clock.Elapsed.TotalSeconds;
string rec = await engine.StopRecordingAsync();
Console.WriteLine($"    wall-clock time between Record and Stop: {wallRecorded:0.0}s");
await engine.StopOutputAsync(outB.Id);
srvA.WaitForExit(5000); srvB.WaitForExit(5000);
await engine.StopAllAsync();

var rp = await ProbeAsync(replay);
Check(rp.ok && rp.duration is > 4 and < 12, $"replay saved: {Path.GetFileName(replay)} {rp.duration:0.0}s, {rp.streams}");
var rr = await ProbeAsync(rec);
Check(rr.ok && rr.duration > wallRecorded - 9 && rr.duration < wallRecorded + 1, $"recording spans the restart: {rr.duration:0.0}s of {wallRecorded:0.0}s wall time (restart gaps excluded), {rr.streams}");
var lf = await ProbeAsync(Path.Combine(liveDir, "a.flv"));
Check(lf.ok && lf.streams.Contains("h264") && lf.streams.Contains("aac") && lf.duration > 14, $"destination A received a valid stream: {lf.duration:0.0}s, {lf.streams}");
var lfb = await ProbeAsync(Path.Combine(liveDir, "b.flv"));
var (_, bErr) = await RunAsync(new[] { "-hide_banner", "-v", "error", "-i", Path.Combine(liveDir, "b.flv"), "-f", "null", "-" });
Check(lfb.ok && lfb.streams.Contains("h264") && lfb.streams.Contains("aac") && lfb.duration > 6 && bErr.Length == 0,
    $"destination B (joined mid-stream) decodes cleanly from its first frame: {lfb.duration:0.0}s, {lfb.streams}{(bErr.Length > 0 ? " — " + bErr.Split('\n')[0] : "")}");
var lipLive = await LipSyncAsync(Path.Combine(liveDir, "a.flv"), 539, 293);
var lip = await LipSyncAsync(rec, 539, 293); // centre of the bottom-right camera overlay at 640×360
// The webcam path itself adds ~0.1 s on a loaded 2-vCPU VM; the stream must be in sync within a few frames and
// must not drift, and the exported recording must match the live stream.
Check(lipLive.events >= 3 && Math.Abs(lipLive.offsetMs) < 150 && lipLive.spreadMs < 160,
    $"live lip sync: flash vs beep {lipLive.offsetMs:+0;-0} ms (spread {lipLive.spreadMs:0} ms over {lipLive.events} events)");
Check(lip.events >= 4 && Math.Abs(lip.offsetMs) < 150 && lip.spreadMs < 160,
    $"recording lip sync: flash vs beep {lip.offsetMs:+0;-0} ms (spread {lip.spreadMs:0} ms over {lip.events} events, no drift)");
var dims = await RunCaptureAsync("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate", "-of", "csv=p=0", rec });
Check(dims.Trim().StartsWith("640,360,"), $"output is 640x360@30 ({dims.Trim()})");

// ---------------------------------------------------------------- bad destination
Console.WriteLine("A destination that refuses the connection fails on its own");
var badSet = settings.Clone();
await engine.UpdateAsync(scene, badSet, false);
var badProblems = await engine.StartOutputAsync(outA.Id); // nobody listening on port A now
var badWait = Stopwatch.StartNew();
while (Out(outA)?.State != OutputState.Error && badWait.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(250);
var badStatus = Out(outA);
Check(badProblems.Count == 0 && badStatus?.State == OutputState.Error && !engine.IsOutputActive(outA.Id),
    $"refused destination reported as an error and stopped ({badStatus?.State}: {badStatus?.Message})");
await Task.Delay(1500);
Check(!engine.Running, "nothing left encoding after the only output failed");
var missing = StreamEngine.ValidateOutput(new StreamOutput { Service = "Twitch", ServerUrl = "rtmp://live.twitch.tv/app" }, "");
Check(missing is not null, $"missing key is caught before connecting: {missing}");
var tiktok = new StreamOutput { Service = "TikTok", ServerUrl = "" };
Check(StreamEngine.ValidateOutput(tiktok, "k")?.Contains("LIVE Producer") == true, "TikTok without a server URL explains where to find it");
engine.ClearOutputError(outA.Id);

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
await engine.UpdateAsync(light, WithEncoder(settings, "libx264"), false);
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
    // BGRA: redness = R - G at the centre of the horizontal preview
    var pf = engine.PreviewFrame;
    if (f.Length != pf.Width * pf.Height * 4) return;
    int i = ((pf.Height / 2) * pf.Width + pf.HorizontalWidth / 2) * 4;
    lock (samples) samples.Add((tclock.Elapsed.TotalSeconds, f[i + 2] - f[i + 1]));
};
await engine.UpdateAsync(both, sceneA, tset, false);
await engine.SetReplayBufferAsync(true);
await Task.Delay(5000);
int starts = engine.PipelineStarts;
var sc0 = engine.StreamClock();
Console.WriteLine($"    transition-debug: stream clock wall {sc0.wall:0.00}s, ffmpeg reported {sc0.reported:0.00}s");
double switchAt = tclock.Elapsed.TotalSeconds;
await engine.UpdateAsync(both, sceneB, tset, false);
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
File.WriteAllText(Path.Combine(work, "transition-run.log"), ReadShared(Path.Combine(work, "data", "logs", "ffmpeg-last.log")));
if (mids < 3)
{
    Console.WriteLine("    transition-debug: redness after switch: " + string.Join(" ", after.Where((x, i) => i % 3 == 0).Select(x => $"{x.t - switchAt:0.0}:{x.red}")));
    string fl = ReadShared(Path.Combine(work, "data", "logs", "ffmpeg-last.log"));
    foreach (var l in fl.Split('\n').Where(l => l.Contains("eq@fade") || l.Contains("ret:") || l.Contains("Error") || l.Contains("rror")).Take(6))
        Console.WriteLine("    transition-debug: " + l.Trim());
}
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
await engine.UpdateAsync(both, sceneA, tset, false);
await Task.Delay(3000);
lock (samples) after = samples.Where(x => x.t >= switchAt).ToList();
mids = after.Count(x => x.red is > 40 and < 215);
Check(engine.PipelineStarts == starts && after.Last().red < 30 && mids <= 1, $"cut transition back is instant ({mids} in-between frames) and still no restart");
var rep2 = await engine.SaveReplayAsync();
var rp2 = await ProbeAsync(rep2);
Check(rp2.ok && rp2.duration > 5, $"replay spanning both transitions is continuous: {rp2.duration:0.0}s");

gameSrc.CropLeft = 10; // editing a source changes the graph → rebuild
await engine.UpdateAsync(both, sceneA, tset, false);
await Task.Delay(1500);
Check(engine.PipelineStarts == starts + 1, "editing a source rebuilds the pipeline (expected)");
await engine.SetReplayBufferAsync(false);
await engine.UpdateAsync(new[] { light }, light, WithEncoder(settings, "libx264"), false);

if (OperatingSystem.IsWindows() && engine.GpuWindowCaptureSupported)
{
    Console.WriteLine("GPU window capture (gfxcapture) of a real window");
    var np = Process.Start("notepad.exe")!;
    await Task.Delay(2500);
    string title = WinApi.TitleOf(np) ?? "Untitled - Notepad";
    var wscene = new StudioScene { Name = "window" };
    wscene.Sources.Add(new StudioSource { Name = "Notepad", Type = SourceTypes.Window, WindowTitle = title });
    int wf = 0; void Count(byte[] _) => Interlocked.Increment(ref wf);
    engine.FrameReady += Count;
    await engine.UpdateAsync(new[] { wscene }, wscene, WithEncoder(settings, "libx264"), false);
    try
    {
        bool sawFallback() { lock (messages) return messages.Any(m => m.Contains("switched to compatibility capture")); }
        try { await engine.SetPreviewAsync(true); } catch (Exception ex) { Console.WriteLine("    gpu-window: start: " + ex.Message); }
        var gw = Stopwatch.StartNew();
        while (wf < 30 && gw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(200);
        string wlog = ReadShared(Path.Combine(work, "data", "logs", "ffmpeg-last.log"));
        File.WriteAllText(Path.Combine(work, "gpu-window-run.log"), wlog);
        string cmd = wlog.Split('\n').FirstOrDefault() ?? "";
        Console.WriteLine($"    gpu-window: title \"{title}\", final capture: {(cmd.Contains("gfxcapture=") ? "GPU (gfxcapture)" : cmd.Contains("gdigrab") ? "GDI fallback" : "?")}, frames {wf}, fallback used {sawFallback()}");
        Console.WriteLine("    gpu-window: reader " + engine.WindowCaptureStats());
        Check(engine.Running && wf >= 30 && (engine.WindowCaptureStats().Contains(" gpu ") || sawFallback()),
            "window capture delivers frames (GPU capture, or automatic GDI fallback where the GPU can't capture)");
        // the "game" closes and reopens its window: capture re-attaches, the pipeline keeps running
        var npSrc = wscene.Sources[0];
        int npStarts = engine.PipelineStarts, readerStarts = engine.WindowReaderStarts(npSrc);
        try { np.Kill(); } catch { }
        await Task.Delay(1500);
        np = Process.Start("notepad.exe")!;
        await Task.Delay(6000);
        long npFrames = engine.WindowFrames(npSrc);
        await Task.Delay(2000);
        Console.WriteLine("    gpu-window: after reopening: " + engine.WindowCaptureStats());
        Check(engine.WindowReaderStarts(npSrc) > readerStarts && engine.WindowFrames(npSrc) > npFrames && engine.PipelineStarts == npStarts,
            "window closed and reopened: capture re-attached by itself, no pipeline restart");
        await engine.SetPreviewAsync(false);

        // GPU display capture (desktop duplication) with the same automatic fallback
        var dscene = new StudioScene { Name = "display" };
        dscene.Sources.Add(new StudioSource { Name = "Screen", Type = SourceTypes.Display, CaptureMethod = "Auto" });
        wf = 0;
        await engine.UpdateAsync(new[] { dscene }, dscene, WithEncoder(settings, "libx264"), false);
        try { await engine.SetPreviewAsync(true); } catch (Exception ex) { Console.WriteLine("    gpu-display: start: " + ex.Message); }
        gw.Restart();
        while (wf < 30 && gw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(200);
        string dcmd = (ReadShared(Path.Combine(work, "data", "logs", "ffmpeg-last.log")).Split('\n').FirstOrDefault() ?? "");
        Console.WriteLine($"    gpu-display: final capture: {(dcmd.Contains("ddagrab=") ? "GPU (ddagrab)" : dcmd.Contains("gdigrab") ? "GDI fallback" : "?")}, frames {wf}");
        Check(engine.Running && wf >= 30, "display capture delivers frames (GPU, or automatic GDI fallback)");
    }
    catch (Exception ex) { Console.WriteLine("    gpu-window: " + ex.Message); }
    engine.FrameReady -= Count;
    await engine.SetPreviewAsync(false);
    try { np.Kill(); } catch { }
}

// ---------------------------------------------------------------- vertical canvas
Console.WriteLine("Vertical 9:16 canvas: TikTok-style output next to a horizontal one");
var vset = WithEncoder(settings, "libx264");
vset.VerticalCanvas = true; vset.VerticalResolution = "360x640";
vset.Outputs = new() { outA, new StreamOutput { Id = outV.Id, Name = outV.Name, Service = outV.Service, ServerUrl = outV.ServerUrl, Canvas = "Vertical", Enabled = true } };
await engine.UpdateAsync(new[] { scene }, scene, vset, false);
var srvA2 = RtmpServer.Start(ffmpeg, portA, "a", Path.Combine(liveDir, "a2.flv"));
var srvV = RtmpServer.Start(ffmpeg, portV, "v", Path.Combine(liveDir, "v.flv"));
var vProblems = await engine.StartAllOutputsAsync();
await Task.Delay(9000);
Check(vProblems.Count == 0 && Out(outA)?.State == OutputState.Live && Out(outV)?.State == OutputState.Live,
    $"horizontal + vertical destinations live together ({Out(outA)?.State}, {Out(outV)?.State}{string.Concat(vProblems.Select(x => "; " + x))})");
Check(engine.PreviewFrame.VerticalWidth > 0 && engine.PreviewFrame.Width == engine.PreviewFrame.HorizontalWidth + engine.PreviewFrame.VerticalWidth,
    $"preview shows both canvases side by side ({engine.PreviewFrame})");
var camSrc = scene.Sources.First(x => x.Type == SourceTypes.Camera);
Check(engine.SourceSize(camSrc) is { W: > 0 }, $"real source sizes learned from ffmpeg (camera {engine.SourceSize(camSrc)})");
Console.WriteLine("    vertical plan: " + engine.VerticalPlan?.Summary);
File.WriteAllText(Path.Combine(work, "vertical-run.log"), ReadShared(Path.Combine(work, "data", "logs", "ffmpeg-last.log")));
await engine.StopAllOutputsAsync();
srvA2.WaitForExit(5000); srvV.WaitForExit(5000);
var vd = await RunCaptureAsync("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", Path.Combine(liveDir, "v.flv") });
var hd = await RunCaptureAsync("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", Path.Combine(liveDir, "a2.flv") });
var vp = await ProbeAsync(Path.Combine(liveDir, "v.flv"));
Check(vd.Trim() == "360,640" && hd.Trim() == "640,360" && vp.duration > 5 && vp.streams.Contains("aac"),
    $"vertical destination got 360x640 with audio ({vd.Trim()}, {vp.duration:0.0}s, {vp.streams}); horizontal got {hd.Trim()}");
await engine.SetPreviewAsync(false);

// ---------------------------------------------------------------- live layout editing
Console.WriteLine("Moving a source on the preview is applied live (no restart)");
var mscene = new StudioScene { Name = "move" };
var movable = new StudioSource { Name = "Red card", Type = SourceTypes.Image, FilePath = redPng, Anchor = Anchors.Free, X = 0.05, Y = 0.05, W = 0.2 };
mscene.Sources.Add(movable);
byte[]? lastFrame = null;
void Grab(byte[] f) { var pf = engine.PreviewFrame; if (f.Length == pf.Width * pf.Height * 4) lastFrame = (byte[])f.Clone(); }
engine.FrameReady += Grab;
int Red(double fx, double fy)
{
    var f = lastFrame; var pf = engine.PreviewFrame;
    if (f is null) return -999;
    int x = (int)(fx * pf.HorizontalWidth), y = (int)(fy * pf.Height);
    int i = (y * pf.Width + x) * 4;
    return f[i + 2] - f[i + 1];
}
// the 20%-wide card is 0.2 × 640 / (16/9) = 72 px tall on the 640×360 canvas: centre is 0.1 W right and 36 px down
double CenterY(double y) => (y * 360 + 36) / 360;
await engine.UpdateAsync(new[] { mscene }, mscene, WithEncoder(settings, "libx264"), false);
await engine.SetPreviewAsync(true);
await Task.Delay(3000);
int redA = Red(0.15, CenterY(0.05)), redB = Red(0.70, CenterY(0.5));
Check(redA > 150 && redB < 50, $"card drawn where placed (red {redA} there, {redB} at the target)");
int moveStarts = engine.PipelineStarts;
movable.X = 0.6; movable.Y = 0.5;
engine.RefreshLayout();
await Task.Delay(2000);
redA = Red(0.15, CenterY(0.05)); redB = Red(0.70, CenterY(0.5));
Check(redB > 150 && redA < 50 && engine.PipelineStarts == moveStarts, $"card moved live (red {redB} at the new spot, {redA} at the old), starts {moveStarts} → {engine.PipelineStarts}");
await engine.UpdateAsync(new[] { mscene }, mscene, WithEncoder(settings, "libx264"), false);
await Task.Delay(500);
Check(engine.PipelineStarts == moveStarts, "saving the moved layout doesn't rebuild the pipeline");
for (int step = 0; step < 30; step++) { movable.X = 0.05 + step * 0.01; engine.RefreshLayout(); await Task.Delay(15); } // fast drag
await Task.Delay(1500);
Check(Red(0.44, CenterY(0.5)) > 150, $"a fast drag settles on the final position (red {Red(0.44, CenterY(0.5))})");
movable.SizePercent = movable.W * 100; movable.Anchor = Anchors.BottomRight; movable.MarginPx = 24; // right-click → snap to corner
await engine.UpdateAsync(new[] { mscene }, mscene, WithEncoder(settings, "libx264"), false);
await Task.Delay(1500);
// 128×72 card, 24 px from the bottom-right corner of 640×360: centre at (552, 300)
Check(Red(552 / 640.0, 300 / 360.0) > 150 && engine.PipelineStarts == moveStarts,
    $"snapping to a corner at the same size is live too (red {Red(552 / 640.0, 300 / 360.0)}, starts {engine.PipelineStarts})");
movable.Anchor = Anchors.Free; movable.W = 0.3; // resizing rebuilds (applied on mouse-up)
await engine.UpdateAsync(new[] { mscene }, mscene, WithEncoder(settings, "libx264"), false);
Check(engine.PipelineStarts == moveStarts + 1, "resizing rebuilds the pipeline once");
engine.FrameReady -= Grab;
await engine.SetPreviewAsync(false);
await engine.UpdateAsync(new[] { light }, light, WithEncoder(settings, "libx264"), false);

// ---------------------------------------------------------------- game window bridge + fill
Console.WriteLine("Game window: own capture reader, 16:10 fills 16:9 (no bars, no stretching), survives the window closing");
var gscene = new StudioScene { Name = "game window" };
var gameWin = new StudioSource { Name = "Game", Type = SourceTypes.Window, WindowTitle = "TestGame", Anchor = Anchors.Fill };
gscene.Sources.Add(gameWin);
gscene.Sources.Add(new StudioSource { Name = "Mic", Type = SourceTypes.Microphone });
// an 800×500 game picture: green, with a red band across the top 4% that a 16:9 crop must trim away
engine.WindowDeviceOverride = _ => new InputSpec(new[] { "-re", "-f", "lavfi", "-i",
    "color=c=0x00C000:s=800x500:r=30,drawbox=x=0:y=0:w=800:h=20:color=red:t=fill" }, false);
byte[]? gFrame = null;
void GrabG(byte[] f) { var pf = engine.PreviewFrame; if (f.Length == pf.Width * pf.Height * 4) gFrame = (byte[])f.Clone(); }
(int r, int g) Px(double fx, double fy)
{
    var f = gFrame; var pf = engine.PreviewFrame;
    if (f is null) return (-1, -1);
    int i = ((int)(fy * (pf.Height - 1)) * pf.Width + (int)(fx * (pf.HorizontalWidth - 1))) * 4;
    return (f[i + 2], f[i + 1]);
}
engine.FrameReady += GrabG;
await engine.UpdateAsync(new[] { gscene }, gscene, WithEncoder(settings, "libx264"), false);
await engine.SetPreviewAsync(true);
await Task.Delay(4000);
var edgeL = Px(0.0, 0.5); var edgeT = Px(0.5, 0.0); var mid = Px(0.5, 0.5);
Check(edgeL.g > 150 && edgeL.r < 60 && edgeT.g > 150 && edgeT.r < 60 && mid.g > 150,
    $"fills the whole 16:9 canvas: no side bars, top band trimmed instead of squashed (left {edgeL}, top {edgeT}, centre {mid})");
int gStarts = engine.PipelineStarts, gReader = engine.WindowReaderStarts(gameWin);
long gFramesBefore = engine.WindowFrames(gameWin);
engine.SimulateWindowClosed(gameWin);
await Task.Delay(4000);
Console.WriteLine("    window reader: " + engine.WindowCaptureStats());
Check(engine.WindowReaderStarts(gameWin) > gReader && engine.WindowFrames(gameWin) > gFramesBefore + 30 && engine.PipelineStarts == gStarts && Px(0.5, 0.5).g > 150,
    $"window closed: capture re-attached by itself, stream never restarted (reader starts {gReader} → {engine.WindowReaderStarts(gameWin)}, pipeline starts {gStarts} → {engine.PipelineStarts})");
engine.FrameReady -= GrabG;
await engine.SetPreviewAsync(false);
engine.WindowDeviceOverride = null;

// ---------------------------------------------------------------- browser source
Console.WriteLine("Browser source (a web page drawn over the scene)");
var bscene = new StudioScene { Name = "chat" };
var chat = new StudioSource { Name = "Chat", Type = SourceTypes.Browser, Url = "https://example.invalid/chat", BrowserWidth = 160, BrowserHeight = 90,
    Anchor = Anchors.Free, X = 0.5, Y = 0.5, W = 0 };
bscene.Sources.Add(chat);
engine.FrameReady += GrabG;
await engine.UpdateAsync(new[] { bscene }, bscene, WithEncoder(settings, "libx264"), false);
await engine.SetPreviewAsync(true);
await Task.Delay(3500);
// 160×90 page at (320,180) on the 640×360 canvas: its centre is at (400,225)
var inPage = Px(400 / 640.0, 225 / 360.0); var outside = Px(0.25, 0.25);
Check(inPage.r > 200 && inPage.g < 60 && outside.r < 60, $"page drawn where placed (inside {inPage}, outside {outside}), transparent around it");
Check(engine.SourceSize(chat) is (160, 90), $"page size known for the editor ({engine.SourceSize(chat)})");
engine.FrameReady -= GrabG;
await engine.SetPreviewAsync(false);

// ---------------------------------------------------------------- camera sync (clap test)
await SyncSection();
await engine.UpdateAsync(new[] { light }, light, WithEncoder(settings, "libx264"), false);

// ---------------------------------------------------------------- reconnects
Console.WriteLine("A destination that drops reconnects on its own; the others don't notice");
var srvA3 = RtmpServer.Start(ffmpeg, portA, "a", Path.Combine(liveDir, "a3.flv"));
var srvB3 = RtmpServer.Start(ffmpeg, portB, "b", Path.Combine(liveDir, "b3.flv"));
await engine.StartOutputsAsync(new[] { (outA.Id, (string?)null), (outB.Id, (string?)null) });
await Task.Delay(8000);
int rcStarts = engine.PipelineStarts;
var bLiveFor = Out(outB)?.LiveFor ?? TimeSpan.Zero;
try { srvA3.Kill(true); } catch { }
srvA3.WaitForExit(3000);
var srvA4 = RtmpServer.Start(ffmpeg, portA, "a", Path.Combine(liveDir, "a4.flv"));
await Task.Delay(1500);
bool sawReconnect; lock (messages) sawReconnect = messages.Any(m => m.Contains("Twitch test") && m.Contains("Reconnecting"));
Check(sawReconnect && engine.IsOutputActive(outA.Id), "drop detected on A, reconnect scheduled");
await Task.Delay(8000);
Check(Out(outA)?.State == OutputState.Live && Out(outB)?.State == OutputState.Live && (Out(outB)?.LiveFor ?? TimeSpan.Zero) > bLiveFor + TimeSpan.FromSeconds(8)
      && engine.PipelineStarts == rcStarts,
    $"A back live, B uninterrupted, no encoder restart (A {Out(outA)?.State}, B {Out(outB)?.State} for {Out(outB)?.LiveFor.TotalSeconds:0}s)");

Console.WriteLine("Auto-restart when the encoder itself dies mid-broadcast");
var pid = engine.PipelineProcessId;
try { if (pid is { } id) Process.GetProcessById(id).Kill(true); } catch { }
await Task.Delay(300);
// the destinations see the disconnect; bring the "servers" back like a real ingest would be
srvA4.WaitForExit(3000); srvB3.WaitForExit(3000);
var srvA5 = RtmpServer.Start(ffmpeg, portA, "a", Path.Combine(liveDir, "a5.flv"));
var srvB5 = RtmpServer.Start(ffmpeg, portB, "b", Path.Combine(liveDir, "b5.flv"));
await Task.Delay(1200);
bool sawRestart; lock (messages) sawRestart = messages.Any(m => m.Contains("Restarting in"));
Check(sawRestart && engine.Live, "encoder death detected, outputs kept, restart scheduled");
await Task.Delay(10000);
Check(engine.Running && Out(outA)?.State == OutputState.Live && Out(outB)?.State == OutputState.Live && last is { Live: true, Running: true },
    $"pipeline and both destinations back up ({last?.Health}; A {Out(outA)?.State}, B {Out(outB)?.State})");
await engine.StopAllAsync();
Check(!engine.Running && !engine.Live && engine.OutputStatuses().Count == 0, "stop all leaves nothing running");
foreach (var srv in new[] { srvA5, srvB5 }) { srv.WaitForExit(4000); try { srv.Kill(true); } catch { } }

engine.Dispose();
Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------- helpers
static string ReadShared(string path)
{
    using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var r = new StreamReader(f);
    return r.ReadToEnd();
}
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
    public static InputSpec Input(string dir, int w, int h, int fps, double origin, double lagSeconds = 0)
    {
        int size = w * h * 3 / 2;
        var frames = Enumerable.Range(0, 32).Select(i =>  // 32 frames: the stripe pattern loops without a jump
        {
            var f = new byte[size];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    f[y * w + x] = (byte)(((x + i * 8) / 32 % 2 == 0) ? 200 : 40);
            Array.Fill(f, (byte)128, w * h, size - w * h);
            return f;
        }).ToArray();
        // The marker is clap-like: a sudden white flash that fades out over ~0.3 s back into the moving picture
        // (only its start is sudden). Each fade step is blended over the frame that is due at that moment.
        var fadeBuf = new byte[size];
        byte[] Fade(int k, byte[] b)
        {
            double a = 1 - k / 10.0;
            for (int p = 0; p < w * h; p++) fadeBuf[p] = (byte)(235 * a + b[p] * (1 - a));
            Array.Fill(fadeBuf, (byte)128, w * h, size - w * h);
            return fadeBuf;
        }
        // Like a webcam: frames are stamped by ffmpeg when read (wall clock), content is "now".
        string path = PacedPipe.Create(dir, ".yuv", n =>
        {
            double t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - lagSeconds;
            long ms = (long)Math.Round(t * 1000) % 2000;
            return ms < 300 ? Fade(Math.Min(9, (int)(ms / 30)), frames[n % frames.Length]) : frames[n % frames.Length];
        }, fps, -1, () => frames[0]);
        return new InputSpec(new[] { "-f", "rawvideo", "-pix_fmt", "yuv420p", "-video_size", $"{w}x{h}", "-framerate", fps.ToString(), "-thread_queue_size", "64", "-i", path }, true);
    }
}

static partial class Helpers { }

/// <summary>A local stand-in for Twitch/YouTube/TikTok ingest: ffmpeg in RTMP server mode, saving what it receives.</summary>
static class RtmpServer
{
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public static Process Start(string ffmpeg, int port, string key, string file)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-listen", "1", "-i", $"rtmp://127.0.0.1:{port}/live/{key}", "-c", "copy", file })
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        Thread.Sleep(500); // listening before the first publish attempt
        return p;
    }
}

static class WinApi
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int max);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    public static string? TitleOf(Process p) { try { p.Refresh(); return string.IsNullOrEmpty(p.MainWindowTitle) ? null : p.MainWindowTitle; } catch { return null; } }
    public static long? Find(string title)
    {
        long? found = null;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(512);
            GetWindowText(h, sb, 512);
            if (sb.ToString() == title) { found = h.ToInt64(); return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}

sealed class FakePlatform(string png) : IPlatformServices
{
    public IFrameSource? OpenBrowser(StudioSource s) => new SolidPage(BrowserBridge.SizeOf(s).W, BrowserBridge.SizeOf(s).H);
    public long? FindWindow(string title) => OperatingSystem.IsWindows() ? WinApi.Find(title) : null;
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

/// <summary>Stand-in for a rendered web page: opaque magenta.</summary>
sealed class SolidPage(int w, int h) : IFrameSource
{
    private readonly byte[] _frame = Enumerable.Range(0, w * h).SelectMany(_ => new byte[] { 255, 0, 255, 255 }).ToArray();
    public int Width => w;
    public int Height => h;
    public byte[]? Latest() => _frame;
    public string? Error => null;
    public void Dispose() { }
}
