using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Paragraph rules, borders and shading, as InDesign 20 draws them in the doc-samples 08-para-rules and
/// 08-para-borders-shading PDFs.
/// </summary>
public sealed class ParagraphRuleTests
{
    // Liberation Serif 11 on 14 pt leading.
    private static readonly ParagraphFormat Body = new() { FontFamily = "Liberation Serif", PointSize = 11, Leading = 14, Hyphenation = false };

    private const string Sentence = "Mr. Bennet made no answer.";

    private static IdmlDocument NewDocument()
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        document.Swatches.AddCmyk("Red", 0, 100, 100, 0);
        return document;
    }

    /// <summary>A paragraph style with the given attributes and Properties children (name, value).</summary>
    private static ParagraphStyle Style(IdmlDocument document, string name, (string Name, string Value)[] attributes, params (string Name, string Value)[] properties)
    {
        var style = document.Styles.AddParagraphStyle(name, Body);
        foreach (var (attribute, value) in attributes)
        {
            style.Element.SetAttributeValue(attribute, value);
        }
        var element = style.Element.Element("Properties");
        if (element is null)
        {
            element = new XElement("Properties");
            style.Element.AddFirst(element);
        }
        foreach (var (property, value) in properties)
        {
            element.Add(new XElement(property, new XAttribute("type", "object"), value));
        }
        return style;
    }

    private static TextFrame Frame(IdmlDocument document, string text, ParagraphStyle style, Rect? bounds = null, int columns = 1)
    {
        var frame = document.Pages[0].AddTextFrame(bounds ?? new Rect(100, 100, 250, 300));
        frame.Columns = columns;
        frame.ColumnGutter = 12;
        frame.Story.SetText(text, style);
        return frame;
    }

    private static StoryLayout Layout(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story);

    [Fact]
    public void A_rule_below_is_its_offset_under_the_last_baseline_across_the_column_behind_the_text()
    {
        var document = NewDocument();
        var style = Style(document, "Below", [("RuleBelow", "true"), ("RuleBelowLineWeight", "2"), ("RuleBelowOffset", "4")]);
        var frame = Frame(document, Sentence + "\n" + Sentence, style);

        var layout = Layout(document, frame);
        var ops = layout.OpsByFrame[frame.Id];
        var rules = ops.OfType<TextRuleOp>().ToList();
        Assert.Equal(2, rules.Count);
        for (var i = 0; i < 2; i++)
        {
            var line = layout.Lines[i];
            // The rule's top edge is 4 pt below the baseline: its centre 4 + 1 pt.
            Assert.Equal(line.Baseline + 5, rules[i].From.Y, 6);
            Assert.Equal(line.ColumnLeft, rules[i].From.X, 6);
            Assert.Equal(line.ColumnWidth, rules[i].Length, 6);
            Assert.Equal(2, rules[i].Weight);
        }
        // Text Color, the default: the text's black.
        Assert.Equal([0, 0, 0, 1], rules[0].Color.Components);
        Assert.Equal("StrokeStyle/$ID/Solid", rules[0].Type);
        Assert.True(ops.IndexOf(rules[^1]) < ops.FindIndex(op => op is GlyphRunOp), "InDesign paints the rules before the frame's text");
    }

    [Fact]
    public void A_rule_above_is_its_offset_and_weight_over_the_first_baseline_in_its_colour_and_tint()
    {
        var document = NewDocument();
        var style = Style(document, "Above", [("RuleAbove", "true"), ("RuleAboveLineWeight", "3"), ("RuleAboveOffset", "6"), ("RuleAboveTint", "50")],
            ("RuleAboveColor", "Color/Red"), ("RuleAboveType", "StrokeStyle/$ID/Dashed"), ("RuleAboveGapColor", "Color/Red"));
        var frame = Frame(document, Sentence + " " + Sentence + " " + Sentence + " " + Sentence, style);

        var layout = Layout(document, frame);
        Assert.True(layout.Lines.Count > 1);
        var rule = layout.OpsByFrame[frame.Id].OfType<TextRuleOp>().Single();
        Assert.Equal(layout.Lines[0].Baseline - 7.5, rule.From.Y, 6);
        Assert.Equal([0, 0.5, 0.5, 0], rule.Color.Components);
        Assert.Equal("StrokeStyle/$ID/Dashed", rule.Type);
        Assert.Equal([0, 1, 1, 0], rule.Gap!.Components);
    }

    [Fact]
    public void Column_rules_ignore_paragraph_indents_and_text_rules_span_the_lines_text_less_their_indents()
    {
        var document = NewDocument();
        var column = Style(document, "Column", [("LeftIndent", "30"), ("RuleBelow", "true"), ("RuleBelowLeftIndent", "20"), ("RuleBelowRightIndent", "40")]);
        var text = Style(document, "Text", [("LeftIndent", "30"), ("Justification", "CenterAlign"), ("RuleBelow", "true"), ("RuleBelowWidth", "TextWidth"),
            ("RuleBelowLeftIndent", "5"), ("RuleBelowRightIndent", "10")]);
        var columnFrame = Frame(document, Sentence, column);
        var textFrame = Frame(document, Sentence, text, new Rect(100, 450, 250, 100));

        var columnLayout = Layout(document, columnFrame);
        var line = columnLayout.Lines.Single();
        var rule = columnLayout.OpsByFrame[columnFrame.Id].OfType<TextRuleOp>().Single();
        Assert.Equal(line.ColumnLeft + 20, rule.From.X, 6);
        Assert.Equal(line.ColumnWidth - 60, rule.Length, 6);

        var textLayout = Layout(document, textFrame);
        line = textLayout.Lines.Single();
        rule = textLayout.OpsByFrame[textFrame.Id].OfType<TextRuleOp>().Single();
        Assert.True(line.TextLeft > line.ColumnLeft + 30, "centred text starts past the indent");
        Assert.Equal(line.TextLeft + 5, rule.From.X, 6);
        Assert.Equal(line.Width - 15, rule.Length, 6);
    }

    [Fact]
    public void Rules_borders_and_shading_take_no_space()
    {
        var document = NewDocument();
        var plain = Style(document, "Plain", []);
        var decorated = Style(document, "Decorated", [("RuleAbove", "true"), ("RuleAboveOffset", "20"), ("RuleAboveLineWeight", "6"),
            ("RuleBelow", "true"), ("RuleBelowOffset", "12"), ("ParagraphBorderOn", "true"), ("ParagraphBorderTopOffset", "8"),
            ("ParagraphShadingOn", "true"), ("ParagraphShadingBottomOffset", "8")]);
        var text = Sentence + "\n" + Sentence + " " + Sentence + " " + Sentence;
        var before = Layout(document, Frame(document, text, plain)).Lines.Select(line => (line.Baseline, line.TextLeft)).ToList();
        var after = Layout(document, Frame(document, text, decorated)).Lines.Select(line => (line.Baseline, line.TextLeft)).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void A_rule_above_kept_in_its_frame_moves_the_first_line_down_until_the_rule_is_inside()
    {
        var document = NewDocument();
        var loose = Style(document, "Loose", [("RuleAbove", "true"), ("RuleAboveLineWeight", "3"), ("RuleAboveOffset", "20")]);
        var kept = Style(document, "Kept", [("RuleAbove", "true"), ("RuleAboveLineWeight", "3"), ("RuleAboveOffset", "20"), ("KeepRuleAboveInFrame", "true")]);
        var looseFrame = Frame(document, Sentence, loose, new Rect(100, 100, 250, 100));
        var keptFrame = Frame(document, Sentence + "\n" + Sentence, kept, new Rect(100, 450, 250, 100));

        // Not kept: the first baseline at the font's ascent below the column's top, the rule above the frame.
        var looseLayout = Layout(document, looseFrame);
        var top = looseLayout.Lines[0].Baseline - looseLayout.Lines[0].Ascent;
        Assert.True(looseLayout.OpsByFrame[looseFrame.Id].OfType<TextRuleOp>().Single().From.Y < top);

        // Kept (08-para-rules p5): the rule's top at the column's top; the next paragraph is a leading below.
        var keptLayout = Layout(document, keptFrame);
        Assert.Equal(top + 23, keptLayout.Lines[0].Baseline, 6);
        Assert.Equal(top + 1.5, keptLayout.OpsByFrame[keptFrame.Id].OfType<TextRuleOp>().First().From.Y, 6);
        Assert.Equal(top + 23 + 14, keptLayout.Lines[1].Baseline, 6);
    }

    [Fact]
    public void Consecutive_paragraphs_with_the_same_border_are_one_box_and_another_border_breaks_it()
    {
        var document = NewDocument();
        (string, string)[] border = [("ParagraphBorderOn", "true"), ("ParagraphBorderTopOffset", "4"), ("ParagraphBorderBottomOffset", "4"),
            ("ParagraphBorderLeftOffset", "4"), ("ParagraphBorderRightOffset", "4")];
        var same = Style(document, "Same", border);
        var other = Style(document, "Other", [.. border, ("ParagraphBorderTopLineWeight", "2")], ("ParagraphBorderColor", "Color/Red"));
        var frame = Frame(document, Sentence + "\n" + Sentence + "\n" + Sentence, same);
        frame.Story.AddParagraph(Sentence, other);

        var layout = Layout(document, frame);
        var boxes = layout.OpsByFrame[frame.Id].OfType<ParagraphBorderOp>().ToList();
        Assert.Equal(2, boxes.Count);
        var (first, third, fourth) = (layout.Lines[0], layout.Lines[2], layout.Lines[3]);
        // From the first line's ascent to the third paragraph's descent, 4 pt out all round (08-para-borders-shading p4).
        Assert.Equal(Rect.FromEdges(first.ColumnLeft - 4, first.Baseline - first.Ascent - 4, first.ColumnLeft + first.ColumnWidth + 4, third.Baseline + third.Descent + 4),
            boxes[0].Box);
        Assert.Equal(fourth.Baseline - fourth.Ascent - 4, boxes[1].Box.Top, 6);
        Assert.Equal(new Spacing(2, 1, 1, 1), boxes[1].Border.Weights);
        Assert.Equal([0, 1, 1, 0], boxes[1].Border.Color.Components);
        Assert.All(boxes, box => Assert.False(box.OpenTop || box.OpenBottom));
    }

    [Fact]
    public void Shading_alone_is_drawn_paragraph_by_paragraph_across_the_column_whatever_the_indents()
    {
        var document = NewDocument();
        var style = Style(document, "Shaded", [("ParagraphShadingOn", "true"), ("LeftIndent", "12"), ("RightIndent", "12")], ("ParagraphShadingColor", "Color/Red"));
        var frame = Frame(document, Sentence + "\n" + Sentence, style);

        var layout = Layout(document, frame);
        var ops = layout.OpsByFrame[frame.Id];
        var shadings = ops.OfType<ParagraphShadingOp>().ToList();
        Assert.Equal(2, shadings.Count);
        for (var i = 0; i < 2; i++)
        {
            var line = layout.Lines[i];
            Assert.Equal(Rect.FromEdges(line.ColumnLeft, line.Baseline - line.Ascent, line.ColumnLeft + line.ColumnWidth, line.Baseline + line.Descent), shadings[i].Box);
        }
        // The default tint, [No paragraph style]'s 20%.
        Assert.Equal([0, 0.2, 0.2, 0], shadings[0].Color.Components);
        Assert.True(ops.IndexOf(shadings[^1]) < ops.FindIndex(op => op is GlyphRunOp));
    }

    [Fact]
    public void A_bordered_paragraph_split_across_columns_gets_a_box_in_each_open_at_the_break()
    {
        var document = NewDocument();
        var text = string.Join(" ", Enumerable.Repeat(Sentence, 8));
        var open = Style(document, "Open", [("ParagraphBorderOn", "true"), ("ParagraphShadingOn", "true")]);
        var closed = Style(document, "Closed", [("ParagraphBorderOn", "true"), ("ParagraphBorderDisplayIfSplits", "true")]);
        var openFrame = Frame(document, text, open, new Rect(100, 100, 300, 60), columns: 2);
        var closedFrame = Frame(document, text, closed, new Rect(100, 300, 300, 60), columns: 2);

        var layout = Layout(document, openFrame);
        Assert.Equal([0, 1], layout.Lines.Select(line => line.Column).Distinct());
        var boxes = layout.OpsByFrame[openFrame.Id].OfType<ParagraphBorderOp>().ToList();
        Assert.Equal(2, boxes.Count);
        Assert.True(boxes[0] is { OpenTop: false, OpenBottom: true });
        Assert.True(boxes[1] is { OpenTop: true, OpenBottom: false });
        Assert.True(boxes[1].Box.Left > boxes[0].Box.Right);
        Assert.Equal(2, layout.OpsByFrame[openFrame.Id].OfType<ParagraphShadingOp>().Count());

        boxes = Layout(document, closedFrame).OpsByFrame[closedFrame.Id].OfType<ParagraphBorderOp>().ToList();
        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, box => Assert.False(box.OpenTop || box.OpenBottom));
    }

    [Fact]
    public void Borders_are_stroked_as_one_path_with_their_corners_or_side_by_side()
    {
        var document = NewDocument();
        var rounded = Style(document, "Rounded", [("ParagraphBorderOn", "true"), ("ParagraphShadingOn", "true"), ("ParagraphShadingTint", "100"),
            ("ParagraphBorderTopLeftCornerOption", "RoundedCorner"), ("ParagraphBorderTopLeftCornerRadius", "5")], ("ParagraphShadingColor", "Color/Red"));
        Frame(document, Sentence, rounded);
        var content = Content(document);
        // The red shading (a rectangle), then the border's path with one curve, before the text.
        var shading = content.IndexOf(" re\n0 1 1 0 k\nf\n", StringComparison.Ordinal);
        Assert.InRange(shading, 0, content.IndexOf("BT", StringComparison.Ordinal));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(content[shading..content.IndexOf("BT", StringComparison.Ordinal)], @" c\n"));

        document = NewDocument();
        Frame(document, Sentence, Style(document, "Sides", [("ParagraphBorderOn", "true"), ("ParagraphBorderLeftLineWeight", "0"),
            ("ParagraphBorderRightLineWeight", "0"), ("ParagraphBorderTopLineWeight", "2"), ("ParagraphBorderBottomLineWeight", "2")]));
        content = Content(document);
        // Top and bottom only (08-para-borders-shading p2): two 2 pt lines.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(content[..content.IndexOf("BT", StringComparison.Ordinal)], @"\n2 w\n"));
    }

    private static string Content(IdmlDocument document) =>
        System.Text.Encoding.Latin1.GetString(TestSupport.Export(document, new PdfExportOptions { Fonts = TestSupport.Fonts(), CompressContent = false }));
}
