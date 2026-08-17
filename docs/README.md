# The Outer Worlds 2 — save format documentation

Reverse-engineered from a Steam copy of the game, build `1.2.0.1 Release`, on Windows 11.
Everything here was verified against real save data; anything inferred or unconfirmed is
marked as such.

## Reading order

| | |
|---|---|
| [01 — Storage and layout](01-storage.md) | Where saves live, the Steam/Xbox split, and the GDK container store |
| [02 — Metadata.dat](02-metadata.md) | The `GMHF` sidecar that drives the load list |
| [03 — SaveGame.dat container](03-savegame-container.md) | Entries, layers, section headers, and the record stream |
| [04 — Player data](04-player-data.md) | Decoded contents: skills, points, character name, self-pointers |
| [05 — Editing rules](05-editing-rules.md) | What is safe to change, and the five fixups a length change needs |
| [06 — Investigation methods](06-methods.md) | Techniques that worked, and one that looked convincing but wasn't |
| [07 — Open questions](07-open-questions.md) | What is still unmapped |

## The short version

A save is two files in a per-slot folder:

```
Saved Games\TheOuterWorlds2\<Slot>\
    Metadata.dat             GMHF  — load-list entry, ~2 KB
    SaveGame.dat             zlib  — the actual save
    SaveGameScreenshot.png         — thumbnail
```

`SaveGame.dat` inflates to a stream of named **entries** (`Player.dat`, `Quests.dat`, map
files…). Inside an entry are **section headers** and a chain of **records**. Character state
lives in `Player.dat`.

Two facts shape everything else:

**There is no checksum over the save.** Searching `Metadata.dat` for MD5, SHA-1 and SHA-256
of the state file — compressed and inflated — finds no match. Edited saves load.

**The payload contains absolute self-pointers.** Any edit that changes a length shifts them
and must fix them up. This is the single thing that makes editing non-trivial, and it is
covered in [05 — Editing rules](05-editing-rules.md).

## Conventions used here

- `rel N` — a byte offset relative to the start of an entry's payload
- `payload+N` — a byte offset within a record's payload, after its header
- All integers are little-endian
- Strings are length-prefixed: `[u32 byteLength][ASCII bytes][NUL]`, where the declared
  length **includes** the terminating NUL
