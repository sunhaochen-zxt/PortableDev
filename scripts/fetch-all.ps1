[CmdletBinding()]
param(
    [string]$Root = 'E:\PortableDev',
    [int]$Conns = 16,
    [int]$BigConns = 24
)

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'download.ps1'
$logDir = Join-Path $Root 'state\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ('fetch-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
Start-Transcript -Path $log -Force | Out-Null

$items = @(
    @{ T='ninja';      V='1.13.2';   F='ninja-win.zip';        U='https://github.com/ninja-build/ninja/releases/download/v1.13.2/ninja-win.zip'; C=$Conns }
    @{ T='jq';         V='1.7.1';    F='jq-windows-amd64.exe'; U='https://github.com/jqlang/jq/releases/download/jq-1.7.1/jq-windows-amd64.exe'; C=$Conns }
    @{ T='7zip';       V='25.01';    F='7z2501-x64.exe';       U='https://www.7-zip.org/a/7z2501-x64.exe'; C=$Conns }
    @{ T='7zip';       V='25.01';    F='7zr.exe';              U='https://www.7-zip.org/a/7zr.exe'; C=4 }
    @{ T='ripgrep';    V='15.2.0';   F='ripgrep-15.2.0-x86_64-pc-windows-msvc.zip'; U='https://github.com/BurntSushi/ripgrep/releases/download/15.2.0/ripgrep-15.2.0-x86_64-pc-windows-msvc.zip'; C=$Conns }
    @{ T='python';     V='3.13.17';  F='cpython-3.13.17+20261021-x86_64-pc-windows-msvc-install_only.tar.gz'; U='https://registry.npmmirror.com/-/binary/python-build-standalone/20261021/cpython-3.13.17%2B20261021-x86_64-pc-windows-msvc-install_only.tar.gz'; C=$Conns }
    @{ T='nodejs';     V='24.22.1';  F='node-v24.22.1-win-x64.zip'; U='https://registry.npmmirror.com/-/binary/node/v24.22.1/node-v24.22.1-win-x64.zip'; C=$Conns }
    @{ T='git';        V='2.56.0.3'; F='PortableGit-2.56.0.3-64-bit.7z.exe'; U='https://registry.npmmirror.com/-/binary/git-for-windows/v2.56.0.windows.3/PortableGit-2.56.0.3-64-bit.7z.exe'; C=$Conns }
    @{ T='cmake';      V='4.4.4';    F='cmake-4.4.4-windows-x86_64.zip'; U='https://github.com/Kitware/CMake/releases/download/v4.4.4/cmake-4.4.4-windows-x86_64.zip'; C=$BigConns }
    @{ T='llvm-mingw'; V='20261020'; F='llvm-mingw-20261020-ucrt-x86_64.zip'; U='https://github.com/mstorsjo/llvm-mingw/releases/download/20261020/llvm-mingw-20261020-ucrt-x86_64.zip'; C=$BigConns }
)

$ok = @(); $fail = @()
$i = 0
foreach ($it in $items) {
    $i++
    Write-Host ("[{0}/{1}] {2} {3}" -f $i, $items.Count, $it.T, $it.V)
    $outDir = Join-Path $Root ("downloads\{0}\{1}" -f $it.T, $it.V)
    $out = Join-Path $outDir $it.F
    $done = $false
    for ($try = 1; $try -le 2 -and -not $done; $try++) {
        try {
            & $script -Url $it.U -Out $out -Conns $it.C
            $done = $true
        }
        catch {
            Write-Host ("FAIL(try {0}) {1}: {2}" -f $try, $it.T, $_.Exception.Message)
            if ($try -eq 2) { $fail += [pscustomobject]@{ Tool = $it.T; Version = $it.V; Url = $it.U; Error = $_.Exception.Message } }
            elseif (Test-Path $out) { Remove-Item $out -Force -ErrorAction SilentlyContinue }
        }
    }
    if ($done) { $ok += $out }
}

Write-Host ''
Write-Host '=== summary ==='
foreach ($f in $ok) {
    Write-Host ("OK   {0,10:N1} MB  {1}" -f ((Get-Item $f).Length / 1MB), $f)
}
foreach ($f in $fail) { Write-Host ("FAIL {0} {1} :: {2}" -f $f.Tool, $f.Version, $f.Error) }
$sum = ($ok | ForEach-Object { (Get-Item $_).Length } | Measure-Object -Sum).Sum
Write-Host ("total {0:N1} MB, ok={1}, fail={2}" -f ($sum / 1MB), $ok.Count, $fail.Count)

$shaFile = Join-Path $Root 'downloads\checksums.txt'
$lines = foreach ($f in $ok) {
    $rel = $f.Substring($Root.Length).TrimStart('\')
    $h = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLower()
    '{0}  {1}' -f $h, $rel
}
if ($lines) { $lines | Set-Content -LiteralPath $shaFile -Encoding ASCII }
Write-Host "checksums -> $shaFile"

Stop-Transcript | Out-Null
if ($fail.Count -gt 0) { exit 1 }
