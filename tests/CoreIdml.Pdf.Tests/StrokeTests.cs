using CoreIdml.Dom;
using CoreIdml.Pdf.Imaging;

namespace CoreIdml.Pdf.Tests;

/// <summary>Stroke styles of page items: dash lengths of the canned and custom dashed styles.</summary>
public sealed class StrokeTests
{
    [Theory]
    // Custom "Dashed" with its lengths as an attribute, as InDesign writes it: 2 on, 6 off -> 10 dashes in 80 pt.
    [InlineData("StrokeStyle/$ID/Dashed", "2 6", 10)]
    // Canned 4x4 scales with the 2 pt weight: 8 on, 8 off -> 5 dashes (3x2 would give 8).
    [InlineData("StrokeStyle/$ID/Canned Dashed 4x4", null, 5)]
    public void Dashed_strokes_use_the_stored_dash_lengths(string strokeType, string? dashAndGap, int expectedDashes)
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var rectangle = document.Pages[0].AddRectangle(new Rect(100, 100, 80, 40), new ShapeStyle { Fill = Swatch.None, Stroke = Swatch.Black, StrokeWeight = 2 });
        rectangle.Element.SetAttributeValue("StrokeType", strokeType);
        rectangle.Element.SetAttributeValue("StrokeDashAndGap", dashAndGap);

        // 72 dpi: one pixel per point. Count the dashes along the top edge, away from the corners.
        var page = Rasterizer.Render(TestSupport.Export(document), 0, 72);
        Assert.Equal(expectedDashes, DarkRuns(page, 100, 101, 180));
    }

    private static int DarkRuns(RasterImage image, int y, int fromX, int toX)
    {
        var runs = 0;
        var inside = false;
        for (var x = fromX; x < toX; x++)
        {
            var i = ((y * image.Width) + x) * image.Channels;
            var dark = image.Pixels[i] < 160;
            if (dark && !inside)
            {
                runs++;
            }
            inside = dark;
        }
        return runs;
    }
}
