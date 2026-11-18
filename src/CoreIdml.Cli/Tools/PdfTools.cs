using System.Text.Json.Nodes;
using CoreIdml.Pdf;
using CoreIdml.Pdf.Json;

namespace CoreIdml.Cli.Tools;

/// <summary>PDF export and import, page previews and exact text fit.</summary>
internal static partial class PdfTools
{
    internal const string FontProperties =
        """ "fonts":{"type":"array","items":{"type":"string"},"description":"Absolute paths of font folders, searched before the system fonts"},""" +
        """ "useSystemFonts":{"type":"boolean","description":"Also use the installed fonts (default true)"},""" +
        """ "substitutions":{"type":"object","additionalProperties":{"type":"string"},"description":"Family replacements, e.g. {\"Minion Pro\":\"Liberation Serif\"}"}""";

    private const string LinkProperties =
        """ "linkDirectories":{"type":"array","items":{"type":"string"},"description":"Folders searched by file name for images whose link path is missing (the document's folder and its Links folder are always searched)"}""";

    private const string ReportSchema =
        """{"type":"object","properties":{"outPath":{"type":"string"},"pageCount":{"type":"integer"},""" +
        """ "missingFonts":{"type":"array","items":{"type":"object","properties":{"requestedFamily":{"type":"string"},"requestedStyle":{"type":"string"},"usedFont":{"type":"string"},"reason":{"type":"string"},"stories":{"type":"array","items":{"type":"string"}},"pages":{"type":"array","items":{"type":"string"}}}}},""" +
        """ "missingImages":{"type":"array","items":{"type":"string"}},"oversetStories":{"type":"array","items":{"type":"string"}},"warnings":{"type":"array","items":{"type":"string"}}},"required":["outPath","pageCount"]}""";

    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        RenderPageTool,
        CheckFitTool,
        new()
        {
            Name = "idml_to_pdf",
            Title = "Export PDF",
            Description = "Render the document to PDF with this library's own composer and fonts (an approximation of InDesign). Returns the PDF path and what differs from InDesign's output: missing fonts and what replaced them (with stories and pages), missing images, overset stories.",
            InputSchema = Schema.Read(
                """ "outPath":{"type":"string","description":"Absolute path of the PDF; default the .idml path with .pdf"},""" +
                """ "pages":{"type":"array","items":{"type":"integer","minimum":0},"description":"0-based pages to export; default all"},""" +
                FontProperties + "," + LinkProperties + "," +
                """ "markMissingImages":{"type":"boolean","description":"Draw a crossed box where an image is missing"},""" +
                """ "maxImageDpi":{"type":"number","exclusiveMinimum":0,"description":"Downsample placed images above this resolution at their placed size (smaller PDFs); default off"},""" +
                """ "jpegQuality":{"type":"integer","minimum":1,"maximum":100,"description":"Quality of JPEGs re-encoded by maxImageDpi, default 85"},""" +
                """ "options":{"type":"object","description":"Further export options as in the C API's idml_export_pdf (its pages are 1-based)"}"""),
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
                return new(WithOutPath(PdfJson.ToJson(result), outPath));
            },
        },
        new()
        {
            Name = "idml_from_pdf",
            Title = "Import PDF",
            Description = "Rebuild an editable .idml from a PDF: text frames with inferred paragraph/character styles, image frames (images are written to a folder next to the IDML, which links them), rectangles and lines. Returns counts, the fonts the new document needs (available or not) and warnings.",
            InputSchema = Schema.Object(
                """ "path":{"type":"string","description":"Absolute path of the PDF"},"outPath":{"type":"string","description":"Absolute path of the new .idml; default the PDF path with .idml"},""" +
                """ "pages":{"type":"array","items":{"type":"integer","minimum":0},"description":"0-based pages to import; default all"},""" +
                """ "imageDirectory":{"type":"string","description":"Where extracted images go; default <out>-links next to the IDML"},""" +
                """ "preserveLineBreaks":{"type":"boolean"},"importShapes":{"type":"boolean","description":"Default true"},""" +
                """ "fonts":{"type":"array","items":{"type":"string"},"description":"Font folders to check the needed fonts against"},"useSystemFonts":{"type":"boolean"},""" +
                """ "options":{"type":"object","description":"Further import options as in the C API's idml_import_pdf (its pages are 1-based)"}""",
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
        Description = "Render one page as a PNG image (returned as MCP image content) so you can look at the layout: the page is exported with this library's PDF engine and rasterized with PDFium. Also returns the export report for that page (missing fonts and images, overset stories). Default 100 dpi; keep it at or below 150 for big pages.",
        InputSchema = Schema.Read(
            """ "pageIndex":{"type":"integer","minimum":0,"description":"0-based page; default 0"},"dpi":{"type":"number","minimum":10,"maximum":300,"description":"Default 100"},""" +
            FontProperties + "," + LinkProperties),
        OutputSchema = """{"type":"object","properties":{"rendered":{"type":"boolean"},"pageIndex":{"type":"integer"},"width":{"type":"integer"},"height":{"type":"integer"},"dpi":{"type":"number"},"note":{"type":"string"},"pdfPath":{"type":"string"},"missingFonts":{"type":"array"},"missingImages":{"type":"array"},"oversetStories":{"type":"array"}},"required":["rendered","pageIndex"]}""",
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
            return new(JsonText.Write(result), png);
        },
    };

    /// <summary>idml_check_fit: exact fit of every story (or one) with the export's composer.</summary>
    private static ToolDefinition CheckFitTool => new()
    {
        Name = "idml_check_fit",
        Title = "Check text fit",
        Description = "Exact overset check with the PDF export's own fonts and composer (not the summary's estimate): for each story that does not fit its frames, how many lines and points overflow, the overset characters, and the lines placed in each frame. Use the same fonts as for idml_to_pdf; fonts that are missing (and what replaced them) are listed, since they change line breaks.",
        InputSchema = Schema.Read(
            """ "storyId":{"type":"string","description":"Check only this story"},"itemId":{"type":"string","description":"Check only the story of this text frame"},""" +
            """ "all":{"type":"boolean","description":"Also list stories that fit"},""" + FontProperties),
        OutputSchema = """{"type":"object","properties":{"checked":{"type":"integer"},"overset":{"type":"integer"},"stories":{"type":"array","items":{"type":"object","properties":{"storyId":{"type":"string"},"overset":{"type":"boolean"},"overflowLines":{"type":"integer"},"overflowPoints":{"type":"number"},"oversetCharacters":{"type":"integer"},"lines":{"type":"integer"},"frames":{"type":"array","items":{"type":"object"}}}}},"missingFonts":{"type":"array","items":{"type":"object"}}},"required":["checked","overset","stories"]}""",
        Access = ToolAccess.Read,
        Idempotent = true,
        Run = args =>
        {
            using var document = IdmlDocument.Open(args.ExistingFile("path"));
            var options = ExportOptions(args, args.ExistingFile("path"));
            using var measurer = new TextMeasurer(document, options.Fonts);
            IReadOnlyList<StoryFit> fits;
            if (args.OptionalString("storyId") is { } storyId)
            {
                fits = [measurer.CheckFit(DocumentTools.RequireStory(document, storyId))];
            }
            else if (args.Has("itemId"))
            {
                var frame = LayoutTools.RequireItem(document, args) as Dom.TextFrame ?? throw ToolException.Invalid($"Item {args.String("itemId")} is not a text frame.");
                fits = [measurer.CheckFit(frame.Story)];
            }
            else
            {
                fits = measurer.CheckFit(document);
            }
            var listed = args.Bool("all") ? fits : fits.Where(fit => fit.Overset).ToList();
            var result = new JsonObject
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
                    ["frames"] = new JsonArray([.. fit.Frames.Select(frame => (JsonNode)new JsonObject
                    {
                        ["frameId"] = frame.FrameId,
                        ["page"] = frame.PageName,
                        ["lines"] = frame.Lines,
                        ["textAreaHeight"] = frame.TextAreaHeight,
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
            return new(JsonText.Write(result));
        },
    };

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
