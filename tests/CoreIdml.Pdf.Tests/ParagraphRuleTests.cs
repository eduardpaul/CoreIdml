using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Paragraph rules, as InDesign 20 draws them in the doc-samples 08-para-rules PDF.
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
    public void Rules_take_no_space()
    {
        var document = NewDocument();
        var plain = Style(document, "Plain", []);
        var decorated = Style(document, "Decorated", [("RuleAbove", "true"), ("RuleAboveOffset", "20"), ("RuleAboveLineWeight", "6"),
            ("RuleBelow", "true"), ("RuleBelowOffset", "12")]);
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
}
