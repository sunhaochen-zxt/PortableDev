[CmdletBinding()]
param(
    [string]$Root = 'E:\PortableDev'
)

$ErrorActionPreference = 'Stop'
$tar = 'C:\Windows\system32\tar.exe'
$sevenZip = Join-Path $Root 'downloads\7zip\25.01\7zr.exe'

$items = @(
    @{ T = 'ninja';      V = '1.13.2';   A = 'downloads\ninja\1.13.2\ninja-win.zip';                              K = 'zip';   X = 'ninja.exe' }
    @{ T = 'jq';         V = '1.7.1';    A = 'downloads\jq\1.7.1\jq-windows-amd64.exe';                           K = 'exe';   X = 'jq.exe'; N = 'jq.exe' }
    @{ T = 'ripgrep';    V = '15.2.0';   A = 'downloads\ripgrep\15.2.0\ripgrep-15.2.0-x86_64-pc-windows-msvc.zip'; K = 'zip'; X = 'rg.exe' }
    @{ T = 'python';     V = '3.13.17';  A = 'downloads\python\3.13.17\cpython-3.13.17+20261021-x86_64-pc-windows-msvc-install_only.tar.gz'; K = 'targz'; X = 'python.exe' }
    @{ T = 'nodejs';     V = '24.22.1';  A = 'downloads\nodejs\24.22.1\node-v24.22.1-win-x64.zip';                K = 'zip';   X = 'node.exe' }
    @{ T = 'git';        V = '2.56.0.3'; A = 'downloads\git\2.56.0.3\PortableGit-2.56.0.3-64-bit.7z.exe';         K = '7zsfx'; X = 'cmd\git.exe' }
    @{ T = 'cmake';      V = '4.4.4';    A = 'downloads\cmake\4.4.4\cmake-4.4.4-windows-x86_64.zip';              K = 'zip';   X = 'bin\cmake.exe' }
    @{ T = 'llvm-mingw'; V = '20261020'; A = 'downloads\llvm-mingw\20261020\llvm-mingw-20261020-ucrt-x86_64.zip'; K = 'zip';   X = 'bin\clang.exe' }
)

function Invoke-Extract([string]$archive, [string]$dest, [string]$kind) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        switch ($kind) {
            'zip'   { & $tar -xf $archive -C $dest 2>$null }
            'targz' { & $tar -xzf $archive -C $dest 2>$null }
            '7zsfx' { & $sevenZip x -y "-o$dest" $archive 2>$null }
            default { throw "unknown kind $kind" }
        }
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prev }
}

$results = @()
foreach ($it in $items) {
    $archive = Join-Path $Root $it.A
    $dest = Join-Path $Root ("tools\{0}\{1}" -f $it.T, $it.V)
    Write-Host ("[{0} {1}]" -f $it.T, $it.V)

    if (-not (Test-Path $archive)) { Write-Host "  SKIP: archive missing"; $results += "MISSING $($it.T)"; continue }

    if ($it.K -eq 'exe') {
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item $archive (Join-Path $dest $it.N) -Force
        $results += "OK $($it.T)"
        continue
    }

    $tmp = Join-Path $Root ("state\tmp\{0}-{1}" -f $it.T, $it.V)
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        $rc = Invoke-Extract $archive $tmp $it.K
        if ($rc -ne 0) { Write-Host ("  warn: extractor exit {0} (exFAT timestamps), verifying by artifact" -f $rc) }
        $entries = @(Get-ChildItem $tmp -Force)
        if ($entries.Count -eq 1 -and $entries[0].PSIsContainer) {
            $inner = $entries[0].FullName
            if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
            New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
            Move-Item -LiteralPath $inner -Destination $dest
        }
        else {
            if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
            New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
            Move-Item -LiteralPath $tmp -Destination $dest
            $tmp = $null
        }
        $probe = Join-Path $dest $it.X
        if (Test-Path $probe) { $results += "OK $($it.T)" }
        else { $results += "CHECK $($it.T): expected $($it.X) not found"; Write-Host "  WARN: $($it.X) not found" }
    }
    finally {
        if ($tmp -and (Test-Path $tmp)) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ''
Write-Host '=== extract summary ==='
$results | ForEach-Object { Write-Host $_ }
