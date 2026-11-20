using System.Xml.Linq;
using CoreIdml.Dom;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// The fills and strokes a table gives its cells where neither the cell nor its cell style (its own, or the one its
/// table style gives its region) sets them: alternating row and column fills and strokes, and the table border. Each
/// value is looked up attribute by attribute, set on the table or coming from its table style: a cell whose own fill
/// colour is red in a row of a 50% alternating fill is drawn red at 50% (10-table-alternating p6).
/// </summary>
/// <remarks>
/// <para>
/// Alternating patterns run over the body rows: header and footer rows are not counted (10-table-alternating p1).
/// After <c>SkipFirstAlternatingFillRows</c> rows, <c>StartRowFillCount</c> rows take the start fill, then
/// <c>EndRowFillCount</c> rows the end fill, and so on until the last <c>SkipLastAlternatingFillRows</c> rows (p2).
/// Columns alternate the same way, from the first column (p3); this applies the column patterns to body rows only,
/// as for the row patterns (not covered by the samples). <c>ColumnFillsPriority</c> picks the column fill where both
/// a row and a column fill apply. A merged cell takes the fill of its first row and column (p7).
/// </para>
/// <para>
/// An alternating stroke is the rule below a row (right of a column) inside the table: the rule between the header
/// and the first body row stays the header's, and the outer edges are the table border (p4, p5).
/// </para>
/// </remarks>
internal sealed class TableFormatting
{
    private readonly XElement table;
    private readonly AttributeResolver resolver;
    private readonly int rows;
    private readonly int columns;
    private readonly Pattern rowFills;
    private readonly Pattern columnFills;
    private readonly Pattern rowStrokes;
    private readonly Pattern columnStrokes;
    private readonly bool columnFillsFirst;
    private readonly int bodyStart;
    private readonly int bodyEnd;

    public TableFormatting(XElement table, AttributeResolver resolver, int rows, int columns)
    {
        this.table = table;
        this.resolver = resolver;
        this.rows = rows;
        this.columns = columns;
        var header = Math.Min(rows, Count("HeaderRowCount"));
        var footer = Math.Min(rows - header, Count("FooterRowCount"));
        var body = rows - header - footer;
        rowFills = new Pattern("RowFill", Count("StartRowFillCount"), Count("EndRowFillCount"),
            Count("SkipFirstAlternatingFillRows"), Count("SkipLastAlternatingFillRows"), header, body);
        columnFills = new Pattern("ColumnFill", Count("StartColumnFillCount"), Count("EndColumnFillCount"),
            Count("SkipFirstAlternatingFillColumns"), Count("SkipLastAlternatingFillColumns"), 0, columns);
        rowStrokes = new Pattern("RowStroke", Count("StartRowStrokeCount"), Count("EndRowStrokeCount"),
            Count("SkipFirstAlternatingStrokeRows"), Count("SkipLastAlternatingStrokeRows"), header, body);
        columnStrokes = new Pattern("ColumnStroke", Count("StartColumnStrokeCount"), Count("EndColumnStrokeCount"),
            Count("SkipFirstAlternatingStrokeColumns"), Count("SkipLastAlternatingStrokeColumns"), 0, columns);
        columnFillsFirst = Value("ColumnFillsPriority") == "true";
        (bodyStart, bodyEnd) = (header, header + body);
    }

    /// <summary>
    /// The table's source of the fill of a cell starting at (<paramref name="row"/>, <paramref name="column"/>):
    /// an attribute prefix such as <c>StartRowFill</c> (then <c>Color</c>, <c>Tint</c>), or null.
    /// </summary>
    public string? FillSource(int row, int column)
    {
        if (row < bodyStart || row >= bodyEnd)
        {
            return null;
        }
        var byRow = rowFills.At(row);
        var byColumn = columnFills.At(column);
        return columnFillsFirst ? byColumn ?? byRow : byRow ?? byColumn;
    }

    /// <summary>
    /// The table's source of one edge of a cell: the table border on the outside (<c>TopBorderStroke</c>, ...), the
    /// alternating stroke of the rule inside (<c>StartRowStroke</c>, ...), or null. Then <c>Weight</c>, <c>Color</c>,
    /// <c>Tint</c>.
    /// </summary>
    public string? EdgeSource(string side, int row, int column, int rowSpan, int columnSpan)
    {
        var lastRow = row + rowSpan - 1;
        var lastColumn = column + columnSpan - 1;
        var body = row >= bodyStart && row < bodyEnd;
        return side switch
        {
            "Top" => row == 0 ? "TopBorderStroke" : rowStrokes.At(row - 1),
            "Bottom" => lastRow >= rows - 1 ? "BottomBorderStroke" : rowStrokes.At(lastRow),
            "Left" => column == 0 ? "LeftBorderStroke" : body ? columnStrokes.At(column - 1) : null,
            _ => lastColumn >= columns - 1 ? "RightBorderStroke" : body ? columnStrokes.At(lastColumn) : null,
        };
    }

    /// <summary>Whether an edge of a cell is on the outside of the table.</summary>
    public bool IsOuter(string side, int row, int column, int rowSpan, int columnSpan) => side switch
    {
        "Top" => row == 0,
        "Bottom" => row + rowSpan >= rows,
        "Left" => column == 0,
        _ => column + columnSpan >= columns,
    };

    /// <summary>The table's (or its table style's) value of <paramref name="source"/> + <paramref name="attribute"/>.</summary>
    public string? Value(string? source, string attribute) => source is null ? null : Value(source + attribute);

    private string? Value(string name) => resolver.Table(table, name);

    private int Count(string name) => Math.Max(0, (int)Values.Number(Value(name), 0));

    /// <summary>
    /// One alternating pattern over <paramref name="Length"/> rows or columns from <paramref name="First"/>:
    /// <c>Start</c> + <paramref name="Kind"/> for the first <paramref name="StartCount"/> of every cycle, <c>End</c>
    /// + <paramref name="Kind"/> for the next <paramref name="EndCount"/>, none in the skipped ones at either end.
    /// </summary>
    private readonly record struct Pattern(string Kind, int StartCount, int EndCount, int SkipFirst, int SkipLast, int First, int Length)
    {
        public string? At(int index)
        {
            var position = index - First - SkipFirst;
            if (StartCount + EndCount == 0 || position < 0 || index - First >= Length - SkipLast)
            {
                return null;
            }
            return (position % (StartCount + EndCount) < StartCount ? "Start" : "End") + Kind;
        }
    }
}
