using System.Globalization;
using System.Text.Json;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// A drop-in visual sample: a folder in COREIDML_PDF_SAMPLES_DIR, or in the git-ignored <c>tests/CoreIdml.Pdf.Tests/Samples</c> (see the README there).
/// </summary>
internal sealed class VisualSample
{
    private VisualSample(string directory)
    {
        Directory = directory;
        Name = Path.GetFileName(directory);
        var config = Path.Combine(directory, "sample.json");
        if (File.Exists(config))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(config));
            var root = json.RootElement;
            Dpi = root.TryGetProperty("dpi", out var dpi) ? dpi.GetInt32() : Dpi;
            Threshold = root.TryGetProperty("threshold", out var threshold) ? threshold.GetDouble() : Threshold;
            Tolerance = root.TryGetProperty("tolerance", out var tolerance) ? tolerance.GetInt32() : Tolerance;
            SystemFonts = root.TryGetProperty("systemFonts", out var system) && system.GetBoolean();
            Skip = root.TryGetProperty("skip", out var skip) ? skip.GetString() : null;
            if (root.TryGetProperty("fontDirectory", out var fontDirectory) && fontDirectory.GetString() is { Length: > 0 } fonts)
            {
                FontDirectory = Path.GetFullPath(Path.Combine(directory, fonts));
            }
            if (root.TryGetProperty("linkDirectories", out var links))
            {
                foreach (var link in links.EnumerateArray())
                {
                    LinkDirectories.Add(link.GetString() ?? string.Empty);
                }
            }
            if (root.TryGetProperty("pages", out var pages))
            {
                Pages = pages.EnumerateArray().Select(p => p.GetInt32()).ToList();
            }
            if (root.TryGetProperty("substitutions", out var substitutions))
            {
                foreach (var property in substitutions.EnumerateObject())
                {
                    Substitutions[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        Idml = System.IO.Directory.EnumerateFiles(directory, "*.idml").Order(StringComparer.Ordinal).FirstOrDefault();
        var pdfs = System.IO.Directory.EnumerateFiles(directory, "*.pdf").Order(StringComparer.Ordinal).ToList();
        ReferencePdf = pdfs.FirstOrDefault(p => Path.GetFileName(p).Equals("reference.pdf", StringComparison.OrdinalIgnoreCase)) ?? pdfs.FirstOrDefault();
        ReferencePngs = System.IO.Directory.EnumerateFiles(directory, "*.png")
            .Select(path => (Path: path, Page: PageNumber(Path.GetFileNameWithoutExtension(path))))
            .Where(entry => entry.Page > 0)
            .ToDictionary(entry => entry.Page, entry => entry.Path);
    }

    public string Directory { get; }

    public string Name { get; }

    public string? Idml { get; }

    public string? ReferencePdf { get; }

    /// <summary>reference-1.png, page-2.png, ... by page number.</summary>
    public IReadOnlyDictionary<int, string> ReferencePngs { get; }

    public int Dpi { get; } = 96;

    public double Threshold { get; } = 0.05;

    public int Tolerance { get; } = 64;

    public bool SystemFonts { get; }

    public string? Skip { get; }

    public IReadOnlyList<int>? Pages { get; }

    public Dictionary<string, string> Substitutions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extra font folder (relative to the sample), besides its own fonts/ folder.</summary>
    public string? FontDirectory { get; }

    /// <summary>Extra folders searched for links by file name (absolute, or relative to the sample folder).</summary>
    public List<string> LinkDirectories { get; } = [];

    /// <summary>IDML present: export it and compare with the reference. Only a PDF: import, re-export, compare.</summary>
    public bool IsExportCase => Idml is not null;

    public static string Root
    {
        get
        {
            if (Environment.GetEnvironmentVariable("COREIDML_PDF_SAMPLES_DIR") is { Length: > 0 } custom)
            {
                return custom;
            }
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "tests", "CoreIdml.Pdf.Tests", "Samples");
                if (System.IO.Directory.Exists(candidate))
                {
                    return candidate;
                }
                if (File.Exists(Path.Combine(directory.FullName, "CoreIdml.Pdf.Tests.csproj")))
                {
                    return Path.Combine(directory.FullName, "Samples");
                }
            }
            return Path.Combine(AppContext.BaseDirectory, "Samples");
        }
    }

    public static IReadOnlyList<VisualSample> Discover() => Discover(Root);

    /// <summary>Every sample folder directly under <paramref name="root"/> (one level; folders without an .idml or PDF are skipped).</summary>
    public static IReadOnlyList<VisualSample> Discover(string root) =>
        System.IO.Directory.Exists(root)
            ? System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).Select(d => new VisualSample(d))
                .Where(s => s.Idml is not null || s.ReferencePdf is not null).ToList()
            : [];

    /// <summary>
    /// Fonts for this sample: its own fonts/ folder, the sample.json font folder, then <paramref name="fallbackFontDirectory"/>
    /// (the tests pass the bundled Liberation fonts); InDesign's default Minion Pro and Myriad Pro mapped onto Liberation.
    /// </summary>
    public PdfFontOptions FontOptions(string? fallbackFontDirectory)
    {
        var options = new PdfFontOptions { UseSystemFonts = SystemFonts };
        var own = Path.Combine(Directory, "fonts");
        if (System.IO.Directory.Exists(own))
        {
            options.FontDirectories.Add(own);
        }
        if (FontDirectory is not null)
        {
            options.FontDirectories.Add(FontDirectory);
        }
        if (fallbackFontDirectory is not null)
        {
            options.FontDirectories.Add(fallbackFontDirectory);
        }
        options.Substitutions["Minion Pro"] = "Liberation Serif";
        options.Substitutions["Myriad Pro"] = "Liberation Sans";
        foreach (var (from, to) in Substitutions)
        {
            options.Substitutions[from] = to;
        }
        return options;
    }

    private static int PageNumber(string name)
    {
        foreach (var prefix in (string[])["reference-", "page-", "reference_", "page_"])
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
            {
                return page;
            }
        }
        return 0;
    }
}
