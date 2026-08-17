# tow2-save-bridge

Recover **The Outer Worlds 2** saves from Xbox connected storage and write them back in the
local Steam format — so Steam Cloud, Steam Deck, and GeForce Now can see them again.

> **The short version:** signing into an Xbox account in the Steam build diverts your saves
> to Xbox's container store. The Steam save folder keeps receiving screenshots and nothing
> else, so Steam Cloud syncs a folder of thumbnails. The save data is still on your disk,
> unencrypted, and fully recoverable.

## The problem

If you play the Steam version of The Outer Worlds 2 while signed into an Xbox account:

- Your saves go to `%LOCALAPPDATA%\Packages\Microsoft.OE-Arkansas_8wekyb3d8bbwe\…\wgs\`
- Your Steam save folder gets only `SaveGameScreenshot.png` per save
- Steam Cloud dutifully syncs those screenshots
- Steam Deck and GeForce Now can't sign into Xbox, so your progress is stranded

Signing out does **not** migrate anything — it only changes which store the game reads, so
the load list comes up empty and it looks like the saves are gone.

The existing community answer to this is that it can't be fixed. That's not right. Both
backends store the same payload; they differ in the state file's **name** and a **23-byte
header**. This tool bridges the two.

## Usage

Two ways to run it: a GUI, or the PowerShell script.

### GUI

`gui/TOW2SkillEditor` is a WinUI 3 app. **Recover from Xbox…** lists the saves sitting in
the container store and converts the ones you pick; it backs up first, reads the Xbox store
without modifying it, and verifies each converted save by re-inflating it.

```powershell
cd gui\TOW2SkillEditor
dotnet run
```

The same app also edits skills, points, bits, the save name and the character name — see
[docs/05 — Editing rules](docs/05-editing-rules.md) before using that on a save you care about.

### Script

Requires Windows and PowerShell 7+. Close the game first.

```powershell
# preview what would be written -- touches nothing
.\Restore-TOW2Saves.ps1

# back up both sides to your Desktop, then write
.\Restore-TOW2Saves.ps1 -Apply
```

Then **exit Steam fully**, launch the game, and **decline the Xbox sign-in**. If you sign
in, the game reads the container store again and ignores the restored local saves.

Re-runnable after any session played while signed in. The Xbox store is opened read-only
and never modified, so you can extract from it as many times as you like.

### What it does

For each save in the container store: parses the container index, inflates the state file,
swaps the 4-byte Xbox prefix for the 23-byte `SGDF` chunk header, re-compresses, patches
the inflated-size field in the metadata, and writes `Metadata.dat` + `SaveGame.dat` +
`SaveGameScreenshot.png` into `%USERPROFILE%\Saved Games\TheOuterWorlds2\<Slot>\`.

### Safety

- Dry run by default; `-Apply` is required to write anything
- Backs up both the Steam folder and the Xbox container store to your Desktop before writing
- Refuses to run with `-Apply` while the game is open
- Never writes to the Xbox container store

To undo, copy the `SteamSaves` folder from the backup back over
`%USERPROFILE%\Saved Games\TheOuterWorlds2`.

## Gotchas

- **Autosave slot numbers aren't chronological.** `Autosave00/01/02` is a rotating buffer;
  go by the in-game timestamp, not the number.
- **Screenshot-only folders can't be recovered.** If a save folder has a
  `SaveGameScreenshot.png` but no matching container, the state was rotated out of the Xbox
  store. It may still exist in Xbox cloud, but not locally.
- **Pick one platform and stay there.** This tool moves saves one way, Xbox → Steam. There
  is no reverse direction (see [FORMAT.md](FORMAT.md) §7).

## Why the saves show up but won't load

This is the detail that makes the problem look unfixable. `Metadata.dat` alone drives the
load list, so copying the Xbox blob under its own name produces a save entry with the right
character, level, quest, difficulty, playtime, and thumbnail — that just refuses to load.
The game is looking for `SaveGame.dat`; the Xbox blob is called `SavedState.dat`.

A save that appears correctly but won't load reads as corrupted data rather than a missing
file, which sends everyone down the wrong path.

## Format documentation

[`docs/`](docs/) documents the save format in depth — the GDK `wgs` container layout, the
`GMHF` metadata structure, the entry/section/record hierarchy inside `SaveGame.dat`, the
decoded player data, and the rules for editing a save without corrupting it. Investigation
methods and open questions are recorded alongside, so the analysis is picked up rather than
repeated.

Start at [docs/README.md](docs/README.md).

## Also in this repo

`lab/` holds exploratory tooling built while mapping the format — a record-level save
differ, a CLI save editor, a save cloner, and a raw field poke. It is **experimental and
lightly documented**. Read [docs/05 — Editing rules](docs/05-editing-rules.md) before using
any of it on a save you care about.

## Scope

Verified against a Steam copy of The Outer Worlds 2, build `1.2.0.1 Release`, on Windows 11,
with five saves (two manual, three autosaves). Other builds are untested. This reads and
rewrites your own save files; it doesn't touch game binaries or DRM.

## License

MIT
