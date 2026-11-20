using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>A story: the text shown by one text frame or a chain of threaded frames. Stored in its own part.</summary>
public sealed class Story : IdmlNode
{
    internal Story(IdmlDocument document, string partPath, XElement element)
        : base(document, element)
    {
        PartPath = partPath;
    }

    /// <summary>Package path of the part holding this story (e.g. <c>Stories/Story_u1f0.xml</c>).</summary>
    public string PartPath { get; }

    /// <summary>Plain text; paragraphs separated by "\n" (tables, footnotes, notes and deleted tracked text excluded).</summary>
    public string Text => TextOps.GetText(Element);

    /// <summary>Character runs in reading order (runs inside tables included).</summary>
    public IReadOnlyList<TextRun> Runs =>
        Element.Descendants("CharacterStyleRange").Select(run => new TextRun(Document, run)).ToList();

    public IReadOnlyList<Table> Tables => Element.Descendants("Table").Select(table => new Table(Document, table)).ToList();

    /// <summary>Text frames displaying this story, in thread order.</summary>
    public IReadOnlyList<TextFrame> Frames
    {
        get
        {
            var frames = new List<TextFrame>();
            foreach (var spread in Document.Spreads)
            {
                frames.AddRange(spread.TextFrames(Id));
            }
            return ThreadOrder(frames);
        }
    }

    /// <summary>
    /// A story's frames (in document order) in thread order: from the first frame along NextTextFrame, then any others.
    /// The first frame is the first one with no previous frame among <paramref name="frames"/> (none at all, or one
    /// that is not in the list: a broken thread, or master frames listed apart from page frames).
    /// </summary>
    internal static List<TextFrame> ThreadOrder(List<TextFrame> frames)
    {
        var ordered = new List<TextFrame>();
        var ids = frames.Select(frame => frame.Id).ToHashSet(StringComparer.Ordinal);
        var current = frames.FirstOrDefault(frame => frame.PreviousFrameId is not { } previous || !ids.Contains(previous)) ?? frames.FirstOrDefault();
        while (current is not null && !ordered.Contains(current))
        {
            ordered.Add(current);
            var nextId = current.NextFrameId;
            current = nextId is null ? null : frames.FirstOrDefault(frame => frame.Id == nextId);
        }
        ordered.AddRange(frames.Where(frame => !ordered.Any(o => o.Id == frame.Id)));
        return ordered;
    }

    /// <summary>Starts a new paragraph; add text to it with <see cref="Paragraph.AddText"/>.</summary>
    public Paragraph AddParagraph(ParagraphStyle? style = null, ParagraphFormat? format = null)
    {
        var range = TextOps.AppendParagraph(Element, style?.Reference ?? IdmlNames.BasicParagraphStyle);
        format?.ApplyTo(range);
        return new Paragraph(Document, range);
    }

    /// <summary>Appends a paragraph of text ("\n" inside <paramref name="text"/> starts further paragraphs).</summary>
    public Paragraph AddParagraph(string text, ParagraphStyle? style = null, CharacterStyle? characterStyle = null)
    {
        var paragraph = AddParagraph(style);
        paragraph.AddText(text, characterStyle);
        return paragraph;
    }

    /// <summary>
    /// Replaces all text, keeping the formatting of the first paragraph and run (template placeholders keep their
    /// look). Pass <paramref name="style"/> to force a paragraph style.
    /// </summary>
    public void SetText(string text, ParagraphStyle? style = null) => TextOps.SetText(Element, text, style?.Reference);

    /// <summary>
    /// Removes all text, tables and anchored objects. A tag wrapping the whole story (a tagged frame) is kept, empty,
    /// so the XML structure entry pointing at it stays valid.
    /// </summary>
    public void Clear()
    {
        if (StoryWrapper is { } wrapper)
        {
            wrapper.Nodes().Where(node => node is not XElement { Name.LocalName: "XMLAttribute" or "Properties" }).Remove();
            return;
        }
        Element.Elements().Where(element => element.Name.LocalName is "ParagraphStyleRange" or "XMLElement").Remove();
    }

    /// <summary>The XMLElement tagging this whole story, if the story's frame is tagged.</summary>
    internal XElement? StoryWrapper =>
        Element.Elements("XMLElement").FirstOrDefault(element => element.Attribute("XMLContent")?.Value == Id);

    /// <summary>
    /// Every <c>Content</c> element in document order (table cells, footnotes and notes included), indexed from 0.
    /// The same index space idmlkit uses, so indices read from a summary stay valid until the text structure changes.
    /// Runs of deleted tracked text are included (see <see cref="ContentRun.TrackedChange"/>), so indices do not
    /// depend on whether changes are shown.
    /// </summary>
    public IReadOnlyList<ContentRun> ContentRuns =>
        Element.Descendants("Content").Select((content, index) => new ContentRun(Document, content, index)).ToList();

    /// <summary>
    /// Words in the story (text split on whitespace; tables, footnotes and notes included, deleted tracked text not).
    /// </summary>
    public int WordCount => ContentRuns.Where(run => run.TrackedChange != "deleted").Sum(run => ContentRun.CountWords(run.Text));

    /// <summary>Sets the text of content run <paramref name="index"/> (see <see cref="ContentRuns"/>), keeping its formatting.</summary>
    public void SetRunText(int index, string text, bool force = false)
    {
        var runs = ContentRuns;
        if (index < 0 || index >= runs.Count)
        {
            throw new IdmlException(IdmlErrorKind.NotFound, $"Story \"{Id}\" has {runs.Count} content run(s); run index {index} does not exist.");
        }
        runs[index].SetText(text, force);
    }

    /// <summary>Replaces text (matches may span runs, not paragraphs); returns the number of replacements.</summary>
    public int ReplaceText(string find, string replacement, StringComparison comparison = StringComparison.Ordinal) =>
        TextOps.Replace(Element, find, replacement, comparison);

    /// <summary>Appends a table in its own paragraph.</summary>
    public Table AddTable(int rows, int columns, TableOptions? options = null)
    {
        var range = TextOps.AppendParagraph(Element, (options?.CellParagraphStyle ?? null)?.Reference ?? IdmlNames.BasicParagraphStyle);
        var run = new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", IdmlNames.NoCharacterStyle));
        range.Add(run);
        var table = Table.Create(Document, rows, columns, options ?? new TableOptions());
        run.Add(table.Element);
        return table;
    }
}

/// <summary>A paragraph being built (a ParagraphStyleRange element).</summary>
public sealed class Paragraph : IdmlNode
{
    internal Paragraph(IdmlDocument document, XElement element)
        : base(document, element)
    {
    }

    public string Text => TextOps.GetText(Element);

    public string? AppliedStyle => GetAttribute("AppliedParagraphStyle");

    /// <summary>Appends a run of text; "\n" starts a new paragraph with the same style, U+2028 is a forced line break.</summary>
    public Paragraph AddText(string text, CharacterStyle? style = null, CharacterFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var run = NewRun(style, format);
        run.Add(TextOps.TextNodes(text));
        Element.Add(run);
        return this;
    }

    /// <summary>Appends a forced line break (Shift+Enter).</summary>
    public Paragraph AddLineBreak() => AddText(TextOps.ForcedLineBreak.ToString());

    /// <summary>Appends the current page number marker (useful on master pages and footers).</summary>
    public Paragraph AddPageNumber(CharacterStyle? style = null, CharacterFormat? format = null)
    {
        var run = NewRun(style, format);
        run.Add(new XElement("Content", new XProcessingInstruction("ACE", "18")));
        Element.Add(run);
        return this;
    }

    /// <summary>Applies local paragraph formatting on top of the paragraph style.</summary>
    public Paragraph Format(ParagraphFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.ApplyTo(Element);
        return this;
    }

    private static XElement NewRun(CharacterStyle? style, CharacterFormat? format)
    {
        var run = new XElement("CharacterStyleRange", new XAttribute("AppliedCharacterStyle", style?.Reference ?? IdmlNames.NoCharacterStyle));
        format?.ApplyTo(run);
        return run;
    }
}

/// <summary>A run of uniformly formatted text (a CharacterStyleRange element).</summary>
public sealed class TextRun : IdmlNode
{
    internal TextRun(IdmlDocument document, XElement element)
        : base(document, element)
    {
    }

    /// <summary>Text of this run only (Br as "\n"; nested tables excluded).</summary>
    public string Text => TextOps.GetText(Element);

    public string? CharacterStyle => GetAttribute("AppliedCharacterStyle");

    public string? ParagraphStyle => Element.Ancestors("ParagraphStyleRange").FirstOrDefault()?.Attribute("AppliedParagraphStyle")?.Value;

    public string? FontFamily => IdmlXml.GetProperty(Element, "AppliedFont");

    public string? FontStyle => GetAttribute("FontStyle");

    public double? PointSize => IdmlXml.ParseDouble(GetAttribute("PointSize"));

    /// <summary>
    /// Replaces the run's text, keeping its formatting. Only Content and Br elements are replaced; tables, anchored
    /// frames, notes and tags inside the run are kept. "\n" in <paramref name="text"/> becomes a paragraph break.
    /// </summary>
    public void SetText(string text)
    {
        var textNodes = Element.Elements().Where(element => element.Name.LocalName is "Content" or "Br").ToList();
        var nodes = TextOps.TextNodes(text).ToList();
        if (textNodes.Count == 0)
        {
            Element.Add(nodes);
            return;
        }
        textNodes[0].AddBeforeSelf(nodes);
        foreach (var node in textNodes)
        {
            node.Remove();
        }
    }

    public void ApplyFormat(CharacterFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.ApplyTo(Element);
    }
}
