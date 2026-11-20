using CoreIdml.Dom;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// Paragraph rules, borders and shading, drawn from the laid-out lines once the story is composed (after vertical
/// justification, so they move with their lines). None of them takes space: the lines are where they would be without
/// them (08-para-rules p1 and p5, 08-para-borders-shading p1 to p4: the first baselines and the leading between the
/// paragraphs are the same with and without them), except a rule above kept in its frame (see
/// <see cref="ParagraphRule.KeptRoom"/>). InDesign paints them all before the text of the frame
/// (08-para-rules p6: the seven rules of the column, then its text; 08-para-borders-shading p4: both boxes, then the
/// text), so they go at the start of the frame's ops, in story order: each box's shading, its border, then the rules
/// of its paragraphs. Paragraphs holding a table get none (not measured).
/// </summary>
internal sealed partial class TextComposer
{
    private static void DrawParagraphDecorations(StoryLayout layout, IReadOnlyList<FrameArea> frames)
    {
        var lines = layout.Lines;
        var paragraphs = layout.Paragraphs;
        if (lines.Count == 0 || !paragraphs.Any(paragraph => paragraph.Format is { RuleAbove: not null } or { RuleBelow: not null } or { Border: not null } or { Shading: not null }))
        {
            return;
        }
        var decorations = new Dictionary<string, List<DrawOp>>(StringComparer.Ordinal);
        void Add(LineInfo line, DrawOp op)
        {
            var frameId = frames[Math.Min(line.FrameIndex, frames.Count - 1)].FrameId;
            if (!decorations.TryGetValue(frameId, out var ops))
            {
                decorations[frameId] = ops = [];
            }
            ops.Add(op);
        }

        var at = 0;
        while (at < lines.Count)
        {
            var end = ParagraphEnd(at);
            var format = paragraphs[lines[at].Paragraph].Format;
            if (paragraphs[lines[at].Paragraph].Items.Any(item => item is TableItem))
            {
                at = end;
                continue;
            }
            // Consecutive paragraphs with the same border (and shading) are one box (08-para-borders-shading p2 and p4);
            // shading alone is drawn paragraph by paragraph (p1: two one-line paragraphs, two boxes).
            while (format.Border is { Merge: true } border && end < lines.Count && lines[end].Paragraph == lines[end - 1].Paragraph + 1
                && paragraphs[lines[end].Paragraph] is { Format: { Border: { } next } nextFormat } following
                && !following.Items.Any(item => item is TableItem) && border.SameAs(next) && SameShading(format.Shading, nextFormat.Shading))
            {
                end = ParagraphEnd(end);
            }
            if (format.Shading is not null || format.Border is not null)
            {
                DrawBoxes(at, end, format.Shading, format.Border);
            }
            for (var line = at; line < end; line = ParagraphEnd(line))
            {
                DrawRules(line, ParagraphEnd(line));
            }
            at = end;
        }
        foreach (var (frameId, ops) in decorations)
        {
            layout.OpsByFrame[frameId].InsertRange(0, ops);
        }

        // The end of the lines of the paragraph whose line `from` is.
        int ParagraphEnd(int from)
        {
            var end = from + 1;
            while (end < lines.Count && lines[end].Paragraph == lines[from].Paragraph)
            {
                end++;
            }
            return end;
        }

        // The boxes of lines [from, to): one per column they are in (08-para-borders-shading p5).
        void DrawBoxes(int from, int to, ParagraphShading? shading, ParagraphBorder? border)
        {
            for (var start = from; start < to;)
            {
                var stop = start + 1;
                while (stop < to && SameColumn(lines[stop], lines[start]))
                {
                    stop++;
                }
                if (shading is not null)
                {
                    Add(lines[start], new ParagraphShadingOp(Box(start, stop, shading.Box), shading.Box.Corners, shading.Color, shading.ClipToFrame));
                }
                if (border is not null)
                {
                    // Where the paragraph breaks across columns, the edges at the break are drawn only with Display
                    // Border if Paragraph Splits (from memory: no sample breaks a bordered paragraph).
                    Add(lines[start], new ParagraphBorderOp(Box(start, stop, border.Box), border,
                        OpenTop: start > from && !border.DisplayIfSplits, OpenBottom: stop < to && !border.DisplayIfSplits));
                }
                start = stop;
            }
        }

        // The box of lines [from, to) of one column: from the first line's ascent to the last line's descent, across
        // the column (08-para-borders-shading p1: the shading of an indented paragraph still spans its column) or the
        // widest line's text, grown by the offsets (p1: 6 pt all round, 251.75 pt for a 243.75 pt column).
        Rect Box(int from, int to, BoxPlacement placement)
        {
            var (first, last) = (lines[from], lines[to - 1]);
            var top = first.Baseline - placement.TopOrigin switch
            {
                "BaselineTopOrigin" => 0,
                "LeadingTopOrigin" => first.Leading,
                _ => first.Ascent,
            };
            var bottom = last.Baseline + (placement.BottomOrigin == "BaselineBottomOrigin" ? 0 : last.Descent);
            var (left, right) = (first.ColumnLeft, first.ColumnLeft + first.ColumnWidth);
            if (placement.TextWidth)
            {
                (left, right) = (double.MaxValue, double.MinValue);
                for (var i = from; i < to; i++)
                {
                    (left, right) = (Math.Min(left, lines[i].TextLeft), Math.Max(right, lines[i].TextLeft + lines[i].Width));
                }
            }
            var offsets = placement.Offsets;
            return Rect.FromEdges(left - offsets.Left, top - offsets.Top, right + offsets.Right, bottom + offsets.Bottom);
        }

        // The rules of the paragraph whose lines are [from, to): above its first line, below its last.
        void DrawRules(int from, int to)
        {
            var paragraph = paragraphs[lines[from].Paragraph];
            if (paragraph.Format.RuleAbove is { } above)
            {
                DrawRule(lines[from], above, -1, (paragraph.Items.OfType<TextItem>().FirstOrDefault()?.Style ?? paragraph.MarkStyle).Fill);
            }
            if (paragraph.Format.RuleBelow is { } below)
            {
                DrawRule(lines[to - 1], below, 1, (paragraph.Items.OfType<TextItem>().LastOrDefault()?.Style ?? paragraph.MarkStyle).Fill);
            }
        }

        // A rule `direction` (-1 up, 1 down) from the line's baseline: its edge nearer the line is its offset away
        // (08-para-rules p2). "Text Color" is the colour of the paragraph's first (rule above) or last (rule below)
        // text, from memory: every rule of the sample has a swatch.
        void DrawRule(LineInfo line, ParagraphRule rule, int direction, ResolvedColor? fill)
        {
            var (x, length) = rule.TextWidth ? (line.TextLeft, line.Width) : (line.ColumnLeft, line.ColumnWidth);
            x += rule.LeftIndent;
            length -= rule.LeftIndent + rule.RightIndent;
            if (length > 0 && rule.Weight > 0 && rule.ColorFor(fill) is { } color)
            {
                var y = line.Baseline + (direction * (rule.Offset + (rule.Weight / 2)));
                Add(line, new TextRuleOp(new Point(x, y), length, rule.Weight, color, rule.Type, rule.Gap));
            }
        }
    }

    private static bool SameColumn(LineInfo a, LineInfo b) =>
        a.FrameIndex == b.FrameIndex && a.Column == b.Column && a.ColumnLeft == b.ColumnLeft && a.ColumnWidth == b.ColumnWidth;

    private static bool SameShading(ParagraphShading? a, ParagraphShading? b) =>
        a is null ? b is null : b is not null && a with { Color = b.Color } == b && ParagraphDecorations.SameColor(a.Color, b.Color);
}
