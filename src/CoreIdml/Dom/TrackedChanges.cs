using System.Xml.Linq;

namespace CoreIdml.Dom;

/// <summary>
/// Text recorded with InDesign's Track Changes: a <c>Change</c> element wraps the text, its <c>ChangeType</c> says
/// how. <c>InsertedText</c> and <c>MovedText</c> (text at the place it was moved to) are part of the document's
/// current text; <c>DeletedText</c> is not: InDesign keeps it only to show in the story editor, and neither its
/// layout nor its PDF export shows it (<c>11-long-tracked-changes</c>: deleted phrases, a replaced word, and a
/// moved phrase, stored as a deletion at the old place plus <c>MovedText</c> at the new one). Accepting or
/// rejecting a change removes the <c>Change</c> element, leaving plain text or nothing.
/// </summary>
internal static class TrackedChanges
{
    /// <summary>The element is a <c>Change</c> holding deleted text.</summary>
    public static bool IsDeletion(XElement element) =>
        element.Name.LocalName == "Change" && element.Attribute("ChangeType")?.Value == "DeletedText";

    /// <summary>The element lies inside deleted text: it is not part of the document's current text.</summary>
    public static bool IsDeleted(XElement element) => element.Ancestors("Change").Any(IsDeletion);

    /// <summary>"inserted", "deleted" or "moved" for text inside a tracked change; null for other text.</summary>
    public static string? KindOf(XElement element) =>
        IsDeleted(element) ? "deleted"
        : element.Ancestors("Change").FirstOrDefault()?.Attribute("ChangeType")?.Value switch
        {
            "InsertedText" => "inserted",
            "MovedText" => "moved",
            _ => null,
        };
}
