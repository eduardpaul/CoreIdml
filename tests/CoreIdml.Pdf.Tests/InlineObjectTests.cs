using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>Page items anchored inline in text: they flow like a character standing on the baseline.</summary>
public sealed class InlineObjectTests
{
    private static readonly string Replacement = ((char)0xFFFC).ToString();

    private static (IdmlDocument Document, TextFrame Frame, XElement Shape) DocumentWithInlineShape(string fill = "Color/Black", double strokeWeight = 0, XElement? setting = null)
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 12, Leading = 14 });
        var frame = document.Pages[0].AddText(new Rect(100, 100, 300, 100), "A", style);
        var content = frame.Story.Element.Descendants("Content").Single();
        // InDesign stores where the item was before it was pasted into the text; only its size matters.
        var shape = new XElement("Rectangle",
            new XAttribute("Self", "uinline"),
            new XAttribute("ItemTransform", "1 0 0 1 500 -3000"),
            new XAttribute("FillColor", fill),
            new XAttribute("StrokeColor", strokeWeight > 0 ? "Color/Black" : "Swatch/None"),
            new XAttribute("StrokeWeight", strokeWeight.ToString(CultureInfo.InvariantCulture)),
            new XElement("Properties", new XElement("PathGeometry", new XElement("GeometryPathType", new XAttribute("PathOpen", "false"),
                new XElement("PathPointArray", Corner(0, 0), Corner(0, 6), Corner(10, 6), Corner(10, 0))))),
            setting);
        content.AddAfterSelf(shape, new XElement("Content", "B"));
        return (document, frame, shape);
    }

    private static XElement Corner(double x, double y)
    {
        var point = string.Create(CultureInfo.InvariantCulture, $"{x} {y}");
        return new XElement("PathPointType", new XAttribute("Anchor", point), new XAttribute("LeftDirection", point), new XAttribute("RightDirection", point));
    }

    private static StoryLayout Layout(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story);

    [Fact]
    public void An_inline_shape_takes_its_width_in_the_line_and_stands_on_the_baseline()
    {
        var (document, frame, _) = DocumentWithInlineShape();
        using var _ = document;
        var layout = Layout(document, frame);
        var ops = layout.OpsByFrame[frame.Id];
        var a = ops.OfType<GlyphRunOp>().Single(run => run.Text == "A");
        var b = ops.OfType<GlyphRunOp>().Single(run => run.Text == "B");
        var shape = Assert.Single(ops.OfType<PageItemOp>());

        var afterA = a.X + a.Width;
        // The shape's bottom-left corner (its ItemTransform applied) lands at the pen position on the baseline.
        var corner = shape.Placement.Apply(new Point(500, -3000 + 6));
        Assert.Equal(afterA, corner.X, 6);
        Assert.Equal(a.Y, corner.Y, 6);
        Assert.Equal(afterA + 10, b.X, 6);
        Assert.Equal("A" + Replacement + "B", Assert.Single(layout.Lines).Text);
    }

    [Fact]
    public void The_stroke_outside_the_path_counts_in_the_width_and_the_y_offset_raises_it()
    {
        var setting = new XElement("AnchoredObjectSetting", new XAttribute("AnchoredPosition", "InlinePosition"), new XAttribute("AnchorYoffset", "3"));
        var (document, frame, _) = DocumentWithInlineShape(strokeWeight: 2, setting: setting);
        using var _ = document;
        var ops = Layout(document, frame).OpsByFrame[frame.Id];
        var a = ops.OfType<GlyphRunOp>().Single(run => run.Text == "A");
        var b = ops.OfType<GlyphRunOp>().Single(run => run.Text == "B");
        var shape = Assert.Single(ops.OfType<PageItemOp>());

        // A centred 2 pt stroke adds 1 pt on each side: 12 pt wide, its visible bottom 3 pt above the baseline.
        Assert.Equal(a.X + a.Width + 12, b.X, 6);
        var visibleCorner = shape.Placement.Apply(new Point(500 - 1, -3000 + 6 + 1));
        Assert.Equal(a.X + a.Width, visibleCorner.X, 6);
        Assert.Equal(a.Y - 3, visibleCorner.Y, 6);
    }

    [Fact]
    public void Custom_positioned_anchored_objects_take_no_room_in_the_line()
    {
        var setting = new XElement("AnchoredObjectSetting", new XAttribute("AnchoredPosition", "Anchored"));
        var (document, frame, _) = DocumentWithInlineShape(setting: setting);
        using var _ = document;
        var ops = Layout(document, frame).OpsByFrame[frame.Id];
        Assert.True(Assert.Single(ops.OfType<PageItemOp>()).AfterText);
        Assert.Equal("AB", ops.OfType<GlyphRunOp>().Single().Text);
    }

    [Fact]
    public void Inline_shapes_are_painted_in_the_PDF()
    {
        var (document, _, shape) = DocumentWithInlineShape();
        using var _ = document;
        var red = document.Swatches.AddRgb("Marker", 255, 0, 0);
        shape.SetAttributeValue("FillColor", red.Reference);

        var page = Rasterizer.Render(TestSupport.Export(document), 0, 72);
        var redPixels = 0;
        for (var i = 0; i < page.Pixels.Length; i += page.Channels)
        {
            if (page.Pixels[i] > 200 && page.Pixels[i + 1] < 60 && page.Pixels[i + 2] < 60)
            {
                redPixels++;
            }
        }
        // 10 x 6 pt at one pixel per point, give or take the anti-aliased edges.
        Assert.InRange(redPixels, 40, 80);
    }
}
