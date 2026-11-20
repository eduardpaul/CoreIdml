using System.Text;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;

namespace CoreIdml.Pdf.Rendering;

/// <summary>
/// What page-dependent markers and text variables print in each frame of a document being exported
/// (<see cref="FrameMarkers"/>), as InDesign 20 composes them (checked against 11-long-markers-variables):
/// <list type="bullet">
/// <item>Next and previous page numbers (jump lines) print the page of the next or previous frame of a story thread:
/// the frame's own thread, else the thread of the frame it overlaps or touches ("Continued on page 4" under the first
/// of three threaded frames on pages 2, 4 and 6).</item>
/// <item>The section marker (<c>&lt;?ACE 19?&gt;</c>) prints the <c>Marker</c> of the page's section.</item>
/// <item>Running headers (<c>MatchParagraphStyleType</c>, <c>MatchCharacterStyleType</c>) print the first or last text
/// in the style on the page. A page without one repeats the previous page's header (pages 3 and 5 show the headings of
/// pages 2 and 4); before the first one there is nothing, not even the text before (page 1).</item>
/// <item>The last page number (<c>LastPageNumberType</c>) prints the name of the last page of the document or of the
/// page's section: on masters InDesign stores "1", and prints "6" on each of six pages.</item>
/// </list>
/// Every other variable (dates, file name, custom text, chapter number, live captions) prints the <c>ResultText</c>
/// InDesign stored, which is the same on every page (the reference prints the stored dates too).
/// </summary>
internal sealed class TextVariables
{
    private readonly IReadOnlyList<Page> pages;
    private readonly Dictionary<string, int> pageIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextFrame> frames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> variables = new(StringComparer.Ordinal);
    // Where each section starts (page index, ascending) and its marker.
    private readonly List<(int Start, string Marker)> sections = [];
    // The paragraphs on each document page in reading order; null until UseLayouts.
    private List<ParagraphModel>[]? paragraphsOnPage;

    public TextVariables(IdmlDocument document)
    {
        pages = document.Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            pageIndex[pages[i].Id] = i;
        }
        foreach (var frame in document.Spreads.SelectMany(spread => spread.AllPageItems).OfType<TextFrame>())
        {
            frames.TryAdd(frame.Id, frame);
        }
        foreach (var variable in document.DesignMap.Elements("TextVariable"))
        {
            if (variable.Attribute("Self")?.Value is { } id)
            {
                variables.TryAdd(id, variable);
            }
        }
        foreach (var section in document.DesignMap.Elements("Section"))
        {
            if (section.Attribute("PageStart")?.Value is { } start && pageIndex.TryGetValue(start, out var index))
            {
                sections.Add((index, section.Attribute("Marker")?.Value ?? string.Empty));
            }
        }
        sections.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    /// <summary>
    /// What <paramref name="frame"/> prints when drawn on <paramref name="page"/> (a document page; a master frame is
    /// composed for each page it is drawn on).
    /// </summary>
    public FrameMarkers For(TextFrame frame, Page? page)
    {
        var thread = Thread(frame);
        var index = page is not null && pageIndex.TryGetValue(page.Id, out var found) ? found : -1;
        return new FrameMarkers(
            PageName(thread?.NextFrameId),
            PageName(thread?.PreviousFrameId),
            index >= 0 && sections.FindLastIndex(section => section.Start <= index) is >= 0 and var section ? sections[section].Marker : string.Empty,
            index < 0 ? null : instance => Text(instance, index));
    }

    /// <summary>
    /// Records where the paragraphs of the document's stories were composed (each story's frames in thread order, with
    /// its layout), which running headers search.
    /// </summary>
    public void UseLayouts(IEnumerable<(IReadOnlyList<TextFrame> Frames, StoryLayout Layout)> stories)
    {
        var placed = new List<(int Page, double Top, double Left, int Story, int Paragraph, ParagraphModel Model)>();
        var storyIndex = 0;
        foreach (var (storyFrames, layout) in stories)
        {
            for (var i = 0; i < layout.ParagraphFrames.Count; i++)
            {
                var frameIndex = layout.ParagraphFrames[i];
                if (frameIndex < 0 || frameIndex >= storyFrames.Count || storyFrames[frameIndex].Page is not { } page
                    || !pageIndex.TryGetValue(page.Id, out var index))
                {
                    continue;
                }
                var bounds = storyFrames[frameIndex].SpreadBounds;
                placed.Add((index, bounds.Top, bounds.Left, storyIndex, i, layout.Paragraphs[i]));
            }
            storyIndex++;
        }
        paragraphsOnPage = new List<ParagraphModel>[pages.Count];
        for (var i = 0; i < pages.Count; i++)
        {
            paragraphsOnPage[i] = [];
        }
        // Reading order on a page: frames top to bottom, then left to right; paragraphs in story order.
        foreach (var paragraph in placed.OrderBy(p => p.Page).ThenBy(p => p.Top).ThenBy(p => p.Left).ThenBy(p => p.Story).ThenBy(p => p.Paragraph))
        {
            paragraphsOnPage[paragraph.Page].Add(paragraph.Model);
        }
    }

    /// <summary>Whether <paramref name="story"/> holds a running header, whose text depends on the composed pages.</summary>
    public bool HasRunningHeader(XElement story) =>
        story.Descendants("TextVariableInstance").Any(instance =>
            Variable(instance)?.Attribute("VariableType")?.Value is "MatchParagraphStyleType" or "MatchCharacterStyleType");

    /// <summary>
    /// The threaded frame whose next and previous pages <paramref name="frame"/>'s jump-line markers print: the frame
    /// itself when threaded, else the threaded frame of another story it overlaps or touches most.
    /// </summary>
    private static TextFrame? Thread(TextFrame frame)
    {
        if (frame.NextFrameId is not null || frame.PreviousFrameId is not null)
        {
            return frame;
        }
        var bounds = frame.SpreadBounds;
        TextFrame? best = null;
        var bestOverlap = -1.0;
        foreach (var candidate in frame.Spread.AllPageItems.OfType<TextFrame>())
        {
            if (candidate.StoryId == frame.StoryId || (candidate.NextFrameId is null && candidate.PreviousFrameId is null))
            {
                continue;
            }
            var other = candidate.SpreadBounds;
            var width = Math.Min(bounds.Right, other.Right) - Math.Max(bounds.Left, other.Left);
            var height = Math.Min(bounds.Bottom, other.Bottom) - Math.Max(bounds.Top, other.Top);
            if (width >= 0 && height >= 0 && width * height > bestOverlap)
            {
                best = candidate;
                bestOverlap = width * height;
            }
        }
        return best;
    }

    private string? PageName(string? frameId) =>
        frameId is not null && frames.TryGetValue(frameId, out var frame) ? frame.Page?.Name : null;

    private XElement? Variable(XElement instance) =>
        instance.Attribute("AssociatedTextVariable")?.Value is { } id && variables.TryGetValue(id, out var variable) ? variable : null;

    /// <summary>The text of a variable on document page <paramref name="page"/>; null to print its <c>ResultText</c>.</summary>
    private string? Text(XElement instance, int page)
    {
        var variable = Variable(instance);
        switch (variable?.Attribute("VariableType")?.Value)
        {
            case "MatchParagraphStyleType" when paragraphsOnPage is not null && variable.Element("MatchParagraphStylePreference") is { } preference:
            {
                var style = preference.Attribute("AppliedParagraphStyle")?.Value;
                return RunningHeader(preference, page, paragraphs => paragraphs.Where(p => p.ParagraphStyle == style).Select(ParagraphText));
            }
            case "MatchCharacterStyleType" when paragraphsOnPage is not null && variable.Element("MatchCharacterStylePreference") is { } preference:
            {
                var style = preference.Attribute("AppliedCharacterStyle")?.Value;
                return RunningHeader(preference, page, paragraphs => paragraphs.SelectMany(p => Runs(p, style)));
            }
            case "LastPageNumberType" when variable.Element("PageNumberVariablePreference") is { } preference
                && preference.Attribute("Format")?.Value is null or "Current":
            {
                var last = pages.Count - 1;
                if (preference.Attribute("Scope")?.Value != "DocumentScope" && sections.FindIndex(section => section.Start > page) is >= 0 and var next)
                {
                    last = sections[next].Start - 1;
                }
                return Wrap(preference, pages[last].Name);
            }
            default:
                return null;
        }
    }

    private string RunningHeader(XElement preference, int page, Func<IEnumerable<ParagraphModel>, IEnumerable<string>> matches)
    {
        var last = preference.Attribute("SearchStrategy")?.Value == "LastOnPage";
        for (var p = page; p >= 0; p--)
        {
            var found = matches(paragraphsOnPage![p]).Where(text => text.Length > 0).ToList();
            if (found.Count > 0)
            {
                return Wrap(preference, Format(last ? found[^1] : found[0], preference));
            }
        }
        return string.Empty;
    }

    private static string Wrap(XElement preference, string value) =>
        (preference.Attribute("TextBefore")?.Value ?? string.Empty) + value + (preference.Attribute("TextAfter")?.Value ?? string.Empty);

    private static string ParagraphText(ParagraphModel paragraph) =>
        string.Concat(paragraph.Items.OfType<TextItem>().Where(item => !item.Generated).Select(item => item.Text));

    /// <summary>The runs of consecutive text in character style <paramref name="style"/> in a paragraph.</summary>
    private static IEnumerable<string> Runs(ParagraphModel paragraph, string? style)
    {
        var run = new StringBuilder();
        foreach (var item in paragraph.Items)
        {
            if (item is TextItem { Generated: false } text && text.CharacterStyle == style)
            {
                run.Append(text.Text);
            }
            else if (run.Length > 0)
            {
                yield return run.ToString();
                run.Clear();
            }
        }
        if (run.Length > 0)
        {
            yield return run.ToString();
        }
    }

    /// <summary>The header's Change Case and Delete End Punctuation options.</summary>
    private static string Format(string text, XElement preference)
    {
        if (preference.Attribute("DeleteEndPunctuation")?.Value == "true")
        {
            text = text.TrimEnd('.', ',', ';', ':', '!', '?', '…');
        }
        return preference.Attribute("ChangeCase")?.Value switch
        {
            "UpperCase" => text.ToUpperInvariant(),
            "LowerCase" => text.ToLowerInvariant(),
            "TitleCase" => Capitalize(text, everyWord: true),
            "SentenceCase" => Capitalize(text, everyWord: false),
            _ => text,
        };
    }

    private static string Capitalize(string text, bool everyWord)
    {
        var result = new StringBuilder(text.Length);
        var start = true;
        foreach (var c in text)
        {
            result.Append(start && char.IsLetter(c) ? char.ToUpperInvariant(c) : c);
            if (char.IsLetter(c))
            {
                start = false;
            }
            else if (everyWord && char.IsWhiteSpace(c))
            {
                start = true;
            }
        }
        return result.ToString();
    }
}
