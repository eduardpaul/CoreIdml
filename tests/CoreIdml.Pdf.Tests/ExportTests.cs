using CoreIdml.Dom;
using CoreIdml.Pdf.Imaging;
using CoreIdml.Samples;
using UglyToad.PdfPig;

namespace CoreIdml.Pdf.Tests;

public sealed class ExportTests(ITestOutputHelper output)
{
    [Fact]
    public void Ficha_demo_exports_two_A4_pages_with_its_text()
    {
        var directory = TestSupport.OutputDirectory("ficha-export");
        var photo = TestSupport.WriteTestPng(Path.Combine(directory, "foto.png"), 320, 200);
        using var document = FichaDemo.Build(photo);
        using var stream = new MemoryStream();
        var result = IdmlPdf.Export(document, stream, TestSupport.ExportOptions());
        var pdf = stream.ToArray();
        File.WriteAllBytes(Path.Combine(directory, "ficha-demo.pdf"), pdf);
        for (var page = 0; page < 2; page++)
        {
            var raster = Rasterizer.Render(pdf, page, 72);
            File.WriteAllBytes(Path.Combine(directory, $"page-{page + 1}.png"), PngCodec.Encode(raster));
        }
        foreach (var substitution in result.FontSubstitutions)
        {
            output.WriteLine($"font: {substitution.RequestedFamily} {substitution.RequestedStyle} -> {substitution.UsedFont} ({substitution.Reason})");
        }
        output.WriteLine($"overset: {string.Join(", ", result.OversetStories)}; missing images: {string.Join(", ", result.MissingImages)}");

        Assert.Equal(2, result.PageCount);
        Assert.Empty(result.MissingImages);
        using var reader = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal(2, reader.NumberOfPages);
        var first = reader.GetPage(1);
        Assert.Equal(595.3, first.Width, 0.5);
        Assert.Equal(841.9, first.Height, 0.5);
        var text = string.Join(" ", first.GetWords().Select(w => w.Text));
        Assert.Contains("Ficha", text, StringComparison.Ordinal);
        Assert.Contains("F-001", text, StringComparison.Ordinal);
        Assert.Contains("Edificio", text, StringComparison.Ordinal);
        Assert.Single(first.GetImages());
        var second = string.Join(" ", reader.GetPage(2).GetWords().Select(w => w.Text));
        Assert.Contains("PÁGINA 2", second, StringComparison.Ordinal);

    }

    [Fact]
    public void Vertical_scale_stretches_glyphs_up_from_the_baseline()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Sans", PointSize = 20, Leading = 24 });
        document.Pages[0].AddText(new Rect(100, 100, 200, 50), "H", style);
        var tall = document.Pages[0].AddText(new Rect(100, 200, 200, 50), "H", style);
        tall.Story.Element.Descendants("CharacterStyleRange").Single().SetAttributeValue("VerticalScale", "150");

        using var reader = PdfDocument.Open(TestSupport.Export(document));
        var letters = reader.GetPage(1).Letters.Where(l => l.Value == "H").OrderByDescending(l => l.BoundingBox.Bottom).ToList();
        Assert.Equal(2, letters.Count);
        var (normal, scaled) = (letters[0].BoundingBox, letters[1].BoundingBox);
        Assert.Equal(normal.Height * 1.5, scaled.Height, 1);
        Assert.Equal(normal.Width, scaled.Width, 1);
        // Same baseline position within each frame: the stretch goes up.
        Assert.Equal(normal.Bottom - 100, scaled.Bottom, 1);
    }
}
