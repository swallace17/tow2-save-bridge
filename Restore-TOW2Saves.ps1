<#
.SYNOPSIS
    Pulls The Outer Worlds 2 saves out of Xbox connected storage and writes them
    in the local Steam format, so Steam Cloud (and Steam Deck / GeForce Now) can see them.

.DESCRIPTION
    Signing into an Xbox account in the Steam build of TOW2 diverts saves to the Xbox
    "wgs" container store. The Steam save folder then receives only SaveGameScreenshot.png,
    so Steam Cloud has nothing to sync.

    Two format differences have to be bridged:
      1. The container blob is named SavedState.dat; the local format reads SaveGame.dat.
         (Copy it under the wrong name and the save APPEARS in the load list but will
         not load -- metadata parses fine, the game just can't find the state file.)
      2. Inflated, the local file begins with a 23-byte SGDF chunk header. The Xbox blob
         replaces that with a bare 4-byte value. Everything from the "Player.dat" entry
         onward is identical.

    Metadata.dat stores the INFLATED size of the state file as an int32 at a variable
    offset (it shifts with slot- and character-name length), so it is located by value.

.EXAMPLE
    .\Restore-TOW2Saves.ps1            # dry run -- shows what would be written
    .\Restore-TOW2Saves.ps1 -Apply     # back up, then write

.NOTES
    Close the game before running. Afterwards: exit Steam fully, launch, and decline
    the Xbox sign-in, or the game will read the container store again.
#>
param(
    [switch]$Apply,
    [string]$SteamSaveDir = "$env:USERPROFILE\Saved Games\TheOuterWorlds2"
)

$ErrorActionPreference = 'Stop'

# 23-byte SGDF chunk header. Format-constant; verified against a locally-made save.
$SGDF_HEADER = [byte[]]@(
    0x05,0x00,0x00,0x00, 0x53,0x47,0x44,0x46,0x00, 0x01,0x00,0x00,0x00,
    0x27,0x00, 0x0A,0x02,0x00,0x00, 0xF4,0x03,0x00,0x00
)

function Inflate([byte[]]$b) {
    $ms = New-Object IO.MemoryStream(,$b); $ms.Position = 2
    $ds = New-Object IO.Compression.DeflateStream($ms, [IO.Compression.CompressionMode]::Decompress)
    $o  = New-Object IO.MemoryStream; $ds.CopyTo($o); $o.ToArray()
}
function Deflate([byte[]]$b) {
    $o  = New-Object IO.MemoryStream
    $zs = New-Object IO.Compression.ZLibStream($o, [IO.Compression.CompressionLevel]::Optimal)
    $zs.Write($b, 0, $b.Length); $zs.Dispose(); $o.ToArray()
}

# --- locate the Xbox container store -----------------------------------------
$pkgRoot = "$env:LOCALAPPDATA\Packages\Microsoft.OE-Arkansas_8wekyb3d8bbwe\SystemAppData\wgs"
if (-not (Test-Path $pkgRoot)) { throw "Xbox container store not found at $pkgRoot" }
$wgs = Get-ChildItem $pkgRoot -Directory | Where-Object { $_.Name -match '^[0-9A-F]{16}_' } |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $wgs) { throw "No user container folder under $pkgRoot" }
Write-Host "Xbox store : $($wgs.FullName)" -ForegroundColor DarkGray
Write-Host "Steam saves: $SteamSaveDir`n" -ForegroundColor DarkGray

if ($Apply -and (Get-Process -Name "*OuterWorlds*","*Arkansas*" -ErrorAction SilentlyContinue)) {
    throw "The Outer Worlds 2 is running. Close it before using -Apply."
}

# --- parse a container.N: blob name -> on-disk file --------------------------
function Get-Blobs([string]$dir) {
    $cf = Get-ChildItem $dir -Filter 'container.*' -File |
          Sort-Object { [int]($_.Name -replace '\D') } | Select-Object -Last 1
    if (-not $cf) { return @() }
    $b = [IO.File]::ReadAllBytes($cf.FullName)
    $count = [BitConverter]::ToInt32($b, 4)
    $out = @()
    for ($i = 0; $i -lt $count; $i++) {
        $off  = 8 + $i * 160
        $name = [Text.Encoding]::Unicode.GetString($b, $off, 128).TrimEnd([char]0)
        foreach ($go in @(128, 144)) {
            $gb = New-Object byte[] 16
            [Array]::Copy($b, $off + $go, $gb, 0, 16)
            $cand = Join-Path $dir ([Guid]::new($gb).ToString('N').ToUpper())
            if (Test-Path -LiteralPath $cand) {
                $out += [pscustomobject]@{ Name = $name; File = $cand }; break
            }
        }
    }
    $out
}

if ($Apply) {
    $bak = Join-Path ([Environment]::GetFolderPath('Desktop')) ("TOW2-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory $bak -Force | Out-Null
    if (Test-Path $SteamSaveDir) { Copy-Item $SteamSaveDir (Join-Path $bak 'SteamSaves') -Recurse }
    Copy-Item $wgs.FullName (Join-Path $bak 'wgs') -Recurse
    Write-Host "Backup: $bak`n" -ForegroundColor Cyan
}

$done = 0
foreach ($dir in Get-ChildItem $wgs.FullName -Directory) {
    $blobs = Get-Blobs $dir.FullName
    $meta  = $blobs | Where-Object Name -eq 'Metadata.dat'
    $state = $blobs | Where-Object Name -eq 'SavedState.dat'
    $shot  = $blobs | Where-Object Name -eq 'SaveGameScreenshot.png'
    if (-not ($meta -and $state)) { continue }

    # slot name is embedded in Metadata.dat as "<Slot>/SaveGameScreenshot.png"
    $m   = [IO.File]::ReadAllBytes($meta.File)
    $txt = [Text.Encoding]::ASCII.GetString($m)
    if ($txt -notmatch '([0-9A-F]{32}|Autosave\d{2}|Quicksave\d*)/SaveGameScreenshot\.png') {
        Write-Warning "$($dir.Name): could not determine slot name; skipping"; continue
    }
    $slot = $Matches[1]

    $sb  = [IO.File]::ReadAllBytes($state.File)
    $raw = if ($sb[0] -eq 0x78 -and $sb[1] -eq 0x9C) { Inflate $sb } else { $sb }
    if ([Text.Encoding]::ASCII.GetString($raw, 8, 10) -ne 'Player.dat') {
        Write-Warning "$slot : unexpected payload layout; skipping"; continue
    }

    # swap the 4-byte Xbox prefix for the 23-byte SGDF chunk header
    $body = New-Object byte[] ($raw.Length - 4)
    [Array]::Copy($raw, 4, $body, 0, $body.Length)
    $out = New-Object byte[] ($SGDF_HEADER.Length + $body.Length)
    [Array]::Copy($SGDF_HEADER, 0, $out, 0, $SGDF_HEADER.Length)
    [Array]::Copy($body, 0, $out, $SGDF_HEADER.Length, $body.Length)

    # patch the inflated-size field in Metadata.dat (variable offset -> find by value)
    $hits = @()
    for ($i = 0; $i -le $m.Length - 4; $i++) { if ([BitConverter]::ToInt32($m, $i) -eq $raw.Length) { $hits += $i } }
    if ($hits.Count -ne 1) { Write-Warning "$slot : $($hits.Count) size-field candidates; skipping"; continue }

    $comp = Deflate $out
    Write-Host ("{0,-34} {1,9} -> SaveGame.dat {2,9} (inflated {3})  meta@{4}" -f `
        $slot, $sb.Length, $comp.Length, $out.Length, $hits[0]) -ForegroundColor Green

    if ($Apply) {
        [Array]::Copy([BitConverter]::GetBytes([int]$out.Length), 0, $m, $hits[0], 4)
        $dest = Join-Path $SteamSaveDir $slot
        New-Item -ItemType Directory $dest -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $dest 'Metadata.dat'), $m)
        [IO.File]::WriteAllBytes((Join-Path $dest 'SaveGame.dat'), $comp)
        if ($shot) { Copy-Item -LiteralPath $shot.File -Destination (Join-Path $dest 'SaveGameScreenshot.png') -Force }
        Remove-Item (Join-Path $dest 'SavedState.dat') -Force -ErrorAction SilentlyContinue
    }
    $done++
}

Write-Host ""
if ($Apply) {
    Write-Host "APPLIED - $done save(s) restored to $SteamSaveDir" -ForegroundColor Green
    Write-Host "Exit Steam fully, launch the game, and decline the Xbox sign-in." -ForegroundColor Yellow
} else {
    Write-Host "DRY RUN - $done save(s) would be restored. Re-run with -Apply." -ForegroundColor Magenta
}
