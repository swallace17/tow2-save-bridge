# 07 — Open questions

Things that are unmapped, unverified, or would be the obvious next work.

## Unidentified fields

- **`2A 00 00 00` (42)** — the 4-byte prefix the Xbox blob carries where the local file has
  the 23-byte `SGDF` header.
- **`Player.dat +40`, `+68`, `+76`, `+84`** — in the `PSHF` body alongside the two known
  self-pointers. `+40` and `+76` look like GUID material; `+68` is near-constant across saves
  (a count?); `+84` is erratic.
- **CSHF `+17`** — a `u32` that tracks the record `id` but is not equal to it.
- **`Metadata.dat`** — the `u32` between the state size and the inner blob sizes, and the
  ~20-byte trailer after the inner zlib stream (a SHA-1 over the metadata is the obvious
  guess; it is demonstrably not validated against the state file).
- **`containers.index`** internals. Not needed so far, but parsing it would be more robust
  than recovering slot names from metadata.

## Unmapped structures

- **Whether more self-pointers exist.** A scan of one full entry found only the two at `+24`
  and `+32` — but the scan's heuristic only recognises pointers to string-table entries. A
  pointer to a record or an arbitrary structure would not have been detected. Worth
  re-running the differential-delta technique on a larger set of save pairs.
- **Other section magics.** `ASHF`, `BLHF`, `WSHF`, `TMHF` and ~15 singletons are assumed to
  use either the 23-byte section or 25-byte record layout, but only `SGDF`/`PSHF`/`CSHF` are
  confirmed.
- **Entries with no chunk headers** — `Quests.dat`, `GlobalVars.dat`, `Factions.dat`,
  `Conversations.dat` contain no `[u32 5][MAGC][NUL]` patterns at all, so they use some other
  internal shape. These are where quest flags and world state would live.

## Unmapped game systems

Perks, Traits, Flaws and Background appear as asset-path references in the
[PlayerInfoComponent class list](04-player-data.md#the-playerinfocomponent-class-list--a-registry-not-the-acquired-set),
but **that list is a class registry, not the acquired set** — it carries all 28 flaws in
every save and its ordering is unstable. The real grant state has not been found.

The next concrete step is a **tight save-pair**: save, take a perk, save again without moving,
then diff with `lab/Diff-TOW2Save.ps1`. Two things make this tractable:

- The class registry gives a known-good marker to check the diff against — the new perk's
  asset path will appear there, so any *other* changed record in the same save is a candidate
  for the actual grant state.
- A save that carries a base layer already contains a free before/after pair, since the base
  snapshot predates recent acquisitions. That is how the registry's behaviour was established
  without playing at all.

Traits and Background are set at character creation and never change, so a save-pair will not
isolate them. The tractable approach there is a length-neutral swap of one asset path for
another of identical length (`SuaveTrait` → `WittyTrait`, both 10 characters) on a clone, and
see what the game reports.

## Unverified assumptions

- **Quicksave slot naming** — assumed `Quicksave*`; none appeared in the sample data.
- **The metadata offset formula** `179 + 2·len(slot) + len(character)` fits three data points
  against three unknowns. Consistent, not proven. Tools locate the field by value instead.
- **Build coverage** — everything here is from `1.2.0.1 Release` on Windows. Nothing has been
  checked against another build, and the tooling hardcodes offsets that a patch could move.
  The structural anchors should survive; the fixed offsets (skills at `+236`, pointers at
  `+24`/`+32`) may not.

## Bigger pieces

- **Steam → Xbox conversion**, the reverse direction, would require writing `containers.index`
  and `container.<N>` correctly. Not attempted, and a malformed container could plausibly
  damage the Xbox-side store rather than just failing.
- **Why saves accumulate a base layer.** The `SavedState.dat` nested container is understood
  structurally, but not *when* the game decides to write one, or whether it grows without
  bound across many saves.
