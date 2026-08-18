# 04 — Player data

Everything here lives in the **live** `Player.dat` entry — the last one in the file. See
[03 — layered saves](03-savegame-container.md#layered-saves--savedstatedat).

## Entry layout

```
rel 0        01                          first payload byte
rel 1..23    PSHF section header         23 bytes
rel 24       u64 self-pointer            -> string-table entry
rel 32       u64 self-pointer            -> string-table entry
rel 40..     GUIDs, counters, flags      partly unidentified
rel 103…     CSHF record chain
```

In a sampled save the record chain ran rel 103 → 21152, inside a 57,782-byte entry. The
remainder is string-table and other data reachable via the self-pointers.

## Self-pointers — the critical detail

At `rel 24` and `rel 32` sit two **u64 absolute offsets into the entry**. Each points at a
string-table entry of the form:

```
[u32 id][u32 len][ASCII][NUL]
```

Observed targets: `/Script/Arkansas` and `CharMoveComp`.

**Any edit that changes the payload length must shift the pointers that sit past the edit
point.** Miss them and they land mid-string; the game fails to resolve them and loads the
character as `DefaultPlayerMax`, level 1, all skills zero.

They were identified by differential analysis of game-written saves: between two autosaves
the entry size changed by exactly ±572 bytes and the `+32` pointer changed by exactly ±572,
while `+40`/`+76` were random (GUIDs) and `+68` near-constant (a count). A brute scan over
the entire entry for values that resolve to string-table entries found only these two plus
two coincidences, so the pointer surface is small.

## Skills

In the `CSHF` record whose payload contains the `OEIUserSetting.Difficulty.*` string table.
Locate that record by searching for `OEIUserSetting.Difficulty.Multiplier.PlayerDamage` and
walking back to the nearest chunk header — **taking the last match**, since the base layer
contains a stale copy.

A `u32` array, stride 4, base `payload+236`:

| offset | skill | offset | skill |
|---|---|---|---|
| +236 | **Melee** | +264 | Medical |
| +240 | **Guns** | +268 | Science! |
| +244 | Sneak | +272 | Observation |
| +248 | Lockpick | +276 | Speech |
| +252 | Engineering | +280 | Leadership |
| +256 | Explosives | | |
| +260 | Hack | | |

> **Melee and Guns are transposed relative to the in-game display order.** The internal
> order puts Melee first. The other ten follow the UI exactly. Extrapolating from a partial
> sample gets this wrong — it was caught by writing a distinct sentinel value into all
> twelve slots at once and reading the results off the skill screen.

`payload+337` is a **u8**: Points Available.

### Displayed value vs stored value

The Outer Worlds 2 gives 12 skills, and **tagging a skill at character creation grants two
free points**. Tagged skills show a star in game.

```
displayed = stored + 2   for tagged skills
displayed = stored       for everything else
```

Verified: Speech stored 44 → displayed 46; Leadership stored 40 → displayed 42; Leadership
stored 0 → displayed 2; all ten untagged skills exact. The array holds the **base**; the tag
bonus is applied at load.

Which skills are tagged is not readable from the array.

## The entry list

Inside the large `CSHF` record sits a list of fixed-size **84-byte entries**:

```
+0   u32   2                  constant
+4   u32   1                  constant
+8   u32   id                 see below
+12  u32   0x00010B01         constant tag
+16  …     17 × u32 payload
```

Entries are contiguous — 84 bytes apart — and a mid-game save holds ~127 of them. They look
like per-object counters and stats.

> **`id` is an allocation counter, not a meaning.** Do not key on it. Across saves of one
> character the ids shift by one between sessions (what is id 63 in a later save is id 62 in
> an earlier one), and a brand-new character has only ids 36–45. Keying on `id == 63` reads
> the neighbouring entry on some saves.

Common payload shape for a counter entry:

```
[0, 0, 0, 65536, 65536, -65536, 131071, 65536, 65536, 65536, 0, 0x01000000, VALUE, 1, 0, ptr, 0]
                                                                            ^^^^^ payload[12]
```

`payload[15]` is a forward offset — usually `entryPos + 160` — into a later entry. Purpose
unconfirmed.

## Bits (currency)

Bits is `payload[12]` of one of those entries. **Its absolute offset is not stable**: it
moved 6049 → 6001 → 6104 → 6145 across one character's saves, because the entry list grows
as objects are created.

**Locate it by landmark.** A neighbouring entry has a fully constant payload and sits exactly
154 bytes before the bits entry:

```
landmark payload[11..16] == [33554432, 1161527296, 0, 11, 256, 256]

bits u32 = landmarkEntryPos + 218          (154 to the entry, +16 header, +48 to payload[12])
```

Verified across 12 saves spanning two sessions — **exactly one landmark match in each**, and
the values form a clean monotonic progression:

```
2877 → 2886 → 3791 → 4148 → 4148 → 5197 → 5239
```

ending on a figure confirmed on the in-game character sheet. Writing through the locator was
confirmed in game twice, including on a save whose bits offset is 6104 rather than 6145, so
it is the locator working and not a lucky constant.

Editing is a Class 1 length-neutral poke — no size or pointer fixups.

### Caveats

- A **brand-new character** has no landmark match (its entry list is only 10 entries). The
  tooling reports "not found" and disables the field rather than guessing.
- The landmark includes `1161527296` (float `2992.0`), which *may* be character-specific. It
  was constant across this character's whole playthrough. Dropping it from the signature makes
  the match non-unique (4 hits), so it is kept — and the code **requires exactly one match**,
  failing safe if another character's save differs.

### Approaches that failed

- **Fixed offset** — drifts on both sides. Offset from the record start moved 2125 → 2029
  while offset from its end moved 7937 → 12358.
- **String anchor** — the enclosing record contains no strings at all.
- **Suffix marker** `2, 1, 64, 68353` — walking back over `[count][count × 12-byte entries]`
  self-validates in principle, but matches `count = 0` spuriously whenever the preceding
  `u32` is zero, and the marker is not unique.
- **Constant-prefix signature** (the nine `u32` before bits) — 23–39 matches per save, and
  absent entirely at the bits location in one save.
- **Value search** — asking for the figure shown in game and matching it. Works, but it is
  not a locator; realistic amounts happen to be unique while round numbers are not (100
  matched eight times).

## Character name

A length-prefixed string inside a `CSHF` record. Locate it **structurally**, not by matching
the name in `Metadata.dat` — the two diverge as soon as either is edited.

Signature: a `CSHF` record where

```
recLen == 21 + L        L = u32 at payload+16
```

and an ASCII NUL-terminated string of `L` bytes follows at `payload+20`. This matched
**exactly once** in every sampled save, including renamed ones and saves from an earlier
session.

Record layout for a 13-character name:

```
payload +0    01 02 05 00
        +4    00 × 10
        +14   01
        +16   0E 00 00 00        length prefix (14 = 13 chars + NUL)
        +20   "Earnest Jones\0"
        +34   00 00
```

Note the name also appears in the **base layer** copy of `Player.dat`. Editing only the live
copy is sufficient and is what the tooling does.

## The PlayerInfoComponent class list — a registry, not the acquired set

After a `"PlayerInfoComponent"` string in the live `Player.dat` sits a long, uniform list of
triples:

```
[u32 0][str assetPath][str className]
```

repeated with no count prefix and no terminator — it simply stops when the next `u32` is not
zero, followed by `"/Script/Arkansas"`. In a sampled mid-game save it held **36 entries over
2,961 bytes** at `+23175`.

```
Perks         4     Perk_Player_SpaceRanger, Perk_Player_Pickpocket,
                    0220_Perk_Reward_ScrabblesPlushie, 02_Perk_Reward_RegionCollection_Tier1
Traits        3     SuaveTrait, WittyTrait, SicklyTrait
Backgrounds   1     ProfessorBackground
Flaws        28     Flaw_Sys_Consumerism, Flaw_Sys_Kleptomania, …
```

> **Do not treat this as the character's perks and flaws.** Two things rule that out:
>
> - **All 28 flaws are present**, in both the base and live layers, unchanged. No character
>   has 28 flaws — that is the full flaw catalogue.
> - **The order is not stable.** Between the base and live layers `Flaw_Sys_BadKnees` moves
>   relative to `ProfessorBackground`, which is what set iteration looks like, not an
>   ordered acquisition log.
>
> It reads as a registry of classes the component references. Adding an entry would very
> likely register a class without granting anything.

It *does* respond to acquisition, which makes it a useful signal. The base layer of the same
save carries 35 entries and 3 perks; the live layer has 36 and 4, differing by exactly
`02_Perk_Reward_RegionCollection_Tier1` — a perk taken between the two snapshots. So a save
file with a base layer contains its own before/after pair for free.

## The spell list — this one does track what you have

A **second** string region, roughly `+42200`–`+46800` in the same entry, holds
`/Game/Blueprints/Spells/…` asset paths in the same `path` + `_C` class pairs. Unlike the
class registry, its contents match the character exactly:

```
Spell_SuaveTrait  Spell_WittyTrait  Spell_SicklyTrait          3 traits  -> 3 spells
Spell_Perk_SpaceRanger                                          }
0220_Spell_Perk_ScrabblesPlushie                                }  4 perks -> 4 effects
02_Spell_Perk_RegionCollection_Tier1                            }
Spell_Player_UnlockPickPocketing   (Pickpocket's effect)        }
Spell_ExplosivesDamage, Spell_HackDamageToAutomechs, …          skill effects
Spell_Helmet_…, Spell_SMG_…, Spell_ArmorMod_…                   equipped gear
```

**No flaw spells at all**, despite 28 flaws sitting in the class registry — independent
confirmation that the registry is not the acquired set.

It also tracks acquisition precisely. Diffing the base and live layers of a single save:

```
base (3 perks)  29 spells
live (4 perks)  32 spells
only in live:   Spell_Helmet_Tank_T0, Spell_Helmet_PremiumMoonman,
                02_Spell_Perk_RegionCollection_Tier1
```

The perk's spell appears exactly when the perk was taken; the other two are helmets equipped
between the snapshots. Nothing spurious in either direction.

This reads as the SpellManagerComponent's active effects, and it is the closest thing found
so far to real character state.

### Working model for granting a perk — untested

A perk plausibly needs **two** insertions:

1. its class into the `PlayerInfoComponent` registry (`path` + `_C` pair)
2. its effect into the spell list (`path` + `_C` pair)

Both are string inserts, so both are **Class 2 edits** needing the full five fixups from
[05 — Editing rules](05-editing-rules.md). Note the self-pointer at `Player.dat +32` targets
43472, which sits *between* the two regions — an insert at the registry (~23179) shifts it,
an insert in the spell list (~44816) does not.

This has **not been tested in game**. A perk may also need an entry in the 84-byte entry list
or elsewhere; only trying it will say.

Note also that adding to this list would be a **Class 2 edit**: entries are 90–120 bytes of
string data, so an insert changes the payload length and needs the full five fixups from
[05 — Editing rules](05-editing-rules.md), including the self-pointer at `Player.dat +32`
(which targets 43472, past the list at ~23175, so it would need adjusting).

## What is not in the save

**The Outer Worlds 2 has no attributes.** They were removed from the first game's design.
The character systems are Backgrounds (6), Traits (12 — 9 positive, 3 negative), Skills (12),
Perks, and Flaws. The save agrees: zero occurrences of the string `Attribute`, and
`/Game/Blueprints/` has `Perks`, `Flaws`, `Traits` and `Backgrounds` but no `Attributes`.

Perks, traits, flaws and background are stored as asset-path references and are **not yet
mapped** to specific records.
