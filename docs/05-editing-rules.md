# 05 — Editing rules

There is **no checksum or signature** over the save. Edited saves load. The only thing
standing between you and an edit is keeping the format's internal sizes and pointers
consistent.

Edits fall into two classes, and the difference is severe.

## Class 1 — length-neutral edits (easy)

Changing a fixed-width value in place. Nothing moves, so nothing else needs touching.

**Requires:** nothing. No size fixups, no metadata patch.
**Examples:** skill values (`u32`), Points Available (`u8`), any numeric field.

The only care needed is picking the right record — see the layered-save warning below.

## Class 2 — length-changing edits (hard)

Changing the length of a string inside the payload. **Five** values must stay consistent:

| # | what | where |
|---|---|---|
| 1 | CSHF record payload length | `record + 21`, u32 |
| 2 | **self-pointer** | `Player.dat` payload `+24`, u64 |
| 3 | **self-pointer** | `Player.dat` payload `+32`, u64 |
| 4 | `Player.dat` entry size | entry prefix + 4 + nameLen, u32 |
| 5 | metadata inflated-size field | `Metadata.dat`, located by value |

Items 2 and 3 are the ones that are easy to miss. Adjust a pointer only if its value is
**greater than the offset you edited** — pointers before the edit point don't move.

Getting 1, 4 and 5 right but missing 2 and 3 produces a save that passes every structural
check — entry table intact, record chain intact — and still loads as `DefaultPlayerMax`.

## Verification that actually proves something

Before writing:

- **Pointers resolve to the same strings** after adjustment as before. This is the sharpest
  check available — a broken pointer lands mid-string and fails immediately.
- **Entry table still chains**: same entry count, same order, and the same number of entries
  whose `payload + size` lands exactly on the next entry's prefix.
- **Record chain still walks**: `next = pos + 25 + len` from the first record to the end of
  the entry.
- **Metadata size field is unambiguous**: the inflated size appears exactly once.

After writing, re-inflate and read the edited values back.

> **A check that looks convincing but is worthless:** confirming the skill values still read
> correctly after a name edit. The skill record sits *before* the name record, and an edit
> only shifts bytes *after* it, so those values can never move. Any verification must target
> data downstream of the edit. This one passed while the save was thoroughly broken.

## Approaches that do not work

Tested in game, on clones:

| approach | result |
|---|---|
| Same byte length, overwrite in place | ✅ works, needs no fixups |
| Change length, fix all five values | ✅ works |
| Change length, fix only record/entry/metadata | ❌ loads as `DefaultPlayerMax` |
| Keep declared length, NUL-pad the tail | ⚠️ loads, renders the NULs as boxes |
| Shrink declared length, leave slack in the record | 💥 **crashes the game** |

The last two establish that the parser reads fields **sequentially** — trailing slack
desyncs it — and renders exactly `declared − 1` characters rather than stopping at a NUL.

## Practical safety

Everything in this repo follows the same pattern, and it is worth copying:

1. **Work on a clone.** Cloning is length-neutral and cheap — see
   [02 — Metadata.dat](02-metadata.md#editing-metadata).
2. **Back up before writing.** Both files, to somewhere outside the save folder.
3. **Target the live layer.** Always the *last* occurrence of an entry name; the base layer
   inside `SavedState.dat` is stale and editing it does nothing.
4. **Refuse rather than guess.** If a record isn't where it should be, or a value doesn't
   look like what it should be, abort instead of writing. A game build change should produce
   an error, not a corrupted save.
5. **Verify by read-back**, then confirm in game. Static checks caught most problems here,
   but not all — the crash case passed every static check.
