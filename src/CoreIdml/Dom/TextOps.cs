using System.Text;
using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>
/// Text operations shared by every container of paragraphs (stories, table cells, tagged XML elements).
/// IDML text is a sequence of ParagraphStyleRange &gt; CharacterStyleRange &gt; Content/Br elements; a
/// <c>Br</c> ends a paragraph, U+2028 inside Content is a forced line break.
/// </summary>
internal static class TextOps
{
    public const char ForcedLineBreak = '\u2028';

    private static readonly HashSet<string> NestedScopes = ["Cell", "Footnote", "Note", "Table"];

    public static string GetText(XElement container)
    {
        var builder = new StringBuilder();
        AppendText(container, builder);
        return builder.ToString();
    }

    private static void AppendText(XElement element, StringBuilder builder)
    {
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Content":
                    builder.Append(child.Value);
                    break;
                case "Br":
                    builder.Append('\n');
                    break;
                case "Properties":
                    break;
                case var name when NestedScopes.Contains(name):
                    // Tables, footnotes and notes have their own text.
                    break;
                case "Change" when TrackedChanges.IsDeletion(child):
                    // Deleted tracked text is not part of the current text (see TrackedChanges).
                    break;
                default:
                    AppendText(child, builder);
                    break;
            }
        }
    }

    /// <summary>Content/Br nodes for a piece of text: "\n" becomes a paragraph break.</summary>
    public static IEnumerable<XElement> TextNodes(string text)
    {
        var normalized = IdmlXml.SanitizeText(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0)
            {
                yield return new XElement("Content", lines[i]);
            }
            if (i < lines.Length - 1)
            {
                yield return new XElement("Br");
            }
        }
    }

    /// <summary>The ParagraphStyleRange elements directly holding this container's text (through XMLElement wrappers).</summary>
    public static IEnumerable<XElement> ParagraphRanges(XElement container) =>
        container.Elements().SelectMany(child => child.Name.LocalName switch
        {
            "ParagraphStyleRange" => [child],
            "XMLElement" => ParagraphRanges(child),
            _ => Enumerable.Empty<XElement>(),
        });

    /// <summary>Appends a new, empty paragraph range, ending the previous paragraph with a Br when needed.</summary>
    public static XElement AppendParagraph(XElement container, string paragraphStyle)
    {
        EnsureTrailingBreak(container);
        var paragraph = new XElement("ParagraphStyleRange", new XAttribute("AppliedParagraphStyle", paragraphStyle));
        container.Add(paragraph);
        return paragraph;
    }

    private static void EnsureTrailingBreak(XElement container)
    {
        var lastRange = ParagraphRanges(container).LastOrDefault();
        if (lastRange is null || !HasContent(lastRange))
        {
            return;
        }
        var lastRun = lastRange.Elements("CharacterStyleRange").LastOrDefault();
        if (lastRun is null)
        {
            lastRun = new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle));
            lastRange.Add(lastRun);
        }
        if (lastRun.Elements().LastOrDefault()?.Name.LocalName != "Br")
        {
            lastRun.Add(new XElement("Br"));
        }
    }

    private static bool HasContent(XElement range) =>
        range.Descendants().Any(element => element.Name.LocalName is "Content" or "Table" or "Br" or "Rectangle" or "TextFrame" or "Group");

    /// <summary>
    /// Replaces the container's text with <paramref name="text"/>, keeping the formatting of its first paragraph and
    /// first run (so a template's styled placeholder keeps its look). XML tags wrapping the whole text are kept.
    /// </summary>
    public static void SetText(XElement container, string text, string? paragraphStyle = null)
    {
        // A story fully wrapped by one XMLElement (a tagged story): rewrite inside the tag so the structure survives.
        if (!container.Elements("ParagraphStyleRange").Any()
            && container.Elements("XMLElement").ToList() is [var wrapper]
            && ParagraphRanges(wrapper).Any())
        {
            SetText(wrapper, text, paragraphStyle);
            return;
        }

        var ranges = container.Elements("ParagraphStyleRange").ToList();
        if (ranges.Count == 0 && container.Elements("CharacterStyleRange").Any())
        {
            // An XMLElement inside a paragraph: holds runs and, in rich text, nested inline tags (bold, sup, ...).
            // Everything is replaced by one run formatted like the first, so no stale formatted text survives.
            ReplaceRuns(container, container.Elements("CharacterStyleRange").First(), text);
            return;
        }
        if (ranges.Count == 0 && container.Name.LocalName == "XMLElement")
        {
            // An XMLElement inside a run: holds Content/Br directly.
            container.Nodes().Where(node => node is not XElement { Name.LocalName: "XMLAttribute" or "Properties" }).Remove();
            container.Add(TextNodes(text));
            return;
        }

        var templateRange = ranges.FirstOrDefault();
        var templateRun = templateRange?.Elements("CharacterStyleRange").FirstOrDefault();
        var newRange = templateRange is null
            ? new XElement("ParagraphStyleRange", new XAttribute("AppliedParagraphStyle", paragraphStyle ?? IdmlNames.BasicParagraphStyle))
            : ShallowCopy(templateRange);
        if (paragraphStyle is not null)
        {
            newRange.SetAttributeValue("AppliedParagraphStyle", paragraphStyle);
        }
        var newRun = templateRun is null
            ? new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle))
            : ShallowCopy(templateRun);
        newRun.Add(TextNodes(text));
        newRange.Add(newRun);

        if (templateRange is not null)
        {
            templateRange.AddBeforeSelf(newRange);
        }
        else
        {
            container.Add(newRange);
        }
        foreach (var range in ranges)
        {
            range.Remove();
        }
    }

    private static void ReplaceRuns(XElement container, XElement templateRun, string text)
    {
        var newRun = ShallowCopy(templateRun);
        newRun.Add(TextNodes(text));
        container.Nodes().Where(node => node is not XElement { Name.LocalName: "XMLAttribute" or "Properties" }).Remove();
        container.Add(newRun);
    }

    /// <summary>
    /// Replaces only the element's own text (not text inside nested tags), keeping child tags and the paragraph
    /// breaks between them. This is how XML import writes a parent element that also has tagged children.
    /// </summary>
    public static void SetOwnText(XElement element, string text)
    {
        var own = OwnContents(element).ToList();
        var nested = element.Descendants("XMLElement").Any(child => NearestTag(child) == element);
        if (!nested)
        {
            SetText(element, text);
            return;
        }
        if (own.Count > 0)
        {
            var nodes = TextNodes(text).ToList();
            own[0].AddBeforeSelf(nodes);
            foreach (var content in own)
            {
                content.Remove();
            }
            return;
        }
        if (text.Length == 0)
        {
            return;
        }
        // No own text yet: put it in front of the first child tag, in a run formatted like the first run around.
        var firstChild = element.Elements().First(child => child.Name.LocalName is not ("XMLAttribute" or "Properties"));
        var template = element.Descendants("CharacterStyleRange").FirstOrDefault() ?? element.Ancestors("CharacterStyleRange").FirstOrDefault();
        if (firstChild.Name.LocalName == "ParagraphStyleRange")
        {
            var run = firstChild.Elements("CharacterStyleRange").FirstOrDefault();
            if (run is null)
            {
                run = new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle));
                firstChild.AddFirst(run);
            }
            var anchor = run.Elements().FirstOrDefault(child => child.Name.LocalName != "Properties");
            if (anchor is null)
            {
                run.Add(TextNodes(text));
            }
            else
            {
                anchor.AddBeforeSelf(TextNodes(text));
            }
        }
        else if (template is not null && element.Ancestors("CharacterStyleRange").FirstOrDefault() is null)
        {
            var run = ShallowCopy(template);
            run.Add(TextNodes(text));
            firstChild.AddBeforeSelf(run);
        }
        else
        {
            firstChild.AddBeforeSelf(TextNodes(text));
        }
    }

    /// <summary>Content/Br elements whose nearest enclosing tag is <paramref name="element"/>.</summary>
    public static IEnumerable<XElement> OwnContents(XElement element) =>
        element.Descendants().Where(node => node.Name.LocalName is "Content" && NearestTag(node) == element
            && !node.Ancestors().TakeWhile(ancestor => ancestor != element).Any(ancestor => NestedScopes.Contains(ancestor.Name.LocalName)));

    private static XElement? NearestTag(XElement node) => node.Ancestors("XMLElement").FirstOrDefault();

    /// <summary>The element with its attributes and Properties, without content.</summary>
    public static XElement ShallowCopy(XElement element)
    {
        var copy = new XElement(element.Name, element.Attributes());
        if (element.Element(IdmlXml.Properties) is { } properties)
        {
            copy.Add(new XElement(properties));
        }
        return copy;
    }

    /// <summary>
    /// Replaces every occurrence of <paramref name="find"/> under <paramref name="scope"/>. A match may span several
    /// runs (InDesign often splits words when formatting changes or after edits); the replacement takes the
    /// formatting of the run where the match starts. Matches never cross paragraph breaks or table cells. Deleted
    /// tracked text is neither searched nor changed: matches are found in the text InDesign shows.
    /// </summary>
    public static int Replace(XElement scope, string find, string replacement, StringComparison comparison)
    {
        ArgumentException.ThrowIfNullOrEmpty(find);
        replacement = IdmlXml.SanitizeText(replacement);
        var count = 0;
        var scopes = new List<XElement> { scope };
        scopes.AddRange(scope.Descendants().Where(element => element.Name.LocalName is "Cell" or "Footnote" or "Note" && !TrackedChanges.IsDeleted(element)));
        foreach (var current in scopes)
        {
            foreach (var paragraph in Paragraphs(current))
            {
                count += ReplaceInSegments(paragraph, find, replacement, comparison);
            }
        }
        return count;
    }

    /// <summary>The Content elements of each paragraph in the scope (nested scopes excluded).</summary>
    private static IEnumerable<List<XElement>> Paragraphs(XElement scope)
    {
        var current = new List<XElement>();
        foreach (var element in WalkScope(scope))
        {
            // Paragraph breaks and special characters (page numbers, ...) end a segment: matches never span them.
            if (element.Name.LocalName == "Br" || element.Nodes().OfType<XProcessingInstruction>().Any())
            {
                if (current.Count > 0)
                {
                    yield return current;
                }
                current = [];
            }
            else
            {
                current.Add(element);
            }
        }
        if (current.Count > 0)
        {
            yield return current;
        }
    }

    private static IEnumerable<XElement> WalkScope(XElement element)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;
            if (name is "Content" or "Br")
            {
                yield return child;
            }
            else if (name != "Properties" && !NestedScopes.Contains(name) && !TrackedChanges.IsDeletion(child))
            {
                foreach (var nested in WalkScope(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static int ReplaceInSegments(List<XElement> contents, string find, string replacement, StringComparison comparison)
    {
        // Content elements holding processing instructions (special characters) are left alone.
        var segments = contents.Where(content => !content.Nodes().OfType<XProcessingInstruction>().Any()).ToList();
        if (segments.Count == 0)
        {
            return 0;
        }
        var texts = segments.Select(segment => segment.Value).ToList();
        var joined = string.Concat(texts);
        var starts = new int[texts.Count];
        for (int i = 0, offset = 0; i < texts.Count; i++)
        {
            starts[i] = offset;
            offset += texts[i].Length;
        }

        var matches = new List<int>();
        for (var index = joined.IndexOf(find, comparison); index >= 0; index = joined.IndexOf(find, index + find.Length, comparison))
        {
            matches.Add(index);
            if (index + find.Length >= joined.Length)
            {
                break;
            }
        }
        if (matches.Count == 0)
        {
            return 0;
        }

        // Right to left so earlier offsets stay valid.
        for (var m = matches.Count - 1; m >= 0; m--)
        {
            var matchStart = matches[m];
            var matchEnd = matchStart + find.Length;
            var first = true;
            for (var i = 0; i < texts.Count; i++)
            {
                var segmentStart = starts[i];
                var segmentEnd = segmentStart + texts[i].Length;
                if (segmentEnd <= matchStart || segmentStart >= matchEnd)
                {
                    continue;
                }
                var cutStart = Math.Max(matchStart, segmentStart) - segmentStart;
                var cutEnd = Math.Min(matchEnd, segmentEnd) - segmentStart;
                texts[i] = string.Concat(texts[i].AsSpan(0, cutStart), first ? replacement : string.Empty, texts[i].AsSpan(cutEnd));
                first = false;
            }
        }

        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Value != texts[i])
            {
                SetContentText(segments[i], texts[i]);
            }
        }
        return matches.Count;
    }

    /// <summary>Sets a Content element's text; "\n" in the new text becomes real paragraph breaks.</summary>
    private static void SetContentText(XElement content, string text)
    {
        if (!text.Contains('\n', StringComparison.Ordinal) && !text.Contains('\r', StringComparison.Ordinal))
        {
            content.Value = text;
            return;
        }
        var nodes = TextNodes(text).ToList();
        content.AddBeforeSelf(nodes);
        content.Remove();
    }
}
