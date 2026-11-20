using System.Text.Json;
using System.Text.Json.Serialization;
using CoreIdml.Pdf.Fonts;

namespace CoreIdml.Pdf.Json;

/// <summary>Export options as JSON (C ABI, Dart, CLI). Lengths in points.</summary>
public sealed class PdfExportOptionsSpec
{
    public List<int>? Pages { get; set; }

    public List<string>? FontDirectories { get; set; }

    public bool? UseSystemFonts { get; set; }

    /// <summary>Family replacements, e.g. {"Minion Pro": "Liberation Serif"}.</summary>
    public Dictionary<string, string>? Substitutions { get; set; }

    public List<string>? FallbackFamilies { get; set; }

    public List<string>? LinkDirectories { get; set; }

    /// <summary>File name (or link URI) → local file.</summary>
    public Dictionary<string, string>? LinkRemap { get; set; }

    public string? DocumentPath { get; set; }

    public bool? MarkMissingImages { get; set; }

    public bool? IncludeMasterItems { get; set; }

    /// <summary>Pages with the document's bleed around the trim (TrimBox); default false, trim-size pages.</summary>
    public bool? IncludeBleed { get; set; }

    public string? Title { get; set; }

    /// <summary>Downsample placed images above this resolution (pixels per inch at their placed size); null keeps them.</summary>
    public double? MaxImageDpi { get; set; }

    /// <summary>Quality (1-100) of JPEGs re-encoded by maxImageDpi; default 85.</summary>
    public int? JpegQuality { get; set; }
}

/// <summary>Import options as JSON.</summary>
public sealed class PdfImportOptionsSpec
{
    public List<int>? Pages { get; set; }

    public string? ImageDirectory { get; set; }

    public bool? PreserveLineBreaks { get; set; }

    public bool? ImportShapes { get; set; }

    /// <summary>Font folders to check the rebuilt document's fonts against (reported as available or missing).</summary>
    public List<string>? FontDirectories { get; set; }

    public bool? UseSystemFonts { get; set; }
}

public sealed class FontSubstitutionSpec
{
    public string RequestedFamily { get; set; } = string.Empty;

    public string RequestedStyle { get; set; } = string.Empty;

    public string UsedFont { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public bool EmbeddingRestricted { get; set; }

    public List<string> Stories { get; set; } = [];

    public List<string> Pages { get; set; } = [];
}

/// <summary>Export report as JSON: what an app shows as "missing fonts / missing links".</summary>
public sealed class PdfExportReportSpec
{
    public int PageCount { get; set; }

    public List<FontSubstitutionSpec> MissingFonts { get; set; } = [];

    public List<string> MissingImages { get; set; } = [];

    public List<string> OversetStories { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

public sealed class ImportedFontSpec
{
    public string Family { get; set; } = string.Empty;

    public string Style { get; set; } = string.Empty;

    public string PdfFontName { get; set; } = string.Empty;

    public List<int> Pages { get; set; } = [];

    public bool? Available { get; set; }
}

public sealed class PdfImportReportSpec
{
    public int TextFrames { get; set; }

    public int ImageFrames { get; set; }

    public int Shapes { get; set; }

    public List<string> Images { get; set; } = [];

    public List<ImportedFontSpec> Fonts { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PdfExportOptionsSpec))]
[JsonSerializable(typeof(PdfImportOptionsSpec))]
[JsonSerializable(typeof(PdfExportReportSpec))]
[JsonSerializable(typeof(PdfImportReportSpec))]
internal sealed partial class PdfJsonContext : JsonSerializerContext;

/// <summary>JSON bridge for the C ABI, Dart and the CLI (source-generated, Native AOT safe).</summary>
public static class PdfJson
{
    private static readonly PdfJsonContext Output = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static PdfExportOptions ParseExportOptions(string? json)
    {
        var spec = string.IsNullOrWhiteSpace(json) ? new PdfExportOptionsSpec() : JsonSerializer.Deserialize(json, PdfJsonContext.Default.PdfExportOptionsSpec) ?? new PdfExportOptionsSpec();
        var options = new PdfExportOptions
        {
            Pages = spec.Pages,
            DocumentPath = spec.DocumentPath,
            MarkMissingImages = spec.MarkMissingImages ?? false,
            IncludeMasterItems = spec.IncludeMasterItems ?? true,
            IncludeBleed = spec.IncludeBleed ?? false,
            Title = spec.Title,
            MaxImageDpi = spec.MaxImageDpi is > 0 ? spec.MaxImageDpi : null,
            JpegQuality = Math.Clamp(spec.JpegQuality ?? 85, 1, 100),
        };
        options.Fonts.UseSystemFonts = spec.UseSystemFonts ?? true;
        foreach (var directory in spec.FontDirectories ?? [])
        {
            options.Fonts.FontDirectories.Add(directory);
        }
        foreach (var (from, to) in spec.Substitutions ?? [])
        {
            options.Fonts.Substitutions[from] = to;
        }
        foreach (var family in spec.FallbackFamilies ?? [])
        {
            options.Fonts.FallbackFamilies.Add(family);
        }
        foreach (var directory in spec.LinkDirectories ?? [])
        {
            options.LinkDirectories.Add(directory);
        }
        foreach (var (from, to) in spec.LinkRemap ?? [])
        {
            options.LinkRemap[from] = to;
        }
        return options;
    }

    public static PdfImportOptions ParseImportOptions(string? json)
    {
        var spec = string.IsNullOrWhiteSpace(json) ? new PdfImportOptionsSpec() : JsonSerializer.Deserialize(json, PdfJsonContext.Default.PdfImportOptionsSpec) ?? new PdfImportOptionsSpec();
        var options = new PdfImportOptions
        {
            Pages = spec.Pages,
            ImageDirectory = spec.ImageDirectory,
            PreserveLineBreaks = spec.PreserveLineBreaks ?? false,
            ImportShapes = spec.ImportShapes ?? true,
        };
        if (spec.FontDirectories is { Count: > 0 } || spec.UseSystemFonts == true)
        {
            options.Fonts = new PdfFontOptions { UseSystemFonts = spec.UseSystemFonts ?? false };
            foreach (var directory in spec.FontDirectories ?? [])
            {
                options.Fonts.FontDirectories.Add(directory);
            }
        }
        return options;
    }

    public static string ToJson(PdfExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var spec = new PdfExportReportSpec
        {
            PageCount = result.PageCount,
            MissingFonts = result.FontSubstitutions.Select(Spec).ToList(),
            MissingImages = result.MissingImages.ToList(),
            OversetStories = result.OversetStories.ToList(),
            Warnings = result.Warnings.ToList(),
        };
        return JsonSerializer.Serialize(spec, Output.PdfExportReportSpec);
    }

    public static string ToJson(PdfImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var spec = new PdfImportReportSpec
        {
            TextFrames = result.TextFrames,
            ImageFrames = result.ImageFrames,
            Shapes = result.Shapes,
            Images = result.Images.ToList(),
            Fonts = result.Fonts.Select(f => new ImportedFontSpec { Family = f.Family, Style = f.Style, PdfFontName = f.PdfFontName, Pages = f.Pages.ToList(), Available = f.Available }).ToList(),
            Warnings = result.Warnings.ToList(),
        };
        return JsonSerializer.Serialize(spec, Output.PdfImportReportSpec);
    }

    private static FontSubstitutionSpec Spec(FontSubstitution s) => new()
    {
        RequestedFamily = s.RequestedFamily,
        RequestedStyle = s.RequestedStyle,
        UsedFont = s.UsedFont,
        Reason = s.Reason,
        EmbeddingRestricted = s.EmbeddingRestricted,
        Stories = s.Stories.ToList(),
        Pages = s.Pages.ToList(),
    };
}
