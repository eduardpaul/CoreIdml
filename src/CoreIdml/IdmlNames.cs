using System.Xml.Linq;

namespace CoreIdml;

/// <summary>Well-known IDML names and paths.</summary>
public static class IdmlNames
{
    /// <summary>Content of the <c>mimetype</c> entry, which must be the first zip entry and stored uncompressed.</summary>
    public const string MimeType = "application/vnd.adobe.indesign-idml-package";

    /// <summary>Path of the <c>mimetype</c> part.</summary>
    public const string MimeTypePath = "mimetype";

    /// <summary>Path of the manifest part that lists every spread, story and resource of the document.</summary>
    public const string DesignMapPath = "designmap.xml";

    /// <summary>Path of the container part.</summary>
    public const string ContainerPath = "META-INF/container.xml";

    /// <summary>The <c>idPkg</c> namespace of the package wrapper elements (<c>idPkg:Spread</c>, <c>idPkg:Story</c>...).</summary>
    public static readonly XNamespace PackagingNamespace = "http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging";
}
