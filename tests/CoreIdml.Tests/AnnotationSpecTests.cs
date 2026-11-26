using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Json;

namespace CoreIdml.Tests;

/// <summary>
/// The IDML a spec writes for annotated sheets: rotated image frames, custom dashes on shapes and lines, ellipses,
/// arrowheads, and superscript and subscript runs, as InDesign stores them.
/// </summary>
public class AnnotationSpecTests
{
    private static XElement Item(IdmlDocument document, string name) =>
        document.AllPageItems.Single(item => item.Name == name).Element;

    private static string Png()
    {
        var path = TestFiles.TempPath(".png");
        File.WriteAllBytes(path, TestFiles.Png(40, 10));
        return path;
    }

    [Fact]
    public void A_rotated_image_frame_turns_with_its_image_inside()
    {
        var png = Png();
        using var document = IdmlJson.CreateDocument($$"""
        { "unit": "mm", "pages": [ { "items": [
          { "type": "image", "name": "logo", "x": 10, "y": 20, "width": 40, "height": 10, "rotation": 90, "path": "{{png.Replace("\\", "\\\\", StringComparison.Ordinal)}}", "fit": "fit" } ] } ] }
        """);

        var frame = (GraphicFrame)document.FindPageItem("logo")!;
        // The frame turns 90 degrees counter-clockwise about its centre: the box given is the unrotated frame.
        var transform = frame.Transform;
        Assert.Equal(0, transform.A, 9);
        Assert.Equal(-1, transform.B, 9);
        Assert.Equal(1, transform.C, 9);
        Assert.Equal(0, transform.D, 9);
        var bounds = frame.Bounds;
        Assert.Equal(Units.FromUnit(10, "mm") + Units.FromUnit(15, "mm"), bounds.X, 6);
        Assert.Equal(Units.FromUnit(20, "mm") - Units.FromUnit(15, "mm"), bounds.Y, 6);
        Assert.Equal(Units.FromUnit(10, "mm"), bounds.Width, 6);
        Assert.Equal(Units.FromUnit(40, "mm"), bounds.Height, 6);
        // The image keeps its own (unrotated) transform inside the frame, as InDesign rotates a frame with its content.
        var image = frame.Element.Element("Image")!;
        var placed = Matrix.Parse(image.Attribute("ItemTransform")!.Value);
        Assert.Equal(0, placed.B, 9);
        Assert.Equal(0, placed.C, 9);
        Assert.True(IdmlValidator.Validate(document).IsValid);
    }

    [Fact]
    public void Stroke_dashes_write_InDesigns_custom_dashed_type_in_points()
    {
        using var document = IdmlJson.CreateDocument("""
        { "unit": "mm", "colors": [ { "name": "Red", "hex": "#E30613" } ], "pages": [ { "items": [
          { "type": "rectangle", "name": "box", "x": 10, "y": 10, "width": 30, "height": 20, "stroke": "Red", "strokeWeight": 1.5, "strokeDash": [6, 3] },
          { "type": "ellipse", "name": "ring", "x": 50, "y": 10, "width": 20, "height": 20, "stroke": "Red", "strokeWeight": 1, "strokeDash": [4] },
          { "type": "line", "name": "rule", "x": 10, "y": 50, "x2": 60, "y2": 50, "stroke": "Red", "strokeDash": [6, 2, 1, 2] },
          { "type": "rectangle", "name": "solid", "x": 10, "y": 60, "width": 10, "height": 10, "stroke": "Red" },
          { "type": "rectangle", "name": "cleared", "x": 30, "y": 60, "width": 10, "height": 10, "stroke": "Red", "strokeType": "dotted", "strokeDash": [] }
        ] } ] }
        """);

        var box = Item(document, "box");
        Assert.Equal("Rectangle", box.Name.LocalName);
        Assert.Equal("StrokeStyle/$ID/Dashed", box.Attribute("StrokeType")?.Value);
        Assert.Equal("6 3", box.Attribute("StrokeDashAndGap")?.Value);
        var ring = Item(document, "ring");
        Assert.Equal("Oval", ring.Name.LocalName);
        Assert.Equal("Color/Red", ring.Attribute("StrokeColor")?.Value);
        Assert.Equal("StrokeStyle/$ID/Dashed", ring.Attribute("StrokeType")?.Value);
        Assert.Equal("4 4", ring.Attribute("StrokeDashAndGap")?.Value);
        Assert.Equal("6 2 1 2", Item(document, "rule").Attribute("StrokeDashAndGap")?.Value);
        // Without strokeDash nothing changes: no stroke type of its own, no dashes.
        var solid = Item(document, "solid");
        Assert.Null(solid.Attribute("StrokeType"));
        Assert.Null(solid.Attribute("StrokeDashAndGap"));
        // [] is solid, whatever strokeType says.
        var cleared = Item(document, "cleared");
        Assert.Equal("StrokeStyle/$ID/Solid", cleared.Attribute("StrokeType")?.Value);
        Assert.Null(cleared.Attribute("StrokeDashAndGap"));
        Assert.True(IdmlValidator.Validate(document).IsValid);
    }

    [Fact]
    public void A_stroke_type_set_later_drops_the_custom_dashes()
    {
        using var document = IdmlDocument.Create();
        var box = document.Pages[0].AddRectangle(new Rect(10, 10, 50, 50), new ShapeStyle { Stroke = Swatch.Black, StrokeDash = [3, 1] });
        Assert.Equal("3 1", box.GetAttribute("StrokeDashAndGap"));

        box.ApplyStyle(new ShapeStyle { StrokeKind = StrokeKind.Dotted });

        Assert.Equal("StrokeStyle/$ID/Canned Dotted", box.GetAttribute("StrokeType"));
        Assert.Null(box.GetAttribute("StrokeDashAndGap"));
    }

    [Fact]
    public void Arrowheads_are_InDesigns_line_ends()
    {
        using var document = IdmlJson.CreateDocument("""
        { "pages": [ { "items": [
          { "type": "line", "name": "arrow", "x": 10, "y": 10, "x2": 100, "y2": 10, "strokeWeight": 2, "endArrow": "triangle" },
          { "type": "line", "name": "both", "x": 10, "y": 30, "x2": 100, "y2": 30, "startArrow": "Simple-Wide", "endArrow": "CircleSolidArrowHead" },
          { "type": "line", "name": "plain", "x": 10, "y": 50, "x2": 100, "y2": 50 }
        ] } ] }
        """);

        var arrow = Item(document, "arrow");
        Assert.Equal("TriangleArrowHead", arrow.Attribute("RightLineEnd")?.Value);
        Assert.Null(arrow.Attribute("LeftLineEnd"));
        var both = Item(document, "both");
        Assert.Equal("SimpleWideArrowHead", both.Attribute("LeftLineEnd")?.Value);
        Assert.Equal("CircleSolidArrowHead", both.Attribute("RightLineEnd")?.Value);
        // The start is the line's x/y: the first point of its path.
        var first = both.Descendants("PathPointType").First().Attribute("Anchor")!.Value;
        Assert.Equal(-45, double.Parse(first.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Null(Item(document, "plain").Attribute("RightLineEnd"));
        Assert.True(IdmlValidator.Validate(document).IsValid);
    }

    [Theory]
    [InlineData("""{ "type": "rectangle", "strokeDash": [1, 2, 3] }""", "A dash pattern has 1, 2, 4 or 6 lengths (dash, gap, ...), not 3.")]
    [InlineData("""{ "type": "line", "strokeDash": [0, 2] }""", "A dash pattern needs a dash longer than 0.")]
    [InlineData("""{ "type": "ellipse", "strokeDash": [2, -1] }""", "Dash and gap lengths must be 0 or more.")]
    [InlineData("""{ "type": "line", "endArrow": "star" }""",
        "Unknown endArrow 'star'. Use none, simple, simpleWide, triangle, triangleWide, barbed, curved, circle, circleSolid, square, squareSolid or bar.")]
    public void Wrong_dashes_and_arrowheads_are_spec_errors(string item, string message)
    {
        var exception = Assert.Throws<IdmlException>(() => IdmlJson.CreateDocument($$"""{ "pages": [ { "items": [ {{item}} ] } ] }"""));

        Assert.EndsWith(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_spec_check_knows_the_new_fields_and_where_they_apply()
    {
        Assert.Empty(IdmlJson.CheckSpec("""
        { "pages": [ { "items": [
          { "type": "ellipse", "strokeDash": [2, 1] },
          { "type": "line", "strokeDash": [2, 1], "startArrow": "none", "endArrow": "barbed" },
          { "type": "image", "path": "a.png", "rotation": 90 },
          { "type": "text", "paragraphs": [ { "runs": [ { "text": "2", "position": "subscript" } ] } ] }
        ] } ] }
        """));
        Assert.Equal(
            [
                "pages[0].items[0]: 'endArrow' does not apply to a rectangle item; it is ignored.",
                "pages[0].items[1]: unknown field 'dashArray', ignored (did you mean 'strokeDash'?).",
            ],
            IdmlJson.CheckSpec("""
            { "pages": [ { "items": [ { "type": "rectangle", "endArrow": "triangle" }, { "type": "line", "dashArray": [1, 1] } ] } ] }
            """));
    }

    [Fact]
    public void Superscript_and_subscript_runs_are_character_positions()
    {
        using var document = IdmlJson.CreateDocument("""
        { "pages": [ { "items": [ { "type": "text", "name": "t", "x": 10, "y": 10, "width": 200, "height": 40, "paragraphs": [ { "runs": [
          { "text": "NO" }, { "text": "2", "position": "subscript" }, { "text": "−", "position": "superscript" }, { "text": " and Cl" },
          { "text": "−", "position": "superscript" }, { "text": " in water", "position": "normal" }
        ] } ] } ] } ] }
        """);

        var ranges = ((TextFrame)document.FindPageItem("t")!).Story.Element.Descendants("CharacterStyleRange").ToList();
        Assert.Equal(
            [(null, "NO"), ("Subscript", "2"), ("Superscript", "−"), (null, " and Cl"), ("Superscript", "−"), ("Normal", " in water")],
            ranges.Select(range => (range.Attribute("Position")?.Value, string.Concat(range.Elements("Content").Select(content => content.Value)))));
    }
}
