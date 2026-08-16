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

        return new SkillData
        {
            Record = rec,
            PayloadLen = len,
            Magic = Encoding.ASCII.GetString(raw, rec + 4, 4),
            Values = vals,
            Points = raw[p + PointsOff]
        };
    }

    /// <summary>Writes values in place. Returns the backup directory.</summary>
    public static string Write(string slot, int[] values, int points)
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

        return backup;
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
