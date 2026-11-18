using CoreIdml.Dom;
using CoreIdml.Xml;

namespace CoreIdml;

public sealed partial class IdmlDocument
{
    /// <summary>Every link (placed file) in spreads, master spreads and stories.</summary>
    public IReadOnlyList<ImageLink> Links
    {
        get
        {
            var parts = ManifestPaths("Spread").Concat(ManifestPaths("MasterSpread")).Concat(ManifestPaths("Story"));
            return parts.SelectMany(path => Package.GetXml(path).Descendants("Link").Select(link => new ImageLink(this, link, path))).ToList();
        }
    }

    /// <summary>Relinks the link with the given id (see <see cref="Links"/>).</summary>
    public void RelinkImage(string linkId, string uriOrPath, ImageFit? refit = null)
    {
        var link = Links.FirstOrDefault(candidate => candidate.Id == linkId)
            ?? throw new IdmlException(IdmlErrorKind.NotFound, $"No <Link> with Self=\"{linkId}\" found in any spread or story.");
        link.Relink(uriOrPath, refit);
    }

    /// <summary>The story with the given id, or an error naming it.</summary>
    public Story RequireStory(string id) =>
        FindStory(id) ?? throw new IdmlException(IdmlErrorKind.NotFound, $"No story with Self=\"{id}\" in this document.");

    /// <summary>Sets the text of one content run of a story (idmlkit-style surgical edit).</summary>
    public void SetRunText(string storyId, int runIndex, string text, bool force = false) =>
        RequireStory(storyId).SetRunText(runIndex, text, force);

    /// <summary>Font families recorded in Resources/Fonts.xml (what InDesign reported as used or installed).</summary>
    public IReadOnlyList<FontFamilyInfo> Fonts =>
        (Package.TryGetXml("Resources/Fonts.xml")?.Root?.Elements("FontFamily") ?? [])
            .Select(family => new FontFamilyInfo(
                family.Attribute("Name")?.Value ?? string.Empty,
                family.Elements("Font").Select(font => font.Attribute("FontStyleName")?.Value ?? string.Empty).ToList(),
                family.Elements("Font").Select(font => font.Attribute("Status")?.Value ?? string.Empty).Distinct().ToList()))
            .ToList();

    /// <summary>Font families referenced by styles and text (what the document needs installed to print).</summary>
    public IReadOnlyList<string> UsedFontFamilies
    {
        get
        {
            var parts = ManifestPaths("Story").Concat(ManifestPaths("Spread")).Concat(ManifestPaths("MasterSpread")).Append("Resources/Styles.xml");
            return parts.Where(Package.Contains)
                .SelectMany(path => Package.GetXml(path).Descendants("AppliedFont"))
                .Select(font => font.Value)
                .Where(name => name.Length > 0 && name != IdmlNames.EmptyString)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
        }
    }
}
