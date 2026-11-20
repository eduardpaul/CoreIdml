using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>One <c>Content</c> element of a story: the unit idmlkit-style edits address by index.</summary>
public sealed class ContentRun : IdmlNode
{
    internal ContentRun(IdmlDocument document, XElement element, int index)
        : base(document, element)
    {
        Index = index;
    }

    public int Index { get; }

    public string Text => Element.Value;

    /// <summary>Display name of the paragraph style applied to the run.</summary>
    public string? ParagraphStyle => StyleCatalog.DisplayNameFromReference(
        Element.Ancestors("ParagraphStyleRange").FirstOrDefault()?.Attribute("AppliedParagraphStyle")?.Value);

    /// <summary>Display name of the character style applied to the run.</summary>
    public string? CharacterStyle => StyleCatalog.DisplayNameFromReference(
        Element.Ancestors("CharacterStyleRange").FirstOrDefault()?.Attribute("AppliedCharacterStyle")?.Value);

    /// <summary>Where the run sits: "body", "cell column:row", "footnote" or "note".</summary>
    public string Scope
    {
        get
        {
            foreach (var ancestor in Element.Ancestors())
            {
                switch (ancestor.Name.LocalName)
                {
                    case "Cell":
                        return "cell " + ancestor.Attribute("Name")?.Value;
                    case "Footnote":
                        return "footnote";
                    case "Note":
                        return "note";
                    case "Story":
                        return "body";
                }
            }
            return "body";
        }
    }

    /// <summary>The run holds special characters stored as processing instructions (page number markers, ...).</summary>
    public bool HasSpecialCharacters => Element.Nodes().OfType<XProcessingInstruction>().Any();

    /// <summary>
    /// "inserted", "deleted" or "moved" when the run was recorded with Track Changes; null otherwise. Deleted runs
    /// keep their index but are not part of the text InDesign shows (or <see cref="Story.Text"/>).
    /// </summary>
    public string? TrackedChange => TrackedChanges.KindOf(Element);

    /// <summary>
    /// Replaces the run's text, keeping the element (and so every later index) even when the text is empty.
    /// "\n" splits the run into paragraphs, which shifts later indices. Runs holding special characters are only
    /// overwritten with <paramref name="force"/>.
    /// </summary>
    public void SetText(string text, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (HasSpecialCharacters && !force)
        {
            throw new IdmlException($"Content run {Index} holds special characters (such as a page number); pass force to overwrite it.");
        }
        var clean = IdmlXml.SanitizeText(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!clean.Contains('\n', StringComparison.Ordinal))
        {
            Element.RemoveNodes();
            Element.Value = clean;
            return;
        }
        var nodes = TextOps.TextNodes(clean).ToList();
        Element.AddBeforeSelf(nodes);
        Element.Remove();
    }

    internal static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
