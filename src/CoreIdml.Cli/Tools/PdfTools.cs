using System.Text.Json.Nodes;
using CoreIdml.Pdf;
using CoreIdml.Pdf.Json;

namespace CoreIdml.Cli.Tools;

/// <summary>PDF export and import, page previews and exact text fit.</summary>
internal static partial class PdfTools
{
    internal const string FontProperties =
        """ "fonts":{"type":"array","items":{"type":"string"},"description":"Font folders, searched first"},""" +
        """ "useSystemFonts":{"type":"boolean","description":"Default true"},""" +
        """ "substitutions":{"type":"object","additionalProperties":{"type":"string"},"description":"Family replacements"}""";

    private const string LinkProperties =
        """ "linkDirectories":{"type":"array","items":{"type":"string"},"description":"More folders to find missing images in, by file name"}""";

    private const string ReportSchema =
        """{"type":"object","properties":{"outPath":{"type":"string"},"pageCount":{"type":"integer"},"oversetFrames":{"type":"array","items":{"type":"object"}},""" +
        """ "missingFonts":{"type":"array","items":{"type":"object","properties":{"requestedFamily":{"type":"string"},"requestedStyle":{"type":"string"},"usedFont":{"type":"string"},"reason":{"type":"string"},"stories":{"type":"array","items":{"type":"string"}},"pages":{"type":"array","items":{"type":"string"}}}}},""" +
        """ "missingImages":{"type":"array","items":{"type":"string"}},"oversetStories":{"type":"array","items":{"type":"string"}},"warnings":{"type":"array","items":{"type":"string"}}},"required":["outPath","pageCount"]}""";

    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        RenderPageTool,
        CheckFitTool,
        ListFontsTool,
        new()
        {
            Name = "idml_to_pdf",
            Title = "Export PDF",
            Description = "Export a PDF with this library's own composer and fonts (approximates InDesign). Returns the PDF path, missing fonts and their replacements (stories, pages), missing images, overset stories and the frames they overflow (oversetFrames). Not drawn (kept in the .idml): effects other than opacity, text wrap, placed EPS/AI.",
            InputSchema = Schema.Read(
                """ "outPath":{"type":"string","description":"Default: the .idml path with .pdf"},""" +
                """ "pages":{"type":"array","items":{"type":"integer","minimum":0},"description":"0-based; default all"},""" +
                FontProperties + "," + LinkProperties + "," +
                """ "markMissingImages":{"type":"boolean","description":"Crossed box where an image is missing"},""" +
                """ "maxImageDpi":{"type":"number","exclusiveMinimum":0,"description":"Downsample images above this placed resolution; default off"},""" +
                """ "jpegQuality":{"type":"integer","minimum":1,"maximum":100,"description":"For maxImageDpi; default 85"},""" +
                """ "includeBleed":{"type":"boolean","description":"Pages with the document's bleed around the trim size (TrimBox, BleedBox); default false: trim size"},""" +
                """ "options":{"type":"object","description":"More options as in the C API's idml_export_pdf (1-based pages)"}"""),
            OutputSchema = ReportSchema,
            Access = ToolAccess.Write,
            Idempotent = true,
            Run = args =>
            {
                var path = args.ExistingFile("path");
                var outPath = args.OptionalPath("outPath") ?? Path.ChangeExtension(path, ".pdf");
                var options = ExportOptions(args, path);
                using var document = IdmlDocument.Open(path);
                var result = IdmlPdf.Export(document, outPath, options);
                var report = JsonNode.Parse(WithOutPath(PdfJson.ToJson(result), outPath))!.AsObject();
                if (result.OversetStories.Count > 0)
                {
                    report["oversetFrames"] = OversetFrames(document, result.OversetStories);
                }
                return new(JsonText.Write(report));
            },
        },
        new()
        {
            Name = "idml_from_pdf",
            Title = "Import PDF",
            Description = "Rebuild an editable .idml from a PDF (an approximation): text frames with inferred styles, image frames (images extracted to a linked folder), rectangles, lines. Returns counts, the fonts needed (available or not) and warnings.",
            InputSchema = Schema.Object(
                """ "path":{"type":"string","description":"Absolute PDF path"},"outPath":{"type":"string","description":"Default: the PDF path with .idml"},""" +
                """ "pages":{"type":"array","items":{"type":"integer","minimum":0},"description":"0-based; default all"},""" +
                """ "imageDirectory":{"type":"string","description":"Default <out>-links"},""" +
                """ "preserveLineBreaks":{"type":"boolean"},"importShapes":{"type":"boolean","description":"Default true"},""" +
                """ "fonts":{"type":"array","items":{"type":"string"},"description":"Font folders to check needed fonts against"},"useSystemFonts":{"type":"boolean"},""" +
                """ "options":{"type":"object","description":"More options as in the C API's idml_import_pdf (1-based pages)"}""",
                ["path"]),
            OutputSchema = """{"type":"object","properties":{"outPath":{"type":"string"},"textFrames":{"type":"integer"},"imageFrames":{"type":"integer"},"shapes":{"type":"integer"},"images":{"type":"array","items":{"type":"string"}},"fonts":{"type":"array","items":{"type":"object"}},"warnings":{"type":"array","items":{"type":"string"}}},"required":["outPath"]}""",
            Access = ToolAccess.Write,
            Run = args =>
            {
                var path = args.ExistingFile("path");
                var outPath = args.OptionalPath("outPath") ?? Path.ChangeExtension(path, ".idml");
                var options = PdfJson.ParseImportOptions(args.Get("options") is { } raw ? raw.GetRawText() : null);
                options.Pages = OneBased(args) ?? options.Pages;
                options.ImageDirectory = args.OptionalPath("imageDirectory") ?? options.ImageDirectory;
                options.PreserveLineBreaks = args.OptionalBool("preserveLineBreaks") ?? options.PreserveLineBreaks;
                options.ImportShapes = args.OptionalBool("importShapes") ?? options.ImportShapes;
                var fonts = args.ExistingDirectories("fonts");
                if (fonts.Count > 0 || args.OptionalBool("useSystemFonts") == true)
                {
                    options.Fonts = new PdfFontOptions { UseSystemFonts = args.OptionalBool("useSystemFonts") ?? false };
                    foreach (var directory in fonts)
                    {
                        options.Fonts.FontDirectories.Add(directory);
                    }
                }
                var result = IdmlPdf.Import(path, outPath, options);
                return new(WithOutPath(PdfJson.ToJson(result), outPath));
            },
        },
    ];

    /// <summary>idml_render_page: a PNG preview of one page, rendered from this library's own PDF export.</summary>
    private static ToolDefinition RenderPageTool => new()
    {
        Name = "idml_render_page",
        Title = "Preview a page",
        Description = "Render one page as a PNG image (via this library's PDF engine) to look at the layout, with that page's report: missing fonts and images, overset stories and their frames. Keep dpi at or below 150 for big pages. Not drawn (kept in the .idml): effects other than opacity, text wrap, placed EPS/AI.",
        InputSchema = Schema.Read(
            """ "pageIndex":{"type":"integer","minimum":0,"description":"Default 0"},"dpi":{"type":"number","minimum":10,"maximum":300,"description":"Default 100"},""" +
            FontProperties + "," + LinkProperties),
        OutputSchema = """{"type":"object","properties":{"rendered":{"type":"boolean"},"pageIndex":{"type":"integer"},"width":{"type":"integer"},"height":{"type":"integer"},"dpi":{"type":"number"},"note":{"type":"string"},"pdfPath":{"type":"string"},"missingFonts":{"type":"array"},"missingImages":{"type":"array"},"oversetStories":{"type":"array"},"oversetFrames":{"type":"array"}},"required":["rendered","pageIndex"]}""",
        Access = ToolAccess.Read,
        Idempotent = true,
        Run = args =>
        {
            var path = args.ExistingFile("path");
            var pageIndex = args.OptionalInt("pageIndex") ?? 0;
            var dpi = args.OptionalDouble("dpi") ?? 100;
            if (dpi is < 10 or > 300)
            {
                throw ToolException.Invalid("dpi must be between 10 and 300.");
            }
            using var document = IdmlDocument.Open(path);
            LayoutTools.RequirePage(document, pageIndex);
            var options = ExportOptions(args, path);
            options.Pages = [pageIndex + 1];
            using var pdf = new MemoryStream();
            var report = JsonNode.Parse(PdfJson.ToJson(IdmlPdf.Export(document, pdf, options)))!.AsObject();
            var result = new JsonObject { ["pageIndex"] = pageIndex };
            byte[]? png = null;
            if (Rendering.PageRasterizer.IsAvailable(out var reason))
            {
                var page = Rendering.PageRasterizer.Render(pdf.ToArray(), 0, dpi);
                png = page.Png;
                result["rendered"] = true;
                result["width"] = page.Width;
                result["height"] = page.Height;
                result["dpi"] = dpi;
            }
            else
            {
                // No rasterizer next to the binary: hand over the PDF instead.
                var pdfPath = Path.Combine(Path.GetTempPath(), $"idml-preview-{Path.GetFileNameWithoutExtension(path)}-p{pageIndex}.pdf");
                File.WriteAllBytes(pdfPath, pdf.ToArray());
                result["rendered"] = false;
                result["pdfPath"] = pdfPath;
                result["note"] = "Page previews need the PDFium library next to coreidml (pdfium.dll / libpdfium.so / libpdfium.dylib); it was not found (" + reason + "). The page was exported to pdfPath instead.";
            }
            foreach (var key in (string[])["missingFonts", "missingImages", "oversetStories"])
            {
                result[key] = report[key]?.DeepClone();
            }
            if (report["oversetStories"] is JsonArray { Count: > 0 } overset)
            {
                result["oversetFrames"] = OversetFrames(document, overset.Select(id => id!.GetValue<string>()));
            }
            return new(JsonText.Write(result), png);
        },
    };

    /// <summary>idml_check_fit: exact fit of every story (or one) with the export's composer.</summary>
    private static ToolDefinition CheckFitTool => new()
    {
        Name = "idml_check_fit",
        Title = "Check text fit",
        Description = "Exact overset check with the PDF export's fonts and composer (not the summary's estimate): per story that does not fit, lines and points overflowing, overset characters, lines per frame. Pass the same fonts as for idml_to_pdf: fonts decide line breaks; missing ones and their replacements are listed.",
        InputSchema = Schema.Read(
            """ "storyId":{"type":"string","description":"Only this story"},"itemId":{"type":"string","description":"Only this text frame's story"},""" +
            """ "all":{"type":"boolean","description":"Also list stories that fit"},""" + FontProperties),
        OutputSchema = """{"type":"object","properties":{"checked":{"type":"integer"},"overset":{"type":"integer"},"stories":{"type":"array","items":{"type":"object","properties":{"storyId":{"type":"string"},"overset":{"type":"boolean"},"overflowLines":{"type":"integer"},"overflowPoints":{"type":"number"},"oversetCharacters":{"type":"integer"},"lines":{"type":"integer"},"frames":{"type":"array","items":{"type":"object"}}}}},"missingFonts":{"type":"array","items":{"type":"object"}}},"required":["checked","overset","stories"]}""",
        Access = ToolAccess.Read,
        Idempotent = true,
        Run = args =>
        {
            using var document = IdmlDocument.Open(args.ExistingFile("path"));
            var options = ExportOptions(args, args.ExistingFile("path"));
            var storyId = args.OptionalString("storyId");
            if (storyId is null && args.Has("itemId"))
            {
                storyId = (LayoutTools.RequireItem(document, args) as Dom.TextFrame ?? throw ToolException.Invalid($"Item {args.String("itemId")} is not a text frame.")).StoryId;
            }
            else if (storyId is not null)
            {
                DocumentTools.RequireStory(document, storyId);
            }
            return new(JsonText.Write(FitReport(document, options.Fonts, args.Bool("all"), storyId)));
        },
    };

    /// <summary>idml_list_fonts: the families and styles exports can use, to choose fonts before writing a spec or style.</summary>
    private static ToolDefinition ListFontsTool => new()
    {
        Name = "idml_list_fonts",
        Title = "List available fonts",
        Description = "Font families (installed, plus the folders given) with exact style names that idml_to_pdf, idml_render_page and idml_check_fit can use: the values for font and fontStyle. Other families or styles are substituted. (idml_fonts reports a document's own fonts.)",
        InputSchema = Schema.Object(
            """ "query":{"type":"string","description":"Family name contains (case-insensitive)"},""" +
            """ "max":{"type":"integer","minimum":1,"description":"Default 100"},""" + FontProperties,
            []),
        OutputSchema = """{"type":"object","properties":{"families":{"type":"array","items":{"type":"object","properties":{"family":{"type":"string"},"styles":{"type":"array","items":{"type":"string"}}}}},"total":{"type":"integer"},"truncated":{"type":"boolean"}},"required":["families","total","truncated"]}""",
        Access = ToolAccess.Read,
        Idempotent = true,
        Run = args =>
        {
            var query = args.OptionalString("query");
            var max = args.OptionalInt("max") ?? 100;
            if (max < 1)
            {
                throw ToolException.Invalid("max must be 1 or more.");
            }
            var matching = IdmlPdf.ListFonts(ExportOptions(args, string.Empty).Fonts)
                .Where(font => query is null || font.Family.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return new(JsonText.Write(new JsonObject
            {
                ["families"] = new JsonArray([.. matching.Take(max).Select(font => (JsonNode)new JsonObject
                {
                    ["family"] = font.Family,
                    ["styles"] = JsonText.Array(font.Styles),
                })]),
                ["total"] = matching.Count,
                ["truncated"] = matching.Count > max,
            }));
        },
    };

    /// <summary>
    /// The exact fit of every story (or one): for each that overflows, by how much, and the frames it runs through, with
    /// their names. Stories that fit are listed only when <paramref name="all"/> is set.
    /// </summary>
    internal static JsonObject FitReport(IdmlDocument document, PdfFontOptions fonts, bool all, string? storyId = null)
    {
        using var measurer = new TextMeasurer(document, fonts);
        IReadOnlyList<StoryFit> fits = storyId is null ? measurer.CheckFit(document) : [measurer.CheckFit(document.FindStory(storyId)!)];
        var listed = all ? fits : fits.Where(fit => fit.Overset).ToList();
        var frames = document.AllPageItems.OfType<Dom.TextFrame>().ToDictionary(frame => frame.Id, StringComparer.Ordinal);
        return new JsonObject
        {
            ["checked"] = fits.Count,
            ["overset"] = fits.Count(fit => fit.Overset),
            ["stories"] = new JsonArray([.. listed.Select(fit => (JsonNode)new JsonObject
            {
                ["storyId"] = fit.StoryId,
                ["overset"] = fit.Overset,
                ["overflowLines"] = fit.OverflowLines,
                ["overflowPoints"] = fit.OverflowPoints,
                ["oversetCharacters"] = fit.OversetCharacters,
                ["lines"] = fit.Lines,
                ["frames"] = new JsonArray([.. fit.Frames.Select(frame =>
                {
                    var item = frames.GetValueOrDefault(frame.FrameId);
                    var entry = new JsonObject
                    {
                        ["frameId"] = frame.FrameId,
                        ["name"] = item?.Name is { Length: > 0 } name ? name : null,
                    };
                    LayoutTools.AddPage(entry, item?.Page);
                    entry["lines"] = frame.Lines;
                    entry["textAreaHeight"] = frame.TextAreaHeight;
                    return (JsonNode)WithoutNulls(entry);
                })]),
            })]),
            ["missingFonts"] = new JsonArray([.. measurer.FontSubstitutions.Select(font => (JsonNode)new JsonObject
            {
                ["requestedFamily"] = font.RequestedFamily,
                ["requestedStyle"] = font.RequestedStyle,
                ["usedFont"] = font.UsedFont,
                ["reason"] = font.Reason,
            })]),
        };
    }

    /// <summary>For each overflowing story, the frame its text runs out of (the last of its thread), by name and page.</summary>
    internal static JsonArray OversetFrames(IdmlDocument document, IEnumerable<string> storyIds) =>
        new([.. storyIds.Select(storyId =>
        {
            var frames = document.FindStory(storyId)?.Frames;
            var last = frames is { Count: > 0 } ? frames[^1] : null;
            var entry = WithoutNulls(new JsonObject
            {
                ["storyId"] = storyId,
                ["frame"] = last?.Name is { Length: > 0 } name ? name : null,
                ["frameId"] = last?.Id,
            });
            LayoutTools.AddPage(entry, last?.Page);
            return (JsonNode)entry;
        })]);

    private static JsonObject WithoutNulls(JsonObject value)
    {
        foreach (var (key, member) in value.ToList())
        {
            if (member is null)
            {
                value.Remove(key);
            }
        }
        return value;
    }

    /// <summary>Export options from a tool's arguments: the C API's options object first, then the tool's own arguments.</summary>
    internal static PdfExportOptions ExportOptions(ToolArguments args, string documentPath)
    {
        var options = PdfJson.ParseExportOptions(args.Get("options") is { } raw ? raw.GetRawText() : null);
        options.DocumentPath ??= documentPath;
        if (OneBased(args) is { } pages)
        {
            options.Pages = pages;
        }
        foreach (var directory in args.ExistingDirectories("fonts"))
        {
            options.Fonts.FontDirectories.Add(directory);
        }
        if (args.OptionalBool("useSystemFonts") is { } system)
        {
            options.Fonts.UseSystemFonts = system;
        }
        foreach (var (from, to) in args.Map("substitutions"))
        {
            options.Fonts.Substitutions[from] = to;
        }
        foreach (var directory in args.ExistingDirectories("linkDirectories"))
        {
            options.LinkDirectories.Add(directory);
        }
        if (args.OptionalBool("markMissingImages") is { } mark)
        {
            options.MarkMissingImages = mark;
        }
        if (args.OptionalDouble("maxImageDpi") is { } dpi)
        {
            options.MaxImageDpi = dpi > 0 ? dpi : throw ToolException.Invalid("maxImageDpi must be positive.");
        }
        if (args.OptionalInt("jpegQuality") is { } quality)
        {
            options.JpegQuality = quality is >= 1 and <= 100 ? quality : throw ToolException.Invalid("jpegQuality must be 1 to 100.");
        }
        if (args.OptionalBool("includeBleed") is { } bleed)
        {
            options.IncludeBleed = bleed;
        }
        return options;
    }

    /// <summary>The tool's 0-based "pages" as the 1-based page numbers the PDF options use.</summary>
    private static List<int>? OneBased(ToolArguments args) =>
        args.Has("pages") ? args.Ints("pages").Select(index => index >= 0 ? index + 1 : throw ToolException.Invalid("Argument 'pages' holds 0-based page indices.")).ToList() : null;

    private static string WithOutPath(string reportJson, string outPath)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var result = new JsonObject { ["outPath"] = outPath };
        foreach (var (key, value) in report.ToList())
        {
            report.Remove(key);
            result[key] = value;
        }
        return JsonText.Write(result);
    }
}
