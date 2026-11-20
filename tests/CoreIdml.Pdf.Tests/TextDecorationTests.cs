using System.Text.RegularExpressions;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Text strokes, skew, underline and strikethrough options, as InDesign 20 writes them in the doc-samples
/// 07-char-outlined-text, 07-char-basics and 07-char-underline-strike PDFs.
/// </summary>
public sealed partial class TextDecorationTests
{
    private const double Size = 22;

    /// <summary>One run of 22 pt Liberation Serif with the given attributes and Properties children.</summary>
    private static (IdmlDocument Document, TextFrame Frame) Run(Action<IdmlDocument, XElement> format)
    {
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var style = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = Size });
        var frame = document.Pages[0].AddText(new Rect(100, 100, 300, 60), "Mr. Bennet", style);
        format(document, frame.Story.Element.Descendants("CharacterStyleRange").Single());
        return (document, frame);
    }

    private static void Property(XElement run, string name, string value)
    {
        var properties = run.Element("Properties") ?? new XElement("Properties");
        if (properties.Parent is null)
        {
            run.AddFirst(properties);
        }
        properties.Add(new XElement(name, new XAttribute("type", "object"), value));
    }

    private static List<DrawOp> Ops(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story).OpsByFrame[frame.Id];

    private static string Content(IdmlDocument document) =>
        System.Text.Encoding.Latin1.GetString(TestSupport.Export(document, new PdfExportOptions { Fonts = TestSupport.Fonts(), CompressContent = false }));

    [Fact]
    public void An_automatic_underline_takes_the_fonts_post_table_and_goes_under_the_text()
    {
        var (document, frame) = Run((_, run) =>
        {
            run.SetAttributeValue("Underline", "true");
            run.SetAttributeValue("UnderlineWeight", "-9999");
        });

        var ops = Ops(document, frame);
        var rule = ops.OfType<TextRuleOp>().Single();
        var text = ops.OfType<GlyphRunOp>().Single();
        // Liberation Serif: underlineThickness 100, underlinePosition -123 of 2048 (1.074 and 1.321 pt at 22 pt).
        Assert.Equal(Size * 100 / 2048, rule.Weight, 6);
        Assert.Equal(Size * 123 / 2048, rule.From.Y - text.Y, 6);
        Assert.Equal(text.X, rule.From.X, 6);
        Assert.Equal(text.Width, rule.Length, 6);
        Assert.True(ops.IndexOf(rule) < ops.IndexOf(text), "InDesign paints underlines before the text");
    }

    [Fact]
    public void Underline_options_set_weight_offset_colour_and_tint()
    {
        var (document, frame) = Run((document, run) =>
        {
            var red = document.Swatches.AddCmyk("Red", 0, 100, 100, 0);
            run.SetAttributeValue("Underline", "true");
            run.SetAttributeValue("UnderlineWeight", "3");
            run.SetAttributeValue("UnderlineOffset", "6");
            run.SetAttributeValue("UnderlineTint", "50");
            Property(run, "UnderlineColor", red.Reference);
            Property(run, "UnderlineType", "StrokeStyle/$ID/Wavy");
        });

        var ops = Ops(document, frame);
        var rule = ops.OfType<TextRuleOp>().Single();
        Assert.Equal(3, rule.Weight);
        Assert.Equal(6, rule.From.Y - ops.OfType<GlyphRunOp>().Single().Y, 6);
        Assert.Equal([0, 0.5, 0.5, 0], rule.Color.Components);
        Assert.Equal("StrokeStyle/$ID/Wavy", rule.Type);
    }

    [Fact]
    public void A_strikethrough_sits_a_third_of_the_size_up_or_at_its_offset_over_the_text()
    {
        var (document, frame) = Run((_, run) => run.SetAttributeValue("StrikeThru", "true"));
        var ops = Ops(document, frame);
        var rule = ops.OfType<TextRuleOp>().Single();
        var text = ops.OfType<GlyphRunOp>().Single();
        Assert.Equal(Size / 3, text.Y - rule.From.Y, 6);
        Assert.Equal(Size * 100 / 2048, rule.Weight, 6);
        Assert.True(ops.IndexOf(rule) > ops.IndexOf(text), "InDesign paints strikethroughs over the text");

        (document, frame) = Run((_, run) =>
        {
            run.SetAttributeValue("StrikeThru", "true");
            run.SetAttributeValue("StrikeThroughOffset", "-4");
            run.SetAttributeValue("StrikeThroughWeight", "2");
        });
        ops = Ops(document, frame);
        rule = ops.OfType<TextRuleOp>().Single();
        Assert.Equal(-4, ops.OfType<GlyphRunOp>().Single().Y - rule.From.Y, 6);
        Assert.Equal(2, rule.Weight);
    }

    [Fact]
    public void A_dashed_underline_paints_its_gap_colour_then_whole_dashes_of_about_12_pt()
    {
        var (document, frame) = Run((document, run) =>
        {
            var yellow = document.Swatches.AddCmyk("Yellow", 0, 0, 100, 0);
            run.SetAttributeValue("Underline", "true");
            run.SetAttributeValue("UnderlineWeight", "3");
            Property(run, "UnderlineType", "StrokeStyle/$ID/Dashed");
            Property(run, "UnderlineGapColor", yellow.Reference);
        });
        var length = Ops(document, frame).OfType<TextRuleOp>().Single().Length;

        var content = Content(document);
        var dash = DashPattern().Match(content);
        Assert.True(dash.Success);
        var unit = double.Parse(dash.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(dash.Groups[1].Value, dash.Groups[2].Value);
        // A whole number of dashes and gaps, starting and ending with a dash.
        var dashes = (length / unit + 1) / 2;
        Assert.Equal(Math.Round(dashes), dashes, 2);
        Assert.InRange(unit, 9, 15);
        var gap = content.IndexOf("0 0 1 0 K", StringComparison.Ordinal);
        Assert.InRange(gap, 0, dash.Index);
    }

    [Fact]
    public void Text_with_no_fill_is_stroked_at_its_weight()
    {
        var (document, _) = Run((_, run) =>
        {
            run.SetAttributeValue("FillColor", "Swatch/None");
            run.SetAttributeValue("StrokeColor", "Color/Black");
            run.SetAttributeValue("StrokeWeight", "0.5");
        });

        var content = Content(document);
        Assert.Contains("0.5 w", content, StringComparison.Ordinal);
        Assert.Contains("1 Tr", content, StringComparison.Ordinal);
        Assert.Equal(1, TextObjects().Count(content));
    }

    [Fact]
    public void An_outside_stroke_under_a_fill_is_stroked_first_at_twice_its_weight()
    {
        var (document, _) = Run((document, run) =>
        {
            var red = document.Swatches.AddCmyk("Red", 0, 100, 100, 0);
            run.SetAttributeValue("FillColor", red.Reference);
            run.SetAttributeValue("StrokeColor", "Color/Black");
            run.SetAttributeValue("StrokeWeight", "2");
            run.SetAttributeValue("StrokeAlignment", "OutsideAlignment");
            run.SetAttributeValue("EndJoin", "RoundEndJoin");
        });

        var content = Content(document);
        var stroke = content.IndexOf("1 Tr", StringComparison.Ordinal);
        Assert.InRange(content.IndexOf("4 w\n1 j", StringComparison.Ordinal), 0, stroke);
        Assert.InRange(content.IndexOf("0 1 1 0 k", stroke, StringComparison.Ordinal), stroke, int.MaxValue);
        Assert.Equal(2, TextObjects().Count(content));
    }

    [Fact]
    public void Skew_shears_the_text_matrix_about_the_baseline()
    {
        var (document, _) = Run((_, run) => run.SetAttributeValue("Skew", "15"));

        // 07-char-basics p5: tan 15° = 0.2679 (InDesign writes 24 0 6.4308 24 for 24 pt; the size is in Tf here).
        var content = Content(document);
        Assert.Matches(@"1 0 0\.2679 -1 -?[\d.]+ -?[\d.]+ Tm", content);
    }

    [GeneratedRegex(@"\[([\d.]+) ([\d.]+)\] 0 d")]
    private static partial Regex DashPattern();

    [GeneratedRegex(@"\bBT\b")]
    private static partial Regex TextObjects();
}
