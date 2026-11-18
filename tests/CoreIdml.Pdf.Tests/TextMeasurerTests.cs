using CoreIdml.Dom;

namespace CoreIdml.Pdf.Tests;

/// <summary>Sizing frames to their text before export, with the export's own composer.</summary>
public sealed class TextMeasurerTests
{
    [Fact]
    public void A_frame_sized_to_its_measured_height_shows_every_line()
    {
        using var doc = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var style = doc.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12 });
        var text = string.Join(" ", Enumerable.Repeat("Estado de conservación de la superficie.", 8));
        var frame = doc.Pages[0].AddText(new Rect(50, 50, 150, 10), text, style);
        frame.Inset = Spacing.All(4);

        using var measurer = new TextMeasurer(doc, TestSupport.ExportOptions().Fonts);
        var measure = measurer.Measure(frame);

        Assert.True(measure.Lines >= 4, $"{measure.Lines} lines");
        Assert.InRange(measure.Height, (measure.Lines * 12) + 8 - 12, (measure.Lines * 12) + 8 + 6);
        Assert.True(measure.Width <= 142 + 0.01);

        frame.SetBounds(new Rect(50, 50, 150, measure.Height));
        using var output = new MemoryStream();
        var result = IdmlPdf.Export(doc, output, TestSupport.ExportOptions());
        Assert.Empty(result.OversetStories);

        frame.SetBounds(new Rect(50, 50, 150, measure.Height - 14));
        Assert.NotEmpty(IdmlPdf.Export(doc, new MemoryStream(), TestSupport.ExportOptions()).OversetStories);
    }

    [Fact]
    public void One_line_width_is_the_natural_width_of_the_text()
    {
        using var doc = IdmlDocument.Create();
        var style = doc.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Sans", PointSize = 10 });
        var shortFrame = doc.Pages[0].AddText(new Rect(0, 0, 1000, 20), "pH", style);
        var longFrame = doc.Pages[0].AddText(new Rect(0, 30, 1000, 20), "pH inicial de la muestra", style);

        using var measurer = new TextMeasurer(doc, TestSupport.ExportOptions().Fonts);
        var a = measurer.Measure(shortFrame);
        var b = measurer.Measure(longFrame);

        Assert.Equal(1, a.Lines);
        Assert.True(b.Width > a.Width * 4);
        Assert.InRange(b.Width, 80, 140);
    }

    [Fact]
    public void Check_fit_reports_how_much_overflows_and_growing_by_that_much_fits()
    {
        using var doc = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var style = doc.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12 });
        var text = string.Join(" ", Enumerable.Repeat("Estado de conservación de la superficie.", 8));
        var frame = doc.Pages[0].AddText(new Rect(50, 50, 150, 30), text, style);
        var fitting = doc.Pages[0].AddText(new Rect(50, 300, 150, 30), "Corto", style);

        using var measurer = new TextMeasurer(doc, TestSupport.ExportOptions().Fonts);
        var fit = measurer.CheckFit(frame.Story);
        var all = measurer.CheckFit(doc);

        Assert.True(fit.Overset);
        Assert.True(fit.OverflowLines >= 2, $"{fit.OverflowLines} lines");
        Assert.InRange(fit.OverflowPoints, (fit.OverflowLines - 1) * 12, (fit.OverflowLines + 1) * 12);
        Assert.True(fit.OversetCharacters > 0);
        Assert.Equal(fit.Lines, Assert.Single(fit.Frames).Lines);
        Assert.Equal(2, all.Count);
        Assert.False(all.Single(story => story.StoryId == fitting.StoryId).Overset);
        Assert.Equal(0, all.Single(story => story.StoryId == fitting.StoryId).OverflowPoints);

        frame.SetBounds(new Rect(50, 50, 150, 30 + fit.OverflowPoints + 0.5));
        using var grown = new TextMeasurer(doc, TestSupport.ExportOptions().Fonts);
        Assert.False(grown.CheckFit(frame.Story).Overset);
        Assert.Empty(IdmlPdf.Export(doc, new MemoryStream(), TestSupport.ExportOptions()).OversetStories);
    }

    [Fact]
    public void Check_fit_follows_threads_to_the_last_frame()
    {
        using var doc = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var style = doc.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Serif", PointSize = 10, Leading = 12 });
        var first = doc.Pages[0].AddText(new Rect(50, 50, 150, 40), string.Join(" ", Enumerable.Repeat("Texto que sigue en otro marco.", 12)), style);
        var second = doc.Pages[0].AddTextFrame(new Rect(250, 50, 150, 40));
        first.ThreadTo(second);

        using var measurer = new TextMeasurer(doc, TestSupport.ExportOptions().Fonts);
        var fit = measurer.CheckFit(first.Story);

        Assert.True(fit.Overset);
        Assert.Equal([first.Id, second.Id], fit.Frames.Select(frame => frame.FrameId));
        Assert.All(fit.Frames, frame => Assert.True(frame.Lines >= 3));
        Assert.True(fit.OverflowLines > 0);
    }
}
