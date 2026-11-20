using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>Setup of a new table. Lengths in points.</summary>
public sealed record TableOptions
{
    /// <summary>Width of each column; defaults to the frame's inner width split evenly (100 pt each outside a frame).</summary>
    public double[]? ColumnWidths { get; init; }

    /// <summary>Minimum row height; rows grow to fit their text.</summary>
    public double RowHeight { get; init; } = 18;

    /// <summary>Rows repeated at the top of every frame the table flows into.</summary>
    public int HeaderRows { get; init; }

    public ParagraphStyle? CellParagraphStyle { get; init; }

    public ParagraphStyle? HeaderParagraphStyle { get; init; }

    public Swatch? HeaderFill { get; init; }

    /// <summary>
    /// Fill of every other body row, from the first (zebra stripes): the table's alternating row fills, one row
    /// filled and one not. Header rows are not counted.
    /// </summary>
    public Swatch? AlternateFill { get; init; }

    /// <summary>Colour of every cell edge; null keeps InDesign's default (black).</summary>
    public Swatch? Stroke { get; init; }

    /// <summary>Weight of every cell edge; null keeps InDesign's default (1 pt).</summary>
    public double? StrokeWeight { get; init; }

    /// <summary>Space between cell edges and text; null keeps InDesign's default (4 pt).</summary>
    public double? CellInset { get; init; }
}

/// <summary>A table, anchored in a story. Cells are addressed (row, column), 0-based.</summary>
public sealed class Table : IdmlNode
{
    internal Table(IdmlDocument document, XElement element)
        : base(document, element)
    {
    }

    public int ColumnCount => Element.Elements("Column").Count();

    public int RowCount => Element.Elements("Row").Count();

    /// <summary>Column widths in points (<c>SingleColumnWidth</c>).</summary>
    public IReadOnlyList<double> ColumnWidths =>
        Element.Elements("Column").Select(column => IdmlXml.ParseDouble(column.Attribute("SingleColumnWidth")?.Value) ?? 0).ToList();

    /// <summary>Stored row heights in points (<c>SingleRowHeight</c>); rows with AutoGrow may grow to fit their text.</summary>
    public IReadOnlyList<double> RowHeights =>
        Element.Elements("Row").Select(row => IdmlXml.ParseDouble(row.Attribute("SingleRowHeight")?.Value) ?? 0).ToList();

    public int FooterRowCount => int.TryParse(GetAttribute("FooterRowCount"), CultureInfo.InvariantCulture, out var value) ? value : 0;

    public int HeaderRowCount => int.TryParse(GetAttribute("HeaderRowCount"), CultureInfo.InvariantCulture, out var value) ? value : 0;

    public TableCell this[int row, int column] => Cell(row, column);

    public TableCell Cell(int row, int column)
    {
        var name = $"{column}:{row}";
        var cell = Element.Elements("Cell").FirstOrDefault(element => element.Attribute("Name")?.Value == name)
            ?? throw new ArgumentOutOfRangeException(nameof(row), $"No cell at row {row}, column {column}.");
        return new TableCell(Document, cell);
    }

    public IEnumerable<TableCell> Cells => Element.Elements("Cell").Select(cell => new TableCell(Document, cell));

    /// <summary>Fills a row with text, one value per column (extra values ignored).</summary>
    public Table SetRow(int row, params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        for (var column = 0; column < Math.Min(values.Length, ColumnCount); column++)
        {
            Cell(row, column).SetText(values[column]);
        }
        return this;
    }

    /// <summary>Appends a body row (copying the formatting of the last row) and returns its index.</summary>
    public int AddRow(params string[] values)
    {
        var rows = Element.Elements("Row").ToList();
        var index = rows.Count;
        var templateRow = rows.LastOrDefault();
        var row = templateRow is null
            ? new XElement("Row", new XAttribute("SingleRowHeight", "18"), new XAttribute("MinimumHeight", "18"))
            : new XElement(templateRow.Name, templateRow.Attributes());
        row.SetAttributeValue("Self", $"{Id}Row{index}");
        row.SetAttributeValue("Name", index.ToString(CultureInfo.InvariantCulture));
        if (templateRow is not null)
        {
            templateRow.AddAfterSelf(row);
        }
        else if (Element.Element("Column") is { } firstColumn)
        {
            firstColumn.AddBeforeSelf(row);
        }
        else
        {
            Element.Add(row);
        }

        for (var column = 0; column < ColumnCount; column++)
        {
            var template = templateRow is null ? null : Cell(index - 1, column).Element;
            var cell = template is null ? NewCell(Id, column, index, IdmlNames.BasicParagraphStyle) : CopyCell(template, column, index);
            Element.Add(cell);
        }
        var body = int.TryParse(GetAttribute("BodyRowCount"), CultureInfo.InvariantCulture, out var count) ? count : 0;
        SetAttribute("BodyRowCount", (body + 1).ToString(CultureInfo.InvariantCulture));
        SetRow(index, values);
        return index;
    }

    public void SetColumnWidth(int column, double width) =>
        ColumnElement(column).SetAttributeValue("SingleColumnWidth", IdmlXml.Format(width));

    public void SetRowHeight(int row, double height)
    {
        var element = Element.Elements("Row").ElementAtOrDefault(row) ?? throw new ArgumentOutOfRangeException(nameof(row));
        element.SetAttributeValue("SingleRowHeight", IdmlXml.Format(height));
        element.SetAttributeValue("MinimumHeight", IdmlXml.Format(height));
    }

    private XElement ColumnElement(int column) =>
        Element.Elements("Column").ElementAtOrDefault(column) ?? throw new ArgumentOutOfRangeException(nameof(column));

    internal static Table Create(IdmlDocument document, int rows, int columns, TableOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        var headerRows = Math.Clamp(options.HeaderRows, 0, rows);
        var id = document.NewId();
        var table = new XElement("Table",
            new XAttribute("Self", id),
            new XAttribute("HeaderRowCount", headerRows.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("FooterRowCount", "0"),
            new XAttribute("BodyRowCount", (rows - headerRows).ToString(CultureInfo.InvariantCulture)),
            new XAttribute("ColumnCount", columns.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("AppliedTableStyle", IdmlNames.BasicTableStyle),
            new XAttribute("TableDirection", "LeftToRightDirection"));
        if (options.AlternateFill is { } alternate)
        {
            table.SetAttributeValue("StartRowFillColor", alternate.Reference);
            table.SetAttributeValue("StartRowFillTint", "100");
            table.SetAttributeValue("StartRowFillCount", "1");
            table.SetAttributeValue("EndRowFillColor", Swatch.None.Reference);
            table.SetAttributeValue("EndRowFillCount", "1");
        }

        for (var row = 0; row < rows; row++)
        {
            table.Add(new XElement("Row",
                new XAttribute("Self", $"{id}Row{row}"),
                new XAttribute("Name", row.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("SingleRowHeight", IdmlXml.Format(options.RowHeight)),
                new XAttribute("MinimumHeight", IdmlXml.Format(options.RowHeight)),
                new XAttribute("AutoGrow", "true")));
        }
        for (var column = 0; column < columns; column++)
        {
            var width = options.ColumnWidths is { } widths && column < widths.Length ? widths[column] : 100;
            table.Add(new XElement("Column",
                new XAttribute("Self", $"{id}Column{column}"),
                new XAttribute("Name", column.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("SingleColumnWidth", IdmlXml.Format(width))));
        }
        // InDesign writes cells column by column; Name is "column:row".
        for (var column = 0; column < columns; column++)
        {
            for (var row = 0; row < rows; row++)
            {
                var isHeader = row < headerRows;
                var style = (isHeader ? options.HeaderParagraphStyle ?? options.CellParagraphStyle : options.CellParagraphStyle)?.Reference
                    ?? IdmlNames.BasicParagraphStyle;
                var cell = NewCell(id, column, row, style);
                if (isHeader && options.HeaderFill is { } fill)
                {
                    cell.SetAttributeValue("FillColor", fill.Reference);
                    cell.SetAttributeValue("FillTint", "100");
                }
                ApplyEdges(cell, options);
                table.Add(cell);
            }
        }
        return new Table(document, table);
    }

    private static XElement NewCell(string tableId, int column, int row, string paragraphStyle) =>
        new("Cell",
            new XAttribute("Self", $"{tableId}i{column}i{row}"),
            new XAttribute("Name", $"{column}:{row}"),
            new XAttribute("RowSpan", "1"),
            new XAttribute("ColumnSpan", "1"),
            new XAttribute("AppliedCellStyle", IdmlNames.NoCellStyle),
            new XAttribute("AppliedCellStylePriority", "0"),
            new XElement("ParagraphStyleRange",
                new XAttribute("AppliedParagraphStyle", paragraphStyle),
                new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle))));

    private XElement CopyCell(XElement template, int column, int row)
    {
        var cell = new XElement(template.Name, template.Attributes());
        cell.SetAttributeValue("Self", $"{Id}i{column}i{row}");
        cell.SetAttributeValue("Name", $"{column}:{row}");
        var range = template.Element("ParagraphStyleRange");
        var run = range?.Element("CharacterStyleRange");
        var newRange = range is null ? new XElement("ParagraphStyleRange", new XAttribute("AppliedParagraphStyle", IdmlNames.BasicParagraphStyle)) : TextOps.ShallowCopy(range);
        newRange.Add(run is null ? new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle)) : TextOps.ShallowCopy(run));
        cell.Add(newRange);
        return cell;
    }

    private static void ApplyEdges(XElement cell, TableOptions options)
    {
        foreach (var edge in (string[])["Top", "Left", "Bottom", "Right"])
        {
            if (options.StrokeWeight is { } weight)
            {
                cell.SetAttributeValue(edge + "EdgeStrokeWeight", IdmlXml.Format(weight));
            }
            if (options.Stroke is { } stroke)
            {
                cell.SetAttributeValue(edge + "EdgeStrokeColor", stroke.Reference);
            }
            if (options.CellInset is { } inset)
            {
                cell.SetAttributeValue(edge + "Inset", IdmlXml.Format(inset));
            }
        }
    }
}

/// <summary>A table cell; holds its own paragraphs.</summary>
public sealed class TableCell : IdmlNode
{
    internal TableCell(IdmlDocument document, XElement element)
        : base(document, element)
    {
    }

    public int Column => int.Parse(GetAttribute("Name")!.Split(':')[0], CultureInfo.InvariantCulture);

    public int Row => int.Parse(GetAttribute("Name")!.Split(':')[1], CultureInfo.InvariantCulture);

    public int RowSpan => int.TryParse(GetAttribute("RowSpan"), CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 1;

    public int ColumnSpan => int.TryParse(GetAttribute("ColumnSpan"), CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 1;

    public string Text => TextOps.GetText(Element);

    /// <summary>Replaces the cell text, keeping the formatting of its first paragraph and run.</summary>
    public TableCell SetText(string text, ParagraphStyle? style = null)
    {
        TextOps.SetText(Element, text, style?.Reference);
        return this;
    }

    public Paragraph AddParagraph(ParagraphStyle? style = null)
    {
        // An empty cell holds one empty paragraph; reuse it instead of leaving a blank first line.
        var ranges = Element.Elements("ParagraphStyleRange").ToList();
        if (ranges is [var only] && !only.Descendants("Content").Any() && !only.Descendants("Br").Any())
        {
            only.Elements("CharacterStyleRange").Where(run => !run.HasElements).Remove();
            if (style is not null)
            {
                only.SetAttributeValue("AppliedParagraphStyle", style.Reference);
            }
            return new Paragraph(Document, only);
        }
        return new Paragraph(Document, TextOps.AppendParagraph(Element, style?.Reference ?? IdmlNames.BasicParagraphStyle));
    }

    public TableCell SetFill(Swatch fill, double tint = 100)
    {
        ArgumentNullException.ThrowIfNull(fill);
        SetAttribute("FillColor", fill.Reference);
        SetAttribute("FillTint", IdmlXml.Format(tint));
        return this;
    }

    public TableCell SetVerticalJustification(VerticalJustification value)
    {
        SetAttribute("VerticalJustification", value == VerticalJustification.Justify ? "JustifyAlign" : value + "Align");
        return this;
    }
}
