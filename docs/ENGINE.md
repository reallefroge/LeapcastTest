# Engine architecture

```
 Wizard101 (ddagrab GPU / gdigrab) ─┐
 Webcam (dshow) → scale → hqdn3d ───┤  wall-clock      ┌─► split ─► encoder (NVENC/QSV/AMF/x264, Smart VBR)
 Image / text PNG overlays ─────────┼─► stamped, ─────►│           │
                                    │  composited      │           └─► tee ─┬─► FLV → RTMP(S)   (live)
 Mic  (WASAPI → named pipe) ─ afftdn┤  (one ffmpeg)    │                    └─► 2 s MPEG-TS segments
 Desktop audio (WASAPI loopback) ───┘                  └─► 640×360 BGRA preview → app        │
                                                                                             ├─ replay = last N s → MP4
                                                                                             ├─ recording = Start..Stop → MP4
                                                                                             └─ exact bitrate stats
```

* **Core/** has no Windows dependencies and is unit/integration tested on any OS (`tests/CoreTests`).
  * `SmartTuner` – content type + profile + measured upload + platform cap + hardware → `EncodePlan` (with human-readable reasons).
  * `EncoderCatalog` – per-encoder "Rich" (look-ahead, AQ, B-frames) and "Basic" argument sets; startup probe test-encodes each.
  * `PipelineBuilder` – builds the single ffmpeg command: inputs, filter graph, tee outputs, preview pipe.
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

## Health signal
Because inputs are wall-clock stamped, the output can only fall behind real time when the encoder or the network
can't keep up. The engine measures *output seconds per real second* over a rolling ~15 s window. A sustained value
below 0.95 marks the connection as "Struggling", and if enabled, the cap drops 25% (at most once a minute).
FFmpeg's own `speed=`/`fps=` include start-up time, so they aren't used for this.

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
