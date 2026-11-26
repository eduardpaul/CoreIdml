using System.Globalization;
using System.Text;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Fonts;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// Lays out story text into threaded frames and columns, the way InDesign composes it: line breaking at spaces and
/// hyphens, for the whole paragraph at once (the Paragraph Composer, <see cref="ComposeParagraph"/>) or one line at a
/// time (the Single-line Composer, <see cref="FitLine"/>), leading (fixed or Auto = 120%), first-baseline offset,
/// space before/after (no space before at the top of a column), indents, tabs, alignment and justification (word
/// spacing, letter spacing and glyph scaling within the paragraph's limits, <see cref="Justifier"/>), vertical
/// justification, keep options and Start Paragraph (a forbidden column break moves to an earlier line, laying the text
/// out again from there), paragraphs that span or split columns and balanced columns (the text laid out again with a
/// lower column bottom), inline tables (rows flow across columns and frames, header rows repeat), anchored objects
/// (inline, above the line, custom-positioned: <see cref="PlaceCustom"/>) and footnotes at the foot of their column
/// (<see cref="PlaceFootnotes"/>).
/// Not done: optical kerning, text wrap.
/// Deterministic: same input, same output.
/// </summary>
internal sealed partial class TextComposer
{
    private const double Epsilon = 0.01;
    private const double DefaultTabInterval = 36;

    // U+FFFC OBJECT REPLACEMENT CHARACTER: what an inline object reads as in a line's text.
    private static readonly string ObjectReplacement = ((char)0xFFFC).ToString();

    private readonly StoryReader reader;
    private readonly FontLibrary fonts;
    private readonly IdmlDocument document;
    // ShapeText's hyphenation points, reused (ShapeText does not nest).
    private readonly List<int> hyphens = [];
    // The document's superscript and subscript settings (Positions), read once.
    private (double, double, double, double)? positions;

    public TextComposer(IdmlDocument document, StoryReader reader, FontLibrary fonts)
    {
        this.document = document;
        this.reader = reader;
        this.fonts = fonts;
    }

    /// <summary>
    /// Size and offset of superscript and subscript text, as fractions of the text's size: the document's
    /// Preferences &gt; Advanced Type (<c>TextPreference</c> <c>SuperscriptSize</c>, <c>SuperscriptPosition</c>,
    /// <c>SubscriptSize</c>, <c>SubscriptPosition</c>, in percent), InDesign's 58.3 % and 33.3 % by default.
    /// </summary>
    private (double SuperscriptSize, double SuperscriptPosition, double SubscriptSize, double SubscriptPosition) Positions =>
        positions ??= ReadPositions(document);

    private static (double, double, double, double) ReadPositions(IdmlDocument document)
    {
        var preference = document.Package.TryGetXml("Resources/Preferences.xml")?.Root?.Element("TextPreference");
        double Percent(string name, double fallback) =>
            Values.Number(preference?.Attribute(name)?.Value, fallback) is var value && value > 0 ? value / 100 : fallback / 100;
        return (Percent("SuperscriptSize", 58.3), Percent("SuperscriptPosition", 33.3), Percent("SubscriptSize", 58.3), Percent("SubscriptPosition", 33.3));
    }

    /// <summary>Composes the paragraphs of <paramref name="container"/> (a Story or a Cell) into <paramref name="frames"/>.</summary>
    public StoryLayout Compose(XElement container, IReadOnlyList<FrameArea> frames)
    {
        var layout = new StoryLayout();
        var paragraphs = reader.Read(container);
        if (frames.Count == 0)
        {
            layout.Overset = paragraphs.Any(p => p.Items.Count > 0);
            return layout;
        }
        var flow = new Flow(frames, layout) { Notes = NotesFor(container) };
        layout.Paragraphs = paragraphs;
        // Keep options: the flow state before each paragraph, the column of each of its lines (null for a paragraph
        // holding a table, which keeps leave alone), and the column breaks moved earlier to honour them.
        var states = new Flow.State[paragraphs.Count];
        var columns = new List<int>?[paragraphs.Count];
        // The frame each paragraph starts in (StoryLayout.ParagraphFrames), kept apart while lines can still move.
        var paragraphFrames = new int[paragraphs.Count];
        var forcedBreaks = new HashSet<(int Paragraph, int Line)>();
        // Every move is to an earlier break than any forced after it, so this ends; the budget bounds pathological input.
        var budget = (paragraphs.Count * 4) + 16;
        // The split paragraphs being balanced (by SplitColumns id), which their own layout must not balance again.
        var balancing = new HashSet<int>();
        var index = Run(0, paragraphs.Count);
        // Balance Columns: the story ends in a frame that balances its columns (06-frame-span-balance p5: 18 lines
        // go 6, 6 and 6 into three columns; p6: 10 lines below a headline go 4, 4 and 2).
        if (!flow.Exhausted && flow.Frame.BalanceColumns && flow.Frame.Columns.Count > 1 && flow.BandHasText)
        {
            var band = flow.BandId;
            Balance(BandStart(paragraphs.Count, band), paragraphs.Count, band, () => !flow.Exhausted && flow.BandId == band);
        }
        // The rest of a footnote split at the end of the text goes on in the next columns.
        flow.PlaceRemainingNotes();
        // Paragraphs after the one that ran out of room were not laid out.
        layout.ParagraphFrames.AddRange(paragraphFrames.Take(Math.Min(index + 1, paragraphs.Count)));
        if (flow.Exhausted)
        {
            layout.Overset = true;
            layout.OversetCharacters += paragraphs.Skip(index + 1).Sum(p => p.Items.OfType<TextItem>().Where(t => !t.Generated).Sum(t => t.Text.Length));
        }
        layout.LastDescent = flow.LastDescent;
        flow.Finish();
        DrawParagraphDecorations(layout, frames);
        return layout;

        // Lays out paragraphs `from` up to `to`, honouring keep options; returns where it stopped (`to`, or the
        // paragraph that ran out of room).
        int Run(int from, int to)
        {
            var next = from;
            for (; next < to; next++)
            {
                var paragraph = paragraphs[next];
                // A paragraph spanning the columns starts below the text above it in the frame, which is balanced
                // first (06-frame-span-balance p2: 13 lines above the headline go 5, 5 and 3 into three columns).
                if (paragraph.Format.ColumnSpan is { Split: false } && paragraph.Format.Keeps.Start == "Anywhere"
                    && !flow.Exhausted && flow.Frame.Columns.Count > 1 && flow.BandHasText)
                {
                    var band = flow.BandId;
                    Balance(BandStart(next, band), next, band, () => !flow.Exhausted && flow.BandId == band);
                }
                states[next] = flow.Save();
                var linesBefore = layout.Lines.Count;
                var lines = LayoutParagraph(paragraph, flow, isFirst: next == 0, next, forcedBreaks);
                paragraphFrames[next] = layout.Lines.Count > linesBefore ? layout.Lines[linesBefore].FrameIndex : -1;
                columns[next] = paragraph.Items.Any(item => item is TableItem) ? null : lines;
                if (budget > 0 && EarlierBreak(paragraphs, columns, next, forcedBreaks) is { } moved)
                {
                    budget--;
                    flow.Restore(states[moved.Paragraph]);
                    forcedBreaks.RemoveWhere(at => at.Paragraph > moved.Paragraph || (at.Paragraph == moved.Paragraph && at.Line > moved.Line));
                    forcedBreaks.Add(moved);
                    next = moved.Paragraph - 1;
                    continue;
                }
                // A split paragraph's lines are balanced across its sub-columns when it stays in one column
                // (06-frame-span-balance p4: 7 lines go 4 and 3).
                if (paragraph.Format.ColumnSpan is { Split: true } && !flow.Exhausted
                    && flow.LastSplit is { OneColumn: true } split && balancing.Add(split.Id))
                {
                    Balance(next, next + 1, split.Id, () => flow.LastSplit is { OneColumn: true } again && again.Id == split.Id);
                    balancing.Remove(split.Id);
                }
                if (flow.Exhausted)
                {
                    break;
                }
            }
            return next;
        }

        // The first paragraph with lines in band `band` once the paragraphs before `to` are laid out: the first to
        // start in it, or the one before when that one already put lines in it.
        int BandStart(int to, int band)
        {
            var current = flow.Save();
            Flow.State At(int p) => p == to ? current : states[p];
            var from = to;
            while (from > 0 && At(from - 1).Bands.Id == band)
            {
                from--;
            }
            return from > 0 && !double.IsNaN(At(from).BandBottom) ? from - 1 : from;
        }

        // Lays paragraphs `from` to `to` out again with the lines of band (or split paragraph) `id` kept above the
        // highest bottom at which `holds` (they all stay in it), found by bisection: the columns end at about the
        // same height, the first ones taking the extra line.
        void Balance(int from, int to, int id, Func<bool> holds)
        {
            const double Precision = 0.05;
            var (low, high) = flow.FrameExtent;
            var start = states[from];
            bool LayOut(double bottom)
            {
                flow.Restore(start);
                flow.SetLimit(id, bottom);
                Run(from, to);
                return holds();
            }
            var fitted = false;
            while (high - low > Precision)
            {
                var middle = (low + high) / 2;
                fitted = LayOut(middle);
                (low, high) = fitted ? (low, middle) : (middle, high);
            }
            if (!fitted && !LayOut(high))
            {
                // Keep options move the text on even at the full height: as it was laid out.
                flow.ClearLimit(id);
                flow.Restore(start);
                Run(from, to);
            }
        }
    }

    // ---- keep options ----------------------------------------------------------------------------------

    /// <summary>
    /// The first column break in paragraph <paramref name="paragraph"/> (or between it and the paragraph before) that
    /// its keep options forbid, moved to the latest allowed break before it in the same column: the lines after that
    /// go to the next column. Null when every break is allowed, or when no break in that column is (InDesign then
    /// breaks the keep rather than leave a column empty). <paramref name="columns"/> holds each laid-out line's column
    /// (<see cref="int.MaxValue"/> for overset lines), so pushing lines into overset counts as a break too.
    /// Measured in 08-para-keeps-breaks p1 to p3: a heading with Keep with Next moves to the next frame with its
    /// paragraph; Keep All Lines Together moves a 4-line paragraph whole; Keep First 3 moves a paragraph that would
    /// leave 2 lines behind; Keep Last 3 moves a line more to the next frame.
    /// </summary>
    private static (int Paragraph, int Line)? EarlierBreak(IReadOnlyList<ParagraphModel> paragraphs, List<int>?[] columns, int paragraph, HashSet<(int Paragraph, int Line)> forcedBreaks)
    {
        if (columns[paragraph] is not { } lines)
        {
            return null;
        }
        for (var line = 0; line < lines.Count; line++)
        {
            var previous = Previous(paragraph, line);
            if (previous is not { } column || lines[line] == column || forcedBreaks.Contains((paragraph, line))
                || (line == 0 && paragraphs[paragraph].Format.Keeps.Start != "Anywhere") || BreakAllowed(paragraphs, columns, paragraph, line))
            {
                continue;
            }
            // Walk back through the column's lines; a break before its first line would leave it empty.
            var (p, l) = (paragraph, line);
            while (true)
            {
                if (l > 0)
                {
                    l--;
                }
                else if (p > 0 && columns[p - 1] is { Count: > 0 } before)
                {
                    p--;
                    l = before.Count - 1;
                }
                else
                {
                    return null;
                }
                if (Previous(p, l) != column)
                {
                    return null;
                }
                if (BreakAllowed(paragraphs, columns, p, l))
                {
                    return (p, l);
                }
            }
        }
        return null;

        // The column of the line before line l of paragraph p; null at the story's start or after a table.
        int? Previous(int p, int l) =>
            l > 0 ? columns[p]![l - 1] : p > 0 && columns[p - 1] is { Count: > 0 } before ? before[^1] : null;
    }

    /// <summary>
    /// Whether the keep options allow a column break before line <paramref name="line"/> of paragraph
    /// <paramref name="paragraph"/>: not inside a paragraph that keeps its lines together (all of them, or fewer than
    /// its first or last lines on one side), nor before the first lines the previous paragraph keeps with it.
    /// </summary>
    private static bool BreakAllowed(IReadOnlyList<ParagraphModel> paragraphs, List<int>?[] columns, int paragraph, int line)
    {
        var keeps = paragraphs[paragraph].Format.Keeps;
        if (line > 0 && keeps.LinesTogether
            && (keeps.AllLines || line < keeps.FirstLines || columns[paragraph]!.Count - line < keeps.LastLines))
        {
            return false;
        }
        return paragraph == 0 || columns[paragraph - 1] is null || line >= paragraphs[paragraph - 1].Format.Keeps.WithNext;
    }

    // ---- shaping ---------------------------------------------------------------------------------------

    private enum GlyphKind
    {
        Normal,
        Space,
        Tab,
        Break,
        Table,
        SoftHyphen,
        Inline,
    }

    private sealed class Shaped
    {
        public GlyphKind Kind;
        public FontFace Font = null!;
        public ushort Glyph;
        public string Text = string.Empty;
        public double Advance;
        public double Natural;
        // The width of a word space in this glyph's font and size: the unit of letter spacing.
        public double Band;
        public double Size;
        public double Rise;
        public RunStyle Style = null!;
        public bool BreakAfter;
        // Part of a list's generated bullet or number.
        public bool Marker;
        // A tab's leader (from the stop it went to), drawn across its space.
        public string? Leader;
        public TabItem? Tab;
        public XElement? Table;
        public InlineObjectItem? Object;
        // The item it was shaped from, and the index in that item's text of its (first) character.
        public InlineItem? Item;
        public int Index;
        // A hyphenation point inside a ligature: the ligature that replaces the UnbrokenBefore glyphs before the point
        // and the UnbrokenAfter glyphs after it when the line does not break there.
        public Shaped? Unbroken;
        public int UnbrokenBefore;
        public int UnbrokenAfter;
    }

    private List<Shaped> Shape(ParagraphModel paragraph, Flow flow) => Shape(paragraph.Items, paragraph.Format, flow);

    private List<Shaped> Shape(IReadOnlyList<InlineItem> items, ParagraphFormatting format, Flow flow)
    {
        var frame = flow.Frame;
        var result = new List<Shaped>();
        var footnotes = 0;
        foreach (var item in items)
        {
            var first = result.Count;
            switch (item)
            {
                case TextItem text:
                    ShapeText(text.Text, text.Style, result, text.Generated ? null : format.Hyphenation);
                    break;
                case FootnoteItem footnote:
                    // Numbered for the page the paragraph starts on; PlaceFootnotes numbers it again where its line goes.
                    ShapeText(FootnoteReference(flow, footnotes++), footnote.Style, result);
                    break;
                case FootnoteNumberItem footnoteNumber:
                    ShapeText(frame.FootnoteNumber, footnoteNumber.Style, result);
                    break;
                case PageNumberItem number:
                    ShapeText(number.Marker switch
                    {
                        PageMarker.Next => frame.Markers.NextPage ?? frame.PageName,
                        PageMarker.Previous => frame.Markers.PreviousPage ?? frame.PageName,
                        PageMarker.Section => frame.Markers.SectionMarker,
                        _ => frame.PageName,
                    }, number.Style, result);
                    break;
                case TextVariableItem variable:
                    ShapeText(frame.Markers.Variable?.Invoke(variable.Instance) ?? variable.ResultText, variable.Style, result);
                    // InDesign sets a variable as one character: it never breaks across lines.
                    for (var i = first; i < result.Count - 1; i++)
                    {
                        result[i].BreakAfter = false;
                    }
                    break;
                case TabItem tab:
                    result.Add(new Shaped { Kind = GlyphKind.Tab, Style = tab.Style, Font = tab.Style.Font, Size = tab.Style.Size, Tab = tab });
                    break;
                case LineBreakItem lineBreak:
                    result.Add(new Shaped { Kind = GlyphKind.Break, Style = lineBreak.Style, Font = lineBreak.Style.Font, Size = lineBreak.Style.Size });
                    break;
                case TableItem table:
                    result.Add(new Shaped { Kind = GlyphKind.Table, Style = table.Style, Font = table.Style.Font, Size = table.Style.Size, Table = table.Table });
                    break;
                case InlineObjectItem inline:
                    // An above-line or custom object takes no room in the line: only its anchor marker is there.
                    var width = inline.Anchored is null ? inline.Bounds.Width : 0;
                    result.Add(new Shaped
                    {
                        Kind = GlyphKind.Inline, Style = inline.Style, Font = inline.Style.Font, Size = inline.Style.Size, Text = ObjectReplacement,
                        Advance = width, Natural = width, Rise = inline.Anchored is null ? inline.YOffset : 0, Object = inline,
                    });
                    break;
            }
            for (var i = first; i < result.Count; i++)
            {
                result[i].Item = item;
                result[i].Marker = item.Generated;
                if (result[i].Unbroken is { } unbroken)
                {
                    unbroken.Item = item;
                    unbroken.Marker = item.Generated;
                }
            }
        }
        // Pair kerning between neighbours in the same font and size. A hyphenation point between two letters does not
        // separate them (only an actual break there would).
        for (var i = 1; i < result.Count; i++)
        {
            var before = i - 1;
            while (before > 0 && result[before].Kind == GlyphKind.SoftHyphen)
            {
                before--;
            }
            var previous = result[before];
            var current = result[i];
            if (previous.Kind is GlyphKind.Normal or GlyphKind.Space && current.Kind is GlyphKind.Normal or GlyphKind.Space
                && ReferenceEquals(previous.Font, current.Font) && previous.Size == current.Size && previous.Style.Kerning && current.Style.Kerning)
            {
                previous.Advance += previous.Font.Kerning(previous.Glyph, current.Glyph) * previous.Size * previous.Style.HorizontalScale;
            }
            // The marker of an above-line or custom object kerns with the letter after it as a word space would
            // (12-pdf-accessibility p4: "Above-line" starts 0.58 pt left of the column, Liberation Serif's space-A pair
            // at 10.5 pt).
            else if (previous.Object?.Anchored is not null && current.Kind is GlyphKind.Normal && current.Style.Kerning
                && current.Font.TryMap(' ', out var space))
            {
                previous.Advance += current.Font.Kerning(space, current.Glyph) * current.Size * current.Style.HorizontalScale;
            }
        }
        // A ligature drawn whole across a hyphenation point kerns with the glyph after it (07-char-opentype p1: ffl
        // then u in "affluent"); the glyph before it keeps its kerning with the first component.
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i] is { Kind: GlyphKind.SoftHyphen, Unbroken: { } ligature } soft && i + soft.UnbrokenAfter + 1 < result.Count
                && result[i + soft.UnbrokenAfter + 1] is { Kind: GlyphKind.Normal or GlyphKind.Space } next
                && ReferenceEquals(ligature.Font, next.Font) && ligature.Size == next.Size && ligature.Style.Kerning && next.Style.Kerning)
            {
                ligature.Advance += ligature.Font.Kerning(ligature.Glyph, next.Glyph) * ligature.Size * ligature.Style.HorizontalScale;
            }
        }
        // Desired word and letter spacing (Paragraph > Justification) are the natural spacing of every line, whatever
        // its alignment.
        var spacing = format.Spacing;
        if (spacing.ChangesNaturalSpacing)
        {
            foreach (var glyph in result)
            {
                ApplyDesiredSpacing(glyph, spacing);
                if (glyph.Unbroken is { } unbroken)
                {
                    ApplyDesiredSpacing(unbroken, spacing);
                }
            }
        }
        return result;
    }

    private static void ApplyDesiredSpacing(Shaped glyph, JustificationSettings spacing)
    {
        if (Stretches(glyph))
        {
            glyph.Advance += glyph.Natural * (spacing.DesiredWordSpacing - 1);
        }
        if (glyph.Kind is GlyphKind.Normal or GlyphKind.Space)
        {
            glyph.Advance += glyph.Band * spacing.DesiredLetterSpacing;
        }
    }

    private void ShapeText(string text, RunStyle style, List<Shaped> output, HyphenationSettings? hyphenation = null)
    {
        // Discretionary break points from hyphenation, as soft hyphens after these text indices (ascending).
        hyphens.Clear();
        var language = Hyphenator.LanguageOf(style.Language);
        if (hyphenation is { Enabled: true } && language != HyphenationLanguage.None)
        {
            for (var start = 0; start < text.Length;)
            {
                if (!char.IsLetter(text[start]))
                {
                    start++;
                    continue;
                }
                // A word with a discretionary hyphen breaks only there (07-char-language-nobreak p3: InDesign breaks
                // "extra-ordinary" at its discretionary hyphen, not at "extraordi-").
                var end = start;
                var discretionary = false;
                while (end < text.Length && (char.IsLetter(text[end]) || text[end] == '\u00AD'))
                {
                    discretionary |= text[end] == '\u00AD';
                    end++;
                }
                if (!discretionary)
                {
                    Hyphenator.AddPoints(text.AsSpan(start, end - start), language, hyphenation, start, hyphens);
                }
                start = end;
            }
        }
        var nextHyphen = 0;
        var firstShaped = output.Count;
        // The space band (letter spacing's unit) per point of size.
        var band = (style.Font.TryMap(' ', out var bandGlyph) ? style.Font.Advance(bandGlyph) : 0.25) * style.HorizontalScale;
        for (var i = 0; i < text.Length; i++)
        {
            var index = i;
            int codePoint = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            var size = style.Size;
            var rise = style.BaselineShift;
            switch (style.Position)
            {
                case "Superscript" or "OTSuperscript":
                    size *= Positions.SuperscriptSize;
                    rise += style.Size * Positions.SuperscriptPosition;
                    break;
                case "Subscript" or "OTSubscript":
                    size *= Positions.SubscriptSize;
                    rise -= style.Size * Positions.SubscriptPosition;
                    break;
            }
            if (style.Capitalization == "AllCaps")
            {
                codePoint = ToUpper(codePoint);
            }
            else if (style.Capitalization is "SmallCaps" or "CapToSmallCap" && IsLower(codePoint))
            {
                codePoint = ToUpper(codePoint);
                size *= 0.7;
            }
            var value = CharacterText(codePoint);
            if (codePoint == 0x00AD)
            {
                output.Add(new Shaped { Kind = GlyphKind.SoftHyphen, Style = style, Font = style.Font, Size = size, Rise = rise, BreakAfter = true, Text = string.Empty, Index = index });
                continue;
            }
            var isSpace = codePoint is 0x20 or 0xA0 or (>= 0x2000 and <= 0x200A) or 0x3000 or 0x202F or 0x205F;
            var font = style.Font;
            if (!font.TryMap(codePoint, out var glyph))
            {
                if (isSpace && font.TryMap(' ', out var spaceGlyph))
                {
                    glyph = spaceGlyph;
                }
                else if (codePoint == 0x2011 && font.TryMap('-', out var hyphenGlyph))
                {
                    // A non-breaking hyphen the font lacks prints as its hyphen (07-char-language-nobreak p3: InDesign
                    // sets "twenty‑three" in Liberation Serif, which has no U+2011, with the 4.33 pt hyphen at 13 pt).
                    glyph = hyphenGlyph;
                    value = "-";
                }
                else if (!style.FontIsSubstitute)
                {
                    // The document's own font cannot show it: InDesign prints nothing and takes no space (it only
                    // highlights the character on screen). Reported in the export warnings.
                    fonts.NoteMissingGlyph(font, codePoint);
                    continue;
                }
                else if (fonts.FallbackFor(codePoint, font) is { } fallback && fallback.TryMap(codePoint, out var fallbackGlyph))
                {
                    font = fallback;
                    glyph = fallbackGlyph;
                }
            }
            var natural = font.Advance(glyph) * size * style.HorizontalScale;
            var advance = SpecialSpaceWidth(codePoint, size) ?? natural;
            advance += style.Tracking / 1000 * size;
            output.Add(new Shaped
            {
                Kind = isSpace ? GlyphKind.Space : GlyphKind.Normal,
                Font = font,
                Glyph = glyph,
                Text = value,
                Advance = advance,
                Natural = natural,
                Band = band * size,
                Size = size,
                Rise = rise,
                Style = style,
                Index = index,
                BreakAfter = codePoint is 0x20 or 0x200B or 0x2002 or 0x2003 or 0x2009 or 0x200A or 0x2008 or 0x2004 or 0x2005 or 0x2006 or 0x3000
                    or '-' or 0x2010 or 0x2013 or 0x2014,
            });
            while (nextHyphen < hyphens.Count && hyphens[nextHyphen] < index)
            {
                nextHyphen++;
            }
            if (nextHyphen < hyphens.Count && hyphens[nextHyphen] == index)
            {
                output.Add(new Shaped { Kind = GlyphKind.SoftHyphen, Style = style, Font = style.Font, Size = size, Rise = rise, BreakAfter = true, Text = string.Empty, Index = index });
            }
        }
        if (style.Ligatures)
        {
            ApplyLigatures(output, firstShaped, style);
        }
    }

    /// <summary>
    /// Replaces runs of glyphs by the font's standard ligatures (fi, fl, ffi, ...), as InDesign does by default. Only
    /// glyphs of one font, size and position combine; a space keeps them apart. A ligature across a hyphenation point
    /// (the ffi of "of-fice") is drawn whole unless the line breaks there, as in InDesign (07-char-opentype p1:
    /// "office" keeps its ffi ligature): the glyphs stay split at the point, and the point carries the ligature that
    /// <see cref="EmitLine"/> puts back when the line does not break at it.
    /// </summary>
    private static void ApplyLigatures(List<Shaped> output, int start, RunStyle style)
    {
        List<(int Start, int Hyphen, int End, ushort Glyph)>? crossings = null;
        Span<ushort> components = stackalloc ushort[MaxLigatureComponents];
        Span<int> indices = stackalloc int[MaxLigatureComponents];
        // The ligatures of the unbroken text, looking through hyphenation points: keep those that span one.
        for (var i = start; i < output.Count; i++)
        {
            var count = LigatureComponents(output, i, components, indices);
            if (count < 2 || !output[i].Font.TryLigature(components[..count], out var ligature, out var length))
            {
                continue;
            }
            var end = indices[length - 1];
            var points = 0;
            var hyphen = -1;
            for (var k = i + 1; k < end; k++)
            {
                if (output[k].Kind == GlyphKind.SoftHyphen)
                {
                    points++;
                    hyphen = k;
                }
            }
            if (points == 1)
            {
                (crossings ??= []).Add((i, hyphen, end, ligature));
            }
            i = end;
        }
        // Split text: ligatures that do not cross a hyphenation point. Right to left, so indices stay valid.
        var to = output.Count;
        for (var c = (crossings?.Count ?? 0) - 1; c >= 0; c--)
        {
            var (first, hyphen, last, glyph) = crossings![c];
            Ligate(output, last + 1, to, style);
            var parts = output.GetRange(first, last - first + 1);
            parts.RemoveAll(part => part.Kind == GlyphKind.SoftHyphen);
            var soft = output[hyphen];
            soft.Unbroken = NewLigature(parts, glyph, style);
            soft.UnbrokenAfter = Ligate(output, hyphen + 1, last + 1, style) - (hyphen + 1);
            soft.UnbrokenBefore = Ligate(output, first, hyphen, style) - first;
            to = first;
        }
        Ligate(output, start, to, style);
    }

    private const int MaxLigatureComponents = 4;

    /// <summary>
    /// The glyphs that could form a ligature starting at <paramref name="index"/>: normal glyphs of one font, size
    /// and position, read through hyphenation points. Their glyph ids go to <paramref name="components"/> and their
    /// indices in <paramref name="output"/> to <paramref name="indices"/>.
    /// </summary>
    private static int LigatureComponents(List<Shaped> output, int index, Span<ushort> components, Span<int> indices)
    {
        var first = output[index];
        if (first.Kind != GlyphKind.Normal)
        {
            return 0;
        }
        var count = 0;
        for (var i = index; count < MaxLigatureComponents && i < output.Count; i++)
        {
            var next = output[i];
            if (next.Kind == GlyphKind.SoftHyphen)
            {
                continue;
            }
            if (next.Kind != GlyphKind.Normal || !ReferenceEquals(next.Font, first.Font) || next.Size != first.Size || next.Rise != first.Rise)
            {
                break;
            }
            components[count] = next.Glyph;
            indices[count] = i;
            count++;
        }
        return count;
    }

    /// <summary>Forms the ligatures of output[from..to) that no hyphenation point splits; returns the new end.</summary>
    private static int Ligate(List<Shaped> output, int from, int to, RunStyle style)
    {
        Span<ushort> glyphs = stackalloc ushort[MaxLigatureComponents];
        for (var i = from; i < to; i++)
        {
            var first = output[i];
            if (first.Kind != GlyphKind.Normal)
            {
                continue;
            }
            var count = 0;
            while (count < MaxLigatureComponents && i + count < to && output[i + count] is { Kind: GlyphKind.Normal } next
                && ReferenceEquals(next.Font, first.Font) && next.Size == first.Size && next.Rise == first.Rise)
            {
                glyphs[count] = next.Glyph;
                count++;
            }
            if (count < 2 || !first.Font.TryLigature(glyphs[..count], out var ligature, out var length))
            {
                continue;
            }
            output[i] = NewLigature(output.GetRange(i, length), ligature, style);
            output.RemoveRange(i + 1, length - 1);
            to -= length - 1;
        }
        return to;
    }

    private static Shaped NewLigature(List<Shaped> parts, ushort ligature, RunStyle style)
    {
        var first = parts[0];
        var natural = first.Font.Advance(ligature) * first.Size * style.HorizontalScale;
        return new Shaped
        {
            Kind = GlyphKind.Normal,
            Font = first.Font,
            Glyph = ligature,
            Text = string.Concat(parts.Select(glyph => glyph.Text)),
            Advance = natural + (style.Tracking / 1000 * first.Size),
            Natural = natural,
            Band = first.Band,
            Size = first.Size,
            Rise = first.Rise,
            Style = first.Style,
            Index = first.Index,
            BreakAfter = parts[^1].BreakAfter,
        };
    }

    /// <summary>Strings of the most common characters, made once: every glyph carries the text it stands for.</summary>
    private static readonly string?[] CommonCharacters = new string?[0x800];

    private static string CharacterText(int codePoint)
    {
        if (codePoint >= CommonCharacters.Length)
        {
            return char.ConvertFromUtf32(codePoint);
        }
        // A race only makes the same string twice.
        return CommonCharacters[codePoint] ??= char.ConvertFromUtf32(codePoint);
    }

    private static double? SpecialSpaceWidth(int codePoint, double size) => codePoint switch
    {
        0x2002 => size / 2,   // en space
        0x2003 => size,       // em space
        0x2009 => size / 8,   // thin space (InDesign: 1/8 em)
        0x200A => size / 24,  // hair space
        0x2004 => size / 3,
        0x2005 => size / 4,
        0x2006 => size / 6,
        0x200B => 0,
        _ => null,
    };

    private static int ToUpper(int codePoint) =>
        codePoint <= 0xFFFF ? char.ToUpperInvariant((char)codePoint) : codePoint;

    private static bool IsLower(int codePoint) => codePoint <= 0xFFFF && char.IsLower((char)codePoint);

    // ---- paragraph layout ------------------------------------------------------------------------------

    /// <summary>
    /// Lays out paragraph number <paramref name="number"/>: starts it where its Start Paragraph option says, goes to the
    /// next column before the lines in <paramref name="forcedBreaks"/> (breaks moved earlier by keep options), and
    /// returns the column (<see cref="Flow.ColumnOrdinal"/>) of each of its lines, <see cref="int.MaxValue"/> for the
    /// lines that did not fit (counted at the last column's width).
    /// </summary>
    private List<int> LayoutParagraph(ParagraphModel paragraph, Flow flow, bool isFirst, int number, HashSet<(int Paragraph, int Line)> forcedBreaks)
    {
        var format = paragraph.Format;
        // Before shaping, so that a page number in the paragraph is the page it moves to.
        flow.StartParagraph(format.Keeps.Start);
        if (forcedBreaks.Contains((number, 0)) && !flow.AtColumnTop)
        {
            flow.NextColumn();
        }
        var glyphs = Shape(paragraph, flow);
        var dropCap = TakeDropCap(glyphs, format, flow.Frame);
        flow.PendingSpace += isFirst ? 0 : format.SpaceBefore;
        // Span Columns: the lines are as wide as the columns spanned, or as a sub-column of a split paragraph.
        var spans = format.ColumnSpan is { Split: false } span && flow.BeginSpan(span.Count, span.SpaceBefore);
        var splits = format.ColumnSpan is { Split: true } split && flow.BeginSplit(split.Count, split.InsideGutter, split.OutsideGutter);
        var columns = new List<int>();
        var index = 0;
        var firstLine = true;
        var emitted = false;
        // Nested line styles: the items come styled for the first line; a line that takes another style restyles the
        // rest of the paragraph before it is broken, so its breaks use its own widths (08-para-nested-grep-styles p3).
        // Lines are counted by `columns` (every line, a forced line break starting a new one, lines that do not fit
        // included); a paragraph laid out again after a keep restore starts over from its own items at line 0.
        var lineStyles = paragraph.LineStyles;
        var lineStyle = lineStyles?.LineStyle(0);
        // The paragraph composer's line ends, from plannedStart at plannedWidth (see ComposeParagraph).
        var paragraphComposer = format.ParagraphComposer;
        List<(int End, bool Forced, int Line)>? plan = null;
        // With text wrap: the stretches the plan was made for.
        List<Slot>? slots = null;
        var (planned, plannedStart, plannedWidth) = (0, -1, double.NaN);
        // Line n's indent from the column's left edge: the first-line indent goes to line 0, unless the paragraph starts
        // with a table; lines beside a drop cap start after it; every line from steadyLines on is indented alike.
        var indentedLine = 0;
        var steadyLines = Math.Max(1, dropCap?.Lines ?? 1);
        double LineIndent(int line) => format.LeftIndent + (line == indentedLine ? format.FirstLineIndent : 0) + DropCapIndent(dropCap, line, format);
        while (index < glyphs.Count || !emitted)
        {
            if (index < glyphs.Count && glyphs[index].Kind == GlyphKind.Table)
            {
                if (!flow.Exhausted)
                {
                    LayoutTable(glyphs[index].Table!, flow, format);
                }
                index++;
                emitted = true;
                firstLine = false;
                indentedLine = columns.Count == 0 ? -1 : indentedLine;
                continue;
            }
            if (columns.Count > 0 && forcedBreaks.Contains((number, columns.Count)) && !flow.AtColumnTop)
            {
                flow.NextColumn();
            }
            if (lineStyles is not null && index < glyphs.Count && glyphs[index].Item is { } source
                && lineStyles.LineStyle(columns.Count) is var wanted && wanted != lineStyle)
            {
                var rest = Shape(lineStyles.ItemsFrom(source, glyphs[index].Index, wanted), format, flow);
                // After a line that ends in a hyphen the rest of the word is not a word of its own: it keeps no hyphenation
                // points (and no split ligatures) of its own.
                if (index > 0 && glyphs[index - 1].Kind == GlyphKind.SoftHyphen)
                {
                    var wordEnd = 0;
                    while (wordEnd < rest.Count && (rest[wordEnd].Kind == GlyphKind.SoftHyphen
                        || (rest[wordEnd].Kind == GlyphKind.Normal && rest[wordEnd].Text.Length > 0 && char.IsLetter(rest[wordEnd].Text[0]))))
                    {
                        wordEnd++;
                    }
                    DropHyphenationPoints(rest, 0, wordEnd);
                }
                glyphs.RemoveRange(index, glyphs.Count - index);
                glyphs.AddRange(rest);
                lineStyle = wanted;
            }
            var column = flow.Column;
            var indent = LineIndent(columns.Count);
            var start = index;
            var snapToGrid = format.AlignToBaselineGrid && (firstLine || !format.GridAlignFirstLineOnly);
            firstLine = false;
            emitted = true;
            int end;
            bool forced;
            double available;
            LineMetrics metrics;
            if (flow.Area is not null)
            {
                if (!PlaceWrapped(out end, out forced, out available, out metrics))
                {
                    index = end;
                    columns.Add(int.MaxValue);
                    continue;
                }
            }
            else
            {
                (end, forced, available) = BreakLine(column.Width);
                metrics = Metrics(end);
                ReserveFootnotes(glyphs, start, end, flow);
                if (!flow.Place(metrics, snapToGrid))
                {
                    index = end;
                    columns.Add(int.MaxValue);
                    continue;
                }
                if (flow.Column.Width != column.Width)
                {
                    // The line went on to a column of another width: it is broken again at that width.
                    (end, forced, available) = BreakLine(flow.Column.Width);
                }
            }
            var line = glyphs.GetRange(start, end - start);
            PlaceFootnotes(line, flow);
            index = end;
            var isLast = index >= glyphs.Count || glyphs[index].Kind == GlyphKind.Table;
            columns.Add(flow.ColumnOrdinal);
            EmitLine(line, flow, format, indent + flow.Line.Inset, available, isLast, forced, metrics, number);
            if (dropCap is not null && columns.Count == 1)
            {
                EmitDropCap(dropCap, flow, format);
            }

            // The end of the line starting at `start` in a column `width` wide, and the line's width.
            (int End, bool Forced, double Available) BreakLine(double width)
            {
                var lineWidth = width - indent - format.RightIndent;
                if (!paragraphComposer || lineStyles is not null || start >= glyphs.Count)
                {
                    var (fitEnd, fitForced) = FitLine(glyphs, start, lineWidth, format, indent);
                    return (fitEnd, fitForced, lineWidth);
                }
                // The paragraph composer plans the rest of the paragraph at this column's width; a line that starts
                // in a column of another width (or after a table) plans again from there.
                if (plan is null || planned >= plan.Count || plannedStart != start || plannedWidth != width)
                {
                    plan = ComposeParagraph(glyphs, start, columns.Count, LineIndent, steadyLines, _ => width, 0, _ => false, format, paragraph.MarkStyle.Size);
                    planned = 0;
                    plannedWidth = width;
                }
                var (planEnd, planForced, _) = plan[planned++];
                plannedStart = planEnd;
                return (planEnd, planForced, lineWidth);
            }

            // Text wrap or a shaped frame: how wide the line may be depends on where it goes, which depends on its
            // height. The line goes where its first word fits (a stretch at least that wide); it is broken for the
            // stretch found at its first word's height, and again when its own height finds another.
            bool PlaceWrapped(out int wrappedEnd, out bool wrappedForced, out double wrappedWidth, out LineMetrics wrappedMetrics)
            {
                var (wordEnd, wordWidth) = FirstWord(glyphs, start, format);
                var least = wordWidth + indent + format.RightIndent;
                var estimate = Metrics(wordEnd);
                if (flow.Find(estimate, snapToGrid, least) is not { } found)
                {
                    (wrappedEnd, wrappedForced) = FitLine(glyphs, start, column.Width - indent - format.RightIndent, format, indent);
                    (wrappedWidth, wrappedMetrics) = (0, estimate);
                    flow.Place(estimate, snapToGrid, least);
                    return false;
                }
                (wrappedEnd, wrappedForced, wrappedWidth) = BreakInSlot(found, estimate, least, replan: false);
                wrappedMetrics = Metrics(wrappedEnd);
                if (flow.Find(wrappedMetrics, snapToGrid, least) is { } actual && (actual.Inset != found.Inset || actual.Width != found.Width))
                {
                    (wrappedEnd, wrappedForced, wrappedWidth) = BreakInSlot(actual, wrappedMetrics, least, replan: true);
                    wrappedMetrics = Metrics(wrappedEnd);
                }
                ReserveFootnotes(glyphs, start, wrappedEnd, flow);
                return flow.Place(wrappedMetrics, snapToGrid, least);
            }

            // The end of the line starting at `start` in `slot`, and the line's width. The paragraph composer plans
            // the rest of the paragraph in the stretches the next lines would take (Flow.Peek), and plans again when
            // a line goes elsewhere.
            (int End, bool Forced, double Available) BreakInSlot(Slot slot, LineMetrics lineMetrics, double least, bool replan)
            {
                var lineWidth = slot.Width - indent - format.RightIndent;
                if (!paragraphComposer || lineStyles is not null || start >= glyphs.Count)
                {
                    var (fitEnd, fitForced) = FitLine(glyphs, start, lineWidth, format, indent);
                    return (fitEnd, fitForced, lineWidth);
                }
                if (replan || plan is null || planned >= plan.Count || plannedStart != start || !Expected(plan[planned].Line, slot))
                {
                    // The paragraph has at most a line more than it has places to break, and needs stretches about as wide as
                    // its text (twice that is plenty: the plan sets the lines past the last stretch like it).
                    var (breaks, length) = (1, 0.0);
                    for (var i = start; i < glyphs.Count && glyphs[i].Kind != GlyphKind.Table; i++)
                    {
                        breaks += glyphs[i].BreakAfter || glyphs[i].Kind == GlyphKind.Break ? 1 : 0;
                        length += glyphs[i].Advance;
                    }
                    var peeked = flow.Peek(lineMetrics, snapToGrid, least, Math.Min(MaxPlannedSlots, breaks), 2 * length);
                    if (peeked.Count == 0)
                    {
                        peeked.Add(slot);
                    }
                    // Lines past the last stretch are planned like it.
                    var last = peeked[^1];
                    var steady = peeked.FindLastIndex(s => s.Inset != last.Inset || s.Width != last.Width || s.Wrapped != last.Wrapped) + 1;
                    slots = peeked;
                    plan = ComposeParagraph(glyphs, start, columns.Count, LineIndent, steadyLines, n => peeked[Math.Min(n, peeked.Count - 1)].Width,
                        steady, n => peeked[Math.Min(n, peeked.Count - 1)].Wrapped, format, paragraph.MarkStyle.Size);
                    planned = 0;
                    plannedWidth = double.NaN;
                }
                var (planEnd, planForced, _) = plan[planned++];
                plannedStart = planEnd;
                return (planEnd, planForced, lineWidth);
            }

            // Whether plan line `number` was planned for the stretch `slot` takes.
            bool Expected(int number, Slot slot) =>
                slots is { Count: > 0 } made && made[Math.Min(number, made.Count - 1)] is var planFor && planFor.Inset == slot.Inset && planFor.Width == slot.Width;

            // The metrics of the line from `start` to `to`; the paragraph's first line also keeps a rule above that is
            // kept in its frame inside the column (see Flow.Place).
            LineMetrics Metrics(int to)
            {
                var measured = Measure(glyphs.GetRange(start, to - start), paragraph.MarkStyle);
                return columns.Count == 0 && format.RuleAbove is { KeptRoom: > 0 } rule ? measured with { KeptRuleAbove = rule.KeptRoom } : measured;
            }
        }
        flow.PendingSpace = format.SpaceAfter;
        if (spans)
        {
            flow.EndSpan(format.ColumnSpan!.SpaceAfter);
        }
        if (splits)
        {
            flow.EndSplit();
        }
        return columns;
    }

    /// <summary>
    /// Single-line composition (the Single-line Composer, and paragraphs with nested line styles, whose lines change
    /// width as they are styled): the end index (exclusive) of the line starting at <paramref name="start"/>, and
    /// whether a forced line break ends it. Every break the line reaches within its spacing limits is a candidate.
    /// Justified text takes the cheapest by <see cref="LineCost"/> (the later one on a tie), so it may compress word
    /// spaces down to the minimum to take one more word, or hyphenate, when that sets the line closer to its desired
    /// spacing than breaking earlier: InDesign's single-line composer in 08-para-hyphenation-justification p5 sets
    /// line 2 at 90% to fit "all" rather than at 134% without it, and hyphenates "ac-" at 91% rather than leaving
    /// line 3 at 174%. Ragged text keeps its desired spacing (the minimum and maximum apply to justified text only)
    /// and takes the last break that fits, hyphenating only when the last word break would leave more than the
    /// hyphenation zone empty.
    /// </summary>
    private static (int End, bool Forced) FitLine(List<Shaped> glyphs, int start, double available, ParagraphFormatting format, double indent)
    {
        var justified = IsJustified(format.Justification);
        var line = new LineMeasure();
        var (best, bestForced, bestCost) = (-1, false, double.MaxValue);
        // Ragged text: what the last word break would leave empty at the end of the line.
        var wordBreakGap = double.PositiveInfinity;
        var i = start;
        for (; i < glyphs.Count; i++)
        {
            var glyph = glyphs[i];
            if (glyph.Kind == GlyphKind.Table)
            {
                if (i > start)
                {
                    Consider(i, forced: false, hyphen: false, last: true);
                }
                return best >= 0 ? (best, bestForced) : (i, false);
            }
            if (glyph.Kind == GlyphKind.Tab)
            {
                // Right, centre and decimal stops count the text after them, so a right tab at the column's edge
                // (a table of contents) keeps its page number on the line.
                glyph.Advance = Math.Max(0.1, TabAdvance(glyphs, i, line.X, format, indent, available).Advance);
            }
            if (i > start && line.Overflows(glyph, available, format, justified))
            {
                break;
            }
            line.Add(glyph);
            if (glyph.Kind == GlyphKind.Break)
            {
                Consider(i + 1, forced: true, hyphen: false, last: false);
                return best >= 0 ? (best, bestForced) : (i + 1, true);
            }
            if (glyph.BreakAfter)
            {
                Consider(i + 1, forced: false, hyphen: glyph.Kind == GlyphKind.SoftHyphen, last: false);
            }
        }
        if (i >= glyphs.Count)
        {
            Consider(glyphs.Count, forced: false, hyphen: false, last: true);
        }
        // Nothing fits (a word wider than the column): break inside it, where it runs out of room.
        return best >= 0 ? (best, bestForced) : (Math.Max(i, start + 1), false);

        void Consider(int end, bool forced, bool hyphen, bool last)
        {
            var space = hyphen ? line.WithHyphen(HyphenWidth(glyphs[end - 1])) : line.Space;
            if (line.Tightest(space, format, justified) > available + Epsilon)
            {
                return;
            }
            double cost;
            if (justified && !line.HasTab)
            {
                cost = LineCost(space, available, format, forced, last) + (hyphen ? HyphenPenalty : 0);
            }
            else
            {
                if (hyphen && wordBreakGap <= format.Hyphenation.Zone)
                {
                    return;
                }
                if (!hyphen)
                {
                    wordBreakGap = available - (space.Width * format.Spacing.DesiredGlyphScaling);
                }
                cost = 0;
            }
            if (cost <= bestCost)
            {
                (best, bestForced, bestCost) = (end, forced, cost);
            }
        }
    }

    /// <summary>The most stretches the paragraph composer plans a paragraph in at once (a bound on its work).</summary>
    private const int MaxPlannedSlots = 400;

    /// <summary>
    /// The first word of the line starting at <paramref name="start"/>: where it ends (after the line's first break
    /// opportunity) and how wide it is set at its tightest. A stretch text wrap leaves narrower than that takes no text.
    /// </summary>
    private static (int End, double Width) FirstWord(List<Shaped> glyphs, int start, ParagraphFormatting format)
    {
        var line = new LineMeasure();
        var end = start;
        while (end < glyphs.Count && glyphs[end].Kind != GlyphKind.Table)
        {
            var glyph = glyphs[end++];
            line.Add(glyph);
            if (glyph.Kind == GlyphKind.Break || glyph.BreakAfter)
            {
                break;
            }
        }
        var space = end > start && glyphs[end - 1].Kind == GlyphKind.SoftHyphen ? line.WithHyphen(HyphenWidth(glyphs[end - 1])) : line.Space;
        return (end, line.Tightest(space, format, IsJustified(format.Justification), pastMinimum: true));
    }

    /// <summary>What a hyphen adds to the cost of a justified line (about as much as word spaces at their maximum).</summary>
    private const double HyphenPenalty = 100;

    /// <summary>
    /// How much more compressing a line costs than stretching it as far. InDesign's justified text in the references is
    /// stretched about five times as often as it is compressed (in 08-para-drop-caps, -hyphenation-justification,
    /// 07-char-language-nobreak and 08-para-alignment-indents, 32 compressed lines to 214 stretched), yet its
    /// single-line composer still sets line 2 of 08-para-hyphenation-justification p5 at 90% (cost 12.5 times this)
    /// rather than at 134% (109): any weight below 8.7 keeps that; 4 measured best over the doc-samples.
    /// </summary>
    private const double ShrinkWeight = 4;

    private static bool IsJustified(string justification) =>
        justification is "LeftJustified" or "CenterJustified" or "RightJustified" or "FullyJustified";

    /// <summary>
    /// The cost of setting a line of a justified paragraph: 100 r³ for a line set a share r of the way from its desired
    /// spacing to its limits (TeX's badness), <see cref="ShrinkWeight"/> times that when it is compressed, nothing for a
    /// last line that fits (it is not stretched, except in <c>FullyJustified</c> paragraphs), and a cost no ordinary
    /// line reaches for a line that must stretch and has nothing that may (a single word without single-word
    /// justification).
    /// </summary>
    private static double LineCost(LineSpace space, double available, ParagraphFormatting format, bool forced, bool last)
    {
        var stretch = !last || forced || format.Justification == "FullyJustified";
        var ratio = Justifier.Solve(space, available, format.Spacing, stretch, shrink: true).Ratio;
        return double.IsInfinity(ratio) ? 1e12 : 100 * Math.Abs(ratio * ratio * ratio) * (ratio < 0 ? ShrinkWeight : 1);
    }

    /// <summary>
    /// A line being read: its pen position, its width up to the last glyph that is not a space, and what justification
    /// may change in it (<see cref="LineSpace"/>).
    /// </summary>
    private struct LineMeasure
    {
        private double content;
        private double wordSpace;
        private double letterSpace;
        // Spaces after the last glyph that is not one: they count only once a glyph follows them.
        private double pendingWord;
        private double pendingLetter;
        // The space band of the last glyph: letter spacing follows it only when another glyph comes after it.
        private double lastBand;

        public double X { get; private set; }

        public bool HasTab { get; private set; }

        public readonly LineSpace Space => new(content, wordSpace, letterSpace);

        /// <summary>The line ending in a hyphen <paramref name="width"/> wide.</summary>
        public readonly LineSpace WithHyphen(double width) => new(content + width, wordSpace, letterSpace + lastBand);

        /// <summary>The tightest width <paramref name="space"/> can be set at: compressed only in justified text.</summary>
        public readonly double Tightest(LineSpace space, ParagraphFormatting format, bool justified, bool pastMinimum = false) =>
            HasTab ? space.Width
            : justified ? Justifier.TightestWidth(space, format.Spacing, pastMinimum)
            : space.Width * format.Spacing.DesiredGlyphScaling;

        /// <summary>Whether <paramref name="glyph"/> no longer fits on the line, however tightly it is set.</summary>
        public readonly bool Overflows(Shaped glyph, double available, ParagraphFormatting format, bool justified, bool pastMinimum = false)
        {
            if (glyph.Kind is GlyphKind.Space or GlyphKind.SoftHyphen or GlyphKind.Break)
            {
                return false;
            }
            var space = new LineSpace(X + glyph.Advance, wordSpace + pendingWord, letterSpace + pendingLetter + lastBand);
            return (HasTab || glyph.Kind == GlyphKind.Tab ? space.Width : Tightest(space, format, justified, pastMinimum)) > available + Epsilon;
        }

        public void Add(Shaped glyph)
        {
            X += glyph.Advance;
            switch (glyph.Kind)
            {
                case GlyphKind.Space:
                    pendingWord += Stretches(glyph) ? glyph.Natural : 0;
                    pendingLetter += glyph.Band;
                    break;
                case GlyphKind.SoftHyphen or GlyphKind.Break:
                    break;
                default:
                    HasTab |= glyph.Kind == GlyphKind.Tab;
                    content = X;
                    wordSpace += pendingWord;
                    letterSpace += pendingLetter + lastBand;
                    lastBand = glyph.Kind == GlyphKind.Normal ? glyph.Band : 0;
                    (pendingWord, pendingLetter) = (0, 0);
                    break;
            }
        }
    }

    private static double HyphenWidth(Shaped soft) =>
        soft.Font.TryMap('-', out var glyph) ? soft.Font.Advance(glyph) * soft.Size * soft.Style.HorizontalScale : soft.Size * 0.33;

    /// <summary>
    /// The next tab stop after <paramref name="position"/> (measured from the column's left edge). A line that starts
    /// left of the left indent (a hanging first line, <paramref name="lineIndent"/> &lt; LeftIndent) also has an
    /// implied stop at the left indent, as in InDesign: that is where a list's text goes after "bullet, tab". Only an
    /// explicit tab stop comes before it; the default stops (every 36 pt) left of the left indent do not count
    /// (08-para-bullets-numbering: "Step 1:" ending at 31 pt with a 48 pt indent puts the text at 48, not at 36).
    /// </summary>
    private static double NextTabStop(double position, ParagraphFormatting format, double columnWidth, double lineIndent)
    {
        foreach (var tab in format.Tabs)
        {
            if (tab.Position > position + Epsilon)
            {
                return HangingStop(tab.Position);
            }
        }
        var last = format.Tabs.Count > 0 ? format.Tabs[^1].Position : 0;
        var next = last + (Math.Floor((Math.Max(position, last) - last) / DefaultTabInterval) + 1) * DefaultTabInterval;
        var hanging = format.LeftIndent;
        if (lineIndent < hanging - Epsilon && hanging > position + Epsilon)
        {
            return hanging;
        }
        return Math.Min(next, Math.Max(columnWidth, position));

        double HangingStop(double explicitStop) =>
            lineIndent < format.LeftIndent - Epsilon && format.LeftIndent > position + Epsilon && format.LeftIndent < explicitStop
                ? format.LeftIndent
                : explicitStop;
    }

    private sealed record LineMetrics(double Ascent, double Descent, double Leading, double CapHeight, double XHeight, double MaxSize)
    {
        /// <summary>The room the line's above-line objects take above it (<see cref="AboveLineRoom"/>).</summary>
        public double AboveLine { get; init; }

        /// <summary>
        /// How far above the baseline a rule above kept in the frame reaches (<see cref="ParagraphRule.KeptRoom"/>), on
        /// a paragraph's first line: at the top of a column the baseline goes at least that far down.
        /// </summary>
        public double KeptRuleAbove { get; init; }
    }

    private static LineMetrics Measure(List<Shaped> line, RunStyle markStyle)
    {
        double ascent = 0, descent = 0, leading = 0, cap = 0, xHeight = 0, size = 0, aboveLine = 0;
        var any = false;
        foreach (var glyph in line)
        {
            if (glyph.Kind is GlyphKind.Table)
            {
                continue;
            }
            if (glyph.Object?.Anchored is { } anchored)
            {
                aboveLine += anchored.AboveLine ? AboveLineRoom(glyph.Object) : 0;
                continue;
            }
            any = true;
            if (glyph.Object is { } inline)
            {
                // Like a tall glyph: the object stands on the baseline (raised by its offset); with auto leading the
                // line makes room for it.
                var height = inline.Bounds.Height;
                ascent = Math.Max(ascent, height + glyph.Rise);
                descent = Math.Max(descent, -Math.Min(0, glyph.Rise));
                leading = Math.Max(leading, glyph.Style.Leading ?? (glyph.Style.AutoLeading * (height + Math.Max(0, glyph.Rise))));
                continue;
            }
            // A superscript's raise does not count, a baseline shift does: InDesign sets the first baseline of a line
            // with superscripts where it sets it without (07-char-case-position p3: "E = mc2" in 18 pt Liberation Serif,
            // superscript or not, 36 pt below its label; 11-long-footnotes-endnotes p1: a footnote reference).
            ascent = Math.Max(ascent, (glyph.Style.Font.Ascent * glyph.Size) + Math.Max(0, glyph.Style.BaselineShift));
            descent = Math.Max(descent, (glyph.Style.Font.Descent * glyph.Size) - Math.Min(0, glyph.Rise));
            leading = Math.Max(leading, glyph.Style.EffectiveLeading);
            cap = Math.Max(cap, glyph.Style.Font.CapHeight * glyph.Size);
            xHeight = Math.Max(xHeight, glyph.Style.Font.XHeight * glyph.Size);
            size = Math.Max(size, glyph.Size);
        }
        if (!any)
        {
            var font = markStyle.Font;
            return new LineMetrics(font.Ascent * markStyle.Size, font.Descent * markStyle.Size, markStyle.EffectiveLeading, font.CapHeight * markStyle.Size, font.XHeight * markStyle.Size, markStyle.Size)
            {
                AboveLine = aboveLine,
            };
        }
        return new LineMetrics(ascent, descent, leading, cap, xHeight, size) { AboveLine = aboveLine };
    }

    /// <summary>
    /// Drops the hyphenation points in glyphs[from..to) (points the text does not break at); a ligature one of them
    /// split is whole again. Returns the new end.
    /// </summary>
    private static int DropHyphenationPoints(List<Shaped> glyphs, int from, int to)
    {
        for (var i = to - 1; i >= from; i--)
        {
            var soft = glyphs[i];
            if (soft.Kind != GlyphKind.SoftHyphen)
            {
                continue;
            }
            if (soft.Unbroken is { } unbroken && i - soft.UnbrokenBefore >= from && i + soft.UnbrokenAfter < to)
            {
                glyphs.RemoveRange(i - soft.UnbrokenBefore, soft.UnbrokenBefore + 1 + soft.UnbrokenAfter);
                glyphs.Insert(i - soft.UnbrokenBefore, unbroken);
                to -= soft.UnbrokenBefore + soft.UnbrokenAfter;
                i -= soft.UnbrokenBefore;
            }
            else
            {
                glyphs.RemoveAt(i);
                to--;
            }
        }
        return to;
    }

    private void EmitLine(List<Shaped> line, Flow flow, ParagraphFormatting format, double indent, double available, bool isLast, bool forced, LineMetrics metrics, int paragraph)
    {
        // Drop trailing spaces and the break itself; a soft hyphen at the end becomes a visible hyphen.
        var visible = new List<Shaped>(line);
        while (visible.Count > 0 && visible[^1].Kind is GlyphKind.Space or GlyphKind.Break)
        {
            visible.RemoveAt(visible.Count - 1);
        }
        if (visible.Count > 0 && visible[^1].Kind == GlyphKind.SoftHyphen && !isLast)
        {
            var soft = visible[^1];
            var hyphen = new List<Shaped>();
            ShapeText("-", soft.Style, hyphen);
            // A font without a hyphen glyph (reported as missing, as for any character): the break shows no hyphen.
            if (hyphen.Count > 0)
            {
                visible[^1] = hyphen[0];
            }
        }
        DropHyphenationPoints(visible, 0, visible.Count);

        // Tab alignment (right/centre stops shift the text that follows them).
        ApplyTabAlignment(visible, format, indent, available);

        var justification = format.Justification;
        var justify = justification == "FullyJustified" || (IsJustified(justification) && (!isLast || forced));
        var align = justification switch
        {
            "CenterAlign" or "CenterJustified" => "center",
            "RightAlign" or "RightJustified" or "AwayFromBindingSide" => "right",
            _ => "left",
        };
        // Justification: word spacing, letter spacing and glyph scaling within the paragraph's limits (Justifier). A line
        // with tabs keeps its spacing (its tab stops place it). A justified line holding a single word is letter spaced
        // across, or aligned as Single Word Justification says.
        var space = MeasureLine(visible);
        if (justify && space.WordSpace <= 0 && format.Spacing.SingleWordJustification != "FullyJustified")
        {
            justify = false;
            align = format.Spacing.SingleWordJustification switch
            {
                "CenterAlign" => "center",
                "RightAlign" => "right",
                _ => "left",
            };
        }
        var spacing = visible.Any(glyph => glyph.Kind == GlyphKind.Tab)
            ? new LineSpacing(0, 0, 1, 0)
            : Justifier.Solve(space, available, format.Spacing, stretch: justify, shrink: IsJustified(justification));
        var lastLetter = visible.Count - 1;
        double Advance(Shaped glyph, int index) => glyph.Kind == GlyphKind.Tab ? glyph.Advance
            : glyph.Object is not null ? glyph.Advance + (index < lastLetter ? glyph.Band * spacing.Letter : 0)
            : (glyph.Advance + (Stretches(glyph) ? glyph.Natural * spacing.Word : 0) + (index < lastLetter ? glyph.Band * spacing.Letter : 0)) * spacing.Scale;
        var width = 0.0;
        for (var i = 0; i < visible.Count; i++)
        {
            width += Advance(visible[i], i);
        }
        var column = flow.Column;
        var x = column.X + indent + align switch
        {
            "center" => (available - width) / 2,
            "right" => available - width,
            _ => 0,
        };
        var baseline = flow.Baseline;
        var textLeft = x;
        var lineText = new StringBuilder();
        var runStart = x;
        var run = new List<PlacedGlyph>();
        Shaped? runFirst = null;
        // InDesign paints a line's underlines before its text, strikethroughs after each run (07-char-underline-strike).
        var underlineAt = flow.OpCount;

        void FlushRun()
        {
            if (runFirst is not null && run.Count > 0)
            {
                var style = runFirst.Style;
                var y = baseline - runFirst.Rise;
                flow.Add(GlyphRunOp.Of(style, runFirst.Font, runFirst.Size, style.HorizontalScale * spacing.Scale, runStart, y, run.ToList()));
                var runWidth = run.Sum(glyph => glyph.Advance);
                if (style.Underline is { } underline && underline.ColorFor(style.Fill) is { } color)
                {
                    // Auto weight and offset come from the font's post table; an offset in points is down from the baseline.
                    var weight = underline.Weight ?? (runFirst.Font.UnderlineThickness * runFirst.Size);
                    var offset = underline.Offset ?? (runFirst.Font.UnderlineOffset * runFirst.Size);
                    flow.Insert(underlineAt++, new TextRuleOp(new Point(runStart, y + offset), runWidth, weight, color, underline.Type, underline.Gap));
                }
                if (style.StrikeThrough is { } strike && strike.ColorFor(style.Fill) is { } strikeColor)
                {
                    // An offset in points is up from the baseline; auto is a third of the point size (07-char-underline-strike:
                    // 7.33 pt for 22 pt, 3.33 pt for 10 pt, 10 pt for 30 pt, 4.67 pt for 14 pt Liberation Serif), not the
                    // font's yStrikeoutPosition (0.21 em).
                    var weight = strike.Weight ?? (runFirst.Font.StrikeoutThickness * runFirst.Size);
                    var offset = strike.Offset ?? (runFirst.Size / 3);
                    flow.Add(new TextRuleOp(new Point(runStart, y - offset), runWidth, weight, strikeColor, strike.Type, strike.Gap));
                }
            }
            run.Clear();
            runFirst = null;
        }

        EmitAboveLine(visible, flow, format, baseline, metrics);
        var markerLength = 0;
        for (var index = 0; index < visible.Count; index++)
        {
            var glyph = visible[index];
            if (glyph.Marker && markerLength == lineText.Length)
            {
                markerLength += glyph.Kind == GlyphKind.Tab ? 1 : glyph.Text.Length;
            }
            var advance = Advance(glyph, index);
            if (glyph.Kind == GlyphKind.Tab)
            {
                FlushRun();
                // InDesign writes a tab as a space glyph where it starts: invisible, but text extraction (copy, search,
                // accessibility) then sees a word break there, also when a leader or the next column follows closely.
                if (glyph.Font.TryMap(' ', out var spaceGlyph))
                {
                    var spaceWidth = glyph.Font.Advance(spaceGlyph) * glyph.Size * glyph.Style.HorizontalScale;
                    flow.Add(GlyphRunOp.Of(glyph.Style, glyph.Font, glyph.Size, glyph.Style.HorizontalScale, x, baseline - glyph.Rise,
                        [new PlacedGlyph(spaceGlyph, " ", spaceWidth, spaceWidth)]));
                }
                if (glyph.Leader is { Length: > 0 } leader)
                {
                    DrawLeader(leader, glyph, column.X, x, x + advance, baseline, flow);
                }
                x += advance;
                runStart = x;
                lineText.Append('\t');
                continue;
            }
            if (glyph.Object is { Anchored: { } anchored } placed)
            {
                // Above-line objects are placed with their line (EmitAboveLine); a custom one from its anchor's position.
                if (!anchored.AboveLine)
                {
                    var custom = PlaceCustom(placed, anchored, glyph, flow, x, baseline, metrics);
                    flow.Add(custom);
                    if (anchored.Wrap is { } wrap)
                    {
                        flow.AddWrap(wrap with { Box = wrap.Box.Offset(custom.Placement.Tx, custom.Placement.Ty) });
                    }
                }
                lineText.Append(glyph.Text);
                // The marker is only as wide as its kerning with the next letter.
                if (advance != 0)
                {
                    FlushRun();
                    x += advance;
                    runStart = x;
                }
                continue;
            }
            if (glyph.Object is { } inline)
            {
                FlushRun();
                var bounds = inline.Bounds;
                flow.Add(new PageItemOp(inline.Element, Matrix.Translation(x - bounds.Left, baseline - glyph.Rise - bounds.Bottom)));
                x += advance;
                runStart = x;
                lineText.Append(glyph.Text);
                continue;
            }
            if (runFirst is not null && (!ReferenceEquals(runFirst.Font, glyph.Font) || runFirst.Size != glyph.Size || runFirst.Rise != glyph.Rise
                || !ReferenceEquals(runFirst.Style, glyph.Style)))
            {
                FlushRun();
                runStart = x;
            }
            runFirst ??= glyph;
            run.Add(new PlacedGlyph(glyph.Glyph, glyph.Text, advance, glyph.Natural * spacing.Scale));
            lineText.Append(glyph.Text);
            x += advance;
        }
        FlushRun();
        flow.Layout.Lines.Add(new LineInfo(flow.FrameIndex, flow.ColumnIndex, baseline, column.X + indent, width, lineText.ToString())
        {
            MarkerLength = markerLength,
            Paragraph = paragraph,
            TextLeft = textLeft,
            ColumnLeft = column.X,
            ColumnWidth = column.Width,
            Ascent = metrics.Ascent,
            Descent = metrics.Descent,
            Leading = metrics.Leading,
        });
    }

    /// <summary>
    /// Whether justification widens this glyph: word spaces and (flexible) no-break spaces only. InDesign's fixed
    /// spaces (em, en, thin, ... often after a list's bullet) keep their width.
    /// </summary>
    private static bool Stretches(Shaped glyph) => glyph.Kind == GlyphKind.Space && glyph.Text is " " or " ";

    /// <summary>What justification may change in the glyphs of a line set as they are (see <see cref="LineSpace"/>).</summary>
    private static LineSpace MeasureLine(List<Shaped> visible)
    {
        double width = 0, wordSpace = 0, letterSpace = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            var glyph = visible[i];
            width += glyph.Advance;
            wordSpace += Stretches(glyph) ? glyph.Natural : 0;
            letterSpace += i < visible.Count - 1 && glyph.Kind is GlyphKind.Normal or GlyphKind.Space ? glyph.Band : 0;
        }
        return new LineSpace(width, wordSpace, letterSpace);
    }

    private static void ApplyTabAlignment(List<Shaped> line, ParagraphFormatting format, double indent, double available)
    {
        double x = 0;
        for (var i = 0; i < line.Count; i++)
        {
            var glyph = line[i];
            if (glyph.Kind != GlyphKind.Tab)
            {
                x += glyph.Advance;
                continue;
            }
            var (advance, stop) = TabAdvance(line, i, x, format, indent, available);
            glyph.Advance = advance;
            glyph.Leader = stop.Leader;
            x += glyph.Advance;
        }
    }

    /// <summary>
    /// The space the tab at <paramref name="glyphs"/>[<paramref name="index"/>] takes when the line before it is
    /// <paramref name="x"/> wide, and the stop it goes to: the text after it (up to the next tab or break) starts at a
    /// left stop, ends at a right one, is centred on a centre one, and for a character stop has its alignment
    /// character's left edge on the stop, or ends there without one (InDesign, 08-para-tabs p3).
    /// </summary>
    private static (double Advance, ResolvedTab Stop) TabAdvance(List<Shaped> glyphs, int index, double x, ParagraphFormatting format, double indent, double available)
    {
        var tab = glyphs[index].Tab!;
        var target = tab.RightIndentTab ? available + indent : NextTabStop(x + indent, format, available + indent, indent);
        var stop = tab.RightIndentTab
            ? new ResolvedTab(target, "RightAlign")
            : format.Tabs.FirstOrDefault(candidate => Math.Abs(candidate.Position - target) < Epsilon) is { Alignment: not null } found ? found : new ResolvedTab(target, "LeftAlign");
        if (stop.Alignment is not ("RightAlign" or "CenterAlign" or "CharacterAlign"))
        {
            return (Math.Max(0, target - indent - x), stop);
        }
        var segment = 0.0;
        double? beforeCharacter = null;
        for (var j = index + 1; j < glyphs.Count && glyphs[j].Kind is not (GlyphKind.Tab or GlyphKind.Break or GlyphKind.Table); j++)
        {
            if (beforeCharacter is null && stop.Alignment == "CharacterAlign" && glyphs[j].Text == stop.AlignmentCharacter)
            {
                beforeCharacter = segment;
            }
            segment += glyphs[j].Advance;
        }
        var start = stop.Alignment switch
        {
            "RightAlign" => target - segment,
            "CenterAlign" => target - (segment / 2),
            _ => target - (beforeCharacter ?? segment),
        };
        return (Math.Max(0, start - indent - x), stop);
    }

    /// <summary>
    /// Draws a tab leader across a tab's space, from <paramref name="from"/> to <paramref name="to"/>, the way InDesign
    /// does (measured in 08-para-tabs): copies of the leader text stand on a grid of its width counted from the
    /// column's left edge, so leaders line up from line to line; the first copy starts at or after the text before the
    /// tab, and a copy is drawn while its first glyph fits before the text after it (the rest of a multi-character
    /// leader may run into it). The glyphs take the tab's own formatting.
    /// </summary>
    private void DrawLeader(string leader, Shaped tab, double columnLeft, double from, double to, double baseline, Flow flow)
    {
        var unit = new List<Shaped>();
        ShapeText(leader, tab.Style, unit);
        unit.RemoveAll(glyph => glyph.Kind == GlyphKind.SoftHyphen);
        var width = unit.Sum(glyph => glyph.Advance);
        if (unit.Count == 0 || width <= Epsilon)
        {
            return;
        }
        var first = Math.Ceiling(((from - columnLeft) / width) - 1e-6);
        var glyphs = new List<PlacedGlyph>();
        double start = columnLeft + (first * width), position = start;
        while (position + unit[0].Advance <= to + Epsilon)
        {
            glyphs.AddRange(unit.Select(glyph => new PlacedGlyph(glyph.Glyph, glyph.Text, glyph.Advance, glyph.Natural)));
            position += width;
        }
        if (glyphs.Count > 0)
        {
            var head = unit[0];
            flow.Add(GlyphRunOp.Of(head.Style, head.Font, head.Size, head.Style.HorizontalScale, start, baseline - head.Rise, glyphs));
        }
    }

    // ---- tables --------------------------------------------------------------------------------------

    private sealed record CellLayout(XElement Cell, int Column, int Row, int ColumnSpan, int RowSpan, double Width, double ContentHeight, StoryLayout Text);

    private void LayoutTable(XElement table, Flow flow, ParagraphFormatting format)
    {
        var resolver = reader.Resolver;
        var columnWidths = table.Elements("Column").Select(c => Values.Number(c.Attribute("SingleColumnWidth")?.Value, 100)).ToList();
        var rows = table.Elements("Row").ToList();
        if (columnWidths.Count == 0 || rows.Count == 0)
        {
            return;
        }
        var headerCount = Math.Min(rows.Count, (int)Values.Number(table.Attribute("HeaderRowCount")?.Value, 0));
        var footerCount = Math.Min(rows.Count - headerCount, (int)Values.Number(table.Attribute("FooterRowCount")?.Value, 0));
        if (headerCount + footerCount == rows.Count)
        {
            // No body rows: nothing to repeat, the rows simply follow one another.
            (headerCount, footerCount) = (0, 0);
        }
        var spaceBefore = Values.Number(resolver.Table(table, "SpaceBefore"), 4);
        var spaceAfter = Values.Number(resolver.Table(table, "SpaceAfter"), -4);

        // Compose every cell at its width.
        var cells = new List<CellLayout>();
        foreach (var cell in table.Elements("Cell"))
        {
            var name = (cell.Attribute("Name")?.Value ?? "0:0").Split(':');
            var column = int.Parse(name[0], CultureInfo.InvariantCulture);
            var row = int.Parse(name.Length > 1 ? name[1] : "0", CultureInfo.InvariantCulture);
            var columnSpan = Math.Max(1, (int)Values.Number(cell.Attribute("ColumnSpan")?.Value, 1));
            var rowSpan = Math.Max(1, (int)Values.Number(cell.Attribute("RowSpan")?.Value, 1));
            var width = columnWidths.Skip(column).Take(columnSpan).Sum();
            var inset = CellInsets(cell);
            var innerWidth = Math.Max(1, width - inset.Left - inset.Right);
            var area = new FrameArea("cell", [new Rect(0, 0, innerWidth, 1e6)], "TopAlign",
                resolver.Cell(cell, "FirstBaselineOffset") ?? "AscentOffset", Values.Number(resolver.Cell(cell, "MinimumFirstBaselineOffset"), 0), flow.PageName)
            {
                Markers = flow.Frame.Markers,
            };
            var text = Compose(cell, [area]);
            // InDesign measures cell content to the last baseline (descenders may enter the bottom inset): a row
            // stored by InDesign at its composed height then keeps that height here too.
            var bottom = text.Lines.Count == 0 ? 0 : text.Lines.Max(line => line.Baseline);
            cells.Add(new CellLayout(cell, column, row, columnSpan, rowSpan, width, bottom + inset.Top + inset.Bottom, text));
        }

        // Row heights: stored height (InDesign writes the composed height), grown to fit the text when AutoGrow.
        var heights = new double[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var single = Values.Number(row.Attribute("SingleRowHeight")?.Value, 0);
            var minimum = Values.Number(row.Attribute("MinimumHeight")?.Value, Math.Min(single, 3));
            var autoGrow = row.Attribute("AutoGrow")?.Value != "false";
            var content = cells.Where(c => c.Row == r && c.RowSpan == 1).Select(c => c.ContentHeight).DefaultIfEmpty(0).Max();
            heights[r] = autoGrow ? Math.Max(Math.Max(minimum, single), content) : Math.Max(single, minimum);
            var maximum = Values.Number(row.Attribute("MaximumHeight")?.Value, double.MaxValue);
            heights[r] = Math.Min(heights[r], Math.Max(maximum, minimum));
        }
        foreach (var spanning in cells.Where(c => c.RowSpan > 1))
        {
            var last = Math.Min(rows.Count - 1, spanning.Row + spanning.RowSpan - 1);
            var available = heights.Skip(spanning.Row).Take(spanning.RowSpan).Sum();
            if (spanning.ContentHeight > available)
            {
                heights[last] += spanning.ContentHeight - available;
            }
        }

        var x = flow.Column.X + format.LeftIndent;
        double top;
        if (flow.AtColumnTop)
        {
            top = flow.Column.Y;
        }
        else
        {
            top = flow.Baseline + flow.LastDescent + spaceBefore;
        }
        flow.BeginBlock();

        var formatting = new TableFormatting(table, resolver, rows.Count, columnWidths.Count);
        var edges = new Dictionary<(long, long, long, long), LineOp>();
        void FlushEdges()
        {
            foreach (var edge in MergeEdges(edges.Values))
            {
                flow.Add(edge);
            }
            edges.Clear();
        }

        void DrawRow(int r, double rowTop)
        {
            foreach (var cell in cells.Where(c => c.Row == r))
            {
                var height = heights.Skip(r).Take(cell.RowSpan).Sum();
                var cellX = x + columnWidths.Take(cell.Column).Sum();
                DrawCell(cell, new Rect(cellX, rowTop, cell.Width, height), flow, formatting, edges);
            }
        }

        // Rows joined by a merged cell move together (InDesign never splits a merged cell between frames):
        // blockEnd[r] is the last row of the block starting at r.
        var blockEnd = new int[rows.Count];
        for (var r = 0; r < rows.Count;)
        {
            var end = r;
            for (var row = r; row <= end; row++)
            {
                foreach (var cell in cells.Where(c => c.Row == row))
                {
                    end = Math.Max(end, Math.Min(rows.Count - 1, cell.Row + cell.RowSpan - 1));
                }
            }
            for (var row = r; row <= end; row++)
            {
                blockEnd[row] = end;
            }
            r = end + 1;
        }

        void DrawRows(int from, int to)
        {
            for (var r = from; r < to; r++)
            {
                DrawRow(r, top);
                top += heights[r];
            }
            if (to > from)
            {
                flow.PlaceBlock();
            }
        }

        // A table split over columns and frames repeats its header rows at the top and its footer rows at the bottom
        // of every part, and the footer's height is kept free when deciding which body rows fit (09-style-table-
        // cell-styles p3: three body rows and the footer in the first frame, where four body rows alone would fit;
        // 10-table-structure p4). The table starts in the next column unless its header, first body rows and footer
        // fit together, so a header is never left alone (from memory, not in the samples). The samples keep the
        // default repeat, in every text column, which IDML leaves out; the other options and their attribute names
        // are from memory of InDesign's Table Options: BreakHeaders/BreakFooters OncePerTextFrame or OncePerPage
        // repeat only where the table comes into (leaves) another frame or page, SkipFirstHeader leaves the header
        // out of the first part and SkipLastFooter the footer out of the last.
        var bodyEnd = rows.Count - footerCount;
        var footerHeight = heights.Skip(bodyEnd).Sum();
        var headerRepeat = resolver.Table(table, "BreakHeaders") ?? "InAllTextColumns";
        var footerRepeat = resolver.Table(table, "BreakFooters") ?? "InAllTextColumns";
        var skipFirstHeader = resolver.Table(table, "SkipFirstHeader") == "true";
        var skipLastFooter = resolver.Table(table, "SkipLastFooter") == "true";

        (int Frame, int Page) Position() => (flow.FrameIndex, flow.Frame.PageIndex);
        static bool Repeats(string repeat, (int Frame, int Page) from, (int Frame, int Page) to) => repeat switch
        {
            "OncePerTextFrame" => from.Frame != to.Frame,
            "OncePerPage" => from.Page != to.Page,
            _ => true,
        };

        // Moves on to the next part of the table: the next column, or where the row's Start Row puts it
        // (10-table-structure p4: row 3 starts in the next column, here the next frame).
        bool MoveOn(string? start)
        {
            if (start is null)
            {
                return flow.NextColumn();
            }
            flow.StartParagraph(start);
            return !flow.Exhausted;
        }

        // Whether the footer closes the part of the table in this column when the table moves on as start says.
        bool FooterAtBreak(string? start)
        {
            if (footerCount == 0)
            {
                return false;
            }
            if (footerRepeat is not ("OncePerTextFrame" or "OncePerPage"))
            {
                return true;
            }
            var here = Position();
            var saved = flow.Save();
            var to = MoveOn(start) ? Position() : (-1, -1);
            flow.Restore(saved);
            return Repeats(footerRepeat, here, to);
        }

        // The footer below the block ending at row last: the table's last footer, or the repeated one.
        double FooterBelow(int last, bool repeated) => last == bodyEnd - 1 ? (skipLastFooter ? 0 : footerHeight) : repeated ? footerHeight : 0;

        var footerHere = false;
        for (var r = headerCount; r < bodyEnd;)
        {
            // The first row of a block needs room for the whole block; the rest of the block follows it.
            var last = Math.Min(blockEnd[r], bodyEnd - 1);
            var needed = heights.Skip(r).Take(last - r + 1).Sum();
            var start = rows[r].Attribute("StartRow")?.Value is { } value && value != "Anywhere" ? value : null;
            if (r == headerCount)
            {
                // The table's first part: its header, first block and footer go to the next column together.
                if (start is not null)
                {
                    var ordinal = flow.ColumnOrdinal;
                    if (!MoveOn(start))
                    {
                        flow.Layout.Overset = true;
                        return;
                    }
                    if (flow.ColumnOrdinal != ordinal)
                    {
                        top = flow.Column.Y;
                    }
                }
                var header = skipFirstHeader ? 0 : heights.Take(headerCount).Sum();
                if (!flow.AtColumnTop && top + header + needed + FooterBelow(last, FooterAtBreak(null)) > flow.Column.Bottom + Epsilon)
                {
                    if (!flow.NextColumn())
                    {
                        flow.Layout.Overset = true;
                        return;
                    }
                    top = flow.Column.Y;
                }
                x = flow.Column.X + format.LeftIndent;
                footerHere = FooterAtBreak(null);
                if (!skipFirstHeader)
                {
                    DrawRows(0, headerCount);
                }
            }
            else if (start is not null || top + needed + FooterBelow(last, footerHere) > flow.Column.Bottom + Epsilon)
            {
                // Close this part with its footer and open the next with its header. A block taller than a whole
                // column is drawn there anyway.
                if (FooterAtBreak(start))
                {
                    DrawRows(bodyEnd, rows.Count);
                }
                FlushEdges();
                var from = Position();
                if (!MoveOn(start))
                {
                    flow.Layout.Overset = true;
                    return;
                }
                x = flow.Column.X + format.LeftIndent;
                top = flow.Column.Y;
                footerHere = FooterAtBreak(null);
                if (Repeats(headerRepeat, from, Position()))
                {
                    DrawRows(0, headerCount);
                }
            }
            DrawRows(r, last + 1);
            r = last + 1;
        }
        if (!skipLastFooter)
        {
            DrawRows(bodyEnd, rows.Count);
        }
        FlushEdges();
        flow.EndBlock(top + spaceAfter);
    }

    /// <summary>
    /// One stroke per stretch of a table rule. Cells on either side of a rule can describe it with different extents
    /// (a merged cell's whole bottom edge, the top edges of the cells below it): each rule is split at every cell
    /// boundary, the heavier stroke wins each piece (the first drawn on a tie), and neighbouring pieces with the same
    /// stroke are joined again. Drawing overlapping copies would darken the shared part of the rule.
    /// </summary>
    private static List<LineOp> MergeEdges(IEnumerable<LineOp> edges)
    {
        const double Tolerance = 1e-4;
        var result = new List<LineOp>();
        var ordered = edges.ToList();
        foreach (var horizontal in new[] { true, false })
        {
            var axis = ordered.Where(e => horizontal ? Math.Abs(e.From.Y - e.To.Y) < Tolerance : Math.Abs(e.From.X - e.To.X) < Tolerance && Math.Abs(e.From.Y - e.To.Y) >= Tolerance);
            foreach (var line in axis.GroupBy(e => Math.Round(horizontal ? e.From.Y : e.From.X, 3)))
            {
                var position = line.First();
                double Start(LineOp e) => horizontal ? Math.Min(e.From.X, e.To.X) : Math.Min(e.From.Y, e.To.Y);
                double End(LineOp e) => horizontal ? Math.Max(e.From.X, e.To.X) : Math.Max(e.From.Y, e.To.Y);
                var cuts = line.SelectMany(e => new[] { Start(e), End(e) }).Order().ToList();
                LineOp? current = null;
                double currentStart = 0, currentEnd = 0;
                void Emit()
                {
                    if (current is not null)
                    {
                        result.Add(horizontal
                            ? current with { From = new Point(currentStart, position.From.Y), To = new Point(currentEnd, position.From.Y) }
                            : current with { From = new Point(position.From.X, currentStart), To = new Point(position.From.X, currentEnd) });
                    }
                    current = null;
                }
                for (var i = 0; i + 1 < cuts.Count; i++)
                {
                    var (a, b) = (cuts[i], cuts[i + 1]);
                    if (b - a < Tolerance)
                    {
                        continue;
                    }
                    LineOp? winner = null;
                    foreach (var edge in line)
                    {
                        if (Start(edge) <= a + Tolerance && End(edge) >= b - Tolerance && (winner is null || edge.Width > winner.Width))
                        {
                            winner = edge;
                        }
                    }
                    if (winner is null || current is null || Math.Abs(currentEnd - a) > Tolerance || !SameStroke(current, winner))
                    {
                        Emit();
                        if (winner is null)
                        {
                            continue;
                        }
                        current = winner;
                        currentStart = a;
                    }
                    currentEnd = b;
                }
                Emit();
            }
        }
        // Anything not horizontal or vertical as it came.
        result.AddRange(ordered.Where(e => Math.Abs(e.From.Y - e.To.Y) >= Tolerance && Math.Abs(e.From.X - e.To.X) >= Tolerance));
        return result;
    }

    private static bool SameStroke(LineOp a, LineOp b) =>
        a.Width == b.Width && a.Color.Space == b.Color.Space && a.Color.Components.SequenceEqual(b.Color.Components);

    private static double LastDescent(StoryLayout text)
    {
        var lastRun = text.OpsByFrame.Values.SelectMany(ops => ops).OfType<GlyphRunOp>().LastOrDefault();
        return lastRun is null ? 0 : lastRun.Font.Descent * lastRun.Size;
    }

    private Spacing CellInsets(XElement cell)
    {
        var resolver = reader.Resolver;
        // CS6+ writes TextTopInset (text cells); older files TopInset.
        double Inset(string side) => Values.Number(resolver.Cell(cell, "Text" + side + "Inset") ?? resolver.Cell(cell, side + "Inset"), 4);
        return new Spacing(Inset("Top"), Inset("Right"), Inset("Bottom"), Inset("Left"));
    }

    /// <summary>
    /// Draws a cell's fill, text and edges. Each fill and stroke attribute comes from the cell (local, then its cell
    /// style, own or from its table style's region), else from the table (<see cref="TableFormatting"/>).
    /// </summary>
    private void DrawCell(CellLayout cell, Rect box, Flow flow, TableFormatting table, Dictionary<(long, long, long, long), LineOp> edges)
    {
        var resolver = reader.Resolver;
        var element = cell.Cell;
        var fillSource = table.FillSource(cell.Row, cell.Column);
        if (document.Swatches.ResolveColor(resolver.Cell(element, "FillColor") ?? table.Value(fillSource, "Color")) is { } fill)
        {
            var tint = Values.Number(resolver.Cell(element, "FillTint") ?? table.Value(fillSource, "Tint"), -1);
            flow.Add(new RectOp(box, tint >= 0 ? fill.WithTint(tint) : fill));
        }
        var inset = CellInsets(element);
        var innerHeight = box.Height - inset.Top - inset.Bottom;
        var contentHeight = cell.ContentHeight - inset.Top - inset.Bottom;
        var dy = (resolver.Cell(element, "VerticalJustification") ?? "TopAlign") switch
        {
            "CenterAlign" => Math.Max(0, (innerHeight - contentHeight) / 2),
            "BottomAlign" => Math.Max(0, innerHeight - contentHeight),
            _ => 0,
        };
        foreach (var op in cell.Text.OpsByFrame.Values.SelectMany(ops => ops))
        {
            flow.Add(op.Offset(box.X + inset.Left, box.Y + inset.Top + dy));
        }
        void Edge(string side, Point from, Point to)
        {
            var stored = resolver.Cell(element, side + "EdgeStrokeWeight");
            // A merged cell borders several cells along its spanning sides. InDesign leaves the weight out when the
            // segments differ ("mixed"); the neighbouring cells then hold each segment's stroke, so draw nothing here.
            // On the outside of the table there is no neighbour: a cell that stores no edge weight at all has default
            // edges, not mixed ones, and gets the table border there (10-table-alternating p7).
            var spans = side is "Left" or "Right" ? cell.RowSpan > 1 : cell.ColumnSpan > 1;
            if (stored is null && spans && (!table.IsOuter(side, cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan)
                || ((string[])["Top", "Bottom", "Left", "Right"]).Any(other => resolver.Cell(element, other + "EdgeStrokeWeight") is not null)))
            {
                return;
            }
            var source = table.EdgeSource(side, cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan);
            var weight = Values.Number(stored ?? table.Value(source, "Weight"), 1);
            if (weight <= 0)
            {
                return;
            }
            var color = document.Swatches.ResolveColor(resolver.Cell(element, side + "EdgeStrokeColor") ?? table.Value(source, "Color") ?? "Color/Black");
            if (color is null)
            {
                return;
            }
            var tint = Values.Number(resolver.Cell(element, side + "EdgeStrokeTint") ?? table.Value(source, "Tint"), -1);
            // Neighbouring cells share an edge: keep the heavier stroke, drawn once (InDesign's best-joins look).
            var key = ((long)Math.Round(from.X * 100), (long)Math.Round(from.Y * 100), (long)Math.Round(to.X * 100), (long)Math.Round(to.Y * 100));
            if (!edges.TryGetValue(key, out var existing) || existing.Width < weight)
            {
                edges[key] = new LineOp(from, to, weight, tint >= 0 ? color.WithTint(tint) : color);
            }
        }
        Edge("Top", new Point(box.Left, box.Top), new Point(box.Right, box.Top));
        Edge("Bottom", new Point(box.Left, box.Bottom), new Point(box.Right, box.Bottom));
        Edge("Left", new Point(box.Left, box.Top), new Point(box.Left, box.Bottom));
        Edge("Right", new Point(box.Right, box.Top), new Point(box.Right, box.Bottom));
    }

    // ---- flow through frames and columns ---------------------------------------------------------------

    /// <summary>
    /// The stretch of the column a line takes: <paramref name="Inset"/> from the column's left edge, <paramref name="Width"/>
    /// wide; <paramref name="Wrapped"/> when text wrap or the frame's shape limits it.
    /// </summary>
    private readonly record struct Slot(double Inset, double Width, bool Wrapped);

    /// <summary>
    /// The position in the frame/column sequence and the ops of the columns filled so far. Columns are moved into
    /// their frames only at the end (<see cref="Finish"/>), so that keep options can return to a saved
    /// <see cref="State"/> and lay the text out again. Paragraphs that span columns divide a frame into bands: the
    /// columns below a span start under it (<see cref="BeginSpan"/>, <see cref="EndSpan"/>); a split paragraph flows
    /// through sub-columns of its column (<see cref="BeginSplit"/>). A band can be given a lowest baseline
    /// (<see cref="SetLimit"/>), which is how <see cref="Compose"/> balances columns.
    /// </summary>
    private sealed partial class Flow
    {
        private readonly IReadOnlyList<FrameArea> frames;
        private readonly List<FilledColumn> filled = [];
        // The lowest baseline of the balanced bands and split paragraphs, by Bands.Id and SplitColumns.Id.
        private readonly Dictionary<int, double> limits = [];
        private List<DrawOp> columnOps = [];
        // Layout.Lines.Count when the current column's ops began.
        private int columnLines;
        private double usedBottom;
        // The page (FrameArea.PageIndex) of the last line or table placed: a page the story has not used yet is
        // where "start on next page" can begin without moving.
        private int contentPage = int.MinValue;
        private Bands bands;
        // The lowest baseline placed in the current band, and in the current split paragraph's column; NaN for none yet.
        private double bandBottom = double.NaN;
        private double splitBottom = double.NaN;
        // The last band or split id handed out: the same layout gives the same ids, so a limit finds its band again.
        private int ids;
        // The stretches text wrap leaves for a band (reused), and the ones right of the last line on its baseline: that
        // band and the next stretch to fill.
        private readonly List<(double Left, double Right)> pieces = [];
        private (double Baseline, double Top, int Next)? pending;
        // The wraps of the custom-positioned objects placed so far, with the frame (index) each is in: replaced, never
        // changed, so that a State keeps the ones placed before it. The text area they make with the frame's, cached.
        private (int Frame, WrapRegion Region)[] anchoredWraps = [];
        private (FrameArea? Frame, (int Frame, WrapRegion Region)[]? Wraps, TextArea? Area) areaCache;

        /// <summary>
        /// Where text wrap and the frame's shape let the lines of the current frame go: its <see cref="FrameArea.TextArea"/>
        /// and the wraps of the anchored objects placed in it so far (02-item-anchored p4: the lines after the anchor
        /// go round the box); null when nothing limits its columns.
        /// </summary>
        public TextArea? Area
        {
            get
            {
                var frame = Frame;
                if (anchoredWraps.Length == 0)
                {
                    return frame.TextArea;
                }
                if (!ReferenceEquals(areaCache.Frame, frame) || !ReferenceEquals(areaCache.Wraps, anchoredWraps))
                {
                    var index = Math.Min(FrameIndex, frames.Count - 1);
                    var placed = anchoredWraps.Where(wrap => wrap.Frame == index).Select(wrap => wrap.Region).ToList();
                    areaCache = (frame, anchoredWraps, placed.Count == 0 ? frame.TextArea : new TextArea([.. frame.TextArea?.Regions ?? [], .. placed]));
                }
                return areaCache.Area;
            }
        }

        /// <summary>Text wrap of an anchored object placed in the current frame: it wraps the lines placed after it.</summary>
        public void AddWrap(WrapRegion region) => anchoredWraps = [.. anchoredWraps, (Math.Min(FrameIndex, frames.Count - 1), region)];

        private sealed record FilledColumn(int FrameIndex, int ColumnIndex, List<DrawOp> Ops, double UsedBottom, int FirstLine);

        /// <summary>
        /// How the current frame's columns are divided (immutable, so that a <see cref="State"/> can hold it): where
        /// each column of the current band starts (<paramref name="Tops"/>) and whether that is below a paragraph that
        /// spans columns, where the first line is a leading below it, not at the first-baseline offset
        /// (<paramref name="LeadingTops"/>); the columns the current paragraph spans (<paramref name="Span"/>, 0 for
        /// none) and where it starts; the sub-columns of the current split paragraph (<paramref name="Split"/>).
        /// </summary>
        public sealed record Bands(int Id, double[] Tops, bool[] LeadingTops, int Span, double SpanTop, bool SpanLeadingTop, SplitColumns? Split);

        /// <summary>
        /// A split paragraph's <paramref name="Count"/> sub-columns, the one in use (<paramref name="Index"/>), their
        /// top, and the column (<see cref="ColumnOrdinal"/>) the paragraph started in.
        /// </summary>
        public sealed record SplitColumns(int Id, int Count, double Inside, double Outside, int Index, double Top, bool LeadingTop, int StartOrdinal);

        /// <summary>Everything needed to go back to a point of the layout.</summary>
        public readonly record struct State(
            int FrameIndex, int ColumnIndex, int ColumnOrdinal, bool Exhausted, bool AtColumnTop, double Baseline, double LastDescent,
            double PendingSpace, double UsedBottom, int ContentPage, int FilledColumns, int ColumnOps, int Lines, bool Overset,
            int ColumnLines, Bands Bands, double BandBottom, double SplitBottom, int Ids, (int Id, bool OneColumn)? LastSplit,
            (double Baseline, double Top, int Next)? Pending, (int Frame, WrapRegion Region)[] AnchoredWraps, NoteState Notes);

        public Flow(IReadOnlyList<FrameArea> frames, StoryLayout layout)
        {
            this.frames = frames;
            Layout = layout;
            foreach (var frame in frames)
            {
                layout.OpsByFrame.TryAdd(frame.FrameId, []);
            }
            bands = FrameBands();
            AtColumnTop = true;
            Baseline = Column.Y;
        }

        public StoryLayout Layout { get; }

        public int FrameIndex { get; private set; }

        public int ColumnIndex { get; private set; }

        /// <summary>How many columns the text has moved on from the first: the same number means the same column.</summary>
        public int ColumnOrdinal { get; private set; }

        public bool Exhausted { get; private set; }

        public bool AtColumnTop { get; set; }

        public double Baseline { get; private set; }

        public double LastDescent { get; private set; }

        public double PendingSpace { get; set; }

        /// <summary>Where the last line placed went (<see cref="Place"/>).</summary>
        public Slot Line { get; private set; }

        /// <summary>The current band of the frame's columns: a new frame and the end of a spanning paragraph start one.</summary>
        public int BandId => bands.Id;

        /// <summary>Text has been placed in the current band.</summary>
        public bool BandHasText => !double.IsNaN(bandBottom);

        /// <summary>The last split paragraph ended: its id, and whether all of it stayed in the column it began in.</summary>
        public (int Id, bool OneColumn)? LastSplit { get; private set; }

        public FrameArea Frame => frames[Math.Min(FrameIndex, frames.Count - 1)];

        /// <summary>
        /// Where the next line goes: the frame's column below the band's top, the columns a paragraph spans, or a
        /// split paragraph's sub-column (its column less the outside gutters and the inside gutters between them,
        /// shared equally: 06-frame-span-balance p4 splits a 527.5 pt column into two of 251.75 pt, 6 pt in from each
        /// side and 12 pt apart).
        /// </summary>
        public Rect Column
        {
            get
            {
                var columns = Frame.Columns;
                if (columns.Count == 0)
                {
                    return default;
                }
                var index = Math.Min(ColumnIndex, columns.Count - 1);
                var column = columns[index];
                if (bands.Span > 0)
                {
                    var last = columns[Math.Min(bands.Span, columns.Count) - 1];
                    return new Rect(columns[0].X, bands.SpanTop, last.Right - columns[0].X, columns[0].Bottom - bands.SpanTop);
                }
                if (bands.Split is { } split)
                {
                    var width = (column.Width - (2 * split.Outside) - ((split.Count - 1) * split.Inside)) / split.Count;
                    return new Rect(column.X + split.Outside + (split.Index * (width + split.Inside)), split.Top, width, column.Bottom - split.Top);
                }
                var top = bands.Tops[index];
                return new Rect(column.X, top, column.Width, column.Bottom - top);
            }
        }

        /// <summary>The top and bottom of the current frame's columns, between which a balanced band ends.</summary>
        public (double Top, double Bottom) FrameExtent =>
            Frame.Columns.Count == 0 ? (0, 0) : (Frame.Columns.Min(column => column.Y), Frame.Columns.Max(column => column.Bottom));

        public string PageName => Frame.PageName;

        // The first line of the current column is a leading below its top (below a span, or a split paragraph that
        // starts below other text), not at the frame's first-baseline offset.
        private bool LeadingTop =>
            bands.Split?.LeadingTop
            ?? (bands.Span > 0 ? bands.SpanLeadingTop : bands.LeadingTops.Length > 0 && bands.LeadingTops[Math.Min(ColumnIndex, bands.LeadingTops.Length - 1)]);

        public void Add(DrawOp op) => columnOps.Add(op);

        /// <summary>How many ops the current column holds: where the ops of the next line start.</summary>
        public int OpCount => columnOps.Count;

        /// <summary>Puts <paramref name="op"/> under the ops from <paramref name="index"/> on (painted before them).</summary>
        public void Insert(int index, DrawOp op) => columnOps.Insert(index, op);

        /// <summary>Lines of band (or split paragraph) <paramref name="id"/> keep their baselines above <paramref name="bottom"/>.</summary>
        public void SetLimit(int id, double bottom) => limits[id] = bottom;

        public void ClearLimit(int id) => limits.Remove(id);

        /// <summary>
        /// Positions the next line; false when the text no longer fits anywhere. With <paramref name="snapToGrid"/>
        /// the baseline that leading, space before/after or the first-baseline offset give moves down to the frame's
        /// baseline grid, and the next line's leading counts from there (06-frame-baseline-grid: 12 pt leading on a
        /// 14 pt grid, lines 14 pt apart; 7 pt space after with 14 pt leading, paragraphs 28 pt apart; a 22/26
        /// headline's first baseline down to the grid line below it). Whether it still fits is decided
        /// after the snap. Below a spanning paragraph every column's first line is a leading below the span's last
        /// baseline and its space after (06-frame-span-balance p1: the 22/8 pt headline's baseline at 79.4, the
        /// 12.5 pt body lines of all three columns at 99.9). In a frame with a <see cref="FrameArea.TextArea"/> the line goes
        /// where text wrap leaves a stretch at least <paramref name="least"/> wide (see <see cref="Wrap"/>); <see cref="Line"/>
        /// is where it went.
        /// </summary>
        public bool Place(LineMetrics metrics, bool snapToGrid, double least = 0)
        {
            if (PlacePending(metrics, least))
            {
                return true;
            }
            while (!Exhausted)
            {
                var column = Column;
                double baseline;
                if (metrics.AboveLine != 0)
                {
                    // Above-line objects stack between the line before (or the column top) and this one, which keeps
                    // its leading below them (see AboveLineRoom).
                    baseline = (AtColumnTop ? column.Y : Baseline + PendingSpace) + metrics.AboveLine + metrics.Leading;
                }
                else if (AtColumnTop && LeadingTop)
                {
                    baseline = column.Y + metrics.Leading;
                }
                else if (AtColumnTop)
                {
                    var offset = Frame.FirstBaselineOffset switch
                    {
                        "CapHeight" => metrics.CapHeight,
                        "LeadingOffset" => metrics.Leading,
                        "XHeight" => metrics.XHeight,
                        "EmboxHeight" => metrics.MaxSize * 0.88,
                        "FixedHeight" => Frame.MinimumFirstBaselineOffset,
                        _ => metrics.Ascent,
                    };
                    // A rule above kept in the frame stays below the column's top (08-para-rules p5).
                    baseline = column.Y + Math.Max(Math.Max(offset, Frame.MinimumFirstBaselineOffset), metrics.KeptRuleAbove);
                }
                else
                {
                    baseline = Baseline + PendingSpace + metrics.Leading;
                }
                if (snapToGrid && Frame.BaselineGrid is { } grid)
                {
                    baseline = grid.Snap(baseline);
                }
                // InDesign keeps a line while its baseline is inside the column; descenders may hang below the frame.
                if (baseline > Bottom(column) + Epsilon && !(AtColumnTop && !LeadingTop && column.Height <= 0))
                {
                    if (!NextColumn())
                    {
                        return false;
                    }
                    continue;
                }
                var line = new Slot(0, column.Width, false);
                if (Area is { } area)
                {
                    // The band a line is tested over starts at the baseline before it, or at the column's top.
                    var rise = AtColumnTop ? baseline - column.Y : metrics.Leading + metrics.AboveLine;
                    if (Wrap(area, column, ref baseline, rise, metrics, snapToGrid, least) is not { } wrapped)
                    {
                        if (!NextColumn())
                        {
                            return false;
                        }
                        continue;
                    }
                    line = wrapped;
                }
                Commit(baseline, line, metrics);
                return true;
            }
            return false;
        }

        // The line is placed on `baseline`, in `line`.
        private void Commit(double baseline, Slot line, LineMetrics metrics)
        {
            Line = line;
            Baseline = baseline;
            LastDescent = metrics.Descent;
            usedBottom = baseline;
            AtColumnTop = false;
            PendingSpace = 0;
            contentPage = Frame.PageIndex;
            bandBottom = Deepest(bandBottom, baseline);
            if (bands.Split is not null)
            {
                splitBottom = Deepest(splitBottom, baseline);
            }
        }

        /// <summary>
        /// The first stretch at or below <paramref name="baseline"/> in <paramref name="column"/> that text wrap leaves
        /// at least <paramref name="least"/> wide (any stretch when nothing wraps the band), for a line whose band
        /// rises <paramref name="rise"/> above its baseline; null when the column has none (or a Jump to Next Column
        /// region sends the line on). A line that finds no room moves down a leading (02-item-text-wrap p5: below the
        /// jumped object the lines stay on the leading's steps, 276.9 rather than right below its 256.5 pt edge); the
        /// first line of a column moves down 1 pt at a time (06-frame-shapes: the oval's first baseline 1 pt below
        /// the rectangle's, the triangle's 15 pt, the hexagon's 5 pt, where the first word first fits). Stretches
        /// right of the one taken are kept for the next lines on the same baseline (06-frame-shapes p2 fills both
        /// legs of the star).
        /// </summary>
        private Slot? Wrap(TextArea area, Rect column, ref double baseline, double rise, LineMetrics metrics, bool snapToGrid, double least)
        {
            var step = AtColumnTop ? 1 : Math.Max(1, metrics.Leading + metrics.AboveLine);
            while (true)
            {
                var top = baseline - rise;
                switch (area.Pieces(column, top, baseline, pieces))
                {
                    case BandWrap.Clear:
                        return new Slot(0, column.Width, false);
                    case BandWrap.NextColumn:
                        return null;
                }
                var found = pieces.FindIndex(piece => piece.Right - piece.Left + Epsilon >= least);
                if (found >= 0)
                {
                    var (left, right) = pieces[found];
                    pending = found + 1 < pieces.Count ? (baseline, top, found + 1) : null;
                    return new Slot(left - column.X, right - left, true);
                }
                baseline += step;
                if (snapToGrid && Frame.BaselineGrid is { } grid)
                {
                    baseline = grid.Snap(baseline);
                }
                if (baseline > Bottom(column) + Epsilon)
                {
                    return null;
                }
            }
        }

        // The next stretch on the baseline of the line before, when text wrap left more than one there.
        private bool PlacePending(LineMetrics metrics, double least)
        {
            if (pending is not { } rest || Area is not { } area || PendingSpace != 0 || metrics.AboveLine != 0)
            {
                pending = null;
                return false;
            }
            pending = null;
            var column = Column;
            area.Pieces(column, rest.Top, rest.Baseline, pieces);
            for (var i = rest.Next; i < pieces.Count; i++)
            {
                var (left, right) = pieces[i];
                if (right - left + Epsilon >= least)
                {
                    pending = i + 1 < pieces.Count ? (rest.Baseline, rest.Top, i + 1) : null;
                    Commit(rest.Baseline, new Slot(left - column.X, right - left, true), metrics);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Where a line with <paramref name="metrics"/> would go (see <see cref="Place"/>), without placing it.</summary>
        public Slot? Find(LineMetrics metrics, bool snapToGrid, double least)
        {
            var state = Save();
            var line = Place(metrics, snapToGrid, least) ? Line : (Slot?)null;
            Restore(state);
            return line;
        }

        /// <summary>
        /// Where the next lines with <paramref name="metrics"/> would go: the first at least <paramref name="least"/>
        /// wide, then every stretch after it, on into the next columns, up to <paramref name="count"/> stretches or until
        /// they are <paramref name="width"/> wide together.
        /// </summary>
        public List<Slot> Peek(LineMetrics metrics, bool snapToGrid, double least, int count, double width)
        {
            var state = Save();
            var slots = new List<Slot>();
            var total = 0.0;
            while (slots.Count < count && total < width && Place(metrics, snapToGrid, slots.Count == 0 ? least : 0))
            {
                slots.Add(Line);
                total += Line.Width;
            }
            Restore(state);
            return slots;
        }

        // The column's bottom, or the lowest baseline its band or split paragraph is limited to.
        private double Bottom(Rect column)
        {
            var bottom = column.Bottom;
            if (bands.Span == 0 && limits.TryGetValue(bands.Id, out var band))
            {
                bottom = Math.Min(bottom, band);
            }
            if (bands.Split is { } split && limits.TryGetValue(split.Id, out var sub))
            {
                bottom = Math.Min(bottom, sub);
            }
            return Math.Min(bottom, NoteFloor());
        }

        private static double Deepest(double bottom, double baseline) => double.IsNaN(bottom) ? baseline : Math.Max(bottom, baseline);

        public void BeginBlock()
        {
            PendingSpace = 0;
            pending = null;
        }

        /// <summary>A block's rows (a table's) now take up the column and its page.</summary>
        public void PlaceBlock()
        {
            AtColumnTop = false;
            contentPage = Frame.PageIndex;
        }

        /// <summary>After a table: the next line is placed a leading below <paramref name="bottom"/>.</summary>
        public void EndBlock(double bottom)
        {
            pending = null;
            Baseline = bottom;
            LastDescent = 0;
            usedBottom = Math.Max(usedBottom, bottom);
            AtColumnTop = false;
            contentPage = Frame.PageIndex;
            bandBottom = Deepest(bandBottom, bottom);
        }

        public bool NextColumn()
        {
            pending = null;
            if (Exhausted)
            {
                return false;
            }
            if (bands.Split is { } split && split.Index + 1 < split.Count)
            {
                // The next sub-column of a split paragraph, in the same column of the frame.
                bands = bands with { Split = split with { Index = split.Index + 1 } };
                AtColumnTop = true;
                PendingSpace = 0;
                Baseline = split.Top;
                return true;
            }
            PushColumn();
            ColumnOrdinal++;
            // A spanning paragraph takes every column of its band: it goes on in the next frame.
            ColumnIndex = bands.Span > 0 ? Frame.Columns.Count : ColumnIndex + 1;
            if (ColumnIndex >= Frame.Columns.Count)
            {
                ColumnIndex = 0;
                FrameIndex++;
                while (FrameIndex < frames.Count && frames[FrameIndex].Columns.Count == 0)
                {
                    FrameIndex++;
                }
                if (FrameIndex >= frames.Count)
                {
                    FrameIndex = frames.Count - 1;
                    Exhausted = true;
                    return false;
                }
                bands = FrameBands() with { Span = bands.Span, SpanTop = Frame.Columns[0].Y, Split = bands.Split };
                bandBottom = double.NaN;
            }
            if (bands.Split is { } continued)
            {
                // A split paragraph goes on in the sub-columns of the next column.
                bands = bands with { Split = continued with { Index = 0, Top = bands.Tops[ColumnIndex], LeadingTop = bands.LeadingTops[ColumnIndex] } };
                splitBottom = double.NaN;
            }
            AtColumnTop = true;
            PendingSpace = 0;
            Baseline = Column.Y;
            usedBottom = Column.Y;
            PlaceCarriedNotes();
            return true;
        }

        // A frame's first band: its columns from their tops.
        private Bands FrameBands()
        {
            var columns = Frame.Columns;
            return new Bands(++ids, [.. columns.Select(column => column.Y)], new bool[columns.Count], 0, 0, false, null);
        }

        private void PushColumn()
        {
            pending = null;
            filled.Add(new FilledColumn(FrameIndex, ColumnIndex, columnOps, usedBottom, columnLines));
            columnOps = [];
            columnLines = Layout.Lines.Count;
        }

        /// <summary>
        /// Starts a paragraph that spans <paramref name="count"/> columns of the frame from the first (0: all of them):
        /// at the top of the band when nothing is in it yet, else below its lowest line, at least
        /// <paramref name="spaceBefore"/> from it (06-frame-span-balance p2: the 22 pt headline's baseline 22 pt
        /// below the last line of the balanced columns above). False when there is nothing to span (one column): the
        /// paragraph is laid out as any other.
        /// </summary>
        public bool BeginSpan(int count, double spaceBefore)
        {
            var columns = Frame.Columns.Count;
            var span = count <= 0 ? columns : Math.Min(count, columns);
            if (Exhausted || span < 2)
            {
                return false;
            }
            var (top, leadingTop) = (bands.Tops[0], bands.LeadingTops[0]);
            if (BandHasText)
            {
                (top, leadingTop) = (bandBottom, true);
                Baseline = bandBottom;
                AtColumnTop = false;
                PendingSpace = Math.Max(PendingSpace, spaceBefore);
            }
            PushColumn();
            ColumnIndex = 0;
            usedBottom = top;
            bands = bands with { Span = span, SpanTop = top, SpanLeadingTop = leadingTop };
            return true;
        }

        /// <summary>
        /// Ends a spanning paragraph: the columns it spanned start a new band below its last line and its space after
        /// (at least <paramref name="spaceAfter"/>), the text going on in the first; the columns beside it keep
        /// starting where it started (06-frame-span-balance p3, a headline across two of three columns).
        /// </summary>
        public void EndSpan(double spaceAfter)
        {
            if (bands.Span == 0 || Exhausted)
            {
                return;
            }
            PushColumn();
            var top = Baseline + Math.Max(PendingSpace, spaceAfter);
            var tops = (double[])bands.Tops.Clone();
            var leadingTops = (bool[])bands.LeadingTops.Clone();
            for (var i = 0; i < tops.Length; i++)
            {
                (tops[i], leadingTops[i]) = i < bands.Span ? (top, true) : (bands.SpanTop, bands.SpanLeadingTop);
            }
            bands = new Bands(++ids, tops, leadingTops, 0, 0, false, null);
            bandBottom = double.NaN;
            ColumnIndex = 0;
            AtColumnTop = true;
            PendingSpace = 0;
            Baseline = top;
            usedBottom = top;
        }

        /// <summary>
        /// Starts a paragraph split into <paramref name="count"/> sub-columns of the current column, from where its
        /// next line would go (a leading below the text above, space before included): in 06-frame-span-balance p4
        /// both sub-columns' first lines are 20.5 pt (the headline's 8 pt space after, 12.5 pt leading) below the
        /// headline.
        /// </summary>
        public bool BeginSplit(int count, double inside, double outside)
        {
            if (Exhausted || count < 2 || Frame.Columns.Count == 0)
            {
                return false;
            }
            var (top, leadingTop) = AtColumnTop ? (Column.Y, LeadingTop) : (Baseline + PendingSpace, true);
            bands = bands with { Split = new SplitColumns(++ids, count, inside, outside, 0, top, leadingTop, ColumnOrdinal) };
            splitBottom = double.NaN;
            AtColumnTop = true;
            PendingSpace = 0;
            Baseline = top;
            return true;
        }

        /// <summary>Ends a split paragraph: the text goes on below its lowest sub-column (06-frame-span-balance p4).</summary>
        public void EndSplit()
        {
            if (bands.Split is not { } split)
            {
                return;
            }
            LastSplit = (split.Id, split.StartOrdinal == ColumnOrdinal);
            bands = bands with { Split = null };
            if (!double.IsNaN(splitBottom))
            {
                Baseline = splitBottom;
                AtColumnTop = false;
            }
        }

        /// <summary>
        /// Moves to where a paragraph with Start Paragraph <paramref name="start"/> begins: the next column, the next
        /// frame, the first frame on the next page, or on the next odd or even page (08-para-keeps-breaks p4 to p9; the
        /// story's frames decide where that is: there p7's continuation frame is on p9, and p8 stays blank). A
        /// paragraph already at the top of a column (frame, page) the story has put nothing in stays there.
        /// A paragraph starts on a baseline of its own: stretches text wrap leaves right of the last line before it stay
        /// empty (02-item-text-wrap p4: below the star the next paragraph starts on the next line, not in the notch).
        /// </summary>
        public void StartParagraph(string start)
        {
            pending = null;
            switch (start)
            {
                case "NextColumn":
                    if (!AtColumnTop)
                    {
                        NextColumn();
                    }
                    break;
                case "NextFrame":
                    while (!Exhausted && !(AtColumnTop && ColumnIndex == 0))
                    {
                        NextFrame();
                    }
                    break;
                case "NextPage" or "NextOddPage" or "NextEvenPage":
                    while (!Exhausted && !(AtColumnTop && ColumnIndex == 0 && contentPage != Frame.PageIndex
                        && (start == "NextPage" || Frame.OddPage == (start == "NextOddPage"))))
                    {
                        NextFrame();
                    }
                    break;
            }
        }

        private void NextFrame()
        {
            while (NextColumn() && ColumnIndex != 0)
            {
            }
        }

        public State Save() => new(FrameIndex, ColumnIndex, ColumnOrdinal, Exhausted, AtColumnTop, Baseline, LastDescent, PendingSpace, usedBottom,
            contentPage, filled.Count, columnOps.Count, Layout.Lines.Count, Layout.Overset, columnLines, bands, bandBottom, splitBottom, ids, LastSplit, pending, anchoredWraps, notes);

        /// <summary>Goes back to <paramref name="state"/>, dropping everything laid out since.</summary>
        public void Restore(State state)
        {
            if (filled.Count > state.FilledColumns)
            {
                columnOps = filled[state.FilledColumns].Ops;
                filled.RemoveRange(state.FilledColumns, filled.Count - state.FilledColumns);
            }
            columnOps.RemoveRange(state.ColumnOps, columnOps.Count - state.ColumnOps);
            Layout.Lines.RemoveRange(state.Lines, Layout.Lines.Count - state.Lines);
            Layout.Overset = state.Overset;
            (FrameIndex, ColumnIndex, ColumnOrdinal, Exhausted, AtColumnTop) = (state.FrameIndex, state.ColumnIndex, state.ColumnOrdinal, state.Exhausted, state.AtColumnTop);
            (Baseline, LastDescent, PendingSpace, usedBottom, contentPage) = (state.Baseline, state.LastDescent, state.PendingSpace, state.UsedBottom, state.ContentPage);
            (columnLines, bands, bandBottom, splitBottom, ids, LastSplit, pending, anchoredWraps) = (state.ColumnLines, state.Bands, state.BandBottom, state.SplitBottom, state.Ids, state.LastSplit, state.Pending, state.AnchoredWraps);
            // No line is being placed at a saved point.
            (notes, lineNotes) = (state.Notes, 0);
        }

        /// <summary>
        /// Moves every column's ops into its frame, applying vertical justification; custom-positioned objects go after
        /// the frame's text (<see cref="PageItemOp.AfterText"/>).
        /// </summary>
        public void Finish()
        {
            PushColumn();
            var afterText = new List<(List<DrawOp> Target, DrawOp Op)>();
            for (var i = 0; i < filled.Count; i++)
            {
                FinishColumn(filled[i], i + 1 < filled.Count ? filled[i + 1].FirstLine : Layout.Lines.Count, afterText);
            }
            DrawNotes();
            foreach (var (target, op) in afterText)
            {
                target.Add(op);
            }
            filled.Clear();
        }

        // Moves a column's ops into its frame; its lines are Layout.Lines from its FirstLine to endLine.
        private void FinishColumn(FilledColumn filledColumn, int endLine, List<(List<DrawOp> Target, DrawOp Op)> afterText)
        {
            var (frameIndex, columnIndex, ops, used, firstLine) = filledColumn;
            if (ops.Count == 0)
            {
                return;
            }
            var frame = frames[Math.Min(frameIndex, frames.Count - 1)];
            var column = frame.Columns.Count == 0 ? default : frame.Columns[Math.Min(columnIndex, frame.Columns.Count - 1)];
            var free = NoteFloor(frameIndex, columnIndex, column.Bottom) - used;
            var dy = frame.VerticalJustification switch
            {
                "CenterAlign" => free / 2,
                "BottomAlign" => free,
                _ => 0,
            };
            var target = Layout.OpsByFrame[frame.FrameId];
            foreach (var op in ops)
            {
                var moved = dy > 0 && op is not PageItemOp { FixedInFrame: true } ? op.Offset(0, dy) : op;
                if (op is PageItemOp { AfterText: true })
                {
                    afterText.Add((target, moved));
                }
                else
                {
                    target.Add(moved);
                }
            }
            if (dy > 0)
            {
                for (var i = firstLine; i < endLine; i++)
                {
                    Layout.Lines[i] = Layout.Lines[i] with { Baseline = Layout.Lines[i].Baseline + dy };
                }
            }
        }
    }
}
