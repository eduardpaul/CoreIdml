namespace CoreIdml.Json;

// Read-side JSON. Designed for programs and LLM agents alike: stable ids for every object an edit can target
// (story, content run index, link, frame), page-relative geometry, and size controls (SummaryOptions).

/// <summary>Controls the size of a <see cref="DocumentSummary"/>.</summary>
public sealed class SummaryOptions
{
    /// <summary>Characters of text kept per frame and story; 0 omits text, -1 keeps all. Default 200.</summary>
    public int MaxTextLength { get; set; } = 200;

    /// <summary>Include each story's indexed content runs (needed for set-run-text edits). Default false.</summary>
    public bool IncludeRuns { get; set; }

    /// <summary>Include styles, swatches, layers, tags and fonts. Default true.</summary>
    public bool IncludeResources { get; set; } = true;

    /// <summary>
    /// 0-based pages to describe; null describes every page. With a page filter, stories and links are limited to
    /// those shown on these pages and pasteboard items are left out (unless "offPage" is asked for in <see cref="Sections"/>).
    /// </summary>
    public List<int>? Pages { get; set; }

    /// <summary>
    /// Parts of the summary to include: "pages", "offPage", "stories", "links", "resources" (styles, swatches,
    /// layers, tags, fonts). Null includes all of them. Parts left out are omitted from the JSON.
    /// </summary>
    public List<string>? Sections { get; set; }

    /// <summary>The section names <see cref="Sections"/> accepts.</summary>
    public static IReadOnlyList<string> SectionNames { get; } = ["pages", "offPage", "stories", "links", "resources"];
}

public sealed class DocumentSummary
{
    public string DomVersion { get; set; } = string.Empty;

    public double PageWidth { get; set; }

    public double PageHeight { get; set; }

    public bool FacingPages { get; set; }

    public int PageCount { get; set; }

    public List<PageSummary>? Pages { get; set; } = [];

    /// <summary>Items on the pasteboard (centre outside every page).</summary>
    public List<ItemSummary>? OffPage { get; set; } = [];

    public List<StorySummary>? Stories { get; set; } = [];

    public List<LinkSummary>? Links { get; set; } = [];

    public List<string>? ParagraphStyles { get; set; }

    public List<string>? CharacterStyles { get; set; }

    public List<string>? ObjectStyles { get; set; }

    public List<string>? Swatches { get; set; }

    public List<string>? Layers { get; set; }

    public List<string>? Tags { get; set; }

    /// <summary>Font families the document uses.</summary>
    public List<string>? Fonts { get; set; }
}

public sealed class PageSummary
{
    public int Index { get; set; }

    public string Id { get; set; } = string.Empty;

    public string SpreadId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public double Width { get; set; }

    public double Height { get; set; }

    public List<ItemSummary> Items { get; set; } = [];
}

public sealed class ItemSummary
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string? Label { get; set; }

    /// <summary>XML tag bound to the item (or to its story or placed graphic).</summary>
    public string? Tag { get; set; }

    /// <summary>Page-relative position in points (spread-relative for off-page items).</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public string? StoryId { get; set; }

    public string? Text { get; set; }

    /// <summary>The story probably does not fit its frames (an estimate: InDesign's composer is not available).</summary>
    public bool? LikelyOverset { get; set; }

    public string? LinkId { get; set; }

    public string? ImageUri { get; set; }

    /// <summary>The object style applied to the item, when it is not a built-in one ([None], [Normal Text Frame], ...).</summary>
    public string? ObjectStyle { get; set; }
}

public sealed class StorySummary
{
    public string Id { get; set; } = string.Empty;

    public string? Text { get; set; }

    public int WordCount { get; set; }

    public int RunCount { get; set; }

    public int Tables { get; set; }

    /// <summary>Ids of the frames showing the story, in thread order.</summary>
    public List<string> Frames { get; set; } = [];

    public List<RunSummary>? Runs { get; set; }
}

public sealed class RunSummary
{
    public int Index { get; set; }

    public string Text { get; set; } = string.Empty;

    public string? ParagraphStyle { get; set; }

    public string? CharacterStyle { get; set; }

    /// <summary>"body", "cell column:row", "footnote" or "note".</summary>
    public string Scope { get; set; } = "body";

    /// <summary>Holds special characters (page numbers...): editing it requires force.</summary>
    public bool? Special { get; set; }

    /// <summary>
    /// "inserted", "deleted" or "moved" for text recorded with Track Changes. Deleted text is not shown in InDesign
    /// (nor in the story text, previews or fit checks); it keeps its run index.
    /// </summary>
    public string? Change { get; set; }
}

public sealed class LinkSummary
{
    public string Id { get; set; } = string.Empty;

    public string Uri { get; set; } = string.Empty;

    public string? Format { get; set; }

    public string? FrameId { get; set; }

    public int? PageIndex { get; set; }

    /// <summary>"spread", "master" or "story" (anchored in text).</summary>
    public string Location { get; set; } = "spread";

    public string? ActualPpi { get; set; }

    public string? EffectivePpi { get; set; }
}

public sealed class XmlNodeSummary
{
    public string Tag { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>"story", "frame", "graphic" or null.</summary>
    public string? BoundTo { get; set; }

    public string? ContentId { get; set; }

    public Dictionary<string, string>? Attributes { get; set; }

    public string? Text { get; set; }

    public List<XmlNodeSummary>? Children { get; set; }
}

public sealed class XmlImportOptionsSpec
{
    /// <summary>fill (default), fit, stretch, center, original.</summary>
    public string? Fit { get; set; }

    /// <summary>Tag → character style name.</summary>
    public Dictionary<string, string>? CharacterStyles { get; set; }

    /// <summary>Only relink images from href, never re-measure/refit them.</summary>
    public bool? RelinkOnly { get; set; }

    public bool? KeepControlAttributes { get; set; }
}

public sealed class XmlImportReportSpec
{
    public int Updated { get; set; }

    public int Added { get; set; }

    public int Deleted { get; set; }

    public List<string> Unmatched { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

public sealed class DiffReport
{
    public bool Identical { get; set; }

    public List<DiffEntry> Changes { get; set; } = [];
}

public sealed class DiffEntry
{
    /// <summary>
    /// story-added, story-removed, run-changed, runs-restructured, link-changed, link-added, link-removed, page-count,
    /// item-added, item-removed, item-geometry (page-relative bounds), item-changed (the item's changed settings, as name=value).
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    public string? Id { get; set; }

    public int? RunIndex { get; set; }

    public string? Before { get; set; }

    public string? After { get; set; }
}
