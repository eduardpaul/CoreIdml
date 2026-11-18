using CoreIdml.Dom;

namespace CoreIdml.Tests;

/// <summary>Phone photos stored sideways (EXIF orientation), dashed strokes, and resizing generated frames.</summary>
public sealed class OrientationAndBoundsTests
{
    /// <summary>A JPEG header with an EXIF orientation tag (little-endian TIFF) before SOF0.</summary>
    private static byte[] Jpeg(int width, int height, int orientation)
    {
        var exif = new List<byte>("Exif\0\0"u8.ToArray());
        exif.AddRange("II"u8.ToArray());
        exif.AddRange([0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]);
        exif.AddRange([0x01, 0x00]);
        exif.AddRange([0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00]);
        exif.AddRange([0x00, 0x00, 0x00, 0x00]);
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1, (byte)((exif.Count + 2) >> 8), (byte)(exif.Count + 2) };
        bytes.AddRange(exif);
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03]);
        bytes.AddRange(new byte[9]);
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    [Theory]
    [InlineData(1, 400, 300)]
    [InlineData(3, 400, 300)]
    [InlineData(6, 300, 400)]
    [InlineData(8, 300, 400)]
    public void The_exif_orientation_is_read_and_gives_the_upright_size(int orientation, double width, double height)
    {
        var image = ImageSource.FromBytes(Jpeg(400, 300, orientation), "file:/x.jpg");

        Assert.Equal(orientation, image.Orientation);
        Assert.Equal((width, height), (image.UprightWidthPoints, image.UprightHeightPoints));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void A_placed_photo_is_turned_upright_and_fills_its_frame(int orientation)
    {
        using var doc = IdmlDocument.Create();
        var frame = doc.Pages[0].AddImage(new Rect(100, 100, 150, 200), ImageSource.FromBytes(Jpeg(400, 300, orientation), "file:/x.jpg"));

        // The stored 400×300 image, through its transform, covers the frame exactly (Fill, centred).
        var image = frame.Element.Element("Image")!;
        var transform = Matrix.Parse(image.Attribute("ItemTransform")!.Value);
        var corners = new[] { new Point(0, 0), new Point(400, 0), new Point(0, 300), new Point(400, 300) }.Select(transform.Apply).ToList();
        var bounds = Rect.FromEdges(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
        var expected = orientation >= 5 ? new Size(150, 200) : new Size(266.667, 200); // upright 300×400 fits exactly
        Assert.Equal(expected.Width, bounds.Width, 2);
        Assert.Equal(expected.Height, bounds.Height, 2);
        Assert.Equal(0, bounds.Center.X, 6);   // frame inner coordinates are centred on the frame
        Assert.Equal(0, bounds.Center.Y, 6);
        if (orientation == 6)
        {
            // Camera held upright: the stored top-left pixel ends up top-right.
            var topLeft = transform.Apply(new Point(0, 0));
            Assert.True(topLeft.X > 0 && topLeft.Y < 0);
        }
    }

    [Fact]
    public void Dashed_strokes_use_the_built_in_stroke_style()
    {
        using var doc = IdmlDocument.Create();
        var box = doc.Pages[0].AddRectangle(new Rect(10, 10, 50, 30), new ShapeStyle { Stroke = Swatch.Black, StrokeWeight = 1, StrokeKind = StrokeKind.Dashed });

        Assert.Equal("StrokeStyle/$ID/Canned Dashed 3x2", box.Element.Attribute("StrokeType")?.Value);
    }

    [Fact]
    public void A_text_frame_can_be_moved_and_resized_and_keeps_its_story()
    {
        using var doc = IdmlDocument.Create();
        var frame = doc.Pages[0].AddText(new Rect(10, 10, 100, 20), "Hola");

        frame.SetBounds(new Rect(50, 60, 200, 80));

        Assert.Equal(new Rect(50, 60, 200, 80), frame.Bounds);
        Assert.Equal("Hola", frame.Story.Text.TrimEnd());
        Assert.Throws<IdmlException>(() => doc.Pages[0].AddLine(new Point(0, 0), new Point(10, 10)).SetBounds(new Rect(0, 0, 5, 5)));
    }

    [Fact]
    public void Spanish_can_be_set_for_hyphenation()
    {
        using var doc = IdmlDocument.Create();
        var style = doc.Styles.AddParagraphStyle("Texto", new ParagraphFormat { Language = Languages.Spanish, Hyphenation = true });

        Assert.Equal("$ID/Spanish: Castilian", style.Element.Attribute("AppliedLanguage")?.Value);
        Assert.Contains(doc.DesignMap.Elements("Language"), l => l.Attribute("Name")?.Value == Languages.Spanish);
    }
}
