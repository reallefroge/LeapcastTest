[CmdletBinding()]
param(
    [string]$QtRoot = "",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "",
    [string]$TwitchClientId = $env:LEAPCAST_TWITCH_CLIENT_ID,
    # Automatic updates are OFF by default so a test build can never be
    # replaced by the published release. Pass -AutoUpdate to build a normal,
    # self-updating installer.
    [switch]$AutoUpdate,
    # Test builds only: allow an empty LEAPCAST_TWITCH_CLIENT_ID instead of
    # refusing to build. Never use this for a published release.
    [switch]$AllowMissingTwitchClientId
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ProjectRoot = $PSScriptRoot
$VersionFile = Join-Path $ProjectRoot "VERSION"
$Version = (Get-Content $VersionFile -Raw).Trim()

if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "VERSION must contain three or four numeric parts, such as 3.0.9 or 3.0.9.1."
}

if ([string]::IsNullOrWhiteSpace($TwitchClientId)) {
    # A release must never ship without the bundled Twitch application ID, but a
    # test build should not be blocked by it. Without an ID the app now shows a
    # "Twitch application Client ID" box on the Keys page, so Twitch Connect is
    # configurable by hand instead of dead.
    if (-not $AllowMissingTwitchClientId) {
        throw "LEAPCAST_TWITCH_CLIENT_ID is required. Refusing to create an installer with a broken Twitch Connect button. Pass -AllowMissingTwitchClientId to build a test installer anyway; Twitch Connect will then need a Client ID entered on the Keys page."
    }
    Write-Host "WARNING: building WITHOUT a bundled Twitch Client ID." -ForegroundColor Yellow
    Write-Host "         Twitch Connect will ask for a Client ID on the Keys page." -ForegroundColor Yellow
    Write-Host "         Do not publish this installer as a release." -ForegroundColor Yellow
}

if ([string]::IsNullOrWhiteSpace($QtRoot)) {
    $Qmake = Get-Command qmake.exe -ErrorAction SilentlyContinue
    if (-not $Qmake) {
        $Qmake = Get-Command qmake -ErrorAction SilentlyContinue
    }
    if (-not $Qmake) {
        throw "Qt was not found. Pass -QtRoot or add qmake to PATH."
    }
    $QtRoot = (& $Qmake.Source -query QT_INSTALL_PREFIX).Trim()
}

$Cmake = Get-Command cmake.exe -ErrorAction SilentlyContinue
if (-not $Cmake) {
    $Cmake = Get-Command cmake -ErrorAction SilentlyContinue
}
if (-not $Cmake) {
    throw "CMake was not found."
}

$BuildDir = Join-Path $ProjectRoot "build"
$DeployDir = Join-Path $ProjectRoot "deploy"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $ProjectRoot "artifacts"
}

if (Test-Path $DeployDir) {
    Remove-Item $DeployDir -Recurse -Force
}
New-Item -ItemType Directory -Force $DeployDir, $OutputDirectory | Out-Null

# Never let a previous-version installer remain in the publish directory.
# The release workflow must upload only the installer matching VERSION.
Get-ChildItem -Path $OutputDirectory -Filter "LeapcastStudio-Setup-*.exe*" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

Write-Host "Building Leapcast Studio v$Version..."
$AutoUpdateFlag = if ($AutoUpdate) { "ON" } else { "OFF" }
if (-not $AutoUpdate) {
    Write-Host "Automatic updates are DISABLED in this build. Pass -AutoUpdate to re-enable them." -ForegroundColor Yellow
}
& $Cmake.Source -S $ProjectRoot -B $BuildDir -G "Visual Studio 17 2022" -A x64 `
    "-DCMAKE_PREFIX_PATH=$QtRoot" "-DLEAPCAST_TWITCH_CLIENT_ID=$TwitchClientId" `
    "-DLEAPCAST_AUTO_UPDATE=$AutoUpdateFlag"
if ($LASTEXITCODE -ne 0) { throw "CMake configuration failed." }

& $Cmake.Source --build $BuildDir --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw "Compilation failed." }

$Executable = Join-Path $BuildDir "$Configuration\LeapcastStudio.exe"
if (-not (Test-Path $Executable)) {
    throw "The compiled application was not found at $Executable."
}
Copy-Item $Executable (Join-Path $DeployDir "LeapcastStudio.exe") -Force

$WinDeployQt = Join-Path $QtRoot "bin\windeployqt.exe"
if (-not (Test-Path $WinDeployQt)) {
    throw "windeployqt was not found under $QtRoot."
}
$DeployMode = if ($Configuration -eq "Debug") { "--debug" } else { "--release" }
& $WinDeployQt $DeployMode --no-translations --compiler-runtime (Join-Path $DeployDir "LeapcastStudio.exe")
if ($LASTEXITCODE -ne 0) { throw "Qt deployment failed." }

$IsccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles} "Inno Setup 6\ISCC.exe")
)
$IsccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$Iscc = if ($IsccCommand) {
    $IsccCommand.Source
} else {
    $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) {
    throw "Inno Setup 6 was not found."
}

$InstallerScript = Join-Path $ProjectRoot "installer\LeapcastStudio.iss"
& $Iscc "/DAppVersion=$Version" "/DBuildRoot=$DeployDir" "/O$OutputDirectory" $InstallerScript
if ($LASTEXITCODE -ne 0) { throw "Installer creation failed." }

$Installer = Get-Item (Join-Path $OutputDirectory "LeapcastStudio-Setup-$Version.exe")
$Hash = (Get-FileHash $Installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$ChecksumPath = "$($Installer.FullName).sha256"
Set-Content -Path $ChecksumPath -Value "$Hash  $($Installer.Name)" -Encoding ascii

Write-Host ""
Write-Host "Installer: $($Installer.FullName)"
Write-Host "SHA-256:  $ChecksumPath"
