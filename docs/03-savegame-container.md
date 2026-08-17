# 03 — `SaveGame.dat` container

zlib-compressed (`78 9C`). A 3 MB payload compresses to roughly 450 KB; a 6 MB payload to
roughly 940 KB.

Inflated, the structure is:

```
[SGDF section header, 23 bytes]
[entry] [entry] [entry] …
```

## Top-level entries

```
[u32 nameLen]  name length including NUL
[ASCII + NUL]  e.g. "Player.dat"
[u32 size]     payload size in bytes
[payload]      starts IMMEDIATELY after the size field
```

So `payloadStart = prefixPos + 4 + nameLen + 4`. There is no flag byte between the size and
the payload — the `01` that appears there is the first byte of the payload itself.

**Entries chain exactly**: `payloadStart + size` equals the next entry's prefix position.
Verified for 48 of 56 entries in a sampled save; the exceptions are nested containers (see
below) and are themselves consistent.

### Observed entries

`Player.dat`, `CharacterCreation_Lite_<hash>.dat`, `LevelRefs.dat`, `CrowdManager.dat`,
`Companion7-12.dat`, `Calendar.dat`, `GlobalVars.dat`, `Quests.dat`, `Conversations.dat`,
`Factions.dat`, `Teams.dat`, `CompanionManager.dat`, `Beacons.dat`, `Tutorial.dat`,
`Travel.dat`, `AIGroupBehavior.dat`, `RadioManager.dat`, `BroadcastManager.dat`,
`Achievement.dat`, plus per-map entries like `0201_PI_P_<hash>.dat`.

Only `Player.dat` has been decoded. Several entries (`Quests.dat`, `GlobalVars.dat`,
`Factions.dat`, `Conversations.dat`) contain no chunk-record headers at all, so they use a
different internal shape — unmapped.

## Where the bytes go

Measured on a mid-game save: 28 live-layer entries totalling **3,143,057 bytes**.

| | bytes | share |
|---|---|---|
| Map / world entries (4) | 2,912,523 | **92.7%** |
| `Conversations`, `Teams`, `Beacons`, `Quests` | 115,430 | 3.7% |
| `Player.dat` | 57,782 | 1.8% |
| `Companion7–12.dat` (6) | 42,252 | 1.3% |
| Everything else (18 entries) | 15,070 | 0.5% |

**The save is overwhelmingly persisted world state**, one entry per visited map. The largest
in this sample:

```
0201_PI_P_<hash>.dat        2,236,568 bytes
  ASHF  x 2,996             one per persisted actor
  CSHF  x 9,121             roughly 3 per actor
  WSHF  x 1
  2,114 distinct strings, 445 of them Unreal blueprint classes (_C)
```

The string vocabulary is exactly what you'd expect of restored actors —
`Accessory_Glasses_HalfMoon_C`, faction and creature blueprints, armour, weapons — so these
entries are the engine persisting every actor it needs to rebuild: NPCs, containers, doors,
dropped items, their positions and states.

Two consequences worth knowing:

- **Saves grow with places visited, not with progress.** Four maps are recorded here; a full
  playthrough carries many more. A save that doubles in size usually means a new map, or the
  arrival of a base layer.
- **The interesting data is the small part.** Everything a save editor plausibly wants —
  skills, perks, inventory, reputation, quest flags — lives in the ~7% outside the map
  entries, and `Player.dat` is only 57 KB of it. The 92% is engine bookkeeping, and it is
  where you would do real damage.

## Layered saves — `SavedState.dat`

Saves may carry the **entire previous state as a nested container entry**:

```
SavedState.dat   prefix=617   size=3081560   payload ends at 3082200
Player.dat       prefix=644   size=48127                              <- base layer
…                                                                     <- base layer entries
Player.dat       prefix=3082200                                       <- LIVE layer
…                                                                     <- live entries
```

The `SavedState.dat` entry's payload spans the whole base layer and ends **exactly** where
the live `Player.dat` begins. This is a genuine format feature — a base snapshot plus a
live layer — not damage from Xbox→Steam conversion.

> **Consequence:** always target the **last** occurrence of an entry name. The base-layer
> copy is stale; on a converted save its skill array reads all zeros. Editing the wrong copy
> silently does nothing.

## Section headers — 23 bytes

Magics seen: `SGDF` (file root), `PSHF` (first thing inside `Player.dat`).

```
+0   4  u32   magic length including NUL      always 5
+4   5  char  4-char ASCII magic + NUL
+9   4  u32   version                         always 1
+13  2        constant                        27 00
+15  4  u32   constant                        0x0000020A (522)
+19  4  u32   constant                        0x000003F4 (1012)
```

Total 23 bytes. The section's body follows immediately; the header carries **no length
field**. Reading a length at +21 — as a record would have — yields garbage.

## Records — 25 bytes, `CSHF`

```
+0   4  u32   magic length including NUL      always 5
+4   5  char  "CSHF" + NUL
+9   4  u32   id            increments within a scope, resets per object
+13  4  u32   version       always 1
+17  4  u32   (unidentified, tracks id)
+21  4  u32   payload length
+25  …        payload
```

**Records chain exactly**: `next = pos + 25 + payloadLen`. Verified across the whole record
run inside `Player.dat`.

Counts in one 3 MB save: `CSHF` ×11,884, `ASHF` ×4,093, `BLHF` ×14, `WSHF` ×5, `TMHF` ×2,
plus singletons (`SGDF`, `PSHF`, `CCHF`, `QMHF`, `RMHF`, `AMHF`, `BRHI`, `CPHF`, `LRHF`,
`GVHF`, `TVMF`, `CMHF`, `BMHF`, `GCHF`, `FMHF`, `AIGB`). Only `SGDF`/`PSHF` (section) and
`CSHF` (record) layouts are confirmed; the others are assumed to follow one of the two.

> **This distinction cost real debugging time.** Section headers are 23 bytes with no
> length; records are 25 bytes with a length at +21. Applying the record layout to `PSHF`
> produced a length of `1505427456` and made the rest of the entry look like undecoded
> mystery data.

## Record payload conventions

Payloads commonly open with a small type tag (`01 02 XX`) followed by typed fields.
Length-prefixed strings inside payloads use the same `[u32 len][ASCII][NUL]` form as
elsewhere.

Game content is referenced by Unreal asset path, so the vocabulary is legible in the file:

```
/Game/Blueprints/Perks/Player/Perk_Player_Pickpocket
/Game/Blueprints/Flaws/Flaw_Sys_Kleptomania
/Game/Blueprints/Traits/WittyTrait
/Game/Blueprints/Backgrounds/ProfessorBackground
/Game/Blueprints/Spells/Skills/Spell_ExplosivesDamage
```

Roughly 2,145 distinct asset paths appear in a mid-game save, across `Item` (230),
`Armor` (133), `WEAP` (109), `Spells` (81), `Flaws` (28), `Traits` (7), `Perks` (3) and
`Backgrounds` (1).
