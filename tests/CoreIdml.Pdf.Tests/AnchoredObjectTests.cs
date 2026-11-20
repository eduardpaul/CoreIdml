using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Above-line and custom-positioned anchored objects (<c>AnchoredObjectSetting@AnchoredPosition</c> <c>AboveLine</c>
/// and <c>Anchored</c>), as measured in 02-item-anchored and 12-pdf-accessibility.
/// </summary>
public sealed class AnchoredObjectTests
{
    // The shape's path: 90 x 30 pt, its ItemTransform puts it at (500, -3000) (where it was before it was anchored).
    private const double Width = 90;
    private const double Height = 30;
    private static readonly Point StoredTopLeft = new(500, -3000);

    private static (IdmlDocument Document, TextFrame Frame) DocumentWithAnchoredShape(string text, params (string Name, string Value)[] setting)
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12.5 });
        var frame = document.Pages[0].AddText(new Rect(100, 100, 300, 200), text, style);
        var content = frame.Story.Element.Descendants("Content").First();
        content.AddBeforeSelf(new XElement("Rectangle",
            new XAttribute("Self", "uanchored"),
            new XAttribute("ItemTransform", "1 0 0 1 500 -3000"),
            new XAttribute("FillColor", "Color/Black"),
            new XAttribute("StrokeColor", "Swatch/None"),
            new XElement("Properties", new XElement("PathGeometry", new XElement("GeometryPathType", new XAttribute("PathOpen", "false"),
                new XElement("PathPointArray", Corner(0, 0), Corner(0, Height), Corner(Width, Height), Corner(Width, 0))))),
            new XElement("AnchoredObjectSetting", setting.Select(attribute => new XAttribute(attribute.Name, attribute.Value)))));
        return (document, frame);
    }

    private static XElement Corner(double x, double y)
    {
        var point = string.Create(CultureInfo.InvariantCulture, $"{x} {y}");
        return new XElement("PathPointType", new XAttribute("Anchor", point), new XAttribute("LeftDirection", point), new XAttribute("RightDirection", point));
    }

    private static (StoryLayout Layout, FrameArea Area) Layout(IdmlDocument document, TextFrame frame)
    {
        var renderer = new IdmlRenderer(document, TestSupport.ExportOptions());
        return (renderer.LayoutStory(frame.Story), renderer.FrameArea(frame, "1"));
    }

    /// <summary>Where the shape's corner (0 or 1 of its width and height) lands in the frame.</summary>
    private static Point Corner(PageItemOp op, double x, double y) =>
        op.Placement.Apply(new Point(StoredTopLeft.X + (x * Width), StoredTopLeft.Y + (y * Height)));

    [Theory]
    [InlineData("LeftAlign", 0)]
    [InlineData("CenterAlign", 0.5)]
    [InlineData("RightAlign", 1)]
    public void An_above_line_object_takes_its_height_and_space_before_above_its_line(string alignment, double side)
    {
        var (document, frame) = DocumentWithAnchoredShape("Paragraph", ("AnchoredPosition", "AboveLine"), ("HorizontalAlignment", alignment), ("AnchorSpaceAbove", "6"));
        using var _ = document;
        var (layout, area) = Layout(document, frame);
        var column = area.Columns[0];
        var line = Assert.Single(layout.Lines);
        var shape = Assert.Single(layout.OpsByFrame[frame.Id].OfType<PageItemOp>());

        // 02-item-anchored p2: the baseline 6 + 30 + 12.5 pt below the column top; the object's bottom 0.8274 em above it.
        Assert.Equal(column.Y + 6 + Height + 12.5, line.Baseline, 6);
        var bottomLeft = Corner(shape, 0, 1);
        Assert.Equal(line.Baseline - 8.274, bottomLeft.Y, 6);
        Assert.Equal(column.X + ((column.Width - Width) * side), bottomLeft.X, 6);
    }

    [Fact]
    public void A_line_after_another_counts_the_object_from_the_previous_baseline()
    {
        var (document, frame) = DocumentWithAnchoredShape("One", ("AnchoredPosition", "AboveLine"), ("AnchorSpaceAbove", "12"));
        using var _ = document;
        var content = frame.Story.Element.Descendants("Content").Single();
        // Move the shape to the second paragraph.
        var shape = frame.Story.Element.Descendants("Rectangle").Single();
        shape.Remove();
        content.AddAfterSelf(new XElement("Br"), shape, new XElement("Content", "Two"));
        var (layout, _) = Layout(document, frame);

        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal(layout.Lines[0].Baseline + 12 + Height + 12.5, layout.Lines[1].Baseline, 6);
    }

    [Fact]
    public void The_marker_kerns_with_the_letter_after_it_as_a_space()
    {
        var (document, frame) = DocumentWithAnchoredShape("Above", ("AnchoredPosition", "AboveLine"));
        using var _ = document;
        var (layout, area) = Layout(document, frame);
        var run = Assert.Single(layout.OpsByFrame[frame.Id].OfType<GlyphRunOp>());
        var font = run.Font;
        Assert.True(font.TryMap(' ', out var space));
        Assert.True(font.TryMap('A', out var a));

        // 12-pdf-accessibility p4: Liberation Serif's space-A pair moves "Above" left of the column.
        var kerning = font.Kerning(space, a) * 10;
        Assert.True(kerning < -0.5);
        Assert.Equal(area.Columns[0].X + kerning, run.X, 6);
    }

    [Fact]
    public void A_custom_object_goes_on_its_reference_point_and_takes_no_room_in_the_line()
    {
        var (document, frame) = DocumentWithAnchoredShape("Paragraph", ("AnchoredPosition", "Anchored"), ("AnchorPoint", "BottomRightAnchor"),
            ("HorizontalReferencePoint", "ColumnEdge"), ("HorizontalAlignment", "RightAlign"), ("VerticalReferencePoint", "LineBaseline"),
            ("AnchorXoffset", "10"), ("AnchorYoffset", "-8"), ("PinPosition", "false"));
        using var _ = document;
        var (layout, area) = Layout(document, frame);
        var column = area.Columns[0];
        var line = Assert.Single(layout.Lines);
        var ops = layout.OpsByFrame[frame.Id];
        var shape = Assert.Single(ops.OfType<PageItemOp>());

        // 02-item-anchored p3: the box's bottom-right corner 10 pt right of the column, 8 pt above the baseline.
        var bottomRight = Corner(shape, 1, 1);
        Assert.Equal(column.Right + 10, bottomRight.X, 6);
        Assert.Equal(line.Baseline - 8, bottomRight.Y, 6);
        // The text starts at the column edge, and the object is drawn after it.
        var run = Assert.Single(ops.OfType<GlyphRunOp>());
        Assert.Equal(column.X, run.X, 6);
        Assert.True(ops.IndexOf(shape) > ops.IndexOf(run));
    }

    [Fact]
    public void An_x_offset_from_a_left_edge_moves_the_object_out_of_the_area()
    {
        var (document, frame) = DocumentWithAnchoredShape("Paragraph", ("AnchoredPosition", "Anchored"), ("AnchorPoint", "TopLeftAnchor"),
            ("HorizontalReferencePoint", "ColumnEdge"), ("HorizontalAlignment", "LeftAlign"), ("VerticalReferencePoint", "LineBaseline"),
            ("AnchorXoffset", "-20"), ("PinPosition", "false"));
        using var _ = document;
        var (layout, area) = Layout(document, frame);
        var shape = Assert.Single(layout.OpsByFrame[frame.Id].OfType<PageItemOp>());

        // 02-item-anchored p4: X offset -90 from the column's left edge ends the box 90 pt inside the column.
        var topLeft = Corner(shape, 0, 0);
        Assert.Equal(area.Columns[0].X + 20, topLeft.X, 6);
        Assert.Equal(Assert.Single(layout.Lines).Baseline, topLeft.Y, 6);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void A_pinned_object_stays_within_the_column_top(string pinned)
    {
        var (document, frame) = DocumentWithAnchoredShape("Paragraph", ("AnchoredPosition", "Anchored"), ("AnchorPoint", "BottomRightAnchor"),
            ("HorizontalReferencePoint", "ColumnEdge"), ("HorizontalAlignment", "LeftAlign"), ("VerticalReferencePoint", "LineBaseline"),
            ("PinPosition", pinned));
        using var _ = document;
        var (layout, area) = Layout(document, frame);
        var shape = Assert.Single(layout.OpsByFrame[frame.Id].OfType<PageItemOp>());

        // The first baseline is less than 30 pt below the column top: unpinned, the box sticks out above it.
        var top = Corner(shape, 0, 0).Y;
        Assert.Equal(pinned == "true" ? area.Columns[0].Y : Assert.Single(layout.Lines).Baseline - Height, top, 6);
    }

    [Fact]
    public void A_custom_object_can_be_placed_from_the_page_edge()
    {
        var (document, frame) = DocumentWithAnchoredShape("Paragraph", ("AnchoredPosition", "Anchored"), ("AnchorPoint", "BottomRightAnchor"),
            ("HorizontalReferencePoint", "PageEdge"), ("HorizontalAlignment", "RightAlign"), ("VerticalReferencePoint", "PageEdge"),
            ("VerticalAlignment", "BottomAlign"), ("AnchorXoffset", "-4"), ("AnchorYoffset", "-6"));
        using var _ = document;
        var (layout, _) = Layout(document, frame);
        var shape = Assert.Single(layout.OpsByFrame[frame.Id].OfType<PageItemOp>());

        var page = document.Pages[0].SpreadBounds;
        var bottomRight = frame.SpreadTransform.Apply(Corner(shape, 1, 1));
        Assert.Equal(page.Right - 4, bottomRight.X, 6);
        Assert.Equal(page.Bottom - 6, bottomRight.Y, 6);
        Assert.True(shape.FixedInFrame);
    }
}
