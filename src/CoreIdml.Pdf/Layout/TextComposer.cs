using System.Globalization;
using System.Text;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Fonts;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// Lays out story text into threaded frames and columns, the way InDesign's single-line composer would:
/// greedy line breaking at spaces and hyphens, leading (fixed or Auto = 120%), first-baseline offset, space
/// before/after (no space before at the top of a column), indents, tabs, alignment and justification (word spacing),
/// vertical justification, keep options and Start Paragraph (a forbidden column break moves to an earlier line,
/// laying the text out again from there), and inline tables (rows flow across columns and frames, header rows repeat).
/// Not done: the paragraph composer, optical kerning, ligatures, text wrap, anchored objects.
/// Deterministic: same input, same output.
/// </summary>
internal sealed class TextComposer
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

    public TextComposer(IdmlDocument document, StoryReader reader, FontLibrary fonts)
    {
        this.document = document;
        this.reader = reader;
        this.fonts = fonts;
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
        var flow = new Flow(frames, layout);
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
        var index = 0;
        for (; index < paragraphs.Count; index++)
        {
            states[index] = flow.Save();
            var linesBefore = layout.Lines.Count;
            var lines = LayoutParagraph(paragraphs[index], flow, isFirst: index == 0, index, forcedBreaks);
            paragraphFrames[index] = layout.Lines.Count > linesBefore ? layout.Lines[linesBefore].FrameIndex : -1;
            columns[index] = paragraphs[index].Items.Any(item => item is TableItem) ? null : lines;
            if (budget > 0 && EarlierBreak(paragraphs, columns, index, forcedBreaks) is { } moved)
            {
                budget--;
                flow.Restore(states[moved.Paragraph]);
                forcedBreaks.RemoveWhere(at => at.Paragraph > moved.Paragraph || (at.Paragraph == moved.Paragraph && at.Line > moved.Line));
                forcedBreaks.Add(moved);
                index = moved.Paragraph - 1;
                continue;
            }
            if (flow.Exhausted)
            {
                break;
            }
        }
        // Paragraphs after the one that ran out of room were not laid out.
        layout.ParagraphFrames.AddRange(paragraphFrames.Take(Math.Min(index + 1, paragraphs.Count)));
        if (flow.Exhausted)
        {
            layout.Overset = true;
            layout.OversetCharacters += paragraphs.Skip(index + 1).Sum(p => p.Items.OfType<TextItem>().Where(t => !t.Generated).Sum(t => t.Text.Length));
        }
        layout.LastDescent = flow.LastDescent;
        flow.Finish();
        return layout;
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

    private List<Shaped> Shape(ParagraphModel paragraph, FrameArea frame) => Shape(paragraph.Items, paragraph.Format, frame);

    private List<Shaped> Shape(IReadOnlyList<InlineItem> items, ParagraphFormatting format, FrameArea frame)
    {
        var result = new List<Shaped>();
        foreach (var item in items)
        {
            var first = result.Count;
            switch (item)
            {
                case TextItem text:
                    ShapeText(text.Text, text.Style, result, text.Generated ? null : format.Hyphenation);
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
                    result.Add(new Shaped
                    {
                        Kind = GlyphKind.Inline, Style = inline.Style, Font = inline.Style.Font, Size = inline.Style.Size, Text = ObjectReplacement,
                        Advance = inline.Bounds.Width, Natural = inline.Bounds.Width, Rise = inline.YOffset, Object = inline,
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
        return result;
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
                var end = start;
                while (end < text.Length && char.IsLetter(text[end]))
                {
                    end++;
                }
                Hyphenator.AddPoints(text.AsSpan(start, end - start), language, hyphenation, start, hyphens);
                start = end;
            }
        }
        var nextHyphen = 0;
        var firstShaped = output.Count;
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
                    size *= 0.583;
                    rise += style.Size * 0.333;
                    break;
                case "Subscript" or "OTSubscript":
                    size *= 0.583;
                    rise -= style.Size * 0.333;
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
        var glyphs = Shape(paragraph, flow.Frame);
        flow.PendingSpace += isFirst ? 0 : format.SpaceBefore;
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
                continue;
            }
            if (columns.Count > 0 && forcedBreaks.Contains((number, columns.Count)) && !flow.AtColumnTop)
            {
                flow.NextColumn();
            }
            if (lineStyles is not null && index < glyphs.Count && glyphs[index].Item is { } source
                && lineStyles.LineStyle(columns.Count) is var wanted && wanted != lineStyle)
            {
                var rest = Shape(lineStyles.ItemsFrom(source, glyphs[index].Index, wanted), format, flow.Frame);
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
            var indent = format.LeftIndent + (firstLine ? format.FirstLineIndent : 0);
            var available = column.Width - indent - format.RightIndent;
            var (end, forced) = FitLine(glyphs, index, available, format, indent);
            var line = glyphs.GetRange(index, end - index);
            index = end;
            emitted = true;
            var snapToGrid = format.AlignToBaselineGrid && (firstLine || !format.GridAlignFirstLineOnly);
            firstLine = false;
            var isLast = index >= glyphs.Count || glyphs[index].Kind == GlyphKind.Table;
            var metrics = Measure(line, paragraph.MarkStyle);
            if (!flow.Place(metrics, snapToGrid))
            {
                columns.Add(int.MaxValue);
                continue;
            }
            columns.Add(flow.ColumnOrdinal);
            EmitLine(line, flow, format, indent, available, isLast, forced);
        }
        flow.PendingSpace = format.SpaceAfter;
        return columns;
    }

    /// <summary>Greedy fit: the end index (exclusive) of the line starting at <paramref name="start"/>.</summary>
    private static (int End, bool Forced) FitLine(List<Shaped> glyphs, int start, double available, ParagraphFormatting format, double indent)
    {
        double x = 0, content = 0;
        var lastBreak = -1;
        // The last break at a space (or hard hyphen) and the line it would give, to weigh against hyphenating.
        var lastWordBreak = -1;
        double wordBreakContent = 0, spaceWidth = 0;
        var wordBreakSpaces = 0;
        var spaces = 0;
        for (var i = start; i < glyphs.Count; i++)
        {
            var glyph = glyphs[i];
            switch (glyph.Kind)
            {
                case GlyphKind.Break:
                    return (i + 1, true);
                case GlyphKind.Table:
                    return (i, false);
                case GlyphKind.Tab:
                    // Right, centre and decimal stops count the text after them, so a right tab at the column's edge
                    // (a table of contents) keeps its page number on the line.
                    glyph.Advance = Math.Max(0.1, TabAdvance(glyphs, i, x, format, indent, available).Advance);
                    break;
            }
            var width = x + glyph.Advance;
            var contentWidth = glyph.Kind == GlyphKind.Space ? content : width;
            if (glyph.Kind == GlyphKind.SoftHyphen)
            {
                contentWidth = content;
            }
            if (contentWidth > available + Epsilon && i > start)
            {
                if (lastBreak >= start && glyphs[lastBreak].Kind == GlyphKind.SoftHyphen && lastWordBreak >= start
                    && !NeedsHyphen(format, available - wordBreakContent, wordBreakSpaces, spaceWidth))
                {
                    return (lastWordBreak + 1, false);
                }
                return (lastBreak >= start ? lastBreak + 1 : i, false);
            }
            x = width;
            content = contentWidth;
            if (glyph.Kind == GlyphKind.Space)
            {
                spaces++;
                spaceWidth = glyph.Advance;
            }
            if (glyph.BreakAfter && (glyph.Kind != GlyphKind.SoftHyphen || content + HyphenWidth(glyph) <= available + Epsilon))
            {
                lastBreak = i;
                if (glyph.Kind != GlyphKind.SoftHyphen)
                {
                    lastWordBreak = i;
                    wordBreakContent = content;
                    wordBreakSpaces = Math.Max(0, spaces - 1);
                }
            }
        }
        return (glyphs.Count, false);
    }

    /// <summary>
    /// Whether breaking at the last space would leave the line so loose that hyphenating is better: justified text
    /// when word spaces would grow past 133% (InDesign's default maximum word spacing), ragged text when the gap
    /// exceeds the hyphenation zone (36 pt by default).
    /// </summary>
    private static bool NeedsHyphen(ParagraphFormatting format, double gap, int spaces, double spaceWidth)
    {
        var justified = format.Justification is "LeftJustified" or "CenterJustified" or "RightJustified" or "FullyJustified";
        if (justified)
        {
            return spaces == 0 || gap / spaces > spaceWidth * 0.33;
        }
        return gap > format.Hyphenation.Zone;
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

    private sealed record LineMetrics(double Ascent, double Descent, double Leading, double CapHeight, double XHeight, double MaxSize);

    private static LineMetrics Measure(List<Shaped> line, RunStyle markStyle)
    {
        double ascent = 0, descent = 0, leading = 0, cap = 0, xHeight = 0, size = 0;
        var any = false;
        foreach (var glyph in line)
        {
            if (glyph.Kind is GlyphKind.Table)
            {
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
            ascent = Math.Max(ascent, (glyph.Style.Font.Ascent * glyph.Size) + Math.Max(0, glyph.Rise));
            descent = Math.Max(descent, (glyph.Style.Font.Descent * glyph.Size) - Math.Min(0, glyph.Rise));
            leading = Math.Max(leading, glyph.Style.EffectiveLeading);
            cap = Math.Max(cap, glyph.Style.Font.CapHeight * glyph.Size);
            xHeight = Math.Max(xHeight, glyph.Style.Font.XHeight * glyph.Size);
            size = Math.Max(size, glyph.Size);
        }
        if (!any)
        {
            var font = markStyle.Font;
            return new LineMetrics(font.Ascent * markStyle.Size, font.Descent * markStyle.Size, markStyle.EffectiveLeading, font.CapHeight * markStyle.Size, font.XHeight * markStyle.Size, markStyle.Size);
        }
        return new LineMetrics(ascent, descent, leading, cap, xHeight, size);
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

    private void EmitLine(List<Shaped> line, Flow flow, ParagraphFormatting format, double indent, double available, bool isLast, bool forced)
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

        var width = visible.Sum(glyph => glyph.Advance);
        var justification = format.Justification;
        var justify = justification == "FullyJustified"
            || (justification is "LeftJustified" or "CenterJustified" or "RightJustified" && (!isLast || forced));
        var align = justification switch
        {
            "CenterAlign" or "CenterJustified" => "center",
            "RightAlign" or "RightJustified" or "AwayFromBindingSide" => "right",
            _ => "left",
        };
        var extraPerSpace = 0.0;
        var spaces = visible.Count(Stretches);
        if (justify && spaces > 0 && width < available && !visible.Any(glyph => glyph.Kind == GlyphKind.Tab))
        {
            extraPerSpace = (available - width) / spaces;
            width = available;
        }
        var column = flow.Column;
        var x = column.X + indent + align switch
        {
            "center" => (available - width) / 2,
            "right" => available - width,
            _ => 0,
        };
        var baseline = flow.Baseline;
        var lineText = new StringBuilder();
        var runStart = x;
        var run = new List<PlacedGlyph>();
        Shaped? runFirst = null;

        void FlushRun()
        {
            if (runFirst is not null && run.Count > 0)
            {
                flow.Add(new GlyphRunOp(runFirst.Font, runFirst.Size, runFirst.Style.HorizontalScale, runFirst.Style.Fill, runStart, baseline - runFirst.Rise, run.ToList()) { VerticalScale = runFirst.Style.VerticalScale });
                var runWidth = run.Sum(glyph => glyph.Advance);
                if (runFirst.Style.Underline && runFirst.Style.Fill is { } color)
                {
                    var y = baseline - runFirst.Rise + (runFirst.Size * 0.1);
                    flow.Add(new LineOp(new Point(runStart, y), new Point(runStart + runWidth, y), runFirst.Size / 18, color));
                }
                if (runFirst.Style.StrikeThrough && runFirst.Style.Fill is { } strikeColor)
                {
                    var y = baseline - runFirst.Rise - (runFirst.Font.XHeight * runFirst.Size / 2);
                    flow.Add(new LineOp(new Point(runStart, y), new Point(runStart + runWidth, y), runFirst.Size / 18, strikeColor));
                }
            }
            run.Clear();
            runFirst = null;
        }

        var markerLength = 0;
        foreach (var glyph in visible)
        {
            if (glyph.Marker && markerLength == lineText.Length)
            {
                markerLength += glyph.Kind == GlyphKind.Tab ? 1 : glyph.Text.Length;
            }
            var advance = glyph.Advance + (Stretches(glyph) ? extraPerSpace : 0);
            if (glyph.Kind == GlyphKind.Tab)
            {
                FlushRun();
                // InDesign writes a tab as a space glyph where it starts: invisible, but text extraction (copy, search,
                // accessibility) then sees a word break there, also when a leader or the next column follows closely.
                if (glyph.Font.TryMap(' ', out var space))
                {
                    var spaceWidth = glyph.Font.Advance(space) * glyph.Size * glyph.Style.HorizontalScale;
                    flow.Add(new GlyphRunOp(glyph.Font, glyph.Size, glyph.Style.HorizontalScale, glyph.Style.Fill, x, baseline - glyph.Rise,
                        [new PlacedGlyph(space, " ", spaceWidth, spaceWidth)]) { VerticalScale = glyph.Style.VerticalScale });
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
            run.Add(new PlacedGlyph(glyph.Glyph, glyph.Text, advance, glyph.Natural));
            lineText.Append(glyph.Text);
            x += advance;
        }
        FlushRun();
        flow.Layout.Lines.Add(new LineInfo(flow.FrameIndex, flow.ColumnIndex, baseline, column.X + indent, width, lineText.ToString()) { MarkerLength = markerLength });
    }

    /// <summary>
    /// Whether justification widens this glyph: word spaces and (flexible) no-break spaces only. InDesign's fixed
    /// spaces (em, en, thin, ... often after a list's bullet) keep their width.
    /// </summary>
    private static bool Stretches(Shaped glyph) => glyph.Kind == GlyphKind.Space && glyph.Text is " " or " ";

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
            flow.Add(new GlyphRunOp(head.Font, head.Size, head.Style.HorizontalScale, head.Style.Fill, start, baseline - head.Rise, glyphs) { VerticalScale = head.Style.VerticalScale });
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
    /// The position in the frame/column sequence and the ops of the columns filled so far. Columns are moved into
    /// their frames only at the end (<see cref="Finish"/>), so that keep options can return to a saved
    /// <see cref="State"/> and lay the text out again.
    /// </summary>
    private sealed class Flow
    {
        private readonly IReadOnlyList<FrameArea> frames;
        private readonly List<FilledColumn> filled = [];
        private List<DrawOp> columnOps = [];
        private double usedBottom;
        // The page (FrameArea.PageIndex) of the last line or table placed: a page the story has not used yet is
        // where "start on next page" can begin without moving.
        private int contentPage = int.MinValue;

        private sealed record FilledColumn(int FrameIndex, int ColumnIndex, List<DrawOp> Ops, double UsedBottom);

        /// <summary>Everything needed to go back to a point of the layout.</summary>
        public readonly record struct State(
            int FrameIndex, int ColumnIndex, int ColumnOrdinal, bool Exhausted, bool AtColumnTop, double Baseline, double LastDescent,
            double PendingSpace, double UsedBottom, int ContentPage, int FilledColumns, int ColumnOps, int Lines, bool Overset);

        public Flow(IReadOnlyList<FrameArea> frames, StoryLayout layout)
        {
            this.frames = frames;
            Layout = layout;
            foreach (var frame in frames)
            {
                layout.OpsByFrame.TryAdd(frame.FrameId, []);
            }
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

        public FrameArea Frame => frames[Math.Min(FrameIndex, frames.Count - 1)];

        public Rect Column => Frame.Columns.Count == 0 ? default : Frame.Columns[Math.Min(ColumnIndex, Frame.Columns.Count - 1)];

        public string PageName => Frame.PageName;

        public void Add(DrawOp op) => columnOps.Add(op);

        /// <summary>
        /// Positions the next line; false when the text no longer fits anywhere. With <paramref name="snapToGrid"/>
        /// the baseline that leading, space before/after or the first-baseline offset give moves down to the frame's
        /// baseline grid, and the next line's leading counts from there (06-frame-baseline-grid: 12 pt leading on a
        /// 14 pt grid, lines 14 pt apart; 7 pt space after with 14 pt leading, paragraphs 28 pt apart; a 22/26
        /// headline's first baseline down to the grid line below it). Whether it still fits is decided
        /// after the snap.
        /// </summary>
        public bool Place(LineMetrics metrics, bool snapToGrid)
        {
            while (!Exhausted)
            {
                var column = Column;
                double baseline;
                if (AtColumnTop)
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
                    baseline = column.Y + Math.Max(offset, Frame.MinimumFirstBaselineOffset);
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
                if (baseline > column.Bottom + Epsilon && !(AtColumnTop && column.Height <= 0))
                {
                    if (!NextColumn())
                    {
                        return false;
                    }
                    continue;
                }
                Baseline = baseline;
                LastDescent = metrics.Descent;
                usedBottom = baseline;
                AtColumnTop = false;
                PendingSpace = 0;
                contentPage = Frame.PageIndex;
                return true;
            }
            return false;
        }

        public void BeginBlock() => PendingSpace = 0;

        /// <summary>A block's rows (a table's) now take up the column and its page.</summary>
        public void PlaceBlock()
        {
            AtColumnTop = false;
            contentPage = Frame.PageIndex;
        }

        /// <summary>After a table: the next line is placed a leading below <paramref name="bottom"/>.</summary>
        public void EndBlock(double bottom)
        {
            Baseline = bottom;
            LastDescent = 0;
            usedBottom = Math.Max(usedBottom, bottom);
            AtColumnTop = false;
            contentPage = Frame.PageIndex;
        }

        public bool NextColumn()
        {
            if (Exhausted)
            {
                return false;
            }
            filled.Add(new FilledColumn(FrameIndex, ColumnIndex, columnOps, usedBottom));
            columnOps = [];
            ColumnOrdinal++;
            ColumnIndex++;
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
            }
            AtColumnTop = true;
            PendingSpace = 0;
            Baseline = Column.Y;
            usedBottom = Column.Y;
            return true;
        }

        /// <summary>
        /// Moves to where a paragraph with Start Paragraph <paramref name="start"/> begins: the next column, the next
        /// frame, the first frame on the next page, or on the next odd or even page (08-para-keeps-breaks p4 to p9; the
        /// story's frames decide where that is: there p7's continuation frame is on p9, and p8 stays blank). A
        /// paragraph already at the top of a column (frame, page) the story has put nothing in stays there.
        /// </summary>
        public void StartParagraph(string start)
        {
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
            contentPage, filled.Count, columnOps.Count, Layout.Lines.Count, Layout.Overset);

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
        }

        /// <summary>Moves every column's ops into its frame, applying vertical justification.</summary>
        public void Finish()
        {
            filled.Add(new FilledColumn(FrameIndex, ColumnIndex, columnOps, usedBottom));
            columnOps = [];
            foreach (var column in filled)
            {
                FinishColumn(column);
            }
            filled.Clear();
        }

        private void FinishColumn(FilledColumn filledColumn)
        {
            var (frameIndex, columnIndex, ops, used) = filledColumn;
            if (ops.Count == 0)
            {
                return;
            }
            var frame = frames[Math.Min(frameIndex, frames.Count - 1)];
            var column = frame.Columns.Count == 0 ? default : frame.Columns[Math.Min(columnIndex, frame.Columns.Count - 1)];
            var free = column.Bottom - used;
            var dy = frame.VerticalJustification switch
            {
                "CenterAlign" => free / 2,
                "BottomAlign" => free,
                _ => 0,
            };
            var target = Layout.OpsByFrame[frame.FrameId];
            foreach (var op in ops)
            {
                target.Add(dy > 0 ? op.Offset(0, dy) : op);
            }
            if (dy > 0)
            {
                for (var i = 0; i < Layout.Lines.Count; i++)
                {
                    var line = Layout.Lines[i];
                    if (line.FrameIndex == frameIndex && line.Column == columnIndex)
                    {
                        Layout.Lines[i] = line with { Baseline = line.Baseline + dy };
                    }
                }
            }
        }
    }
}
