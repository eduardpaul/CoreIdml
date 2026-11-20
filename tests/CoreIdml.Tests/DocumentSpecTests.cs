using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Json;
using CoreIdml.Packaging;
using CoreIdml.Xml;

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
    public void Keep_options_write_InDesigns_attributes()
    {
        var spec = """
        {
          "paragraphStyles": [
            { "name": "Heading", "keepWithNext": 1, "keepTogether": true, "startParagraph": "nextOddPage" },
            { "name": "Body", "keepFirstLines": 3, "keepLastLines": 2 }
          ]
        }
        """;
        Assert.Empty(IdmlJson.CheckSpec(spec));
        using var document = IdmlJson.CreateDocument(spec);

        var heading = document.Styles.FindParagraphStyle("Heading")!.Element;
        Assert.Equal("1", heading.Attribute("KeepWithNext")!.Value);
        Assert.Equal("true", heading.Attribute("KeepLinesTogether")!.Value);
        Assert.Equal("true", heading.Attribute("KeepAllLinesTogether")!.Value);
        Assert.Equal("NextOddPage", heading.Attribute("StartParagraph")!.Value);
        var body = document.Styles.FindParagraphStyle("Body")!.Element;
        // First and last lines only count with Keep Lines Together on.
        Assert.Equal("true", body.Attribute("KeepLinesTogether")!.Value);
        Assert.Null(body.Attribute("KeepAllLinesTogether"));
        Assert.Equal("3", body.Attribute("KeepFirstLines")!.Value);
        Assert.Equal("2", body.Attribute("KeepLastLines")!.Value);
        Assert.Contains("paragraphStyles[0]: unknown field 'widows', ignored (did you mean 'keepLastLines'?).",
            IdmlJson.CheckSpec("""{ "paragraphStyles": [ { "name": "H", "widows": 2 } ] }"""));
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
    [InlineData("""{ "keepWithNext": 6 }""", "keepWithNext 6 is out of range. Use 0 to 5 lines.")]
    [InlineData("""{ "keepLastLines": 0 }""", "keepLastLines 0 is out of range. Use 1 to 50.")]
    [InlineData("""{ "startParagraph": "nextChapter" }""", "Unknown startParagraph 'nextChapter'. Use anywhere, nextColumn, nextFrame, nextPage, nextOddPage or nextEvenPage.")]
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

    [Fact]
    public void Drop_caps_nested_and_grep_styles_are_written_as_InDesign_stores_them()
    {
        var spec = """
        {
          "characterStyles": [ { "name": "Drop", "color": "Black" }, { "name": "Lead", "fontStyle": "Bold" }, { "name": "Num", "fontStyle": "Italic" } ],
          "paragraphStyles": [
            { "name": "Opener", "dropCapLines": 3, "dropCapStyle": "Drop",
              "nestedStyles": [ { "style": "Lead", "through": "words", "count": 3 }, { "style": "Num", "upTo": ":" } ],
              "grepStyles": [ { "style": "Num", "expression": "\\d+" } ] }
          ],
          "pages": [ { "items": [ { "type": "text", "name": "t", "paragraphs": [ { "text": "x", "style": "Opener", "nestedStyles": [] } ] } ] } ]
        }
        """;
        Assert.Empty(IdmlJson.CheckSpec(spec));
        using var document = IdmlJson.CreateDocument(spec);

        // 08-para-drop-caps and 08-para-nested-grep-styles: the drop cap's character style is a first nested style
        // "through 1 Dropcap"; the lists are typed records with their Empty... flag false.
        var opener = document.Styles.FindParagraphStyle("Opener")!.Element;
        Assert.Equal("3", opener.Attribute("DropCapLines")!.Value);
        Assert.Equal("1", opener.Attribute("DropCapCharacters")!.Value);
        Assert.Equal("false", opener.Attribute("EmptyNestedStyles")!.Value);
        Assert.Equal("false", opener.Attribute("EmptyGrepStyles")!.Value);
        var nested = opener.Element("Properties")!.Element("AllNestedStyles")!;
        Assert.Equal("list", nested.Attribute("type")!.Value);
        Assert.Equal(
            [
                "CharacterStyle/Drop|enumeration:Dropcap|1|true",
                "CharacterStyle/Lead|enumeration:AnyWord|3|true",
                "CharacterStyle/Num|string::|1|false",
            ],
            nested.Elements("ListItem").Select(item =>
                $"{item.Element("AppliedCharacterStyle")!.Value}|{item.Element("Delimiter")!.Attribute("type")!.Value}:{item.Element("Delimiter")!.Value}|{item.Element("Repetition")!.Value}|{item.Element("Inclusive")!.Value}"));
        Assert.Equal(["record"], nested.Elements("ListItem").Select(item => item.Attribute("type")!.Value).Distinct());
        Assert.Equal(["object", "long", "boolean"], ((string[])["AppliedCharacterStyle", "Repetition", "Inclusive"]).Select(name => nested.Element("ListItem")!.Element(name)!.Attribute("type")!.Value));
        var grep = opener.Element("Properties")!.Element("AllGREPStyles")!.Element("ListItem")!;
        Assert.Equal("CharacterStyle/Num", grep.Element("AppliedCharacterStyle")!.Value);
        Assert.Equal(@"\d+", grep.Element("GrepExpression")!.Value);
        Assert.Equal("string", grep.Element("GrepExpression")!.Attribute("type")!.Value);
        // A paragraph's empty list turns the style's nested styles off for it, as InDesign marks an empty list.
        var range = ((TextFrame)document.AllPageItems.Single(item => item.Name == "t")).Story.Element.Descendants("ParagraphStyleRange").Single();
        Assert.Equal("true", range.Attribute("EmptyNestedStyles")!.Value);
        Assert.Null(range.Element("Properties")?.Element("AllNestedStyles"));
    }

    [Fact]
    public void A_drop_cap_style_given_later_keeps_the_nested_styles()
    {
        using var document = IdmlJson.CreateDocument("""
            { "characterStyles": [ { "name": "Drop" }, { "name": "Lead" } ],
              "paragraphStyles": [ { "name": "P", "nestedStyles": [ { "style": "Lead", "through": "." } ] } ] }
            """);
        IdmlJson.Apply(document, """{ "paragraphStyles": [ { "name": "P", "dropCapLines": 2, "dropCapStyle": "Drop" } ] }""");

        var items = document.Styles.FindParagraphStyle("P")!.Element.Element("Properties")!.Element("AllNestedStyles")!.Elements("ListItem");
        Assert.Equal(["CharacterStyle/Drop", "CharacterStyle/Lead"], items.Select(item => item.Element("AppliedCharacterStyle")!.Value));
        Assert.Equal(["Dropcap", "."], items.Select(item => item.Element("Delimiter")!.Value));
    }

    [Fact]
    public void Grid_alignment_column_spans_and_the_baseline_grid_are_written_as_InDesign_stores_them()
    {
        var spec = """
        {
          "unit": "mm",
          "page": { "baselineGrid": { "start": 20, "increment": 14, "relativeTo": "topMargin" } },
          "paragraphStyles": [
            { "name": "Body", "alignToBaselineGrid": true, "gridFirstLineOnly": true },
            { "name": "Head", "spanColumns": 0 },
            { "name": "Two", "spanColumns": 2 },
            { "name": "Split", "splitColumns": 3, "splitGutter": 8 },
            { "name": "Free", "alignToBaselineGrid": false, "spanColumns": 1 }
          ]
        }
        """;
        Assert.Empty(IdmlJson.CheckSpec(spec));
        using var document = IdmlJson.CreateDocument(spec);

        // 06-frame-baseline-grid: GridPreference in Preferences.xml, GridAlignment on the paragraph style.
        Assert.Equal(new BaselineGridOptions(Units.FromUnit(20, "mm"), 14, RelativeToTopMargin: true), document.BaselineGrid);
        XElement Style(string name) => document.Styles.FindParagraphStyle(name)!.Element;
        Assert.Equal("AlignBaseline", Style("Body").Attribute("GridAlignment")!.Value);
        Assert.Equal("true", Style("Body").Attribute("GridAlignFirstLineOnly")!.Value);
        Assert.Equal("None", Style("Free").Attribute("GridAlignment")!.Value);
        // 06-frame-span-balance: SpanColumnType attribute, SpanSplitColumnCount in Properties ("All" or a short).
        string Count(string name) => $"{Style(name).Element("Properties")!.Element("SpanSplitColumnCount")!.Attribute("type")!.Value}:{Style(name).Element("Properties")!.Element("SpanSplitColumnCount")!.Value}";
        Assert.Equal("SpanColumns", Style("Head").Attribute("SpanColumnType")!.Value);
        Assert.Equal("enumeration:All", Count("Head"));
        Assert.Equal("short:2", Count("Two"));
        Assert.Equal("SplitColumns", Style("Split").Attribute("SpanColumnType")!.Value);
        Assert.Equal("short:3", Count("Split"));
        Assert.Equal("8", Style("Split").Attribute("SplitColumnInsideGutter")!.Value);
        Assert.Equal("SingleColumn", Style("Free").Attribute("SpanColumnType")!.Value);
    }

    [Theory]
    [InlineData("""{ "dropCapLines": 30 }""", "dropCapLines 30 is out of range. Use 0 (none) to 25.")]
    [InlineData("""{ "nestedStyles": [ { "style": "C", "through": "paragraphs" } ] }""", "Unknown nested style delimiter 'paragraphs'. Use words, characters, sentences, letters, digits, tabs, lineBreak, endNestedStyle or one character such as \":\".")]
    [InlineData("""{ "nestedStyles": [ { "style": "C", "through": "words", "upTo": "." } ] }""", "Nested style 'C' needs through or upTo (one of them).")]
    [InlineData("""{ "nestedStyles": [ { "style": "C", "through": "words", "count": 0 } ] }""", "Nested style count 0 is out of range. Use 1 or more.")]
    [InlineData("""{ "grepStyles": [ { "style": "C" } ] }""", "GREP style 'C' needs an expression.")]
    [InlineData("""{ "spanColumns": 2, "splitColumns": 2 }""", "A paragraph either spans columns (spanColumns) or splits its column (splitColumns), not both.")]
    [InlineData("""{ "splitColumns": 0 }""", "splitColumns 0 is out of range. Use 1 (a single column) to 40.")]
    [InlineData("""{ "dropCapStyle": "Missing" }""", "Unknown character style 'Missing'.")]
    public void Paragraph_style_values_name_the_ones_allowed(string fields, string message)
    {
        var exception = Assert.Throws<IdmlException>(() => IdmlJson.CreateDocument(
            $$"""{ "characterStyles": [ { "name": "C" } ], "paragraphStyles": [ { "name": "L", {{fields[1..^1]}} } ] }"""));

        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void Nested_grep_and_grid_fields_are_checked()
    {
        var warnings = IdmlJson.CheckSpec("""
            { "page": { "baselineGrid": { "increment": 12, "division": 12 } },
              "paragraphStyles": [ { "name": "P", "dropCap": 2, "alignToGrid": true, "nestedStyles": [ { "style": "C", "delimiter": "words" } ],
                "grepStyles": [ { "style": "C", "regex": "\\d" } ] } ],
              "characterStyles": [ { "name": "C", "dropCapLines": 2 } ] }
            """);

        Assert.Contains("page.baselineGrid: unknown field 'division', ignored (did you mean 'increment'?).", warnings);
        Assert.Contains("paragraphStyles[0]: unknown field 'dropCap', ignored (did you mean 'dropCapLines'?).", warnings);
        Assert.Contains("paragraphStyles[0]: unknown field 'alignToGrid', ignored (did you mean 'alignToBaselineGrid'?).", warnings);
        Assert.Contains("paragraphStyles[0].nestedStyles[0]: unknown field 'delimiter', ignored (did you mean 'through'?).", warnings);
        Assert.Contains("paragraphStyles[0].grepStyles[0]: unknown field 'regex', ignored (did you mean 'expression'?).", warnings);
        Assert.Contains("characterStyles[0]: 'dropCapLines' does not apply to a character style; it is ignored.", warnings);
        Assert.Equal("baselineGrid increment 0 must be positive: the body text's leading in points.",
            Assert.Throws<IdmlException>(() => IdmlJson.CreateDocument("""{ "page": { "baselineGrid": { "start": 10 } } }""")).Message);
    }

    [Fact]
    public void Text_stroke_skew_and_line_options_are_written_as_InDesign_stores_them()
    {
        var spec = """
        {
          "colors": [ { "name": "Red", "cmyk": [0, 100, 100, 0] } ],
          "characterStyles": [ { "name": "Outline", "stroke": "Black", "strokeWeight": 2, "strokeTint": 80, "skew": -15 } ],
          "pages": [ { "items": [ { "type": "text", "name": "t", "paragraphs": [ { "runs": [ { "text": "x", "underline": true,
            "underlineWeight": 3, "underlineOffset": 6, "underlineColor": "Red", "underlineTint": 50, "underlineType": "dashed",
            "strikeThrough": true, "strikeThroughWeight": 1, "strikeThroughOffset": -4, "strikeThroughColor": "Black", "strikeThroughType": "thickThin" } ] } ] } ] } ]
        }
        """;
        Assert.Empty(IdmlJson.CheckSpec(spec));
        using var document = IdmlJson.CreateDocument(spec);

        // 07-char-outlined-text and 07-char-basics: stroke and skew are attributes of the text.
        var outline = document.Styles.FindCharacterStyle("Outline")!.Element;
        Assert.Equal(["Color/Black", "2", "80", "-15"], ((string[])["StrokeColor", "StrokeWeight", "StrokeTint", "Skew"]).Select(name => outline.Attribute(name)!.Value));
        // 07-char-underline-strike: weights, offsets and tints are attributes; colours and types object properties.
        var run = ((TextFrame)document.AllPageItems.Single(item => item.Name == "t")).Story.Element.Descendants("CharacterStyleRange").Single();
        Assert.Equal(["3", "6", "50", "1", "-4"], ((string[])["UnderlineWeight", "UnderlineOffset", "UnderlineTint", "StrikeThroughWeight", "StrikeThroughOffset"]).Select(name => run.Attribute(name)!.Value));
        string Property(string name) => $"{run.Element("Properties")!.Element(name)!.Attribute("type")!.Value}:{run.Element("Properties")!.Element(name)!.Value}";
        Assert.Equal("object:Color/Red", Property("UnderlineColor"));
        Assert.Equal("object:StrokeStyle/$ID/Dashed", Property("UnderlineType"));
        Assert.Equal("object:Color/Black", Property("StrikeThroughColor"));
        Assert.Equal("object:StrokeStyle/$ID/ThickThin", Property("StrikeThroughType"));
    }

    [Theory]
    [InlineData("""{ "skew": 90 }""", "skew 90 is out of range. Use -85 to 85 degrees.")]
    [InlineData("""{ "underlineType": "double" }""", "Unknown underlineType 'double'. Use solid, dashed, dotted, wavy, thickThin.")]
    [InlineData("""{ "strokeWeight": -1 }""", "strokeWeight -1 is out of range. Use 0 to 800 pt.")]
    [InlineData("""{ "strikeThroughTint": 120 }""", "strikeThroughTint 120 is out of range. Use 0 to 100.")]
    [InlineData("""{ "stroke": "Teal" }""", "Unknown swatch 'Teal'. Declare it in colors first.")]
    public void Character_values_name_the_ones_allowed(string fields, string message)
    {
        var exception = Assert.Throws<IdmlException>(() => IdmlJson.CreateDocument($$"""{ "characterStyles": [ { "name": "C", {{fields[1..^1]}} } ] }"""));

        Assert.Equal(message, exception.Message);
        Assert.Contains("characterStyles[0]: unknown field 'slant', ignored (did you mean 'skew'?).",
            IdmlJson.CheckSpec("""{ "characterStyles": [ { "name": "C", "slant": 10 } ] }"""));
    }

    [Fact]
    public void Text_wrap_and_text_frame_options_are_written_as_InDesign_stores_them()
    {
        var spec = """
        {
          "unit": "mm",
          "pages": [ { "items": [
            { "type": "image", "name": "photo", "path": "PHOTO", "x": 10, "y": 10, "width": 50, "height": 40, "wrap": "objectShape", "wrapOffset": { "all": 2 }, "wrapSide": "largest" },
            { "type": "text", "name": "copy", "x": 10, "y": 60, "width": 100, "height": 50, "text": "x", "columns": 2, "wrap": "jumpObject", "wrapInverse": true,
              "autoSize": "heightAndWidth", "autoSizeMinHeight": 20, "balanceColumns": true, "firstBaseline": "fixed", "firstBaselineMin": 5, "ignoreWrap": true }
          ] } ]
        }
        """.Replace("PHOTO", Png().Replace("\\", "/", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Empty(IdmlJson.CheckSpec(spec));
        using var document = IdmlJson.CreateDocument(spec);

        // 02-item-text-wrap: Inverse, ApplyToMasterPageOnly, TextWrapSide, TextWrapMode, then the offsets in Properties;
        // after the frame's own options and before the placed image.
        var photo = Item(document, "photo");
        var wrap = photo.Element("TextWrapPreference")!;
        Assert.Equal(["Inverse", "ApplyToMasterPageOnly", "TextWrapSide", "TextWrapMode"], wrap.Attributes().Select(attribute => attribute.Name.LocalName));
        Assert.Equal(["false", "false", "LargestArea", "Contour"], wrap.Attributes().Select(attribute => attribute.Value));
        var mm2 = IdmlXml.Format(Units.FromUnit(2, "mm"));
        Assert.Equal([mm2, mm2, mm2, mm2], wrap.Element("Properties")!.Element("TextWrapOffset")!.Attributes().Select(attribute => attribute.Value));
        Assert.Equal(["Properties", "TextWrapPreference", "Image"], photo.Elements().Select(element => element.Name.LocalName));
        var copy = Item(document, "copy");
        Assert.Equal(["Properties", "TextFramePreference", "TextWrapPreference"], copy.Elements().Select(element => element.Name.LocalName));
        Assert.Equal(("JumpObjectTextWrap", "true"), (copy.Element("TextWrapPreference")!.Attribute("TextWrapMode")!.Value, copy.Element("TextWrapPreference")!.Attribute("Inverse")!.Value));
        // 06-frame-autosize, 06-frame-span-balance, 06-frame-basics: TextFramePreference attributes.
        var preference = copy.Element("TextFramePreference")!;
        string Attribute(string name) => preference.Attribute(name)!.Value;
        Assert.Equal("HeightAndWidth", Attribute("AutoSizingType"));
        Assert.Equal("TopLeftPoint", Attribute("AutoSizingReferencePoint"));
        Assert.Equal("true", Attribute("UseMinimumHeightForAutoSizing"));
        Assert.Equal(IdmlXml.Format(Units.FromUnit(20, "mm")), Attribute("MinimumHeightForAutoSizing"));
        Assert.Equal("true", Attribute("VerticalBalanceColumns"));
        Assert.Equal("FixedHeight", Attribute("FirstBaselineOffset"));
        Assert.Equal(IdmlXml.Format(Units.FromUnit(5, "mm")), Attribute("MinimumFirstBaselineOffset"));
        Assert.Equal("true", Attribute("IgnoreWrap"));
    }

    [Theory]
    [InlineData("""{ "type": "rectangle", "wrap": "around" }""", "Unknown wrap 'around'. Use none, boundingBox, objectShape, jumpObject or nextColumn.")]
    [InlineData("""{ "type": "rectangle", "wrap": "boundingBox", "wrapSide": "top" }""", "Unknown wrapSide 'top'. Use both, left, right, towardsSpine, awayFromSpine or largest.")]
    [InlineData("""{ "type": "text", "autoSize": "grow" }""", "Unknown autoSize 'grow'. Use off, height, width, heightAndWidth or proportional.")]
    [InlineData("""{ "type": "text", "autoSize": "height", "autoSizeFrom": "middle" }""", "Unknown autoSizeFrom 'middle'. Use topLeft, top, topRight, left, center, right, bottomLeft, bottom or bottomRight.")]
    [InlineData("""{ "type": "text", "firstBaseline": "descent" }""", "Unknown firstBaseline 'descent'. Use ascent, capHeight, leading, xHeight, emBox or fixed.")]
    [InlineData("""{ "type": "line", "wrapOffset": { "all": 3 } }""", "wrapOffset, wrapSide and wrapInverse need wrap.")]
    public void Unknown_frame_option_values_name_the_ones_allowed(string item, string message)
    {
        var exception = Assert.Throws<IdmlException>(() => IdmlJson.CreateDocument($$"""{ "pages": [ { "items": [ {{item}} ] } ] }"""));

        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void Text_frame_options_on_other_items_are_reported_as_misplaced()
    {
        var warnings = IdmlJson.CheckSpec("""
            { "pages": [ { "items": [ { "type": "rectangle", "autoSize": "height", "textWrap": "boundingBox" }, { "type": "text", "autoSizing": "height" } ] } ] }
            """);

        Assert.Contains("pages[0].items[0]: 'autoSize' does not apply to a rectangle item; it is ignored.", warnings);
        Assert.Contains("pages[0].items[0]: unknown field 'textWrap', ignored (did you mean 'wrap'?).", warnings);
        Assert.Contains("pages[0].items[1]: unknown field 'autoSizing', ignored (did you mean 'autoSize'?).", warnings);
    }

    [Fact]
    public void Gradients_and_object_styles_are_written_as_InDesign_stores_them()
    {
        var spec = """
        {
          "colors": [ { "name": "Sun", "cmyk": [0, 20, 90, 0] } ],
          "gradients": [ { "name": "Dawn", "stops": [ { "color": "Sun" }, { "color": "Black", "midpoint": 30 } ] }, { "name": "Glow", "type": "radial", "stops": [ { "color": "Paper" }, { "color": "Sun", "location": 60 }, { "color": "Black" } ] } ],
          "objectStyles": [
            { "name": "Card", "fill": "Sun", "stroke": "Black", "strokeWeight": 2, "cornerRadius": 6, "columns": 2, "inset": { "all": 4 }, "verticalAlign": "center" },
            { "name": "Ghost", "basedOn": "Card", "opacity": 40 }
          ],
          "pages": [ { "items": [
            { "type": "rectangle", "name": "band", "x": 0, "y": 0, "width": 200, "height": 100, "fill": "Dawn", "gradientAngle": 90 },
            { "type": "ellipse", "name": "dot", "x": 0, "y": 0, "width": 80, "height": 60, "fill": "Glow" },
            { "type": "text", "name": "card", "objectStyle": "Ghost", "x": 0, "y": 200, "width": 200, "height": 100, "text": "x", "fill": "Black" },
            { "type": "rectangle", "name": "plain", "objectStyle": "Card", "x": 0, "y": 400, "width": 50, "height": 50 }
          ] } ]
        }
        """;
        Assert.Empty(IdmlJson.CheckSpec(spec));
        using var document = IdmlJson.CreateDocument(spec);

        // 03-color-gradients: a Gradient swatch with typed stops; items name it and place it with GradientFill*.
        var glow = document.GraphicRoot.Elements("Gradient").Single(gradient => gradient.Attribute("Name")!.Value == "Glow");
        Assert.Equal("Radial", glow.Attribute("Type")!.Value);
        Assert.Equal(["0", "60", "100"], glow.Elements("GradientStop").Select(stop => stop.Attribute("Location")!.Value));
        Assert.Equal("30", document.GraphicRoot.Elements("Gradient").Single(gradient => gradient.Attribute("Name")!.Value == "Dawn").Elements("GradientStop").Last().Attribute("Midpoint")!.Value);
        var band = Item(document, "band");
        Assert.Equal("Gradient/Dawn", band.Attribute("FillColor")!.Value);
        // At 90 degrees the gradient runs up the box from its bottom edge (inner coordinates, y down), the box's height long.
        Assert.Equal(("0 50", "100", "90"), (band.Attribute("GradientFillStart")!.Value, band.Attribute("GradientFillLength")!.Value, band.Attribute("GradientFillAngle")!.Value));
        var dot = Item(document, "dot");
        Assert.Equal(("0 0", "40"), (dot.Attribute("GradientFillStart")!.Value, dot.Attribute("GradientFillLength")!.Value));

        // 02-item-object-styles: category flags, values as attributes, BasedOn in Properties, text frame options in a
        // TextFramePreference child and the opacity with its effects category flag.
        var card = document.Styles.FindObjectStyle("Card")!.Element;
        Assert.Equal(["true", "true", "true", "true", "false"],
            ((string[])["EnableFill", "EnableStroke", "EnableStrokeAndCornerOptions", "EnableTextFrameGeneralOptions", "EnableTextWrapAndOthers"]).Select(name => card.Attribute(name)!.Value));
        Assert.Equal(("Color/Sun", "Color/Black", "2", "RoundedCorner", "6"),
            (card.Attribute("FillColor")!.Value, card.Attribute("StrokeColor")!.Value, card.Attribute("StrokeWeight")!.Value, card.Attribute("TopLeftCornerOption")!.Value, card.Attribute("BottomRightCornerRadius")!.Value));
        Assert.Equal("$ID/[None]", card.Element("Properties")!.Element("BasedOn")!.Value);
        Assert.Equal(("2", "CenterAlign"), (card.Element("TextFramePreference")!.Attribute("TextColumnCount")!.Value, card.Element("TextFramePreference")!.Attribute("VerticalJustification")!.Value));
        var ghost = document.Styles.FindObjectStyle("Ghost")!.Element;
        Assert.Equal("ObjectStyle/Card", ghost.Element("Properties")!.Element("BasedOn")!.Value);
        Assert.Equal("true", ghost.Attribute("EnableFill")!.Value);
        Assert.Equal("40", ghost.Element("TransparencySetting")!.Element("BlendingSetting")!.Attribute("Opacity")!.Value);
        Assert.Equal("true", ghost.Element("ObjectStyleObjectEffectsCategorySettings")!.Attribute("EnableTransparency")!.Value);

        // Items take the style without values of their own in its categories, except the fields the spec gives.
        var frame = Item(document, "card");
        Assert.Equal("ObjectStyle/Ghost", frame.Attribute("AppliedObjectStyle")!.Value);
        Assert.Equal("Color/Black", frame.Attribute("FillColor")!.Value);
        Assert.Null(frame.Element("TextFramePreference")!.Attribute("TextColumnCount"));
        Assert.Null(frame.Element("TextFramePreference")!.Attribute("VerticalJustification"));
        var plain = Item(document, "plain");
        Assert.Null(plain.Attribute("StrokeColor"));
        var resolver = new AttributeResolver(document);
        Assert.Equal("Color/Black", resolver.Item(plain, "StrokeColor"));
        Assert.Equal("40", resolver.Blending(frame, "Opacity"));
    }

    [Theory]
    [InlineData("""{ "gradients": [ { "name": "G", "type": "conic", "stops": [ { "color": "Black" }, { "color": "Paper" } ] } ] }""", "Unknown gradient type 'conic'. Use linear or radial.")]
    [InlineData("""{ "gradients": [ { "name": "G", "stops": [ { "color": "Black" } ] } ] }""", "Gradient 'G' needs at least two stops.")]
    [InlineData("""{ "gradients": [ { "name": "G", "stops": [ { "color": "Black" }, { "color": "Teal" } ] } ] }""", "Unknown swatch 'Teal'. Declare it in colors first.")]
    [InlineData("""{ "objectStyles": [ { "name": "O", "basedOn": "Base" } ] }""", "Unknown object style 'Base'. Declare it in objectStyles first.")]
    [InlineData("""{ "objectStyles": [ { "name": "O", "opacity": 120 } ] }""", "opacity 120 is out of range. Use 0 to 100.")]
    [InlineData("""{ "pages": [ { "items": [ { "type": "rectangle", "objectStyle": "Card" } ] } ] }""", "Unknown object style 'Card'. Declare it in objectStyles first.")]
    public void Gradient_and_object_style_errors_name_what_is_allowed(string spec, string message)
    {
        var exception = Assert.Throws<IdmlException>(() => IdmlJson.CreateDocument(spec));

        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void Paragraph_rules_borders_and_shading_are_written_as_InDesign_stores_them()
    {
        var spec = """
        {
          "colors": [ { "name": "Red", "cmyk": [0, 100, 100, 0] } ],
          "paragraphStyles": [
            { "name": "Head", "ruleAbove": true, "ruleAboveWeight": 3, "ruleAboveOffset": 6, "ruleAboveColor": "Red", "ruleAboveType": "dotted",
              "ruleBelowWeight": 1, "ruleBelowWidth": "text", "ruleBelowLeftIndent": 20, "ruleBelowTint": 50 },
            { "name": "Box", "border": "Red", "borderWeight": 1.5, "borderOffset": { "all": 4, "top": 6 }, "borderCornerRadius": 10, "borderType": "dashed",
              "shading": "Red", "shadingTint": 20, "shadingOffset": { "all": 6 } },
            { "name": "Plain", "basedOn": "Box", "border": "None", "ruleAbove": false }
          ]
        }
        """;
        Assert.Empty(IdmlJson.CheckSpec(spec));
        using var document = IdmlJson.CreateDocument(spec);

        // 08-para-rules: RuleAbove/RuleBelow switches, weights, offsets, tints, indents and widths as attributes; the
        // colour and type as object properties.
        XElement Style(string name) => document.Styles.FindParagraphStyle(name)!.Element;
        string Property(XElement element, string name) => $"{element.Element("Properties")!.Element(name)!.Attribute("type")!.Value}:{element.Element("Properties")!.Element(name)!.Value}";
        var head = Style("Head");
        Assert.Equal(["true", "3", "6", "true", "1", "TextWidth", "20", "50"],
            ((string[])["RuleAbove", "RuleAboveLineWeight", "RuleAboveOffset", "RuleBelow", "RuleBelowLineWeight", "RuleBelowWidth", "RuleBelowLeftIndent", "RuleBelowTint"]).Select(name => head.Attribute(name)!.Value));
        Assert.Equal("object:Color/Red", Property(head, "RuleAboveColor"));
        Assert.Equal("object:StrokeStyle/$ID/Canned Dotted", Property(head, "RuleAboveType"));
        // 08-para-borders-shading: ParagraphBorderOn and ParagraphShadingOn, per-side offsets, weights and corners.
        var box = Style("Box");
        Assert.Equal(["true", "6", "4", "1.5", "1.5", "RoundedCorner", "10", "true", "20", "6"],
            ((string[])["ParagraphBorderOn", "ParagraphBorderTopOffset", "ParagraphBorderLeftOffset", "ParagraphBorderTopLineWeight", "ParagraphBorderRightLineWeight",
                "ParagraphBorderTopLeftCornerOption", "ParagraphBorderBottomRightCornerRadius", "ParagraphShadingOn", "ParagraphShadingTint", "ParagraphShadingBottomOffset"])
            .Select(name => box.Attribute(name)!.Value));
        Assert.Equal("object:Color/Red", Property(box, "ParagraphBorderColor"));
        Assert.Equal("object:StrokeStyle/$ID/Dashed", Property(box, "ParagraphBorderType"));
        Assert.Equal("object:Color/Red", Property(box, "ParagraphShadingColor"));
        Assert.Equal(("false", "false"), (Style("Plain").Attribute("ParagraphBorderOn")!.Value, Style("Plain").Attribute("RuleAbove")!.Value));
        Assert.Equal("Unknown ruleBelowWidth 'page'. Use column or text.",
            Assert.Throws<IdmlException>(() => IdmlJson.CreateDocument("""{ "paragraphStyles": [ { "name": "P", "ruleBelowWidth": "page" } ] }""")).Message);
    }

    private static string Png()
    {
        var path = TestFiles.TempPath(".png");
        File.WriteAllBytes(path, TestFiles.Png(40, 30));
        return path;
    }

    private static XElement Item(IdmlDocument document, string name) =>
        document.AllPageItems.Single(item => item.Name == name).Element;
}
