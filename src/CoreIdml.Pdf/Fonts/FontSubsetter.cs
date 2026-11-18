using System.Buffers.Binary;

namespace CoreIdml.Pdf.Fonts;

/// <summary>
/// Subsets a TrueType-outline font for embedding as a CIDFontType2 with an identity CID-to-GID map: glyph ids are
/// kept, outlines of unused glyphs are emptied, and only the tables a PDF renderer needs are written. Composite
/// glyphs pull in their components.
/// </summary>
internal static class FontSubsetter
{
    private static readonly string[] KeptTables = ["cmap", "cvt ", "fpgm", "glyf", "head", "hhea", "hmtx", "loca", "maxp", "name", "OS/2", "post", "prep"];

    public static byte[] Subset(OpenTypeFont font, IEnumerable<ushort> usedGlyphs)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(usedGlyphs);
        var data = font.RawData;
        var tables = font.Tables;
        if (font.IsCff || !tables.TryGetValue("glyf", out var glyf) || !tables.TryGetValue("loca", out var loca))
        {
            throw new InvalidOperationException("Only TrueType-outline fonts can be subset.");
        }
        var head = tables["head"];
        var longLoca = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(head.Offset + 50)) == 1;
        var glyphCount = font.GlyphCount;

        int GlyphOffset(int glyph) => longLoca
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(loca.Offset + (4 * glyph)))
            : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(loca.Offset + (2 * glyph))) * 2;

        // Close the set over composite components.
        var keep = new SortedSet<int> { 0 };
        var pending = new Stack<int>(usedGlyphs.Select(g => (int)g).Where(g => g < glyphCount));
        while (pending.Count > 0)
        {
            var glyph = pending.Pop();
            if (!keep.Add(glyph) && glyph != 0)
            {
                continue;
            }
            var start = GlyphOffset(glyph);
            var end = GlyphOffset(glyph + 1);
            if (end - start < 10)
            {
                continue;
            }
            var at = glyf.Offset + start;
            if (BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(at)) >= 0)
            {
                continue;
            }
            // Composite: flags, glyphIndex, args, optional transform.
            at += 10;
            while (true)
            {
                var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
                var component = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2));
                if (!keep.Contains(component))
                {
                    pending.Push(component);
                }
                at += 4 + ((flags & 1) != 0 ? 4 : 2);
                at += (flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0;
                if ((flags & 0x20) == 0)
                {
                    break;
                }
            }
        }

        // New glyf and (long) loca.
        using var glyfOut = new MemoryStream();
        var newLoca = new byte[4 * (glyphCount + 1)];
        for (var glyph = 0; glyph < glyphCount; glyph++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(4 * glyph), (uint)glyfOut.Length);
            if (keep.Contains(glyph))
            {
                var start = GlyphOffset(glyph);
                var end = GlyphOffset(glyph + 1);
                if (end > start && glyf.Offset + end <= data.Length)
                {
                    glyfOut.Write(data, glyf.Offset + start, end - start);
                    while (glyfOut.Length % 4 != 0)
                    {
                        glyfOut.WriteByte(0);
                    }
                }
            }
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(4 * glyphCount), (uint)glyfOut.Length);

        var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in KeptTables)
        {
            if (tables.TryGetValue(tag, out var table))
            {
                output[tag] = data.AsSpan(table.Offset, table.Length).ToArray();
            }
        }
        output["glyf"] = glyfOut.ToArray();
        output["loca"] = newLoca;
        var newHead = output["head"];
        BinaryPrimitives.WriteInt16BigEndian(newHead.AsSpan(50), 1);
        BinaryPrimitives.WriteUInt32BigEndian(newHead.AsSpan(8), 0);
        if (output.TryGetValue("post", out var post) && post.Length >= 32)
        {
            // Glyph names are not needed (format 3 keeps only the header).
            var header = post.AsSpan(0, 32).ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(header, 0x00030000);
            output["post"] = header;
        }
        return Assemble(output);
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var count = tables.Count;
        var searchRange = 1;
        var entrySelector = 0;
        while (searchRange * 2 <= count)
        {
            searchRange *= 2;
            entrySelector++;
        }
        searchRange *= 16;
        var headerLength = 12 + (16 * count);
        var total = headerLength + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var result = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(result, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10), (ushort)((count * 16) - searchRange));
        var offset = headerLength;
        var index = 0;
        var headOffset = -1;
        foreach (var (tag, table) in tables)
        {
            var record = 12 + (16 * index++);
            for (var i = 0; i < 4; i++)
            {
                result[record + i] = (byte)tag[i];
            }
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(record + 4), Checksum(table));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(record + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(record + 12), (uint)table.Length);
            table.CopyTo(result, offset);
            if (tag == "head")
            {
                headOffset = offset;
            }
            offset += (table.Length + 3) & ~3;
        }
        if (headOffset >= 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(headOffset + 8), unchecked(0xB1B0AFBA - Checksum(result)));
        }
        return result;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        }
        if (i < data.Length)
        {
            Span<byte> tail = stackalloc byte[4];
            tail.Clear();
            data[i..].CopyTo(tail);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(tail));
        }
        return sum;
    }
}
