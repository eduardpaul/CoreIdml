using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CoreIdml.Xml;

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

    /// <summary>A two-page magazine editorial exported by InDesign CS5.5.</summary>
    public static string Editorial => Fixture("magazineA-edito.idml");

    /// <summary>SimpleIDML's XML-import template: a tagged module with bold, italic and superscript mapped to character styles.</summary>
    public static string ImportXmlTemplate => Fixture("article-1photo_import-xml.idml");

    /// <summary>The same article after InDesign imported XML with nested inline tags.</summary>
    public static string ImportedNestedXml => Fixture("article-1photo_imported-nested-xml.idml");

    /// <summary>An XML data file from SimpleIDML's import tests.</summary>
    /// <remarks>Read as checked out (LF here; the import must handle CRLF the same way, which a test checks).</remarks>
    public static string Xml(string name) => File.ReadAllText(Fixture(Path.Combine("xml", name)));

    /// <summary>The Liberation fonts (SIL OFL 1.1) used wherever layout must be reproducible.</summary>
    public static string Fonts => Fixture("fonts");

    /// <summary>
    /// A real studio document that is never committed: <c>FICHA_CORESCEM_AP-ML.idml</c> in the folder named by
    /// <c>COREIDML_SAMPLES_DIR</c>, or in <c>real_samples_fichas/</c> at the repository root. <c>null</c> when absent.
    /// </summary>
    public static string? RealSample => RealSampleFile("FICHA_CORESCEM_AP-ML.idml");

    /// <summary>A file of the real samples folder (see <see cref="RealSample"/>), or <c>null</c> when absent.</summary>
    public static string? RealSampleFile(string name)
    {
        var directory = Environment.GetEnvironmentVariable("COREIDML_SAMPLES_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(RepositoryRoot, "real_samples_fichas");
        var path = Path.Combine(directory, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>A file or folder of tests/fixtures.</summary>
    public static string Fixture(string name) => Path.Combine(RepositoryRoot, "tests", "fixtures", name);

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

    /// <summary>The link URI InDesign writes for a local file: "file:C:/dir/a.jpg" on Windows, "file:/dir/a.jpg" elsewhere.</summary>
    public static string LinkUri(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        return "file:" + string.Join('/', full.Split('/').Select(segment => segment.EndsWith(':') ? segment : Uri.EscapeDataString(segment)));
    }

    /// <summary>An absolute path that exists nowhere (rooted on every platform).</summary>
    public static string Nowhere(string name) => Path.Combine(Path.GetTempPath(), "coreidml-nowhere-" + Guid.NewGuid().ToString("N"), name);

    public static string TempPath(string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "coreidml-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
    }

    /// <summary>A valid solid-colour PNG with the given size and resolution.</summary>
    public static byte[] Png(int width, int height, double dpi = 72)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        WriteChunk(output, "IHDR", header);

        var pixelsPerMetre = (uint)Math.Round(dpi / 0.0254);
        var physical = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(physical, pixelsPerMetre);
        BinaryPrimitives.WriteUInt32BigEndian(physical.AsSpan(4), pixelsPerMetre);
        physical[8] = 1;
        WriteChunk(output, "pHYs", physical);

        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + (width * 3)];
            for (var x = 0; x < width; x++)
            {
                row[1 + (x * 3)] = 0x2E;
                row[2 + (x * 3)] = 0x86;
                row[3 + (x * 3)] = 0xC1;
            }
            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }
        WriteChunk(output, "IDAT", raw.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Just enough of a JPEG for header parsing: SOI, JFIF APP0 with density, SOF0.</summary>
    public static byte[] JpegHeader(int width, int height, int dpi)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x01]);
        bytes.AddRange([(byte)(dpi >> 8), (byte)dpi, (byte)(dpi >> 8), (byte)dpi, 0x00, 0x00]);
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03]);
        bytes.AddRange(new byte[9]);
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    /// <summary>Writes a PNG chunk (length, type, data, CRC).</summary>
    public static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32([.. typeBytes, .. data]));
        output.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
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
