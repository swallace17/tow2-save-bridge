param(
    [Parameter(Mandatory)][string]$A,
    [Parameter(Mandatory)][string]$B,
    [string]$Entry,           # filter: only this top-level entry (e.g. Player.dat)
    [string]$Magic,           # filter: only this chunk magic (e.g. CSHF)
    [int]$Top = 60,
    [switch]$Inventory,       # just dump A's structure, no diff
    [switch]$IncludeUnkeyed   # include records whose key appears only once per side
)

$ErrorActionPreference = 'Stop'
$SaveRoot = "$env:USERPROFILE\Saved Games\TheOuterWorlds2"

function Resolve-Save([string]$p) {
    foreach ($c in @($p, (Join-Path $p 'SaveGame.dat'), (Join-Path $SaveRoot $p), (Join-Path $SaveRoot "$p\SaveGame.dat"))) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { return (Resolve-Path -LiteralPath $c).Path }
    }
    throw "Could not resolve save: $p"
}

function Get-Payload([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    if ($b[0] -eq 0x78 -and $b[1] -eq 0x9C) {
        $ms = New-Object IO.MemoryStream(,$b); $ms.Position = 2
        $ds = New-Object IO.Compression.DeflateStream($ms, [IO.Compression.CompressionMode]::Decompress)
        $o  = New-Object IO.MemoryStream; $ds.CopyTo($o); return $o.ToArray()
    }
    $b
}

# top-level entries: [u32 nameLen][name.dat NUL][u32 size][01]
function Get-Entries([byte[]]$raw, [string]$s) {
    $out = @()
    foreach ($m in [regex]::Matches($s, "([\x05-\x40])\x00\x00\x00([A-Za-z0-9_]{2,60}\.dat)\x00")) {
        $name = $m.Groups[2].Value
        if ($name.Length + 1 -ne [int][char]$m.Groups[1].Value) { continue }
        $szOff = $m.Index + 4 + $name.Length + 1
        if ($szOff + 4 -gt $raw.Length) { continue }
        $out += [pscustomobject]@{ Name = $name; Start = $m.Index; DataStart = $szOff + 5; Size = [BitConverter]::ToInt32($raw, $szOff) }
    }
    $out | Sort-Object Start
}

# chunk records: [u32 5]["MAGC" NUL][u32 id][u32 ver][u32 field][u32 len][payload]
function Get-Records([byte[]]$raw, [string]$s, $entries) {
    $recs = [Collections.Generic.List[object]]::new()
    $seen = @{}
    foreach ($m in [regex]::Matches($s, "\x05\x00\x00\x00([A-Z]{4})\x00")) {
        $o = $m.Index
        if ($o + 25 -gt $raw.Length) { continue }
        $len = [BitConverter]::ToInt32($raw, $o + 21)
        if ($len -lt 0 -or $len -gt 262144 -or $o + 25 + $len -gt $raw.Length) { continue }

        $ent = 'root'
        foreach ($e in $entries) { if ($o -ge $e.DataStart) { $ent = $e.Name } else { break } }

        $key = "{0}|{1}|{2}|{3}" -f $ent, $m.Groups[1].Value, [BitConverter]::ToInt32($raw,$o+9), [BitConverter]::ToInt32($raw,$o+17)
        $n = 1 + $(if ($seen.ContainsKey($key)) { $seen[$key] } else { 0 })
        $seen[$key] = $n

        $pay = New-Object byte[] $len
        [Array]::Copy($raw, $o + 25, $pay, 0, $len)

        $recs.Add([pscustomobject]@{
            Key = "$key#$n"; Entry = $ent; Magic = $m.Groups[1].Value
            Id = [BitConverter]::ToInt32($raw,$o+9); Field = [BitConverter]::ToInt32($raw,$o+17)
            Offset = $o; Len = $len; Payload = $pay
        })
    }
    $recs
}

function Describe([byte[]]$pay) {
    $bits = @()
    $ps = [Text.Encoding]::Latin1.GetString($pay)
    foreach ($m in [regex]::Matches($ps, "([\x02-\x7F])\x00\x00\x00([\x20-\x7E]{1,160})\x00")) {
        if ($m.Groups[2].Value.Length + 1 -eq [int][char]$m.Groups[1].Value) { $bits += '"' + $m.Groups[2].Value + '"' }
    }
    if ($bits.Count) { return ($bits | Select-Object -First 2) -join ' ' }
    $hex = (($pay | Select-Object -First 16) | ForEach-Object { $_.ToString('X2') }) -join ' '
    if ($pay.Length -gt 16) { $hex += ' ...' }
    $hex
}

# interpret a differing byte range every way that fits
function Interpret([byte[]]$p, [int]$off, [int]$width) {
    $out = @()
    if ($width -le 4 -and $off + 4 -le $p.Length) {
        $i = [BitConverter]::ToInt32($p, $off)
        $f = [BitConverter]::ToSingle($p, $off)
        $out += "i32=$i"
        if ([Math]::Abs($f) -ge 1e-4 -and [Math]::Abs($f) -lt 1e9) { $out += ("f32=" + [Math]::Round($f,4)) }
    }
    if ($width -le 8 -and $off + 8 -le $p.Length) {
        $d = [BitConverter]::ToDouble($p, $off)
        if ([Math]::Abs($d) -ge 1e-4 -and [Math]::Abs($d) -lt 1e12) { $out += ("f64=" + [Math]::Round($d,4)) }
    }
    if ($width -eq 1) { $out += ("u8=" + $p[$off]) }
    if ($out.Count) { $out -join '  ' } else { '' }
}

function Diff-Payload([byte[]]$x, [byte[]]$y) {
    $runs = @()
    if ($x.Length -ne $y.Length) { return @([pscustomobject]@{ Off = -1; Text = "length $($x.Length) -> $($y.Length)" }) }
    $i = 0
    while ($i -lt $x.Length) {
        if ($x[$i] -ne $y[$i]) {
            $st = $i
            while ($i -lt $x.Length -and $x[$i] -ne $y[$i]) { $i++ }
            $w = $i - $st
            $ox = (($x[$st..($i-1)]) | ForEach-Object { $_.ToString('X2') }) -join ' '
            $oy = (($y[$st..($i-1)]) | ForEach-Object { $_.ToString('X2') }) -join ' '
            $ix = Interpret $x $st $w; $iy = Interpret $y $st $w
            $t = "@{0,-5} {1}  ->  {2}" -f $st, $ox, $oy
            if ($ix -or $iy) { $t += "    [ $ix  ->  $iy ]" }
            $runs += [pscustomobject]@{ Off = $st; Text = $t }
        } else { $i++ }
    }
    $runs
}

# ---------------------------------------------------------------------------
$pa = Resolve-Save $A
$rawA = Get-Payload $pa; $sA = [Text.Encoding]::Latin1.GetString($rawA)
$entA = Get-Entries $rawA $sA; $recA = Get-Records $rawA $sA $entA

if ($Inventory) {
    Write-Host "$pa  ($($rawA.Length) bytes inflated)`n" -ForegroundColor Cyan
    Write-Host "--- top-level entries ---" -ForegroundColor Yellow
    $entA | ForEach-Object { "  @{0,-9} {1,-46} {2}" -f $_.Start, $_.Name, $_.Size }
    Write-Host "`n--- records by entry / magic ---" -ForegroundColor Yellow
    $recA | Group-Object Entry | Sort-Object { $_.Group[0].Offset } | ForEach-Object {
        $byMagic = ($_.Group | Group-Object Magic | Sort-Object Count -Descending |
                    ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', '
        "  {0,-46} {1,5} recs   {2}" -f $_.Name, $_.Count, $byMagic
    }
    return
}

$pb = Resolve-Save $B
$rawB = Get-Payload $pb; $sB = [Text.Encoding]::Latin1.GetString($rawB)
$entB = Get-Entries $rawB $sB; $recB = Get-Records $rawB $sB $entB

Write-Host "A  $pa   ($($rawA.Length) bytes, $($recA.Count) records)" -ForegroundColor DarkGray
Write-Host "B  $pb   ($($rawB.Length) bytes, $($recB.Count) records)" -ForegroundColor DarkGray

$mapA = @{}; foreach ($r in $recA) { $mapA[$r.Key] = $r }
$mapB = @{}; foreach ($r in $recB) { $mapB[$r.Key] = $r }

$keys = [Collections.Generic.HashSet[string]]::new()
foreach ($k in $mapA.Keys) { [void]$keys.Add($k) }
foreach ($k in $mapB.Keys) { [void]$keys.Add($k) }

$changed = @(); $onlyA = @(); $onlyB = @()
foreach ($k in $keys) {
    $ra = $mapA[$k]; $rb = $mapB[$k]
    if ($Entry -and (($ra ?? $rb).Entry -notlike "*$Entry*")) { continue }
    if ($Magic -and (($ra ?? $rb).Magic -ne $Magic))          { continue }
    if     (-not $rb) { $onlyA += $ra }
    elseif (-not $ra) { $onlyB += $rb }
    else {
        if ([Convert]::ToBase64String($ra.Payload) -ne [Convert]::ToBase64String($rb.Payload)) {
            $changed += [pscustomobject]@{ Key = $k; A = $ra; B = $rb }
        }
    }
}

Write-Host ("`n{0} changed   {1} only in A   {2} only in B" -f $changed.Count, $onlyA.Count, $onlyB.Count) -ForegroundColor Yellow
Write-Host ("-" * 100)

$sorted = $changed | Sort-Object { $_.A.Offset }
foreach ($c in ($sorted | Select-Object -First $Top)) {
    Write-Host ("{0}  id={1} field={2}  len {3}->{4}  @{5}" -f `
        $c.Key, $c.A.Id, $c.A.Field, $c.A.Len, $c.B.Len, $c.A.Offset) -ForegroundColor Green
    $da = Describe $c.A.Payload; $db = Describe $c.B.Payload
    if ($da -ne $db) { Write-Host "    A: $da"; Write-Host "    B: $db" }
    foreach ($r in (Diff-Payload $c.A.Payload $c.B.Payload | Select-Object -First 8)) {
        Write-Host ("    " + $r.Text) -ForegroundColor Gray
    }
}
if ($sorted.Count -gt $Top) { Write-Host "`n... $($sorted.Count - $Top) more changed records (raise -Top)" -ForegroundColor DarkGray }

if ($IncludeUnkeyed -and ($onlyA.Count -or $onlyB.Count)) {
    Write-Host "`n--- only in A ---" -ForegroundColor Magenta
    $onlyA | Sort-Object Offset | Select-Object -First 25 | ForEach-Object { "  {0,-44} @{1,-9} {2}" -f $_.Key, $_.Offset, (Describe $_.Payload) }
    Write-Host "`n--- only in B ---" -ForegroundColor Magenta
    $onlyB | Sort-Object Offset | Select-Object -First 25 | ForEach-Object { "  {0,-44} @{1,-9} {2}" -f $_.Key, $_.Offset, (Describe $_.Payload) }
}
