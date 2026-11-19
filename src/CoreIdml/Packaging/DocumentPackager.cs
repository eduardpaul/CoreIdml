using System.IO.Compression;
using CoreIdml.Dom;

namespace CoreIdml.Packaging;

/// <summary>Where a link's file is: the file found on disk, or null when it is missing.</summary>
public sealed record LinkFile(ImageLink Link, string? Path);

/// <summary>Options for <see cref="DocumentPackager.Write"/>.</summary>
public sealed class PackageOptions
{
    /// <summary>Name of the .idml inside the zip; default "document.idml".</summary>
    public string DocumentName { get; set; } = "document.idml";

    /// <summary>Path of the .idml on disk, to resolve relative links and its Links folder.</summary>
    public string? DocumentPath { get; set; }

    /// <summary>More folders searched by file name for links whose path does not exist.</summary>
    public IList<string> LinkDirectories { get; } = [];

    /// <summary>Font files to include under "Document fonts/" (choose them yourself: font licences vary).</summary>
    public IList<string> FontFiles { get; } = [];
}

/// <summary>What <see cref="DocumentPackager.Write"/> put in the zip.</summary>
public sealed class PackageReport
{
    /// <summary>Link id → zip entry ("Links/photo.jpg").</summary>
    public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);

    /// <summary>Links whose file was not found (by id), with their URI.</summary>
    public Dictionary<string, string> MissingLinks { get; } = new(StringComparer.Ordinal);

    public List<string> Fonts { get; } = [];

    public List<string> Entries { get; } = [];
}

/// <summary>
/// InDesign's File &gt; Package for IDML: the document plus the files it links, in one zip, with a "Links" folder next
/// to the .idml (where InDesign and CoreIdml.Pdf look for missing links) and optional fonts in "Document fonts".
/// </summary>
public static class DocumentPackager
{
    /// <summary>
    /// Finds each link's file: its own path (file: URIs and plain paths; relative ones against the document's folder),
    /// else a file of the same name in the document's Links folder or in <paramref name="directories"/>.
    /// </summary>
    public static IReadOnlyList<LinkFile> ResolveLinks(IdmlDocument document, string? documentPath, IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(directories);
        var folders = new List<string>();
        var documentFolder = documentPath is null ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(documentPath));
        if (documentFolder is not null)
        {
            folders.Add(documentFolder);
            folders.Add(System.IO.Path.Combine(documentFolder, "Links"));
        }
        folders.AddRange(directories);
        var byName = FilesByName(folders);
        return document.Links.Select(link =>
        {
            var local = LocalPath(link.Uri);
            if (local is not null && !System.IO.Path.IsPathRooted(local) && documentFolder is not null)
            {
                local = System.IO.Path.Combine(documentFolder, local);
            }
            if (local is not null && File.Exists(local))
            {
                return new LinkFile(link, System.IO.Path.GetFullPath(local));
            }
            return new LinkFile(link, FindByName(link, byName));
        }).ToList();
    }

    /// <summary>
    /// Finds each link's file by name only: a file of the same name (case-insensitive) in <paramref name="directories"/>,
    /// the first folder first, whether or not the link's own path exists (InDesign's Relink to Folder).
    /// </summary>
    public static IReadOnlyList<LinkFile> FindInFolders(IdmlDocument document, IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(directories);
        var byName = FilesByName(directories);
        return document.Links.Select(link => new LinkFile(link, FindByName(link, byName))).ToList();
    }

    /// <summary>The files of the existing <paramref name="folders"/> by name; the first folder wins a name.</summary>
    private static Dictionary<string, string> FilesByName(IEnumerable<string> folders)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                byName.TryAdd(System.IO.Path.GetFileName(file), file);
            }
        }
        return byName;
    }

    private static string? FindByName(ImageLink link, Dictionary<string, string> byName) =>
        FileName(link.Uri) is { } name && byName.TryGetValue(name, out var found) ? found : null;

    /// <summary>Writes the package zip: the document, its linked files under Links/, and the font files given.</summary>
    public static PackageReport Write(IdmlDocument document, Stream zip, PackageOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(options);
        var report = new PackageReport();
        using var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true);
        var documentEntry = archive.CreateEntry(options.DocumentName, CompressionLevel.NoCompression);
        using (var stream = documentEntry.Open())
        {
            document.Save(stream);
        }
        report.Entries.Add(options.DocumentName);

        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // source file → entry
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (link, path) in ResolveLinks(document, options.DocumentPath, options.LinkDirectories))
        {
            if (path is null)
            {
                report.MissingLinks.TryAdd(link.Id, link.Uri);
                continue;
            }
            if (!written.TryGetValue(path, out var entry))
            {
                entry = "Links/" + Unique(names, System.IO.Path.GetFileName(path));
                // Images are compressed already.
                archive.CreateEntryFromFile(path, entry, CompressionLevel.NoCompression);
                written[path] = entry;
                report.Entries.Add(entry);
            }
            report.Links.TryAdd(link.Id, entry);
        }
        foreach (var font in options.FontFiles)
        {
            var entry = "Document fonts/" + Unique(names, System.IO.Path.GetFileName(font));
            archive.CreateEntryFromFile(font, entry, CompressionLevel.Optimal);
            report.Fonts.Add(entry);
            report.Entries.Add(entry);
        }
        return report;
    }

    /// <summary>
    /// The local path a link URI names ("file:/C:/x.jpg", "file:///srv/x.jpg", "file://server/share/x.jpg" as a UNC
    /// path, a plain path), or null for other schemes.
    /// </summary>
    public static string? LocalPath(string uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }
        if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = Uri.UnescapeDataString(uri[5..]);
            if (rest.StartsWith("//", StringComparison.Ordinal) && !rest.StartsWith("///", StringComparison.Ordinal))
            {
                // "file://host/path": a UNC path, except for localhost (and a drive letter written after "//").
                var slash = rest.IndexOf('/', 2);
                var host = slash < 0 ? rest[2..] : rest[2..slash];
                var isDrive = host.Length == 2 && host[1] == ':';
                if (host.Length > 0 && !isDrive)
                {
                    var path = slash < 0 ? string.Empty : rest[slash..];
                    if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        return "//" + host + path;
                    }
                    rest = path;
                }
            }
            rest = rest.TrimStart('/');
            // "file:/C:/x" or "file:///C:/x" on Windows; "file:/srv/x" elsewhere.
            return rest.Length > 1 && rest[1] == ':' ? rest : "/" + rest;
        }
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !parsed.IsFile && parsed.Scheme.Length > 1 ? null : uri;
    }

    private static string? FileName(string uri)
    {
        var path = Uri.UnescapeDataString(uri);
        var slash = path.LastIndexOfAny(['/', '\\', ':']);
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.Length == 0 ? null : name;
    }

    private static string Unique(HashSet<string> names, string name)
    {
        var candidate = name;
        for (var i = 2; !names.Add(candidate); i++)
        {
            candidate = System.IO.Path.GetFileNameWithoutExtension(name) + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + System.IO.Path.GetExtension(name);
        }
        return candidate;
    }
}
