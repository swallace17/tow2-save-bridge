# The Outer Worlds 2 — save format notes

Reverse-engineered from a Steam copy of the game, build `1.2.0.1 Release`, on Windows 11.
Everything here was verified against real save data; anything inferred is marked as such.

---

## 1. Two storage backends

The Steam build writes saves to one of two places depending on whether you are signed
into an Xbox account:

| | Signed **out** of Xbox | Signed **in** to Xbox |
|---|---|---|
| Location | `%USERPROFILE%\Saved Games\TheOuterWorlds2\<Slot>\` | `%LOCALAPPDATA%\Packages\Microsoft.OE-Arkansas_8wekyb3d8bbwe\SystemAppData\wgs\<user>\<ContainerGUID>\` |
| State file | `SaveGame.dat` | `SavedState.dat` |
| Metadata | `Metadata.dat` | `Metadata.dat` |
| Screenshot | `SaveGameScreenshot.png` | `SaveGameScreenshot.png` |
| Synced by | Steam Cloud | Xbox cloud |

`OE-Arkansas` is Obsidian's package identifier for the game.

When signed in, the local Steam folder still receives `SaveGameScreenshot.png` and nothing
else — so Steam Cloud faithfully syncs a folder of thumbnails with no save data in it.

### Slot names

- Manual saves: 32 uppercase hex characters, e.g. `7D5D8FAA479568B3A7D2288834065174`
- Autosaves: `Autosave00`, `Autosave01`, `Autosave02` — a rotating buffer, so the
  number does **not** indicate recency
- Quicksaves are assumed to follow `Quicksave*` — **not verified**, no quicksave was
  present in the sample data

---

## 2. The `wgs` container store (Microsoft GDK connected storage)

```
wgs\
  <XUID>_<...>\
    containers.index
    <ContainerGUID>\
      container.<N>
      <BlobGUID>        <- raw blob, no extension
      <BlobGUID>
      <BlobGUID>
```

### `container.<N>`

`N` increments as the container is rewritten; the highest `N` present is current.

```
offset  size  meaning
------  ----  ---------------------------------------------------
0       4     version                       observed: 04 00 00 00
4       4     blob count                    observed: 03 00 00 00
8       …     blob table, 160 bytes/entry
```

Each 160-byte entry:

```
+0    128  blob name, UTF-16LE, NUL-padded   e.g. "SavedState.dat"
+128   16  GUID A  — the live blob
+144   16  GUID B  — alternate generation
```

The blob's filename on disk is the GUID formatted as 32 uppercase hex characters with no
dashes (.NET `Guid.ToString("N").ToUpper()`).

In all sampled containers **GUID A was the live blob**, but reading both and taking
whichever exists is the safe approach — this looks like a double-buffer for torn-write
protection.

The three blobs per container are always `Metadata.dat`, `SavedState.dat`, and
`SaveGameScreenshot.png`.

### `containers.index`

Maps container GUIDs to display names (the slot names above) and carries ETag-style
version strings. **Not fully mapped** — it was unnecessary, because the slot name can be
recovered from `Metadata.dat` (see below). Parsing it is the more robust long-term
approach if you want to handle containers whose metadata is damaged.

---

## 3. `Metadata.dat` — magic `GMHF`

Small (≈1.5–2 KB). Drives the load-screen entry: character name, level, quest, difficulty,
playtime, and thumbnail. **It does not name the state file** — the game opens a hardcoded
filename per backend, which is the crux of the whole problem (see §5).

Plaintext strings visible near the start, in order:

```
GMHF
<SlotName>
<CharacterName>
<SlotName>/SaveGameScreenshot.png
1.2.0.1
Release
```

Then a fixed block:

```
"Release\0" 27 00 00 00 00 00 00 00
[u32]  inflated size of the state file      <- the field that matters
[u32]  1
[u32]  (unidentified)
[u32]  1
[u32]  inner blob inflated size
[u32]  inner blob compressed size
78 9C  …  inner zlib stream
~20 bytes trailer (possibly SHA-1 over the metadata; not confirmed)
```

### The size field

`Metadata.dat` records the **inflated** size of the state file. It does *not* record the
compressed size, and carries no checksum over the state file — verified by searching the
metadata for MD5/SHA-1/SHA-256 of the state file in both compressed and decompressed form:
no match.

The field sits at a **variable offset**, because the plaintext strings before it vary in
length. Observed:

| slot name | len | character name | len | offset |
|---|---|---|---|---|
| `7D5D8FAA…` | 32 | `Earnest Jones` | 13 | 256 |
| `Autosave00` | 10 | `Earnest Jones` | 13 | 212 |
| `F5B3B214…` | 32 | `test A` | 6 | 249 |

These fit `offset = 179 + 2·len(slot) + len(character)` — the slot name appears twice
(standalone, and inside the screenshot path), the character name once. Three data points
against three unknowns, so treat the formula as a consistent hypothesis rather than proof.

**In practice, locate the field by value**: scan for the `u32` equal to the state file's
inflated length and assert exactly one match. That is backend-agnostic and immune to
string-length surprises.

---

## 4. The state file — `SaveGame.dat` / `SavedState.dat`

Both backends store the same payload, zlib-compressed (`78 9C`). A 3 MB save compresses to
roughly 450 KB.

Inflated, the file is a stream of chunks. Chunk header, 23 bytes:

```
+0   4  u32   length of magic including NUL   always 5
+4   5  char  4-char ASCII magic + NUL        "SGDF" / "PSHF" / "CSHF"
+9   4  u32   version                         always 1
+13  2        27 00                           constant
+15  4  u32   0x0000020A (522)                constant
+19  4  u32   0x000003F4 (1012)               constant
```

Magics observed in one 3 MB save: `SGDF` ×1, `PSHF` ×1, `CSHF` ×11,884.

Entries between chunks are length-prefixed:

```
+0   4  u32   name length including NUL       11
+4   n  char  name + NUL                      "Player.dat\0"
+..  4  u32   payload size
+..  1        01
```

### The one real difference between backends

```
local (SaveGame.dat):   05 00 00 00 "SGDF\0" 01 00 00 00 27 00 0A 02 00 00 F4 03 00 00 │ 0B 00 00 00 "Player.dat\0" …
Xbox  (SavedState.dat): 2A 00 00 00                                                    │ 0B 00 00 00 "Player.dat\0" …
```

The local format opens with a full 23-byte `SGDF` chunk header. The Xbox blob replaces it
with a bare 4-byte value (`2A 00 00 00` = 42; meaning unidentified). **From the
`Player.dat` entry onward the two are byte-identical.**

So the payload delta is exactly **+19 bytes** (23 − 4) converting Xbox → local.

The `SGDF` header is format-constant — the same `27 00 / 0A 02 00 00 / F4 03 00 00` tail
appears in the `PSHF` and `CSHF` chunk headers of both files — so it can be emitted
literally rather than lifted from a reference save.

---

## 5. Why this looks like corruption

Copy the Xbox blob into the Steam folder under its own name and the save **appears in the
load list**, fully populated — correct character, level, quest, difficulty, playtime, and
thumbnail. It simply will not load.

That is because `Metadata.dat` alone drives the list. The game then opens `SaveGame.dat`,
which does not exist, and fails.

A save that shows up correctly but won't load reads as damaged save data, not as a missing
file — which is almost certainly why the community threads on this all end in "the
recovered saves just don't load."

---

## 6. Conversion, Xbox → Steam

1. Parse `container.<N>`; resolve `Metadata.dat`, `SavedState.dat`, `SaveGameScreenshot.png`
2. Read the slot name out of `Metadata.dat` (`<Slot>/SaveGameScreenshot.png`)
3. Inflate `SavedState.dat`
4. Replace the leading 4 bytes with the 23-byte `SGDF` chunk header
5. Deflate (zlib; compression level is irrelevant to correctness)
6. Patch the inflated-size field in `Metadata.dat`: `oldSize` → `oldSize + 19`
7. Write `Metadata.dat`, `SaveGame.dat`, `SaveGameScreenshot.png` into
   `Saved Games\TheOuterWorlds2\<Slot>\`

Then exit Steam fully, launch the game, and decline the Xbox sign-in — otherwise the game
reads the container store again and ignores the local folder.

---

## 7. Open questions

- `2A 00 00 00` (42) — the Xbox prefix the `SGDF` header replaces. Meaning unknown.
- The `u32` between the state size and the inner blob sizes in `Metadata.dat`.
- The ~20-byte trailer after the metadata's inner zlib stream. A SHA-1 over the metadata
  is the obvious guess but was not confirmed. It evidently is not validated against the
  state file, since patching the size field and swapping the payload did not break loading.
- `containers.index` internals.
- Steam → Xbox (the reverse direction) would require writing `containers.index` and
  `container.<N>` correctly. Not attempted.
- Whether any of this holds on game builds other than `1.2.0.1`.
