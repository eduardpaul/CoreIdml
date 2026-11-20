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

    // Anchored items live in a story, not a spread; their geometry does not depend on the spread they are wrapped with.
    private Spread? anchorSpread;

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
        // Where the paragraph's text starts: its list marker takes this character's formatting.
        XElement? firstText = null;
        RunStyle? lastStyle = null;
        var markers = new ListMarkers(this, container.Name.LocalName == "Story" ? CarriedInto(container) : null);

        void EndParagraph(XElement? markSource)
        {
            var range = currentRange ?? markSource?.AncestorsAndSelf("ParagraphStyleRange").FirstOrDefault();
            var markStyle = markSource is not null ? StyleOf(markSource) : lastStyle ?? (range is not null ? StyleOf(range) : DefaultStyle());
            if (range is not null && markers.Marker(range, firstText ?? markSource ?? range) is { } marker)
            {
                items.InsertRange(0, marker);
            }
            paragraphs.Add(new ParagraphModel(range is null ? new ParagraphFormatting() : FormatOf(range), items, markStyle));
            items = [];
            currentRange = null;
            firstText = null;
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
                        if (firstText is null && child.Nodes().Any())
                        {
                            firstText = child;
                        }
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
                    case "Rectangle" or "Oval" or "Polygon" or "GraphicLine" or "TextFrame" or "Group":
                        if (InlineObject(child) is { } inline)
                        {
                            currentRange ??= child.Ancestors("ParagraphStyleRange").FirstOrDefault();
                            items.Add(inline);
                        }
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

    // ---- lists continued across stories ----------------------------------------------------------------

    // Counters each story starts with, by story id, for the lists that continue across stories; null until needed.
    private Dictionary<string, Dictionary<string, ListMarkers.ListState>>? carriedByStory;

    /// <summary>
    /// The counters <paramref name="story"/> starts with for the lists that continue across stories
    /// (<c>NumberingList@ContinueNumbersAcrossStories="true"</c>): what the stories before it left. Null when the
    /// document has no such list. Stories are ordered by their first frame: page, then top, then left.
    /// </summary>
    private Dictionary<string, ListMarkers.ListState>? CarriedInto(XElement story)
    {
        if (carriedByStory is null)
        {
            carriedByStory = new Dictionary<string, Dictionary<string, ListMarkers.ListState>>(StringComparer.Ordinal);
            var continued = document.DesignMap.Elements("NumberingList")
                .Where(list => list.Attribute("ContinueNumbersAcrossStories")?.Value == "true")
                .Select(list => list.Attribute("Self")?.Value)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            if (continued.Count > 0)
            {
                var stories = document.Stories.ToDictionary(s => s.Id, s => s.Element, StringComparer.Ordinal);
                var carried = new Dictionary<string, ListMarkers.ListState>(StringComparer.Ordinal);
                foreach (var id in StoriesInLayoutOrder())
                {
                    if (!stories.TryGetValue(id, out var element))
                    {
                        continue;
                    }
                    carriedByStory[id] = carried;
                    var markers = new ListMarkers(this, carried);
                    CountParagraphs(element, markers);
                    carried = markers.Carried(continued);
                }
            }
        }
        var storyId = story.Attribute("Self")?.Value;
        return storyId is not null && carriedByStory.TryGetValue(storyId, out var state) ? state : null;
    }

    /// <summary>Ids of the stories shown on document pages, ordered by their first frame: page, then top, then left.</summary>
    private List<string> StoriesInLayoutOrder()
    {
        var pageIndex = document.Pages.Select((page, index) => (page.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        var first = new Dictionary<string, (int Page, double Top, double Left)>(StringComparer.Ordinal);
        foreach (var frame in document.Spreads.SelectMany(spread => spread.AllPageItems).OfType<TextFrame>())
        {
            if (frame.StoryId.Length == 0)
            {
                continue;
            }
            var page = frame.Page;
            var bounds = frame.Bounds;
            var key = (page is not null && pageIndex.TryGetValue(page.Id, out var index) ? index : int.MaxValue, Math.Round(bounds.Top, 3), Math.Round(bounds.Left, 3));
            if (!first.TryGetValue(frame.StoryId, out var known) || key.CompareTo(known) < 0)
            {
                first[frame.StoryId] = key;
            }
        }
        return first.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).ToList();
    }

    /// <summary>Advances <paramref name="markers"/> through the paragraphs of a story, as <see cref="Read"/> splits them.</summary>
    private void CountParagraphs(XElement container, ListMarkers markers)
    {
        XElement? currentRange = null;
        var hasItems = false;
        var count = 0;

        void EndParagraph(XElement? markSource)
        {
            if ((currentRange ?? markSource?.AncestorsAndSelf("ParagraphStyleRange").FirstOrDefault()) is { } range)
            {
                markers.Count(range);
            }
            count++;
            hasItems = false;
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
                        hasItems |= child.Value.Length > 0 || child.Nodes().OfType<XProcessingInstruction>().Any();
                        break;
                    case "Br":
                        currentRange ??= child.Ancestors("ParagraphStyleRange").FirstOrDefault();
                        EndParagraph(child);
                        break;
                    case "Table":
                        currentRange ??= child.Ancestors("ParagraphStyleRange").FirstOrDefault();
                        hasItems = true;
                        break;
                    case "Cell" or "Row" or "Column":
                        break;
                    case "Rectangle" or "Oval" or "Polygon" or "GraphicLine" or "TextFrame" or "Group":
                        if (InlineObject(child) is not null)
                        {
                            currentRange ??= child.Ancestors("ParagraphStyleRange").FirstOrDefault();
                            hasItems = true;
                        }
                        break;
                    case var _ when Skipped.Contains(name):
                        break;
                    default:
                        Walk(child);
                        break;
                }
            }
        }

        Walk(container);
        if (hasItems || count == 0 || currentRange is not null)
        {
            EndParagraph(null);
        }
    }

    /// <summary>
    /// A page item anchored in the text, when it flows inline (InDesign's default; above-line and custom-positioned
    /// objects are not drawn).
    /// </summary>
    private InlineObjectItem? InlineObject(XElement element)
    {
        var setting = resolver.ObjectChain(element)
            .Select(source => source.Element("AnchoredObjectSetting"))
            .FirstOrDefault(candidate => candidate?.Attribute("AnchoredPosition") is not null);
        if ((setting?.Attribute("AnchoredPosition")?.Value ?? "InlinePosition") != "InlinePosition")
        {
            return null;
        }
        anchorSpread ??= new Spread(document, string.Empty, new XElement("Spread"));
        if (PageItem.TryWrap(document, anchorSpread, element) is not { } item)
        {
            return null;
        }
        var bounds = VisibleBounds(item);
        if (bounds.Width <= 0 && bounds.Height <= 0)
        {
            return null;
        }
        // Inline: a positive Y offset raises the object above the baseline.
        return new InlineObjectItem(element, bounds, Values.Number(setting?.Attribute("AnchorYoffset")?.Value, 0), StyleOf(element));
    }

    /// <summary>Bounds including the part of the stroke outside the path, as InDesign sizes an inline object.</summary>
    private Rect VisibleBounds(PageItem item)
    {
        if (item is Group group && group.Items.Count > 0)
        {
            var children = group.Items.Select(VisibleBounds).ToList();
            return Rect.FromEdges(children.Min(r => r.Left), children.Min(r => r.Top), children.Max(r => r.Right), children.Max(r => r.Bottom));
        }
        var bounds = item.SpreadBounds;
        var weight = Values.Number(resolver.Item(item.Element, "StrokeWeight"), 0);
        var color = resolver.Item(item.Element, "StrokeColor");
        if (weight <= 0 || color is null || color == "Swatch/None")
        {
            return bounds;
        }
        var outside = (resolver.Item(item.Element, "StrokeAlignment") ?? "CenterAlignment") switch
        {
            "InsideAlignment" => 0,
            "OutsideAlignment" => weight,
            _ => weight / 2,
        };
        return Rect.FromEdges(bounds.Left - outside, bounds.Top - outside, bounds.Right + outside, bounds.Bottom + outside);
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
        var style = Build(resolver.TextChain(run), run, null, null);
        runStyles[run] = style;
        return style;
    }

    private readonly Dictionary<(XElement, string?, string?, string?), RunStyle> markerStyles = [];

    /// <summary>
    /// The formatting of a list's bullet or number: the text at <paramref name="firstText"/> under the list's
    /// character style (<paramref name="characterStyle"/>), in the bullet's own font when it has one.
    /// </summary>
    public RunStyle MarkerStyle(XElement firstText, string? characterStyle, string? family, string? fontStyle)
    {
        var run = firstText.AncestorsAndSelf("CharacterStyleRange").FirstOrDefault()
            ?? firstText.AncestorsAndSelf("ParagraphStyleRange").FirstOrDefault();
        if (run is null)
        {
            return DefaultStyle();
        }
        var key = (run, characterStyle, family, fontStyle);
        if (!markerStyles.TryGetValue(key, out var style))
        {
            var listStyle = resolver.StyleChain(characterStyle);
            style = listStyle.Count == 0 && family is null
                ? StyleOf(firstText)
                : Build([.. listStyle, .. resolver.TextChain(run)], run, family, fontStyle);
            markerStyles[key] = style;
        }
        return style;
    }

    /// <summary>
    /// A paragraph property stored as an element with attributes (<c>BulletChar</c>, <c>NumberingRestartPolicies</c>):
    /// the paragraph's own, else its style chain's.
    /// </summary>
    public XElement? ParagraphProperty(XElement range, string name) =>
        range.Element("Properties")?.Element(name)
        ?? resolver.StyleChain(range.Attribute("AppliedParagraphStyle")?.Value)
            .Select(style => style.Element("Properties")?.Element(name))
            .FirstOrDefault(element => element is not null);

    private RunStyle Build(IReadOnlyList<XElement> chain, XElement run, string? familyOverride, string? fontStyleOverride)
    {
        string? Get(string name) => AttributeResolver.First(chain, name);
        var family = familyOverride ?? Get("AppliedFont") ?? "Minion Pro";
        var fontStyle = fontStyleOverride ?? (familyOverride is not null ? "Regular" : null) ?? Get("FontStyle") ?? "Regular";
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
        return new RunStyle
        {
            Font = fonts.Resolve(family, fontStyle, PostScriptName(family, fontStyle), run.AncestorsAndSelf("Story").FirstOrDefault()?.Attribute("Self")?.Value),
            FontIsSubstitute = !fonts.IsAvailable(family, fontStyle),
            Size = size,
            Fill = fill,
            Tracking = Values.Number(Get("Tracking"), 0),
            HorizontalScale = Math.Max(0.01, Values.Number(Get("HorizontalScale"), 100) / 100),
            VerticalScale = Math.Max(0.01, Values.Number(Get("VerticalScale"), 100) / 100),
            BaselineShift = Values.Number(Get("BaselineShift"), 0),
            Capitalization = Get("Capitalization") ?? "Normal",
            Position = Get("Position") ?? "Normal",
            Underline = Get("Underline") == "true",
            StrikeThrough = Get("StrikeThru") == "true",
            Kerning = kerning is not ("None" or "$ID/None"),
            Ligatures = Get("Ligatures") != "false",
            Leading = leading is > 0 ? leading : null,
            AutoLeading = Values.Number(Get("AutoLeading"), 120) / 100,
            Language = Get("AppliedLanguage"),
        };
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
