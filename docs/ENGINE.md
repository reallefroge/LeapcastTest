# Engine architecture

```
 Wizard101 (gfxcapture/ddagrab GPU, gdigrab) ─┐                 ┌─► horizontal canvas ─► encode ─► tee ─┬─► 2 s TS segments (replay, recording, stats)
 Webcam (reader process → counted pipe) ──────┤  wall-clock     │   (all scenes, fades)  (Smart VBR)       └─► TS bus ─┬─► pusher → Twitch  (FLV, -c copy)
 Image / text overlays ───────────────────────┼─► stamped, ─────┤                                                     └─► pusher → YouTube
 Mic / desktop audio (WASAPI → named pipes) ──┘  composited     ├─► vertical 9:16 canvas ─► encode ─► TS bus ─► pusher → TikTok
                                                 (one ffmpeg)   │   (blurred game bg, cam on top)
                                                                ├─► optional extra encodes (an output with its own bitrate) ─► TS bus ─► pusher
                                                                └─► preview: horizontal | vertical side by side, BGRA → app
```

* **Core/** has no Windows dependencies and is unit/integration tested on any OS (`tests/CoreTests`).
  * `SmartTuner` – content type + profile + measured upload + platform cap + hardware → `EncodePlan` (with human-readable reasons).
  * `EncoderCatalog` – per-encoder "Rich" (look-ahead, AQ, B-frames) and "Basic" argument sets; startup probe test-encodes each.
  * `PipelineBuilder` – builds the single ffmpeg command: inputs, both canvases, encodes, TS buses, preview pipe.
  * `LayoutMath` – where each source sits on each canvas; shared by the builder and the preview editor so they never disagree.
  * `OutputManager` / `TsBus` – Aitum-style outputs (see below).
  * `StreamEngine` – modes (preview / live / recording / replay buffer), restarts, auto-reconnect with back-off,
    rolling-window health (output-seconds per real second) and automatic bitrate reduction.
  * `SegmentCache` – segment sessions, replay/recording export via concat (stream copy, no re-encode), housekeeping.
  * `BandwidthTester` – incompressible CBR test pattern to Twitch `?bandwidthtest=true` and measures sustained throughput.
* **Windows/** – `WasapiAudioBridge` (NAudio: mic + loopback → paced, silence-padded 48 kHz float named pipes, level meters),
  `WindowsPlatform` (WPF text rendering to PNG, monitor/window enumeration), `SecretStore` (DPAPI stream key).

## Sync model
Every live input is opened with `-use_wallclock_as_timestamps 1` and shifted by one shared origin
(`setpts=PTS-T0/TB`, `asetpts=…`) under `-copyts`. Devices that take a second to open no longer offset the
picture from the sound, and `aresample=async` absorbs device clock drift and pipe jitter.

## Health signal and the performance governor
`PerformanceGovernor` (Core/Performance.cs) is pure logic; the engine feeds it once a second and applies its decisions.

Signals:
- **speed**: output seconds per real second over the last 4 s of progress reports. FFmpeg's own `speed=` includes start-up time, so it isn't used.
- **compositor backlog**: seconds of frames queued in the counted capture pipes (cameras, game windows, browser sources). It grows before stutter is visible, and it is also camera delay.
- **upload backlog**: the deepest per-destination send queue.

Decisions:
- Severe lag (speed < 0.85 or backlog > 1 s) for 4 s, or mild lag (speed < 0.95 or backlog > 0.4 s) for 10 s, steps down one *useful* rung of `PerfLevel.Ladder`. A rung that changes nothing for the current plan, for example 30 FPS when already at 30, is skipped. After that it waits 20 s before acting again, so it never cascades.
- The rungs are: light encoder presets (validated per encoder at probe time) with a 15 FPS / 640×360 preview → 30 FPS → only the on-air scene composited (a scene switch then rebuilds the pipeline) → 0.8× picture → 0.64× picture (never below 640×360).
- It steps up only after 2 healthy minutes, only while nothing is live or recording, and never into a rung that failed in the last 10 minutes.
- An upload backlog above 2.5 s for 10 s lowers the bitrate 25% (at most once a minute, down to 35%).
- Stall watchdog: no progress at all for 10 s means the pipeline is rebuilt. The log is kept as `ffmpeg-stalled.log`.

`HardwareProfile.Classify` sorts PCs into Low / Mid / High tiers. Low-end PCs start on the 30 FPS rung.

## Scene transitions
All scenes are composited in the same filter graph from shared, de-duplicated inputs (`split` per device), then stacked
bottom → top. Every scene above the first is laid over the stack through its own alpha mask: a black frame whose
brightness is an `eq@fadeK` expression of stream time `t`. The invariant is "every mask above the on-air scene is transparent".

* Switching **up** the stack: one command fades the new scene's mask in.
* Switching **down**: masks between are cleared, the target is made opaque underneath, and the current scene's mask fades out.

Commands go through ffmpeg's stdin (`C eq@fade2 -1 brightness -0.1+1.1*clip((t-T1)/D,0,1)`), with `T1` set just after the
last command lands. Only expressions change, so there's no filter reconfiguration, no restart and no black frame, and the
recording/replay is uninterrupted. Per-device `volume@g*` stages crossfade audio when scenes use different levels.
`-stats_period 0.1` keeps ffmpeg polling stdin fast enough (ffmpeg 7+ checks keyboard input once per stats period).

## Multistream outputs
Each canvas is encoded once. The encoder writes MPEG-TS into a `TsBus` (a named pipe the app reads), which fans the
bytes out to one small "pusher" ffmpeg per destination (`-c copy` to FLV/RTMP). Consequences:

* **Independent outputs.** A destination starts, stops or reconnects by adding/removing its pusher; the encoder and the
  other destinations don't notice. A new pusher joins mid-stream: the encoder repeats SPS/PPS on every keyframe
  (`dump_extra=freq=keyframe`), the bus aligns the joiner to a 188-byte TS packet, and the pusher probes at most one
  keyframe interval (~1–2 s to go live).
* **No head-of-line blocking.** Each pusher has its own bounded queue (24 MB). A slow upload to one platform shows up as a
  growing backlog in that queue (reported as "N s behind" and used by the auto-bitrate logic), never as the encoder,
  recording or other platforms slowing down. A destination that can't keep up at all is cut off and reconnected.
* **Failures stay local.** A destination that refuses the connection before ever going live (wrong key/URL) is stopped
  with its error instead of retried forever; one that drops while live reconnects with 2/4/8/15/30 s back-off.
* **Warm encodes.** While anything is live, every canvas with an *enabled* output keeps encoding, so starting another
  destination on that canvas never restarts the pipeline.
* **Budget.** `OutputBudget` splits 75% of the measured upload: shared encodes are uploaded once per destination, a
  720×1280 vertical destination gets about 60% of a horizontal one, and the strictest platform sets the main cap.

## Live layout editing
Every overlay is a named `overlay@h<id>` / `overlay@v<id>` filter. Moving a source sends `x`/`y` commands (pixels, or the
anchor expression such as `W-w-24`), so moves are applied live. ffmpeg reads one command per ~100 ms, so the engine
coalesces: only the newest position of each overlay is sent, one axis per tick. The rebuild check
(`LayoutFingerprint`) compares what is composited (sizes, layers), not raw placement fields, so changing a corner anchor
to a free position at the same size does not rebuild. Resizing does (runtime scale changes aren't safe with every filter
on every FFmpeg version). FFmpeg's info log reports each input's real size, which makes the vertical auto-layout exact.
