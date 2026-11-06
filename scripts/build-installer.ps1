[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [switch]$SkipInstaller,
    # Use a specific FFmpeg build zip instead of downloading (any Windows x64 *GPL* build with ddagrab works).
    [string]$FfmpegZip = "",
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
    -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=false -p:PublishReadyToRun=true `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

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
            Invoke-WebRequest $asset.browser_download_url -OutFile $FfmpegZip -Headers @{ "User-Agent" = "leapcast-build" }
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
