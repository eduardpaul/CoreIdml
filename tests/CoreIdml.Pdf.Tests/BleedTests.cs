using CoreIdml.Dom;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Exporting with the document's bleed, as InDesign's Use Document Bleed Settings writes it (01-doc-bleed-slug
/// reference-bleed.pdf: a 612 × 792 pt page with 9 pt of bleed is a 630 × 810 pt MediaBox and BleedBox, TrimBox
/// [9 9 621 801]), and the default trim-size export.
/// </summary>
public sealed class BleedTests
{
    private static IdmlDocument Document(bool facing = false, int pages = 1)
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = new Size(612, 792), Bleed = 9, FacingPages = facing, PageCount = pages });
        var red = document.Swatches.AddCmyk("Red", 0, 100, 100, 0);
        // A background running 9 pt off every edge.
        document.Pages[0].AddRectangle(new Rect(-9, -9, 630, 810), new ShapeStyle { Fill = red, Stroke = Swatch.None });
        return document;
    }

    private static double[] Box(PdfDocument pdf, int page, string name) =>
        ((ArrayToken)pdf.GetPage(page).Dictionary.Data[name]).Data.Select(value => ((NumericToken)value).Double).ToArray();

    [Fact]
    public void The_default_export_is_the_trim_size()
    {
        using var document = Document();
        using var pdf = PdfDocument.Open(TestSupport.Export(document));

        Assert.Equal([0, 0, 612, 792], Box(pdf, 1, "MediaBox"));
        Assert.Equal([0, 0, 612, 792], Box(pdf, 1, "BleedBox"));
        Assert.Equal([0, 0, 612, 792], Box(pdf, 1, "TrimBox"));
    }

    [Fact]
    public void With_the_bleed_the_page_grows_by_it_and_items_are_drawn_into_it()
    {
        using var document = Document();
        var options = TestSupport.ExportOptions();
        options.IncludeBleed = true;
        var bytes = TestSupport.Export(document, options);
        using var pdf = PdfDocument.Open(bytes);

        Assert.Equal([0, 0, 630, 810], Box(pdf, 1, "MediaBox"));
        Assert.Equal([0, 0, 630, 810], Box(pdf, 1, "BleedBox"));
        Assert.Equal([9, 9, 621, 801], Box(pdf, 1, "TrimBox"));
        // 72 dpi: one pixel per point. The background reaches the corner of the bleed.
        var image = Rasterizer.Render(bytes, 0, 72);
        Assert.Equal(630, image.Width);
        var corner = image.Pixels[((2 * image.Width) + 2) * image.Channels];
        var green = image.Pixels[(((2 * image.Width) + 2) * image.Channels) + 1];
        Assert.True(corner > 200 && green < 100, $"corner {corner},{green}");
    }

    [Fact]
    public void A_left_page_has_its_inside_bleed_on_the_right()
    {
        using var document = Document(facing: true, pages: 2);
        var preference = document.Package.TryGetXml("Resources/Preferences.xml")!.Root!.Element("DocumentPreference")!;
        preference.SetAttributeValue("DocumentBleedInsideOrLeftOffset", 3);
        preference.SetAttributeValue("DocumentBleedOutsideOrRightOffset", 9);
        var options = TestSupport.ExportOptions();
        options.IncludeBleed = true;
        using var pdf = PdfDocument.Open(TestSupport.Export(document, options));

        // Page 1 is a right page (inside on the left), page 2 a left page (inside on the right).
        Assert.Equal([3, 9, 615, 801], Box(pdf, 1, "TrimBox"));
        Assert.Equal([9, 9, 621, 801], Box(pdf, 2, "TrimBox"));
        Assert.Equal([0, 0, 624, 810], Box(pdf, 2, "MediaBox"));
    }
}
