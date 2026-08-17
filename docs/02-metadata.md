# 02 — `Metadata.dat`

Magic `GMHF`. Roughly 1.5–2 KB. Drives the load-screen entry: character name, level, quest,
difficulty, playtime and thumbnail.

**It does not name the state file.** The game opens a hardcoded filename per backend. That
decoupling is why a wrongly-named payload produces a save that renders perfectly and refuses
to load.

## String table

Length-prefixed ASCII strings, in this order:

| index | value |
|---|---|
| 0 | `GMHF` |
| 1 | `<Slot>` — 32 hex chars, matches the folder name |
| 2 | `<CharacterName>` — the label shown in the load list |
| 3 | `<Slot>/SaveGameScreenshot.png` |
| 4 | `1.2.0.1` — build |
| 5 | `Release` |

Each is `[u32 byteLength][ASCII][NUL]`, where the declared length **includes** the NUL. The
slot name therefore appears **twice** (index 1 and inside index 3), which matters when
cloning a save.

## Fixed block

Immediately after the `Release` string:

```
"Release\0"  27 00 00 00 00 00 00 00
[u32]  inflated size of the state file      <- the field that matters
[u32]  1
[u32]  (unidentified)
[u32]  1
[u32]  inner blob inflated size
[u32]  inner blob compressed size
78 9C  …  inner zlib stream
~20 byte trailer
```

The inner zlib stream holds additional load-list detail (level, quest, difficulty,
playtime). Not decoded further — nothing needed it.

The ~20-byte trailer is possibly a SHA-1 over the metadata, unconfirmed. It is evidently
**not** validated against the state file: patching the size field and swapping the payload
does not break loading.

## The inflated-size field

`Metadata.dat` records the **inflated** size of `SaveGame.dat`. It does not record the
compressed size, and carries no checksum over it — verified by searching the metadata for
MD5, SHA-1 and SHA-256 of the state file in both compressed and decompressed form. No match.

The field sits at a **variable offset**, because the plaintext strings before it vary in
length:

| slot name | len | character name | len | offset |
|---|---|---|---|---|
| `7D5D8FAA…` | 32 | `Earnest Jones` | 13 | 256 |
| `Autosave00` | 10 | `Earnest Jones` | 13 | 212 |
| `F5B3B214…` | 32 | `test A` | 6 | 249 |

These fit `offset = 179 + 2·len(slot) + len(character)` — the slot name appears twice, the
character name once. Three data points against three unknowns, so treat it as a consistent
hypothesis rather than proof.

> **Locate the field by value, not by offset.** Scan for the `u32` equal to the state file's
> inflated length and assert exactly one match. That is backend-agnostic and immune to
> string-length surprises. Every tool in this repo does it this way.

## Editing metadata

`Metadata.dat` has **no internal position dependence**. Unlike the payload, it can be
rebuilt at a different length freely:

- **Renaming the load-list label** (string index 2) — rebuild the file around the new
  string. Verified at 13 → 27 chars and 13 → 11 chars.
- **Cloning a save** — replace both copies of the slot name with a fresh 32-hex id. Both are
  32 characters, so this is a length-neutral in-place substitution.

After any rebuild, re-verify that the inflated-size value still appears exactly once, since
the rebuild moves it.
