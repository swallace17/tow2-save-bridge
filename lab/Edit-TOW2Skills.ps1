<#
  Skill editor for The Outer Worlds 2 saves.

    .\Edit-TOW2Skills.ps1 -List
    .\Edit-TOW2Skills.ps1 -Slot <slot>
    .\Edit-TOW2Skills.ps1 -Slot <slot> -Set @{Hack=50; Speech=40} -Points 10 -Apply
    .\Edit-TOW2Skills.ps1 -Slot <slot> -CloneFirst -Set @{Guns=100} -Apply

  Values written are the BASE. Tagged skills (star in-game) display +2 on top,
  so to hit a displayed number on a tagged skill, write target-2.
#>
[CmdletBinding()]
param(
    [string]$Slot,
    [hashtable]$Set,
    [int]$Points = -1,
    [int]$Bits = -1,
    [switch]$CloneFirst,
    [switch]$List,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$Root   = "$env:USERPROFILE\Saved Games\TheOuterWorlds2"
$ANCHOR = 'OEIUserSetting.Difficulty.Multiplier.PlayerDamage'
$BASE   = 236     # payload offset of skill[0]
$STRIDE = 4
$PTS    = 337     # payload offset of Points Available (u8)

# internal order -- note Melee precedes Guns, which is NOT the display order
$SKILLS = @('Melee','Guns','Sneak','Lockpick','Engineering','Explosives',
            'Hack','Medical','Science!','Observation','Speech','Leadership')

function Inflate([byte[]]$b){
    if($b[0] -ne 0x78){ return $b }
    $ms=New-Object IO.MemoryStream(,$b); $ms.Position=2
    $ds=New-Object IO.Compression.DeflateStream($ms,[IO.Compression.CompressionMode]::Decompress)
    $o=New-Object IO.MemoryStream; $ds.CopyTo($o); $o.ToArray()
}
function Deflate([byte[]]$b){
    $o=New-Object IO.MemoryStream
    $zs=New-Object IO.Compression.ZLibStream($o,[IO.Compression.CompressionLevel]::Optimal)
    $zs.Write($b,0,$b.Length); $zs.Dispose(); $o.ToArray()
}
# --- bits ------------------------------------------------------------------
# Player.dat holds 84-byte entries: [u32 2][u32 1][u32 id][u32 0x00010B01][17 x u32].
# Bits is payload[12] of one of them. The entry id is an allocation counter and shifts
# between sessions, so anchor instead on a neighbouring entry with a constant payload
# that sits exactly 154 bytes earlier.  See docs/04-player-data.md.
$script:BitsLandmark = @(33554432, 1161527296, 0, 11, 256, 256)
$script:BitsDelta    = 218

function Get-LivePlayerDat([byte[]]$raw){
    $s = [Text.Encoding]::Latin1.GetString($raw)
    $m = [regex]::Matches($s, "\x0B\x00\x00\x00Player\.dat\x00")
    if($m.Count -eq 0){ return $null }
    $pre = $m[$m.Count-1].Index                       # LAST = live layer
    [pscustomobject]@{ Payload = $pre+19; Size = [BitConverter]::ToInt32($raw,$pre+15) }
}

function Get-BitsOffset([byte[]]$raw, $live){
    if(-not $live){ return -1 }
    $found = -1; $count = 0
    for($r=0; $r+84 -le $live.Size; $r++){
        if([BitConverter]::ToInt32($raw,$live.Payload+$r) -ne 2){ continue }
        if([BitConverter]::ToInt32($raw,$live.Payload+$r+4) -ne 1){ continue }
        if([BitConverter]::ToInt32($raw,$live.Payload+$r+12) -ne 0x00010B01){ continue }
        $ok = $true
        for($k=0; $k -lt $script:BitsLandmark.Count; $k++){
            if([BitConverter]::ToInt32($raw,$live.Payload+$r+16+4*(11+$k)) -ne $script:BitsLandmark[$k]){ $ok=$false; break }
        }
        if($ok){ $found=$r; $count++ }
    }
    if($count -ne 1){ return -1 }        # ambiguous or absent -> refuse
    $off = $found + $script:BitsDelta
    if($off+4 -gt $live.Size){ return -1 }
    $off
}

function Get-MetaStrings([string]$path){
    $m=[IO.File]::ReadAllBytes($path); $s=[Text.Encoding]::Latin1.GetString($m)
    $out=@()
    foreach($x in [regex]::Matches($s,"([\x02-\x7F])\x00\x00\x00([\x20-\x7E]{1,120})\x00")){
        if($x.Groups[2].Value.Length+1 -eq [int][char]$x.Groups[1].Value){ $out += $x.Groups[2].Value }
    }
    $out
}

# ---------------------------------------------------------------- list
if($List -or -not $Slot){
    Get-ChildItem $Root -Directory | ForEach-Object {
        $sg = Join-Path $_.FullName 'SaveGame.dat'
        $md = Join-Path $_.FullName 'Metadata.dat'
        if(-not (Test-Path $sg)){ return }
        # strings run: GMHF, <slot>, <character>, <slot>/SaveGameScreenshot.png, version, Release
        $who = '?'
        if(Test-Path $md){
            $st = @(Get-MetaStrings $md)
            if($st.Count -gt 2){ $who = $st[2] }
        }
        [pscustomobject]@{
            Slot      = $_.Name
            Character = $who
            Saved     = (Get-Item $sg).LastWriteTime.ToString('MM-dd HH:mm:ss')
            MB        = [math]::Round((Get-Item $sg).Length/1MB,2)
        }
    } | Sort-Object Saved | Format-Table -AutoSize
    if(-not $Slot){ return }
}

# ---------------------------------------------------------------- clone
if($CloneFirst){
    if(-not $Apply){ throw "-CloneFirst requires -Apply" }
    $clone = & (Join-Path $PSScriptRoot 'Copy-TOW2Save.ps1') -Slot $Slot -Apply | Select-Object -Last 1
    Write-Host "editing clone: $clone`n" -ForegroundColor Cyan
    $Slot = $clone
}

$sgPath = Join-Path $Root "$Slot\SaveGame.dat"
$mdPath = Join-Path $Root "$Slot\Metadata.dat"
if(-not (Test-Path $sgPath)){ throw "no SaveGame.dat for slot $Slot" }
if($Apply -and (Get-Process -Name "*OuterWorlds*","*Arkansas*" -ErrorAction SilentlyContinue)){
    throw "The Outer Worlds 2 is running. Close it before applying edits."
}

$raw = Inflate ([IO.File]::ReadAllBytes($sgPath))
$s   = [Text.Encoding]::Latin1.GetString($raw)

# ------------------------------------------------- locate the live record
# Saves may carry a stale base layer; the LAST anchor hit is the live one.
$rxHdr = [regex]"\x05\x00\x00\x00[A-Z]{4}\x00"
$rec = -1
foreach($a in [regex]::Matches($s,[regex]::Escape($ANCHOR))){
    $winStart=[Math]::Max(0,$a.Index-8000)
    $hdrs=$rxHdr.Matches($s.Substring($winStart,$a.Index-$winStart))
    if($hdrs.Count){ $rec = $winStart + $hdrs[$hdrs.Count-1].Index }
}
if($rec -lt 0){ throw "could not locate the skill record (anchor not found)" }

$magic = [Text.Encoding]::ASCII.GetString($raw,$rec+4,4)
$len   = [BitConverter]::ToInt32($raw,$rec+21)
$p     = $rec + 25
if($BASE + $STRIDE*$SKILLS.Count -gt $len){ throw "skill array runs past the record payload" }

# sanity: skill values should be small non-negative numbers
$cur = 0..($SKILLS.Count-1) | ForEach-Object { [BitConverter]::ToInt32($raw, $p + $BASE + $STRIDE*$_) }
if(($cur | Where-Object { $_ -lt 0 -or $_ -gt 1000 }).Count){
    throw "values at the expected offsets don't look like skills ($($cur -join ',')) -- refusing to write"
}

Write-Host "$Slot   $magic record @$rec  len=$len" -ForegroundColor DarkGray
Write-Host ""

# ---------------------------------------------------------------- report / edit
$changes = @()
for($i=0;$i -lt $SKILLS.Count;$i++){
    $name = $SKILLS[$i]
    $off  = $BASE + $STRIDE*$i
    $old  = $cur[$i]
    $new  = $old
    if($Set){
        foreach($k in $Set.Keys){ if($k -eq $name){ $new = [int]$Set[$k] } }
    }
    if($new -ne $old){ $changes += [pscustomobject]@{ Name=$name; Off=$off; Old=$old; New=$new } }
    $flag = if($new -ne $old){ '  ->  {0}' -f $new } else { '' }
    "  {0,-12} +{1,-5} {2,4}{3}" -f $name,$off,$old,$flag
}

$curPts = $raw[$p+$PTS]
$newPts = if($Points -ge 0){ $Points } else { $curPts }
"  {0,-12} +{1,-5} {2,4}{3}" -f 'Points',$PTS,$curPts,$(if($newPts -ne $curPts){'  ->  {0}' -f $newPts}else{''})

$live    = Get-LivePlayerDat $raw
$bitsOff = Get-BitsOffset $raw $live
if($bitsOff -ge 0){
    $curBits = [BitConverter]::ToInt32($raw,$live.Payload+$bitsOff)
    $newBits = if($Bits -ge 0){ $Bits } else { $curBits }
    "  {0,-12} +{1,-5} {2,4}{3}" -f 'Bits',$bitsOff,$curBits,$(if($newBits -ne $curBits){'  ->  {0}' -f $newBits}else{''})
} else {
    $newBits = -1
    Write-Host "  Bits         (landmark not found in this save -- field unavailable)" -ForegroundColor DarkGray
    if($Bits -ge 0){ Write-Warning "-Bits was given but the field could not be located; it will not be changed." }
}

if($Set){
    $unknown = @($Set.Keys | Where-Object { $_ -notin $SKILLS })
    if($unknown.Count){ Write-Warning "unknown skill name(s): $($unknown -join ', ')" }
}
$bitsChanged = ($bitsOff -ge 0 -and $newBits -ge 0 -and $newBits -ne $curBits)
if(-not $changes.Count -and $newPts -eq $curPts -and -not $bitsChanged){ Write-Host "`nnothing to change." -ForegroundColor DarkGray; return }
if(-not $Apply){ Write-Host "`nDRY RUN. Re-run with -Apply." -ForegroundColor Magenta; return }

# ---------------------------------------------------------------- write
$bak = Join-Path ([Environment]::GetFolderPath('Desktop')) ("TOW2-skills-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + "-$Slot")
New-Item -ItemType Directory $bak -Force | Out-Null
Copy-Item (Join-Path $Root $Slot) $bak -Recurse -Force

$before = $raw.Length
foreach($c in $changes){ [Array]::Copy([BitConverter]::GetBytes([int]$c.New),0,$raw,$p+$c.Off,4) }
if($newPts -gt 255){ throw "Points must be 0-255 (single byte)" }
$raw[$p+$PTS] = [byte]$newPts
if($bitsChanged){ [Array]::Copy([BitConverter]::GetBytes([int]$newBits),0,$raw,$live.Payload+$bitsOff,4) }
if($raw.Length -ne $before){ throw "payload length changed -- aborting" }

# in-place edits leave length alone, so the metadata size field must still match
$m = [IO.File]::ReadAllBytes($mdPath)
$hits=@(); for($i=0;$i -le $m.Length-4;$i++){ if([BitConverter]::ToInt32($m,$i) -eq $raw.Length){ $hits+=$i } }
if($hits.Count -ne 1){ throw "metadata size field: expected 1 match for $($raw.Length), found $($hits.Count)" }

[IO.File]::WriteAllBytes($sgPath,(Deflate $raw))

# verify by reading back
$v = Inflate ([IO.File]::ReadAllBytes($sgPath))
$bad = @()
foreach($c in $changes){ if([BitConverter]::ToInt32($v,$p+$c.Off) -ne $c.New){ $bad += $c.Name } }
if($v[$p+$PTS] -ne $newPts){ $bad += 'Points' }
if($bitsChanged -and [BitConverter]::ToInt32($v,$live.Payload+$bitsOff) -ne $newBits){ $bad += 'Bits' }
if($bad.Count){ throw "read-back mismatch: $($bad -join ', ')" }

Write-Host "`nAPPLIED and verified. Backup: $bak" -ForegroundColor Green
Write-Host "Tagged skills display +2 over the stored value." -ForegroundColor DarkGray
