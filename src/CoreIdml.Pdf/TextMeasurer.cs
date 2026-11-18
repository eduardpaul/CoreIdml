using CoreIdml.Dom;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf;

/// <summary>Size of a story's text once composed: widest line, height from the frame's top inset to the last line's descent.</summary>
public readonly record struct TextMeasure(double Width, double Height, int Lines);

/// <summary>How a story's text sits in one of its frames: lines placed there and the frame's text area height.</summary>
public sealed record FrameFit(string FrameId, string? PageName, int Lines, double TextAreaHeight);

/// <summary>
/// Whether a story fits its frames, measured with the PDF export's composer: the lines that did not fit
/// (<see cref="OverflowLines"/>) and how many points taller the last frame's last column would have to be
/// (<see cref="OverflowPoints"/>, 0 when it fits). <see cref="OversetCharacters"/> counts the characters of the lines
/// that do not fit (table contents excluded).
/// </summary>
public sealed record StoryFit(string StoryId, bool Overset, int OversetCharacters, int Lines, int OverflowLines, double OverflowPoints, IReadOnlyList<FrameFit> Frames);

/// <summary>
/// Measures text with the same fonts and composer the PDF export uses, so a program that builds a document can size
/// frames to their text (no overset, no gaps) before saving or exporting. Use the same <see cref="PdfFontOptions"/>
/// as the export, or the measurements and the PDF will disagree.
/// </summary>
public sealed class TextMeasurer : IDisposable
{
    private readonly IdmlRenderer renderer;

    public TextMeasurer(IdmlDocument document, PdfFontOptions? fonts = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        renderer = new IdmlRenderer(document, new PdfExportOptions { Fonts = fonts ?? new PdfFontOptions() });
    }

    /// <summary>
    /// Composes <paramref name="frame"/>'s story at the frame's width (insets and columns included) with no height
    /// limit. <see cref="TextMeasure.Height"/> is the frame height that shows every line, insets included.
    /// </summary>
    public TextMeasure Measure(TextFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return renderer.MeasureFrame(frame);
    }

    /// <summary>
    /// Composes <paramref name="story"/> in its frames (document pages only) as the PDF export does, then again with the
    /// last frame's last column unlimited, to tell how many lines and points do not fit.
    /// </summary>
    public StoryFit CheckFit(Story story)
    {
        ArgumentNullException.ThrowIfNull(story);
        const double Unlimited = 100_000;
        var frames = story.Frames;
        var areas = frames.Select(frame => renderer.FrameArea(frame, frame.Page?.Name ?? "1")).ToList();
        var layout = renderer.Compose(story, areas);
        var frameFits = frames.Select((frame, index) => new FrameFit(
            frame.Id,
            frame.Page?.Name,
            layout.Lines.Count(line => line.FrameIndex == index),
            areas[index].Columns.Count == 0 ? 0 : Math.Round(areas[index].Columns[0].Height, 3))).ToList();
        if (!layout.Overset || areas.Count == 0 || areas[^1].Columns.Count == 0)
        {
            return new StoryFit(story.Id, layout.Overset, layout.OversetCharacters, layout.Lines.Count, 0, 0, frameFits);
        }
        // Let the last column grow: everything that did not fit is laid out below its bottom edge.
        var last = areas[^1];
        var column = last.Columns[^1];
        var extended = areas.Take(areas.Count - 1)
            .Append(last with { Columns = [.. last.Columns.Take(last.Columns.Count - 1), column with { Height = Unlimited }], VerticalJustification = "TopAlign" })
            .ToList();
        var full = renderer.Compose(story, extended);
        var lastColumn = last.Columns.Count - 1;
        var bottom = full.Lines.Where(line => line.FrameIndex == areas.Count - 1 && line.Column == lastColumn).Select(line => line.Baseline).DefaultIfEmpty(column.Y).Max()
            + full.LastDescent;
        var overflowPoints = Math.Max(0, bottom - (column.Y + column.Height));
        // The composer only counts whole paragraphs that did not start; count the characters of the lines that did not fit.
        var oversetCharacters = Math.Max(layout.OversetCharacters, full.Lines.Skip(layout.Lines.Count).Sum(line => line.Text.Length));
        return new StoryFit(
            story.Id, true, oversetCharacters, layout.Lines.Count,
            Math.Max(0, full.Lines.Count - layout.Lines.Count), Math.Round(overflowPoints, 3), frameFits);
    }

    /// <summary>Fonts that were not found while measuring, and what replaced them (line breaks depend on them).</summary>
    public IReadOnlyList<Fonts.FontSubstitution> FontSubstitutions => renderer.Fonts.Substitutions;

    /// <summary><see cref="CheckFit(Story)"/> for every story shown on a document page.</summary>
    public IReadOnlyList<StoryFit> CheckFit(IdmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var shown = document.Spreads.SelectMany(spread => spread.AllPageItems).OfType<TextFrame>().Select(frame => frame.StoryId).ToHashSet(StringComparer.Ordinal);
        return document.Stories.Where(story => shown.Contains(story.Id)).Select(CheckFit).ToList();
    }

    public void Dispose() => renderer.Dispose();
}
