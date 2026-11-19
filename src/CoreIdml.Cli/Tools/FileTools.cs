using System.Text.Json.Nodes;
using CoreIdml.Dom;
using CoreIdml.Packaging;
using CoreIdml.Pdf;

namespace CoreIdml.Cli.Tools;

/// <summary>Fonts, links and packaging: what a document needs from the disk.</summary>
internal static class FileTools
{
    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new()
        {
            Name = "idml_fonts",
            Title = "Fonts report",
            Description = "The font families the document uses (InDesign's recorded styles and status) and, composing its text with the given fonts as the PDF export does, which are missing or substituted, by what, and the stories and pages affected.",
            InputSchema = Schema.Read(PdfTools.FontProperties),
            OutputSchema = """{"type":"object","properties":{"fonts":{"type":"array","items":{"type":"object","properties":{"family":{"type":"string"},"status":{"type":"string"},"styles":{"type":"array","items":{"type":"string"}}}}},"substitutions":{"type":"array","items":{"type":"object"}}},"required":["fonts","substitutions"]}""",
            Access = ToolAccess.Read,
            Idempotent = true,
            Run = args =>
            {
                var path = args.ExistingFile("path");
                using var document = IdmlDocument.Open(path);
                var options = PdfTools.ExportOptions(args, path);
                using var measurer = new TextMeasurer(document, options.Fonts);
                measurer.CheckFit(document);
                // With their pages filled in as the PDF export reports them (master stories as "master <name>").
                var substitutions = measurer.FontSubstitutions;
                var recorded = document.Fonts.ToDictionary(font => font.Name, StringComparer.Ordinal);
                var families = document.UsedFontFamilies.Concat(substitutions.Select(font => font.RequestedFamily)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                var result = new JsonObject
                {
                    ["fonts"] = new JsonArray([.. families.Select(family =>
                    {
                        var missing = substitutions.Where(font => font.RequestedFamily == family).Select(font => font.RequestedStyle).Distinct(StringComparer.Ordinal).ToList();
                        var entry = new JsonObject
                        {
                            ["family"] = family,
                            ["status"] = missing.Count == 0 ? "available" : "substituted",
                            ["substitutedStyles"] = missing.Count == 0 ? null : JsonText.Array(missing),
                            ["styles"] = recorded.TryGetValue(family, out var info) ? JsonText.Array(info.Styles) : null,
                            ["inDesignStatus"] = info is null ? null : JsonText.Array(info.Statuses),
                        };
                        return (JsonNode)WithoutNulls(entry);
                    })]),
                    ["substitutions"] = new JsonArray([.. substitutions.Select(font => (JsonNode)new JsonObject
                    {
                        ["requestedFamily"] = font.RequestedFamily,
                        ["requestedStyle"] = font.RequestedStyle,
                        ["usedFont"] = font.UsedFont,
                        ["reason"] = font.Reason,
                        ["embeddingRestricted"] = font.EmbeddingRestricted,
                        ["stories"] = JsonText.Array(font.Stories),
                        ["pages"] = JsonText.Array(font.Pages),
                    })]),
                };
                return new(JsonText.Write(result));
            },
        },
        new()
        {
            Name = "idml_relink_folder",
            Title = "Relink from folders",
            Description = "Relink every link whose file is missing to a same-named file (case-insensitive) in the folders, like InDesign's Relink to Folder. Returns what was relinked and what is still missing.",
            InputSchema = Schema.Edit(
                """ "folders":{"type":"array","items":{"type":"string"}},""" +
                """ "all":{"type":"boolean","description":"Also relink links that resolve"},"refit":{"type":"boolean","description":"Re-measure images and fit them to their frames"}""",
                "folders"),
            OutputSchema = """{"type":"object","properties":{"relinked":{"type":"array","items":{"type":"object"}},"missing":{"type":"array","items":{"type":"object"}}},"required":["relinked","missing"]}""",
            Access = ToolAccess.Edit,
            Idempotent = true,
            Apply = edit =>
            {
                var folders = edit.Args.ExistingDirectories("folders");
                if (folders.Count == 0)
                {
                    throw ToolException.Invalid("Argument 'folders' is empty.");
                }
                var everything = edit.Args.Bool("all");
                var current = DocumentPackager.ResolveLinks(edit.Document, edit.DocumentPath, []).ToDictionary(link => link.Link.Id, link => link.Path, StringComparer.Ordinal);
                // By name in the folders only, whether or not the link's own file exists (all: true relinks those too).
                var found = DocumentPackager.FindInFolders(edit.Document, folders);
                var relinked = new JsonArray();
                var missing = new JsonArray();
                foreach (var (link, path) in found)
                {
                    var exists = current.GetValueOrDefault(link.Id) is not null;
                    if ((exists && !everything) || path is null)
                    {
                        if (!exists)
                        {
                            missing.Add((JsonNode)new JsonObject { ["linkId"] = link.Id, ["uri"] = link.Uri });
                        }
                        continue;
                    }
                    var uri = link.Uri;
                    var target = Path.GetFullPath(path);
                    link.Relink(target, edit.Args.Bool("refit") ? ImageFit.Fill : null);
                    relinked.Add((JsonNode)new JsonObject { ["linkId"] = link.Id, ["from"] = uri, ["to"] = target });
                }
                return JsonText.Write(new JsonObject { ["relinked"] = relinked, ["missing"] = missing });
            },
        },
        new()
        {
            Name = "idml_package",
            Title = "Package document",
            Description = "Like InDesign's File > Package: a zip with the .idml, every linked file found (under Links/) and optionally font files (under Document fonts/; check font licences before sending). Reports missing links.",
            InputSchema = Schema.Read(
                """ "outPath":{"type":"string","description":"The .zip"},""" +
                """ "linkDirectories":{"type":"array","items":{"type":"string"},"description":"More folders to find links in, by file name"},""" +
                """ "fontDirectories":{"type":"array","items":{"type":"string"},"description":"Font folders; files whose name matches a used family are included"}""",
                "outPath"),
            OutputSchema = """{"type":"object","properties":{"outPath":{"type":"string"},"entries":{"type":"array","items":{"type":"string"}},"links":{"type":"object"},"missingLinks":{"type":"object"},"fonts":{"type":"array","items":{"type":"string"}}},"required":["outPath","entries"]}""",
            Access = ToolAccess.Write,
            Idempotent = true,
            Run = args =>
            {
                var path = args.ExistingFile("path");
                var outPath = args.Path("outPath");
                using var document = IdmlDocument.Open(path);
                var options = new PackageOptions { DocumentName = Path.GetFileName(path), DocumentPath = path };
                foreach (var folder in args.ExistingDirectories("linkDirectories"))
                {
                    options.LinkDirectories.Add(folder);
                }
                foreach (var font in MatchingFonts(document, args.ExistingDirectories("fontDirectories")))
                {
                    options.FontFiles.Add(font);
                }
                var temporary = outPath + ".tmp";
                PackageReport report;
                try
                {
                    using (var zip = File.Create(temporary))
                    {
                        report = DocumentPackager.Write(document, zip, options);
                    }
                    File.Move(temporary, outPath, overwrite: true);
                }
                finally
                {
                    File.Delete(temporary);
                }
                return new(JsonText.Write(new JsonObject
                {
                    ["outPath"] = outPath,
                    ["entries"] = JsonText.Array(report.Entries),
                    ["links"] = new JsonObject([.. report.Links.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))]),
                    ["missingLinks"] = new JsonObject([.. report.MissingLinks.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))]),
                    ["fonts"] = JsonText.Array(report.Fonts),
                }));
            },
        },
    ];

    /// <summary>Font files whose name, without spaces, hyphens or case, starts with a used family's name.</summary>
    private static IEnumerable<string> MatchingFonts(IdmlDocument document, IReadOnlyList<string> directories)
    {
        if (directories.Count == 0)
        {
            return [];
        }
        static string Key(string value) => new([.. value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        var families = document.UsedFontFamilies.Select(Key).Where(key => key.Length > 0).ToList();
        string[] extensions = [".ttf", ".otf", ".ttc", ".otc"];
        return directories
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => families.Any(family => Key(Path.GetFileNameWithoutExtension(file)).StartsWith(family, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
    }

    private static JsonObject WithoutNulls(JsonObject value)
    {
        foreach (var (key, item) in value.ToList())
        {
            if (item is null)
            {
                value.Remove(key);
            }
        }
        return value;
    }
}
