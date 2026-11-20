namespace CoreIdml.Cli.Tools;

/// <summary>
/// The JSON Schema of a document spec (CoreIdml.Json.DocumentSpec, read by IdmlJson.CreateDocument and Apply), for the
/// input schemas of idml_create and idml_apply: every field, its unit and its allowed values, so a client can write a
/// spec without the source. Kept in step with SpecModel.cs and SpecChecker.cs (CliAndMcpTests checks the field names).
/// </summary>
internal static class SpecSchema
{
    /// <summary>The "spec" property, referring to <see cref="Definitions"/>.</summary>
    public const string Property = """ "spec":{"$ref":"#/$defs/spec"}""";

    /// <summary>
    /// The "$defs" member to add to the root of the input schema, on one line: MCP over stdio sends one message per
    /// line, so a schema must not contain a line break.
    /// </summary>
    public static readonly string Definitions = Compact(Source);

    private const string Source = """
        "$defs":{
        "spec":{"type":"object","description":"Declare colours and styles before anything uses them (or they must exist already, for idml_apply).","properties":{
          "unit":{"type":"string","enum":["pt","mm","cm","in"],"description":"For positions, sizes, margins, bleed, gutters, insets, corner radii, table sizes. Default pt. Font size, leading, spacing, indents, stroke weights: always pt."},
          "page":{"type":"object","description":"idml_create only","properties":{
            "size":{"type":"string","enum":["A4","A3","A5","Letter","Legal"],"description":"Default A4; ignored when width and height are given"},
            "width":{"type":"number"},"height":{"type":"number"},"landscape":{"type":"boolean"},
            "margins":{"$ref":"#/$defs/spacing","description":"Default 36 pt"},
            "facingPages":{"type":"boolean"},
            "columns":{"type":"integer","minimum":1},"columnGutter":{"type":"number"},
            "bleed":{"type":"number"}}},
          "colors":{"type":"array","description":"Black, Paper, Registration, None always exist","items":{"type":"object","required":["name"],"properties":{
            "name":{"type":"string"},
            "cmyk":{"type":"array","items":{"type":"number","minimum":0,"maximum":100},"minItems":4,"maxItems":4},
            "rgb":{"type":"array","items":{"type":"number","minimum":0,"maximum":255},"minItems":3,"maxItems":3},
            "hex":{"type":"string","pattern":"^#[0-9A-Fa-f]{6}$"}}}},
          "characterStyles":{"type":"array","items":{"$ref":"#/$defs/style"}},
          "paragraphStyles":{"type":"array","items":{"$ref":"#/$defs/style"}},
          "pages":{"type":"array","items":{"type":"object","properties":{
            "index":{"type":"integer","minimum":0,"description":"Draw on this existing page. Without it, idml_create uses the next page and idml_apply appends one."},
            "margins":{"$ref":"#/$defs/spacing"},
            "items":{"type":"array","items":{"$ref":"#/$defs/item"}}}}}}},
        "spacing":{"type":"object","description":"Sides fall back to all, then 0","properties":{"all":{"type":"number"},"top":{"type":"number"},"right":{"type":"number"},"bottom":{"type":"number"},"left":{"type":"number"}}},
        "style":{"allOf":[{"$ref":"#/$defs/format"}],"type":"object","required":["name"],"properties":{"name":{"type":"string"},"basedOn":{"type":"string","description":"Declared before this one"}},"description":"Updated when the name exists"},
        "format":{"type":"object","description":"Runs and character styles take only font, fontStyle, size, color, tint, underline, strikeThrough, tracking, caps, position. For lists, prefer a paragraph style with list set.","properties":{
          "font":{"type":"string","description":"Installed family (idml_list_fonts), e.g. Arial"},
          "fontStyle":{"type":"string","description":"e.g. Regular, Bold, Bold Italic"},
          "size":{"type":"number"},"leading":{"type":"number"},
          "color":{"type":"string","description":"Swatch"},"tint":{"type":"number","minimum":0,"maximum":100},
          "align":{"type":"string","enum":["left","center","right","justify","justifyAll"],"description":"justify: last line left"},
          "spaceBefore":{"type":"number"},"spaceAfter":{"type":"number"},
          "firstLineIndent":{"type":"number"},"leftIndent":{"type":"number"},"rightIndent":{"type":"number"},
          "underline":{"type":"boolean"},"strikeThrough":{"type":"boolean"},
          "tracking":{"type":"number","description":"1/1000 em"},
          "caps":{"type":"string","enum":["normal","allCaps","smallCaps","capToSmallCap"]},
          "position":{"type":"string","enum":["normal","superscript","subscript"]},
          "list":{"type":"string","enum":["bullet","numbered","none"],"description":"Paragraphs and paragraph styles. InDesign draws the bullet or number: do not type it in the text. Hanging list: leftIndent 18, firstLineIndent -18 (the tab after the marker stops at the left indent)."},
          "bullet":{"type":"string","description":"One character; default •"},
          "bulletAfter":{"type":"string","description":"After the bullet; default a tab. ^m em space, ^> en space"},
          "numberFormat":{"type":"string","enum":["1","01","001","0001","a","A","i","I"],"description":"Default 1"},
          "numberText":{"type":"string","description":"^# the number, ^1-^9 that level's number, \\t a tab. Default ^#.\\t; e.g. ^#)\\t or ^1.^#\\t"},
          "listLevel":{"type":"integer","minimum":1,"maximum":9,"description":"Restarts after any paragraph of a higher level"},
          "startAt":{"type":"integer","description":"Paragraph: restart numbering at this number. Style: first number"},
          "tabs":{"type":"array","description":"Paragraphs and paragraph styles: tab stops, replacing the style's; [] clears them (default stops every 36 pt). Put \\t in the text. Table of contents: [{\"position\":400,\"align\":\"right\",\"leader\":\".\"}]","items":{"$ref":"#/$defs/tab"}}}},
        "tab":{"type":"object","required":["position"],"properties":{
          "position":{"type":"number","minimum":0,"description":"pt from the frame's left edge, inside its inset"},
          "align":{"type":"string","enum":["left","center","right","decimal"],"description":"Default left. decimal: alignOn sits on the stop; text without it ends there"},
          "leader":{"type":"string","maxLength":8,"description":"Repeated across the tab's space: . - _ ·"},
          "alignOn":{"type":"string","description":"decimal: the character, default ."}}},
        "item":{"type":"object","description":"x/y: top-left corner from the page's top-left, y down, margins not added.","properties":{
          "type":{"type":"string","enum":["text","table","image","rectangle","ellipse","line"],"description":"Default text"},
          "name":{"type":"string","description":"idml_fill, continueFrom and reports find frames by it"},
          "label":{"type":"string","description":"Script label"},"tag":{"type":"string","description":"XML tag to bind"},
          "layer":{"type":"string","description":"Created if missing"},
          "x":{"type":"number"},"y":{"type":"number"},"width":{"type":"number"},"height":{"type":"number"},
          "fill":{"type":"string","description":"Swatch"},"fillTint":{"type":"number","minimum":0,"maximum":100},
          "stroke":{"type":"string","description":"Swatch. Shapes have no outline without it; lines default to Black 1 pt."},
          "strokeWeight":{"type":"number"},"strokeTint":{"type":"number","minimum":0,"maximum":100},
          "strokeType":{"type":"string","enum":["solid","dashed","dotted"]},
          "endCap":{"type":"string","enum":["butt","round","projecting"]},
          "opacity":{"type":"number","minimum":0,"maximum":100},
          "cornerRadius":{"type":"number"},"rotation":{"type":"number","description":"Degrees counter-clockwise"},
          "text":{"type":"string","description":"text: \\n starts a paragraph"},
          "style":{"type":"string","description":"text: paragraph style"},
          "paragraphs":{"type":"array","description":"text: after text","items":{"allOf":[{"$ref":"#/$defs/format"}],"type":"object","properties":{
            "text":{"type":"string"},"style":{"type":"string","description":"Paragraph style"},
            "runs":{"type":"array","items":{"allOf":[{"$ref":"#/$defs/format"}],"type":"object","required":["text"],"properties":{"text":{"type":"string"},"style":{"type":"string","description":"Character style"}}}}}}},
          "columns":{"type":"integer","minimum":1,"description":"text"},"columnGutter":{"type":"number","description":"text"},
          "inset":{"$ref":"#/$defs/spacing","description":"text"},
          "verticalAlign":{"type":"string","enum":["top","center","bottom","justify"],"description":"text"},
          "continueFrom":{"type":"string","description":"text: name of an earlier frame whose story continues here (no text of its own)"},
          "path":{"type":"string","description":"image: absolute JPEG/PNG path (linked)"},
          "fit":{"type":"string","enum":["fill","fit","stretch","center","original"],"description":"image: default fill (crops)"},
          "rows":{"type":"array","items":{"type":"array","items":{"type":"string"}},"description":"table: cell texts"},
          "columnWidths":{"type":"array","items":{"type":"number"},"description":"table"},
          "headerRows":{"type":"integer","minimum":0,"description":"table"},"rowHeight":{"type":"number","description":"table: default 18 pt"},
          "cellStyle":{"type":"string","description":"table: paragraph style"},"headerStyle":{"type":"string","description":"table: paragraph style"},
          "headerFill":{"type":"string","description":"table: swatch"},"cellInset":{"type":"number","description":"table"},
          "x2":{"type":"number","description":"line: end; default x + width"},"y2":{"type":"number","description":"line: default y + height"}}}}
        """;

    /// <summary>The members of <paramref name="members"/> as one line of JSON (it is checked to be valid JSON too).</summary>
    private static string Compact(string members)
    {
        var compact = System.Text.Json.Nodes.JsonNode.Parse("{" + members + "}")!.ToJsonString();
        return compact[1..^1];
    }

    /// <summary>An input schema: <paramref name="properties"/> (comma-separated members), the required names, and the spec's definitions.</summary>
    public static string Object(string properties, params string[] required) =>
        "{\"type\":\"object\",\"properties\":{" + properties + "},\"required\":[" + string.Join(",", required.Select(name => "\"" + name + "\"")) + "]," + Definitions + "}";
}
