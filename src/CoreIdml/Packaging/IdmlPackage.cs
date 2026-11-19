using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CoreIdml.Packaging;

/// <summary>
/// The zip container of an IDML file (the "package" layer, like <c>System.IO.Packaging</c> for OpenXML).
/// Parts are kept as the bytes they were read with until somebody asks for their XML, and a part whose XML was
/// never changed is written back byte for byte, so editing one story does not rewrite the rest of a designer's file.
/// </summary>
/// <remarks>
/// A package is not thread-safe. Everything here works on memory: the only I/O is reading the source and writing the
/// result, which is why the asynchronous methods await just that.
/// </remarks>
public sealed partial class IdmlPackage
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly List<string> order = [];
    private readonly Dictionary<string, Part> parts = new(StringComparer.Ordinal);

    private IdmlPackage()
    {
    }

    /// <summary>Part paths in package order.</summary>
    public IReadOnlyList<string> PartPaths => order;

    /// <summary>A new package holding only the <c>mimetype</c> part.</summary>
    public static IdmlPackage Create()
    {
        var package = new IdmlPackage();
        package.SetBytes(IdmlNames.MimeTypePath, Encoding.ASCII.GetBytes(IdmlNames.MimeType));
        return package;
    }

    /// <summary>Opens an .idml file.</summary>
    /// <exception cref="IdmlException">The file is not an IDML package.</exception>
    public static IdmlPackage Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        return Open(stream);
    }

    /// <summary>Opens an .idml file held in memory. The data is not kept after the call.</summary>
    /// <exception cref="IdmlException">The data is not an IDML package.</exception>
    public static IdmlPackage Open(ReadOnlyMemory<byte> data)
    {
        using var stream = new ReadOnlyMemoryStream(data);
        return Open(stream);
    }

    /// <summary>Opens an .idml file from a stream, which is left open.</summary>
    /// <exception cref="IdmlException">The stream does not hold an IDML package.</exception>
    public static IdmlPackage Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var package = new IdmlPackage();
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue; // a directory entry
                }
                using var entryStream = entry.Open();
                var data = ReadAll(entryStream, entry.Length);
#if !NET11_0_OR_GREATER
                // .NET 11 checks the CRC of every entry it reads; older runtimes do not.
                if (Crc32(data) != entry.Crc32)
                {
                    throw new InvalidDataException($"The CRC of '{entry.FullName}' does not match its data.");
                }
#endif
                var part = new Part(
                    new RawPart(data),
                    modified: false,
                    entry.LastWriteTime,
                    stored: IsStored(entry));
                if (!package.parts.TryAdd(entry.FullName, part))
                {
                    throw new IdmlException(IdmlErrorKind.InvalidFormat, $"Not an IDML package: the entry '{entry.FullName}' appears twice.");
                }
                package.order.Add(entry.FullName);
            }
        }
        catch (InvalidDataException exception)
        {
            // Not a zip, an unsupported compression method, or a failed CRC (checked by .NET 11 on read, by Open itself before).
            throw new IdmlException(IdmlErrorKind.InvalidFormat, $"Not an IDML package: {exception.Message}", exception);
        }
        if (!package.Contains(IdmlNames.DesignMapPath))
        {
            throw new IdmlException(IdmlErrorKind.InvalidFormat, "Not an IDML package: designmap.xml is missing.");
        }
        return package;
    }

    /// <summary>Opens an .idml file from a stream without blocking on it. The stream is left open.</summary>
    /// <exception cref="IdmlException">The stream does not hold an IDML package.</exception>
    public static async Task<IdmlPackage> OpenAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // A zip is read from its end, so the stream is buffered once (the only I/O); the rest is work on memory.
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return Open(buffer);
    }

    /// <summary>Whether the package has a part at <paramref name="path"/>.</summary>
    public bool Contains(string path) => parts.ContainsKey(path);

    /// <summary>The part as XML, parsed on first access with its whitespace kept. Changing it marks the part as modified.</summary>
    /// <exception cref="IdmlException">The part does not exist or is not well-formed XML.</exception>
    public XDocument GetXml(string path) => GetPart(path).GetXml(path);

    /// <summary>The XML of the part if it exists, else <c>null</c>.</summary>
    public XDocument? TryGetXml(string path) => parts.TryGetValue(path, out var part) ? part.GetXml(path) : null;

    /// <summary>Adds or replaces a part with an XML document, which the package then tracks.</summary>
    public void SetXml(string path, XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Replace(path, new EditedPart(document, TrailingNewline: false));
    }

    /// <summary>The bytes of a part as they would be written now (serialized XML for a part that was changed).</summary>
    /// <exception cref="IdmlException">The part does not exist.</exception>
    public ReadOnlyMemory<byte> GetBytes(string path) => GetPart(path).GetBytes();

    /// <summary>Adds or replaces a part with raw bytes. The package keeps the memory; it is not copied.</summary>
    public void SetBytes(string path, ReadOnlyMemory<byte> data) => Replace(path, new RawPart(data));

    /// <summary>Removes a part. Returns <c>false</c> when there was none.</summary>
    public bool Remove(string path)
    {
        if (!parts.Remove(path))
        {
            return false;
        }
        order.Remove(path);
        return true;
    }

    /// <summary>Whether the part was added, replaced or had its XML changed since the package was opened.</summary>
    /// <exception cref="IdmlException">The part does not exist.</exception>
    public bool IsModified(string path) => GetPart(path).IsModified;

    /// <summary>Writes the package to a file.</summary>
    public void Save(string path, IdmlSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // Built in memory first, so a failure while serializing cannot leave a truncated file behind.
        File.WriteAllBytes(path, ToArray(options));
    }

    /// <summary>Writes the package to a stream, which is left open.</summary>
    public void Save(Stream stream, IdmlSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
        {
            Write(stream, options);
            return;
        }
        // Without seeking, ZipArchive adds data descriptors, to mimetype too, and tools that sniff the file type by
        // its first bytes expect none.
        using var buffer = new MemoryStream();
        Write(buffer, options);
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }

    /// <summary>Writes the package to a stream without blocking on it. The stream is left open.</summary>
    public async Task SaveAsync(Stream stream, IdmlSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        Write(buffer, options);
        buffer.Position = 0;
        await buffer.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The package as the bytes of an .idml file.</summary>
    public byte[] ToArray(IdmlSaveOptions? options = null)
    {
        using var buffer = new MemoryStream();
        Write(buffer, options);
        return buffer.ToArray();
    }

    /// <summary>Writes the zip: <c>mimetype</c> first and stored, then every part in package order.</summary>
    private void Write(Stream target, IdmlSaveOptions? options)
    {
        var now = options?.Timestamp ?? TimeProvider.System.GetLocalNow();
        using var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
        if (parts.TryGetValue(IdmlNames.MimeTypePath, out var mimetype))
        {
            WriteEntry(archive, IdmlNames.MimeTypePath, mimetype.GetBytes().Span, CompressionLevel.NoCompression, mimetype.TimestampOr(now));
        }
        else
        {
            WriteEntry(archive, IdmlNames.MimeTypePath, Encoding.ASCII.GetBytes(IdmlNames.MimeType), CompressionLevel.NoCompression, now);
        }
        foreach (var path in order)
        {
            if (path == IdmlNames.MimeTypePath)
            {
                continue;
            }
            var part = parts[path];
            var level = part.Stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            WriteEntry(archive, path, part.GetBytes().Span, level, part.TimestampOr(now));
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, ReadOnlySpan<byte> data, CompressionLevel level, DateTimeOffset timestamp)
    {
        var entry = archive.CreateEntry(path, level);
        entry.LastWriteTime = ClampToZipRange(timestamp);
        using var entryStream = entry.Open();
        entryStream.Write(data);
    }

    /// <summary>Zip stores MS-DOS timestamps, which cover 1980 to 2107 only.</summary>
    private static DateTimeOffset ClampToZipRange(DateTimeOffset timestamp) => timestamp.Year switch
    {
        < 1980 => new DateTimeOffset(1980, 1, 1, 0, 0, 0, timestamp.Offset),
        > 2107 => new DateTimeOffset(2107, 12, 31, 23, 59, 58, timestamp.Offset),
        _ => timestamp,
    };

    /// <summary>
    /// The entry's bytes, read straight into an array of the size the zip directory gives (no growing buffer and
    /// copy); a size that turns out wrong falls back to reading what the stream really holds.
    /// </summary>
    private static byte[] ReadAll(Stream stream, long expectedLength)
    {
        if (expectedLength is < 0 or > 1 << 30)
        {
            return CopyRest(stream, []);
        }
        var data = new byte[expectedLength];
        var read = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
        if (read < data.Length)
        {
            return data[..read];
        }
        var next = stream.ReadByte();
        return next < 0 ? data : CopyRest(stream, [.. data, (byte)next]);
    }

    private static byte[] CopyRest(Stream stream, ReadOnlySpan<byte> start)
    {
        using var buffer = new MemoryStream();
        buffer.Write(start);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Serializes a part the way InDesign writes it: UTF-8 without a BOM, tab indentation, LF line ends.</summary>
    internal static byte[] Serialize(XDocument document, bool trailingNewline = false)
    {
        using var buffer = new MemoryStream();
        buffer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"u8);
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            Encoding = Utf8NoBom,
            Indent = true,
            IndentChars = "\t",
            NewLineChars = "\n",
            // Entitize keeps tabs and line breaks inside attribute values (&#x9; &#xA;) and CR in text, which a reader
            // would otherwise normalize to spaces. InDesign stores footnote separators and print records that way.
            NewLineHandling = NewLineHandling.Entitize,
        };
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            var first = true;
            foreach (var node in document.Nodes())
            {
                if (node is XText)
                {
                    continue;
                }
                if (!first)
                {
                    writer.WriteRaw("\n");
                }
                node.WriteTo(writer);
                first = false;
            }
        }
        if (trailingNewline)
        {
            buffer.WriteByte((byte)'\n');
        }
        return buffer.ToArray();
    }

#if NET11_0_OR_GREATER
    private static bool IsStored(ZipArchiveEntry entry) => entry.CompressionMethod == ZipCompressionMethod.Stored;
#else
    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c;
    })];

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }

    // No CompressionMethod before .NET 11. Deflate output never matches the input length exactly in practice.
    private static bool IsStored(ZipArchiveEntry entry) => entry.CompressedLength == entry.Length;
#endif

    private static XDocument Parse(string path, ReadOnlyMemory<byte> bytes)
    {
        // IDML has no DTD; refusing one keeps entity-expansion attacks out of files that come from anywhere.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var stream = new ReadOnlyMemoryStream(bytes);
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new IdmlException(IdmlErrorKind.InvalidFormat, $"Part '{path}' is not well-formed XML: {exception.Message}", exception);
        }
    }

    private Part GetPart(string path) =>
        parts.TryGetValue(path, out var part)
            ? part
            : throw new IdmlException(IdmlErrorKind.NotFound, $"Part '{path}' does not exist in the package.");

    private void Replace(string path, PartState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains('\\', StringComparison.Ordinal) || path.StartsWith('/') || path.EndsWith('/'))
        {
            throw new ArgumentException($"'{path}' is not a part path: use '/' between folders, with none at either end.", nameof(path));
        }
        if (parts.TryGetValue(path, out var existing))
        {
            // The replacement keeps the place and the compression of the part it replaces.
            parts[path] = new Part(state, modified: true, stored: existing.Stored);
        }
        else
        {
            parts.Add(path, new Part(state, modified: true));
            order.Add(path);
        }
    }
}
