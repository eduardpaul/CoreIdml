using System.Text.Json.Nodes;
using CoreIdml.Dom;

namespace CoreIdml.Cli.Tools;

/// <summary>idml_find: page items by name, label, tag and kind; stories by text.</summary>
internal static class Finder
{
    private const int SnippetRadius = 40;

    public static string Find(IdmlDocument document, ToolArguments args)
    {
        var text = args.OptionalString("text");
        var name = args.OptionalString("name");
        var label = args.OptionalString("label");
        var tag = args.OptionalString("tag");
        var kind = args.OptionalString("kind");
        var pageFilter = args.OptionalInt("pageIndex");
        var max = args.OptionalInt("maxResults") ?? 50;
        if (text is null && name is null && label is null && tag is null && kind is null && pageFilter is null)
        {
            throw ToolException.Invalid("Pass at least one of text, name, label, tag, kind or pageIndex.");
        }
        if (pageFilter is { } wantedPage)
        {
            LayoutTools.RequirePage(document, wantedPage);
        }

        var pageIndex = document.Pages.Select((page, index) => (page.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in document.Tags.AllNodes)
        {
            if (node.ContentId is { } content)
            {
                tags.TryAdd(content, node.Tag);
            }
        }
        string? TagOf(PageItem item) =>
            tags.GetValueOrDefault(item.Id) ?? (item is TextFrame frame ? tags.GetValueOrDefault(frame.StoryId) : null)
            ?? (item is GraphicFrame { Graphic: { } graphic } && graphic.Attribute("Self")?.Value is { } graphicId ? tags.GetValueOrDefault(graphicId) : null);
        static bool Has(string? value, string? wanted) => wanted is null || (value is not null && value.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        var truncated = false;
        var items = new JsonArray();
        var storyPages = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var item in document.AllPageItems)
        {
            var page = item.Page;
            var index = page is null ? (int?)null : pageIndex.GetValueOrDefault(page.Id);
            if (item is TextFrame textFrame && index is { } shown)
            {
                (storyPages.TryGetValue(textFrame.StoryId, out var set) ? set : storyPages[textFrame.StoryId] = []).Add(shown);
            }
            if (name is null && label is null && tag is null && kind is null)
            {
                continue; // text-only searches report stories
            }
            var itemTag = TagOf(item);
            if (!Has(item.Name, name) || !Has(item.Label, label) || !Has(itemTag, tag) || (kind is not null && !string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
                || (pageFilter is not null && index != pageFilter))
            {
                continue;
            }
            if (text is not null && (item is not TextFrame frame || document.FindStory(frame.StoryId)?.Text.Contains(text, StringComparison.OrdinalIgnoreCase) != true))
            {
                continue;
            }
            if (items.Count >= max)
            {
                truncated = true;
                break;
            }
            var bounds = item.Bounds;
            var entry = new JsonObject
            {
                ["id"] = item.Id,
                ["kind"] = item.Kind,
                ["name"] = item.Name,
                ["label"] = item.Label,
                ["tag"] = itemTag,
                ["pageIndex"] = index,
                ["x"] = Math.Round(bounds.X, 3),
                ["y"] = Math.Round(bounds.Y, 3),
                ["width"] = Math.Round(bounds.Width, 3),
                ["height"] = Math.Round(bounds.Height, 3),
                ["storyId"] = (item as TextFrame)?.StoryId,
                ["linkId"] = (item as GraphicFrame)?.LinkId,
            };
            items.Add((JsonNode)WithoutNulls(entry));
        }

        var stories = new JsonArray();
        if (text is not null || (tag is not null && name is null && label is null && kind is null))
        {
            foreach (var story in document.Stories)
            {
                var pages = storyPages.GetValueOrDefault(story.Id);
                if (pageFilter is not null && pages?.Contains(pageFilter.Value) != true)
                {
                    continue;
                }
                var storyTag = tags.GetValueOrDefault(story.Id);
                if (tag is not null && !Has(storyTag, tag))
                {
                    continue;
                }
                var matches = new JsonArray();
                if (text is not null)
                {
                    foreach (var run in story.ContentRuns.Where(run => run.TrackedChange != "deleted" && run.Text.Contains(text, StringComparison.OrdinalIgnoreCase)))
                    {
                        matches.Add((JsonNode)new JsonObject { ["runIndex"] = run.Index, ["text"] = Snippet(run.Text, text) });
                    }
                    if (matches.Count == 0)
                    {
                        // The text may span runs (formatting changes inside it).
                        var full = story.Text;
                        if (!full.Contains(text, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        matches.Add((JsonNode)new JsonObject { ["text"] = Snippet(full, text) });
                    }
                }
                if (stories.Count >= max)
                {
                    truncated = true;
                    break;
                }
                stories.Add((JsonNode)WithoutNulls(new JsonObject
                {
                    ["id"] = story.Id,
                    ["tag"] = storyTag,
                    ["frames"] = JsonText.Array(story.Frames.Select(frame => frame.Id)),
                    ["pages"] = pages is null ? null : JsonText.Array(pages),
                    ["matches"] = text is null ? null : matches,
                }));
            }
        }
        return JsonText.Write(new JsonObject { ["items"] = items, ["stories"] = stories, ["truncated"] = truncated });
    }

    private static string Snippet(string text, string wanted)
    {
        var at = text.IndexOf(wanted, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, at - SnippetRadius);
        var end = Math.Min(text.Length, at + wanted.Length + SnippetRadius);
        return (start > 0 ? "…" : string.Empty) + text[start..end] + (end < text.Length ? "…" : string.Empty);
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
