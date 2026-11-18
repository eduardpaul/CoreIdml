using System.Xml.Linq;

namespace CoreIdml.Dom;

/// <summary>
/// Base of the typed wrappers. Every wrapper is a thin view over a live <see cref="XElement"/>; anything the typed
/// API does not cover is reachable through <see cref="Element"/>, so nothing in the IDML spec is out of reach.
/// </summary>
public abstract class IdmlNode
{
    private protected IdmlNode(IdmlDocument document, XElement element)
    {
        Document = document;
        Element = element;
    }

    public IdmlDocument Document { get; }

    /// <summary>The underlying XML element (escape hatch to the full IDML vocabulary).</summary>
    public XElement Element { get; }

    /// <summary>The element's unique id within the document.</summary>
    public string Id => Element.Attribute("Self")?.Value ?? string.Empty;

    public string? GetAttribute(string name) => Element.Attribute(name)?.Value;

    public void SetAttribute(string name, string? value) => Element.SetAttributeValue(name, value);

    public override string ToString() => $"{Element.Name.LocalName} {Id}";
}
