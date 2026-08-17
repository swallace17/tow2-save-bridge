using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace TOW2SkillEditor;

public sealed class SaveSlot
{
    public string Slot { get; init; } = "";
    public string Character { get; init; } = "";
    public DateTime Saved { get; init; }
    public long Bytes { get; init; }
    public string Display => $"{Character}   ·   {Saved:MMM d, HH:mm:ss}";
}

/// <summary>
/// Reads and writes the 12-skill array in a The Outer Worlds 2 save.
///
/// SaveGame.dat is zlib. Inflated, it is a stream of chunk records
/// [u32 5]["MAGC" NUL][u32 id][u32 ver][u32 field][u32 payloadLen][payload].
/// The skills live in the CSHF record whose payload holds the difficulty
/// settings string table, as a u32 array at payload+236, stride 4.
/// Points Available is a single byte at payload+337.
/// </summary>
public static class SaveIo
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "Saved Games", "TheOuterWorlds2");

    // Internal order. Note Melee precedes Guns -- this is NOT the in-game display order.
    public static readonly string[] SkillNames =
    {
        "Melee", "Guns", "Sneak", "Lockpick", "Engineering", "Explosives",
        "Hack", "Medical", "Science!", "Observation", "Speech", "Leadership"
    };

    // Tagged skills display +2 over the stored base. We can't tell which are
    // tagged from the array, so the UI just notes the rule.
    public const int TagBonus = 2;

    private const string Anchor = "OEIUserSetting.Difficulty.Multiplier.PlayerDamage";
    private const int ArrayBase = 236;
    private const int Stride    = 4;
    private const int PointsOff = 337;

    // ---------------------------------------------------------------- slots

    public static List<SaveSlot> ListSaves()
    {
        var list = new List<SaveSlot>();
        if (!Directory.Exists(Root)) return list;

        foreach (var dir in Directory.GetDirectories(Root))
        {
            var sg = Path.Combine(dir, "SaveGame.dat");
            var md = Path.Combine(dir, "Metadata.dat");
            if (!File.Exists(sg)) continue;

            string who = "?";
            if (File.Exists(md))
            {
                var strings = MetaStrings(File.ReadAllBytes(md));
                // GMHF, <slot>, <character>, <slot>/SaveGameScreenshot.png, version, Release
                if (strings.Count > 2) who = strings[2];
            }

            list.Add(new SaveSlot
            {
                Slot = Path.GetFileName(dir),
                Character = who,
                Saved = File.GetLastWriteTime(sg),
                Bytes = new FileInfo(sg).Length
            });
        }
        return list.OrderByDescending(s => s.Saved).ToList();
    }

    private readonly record struct MetaString(int Pos, int Declared, string Value);

    /// <summary>
    /// Length-prefixed ASCII strings in Metadata.dat, in order:
    /// GMHF, &lt;slot&gt;, &lt;character&gt;, &lt;slot&gt;/SaveGameScreenshot.png, version, Release
    /// </summary>
    private static List<MetaString> MetaStringsWithPos(byte[] m)
    {
        var found = new List<MetaString>();
        for (int i = 0; i + 4 < m.Length; i++)
        {
            int declared = BitConverter.ToInt32(m, i);
            if (declared < 2 || declared > 128 || i + 4 + declared > m.Length) continue;
            bool ok = m[i + 4 + declared - 1] == 0;
            for (int j = 0; ok && j < declared - 1; j++)
                if (m[i + 4 + j] < 0x20 || m[i + 4 + j] > 0x7E) ok = false;
            if (!ok) continue;
            found.Add(new MetaString(i, declared, Encoding.ASCII.GetString(m, i + 4, declared - 1)));
            i += 4 + declared - 1;
        }
        return found;
    }

    private static List<string> MetaStrings(byte[] m) =>
        MetaStringsWithPos(m).Select(s => s.Value).ToList();

    private const int NameIndex = 2;
    public const int MaxNameLength = 40;

    public static string ReadName(string slot)
    {
        var md = File.ReadAllBytes(Path.Combine(Root, slot, "Metadata.dat"));
        var strings = MetaStringsWithPos(md);
        return strings.Count > NameIndex ? strings[NameIndex].Value : "";
    }

    /// <summary>
    /// Rewrites the name shown in the load list. Unlike the skill edits this changes a
    /// string length, so Metadata.dat is rebuilt around it rather than patched in place.
    /// Only Metadata.dat is touched -- the character name inside SaveGame.dat is a
    /// separate string and is left alone.
    /// </summary>
    public static void Rename(string slot, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0)
            throw new ArgumentException("Name cannot be empty.");
        if (newName.Length > MaxNameLength)
            throw new ArgumentException($"Name must be {MaxNameLength} characters or fewer.");
        if (newName.Any(c => c < 0x20 || c > 0x7E))
            throw new ArgumentException("Name must be plain ASCII (no accents or emoji).");

        string mdPath = Path.Combine(Root, slot, "Metadata.dat");
        var md = File.ReadAllBytes(mdPath);
        var strings = MetaStringsWithPos(md);
        if (strings.Count <= NameIndex)
            throw new InvalidDataException("Could not find the name field in Metadata.dat.");

        var target = strings[NameIndex];
        if (target.Value == newName) return;

        var body = Encoding.ASCII.GetBytes(newName);
        using var ms = new MemoryStream();
        ms.Write(md, 0, target.Pos);                                  // everything before
        ms.Write(BitConverter.GetBytes(body.Length + 1));             // new length prefix
        ms.Write(body);
        ms.WriteByte(0);
        int after = target.Pos + 4 + target.Declared;
        ms.Write(md, after, md.Length - after);                       // everything after
        var rebuilt = ms.ToArray();

        // The metadata carries the state file's inflated size. Renaming shifts where that
        // field sits, so confirm it survived the rebuild and is still unambiguous.
        int inflated = Inflate(File.ReadAllBytes(Path.Combine(Root, slot, "SaveGame.dat"))).Length;
        int hits = 0;
        for (int i = 0; i + 4 <= rebuilt.Length; i++)
            if (BitConverter.ToInt32(rebuilt, i) == inflated) hits++;
        if (hits != 1)
            throw new InvalidDataException(
                $"After rename the size field for {inflated} appears {hits} times, expected 1. Not written.");

        var check = MetaStringsWithPos(rebuilt);
        if (check.Count != strings.Count || check[NameIndex].Value != newName)
            throw new InvalidDataException("Rebuilt metadata did not re-parse correctly. Not written.");

        File.WriteAllBytes(mdPath, rebuilt);
    }

    // ---------------------------------------------------------- compression

    public static byte[] Inflate(byte[] b)
    {
        if (b.Length < 2 || b[0] != 0x78) return b;
        using var src = new MemoryStream(b);
        src.Position = 2;                              // skip zlib header
        using var ds = new DeflateStream(src, CompressionMode.Decompress);
        using var outp = new MemoryStream();
        ds.CopyTo(outp);
        return outp.ToArray();
    }

    public static byte[] Deflate(byte[] b)
    {
        using var outp = new MemoryStream();
        using (var zs = new ZLibStream(outp, CompressionLevel.Optimal, true))
            zs.Write(b, 0, b.Length);
        return outp.ToArray();
    }

    // ------------------------------------------------------------- locating

    private static IEnumerable<int> IndexOfAll(byte[] hay, byte[] needle)
    {
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) yield return i;
        }
    }

    private static bool IsChunkHeader(byte[] b, int i)
    {
        if (i < 0 || i + 25 > b.Length) return false;
        if (b[i] != 5 || b[i + 1] != 0 || b[i + 2] != 0 || b[i + 3] != 0) return false;
        for (int k = 4; k < 8; k++) if (b[i + k] < (byte)'A' || b[i + k] > (byte)'Z') return false;
        return b[i + 8] == 0;
    }

    /// <summary>
    /// Absolute offset of the chunk header holding the skills, or -1.
    /// Converted saves carry a stale base layer, so the LAST anchor hit is the live one.
    /// </summary>
    public static int FindSkillRecord(byte[] raw)
    {
        var anchor = Encoding.ASCII.GetBytes(Anchor);
        int rec = -1;
        foreach (int at in IndexOfAll(raw, anchor))
        {
            int limit = Math.Max(0, at - 8000);
            for (int i = at; i >= limit; i--)
                if (IsChunkHeader(raw, i)) { rec = i; break; }
        }
        return rec;
    }

    // ----------------------------------------------------------- read/write

    public sealed class SkillData
    {
        public int Record;
        public int PayloadLen;
        public string Magic = "";
        public int[] Values = Array.Empty<int>();
        public int Points;
        public int? Bits;            // null when the landmark isn't found -- field is disabled
    }

    public static SkillData Read(string slot)
    {
        var raw = Inflate(File.ReadAllBytes(Path.Combine(Root, slot, "SaveGame.dat")));
        int rec = FindSkillRecord(raw);
        if (rec < 0) throw new InvalidDataException(
            "Could not find the skill record. This save may be from a different game build.");

        int len = BitConverter.ToInt32(raw, rec + 21);
        int p = rec + 25;
        if (ArrayBase + Stride * SkillNames.Length > len)
            throw new InvalidDataException("Skill array runs past the record payload.");

        var vals = new int[SkillNames.Length];
        for (int i = 0; i < vals.Length; i++)
            vals[i] = BitConverter.ToInt32(raw, p + ArrayBase + Stride * i);

        if (vals.Any(v => v < 0 || v > 1000))
            throw new InvalidDataException(
                $"Values at the expected offsets don't look like skills ({string.Join(", ", vals)}).");

        int bitsOff = LocateBits(raw, FindLivePlayerDat(raw));

        return new SkillData
        {
            Record = rec,
            PayloadLen = len,
            Magic = Encoding.ASCII.GetString(raw, rec + 4, 4),
            Values = vals,
            Points = raw[p + PointsOff],
            Bits = bitsOff < 0 ? null : BitConverter.ToInt32(raw, FindLivePlayerDat(raw).Payload + bitsOff)
        };
    }

    /// <summary>Writes values in place. Returns the backup directory.</summary>
    public static string Write(string slot, int[] values, int points, int? bits = null)
    {
        if (values.Length != SkillNames.Length) throw new ArgumentException("wrong skill count");
        if (points is < 0 or > 255) throw new ArgumentException("Points must be 0-255.");
        if (values.Any(v => v < 0)) throw new ArgumentException("Skill values cannot be negative.");

        string dir = Path.Combine(Root, slot);
        string sgPath = Path.Combine(dir, "SaveGame.dat");
        string mdPath = Path.Combine(dir, "Metadata.dat");

        var raw = Inflate(File.ReadAllBytes(sgPath));
        int before = raw.Length;
        int rec = FindSkillRecord(raw);
        if (rec < 0) throw new InvalidDataException("Could not find the skill record.");
        int p = rec + 25;

        string backup = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"TOW2-skills-{DateTime.Now:yyyyMMdd-HHmmss}-{slot}");
        CopyDir(dir, backup);

        for (int i = 0; i < values.Length; i++)
            BitConverter.GetBytes(values[i]).CopyTo(raw, p + ArrayBase + Stride * i);
        raw[p + PointsOff] = (byte)points;

        if (bits.HasValue)
        {
            if (bits.Value < 0) throw new ArgumentException("Bits cannot be negative.");
            var live = FindLivePlayerDat(raw);
            int bitsOff = LocateBits(raw, live);
            if (bitsOff < 0)
                throw new InvalidDataException(
                    "Could not locate the bits field in this save, so it was not changed.");
            BitConverter.GetBytes(bits.Value).CopyTo(raw, live.Payload + bitsOff);
        }

        if (raw.Length != before)
            throw new InvalidDataException("Payload length changed -- aborted.");

        // In-place edits leave the length alone, so the metadata size field must still match.
        var md = File.ReadAllBytes(mdPath);
        int hits = 0;
        for (int i = 0; i + 4 <= md.Length; i++)
            if (BitConverter.ToInt32(md, i) == raw.Length) hits++;
        if (hits != 1)
            throw new InvalidDataException(
                $"Metadata size field: expected exactly 1 match for {raw.Length}, found {hits}.");

        File.WriteAllBytes(sgPath, Deflate(raw));

        // verify by read-back
        var check = Read(slot);
        for (int i = 0; i < values.Length; i++)
            if (check.Values[i] != values[i])
                throw new InvalidDataException($"Read-back mismatch on {SkillNames[i]}.");
        if (check.Points != points) throw new InvalidDataException("Read-back mismatch on Points.");
        if (bits.HasValue && check.Bits != bits.Value)
            throw new InvalidDataException("Read-back mismatch on Bits.");

        return backup;
    }

    // -------------------------------------------------------------- bits (currency)

    // Player.dat holds a list of 84-byte entries: [u32 2][u32 1][u32 id][u32 0x00010B01]
    // then 17 u32 of payload. Bits is payload[12] of one of them.
    //
    // The entry `id` is NOT usable as a key -- it is an allocation counter, not a
    // meaning. Across saves of one character the ids shift by one, and a brand-new
    // character only has ids 36-45 at all.
    //
    // Instead, anchor on a neighbouring entry whose payload is constant, and which sits a
    // fixed 154 bytes before the bits entry. Verified across 12 saves spanning two
    // sessions: exactly one match each, and the values form a clean monotonic progression
    // (2877, 2886, 3791, 4148, 4148, 5197, 5239) ending on a figure confirmed in game.
    private const int EntryTag = 0x00010B01;
    private const int EntryStride = 84;
    private static readonly int[] BitsLandmark = { 33554432, 1161527296, 0, 11, 256, 256 };
    private const int BitsLandmarkFirstIndex = 11;   // payload index the landmark starts at
    private const int BitsDelta = 218;               // landmark entry start -> bits u32

    /// <summary>Offset of the bits u32 within the live Player.dat, or -1 if not locatable.</summary>
    private static int LocateBits(byte[] raw, LiveEntry live)
    {
        int found = -1, count = 0;
        for (int r = 0; r + EntryStride <= live.Size; r++)
        {
            if (BitConverter.ToInt32(raw, live.Payload + r) != 2) continue;
            if (BitConverter.ToInt32(raw, live.Payload + r + 4) != 1) continue;
            if (BitConverter.ToInt32(raw, live.Payload + r + 12) != EntryTag) continue;

            bool ok = true;
            for (int k = 0; k < BitsLandmark.Length; k++)
            {
                int idx = BitsLandmarkFirstIndex + k;
                if (BitConverter.ToInt32(raw, live.Payload + r + 16 + 4 * idx) != BitsLandmark[k]) { ok = false; break; }
            }
            if (!ok) continue;
            found = r; count++;
        }
        // Ambiguity means the assumption has broken -- refuse rather than guess.
        if (count != 1) return -1;
        int off = found + BitsDelta;
        return off + 4 <= live.Size ? off : -1;
    }

    public static int? ReadBits(string slot)
    {
        var raw = Inflate(File.ReadAllBytes(Path.Combine(Root, slot, "SaveGame.dat")));
        var live = FindLivePlayerDat(raw);
        int off = LocateBits(raw, live);
        return off < 0 ? null : BitConverter.ToInt32(raw, live.Payload + off);
    }

    // ------------------------------------------------- character name (payload)

    // Absolute offsets into the live Player.dat payload holding u64 self-pointers.
    // Both reference string-table entries later in the entry, so an edit that changes
    // the payload length must shift any that sit past the edit point. Confirmed by
    // diffing game-written saves: when the entry grew 572 bytes, +32 grew by 572.
    private static readonly int[] SelfPointerOffsets = { 24, 32 };

    private readonly record struct LiveEntry(int Prefix, int SizePos, int Size, int Payload);

    private static LiveEntry FindLivePlayerDat(byte[] raw)
    {
        // Saves carry a base layer nested inside a SavedState.dat container, so the
        // LAST Player.dat entry is the live one.
        var name = Encoding.ASCII.GetBytes("Player.dat");
        int found = -1;
        for (int i = 0; i + 15 < raw.Length; i++)
        {
            if (BitConverter.ToInt32(raw, i) != 11) continue;
            bool ok = true;
            for (int j = 0; j < 10; j++) if (raw[i + 4 + j] != name[j]) { ok = false; break; }
            if (ok && raw[i + 14] == 0) found = i;
        }
        if (found < 0) throw new InvalidDataException("No Player.dat entry found.");
        int sizePos = found + 15;
        return new LiveEntry(found, sizePos, BitConverter.ToInt32(raw, sizePos), sizePos + 4);
    }

    /// <summary>Does rel point at a [u32 id][u32 len][ascii][NUL] string-table entry?</summary>
    private static bool ResolvesToString(byte[] raw, int payload, int limit, long rel, out string value)
    {
        value = "";
        if (rel < 8 || rel + 8 >= limit) return false;
        int L = BitConverter.ToInt32(raw, payload + (int)rel + 4);
        if (L < 3 || L > 160 || rel + 8 + L > limit) return false;
        if (raw[payload + (int)rel + 8 + L - 1] != 0) return false;
        for (int k = 0; k < L - 1; k++)
        {
            byte c = raw[payload + (int)rel + 8 + k];
            if (c < 0x20 || c > 0x7E) return false;
        }
        value = Encoding.ASCII.GetString(raw, payload + (int)rel + 8, L - 1);
        return true;
    }

    /// <summary>
    /// Locates the character-name string inside the live Player.dat by structure rather
    /// than by matching the metadata name, since the two can diverge once either is
    /// edited. Signature: a CSHF record whose length is exactly 21 + L, where L is the
    /// u32 at payload+16 and an ASCII string of L bytes (NUL-terminated) follows at +20.
    /// Verified to match exactly once across every sampled save.
    /// </summary>
    private static (int StrPos, string Value) FindCharacterName(byte[] raw, LiveEntry live)
    {
        var hits = new List<(int, string)>();
        int end = live.Payload + live.Size;
        for (int i = live.Payload; i + 25 < end && i + 25 < raw.Length; i++)
        {
            if (raw[i] != 5 || raw[i + 1] != 0 || raw[i + 2] != 0 || raw[i + 3] != 0 || raw[i + 8] != 0) continue;
            if (raw[i + 4] != 'C' || raw[i + 5] != 'S' || raw[i + 6] != 'H' || raw[i + 7] != 'F') continue;

            int recLen = BitConverter.ToInt32(raw, i + 21);
            int pl = i + 25;
            if (recLen < 24 || recLen > 200 || pl + recLen > raw.Length) continue;

            int L = BitConverter.ToInt32(raw, pl + 16);
            if (L < 2 || L > 64 || recLen != 21 + L) continue;
            if (raw[pl + 20 + L - 1] != 0) continue;

            bool ascii = true;
            for (int k = 0; k < L - 1; k++)
            {
                byte c = raw[pl + 20 + k];
                if (c < 0x20 || c > 0x7E) { ascii = false; break; }
            }
            if (!ascii) continue;

            hits.Add((pl + 16, Encoding.ASCII.GetString(raw, pl + 20, L - 1)));
        }
        if (hits.Count != 1)
            throw new InvalidDataException(
                $"Expected exactly one character-name record, found {hits.Count}. " +
                "The format may differ on this game build.");
        return hits[0];
    }

    public static string ReadCharacterName(string slot)
    {
        var raw = Inflate(File.ReadAllBytes(Path.Combine(Root, slot, "SaveGame.dat")));
        return FindCharacterName(raw, FindLivePlayerDat(raw)).Value;
    }

    /// <summary>
    /// Renames the character inside SaveGame.dat. This changes the payload length, so
    /// five values must stay consistent: the CSHF record length, the two u64
    /// self-pointers in the PSHF preamble, the Player.dat entry size, and the metadata's
    /// inflated-size field. Missing the pointers loads the character as DefaultPlayerMax.
    /// </summary>
    public static void RenameCharacter(string slot, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) throw new ArgumentException("Character name cannot be empty.");
        if (newName.Length > MaxNameLength)
            throw new ArgumentException($"Character name must be {MaxNameLength} characters or fewer.");
        if (newName.Any(c => c < 0x20 || c > 0x7E))
            throw new ArgumentException("Character name must be plain ASCII.");

        string dir = Path.Combine(Root, slot);
        string sgPath = Path.Combine(dir, "SaveGame.dat");
        string mdPath = Path.Combine(dir, "Metadata.dat");

        var raw = Inflate(File.ReadAllBytes(sgPath));
        var live = FindLivePlayerDat(raw);
        var (strPos, oldName) = FindCharacterName(raw, live);
        if (oldName == newName) return;

        int oldFieldLen = 4 + oldName.Length + 1;   // length prefix + string + NUL
        int nameRel = strPos - live.Payload;
        int delta = newName.Length - oldName.Length;

        // enclosing CSHF record
        int recPos = -1;
        for (int i = strPos; i >= live.Payload; i--)
        {
            if (raw[i] != 5 || raw[i + 1] != 0 || raw[i + 2] != 0 || raw[i + 3] != 0 || raw[i + 8] != 0) continue;
            bool up = true;
            for (int k = 4; k < 8; k++) if (raw[i + k] < (byte)'A' || raw[i + k] > (byte)'Z') { up = false; break; }
            if (up) { recPos = i; break; }
        }
        if (recPos < 0) throw new InvalidDataException("No chunk record encloses the name.");
        int recLen = BitConverter.ToInt32(raw, recPos + 21);
        if (strPos + oldFieldLen > recPos + 25 + recLen)
            throw new InvalidDataException("The name straddles the end of its record.");

        // the self-pointers must look like pointers BEFORE we touch anything
        var ptrVals = new long[SelfPointerOffsets.Length];
        for (int i = 0; i < SelfPointerOffsets.Length; i++)
        {
            ptrVals[i] = BitConverter.ToInt64(raw, live.Payload + SelfPointerOffsets[i]);
            if (!ResolvesToString(raw, live.Payload, live.Size, ptrVals[i], out _))
                throw new InvalidDataException(
                    $"The value at Player.dat+{SelfPointerOffsets[i]} is not a self-pointer in this save. " +
                    "Refusing to rename -- the format may differ on this build.");
        }

        // rebuild
        using var ms = new MemoryStream();
        ms.Write(raw, 0, strPos);
        ms.Write(BitConverter.GetBytes(newName.Length + 1));
        ms.Write(Encoding.ASCII.GetBytes(newName));
        ms.WriteByte(0);
        int after = strPos + oldFieldLen;
        ms.Write(raw, after, raw.Length - after);
        var built = ms.ToArray();

        // the five fixups
        BitConverter.GetBytes(recLen + delta).CopyTo(built, recPos + 21);
        BitConverter.GetBytes(live.Size + delta).CopyTo(built, live.SizePos);
        for (int i = 0; i < SelfPointerOffsets.Length; i++)
            if (ptrVals[i] > nameRel && ptrVals[i] < live.Size)
                BitConverter.GetBytes(ptrVals[i] + delta).CopyTo(built, live.Payload + SelfPointerOffsets[i]);

        // verify: pointers still resolve to the same strings
        int newSize = live.Size + delta;
        for (int i = 0; i < SelfPointerOffsets.Length; i++)
        {
            long v = BitConverter.ToInt64(built, live.Payload + SelfPointerOffsets[i]);
            ResolvesToString(raw, live.Payload, live.Size, ptrVals[i], out string was);
            if (!ResolvesToString(built, live.Payload, newSize, v, out string now) || now != was)
                throw new InvalidDataException(
                    $"Self-pointer at +{SelfPointerOffsets[i]} no longer resolves to \"{was}\". Not written.");
        }
        if (built.Length != raw.Length + delta)
            throw new InvalidDataException("Payload length math is wrong. Not written.");

        // verify: metadata size field is unambiguous, then update it
        var md = File.ReadAllBytes(mdPath);
        var hits = new List<int>();
        for (int i = 0; i + 4 <= md.Length; i++)
            if (BitConverter.ToInt32(md, i) == raw.Length) hits.Add(i);
        if (hits.Count != 1)
            throw new InvalidDataException($"Metadata size field: expected 1 match, found {hits.Count}.");
        BitConverter.GetBytes(built.Length).CopyTo(md, hits[0]);

        File.WriteAllBytes(sgPath, Deflate(built));
        File.WriteAllBytes(mdPath, md);
    }

    // ---------------------------------------------------------------- clone

    /// <summary>
    /// Clones a 32-hex manual save under a fresh slot id. The slot name appears twice in
    /// Metadata.dat (standalone, and in "&lt;Slot&gt;/SaveGameScreenshot.png"); both are 32
    /// chars, so the swap is length-neutral. SaveGame.dat holds no slot reference.
    /// </summary>
    public static string Clone(string slot)
    {
        if (slot.Length != 32 || !slot.All(Uri.IsHexDigit))
            throw new ArgumentException("Only 32-hex manual saves can be cloned (not autosaves).");

        string src = Path.Combine(Root, slot);
        string newSlot = Guid.NewGuid().ToString("N").ToUpperInvariant();
        string dst = Path.Combine(Root, newSlot);
        if (Directory.Exists(dst)) throw new IOException("Destination slot already exists.");

        CopyDir(src, dst);

        var md = File.ReadAllBytes(Path.Combine(dst, "Metadata.dat"));
        var oldB = Encoding.ASCII.GetBytes(slot);
        var newB = Encoding.ASCII.GetBytes(newSlot);
        int patched = 0;
        foreach (int at in IndexOfAll(md, oldB)) { newB.CopyTo(md, at); patched++; }
        if (patched == 0) throw new InvalidDataException("Slot name not found in Metadata.dat.");

        File.WriteAllBytes(Path.Combine(dst, "Metadata.dat"), md);
        return newSlot;
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
    }

    public static bool GameIsRunning() =>
        System.Diagnostics.Process.GetProcesses().Any(p =>
            p.ProcessName.Contains("OuterWorlds", StringComparison.OrdinalIgnoreCase) ||
            p.ProcessName.Contains("Arkansas", StringComparison.OrdinalIgnoreCase));
}
