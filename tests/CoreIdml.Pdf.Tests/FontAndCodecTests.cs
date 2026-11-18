using CoreIdml.Pdf.Fonts;
using CoreIdml.Pdf.Imaging;

namespace CoreIdml.Pdf.Tests;

public sealed class FontAndCodecTests
{
    private static OpenTypeFont Serif() =>
        OpenTypeFont.Load(File.ReadAllBytes(Path.Combine(TestSupport.FontDirectory, "LiberationSerif-Regular.ttf")));

    [Fact]
    public void OpenType_parser_reads_names_metrics_and_cmap()
    {
        var font = Serif();
        Assert.Equal("Liberation Serif", font.FamilyName);
        Assert.Equal("Regular", font.StyleName);
        Assert.Equal(2048, font.UnitsPerEm);
        Assert.False(font.IsCff);
        var a = font.GlyphId('A');
        Assert.NotEqual(0, a);
        // Liberation Serif is metric-compatible with Times New Roman: 'A' is 722/1000 em.
        Assert.Equal(722, Math.Round(font.Advance(a) * 1000.0 / font.UnitsPerEm));
        Assert.Equal(0, font.GlyphId(0x4E2D)); // no CJK
    }

    [Fact]
    public void Subset_keeps_glyph_ids_and_is_a_valid_smaller_font()
    {
        var font = Serif();
        var used = "Hola señor".Select(c => font.GlyphId(c)).ToList();
        var subset = FontSubsetter.Subset(font, used);
        Assert.True(subset.Length < font.RawData.Length / 4, $"subset is {subset.Length} bytes");
        var reloaded = OpenTypeFont.Load(subset);
        Assert.Equal(font.GlyphCount, reloaded.GlyphCount);
        Assert.Equal(font.GlyphId('ñ'), reloaded.GlyphId('ñ'));
        Assert.Equal(font.Advance(font.GlyphId('H')), reloaded.Advance(reloaded.GlyphId('H')));
    }

    [Fact]
    public void Font_library_matches_family_and_style_names()
    {
        var library = TestSupport.FontLibrary();
        Assert.Equal("Liberation Serif Bold", library.Resolve("Liberation Serif", "Bold").DisplayName);
        Assert.Equal("Liberation Serif Italic", library.Resolve("liberation serif", "Italic").DisplayName);
        Assert.Equal("Liberation Sans Regular", library.Resolve("LiberationSans-Regular", "Regular").DisplayName);
        Assert.True(library.Substitutions.Count == 0, string.Join("; ", library.Substitutions));
        // Not installed: the configured substitution, reported once.
        Assert.Equal("Liberation Serif Bold", library.Resolve("Minion Pro", "Bold").DisplayName);
        Assert.Single(library.Substitutions);
    }

    [Theory]
    [InlineData("Raleway", "Light", "Liberation Sans Regular")]
    [InlineData("Raleway (TT)", "SemiBold", "Liberation Sans Bold")]
    [InlineData("Poppins", "Regular", "Liberation Sans Regular")]
    [InlineData("Unheard Of Display", "Regular", "Liberation Serif Regular")]
    public void Missing_fonts_fall_back_to_a_face_of_the_same_kind(string family, string style, string expected)
    {
        Assert.Equal(expected, TestSupport.FontLibrary().Resolve(family, style).DisplayName);
    }

    [Fact]
    public void Standard_font_metrics_match_the_PDF_core_fonts()
    {
        var helvetica = FontFace.FromStandard("Helvetica");
        Assert.True(helvetica.TryMap('A', out var a));
        Assert.Equal(0.667, helvetica.Advance(a), 3);
        Assert.True(helvetica.TryMap('€', out var euro));
        Assert.Equal(0x80, euro);
        Assert.False(helvetica.TryMap('中', out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Png_round_trips_through_the_codec(int channels)
    {
        var pixels = new byte[7 * 5 * channels];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 37) % 256);
        }
        var image = new RasterImage(7, 5, channels, pixels);
        var decoded = PngCodec.Decode(PngCodec.Encode(image));
        Assert.Equal(image.Width, decoded.Width);
        Assert.Equal(image.Height, decoded.Height);
        Assert.Equal(channels, decoded.Channels);
        Assert.Equal(pixels, decoded.Pixels);
    }
}
