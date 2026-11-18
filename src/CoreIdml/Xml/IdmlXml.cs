using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace CoreIdml.Xml;

/// <summary>Well-known IDML names, paths and sentinel values.</summary>
public static class IdmlNames
{
    /// <summary>Content of the <c>mimetype</c> entry; must be the first, uncompressed zip entry.</summary>
    public const string MimeType = "application/vnd.adobe.indesign-idml-package";

    public const string MimeTypePath = "mimetype";
    public const string DesignMapPath = "designmap.xml";
    public const string ContainerPath = "META-INF/container.xml";

    /// <summary>IDML's "nothing" object reference (e.g. <c>PreviousTextFrame="n"</c>).</summary>
    public const string NoReference = "n";

    /// <summary>IDML's empty localizable string.</summary>
    public const string EmptyString = "$ID/";

    public const string NoParagraphStyle = "ParagraphStyle/$ID/[No paragraph style]";
    public const string BasicParagraphStyle = "ParagraphStyle/$ID/NormalParagraphStyle";
    public const string NoCharacterStyle = "CharacterStyle/$ID/[No character style]";
    public const string NormalTextFrameStyle = "ObjectStyle/$ID/[Normal Text Frame]";
    public const string NormalGraphicsFrameStyle = "ObjectStyle/$ID/[Normal Graphics Frame]";
    public const string NoObjectStyle = "ObjectStyle/$ID/[None]";
    public const string BasicTableStyle = "TableStyle/$ID/[Basic Table]";
    public const string NoCellStyle = "CellStyle/$ID/[None]";

    public static readonly XNamespace PackagingNamespace = "http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging";
}

/// <summary>Small helpers over <see cref="XElement"/> for IDML conventions.</summary>
internal static class IdmlXml
{
    public static readonly XName Properties = "Properties";

    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string FormatList(params ReadOnlySpan<double> values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            builder.Append(Format(value));
        }
        return builder.ToString();
    }

    public static double[] ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }
        // Space-separated numbers, parsed in place (geometry and transforms are read for every item drawn).
        var text = value.AsSpan();
        var count = 0;
        foreach (var part in text.Split(' '))
        {
            count += part.End.Value > part.Start.Value ? 1 : 0;
        }
        var result = new double[count];
        var i = 0;
        foreach (var part in text.Split(' '))
        {
            if (part.End.Value > part.Start.Value)
            {
                result[i++] = double.Parse(text[part], NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
        return result;
    }

    public static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    /// <summary>The element's <c>Properties</c> child, created as the first child when missing (IDML requires it first).</summary>
    public static XElement GetOrCreateProperties(XElement element)
    {
        var properties = element.Element(Properties);
        if (properties is null)
        {
            properties = new XElement(Properties);
            element.AddFirst(properties);
        }
        return properties;
    }

    /// <summary>Sets a typed property (<c>&lt;Properties&gt;&lt;Name type="..."&gt;value&lt;/Name&gt;</c>).</summary>
    public static void SetProperty(XElement element, string name, string type, string value)
    {
        var properties = GetOrCreateProperties(element);
        var property = properties.Element(name);
        if (property is null)
        {
            property = new XElement(name);
            properties.Add(property);
        }
        property.RemoveNodes();
        property.SetAttributeValue("type", type);
        property.Value = value;
    }

    public static string? GetProperty(XElement element, string name) =>
        element.Element(Properties)?.Element(name)?.Value;

    public static void RemoveProperty(XElement element, string name)
    {
        var properties = element.Element(Properties);
        properties?.Element(name)?.Remove();
        if (properties is not null && !properties.HasElements)
        {
            properties.Remove();
        }
    }

    /// <summary>Escapes a user name for use inside a <c>Self</c> reference (":" separates style groups).</summary>
    public static string EscapeSelfName(string name) => name.Replace("%", "%25", StringComparison.Ordinal).Replace(":", "%3a", StringComparison.Ordinal);

    /// <summary>Removes characters XML 1.0 cannot carry (control characters other than tab, CR, LF).</summary>
    public static string SanitizeText(string text)
    {
        var needsCleaning = false;
        foreach (var c in text)
        {
            if (!IsValidXmlChar(c))
            {
                needsCleaning = true;
                break;
            }
        }
        if (!needsCleaning)
        {
            return text;
        }
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (IsValidXmlChar(c))
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    private static bool IsValidXmlChar(char c) =>
        c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || char.IsSurrogate(c) || (c >= 0xE000 && c <= 0xFFFD);

    /// <summary>The package wrapper root (<c>idPkg:Spread</c>, <c>idPkg:Story</c>, ...).</summary>
    public static XElement PackageRoot(string localName, string domVersion) =>
        new(IdmlNames.PackagingNamespace + localName,
            new XAttribute(XNamespace.Xmlns + "idPkg", IdmlNames.PackagingNamespace.NamespaceName),
            new XAttribute("DOMVersion", domVersion));
}
