# Build an installable Windows package

## Prerequisites
1. Windows 10 22H2 / 11, x64.
2. .NET 10 SDK: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
3. Inno Setup 6: https://jrsoftware.org/isinfo.php
4. Internet access during the build (NuGet packages + the FFmpeg download from GitHub).

## Build the installer
Open PowerShell in the extracted folder:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build-installer.ps1
```

The self-contained app (with FFmpeg in `ffmpeg\bin`) is published to `publish\win-x64`, and the setup program is written to
`dist\LeapcastStudioAI-Setup-0.30.0-win-x64.exe`.

Options:
* `-SkipInstaller`: publish the app folder only.
* `-FfmpegZip C:\path\ffmpeg-…-win64-gpl-….zip`: use an FFmpeg build you downloaded yourself (it must be a *GPL* build so x264 is included).
* `-SkipFfmpeg`: don't bundle FFmpeg; the app then looks on PATH or in Settings → Advanced.

## First run checklist
1. The header badge should read **READY · <your encoder>** within a few seconds. If it says FFMPEG MISSING, see Settings → Advanced.
2. Settings → paste your stream key → **Test my connection** (Twitch).
3. Click **Preview** and check the meters move when you talk and when the game makes sound.
4. **Go live.** Watch *Smart stream → Data saved vs. constant bitrate*.

Logs: Settings → Advanced → *Open log folder* (`ffmpeg-last.log` holds the exact command and FFmpeg's messages).
