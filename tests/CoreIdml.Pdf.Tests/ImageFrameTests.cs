using CoreIdml.Pdf.Imaging;
using CoreIdml.Dom;

namespace CoreIdml.Pdf.Tests;

/// <summary>Image frames are drawn without a border, as InDesign places images.</summary>
public sealed class ImageFrameTests
{
    [Fact]
    public void Placed_images_have_no_border_unless_one_is_applied()
    {
        var directory = TestSupport.OutputDirectory(nameof(ImageFrameTests));
        var white = Path.Combine(directory, "blanca.png");
        File.WriteAllBytes(white, PngCodec.Encode(new RasterImage(200, 100, 3, Enumerable.Repeat((byte)255, 200 * 100 * 3).ToArray())));

        using var doc = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var plain = doc.Pages[0].AddImage(new Rect(100, 100, 200, 100), white, ImageFit.Fill);
        var bordered = doc.Pages[0].AddImage(new Rect(100, 300, 200, 100), white, ImageFit.Fill);
        bordered.ApplyStyle(new ShapeStyle { Stroke = Swatch.Black, StrokeWeight = 2 });

        Assert.Equal(Swatch.None.Reference, plain.GetAttribute("StrokeColor"));

        // 72 dpi: one pixel per point. Look along the top edge of each frame.
        var page = Rasterizer.Render(TestSupport.Export(doc), 0, 72);
        Assert.Equal(0, DarkPixelsOnRow(page, 100, 110, 290));
        Assert.True(DarkPixelsOnRow(page, 300, 110, 290) > 150, "the applied border is drawn");
    }

    private static int DarkPixelsOnRow(RasterImage image, int y, int fromX, int toX)
    {
        var dark = 0;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var x = fromX; x < toX; x++)
            {
                var i = (((y + dy) * image.Width) + x) * image.Channels;
                if (image.Pixels[i] < 128 && image.Pixels[i + 1] < 128 && image.Pixels[i + 2] < 128)
                {
                    dark++;
                }
            }
        }
        return dark;
    }
}
