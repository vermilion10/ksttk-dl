using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace KsttkDl.Core.Fonts;

/// <summary>
/// unpacks a WOFF2 web font into a plain OpenType file that the desktop can load. only fonts whose tables are
/// stored untransformed are handled, which covers CFF-flavoured fonts like the game's: the glyf/loca transform
/// only exists for TrueType outlines
/// </summary>
public static class Woff2
{
    private const uint Signature = 0x774F4632; // "wOF2"

    // the spec's table of known tags, indexed by the low six bits of a directory entry's flags
    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep",
        "CFF ", "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE",
        "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt",
        "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just", "lcar",
        "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    private sealed record Table(string Tag, int Length);

    public static bool IsWoff2(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == Signature;

    /// <summary>the sfnt (.otf/.ttf) bytes inside a woff2 file.</summary>
    /// <exception cref="NotSupportedException">the font uses a table transform, or is a font collection.</exception>
    /// <exception cref="InvalidDataException">the data is not a well-formed woff2 file.</exception>
    public static byte[] ToSfnt(byte[] woff2)
    {
        ReadOnlySpan<byte> data = woff2;
        if (!IsWoff2(data) || data.Length < 48)
        {
            throw new InvalidDataException("Not a WOFF2 font.");
        }

        var flavor = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (flavor == 0x74746366) // "ttcf"
        {
            throw new NotSupportedException("WOFF2 font collections are not supported.");
        }

        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
        var compressedLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]);

        var offset = 48;
        var tables = new List<Table>(tableCount);
        for (var i = 0; i < tableCount; i++)
        {
            var flags = data[offset++];
            string tag;
            if ((flags & 0x3F) == 0x3F)
            {
                tag = Encoding.ASCII.GetString(data.Slice(offset, 4));
                offset += 4;
            }
            else
            {
                tag = KnownTags[flags & 0x3F];
            }

            var length = (int)ReadBase128(data, ref offset);
            var transform = (flags >> 6) & 3;

            // glyf and loca are transformed unless the version is 3; every other table is plain unless it is not 0
            var transformed = tag is "glyf" or "loca" ? transform != 3 : transform != 0;
            if (transformed)
            {
                throw new NotSupportedException($"The {tag.Trim()} table uses a WOFF2 transform, which is not supported.");
            }

            tables.Add(new Table(tag, length));
        }

        var stream = Decompress(woff2, offset, compressedLength);
        return BuildSfnt(flavor, tables, stream);
    }

    private static byte[] Decompress(byte[] source, int offset, int length)
    {
        using var input = new MemoryStream(source, offset, Math.Min(length, source.Length - offset), writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] BuildSfnt(uint flavor, List<Table> tables, byte[] stream)
    {
        // the directory has to be sorted by tag, while the data keeps the order it came in
        var sorted = tables.Select((t, i) => (Table: t, Index: i))
            .OrderBy(t => t.Table.Tag, StringComparer.Ordinal).ToList();

        var sources = new int[tables.Count];
        var position = 0;
        for (var i = 0; i < tables.Count; i++)
        {
            sources[i] = position;
            position += tables[i].Length;
            if (position > stream.Length)
            {
                throw new InvalidDataException("WOFF2 table data is shorter than its directory says.");
            }
        }

        var headerLength = 12 + 16 * tables.Count;
        var total = headerLength + tables.Sum(t => Pad4(t.Length));
        var output = new byte[total];
        Span<byte> span = output;

        var entrySelector = (int)Math.Floor(Math.Log2(tables.Count));
        var searchRange = (1 << entrySelector) * 16;
        BinaryPrimitives.WriteUInt32BigEndian(span, flavor);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)tables.Count);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(tables.Count * 16 - searchRange));

        var dataOffset = headerLength;
        var headOffset = -1;
        for (var i = 0; i < sorted.Count; i++)
        {
            var (table, index) = sorted[i];
            stream.AsSpan(sources[index], table.Length).CopyTo(span[dataOffset..]);
            if (table.Tag == "head" && table.Length >= 12)
            {
                headOffset = dataOffset;
                BinaryPrimitives.WriteUInt32BigEndian(span[(dataOffset + 8)..], 0);
            }

            var entry = span[(12 + 16 * i)..];
            Encoding.ASCII.GetBytes(table.Tag, entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], Checksum(span.Slice(dataOffset, Pad4(table.Length))));
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)dataOffset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], (uint)table.Length);
            dataOffset += Pad4(table.Length);
        }

        if (headOffset >= 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[(headOffset + 8)..], 0xB1B0AFBA - Checksum(span));
        }

        return output;
    }

    private static int Pad4(int length) => (length + 3) & ~3;

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        }

        return sum;
    }

    private static uint ReadBase128(ReadOnlySpan<byte> data, ref int offset)
    {
        uint value = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = data[offset++];
            if (i == 0 && b == 0x80)
            {
                throw new InvalidDataException("Malformed WOFF2 number.");
            }

            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("Malformed WOFF2 number.");
    }

    /// <summary>the font's family name, as Windows lists it (name id 1, US English), or null when it has none.</summary>
    public static string? FamilyName(byte[] sfnt)
    {
        ReadOnlySpan<byte> data = sfnt;
        if (data.Length < 12)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        for (var i = 0; i < count; i++)
        {
            var entry = data[(12 + 16 * i)..];
            if (Encoding.ASCII.GetString(entry[..4]) != "name")
            {
                continue;
            }

            var table = data[(int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..])..];
            var records = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
            var strings = table[BinaryPrimitives.ReadUInt16BigEndian(table[4..])..];
            string? fallback = null;
            for (var r = 0; r < records; r++)
            {
                var record = table[(6 + 12 * r)..];
                var platform = BinaryPrimitives.ReadUInt16BigEndian(record);
                var language = BinaryPrimitives.ReadUInt16BigEndian(record[4..]);
                var nameId = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
                var length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
                var start = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
                if (nameId != 1 || platform != 3)
                {
                    continue;
                }

                var name = Encoding.BigEndianUnicode.GetString(strings.Slice(start, length));
                if (language == 0x409)
                {
                    return name;
                }

                fallback ??= name;
            }

            return fallback;
        }

        return null;
    }
}
