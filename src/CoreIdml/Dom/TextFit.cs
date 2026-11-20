using System.Xml.Linq;
using CoreIdml.Xml;

namespace CoreIdml.Dom;

/// <summary>Result of <see cref="TextFit.Estimate"/>.</summary>
/// <param name="NeededHeight">Estimated height the text needs, in points (summed over columns).</param>
/// <param name="AvailableHeight">Column height available in all frames of the story, in points.</param>
public readonly record struct TextFitEstimate(double NeededHeight, double AvailableHeight)
{
    /// <summary>Needed / available; above 1 the text probably overflows.</summary>
    public double Ratio => AvailableHeight <= 0 ? double.PositiveInfinity : NeededHeight / AvailableHeight;

    /// <summary>The text probably does not fit (with a 10% margin for the estimate's error).</summary>
    public bool LikelyOverset => Ratio > 1.1;
}

/// <summary>
/// Rough overset detection without InDesign's composer: each paragraph's lines are estimated from its character
/// count and point size (average glyph width ≈ half the point size) against the column width, then summed with the
/// leading (Auto = 120%). Good for flagging "this text is much too long for its frame" after filling a template;
/// not a substitute for checking in InDesign.
/// </summary>
public static class TextFit
{
    private const double AverageGlyphWidth = 0.5;
    private const double AutoLeading = 1.2;
    private const double DefaultPointSize = 12;

    public static TextFitEstimate Estimate(Story story)
    {
        ArgumentNullException.ThrowIfNull(story);
        return EstimateWith(story, story.Frames, new AttributeResolver(story.Document));
    }

    /// <summary>The estimate for a story whose frames are known, with a resolver shared by several estimates.</summary>
    internal static TextFitEstimate EstimateWith(Story story, IReadOnlyList<TextFrame> frames, AttributeResolver resolver)
    {
        if (frames.Count == 0)
        {
            return new TextFitEstimate(0, 0);
        }
        var first = frames[0];
        var inset = first.Inset;
        var columns = Math.Max(1, first.Columns);
        var columnWidth = Math.Max(1, (first.SpreadBounds.Width - inset.Left - inset.Right - (first.ColumnGutter * (columns - 1))) / columns);
        var available = frames.Sum(frame => Math.Max(0, frame.SpreadBounds.Height - frame.Inset.Top - frame.Inset.Bottom) * Math.Max(1, frame.Columns));

        var styles = new StyleResolver(resolver);
        double needed = 0, lineWidth = 0, leading = 0;
        void EndParagraph()
        {
            if (leading > 0)
            {
                needed += Math.Max(1, Math.Ceiling(lineWidth / columnWidth)) * leading;
            }
            lineWidth = 0;
            leading = 0;
        }
        foreach (var element in story.Element.Descendants())
        {
            switch (element.Name.LocalName)
            {
                case "Content" when !InNestedText(element):
                {
                    var (size, lead) = styles.Resolve(element);
                    lineWidth += element.Value.Length * size * AverageGlyphWidth;
                    leading = Math.Max(leading, lead);
                    break;
                }
                case "Br" when !InNestedText(element):
                    EndParagraph();
                    break;
                case "Row" when !TrackedChanges.IsDeleted(element):
                    needed += IdmlXml.ParseDouble(element.Attribute("SingleRowHeight")?.Value) ?? 18;
                    break;
            }
        }
        EndParagraph();
        return new TextFitEstimate(needed, available);
    }

    /// <summary>Text with its own estimate (tables, footnotes, notes) or not shown at all (deleted tracked text).</summary>
    private static bool InNestedText(XElement element) =>
        element.Ancestors().Any(ancestor => ancestor.Name.LocalName is "Table" or "Footnote" or "Note" || TrackedChanges.IsDeletion(ancestor));

    /// <summary>Point size and leading of a run: local formatting, then character style, paragraph, paragraph style (with BasedOn).</summary>
    private sealed class StyleResolver(AttributeResolver resolver)
    {
        public (double Size, double Leading) Resolve(XElement content)
        {
            var chain = resolver.TextChain(content);
            var size = chain.Select(element => IdmlXml.ParseDouble(element.Attribute("PointSize")?.Value)).FirstOrDefault(value => value is > 0) ?? DefaultPointSize;
            var leadingText = chain.Select(element => IdmlXml.GetProperty(element, "Leading")).FirstOrDefault(value => value is not null);
            var leading = IdmlXml.ParseDouble(leadingText) is { } points and > 0 ? points : size * AutoLeading;
            return (size, leading);
        }
    }
}
