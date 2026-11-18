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
}
