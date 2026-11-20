using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Json;
using CoreIdml.Packaging;

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

    [Fact]
    public void Every_error_of_a_spec_is_reported_at_once()
    {
        var exception = Assert.Throws<IdmlException>(() => IdmlJson.CreateDocument("""
            { "page": { "size": "B9" },
              "colors": [ { "name": "Brand" } ],
              "paragraphStyles": [ { "name": "Body", "basedOn": "Nope", "align": "middle" } ],
              "pages": [ { "items": [
                { "type": "rectangle", "fill": "Accent" },
                { "type": "image", "fit": "cover" },
                { "type": "text", "text": "x", "style": "Body", "continueFrom": "missing" },
                { "type": "star" }
              ] } ] }
            """));

        Assert.Equal(
            """
            The spec has 9 errors; nothing was created:
            - page: Unknown page size 'B9'. Use A3, A4, A5, Letter, Legal or width/height.
            - colors[0]: Colour 'Brand' needs cmyk, rgb or hex.
            - paragraphStyles[0]: Unknown paragraph style 'Nope'.
            - paragraphStyles[0]: Unknown align 'middle'. Use left, center, right, justify or justifyAll.
            - pages[0].items[0]: Unknown swatch 'Accent'. Declare it in colors first.
            - pages[0].items[1]: Image items need a path.
            - pages[0].items[1]: Unknown fit 'cover'. Use fill, fit, stretch, center, original.
            - pages[0].items[2]: continueFrom: no text frame named 'missing'.
            - pages[0].items[3]: Unknown item type 'star'. Use text, rectangle, ellipse, line, image or table.
            """.ReplaceLineEndings("\n"),
            exception.Message);
    }

    [Fact]
    public void A_spec_with_errors_leaves_the_document_as_it_was()
    {
        using var document = IdmlJson.CreateDocument("""{ "pages": [ { "items": [ { "type": "text", "name": "first", "text": "x" } ] } ] }""");
        var before = document.ToArray(new IdmlSaveOptions { Timestamp = DateTimeOffset.UnixEpoch });

        var exception = Assert.Throws<IdmlException>(() => IdmlJson.Apply(document, """
            { "colors": [ { "name": "Brand", "hex": "#123456" } ],
              "pages": [ { "items": [ { "type": "rectangle", "fill": "Brand" }, { "type": "rectangle", "fill": "Accent" }, { "type": "text", "continueFrom": "first", "stroke": "Nope" } ] } ] }
            """));

        Assert.Equal(IdmlErrorKind.NotFound, exception.Kind);
        Assert.StartsWith("The spec has 2 errors; nothing was changed:", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, document.ToArray(new IdmlSaveOptions { Timestamp = DateTimeOffset.UnixEpoch }));
    }

    [Fact]
    public void Names_declared_earlier_in_the_spec_are_not_errors()
    {
        using var document = IdmlJson.CreateDocument("""
            { "colors": [ { "name": "Brand", "rgb": [10, 20, 30] } ],
              "characterStyles": [ { "name": "Strong", "fontStyle": "Bold" } ],
              "paragraphStyles": [ { "name": "Base", "color": "Brand" }, { "name": "Body", "basedOn": "Base" } ],
              "pages": [ { "items": [
                { "type": "text", "name": "a", "paragraphs": [ { "style": "Body", "runs": [ { "text": "x", "style": "Strong" } ] } ] },
                { "type": "text", "continueFrom": "a" }
              ] } ] }
            """);

        Assert.Equal(2, document.AllPageItems.Count());
    }

    [Fact]
    public void List_fields_write_InDesigns_bullets_and_numbering_attributes()
    {
        var spec = """
        {
          "paragraphStyles": [
            { "name": "Bullet", "list": "bullet", "bullet": "–", "bulletAfter": "^m", "leftIndent": 14, "firstLineIndent": -14 },
            { "name": "Step", "list": "numbered", "numberFormat": "a", "numberText": "^#)\t", "listLevel": 2, "startAt": 3 }
          ],
          "pages": [ { "items": [ { "type": "text", "name": "list", "x": 0, "y": 0, "width": 200, "height": 200, "paragraphs": [
            { "text": "One", "style": "Bullet" },
            { "text": "Two", "style": "Step", "startAt": 5 },
            { "text": "Plain", "style": "Bullet", "list": "none" }
          ] } ] } ]
        }
        """;
        Assert.Empty(IdmlJson.CheckSpec(spec));
        using var document = IdmlJson.CreateDocument(spec);

        var bullet = document.Styles.FindParagraphStyle("Bullet")!.Element;
        Assert.Equal("BulletList", bullet.Attribute("BulletsAndNumberingListType")?.Value);
        Assert.Equal("^m", bullet.Attribute("BulletsTextAfter")?.Value);
        var bulletChar = bullet.Element("Properties")?.Element("BulletChar");
        Assert.Equal("UnicodeOnly", bulletChar?.Attribute("BulletCharacterType")?.Value);
        Assert.Equal("8211", bulletChar?.Attribute("BulletCharacterValue")?.Value);

        var step = document.Styles.FindParagraphStyle("Step")!.Element;
        Assert.Equal("NumberedList", step.Attribute("BulletsAndNumberingListType")?.Value);
        Assert.Equal("a, b, c, d...", step.Element("Properties")?.Element("NumberingFormat")?.Value);
        Assert.Equal("^#)^t", step.Attribute("NumberingExpression")?.Value);
        Assert.Equal("2", step.Attribute("NumberingLevel")?.Value);
        Assert.Equal("3", step.Attribute("NumberingStartAt")?.Value);
        // A style's startAt only sets where lists start; restarting would give every paragraph the same number.
        Assert.Null(step.Attribute("NumberingContinue"));

        var story = ((TextFrame)document.AllPageItems.Single(item => item.Name == "list")).Story;
        var ranges = story.Element.Descendants("ParagraphStyleRange").ToList();
        Assert.Equal("false", ranges[1].Attribute("NumberingContinue")?.Value);
        Assert.Equal("5", ranges[1].Attribute("NumberingStartAt")?.Value);
        Assert.Equal("NoList", ranges[2].Attribute("BulletsAndNumberingListType")?.Value);
        Assert.Equal(["bullet", "numbered", null], story.Paragraphs().Select(paragraph => paragraph.List));
    }

    [Fact]
    public void Tabs_write_InDesigns_tab_list_and_a_paragraphs_tabs_replace_its_styles()
    {
        var spec = """
        {
          "paragraphStyles": [ { "name": "Contents", "tabs": [ { "position": 400, "align": "right", "leader": "." }, { "position": 20 } ] } ],
          "pages": [ { "items": [ { "type": "text", "name": "toc", "x": 0, "y": 0, "width": 420, "height": 200, "paragraphs": [
            { "text": "Chapter 1\t1", "style": "Contents" },
            { "text": "Total\t4,000.50", "style": "Contents", "tabs": [ { "position": 300, "align": "decimal", "alignOn": "," } ] }
          ] } ] } ]
        }
        """;
        Assert.Empty(IdmlJson.CheckSpec(spec));
        using var document = IdmlJson.CreateDocument(spec);

        var items = document.Styles.FindParagraphStyle("Contents")!.Element.Element("Properties")!.Element("TabList")!.Elements("ListItem").ToList();
        Assert.Equal(["20", "400"], items.Select(item => item.Element("Position")!.Value));
        Assert.Equal(["LeftAlign", "RightAlign"], items.Select(item => item.Element("Alignment")!.Value));
        Assert.Equal(["", "."], items.Select(item => item.Element("Leader")!.Value));
        var story = ((TextFrame)document.AllPageItems.Single(item => item.Name == "toc")).Story;
        var local = story.Element.Descendants("ParagraphStyleRange").ElementAt(1).Element("Properties")!.Element("TabList")!.Elements("ListItem").Single();
        Assert.Equal("CharacterAlign", local.Element("Alignment")!.Value);
        Assert.Equal(",", local.Element("AlignmentCharacter")!.Value);
    }

    [Fact]
    public void Tab_fields_are_checked()
    {
        var warnings = IdmlJson.CheckSpec("""
            { "paragraphStyles": [ { "name": "T", "tabStops": [], "tabs": [ { "pos": 10, "align": "right", "leaderChar": "." }, { "align": "left" } ] } ] }
            """);

        Assert.Contains("paragraphStyles[0]: unknown field 'tabStops', ignored (did you mean 'tabs'?).", warnings);
        Assert.Contains("paragraphStyles[0].tabs[0]: unknown field 'pos', ignored (did you mean 'position'?).", warnings);
        Assert.Contains("paragraphStyles[0].tabs[0]: unknown field 'leaderChar', ignored (did you mean 'leader'?).", warnings);
        Assert.Contains("paragraphStyles[0].tabs[1]: a tab stop needs a position; 0 is used.", warnings);
    }

    [Theory]
    [InlineData("""{ "tabs": [ { "position": 10, "align": "middle" } ] }""", "Unknown tab align 'middle'. Use left, center, right or decimal.")]
    [InlineData("""{ "tabs": [ { "position": -5 } ] }""", "Tab position -5 is negative.")]
    [InlineData("""{ "tabs": [ { "position": 5, "align": "decimal", "alignOn": ".," } ] }""", "Tab alignOn '.,' must be one character.")]
    [InlineData("""{ "tabs": [ { "position": 5, "leader": "123456789" } ] }""", "Tab leader '123456789' is longer than InDesign's 8 characters.")]
    [InlineData("""{ "list": "dots" }""", "Unknown list 'dots'. Use bullet, numbered or none.")]
    [InlineData("""{ "list": "numbered", "numberFormat": "roman" }""", "Unknown numberFormat 'roman'. Use 1, 01, 001, 0001, a, A, i or I.")]
    [InlineData("""{ "list": "numbered", "listLevel": 10 }""", "listLevel 10 is out of range. Use 1 to 9.")]
    [InlineData("""{ "list": "bullet", "bullet": "->" }""", "bullet '->' must be one character.")]
    public void Unknown_list_values_name_the_ones_allowed(string fields, string message)
    {
        var exception = Assert.Throws<IdmlException>(() => IdmlJson.CreateDocument($$"""{ "paragraphStyles": [ { "name": "L", {{fields[1..^1]}} } ] }"""));

        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void List_fields_on_a_run_are_reported_as_misplaced()
    {
        var warnings = IdmlJson.CheckSpec("""
            { "pages": [ { "items": [ { "type": "text", "paragraphs": [ { "runs": [ { "text": "x", "list": "bullet" } ] } ] } ] } ],
              "paragraphStyles": [ { "name": "L", "listType": "bullet" } ] }
            """);

        Assert.Contains("pages[0].items[0].paragraphs[0].runs[0]: 'list' does not apply to a run (set it on the paragraph); it is ignored.", warnings);
        Assert.Contains("paragraphStyles[0]: unknown field 'listType', ignored (did you mean 'list'?).", warnings);
    }

    private static XElement Item(IdmlDocument document, string name) =>
        document.AllPageItems.Single(item => item.Name == name).Element;
}
