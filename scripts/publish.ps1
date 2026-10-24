# Publishes PortableDev to the stick.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1 -StickRoot X:\PortableDev
#
# The published app is self-contained (it carries its own .NET runtime) so it can
# start on a freshly restored machine with no DOTNET_ROOT and no installed runtime.
# Kept ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less files as ANSI and
# a stray non-ASCII byte silently eats the following line.

[CmdletBinding()]
param(
    # Defaults to the folder this script lives under when that folder is a stick root.
    [string] $StickRoot = '',
    [string] $Configuration = 'Release',
    [switch] $SkipManifest,
    [switch] $PruneManifest,
    [switch] $KeepStage
)

$ErrorActionPreference = 'Stop'

# Two layouts are supported:
#   dev repo : <repo>\scripts\publish.ps1  with <repo>\PortableDev.sln and <repo>\manifest
#   on stick : <stick>\scripts\publish.ps1 with <stick>\source\PortableDev.sln and <stick>\manifest
$root = Split-Path -Parent $PSScriptRoot

# Infer the stick root instead of hard-coding "E:\PortableDev": the stick gets whatever
# drive letter the machine assigns, and a wrong default silently published nowhere.
if ([string]::IsNullOrWhiteSpace($StickRoot)) {
    foreach ($candidate in @($root, (Split-Path -Parent $root))) {
        if ($candidate -and (Test-Path (Join-Path $candidate '.portabledev-root'))) {
            $StickRoot = $candidate
            break
        }
    }
}
if ([string]::IsNullOrWhiteSpace($StickRoot)) {
    throw "cannot infer the stick root; pass -StickRoot <path>"
}

$solutionRoot = $root
if (-not (Test-Path (Join-Path $root 'PortableDev.sln'))) {
    $nested = Join-Path $root 'source'
    if (Test-Path (Join-Path $nested 'PortableDev.sln')) {
        $solutionRoot = $nested
    }
    else {
        throw "cannot find PortableDev.sln in $root or $root\source"
    }
}

$manifestRoot = Join-Path $root 'manifest'
if (-not (Test-Path $manifestRoot)) { $manifestRoot = Join-Path $solutionRoot 'manifest' }

if (-not (Test-Path $StickRoot)) {
    throw "stick root not found: $StickRoot"
}

Write-Host "solution : $solutionRoot"
Write-Host "manifest : $manifestRoot"

# ---------------------------------------------------------------- toolchain ----
$sdkRoot = Join-Path $StickRoot 'tools\dotnet-sdk'
if (-not (Test-Path $sdkRoot)) {
    throw "no dotnet sdk on the stick at $sdkRoot"
}

$sdkDir = Get-ChildItem $sdkRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
if ($null -eq $sdkDir) { throw "no sdk version folder under $sdkRoot" }

$dotnet = Join-Path $sdkDir.FullName 'dotnet.exe'
if (-not (Test-Path $dotnet)) { throw "dotnet.exe not found at $dotnet" }

Write-Host "sdk      : $($sdkDir.FullName)"
Write-Host "stick    : $StickRoot"

# ---------------------------------------------------------------- publish ----
$stage = Join-Path $solutionRoot 'artifacts\app'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

Write-Host "publish  : app -> $stage"
$publishArgs = @(
    'publish',
    (Join-Path $solutionRoot 'src\PortableDev.App\PortableDev.App.csproj'),
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:DebugType=none',
    # Single node: the build must not depend on MSBuild's worker-node pipe, which
    # some restricted/remote sessions block (symptom: 'failed' with zero errors).
    '-m:1',
    '-nodeReuse:false',
    '-o', $stage,
    '-v', 'q',
    '--nologo'
)

& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$appExe = Join-Path $stage 'PortableDev.exe'
if (-not (Test-Path $appExe)) { throw "publish produced no PortableDev.exe" }

# The GUI is a sibling executable in the same folder: it reuses the console app as
# its only write path (apply/clean go through the elevation broker there), and the
# console app keeps working even if the GUI cannot start.
Write-Host "publish  : gui -> $stage"
$guiArgs = @(
    'publish',
    (Join-Path $solutionRoot 'src\PortableDev.Gui\PortableDev.Gui.csproj'),
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:DebugType=none',
    # Single node: the build must not depend on MSBuild's worker-node pipe, which
    # some restricted/remote sessions block (symptom: 'failed' with zero errors).
    '-m:1',
    '-nodeReuse:false',
    '-o', $stage,
    '-v', 'q',
    '--nologo'
)

& $dotnet @guiArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (gui) failed with exit code $LASTEXITCODE" }

$guiExe = Join-Path $stage 'PortableDev.Gui.exe'
if (-not (Test-Path $guiExe)) { throw "publish produced no PortableDev.Gui.exe" }

$files = Get-ChildItem $stage -Recurse -File
$sizeMb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "publish  : $($files.Count) files, $sizeMb MB"

# ---------------------------------------------------------------- copy to stick ----
$appTarget = Join-Path $StickRoot 'app'
if (Test-Path $appTarget) {
    Write-Host "copy     : replacing $appTarget"
    Remove-Item $appTarget -Recurse -Force
}

New-Item -ItemType Directory -Path $appTarget -Force | Out-Null
Copy-Item -Path (Join-Path $stage '*') -Destination $appTarget -Recurse -Force
Write-Host "copy     : $($files.Count) files -> $appTarget"

# Convenience launcher so the app can be started from the drive root.
# No arguments -> open the window; anything else -> the command line.
$launcher = Join-Path $StickRoot 'PortableDev.cmd'
$launcherBody = @"
@echo off
setlocal
if "%~1"=="" goto gui
"%~dp0app\PortableDev.exe" %*
exit /b %ERRORLEVEL%
:gui
start "" "%~dp0app\PortableDev.Gui.exe"
"@
Set-Content -Path $launcher -Value $launcherBody -Encoding ASCII
Write-Host "launcher : $launcher"

# ---------------------------------------------------------------- manifest sync ----
if (-not $SkipManifest) {
    # The repo is the single source of truth; the stick copy is generated.
    $srcManifest = $manifestRoot
    $dstManifest = Join-Path $StickRoot 'manifest'

    if ($srcManifest.TrimEnd('\') -ieq $dstManifest.TrimEnd('\')) {
        # Running from the stick's own source folder: there is only one copy.
        Write-Host "manifest : in place ($dstManifest)"
    }
    else {

    if (-not (Test-Path $dstManifest)) {
        New-Item -ItemType Directory -Path $dstManifest -Force | Out-Null
    }

    $srcFiles = Get-ChildItem $srcManifest -Recurse -File
    $copied = 0
    foreach ($f in $srcFiles) {
        $rel = $f.FullName.Substring($srcManifest.Length + 1)
        $dest = Join-Path $dstManifest $rel
        $destDir = Split-Path -Parent $dest
        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

        $same = (Test-Path $dest) -and
                ((Get-FileHash $f.FullName -Algorithm SHA256).Hash -eq (Get-FileHash $dest -Algorithm SHA256).Hash)

        if (-not $same) {
            Copy-Item $f.FullName $dest -Force
            Write-Host "manifest : updated $rel"
            $copied++
        }
    }

    $dstFiles = Get-ChildItem $dstManifest -Recurse -File
    $extras = @()
    foreach ($f in $dstFiles) {
        $rel = $f.FullName.Substring($dstManifest.Length + 1)
        if (-not (Test-Path (Join-Path $srcManifest $rel))) { $extras += $rel }
    }

    if ($extras.Count -gt 0) {
        if ($PruneManifest) {
            foreach ($rel in $extras) {
                Remove-Item (Join-Path $dstManifest $rel) -Force
                Write-Host "manifest : pruned $rel"
            }
        }
        else {
            Write-Host "manifest : WARNING $($extras.Count) file(s) on the stick are not in the repo:"
            foreach ($rel in $extras) { Write-Host "           $rel" }
            Write-Host "manifest : rerun with -PruneManifest to delete them"
        }
    }

    Write-Host "manifest : $($srcFiles.Count) file(s) in sync, $copied updated"
    }
}

# ---------------------------------------------------------------- cleanup ----
if (-not $KeepStage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host "done. Start the app with:"
Write-Host "  $StickRoot\PortableDev.cmd plan"
Write-Host "  $StickRoot\PortableDev.cmd apply"

