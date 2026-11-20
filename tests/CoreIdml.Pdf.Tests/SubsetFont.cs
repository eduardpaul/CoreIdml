using System.Buffers.Binary;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Reads an embedded TrueType subset the way a PDF renderer does: by glyph id. A subset has no <c>cmap</c> (glyphs are
/// addressed by id), so <see cref="Fonts.OpenTypeFont"/>, which needs one, cannot load it.
/// </summary>
internal sealed class SubsetFont
{
    private readonly byte[] data;

    public SubsetFont(byte[] data)
    {
        this.data = data;
        for (var t = 0; t < U16(4); t++)
        {
            var record = 12 + (16 * t);
            Tables[System.Text.Encoding.ASCII.GetString(data, record, 4)] = ((int)U32(record + 8), (int)U32(record + 12));
        }
    }

    public Dictionary<string, (int Offset, int Length)> Tables { get; } = [];

    public int GlyphCount => U16(Tables["maxp"].Offset + 4);

    /// <summary>Advance width in font units (hmtx).</summary>
    public int Advance(int glyph)
    {
        var metrics = U16(Tables["hhea"].Offset + 34);
        return U16(Tables["hmtx"].Offset + (4 * Math.Min(glyph, metrics - 1)));
    }

    /// <summary>Length of the glyph's outline in glyf (0: emptied).</summary>
    public int OutlineLength(int glyph) => Loca(glyph + 1) - Loca(glyph);

    private int Loca(int glyph) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(Tables["head"].Offset + 50)) == 1
        ? (int)U32(Tables["loca"].Offset + (4 * glyph))
        : U16(Tables["loca"].Offset + (2 * glyph)) * 2;

    private int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));

    private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
}
