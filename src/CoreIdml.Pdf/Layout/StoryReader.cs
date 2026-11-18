using System.Text;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Fonts;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// Turns story (or table cell) XML into paragraphs of styled inline items, resolving every run's formatting
/// through <see cref="AttributeResolver"/> (local, character style, paragraph, paragraph style, BasedOn chains).
/// </summary>
internal sealed class StoryReader
{
    private static readonly HashSet<string> Transparent =
        ["ParagraphStyleRange", "CharacterStyleRange", "XMLElement", "HyperlinkTextSource", "HyperlinkTextDestination", "Change", "TextVariableInstance", "Story"];

    private static readonly HashSet<string> Skipped =
        ["Properties", "Footnote", "Note", "XMLAttribute", "XMLComment", "XMLInstruction", "DTD", "StoryPreference", "InCopyExportOption",
         "Rectangle", "Oval", "Polygon", "GraphicLine", "TextFrame", "Group", "Button", "EPSText", "Image", "PDF", "EPS", "Index", "Bookmark"];

    private readonly IdmlDocument document;
    private readonly AttributeResolver resolver;
    private readonly FontLibrary fonts;
    private readonly Dictionary<XElement, RunStyle> runStyles = [];
    private readonly Dictionary<XElement, ParagraphFormatting> paragraphFormats = [];

    public StoryReader(IdmlDocument document, AttributeResolver resolver, FontLibrary fonts)
    {
        this.document = document;
        this.resolver = resolver;
        this.fonts = fonts;
    }

    public AttributeResolver Resolver => resolver;

    /// <summary>Reads the paragraphs of a story or of a table cell.</summary>
    public IReadOnlyList<ParagraphModel> Read(XElement container)
    {
        var paragraphs = new List<ParagraphModel>();
        var items = new List<InlineItem>();
        XElement? currentRange = null;
        RunStyle? lastStyle = null;

        void EndParagraph(XElement? markSource)
        {
            var range = currentRange ?? markSource?.AncestorsAndSelf("ParagraphStyleRange").FirstOrDefault();
            var markStyle = markSource is not null ? StyleOf(markSource) : lastStyle ?? (range is not null ? StyleOf(range) : DefaultStyle());
            paragraphs.Add(new ParagraphModel(range is null ? new ParagraphFormatting() : FormatOf(range), items, markStyle));
            items = [];
            currentRange = null;
        }

        void Walk(XElement element)
        {
            foreach (var child in element.Elements())
            {
                var name = child.Name.LocalName;
                switch (name)
                {
                    case "Content":
                        currentRange ??= child.Ancestors("ParagraphStyleRange").FirstOrDefault();
                        var style = StyleOf(child);
                        lastStyle = style;
                        AddContent(child, style, items);
                        break;
                    case "Br":
                        currentRange ??= child.Ancestors("ParagraphStyleRange").FirstOrDefault();
                        EndParagraph(child);
                        break;
                    case "Table":
                        currentRange ??= child.Ancestors("ParagraphStyleRange").FirstOrDefault();
                        items.Add(new TableItem(child, StyleOf(child)));
                        break;
                    case "Cell" or "Row" or "Column":
                        break;
                    case var _ when Skipped.Contains(name):
                        break;
                    case var _ when Transparent.Contains(name):
                        Walk(child);
                        break;
                    default:
                        Walk(child);
                        break;
                }
            }
        }

        Walk(container);
        // The last paragraph has no Br; it exists when anything was written after the last Br.
        if (items.Count > 0 || paragraphs.Count == 0 || currentRange is not null)
        {
            EndParagraph(null);
        }
        return paragraphs;
    }

    private static void AddContent(XElement content, RunStyle style, List<InlineItem> items)
    {
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0)
            {
                items.Add(new TextItem(text.ToString(), style));
                text.Clear();
            }
        }
        foreach (var node in content.Nodes())
        {
            switch (node)
            {
                case XText value:
                    foreach (var c in value.Value)
                    {
                        switch (c)
                        {
                            case '\t':
                                Flush();
                                items.Add(new TabItem(style));
                                break;
                            case '\u2028' or '\n' or '\r':
                                Flush();
                                items.Add(new LineBreakItem(style));
                                break;
                            default:
                                text.Append(c);
                                break;
                        }
                    }
                    break;
                case XProcessingInstruction { Target: "ACE" } instruction:
                    switch (instruction.Data.Trim())
                    {
                        case "18":
                            Flush();
                            items.Add(new PageNumberItem(style));
                            break;
                        case "8":
                            Flush();
                            items.Add(new TabItem(style, RightIndentTab: true));
                            break;
                    }
                    break;
            }
        }
        Flush();
    }

    /// <summary>The formatting of text at a Content/Br/run element.</summary>
    public RunStyle StyleOf(XElement element)
    {
        var run = element.AncestorsAndSelf("CharacterStyleRange").FirstOrDefault()
            ?? element.AncestorsAndSelf("ParagraphStyleRange").FirstOrDefault();
        if (run is null)
        {
            return DefaultStyle();
        }
        if (runStyles.TryGetValue(run, out var cached))
        {
            return cached;
        }
        // Every attribute resolves along the same chain (run, character style, paragraph, paragraph style).
        var chain = resolver.TextChain(run);
        string? Get(string name) => AttributeResolver.First(chain, name);
        var family = Get("AppliedFont") ?? "Minion Pro";
        var fontStyle = Get("FontStyle") ?? "Regular";
        var size = Values.Number(Get("PointSize"), 12);
        if (size <= 0)
        {
            size = 12;
        }
        var fill = document.Swatches.ResolveColor(Get("FillColor") ?? "Color/Black");
        var tint = Values.Number(Get("FillTint"), -1);
        if (fill is not null && tint >= 0)
        {
            fill = fill.WithTint(tint);
        }
        var leadingValue = Get("Leading");
        double? leading = leadingValue is null || leadingValue == "Auto" ? null : Values.Number(leadingValue);
        var kerning = Get("KerningMethod") ?? "$ID/Metrics";
        var style = new RunStyle
        {
            Font = fonts.Resolve(family, fontStyle, PostScriptName(family, fontStyle), run.AncestorsAndSelf("Story").FirstOrDefault()?.Attribute("Self")?.Value),
            Size = size,
            Fill = fill,
            Tracking = Values.Number(Get("Tracking"), 0),
            HorizontalScale = Math.Max(0.01, Values.Number(Get("HorizontalScale"), 100) / 100),
            BaselineShift = Values.Number(Get("BaselineShift"), 0),
            Capitalization = Get("Capitalization") ?? "Normal",
            Position = Get("Position") ?? "Normal",
            Underline = Get("Underline") == "true",
            StrikeThrough = Get("StrikeThru") == "true",
            Kerning = kerning is not ("None" or "$ID/None"),
            Leading = leading is > 0 ? leading : null,
            AutoLeading = Values.Number(Get("AutoLeading"), 120) / 100,
            Language = Get("AppliedLanguage"),
        };
        runStyles[run] = style;
        return style;
    }

    private Dictionary<(string, string), string>? postScriptNames;

    /// <summary>The PostScript name InDesign recorded for a family and style in Resources/Fonts.xml.</summary>
    private string? PostScriptName(string family, string style)
    {
        if (postScriptNames is null)
        {
            postScriptNames = [];
            foreach (var font in document.Package.TryGetXml("Resources/Fonts.xml")?.Root?.Descendants("Font") ?? [])
            {
                var key = (font.Attribute("FontFamily")?.Value ?? string.Empty, font.Attribute("FontStyleName")?.Value ?? string.Empty);
                if (font.Attribute("PostScriptName")?.Value is { Length: > 0 } name && name != "$ID/")
                {
                    postScriptNames.TryAdd(key, name);
                }
            }
        }
        return postScriptNames.TryGetValue((family, style), out var value) ? value : null;
    }

    public RunStyle DefaultStyle() => new() { Font = fonts.Resolve("Minion Pro", "Regular") };

    public ParagraphFormatting FormatOf(XElement range)
    {
        if (paragraphFormats.TryGetValue(range, out var cached))
        {
            return cached;
        }
        string? Get(string name) => resolver.Paragraph(range, name);
        var tabs = new List<TabStop>();
        var tabSource = range.Element("Properties")?.Element("TabList")
            ?? resolver.StyleChain(range.Attribute("AppliedParagraphStyle")?.Value)
                .Select(style => style.Element("Properties")?.Element("TabList")).FirstOrDefault(list => list is not null);
        if (tabSource is not null)
        {
            foreach (var item in tabSource.Elements("ListItem"))
            {
                var position = Values.Number(item.Element("Position")?.Value);
                if (position is not null)
                {
                    tabs.Add(new TabStop(position.Value, item.Element("Alignment")?.Value ?? "LeftAlign"));
                }
            }
        }
        var format = new ParagraphFormatting
        {
            Justification = Get("Justification") ?? "LeftAlign",
            SpaceBefore = Values.Number(Get("SpaceBefore"), 0),
            SpaceAfter = Values.Number(Get("SpaceAfter"), 0),
            LeftIndent = Values.Number(Get("LeftIndent"), 0),
            RightIndent = Values.Number(Get("RightIndent"), 0),
            FirstLineIndent = Values.Number(Get("FirstLineIndent"), 0),
            LastLineIndent = Values.Number(Get("LastLineIndent"), 0),
            Tabs = tabs.OrderBy(tab => tab.Position).ToList(),
            Hyphenation = new HyphenationSettings(
                Get("Hyphenation") != "false",
                (int)Values.Number(Get("HyphenateAfterFirst"), 2),
                (int)Values.Number(Get("HyphenateBeforeLast"), 2),
                (int)Values.Number(Get("HyphenateWordsLongerThan"), 5),
                Get("HyphenateCapitalizedWords") != "false",
                Values.Number(Get("HyphenationZone"), 36)),
        };
        paragraphFormats[range] = format;
        return format;
    }
}
