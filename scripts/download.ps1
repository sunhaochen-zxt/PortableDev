[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Url,
    [Parameter(Mandatory)][string]$Out,
    [long]$Size = 0,
    [int]$Conns = 16,
    [int]$Retries = 3
)

$ErrorActionPreference = 'Stop'
$curl = (Get-Command curl.exe).Source

function Get-RemoteSize([string]$u) {
    $h = & $curl -sSLI -m 40 $u 2>$null
    $len = 0
    foreach ($line in $h) {
        if ($line -match '^content-length:\s*(\d+)') { $len = [long]$Matches[1] }
    }
    return $len
}

if ($Size -le 0) { $Size = Get-RemoteSize $Url }
if ($Size -le 0) { throw "cannot determine size for $Url" }

if ((Test-Path $Out) -and ((Get-Item $Out).Length -eq $Size)) {
    Write-Host ("SKIP {0} (already complete)" -f (Split-Path -Leaf $Out))
    return
}

$dir = Split-Path -Parent $Out
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$tmp = Join-Path $dir ('.parts-' + [IO.Path]::GetFileName($Out))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$name = Split-Path -Leaf $Out
$probe = & $curl -sSL -o NUL -r 0-1023 -m 40 -w '%{http_code}' $Url 2>$null
$rangeOk = ($probe -eq '206')

$sw = [Diagnostics.Stopwatch]::StartNew()

if (-not $rangeOk) {
    Write-Host ("DOWN {0}  {1:N1} MB  single-conn (no range support)" -f $name, ($Size / 1MB))
    & $curl -sSL --fail -o $Out -m 3600 $Url
}
else {
    $chunk = [long][math]::Max(4194304, [math]::Ceiling($Size / $Conns))
    $parts = @()
    for ($i = 0; $i -lt $Conns; $i++) {
        $start = [long]$i * $chunk
        if ($start -ge $Size) { break }
        $end = [math]::Min($start + $chunk - 1, $Size - 1)
        $parts += [pscustomobject]@{
            Index = $i; Start = $start; End = $end
            File  = (Join-Path $tmp ('part{0:D3}' -f $i))
        }
    }

    Write-Host ("DOWN {0}  {1:N1} MB  {2} conns" -f $name, ($Size / 1MB), $parts.Count)

    $procs = @{}
    foreach ($p in $parts) {
        $procs[$p.Index] = Start-Process -FilePath $curl -PassThru -WindowStyle Hidden -ArgumentList @(
            '-sSL', '--fail', '--retry', '4', '--retry-all-errors', '--retry-delay', '2',
            '--connect-timeout', '20', '-o', $p.File, '--range', "$($p.Start)-$($p.End)", '-m', '3600', $Url
        )
    }
    foreach ($k in $procs.Keys) { $procs[$k].WaitForExit() }

    function Get-BadParts($parts) {
        $bad = @()
        foreach ($p in $parts) {
            $exp = $p.End - $p.Start + 1
            $got = if (Test-Path $p.File) { (Get-Item $p.File).Length } else { 0 }
            if ($got -ne $exp) { $bad += $p }
        }
        return $bad
    }

    $bad = Get-BadParts $parts
    for ($attempt = 1; $attempt -le $Retries -and $bad.Count -gt 0; $attempt++) {
        Write-Host ("RETRY {0}  {1} part(s) of {2}" -f $attempt, $bad.Count, $name)
        $ps = @{}
        foreach ($p in $bad) {
            $ps[$p.Index] = Start-Process -FilePath $curl -PassThru -WindowStyle Hidden -ArgumentList @(
                '-sSL', '--fail', '--retry', '4', '--retry-all-errors', '--retry-delay', '2',
                '--connect-timeout', '20', '-o', $p.File, '--range', "$($p.Start)-$($p.End)", '-m', '3600', $Url
            )
        }
        foreach ($k in $ps.Keys) { $ps[$k].WaitForExit() }
        $bad = Get-BadParts $parts
    }
    if ($bad.Count -gt 0) { throw ("incomplete: {0} part(s) failed for {1}" -f $bad.Count, $Out) }

    $fs = [IO.File]::Open($Out, [IO.FileMode]::Create, [IO.FileAccess]::Write)
    try {
        foreach ($p in ($parts | Sort-Object Index)) {
            $in = [IO.File]::OpenRead($p.File)
            try { $in.CopyTo($fs) } finally { $in.Dispose() }
        }
    }
    finally { $fs.Dispose() }
}

$sw.Stop()
$final = (Get-Item $Out).Length
if ($final -ne $Size) {
    Remove-Item $Out -Force -ErrorAction SilentlyContinue
    throw ("size mismatch: got {0}, expected {1}" -f $final, $Size)
}
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

$sha = (Get-FileHash $Out -Algorithm SHA256).Hash.ToLower()
Write-Host ("OK   {0}  {1:N1} MB  {2:N0}s  {3:N0} KB/s" -f $name, ($final / 1MB), $sw.Elapsed.TotalSeconds, ($final / 1KB / $sw.Elapsed.TotalSeconds))
Write-Host ("SHA256 {0}  {1}" -f $sha, $name)
