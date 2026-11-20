using System.Text;
using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>
/// A paragraph of a story's body (tables, footnotes and notes excluded): its text, paragraph style, and the range of
/// content-run indices (see <see cref="Story.ContentRuns"/>) it covers; -1 when it holds no text.
/// </summary>
public sealed record StoryParagraph(int Index, string Text, string? ParagraphStyle, int FirstRun, int LastRun)
{
    /// <summary>
    /// "bullet" or "numbered" when InDesign draws a bullet or number in front of the paragraph (it is not in
    /// <see cref="Text"/>); null otherwise.
    /// </summary>
    public string? List { get; init; }
}

/// <summary>
/// Applying paragraph and character styles to existing text. Styles are applied the way InDesign does by default:
/// local overrides are kept. Ranges are split where needed, so only the chosen paragraph or run changes.
/// </summary>
public static class StoryStyling
{
    private static readonly HashSet<string> NestedScopes = ["Cell", "Footnote", "Note", "Table"];

    /// <summary>The story's body paragraphs, in order. Paragraph <c>n</c> ends at the <c>n</c>-th paragraph break.</summary>
    public static IReadOnlyList<StoryParagraph> Paragraphs(this Story story)
    {
        ArgumentNullException.ThrowIfNull(story);
        var paragraphs = new List<StoryParagraph>();
        var text = new StringBuilder();
        string? style = null;
        XElement? range = null;
        AttributeResolver? resolver = null;
        int first = -1, last = -1, runIndex = -1;
        void Close()
        {
            string? list = null;
            if (range is not null)
            {
                resolver ??= new AttributeResolver(story.Document);
                list = resolver.Paragraph(range, "BulletsAndNumberingListType") switch
                {
                    "BulletList" => "bullet",
                    "NumberedList" => "numbered",
                    _ => null,
                };
            }
            paragraphs.Add(new StoryParagraph(paragraphs.Count, text.ToString(), style, first, last) { List = list });
            text.Clear();
            style = null;
            range = null;
            first = last = -1;
        }
        foreach (var element in story.Element.Descendants())
        {
            var name = element.Name.LocalName;
            if (name is not ("Content" or "Br"))
            {
                continue;
            }
            var body = IsBody(element, story.Element);
            if (name == "Content")
            {
                runIndex++;
                if (!body)
                {
                    continue;
                }
                text.Append(element.Value);
                first = first < 0 ? runIndex : first;
                last = runIndex;
            }
            if (!body)
            {
                continue;
            }
            range ??= element.Ancestors("ParagraphStyleRange").FirstOrDefault();
            style ??= StyleCatalog.DisplayNameFromReference(range?.Attribute("AppliedParagraphStyle")?.Value);
            if (name == "Br")
            {
                Close();
            }
        }
        Close();
        return paragraphs;
    }

    /// <summary>Applies <paramref name="style"/> to body paragraph <paramref name="paragraphIndex"/> (see <see cref="Paragraphs"/>).</summary>
    public static void ApplyParagraphStyle(this Story story, int paragraphIndex, ParagraphStyle style)
    {
        ArgumentNullException.ThrowIfNull(story);
        ArgumentNullException.ThrowIfNull(style);
        var breaks = BodyBreaks(story);
        if (paragraphIndex < 0 || paragraphIndex > breaks.Count)
        {
            throw new IdmlException(IdmlErrorKind.NotFound, $"Story \"{story.Id}\" has {breaks.Count + 1} paragraph(s); paragraph {paragraphIndex} does not exist.");
        }
        // Split after the paragraph's own break first, then after the previous one: the paragraph ends up alone in its ranges.
        if (paragraphIndex < breaks.Count)
        {
            SplitAfter(breaks[paragraphIndex], Range(breaks[paragraphIndex]), story);
        }
        if (paragraphIndex > 0)
        {
            SplitAfter(breaks[paragraphIndex - 1], Range(breaks[paragraphIndex - 1]), story);
        }
        breaks = BodyBreaks(story);
        var members = story.Element.Descendants()
            .Where(element => element.Name.LocalName is "Content" or "Br" or "Table" && IsBody(element, story.Element) && ParagraphOf(element, breaks) == paragraphIndex);
        var ranges = members.Select(Range).Distinct().ToList();
        if (ranges.Count == 0)
        {
            // An empty last paragraph: the range after the last break, if there is one.
            ranges = paragraphIndex > 0 && Range(breaks[paragraphIndex - 1]).ElementsAfterSelf("ParagraphStyleRange").FirstOrDefault() is { } next ? [next] : [];
        }
        if (ranges.Count == 0)
        {
            throw new IdmlException(IdmlErrorKind.NotFound, $"Paragraph {paragraphIndex} of story \"{story.Id}\" holds no text to style.");
        }
        foreach (var range in ranges)
        {
            range.SetAttributeValue("AppliedParagraphStyle", style.Reference);
        }
    }

    /// <summary>Applies <paramref name="style"/> to content run <paramref name="runIndex"/> only (see <see cref="Story.ContentRuns"/>).</summary>
    public static void ApplyCharacterStyle(this Story story, int runIndex, CharacterStyle style)
    {
        ArgumentNullException.ThrowIfNull(story);
        ArgumentNullException.ThrowIfNull(style);
        var contents = story.Element.Descendants("Content").ToList();
        if (runIndex < 0 || runIndex >= contents.Count)
        {
            throw new IdmlException(IdmlErrorKind.NotFound, $"Story \"{story.Id}\" has {contents.Count} content run(s); run index {runIndex} does not exist.");
        }
        var content = contents[runIndex];
        if (content.Parent is not { Name.LocalName: "CharacterStyleRange" } run)
        {
            throw new IdmlException($"Run {runIndex} of story \"{story.Id}\" is not inside a character range.");
        }
        if (content.ElementsBeforeSelf().LastOrDefault(element => element.Name != IdmlXml.Properties) is { } previous)
        {
            SplitAfter(previous, run, story);
            run = (XElement)content.Parent!;
        }
        SplitAfter(content, run, story);
        run.SetAttributeValue("AppliedCharacterStyle", style.Reference);
    }

    /// <summary>
    /// Moves everything after <paramref name="node"/> inside <paramref name="top"/> into a copy of <paramref name="top"/>
    /// placed right after it (each element in between is copied too, with its attributes and properties).
    /// </summary>
    private static void SplitAfter(XElement node, XElement top, Story story)
    {
        if (!FollowedWithin(node, top))
        {
            return;
        }
        XElement? carried = null;
        XElement current = node;
        for (var level = node.Parent!; ; level = level.Parent!)
        {
            if (level != top && level.Attribute("Self") is not null)
            {
                throw new IdmlException($"Story \"{story.Id}\": the range to style crosses a {level.Name.LocalName} ({level.Attribute("Self")!.Value}), such as an XML tag or hyperlink; style it in InDesign.");
            }
            var copy = TextOps.ShallowCopy(level);
            if (carried is not null)
            {
                copy.Add(carried);
            }
            foreach (var following in current.NodesAfterSelf().ToList())
            {
                following.Remove();
                copy.Add(following);
            }
            carried = copy.Nodes().Any(child => child is not XElement { Name.LocalName: "Properties" }) ? copy : null;
            if (level == top)
            {
                if (carried is not null)
                {
                    top.AddAfterSelf(carried);
                }
                return;
            }
            current = level;
        }
    }

    private static bool FollowedWithin(XElement node, XElement top)
    {
        for (XElement current = node; current != top; current = current.Parent!)
        {
            if (current.NodesAfterSelf().Any(other => other is XElement or XText { Value.Length: > 0 }))
            {
                return true;
            }
        }
        return false;
    }

    private static List<XElement> BodyBreaks(Story story) =>
        story.Element.Descendants("Br").Where(element => IsBody(element, story.Element)).ToList();

    private static int ParagraphOf(XElement element, List<XElement> breaks)
    {
        var index = 0;
        foreach (var brk in breaks)
        {
            if (brk == element || brk.IsAfter(element))
            {
                return index;
            }
            index++;
        }
        return index;
    }

    private static XElement Range(XElement element) =>
        element.Ancestors("ParagraphStyleRange").FirstOrDefault() ?? throw new IdmlException("Text outside a paragraph range.");

    /// <summary>
    /// The element is body text: not in a table, footnote or note, and not deleted tracked text (a deleted paragraph
    /// break does not end a paragraph, as in InDesign's layout).
    /// </summary>
    private static bool IsBody(XElement element, XElement story) =>
        !element.Ancestors().TakeWhile(ancestor => ancestor != story)
            .Any(ancestor => NestedScopes.Contains(ancestor.Name.LocalName) || TrackedChanges.IsDeletion(ancestor));
}
