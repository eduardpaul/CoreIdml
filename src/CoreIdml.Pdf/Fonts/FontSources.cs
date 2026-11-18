namespace CoreIdml.Pdf;

/// <summary>
/// Where font files come from. Implement it to serve fonts from anywhere the app can read: a folder in app storage
/// (Android, iOS), app assets/resources, a download cache. Font files are .ttf, .otf, .ttc or .otc data.
/// </summary>
public interface IFontSource
{
    /// <summary>Identifiers of the font files offered (paths, asset names, ...), in a stable order.</summary>
    IEnumerable<string> FontIds { get; }

    /// <summary>Opens one font file for reading.</summary>
    Stream Open(string fontId);
}

/// <summary>Font files in a folder and its subfolders (desktop font folders, or app storage on Android/iOS).</summary>
public sealed class DirectoryFontSource : IFontSource
{
    public DirectoryFontSource(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
    }

    public string Directory { get; }

    public IEnumerable<string> FontIds
    {
        get
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                {
                    return [];
                }
                return System.IO.Directory.EnumerateFiles(Directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(IsFontFile)
                    .Order(StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    public Stream Open(string fontId) => File.OpenRead(fontId);

    internal static bool IsFontFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc";
}

/// <summary>Fonts held in memory (downloaded, read from app resources, or picked by the user).</summary>
public sealed class MemoryFontSource : IFontSource
{
    private readonly List<(string Id, byte[] Data)> fonts = [];

    public IEnumerable<string> FontIds => fonts.Select(font => font.Id);

    /// <summary>Adds a font file's bytes; <paramref name="name"/> identifies it in reports (e.g. its file name).</summary>
    public MemoryFontSource Add(string name, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(data);
        fonts.Add(("memory:" + fonts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + name, data));
        return this;
    }

    public MemoryFontSource Add(string name, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Add(name, buffer.ToArray());
    }

    public Stream Open(string fontId) =>
        Data(fontId) is { } data ? new MemoryStream(data, writable: false) : throw new FileNotFoundException("Unknown font.", fontId);

    /// <summary>The bytes of a font added here (they identify it in the font cache), or null.</summary>
    internal byte[]? Data(string fontId)
    {
        foreach (var (id, data) in fonts)
        {
            if (id == fontId)
            {
                return data;
            }
        }
        return null;
    }
}
