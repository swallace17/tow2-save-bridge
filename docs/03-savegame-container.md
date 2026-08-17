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
