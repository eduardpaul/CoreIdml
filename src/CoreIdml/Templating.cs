using CoreIdml.Dom;

namespace CoreIdml;

/// <summary>
/// Data for <see cref="IdmlDocument.Fill"/>. Keys are matched, in order, against: frame names (Layers panel),
/// script labels, XML tags, and finally <c>{{key}}</c> placeholders typed in the text.
/// </summary>
public sealed class TemplateData
{
    public Dictionary<string, string> Text { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Image keys to local image file paths (JPEG or PNG).</summary>
    public Dictionary<string, string> Images { get; init; } = new(StringComparer.Ordinal);

    public ImageFit ImageFit { get; init; } = ImageFit.Fill;

    /// <summary>Placeholder pattern; <c>{0}</c> is the key.</summary>
    public string PlaceholderFormat { get; init; } = "{{{{{0}}}}}";
}

/// <summary>What <see cref="IdmlDocument.Fill"/> did.</summary>
public sealed class FillResult
{
    /// <summary>Number of places filled per key.</summary>
    public Dictionary<string, int> Filled { get; } = new(StringComparer.Ordinal);

    /// <summary>Keys that matched nothing in the document.</summary>
    public List<string> Missing { get; } = [];
}

public sealed partial class IdmlDocument
{
    /// <summary>Fills a template made in InDesign with data. See <see cref="TemplateData"/> for how keys match.</summary>
    public FillResult Fill(TemplateData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var result = new FillResult();

        foreach (var (key, value) in data.Text)
        {
            var count = 0;
            var filledStories = new HashSet<string>(StringComparer.Ordinal);

            foreach (var frame in FindPageItems(key).OfType<TextFrame>())
            {
                if (filledStories.Add(frame.StoryId))
                {
                    frame.Story.SetText(value);
                    count++;
                }
            }
            foreach (var node in Tags.FindElements(key))
            {
                if (node.BoundStory is { } story)
                {
                    if (filledStories.Add(story.Id))
                    {
                        story.SetText(value);
                        count++;
                    }
                }
                else if (node.IsInStory && node.ContentId is null)
                {
                    node.SetText(value);
                    count++;
                }
            }
            count += ReplaceText(string.Format(System.Globalization.CultureInfo.InvariantCulture, data.PlaceholderFormat, key), value);
            Record(result, key, count);
        }

        foreach (var (key, path) in data.Images)
        {
            var frames = FindPageItems(key).OfType<GraphicFrame>()
                .Concat(Tags.FindElements(key).Select(node => node.BoundPageItem).OfType<GraphicFrame>())
                .DistinctBy(frame => frame.Id)
                .ToList();
            if (frames.Count > 0)
            {
                var image = ImageSource.FromFile(path);
                foreach (var frame in frames)
                {
                    frame.PlaceImage(image, data.ImageFit);
                }
            }
            Record(result, key, frames.Count);
        }
        return result;
    }

    private static void Record(FillResult result, string key, int count)
    {
        result.Filled[key] = count;
        if (count == 0)
        {
            result.Missing.Add(key);
        }
    }
}
