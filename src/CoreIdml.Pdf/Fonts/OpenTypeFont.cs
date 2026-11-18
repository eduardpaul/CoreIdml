using System.Buffers.Binary;
using System.Text;

namespace CoreIdml.Pdf.Fonts;

/// <summary>
/// A parsed TrueType/OpenType face (.ttf, .otf, or one face of a .ttc): names, metrics, character map, advances
/// pair kerning (legacy <c>kern</c> table and GPOS pair adjustment) and standard ligatures (GSUB <c>liga</c>).
/// No shaping beyond that: no contextual forms or mark positioning. Immutable once parsed (shared through <see cref="FontCache"/>).
/// </summary>
internal sealed class OpenTypeFont
{
    private readonly byte[] data;
    private readonly Dictionary<string, (int Offset, int Length)> tables;
    private readonly Dictionary<int, ushort> cmap = [];
    private readonly ushort[] advances;
    private readonly Dictionary<uint, short> kernPairs = [];
    private readonly List<PairSubtable> gposPairs = [];
    // Standard ligatures by first glyph, in the font's order of preference.
    private readonly Dictionary<ushort, List<(ushort[] Following, ushort Ligature)>> ligatures = [];

    private OpenTypeFont(byte[] data, int fontOffset, string sourcePath, int faceIndex)
    {
        this.data = data;
        SourcePath = sourcePath;
        FaceIndex = faceIndex;
        tables = ReadTableDirectory(data, fontOffset);
        IsCff = tables.ContainsKey("CFF ") || tables.ContainsKey("CFF2");

        var head = Table("head");
        UnitsPerEm = U16(head, 18);
        BBox = (S16(head, 36), S16(head, 38), S16(head, 40), S16(head, 42));
        var macStyle = U16(head, 44);

        var maxp = Table("maxp");
        GlyphCount = U16(maxp, 4);

        var hhea = Table("hhea");
        HheaAscender = S16(hhea, 4);
        HheaDescender = S16(hhea, 6);
        LineGap = S16(hhea, 8);
        var metricCount = U16(hhea, 34);

        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 78)
        {
            var o = os2.Offset;
            WeightClass = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o + 4));
            FsType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o + 8));
            var fsSelection = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o + 62));
            TypoAscender = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(o + 68));
            TypoDescender = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(o + 70));
            IsItalic = (fsSelection & 1) != 0 || (fsSelection & 0x200) != 0;
            IsBold = (fsSelection & 0x20) != 0;
            if (os2.Length >= 90 && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o)) >= 2)
            {
                XHeight = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(o + 86));
                CapHeight = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(o + 88));
            }
        }
        else
        {
            TypoAscender = HheaAscender;
            TypoDescender = HheaDescender;
            IsBold = (macStyle & 1) != 0;
            IsItalic = (macStyle & 2) != 0;
            WeightClass = IsBold ? 700 : 400;
        }
        if (CapHeight <= 0)
        {
            CapHeight = (short)Math.Round(TypoAscender * 0.7);
        }
        if (XHeight <= 0)
        {
            XHeight = (short)Math.Round(TypoAscender * 0.5);
        }

        if (tables.TryGetValue("post", out var post) && post.Length >= 8)
        {
            ItalicAngle = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(post.Offset + 4)) / 65536.0;
        }

        advances = new ushort[GlyphCount];
        var hmtx = Table("hmtx");
        ushort last = 0;
        for (var glyph = 0; glyph < GlyphCount; glyph++)
        {
            if (glyph < metricCount && (glyph * 4) + 2 <= hmtx.Length)
            {
                last = U16(hmtx, glyph * 4);
            }
            advances[glyph] = last;
        }

        ReadNames();
        ReadCmap();
        ReadKern();
        ReadGpos();
        ReadGsubLigatures();
    }

    public string SourcePath { get; }

    public int FaceIndex { get; }

    /// <summary>Typographic family (name 16) when present, else the legacy family (name 1).</summary>
    public string FamilyName { get; private set; } = string.Empty;

    /// <summary>Typographic subfamily (name 17) when present, else the legacy subfamily (name 2).</summary>
    public string StyleName { get; private set; } = string.Empty;

    public string LegacyFamilyName { get; private set; } = string.Empty;

    public string LegacyStyleName { get; private set; } = string.Empty;

    public string PostScriptName { get; private set; } = string.Empty;

    public string FullName { get; private set; } = string.Empty;

    /// <summary>CFF outlines (.otf): embedded whole as an OpenType font program; TrueType outlines are subset.</summary>
    public bool IsCff { get; }

    public int UnitsPerEm { get; }

    public int GlyphCount { get; }

    public (short XMin, short YMin, short XMax, short YMax) BBox { get; }

    public short HheaAscender { get; }

    public short HheaDescender { get; }

    public short TypoAscender { get; }

    public short TypoDescender { get; }

    public short LineGap { get; }

    public short CapHeight { get; }

    public short XHeight { get; }

    public int WeightClass { get; }

    /// <summary>OS/2 fsType: the font's embedding licence bits (0x2 restricted, 0x4 print, 0x8 editable, 0x100 no subsetting, 0x200 bitmap only).</summary>
    public int FsType { get; }

    public bool IsBold { get; }

    public bool IsItalic { get; }

    public double ItalicAngle { get; }

    internal byte[] RawData => data;

    internal IReadOnlyDictionary<string, (int Offset, int Length)> Tables => tables;

    public static OpenTypeFont Load(byte[] data, int faceIndex = 0, string sourcePath = "")
    {
        ArgumentNullException.ThrowIfNull(data);
        return new OpenTypeFont(data, FaceOffset(data, faceIndex), sourcePath, faceIndex);
    }

    /// <summary>Number of faces in a font file (1 unless it is a collection).</summary>
    public static int FaceCount(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && data[..4].SequenceEqual("ttcf"u8) ? (int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]) : 1;

    /// <summary>Glyph for a Unicode code point, 0 (.notdef) when the font does not cover it.</summary>
    public ushort GlyphId(int codePoint) => cmap.TryGetValue(codePoint, out var glyph) ? glyph : (ushort)0;

    public bool HasGlyph(int codePoint) => cmap.ContainsKey(codePoint);

    /// <summary>Advance width in font units.</summary>
    public int Advance(ushort glyph) => glyph < advances.Length ? advances[glyph] : 0;

    /// <summary>
    /// Pair kerning in font units (GPOS when present, else the kern table). Not cached here: a parsed font is
    /// immutable, so it can be shared between exports and threads (see <see cref="FontFace.Kerning"/>).
    /// </summary>
    public int Kerning(ushort left, ushort right)
    {
        var key = ((uint)left << 16) | right;
        short value = 0;
        if (gposPairs.Count > 0)
        {
            foreach (var subtable in gposPairs)
            {
                if (subtable.TryGet(left, right, out var adjustment))
                {
                    value = adjustment;
                    break;
                }
            }
        }
        else if (kernPairs.TryGetValue(key, out var legacy))
        {
            value = legacy;
        }
        return value;
    }

    /// <summary>
    /// The standard ligature (GSUB <c>liga</c>) that starts <paramref name="glyphs"/>, if any: its glyph and how
    /// many glyphs it replaces.
    /// </summary>
    public bool TryLigature(ReadOnlySpan<ushort> glyphs, out ushort ligature, out int length)
    {
        ligature = 0;
        length = 0;
        if (glyphs.Length < 2 || !ligatures.TryGetValue(glyphs[0], out var candidates))
        {
            return false;
        }
        foreach (var (rest, glyph) in candidates)
        {
            if (rest.Length < glyphs.Length && glyphs.Slice(1, rest.Length).SequenceEqual(rest))
            {
                ligature = glyph;
                length = rest.Length + 1;
                return true;
            }
        }
        return false;
    }

    /// <summary>Every code point the font maps (for ToUnicode and fallback decisions).</summary>
    public IEnumerable<int> CodePoints => cmap.Keys;

    // ---- parsing ------------------------------------------------------------------------------------

    private static int FaceOffset(byte[] data, int faceIndex)
    {
        if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("ttcf"u8))
        {
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
            if (faceIndex < 0 || faceIndex >= count)
            {
                throw new ArgumentOutOfRangeException(nameof(faceIndex));
            }
            return (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12 + (4 * faceIndex)));
        }
        return 0;
    }

    private static Dictionary<string, (int Offset, int Length)> ReadTableDirectory(byte[] data, int offset)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4));
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var record = offset + 12 + (16 * i);
            var tag = Encoding.ASCII.GetString(data, record, 4);
            var tableOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8));
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 12));
            if (tableOffset >= 0 && length >= 0 && (long)tableOffset + length <= data.Length)
            {
                result[tag] = (tableOffset, length);
            }
        }
        foreach (var required in (string[])["head", "hhea", "hmtx", "maxp", "cmap"])
        {
            if (!result.ContainsKey(required))
            {
                throw new InvalidDataException($"Font is missing the '{required}' table.");
            }
        }
        return result;
    }

    private (int Offset, int Length) Table(string tag) => tables[tag];

    private ushort U16((int Offset, int Length) table, int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(table.Offset + at));

    private short S16((int Offset, int Length) table, int at) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(table.Offset + at));

    private ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));

    private short S16(int at) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(at));

    private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));

    private void ReadNames()
    {
        if (!tables.TryGetValue("name", out var name))
        {
            return;
        }
        var count = U16(name.Offset + 2);
        var storage = name.Offset + U16(name.Offset + 4);
        var found = new Dictionary<int, (int Priority, string Value)>();
        for (var i = 0; i < count; i++)
        {
            var record = name.Offset + 6 + (12 * i);
            var platform = U16(record);
            var encoding = U16(record + 2);
            var language = U16(record + 4);
            var id = U16(record + 6);
            var length = U16(record + 8);
            var offset = U16(record + 10);
            if (id is not (1 or 2 or 4 or 6 or 16 or 17) || storage + offset + length > data.Length)
            {
                continue;
            }
            string value;
            int priority;
            if (platform == 3 && encoding is 1 or 10 or 0)
            {
                value = Encoding.BigEndianUnicode.GetString(data, storage + offset, length);
                priority = language == 0x409 ? 3 : 2;
            }
            else if (platform == 0)
            {
                value = Encoding.BigEndianUnicode.GetString(data, storage + offset, length);
                priority = 2;
            }
            else if (platform == 1 && encoding == 0)
            {
                value = Encoding.Latin1.GetString(data, storage + offset, length);
                priority = 1;
            }
            else
            {
                continue;
            }
            if (!found.TryGetValue(id, out var existing) || existing.Priority < priority)
            {
                found[id] = (priority, value.Trim());
            }
        }
        string Get(int id) => found.TryGetValue(id, out var entry) ? entry.Value : string.Empty;
        LegacyFamilyName = Get(1);
        LegacyStyleName = Get(2);
        FullName = Get(4);
        PostScriptName = Get(6);
        FamilyName = Get(16) is { Length: > 0 } typographic ? typographic : LegacyFamilyName;
        StyleName = Get(17) is { Length: > 0 } typographicStyle ? typographicStyle : LegacyStyleName;
        if (PostScriptName.Length == 0)
        {
            PostScriptName = (FamilyName + "-" + StyleName).Replace(" ", string.Empty, StringComparison.Ordinal);
        }
    }

    private void ReadCmap()
    {
        var table = Table("cmap");
        var count = U16(table.Offset + 2);
        int best = -1, bestScore = -1;
        for (var i = 0; i < count; i++)
        {
            var record = table.Offset + 4 + (8 * i);
            var platform = U16(record);
            var encoding = U16(record + 2);
            var offset = table.Offset + (int)U32(record + 4);
            if (offset + 4 > data.Length)
            {
                continue;
            }
            var format = U16(offset);
            var score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 6,
                (0, 4, 12) or (0, 6, 12) => 5,
                (3, 1, 4) => 4,
                (0, _, 4) => 3,
                (3, 0, 4) => 2, // symbol fonts
                (1, 0, 0) => 1,
                _ => -1,
            };
            if (score > bestScore)
            {
                bestScore = score;
                best = offset;
            }
        }
        if (best < 0)
        {
            return;
        }
        switch (U16(best))
        {
            case 4:
                ReadCmap4(best, symbol: bestScore == 2);
                break;
            case 12:
                ReadCmap12(best);
                break;
            case 0:
                for (var code = 0; code < 256; code++)
                {
                    cmap[code] = data[best + 6 + code];
                }
                break;
        }
    }

    private void ReadCmap4(int offset, bool symbol)
    {
        var segments = U16(offset + 6) / 2;
        var ends = offset + 14;
        var starts = ends + (2 * segments) + 2;
        var deltas = starts + (2 * segments);
        var rangeOffsets = deltas + (2 * segments);
        for (var s = 0; s < segments; s++)
        {
            int end = U16(ends + (2 * s)), start = U16(starts + (2 * s));
            var delta = S16(deltas + (2 * s));
            var rangeOffset = U16(rangeOffsets + (2 * s));
            for (var code = start; code <= end && code != 0xFFFF; code++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (code + delta) & 0xFFFF;
                }
                else
                {
                    var at = rangeOffsets + (2 * s) + rangeOffset + (2 * (code - start));
                    if (at + 2 > data.Length)
                    {
                        continue;
                    }
                    glyph = U16(at);
                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }
                }
                if (glyph != 0)
                {
                    // Symbol fonts map U+F0xx; expose them at their Latin-1 positions too.
                    cmap.TryAdd(code, (ushort)glyph);
                    if (symbol && code >= 0xF020 && code <= 0xF0FF)
                    {
                        cmap.TryAdd(code - 0xF000, (ushort)glyph);
                    }
                }
            }
        }
    }

    private void ReadCmap12(int offset)
    {
        var groups = (int)U32(offset + 12);
        for (var g = 0; g < groups; g++)
        {
            var record = offset + 16 + (12 * g);
            if (record + 12 > data.Length)
            {
                break;
            }
            var start = (int)U32(record);
            var end = (int)U32(record + 4);
            var glyph = (int)U32(record + 8);
            for (var code = start; code <= end && code - start < 0x10000; code++)
            {
                var id = glyph + (code - start);
                if (id is > 0 and < 0x10000)
                {
                    cmap.TryAdd(code, (ushort)id);
                }
            }
        }
    }

    private void ReadKern()
    {
        if (!tables.TryGetValue("kern", out var kern) || kern.Length < 4 || U16(kern.Offset) != 0)
        {
            return;
        }
        var count = U16(kern.Offset + 2);
        var at = kern.Offset + 4;
        for (var t = 0; t < count && at + 6 <= kern.Offset + kern.Length; t++)
        {
            var length = U16(at + 2);
            var coverage = U16(at + 4);
            if ((coverage >> 8) == 0 && (coverage & 1) == 1)
            {
                var pairs = U16(at + 6);
                for (var p = 0; p < pairs; p++)
                {
                    var record = at + 14 + (6 * p);
                    if (record + 6 > data.Length)
                    {
                        break;
                    }
                    kernPairs.TryAdd(U32(record), S16(record + 4));
                }
            }
            at += Math.Max((int)length, 6);
        }
    }

    private void ReadGpos()
    {
        if (!tables.TryGetValue("GPOS", out var gpos) || gpos.Length < 10)
        {
            return;
        }
        try
        {
            var baseOffset = gpos.Offset;
            var featureList = baseOffset + U16(baseOffset + 6);
            var lookupList = baseOffset + U16(baseOffset + 8);
            var lookupIndices = new SortedSet<int>();
            var featureCount = U16(featureList);
            for (var f = 0; f < featureCount; f++)
            {
                var record = featureList + 2 + (6 * f);
                if (Encoding.ASCII.GetString(data, record, 4) != "kern")
                {
                    continue;
                }
                var feature = featureList + U16(record + 4);
                var lookups = U16(feature + 2);
                for (var l = 0; l < lookups; l++)
                {
                    lookupIndices.Add(U16(feature + 4 + (2 * l)));
                }
            }
            var lookupCount = U16(lookupList);
            foreach (var index in lookupIndices)
            {
                if (index >= lookupCount)
                {
                    continue;
                }
                var lookup = lookupList + U16(lookupList + 2 + (2 * index));
                var type = U16(lookup);
                var subtableCount = U16(lookup + 4);
                for (var s = 0; s < subtableCount; s++)
                {
                    var subtable = lookup + U16(lookup + 6 + (2 * s));
                    var subtableType = type;
                    if (type == 9)
                    {
                        subtableType = U16(subtable + 2);
                        subtable += (int)U32(subtable + 4);
                    }
                    if (subtableType == 2)
                    {
                        gposPairs.Add(new PairSubtable(this, subtable));
                    }
                }
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // A damaged GPOS table: fall back to the kern table.
            gposPairs.Clear();
        }
    }

    private void ReadGsubLigatures()
    {
        if (!tables.TryGetValue("GSUB", out var gsub) || gsub.Length < 10)
        {
            return;
        }
        try
        {
            var baseOffset = gsub.Offset;
            var featureList = baseOffset + U16(baseOffset + 6);
            var lookupList = baseOffset + U16(baseOffset + 8);
            var lookupIndices = new SortedSet<int>();
            var featureCount = U16(featureList);
            for (var f = 0; f < featureCount; f++)
            {
                var record = featureList + 2 + (6 * f);
                if (Encoding.ASCII.GetString(data, record, 4) != "liga")
                {
                    continue;
                }
                var feature = featureList + U16(record + 4);
                var lookups = U16(feature + 2);
                for (var l = 0; l < lookups; l++)
                {
                    lookupIndices.Add(U16(feature + 4 + (2 * l)));
                }
            }
            var lookupCount = U16(lookupList);
            foreach (var index in lookupIndices)
            {
                if (index >= lookupCount)
                {
                    continue;
                }
                var lookup = lookupList + U16(lookupList + 2 + (2 * index));
                var type = U16(lookup);
                var subtableCount = U16(lookup + 4);
                for (var s = 0; s < subtableCount; s++)
                {
                    var subtable = lookup + U16(lookup + 6 + (2 * s));
                    var subtableType = type;
                    if (type == 7)
                    {
                        // Extension: the real subtable is further away (32-bit offset).
                        subtableType = U16(subtable + 2);
                        subtable += (int)U32(subtable + 4);
                    }
                    if (subtableType == 4 && U16(subtable) == 1)
                    {
                        ReadLigatureSubtable(subtable);
                    }
                }
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // A damaged GSUB table: no ligatures.
            ligatures.Clear();
        }
    }

    /// <summary>GSUB ligature substitution, format 1: per first glyph (coverage), ligatures and their other components.</summary>
    private void ReadLigatureSubtable(int subtable)
    {
        var coverage = PairSubtable.ReadCoverage(this, subtable + U16(subtable + 2));
        var setCount = U16(subtable + 4);
        foreach (var (first, coverageIndex) in coverage.OrderBy(pair => pair.Value))
        {
            if (coverageIndex >= setCount)
            {
                continue;
            }
            var set = subtable + U16(subtable + 6 + (2 * coverageIndex));
            var count = U16(set);
            if (!ligatures.TryGetValue(first, out var list))
            {
                ligatures[first] = list = [];
            }
            for (var i = 0; i < count; i++)
            {
                var ligature = set + U16(set + 2 + (2 * i));
                var components = U16(ligature + 2);
                if (components < 2)
                {
                    continue;
                }
                var rest = new ushort[components - 1];
                for (var c = 0; c < rest.Length; c++)
                {
                    rest[c] = U16(ligature + 4 + (2 * c));
                }
                list.Add((rest, U16(ligature)));
            }
        }
    }

    /// <summary>One GPOS pair adjustment subtable (format 1: glyph pairs, format 2: class pairs).</summary>
    private sealed class PairSubtable
    {
        private readonly OpenTypeFont font;
        private readonly int offset;
        private readonly int format;
        private readonly Dictionary<ushort, int> coverage;
        private readonly int valueFormat1;
        private readonly int valueFormat2;
        private readonly int size1;
        private readonly int size2;
        private readonly int xAdvanceOffset;

        public PairSubtable(OpenTypeFont font, int offset)
        {
            this.font = font;
            this.offset = offset;
            format = font.U16(offset);
            coverage = ReadCoverage(font, offset + font.U16(offset + 2));
            valueFormat1 = font.U16(offset + 4);
            valueFormat2 = font.U16(offset + 6);
            size1 = 2 * int.PopCount(valueFormat1);
            size2 = 2 * int.PopCount(valueFormat2);
            // XAdvance sits after XPlacement and YPlacement when those are present.
            xAdvanceOffset = (valueFormat1 & 4) == 0 ? -1 : 2 * int.PopCount(valueFormat1 & 3);
        }

        public bool TryGet(ushort left, ushort right, out short value)
        {
            value = 0;
            if (!coverage.TryGetValue(left, out var coverageIndex))
            {
                return false;
            }
            if (format == 1)
            {
                var pairSetCount = font.U16(offset + 8);
                if (coverageIndex >= pairSetCount)
                {
                    return false;
                }
                var pairSet = offset + font.U16(offset + 10 + (2 * coverageIndex));
                var count = font.U16(pairSet);
                var recordSize = 2 + size1 + size2;
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    var record = pairSet + 2 + (mid * recordSize);
                    var glyph = font.U16(record);
                    if (glyph == right)
                    {
                        value = xAdvanceOffset < 0 ? (short)0 : font.S16(record + 2 + xAdvanceOffset);
                        return true;
                    }
                    if (glyph < right)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }
                return false;
            }
            if (format == 2)
            {
                var class1 = ReadClass(offset + font.U16(offset + 8), left);
                var class2 = ReadClass(offset + font.U16(offset + 10), right);
                var class1Count = font.U16(offset + 12);
                var class2Count = font.U16(offset + 14);
                if (class1 >= class1Count || class2 >= class2Count)
                {
                    return false;
                }
                var record = offset + 16 + (((class1 * class2Count) + class2) * (size1 + size2));
                value = xAdvanceOffset < 0 ? (short)0 : font.S16(record + xAdvanceOffset);
                return true;
            }
            return false;
        }

        private int ReadClass(int classDef, ushort glyph)
        {
            var classFormat = font.U16(classDef);
            if (classFormat == 1)
            {
                var start = font.U16(classDef + 2);
                var count = font.U16(classDef + 4);
                return glyph >= start && glyph < start + count ? font.U16(classDef + 6 + (2 * (glyph - start))) : 0;
            }
            if (classFormat == 2)
            {
                var ranges = font.U16(classDef + 2);
                int lo = 0, hi = ranges - 1;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    var record = classDef + 4 + (6 * mid);
                    var start = font.U16(record);
                    var end = font.U16(record + 2);
                    if (glyph < start)
                    {
                        hi = mid - 1;
                    }
                    else if (glyph > end)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        return font.U16(record + 4);
                    }
                }
            }
            return 0;
        }

        internal static Dictionary<ushort, int> ReadCoverage(OpenTypeFont font, int at)
        {
            var result = new Dictionary<ushort, int>();
            var coverageFormat = font.U16(at);
            if (coverageFormat == 1)
            {
                var count = font.U16(at + 2);
                for (var i = 0; i < count; i++)
                {
                    result.TryAdd(font.U16(at + 4 + (2 * i)), i);
                }
            }
            else if (coverageFormat == 2)
            {
                var ranges = font.U16(at + 2);
                for (var r = 0; r < ranges; r++)
                {
                    var record = at + 4 + (6 * r);
                    var start = font.U16(record);
                    var end = font.U16(record + 2);
                    var startIndex = font.U16(record + 4);
                    for (int glyph = start; glyph <= end; glyph++)
                    {
                        result.TryAdd((ushort)glyph, startIndex + (glyph - start));
                    }
                }
            }
            return result;
        }
    }
}
