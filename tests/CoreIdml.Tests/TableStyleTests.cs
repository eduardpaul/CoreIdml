using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Json;

namespace CoreIdml.Tests;

/// <summary>Table styles: region cell styles resolved for cells, alternating fills authored from the spec.</summary>
public sealed class TableStyleTests
{
    private static XElement CellStyle(string name, string fill) =>
        new("CellStyle", new XAttribute("Self", "CellStyle/" + name), new XAttribute("Name", name), new XAttribute("FillColor", fill));

    [Fact]
    public void Cells_without_a_cell_style_take_the_cell_style_of_their_table_style_region()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddTextFrame(new Rect(36, 36, 400, 300));
        var table = frame.AddTable(5, 3, new TableOptions { HeaderRows = 1 });
        table.Element.SetAttributeValue("FooterRowCount", "1");
        table.Element.SetAttributeValue("BodyRowCount", "3");
        table.Element.SetAttributeValue("AppliedTableStyle", "TableStyle/Regions");
        document.StylesRoot.Element("RootCellStyleGroup")!.Add(
            CellStyle("Head", "Color/H"), CellStyle("Foot", "Color/F"), CellStyle("Left", "Color/L"), CellStyle("Body", "Color/B"), CellStyle("Own", "Color/O"));
        // As in 09-style-table-cell-styles p2, except that the right column is the same as the body.
        document.StylesRoot.Element("RootTableStyleGroup")!.Add(new XElement("TableStyle",
            new XAttribute("Self", "TableStyle/Regions"), new XAttribute("Name", "Regions"),
            new XAttribute("HeaderRegionSameAsBodyRegion", "false"), new XAttribute("HeaderRegionCellStyle", "CellStyle/Head"),
            new XAttribute("FooterRegionSameAsBodyRegion", "false"), new XAttribute("FooterRegionCellStyle", "CellStyle/Foot"),
            new XAttribute("LeftColumnRegionSameAsBodyRegion", "false"), new XAttribute("LeftColumnRegionCellStyle", "CellStyle/Left"),
            new XAttribute("BodyRegionCellStyle", "CellStyle/Body"),
            new XElement("Properties", new XElement("BasedOn", new XAttribute("type", "string"), "$ID/[No table style]"))));
        table[2, 1].Element.SetAttributeValue("AppliedCellStyle", "CellStyle/Own");
        table[3, 1].Element.SetAttributeValue("FillColor", "Color/Local");
        var resolver = new AttributeResolver(document);
        string? Fill(int row, int column) => resolver.Cell(table[row, column].Element, "FillColor");

        Assert.Equal("Color/H", Fill(0, 0)); // header before the left column
        Assert.Equal("Color/H", Fill(0, 2));
        Assert.Equal("Color/L", Fill(1, 0));
        Assert.Equal("Color/B", Fill(1, 1));
        Assert.Equal("Color/B", Fill(1, 2)); // right column: same as the body ([No table style])
        Assert.Equal("Color/O", Fill(2, 1)); // the cell's own style
        Assert.Equal("Color/Local", Fill(3, 1)); // a local value
        Assert.Equal("Color/F", Fill(4, 0)); // footer before the left column
        Assert.Equal("CellStyle/Body", resolver.RegionCellStyle(table[1, 1].Element));
    }

    [Fact]
    public void Spec_alternate_fill_fills_every_other_body_row()
    {
        const string json = """
        {
          "colors": [ { "name": "Stripe", "cmyk": [0, 0, 0, 10] } ],
          "pages": [ { "items": [
            { "type": "table", "name": "prices", "x": 15, "y": 15, "width": 180, "height": 60, "headerRows": 1, "alternateFill": "Stripe",
              "rows": [ ["Item", "Price"], ["A", "1"], ["B", "2"], ["C", "3"] ] }
          ] } ]
        }
        """;

        using var document = IdmlJson.CreateDocument(json);
        var table = ((TextFrame)document.FindPageItem("prices")!).Story.Tables.Single();
        var resolver = new AttributeResolver(document);
        Assert.Equal("Color/Stripe", resolver.Table(table.Element, "StartRowFillColor"));
        Assert.Equal("1", resolver.Table(table.Element, "StartRowFillCount"));
        Assert.Equal("1", resolver.Table(table.Element, "EndRowFillCount"));
        Assert.Equal("Swatch/None", resolver.Table(table.Element, "EndRowFillColor"));
        CreateDocumentTests.AssertValid(document);
    }
}
