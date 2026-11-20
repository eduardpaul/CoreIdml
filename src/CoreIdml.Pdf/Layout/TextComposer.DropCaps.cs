namespace CoreIdml.Pdf.Layout;

/// <summary>
/// Drop caps, as InDesign 20 draws them in 08-para-drop-caps (measured on its reference PDF):
/// <list type="bullet">
/// <item>The first <see cref="DropCapFormat.Characters"/> characters leave the first line and are drawn on their own,
/// scaled so that their cap height (the drop cap font's OS/2 cap height at the new size) reaches from the first line's
/// cap height down to line N's baseline: size = S + (N - 1) × leading / capHeight, where S is the characters' own size
/// (p1: 2 to 5 lines of 11 pt Liberation Serif on 14 pt leading give 32.38, 53.76, 75.14 and 96.52 pt; p3: the
/// character style's font counts, 51.70 pt for Liberation Sans Bold, 50.44 pt for Raleway Italic).</item>
/// <item>Their baseline is line N's: line 1's baseline plus N - 1 leadings (on a baseline grid, N - 1 grid lines),
/// also when the paragraph has fewer lines (p6: a 4-line drop cap in a 2-line paragraph). The next paragraph is not
/// pushed down (from memory; the sample has no paragraph after one).</item>
/// <item>They start where the first line starts (left and first-line indents, p5), moved left by the first glyph's
/// left side bearing with Align Left Edge (p4), and the first N lines start after their advances: lines 2 to N also
/// get the first-line indent (p5: 20 pt). Spaces right after them take no room (p2: with two characters, "is" starts
/// right after "It").</item>
/// </list>
/// Not done: Scale for Descenders (the sample has no paragraph with it), a drop cap whose line N falls in the next
/// column, and the first line of a centred paragraph, which InDesign starts at the drop cap instead of centring it
/// (p5, 3.5 pt).
/// </summary>
internal sealed partial class TextComposer
{
    /// <summary>
    /// A paragraph's drop cap: its scaled <see cref="Glyphs"/>, the first glyph's origin from the first line's start
    /// (<see cref="Origin"/>, negative with Align Left Edge), where the text after it starts (<see cref="Width"/>) and
    /// how far below the first line's baseline it sits (<see cref="Drop"/>).
    /// </summary>
    private sealed record DropCap(List<Shaped> Glyphs, int Lines, double Origin, double Width, double Drop);

    /// <summary>
    /// Takes the drop cap's characters out of <paramref name="glyphs"/> (after a list's marker, which stays in the
    /// line), with the spaces right after them, and scales them. Null when the paragraph has no drop cap or does not
    /// start with text.
    /// </summary>
    private static DropCap? TakeDropCap(List<Shaped> glyphs, ParagraphFormatting format, FrameArea frame)
    {
        if (format.DropCap is not { } dropCap)
        {
            return null;
        }
        var start = 0;
        while (start < glyphs.Count && glyphs[start].Marker)
        {
            start++;
        }
        var end = start;
        var characters = 0;
        while (end < glyphs.Count && characters < dropCap.Characters && glyphs[end].Kind is GlyphKind.Normal or GlyphKind.Space or GlyphKind.SoftHyphen)
        {
            characters += glyphs[end].Text.Length;
            end++;
        }
        var taken = glyphs.GetRange(start, end - start).Where(glyph => glyph.Kind != GlyphKind.SoftHyphen).ToList();
        if (taken.Count == 0)
        {
            return null;
        }
        while (end < glyphs.Count && glyphs[end].Kind is GlyphKind.Space or GlyphKind.SoftHyphen)
        {
            end++;
        }
        glyphs.RemoveRange(start, end - start);

        var first = taken[0];
        var pitch = first.Style.EffectiveLeading;
        if (format.AlignToBaselineGrid && !format.GridAlignFirstLineOnly && frame.BaselineGrid is { Increment: > 0 } grid)
        {
            pitch = Math.Ceiling((pitch / grid.Increment) - 1e-9) * grid.Increment;
        }
        var drop = (dropCap.Lines - 1) * pitch;
        var size = first.Size + (drop / Math.Max(0.01, first.Font.CapHeight));
        for (var i = 0; i < taken.Count; i++)
        {
            var glyph = taken[i];
            var scale = glyph.Style.HorizontalScale;
            glyph.Natural = glyph.Font.Advance(glyph.Glyph) * size * scale;
            glyph.Advance = glyph.Natural + (glyph.Style.Tracking / 1000 * size);
            if (i + 1 < taken.Count && taken[i + 1] is var next && ReferenceEquals(glyph.Font, next.Font) && glyph.Style.Kerning && next.Style.Kerning)
            {
                glyph.Advance += glyph.Font.Kerning(glyph.Glyph, next.Glyph) * size * scale;
            }
            glyph.Size = size;
        }
        var origin = dropCap.AlignLeftEdge ? -first.Font.LeftSideBearing(first.Glyph) * size * first.Style.HorizontalScale : 0;
        return new DropCap(taken, dropCap.Lines, origin, origin + taken.Sum(glyph => glyph.Advance), drop);
    }

    /// <summary>How much further right than usual line <paramref name="line"/> (0 = first) of the paragraph starts.</summary>
    private static double DropCapIndent(DropCap? dropCap, int line, ParagraphFormatting format) =>
        dropCap is null || line >= dropCap.Lines ? 0 : dropCap.Width + (line > 0 ? format.FirstLineIndent : 0);

    /// <summary>Draws the drop cap of the paragraph whose first line was just placed, and adds its text to that line's.</summary>
    private static void EmitDropCap(DropCap dropCap, Flow flow, ParagraphFormatting format)
    {
        var x = flow.Column.X + format.LeftIndent + format.FirstLineIndent + dropCap.Origin;
        var baseline = flow.Baseline + dropCap.Drop;
        var from = 0;
        while (from < dropCap.Glyphs.Count)
        {
            var first = dropCap.Glyphs[from];
            var to = from + 1;
            while (to < dropCap.Glyphs.Count && ReferenceEquals(dropCap.Glyphs[to].Font, first.Font) && ReferenceEquals(dropCap.Glyphs[to].Style, first.Style))
            {
                to++;
            }
            var run = dropCap.Glyphs.GetRange(from, to - from).Select(glyph => new PlacedGlyph(glyph.Glyph, glyph.Text, glyph.Advance, glyph.Natural)).ToList();
            flow.Add(GlyphRunOp.Of(first.Style, first.Font, first.Size, first.Style.HorizontalScale, x, baseline - first.Rise, run));
            x += run.Sum(glyph => glyph.Advance);
            from = to;
        }
        var lines = flow.Layout.Lines;
        lines[^1] = lines[^1] with { Text = lines[^1].Text.Insert(lines[^1].MarkerLength, string.Concat(dropCap.Glyphs.Select(glyph => glyph.Text))) };
    }
}
