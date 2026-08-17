# 08 — Prior art

Surveyed 2026-08-17, to establish what already existed before this work and what is
genuinely unclaimed.

## Nothing documents this format

The chunk magics — `SGDF`, `PSHF`, `CSHF`, `ASHF`, `WSHF`, `BLHF`, `TMHF`, `QMHF`, `RMHF`,
`AMHF`, `CPHF`, `LRHF`, `GVHF`, `CMHF`, `BMHF`, `GCHF`, `FMHF`, `CCHF`, `BRHI`, `AIGB`,
`TVMF` — return **zero matches** in GitHub code search, general web search, and the game's
own UE4SS reflection dump. This is not a known middleware format.

All 100 TOW2 mods on Nexus were enumerated: **there is no save editor and no save tool.**
The save-adjacent entries are "More Save Slots" (raises the slot cap) and `.usmap` mappings
for pak assets.

## The one genuinely transferable finding

**The Outer Worlds 1 uses the same chunk scheme.**
[hayleyxyz/OuterWorldsSaveParser](https://github.com/hayleyxyz/OuterWorldsSaveParser) is an
abandoned, explicitly unfinished C# parser, but its working code establishes two things:

1. The whole `SaveGame.dat` is zlib-compressed from offset 0.
2. Chunks are found by scanning for a UE `FString` whose `int32` length is **5**, followed by
   four `A`–`Z` bytes and a `0x00`.

That confirms our reading of the header: the 4-character magics are not bare magics but
**UE-serialized `FString`s occupying 9 bytes** — `05 00 00 00` + `"CSHF"` + `00`. Our 23-byte
section header and 25-byte record header both begin with that 9-byte name field, leaving 14
and 16 bytes of fields respectively. Same lineage, two games apart.

The same repo's speculative per-chunk layout guess was never run and does not match what we
measured; treat it as unverified.

No save parser exists for **Avowed** or **Grounded**. Pillars of Eternity has mature editors
but is Unity, so nothing transfers.

## Useful adjacent resources

**[nathtest/UProjArkansas](https://github.com/nathtest/UProjArkansas)** — a UE 5.4.4 project
reconstructed from a UE4SS dump of the game. Reflection-only (property and function
signatures, no serialization bodies), but it gives the exact class vocabulary:
`USaveGameManager`, `USaveGameStorageProviderFS` / `…GDK`, `USaveableBlob`,
`FSaveGameGeneralMetadata`, and
`ESaveGameType {Standard, Quicksave, Autosave, PostGame, PointOfNoReturn, BeforeSkip, BeforeEVTransition, Invalid=255}`.

That enum is worth noting: it confirms **Quicksave and Autosave are distinct save types**,
supporting the assumed `Quicksave*` slot naming that we have not yet observed.

**Nexus "Character Editor" (mod 34)** — despite the name, it is a UE5 blueprint pak mod
loaded via a console enabler. It edits the character in-process and never touches save files.
Not prior art for this work.

**[jtasse/tow2-overwrite-oldest-save](https://github.com/jtasse/tow2-overwrite-oldest-save)**
— a UE4SS Lua mod documenting the save *management* API (`SaveGameManager::SaveGame`,
`Quicksave`, `DeleteGame`). Treats saves as opaque folders; no binary content. It does note
that the engine's slot count can drift from the on-disk folder count, leaving orphan GUID
folders behind after deletion.

**Cheat Engine table** (fearlessrevolution.com/viewtopic.php?t=37023) — pure memory editing,
no save-file content.

## A hypothesis this suggested, and why it failed

The class dump shows currency is an **inventory item**, not a scalar:
`UCurrencyItem : UItem`, `EItemType::Currency`, asset
`Arkansas/Content/Blueprints/Item/Currency/Bit_Cartridge`, manipulated via
`UInventoryComponent::AddCurrency` / `RemoveCurrency`. The cheat table further gives Bits a
numeric item ID of **146 (`0x92`)**, with quantity at `entry+0x8` in memory.

That predicted the save would hold `92 00 00 00` adjacent to the bits value. **It does not.**
The value 146 appears exactly twice per save and neither occurrence is within 256 bytes of
the bits field, across three saves checked.

Conclusion: the numeric item ID is the **runtime** representation, not the serialized one.
The save uses the entry-list structure documented in
[04 — Player data](04-player-data.md#the-entry-list) instead. Recorded here so the hypothesis
is not re-tried — it is a very reasonable guess that happens to be wrong.
