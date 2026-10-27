# Leapcast Studio AI

A lightweight Windows streaming studio built to look great on **low upload bandwidth**, tuned for
low-motion games like **Wizard101** plus a webcam.

## What it does

| | |
|---|---|
| **Capture** | Screen via GPU desktop duplication (works with fullscreen DirectX games, near-zero CPU) or GDI; a single game window; webcam (DirectShow); image and text overlays; microphone and desktop/game audio (WASAPI). |
| **One encode, many outputs** | The stream, recordings and instant replay all come from the **same** encode. Recording while live costs no extra GPU/CPU. |
| **Smart VBR** | Quality-targeted encoding with a hard bitrate cap. Static screens (menus, waiting for your turn, dialogue) use a fraction of the cap; battles can use all of it. In testing on mostly-static game-like content at a 3,500 kbps cap: **~965 kbps vs 3,607 kbps** for constant bitrate. |
| **Auto quality** | Picks resolution/FPS/bitrate from your measured upload (Twitch test mode, invisible to viewers), the platform's limit and your hardware. It never plans more than 75% of your upload. |
| **Best encoder automatically** | Test-encodes NVENC, Quick Sync, AMF and x264 at startup with their high-efficiency settings (look-ahead, adaptive quantization, B-frames), falls back to safe settings on older GPUs, then picks the best one for your PC. |
| **Webcam denoise** | Light temporal denoise after downscaling. Sensor grain in a dim room is one of the biggest bitrate wasters on a gaming stream. |
| **Mic cleanup** | High-pass plus adaptive FFT noise reduction (fans, hum, hiss). |
| **Reliability** | Auto-reconnect with back-off; if output falls behind real time, the bitrate cap drops 25% automatically; all inputs are wall-clock synced so game, cam, mic and desktop audio stay aligned. |
| **Live stats** | Real upload (measured from the encoded output), average, % data saved vs constant bitrate, frame rate, connection health, and *why* each setting was chosen. |

### Recommended setup for Wizard101 + camera
1. Settings → Platform **Twitch** → paste your stream key → **Test my connection**.
2. Content: **Low-motion game**, Profile: **Auto**, Bitrate mode: **Smart VBR**.
3. Scene: **Display Capture** (GPU) of the monitor with Wizard101 + **Video Capture Device** (your cam, Smart denoise on) + **Desktop audio** + **Microphone** (Noise cleanup on).

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
It covers the smart tuner, encoder argument sets, a full preview → record → live pipeline (with paced raw-video and audio pipes standing in for devices), recording across pipeline restarts, replay export, A/V alignment, Smart VBR vs CBR bandwidth, the bandwidth-test measurement, false-alarm-free health monitoring, and auto-reconnect.

## Architecture
See [docs/ENGINE.md](docs/ENGINE.md).

## Known limitations
- Changing scenes or sources while live restarts the encoder, so the stream blinks for about a second. A future version could composite on the GPU and switch scenes seamlessly.
- No browser sources and no game-hook capture (use Display Capture; it handles fullscreen Wizard101).
- AI background removal for the webcam isn't included.
- H.264 over RTMP(S) only, for maximum platform compatibility.

## License
Original application code. The installer bundles FFmpeg under the GPL. Keep FFmpeg's LICENSE file (copied into `ffmpeg\`) and honour the GPL's source-offer terms when distributing the installer.
