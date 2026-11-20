using System.Globalization;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// A table split over columns and frames: header rows repeat at the top and footer rows at the bottom of every part,
/// the footer's height kept free when deciding which body rows fit (doc-samples 09-style-table-cell-styles p3,
/// 10-table-structure p4).
/// </summary>
public sealed class TableBreakTests
{
    private const double RowHeight = 20;

    /// <summary>
    /// A one-column table of 20 pt rows, a header row "H", body rows "B1".. and a footer row "F", in the first of
    /// <paramref name="frames"/> threaded frames <paramref name="height"/> high, after a paragraph "Intro" with <paramref name="intro"/>.
    /// </summary>
    private static (IdmlDocument Document, TextFrame[] Frames, Table Table) NewTable(int bodyRows, int frames, double height, int columns = 1, bool intro = false)
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var placed = Enumerable.Range(0, frames)
            .Select(i => document.Pages[0].AddTextFrame(new Rect(0, i * (height + 20), 300, height)))
            .ToArray();
        for (var i = 0; i + 1 < frames; i++)
        {
            placed[i].ThreadTo(placed[i + 1]);
        }
        placed[0].Columns = columns;
        var style = document.Styles.AddParagraphStyle("Cell", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 8, Leading = 10 });
        if (intro)
        {
            placed[0].Story.AddParagraph("Intro", style);
        }
        var table = placed[0].AddTable(bodyRows + 2, 1, new TableOptions { ColumnWidths = [100], RowHeight = RowHeight, HeaderRows = 1, CellParagraphStyle = style, CellInset = 2 });
        table.Element.SetAttributeValue("FooterRowCount", "1");
        table.Element.SetAttributeValue("BodyRowCount", bodyRows.ToString(CultureInfo.InvariantCulture));
        table.SetRow(0, "H");
        for (var row = 1; row <= bodyRows; row++)
        {
            table.SetRow(row, "B" + row.ToString(CultureInfo.InvariantCulture));
        }
        table.SetRow(bodyRows + 1, "F");
        return (document, placed, table);
    }

    private static StoryLayout Layout(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story);

    /// <summary>The cell texts drawn in <paramref name="frame"/>, top to bottom (left column first).</summary>
    private static List<string> Drawn(StoryLayout layout, TextFrame frame) =>
        layout.OpsByFrame[frame.Id].OfType<GlyphRunOp>().OrderBy(run => run.X > frame.InnerBounds.X + 150).ThenBy(run => run.Y)
            .Select(run => run.Text).ToList();

    [Fact]
    public void Footer_rows_repeat_at_each_frame_break_and_their_room_is_kept_free()
    {
        // 150 pt: the header and six body rows would fit, but not with the footer below them.
        var (document, frames, _) = NewTable(bodyRows: 8, frames: 2, height: 150);
        var layout = Layout(document, frames[0]);

        Assert.False(layout.Overset);
        Assert.Equal(["H", "B1", "B2", "B3", "B4", "B5", "F"], Drawn(layout, frames[0]));
        Assert.Equal(["H", "B6", "B7", "B8", "F"], Drawn(layout, frames[1]));
        // The repeated footer closes the first part right below its last body row.
        var footer = layout.OpsByFrame[frames[0].Id].OfType<GlyphRunOp>().Single(run => run.Text == "F");
        var lastBody = layout.OpsByFrame[frames[0].Id].OfType<GlyphRunOp>().Single(run => run.Text == "B5");
        Assert.Equal(RowHeight, footer.Y - lastBody.Y, 6);
    }

    [Theory]
    [InlineData("NextColumn")]
    [InlineData("NextFrame")]
    public void A_row_with_start_row_begins_the_next_part(string start)
    {
        var (document, frames, table) = NewTable(bodyRows: 4, frames: 2, height: 150);
        table.Element.Elements("Row").ElementAt(3).SetAttributeValue("StartRow", start);
        var layout = Layout(document, frames[0]);

        Assert.Equal(["H", "B1", "B2", "F"], Drawn(layout, frames[0]));
        Assert.Equal(["H", "B3", "B4", "F"], Drawn(layout, frames[1]));
    }

    [Fact]
    public void Skip_first_header_and_skip_last_footer_leave_them_out_of_the_first_and_last_parts()
    {
        var (document, frames, table) = NewTable(bodyRows: 8, frames: 2, height: 150);
        table.Element.SetAttributeValue("SkipFirstHeader", "true");
        table.Element.SetAttributeValue("SkipLastFooter", "true");
        var layout = Layout(document, frames[0]);

        Assert.Equal(["B1", "B2", "B3", "B4", "B5", "B6", "F"], Drawn(layout, frames[0]));
        Assert.Equal(["H", "B7", "B8"], Drawn(layout, frames[1]));
    }

    [Fact]
    public void Once_per_frame_repeats_skip_the_column_breaks_inside_a_frame()
    {
        // Two 110 pt columns: the header and four body rows in the first (no footer kept free there, the table stays
        // in the frame), the rest and the footer in the second, without a header.
        var (document, frames, table) = NewTable(bodyRows: 8, frames: 1, height: 110, columns: 2);
        table.Element.SetAttributeValue("BreakHeaders", "OncePerTextFrame");
        table.Element.SetAttributeValue("BreakFooters", "OncePerTextFrame");
        var layout = Layout(document, frames[0]);

        Assert.False(layout.Overset);
        Assert.Equal(["H", "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8", "F"], Drawn(layout, frames[0]));
        var second = layout.OpsByFrame[frames[0].Id].OfType<GlyphRunOp>().Single(run => run.Text == "B5");
        Assert.True(second.X > frames[0].InnerBounds.X + 150, "B5 starts the second column");
    }

    [Fact]
    public void A_header_is_not_left_alone_at_the_bottom_of_a_frame()
    {
        // Below the intro, a 70 pt frame has room for the header and the body row (40 pt) but not with the footer.
        var (document, frames, _) = NewTable(bodyRows: 1, frames: 2, height: 70, intro: true);
        var layout = Layout(document, frames[0]);
        Assert.Equal(["Intro"], Drawn(layout, frames[0]));
        Assert.Equal(["H", "B1", "F"], Drawn(layout, frames[1]));

        (document, frames, _) = NewTable(bodyRows: 1, frames: 2, height: 90, intro: true);
        layout = Layout(document, frames[0]);
        Assert.Equal(["Intro", "H", "B1", "F"], Drawn(layout, frames[0]));
    }
}
