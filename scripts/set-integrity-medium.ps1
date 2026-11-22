<#
.SYNOPSIS
    Raise a PortableDev stick, and everything under it, to Medium integrity.

.DESCRIPTION
    A stick that was first written by a confined (sandboxed) session can carry a Low
    integrity label on its root. Every folder created under it inherits that label and
    low-integrity processes may then reach into the toolchain. This script raises the
    root and every existing child to Medium and makes Medium the inherited label.

    It changes integrity labels only: permissions, owners and file contents are left
    alone. Run it elevated - changing a label needs WRITE_OWNER on the object.

.PARAMETER Path
    Stick root (the folder holding .portabledev-root). Defaults to this script's parent
    folder when that folder is a stick root.

.PARAMETER NoRecurse
    Only label the root, not the existing children.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\set-integrity-medium.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\set-integrity-medium.ps1 -Path Z:\PortableDev
#>
[CmdletBinding()]
param(
    [string] $Path = '',
    [switch] $NoRecurse
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Path)) {
    $candidate = Split-Path -Parent $PSScriptRoot
    if (Test-Path (Join-Path $candidate '.portabledev-root')) { $Path = $candidate }
}
if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
    throw "cannot determine the stick root; pass -Path <folder> (the one holding .portabledev-root)"
}

$Path = (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\')
Write-Host "stick : $Path"
Write-Host ("scope : " + $(if ($NoRecurse) { 'the folder only' } else { 'the folder and everything under it' }))

$aclArgs = @($Path, '/setintegritylevel', '(OI)(CI)M', '/C', '/Q')
if (-not $NoRecurse) { $aclArgs += '/T' }
& icacls.exe @aclArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed with exit code $LASTEXITCODE" }

# Read a handful of labels back rather than trusting the exit code.
$probe = @($Path)
$probe += Get-ChildItem -LiteralPath $Path -Directory -Force -ErrorAction SilentlyContinue |
    Select-Object -First 4 -ExpandProperty FullName
foreach ($p in $probe) {
    $line = (icacls.exe $p 2>$null | Select-String 'Mandatory Label') -join ' '
    if ($line -match 'Medium') { Write-Host ("  OK    {0}" -f $p) }
    elseif (-not $line) { Write-Host ("  none  {0}" -f $p) }
    else { Write-Warning ("  {0} -> {1}" -f $p, $line.Trim()) }
}
Write-Host 'done.'
