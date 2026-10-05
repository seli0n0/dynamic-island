using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DynamicIsland;

/// <summary>
/// Reads the names a font file keeps in its <c>name</c> table, so a loose file can be listed by the name the font
/// answers to rather than by the name someone gave it on disk. Only the header and that one table are looked at,
/// and nothing is installed or registered: a file that turns out not to be a font simply has no names and never
/// complains about it.
/// </summary>
static class FontNames
{
    /// <summary>Endings a loose font is taken under. Collections and web fonts are left out, not skipped over.</summary>
    public static string[] Extensions { get; } = [".ttf", ".otf"];

    const uint Sfnt = 0x0001_0000;                 // the plain TrueType outline
    const uint True = 0x7472_7565, Typ1 = 0x7479_7031;   // 'true', 'typ1'
    const uint Otto = 0x4F54_544F;                 // 'OTTO' — PostScript outlines in the same wrapper
    const uint Ttc = 0x7474_6366;                 // 'ttcf' — several fonts sharing a file, a different layout
    const uint Woff = 0x774F_4646, Woff2 = 0x774F_4632; // 'wOFF', 'wOF2' — WPF cannot draw with these at all

    const uint NameTag = 0x6E61_6D65;              // 'name'
    const int MaxTables = 64, MaxTableBytes = 1 << 20; // a real offset table sits in the low teens, a real name table is small

    /// <summary>Family names a font file claims (name ID 1, falling back to ID 16). Deduplicated, Windows platform IDs preferred, includes non-English names.</summary>
    public static string[] Families(string path) => Names(path, 1, 16);

    /// <summary>Full names (ID 4 / 18) and the postscript name (ID 6), for the settings page to show what a file really is.</summary>
    public static string[] FullNames(string path) => [.. Names(path, 4, 18), .. Names(path, 6, 0)];

    /// <summary>Whether the bytes up front are a single font WPF could be pointed at.</summary>
    public static bool LooksLikeFont(string path)
    {
        try
        {
            return Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                && Supported(Magic(path));
        }
        catch
        {
            return false;
        }
    }

    static uint Magic(string path)
    {
        using var file = File.OpenRead(path);
        var head = new byte[4];
        return ReadAll(file, head) ? BinaryPrimitives.ReadUInt32BigEndian(head) : 0;
    }

    static bool Supported(uint tag) => tag is Sfnt or True or Typ1 or Otto;

    static string[] Names(string path, ushort nameId, ushort instead)
    {
        List<Entry> entries;
        try
        {
            entries = Entries(path) ?? [];
        }
        catch
        {
            return []; // half of it being unreadable is no reason to spoil the rest of the list
        }

        // ID 1 is the family the operating system and the '#' in a font source both match against;
        // ID 16 only says something when ID 1 has nothing to say.
        var picked = entries.FindAll(e => e.Id == nameId);
        if (picked.Count == 0 && instead != 0) picked = entries.FindAll(e => e.Id == instead);

        picked.Sort(static (a, b) => Rank(a).CompareTo(Rank(b))); // stable, so the file's own order holds inside a rank
        var names = new List<string>(picked.Count);
        foreach (var e in picked)
            if (!names.Contains(e.Value, StringComparer.OrdinalIgnoreCase)) names.Add(e.Value);
        return [.. names];
    }

    /// <summary>0 for the Windows English records, which are the ones Windows itself shows, then the rest.</summary>
    static int Rank(Entry e) => e.Platform == 3 ? (e.Language == 0x0409 ? 0 : 1) : 2;

    static List<Entry>? Entries(string path)
    {
        using var file = File.OpenRead(path);
        var head = new byte[12];
        if (!ReadAll(file, head) || !Supported(BinaryPrimitives.ReadUInt32BigEndian(head))) return null;

        var tables = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
        if (tables is 0 or > MaxTables) return null;

        var directory = new byte[tables * 16];
        if (!ReadAll(file, directory)) return null;

        var slot = -1;
        for (int at = 0; at + 16 <= directory.Length; at += 16)
            if (BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(at)) == NameTag) { slot = at; break; }
        // 'loca' alone is enough to draw with, so a font without a name table simply tells us nothing about itself
        if (slot < 0) return null;

        // in a record the four bytes after the tag are the checksum, so the place to look is past that
        long offset = BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(slot + 8));
        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(slot + 12));
        if (length is <= 0 or > MaxTableBytes) return null;

        var table = new byte[length];
        file.Position = offset;
        return ReadAll(file, table) ? Parse(table) : null;
    }

    static List<Entry> Parse(byte[] t)
    {
        var list = new List<Entry>();
        if (t.Length < 6) return list;

        int count = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(2)), storage = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(4));
        for (int i = 0, at = 6; i < count; i++, at += 12)
        {
            if (at + 12 > t.Length) break; // the records run past the table
            var platform = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(at));
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(at + 2));
            var language = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(at + 4));
            var id = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(at + 6));
            int length = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(at + 8)), from = storage + BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(at + 10));

            if (from < 0 || from + length > t.Length) continue; // a record reaching past the strings is junk
            var value = Decode(platform, encoding, t, from, length);
            if (value != null) list.Add(new Entry(platform, language, id, value));
        }
        return list;
    }

    /// <summary>
    /// The Windows records are UTF-16 big endian; the Mac ones are single byte, whose roman table reads as Latin-1.
    /// Anything else is a guess worth making rather than dropping.
    /// </summary>
    static string? Decode(ushort platform, ushort encoding, byte[] t, int from, int length)
    {
        string raw = platform switch
        {
            3 when encoding is 0 or 1 => Utf16Be(t, from, length),
            0 or 2 => Utf16Be(t, from, length),
            1 when encoding != 0 => Encoding.UTF8.GetString(t, from, length),
            _ => Encoding.Latin1.GetString(t, from, length)
        };

        var name = raw.Trim('\0', ' ');
        if (name.Length == 0 || name.Length > 96) return null;
        foreach (var c in name)
            if (char.IsControl(c) || c is '\uFFFD' or '#' or '/') return null; // would break a font source anyway
        return name;
    }

    static string Utf16Be(byte[] t, int from, int length)
    {
        var chars = new char[length / 2];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(from + i * 2));
        return new string(chars);
    }

    static bool ReadAll(Stream stream, byte[] into)
    {
        try
        {
            stream.ReadExactly(into);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    readonly record struct Entry(ushort Platform, ushort Language, ushort Id, string Value);
}
