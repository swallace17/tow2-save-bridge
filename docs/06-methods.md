# 06 — Investigation methods

Notes on what actually worked, for whoever picks this up next. The format details are in the
other files; this is about how to find more of them.

## Differential analysis on game-written saves

The single most productive technique, and it costs no game loads.

Make two saves a short time apart, then compare. `lab/Diff-TOW2Save.ps1` walks both record
streams and aligns records by `(entry, magic, id, field, occurrence)`, so it reports **which
property records changed** rather than which bytes moved. Four minutes of play produced 5
changed records out of 22 in `Player.dat`; a raw binary diff would have buried that.

**To find a specific field**, make the pair as tight as possible: save, change exactly one
thing without moving or advancing time, save again. A 17-second pair with one skill point
spent isolated the change to two bytes.

**To distinguish pointers from constants**, compare saves whose entry sizes differ and look
for values that changed by *exactly* the size delta. That is what identified the self-pointers:
entry size ±572, pointer at `+32` ±572, while neighbouring fields were random or constant.

## Sentinel poking

Far faster than one save-pair per field. Clone a save, write a **distinct** value into every
candidate offset at once, load once, and read the mapping off the in-game screen.

That mapped all twelve skills in a single load — and caught that Melee and Guns are
transposed, which extrapolating from four known offsets had got wrong.

Pick sentinel values that are unmistakable but harmless (31–44 worked well). If an offset
turns out not to be what you thought, a small number is unlikely to break anything.

## Structural signatures over content matching

Locate things by **shape**, not by their current value. The character name is found via
"a CSHF record whose length is exactly `21 + L` for the `u32` at payload+16" rather than by
searching for the name string — because the moment you edit either the payload name or the
metadata name, the two diverge and a content search finds the wrong one or nothing.

Test any candidate signature across several saves and require exactly one match.

## Anchors, not absolute offsets

Record positions move between saves. The skill record was at `@3082765` in one save and
`@3083359` in another, with a different `id` and a different length. Locate it by searching
for a stable string anchor (`OEIUserSetting.Difficulty.Multiplier.PlayerDamage`) and walking
back to the enclosing header.

Always take the **last** anchor match — the base layer holds a stale copy.

## Reading headers correctly before concluding anything

Applying the 25-byte record layout to a 23-byte section header produced a length of
`1505427456`, which made 36 KB of ordinary data look like an undecoded mystery region and
sent the investigation in the wrong direction for a while. When a length field looks absurd,
suspect the header layout before suspecting the data.

## Verifying against the right side of an edit

An edit only shifts bytes **after** it. Any check on data before the edit point is
structurally incapable of failing. See the worked example in
[05 — Editing rules](05-editing-rules.md#verification-that-actually-proves-something).

## Environment notes

Two PowerShell traps cost time here, both silent:

- `[ordered]@{}` with integer keys indexes by **position**, not key, so `$h[236]` is an
  out-of-range lookup returning `$null` — which then casts to `0` and writes zeros. Use
  parallel arrays.
- String comparison is **case-insensitive** by default, so `"Earnest Jones" -ne "EARNEST JONES"`
  is `$false`. Use `-cne` when case matters.

Scanning a multi-megabyte payload byte-by-byte in PowerShell is too slow — a full-entry
pointer scan timed out at 5 minutes. Drop to C# via `Add-Type` for anything that touches
every offset.
