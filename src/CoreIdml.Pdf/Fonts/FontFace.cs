namespace CoreIdml.Pdf.Fonts;

/// <summary>
/// A font the composer can measure and the writer can emit: an OpenType face from a file, or one of the
/// non-embedded standard-14 fonts. Glyph ids of standard fonts are WinAnsi codes.
/// </summary>
internal sealed class FontFace
{
    private FontFace(string key, OpenTypeFont? openType, string? standardName, StandardFontMetrics? standard)
    {
        Key = key;
        OpenType = openType;
        StandardName = standardName;
        Standard = standard;
    }

    /// <summary>Unique key (file path and face index, or the standard font name).</summary>
    public string Key { get; }

    public OpenTypeFont? OpenType { get; }

    public string? StandardName { get; }

    public StandardFontMetrics? Standard { get; }

    public bool IsStandard => Standard is not null;

    /// <summary>The licence (OS/2 fsType) allows embedding in a PDF (installable, preview and print, or editable).</summary>
    public bool EmbeddingAllowed => OpenType is not { } font || ((font.FsType & 0x000F) != 0x0002 && (font.FsType & 0x0200) == 0);

    /// <summary>The licence forbids subsetting: the whole font is embedded.</summary>
    public bool SubsettingForbidden => OpenType is { } font && (font.FsType & 0x0100) != 0;

    /// <summary>"Family Style" as found (for reports).</summary>
    public string DisplayName => OpenType is { } font ? (font.FamilyName + " " + font.StyleName).Trim() : StandardName!;

    public string PostScriptName => OpenType?.PostScriptName ?? StandardName!;

    /// <summary>Ascent in em (positive), used for the first baseline ("Ascent" offset).</summary>
    public double Ascent => OpenType is { } font ? font.TypoAscender / (double)font.UnitsPerEm : Standard!.Ascent / 1000.0;

    /// <summary>Descent in em (positive below the baseline).</summary>
    public double Descent => OpenType is { } font ? -font.TypoDescender / (double)font.UnitsPerEm : -Standard!.Descent / 1000.0;

    public double CapHeight => OpenType is { } font ? font.CapHeight / (double)font.UnitsPerEm : Standard!.CapHeight / 1000.0;

    public double XHeight => OpenType is { } font ? font.XHeight / (double)font.UnitsPerEm : Standard!.CapHeight * 0.72 / 1000.0;

    public static FontFace FromOpenType(OpenTypeFont font) => new($"{font.SourcePath}#{font.FaceIndex}", font, null, null);

    public static FontFace FromStandard(string name) => new(name, null, name, StandardFontMetrics.All[name]);

    /// <summary>Maps a code point to a glyph (a WinAnsi code for standard fonts).</summary>
    public bool TryMap(int codePoint, out ushort glyph)
    {
        if (OpenType is { } font)
        {
            glyph = font.GlyphId(codePoint);
            return glyph != 0;
        }
        glyph = 0;
        if (WinAnsiCode(codePoint) is { } code)
        {
            glyph = code;
            return true;
        }
        return false;
    }

    /// <summary>Advance width in em.</summary>
    public double Advance(ushort glyph) => OpenType is { } font
        ? font.Advance(glyph) / (double)font.UnitsPerEm
        : Standard!.Width((byte)glyph) / 1000.0;

    /// <summary>Left side bearing in em: where the glyph's outline starts right of its origin (0 for standard fonts).</summary>
    public double LeftSideBearing(ushort glyph) => OpenType is { } font ? font.LeftSideBearing(glyph) / (double)font.UnitsPerEm : 0;

    /// <summary>The standard ligature starting <paramref name="glyphs"/> (none for standard fonts).</summary>
    public bool TryLigature(ReadOnlySpan<ushort> glyphs, out ushort ligature, out int length)
    {
        ligature = 0;
        length = 0;
        return OpenType is { } font && font.TryLigature(glyphs, out ligature, out length);
    }

    /// <summary>Kerning between two glyphs in em (0 for standard fonts).</summary>
    public double Kerning(ushort left, ushort right)
    {
        if (OpenType is not { } font)
        {
            return 0;
        }
        // GPOS lookups cost a few binary searches per pair; text repeats the same pairs.
        var key = ((uint)left << 16) | right;
        if (!kerning.TryGetValue(key, out var units))
        {
            units = font.Kerning(left, right);
            kerning[key] = units;
        }
        return units / (double)font.UnitsPerEm;
    }

    // Per face, and a face belongs to one FontLibrary (one export or measurer), so no locking.
    private readonly Dictionary<uint, int> kerning = [];

    public override string ToString() => DisplayName;

    /// <summary>The Windows-1252 (WinAnsiEncoding) code of a character, or null when it has none.</summary>
    internal static byte? WinAnsiCode(int codePoint)
    {
        if (codePoint is (>= 0x20 and < 0x80) or (>= 0xA0 and <= 0xFF))
        {
            return (byte)codePoint;
        }
        var index = Array.IndexOf(High, (char)codePoint);
        return codePoint is > 0 and <= 0xFFFF && index >= 0 ? (byte)(0x80 + index) : null;
    }

    /// <summary>Characters of WinAnsi codes 0x80-0x9F (0 where undefined).</summary>
    private static readonly char[] High =
    [
        '\u20AC', '\0', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021', '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\0', '\u017D', '\0',
        '\0', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014', '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\0', '\u017E', '\u0178',
    ];
}
