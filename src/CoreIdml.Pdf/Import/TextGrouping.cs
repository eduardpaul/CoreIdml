using System.Text;
using CoreIdml.Dom;

namespace CoreIdml.Pdf.Import;

/// <summary>One character read from a PDF, in page coordinates (points, origin top-left, y down).</summary>
internal sealed record TextGlyph(string Text, double X, double Baseline, double Width, double Size, string Family, string Style, ResolvedColor? Color);

/// <summary>A thin horizontal or vertical rule (table border, underline bar) in page coordinates; text never flows across it.</summary>
internal readonly record struct Rule(double Left, double Top, double Right, double Bottom)
{
    public bool IsVertical => Right - Left <= 3 && Bottom - Top > Right - Left;

    public bool IsHorizontal => Bottom - Top <= 3 && Right - Left > Bottom - Top;
}

/// <summary>Glyphs sharing a baseline, left to right, with word spaces restored.</summary>
internal sealed class TextLine
{
    public TextLine(IReadOnlyList<TextGlyph> glyphs)
    {
        Glyphs = glyphs;
        Baseline = glyphs.Average(g => g.Baseline);
        Left = glyphs.Min(g => g.X);
        Right = glyphs.Max(g => g.X + g.Width);
        Size = TextGrouping.Dominant(glyphs, g => g.Size);
    }

    public IReadOnlyList<TextGlyph> Glyphs { get; }

    public double Baseline { get; }

    public double Left { get; }

    public double Right { get; }

    public double Width => Right - Left;

    public double Size { get; }

    public string Text => TextGrouping.JoinGlyphs(Glyphs);
}

/// <summary>Consecutive lines forming one text frame; paragraphs are runs of lines.</summary>
internal sealed class TextBlock
{
    public List<TextLine> Lines { get; } = [];

    /// <summary>Indices into <see cref="Lines"/> where a new paragraph starts (always includes 0).</summary>
    public List<int> ParagraphStarts { get; } = [0];

    public double Left => Lines.Min(l => l.Left);

    public double Right => Lines.Max(l => l.Right);

    public double Top => Lines[0].Baseline - Lines[0].Size;

    public double Bottom => Lines[^1].Baseline + (Lines[^1].Size * 0.3);

    /// <summary>Typical baseline-to-baseline distance (the size × 1.2 for a single line).</summary>
    public double Leading
    {
        get
        {
            if (Lines.Count < 2)
            {
                return Lines[0].Size * 1.2;
            }
            var gaps = Lines.Zip(Lines.Skip(1), (a, b) => b.Baseline - a.Baseline).Where(g => g > 0).Order().ToList();
            return gaps.Count == 0 ? Lines[0].Size * 1.2 : gaps[gaps.Count / 2];
        }
    }

    /// <summary>Alignment guessed from the line edges: LeftAlign, CenterAlign, RightAlign or LeftJustified.</summary>
    public string Alignment
    {
        get
        {
            if (Lines.Count < 2)
            {
                return "LeftAlign";
            }
            var tolerance = Math.Max(1, Lines[0].Size * 0.25);
            var left = Left;
            var right = Right;
            var center = (left + right) / 2;
            var leftAligned = Lines.All(l => Math.Abs(l.Left - left) <= tolerance);
            // Lines that end a paragraph are short in justified text; judge the others.
            var body = Lines.Where((_, i) => i < Lines.Count - 1 && !ParagraphStarts.Contains(i + 1)).ToList();
            var rightAligned = body.Count > 0 && body.All(l => Math.Abs(l.Right - right) <= tolerance);
            if (leftAligned && rightAligned && (Lines.Count >= 3 || body.Count >= 2))
            {
                return "LeftJustified";
            }
            // Two lines: justified when the first line's word spaces are clearly wider than the last line's.
            if (leftAligned && Lines.Count == 2 && WordGap(Lines[0]) is { } first && WordGap(Lines[1]) is { } last && first > last * 1.15)
            {
                return "LeftJustified";
            }
            if (leftAligned)
            {
                return "LeftAlign";
            }
            if (Lines.All(l => Math.Abs(l.Right - right) <= tolerance))
            {
                return "RightAlign";
            }
            if (Lines.All(l => Math.Abs(((l.Left + l.Right) / 2) - center) <= tolerance))
            {
                return "CenterAlign";
            }
            return "LeftAlign";
        }
    }

    /// <summary>Average distance between words (ink to ink), null without word gaps.</summary>
    private static double? WordGap(TextLine line)
    {
        var ink = line.Glyphs.Where(g => !string.IsNullOrWhiteSpace(g.Text)).ToList();
        var gaps = ink.Zip(ink.Skip(1), (a, b) => b.X - (a.X + a.Width)).Where(gap => gap > line.Size * 0.15).ToList();
        return gaps.Count == 0 ? null : gaps.Average();
    }
}

/// <summary>
/// Groups PDF glyphs into lines (shared baseline, split at wide gaps such as column gutters), lines into blocks
/// (vertically close, horizontally overlapping, similar size) and blocks into paragraphs (wider gaps, first-line
/// indents, short last lines).
/// </summary>
internal static class TextGrouping
{
    public static List<TextLine> BuildLines(IEnumerable<TextGlyph> glyphs, IReadOnlyList<Rule>? rules = null)
    {
        var lines = new List<TextLine>();
        var sorted = glyphs.Where(g => !string.IsNullOrEmpty(g.Text)).OrderBy(g => g.Baseline).ThenBy(g => g.X).ToList();
        var bands = new List<List<TextGlyph>>();
        foreach (var glyph in sorted)
        {
            var band = bands.Count > 0 ? bands[^1] : null;
            if (band is not null && Math.Abs(band.Average(g => g.Baseline) - glyph.Baseline) <= Math.Max(0.5, Math.Min(band[0].Size, glyph.Size) * 0.25))
            {
                band.Add(glyph);
            }
            else
            {
                bands.Add([glyph]);
            }
        }
        var ordered = bands.Select(band => band.OrderBy(g => g.X).ToList()).ToList();
        for (var b = 0; b < ordered.Count; b++)
        {
            var current = new List<TextGlyph>();
            TextGlyph? previous = null;
            foreach (var glyph in ordered[b])
            {
                var blank = string.IsNullOrWhiteSpace(glyph.Text);
                if (previous is not null && !blank)
                {
                    var gap = glyph.X - (previous.X + previous.Width);
                    var size = Math.Max(previous.Size, glyph.Size);
                    // Several ems always separate columns or tab stops; a smaller gap does when the lines around it
                    // have the same vertical gap (a gutter), unlike word spaces in justified text.
                    var split = gap > size * 2.5
                        || (gap > size * 0.6 && rules is not null && rules.Any(r => r.IsVertical && r.Left > previous.X + previous.Width - 0.5 && r.Left < glyph.X + 0.5 && r.Top <= glyph.Baseline && r.Bottom >= glyph.Baseline - (size * 0.7)))
                        || (gap > size * 0.6 && IsGutter(ordered, b, previous.X + previous.Width, glyph.X));
                    if (split)
                    {
                        lines.Add(new TextLine(Trim(current)));
                        current = [];
                    }
                }
                current.Add(glyph);
                if (!blank)
                {
                    previous = glyph;
                }
            }
            if (Trim(current).Count > 0)
            {
                lines.Add(new TextLine(Trim(current)));
            }
        }
        return lines.Where(line => line.Glyphs.Count > 0).ToList();
    }

    /// <summary>
    /// True when the gap [<paramref name="from"/>, <paramref name="to"/>] in band <paramref name="index"/> continues
    /// as empty space through at least two neighbouring lines that have text on both sides of it.
    /// </summary>
    private static bool IsGutter(List<List<TextGlyph>> bands, int index, double from, double to)
    {
        var middle = (from + to) / 2;
        var half = Math.Max(0.5, (to - from) * 0.25);
        var spanning = 0;
        for (var j = Math.Max(0, index - 4); j <= Math.Min(bands.Count - 1, index + 4); j++)
        {
            if (j == index)
            {
                continue;
            }
            var band = bands[j];
            var ink = band.Where(g => !string.IsNullOrWhiteSpace(g.Text)).ToList();
            if (ink.Count == 0 || ink[0].X >= middle || ink[^1].X + ink[^1].Width <= middle)
            {
                continue;
            }
            spanning++;
            if (ink.Any(g => g.X < middle + half && g.X + g.Width > middle - half))
            {
                return false;
            }
        }
        return spanning >= 2 || (spanning == 1 && to - from > 0.9 * bands[index].Max(g => g.Size));
    }

    private static List<TextGlyph> Trim(List<TextGlyph> glyphs)
    {
        var start = 0;
        var end = glyphs.Count;
        while (start < end && string.IsNullOrWhiteSpace(glyphs[start].Text))
        {
            start++;
        }
        while (end > start && string.IsNullOrWhiteSpace(glyphs[end - 1].Text))
        {
            end--;
        }
        return glyphs.GetRange(start, end - start);
    }

    public static List<TextBlock> BuildBlocks(IReadOnlyList<TextLine> lines, double gapFactor, IReadOnlyList<Rule>? rules = null)
    {
        var blocks = new List<TextBlock>();
        foreach (var line in lines.OrderBy(l => l.Baseline).ThenBy(l => l.Left))
        {
            TextBlock? target = null;
            foreach (var block in blocks)
            {
                var last = block.Lines[^1];
                var gap = line.Baseline - last.Baseline;
                var overlap = Math.Min(line.Right, block.Right) - Math.Max(line.Left, block.Left);
                var similarSize = Math.Abs(line.Size - last.Size) <= Math.Max(last.Size, line.Size) * 0.35;
                var ruled = rules is not null && rules.Any(r => r.IsHorizontal
                    && r.Top > last.Baseline - 0.5 && r.Bottom < line.Baseline - (line.Size * 0.6)
                    && r.Left < Math.Min(line.Right, last.Right) && r.Right > Math.Max(line.Left, last.Left));
                if (gap > 0 && gap <= Math.Max(last.Size, line.Size) * 1.2 * gapFactor && overlap > -1 && similarSize && !ruled)
                {
                    target = block;
                    break;
                }
            }
            if (target is null)
            {
                target = new TextBlock();
                blocks.Add(target);
            }
            target.Lines.Add(line);
        }
        foreach (var block in blocks)
        {
            FindParagraphs(block);
        }
        return blocks;
    }

    private static void FindParagraphs(TextBlock block)
    {
        var leading = block.Leading;
        var left = block.Left;
        var width = block.Right - left;
        for (var i = 1; i < block.Lines.Count; i++)
        {
            var previous = block.Lines[i - 1];
            var line = block.Lines[i];
            var gap = line.Baseline - previous.Baseline;
            var widerGap = gap > leading * 1.25;
            var indented = line.Left - left > line.Size * 0.6 && Math.Abs(previous.Left - left) <= line.Size * 0.3 && block.Lines.Count > 2;
            var trimmed = previous.Text.TrimEnd();
            var shortEnding = previous.Right < left + (width * 0.8) && trimmed.Length > 0 && ".:!?;".Contains(trimmed[^1], StringComparison.Ordinal);
            var styleChange = Math.Abs(line.Size - previous.Size) > 0.5;
            if (widerGap || indented || shortEnding || styleChange)
            {
                block.ParagraphStarts.Add(i);
            }
        }
    }

    /// <summary>Concatenates glyphs, restoring spaces where the gap is wider than a fraction of the size.</summary>
    public static string JoinGlyphs(IReadOnlyList<TextGlyph> glyphs)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < glyphs.Count; i++)
        {
            if (i > 0)
            {
                var previous = glyphs[i - 1];
                var gap = glyphs[i].X - (previous.X + previous.Width);
                if (gap > Math.Min(previous.Size, glyphs[i].Size) * 0.15 && !previous.Text.EndsWith(' ') && !glyphs[i].Text.StartsWith(' '))
                {
                    builder.Append(' ');
                }
            }
            builder.Append(glyphs[i].Text);
        }
        return builder.ToString();
    }

    /// <summary>The value covering the most characters.</summary>
    public static T Dominant<T>(IEnumerable<TextGlyph> glyphs, Func<TextGlyph, T> key)
        where T : notnull =>
        glyphs.GroupBy(key).OrderByDescending(group => group.Sum(g => g.Text.Length)).First().Key;
}
