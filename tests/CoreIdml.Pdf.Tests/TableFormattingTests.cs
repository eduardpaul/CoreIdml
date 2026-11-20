using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>What a table draws in its cells: alternating row and column fills and strokes, and the table border.</summary>
public sealed class TableFormattingTests
{
    private const double RowHeight = 20;

    private static (IdmlDocument Document, TextFrame Frame, Table Table) NewTable(int rows, int columns, int headerRows = 0, int footerRows = 0)
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, 400));
        var table = frame.AddTable(rows, columns, new TableOptions { RowHeight = RowHeight, HeaderRows = headerRows, CellInset = 2 });
        table.Element.SetAttributeValue("FooterRowCount", footerRows.ToString(System.Globalization.CultureInfo.InvariantCulture));
        table.Element.SetAttributeValue("BodyRowCount", (rows - headerRows - footerRows).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (document, frame, table);
    }

    private static List<DrawOp> Ops(IdmlDocument document, TextFrame frame)
    {
        var renderer = new IdmlRenderer(document, TestSupport.ExportOptions());
        return renderer.LayoutStory(frame.Story).OpsByFrame[frame.Id];
    }

    private static void SetAll(Table table, params (string Name, object Value)[] attributes)
    {
        foreach (var (name, value) in attributes)
        {
            table.Element.SetAttributeValue(name, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The rows with a fill rectangle in the first column, by index.</summary>
    private static List<int> FilledRows(List<DrawOp> ops, TextFrame frame) =>
        ops.OfType<RectOp>().Where(r => Math.Abs(r.Rect.X - frame.InnerBounds.X) < 1e-6)
            .Select(r => (int)Math.Round((r.Rect.Y - frame.InnerBounds.Y) / RowHeight)).Order().ToList();

    [Fact]
    public void Alternating_row_fills_count_body_rows_only_and_skip_the_first_and_last()
    {
        var (document, frame, table) = NewTable(rows: 9, columns: 2, headerRows: 1, footerRows: 1);
        var zebra = document.Swatches.AddCmyk("Zebra", 0, 0, 100, 0);
        // 10-table-alternating p2: two rows filled, three not; one body row skipped at each end.
        SetAll(table, ("StartRowFillColor", zebra.Reference), ("StartRowFillTint", 50), ("StartRowFillCount", 2),
            ("EndRowFillCount", 1), ("SkipFirstAlternatingFillRows", 1), ("SkipLastAlternatingFillRows", 1));
        var ops = Ops(document, frame);

        // Header 0, body 1-7 (1 and 7 skipped), footer 8: rows 2, 3 filled, 4 not, 5, 6 filled.
        Assert.Equal([2, 3, 5, 6], FilledRows(ops, frame));
        var fill = ops.OfType<RectOp>().First().Color;
        Assert.Equal([0, 0, 0.5, 0], fill.Components);
    }

    [Fact]
    public void A_cell_fill_colour_takes_the_tint_of_its_alternating_row()
    {
        var (document, frame, table) = NewTable(rows: 3, columns: 2);
        var zebra = document.Swatches.AddCmyk("Zebra", 0, 0, 100, 0);
        var red = document.Swatches.AddCmyk("Red", 0, 100, 100, 0);
        SetAll(table, ("StartRowFillColor", zebra.Reference), ("StartRowFillTint", 50), ("StartRowFillCount", 1), ("EndRowFillCount", 1));
        // 10-table-alternating p6: the cell's own colour, the row's 50% tint.
        table[0, 1].Element.SetAttributeValue("FillColor", red.Reference);
        var ops = Ops(document, frame);

        var cell = Assert.Single(ops.OfType<RectOp>(), r => r.Rect.X > frame.InnerBounds.X && r.Rect.Y < frame.InnerBounds.Y + 1);
        Assert.Equal([0, 0.5, 0.5, 0], cell.Color.Components);
        Assert.Equal([0, 2], FilledRows(ops, frame));
    }

    [Fact]
    public void Column_fills_take_priority_over_row_fills_when_the_table_says_so()
    {
        var (document, frame, table) = NewTable(rows: 2, columns: 2);
        var rows = document.Swatches.AddCmyk("Rows", 0, 0, 100, 0);
        var columns = document.Swatches.AddCmyk("Columns", 100, 0, 0, 0);
        SetAll(table, ("StartRowFillColor", rows.Reference), ("StartRowFillTint", 100), ("StartRowFillCount", 1), ("EndRowFillCount", 1),
            ("StartColumnFillColor", columns.Reference), ("StartColumnFillTint", 100), ("StartColumnFillCount", 1), ("EndColumnFillCount", 1),
            ("SkipLastAlternatingFillColumns", 1),
            ("ColumnFillsPriority", "true"));
        var ops = Ops(document, frame);

        // Column 0 is cyan in both rows; column 1 (no column fill) shows the row pattern: yellow in row 0 only.
        var fills = ops.OfType<RectOp>().ToDictionary(r => (Row: (int)Math.Round((r.Rect.Y - frame.InnerBounds.Y) / RowHeight), Column: r.Rect.X > frame.InnerBounds.X ? 1 : 0), r => r.Color.Components[0]);
        Assert.Equal(3, fills.Count);
        Assert.Equal(1, fills[(0, 0)]);
        Assert.Equal(1, fills[(1, 0)]);
        Assert.Equal(0, fills[(0, 1)]);
    }

    [Fact]
    public void Alternating_row_strokes_are_the_rules_below_body_rows_inside_the_border()
    {
        var (document, frame, table) = NewTable(rows: 4, columns: 2, headerRows: 1);
        var red = document.Swatches.AddCmyk("Red", 0, 100, 100, 0);
        // 10-table-alternating p4: 1 pt red, then 0.25 pt black.
        SetAll(table, ("StartRowStrokeColor", red.Reference), ("StartRowStrokeWeight", 2), ("StartRowStrokeCount", 1), ("EndRowStrokeCount", 1));
        var ops = Ops(document, frame);

        var rules = ops.OfType<LineOp>().Where(l => Math.Abs(l.From.Y - l.To.Y) < 1e-6)
            .ToDictionary(l => (int)Math.Round((l.From.Y - frame.InnerBounds.Y) / RowHeight), l => l.Width);
        Assert.Equal(1, rules[0]); // table border
        Assert.Equal(1, rules[1]); // below the header: the header's rule
        Assert.Equal(2, rules[2]); // below body row 1: start
        Assert.Equal(0.25, rules[3]); // below body row 2: end ([No table style] default)
        Assert.Equal(1, rules[4]); // below body row 3: the table border
        Assert.Equal([0, 1, 1, 0], ops.OfType<LineOp>().Single(l => l.Width == 2).Color.Components);
    }

    [Fact]
    public void Alternating_column_strokes_are_the_rules_right_of_columns()
    {
        var (document, frame, table) = NewTable(rows: 2, columns: 4);
        SetAll(table, ("StartColumnStrokeWeight", 2), ("StartColumnStrokeCount", 1), ("EndColumnStrokeCount", 1));
        var ops = Ops(document, frame);

        var widths = table.ColumnWidths;
        var rules = ops.OfType<LineOp>().Where(l => Math.Abs(l.From.X - l.To.X) < 1e-6)
            .Select(l => (X: l.From.X - frame.InnerBounds.X, l.Width)).OrderBy(l => l.X).Select(l => l.Width).ToList();
        Assert.Equal(widths.Count + 1, rules.Count);
        Assert.Equal([1, 2, 0.25, 2, 1], rules);
    }

    [Fact]
    public void A_merged_cell_with_default_edges_draws_the_table_border_along_its_outer_side()
    {
        var (document, frame, table) = NewTable(rows: 3, columns: 2);
        // 10-table-alternating p7: no cell stores an edge weight; column 0 merged over rows 1 and 2.
        table[1, 0].Element.SetAttributeValue("RowSpan", "2");
        table[2, 0].Element.Remove();
        var ops = Ops(document, frame);

        var left = Assert.Single(ops.OfType<LineOp>(), l => Math.Abs(l.From.X - frame.InnerBounds.X) < 1e-6 && Math.Abs(l.To.X - frame.InnerBounds.X) < 1e-6);
        Assert.Equal(3 * RowHeight, Math.Abs(left.To.Y - left.From.Y), 6);
    }
}
