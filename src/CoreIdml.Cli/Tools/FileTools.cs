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
            Description = "The font families the document uses (with InDesign's recorded styles and status) and, from composing its text with the given font folders as the PDF export does, which fonts are missing or substituted, by what, and the stories and pages they affect.",
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
                var pagesByStory = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                foreach (var frame in document.AllPageItems.OfType<TextFrame>())
                {
                    if (frame.Page?.Name is { } name)
                    {
                        (pagesByStory.TryGetValue(frame.StoryId, out var set) ? set : pagesByStory[frame.StoryId] = new SortedSet<string>(StringComparer.Ordinal)).Add(name);
                    }
                }
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
                        ["pages"] = JsonText.Array(font.Stories.SelectMany(story => pagesByStory.GetValueOrDefault(story) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                    })]),
                };
                return new(JsonText.Write(result));
            },
        },
        new()
        {
            Name = "idml_relink_folder",
            Title = "Relink missing links from a folder",
            Description = "Relink every link whose file is missing to a file of the same name (case-insensitive) in the given folders, like InDesign's Relink to Folder. Links that already resolve are left alone unless all: true. Returns what was relinked and what is still missing.",
            InputSchema = Schema.Edit(
                """ "folders":{"type":"array","items":{"type":"string"},"description":"Absolute paths of the folders to search"},""" +
                """ "all":{"type":"boolean","description":"Relink every link found in the folders, not only missing ones"},"refit":{"type":"boolean","description":"Re-measure images and fit them to their frames"}""",
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
                var found = DocumentPackager.ResolveLinks(edit.Document, null, folders);
                var relinked = new JsonArray();
                var missing = new JsonArray();
                foreach (var (link, path) in found)
                {
                    var exists = current.GetValueOrDefault(link.Id) is not null;
                    var fromFolder = path is not null && folders.Any(folder =>
                        string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.Ordinal));
                    if ((exists && !everything) || !fromFolder)
                    {
                        if (!exists)
                        {
                            missing.Add((JsonNode)new JsonObject { ["linkId"] = link.Id, ["uri"] = link.Uri });
                        }
                        continue;
                    }
                    var uri = link.Uri;
                    link.Relink(path!, edit.Args.Bool("refit") ? ImageFit.Fill : null);
                    relinked.Add((JsonNode)new JsonObject { ["linkId"] = link.Id, ["from"] = uri, ["to"] = path });
                }
                return JsonText.Write(new JsonObject { ["relinked"] = relinked, ["missing"] = missing });
            },
        },
        new()
        {
            Name = "idml_package",
            Title = "Package document",
            Description = "Like InDesign's File > Package: a zip with the .idml and every linked file it can find under Links/ (where InDesign and the PDF export look), optionally with font files under Document fonts/ (fonts from fontDirectories whose file name matches a used family; check the fonts' licences before sending them). Reports missing links.",
            InputSchema = Schema.Read(
                """ "outPath":{"type":"string","description":"Absolute path of the .zip to write"},""" +
                """ "linkDirectories":{"type":"array","items":{"type":"string"},"description":"More folders to find links in, by file name"},""" +
                """ "fontDirectories":{"type":"array","items":{"type":"string"},"description":"Folders with font files to include (by family name in the file name)"}""",
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
