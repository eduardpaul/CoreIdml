using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Tab stops: character (decimal) alignment and leaders, with the rules measured in InDesign 20's export of the
/// doc-samples 08-para-tabs package.
/// </summary>
public sealed class TabTests
{
    private static (IdmlDocument Document, Page Page) NewDocument()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        return (document, document.Pages[0]);
    }

    private static List<GlyphRunOp> Runs(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story).OpsByFrame.Values.SelectMany(ops => ops).OfType<GlyphRunOp>().ToList();

    private static ParagraphStyle Style(IdmlDocument document, params TabStop[] stops) =>
        document.Styles.AddParagraphStyle("Tabs", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, TabStops = stops });

    /// <summary>The x of the first glyph whose text is <paramref name="text"/>, in any run.</summary>
    private static double GlyphX(IEnumerable<GlyphRunOp> runs, string text)
    {
        foreach (var run in runs)
        {
            var x = run.X;
            foreach (var glyph in run.Glyphs)
            {
                if (glyph.Text == text)
                {
                    return x;
                }
                x += glyph.Advance;
            }
        }
        throw new InvalidOperationException($"No glyph '{text}'.");
    }

    [Theory]
    [InlineData("\t1.5", ".")]
    [InlineData("\t4,000.50", ".")]
    [InlineData("\t333,125", ",")]
    [InlineData("a\tbb=22", "=")]
    public void Character_alignment_puts_the_characters_left_edge_on_the_stop(string text, string alignOn)
    {
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 400, 100), text, Style(document, new TabStop(120, TabAlignment.Character, AlignOn: alignOn)));

        var runs = Runs(document, frame).Where(run => run.Text.Trim().Length > 0).ToList();
        Assert.Equal(frame.InnerBounds.X + 120, GlyphX(runs, alignOn), 3);
    }

    [Fact]
    public void Text_without_the_alignment_character_ends_at_the_stop()
    {
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 400, 100), "\t5", Style(document, new TabStop(120, TabAlignment.Character)));

        var five = Runs(document, frame).Single(run => run.Text == "5");
        Assert.Equal(frame.InnerBounds.X + 120, five.X + five.Width, 3);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("-")]
    [InlineData("_")]
    [InlineData("·")]
    public void Leaders_stand_on_a_grid_from_the_column_edge_and_stop_before_the_next_text(string leader)
    {
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 440, 100), "Netherfield Park\t4,000", Style(document, new TabStop(400, TabAlignment.Right, leader)));
        var runs = Runs(document, frame);
        var left = frame.InnerBounds.X;

        var dots = runs.Single(run => run.Text.Length > 3 && run.Text.All(c => c.ToString() == leader));
        var unit = dots.Glyphs[0].Advance;
        var textEnd = GlyphX(runs, "k") + runs.SelectMany(run => run.Glyphs).First(glyph => glyph.Text == "k").Advance;
        var numberStart = GlyphX(runs, "4");
        // On the grid: a whole number of leader widths from the column's left edge.
        var steps = (dots.X - left) / unit;
        Assert.Equal(Math.Round(steps), steps, 3);
        // The first slot at or after the text, the last one that ends before the number.
        Assert.True(dots.X >= textEnd - 1e-6 && dots.X - unit < textEnd, $"first leader at {dots.X}, text ends at {textEnd}");
        var end = dots.X + dots.Width;
        Assert.True(end <= numberStart + 0.01 && end + unit > numberStart, $"leader ends at {end}, number starts at {numberStart}");
    }

    [Fact]
    public void A_multi_character_leader_repeats_whole_while_its_first_glyph_fits()
    {
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 440, 100), "Netherfield Park\t4,000", Style(document, new TabStop(400, TabAlignment.Right, "ab")));
        var runs = Runs(document, frame);

        var leader = runs.Single(run => run.Text.StartsWith("abab", StringComparison.Ordinal));
        Assert.Equal(0, leader.Glyphs.Count % 2);
        var lastCopy = leader.X + leader.Width - leader.Glyphs[^2].Advance - leader.Glyphs[^1].Advance;
        Assert.True(lastCopy + leader.Glyphs[^2].Advance <= GlyphX(runs, "4") + 0.01);
    }

    [Fact]
    public void Leaders_on_following_lines_line_up()
    {
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 440, 100), "Chapter one\t1\nA much longer chapter title\t17", Style(document, new TabStop(400, TabAlignment.Right, ".")));
        var leaders = Runs(document, frame).Where(run => run.Text.StartsWith("...", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, leaders.Count);
        var offset = (leaders[1].X - leaders[0].X) / leaders[0].Glyphs[0].Advance;
        Assert.Equal(Math.Round(offset), offset, 3);
    }

    [Theory]
    [InlineData(TabAlignment.Right)]
    [InlineData(TabAlignment.Character)]
    public void A_right_tab_at_the_columns_edge_keeps_the_page_number_on_the_line(TabAlignment alignment)
    {
        // The usual table of contents: the stop at the full column width. Breaking lines as if the tab were a left
        // tab would push the number to a line of its own.
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 300, 100), "1 Getting there\t3", Style(document, new TabStop(300, alignment, ".")));

        var line = Assert.Single(new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story).Lines);
        Assert.Equal("1 Getting there\t3", line.Text);
        var three = Runs(document, frame).Single(run => run.Text == "3");
        Assert.Equal(frame.InnerBounds.X + 300, three.X + three.Width, 3);
    }

    [Fact]
    public void A_tab_is_written_as_a_space_glyph_where_it_starts()
    {
        // As InDesign writes it: invisible, but text extraction sees a word break between "Park" and the leader.
        var (document, page) = NewDocument();
        var frame = page.AddText(new Rect(0, 0, 440, 100), "Park\t1", Style(document, new TabStop(400, TabAlignment.Right, ".")));
        var runs = Runs(document, frame);

        var space = runs.Single(run => run.Text == " ");
        Assert.Equal(GlyphX(runs, "k") + runs.SelectMany(run => run.Glyphs).First(glyph => glyph.Text == "k").Advance, space.X, 3);
    }

    [Fact]
    public void Stops_written_by_the_dom_read_back_as_InDesign_stores_them()
    {
        var (document, _) = NewDocument();
        var style = Style(document, new TabStop(200, TabAlignment.Character, "", ","), new TabStop(70), new TabStop(400, TabAlignment.Right, "."));

        var items = style.Element.Element("Properties")!.Element("TabList")!.Elements("ListItem").ToList();
        Assert.Equal(["70", "200", "400"], items.Select(item => item.Element("Position")!.Value));
        Assert.Equal(["LeftAlign", "CharacterAlign", "RightAlign"], items.Select(item => item.Element("Alignment")!.Value));
        Assert.Equal(",", items[1].Element("AlignmentCharacter")!.Value);
        Assert.Equal(".", items[2].Element("Leader")!.Value);
        Assert.Equal("unit", items[0].Element("Position")!.Attribute("type")!.Value);
    }
}
