namespace CoreIdml.Pdf.Layout;

/// <summary>
/// The Adobe Paragraph Composer (<c>Composer="HL Composer"</c>, InDesign's default): the breaks of a whole paragraph
/// chosen together, Knuth and Plass's total fit. Each line costs (10 + badness)² demerits, plus 115² for a hyphen,
/// 3000 when the line before also ends in one and 3500 for a hyphen on the line before the last (TeX's linepenalty,
/// hyphenpenalty, doublehyphendemerits and finalhyphendemerits, with InDesign's own values where the references show
/// them); the paragraph takes the breaks of least total. The badness of a line:
/// <list type="bullet">
/// <item>justified: 100 r³ (see <see cref="Justifier"/> for r), compression weighted <see cref="ShrinkWeight"/>, and
/// stretching past the maximum capped at <see cref="StretchCap"/>: a line far too loose is no worse than one merely too
/// loose (08-para-hyphenation-justification p1 to p3, 120 pt columns: InDesign sets "Mr. Bennet replied that" at 303%
/// rather than "... that he" at 134% followed by a line at 233%). What the cap does not see is how much of the line
/// is white: a line of words whose stretching adds more than <see cref="LooseLineShare"/> of its width costs
/// <see cref="LooseLineCost"/> times the excess more. A few word spaces may stretch far when the line is still mostly
/// type (that 303% line adds 14% of its width, as does "Extraordinarily accom-" at 700%), but InDesign spreads the
/// looseness over more lines rather than set one that is largely white (08-para-hyphenation-justification p2 sets
/// eight lines adding 6 to 17% each rather than "told me all" adding 60%; p1, p3, 08-para-drop-caps p1 to p5 alike).
/// A line holding one word keeps the capped badness (single-word justification letter spaces it across).
/// Compressing past the minimum costs
/// <see cref="OvershrinkCost"/> more, about what stretching a line to r = 2.5 (183% by default) costs: InDesign
/// compresses past the minimum rather than set a loose line or a line more (08-para-alignment-indents p1 justifies all
/// lines in four with one at 70%, not five with one at 161%, but with the last line left aligned stretches the first
/// line to 191% instead; 08-para-drop-caps p1 and p6 end "rightful property of some one or other of their daughters."
/// at 75% rather than leave "of" alone on a line, p1 sets "such a man may be on his first entering a neighbourhood," at
/// 70%; 08-para-hyphenation-justification p4 compresses one line to 26% with a 50% minimum). A line that
/// must stretch with nothing that may (one word without single-word justification) costs
/// <see cref="ImpossibleCost"/>.</item>
/// <item>ragged: 100 gap / (1.75 em + 0.5% of the line's width) for the room it leaves at the end, nothing for the last
/// line: linear, so the demerits grow with the square of the room. The hyphenation zone plays no part (p3: the same
/// breaks with zones of 0 and 60 pt); the rag is evened out instead (08-para-keeps-breaks p1: a word that fits goes to
/// the next line when that makes both lines closer to full). The room is mostly relative to the type: 01-doc-bleed-slug
/// hyphenates twice to fill lines that would leave 38 and 20 pt (10 pt type), while 06-frame-basics p2 leaves 16 pt
/// rather than hyphenate; a wide column tolerates a little more (01-doc-margins-guides p6, 540 pt: "Mr. Morris" ends
/// a line and the next leaves 28.7 pt rather than break "Mor-ris" to leave 9 and 16 pt, while a 66.8 pt column of
/// 06-frame-basics p1 ends a line in "pos-", leaving 20 pt, rather than leave 39.8 pt).
/// A linear badness (TeX cubes it) with a hyphen before the last line costing little more than any other is what the
/// references show:
/// InDesign leaves 20.9 to 24.1 pt rather than end the line before the last in a hyphen (01-doc-page-sizes p4 "This
/// was" rather than "invi-", 01-doc-margins-guides p2, 02-item-object-styles p1, 09-style-text-styles p2 and
/// 11-long-markers-variables p4 at 7 pt) but takes one to avoid 23.7 to 56 pt (06-frame-basics p1 and p4 "invi-",
/// 06-frame-baseline-grid p2, 10-table-structure p5, 11-long-conditional-text p4, 07-char-case-position p3), and
/// leaves "universally" alone in 07-char-basics p3 rather than end it with "ac-"; 11-long-tracked-changes p4 (520 pt)
/// ends the line before the last in "fami-" rather than leave 24.8 pt. Of the 427 paragraphs of two lines or more in
/// the doc-samples whose lines could first be found in the references, the cube composed 356 as InDesign does (358 at
/// most with any hyphen-before-the-last value) and the linear badness 364, none of them one the cube got right. The
/// room in 1.75 em + 0.5% of the width, a hyphen at 115 and the hyphen before the last line at 3500 compose three
/// more as InDesign does and none fewer; 0.4% with 118 and 3000, or 0.6% with 113 and 4000, set no page worse either.</item>
/// </list>
/// Not modelled: fitness classes (adjacent loose and tight lines), the hyphenation weight slider (HyphenWeight; the
/// references show the same breaks with 5 and 10), "hyphenate last word".
/// </summary>
internal sealed partial class TextComposer
{
    /// <summary>What every line adds to its badness before squaring (TeX's linepenalty): fewer lines are better.</summary>
    private const double LinePenalty = 10;

    /// <summary>A hyphen's penalty, squared into the demerits.</summary>
    private const double ParagraphHyphenPenalty = 115;

    /// <summary>Demerits for a hyphen on a line after one that ends in a hyphen.</summary>
    private const double DoubleHyphenDemerits = 3000;

    /// <summary>Demerits for a hyphen on the line before the last.</summary>
    private const double FinalHyphenDemerits = 3500;

    /// <summary>The badness at which a stretched line counts as just loose (r ≈ 3.1, 200% word spacing by default).</summary>
    private const double StretchCap = 3000;

    /// <summary>The share of a justified line's width that stretching may add before it costs more than the cap.</summary>
    private const double LooseLineShare = 0.13;

    /// <summary>
    /// What each share of the width added past <see cref="LooseLineShare"/> adds to a justified line's badness (333 per
    /// percent). Shares of 12 to 13.5% and 333 per percent set eleven doc-samples pages better and none worse; 10.5% or
    /// 1000 per percent set 08-para-hyphenation-justification p4 worse, 100 per percent 08-para-drop-caps p4.
    /// </summary>
    private const double LooseLineCost = 33333;

    /// <summary>
    /// What compressing a justified line past its minimum adds to its badness. Of the costs from 0 to 10000, 1000 to 1500
    /// set the most doc-samples pages as InDesign does and none worse than 10000 (600 sets 08-para-alignment-indents p1
    /// and p2 a line short, 2000 breaks 07-char-language-nobreak p1 worse).
    /// </summary>
    private const double OvershrinkCost = 1200;

    /// <summary>The badness of a line that must stretch and has nothing that may.</summary>
    private const double ImpossibleCost = 100000;

    /// <summary>
    /// The room a ragged line may leave at its end for a badness of 100, in ems of the paragraph's type, to which
    /// <see cref="RaggedRoomShare"/> of the line's width is added.
    /// </summary>
    private const double RaggedRoom = 1.75;

    /// <summary>The share of a ragged line's width added to <see cref="RaggedRoom"/>: wider columns tolerate a little more room.</summary>
    private const double RaggedRoomShare = 0.005;

    /// <summary>Demerits of a break inside a word that is wider than the column: only when nothing else fits.</summary>
    private const double OverfullDemerits = 1e12;

    /// <summary>The most breaks one line is measured for, from each start: a bound on the search for very wide columns.</summary>
    private const int MaxBreaksPerLine = 256;

    /// <summary>
    /// The paragraph composer: the line ends (exclusive), forced flags and line numbers (of this plan, from 0) for
    /// glyphs[start..] up to the paragraph's end or its next table, chosen together to minimise the paragraph's total
    /// cost. The first of these lines is line <paramref name="firstLine"/> of the paragraph; line n is indented
    /// <paramref name="lineIndent"/>(n) from the column's left edge, the same for every line from
    /// <paramref name="steadyFrom"/> on. Line n of the plan is set in a stretch <paramref name="slotWidth"/>(n) wide, the
    /// same for every line from <paramref name="steadySlots"/> on. Text wrap leaves stretches that may be too narrow for
    /// the next word (<paramref name="skippable"/>): the plan then leaves that stretch empty (line numbers skip it), as
    /// InDesign does (see <see cref="Flow.Place"/>).
    /// </summary>
    private static List<(int End, bool Forced, int Line)> ComposeParagraph(List<Shaped> glyphs, int start, int firstLine, Func<int, double> lineIndent,
        int steadyFrom, Func<int, double> slotWidth, int steadySlots, Func<int, bool> skippable, ParagraphFormatting format, double em)
    {
        var stop = start;
        while (stop < glyphs.Count && glyphs[stop].Kind != GlyphKind.Table)
        {
            stop++;
        }
        var justified = IsJustified(format.Justification);
        var limit = Math.Max(0, format.Hyphenation.LadderLimit);
        // A node is a break, the line after it (lines past steadyFrom and steadySlots are alike) and how many lines
        // before it end in a hyphen in a row (capped at 1 without a limit).
        var lines = Math.Max(Math.Max(1, steadyFrom - firstLine + 1), steadySlots + 1);
        var states = (limit > 0 ? limit : 1) + 1;
        var count = stop - start + 1;
        var cost = new double[count * lines * states];
        var back = new int[cost.Length];
        Array.Fill(cost, double.PositiveInfinity);
        cost[0] = 0;
        back[0] = -1;
        // The width of the word each position starts (computed when a stretch may be too narrow for it).
        double[]? words = null;
        for (var position = 0; position < count - 1; position++)
        {
            for (var line = 0; line < lines; line++)
            {
                for (var hyphens = 0; hyphens < states; hyphens++)
                {
                    var node = (((position * lines) + line) * states) + hyphens;
                    if (double.IsPositiveInfinity(cost[node]))
                    {
                        continue;
                    }
                    if (line + 1 < lines && skippable(line) && Word(position) > Width(line) + Epsilon)
                    {
                        var skip = node + states;
                        if (cost[node] < cost[skip])
                        {
                            cost[skip] = cost[node];
                            back[skip] = node;
                        }
                        continue;
                    }
                    Lines(start + position, node, line, hyphens);
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
        var breaks = new List<(int End, bool Forced, int Line)>();
        for (var node = best; node > 0; node = back[node])
        {
            var from = back[node];
            var position = node / (lines * states);
            if (from / (lines * states) == position)
            {
                // A stretch left empty.
                continue;
            }
            var end = start + position;
            breaks.Add((end, glyphs[end - 1].Kind == GlyphKind.Break, from / states % lines));
        }
        breaks.Reverse();
        return breaks;

        double Width(int number) => slotWidth(number) - lineIndent(firstLine + number) - format.RightIndent;

        double Word(int position)
        {
            if (words is null)
            {
                words = new double[count];
                Array.Fill(words, double.NaN);
            }
            if (double.IsNaN(words[position]))
            {
                words[position] = FirstWord(glyphs, start + position, format).Width;
            }
            return words[position];
        }

        // Every line that can start at `from` (node `node`, line `number` of the plan), relaxing the nodes it ends at.
        void Lines(int from, int node, int number, int hyphens)
        {
            var indent = lineIndent(firstLine + number);
            var width = slotWidth(number) - indent - format.RightIndent;
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
                        if (ratio > 0 && space.WordSpace > 0)
                        {
                            var added = (width - (space.Width * format.Spacing.DesiredGlyphScaling)) / width;
                            badness += LooseLineCost * Math.Max(0, added - LooseLineShare);
                        }
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
                    var gap = (width - natural) / ((RaggedRoom * em) + (RaggedRoomShare * width));
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
