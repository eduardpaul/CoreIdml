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
/// vertical justification, and inline tables (rows flow across columns and frames, header rows repeat).
/// Not done: hyphenation, the paragraph composer, optical kerning, ligatures, text wrap, anchored objects.
/// Deterministic: same input, same output.
/// </summary>
internal sealed class TextComposer
{
    private const double Epsilon = 0.01;
    private const double DefaultTabInterval = 36;

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
        for (var index = 0; index < paragraphs.Count && !flow.Exhausted; index++)
        {
            LayoutParagraph(paragraphs[index], flow, isFirst: index == 0);
            if (flow.Exhausted)
            {
                layout.Overset = true;
                layout.OversetCharacters += paragraphs.Skip(index + 1).Sum(p => p.Items.OfType<TextItem>().Sum(t => t.Text.Length));
            }
        }
        layout.LastDescent = flow.LastDescent;
        flow.FinishColumn();
        return layout;
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
        public TabItem? Tab;
        public XElement? Table;
    }

    private List<Shaped> Shape(ParagraphModel paragraph, string pageName)
    {
        var result = new List<Shaped>();
        foreach (var item in paragraph.Items)
        {
            switch (item)
            {
                case TextItem text:
                    ShapeText(text.Text, text.Style, result, paragraph.Format.Hyphenation);
                    break;
                case PageNumberItem number:
                    ShapeText(pageName, number.Style, result);
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
            }
        }
        // Pair kerning between neighbours in the same font and size.
        for (var i = 1; i < result.Count; i++)
        {
            var previous = result[i - 1];
            var current = result[i];
            if (previous.Kind is GlyphKind.Normal or GlyphKind.Space && current.Kind is GlyphKind.Normal or GlyphKind.Space
                && ReferenceEquals(previous.Font, current.Font) && previous.Size == current.Size && previous.Style.Kerning && current.Style.Kerning)
            {
                previous.Advance += previous.Font.Kerning(previous.Glyph, current.Glyph) * previous.Size * previous.Style.HorizontalScale;
            }
        }
        return result;
    }

    private void ShapeText(string text, RunStyle style, List<Shaped> output, HyphenationSettings? hyphenation = null)
    {
        // Discretionary break points from hyphenation, as soft hyphens after these text indices (ascending).
        hyphens.Clear();
        if (hyphenation is { Enabled: true } && Hyphenator.Supports(style.Language))
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
                Hyphenator.AddPoints(text.AsSpan(start, end - start), hyphenation, start, hyphens);
                start = end;
            }
        }
        var nextHyphen = 0;
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
                output.Add(new Shaped { Kind = GlyphKind.SoftHyphen, Style = style, Font = style.Font, Size = size, Rise = rise, BreakAfter = true, Text = string.Empty });
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
                BreakAfter = codePoint is 0x20 or 0x200B or 0x2002 or 0x2003 or 0x2009 or 0x200A or 0x2008 or 0x2004 or 0x2005 or 0x2006 or 0x3000
                    or '-' or 0x2010 or 0x2013 or 0x2014,
            });
            while (nextHyphen < hyphens.Count && hyphens[nextHyphen] < index)
            {
                nextHyphen++;
            }
            if (nextHyphen < hyphens.Count && hyphens[nextHyphen] == index)
            {
                output.Add(new Shaped { Kind = GlyphKind.SoftHyphen, Style = style, Font = style.Font, Size = size, Rise = rise, BreakAfter = true, Text = string.Empty });
            }
        }
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

    private void LayoutParagraph(ParagraphModel paragraph, Flow flow, bool isFirst)
    {
        var format = paragraph.Format;
        var glyphs = Shape(paragraph, flow.PageName);
        flow.PendingSpace += isFirst ? 0 : format.SpaceBefore;
        var index = 0;
        var firstLine = true;
        var emitted = false;
        while (!flow.Exhausted && (index < glyphs.Count || !emitted))
        {
            if (index < glyphs.Count && glyphs[index].Kind == GlyphKind.Table)
            {
                LayoutTable(glyphs[index].Table!, flow, format);
                index++;
                emitted = true;
                firstLine = false;
                continue;
            }
            var column = flow.Column;
            var indent = format.LeftIndent + (firstLine ? format.FirstLineIndent : 0);
            var available = column.Width - indent - format.RightIndent;
            var (end, forced) = FitLine(glyphs, index, available, format, indent);
            var line = glyphs.GetRange(index, end - index);
            index = end;
            var isLast = index >= glyphs.Count || glyphs[index].Kind == GlyphKind.Table;
            var metrics = Measure(line, paragraph.MarkStyle);
            if (!flow.Place(metrics))
            {
                return;
            }
            EmitLine(line, flow, format, indent, available, isLast, forced);
            emitted = true;
            firstLine = false;
        }
        flow.PendingSpace = format.SpaceAfter;
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
                    glyph.Advance = Math.Max(0.1, NextTabStop(x + indent, format, available + indent) - indent - x);
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

    /// <summary>The next tab stop after <paramref name="position"/> (measured from the column's left edge).</summary>
    private static double NextTabStop(double position, ParagraphFormatting format, double columnWidth)
    {
        foreach (var tab in format.Tabs)
        {
            if (tab.Position > position + Epsilon)
            {
                return tab.Position;
            }
        }
        var last = format.Tabs.Count > 0 ? format.Tabs[^1].Position : 0;
        var next = last + (Math.Floor((Math.Max(position, last) - last) / DefaultTabInterval) + 1) * DefaultTabInterval;
        return Math.Min(next, Math.Max(columnWidth, position));
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
            visible[^1] = hyphen[0];
        }
        visible.RemoveAll(glyph => glyph.Kind == GlyphKind.SoftHyphen);

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
        var spaces = visible.Count(glyph => glyph.Kind == GlyphKind.Space);
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
                flow.Add(new GlyphRunOp(runFirst.Font, runFirst.Size, runFirst.Style.HorizontalScale, runFirst.Style.Fill, runStart, baseline - runFirst.Rise, run.ToList()));
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

        foreach (var glyph in visible)
        {
            var advance = glyph.Advance + (glyph.Kind == GlyphKind.Space ? extraPerSpace : 0);
            if (glyph.Kind == GlyphKind.Tab)
            {
                FlushRun();
                x += advance;
                runStart = x;
                lineText.Append('\t');
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
        flow.Layout.Lines.Add(new LineInfo(flow.FrameIndex, flow.ColumnIndex, baseline, column.X + indent, width, lineText.ToString()));
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
            var target = glyph.Tab!.RightIndentTab ? available + indent : NextTabStop(x + indent, format, available + indent);
            var alignment = glyph.Tab.RightIndentTab ? "RightAlign" : format.Tabs.FirstOrDefault(tab => Math.Abs(tab.Position - target) < Epsilon).Alignment ?? "LeftAlign";
            var segment = 0.0;
            for (var j = i + 1; j < line.Count && line[j].Kind != GlyphKind.Tab; j++)
            {
                segment += line[j].Advance;
            }
            var start = alignment switch
            {
                "RightAlign" => target - segment,
                "CenterAlign" => target - (segment / 2),
                _ => target,
            };
            glyph.Advance = Math.Max(0, start - indent - x);
            x += glyph.Advance;
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
                resolver.Cell(cell, "FirstBaselineOffset") ?? "AscentOffset", Values.Number(resolver.Cell(cell, "MinimumFirstBaselineOffset"), 0), flow.PageName);
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

        var edges = new Dictionary<(long, long, long, long), LineOp>();
        void FlushEdges()
        {
            foreach (var edge in edges.Values)
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
                DrawCell(cell, new Rect(cellX, rowTop, cell.Width, height), flow, edges);
            }
        }

        var startRow = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var height = heights[r];
            var fits = top + height <= flow.Column.Bottom + Epsilon;
            if (!fits && !(flow.AtColumnTop && r == startRow))
            {
                FlushEdges();
                if (!flow.NextColumn())
                {
                    flow.Layout.Overset = true;
                    return;
                }
                x = flow.Column.X + format.LeftIndent;
                top = flow.Column.Y;
                startRow = r;
                if (r >= headerCount && headerCount > 0)
                {
                    for (var h = 0; h < headerCount; h++)
                    {
                        DrawRow(h, top);
                        top += heights[h];
                    }
                }
            }
            DrawRow(r, top);
            top += height;
            flow.AtColumnTop = false;
        }
        FlushEdges();
        flow.EndBlock(top + spaceAfter);
    }

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

    private void DrawCell(CellLayout cell, Rect box, Flow flow, Dictionary<(long, long, long, long), LineOp> edges)
    {
        var resolver = reader.Resolver;
        var element = cell.Cell;
        if (document.Swatches.ResolveColor(resolver.Cell(element, "FillColor")) is { } fill)
        {
            var tint = Values.Number(resolver.Cell(element, "FillTint"), -1);
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
            var weight = Values.Number(resolver.Cell(element, side + "EdgeStrokeWeight"), 1);
            if (weight <= 0)
            {
                return;
            }
            var color = document.Swatches.ResolveColor(resolver.Cell(element, side + "EdgeStrokeColor") ?? "Color/Black");
            if (color is null)
            {
                return;
            }
            var tint = Values.Number(resolver.Cell(element, side + "EdgeStrokeTint"), -1);
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

    /// <summary>The position in the frame/column sequence and the ops of the column being filled.</summary>
    private sealed class Flow
    {
        private readonly IReadOnlyList<FrameArea> frames;
        private List<DrawOp> columnOps = [];
        private double usedBottom;

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

        public bool Exhausted { get; private set; }

        public bool AtColumnTop { get; set; }

        public double Baseline { get; private set; }

        public double LastDescent { get; private set; }

        public double PendingSpace { get; set; }

        public FrameArea Frame => frames[Math.Min(FrameIndex, frames.Count - 1)];

        public Rect Column => Frame.Columns.Count == 0 ? default : Frame.Columns[Math.Min(ColumnIndex, Frame.Columns.Count - 1)];

        public string PageName => Frame.PageName;

        public void Add(DrawOp op) => columnOps.Add(op);

        /// <summary>Positions the next line; false when the text no longer fits anywhere.</summary>
        public bool Place(LineMetrics metrics)
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
                return true;
            }
            return false;
        }

        public void BeginBlock() => PendingSpace = 0;

        /// <summary>After a table: the next line is placed a leading below <paramref name="bottom"/>.</summary>
        public void EndBlock(double bottom)
        {
            Baseline = bottom;
            LastDescent = 0;
            usedBottom = Math.Max(usedBottom, bottom);
            AtColumnTop = false;
        }

        public bool NextColumn()
        {
            FinishColumn();
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

        /// <summary>Moves the column's ops into its frame, applying vertical justification.</summary>
        public void FinishColumn()
        {
            if (columnOps.Count == 0)
            {
                return;
            }
            var column = Column;
            var free = column.Bottom - usedBottom;
            var dy = Frame.VerticalJustification switch
            {
                "CenterAlign" => free / 2,
                "BottomAlign" => free,
                _ => 0,
            };
            var target = Layout.OpsByFrame[Frame.FrameId];
            foreach (var op in columnOps)
            {
                target.Add(dy > 0 ? op.Offset(0, dy) : op);
            }
            if (dy > 0)
            {
                for (var i = 0; i < Layout.Lines.Count; i++)
                {
                    var line = Layout.Lines[i];
                    if (line.FrameIndex == FrameIndex && line.Column == ColumnIndex)
                    {
                        Layout.Lines[i] = line with { Baseline = line.Baseline + dy };
                    }
                }
            }
            columnOps = [];
        }
    }
}
