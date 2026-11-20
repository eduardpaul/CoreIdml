using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Text wrap (<c>TextWrapPreference</c>) and text in shaped frames: the stretches a line may take
/// (<see cref="TextArea"/>) and where the composer puts lines, as measured in 02-item-text-wrap and 06-frame-shapes.
/// </summary>
public sealed class TextWrapTests
{
    private const string Text =
        "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife. " +
        "However little known the feelings or views of such a man may be on his first entering a neighbourhood, this truth is so " +
        "well fixed in the minds of the surrounding families, that he is considered the rightful property of some one or other " +
        "of their daughters. My dear Mr. Bennet, said his lady to him one day, have you heard that Netherfield Park is let at last?";

    private static readonly Rect Column = new(0, 0, 200, 300);

    private static List<(double Left, double Right)> Pieces(WrapRegion region, double top, double bottom, Rect? column = null)
    {
        var pieces = new List<(double Left, double Right)>();
        new TextArea([region]).Pieces(column ?? Column, top, bottom, pieces);
        return pieces;
    }

    [Fact]
    public void A_bounding_box_wrap_leaves_the_column_either_side_of_the_box_and_its_offsets()
    {
        var box = new WrapRegion(WrapMode.Around, new Rect(80, 100, 40, 50), new Spacing(6, 4, 6, 8));

        Assert.Equal([(0, 72), (124, 200)], Pieces(box, 90, 102.5));
        // The band ends at the baseline: a line whose band ends where the offset starts is not wrapped.
        Assert.Equal([(0, 200)], Pieces(box, 81.5, 94));
        Assert.Equal(BandWrap.Clear, new TextArea([box]).Pieces(Column, 156, 168.5, []));
    }

    [Theory]
    [InlineData("Left", 0, 72)]
    [InlineData("Right", 124, 200)]
    [InlineData("Largest", 124, 200)]
    public void A_one_sided_wrap_keeps_text_on_that_side(string side, double left, double right)
    {
        var box = new WrapRegion(WrapMode.Around, new Rect(80, 100, 40, 50), new Spacing(6, 4, 6, 8), Enum.Parse<WrapSide>(side));

        Assert.Equal([(left, right)], Pieces(box, 100, 112.5));
    }

    [Fact]
    public void The_largest_area_takes_the_wider_side_and_the_left_one_on_a_tie()
    {
        var rightOfCentre = new WrapRegion(WrapMode.Around, new Rect(110, 100, 40, 50), default, WrapSide.Largest);
        var centred = new WrapRegion(WrapMode.Around, new Rect(80, 100, 40, 50), default, WrapSide.Largest);

        Assert.Equal([(0, 110)], Pieces(rightOfCentre, 100, 112.5));
        Assert.Equal([(0, 80)], Pieces(centred, 100, 112.5));
    }

    [Fact]
    public void Jump_object_leaves_nothing_and_next_column_sends_the_line_on()
    {
        var jump = new WrapRegion(WrapMode.Jump, new Rect(80, 100, 40, 50), default);
        var next = new WrapRegion(WrapMode.NextColumn, new Rect(80, 100, 40, 50), default);
        var pieces = new List<(double Left, double Right)>();

        Assert.Equal(BandWrap.Wrapped, new TextArea([jump]).Pieces(Column, 100, 112.5, pieces));
        Assert.Empty(pieces);
        Assert.Equal(BandWrap.NextColumn, new TextArea([next]).Pieces(Column, 100, 112.5, pieces));
        // An object beside the column does not touch it.
        Assert.Equal(BandWrap.Clear, new TextArea([jump]).Pieces(new Rect(130, 0, 70, 300), 100, 112.5, pieces));
    }

    [Fact]
    public void An_inverted_box_keeps_only_the_bands_wholly_inside_it()
    {
        var inside = new WrapRegion(WrapMode.Inside, new Rect(40, 100, 120, 100), Spacing.All(6));

        Assert.Equal([(46, 154)], Pieces(inside, 110, 122.5));
        // 02-item-text-wrap p9: the band that crosses the top edge stays empty; bands above and below are free.
        Assert.Empty(Pieces(inside, 95, 107.5));
        Assert.Equal([(0, 200)], Pieces(inside, 80, 92.5));
    }

    [Fact]
    public void An_object_shape_wrap_grows_the_shape_round_its_corners_and_truncates_to_whole_points()
    {
        // A square 40 pt wide with an 8 pt offset: the band just below its top meets only the round corner reach.
        var square = new Outline([new(80, 100), new(120, 100), new(120, 140), new(80, 140)], true);
        var region = new WrapRegion(WrapMode.Around, new Rect(80, 100, 40, 40), Spacing.All(8), Outlines: [square]);

        // A band ending 4 pt above the top: the corners reach sqrt(64 - 16) = 6.93 pt sideways, truncated to whole points.
        Assert.Equal([(0, 73), (126, 200)], Pieces(region, 80, 96));
        // Beside the square: its sides plus 8.
        Assert.Equal([(0, 72), (128, 200)], Pieces(region, 110, 120));
        // A band that ends exactly the offset above it is free.
        Assert.Equal([(0, 200)], Pieces(region, 80, 92));
    }

    [Fact]
    public void A_shaped_frame_keeps_text_inside_its_path_less_the_inset()
    {
        // A triangle pointing up, from (100, 0) to (0, 200) and (200, 200): at height y its sides are y / 2 from the centre.
        var triangle = new Outline([new(100, 0), new(200, 200), new(0, 200)], true);
        var frame = new WrapRegion(WrapMode.Inside, new Rect(0, 0, 200, 200), Spacing.All(0), Outlines: [triangle]);

        // The narrowest part of the band is its top: 80 to 120 at y = 40.
        Assert.Equal([(80, 120)], Pieces(frame, 40, 52));
        // With an inset the sides move in by 5 / cos(angle) = 5 * sqrt(5) / 2 = 5.59, truncated to whole points.
        var inset = frame with { Offset = Spacing.All(5) };
        Assert.Equal([(85, 114)], Pieces(inset, 40, 52));
    }

    [Fact]
    public void An_edge_reach_is_exact_above_and_below_the_band()
    {
        // A 45 degree edge from (0, 0) to (100, 100); the band from 50 to 60 and a reach of 10.
        var (left, right) = TextArea.EdgeReach(new Point(0, 0), new Point(100, 100), 10, 50, 60)!.Value;

        // Leftmost: the circle round a point above the band, tangent where its slope matches: 50 - 10 * sqrt(2).
        Assert.Equal(50 - (10 * Math.Sqrt(2)), left, 9);
        Assert.Equal(60 + (10 * Math.Sqrt(2)), right, 9);
        Assert.Null(TextArea.EdgeReach(new Point(0, 0), new Point(100, 0), 10, 20, 30));
    }

    [Fact]
    public void Flattened_curves_stay_within_two_hundredths_of_a_point()
    {
        var radius = 100.0;
        var k = 0.5522847498307936 * radius;
        var circle = new PathContour(false,
        [
            new PathPoint(new(0, -radius), new(-k, -radius), new(k, -radius)),
            new PathPoint(new(radius, 0), new(radius, -k), new(radius, k)),
            new PathPoint(new(0, radius), new(k, radius), new(-k, radius)),
            new PathPoint(new(-radius, 0), new(-radius, k), new(-radius, -k)),
        ]);

        var outline = Assert.Single(TextArea.Flatten([circle], Matrix.Identity));

        Assert.True(outline.Closed);
        // The Bézier quarter circle strays 0.027% from the circle itself; the chords add at most 0.02 pt.
        Assert.All(outline.Points, point => Assert.InRange(Math.Sqrt((point.X * point.X) + (point.Y * point.Y)), radius - 0.03, radius + 0.03));
        for (var i = 0; i < outline.Points.Length; i++)
        {
            var (a, b) = (outline.Points[i], outline.Points[(i + 1) % outline.Points.Length]);
            var middle = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            Assert.InRange(Math.Sqrt((middle.X * middle.X) + (middle.Y * middle.Y)), radius - 0.05, radius + 0.03);
        }
    }

    // ---- layout ----------------------------------------------------------------------------------------

    private static (IdmlDocument Document, TextFrame Frame) Document()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12.5 });
        var frame = document.Pages[0].AddText(new Rect(100, 100, 300, 400), Text, style);
        return (document, frame);
    }

    // A rectangle on the page with a text wrap (and no stroke).
    private static GraphicFrame Wrapping(IdmlDocument document, Rect bounds, string mode, double offset, string side = "BothSides", bool inverse = false)
    {
        var item = document.Pages[0].AddRectangle(bounds);
        item.SetAttribute("StrokeWeight", "0");
        var value = offset.ToString(CultureInfo.InvariantCulture);
        item.Element.Add(new XElement("TextWrapPreference",
            new XAttribute("Inverse", inverse ? "true" : "false"),
            new XAttribute("TextWrapSide", side),
            new XAttribute("TextWrapMode", mode),
            new XElement("Properties", new XElement("TextWrapOffset",
                new XAttribute("Top", value), new XAttribute("Left", value), new XAttribute("Bottom", value), new XAttribute("Right", value)))));
        return item;
    }

    private static (StoryLayout Layout, FrameArea Area, Rect Box) Layout(IdmlDocument document, TextFrame frame, PageItem? wrapping = null)
    {
        var renderer = new IdmlRenderer(document, TestSupport.ExportOptions());
        var area = renderer.FrameArea(frame, "1");
        // The wrapping item in the frame's inner coordinates (neither is rotated).
        var box = wrapping is null ? default : wrapping.Bounds.Offset(frame.InnerBounds.X - frame.Bounds.X, frame.InnerBounds.Y - frame.Bounds.Y);
        return (renderer.LayoutStory(frame.Story), area, box);
    }

    [Fact]
    public void A_frame_with_nothing_around_it_has_no_text_area()
    {
        var (document, frame) = Document();
        using var _ = document;
        document.Pages[0].AddRectangle(new Rect(150, 150, 100, 100));

        var (_, area, _) = Layout(document, frame);

        Assert.Null(area.TextArea);
    }

    [Fact]
    public void Lines_beside_a_bounding_box_wrap_go_either_side_of_it_and_none_runs_under_it()
    {
        var (document, frame) = Document();
        using var _ = document;
        var (plain, _, _) = Layout(document, frame);
        var rectangle = Wrapping(document, new Rect(200, 150, 100, 60), "BoundingBoxTextWrap", 6);

        var (layout, area, box) = Layout(document, frame, rectangle);

        var column = area.Columns[0];
        var beside = layout.Lines.Where(line => line.Baseline - 12.5 < box.Bottom + 6 && line.Baseline > box.Top - 6).ToList();
        Assert.NotEmpty(beside);
        Assert.All(beside, line => Assert.True(line.Left + line.Width <= box.Left - 6 + 0.01 || line.Left >= box.Right + 6 - 0.01, $"{line.Text} at {line.Left}"));
        // Both sides of the box take text: two lines on each baseline beside it.
        Assert.Contains(beside, line => line.Left >= box.Right);
        Assert.Contains(beside.GroupBy(line => line.Baseline), pair => pair.Count() == 2);
        // The wrap takes room: the text needs more lines.
        Assert.True(layout.Lines.Max(line => line.Baseline) > plain.Lines.Max(line => line.Baseline));
        Assert.All(layout.Lines, line => Assert.InRange(line.Left, column.Left, column.Right));
    }

    [Fact]
    public void A_frame_that_ignores_text_wrap_keeps_its_lines()
    {
        var (document, frame) = Document();
        using var _ = document;
        var (plain, _, _) = Layout(document, frame);
        var rectangle = Wrapping(document, new Rect(200, 150, 100, 60), "BoundingBoxTextWrap", 6);
        frame.Element.Element("TextFramePreference")!.SetAttributeValue("IgnoreWrap", "true");

        var (layout, area, _) = Layout(document, frame, rectangle);

        Assert.Null(area.TextArea);
        Assert.Equal(plain.Lines, layout.Lines);
    }

    [Fact]
    public void Jump_object_moves_the_lines_below_the_object_on_the_leading()
    {
        var (document, frame) = Document();
        using var _ = document;
        var rectangle = Wrapping(document, new Rect(200, 150, 100, 60), "JumpObjectTextWrap", 6);

        var (layout, _, box) = Layout(document, frame, rectangle);

        // No band (the leading above a baseline) meets the object and its offsets.
        Assert.All(layout.Lines, line => Assert.True(line.Baseline <= box.Top - 6 + 0.01 || line.Baseline - 12.5 >= box.Bottom + 6 - 0.01, $"{line.Baseline}"));
        // 02-item-text-wrap p5: the lines below it stay on the steps of the leading.
        var first = layout.Lines[0].Baseline;
        Assert.All(layout.Lines, line => Assert.Equal(0, Math.IEEERemainder(line.Baseline - first, 12.5), 6));
    }

    [Fact]
    public void An_alpha_channel_contour_wraps_the_opaque_part_of_the_image()
    {
        var (document, frame) = Document();
        using var _ = document;
        // A 20 x 20 pixel PNG whose right half is opaque, placed 100 pt square: the opaque part starts 50 pt in.
        var pixels = new byte[20 * 20 * 4];
        for (var i = 0; i < 20 * 20; i++)
        {
            pixels[(4 * i) + 3] = (byte)(i % 20 >= 10 ? 255 : 0);
        }
        var path = Path.Combine(TestSupport.OutputDirectory("text-wrap-alpha"), "half.png");
        File.WriteAllBytes(path, Imaging.PngCodec.Encode(new Imaging.RasterImage(20, 20, 4, pixels)));
        var image = document.Pages[0].AddImage(new Rect(150, 150, 100, 100), path);
        image.Element.Add(new XElement("TextWrapPreference",
            new XAttribute("TextWrapSide", "BothSides"),
            new XAttribute("TextWrapMode", "Contour"),
            new XElement("Properties", new XElement("TextWrapOffset", new XAttribute("Top", "0"), new XAttribute("Left", "0"), new XAttribute("Bottom", "0"), new XAttribute("Right", "0"))),
            new XElement("ContourOption", new XAttribute("ContourType", "AlphaChannel"))));

        var (layout, _, box) = Layout(document, frame, image);

        var beside = layout.Lines.Where(line => line.Baseline - 12.5 < box.Bottom && line.Baseline > box.Top).ToList();
        Assert.NotEmpty(beside);
        // The transparent left half takes text: a line ends in it, and none crosses the opaque half.
        Assert.Contains(beside, line => line.Left + line.Width > box.Left + 1 && line.Left + line.Width <= box.Left + 50 + 0.01);
        Assert.All(beside, line => Assert.True(line.Left + line.Width <= box.Left + 50 + 0.01 || line.Left >= box.Right - 0.01, $"{line.Text} at {line.Left}"));
    }

    [Fact]
    public void Text_in_a_polygon_frame_stays_inside_it()
    {
        var (document, frame) = Document();
        using var _ = document;
        // A diamond: the frame's path from its top centre to its right, bottom and left centres.
        var bounds = frame.InnerBounds;
        var (cx, cy) = (bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
        var points = frame.Element.Descendants("PathPointType").ToList();
        (double X, double Y)[] diamond = [(cx, bounds.Top), (bounds.Right, cy), (cx, bounds.Bottom), (bounds.Left, cy)];
        for (var i = 0; i < 4; i++)
        {
            var point = string.Create(CultureInfo.InvariantCulture, $"{diamond[i].X} {diamond[i].Y}");
            foreach (var name in (string[])["Anchor", "LeftDirection", "RightDirection"])
            {
                points[i].SetAttributeValue(name, point);
            }
        }

        var (layout, area, _) = Layout(document, frame);

        Assert.NotNull(area.TextArea);
        Assert.NotEmpty(layout.Lines);
        // Half the diamond's width at height y is (h/2 - |y - cy|) * w / h; the band runs from the baseline before.
        double HalfWidth(double y) => ((bounds.Height / 2) - Math.Abs(y - cy)) * bounds.Width / bounds.Height;
        foreach (var line in layout.Lines)
        {
            // The first line's band starts its first-baseline offset above its baseline (checked at the baseline only).
            var top = line == layout.Lines[0] ? line.Baseline : line.Baseline - 12.5;
            var narrowest = Math.Min(HalfWidth(Math.Max(top, bounds.Top)), HalfWidth(line.Baseline));
            Assert.True(line.Left >= cx - narrowest - 1, $"{line.Text} starts at {line.Left}, the diamond at {cx - narrowest}");
            Assert.True(line.Left + line.Width <= cx + narrowest + 1, $"{line.Text} ends at {line.Left + line.Width}");
        }
    }
}
