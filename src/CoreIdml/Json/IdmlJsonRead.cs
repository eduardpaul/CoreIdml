using System.Text.Json;
using System.Xml.Linq;
using CoreIdml.Dom;

namespace CoreIdml.Json;

public static partial class IdmlJson
{
    /// <summary>A compact description of the document for programs and LLM agents (see <see cref="SummaryOptions"/>).</summary>
    public static DocumentSummary Summarize(IdmlDocument document, SummaryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new SummaryOptions();
        var size = document.PageSize;
        var stories = document.Stories.ToDictionary(story => story.Id, StringComparer.Ordinal);
        var tagsByContent = TagsByContent(document);
        var pages = document.Pages;
        var pageIndexById = pages.Select((page, index) => (page.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        var overset = new Dictionary<string, bool>(StringComparer.Ordinal);
        // Every story's frames from one walk of the spreads (Story.Frames walks them all for each story).
        var framesByStory = new Dictionary<string, List<TextFrame>>(StringComparer.Ordinal);
        foreach (var frame in document.Spreads.SelectMany(spread => spread.TextFrames()))
        {
            (framesByStory.TryGetValue(frame.StoryId, out var list) ? list : framesByStory[frame.StoryId] = []).Add(frame);
        }
        List<TextFrame> FramesOf(Story story) => Story.ThreadOrder(framesByStory.TryGetValue(story.Id, out var frames) ? frames : []);
        var resolver = new AttributeResolver(document);
        var sections = Sections(options.Sections);
        bool Wants(string section) => sections is null || sections.Contains(section);
        var pageFilter = options.Pages is { Count: > 0 } wanted ? wanted.ToHashSet() : null;
        if (pageFilter?.FirstOrDefault(index => index < 0 || index >= pages.Count, -1) is var badPage and >= 0)
        {
            throw new IdmlException(IdmlErrorKind.NotFound, $"Page index {badPage} does not exist (the document has {pages.Count} page(s), 0 to {pages.Count - 1}).");
        }
        // Off-page items have no page: a page filter leaves them out unless they are asked for by name.
        var wantsOffPage = sections is null ? pageFilter is null : sections.Contains("offPage");

        var summary = new DocumentSummary
        {
            DomVersion = document.DomVersion,
            PageWidth = size.Width,
            PageHeight = size.Height,
            FacingPages = document.FacingPages,
            PageCount = pages.Count,
        };
        if (options.IncludeResources && Wants("resources"))
        {
            summary.ParagraphStyles = document.Styles.ParagraphStyles.Select(style => style.DisplayName).ToList();
            summary.CharacterStyles = document.Styles.CharacterStyles.Select(style => style.DisplayName).ToList();
            summary.Swatches = document.Swatches.All.Select(swatch => swatch.Name).Where(name => !string.IsNullOrEmpty(name) && name != "$ID/").ToList();
            summary.Layers = document.Layers.All.Select(layer => layer.Name).ToList();
            summary.Tags = document.Tags.Names.ToList();
            summary.Fonts = document.UsedFontFamilies.ToList();
        }

        ItemSummary Describe(PageItem item, Rect bounds)
        {
            var frame = item as TextFrame;
            var graphic = item as GraphicFrame;
            Story? story = null;
            if (frame is not null)
            {
                stories.TryGetValue(frame.StoryId, out story);
            }
            bool? likelyOverset = null;
            if (story is not null)
            {
                if (!overset.TryGetValue(story.Id, out var value))
                {
                    value = TextFit.EstimateWith(story, FramesOf(story), resolver).LikelyOverset;
                    overset[story.Id] = value;
                }
                likelyOverset = value ? true : null;
            }
            var tag = tagsByContent.GetValueOrDefault(item.Id)
                ?? (frame is not null ? tagsByContent.GetValueOrDefault(frame.StoryId) : null)
                ?? (graphic?.Graphic?.Attribute("Self")?.Value is { } graphicId ? tagsByContent.GetValueOrDefault(graphicId) : null);
            return new ItemSummary
            {
                Id = item.Id,
                Kind = item.Kind,
                Name = item.Name,
                Label = item.Label,
                Tag = tag,
                X = Math.Round(bounds.X, 3),
                Y = Math.Round(bounds.Y, 3),
                Width = Math.Round(bounds.Width, 3),
                Height = Math.Round(bounds.Height, 3),
                StoryId = frame?.StoryId,
                Text = story is null ? null : Truncate(story.Text, options.MaxTextLength),
                LikelyOverset = likelyOverset,
                LinkId = graphic?.LinkId,
                ImageUri = graphic?.LinkUri,
            };
        }

        // Item id → page index of the pages described, for links and the story filter.
        var framePage = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var spread in document.Spreads)
        {
            var spreadPages = spread.Pages;
            var onPage = new HashSet<string>(StringComparer.Ordinal);
            foreach (var page in spreadPages)
            {
                var pageSize = page.Size;
                var pageSummary = new PageSummary
                {
                    Index = pageIndexById[page.Id],
                    Id = page.Id,
                    SpreadId = spread.Id,
                    Name = page.Name,
                    Width = pageSize.Width,
                    Height = pageSize.Height,
                };
                var included = pageFilter is null || pageFilter.Contains(pageSummary.Index);
                foreach (var item in page.PageItems.SelectMany(item => item.SelfAndDescendants()))
                {
                    onPage.Add(item.Id);
                    if (included)
                    {
                        framePage.TryAdd(item.Id, pageSummary.Index);
                        if (Wants("pages"))
                        {
                            pageSummary.Items.Add(Describe(item, page.FromSpread(item.SpreadBounds)));
                        }
                    }
                }
                if (included)
                {
                    summary.Pages!.Add(pageSummary);
                }
            }
            if (wantsOffPage)
            {
                foreach (var item in spread.PageItems.Where(item => !onPage.Contains(item.Id)).SelectMany(item => item.SelfAndDescendants()))
                {
                    summary.OffPage!.Add(Describe(item, item.SpreadBounds));
                }
            }
        }
        summary.Pages = Wants("pages") ? summary.Pages!.OrderBy(page => page.Index).ToList() : null;
        summary.OffPage = wantsOffPage ? summary.OffPage : null;

        summary.Links = !Wants("links") ? null : document.Links.Select(link => new LinkSummary
        {
            Id = link.Id,
            Uri = link.Uri,
            Format = link.Format,
            FrameId = link.FrameId,
            PageIndex = link.FrameId is { } frameId && framePage.TryGetValue(frameId, out var index) ? index : null,
            Location = link.IsInStory ? "story" : link.PartPath.StartsWith("MasterSpreads/", StringComparison.Ordinal) ? "master" : "spread",
            ActualPpi = link.ActualPpi,
            EffectivePpi = link.EffectivePpi,
        }).Where(link => pageFilter is null || link.PageIndex is not null).ToList();

        summary.Stories = !Wants("stories") ? null : stories.Values
            .Where(story => pageFilter is null || FramesOf(story).Any(frame => framePage.ContainsKey(frame.Id)))
            .Select(story => DescribeStory(story, FramesOf(story), options.MaxTextLength, options.IncludeRuns)).ToList();
        return summary;
    }

    /// <summary>The requested sections (null: all), checked against <see cref="SummaryOptions.SectionNames"/>.</summary>
    private static HashSet<string>? Sections(List<string>? requested)
    {
        if (requested is null)
        {
            return null;
        }
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in requested)
        {
            var known = SummaryOptions.SectionNames.FirstOrDefault(section => string.Equals(section, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new IdmlException($"Unknown summary section '{name}' (sections: {string.Join(", ", SummaryOptions.SectionNames)}).");
            result.Add(known);
        }
        return result;
    }

    public static string SummarizeToJson(IdmlDocument document, SummaryOptions? options = null) =>
        JsonSerializer.Serialize(Summarize(document, options), Output.DocumentSummary);

    /// <summary>Summary with options given as JSON (<see cref="SummaryOptions"/>); null or empty uses the defaults.</summary>
    public static string SummarizeToJson(IdmlDocument document, string? optionsJson) =>
        SummarizeToJson(document, string.IsNullOrWhiteSpace(optionsJson) ? null : JsonSerializer.Deserialize(optionsJson, IdmlJsonContext.Default.SummaryOptions));

    /// <summary>One story in full: text, frames, and every indexed content run with its styles.</summary>
    public static StorySummary GetStory(IdmlDocument document, string storyId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var story = document.RequireStory(storyId);
        return DescribeStory(story, story.Frames, maxTextLength: -1, includeRuns: true);
    }

    public static string GetStoryJson(IdmlDocument document, string storyId) =>
        JsonSerializer.Serialize(GetStory(document, storyId), Output.StorySummary);

    /// <summary>The XML structure as a tree (tags, paths, bindings, attributes, text).</summary>
    public static XmlNodeSummary? XmlStructure(IdmlDocument document, int maxTextLength = 200)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Tags.Root is { } root ? DescribeNode(root, maxTextLength) : null;
    }

    public static string XmlStructureJson(IdmlDocument document, int maxTextLength = 200) =>
        XmlStructure(document, maxTextLength) is { } root ? JsonSerializer.Serialize(root, Output.XmlNodeSummary) : "null";

    /// <summary>The XML structure (or the subtree at <paramref name="at"/>) as an XML string.</summary>
    public static string ExportXml(IdmlDocument document, string? at = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Tags.ExportXml(at).ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Imports XML onto the structure; options and report as JSON (<see cref="XmlImportOptionsSpec"/>, <see cref="XmlImportReportSpec"/>).</summary>
    public static string ImportXml(IdmlDocument document, string xml, string? at = null, string? optionsJson = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var spec = string.IsNullOrWhiteSpace(optionsJson) ? new XmlImportOptionsSpec() : JsonSerializer.Deserialize(optionsJson, IdmlJsonContext.Default.XmlImportOptionsSpec) ?? new XmlImportOptionsSpec();
        var options = new XmlImportOptions
        {
            ImageFit = ParseFit(spec.Fit),
            CharacterStyles = spec.CharacterStyles ?? [],
            StripControlAttributes = spec.KeepControlAttributes != true,
            ResolveImage = spec.RelinkOnly == true ? _ => null : null,
        };
        XElement source;
        try
        {
            source = TagCatalog.ParseData(xml);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new IdmlException(IdmlErrorKind.InvalidFormat, "The XML to import is not well-formed: " + exception.Message, exception);
        }
        var report = document.Tags.ImportXml(source, at ?? "/" + (document.Tags.Root?.Tag ?? "Root"), options);
        return JsonSerializer.Serialize(new XmlImportReportSpec
        {
            Updated = report.Updated,
            Added = report.Added,
            Deleted = report.Deleted,
            Unmatched = report.Unmatched,
            Warnings = report.Warnings,
        }, Output.XmlImportReportSpec);
    }

    /// <summary>What changed between two versions of a document: stories (by id, run by run), links, page count and page items.</summary>
    public static DiffReport Diff(IdmlDocument before, IdmlDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var report = new DiffReport();
        if (before.Pages.Count != after.Pages.Count)
        {
            report.Changes.Add(new DiffEntry { Kind = "page-count", Before = before.Pages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), After = after.Pages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        var oldStories = before.Stories.ToDictionary(story => story.Id, StringComparer.Ordinal);
        var newStories = after.Stories.ToDictionary(story => story.Id, StringComparer.Ordinal);
        foreach (var (id, story) in oldStories.Where(pair => !newStories.ContainsKey(pair.Key)))
        {
            report.Changes.Add(new DiffEntry { Kind = "story-removed", Id = id, Before = Truncate(story.Text, 200) });
        }
        foreach (var (id, story) in newStories.Where(pair => !oldStories.ContainsKey(pair.Key)))
        {
            report.Changes.Add(new DiffEntry { Kind = "story-added", Id = id, After = Truncate(story.Text, 200) });
        }
        foreach (var (id, oldStory) in oldStories.Where(pair => newStories.ContainsKey(pair.Key)))
        {
            var oldRuns = oldStory.ContentRuns;
            var newRuns = newStories[id].ContentRuns;
            if (oldRuns.Count != newRuns.Count)
            {
                if (oldStory.Text != newStories[id].Text)
                {
                    report.Changes.Add(new DiffEntry { Kind = "runs-restructured", Id = id, Before = Truncate(oldStory.Text, 200), After = Truncate(newStories[id].Text, 200) });
                }
                continue;
            }
            for (var i = 0; i < oldRuns.Count; i++)
            {
                if (oldRuns[i].Text != newRuns[i].Text)
                {
                    report.Changes.Add(new DiffEntry { Kind = "run-changed", Id = id, RunIndex = i, Before = oldRuns[i].Text, After = newRuns[i].Text });
                }
            }
        }
        var oldLinks = before.Links.GroupBy(link => link.Id).ToDictionary(group => group.Key, group => group.First().Uri, StringComparer.Ordinal);
        var newLinks = after.Links.GroupBy(link => link.Id).ToDictionary(group => group.Key, group => group.First().Uri, StringComparer.Ordinal);
        foreach (var (id, uri) in oldLinks)
        {
            if (!newLinks.TryGetValue(id, out var newUri))
            {
                report.Changes.Add(new DiffEntry { Kind = "link-removed", Id = id, Before = uri });
            }
            else if (newUri != uri)
            {
                report.Changes.Add(new DiffEntry { Kind = "link-changed", Id = id, Before = uri, After = newUri });
            }
        }
        foreach (var (id, uri) in newLinks.Where(pair => !oldLinks.ContainsKey(pair.Key)))
        {
            report.Changes.Add(new DiffEntry { Kind = "link-added", Id = id, After = uri });
        }
        DiffItems(before, after, report.Changes);
        report.Identical = report.Changes.Count == 0;
        return report;
    }

    /// <summary>
    /// Page items by id, on pages and masters: added, removed, moved or resized (page-relative bounds), and changed in
    /// their own settings (fill, stroke, layer, name, visibility, frame options…), each changed setting as name=value.
    /// </summary>
    private static void DiffItems(IdmlDocument before, IdmlDocument after, List<DiffEntry> changes)
    {
        static Dictionary<string, PageItem> ById(IdmlDocument document) =>
            document.AllPageItems.Concat(document.MasterSpreads.SelectMany(master => master.AllPageItems))
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var oldItems = ById(before);
        var newItems = ById(after);
        foreach (var (id, item) in oldItems.Where(pair => !newItems.ContainsKey(pair.Key)))
        {
            changes.Add(new DiffEntry { Kind = "item-removed", Id = id, Before = DescribeItem(item) });
        }
        foreach (var (id, item) in newItems.Where(pair => !oldItems.ContainsKey(pair.Key)))
        {
            changes.Add(new DiffEntry { Kind = "item-added", Id = id, After = DescribeItem(item) });
        }
        foreach (var (id, oldItem) in oldItems)
        {
            if (!newItems.TryGetValue(id, out var newItem))
            {
                continue;
            }
            var (oldPlace, newPlace) = (Placement(oldItem), Placement(newItem));
            if (oldPlace != newPlace)
            {
                changes.Add(new DiffEntry { Kind = "item-geometry", Id = id, Before = oldPlace, After = newPlace });
            }
            var oldSettings = ItemSettings(oldItem.Element);
            var newSettings = ItemSettings(newItem.Element);
            var changed = oldSettings.Keys.Union(newSettings.Keys, StringComparer.Ordinal)
                .Where(key => oldSettings.GetValueOrDefault(key) != newSettings.GetValueOrDefault(key))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (changed.Count > 0)
            {
                changes.Add(new DiffEntry
                {
                    Kind = "item-changed",
                    Id = id,
                    Before = Truncate(string.Join("; ", changed.Select(key => $"{key}={oldSettings.GetValueOrDefault(key) ?? "(none)"}")), 400),
                    After = Truncate(string.Join("; ", changed.Select(key => $"{key}={newSettings.GetValueOrDefault(key) ?? "(none)"}")), 400),
                });
            }
        }
    }

    private static string DescribeItem(PageItem item) =>
        (item.Name is { Length: > 0 } name ? $"{item.Kind} '{name}'" : item.Kind) + ", " + Placement(item);

    /// <summary>Page-relative bounds in points, and the page ("x=10 y=20 width=100 height=0 on page 1").</summary>
    private static string Placement(PageItem item)
    {
        static string N(double value) => Math.Round(value, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var bounds = item.Bounds;
        var where = item.Page is { } page ? $" on page {page.Name}" : item.Spread is MasterSpread master ? $" on master {master.Name}" : " off the pages";
        return $"x={N(bounds.X)} y={N(bounds.Y)} width={N(bounds.Width)} height={N(bounds.Height)}{where}";
    }

    /// <summary>
    /// An item's own settings as path=value: its attributes (not its transform, which <see cref="Placement"/> covers)
    /// and those of its setting elements (Properties, TextFramePreference, TransparencySetting…), leaving out nested page
    /// items (compared on their own), its path (geometry) and placed graphics (links).
    /// </summary>
    private static Dictionary<string, string> ItemSettings(XElement element)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(XElement node, string prefix)
        {
            foreach (var attribute in node.Attributes())
            {
                if (prefix.Length == 0 && attribute.Name.LocalName is "Self" or "ItemTransform")
                {
                    continue;
                }
                settings[prefix + attribute.Name.LocalName] = attribute.Value;
            }
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in node.Elements())
            {
                var name = child.Name.LocalName;
                if (PageItem.IsPageItemKind(name) || name is "PathGeometry" or "Image" or "PDF" or "EPS" or "ImportedPage" or "WMF" or "PICT" or "SVG")
                {
                    continue;
                }
                var count = seen[name] = seen.GetValueOrDefault(name) + 1;
                var path = prefix + name + (count > 1 ? $"[{count}]" : string.Empty);
                if (!child.HasElements && child.Value.Length > 0)
                {
                    settings[path] = child.Value;
                }
                Walk(child, path + ".");
            }
        }
        Walk(element, string.Empty);
        return settings;
    }

    public static string DiffJson(IdmlDocument before, IdmlDocument after) =>
        JsonSerializer.Serialize(Diff(before, after), Output.DiffReport);

    // ---- helpers ---------------------------------------------------------------------------------------

    private static StorySummary DescribeStory(Story story, IReadOnlyList<TextFrame> frames, int maxTextLength, bool includeRuns)
    {
        var runs = story.ContentRuns;
        return new StorySummary
        {
            Id = story.Id,
            Text = Truncate(story.Text, maxTextLength),
            WordCount = runs.Sum(run => ContentRun.CountWords(run.Text)),
            RunCount = runs.Count,
            Tables = story.Tables.Count,
            Frames = frames.Select(frame => frame.Id).ToList(),
            Runs = includeRuns
                ? runs.Select(run => new RunSummary
                {
                    Index = run.Index,
                    Text = run.Text,
                    ParagraphStyle = run.ParagraphStyle,
                    CharacterStyle = run.CharacterStyle,
                    Scope = run.Scope,
                    Special = run.HasSpecialCharacters ? true : null,
                }).ToList()
                : null,
        };
    }

    private static XmlNodeSummary DescribeNode(XmlElementNode node, int maxTextLength)
    {
        var children = node.Children;
        var attributes = node.Attributes;
        string? boundTo = node.ContentId is null ? null
            : node.BoundStory is not null ? "story"
            : node.BoundPageItem is GraphicFrame frame && frame.Id != node.ContentId ? "graphic"
            : node.BoundPageItem is not null ? "frame"
            : null;
        return new XmlNodeSummary
        {
            Tag = node.Tag,
            Id = node.Id,
            Path = node.Path,
            BoundTo = boundTo,
            ContentId = node.ContentId,
            Attributes = attributes.Count == 0 ? null : attributes.ToDictionary(StringComparer.Ordinal),
            Text = children.Count == 0 && node.ContentElement.Ancestors("Story").Any() || node.BoundStory is not null
                ? Truncate(node.Text, maxTextLength) is { Length: > 0 } text ? text : null
                : null,
            Children = children.Count == 0 ? null : children.Select(child => DescribeNode(child, maxTextLength)).ToList(),
        };
    }

    /// <summary>Content id (story, item or graphic) → tag name, for annotating summary items.</summary>
    private static Dictionary<string, string> TagsByContent(IdmlDocument document)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in document.Tags.BindingCarriers())
        {
            if (element.Attribute("XMLContent")?.Value is { } id && !result.ContainsKey(id))
            {
                result[id] = TagCatalog.TagName(element.Attribute("MarkupTag")?.Value);
            }
        }
        return result;
    }

    private static string? Truncate(string? text, int length) =>
        text is null || length == 0 ? null
        : length < 0 || text.Length <= length ? text
        : text[..length] + "…";
}
