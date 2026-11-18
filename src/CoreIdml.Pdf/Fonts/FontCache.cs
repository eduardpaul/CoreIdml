using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace CoreIdml.Pdf.Fonts;

/// <summary>What scanning a font file found about one face: its names and weight (see <see cref="FontLibrary"/>).</summary>
internal sealed record FaceInfo(int Face, string[] FamilyKeys, string[] StyleKeys, string FullName, string PostScriptName, int Weight, bool Bold, bool Italic);

/// <summary>
/// Font files scanned and parsed once per process instead of once per export or measurer (every report creates
/// both, and the system font folders of a phone hold hundreds of files). Only the built-in sources can be cached:
/// a <see cref="DirectoryFontSource"/> file is known by its path, size and modification time, a
/// <see cref="MemoryFontSource"/> font by its byte array and id. Parsed faces are held weakly, so fonts nobody uses
/// any more can be collected. Thread-safe; a parsed <see cref="OpenTypeFont"/> is immutable.
/// </summary>
internal static class FontCache
{
    private static readonly ConcurrentDictionary<FileKey, FaceInfo[]> FileFaces = new();
    private static readonly ConcurrentDictionary<(FileKey File, int Face), WeakReference<OpenTypeFont>> FileFonts = new();
    private static readonly ConditionalWeakTable<byte[], FaceInfo[]> MemoryFaces = new();
    private static readonly ConditionalWeakTable<byte[], ConcurrentDictionary<(string Id, int Face), OpenTypeFont>> MemoryFonts = new();

    /// <summary>The faces of a font file scanned before, or null.</summary>
    public static FaceInfo[]? Faces(IFontSource source, string fontId) => Identify(source, fontId) switch
    {
        FileKey file => FileFaces.GetValueOrDefault(file),
        byte[] data => MemoryFaces.TryGetValue(data, out var faces) ? faces : null,
        _ => null,
    };

    public static void RememberFaces(IFontSource source, string fontId, FaceInfo[] faces)
    {
        switch (Identify(source, fontId))
        {
            case FileKey file:
                FileFaces[file] = faces;
                break;
            case byte[] data:
                MemoryFaces.AddOrUpdate(data, faces);
                break;
        }
    }

    /// <summary>The parsed face, from the cache or from <paramref name="load"/> (which is then cached).</summary>
    public static OpenTypeFont Font(IFontSource source, string fontId, int face, Func<OpenTypeFont> load)
    {
        switch (Identify(source, fontId))
        {
            case FileKey file:
                if (FileFonts.TryGetValue((file, face), out var weak) && weak.TryGetTarget(out var cached))
                {
                    return cached;
                }
                var font = load();
                FileFonts[(file, face)] = new WeakReference<OpenTypeFont>(font);
                return font;
            case byte[] data:
                // The id is part of the key: it is the parsed font's source path (and the face's identity in the PDF).
                return MemoryFonts.GetValue(data, static _ => new()).GetOrAdd((fontId, face), _ => load());
            default:
                return load();
        }
    }

    private static object? Identify(IFontSource source, string fontId)
    {
        switch (source)
        {
            case DirectoryFontSource:
                try
                {
                    var info = new FileInfo(fontId);
                    // The id as given (not the full path): it becomes the parsed font's source path, the face's identity in the PDF.
                    return info.Exists ? new FileKey(fontId, info.Length, info.LastWriteTimeUtc.Ticks) : null;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return null;
                }
            case MemoryFontSource memory:
                return memory.Data(fontId);
            default:
                return null;
        }
    }

    private sealed record FileKey(string Path, long Length, long Modified);
}
