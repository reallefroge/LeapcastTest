[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [switch]$SkipInstaller,
    # Use a specific FFmpeg build zip instead of downloading (any Windows x64 *GPL* build with ddagrab works).
    [string]$FfmpegZip = "",
    # Use a local copy of the Vosk speech model zip (vosk-model-small-en-us-0.15.zip) instead of downloading it.
    [string]$VoskZip = "",
    [switch]$SkipFfmpeg
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # makes Invoke-WebRequest much faster
$Root = Split-Path $PSScriptRoot -Parent
$Project = [System.IO.Path]::GetFullPath((Join-Path $Root "src\LeapcastStudioAI\LeapcastStudioAI.csproj"))
$PublishDir = Join-Path $Root "publish\$Runtime"
$CacheDir = Join-Path $Root ".cache"
$InstallerScript = Join-Path $Root "installer\LeapcastStudioAI.iss"

Write-Host "== Leapcast Studio AI build ==" -ForegroundColor Cyan
dotnet --version
if ($LASTEXITCODE -ne 0) { throw ".NET SDK is missing. Install the .NET 10 SDK and retry." }

if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

dotnet publish $Project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=false -p:PublishReadyToRun=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# ---------------------------------------------------------------- speech model (voice clips)
$VoskModel = "vosk-model-small-en-us-0.15"
$VoskSha = "30f26242c4eb449f948e42cb302dd7a686cb29a3423a8367f99ff41780942498"
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
if (-not $VoskZip) {
    $VoskZip = Join-Path $CacheDir "$VoskModel.zip"
    if (-not (Test-Path $VoskZip)) {
        # CI: the copy kept as a release asset of this repository (.github/workflows/fetch-vosk-model.yml)
        if ($env:GITHUB_REPOSITORY -and (Get-Command gh -ErrorAction SilentlyContinue)) {
            Write-Host "Downloading the speech model from this repository's release asset..." -ForegroundColor Cyan
            if (-not $env:GH_TOKEN -and $env:GITHUB_TOKEN) { $env:GH_TOKEN = $env:GITHUB_TOKEN }
            $rel = gh api "repos/$($env:GITHUB_REPOSITORY)/releases/tags/speech-model-$VoskModel" | ConvertFrom-Json
            $asset = $rel.assets | Where-Object { $_.name -eq "$VoskModel.zip" } | Select-Object -First 1
            if ($asset) {
                Invoke-WebRequest "https://api.github.com/repos/$($env:GITHUB_REPOSITORY)/releases/assets/$($asset.id)" -OutFile $VoskZip `
                    -Headers @{ "Accept" = "application/octet-stream"; "Authorization" = "Bearer $($env:GH_TOKEN)"; "User-Agent" = "leapcast-build" }
            }
        }
        if (-not (Test-Path $VoskZip)) {
            Write-Host "Downloading the speech model from alphacephei.com..." -ForegroundColor Cyan
            Invoke-WebRequest "https://alphacephei.com/vosk/models/$VoskModel.zip" -OutFile $VoskZip
        }
    }
}
$hash = (Get-FileHash $VoskZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $VoskSha) { Remove-Item $VoskZip -ErrorAction SilentlyContinue; throw "Speech model checksum mismatch ($hash)." }
$SpeechDir = Join-Path $PublishDir "speech"
New-Item -ItemType Directory -Force -Path $SpeechDir | Out-Null
Expand-Archive -Path $VoskZip -DestinationPath $SpeechDir -Force
if (-not (Test-Path (Join-Path $SpeechDir "$VoskModel\am\final.mdl"))) { throw "The speech model didn't unpack where expected." }
if (-not (Test-Path (Join-Path $PublishDir "libvosk.dll"))) { throw "libvosk.dll is missing from the published app." }
Write-Host "Bundled speech model: $VoskModel" -ForegroundColor Green

# ---------------------------------------------------------------- Whisper (accurate captions for clips and VOD edits)
# The official whisper.cpp Windows build and the Silero voice-activity model; the base.en model (148 MB) downloads on first use.
$WhisperTag = "b5454"
$WhisperFiles = @{
    "whisper-bin-x64.zip"    = @{ Sha = "b1158bd10edf92c9799149a6fc5d3e58da3f495241d6fb3423088130e88ffbf3"; Url = "https://github.com/ggml-org/whisper.cpp/releases/download/$WhisperTag/whisper-blas-bin-x64.zip" }
    "ggml-silero-v5.1.2.bin" = @{ Sha = "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf"; Url = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin" }
}
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
foreach ($name in $WhisperFiles.Keys) {
    $dest = Join-Path $CacheDir $name
    $want = $WhisperFiles[$name].Sha
    if ((Test-Path $dest) -and ((Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant() -ne $want)) { Remove-Item $dest }
    if (-not (Test-Path $dest) -and $env:GITHUB_REPOSITORY) {
        # CI: the copy kept as a release asset of this repository (.github/workflows/fetch-whisper.yml)
        if (-not $env:GH_TOKEN -and $env:GITHUB_TOKEN) { $env:GH_TOKEN = $env:GITHUB_TOKEN }
        try { gh release download speech-whisper -R $env:GITHUB_REPOSITORY -p $name -D $CacheDir --clobber 2>&1 | Out-Host } catch { }
    }
    if (-not (Test-Path $dest)) {
        Write-Host "Downloading $name from its official source..." -ForegroundColor Cyan
        Invoke-WebRequest $WhisperFiles[$name].Url -OutFile $dest
    }
    $h = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($h -ne $want) { Remove-Item $dest -ErrorAction SilentlyContinue; throw "$name checksum mismatch ($h)." }
}
$WhisperDir = Join-Path $PublishDir "speech\whisper"
New-Item -ItemType Directory -Force -Path $WhisperDir | Out-Null
$wtmp = Join-Path $CacheDir "whisper-unpack"
if (Test-Path $wtmp) { Remove-Item $wtmp -Recurse -Force }
Expand-Archive -Path (Join-Path $CacheDir "whisper-bin-x64.zip") -DestinationPath $wtmp -Force
$cli = Get-ChildItem $wtmp -Recurse -Filter "whisper-cli.exe" | Select-Object -First 1
if (-not $cli) { throw "whisper-cli.exe isn't in the whisper.cpp build." }
Copy-Item $cli.FullName $WhisperDir
Get-ChildItem $cli.DirectoryName -Filter "*.dll" | Where-Object { $_.Name -notmatch '^SDL' } | Copy-Item -Destination $WhisperDir
Copy-Item (Join-Path $CacheDir "ggml-silero-v5.1.2.bin") $WhisperDir
Remove-Item $wtmp -Recurse -Force
foreach ($f in 'whisper-cli.exe','whisper.dll','ggml.dll','ggml-base.dll','ggml-silero-v5.1.2.bin') {
    if (-not (Test-Path (Join-Path $WhisperDir $f))) { throw "Whisper bundle is missing $f." }
}
& (Join-Path $WhisperDir "whisper-cli.exe") --help 2>&1 | Select-Object -First 3 | Out-Host
Write-Host "Bundled Whisper ($WhisperTag): $((Get-ChildItem $WhisperDir).Count) files" -ForegroundColor Green

# ---------------------------------------------------------------- FFmpeg (the media engine)
if (-not $SkipFfmpeg) {
    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    if (-not $FfmpegZip) {
        Write-Host "Finding the latest FFmpeg Windows GPL build (BtbN/FFmpeg-Builds)..." -ForegroundColor Cyan
        $apiHeaders = @{ "User-Agent" = "leapcast-build" }
        if ($env:GITHUB_TOKEN) { $apiHeaders["Authorization"] = "Bearer $env:GITHUB_TOKEN" }   # avoids API rate limits on CI
        $rel = Invoke-RestMethod "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest" -Headers $apiHeaders
        $assets = $rel.assets
        # Prefer the stable 7.1 branch the engine was tested against, then the newest release branch, then master.
        $asset = $assets | Where-Object { $_.name -match '^ffmpeg-n7\.1.*-win64-gpl-7\.1\.zip$' } | Select-Object -First 1
        if (-not $asset) {
            $asset = $assets | Where-Object { $_.name -match '^ffmpeg-n\d+\.\d+.*-win64-gpl-\d+\.\d+\.zip$' } |
                     Sort-Object { [version](($_.name -replace '^ffmpeg-n(\d+\.\d+).*$', '$1')) } -Descending | Select-Object -First 1
        }
        if (-not $asset) { $asset = $assets | Where-Object { $_.name -eq 'ffmpeg-master-latest-win64-gpl.zip' } | Select-Object -First 1 }
        if (-not $asset) { throw "Couldn't find a win64 GPL FFmpeg build. Download one manually and pass -FfmpegZip <path>." }
        $FfmpegZip = Join-Path $CacheDir $asset.name
        if (-not (Test-Path $FfmpegZip)) {
            Write-Host "Downloading $($asset.name) ..." -ForegroundColor Cyan
            # GitHub's download servers sometimes time out (504): try both download routes a few times
            $routes = @(
                @{ Url = $asset.browser_download_url; Headers = @{ "User-Agent" = "leapcast-build" } },
                @{ Url = $asset.url; Headers = ($apiHeaders + @{ "Accept" = "application/octet-stream" }) }
            )
            $ok = $false
            for ($try = 1; $try -le 3 -and -not $ok; $try++) {
                foreach ($r in $routes) {
                    try { Invoke-WebRequest $r.Url -OutFile $FfmpegZip -Headers $r.Headers -TimeoutSec 600; $ok = $true; break }
                    catch {
                        Remove-Item $FfmpegZip -ErrorAction SilentlyContinue
                        Write-Host "FFmpeg download failed ($($_.Exception.Message)); trying again..." -ForegroundColor Yellow
                        Start-Sleep -Seconds ($try * 10)
                    }
                }
            }
            $cacheTag = "ffmpeg-cache"
            if ($ok -and $env:GITHUB_REPOSITORY) {
                # keep a copy in this repository, so an outage upstream can't stop a build
                if (-not $env:GH_TOKEN -and $env:GITHUB_TOKEN) { $env:GH_TOKEN = $env:GITHUB_TOKEN }
                try {
                    $have = gh release view $cacheTag -R $env:GITHUB_REPOSITORY --json assets -q ".assets[].name" 2>$null
                    if ($LASTEXITCODE -ne 0) { gh release create $cacheTag -R $env:GITHUB_REPOSITORY --title "FFmpeg cache (build fallback)" --notes "Copy of the FFmpeg build the installer bundles, used when the upstream download is down." --prerelease 2>&1 | Out-Host; $have = @() }
                    if (-not ($have -contains $asset.name)) { gh release upload $cacheTag $FfmpegZip -R $env:GITHUB_REPOSITORY --clobber 2>&1 | Out-Host }
                } catch { Write-Host "FFmpeg cache not updated: $($_.Exception.Message)" -ForegroundColor Yellow }
            }
            if (-not $ok -and $env:GITHUB_REPOSITORY) {
                Write-Host "Upstream FFmpeg download is down: using this repository's cached copy" -ForegroundColor Yellow
                if (-not $env:GH_TOKEN -and $env:GITHUB_TOKEN) { $env:GH_TOKEN = $env:GITHUB_TOKEN }
                $names = gh release view $cacheTag -R $env:GITHUB_REPOSITORY --json assets -q ".assets[].name" 2>$null
                $pick = $names | Where-Object { $_ -like "*win64-gpl*.zip" } | Select-Object -First 1
                if ($pick) {
                    gh release download $cacheTag -R $env:GITHUB_REPOSITORY -p $pick -D $CacheDir --clobber 2>&1 | Out-Host
                    $FfmpegZip = Join-Path $CacheDir $pick
                    $ok = Test-Path $FfmpegZip
                }
            }
            if (-not $ok) { throw "Couldn't download FFmpeg ($($asset.name)); GitHub's download servers may be down. Try again later." }
        } else { Write-Host "Using cached $($asset.name)" }
    }
    $Extract = Join-Path $CacheDir "ffmpeg-extract"
    if (Test-Path $Extract) { Remove-Item $Extract -Recurse -Force }
    Expand-Archive -Path $FfmpegZip -DestinationPath $Extract -Force
    $exe = Get-ChildItem $Extract -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
    if (-not $exe) { throw "ffmpeg.exe not found inside $FfmpegZip" }
    $buildRoot = Split-Path $exe.DirectoryName -Parent
    $Dest = Join-Path $PublishDir "ffmpeg"
    New-Item -ItemType Directory -Force -Path (Join-Path $Dest "bin") | Out-Null
    Copy-Item (Join-Path $exe.DirectoryName "*") (Join-Path $Dest "bin") -Recurse -Force
    Get-ChildItem $buildRoot -File | Where-Object { $_.Name -match 'LICENSE|README' } | Copy-Item -Destination $Dest -Force

    $ff = Join-Path $Dest "bin\ffmpeg.exe"
    $filters = & $ff -hide_banner -filters 2>$null | Out-String
    if ($filters -notmatch 'ddagrab') {
        Write-Warning "This FFmpeg build has no 'ddagrab' (GPU screen capture). Display Capture will need the GDI method."
    }
    $encoders = & $ff -hide_banner -encoders 2>$null | Out-String
    foreach ($e in 'libx264', 'h264_nvenc', 'h264_qsv', 'h264_amf') {
        if ($encoders -notmatch $e) { Write-Warning "FFmpeg build lacks $e" }
    }
    Write-Host "Bundled FFmpeg: $((& $ff -hide_banner -version | Select-Object -First 1))" -ForegroundColor Green
}

if ($SkipInstaller) {
    Write-Host "Published application files: $PublishDir" -ForegroundColor Green
    exit 0
}

$Iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if (-not $Iscc) {
    $Candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $IsccPath = $Candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $IsccPath) {
        throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isinfo.php, or rerun with -SkipInstaller."
    }
} else {
    $IsccPath = $Iscc.Source
}

& $IsccPath "/DPublishDir=$PublishDir" $InstallerScript
if ($LASTEXITCODE -ne 0) { throw "Inno Setup installer compilation failed." }

Write-Host "Build complete. Look in the dist folder for the setup EXE." -ForegroundColor Green
