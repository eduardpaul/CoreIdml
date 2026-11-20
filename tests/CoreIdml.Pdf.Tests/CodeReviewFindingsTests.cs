using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CoreIdml.Dom;
using CoreIdml.Pdf.Fonts;
using CoreIdml.Pdf.Imaging;

namespace CoreIdml.Pdf.Tests;

/// <summary>Regressions for the defects found by the code review of the PDF export.</summary>
public sealed class CodeReviewFindingsTests
{
    /// <summary>A PNG with the given header fields and raw (filtered) scanlines; <paramref name="headerLength"/> truncates IHDR.</summary>
    private static byte[] Png(int width, int height, byte bitDepth, byte colorType, byte[] scanlines, byte[]? transparency = null, int headerLength = 13, byte interlace = 0)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = bitDepth;
        header[9] = colorType;
        header[12] = interlace;
        TestFiles.WriteChunk(output, "IHDR", header[..headerLength]);
        if (transparency is not null)
        {
            TestFiles.WriteChunk(output, "tRNS", transparency);
        }
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(scanlines);
        }
        TestFiles.WriteChunk(output, "IDAT", raw.ToArray());
        TestFiles.WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>One 2x1 RGB row, 8 bits per sample (filter byte first).</summary>
    private static readonly byte[] RgbRow = [0, 10, 20, 30, 40, 50, 60];

    [Fact]
    public void Png_decoder_reports_damaged_headers_as_invalid_data()
    {
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(Png(2, 1, 8, 2, RgbRow, headerLength: 8)));
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(Png(2, 1, 0, 0, RgbRow)));
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(Png(2, 1, 3, 2, RgbRow)));
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(Png(2, 1, 4, 2, RgbRow)));
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(Png(2, 1, 16, 3, RgbRow)));
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(Png(2, 1, 8, 2, RgbRow, interlace: 2)));
        Assert.Equal(2, PngCodec.Decode(Png(2, 1, 8, 2, RgbRow)).Width);
    }

    [Fact]
    public void A_16_bit_colour_key_matches_the_full_samples()
    {
        // Gray 16-bit: 0x1234 (the key) and 0x12FF (same high byte, not the key).
        var image = PngCodec.Decode(Png(2, 1, 16, 0, [0, 0x12, 0x34, 0x12, 0xFF], transparency: [0x12, 0x34]));

        Assert.Equal(2, image.Channels);
        Assert.Equal([0x12, 0, 0x12, 255], image.Pixels);

        // RGB 16-bit, interlaced: only the exact key is transparent.
        var rows = new List<byte>();
        // Adam7 passes of a 2x1 image: pass 1 holds pixel 0, pass 6 holds pixel 1.
        rows.AddRange([0, 0x12, 0x34, 0, 1, 0, 2]);
        rows.AddRange([0, 0x12, 0x34, 0, 1, 0, 3]);
        var rgb = PngCodec.Decode(Png(2, 1, 16, 2, [.. rows], transparency: [0x12, 0x34, 0, 1, 0, 2], interlace: 1));
        Assert.Equal(4, rgb.Channels);
        Assert.Equal(0, rgb.Pixels[3]);
        Assert.Equal(255, rgb.Pixels[7]);
    }

    /// <summary>A one-page document showing the image file at <paramref name="path"/> (written valid, then replaced by <paramref name="damaged"/>).</summary>
    private static IdmlDocument WithImage(string path, byte[] valid, byte[] damaged)
    {
        File.WriteAllBytes(path, valid);
        var document = IdmlDocument.Create();
        document.Pages[0].AddImage(new Rect(36, 36, 100, 100), path);
        File.WriteAllBytes(path, damaged);
        return document;
    }

    private static int FreeEntries(byte[] pdf) =>
        Encoding.ASCII.GetString(pdf).Split('\n').Count(line => line.StartsWith("0000000000 65535 f", StringComparison.Ordinal));

    [Theory]
    [InlineData(8, 2, 8)]  // IHDR cut short
    [InlineData(13, 0, 0)] // bit depth 0
    [InlineData(13, 2, 3)] // bit depth 3 for RGB
    public void A_damaged_png_is_reported_missing_and_leaves_no_object_behind(int headerLength, byte colorType, byte bitDepth)
    {
        var path = TestFiles.TempPath(".png");
        using var document = WithImage(path, TestFiles.Png(10, 10), Png(2, 1, bitDepth, colorType, RgbRow, headerLength: headerLength));
        using var stream = new MemoryStream();

        var result = IdmlPdf.Export(document, stream, TestSupport.ExportOptions());

        Assert.Single(result.MissingImages);
        Assert.Equal(1, FreeEntries(stream.ToArray()));
    }

    [Fact]
    public void A_12_bit_jpeg_is_reported_missing_instead_of_embedded()
    {
        var path = TestFiles.TempPath(".jpg");
        var twelveBit = TestFiles.JpegHeader(10, 10, 72);
        var sof = twelveBit.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0xC0]);
        twelveBit[sof + 4] = 12;
        using var document = WithImage(path, TestFiles.JpegHeader(10, 10, 72), twelveBit);
        using var stream = new MemoryStream();

        var result = IdmlPdf.Export(document, stream, TestSupport.ExportOptions());

        Assert.Single(result.MissingImages);
        var pdf = stream.ToArray();
        Assert.DoesNotContain("/BitsPerComponent 12", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
        Assert.Equal(1, FreeEntries(pdf));
    }

    [Fact]
    public void Relative_links_resolve_against_the_document_or_the_pdf_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "coreidml-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        var image = Path.Combine(folder, "images", "a.png");
        File.WriteAllBytes(image, TestFiles.Png(10, 10));
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddImage(new Rect(36, 36, 100, 100), image);
        frame.Element.Descendants("Link").Single().SetAttributeValue("LinkResourceURI", "images/a.png");

        var options = TestSupport.ExportOptions();
        options.DocumentPath = Path.Combine(folder, "doc.idml");
        using var stream = new MemoryStream();
        var byDocument = IdmlPdf.Export(document, stream, options);
        var byPdf = IdmlPdf.Export(document, Path.Combine(folder, "doc.pdf"), TestSupport.ExportOptions());

        Assert.Empty(byDocument.MissingImages);
        Assert.Empty(byPdf.MissingImages);
    }

    [Fact]
    public void Subsetting_skips_composite_components_the_font_does_not_have()
    {
        var original = OpenTypeFont.Load(File.ReadAllBytes(Path.Combine(TestSupport.FontDirectory, "LiberationSerif-Regular.ttf")));
        var data = original.RawData.ToArray();
        var tables = original.Tables;
        var longLoca = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(tables["head"].Offset + 50)) == 1;
        int Offset(int glyph) => longLoca
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(tables["loca"].Offset + (4 * glyph)))
            : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(tables["loca"].Offset + (2 * glyph))) * 2;
        // 'ñ' is a composite (n + tilde): point its first component at a glyph id past the end of the font.
        var composite = original.GlyphId('ñ');
        var at = tables["glyf"].Offset + Offset(composite);
        Assert.True(BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(at)) < 0, "ñ should be a composite glyph");
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at + 12), 0xFFFE);
        var damaged = OpenTypeFont.Load(data);

        var subset = FontSubsetter.Subset(damaged, [composite, damaged.GlyphId('a')]);

        Assert.Equal(damaged.GlyphCount, new SubsetFont(subset).GlyphCount);
    }
}
