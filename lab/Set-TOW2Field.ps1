param(
    [Parameter(Mandatory)][string]$Slot,
    [Parameter(Mandatory)][int]$RecordOffset,   # absolute offset of the chunk header in the inflated payload
    [Parameter(Mandatory)][int]$FieldOffset,    # offset within that record's payload
    [Parameter(Mandatory)][int]$Value,
    [ValidateSet('u32','u8')][string]$Type = 'u32',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$root = "$env:USERPROFILE\Saved Games\TheOuterWorlds2"
$sg   = Join-Path $root "$Slot\SaveGame.dat"
$md   = Join-Path $root "$Slot\Metadata.dat"
if (-not (Test-Path $sg)) { throw "no SaveGame.dat for slot $Slot" }

function Inflate([byte[]]$b){ $ms=New-Object IO.MemoryStream(,$b); $ms.Position=2
    $ds=New-Object IO.Compression.DeflateStream($ms,[IO.Compression.CompressionMode]::Decompress)
    $o=New-Object IO.MemoryStream; $ds.CopyTo($o); $o.ToArray() }
function Deflate([byte[]]$b){ $o=New-Object IO.MemoryStream
    $zs=New-Object IO.Compression.ZLibStream($o,[IO.Compression.CompressionLevel]::Optimal)
    $zs.Write($b,0,$b.Length); $zs.Dispose(); $o.ToArray() }

$raw = Inflate ([IO.File]::ReadAllBytes($sg))

# sanity: is there really a chunk header here?
$magic = [Text.Encoding]::ASCII.GetString($raw, $RecordOffset+4, 4)
if ($magic -notmatch '^[A-Z]{4}$') { throw "no chunk header at $RecordOffset (found '$magic')" }
$len = [BitConverter]::ToInt32($raw, $RecordOffset+21)
if ($FieldOffset -lt 0 -or $FieldOffset + 4 -gt $len) { throw "field offset $FieldOffset outside payload (len $len)" }

$abs = $RecordOffset + 25 + $FieldOffset
$old = if ($Type -eq 'u32') { [BitConverter]::ToInt32($raw,$abs) } else { [int]$raw[$abs] }

Write-Host ("{0} record @{1}  payload+{2}  [{3}]" -f $magic, $RecordOffset, $FieldOffset, $Type)
Write-Host ("  {0}  ->  {1}" -f $old, $Value) -ForegroundColor Yellow

if (-not $Apply) { Write-Host "`nDRY RUN. Re-run with -Apply." -ForegroundColor Magenta; return }

$bak = Join-Path ([Environment]::GetFolderPath('Desktop')) ("TOW2-edit-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + "-$Slot")
New-Item -ItemType Directory $bak -Force | Out-Null
Copy-Item (Join-Path $root $Slot) $bak -Recurse -Force
Write-Host "Backup: $bak" -ForegroundColor Cyan

if ($Type -eq 'u32') { [Array]::Copy([BitConverter]::GetBytes([int]$Value), 0, $raw, $abs, 4) }
else                 { $raw[$abs] = [byte]$Value }

# length is unchanged by an in-place poke, so metadata needs no patch -- but verify
$m = [IO.File]::ReadAllBytes($md)
$hits = @(); for ($i=0; $i -le $m.Length-4; $i++) { if ([BitConverter]::ToInt32($m,$i) -eq $raw.Length) { $hits += $i } }
if ($hits.Count -ne 1) { Write-Warning "metadata size field: $($hits.Count) matches for $($raw.Length) -- not patched" }
else { Write-Host "metadata size field @$($hits[0]) still matches ($($raw.Length)) -- no patch needed" -ForegroundColor DarkGray }

[IO.File]::WriteAllBytes($sg, (Deflate $raw))
Write-Host "APPLIED to $Slot" -ForegroundColor Green
