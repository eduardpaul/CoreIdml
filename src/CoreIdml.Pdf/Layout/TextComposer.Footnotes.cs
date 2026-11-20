using System.Globalization;
using System.Xml.Linq;

namespace CoreIdml.Pdf.Layout;

/// <summary>
/// Footnotes, as InDesign 20 sets them in 11-long-footnotes-endnotes (measured on its reference PDF):
/// <list type="bullet">
/// <item>The reference is the footnote's number, in the text's formatting under the options' marker style
/// (<see cref="StoryReader.Footnotes"/>; superscript: 5.83 pt raised 3.33 pt in 10 pt text). Numbers restart on every
/// page with <c>RestartNumbering="PageRestart"</c> (p2 and p4 start again at 1, though p4's label promises 4 to 6).</item>
/// <item>The footnote's text (its own paragraphs, starting with its number, <c>&lt;?ACE 4?&gt;</c>, then the stored
/// separator tab) is set at the column's width at the foot of the column its reference falls in, under its own column
/// in a two-column frame (p3). The footnotes of a column are stacked in reference order, the last one's last baseline
/// on the column's bottom (p1 to p4: 756 pt, the frame's bottom), each first baseline the first-baseline offset
/// (<c>FootnoteFirstBaselineOffset="LeadingOffset"</c>: 10 pt) below where the footnote starts, and
/// <c>SpaceBetween</c> (4 pt) between footnotes: baselines 728, 742, 756 on p1, a two-line footnote's 732 and 742 then
/// 756 on p2.</item>
/// <item>The rule above (0.75 pt red, 120 pt wide on p1 to p4) is centred <c>RuleOffset</c> (4 pt) above the first
/// footnote's start: 714 pt on p1, 718 pt on p2.</item>
/// </list>
/// The column's text ends above its footnotes: a line goes in a column only while its baseline stays above them, the
/// footnotes of the line itself included, so adding a footnote can send its own reference on to the next column. With
/// <c>NoSplitting="false"</c> only the first line of the line's first footnote has to fit; the rest of a footnote that
/// does not fit goes on at the foot of the next column, under the continuing rule. Not measured, as the sample never
/// brings the text near its footnotes: that the text keeps <c>Spacer</c> (Minimum Space Before First Footnote) and the
/// rule's reach above the footnotes clear (from memory), how a footnote splits and continues, restarting by spread or
/// section, the numbering of footnotes in table cells (each cell counts on its own, without setting any footnote
/// text) and footnotes spanning the columns (<c>FootnotesSpanAcrossColumns</c>, not done).
/// </summary>
internal sealed partial class TextComposer
{
    // Footnote texts composed so far: by footnote, number, page name and the columns (width × height) it is set in.
    private readonly Dictionary<(XElement Footnote, string Number, string Page, string Areas), StoryLayout> noteLayouts = [];
    // Footnotes per story element (null: none), so that a story without any pays nothing.
    private readonly Dictionary<XElement, Flow.NoteContext?> noteContexts = [];
    // The spread and section of each document page, for numbering that restarts by spread or section.
    private (string Spread, int Section)[]? pageScopes;

    /// <summary>What the flow of <paramref name="container"/> needs to set footnotes: null unless it is a story holding some.</summary>
    private Flow.NoteContext? NotesFor(XElement container)
    {
        if (container.Name.LocalName != "Story")
        {
            return null;
        }
        if (!noteContexts.TryGetValue(container, out var context))
        {
            var options = reader.Footnotes;
            context = container.Descendants("Footnote").Any()
                ? new Flow.NoteContext(options, ComposeNote, options.Restart == "DontRestart" ? reader.FootnotesBefore(container) : 0, SameNumbering)
                : null;
            noteContexts[container] = context;
        }
        return context;
    }

    /// <summary>
    /// The text of the next footnote reference (<paramref name="ahead"/> more after it), numbered for the current
    /// page: the options' start, plus the footnotes numbered before it where numbering does not restart.
    /// </summary>
    private string FootnoteReference(Flow flow, int ahead) => reader.Footnotes.Format(FootnoteOrdinal(flow) + ahead, reference: true);

    private int FootnoteOrdinal(Flow flow) =>
        reader.Footnotes.StartAt + (flow.Notes is { } notes ? notes.NumberedBefore + flow.NotesNumberedWith(flow.Frame.PageIndex) : 0);

    /// <summary>
    /// Whether footnotes on document pages <paramref name="a"/> and <paramref name="b"/> are numbered together: always,
    /// or on the same page, spread or section as <c>RestartNumbering</c> says.
    /// </summary>
    private bool SameNumbering(int a, int b)
    {
        var restart = reader.Footnotes.Restart;
        if (restart is not ("PageRestart" or "SpreadRestart" or "SectionRestart") || a == b)
        {
            return true;
        }
        if (restart == "PageRestart" || a < 0 || b < 0)
        {
            return false;
        }
        pageScopes ??= PageScopes();
        if (a >= pageScopes.Length || b >= pageScopes.Length)
        {
            return false;
        }
        return restart == "SpreadRestart" ? pageScopes[a].Spread == pageScopes[b].Spread : pageScopes[a].Section == pageScopes[b].Section;
    }

    private (string Spread, int Section)[] PageScopes()
    {
        var starts = document.DesignMap.Elements("Section").Select(section => section.Attribute("PageStart")?.Value).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var section = 0;
        return document.Pages.Select((page, index) =>
        {
            section += index > 0 && starts.Contains(page.Id) ? 1 : 0;
            return (page.Spread.Id, section);
        }).ToArray();
    }

    /// <summary>
    /// Before line glyphs[start..end) is placed: the room its footnotes need below it in the column it goes to (with
    /// splitting allowed, the first line of the first one; else all of them).
    /// </summary>
    private void ReserveFootnotes(List<Shaped> glyphs, int start, int end, Flow flow)
    {
        if (flow.Notes is not { } notes)
        {
            return;
        }
        var options = notes.Options;
        var (room, count, ordinal) = (0.0, 0, FootnoteOrdinal(flow));
        FootnoteItem? last = null;
        for (var i = start; i < end; i++)
        {
            if (glyphs[i].Item is not FootnoteItem footnote || ReferenceEquals(footnote, last))
            {
                continue;
            }
            last = footnote;
            var text = ComposeNote(footnote.Footnote, options.Format(ordinal + count, reference: false), flow.PageName, [(flow.NoteWidth, Flow.Unlimited)]);
            if (options.AllowSplitting)
            {
                room = text.Lines.Count > 0 ? text.Lines[0].Baseline : 0;
                break;
            }
            room += (count++ > 0 ? flow.NoteSpaceBetween : 0) + Flow.AreaHeight(text, 0);
        }
        flow.ReserveNotes(room);
    }

    /// <summary>
    /// A line was placed: its footnote references get the numbers of where it went (the glyphs in
    /// <paramref name="line"/> are replaced when the number changed since the paragraph was shaped), and their
    /// footnotes go to the foot of its column.
    /// </summary>
    private void PlaceFootnotes(List<Shaped> line, Flow flow)
    {
        flow.ReserveNotes(0);
        for (var i = 0; i < line.Count; i++)
        {
            if (line[i].Item is not FootnoteItem footnote)
            {
                continue;
            }
            var end = i;
            while (end < line.Count && ReferenceEquals(line[end].Item, footnote))
            {
                end++;
            }
            var ordinal = FootnoteOrdinal(flow);
            var reference = reader.Footnotes.Format(ordinal, reference: true);
            if (!string.Equals(string.Concat(line.GetRange(i, end - i).Select(glyph => glyph.Text)), reference, StringComparison.Ordinal))
            {
                var shaped = new List<Shaped>();
                ShapeText(reference, footnote.Style, shaped);
                foreach (var glyph in shaped)
                {
                    (glyph.Item, glyph.Marker) = (footnote, true);
                }
                line.RemoveRange(i, end - i);
                line.InsertRange(i, shaped);
                end = i + shaped.Count;
            }
            flow.AddNote(footnote.Footnote, reader.Footnotes.Format(ordinal, reference: false));
            i = end - 1;
        }
    }

    /// <summary>
    /// The footnote's paragraphs set in <paramref name="areas"/> (width × height, the first parts of a split footnote
    /// then the rest), starting with <paramref name="number"/>: an area per part, its ops at the part's top left.
    /// </summary>
    private StoryLayout ComposeNote(XElement footnote, string number, string pageName, IReadOnlyList<(double Width, double Height)> areas)
    {
        var key = (footnote, number, pageName, string.Join(';', areas.Select(area => string.Create(CultureInfo.InvariantCulture, $"{area.Width:R}x{area.Height:R}"))));
        if (!noteLayouts.TryGetValue(key, out var layout))
        {
            var options = reader.Footnotes;
            layout = Compose(footnote, [.. areas.Select((area, index) =>
                new FrameArea(Flow.NoteAreaId(index), [new Rect(0, 0, area.Width, area.Height)], "TopAlign", options.FirstBaselineOffset, options.MinimumFirstBaselineOffset, pageName)
                {
                    FootnoteNumber = number,
                })]);
            noteLayouts[key] = layout;
        }
        return layout;
    }

    private sealed partial class Flow
    {
        /// <summary>The height of a footnote area with no limit.</summary>
        public const double Unlimited = 1e6;

        private NoteState notes = NoteState.Empty;
        // The room the footnotes of the line being placed need below it (Bottom keeps it free).
        private double lineNotes;

        /// <summary>The story's footnote options and numbering; null where footnotes are not set (cells, footnotes).</summary>
        public NoteContext? Notes { get; init; }

        /// <summary>
        /// The footnotes placed (<see cref="Parts"/>), their numbering (the page of each reference, <see cref="Pages"/>),
        /// and the footnotes, or rests of footnotes, waiting for the next column (<see cref="Carried"/>). Immutable, so
        /// that a <see cref="State"/> holds it.
        /// </summary>
        public sealed record NoteState(NotePart[] Parts, int[] Pages, CarriedNote[] Carried)
        {
            public static readonly NoteState Empty = new([], [], []);
        }

        /// <summary>
        /// A footnote, or the part of one that went in a column: at the foot of column <paramref name="ColumnIndex"/> of
        /// frame <paramref name="FrameIndex"/>, area <paramref name="Area"/> of <paramref name="Text"/>,
        /// <paramref name="Height"/> to its last baseline; <paramref name="Continued"/> when it began in a column before.
        /// </summary>
        public sealed record NotePart(int FrameIndex, int ColumnIndex, StoryLayout Text, int Area, double Height, bool Continued);

        /// <summary>A footnote (or its rest) still to place, with the columns its parts placed so far took (width × height).</summary>
        public sealed record CarriedNote(XElement Footnote, string Number, (double Width, double Height)[] Placed);

        /// <summary>
        /// What a story's flow needs to set footnotes: the options, how to compose a footnote, how many footnotes the
        /// stories before it numbered (when numbering does not restart) and which pages number their footnotes together.
        /// </summary>
        public sealed class NoteContext(
            FootnoteOptions options, Func<XElement, string, string, IReadOnlyList<(double Width, double Height)>, StoryLayout> compose,
            int numberedBefore, Func<int, int, bool> sameNumbering)
        {
            public FootnoteOptions Options => options;

            public int NumberedBefore => numberedBefore;

            public StoryLayout Compose(XElement footnote, string number, string pageName, IReadOnlyList<(double Width, double Height)> areas) =>
                compose(footnote, number, pageName, areas);

            public bool SameNumbering(int a, int b) => sameNumbering(a, b);
        }

        public static string NoteAreaId(int index) => "footnote " + index.ToString(CultureInfo.InvariantCulture);

        /// <summary>The last baseline of <paramref name="text"/>'s area <paramref name="area"/> (0: no line there).</summary>
        public static double AreaHeight(StoryLayout text, int area)
        {
            var height = 0.0;
            foreach (var line in text.Lines)
            {
                height = line.FrameIndex == area ? Math.Max(height, line.Baseline) : height;
            }
            return height;
        }

        /// <summary>The width footnotes are set at in the current column: the frame's column, not a span or a split part.</summary>
        public double NoteWidth => CurrentColumn is { } column ? column.Width : 0;

        public double NoteSpaceBetween => SpaceBetween(FrameIndex);

        /// <summary>How many footnotes are numbered together with one on document page <paramref name="page"/>.</summary>
        public int NotesNumberedWith(int page)
        {
            var count = 0;
            foreach (var numbered in notes.Pages)
            {
                count += Notes!.SameNumbering(numbered, page) ? 1 : 0;
            }
            return count;
        }

        /// <summary>The line about to be placed needs <paramref name="room"/> below it for its footnotes (0: none).</summary>
        public void ReserveNotes(double room) => lineNotes = room;

        // The current frame column (null for a frame without columns).
        private Rect? CurrentColumn => Frame.Columns.Count == 0 ? null : Frame.Columns[Math.Min(ColumnIndex, Frame.Columns.Count - 1)];

        private int CurrentFrame => Math.Min(FrameIndex, frames.Count - 1);

        private double SpaceBefore(int frame) => frames[Math.Min(frame, frames.Count - 1)].FootnoteSpacing?.SpaceBefore ?? Notes!.Options.SpaceBefore;

        private double SpaceBetween(int frame) => frames[Math.Min(frame, frames.Count - 1)].FootnoteSpacing?.SpaceBetween ?? Notes!.Options.SpaceBetween;

        /// <summary>The lowest baseline text can have in the current column above its footnotes (and the line's).</summary>
        private double NoteFloor() =>
            Notes is null || CurrentColumn is not { } column ? double.PositiveInfinity : Floor(CurrentFrame, ColumnIndex, column.Bottom, lineNotes);

        /// <summary>The lowest baseline text can have in a column ending at <paramref name="bottom"/> above its footnotes.</summary>
        public double NoteFloor(int frame, int column, double bottom) => Notes is null ? bottom : Floor(frame, column, bottom, 0);

        private double Floor(int frame, int column, double bottom, double extra)
        {
            var (height, count, continued) = NoteArea(frame, column);
            if (extra > 0)
            {
                height += (count > 0 ? SpaceBetween(frame) : 0) + extra;
                continued &= count > 0;
                count++;
            }
            return count == 0 ? bottom : bottom - height - SpaceBefore(frame) - RuleOf(continued).Extent;
        }

        // The footnote area of a column: its height, how many parts it holds and whether the first is a continued one.
        private (double Height, int Count, bool Continued) NoteArea(int frame, int column)
        {
            var (height, count, continued) = (0.0, 0, false);
            foreach (var part in notes.Parts)
            {
                if (part.FrameIndex == frame && part.ColumnIndex == column)
                {
                    continued |= count == 0 && part.Continued;
                    height += part.Height;
                    count++;
                }
            }
            return (count > 1 ? height + ((count - 1) * SpaceBetween(frame)) : height, count, continued);
        }

        private NoteRule RuleOf(bool continued) => continued ? Notes!.Options.ContinuingRule : Notes!.Options.Rule;

        /// <summary>
        /// A reference was placed on the line just placed: numbers its footnote and sets its text at the foot of the
        /// column, as much of it as fits below the line (all of it when it may not split); the rest waits for the next
        /// column. Behind a footnote already waiting, it waits too.
        /// </summary>
        public void AddNote(XElement footnote, string number)
        {
            if (Notes is null)
            {
                return;
            }
            notes = notes with { Pages = [.. notes.Pages, Frame.PageIndex] };
            var note = new CarriedNote(footnote, number, []);
            if (notes.Carried.Length > 0 || CurrentColumn is not { } column)
            {
                notes = notes with { Carried = [.. notes.Carried, note] };
                return;
            }
            var (height, count, continued) = NoteArea(CurrentFrame, ColumnIndex);
            var rule = RuleOf(count > 0 && continued);
            var room = column.Bottom - Baseline - SpaceBefore(FrameIndex) - rule.Extent - height - (count > 0 ? SpaceBetween(FrameIndex) : 0);
            PlaceNote(note, room, column.Width, force: !Notes.Options.AllowSplitting);
        }

        // Sets `note` in the current column with `room` for it; what does not fit is carried (unless `force`).
        private void PlaceNote(CarriedNote note, double room, double width, bool force)
        {
            var area = note.Placed.Length;
            var whole = Notes!.Compose(note.Footnote, note.Number, PageName, [.. note.Placed, (width, Unlimited)]);
            var height = AreaHeight(whole, area);
            if (height <= room + Epsilon || force)
            {
                Add(whole, area, height);
                return;
            }
            var split = room > 0 ? Notes.Compose(note.Footnote, note.Number, PageName, [.. note.Placed, (width, room), (width, Unlimited)]) : null;
            if (split is not null && AreaHeight(split, area) is var first and > 0)
            {
                Add(split, area, first);
                note = note with { Placed = [.. note.Placed, (width, room)] };
            }
            notes = notes with { Carried = [.. notes.Carried, note] };

            void Add(StoryLayout text, int index, double partHeight) =>
                notes = notes with { Parts = [.. notes.Parts, new NotePart(CurrentFrame, ColumnIndex, text, index, partHeight, index > 0)] };
        }

        /// <summary>A new column: the footnotes waiting go at its foot first, as much of them as fits.</summary>
        private void PlaceCarriedNotes()
        {
            if (Notes is null || notes.Carried.Length == 0 || CurrentColumn is not { } column)
            {
                return;
            }
            var carried = notes.Carried;
            notes = notes with { Carried = [] };
            foreach (var note in carried)
            {
                if (notes.Carried.Length > 0)
                {
                    notes = notes with { Carried = [.. notes.Carried, note] };
                    continue;
                }
                var (height, count, continued) = NoteArea(CurrentFrame, ColumnIndex);
                var rule = RuleOf(count > 0 ? continued : note.Placed.Length > 0);
                var room = column.Bottom - Column.Y - rule.Extent - height - (count > 0 ? SpaceBetween(FrameIndex) : 0);
                PlaceNote(note, room, column.Width, force: false);
            }
        }

        /// <summary>At the end of the text: the columns after it take the footnotes still waiting (overset when none is left).</summary>
        public void PlaceRemainingNotes()
        {
            while (Notes is not null && notes.Carried.Length > 0 && !Exhausted)
            {
                NextColumn();
            }
        }

        /// <summary>
        /// Draws the footnotes at the foot of their columns, under the rule (the continuing rule above a continued
        /// footnote), and records how deep each column's footnotes reach below its text (<see cref="StoryLayout.FootnoteDepths"/>).
        /// </summary>
        private void DrawNotes()
        {
            foreach (var group in notes.Parts.GroupBy(part => (part.FrameIndex, part.ColumnIndex)))
            {
                var (frameIndex, columnIndex) = group.Key;
                var frame = frames[frameIndex];
                if (frame.Columns.Count == 0)
                {
                    continue;
                }
                var column = frame.Columns[Math.Min(columnIndex, frame.Columns.Count - 1)];
                var (height, _, continued) = NoteArea(frameIndex, columnIndex);
                var top = column.Bottom - height;
                var target = Layout.OpsByFrame[frame.FrameId];
                var rule = RuleOf(continued);
                if (rule is { On: true, Color: { } color, Weight: > 0 })
                {
                    target.Add(new TextRuleOp(new Point(column.X + rule.LeftIndent, top - rule.Offset), rule.Width, rule.Weight, color, rule.Type, rule.Gap));
                }
                var y = top;
                foreach (var part in group)
                {
                    foreach (var op in part.Text.OpsByFrame.GetValueOrDefault(NoteAreaId(part.Area)) ?? [])
                    {
                        target.Add(op.Offset(column.X, y));
                    }
                    y += part.Height + SpaceBetween(frameIndex);
                }
                Layout.FootnoteDepths[(frameIndex, columnIndex)] = height + SpaceBefore(frameIndex) + rule.Extent;
            }
        }
    }
}
