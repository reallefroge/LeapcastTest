# Leapcast Studio AI

A lightweight Windows streaming studio built to look great on **low upload bandwidth**, tuned for
low-motion games like **Wizard101** plus a webcam.

## What it does

| | |
|---|---|
| **Capture** | Screen via GPU desktop duplication (works with fullscreen DirectX games, near-zero CPU) or GDI; a single game window; webcam (DirectShow); image and text overlays; microphone and desktop/game audio (WASAPI). |
| **Multistream (Aitum-style)** | Stream to Twitch, YouTube, TikTok, Kick or any RTMP(S) server at once. Each destination is its own row with a status light, live bitrate, and its own ▶/■: start, stop or reconnect one without touching the others. All destinations share one encode per canvas (no extra GPU/CPU); a destination can optionally get its own encode at a different bitrate. |
| **Vertical canvas** | A second 9:16 canvas for TikTok / Shorts / Reels with its own layout: camera on top, game full-width below over a blurred copy of itself, automatically. Drag things on the vertical preview to change it. |
| **Edit on the preview** | Drag sources to move them, drag the corner to resize, snap to edges and centre lines (hold Shift to place freely), arrow keys to nudge, right-click for corner snaps. Moves apply live, with no restart and no blink, even while streaming. |
| **Auto-config** | One click: measures your game window, picks the GPU encoder and a webcam mode that matches the stream's frame rate, then stress-tests your real scene (capture + every scene + encode, nothing goes live) and steps down until it keeps up. |
| **Any game size fills 16:9** | Full-screen sources default to *crop to fill*: an 800×500 (16:10) window fills the whole 16:9 stream with the edges trimmed, never stretched and with no black bars. *Fit* and *Stretch* are there if you prefer. Auto quality never upscales a small window (that only costs data). |
| **Game window capture that survives** | Each game window is captured by its own small process at a steady frame rate. If the game recreates its window (loading screens, resolution changes) the capture re-attaches by itself while the stream keeps running. |
| **Browser source** | Any URL (chat, alerts, widgets) rendered off-screen with a transparent background by Microsoft Edge WebView2 (built into Windows 10/11). Frames are only produced when the page changes, so an idle chat costs almost nothing. Custom CSS is injected into the page. |
| **Camera sync (clap test)** | Clap a few times in view: Leapcast measures how far the camera trails your voice and corrects it. |
| **One encode, many outputs** | The stream, recordings and instant replay all come from the **same** encode. Recording while live costs no extra GPU/CPU. |
| **Smart VBR** | Quality-targeted encoding with a hard bitrate cap. Static screens (menus, waiting for your turn, dialogue) use a fraction of the cap; battles can use all of it. In testing on mostly-static game-like content at a 3,500 kbps cap: **~965 kbps vs 3,607 kbps** for constant bitrate. |
| **Auto quality** | Picks resolution/FPS/bitrate from your measured upload (Twitch test mode, invisible to viewers), the strictest platform limit and your hardware. It never plans more than 75% of your upload, split across every destination you stream to at once. |
| **Best encoder automatically** | Test-encodes NVENC, Quick Sync, AMF and x264 at startup with their high-efficiency settings (look-ahead, adaptive quantization, B-frames), falls back to safe settings on older GPUs, then picks the best one for your PC. |
| **Webcam denoise** | Light temporal denoise after downscaling. Sensor grain in a dim room is one of the biggest bitrate wasters on a gaming stream. |
| **Mic cleanup** | High-pass plus adaptive FFT noise reduction (fans, hum, hiss). |
| **Adapts to any PC, live** | A governor watches three signs every second: whether the encode keeps up with real time, whether frames are queuing up in front of the compositor (this happens before stutter is visible), and whether each destination's upload is keeping up. If the PC falls behind it steps down a ladder, cheapest-to-notice first: lighter encoder settings → 30 FPS → only the on-air scene is captured → a smaller picture. Heavy lag is handled within seconds, mild lag within ~10 s. It only steps back up after two calm minutes while nothing is live or recording, and never back into a setting that failed recently, so it doesn't flap. A slow upload gets less bitrate instead of a lower picture quality, and a pipeline that freezes outright is restarted by a watchdog. Low-end PCs (4 threads or fewer, under 6 GB RAM, or CPU-only encoding on 8 threads or fewer) start at 30 FPS with light settings. Toggle it in Settings → *Adapt to my PC automatically*. |
| **Reliability** | Each destination reconnects on its own with back-off; a slow destination can't stall the encoder, the recording or the other destinations, and if its upload keeps falling behind the bitrate drops 25% automatically. All inputs are wall-clock synced so game, cam, mic and desktop audio stay aligned. |
| **Scene transitions** | Every scene stays live inside one encoder, so switching (e.g. Wizard101 → "BRB") is a smooth crossfade or a clean cut: no restart, no black frame, and the recording/replay keeps running. Audio levels crossfade too. A camera used in several scenes is opened only once. |
| **Live stats** | Real upload (measured from the encoded output), average, % data saved vs constant bitrate, frame rate, connection health, and *why* each setting was chosen. |

### Recommended setup for Wizard101 + camera
Click **Auto-config** (top right) with Wizard101 open: it does most of the steps below for you.

1. Multistream panel → **+ Add output** → Twitch (paste the key), again for YouTube, and TikTok (server URL + key from TikTok LIVE Producer; it asks for a fresh key each time you go live).
2. Settings → Connection → **Test my connection**. Quality: **Low-motion game**, Profile **Auto**, Bitrate mode **Smart VBR**.
3. Scene: **Display Capture** (GPU) of the monitor with Wizard101 + **Video Capture Device** (your cam, Smart denoise on) + **Desktop audio** + **Microphone** (Noise cleanup on). Drag the camera where you want it on each canvas.
4. **Go live** starts every enabled output; each row can also be started and stopped on its own.

TikTok LIVE from a PC needs LIVE access on your account (TikTok currently asks for 1,000+ followers and age 18+) and a key from LIVE Producer, which usually changes every stream.

At 30 FPS, Wizard101 looks the same as at 60 FPS, because the game is turn-based and its camera moves slowly. 30 FPS needs roughly half the data.

## Build
Requirements: Windows 10 22H2+/11 x64, the .NET 10 SDK, and [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build-installer.ps1              # publish + bundle FFmpeg + build setup EXE in .\dist
.\scripts\build-installer.ps1 -SkipInstaller   # just the app folder in .\publish\win-x64
```
The script downloads a Windows x64 **GPL** FFmpeg build (BtbN/FFmpeg-Builds, preferring the 7.1 branch) into `publish\win-x64\ffmpeg\bin`.
Pass `-FfmpegZip path\to\build.zip` to use one you downloaded yourself.

For development: `dotnet run --project .\src\LeapcastStudioAI` (put `ffmpeg.exe` on PATH or set it in Settings → Advanced).

### Engine tests
The engine core (`src/LeapcastStudioAI/Core`) is platform-neutral and is exercised end-to-end against real FFmpeg (run on Linux/macOS/WSL: the device stand-ins use `mkfifo`):
```bash
dotnet run -c Release --project tests/CoreTests
```
It covers the smart tuner, encoder argument sets, multistreaming to local RTMP servers (joining mid-stream, stopping one destination while the others continue, per-destination reconnect, encoder restart), the vertical canvas, live source moves, a full preview → record → live pipeline (with paced raw-video and audio pipes standing in for devices), recording across pipeline restarts, replay export, A/V alignment, Smart VBR vs CBR bandwidth, the bandwidth-test measurement, false-alarm-free health monitoring, and auto-reconnect.

## Architecture
See [docs/ENGINE.md](docs/ENGINE.md).

## Known limitations
- Switching scenes and moving sources are seamless. *Resizing* a source or changing capture settings while live rebuilds the pipeline, so the outputs reconnect for about a second.
- Turning the vertical canvas on or off while live also rebuilds the pipeline once.
- Every scene is composited continuously so it can be faded to at any moment; each extra scene costs some CPU.
- No browser sources and no game-hook capture (use Display Capture; it handles fullscreen Wizard101).
- AI background removal for the webcam isn't included.
- H.264 over RTMP(S) only, for maximum platform compatibility.

## License
Original application code. The installer bundles FFmpeg under the GPL. Keep FFmpeg's LICENSE file (copied into `ffmpeg\`) and honour the GPL's source-offer terms when distributing the installer.
