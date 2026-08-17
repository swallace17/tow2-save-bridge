# 01 — Storage and layout

## Two backends

The Steam build writes saves to one of two places depending on whether you are signed into
an Xbox account:

| | Signed **out** of Xbox | Signed **in** to Xbox |
|---|---|---|
| Location | `%USERPROFILE%\Saved Games\TheOuterWorlds2\<Slot>\` | `%LOCALAPPDATA%\Packages\Microsoft.OE-Arkansas_8wekyb3d8bbwe\SystemAppData\wgs\<user>\<ContainerGUID>\` |
| State file | `SaveGame.dat` | `SavedState.dat` |
| Metadata | `Metadata.dat` | `Metadata.dat` |
| Screenshot | `SaveGameScreenshot.png` | `SaveGameScreenshot.png` |
| Synced by | Steam Cloud | Xbox cloud |

`OE-Arkansas` is Obsidian's package identifier for the game.

When signed in, the local Steam folder still receives `SaveGameScreenshot.png` and nothing
else — so Steam Cloud syncs a folder of thumbnails with no save data behind them. See
[../README.md](../README.md) for the recovery tool.

## Slot names

- **Manual saves** — 32 uppercase hex characters, e.g. `7D5D8FAA479568B3A7D2288834065174`
- **Autosaves** — `Autosave00`, `Autosave01`, `Autosave02`. A **rotating buffer**: the
  number does *not* indicate recency. Go by the in-game timestamp.
- **Quicksaves** — assumed to follow `Quicksave*`. Unverified; none appeared in the sample data.

## The GDK `wgs` container store

```
wgs\
  <XUID>_<...>\
    containers.index
    <ContainerGUID>\
      container.<N>      N increments on rewrite; the highest is current
      <BlobGUID>         raw blob, no extension
      <BlobGUID>
      <BlobGUID>
```

### `container.<N>`

```
offset  size  meaning                         observed
------  ----  ------------------------------  -----------
0       4     version                         04 00 00 00
4       4     blob count                      03 00 00 00
8       ...   blob table, 160 bytes per entry
```

Each 160-byte table entry:

```
+0    128  blob name, UTF-16LE, NUL-padded
+128   16  GUID A — the live blob in every container sampled
+144   16  GUID B — alternate generation
```

The blob's filename on disk is the GUID as 32 uppercase hex characters with no dashes
(.NET `Guid.ToString("N")`). Read both GUIDs and take whichever exists — this looks like a
double-buffer for torn-write protection.

The three blobs per container are always `Metadata.dat`, `SavedState.dat`, and
`SaveGameScreenshot.png`.

### `containers.index`

Maps container GUIDs to slot names and carries ETag-style version strings. **Not fully
mapped** — it proved unnecessary, because the slot name is recoverable from `Metadata.dat`
(see [02 — Metadata.dat](02-metadata.md)). Parsing it properly would be more robust for
containers whose metadata is damaged.

## Xbox → Steam conversion

The two backends store the same zlib-compressed payload. They differ in exactly two ways:

1. **Filename.** `SavedState.dat` in the container, `SaveGame.dat` locally.
2. **A 23-byte `SGDF` header.** Inflated, the local file opens with a full section header;
   the Xbox blob replaces it with a bare 4-byte value.

```
local:  05 00 00 00 "SGDF\0" 01 00 00 00 27 00 0A 02 00 00 F4 03 00 00 │ 0B 00 00 00 "Player.dat\0" …
xbox:   2A 00 00 00                                                    │ 0B 00 00 00 "Player.dat\0" …
```

From the `Player.dat` entry onward the two are byte-identical, so converting Xbox → local is
a +19 byte delta: swap the 4-byte prefix for the 23-byte header, then patch the metadata's
inflated-size field.

The meaning of the Xbox prefix `2A 00 00 00` (42) is unidentified.

### Why it looks like corruption

Copy the Xbox blob into the Steam folder under its own name and the save **appears in the
load list**, fully populated — correct character, level, quest, difficulty, playtime and
thumbnail. It simply will not load, because `Metadata.dat` alone drives the list and the
game then opens `SaveGame.dat`, which does not exist.

A save that displays correctly but refuses to load reads as damaged data rather than a
missing file. That is almost certainly why community threads on this all conclude it can't
be fixed.
