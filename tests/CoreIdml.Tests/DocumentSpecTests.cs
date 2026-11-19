using System.Xml.Linq;
using CoreIdml.Json;

namespace CoreIdml.Tests;

/// <summary>What a document spec reports about itself, and the defaults and fields an author would not guess.</summary>
public class DocumentSpecTests
{
    [Fact]
    public void A_clean_spec_has_no_warnings()
    {
        var spec = """
        {
          "unit": "mm", "page": { "size": "A4", "margins": { "all": 15 }, "bleed": 3 },
          "colors": [ { "name": "Sea", "cmyk": [90, 30, 20, 10] } ],
          "characterStyles": [ { "name": "Strong", "fontStyle": "Bold", "color": "Sea" } ],
          "paragraphStyles": [ { "name": "Body", "font": "Arial", "size": 10, "leading": 14, "align": "justify" } ],
          "pages": [ { "items": [
            { "type": "text", "name": "intro", "x": 15, "y": 15, "width": 180, "height": 30, "inset": { "all": 2 },
              "paragraphs": [ { "style": "Body", "runs": [ { "text": "Hi", "style": "Strong", "size": 12 } ] } ] },
            { "type": "table", "x": 15, "y": 50, "width": 180, "height": 30, "rows": [ ["a", "b"] ], "headerRows": 1 },
            { "type": "line", "x": 15, "y": 90, "x2": 195, "y2": 90, "endCap": "round", "strokeTint": 50 }
          ] } ]
        }
        """;

        Assert.Empty(IdmlJson.CheckSpec(spec));
        using var document = IdmlJson.CreateDocument(spec);
    }

    [Fact]
    public void Unknown_fields_are_reported_with_their_place_and_the_field_meant()
    {
        var warnings = IdmlJson.CheckSpec("""
        {
          "paragraphStyles": [ { "name": "Body", "fontSize": 10, "lineHeight": 14 } ],
          "pages": [ { "items": [
            { "type": "rectangle", "x": 0, "y": 0, "width": 10, "height": 10, "fillColor": "Black", "strok": "Black" },
            { "type": "text", "x": 0, "y": 0, "width": 10, "height": 10, "paragraphs": [ { "text": "x", "runs": [ { "text": "y", "fontWeight": "Bold" } ] } ] }
          ] } ],
          "swatches": []
        }
        """);

        Assert.Contains("paragraphStyles[0]: unknown field 'fontSize', ignored (did you mean 'size'?).", warnings);
        Assert.Contains("paragraphStyles[0]: unknown field 'lineHeight', ignored (did you mean 'leading'?).", warnings);
        Assert.Contains("pages[0].items[0]: unknown field 'fillColor', ignored (did you mean 'fill'?).", warnings);
        Assert.Contains("pages[0].items[0]: unknown field 'strok', ignored (did you mean 'stroke'?).", warnings);
        Assert.Contains("pages[0].items[1].paragraphs[0].runs[0]: unknown field 'fontWeight', ignored (did you mean 'fontStyle'?).", warnings);
        Assert.Contains("spec: unknown field 'swatches', ignored (did you mean 'colors'?).", warnings);
        Assert.Equal(6, warnings.Count);
    }

    [Fact]
    public void Fields_in_the_wrong_place_are_reported()
    {
        var warnings = IdmlJson.CheckSpec("""
        {
          "characterStyles": [ { "name": "Strong", "align": "center" } ],
          "pages": [ { "items": [
            { "type": "rectangle", "x": 0, "y": 0, "width": 10, "height": 10, "text": "x", "rows": [] },
            { "type": "text", "x": 0, "y": 0, "width": 10, "height": 10, "margins": 5, "inset": 3,
              "paragraphs": [ { "runs": [ { "text": "y", "leading": 20 } ] } ] },
            { "type": "text", "name": "next", "continueFrom": "first", "text": "lost" }
          ] } ],
          "page": { "margins": 12 }
        }
        """);

        Assert.Contains("characterStyles[0]: 'align' does not apply to a character style; it is ignored.", warnings);
        Assert.Contains("pages[0].items[0]: 'text' does not apply to a rectangle item; it is ignored.", warnings);
        Assert.Contains("pages[0].items[0]: 'rows' does not apply to a rectangle item; it is ignored.", warnings);
        Assert.Contains("pages[0].items[1]: unknown field 'margins', ignored.", warnings);
        Assert.Contains("pages[0].items[1].inset: give an object such as {\"all\": 3}, not a number; the number is ignored.", warnings);
        Assert.Contains("pages[0].items[1].paragraphs[0].runs[0]: 'leading' does not apply to a run (set it on the paragraph); it is ignored.", warnings);
        Assert.Contains("pages[0].items[2]: a frame with continueFrom shows the text of the frame it continues; its own text and paragraphs are ignored.", warnings);
        Assert.Contains("page.margins: give an object such as {\"all\": 12}, not a number; the number is ignored.", warnings);
    }

    [Fact]
    public void Wrong_shapes_of_json_are_reported()
    {
        Assert.Equal(["spec: expected an object, found an array; it is ignored."], IdmlJson.CheckSpec("[]"));
        Assert.Equal(["pages: expected an array, found an object; it is ignored."], IdmlJson.CheckSpec("""{ "pages": { "items": [] } }"""));
        Assert.Equal(["pages[0]: expected an object, found a string; it is ignored."], IdmlJson.CheckSpec("""{ "pages": [ "one" ] }"""));
        Assert.Empty(IdmlJson.CheckSpec(null));
        Assert.Empty(IdmlJson.CheckSpec("""{ /* comments */ "unit": "mm", }"""));
    }

    [Fact]
    public void Spec_shapes_have_no_outline_unless_they_ask_for_one()
    {
        using var document = IdmlJson.CreateDocument("""
        {
          "pages": [ { "items": [
            { "type": "rectangle", "name": "plain", "x": 0, "y": 0, "width": 10, "height": 10, "fill": "Black" },
            { "type": "ellipse", "name": "round", "x": 0, "y": 0, "width": 10, "height": 10 },
            { "type": "rectangle", "name": "framed", "x": 0, "y": 0, "width": 10, "height": 10, "stroke": "Black", "strokeWeight": 2 },
            { "type": "line", "name": "rule", "x": 0, "y": 0, "x2": 10, "y2": 0 }
          ] } ]
        }
        """);

        Assert.Equal("Swatch/None", Item(document, "plain").Attribute("StrokeColor")?.Value);
        Assert.Equal("Swatch/None", Item(document, "round").Attribute("StrokeColor")?.Value);
        Assert.Equal("Color/Black", Item(document, "framed").Attribute("StrokeColor")?.Value);
        Assert.Equal("Color/Black", Item(document, "rule").Attribute("StrokeColor")?.Value);
    }

    [Fact]
    public void Stroke_tint_type_end_caps_and_opacity_are_written()
    {
        using var document = IdmlJson.CreateDocument("""
        {
          "pages": [ { "items": [
            { "type": "line", "name": "rule", "x": 0, "y": 0, "x2": 100, "y2": 0, "strokeWeight": 4, "strokeTint": 40, "strokeType": "dashed", "endCap": "round", "opacity": 50 },
            { "type": "rectangle", "name": "box", "x": 0, "y": 0, "width": 10, "height": 10, "fill": "Black", "opacity": 25 }
          ] } ]
        }
        """);

        var rule = Item(document, "rule");
        Assert.Equal("40", rule.Attribute("StrokeTint")?.Value);
        Assert.Equal("StrokeStyle/$ID/Canned Dashed 3x2", rule.Attribute("StrokeType")?.Value);
        Assert.Equal("RoundEndCap", rule.Attribute("EndCap")?.Value);
        Assert.Equal("50", rule.Element("TransparencySetting")?.Element("BlendingSetting")?.Attribute("Opacity")?.Value);
        var box = Item(document, "box");
        Assert.Equal("25", box.Element("TransparencySetting")?.Element("BlendingSetting")?.Attribute("Opacity")?.Value);
        Assert.Equal("Properties", box.Element("TransparencySetting")?.ElementsBeforeSelf().Last().Name.LocalName);
    }

    [Theory]
    [InlineData("""{ "type": "image", "path": "x.png", "fit": "cover" }""", "Unknown fit 'cover'. Use fill, fit, stretch, center, original.")]
    [InlineData("""{ "type": "text", "verticalAlign": "middle" }""", "Unknown verticalAlign 'middle'. Use top, center, bottom, justify.")]
    [InlineData("""{ "type": "line", "endCap": "square" }""", "Unknown endCap 'square'. Use butt, round, projecting.")]
    [InlineData("""{ "type": "line", "strokeType": "wavy" }""", "Unknown strokeType 'wavy'. Use solid, dashed, dotted.")]
    [InlineData("""{ "type": "text", "paragraphs": [ { "text": "x", "caps": "upper" } ] }""", "Unknown caps 'upper'. Use normal, smallCaps, allCaps, capToSmallCap.")]
    public void Unknown_values_name_the_ones_allowed(string item, string message)
    {
        var exception = Assert.Throws<IdmlException>(() => IdmlJson.CreateDocument($$"""{ "pages": [ { "items": [ {{item}} ] } ] }"""));

        Assert.Equal(message, exception.Message);
    }

    private static XElement Item(IdmlDocument document, string name) =>
        document.AllPageItems.Single(item => item.Name == name).Element;
}
