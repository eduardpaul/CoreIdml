using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml;

public enum IssueSeverity
{
    Warning,
    Error,
}

public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Message, string? Part = null);

public sealed class ValidationResult
{
    public List<ValidationIssue> Issues { get; } = [];

    public bool IsValid => Issues.All(issue => issue.Severity != IssueSeverity.Error);

    public override string ToString() =>
        Issues.Count == 0 ? "OK" : string.Join(Environment.NewLine, Issues.Select(issue => $"{issue.Severity} {issue.Code}: {issue.Message}"));
}

/// <summary>
/// Structural checks catching what makes InDesign refuse or silently damage a file: broken part references,
/// duplicate ids, frames pointing at missing stories, styles or swatches that do not exist. Passing does not
/// prove InDesign will open the file, failing means it very likely will not open it correctly.
/// </summary>
public static class IdmlValidator
{
    private static readonly string[] ReferenceAttributes =
        ["AppliedParagraphStyle", "AppliedCharacterStyle", "FillColor", "StrokeColor"];

    public static ValidationResult Validate(IdmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new ValidationResult();
        var package = document.Package;

        if (!package.Contains(IdmlNames.MimeTypePath) || Encoding.ASCII.GetString(package.GetBytes(IdmlNames.MimeTypePath).Span) != IdmlNames.MimeType)
        {
            result.Issues.Add(new(IssueSeverity.Error, "mimetype", "The mimetype part is missing or wrong."));
        }

        foreach (var reference in document.DesignMap.Elements().Where(element => element.Name.Namespace == IdmlNames.PackagingNamespace))
        {
            var src = reference.Attribute("src")?.Value;
            if (src is null || !package.Contains(src))
            {
                result.Issues.Add(new(IssueSeverity.Error, "missing-part", $"designmap references '{src}', which is not in the package."));
            }
        }

        var referenced = document.DesignMap.Elements().Select(element => element.Attribute("src")?.Value).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var path in package.PartPaths.Where(path => path.StartsWith("Stories/", StringComparison.Ordinal) || path.StartsWith("Spreads/", StringComparison.Ordinal)))
        {
            if (!referenced.Contains(path))
            {
                result.Issues.Add(new(IssueSeverity.Warning, "orphan-part", $"'{path}' is not referenced from designmap.xml and will be ignored.", path));
            }
        }

        var parts = new List<(string Path, XElement Root)>();
        foreach (var path in package.PartPaths.Where(path => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("META-INF/", StringComparison.Ordinal)))
        {
            try
            {
                if (package.GetXml(path).Root is { } root)
                {
                    parts.Add((path, root));
                }
            }
            catch (IdmlException exception)
            {
                result.Issues.Add(new(IssueSeverity.Error, "xml", exception.Message, path));
            }
        }

        CheckDuplicateIds(parts, result);

        var stories = document.Stories.Select(story => story.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var frame in document.Spreads.Concat(document.MasterSpreads).SelectMany(spread => spread.Element.Descendants("TextFrame")))
        {
            var storyId = frame.Attribute("ParentStory")?.Value;
            if (storyId is null || !stories.Contains(storyId))
            {
                result.Issues.Add(new(IssueSeverity.Error, "missing-story", $"Text frame '{frame.Attribute("Self")?.Value}' shows story '{storyId}', which does not exist."));
            }
        }

        var masters = document.MasterSpreads.Select(master => master.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var page in document.Pages)
        {
            var master = page.GetAttribute("AppliedMaster");
            if (master is not null && master != IdmlNames.NoReference && !masters.Contains(master))
            {
                result.Issues.Add(new(IssueSeverity.Error, "missing-master", $"Page '{page.Name}' applies master '{master}', which does not exist."));
            }
        }

        CheckReferences(document, parts, result);

        var known = parts
            .Where(part => part.Path.StartsWith("Resources/", StringComparison.Ordinal))
            .SelectMany(part => part.Root.Descendants())
            .Select(element => element.Attribute("Self")?.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, root) in parts.Where(part => part.Path.StartsWith("Stories/", StringComparison.Ordinal) || part.Path.StartsWith("Spreads/", StringComparison.Ordinal) || part.Path.StartsWith("MasterSpreads/", StringComparison.Ordinal)))
        {
            foreach (var element in root.DescendantsAndSelf())
            {
                foreach (var name in ReferenceAttributes)
                {
                    var value = element.Attribute(name)?.Value;
                    if (value is null || value == IdmlNames.NoReference || !value.Contains('/', StringComparison.Ordinal) || known.Contains(value))
                    {
                        continue;
                    }
                    if (reported.Add(value))
                    {
                        result.Issues.Add(new(IssueSeverity.Error, "missing-resource", $"{name}=\"{value}\" does not exist in Resources.", path));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Checks the zip layout of saved bytes: <c>mimetype</c> first and stored uncompressed.</summary>
    public static ValidationResult ValidatePackageBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var result = new ValidationResult();
        // Local file header: signature(4) version(2) flags(2) method(2) ... name length at 26, name at 30.
        if (data.Length < 38 || BitConverter.ToUInt32(data, 0) != 0x04034b50)
        {
            result.Issues.Add(new(IssueSeverity.Error, "zip", "Not a zip file."));
            return result;
        }
        var method = BitConverter.ToUInt16(data, 8);
        var nameLength = BitConverter.ToUInt16(data, 26);
        var firstName = Encoding.ASCII.GetString(data, 30, Math.Min(nameLength, data.Length - 30));
        if (firstName != IdmlNames.MimeTypePath)
        {
            result.Issues.Add(new(IssueSeverity.Error, "mimetype-order", $"First zip entry is '{firstName}', expected 'mimetype'."));
        }
        if (method != 0)
        {
            result.Issues.Add(new(IssueSeverity.Error, "mimetype-compressed", "The mimetype entry must be stored without compression."));
        }
        try
        {
            using var archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            if (archive.GetEntry(IdmlNames.DesignMapPath) is null)
            {
                result.Issues.Add(new(IssueSeverity.Error, "missing-part", "designmap.xml is missing."));
            }
        }
        catch (InvalidDataException exception)
        {
            result.Issues.Add(new(IssueSeverity.Error, "zip", exception.Message));
        }
        return result;
    }

    /// <summary>XML bindings, layers and text threads must point at things that exist.</summary>
    private static void CheckReferences(IdmlDocument document, List<(string Path, XElement Root)> parts, ValidationResult result)
    {
        var layoutParts = parts.Where(part => part.Path.StartsWith("Stories/", StringComparison.Ordinal)
            || part.Path.StartsWith("Spreads/", StringComparison.Ordinal)
            || part.Path.StartsWith("MasterSpreads/", StringComparison.Ordinal)).ToList();
        var ids = layoutParts.SelectMany(part => part.Root.Descendants())
            .Select(element => element.Attribute("Self")?.Value).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var xmlParts = layoutParts.Concat(parts.Where(part => part.Path.StartsWith("XML/", StringComparison.Ordinal)));
        foreach (var (path, root) in xmlParts)
        {
            foreach (var element in root.Descendants("XMLElement"))
            {
                if (element.Attribute("XMLContent")?.Value is { } target && !ids.Contains(target))
                {
                    result.Issues.Add(new(IssueSeverity.Error, "dangling-xml-binding",
                        $"XML element '{element.Attribute("Self")?.Value}' is bound to '{target}', which does not exist.", path));
                }
            }
        }

        var layers = document.Layers.All.Select(layer => layer.Id).ToHashSet(StringComparer.Ordinal);
        var frames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, root) in layoutParts.Where(part => !part.Path.StartsWith("Stories/", StringComparison.Ordinal)))
        {
            foreach (var element in root.Descendants())
            {
                if (element.Attribute("ItemLayer")?.Value is { } layer && !layers.Contains(layer))
                {
                    result.Issues.Add(new(IssueSeverity.Error, "missing-layer",
                        $"'{element.Attribute("Self")?.Value}' is on layer '{layer}', which does not exist.", path));
                }
                if (element.Name.LocalName == "TextFrame" && element.Attribute("Self")?.Value is { } self)
                {
                    frames.Add(self);
                }
            }
        }
        foreach (var (path, root) in layoutParts)
        {
            foreach (var frame in root.Descendants("TextFrame"))
            {
                foreach (var name in (string[])["NextTextFrame", "PreviousTextFrame"])
                {
                    if (frame.Attribute(name)?.Value is { } target && target != IdmlNames.NoReference && !frames.Contains(target))
                    {
                        result.Issues.Add(new(IssueSeverity.Error, "broken-thread",
                            $"Text frame '{frame.Attribute("Self")?.Value}' has {name}=\"{target}\", which does not exist.", path));
                    }
                }
            }
        }
    }

    private static void CheckDuplicateIds(List<(string Path, XElement Root)> parts, ValidationResult result)
    {
        // A tagged story legitimately repeats its XMLElement Self in the backing story; skip XMLElement ids.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, root) in parts)
        {
            foreach (var element in root.Descendants())
            {
                if (element.Name.LocalName == "XMLElement" || element.Attribute("Self")?.Value is not { } self)
                {
                    continue;
                }
                if (seen.TryGetValue(self, out var firstPath))
                {
                    result.Issues.Add(new(IssueSeverity.Error, "duplicate-id", $"Self=\"{self}\" appears in '{firstPath}' and '{path}'.", path));
                }
                else
                {
                    seen.Add(self, path);
                }
            }
        }
    }
}
