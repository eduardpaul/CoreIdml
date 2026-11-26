using CoreIdml.Dom;
using CoreIdml.Json;
using CoreIdml.Pdf.Imaging;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// What a spec draws to reproduce annotated sheets: rotated image frames (a logo along a page-edge banner), dashed
/// vector shapes and arrows over a photo, and chemical formulas with superscript and subscript runs.
/// </summary>
public sealed class AnnotationTests
{
    /// <summary>A PNG whose left half is red and right half blue.</summary>
    private static string WriteHalves(string name)
    {
        var directory = TestSupport.OutputDirectory(nameof(AnnotationTests) + "_" + name);
        var path = Path.Combine(directory, name + ".png");
        const int width = 64, height = 32;
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                (pixels[i], pixels[i + 1], pixels[i + 2]) = x < width / 2 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255);
            }
        }
        File.WriteAllBytes(path, PngCodec.Encode(new RasterImage(width, height, 3, pixels)));
        return path;
    }

    private static (byte R, byte G, byte B) Pixel(RasterImage image, int x, int y)
    {
        var i = ((y * image.Width) + x) * image.Channels;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }

    private static bool IsRed((byte R, byte G, byte B) p) => p.R > 200 && p.G < 60 && p.B < 60;

    private static bool IsBlue((byte R, byte G, byte B) p) => p.B > 200 && p.R < 60 && p.G < 60;

    private static bool IsWhite((byte R, byte G, byte B) p) => p is { R: > 240, G: > 240, B: > 240 };

    [Theory]
    // The 100 x 50 pt frame at (100, 100) turns about its centre (150, 125). Counter-clockwise, as InDesign: its left
    // (red) half goes to the bottom; clockwise to the top; half a turn to the right.
    [InlineData(90, "bottom")]
    [InlineData(-90, "top")]
    [InlineData(180, "right")]
    public void Rotated_image_frames_turn_their_image_with_them(double rotation, string redSide)
    {
        var image = WriteHalves("halves" + (int)rotation);
        using var document = IdmlJson.CreateDocument($$"""
        { "pages": [ { "items": [ { "type": "image", "name": "logo", "x": 100, "y": 100, "width": 100, "height": 50,
          "rotation": {{rotation}}, "path": "{{image.Replace("\\", "\\\\", StringComparison.Ordinal)}}", "fit": "fill" } ] } ] }
        """);

        var frame = (GraphicFrame)document.FindPageItem("logo")!;
        var bounds = frame.Bounds;
        if (rotation == 180)
        {
            Assert.Equal(new Rect(100, 100, 100, 50), Round(bounds));
        }
        else
        {
            Assert.Equal(new Rect(125, 75, 50, 100), Round(bounds));
        }
        Assert.True(IdmlValidator.Validate(document).IsValid);

        // 72 dpi: one pixel per point.
        var page = Rasterizer.Render(TestSupport.Export(document), 0, 72);
        var (red, blue) = redSide switch
        {
            "bottom" => ((150, 150), (150, 100)),
            "top" => ((150, 100), (150, 150)),
            _ => ((175, 125), (125, 125)),
        };
        Assert.True(IsRed(Pixel(page, red.Item1, red.Item2)), $"red at {red}: {Pixel(page, red.Item1, red.Item2)}");
        Assert.True(IsBlue(Pixel(page, blue.Item1, blue.Item2)), $"blue at {blue}: {Pixel(page, blue.Item1, blue.Item2)}");
        // Nothing outside the rotated frame: the unrotated frame's corner stays blank.
        if (rotation != 180)
        {
            Assert.True(IsWhite(Pixel(page, 105, 105)));
        }
    }

    /// <summary>Dark runs along a row: dashes crossed.</summary>
    private static int DarkRuns(RasterImage image, int y, int fromX, int toX, Func<(byte R, byte G, byte B), bool> dark)
    {
        var runs = 0;
        var inside = false;
        for (var x = fromX; x < toX; x++)
        {
            var on = dark(Pixel(image, x, y));
            if (on && !inside)
            {
                runs++;
            }
            inside = on;
        }
        return runs;
    }

    private static bool IsInk((byte R, byte G, byte B) p) => p.R > 150 && p.G < 120 && p.B < 120;

    [Fact]
    public void Dashed_rectangles_ellipses_and_lines_are_drawn_with_their_dash_lengths_in_points()
    {
        using var document = IdmlJson.CreateDocument("""
        { "colors": [ { "name": "Red", "hex": "#FF0000" } ], "pages": [ { "items": [
          { "type": "rectangle", "x": 100, "y": 100, "width": 80, "height": 40, "stroke": "Red", "strokeWeight": 2, "strokeDash": [6, 4] },
          { "type": "line", "x": 100, "y": 200, "x2": 180, "y2": 200, "stroke": "Red", "strokeWeight": 2, "strokeDash": [2, 6] },
          { "type": "line", "x": 100, "y": 220, "x2": 180, "y2": 220, "stroke": "Red", "strokeWeight": 2 },
          { "type": "ellipse", "x": 300, "y": 100, "width": 100, "height": 100, "stroke": "Red", "strokeWeight": 2, "strokeDash": [5, 5] }
        ] } ] }
        """);

        var page = Rasterizer.Render(TestSupport.Export(document), 0, 72);
        // 6 on, 4 off from the top-left corner: dashes start at 100, 110, ... 170 on the top edge (8 in 80 pt; the
        // corners, where the sides' strokes cross the row, left out).
        Assert.Equal(8, DarkRuns(page, 100, 102, 178, IsInk));
        // 2 on, 6 off: 10 dashes in 80 pt.
        Assert.Equal(10, DarkRuns(page, 200, 100, 180, IsInk));
        // No strokeDash: solid, as before.
        Assert.Equal(1, DarkRuns(page, 220, 100, 180, IsInk));
        // The circle's dashes: 314 pt round, 10 pt a period, so about 31 dashes; count them along a ring of pixels.
        // A dash counts after a gap of at least 2 degrees (1.7 pt of the 5 pt gaps), so edge pixels do not flicker.
        var dashes = 0;
        var off = 3600;
        for (var step = 0; step < 3600; step++)
        {
            var angle = step * Math.PI / 1800;
            var on = IsInk(Pixel(page, (int)Math.Floor(350 + (50 * Math.Cos(angle))), (int)Math.Floor(150 + (50 * Math.Sin(angle)))));
            if (on && off >= 20)
            {
                dashes++;
            }
            off = on ? 0 : off + 1;
        }
        Assert.InRange(dashes, 29, 33);
    }

    [Fact]
    public void Arrowheads_are_drawn_beyond_the_line_ends()
    {
        using var document = IdmlJson.CreateDocument("""
        { "colors": [ { "name": "Red", "hex": "#FF0000" } ], "pages": [ { "items": [
          { "type": "line", "x": 100, "y": 100, "x2": 200, "y2": 100, "stroke": "Red", "strokeWeight": 2, "endArrow": "triangle", "strokeDash": [3, 3] },
          { "type": "line", "x": 300, "y": 300, "x2": 300, "y2": 200, "stroke": "Red", "strokeWeight": 2, "startArrow": "circleSolid", "endArrow": "simple" },
          { "type": "line", "x": 100, "y": 300, "x2": 200, "y2": 300, "stroke": "Red", "strokeWeight": 2 }
        ] } ] }
        """);

        var page = Rasterizer.Render(TestSupport.Export(document), 0, 72);
        // The triangle, 10 pt long and 8 pt wide at 2 pt, its tip 9 pt beyond the end, solid on a dashed line.
        Assert.True(IsInk(Pixel(page, 205, 100)), "the triangle beyond the end");
        Assert.True(IsInk(Pixel(page, 200, 102)) && IsInk(Pixel(page, 200, 97)), "the triangle is wider than the line");
        Assert.False(IsInk(Pixel(page, 211, 100)), "the tip ends 9 pt beyond");
        Assert.False(IsInk(Pixel(page, 190, 103)), "the line itself stays 2 pt wide");
        // Upwards: a filled circle on the start (300, 300) and an open V on the end (300, 200).
        Assert.True(IsInk(Pixel(page, 297, 300)) && IsInk(Pixel(page, 303, 300)) && IsInk(Pixel(page, 300, 303)), "the circle round the start");
        Assert.True(IsInk(Pixel(page, 296, 206)) && IsInk(Pixel(page, 304, 206)), "the arms of the V");
        Assert.False(IsInk(Pixel(page, 300, 196)), "nothing far beyond the V's tip");
        // No arrowheads: the line ends at its end.
        Assert.False(IsInk(Pixel(page, 203, 300)));
    }

    [Fact]
    public void Inside_aligned_arrowheads_put_their_tip_on_the_end()
    {
        using var document = IdmlJson.CreateDocument("""
        { "colors": [ { "name": "Red", "hex": "#FF0000" } ], "pages": [ { "items": [
          { "type": "line", "name": "arrow", "x": 100, "y": 100, "x2": 200, "y2": 100, "stroke": "Red", "strokeWeight": 2, "endArrow": "triangle" } ] } ] }
        """);
        document.FindPageItem("arrow")!.SetAttribute("ArrowHeadAlignment", "InsideArrowHeadAlignment");
        document.FindPageItem("arrow")!.SetAttribute("RightArrowHeadScale", "200");

        var page = Rasterizer.Render(TestSupport.Export(document), 0, 72);
        Assert.False(IsInk(Pixel(page, 202, 100)), "nothing beyond the end");
        // Twice the size: 16 pt wide, 20 pt long, its base at 180.
        Assert.True(IsInk(Pixel(page, 183, 105)) && IsInk(Pixel(page, 183, 94)), "the doubled triangle's wide base");
        Assert.True(IsInk(Pixel(page, 197, 100)), "the tip near the end");
    }

    private static StoryLayout Layout(IdmlDocument document, string frameName) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(((TextFrame)document.FindPageItem(frameName)!).Story);

    [Fact]
    public void Superscript_and_subscript_runs_are_smaller_raised_and_lowered_and_measured_as_set()
    {
        using var document = IdmlJson.CreateDocument("""
        { "pages": [ { "items": [ { "type": "text", "name": "t", "x": 36, "y": 36, "width": 300, "height": 60, "paragraphs": [
          { "font": "Liberation Sans", "size": 12, "leading": 14, "runs": [
            { "text": "NO" }, { "text": "2", "position": "subscript" }, { "text": "x", "position": "superscript" }, { "text": " Cl" } ] } ] } ] } ] }
        """);

        var runs = Layout(document, "t").OpsByFrame.Values.Single().OfType<GlyphRunOp>().ToList();
        var normal = runs.Single(run => run.Text == "NO");
        var subscript = runs.Single(run => run.Text == "2");
        var superscript = runs.Single(run => run.Text == "x");
        var after = runs.Single(run => run.Text == " Cl");
        // InDesign's defaults: 58.3 % of the size, 33.3 % of it below or above the baseline.
        Assert.Equal(12 * 0.583, subscript.Size, 6);
        Assert.Equal(12 * 0.583, superscript.Size, 6);
        Assert.Equal(12 * 0.333, subscript.Y - normal.Y, 6);
        Assert.Equal(-12 * 0.333, superscript.Y - normal.Y, 6);
        // Each run starts where the smaller one before it ends.
        Assert.Equal(subscript.X + subscript.Glyphs.Sum(glyph => glyph.Advance), superscript.X, 6);
        Assert.Equal(superscript.X + superscript.Glyphs.Sum(glyph => glyph.Advance), after.X, 6);
        var digit = subscript.Font.TryMap('2', out var glyph) ? subscript.Font.Advance(glyph) : 0;
        Assert.Equal(digit * 12 * 0.583, subscript.Glyphs.Sum(placed => placed.Advance), 6);
    }

    [Fact]
    public void Superscript_size_and_position_follow_the_documents_advanced_type_preferences()
    {
        using var document = IdmlJson.CreateDocument("""
        { "pages": [ { "items": [ { "type": "text", "name": "t", "x": 36, "y": 36, "width": 300, "height": 60, "paragraphs": [
          { "font": "Liberation Sans", "size": 10, "runs": [ { "text": "m" }, { "text": "2", "position": "superscript" } ] } ] } ] } ] }
        """);
        var preference = document.Package.GetXml("Resources/Preferences.xml").Root!.Element("TextPreference")!;
        preference.SetAttributeValue("SuperscriptSize", "70");
        preference.SetAttributeValue("SuperscriptPosition", "40");

        var runs = Layout(document, "t").OpsByFrame.Values.Single().OfType<GlyphRunOp>().ToList();
        var superscript = runs.Single(run => run.Text == "2");
        Assert.Equal(7, superscript.Size, 6);
        Assert.Equal(-4, superscript.Y - runs.Single(run => run.Text == "m").Y, 6);
    }

    private static Rect Round(Rect r) => new(Math.Round(r.X, 3), Math.Round(r.Y, 3), Math.Round(r.Width, 3), Math.Round(r.Height, 3));
}
