using CoreIdml.Dom;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// Paragraph rules, drawn from the laid-out lines once the story is composed (after vertical justification, so they
/// move with their lines). They take no space: the lines are where they would be without them (08-para-rules p1 and
/// p5: the first baselines and the leading between the paragraphs are the same with and without them), except for a
/// rule above kept in its frame (see <see cref="ParagraphRule.KeptRoom"/>). InDesign paints them all before the text
/// of the frame (08-para-rules p6: the seven rules of the column, then its text), so they go at the start of the
/// frame's ops, in story order. Paragraphs holding a table get none (not measured).
/// </summary>
internal sealed partial class TextComposer
{
    private static void DrawParagraphDecorations(StoryLayout layout, IReadOnlyList<FrameArea> frames)
    {
        var lines = layout.Lines;
        var paragraphs = layout.Paragraphs;
        if (lines.Count == 0 || !paragraphs.Any(paragraph => paragraph.Format is { RuleAbove: not null } or { RuleBelow: not null }))
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
            if (!paragraphs[lines[at].Paragraph].Items.Any(item => item is TableItem))
            {
                DrawRules(at, end);
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
}
