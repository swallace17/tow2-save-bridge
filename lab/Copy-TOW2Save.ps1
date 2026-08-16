param(
    [Parameter(Mandatory)][string]$Slot,
    [string]$NewSlot,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = "$env:USERPROFILE\Saved Games\TheOuterWorlds2"
$src  = Join-Path $root $Slot
if (-not (Test-Path (Join-Path $src 'SaveGame.dat'))) { throw "no SaveGame.dat for slot $Slot" }
if ($Slot -notmatch '^[0-9A-F]{32}$') { throw "only 32-hex manual-save slots can be cloned (got '$Slot')" }

if (-not $NewSlot) { $NewSlot = [Guid]::NewGuid().ToString('N').ToUpper() }
if ($NewSlot -notmatch '^[0-9A-F]{32}$') { throw "new slot name must be 32 hex chars" }
$dst = Join-Path $root $NewSlot
if (Test-Path $dst) { throw "destination slot already exists: $NewSlot" }

# the slot name is embedded in Metadata.dat twice: standalone, and in "<Slot>/SaveGameScreenshot.png".
# Both names are 32 chars, so this is a length-neutral in-place substitution.
$mb  = [IO.File]::ReadAllBytes((Join-Path $src 'Metadata.dat'))
$old = [Text.Encoding]::ASCII.GetBytes($Slot)
$new = [Text.Encoding]::ASCII.GetBytes($NewSlot)

$hits = @()
for ($i = 0; $i -le $mb.Length - 32; $i++) {
    $ok = $true
    for ($j = 0; $j -lt 32; $j++) { if ($mb[$i+$j] -ne $old[$j]) { $ok = $false; break } }
    if ($ok) { $hits += $i }
}

Write-Host "$Slot  ->  $NewSlot"
Write-Host ("  slot name in Metadata.dat at: " + ($hits -join ', '))
if ($hits.Count -ne 2) { Write-Warning "expected 2 occurrences, found $($hits.Count)" }

if (-not $Apply) { Write-Host "`nDRY RUN. Re-run with -Apply." -ForegroundColor Magenta; return }

Copy-Item $src $dst -Recurse
foreach ($h in $hits) { [Array]::Copy($new, 0, $mb, $h, 32) }
[IO.File]::WriteAllBytes((Join-Path $dst 'Metadata.dat'), $mb)

Write-Host "CLONED -> $dst" -ForegroundColor Green
Write-Host "  (SaveGame.dat contains no slot reference, so it is copied verbatim)" -ForegroundColor DarkGray
$NewSlot
