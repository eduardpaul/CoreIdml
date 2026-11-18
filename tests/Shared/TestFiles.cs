using System.IO.Compression;
using System.Text;

namespace CoreIdml.TestSupport;

/// <summary>Fixtures shared by every test project (and, as files, by the tests of every language binding).</summary>
internal static class TestFiles
{
    /// <summary>The repository root: the closest folder above the test binaries that holds CoreIdml.slnx.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>A real InDesign export (DOM 10.0): one spread, one master spread, three stories, an XML structure.</summary>
    public static string Article => Path.Combine(RepositoryRoot, "tests", "fixtures", "article-1photo.idml");

    /// <summary>The bytes of <see cref="Article"/>.</summary>
    public static byte[] ArticleBytes => File.ReadAllBytes(Article);

    /// <summary>
    /// A small package built in memory: a stored <c>mimetype</c> first, then the given parts. Without a
    /// <c>designmap.xml</c> among them, a minimal one is added.
    /// </summary>
    public static byte[] BuildPackage(params (string Path, string Content)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, IdmlNames.MimeTypePath, IdmlNames.MimeType, CompressionLevel.NoCompression);
            if (!parts.Any(part => part.Path == IdmlNames.DesignMapPath))
            {
                Add(archive, IdmlNames.DesignMapPath, MinimalDesignMap, CompressionLevel.Optimal);
            }
            foreach (var (path, content) in parts)
            {
                Add(archive, path, content, CompressionLevel.Optimal);
            }
        }
        return buffer.ToArray();
    }

    public const string MinimalDesignMap =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Document xmlns:idPkg=\"http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging\" DOMVersion=\"7.5\" Self=\"d\" />";

    private static void Add(ZipArchive archive, string path, string content, CompressionLevel level)
    {
        var entry = archive.CreateEntry(path, level);
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 2, 3, 4, 6, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CoreIdml.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException($"No CoreIdml.slnx above {AppContext.BaseDirectory}.");
    }
}
