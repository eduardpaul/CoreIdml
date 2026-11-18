using System.Buffers.Binary;
using System.Text;

namespace CoreIdml.Pdf.Fonts;

/// <summary>A requested font replaced by another one (reported in <see cref="PdfExportResult.FontSubstitutions"/>).</summary>
/// <summary>
/// A requested font that was not used as such, like an entry of InDesign's Missing Fonts dialog: what was asked for,
/// what replaced it, why, and where it is used.
/// </summary>
public sealed record FontSubstitution(string RequestedFamily, string RequestedStyle, string UsedFont, string Reason)
{
    /// <summary>Stories (ids) whose text asked for the font.</summary>
    public List<string> Stories { get; } = [];

    /// <summary>Pages (names) showing that text; master stories are listed as "master &lt;name&gt;".</summary>
    public List<string> Pages { get; } = [];

    /// <summary>True when the font was found but its licence (OS/2 fsType) does not allow embedding.</summary>
    public bool EmbeddingRestricted { get; init; }

    public override string ToString() => $"{RequestedFamily} {RequestedStyle} -> {UsedFont} ({Reason})";
}

/// <summary>
/// Finds font faces by InDesign family and style names in the configured folders, with substitution and fallback.
/// Scanning reads only the font headers; faces are parsed on first use. Deterministic for a given set of folders.
/// </summary>
internal sealed class FontLibrary
{
    private static readonly string[] SansFamilies = ["Liberation Sans", "Arimo", "Arial", "Helvetica", "Helvetica Neue", "Roboto", "Noto Sans", "DejaVu Sans", "Open Sans"];
    private static readonly string[] SerifFamilies = ["Liberation Serif", "Tinos", "Times New Roman", "Times", "Noto Serif", "DejaVu Serif", "Georgia"];
    private static readonly string[] MonoFamilies = ["Liberation Mono", "Cousine", "Courier New", "Courier", "DejaVu Sans Mono", "Noto Sans Mono", "Roboto Mono"];
    private static readonly string[] SansHints = ["sans", "grotesk", "grotesque", "gothic", "helvet", "arial", "myriad", "futura", "frutiger", "univers", "din", "roboto", "verdana", "tahoma", "segoe", "calibri", "avenir", "gill", "lato", "montserrat", "inter", "source sans", "proxima"];
    private static readonly string[] MonoHints = ["mono", "courier", "code", "consol", "menlo"];

    private readonly PdfFontOptions options;
    private readonly List<FaceEntry> entries = [];
    private readonly Dictionary<string, FontFace?> loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), FontFace> resolved = [];
    private readonly Dictionary<(int, string), FontFace?> fallbackCache = [];
    private readonly List<FontSubstitution> substitutions = [];

    public FontLibrary(PdfFontOptions options)
    {
        this.options = options ?? new PdfFontOptions();
        var sources = new List<IFontSource>(this.options.Sources);
        sources.AddRange(this.options.FontDirectories.Distinct(StringComparer.Ordinal).Select(directory => new DirectoryFontSource(directory)));
        if (this.options.UseSystemFonts)
        {
            sources.AddRange(SystemFontDirectories().Select(directory => new DirectoryFontSource(directory)));
        }
        foreach (var source in sources)
        {
            Scan(source);
        }
    }

    public IReadOnlyList<FontSubstitution> Substitutions => substitutions;

    /// <summary>Number of faces found in the folders.</summary>
    public int FaceCount => entries.Count;

    /// <summary>The face for an InDesign family and style ("Minion Pro", "Bold Italic"); never null.</summary>
    public FontFace Resolve(string? family, string? style, string? postScriptName = null, string? usedIn = null)
    {
        family = string.IsNullOrWhiteSpace(family) ? "Minion Pro" : family.Trim();
        style = string.IsNullOrWhiteSpace(style) ? "Regular" : style.Trim();
        if (resolved.TryGetValue((family, style), out var cached))
        {
            NoteUse(family, style, usedIn);
            return cached;
        }
        // InDesign tells same-named families apart with a format suffix: "Raleway (TT)", "Montserrat (OTF)".
        var plainFamily = StripFormatSuffix(family);
        var face = (postScriptName is { Length: > 0 } ? FindPostScript(postScriptName) : null) ?? Find(plainFamily, style);
        var restricted = false;
        if (face is not null && !face.EmbeddingAllowed)
        {
            // Installed, but its licence forbids embedding: treat it as missing and say why.
            restricted = true;
            face = null;
        }
        if (face is null)
        {
            foreach (var candidate in CandidateFamilies(plainFamily))
            {
                face = Find(candidate, style);
                if (face is not null && face.EmbeddingAllowed)
                {
                    break;
                }
                face = null;
            }
            face ??= Standard(family, style);
            var reason = restricted ? "installed, but its licence does not allow embedding" : "family not installed";
            substitutions.Add(new FontSubstitution(family, style, face.DisplayName, face.IsStandard ? reason + "; standard PDF font (not embedded)" : reason) { EmbeddingRestricted = restricted });
        }
        else if (!StyleMatches(face, style))
        {
            substitutions.Add(new FontSubstitution(family, style, face.DisplayName, "style not installed"));
        }
        resolved[(family, style)] = face;
        NoteUse(family, style, usedIn);
        return face;
    }

    /// <summary>The family and style are available (installed and embeddable) in the configured sources.</summary>
    public bool IsAvailable(string family, string style) => Find(StripFormatSuffix(family), style) is { EmbeddingAllowed: true };

    private void NoteUse(string family, string style, string? usedIn)
    {
        if (usedIn is null)
        {
            return;
        }
        foreach (var substitution in substitutions)
        {
            if (substitution.RequestedFamily == family && substitution.RequestedStyle == style && !substitution.Stories.Contains(usedIn))
            {
                substitution.Stories.Add(usedIn);
            }
        }
    }

    /// <summary>A face that has a glyph for <paramref name="codePoint"/>, trying the fallback families.</summary>
    public FontFace? FallbackFor(int codePoint, FontFace primary)
    {
        var bold = IsBoldStyle(primary.OpenType?.StyleName ?? primary.StandardName ?? string.Empty);
        var key = (codePoint, primary.Key);
        if (fallbackCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        FontFace? result = null;
        var families = options.FallbackFamilies.Count > 0
            ? options.FallbackFamilies
            : [.. SansFamilies, .. SerifFamilies, .. MonoFamilies];
        foreach (var family in families)
        {
            var face = Find(family, bold ? "Bold" : "Regular");
            if (face is not null && face.EmbeddingAllowed && face.TryMap(codePoint, out _))
            {
                result = face;
                break;
            }
        }
        if (result is null)
        {
            // Any installed face with the glyph (deterministic: scan order).
            foreach (var entry in entries)
            {
                var face = Load(entry);
                if (face is not null && face.EmbeddingAllowed && face.TryMap(codePoint, out _))
                {
                    result = face;
                    break;
                }
            }
        }
        fallbackCache[key] = result;
        return result;
    }

    // ---- matching --------------------------------------------------------------------------------------

    private static string StripFormatSuffix(string family)
    {
        foreach (var suffix in (string[])[" (TT)", " (OTF)", " (T1)", " (TTF)"])
        {
            if (family.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return family[..^suffix.Length];
            }
        }
        return family;
    }

    private FontFace? FindPostScript(string postScriptName)
    {
        var key = Normalize(postScriptName);
        var entry = entries.FirstOrDefault(e => Normalize(e.PostScriptName) == key);
        return entry is null ? null : Load(entry);
    }

    private FontFace? Find(string family, string style)
    {
        var key = Normalize(family);
        var candidates = entries.Where(entry => entry.FamilyKeys.Contains(key)).ToList();
        if (candidates.Count == 0)
        {
            // "Minion Pro Bold" as a family, or a PostScript name ("MinionPro-Bold").
            candidates = entries.Where(entry => Normalize(entry.FullName) == key || Normalize(entry.PostScriptName) == key).ToList();
            if (candidates.Count == 0)
            {
                // "LiberationSans-Bold": PostScript family plus style.
                var dash = family.LastIndexOf('-');
                return dash > 0 && dash < family.Length - 1 ? Find(family[..dash], family[(dash + 1)..]) : null;
            }
        }
        var wanted = NormalizeStyle(style);
        var exact = candidates.FirstOrDefault(entry => entry.StyleKeys.Contains(wanted));
        if (exact is not null && Load(exact) is { } exactFace)
        {
            return exactFace;
        }
        var bold = IsBoldStyle(style);
        var italic = IsItalicStyle(style);
        var weight = WeightOf(style);
        var best = candidates
            .OrderBy(entry => (entry.Italic == italic ? 0 : 1000) + Math.Abs(entry.Weight - weight) + (entry.Bold == bold ? 0 : 50))
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .FirstOrDefault();
        return best is null ? null : Load(best);
    }

    private IEnumerable<string> CandidateFamilies(string family)
    {
        var key = Normalize(family);
        foreach (var (from, to) in options.Substitutions)
        {
            if (Normalize(from) == key)
            {
                yield return to;
            }
        }
        foreach (var fallback in options.FallbackFamilies)
        {
            yield return fallback;
        }
        var lower = family.ToLowerInvariant();
        var chain = MonoHints.Any(lower.Contains) ? MonoFamilies
            : SansHints.Any(lower.Contains) ? SansFamilies
            : SerifFamilies;
        foreach (var candidate in chain)
        {
            yield return candidate;
        }
    }

    private static FontFace Standard(string family, string style)
    {
        var lower = family.ToLowerInvariant();
        var bold = IsBoldStyle(style);
        var italic = IsItalicStyle(style);
        string name;
        if (MonoHints.Any(lower.Contains))
        {
            name = "Courier" + (bold && italic ? "-BoldOblique" : bold ? "-Bold" : italic ? "-Oblique" : string.Empty);
        }
        else if (SansHints.Any(lower.Contains))
        {
            name = "Helvetica" + (bold && italic ? "-BoldOblique" : bold ? "-Bold" : italic ? "-Oblique" : string.Empty);
        }
        else
        {
            name = bold && italic ? "Times-BoldItalic" : bold ? "Times-Bold" : italic ? "Times-Italic" : "Times-Roman";
        }
        return FontFace.FromStandard(name);
    }

    private static bool StyleMatches(FontFace face, string style) =>
        face.OpenType is { } font && (NormalizeStyle(font.StyleName) == NormalizeStyle(style) || NormalizeStyle(font.LegacyStyleName) == NormalizeStyle(style));

    internal static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }
        return builder.ToString();
    }

    internal static string NormalizeStyle(string style)
    {
        var key = Normalize(style).Replace("oblique", "italic", StringComparison.Ordinal);
        return key is "" or "roman" or "book" or "normal" or "plain" ? "regular" : key;
    }

    internal static bool IsBoldStyle(string style)
    {
        var key = Normalize(style);
        return key.Contains("bold", StringComparison.Ordinal) || key.Contains("black", StringComparison.Ordinal) || key.Contains("heavy", StringComparison.Ordinal);
    }

    internal static bool IsItalicStyle(string style)
    {
        var key = Normalize(style);
        return key.Contains("italic", StringComparison.Ordinal) || key.Contains("oblique", StringComparison.Ordinal);
    }

    private static int WeightOf(string style)
    {
        var key = Normalize(style);
        return key switch
        {
            _ when key.Contains("thin", StringComparison.Ordinal) || key.Contains("hairline", StringComparison.Ordinal) => 100,
            _ when key.Contains("extralight", StringComparison.Ordinal) || key.Contains("ultralight", StringComparison.Ordinal) => 200,
            _ when key.Contains("light", StringComparison.Ordinal) => 300,
            _ when key.Contains("semibold", StringComparison.Ordinal) || key.Contains("demibold", StringComparison.Ordinal) => 600,
            _ when key.Contains("extrabold", StringComparison.Ordinal) || key.Contains("ultrabold", StringComparison.Ordinal) => 800,
            _ when key.Contains("black", StringComparison.Ordinal) || key.Contains("heavy", StringComparison.Ordinal) => 900,
            _ when key.Contains("bold", StringComparison.Ordinal) => 700,
            _ when key.Contains("medium", StringComparison.Ordinal) => 500,
            _ => 400,
        };
    }

    // ---- scanning --------------------------------------------------------------------------------------

    private FontFace? Load(FaceEntry entry)
    {
        var key = entry.Path + "#" + entry.Face;
        if (!loaded.TryGetValue(key, out var face))
        {
            try
            {
                face = FontFace.FromOpenType(FontCache.Font(entry.Source, entry.Path, entry.Face, () =>
                {
                    using var stream = entry.Source.Open(entry.Path);
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    return OpenTypeFont.Load(buffer.ToArray(), entry.Face, entry.Path);
                }));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                face = null;
            }
            loaded[key] = face;
        }
        return face;
    }

    private void Scan(IFontSource source)
    {
        foreach (var file in source.FontIds)
        {
            if (FontCache.Faces(source, file) is { } known)
            {
                entries.AddRange(known.Select(info => new FaceEntry(source, file, info)));
                continue;
            }
            try
            {
                using var opened = source.Open(file);
                Stream stream = opened;
                MemoryStream? copy = null;
                if (!opened.CanSeek)
                {
                    copy = new MemoryStream();
                    opened.CopyTo(copy);
                    stream = copy;
                }
                var faces = ReadFaces(stream);
                FontCache.RememberFaces(source, file, faces);
                entries.AddRange(faces.Select(info => new FaceEntry(source, file, info)));
                copy?.Dispose();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException)
            {
                // Unreadable or damaged font: skip it.
            }
        }
    }

    /// <summary>Reads only the table directory, <c>name</c> and <c>OS/2</c> of each face.</summary>
    private static FaceInfo[] ReadFaces(Stream stream)
    {
        var header = ReadAt(stream, 0, 12);
        var offsets = new List<int>();
        if (header.AsSpan(0, 4).SequenceEqual("ttcf"u8))
        {
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
            var table = ReadAt(stream, 12, 4 * Math.Min(count, 256));
            for (var i = 0; i < Math.Min(count, 256); i++)
            {
                offsets.Add((int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(4 * i)));
            }
        }
        else
        {
            offsets.Add(0);
        }
        var result = new List<FaceInfo>();
        for (var face = 0; face < offsets.Count; face++)
        {
            var directoryHeader = ReadAt(stream, offsets[face], 12);
            var tableCount = BinaryPrimitives.ReadUInt16BigEndian(directoryHeader.AsSpan(4));
            var records = ReadAt(stream, offsets[face] + 12, 16 * tableCount);
            (int Offset, int Length) name = default, os2 = default;
            for (var i = 0; i < tableCount; i++)
            {
                var tag = Encoding.ASCII.GetString(records, 16 * i, 4);
                var location = ((int)BinaryPrimitives.ReadUInt32BigEndian(records.AsSpan((16 * i) + 8)), (int)BinaryPrimitives.ReadUInt32BigEndian(records.AsSpan((16 * i) + 12)));
                if (tag == "name")
                {
                    name = location;
                }
                else if (tag == "OS/2")
                {
                    os2 = location;
                }
            }
            if (name.Length == 0)
            {
                continue;
            }
            var names = ReadNames(ReadAt(stream, name.Offset, Math.Min(name.Length, 1 << 20)));
            var weight = 400;
            bool bold = false, italic = false;
            if (os2.Length >= 64)
            {
                var data = ReadAt(stream, os2.Offset, 64);
                weight = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
                var selection = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(62));
                italic = (selection & 1) != 0 || (selection & 0x200) != 0;
                bold = (selection & 0x20) != 0 || weight >= 600;
            }
            string Get(int id) => names.TryGetValue(id, out var value) ? value : string.Empty;
            var family = Get(16) is { Length: > 0 } f16 ? f16 : Get(1);
            var style = Get(17) is { Length: > 0 } s17 ? s17 : Get(2);
            if (family.Length == 0)
            {
                continue;
            }
            italic |= IsItalicStyle(style);
            result.Add(new FaceInfo(
                face,
                new[] { family, Get(1), Get(21) }.Where(n => n.Length > 0).Select(Normalize).Distinct().ToArray(),
                new[] { style, Get(2), Get(22) }.Where(n => n.Length > 0).Select(NormalizeStyle).Distinct().ToArray(),
                Get(4),
                Get(6),
                weight,
                bold,
                italic));
        }
        return [.. result];
    }

    private static Dictionary<int, string> ReadNames(byte[] table)
    {
        var result = new Dictionary<int, (int Priority, string Value)>();
        var count = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(2));
        var storage = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(4));
        for (var i = 0; i < count && 6 + (12 * i) + 12 <= table.Length; i++)
        {
            var record = table.AsSpan(6 + (12 * i));
            var platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(record[2..]);
            var language = BinaryPrimitives.ReadUInt16BigEndian(record[4..]);
            var id = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            var offset = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            if (storage + offset + length > table.Length || id is not (1 or 2 or 4 or 6 or 16 or 17 or 21 or 22))
            {
                continue;
            }
            int priority;
            string value;
            if (platform == 3 && encoding is 0 or 1 or 10)
            {
                value = Encoding.BigEndianUnicode.GetString(table, storage + offset, length);
                priority = language == 0x409 ? 3 : 2;
            }
            else if (platform == 0)
            {
                value = Encoding.BigEndianUnicode.GetString(table, storage + offset, length);
                priority = 2;
            }
            else if (platform == 1 && encoding == 0)
            {
                value = Encoding.Latin1.GetString(table, storage + offset, length);
                priority = 1;
            }
            else
            {
                continue;
            }
            if (!result.TryGetValue(id, out var existing) || existing.Priority < priority)
            {
                result[id] = (priority, value.Trim());
            }
        }
        return result.ToDictionary(pair => pair.Key, pair => pair.Value.Value);
    }

    private static byte[] ReadAt(Stream stream, long offset, int length)
    {
        if (length < 0 || offset < 0 || offset + length > stream.Length)
        {
            throw new InvalidDataException("Truncated font file.");
        }
        stream.Position = offset;
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static IEnumerable<string> SystemFontDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Not OperatingSystem.IsAndroid(): it is false in the linux-bionic native library used by Flutter apps.
        if (AndroidRuntime.IsAndroid)
        {
            return ["/system/fonts", "/product/fonts"];
        }
        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
        {
            return ["/System/Library/Fonts"];
        }
        if (OperatingSystem.IsMacOS())
        {
            return ["/System/Library/Fonts", "/Library/Fonts", Path.Combine(home, "Library", "Fonts")];
        }
        if (OperatingSystem.IsWindows())
        {
            return [Environment.GetFolderPath(Environment.SpecialFolder.Fonts), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts")];
        }
        return ["/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(home, ".fonts"), Path.Combine(home, ".local", "share", "fonts")];
    }

    /// <summary>A face found in one of the sources.</summary>
    private sealed record FaceEntry(IFontSource Source, string Path, int Face, string[] FamilyKeys, string[] StyleKeys, string FullName, string PostScriptName, int Weight, bool Bold, bool Italic)
    {
        public FaceEntry(IFontSource source, string path, FaceInfo info)
            : this(source, path, info.Face, info.FamilyKeys, info.StyleKeys, info.FullName, info.PostScriptName, info.Weight, info.Bold, info.Italic)
        {
        }
    }
}
