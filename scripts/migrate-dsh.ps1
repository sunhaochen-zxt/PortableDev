# Migrates DSH NEXT (the DeepSeek Harness desktop app) into this PortableDev stick.
#
#   Migrate-DSH.cmd                     double-click: move app + data, declare, apply
#   scripts\migrate-dsh.ps1 -DryRun     show what would happen, change nothing
#
# Order of operations:
#   1. find the DSH NEXT install (default: a DSH-*-Portable folder on any drive)
#   2. refuse to run while DSH NEXT is open - the app and its data are both in use
#   3. move <install> to tools\dsh\<version>       (same volume: one rename)
#   4. copy %USERPROFILE%\.dsh to home\.dsh, then park the old folder as
#      .dsh.portabledev-old-<stamp>                 (nothing is deleted)
#   5. write manifest\manifest.d\dsh.toml and a [[home_dir]] for ~\.dsh
#   6. write DSH.cmd next to PortableDev.cmd
#   7. run "PortableDev.exe apply" so the junction and PATH exist on this machine
#
# Why a junction and not an environment variable: DSH resolves its data directory with
# os.homedir() (the Win32 user profile), and DSH_HOME only reaches its child processes;
# DSH_DESKTOP_NEXT_HOME is read as a fallback behind a location file. A junction at
# %USERPROFILE%\.dsh is what makes sessions, profiles, storages and .credentials.yaml
# travel on the stick without any of that.
#
# ASCII-only on purpose, like publish.ps1: Windows PowerShell 5.1 reads a BOM-less file
# as ANSI and a stray non-ASCII byte silently eats the following line.

[CmdletBinding()]
param(
    # Where DSH NEXT currently lives. Auto-detected when omitted.
    [string] $Source = '',
    # Stick root; defaults to the folder that carries the .portabledev-root marker.
    [string] $StickRoot = '',
    # Tool version / directory name; defaults to the exe's ProductVersion.
    [string] $Version = '',
    # User profile whose .dsh should be migrated. Defaults to %USERPROFILE%.
    [string] $UserHome = '',
    # Copy the app instead of moving it (leaves the original in place).
    [switch] $CopyOnly,
    # Copy the data but keep the real %USERPROFILE%\.dsh directory where it is.
    [switch] $KeepOldData,
    # Do not run "PortableDev.exe apply" at the end.
    [switch] $NoApply,
    # Reuse an existing tools\dsh\<version> instead of refusing.
    [switch] $Force,
    # Report only; no file system writes.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$ExeName = 'DSH NEXT.exe'

function Write-Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "   ok    $text" -ForegroundColor Green }
function Write-Note($text) { Write-Host "         $text" }
function Write-Warn($text) { Write-Host "   warn  $text" -ForegroundColor Yellow }
function Fail($text) {
    Write-Host ''
    Write-Host "ERROR: $text" -ForegroundColor Red
    Write-Host ''
    exit 1
}

function Get-SizeMB($path) {
    $sum = (Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum
    if (-not $sum) { return 0 }
    return [math]::Round($sum / 1MB)
}

# robocopy is the only Windows copy that reports per-file errors and handles long paths;
# 0-7 are success codes, 8+ mean at least one file failed.
function Invoke-Robocopy($from, $to, $what) {
    & robocopy $from $to /E /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $code = $LASTEXITCODE
    if ($code -ge 8) { Fail "$what failed (robocopy exit $code); nothing was deleted" }
    Write-Ok "$what ($code)"
}

function Test-ExeTree($dir) {
    return (Test-Path (Join-Path $dir $ExeName)) -and (Test-Path (Join-Path $dir 'resources\app.asar'))
}

# Runs PortableDev.exe and returns { Code; Text }. Two reasons not to call it directly:
# with $ErrorActionPreference = 'Stop' PowerShell turns the first line a console program
# writes to stderr into a terminating error, and $LASTEXITCODE is a global that would
# otherwise leak out as this script's own exit code.
function Invoke-PortableDev([string[]] $ArgumentList) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = & $pd @ArgumentList 2>&1
        return [pscustomobject]@{ Code = $LASTEXITCODE; Text = @($text) }
    }
    finally { $ErrorActionPreference = $previous }
}

# ---------------------------------------------------------------- 1. stick root ----
Write-Step 'Stick'

if (-not $StickRoot) {
    $probe = Split-Path -Parent $PSScriptRoot
    for ($i = 0; $i -lt 4 -and $probe; $i++) {
        if (Test-Path (Join-Path $probe '.portabledev-root')) { $StickRoot = $probe; break }
        $probe = Split-Path -Parent $probe
    }
}
if (-not $StickRoot -or -not (Test-Path (Join-Path $StickRoot '.portabledev-root'))) {
    Fail 'not a PortableDev stick: no .portabledev-root marker found. Pass -StickRoot <path>.'
}
$StickRoot = (Resolve-Path -LiteralPath $StickRoot).Path.TrimEnd('\')
Write-Ok $StickRoot

$pd = Join-Path $StickRoot 'app\PortableDev.exe'
$havePd = Test-Path $pd
if ($havePd) { Write-Note 'PortableDev.exe found; the manifest will be validated with it' }
else { Write-Warn 'app\PortableDev.exe is missing; the manifest cannot be validated or applied here' }

if (-not $UserHome) { $UserHome = $env:USERPROFILE }
if (-not $UserHome) { Fail 'cannot determine the user profile; pass -UserHome <path>.' }
$srcData = Join-Path $UserHome '.dsh'
$dstData = Join-Path $StickRoot 'home\.dsh'

# ---------------------------------------------------------------- 2. not running ----
Write-Step 'Checking that DSH NEXT is closed'

$running = @(Get-Process -Name 'DSH NEXT' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    if ($DryRun) {
        Write-Warn "DSH NEXT is running ($($running.Count) processes); a real run would refuse here"
    }
    else {
        Write-Note "DSH NEXT is running ($($running.Count) processes): $($running[0].Path)"
        Fail ('close DSH NEXT completely, then run this again from a normal console ' +
              '(or double-click Migrate-DSH.cmd). Its files and its session database are ' +
              'in use while it runs, and moving them under a live process would corrupt the app.')
    }
}
else { Write-Ok 'not running' }

# ---------------------------------------------------------------- 3. find install ----
Write-Step 'Finding the DSH NEXT install'

if ($Source) {
    if (-not (Test-Path (Join-Path $Source $ExeName))) {
        Fail "$ExeName not found in $Source"
    }
}
else {
    $candidates = @()
    $drives = @(Get-PSDrive -PSProvider FileSystem |
        Where-Object { $_.Root -match '^[A-Za-z]:\\$' } |
        Select-Object -ExpandProperty Root)
    foreach ($d in $drives) {
        try {
            $found = Get-ChildItem -LiteralPath $d -Directory -Filter 'DSH-*-Portable' -ErrorAction SilentlyContinue
            if ($found) { $candidates += $found }
        }
        catch { }
    }
    $candidates = @($candidates | Where-Object { Test-Path (Join-Path $_.FullName $ExeName) })

    if ($candidates.Count -eq 0) {
        # Nothing to move. If the tool is already in the stick, keep going: the rest of
        # the script (data, manifest, apply) is idempotent and may still be needed.
        $existing = @(Get-ChildItem -LiteralPath (Join-Path $StickRoot 'tools\dsh') -Directory -ErrorAction SilentlyContinue)
        if ($existing.Count -gt 0) {
            Write-Warn 'no unpacked DSH NEXT install found; assuming the app was already migrated'
            $Source = Join-Path $StickRoot ('tools\dsh\' + $existing[0].Name)
        }
        else {
            Fail ('no DSH NEXT install found. Unpack it first, then pass -Source <folder with ' +
                  "$ExeName>.")
        }
    }
    elseif ($candidates.Count -gt 1) {
        Write-Note 'several candidates:'
        foreach ($c in $candidates) { Write-Note ('  ' + $c.FullName) }
        Fail 'pass -Source <folder> to say which one to migrate'
    }
    else {
        $Source = $candidates[0].FullName
    }
}
$Source = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
Write-Ok $Source
Write-Note ("{0} MB in {1} files" -f (Get-SizeMB $Source),
    (Get-ChildItem -LiteralPath $Source -Recurse -File -Force).Count)

# Belt and braces beyond the name check above: a rebuilt or renamed binary is still a
# live process inside the tree we are about to move, and Windows would let the move fail
# halfway through the file list instead of refusing up front.
$fromSource = @()
foreach ($proc in @(Get-Process -ErrorAction SilentlyContinue)) {
    try {
        $exePath = $proc.Path
        if ($exePath -and $exePath.StartsWith($Source, [StringComparison]::OrdinalIgnoreCase)) {
            $fromSource += $proc
        }
    }
    catch { }
}
if ($fromSource.Count -gt 0) {
    $msg = "$($fromSource.Count) process(es) are still running from $Source"
    if ($DryRun) { Write-Warn $msg }
    else { Fail "$msg. Close DSH NEXT first; its files are in use." }
}
else { Write-Ok 'no process is using that folder' }

$inPlace = $Source -like (Join-Path $StickRoot 'tools\dsh\*')

# ---------------------------------------------------------------- 4. version ----
Write-Step 'Tool version'

if (-not $Version) {
    $info = (Get-Item -LiteralPath (Join-Path $Source $ExeName)).VersionInfo
    if ($info.ProductVersion -and $info.ProductVersion -match '^(\d+\.\d+\.\d+)') {
        $Version = $Matches[1]
    }
    elseif ((Split-Path $Source -Leaf) -match '(\d+\.\d+\.\d+)') {
        $Version = $Matches[1]
    }
}
if (-not $Version -or $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z\.\-]*$') {
    Fail 'cannot determine the version; pass -Version <x.y.z>.'
}
$toolRel = "tools\dsh\$Version"
$target = Join-Path $StickRoot $toolRel
Write-Ok $Version
Write-Note "target: $toolRel"

# ---------------------------------------------------------------- 5. move app ----
Write-Step 'Installing the app into the stick'

if (Test-ExeTree $target) {
    if (-not $Force) {
        Write-Ok "already there: $toolRel"
        Write-Note 'pass -Force to replace it'
    }
    else { Write-Warn 'target exists; -Force was given, keeping the existing copy' }
}
elseif ($inPlace) {
    Write-Ok "already in the stick: $toolRel"
}
elseif ($DryRun) {
    Write-Note "would move $Source -> $target"
}
else {
    $sameVolume = ($Source.Substring(0, 2) -eq $target.Substring(0, 2))
    $parent = Split-Path -Parent $target
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

    if (-not $CopyOnly -and $sameVolume) {
        if (Test-Path $target) { Fail "target already exists and is not a DSH tree: $target" }
        try {
            Move-Item -LiteralPath $Source -Destination $target
        }
        catch {
            Fail ("could not move the install (a file inside it is still open, is DSH really " +
                  "closed?): " + $_.Exception.Message)
        }
        Write-Ok 'moved (single rename, same volume)'
    }
    else {
        Invoke-Robocopy $Source $target 'copied'
        if (-not (Test-ExeTree $target)) { Fail "copy is incomplete: $target" }

        # Count before parking anything: a half copy must never cost the original.
        $srcCount = @(Get-ChildItem -LiteralPath $Source -Recurse -File -Force).Count
        $dstCount = @(Get-ChildItem -LiteralPath $target -Recurse -File -Force).Count
        if ($dstCount -lt $srcCount) {
            Fail "$dstCount of $srcCount files copied; the original is untouched"
        }
        Write-Ok "verified $dstCount files"

        if ($CopyOnly) { Write-Note "original left in place: $Source" }
        else {
            # Never delete an installed app. Parking is a rename: atomic, instant, and it
            # fails cleanly if a file inside is still open - unlike a recursive delete,
            # which would leave a half-removed install behind (this bit a test run).
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $parkedApp = "$Source.migrated-$stamp"
            try { Move-Item -LiteralPath $Source -Destination $parkedApp }
            catch { Fail ("the copy is complete at $target but the original could not be " +
                          "parked (still in use?): " + $_.Exception.Message) }
            Write-Ok "original parked as $parkedApp"
            Write-Note 'delete it once DSH starts from the stick'
        }
    }

    if (-not (Test-ExeTree $target)) { Fail "install is incomplete after the move: $target" }
    Write-Ok ("installed: {0} MB in {1} files" -f (Get-SizeMB $target),
        (Get-ChildItem -LiteralPath $target -Recurse -File -Force).Count)
}

# ---------------------------------------------------------------- 6. move data ----
Write-Step 'Migrating user data (~\.dsh: sessions, profiles, credentials)'

if (-not (Test-Path -LiteralPath $srcData)) {
    Write-Warn "$srcData does not exist; a fresh home will be created on first launch"
}
else {
    $srcFiles = @(Get-ChildItem -LiteralPath $srcData -Recurse -File -Force -ErrorAction SilentlyContinue)
    $sessions = @(Get-ChildItem -LiteralPath (Join-Path $srcData 'sessions') -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'session*.jsonl*' })
    Write-Note ("{0} files, {1} session file(s), {2} MB" -f $srcFiles.Count, $sessions.Count, (Get-SizeMB $srcData))

    if ($DryRun) {
        Write-Note "would copy $srcData -> $dstData"
        if (-not $KeepOldData) { Write-Note "would park the old folder as $srcData.portabledev-old-<stamp>" }
    }
    else {
        if (-not (Test-Path $dstData)) { New-Item -ItemType Directory -Force -Path $dstData | Out-Null }
        Invoke-Robocopy $srcData $dstData 'copied'

        # Verify every source file landed with the same length before anything is moved
        # away: a truncated session file is the one loss this script must not cause.
        $missing = @()
        foreach ($f in $srcFiles) {
            $rel = $f.FullName.Substring($srcData.Length).TrimStart('\')
            $copy = Join-Path $dstData $rel
            if (-not (Test-Path -LiteralPath $copy)) { $missing += $rel; continue }
            if ((Get-Item -LiteralPath $copy).Length -ne $f.Length) { $missing += $rel }
        }
        if ($missing.Count -gt 0) {
            Write-Note 'missing or short:'
            $missing | Select-Object -First 10 | ForEach-Object { Write-Note ('  ' + $_) }
            Fail 'the copy is incomplete; the original .dsh is untouched, fix the cause and re-run'
        }

        $copiedSessions = @(Get-ChildItem -LiteralPath (Join-Path $dstData 'sessions') -Recurse -File -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'session*.jsonl*' })
        if ($copiedSessions.Count -lt $sessions.Count) {
            Fail "only $($copiedSessions.Count) of $($sessions.Count) session files made it; original untouched"
        }
        Write-Ok ("all {0} files verified on the stick ({1} session file(s))" -f $srcFiles.Count, $copiedSessions.Count)

        if ($KeepOldData) {
            Write-Warn '-KeepOldData: the real .dsh stays where it is'
            Write-Note 'apply will therefore SKIP the ~\.dsh link (a real directory with data is never moved silently)'
            Write-Note 'run "PortableDev.cmd apply --adopt-home" yourself if you want it linked'
        }
        else {
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $parked = "$srcData.portabledev-old-$stamp"
            Move-Item -LiteralPath $srcData -Destination $parked
            Write-Ok "old folder parked as $parked"
            Write-Note 'delete it once DSH starts correctly from the stick'
        }
    }
}

# ---------------------------------------------------------------- 7. tool manifest ----
Write-Step 'Writing the tool manifest'

$toolToml = @'
schema = 1

[tool]
id       = 'dsh'
version  = '@@VERSION@@'
title    = 'DSH NEXT (DeepSeek Harness desktop)'
kind     = 'prebuilt'
dir      = 'tools/dsh/@@VERSION@@'
provides = ['dsh']
# The app keeps nothing inside this folder: Electron's userData, sessions, profiles,
# storages and .credentials.yaml all go to %USERPROFILE%\.dsh, which manifest.toml
# links onto the stick. No [[path]] entry: it is a GUI, launched through DSH.cmd.
notes    = 'DSH NEXT desktop; data lives in the ~\.dsh home link, launch through DSH.cmd'

# GUI app, so there is no --version to ask and launching it would sit there until the
# 60 s timeout. `dir` exits non-zero when a target is missing.
[[verify]]
run    = 'cmd.exe /c dir /b "DSH NEXT.exe" resources\app.asar'
expect = 'app.asar'

# The bundled Node runtime for the agent host; without it the app starts and cannot work.
[[verify]]
run    = 'cmd.exe /c dir /b resources\runtime\x64'
expect = 'primary-runtime'
'@
$toolToml = $toolToml.Replace('@@VERSION@@', $Version)

$toolPath = Join-Path $StickRoot "manifest\manifest.d\dsh.toml"
if ($DryRun) { Write-Note "would write $toolPath" }
else {
    $dir = Split-Path -Parent $toolPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllText($toolPath, ($toolToml -replace "`r`n", "`n") + "`n",
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok 'manifest\manifest.d\dsh.toml'

    # The repo keeps a copy of the manifest so a fresh clone can be published.
    $mirror = Join-Path $StickRoot "source\manifest\manifest.d\dsh.toml"
    if (Test-Path (Split-Path -Parent $mirror)) {
        Copy-Item -LiteralPath $toolPath -Destination $mirror -Force
        Write-Ok 'source\manifest\manifest.d\dsh.toml (mirror)'
    }
}

# ---------------------------------------------------------------- 8. home link ----
Write-Step 'Declaring the ~\.dsh home link'

$manifestPath = Join-Path $StickRoot 'manifest\manifest.toml'
if (-not (Test-Path $manifestPath)) { Fail "manifest not found: $manifestPath" }

$homeBlock = @'

# DSH NEXT keeps everything under `~/.dsh`: the conversations in sessions\, the profiles,
# the storages, the Electron userData and .credentials.yaml. It resolves that directory
# from the OS home (os.homedir()), so HOME cannot reach it and DSH_HOME only covers its
# child processes; the junction below is what makes all of it travel on the stick.
[[home_dir]]
for  = 'DSH NEXT conversations, profiles and credentials'
root = 'USERPROFILE'
path = '.dsh'
'@

$declared = $false
foreach ($line in [System.IO.File]::ReadAllLines($manifestPath)) {
    if ($line -match "^\s*path\s*=\s*'\.dsh'\s*$") { $declared = $true; break }
}

if ($declared) { Write-Ok 'already declared in manifest\manifest.toml' }
elseif ($DryRun) { Write-Note 'would add a [[home_dir]] for USERPROFILE\.dsh to manifest\manifest.toml' }
else {
    $backupDir = Join-Path $StickRoot 'state\backup'
    if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Force -Path $backupDir | Out-Null }
    $backup = Join-Path $backupDir ('manifest.toml.' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Copy-Item -LiteralPath $manifestPath -Destination $backup -Force

    $lines = [System.IO.File]::ReadAllLines($manifestPath)
    $out = New-Object System.Collections.Generic.List[string]
    $inserted = $false
    foreach ($line in $lines) {
        # [[home_dir]] must sit at top level; putting it before [apply] keeps the file
        # readable and matches how the other slots are written.
        if (-not $inserted -and $line -match '^\s*\[apply\]\s*$') {
            foreach ($b in ($homeBlock -split '\r?\n')) { $out.Add($b) }
            $inserted = $true
        }
        $out.Add($line)
    }
    if (-not $inserted) { foreach ($b in ($homeBlock -split '\r?\n')) { $out.Add($b) } }
    [System.IO.File]::WriteAllLines($manifestPath, $out)
    Write-Ok 'added [[home_dir]] USERPROFILE\.dsh'

    if ($havePd) {
        $check = Invoke-PortableDev @('plan', '--root', $StickRoot)
        if ($check.Code -eq 3) {
            Copy-Item -LiteralPath $backup -Destination $manifestPath -Force
            Fail 'the edited manifest does not parse; restored the previous one (exit code 3)'
        }
        elseif ($check.Code -ne 0) {
            Write-Warn "plan returned $($check.Code) after the edit; inspect manifest\manifest.toml"
        }
        else { Write-Ok 'manifest parses and plans cleanly' }
    }
}

# ---------------------------------------------------------------- 9. launcher ----
Write-Step 'Writing the launcher'

$cmdPath = Join-Path $StickRoot 'DSH.cmd'
$launcher = @'
@echo off
rem Portable DSH NEXT: the app reads its data directory from the OS home, so
rem %USERPROFILE%\.dsh is a junction into home\.dsh ([[home_dir]] in manifest\manifest.toml).
rem Run "PortableDev.cmd apply" once per machine to create that link; without it DSH
rem writes to the machine instead of the stick. The version in the path is pinned on
rem purpose, like Firefox.cmd: the manifest owns versions, so this line is updated
rem together with a tool upgrade.
setlocal
start "" /d "%~dp0tools\dsh\@@VERSION@@" "%~dp0tools\dsh\@@VERSION@@\DSH NEXT.exe"
'@
$launcher = ($launcher.Replace('@@VERSION@@', $Version) -replace "`r`n", "`n") + "`n"

if ($DryRun) { Write-Note "would write $cmdPath" }
else {
    [System.IO.File]::WriteAllText($cmdPath, $launcher, (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok "DSH.cmd (launch: tools\dsh\$Version\DSH NEXT.exe)"
}

# ---------------------------------------------------------------- 10. apply ----
Write-Step 'Applying to this machine (C:\Dev junction, PATH, ~\.dsh link)'

if ($NoApply) {
    Write-Warn '-NoApply: nothing was written to the registry'
    Write-Note 'run "PortableDev.cmd apply" when you are ready; until then ~\.dsh is not linked'
}
elseif (-not $havePd) {
    Write-Warn 'app\PortableDev.exe is missing; run PortableDev.cmd apply after fixing that'
}
elseif ($DryRun) { Write-Note 'would run: PortableDev.exe apply' }
else {
    Write-Note 'one UAC prompt may appear (it creates the C:\Dev junction)'
    $applied = Invoke-PortableDev @('apply', '--root', $StickRoot, '--yes')
    $applied.Text | Where-Object { "$_" -match '\S' } | ForEach-Object { Write-Host "         $_" }
    if ($applied.Code -ne 0) {
        Write-Warn "apply returned exit code $($applied.Code) - run 'PortableDev.cmd apply' by hand"
        Write-Note 'exit 8 = UAC declined, 7 = junction failed, 9 = home link failed, 3 = manifest'
    }
    else { Write-Ok 'applied' }
}

# ---------------------------------------------------------------- 11. check ----
Write-Step 'Checking the result'

if ($DryRun) {
    Write-Note 'dry run: no verification, no changes'
}
elseif ($havePd) {
    $verified = Invoke-PortableDev @('verify', '--root', $StickRoot)
    $out = $verified.Text
    $dsh = @($out | Where-Object { "$_" -match '^(PASS|FAIL)\s+dsh\s' })
    foreach ($line in $dsh) { Write-Host "   $line" }
    $summary = @($out | Where-Object { "$_" -match '^\d+/\d+ passed$' })
    if ($summary.Count -gt 0) { Write-Note $summary[-1] }

    $linked = $false
    $slot = Join-Path $UserHome '.dsh'
    if (Test-Path -LiteralPath $slot) {
        $item = Get-Item -LiteralPath $slot -Force
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { $linked = $true }
    }
    if ($linked) { Write-Ok "$slot -> the stick" }
    elseif (-not $NoApply) { Write-Warn "$slot is not a junction; 'PortableDev.cmd apply' will create it" }
}

Write-Step 'Summary'
if (Test-Path -LiteralPath $target) {
    Write-Note "app      : $toolRel ($(Get-SizeMB $target) MB)"
}
else {
    Write-Note "app      : $toolRel (not installed yet)"
}
Write-Note "data     : home\.dsh ($((Get-ChildItem -LiteralPath $dstData -Recurse -File -Force -ErrorAction SilentlyContinue).Count) files)"
if (-not (Test-Path -LiteralPath $dstData) -and $DryRun) {
    Write-Note '           (not migrated yet)'
}
if (Test-Path -LiteralPath (Join-Path $dstData 'sessions')) {
    $n = @(Get-ChildItem -LiteralPath (Join-Path $dstData 'sessions') -Recurse -File -Force |
        Where-Object { $_.Name -like 'session*.jsonl*' }).Count
    Write-Note "sessions : $n conversation file(s) carried over"
}
Write-Note 'launch   : DSH.cmd (in the stick root)'
if (-not $DryRun -and -not $NoApply -and $havePd) {
    Write-Note 'the ~\.dsh link is in place, so DSH starts with the stick data'
}
Write-Host ''

# Explicit: without it the process exit code is whatever $LASTEXITCODE held last, so a
# successful migration looked like a failure to Migrate-DSH.cmd.
exit 0
