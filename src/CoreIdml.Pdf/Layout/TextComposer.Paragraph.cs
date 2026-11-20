namespace CoreIdml.Pdf.Layout;

/// <summary>
/// The Adobe Paragraph Composer (<c>Composer="HL Composer"</c>, InDesign's default): the breaks of a whole paragraph
/// chosen together, Knuth and Plass's total fit. Each line costs (10 + badness)² demerits, plus 100² for a hyphen,
/// 3000 when the line before also ends in one and 5000 for a hyphen on the line before the last (TeX's linepenalty,
/// hyphenpenalty, doublehyphendemerits and finalhyphendemerits, with InDesign's own values where the references show
/// them); the paragraph takes the breaks of least total. The badness of a line:
/// <list type="bullet">
/// <item>justified: 100 r³ (see <see cref="Justifier"/> for r), compression weighted <see cref="ShrinkWeight"/>, and
/// stretching past the maximum capped at <see cref="StretchCap"/>: a line far too loose is no worse than one merely too
/// loose (08-para-hyphenation-justification p1 to p3, 120 pt columns: InDesign sets "Mr. Bennet replied that" at 303%
/// rather than "... that he" at 134% followed by a line at 233%). Compressing past the minimum costs
/// <see cref="OvershrinkCost"/> more, about what stretching a line to r = 2.5 (183% by default) costs: InDesign
/// compresses past the minimum rather than set a loose line or a line more (08-para-alignment-indents p1 justifies all
/// lines in four with one at 70%, not five with one at 161%, but with the last line left aligned stretches the first
/// line to 191% instead; 08-para-drop-caps p1 and p6 end "rightful property of some one or other of their daughters."
/// at 75% rather than leave "of" alone on a line, p1 sets "such a man may be on his first entering a neighbourhood," at
/// 70%; 08-para-hyphenation-justification p4 compresses one line to 26% with a 50% minimum). A line that
/// must stretch with nothing that may (one word without single-word justification) costs
/// <see cref="ImpossibleCost"/>.</item>
/// <item>ragged: 100 gap / 2 em for the room it leaves at the end, nothing for the last line: linear, so the demerits
/// grow with the square of the room. The hyphenation zone plays no part (p3: the same breaks with zones of 0 and
/// 60 pt); the rag is evened out instead (08-para-keeps-breaks p1: a word that fits goes to the next line when that
/// makes both lines closer to full). The 2 em is relative to the type: 01-doc-bleed-slug hyphenates twice to fill
/// lines that would leave 38 and 20 pt (10 pt type), while 06-frame-basics p2 leaves 16 pt rather than hyphenate.
/// A linear badness (TeX cubes it) with TeX's own 5000 for a hyphen before the last line is what the references show:
/// InDesign leaves 20.9 to 24.1 pt rather than end the line before the last in a hyphen (01-doc-page-sizes p4 "This
/// was" rather than "invi-", 01-doc-margins-guides p2, 02-item-object-styles p1, 09-style-text-styles p2 and
/// 11-long-markers-variables p4 at 7 pt) but takes one to avoid 23.7 to 56 pt (06-frame-basics p1 and p4 "invi-",
/// 06-frame-baseline-grid p2, 10-table-structure p5, 11-long-conditional-text p4, 07-char-case-position p3), and
/// leaves "universally" alone in 07-char-basics p3 rather than end it with "ac-". Of the 427 paragraphs of two lines or
/// more in the doc-samples whose lines can be found in the references, the cube composes 356 as InDesign does (358 at
/// most with any hyphen-before-the-last value), the linear badness 364, none of them one the cube got right.</item>
/// </list>
/// Not modelled: fitness classes (adjacent loose and tight lines), the hyphenation weight slider (HyphenWeight; the
/// references show the same breaks with 5 and 10), "hyphenate last word".
/// </summary>
internal sealed partial class TextComposer
{
    /// <summary>What every line adds to its badness before squaring (TeX's linepenalty): fewer lines are better.</summary>
    private const double LinePenalty = 10;

    /// <summary>A hyphen's penalty, squared into the demerits.</summary>
    private const double ParagraphHyphenPenalty = 100;

    /// <summary>Demerits for a hyphen on a line after one that ends in a hyphen.</summary>
    private const double DoubleHyphenDemerits = 3000;

    /// <summary>Demerits for a hyphen on the line before the last.</summary>
    private const double FinalHyphenDemerits = 5000;

    /// <summary>The badness at which a stretched line counts as just loose (r ≈ 3.1, 200% word spacing by default).</summary>
    private const double StretchCap = 3000;

    /// <summary>
    /// What compressing a justified line past its minimum adds to its badness. Of the costs from 0 to 10000, 1000 to 1500
    /// set the most doc-samples pages as InDesign does and none worse than 10000 (600 sets 08-para-alignment-indents p1
    /// and p2 a line short, 2000 breaks 07-char-language-nobreak p1 worse).
    /// </summary>
    private const double OvershrinkCost = 1200;

    /// <summary>The badness of a line that must stretch and has nothing that may.</summary>
    private const double ImpossibleCost = 100000;

    /// <summary>The room a ragged line leaves at its end that costs as much as a justified line at its limits, in ems.</summary>
    private const double RaggedRoom = 2;

    /// <summary>Demerits of a break inside a word that is wider than the column: only when nothing else fits.</summary>
    private const double OverfullDemerits = 1e12;

    /// <summary>The most breaks one line is measured for, from each start: a bound on the search for very wide columns.</summary>
    private const int MaxBreaksPerLine = 256;

    /// <summary>
    /// The paragraph composer: the line ends (exclusive) and forced flags for glyphs[start..] up to the paragraph's end
    /// or its next table, chosen together to minimise the paragraph's total cost. The first of these lines is line
    /// <paramref name="firstLine"/> of the paragraph; line n is indented <paramref name="lineIndent"/>(n) from the
    /// column's left edge, the same for every line from <paramref name="steadyFrom"/> on.
    /// </summary>
    private static List<(int End, bool Forced)> ComposeParagraph(List<Shaped> glyphs, int start, int firstLine, Func<int, double> lineIndent,
        int steadyFrom, double columnWidth, ParagraphFormatting format, double em)
    {
        var stop = start;
        while (stop < glyphs.Count && glyphs[stop].Kind != GlyphKind.Table)
        {
            stop++;
        }
        var justified = IsJustified(format.Justification);
        var limit = Math.Max(0, format.Hyphenation.LadderLimit);
        // A node is a break, the line after it (lines past steadyFrom are alike) and how many lines before it end in a
        // hyphen in a row (capped at 1 without a limit).
        var lines = Math.Max(1, steadyFrom - firstLine + 1);
        var states = (limit > 0 ? limit : 1) + 1;
        var count = stop - start + 1;
        var cost = new double[count * lines * states];
        var back = new int[cost.Length];
        Array.Fill(cost, double.PositiveInfinity);
        cost[0] = 0;
        back[0] = -1;
        for (var position = 0; position < count - 1; position++)
        {
            for (var line = 0; line < lines; line++)
            {
                for (var hyphens = 0; hyphens < states; hyphens++)
                {
                    var node = (((position * lines) + line) * states) + hyphens;
                    if (!double.IsPositiveInfinity(cost[node]))
                    {
                        Lines(start + position, node, line, hyphens);
                    }
                }
            }
        }
        var best = -1;
        for (var node = (count - 1) * lines * states; node < cost.Length; node++)
        {
            if (best < 0 || cost[node] < cost[best])
            {
                best = node;
            }
        }
        var breaks = new List<(int End, bool Forced)>();
        for (var node = best; node > 0; node = back[node])
        {
            var end = start + (node / (lines * states));
            breaks.Add((end, glyphs[end - 1].Kind == GlyphKind.Break));
        }
        breaks.Reverse();
        return breaks;

        // Every line that can start at `from` (node `node`, line `number` of the plan), relaxing the nodes it ends at.
        void Lines(int from, int node, int number, int hyphens)
        {
            var indent = lineIndent(firstLine + number);
            var width = columnWidth - indent - format.RightIndent;
            var nextLine = Math.Min(number + 1, lines - 1);
            var line = new LineMeasure();
            var found = 0;
            var i = from;
            for (; i < stop; i++)
            {
                var glyph = glyphs[i];
                if (glyph.Kind == GlyphKind.Tab)
                {
                    glyph.Advance = Math.Max(0.1, TabAdvance(glyphs, i, line.X, format, indent, width).Advance);
                }
                if (i > from && line.Overflows(glyph, width, format, justified, pastMinimum: true))
                {
                    break;
                }
                line.Add(glyph);
                if (glyph.Kind == GlyphKind.Break)
                {
                    Relax(i + 1, forced: true, hyphen: false, last: false);
                    return;
                }
                if (glyph.BreakAfter && Relax(i + 1, forced: false, hyphen: glyph.Kind == GlyphKind.SoftHyphen, last: false) && ++found >= MaxBreaksPerLine)
                {
                    return;
                }
            }
            if (i >= stop)
            {
                Relax(stop, forced: false, hyphen: false, last: true);
            }
            else if (found == 0)
            {
                // Nothing fits (a word wider than the column): break inside it, where it runs out of room.
                Link(Math.Max(i, from + 1), 0, OverfullDemerits);
            }

            bool Relax(int end, bool forced, bool hyphen, bool last)
            {
                var space = hyphen ? line.WithHyphen(HyphenWidth(glyphs[end - 1])) : line.Space;
                double badness;
                if (justified && !line.HasTab)
                {
                    var stretch = !last || forced || format.Justification == "FullyJustified";
                    var ratio = Justifier.Solve(space, width, format.Spacing, stretch, shrink: true).Ratio;
                    if (double.IsInfinity(ratio))
                    {
                        badness = ratio > 0 ? ImpossibleCost : double.PositiveInfinity;
                    }
                    else if (ratio >= 0)
                    {
                        badness = Math.Min(100 * ratio * ratio * ratio, StretchCap);
                    }
                    else
                    {
                        if (Justifier.TightestWidth(space, format.Spacing, pastMinimum: true) > width + Epsilon)
                        {
                            return false;
                        }
                        badness = (ShrinkWeight * 100 * -ratio * ratio * ratio) + (ratio < -1 ? OvershrinkCost : 0);
                    }
                }
                else
                {
                    var natural = space.Width * (line.HasTab ? 1 : format.Spacing.DesiredGlyphScaling);
                    if (natural > width + Epsilon)
                    {
                        return false;
                    }
                    var gap = (width - natural) / (RaggedRoom * em);
                    badness = last || forced ? 0 : 100 * gap;
                }
                if (double.IsPositiveInfinity(badness))
                {
                    return false;
                }
                var next = hyphen ? hyphens + 1 : 0;
                if (limit > 0 && next > limit)
                {
                    return false;
                }
                var demerits = ((LinePenalty + badness) * (LinePenalty + badness)) + (hyphen ? ParagraphHyphenPenalty * ParagraphHyphenPenalty : 0);
                if (last && hyphens > 0)
                {
                    demerits += FinalHyphenDemerits;
                }
                if (hyphen && hyphens > 0)
                {
                    demerits += DoubleHyphenDemerits;
                }
                Link(end, Math.Min(next, states - 1), demerits);
                return true;
            }

            void Link(int end, int state, double demerits)
            {
                var target = ((((end - start) * lines) + nextLine) * states) + state;
                var total = cost[node] + demerits;
                if (total < cost[target])
                {
                    cost[target] = total;
                    back[target] = node;
                }
            }
        }
    }
}
