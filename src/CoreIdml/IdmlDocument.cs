using System.Reflection;
using System.Xml.Linq;
using CoreIdml.Packaging;

namespace CoreIdml;

/// <summary>
/// An InDesign document: the package plus the model read from its manifest (<c>designmap.xml</c>).
/// </summary>
/// <remarks>
/// This is the anchor of the document model. Today it exposes the manifest; spreads, pages, stories, styles and the
/// rest of the model are typed views over the live XML of <see cref="Package"/> and hang off this class.
/// A document is not thread-safe.
/// </remarks>
public sealed class IdmlDocument
{
    private IdmlDocument(IdmlPackage package)
    {
        Package = package;
    }

    /// <summary>Version of this library.</summary>
    public static string LibraryVersion { get; } =
        typeof(IdmlDocument).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>The package behind the document; every part stays reachable through it.</summary>
    public IdmlPackage Package { get; }

    /// <summary>The manifest part, <c>designmap.xml</c>.</summary>
    public XDocument DesignMap => Package.GetXml(IdmlNames.DesignMapPath);

    /// <summary>The InDesign DOM version the document was written with (<c>7.5</c> is CS5.5), if it says.</summary>
    public string? DomVersion => (string?)DesignMap.Root?.Attribute("DOMVersion");

    /// <summary>Paths of the spread parts, in document order.</summary>
    public IReadOnlyList<string> SpreadPaths => ReferencedParts("Spread");

    /// <summary>Paths of the master spread parts, in document order.</summary>
    public IReadOnlyList<string> MasterSpreadPaths => ReferencedParts("MasterSpread");

    /// <summary>Paths of the story parts, in manifest order.</summary>
    public IReadOnlyList<string> StoryPaths => ReferencedParts("Story");

    /// <summary>Opens an .idml file.</summary>
    /// <exception cref="IdmlException">The file is not an IDML package.</exception>
    public static IdmlDocument Open(string path) => new(IdmlPackage.Open(path));

    /// <summary>Opens an .idml file held in memory. The data is not kept after the call.</summary>
    /// <exception cref="IdmlException">The data is not an IDML package.</exception>
    public static IdmlDocument Open(ReadOnlyMemory<byte> data) => new(IdmlPackage.Open(data));

    /// <summary>Opens an .idml file from a stream, which is left open.</summary>
    /// <exception cref="IdmlException">The stream does not hold an IDML package.</exception>
    public static IdmlDocument Open(Stream stream) => new(IdmlPackage.Open(stream));

    /// <summary>Opens an .idml file from a stream without blocking on it. The stream is left open.</summary>
    /// <exception cref="IdmlException">The stream does not hold an IDML package.</exception>
    public static async Task<IdmlDocument> OpenAsync(Stream stream, CancellationToken cancellationToken = default) =>
        new(await IdmlPackage.OpenAsync(stream, cancellationToken).ConfigureAwait(false));

    /// <summary>Writes the document to a file.</summary>
    public void Save(string path, IdmlSaveOptions? options = null) => Package.Save(path, options);

    /// <summary>Writes the document to a stream, which is left open.</summary>
    public void Save(Stream stream, IdmlSaveOptions? options = null) => Package.Save(stream, options);

    /// <summary>Writes the document to a stream without blocking on it. The stream is left open.</summary>
    public Task SaveAsync(Stream stream, IdmlSaveOptions? options = null, CancellationToken cancellationToken = default) =>
        Package.SaveAsync(stream, options, cancellationToken);

    /// <summary>The document as the bytes of an .idml file.</summary>
    public byte[] ToArray(IdmlSaveOptions? options = null) => Package.ToArray(options);

    /// <summary>The <c>src</c> of every <c>idPkg:{localName}</c> entry of the manifest.</summary>
    private List<string> ReferencedParts(string localName)
    {
        List<string> paths = [];
        foreach (var reference in DesignMap.Root?.Elements(IdmlNames.PackagingNamespace + localName) ?? [])
        {
            if ((string?)reference.Attribute("src") is { Length: > 0 } source)
            {
                paths.Add(source);
            }
        }
        return paths;
    }
}
