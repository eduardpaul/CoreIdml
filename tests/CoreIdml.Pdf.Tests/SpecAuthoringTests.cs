using CoreIdml.Dom;
using CoreIdml.Json;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>Documents built from a JSON spec draw the features the spec authors: what an agent writes is what it sees.</summary>
public sealed class SpecAuthoringTests
{
    private const string Text = "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife. "
        + "However little known the feelings or views of such a man may be on his first entering a neighbourhood, this truth is so well "
        + "fixed in the minds of the surrounding families, that he is considered the rightful property of some one or other of their daughters.";

    private static StoryLayout Layout(IdmlDocument document, string frameName) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(((TextFrame)document.AllPageItems.Single(item => item.Name == frameName)).Story);

    private static List<GlyphRunOp> Runs(StoryLayout layout) => layout.OpsByFrame.Values.SelectMany(ops => ops).OfType<GlyphRunOp>().ToList();

    [Fact]
    public void Drop_caps_nested_and_grep_styles_from_the_spec_are_drawn()
    {
        using var document = IdmlJson.CreateDocument($$"""
        {
          "colors": [ { "name": "Brick", "cmyk": [0, 80, 90, 20] } ],
          "characterStyles": [
            { "name": "Drop", "font": "Liberation Sans", "fontStyle": "Bold", "color": "Brick" },
            { "name": "Lead", "fontStyle": "Bold" },
            { "name": "Number", "color": "Brick" }
          ],
          "paragraphStyles": [
            { "name": "Body", "font": "Liberation Serif", "size": 11, "leading": 14 },
            { "name": "Opener", "basedOn": "Body", "dropCapLines": 3, "dropCapStyle": "Drop",
              "nestedStyles": [ { "style": "Lead", "through": "words", "count": 4 } ],
              "grepStyles": [ { "style": "Number", "expression": "\\d+" } ] }
          ],
          "pages": [ { "items": [ { "type": "text", "name": "story", "x": 36, "y": 36, "width": 250, "height": 400,
            "paragraphs": [ { "style": "Opener", "text": "{{Text}} In 1811 they were 24." } ] } ] } ]
        }
        """);

        var runs = Runs(Layout(document, "story"));

        var cap = runs.Single(run => run.Size > 11);
        Assert.Equal("I", cap.Text);
        Assert.Equal("Liberation Sans Bold", cap.Font.DisplayName);
        Assert.Equal([0, 0.8, 0.9, 0.2], cap.Color!.Components);
        var lead = string.Concat(runs.Where(run => run.Size == 11 && run.Font.DisplayName == "Liberation Serif Bold").Select(run => run.Text));
        Assert.Equal("t is a truth ", lead);
        Assert.Equal(["1811", "24"], runs.Where(run => run.Size == 11 && run.Color?.Components[1] == 0.8).Select(run => run.Text.Trim()));
    }

    [Fact]
    public void Text_stroke_skew_and_underline_options_from_the_spec_are_drawn()
    {
        using var document = IdmlJson.CreateDocument("""
        {
          "colors": [ { "name": "Red", "cmyk": [0, 100, 100, 0] } ],
          "characterStyles": [ { "name": "Outline", "color": "None", "stroke": "Black", "strokeWeight": 1.5 } ],
          "pages": [ { "items": [ { "type": "text", "name": "story", "x": 36, "y": 36, "width": 400, "height": 200, "paragraphs": [ { "runs": [
            { "text": "Hollow ", "style": "Outline", "size": 30 },
            { "text": "leaning ", "skew": 15 },
            { "text": "marked", "underline": true, "underlineWeight": 3, "underlineOffset": 4, "underlineColor": "Red", "underlineType": "wavy" },
            { "text": " gone", "strikeThrough": true, "strikeThroughWeight": 2, "strikeThroughColor": "Red" }
          ] } ] } ] } ]
        }
        """);

        var ops = Layout(document, "story").OpsByFrame.Values.Single();
        var runs = ops.OfType<GlyphRunOp>().ToList();

        var hollow = runs.Single(run => run.Text.StartsWith("Hollow", StringComparison.Ordinal));
        Assert.Null(hollow.Color);
        Assert.Equal(1.5, hollow.Stroke!.Weight);
        Assert.Equal(15, runs.Single(run => run.Text.StartsWith("leaning", StringComparison.Ordinal)).Skew);
        var rules = ops.OfType<TextRuleOp>().ToList();
        var underline = rules.Single(rule => rule.Type == "StrokeStyle/$ID/Wavy");
        Assert.Equal(3, underline.Weight);
        Assert.Equal(4, underline.From.Y - runs.Single(run => run.Text.StartsWith("marked", StringComparison.Ordinal)).Y, 6);
        Assert.Equal([0, 1, 1, 0], underline.Color.Components);
        var strike = rules.Single(rule => rule != underline);
        Assert.Equal(2, strike.Weight);
        Assert.Equal([0, 1, 1, 0], strike.Color.Components);
    }

    [Fact]
    public void Paragraphs_aligned_to_the_spec_baseline_grid_sit_on_its_lines()
    {
        using var document = IdmlJson.CreateDocument($$"""
        {
          "page": { "baselineGrid": { "start": 36, "increment": 14 } },
          "paragraphStyles": [ { "name": "Body", "font": "Liberation Serif", "size": 11, "leading": 13, "spaceAfter": 5, "alignToBaselineGrid": true } ],
          "pages": [ { "items": [ { "type": "text", "name": "story", "x": 36, "y": 36, "width": 250, "height": 600,
            "paragraphs": [ { "style": "Body", "text": "{{Text}}" }, { "style": "Body", "text": "{{Text}}" } ] } ] } ]
        }
        """);

        var lines = Layout(document, "story").Lines;

        // 13 pt leading and 5 pt after the first paragraph, each line moved down to the next 14 pt grid line.
        Assert.True(lines.Count > 8);
        Assert.All(lines.Zip(lines.Skip(1)), pair => Assert.Equal(0, (pair.Second.Baseline - pair.First.Baseline) % 14, 6));
        Assert.Contains(lines.Zip(lines.Skip(1)), pair => Math.Abs(pair.Second.Baseline - pair.First.Baseline - 28) < 1e-6);
    }

    [Fact]
    public void Span_and_split_columns_from_the_spec_are_set_across_and_within_the_columns()
    {
        using var document = IdmlJson.CreateDocument($$"""
        {
          "paragraphStyles": [
            { "name": "Body", "font": "Liberation Serif", "size": 10, "leading": 12 },
            { "name": "Head", "basedOn": "Body", "size": 18, "leading": 22, "spanColumns": 0 },
            { "name": "Split", "basedOn": "Body", "splitColumns": 2, "splitGutter": 10 }
          ],
          "pages": [ { "items": [ { "type": "text", "name": "story", "x": 36, "y": 36, "width": 520, "height": 600, "columns": 2, "columnGutter": 20,
            "paragraphs": [ { "style": "Head", "text": "A headline across both columns of the frame" }, { "style": "Split", "text": "{{Text}}" }, { "style": "Body", "text": "{{Text}}" } ] } ] } ]
        }
        """);

        var lines = Layout(document, "story").Lines;

        // The frame's columns are 250 wide: the headline is wider on one line; the split paragraph is set in two
        // sub-columns of 120, the second 130 right of the first, then the body goes on in the frame's columns.
        Assert.True(lines[0].Width > 250);
        var left = lines.Min(line => line.Left);
        var second = lines.Where(line => Math.Abs(line.Left - left - 130) < 1e-6).ToList();
        Assert.True(second.Count >= 3);
        Assert.All(second, line => Assert.True(line.Width <= 120));
        Assert.Equal(left, lines[^1].Left, 6);
    }
}
