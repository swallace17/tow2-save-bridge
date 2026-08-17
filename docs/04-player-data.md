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

## Bits (currency)

A `u32` in the live `Player.dat`. **Its offset is not stable** — it sits after a
variable-length structure inside a record that grows during play. Measured across saves of a
single character:

| save | `u32` at rel 6145 | |
|---|---|---|
| `9779206D`, `Autosave00`, `Autosave02` | 5239 | ✅ bits |
| `Autosave01` | 5197 | ✅ bits |
| `7D5D8FAA` (earlier session) | 6311 | ✅ bits |
| `F49775934` | 131071 | ❌ `0x1FFFF`, unrelated |
| `4755539744`, `A990D10E` | 0 | ❌ unrelated |

The enclosing record grew 10,062 → 14,387 bytes across the session and the field's
record-relative offset drifted 2125 → 2070 → 2029 with it. That record contains **no strings
at all**, so there is no anchor to locate the field structurally.

> **A hardcoded offset would silently edit the wrong bytes** in two of the eight saves
> sampled. Don't.

Editing it is otherwise trivial — a Class 1 length-neutral poke, verified in game by writing
987,654. **The obstacle is purely locating it**, and it is not shipped in the tooling for
that reason.

### Why it is hard to locate, and what would solve it

The field drifts on **both** sides, so neither end of the record anchors it:

| save | record length | bits offset from record start | from record end |
|---|---|---|---|
| `7D5D8FAA` | 10,062 | 2125 | 7937 |
| `9779206D` | 14,387 | 2029 | 12358 |

Things that were tried and do not work:

- **Fixed offset** — drifts, as above.
- **String anchor** — the record contains no strings at all.
- **Suffix marker.** The sequence `2, 1, 64, 68353` follows bits at a variable but principled
  distance: `[bits][u32 count][count × 12-byte entries][marker]`, so count 0 puts the marker
  at +8 and count 1 at +20. Walking back from the marker and requiring `u32 == count`
  self-validates — but it matches `count = 0` spuriously whenever the preceding `u32` happens
  to be zero, and the marker is not unique (one save had two).

What would actually solve it is **decoding the record's field-stream encoding** and walking
forward from the record header. Its payload begins:

```
7D5D8FAA:  01 02 0B 5B  00 00 00 0F  10 00 00 00  00 00 00 02 …
9779206D:  01 02 0B 83  00 00 00 6F  10 00 00 00  00 00 00 02 …
```

The two share a clear skeleton with a few varying bytes, and fields appear to be 4-aligned
from `payload+1` (both bits offsets are ≡ 1 mod 4). That is a bounded piece of work and it
would unlock every other field in this record, not just bits.

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

## What is not in the save

**The Outer Worlds 2 has no attributes.** They were removed from the first game's design.
The character systems are Backgrounds (6), Traits (12 — 9 positive, 3 negative), Skills (12),
Perks, and Flaws. The save agrees: zero occurrences of the string `Attribute`, and
`/Game/Blueprints/` has `Perks`, `Flaws`, `Traits` and `Backgrounds` but no `Attributes`.

Perks, traits, flaws and background are stored as asset-path references and are **not yet
mapped** to specific records.
