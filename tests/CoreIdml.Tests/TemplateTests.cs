using CoreIdml.Dom;

namespace CoreIdml.Tests;

public class TemplateTests
{
    [Fact]
    public void Placeholders_split_across_runs_are_replaced_with_the_first_runs_formatting()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, 100));
        // InDesign often splits text into runs mid-word; simulate "Hola {{nom" + "bre}}!" in two runs.
        frame.Story.AddParagraph()
            .AddText("Hola {{nom", format: new CharacterFormat { PointSize = 14 })
            .AddText("bre}}!", format: new CharacterFormat { PointSize = 9 });

        var count = document.ReplaceText("{{nombre}}", "Lucía");

        Assert.Equal(1, count);
        Assert.Equal("Hola Lucía!", frame.Story.Text);
        var runs = frame.Story.Runs;
        Assert.Equal("Hola Lucía", runs[0].Text);
        Assert.Equal(14, runs[0].PointSize);
        Assert.Equal("!", runs[1].Text);
    }

    [Fact]
    public void Set_text_keeps_the_first_paragraph_and_run_formatting()
    {
        using var document = IdmlDocument.Create();
        var style = document.Styles.AddParagraphStyle("Dato");
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 300, 100));
        frame.Story.AddParagraph(style).AddText("{{valor}}", format: new CharacterFormat { FontStyle = "Bold" });
        frame.Story.AddParagraph("segunda línea");

        frame.Story.SetText("uno\ndos");

        Assert.Equal("uno\ndos", frame.Story.Text);
        var run = Assert.Single(frame.Story.Runs);
        Assert.Equal("Bold", run.FontStyle);
        Assert.Equal(style.Reference, run.ParagraphStyle);
    }

    [Fact]
    public void Fill_matches_frame_names_labels_and_placeholders()
    {
        var photo = TestFiles.TempPath(".png");
        File.WriteAllBytes(photo, TestFiles.Png(64, 48));
        using var document = IdmlDocument.Create();
        var page = document.Pages[0];
        page.AddText(new Rect(0, 0, 200, 40), "{{cliente}}", name: "ignored-name");
        page.AddText(new Rect(0, 50, 200, 40), "placeholder", name: "codigo");
        var labelled = page.AddText(new Rect(0, 100, 200, 40), "x");
        labelled.Label = "fecha";
        page.AddRectangle(new Rect(0, 200, 100, 100), name: "foto");

        var result = document.Fill(new TemplateData
        {
            Text = { ["cliente"] = "ACME", ["codigo"] = "F-001", ["fecha"] = "17/11/2026", ["nada"] = "?" },
            Images = { ["foto"] = photo },
        });

        Assert.Equal(["nada"], result.Missing);
        Assert.Equal("ACME", document.FindPageItem("ignored-name") is TextFrame f ? f.Story.Text : null);
        Assert.Equal("F-001", ((TextFrame)document.FindPageItem("codigo")!).Story.Text);
        Assert.Equal("17/11/2026", labelled.Story.Text);
        Assert.NotNull(((GraphicFrame)document.FindPageItem("foto")!).LinkUri);
        CreateDocumentTests.AssertValid(document);
    }

    [Fact]
    public void Fill_by_xml_tag_on_a_real_InDesign_template()
    {
        var photo = TestFiles.TempPath(".png");
        File.WriteAllBytes(photo, TestFiles.Png(300, 200));
        using var document = IdmlDocument.Open(TestFiles.Article);
        var headline = document.Tags.FindElements("headline").Single();
        Assert.Equal("THE HEADLINE HERE", headline.Text);

        var result = document.Fill(new TemplateData
        {
            Text = { ["headline"] = "Nuevo titular", ["informations"] = "Texto de información" },
            Images = { ["main_picture"] = photo },
        });

        Assert.Empty(result.Missing);
        Assert.Equal(1, result.Filled["headline"]);
        using var reopened = IdmlDocument.Open(document.ToArray());
        Assert.Equal("Nuevo titular", reopened.Tags.FindElements("headline").Single().Text);
        Assert.Equal("Texto de información", reopened.Tags.FindElements("informations").Single().Text);
        // The tag survives the rewrite, so the file still round-trips through InDesign's XML import/export.
        Assert.Contains(reopened.Stories, story => story.Element.Descendants("XMLElement").Any(e => (string?)e.Attribute("MarkupTag") == "XMLTag/headline"));
        var frame = (GraphicFrame)reopened.Tags.FindElements("main_picture").Single().BoundPageItem!;
        Assert.EndsWith(".png", frame.LinkUri, StringComparison.Ordinal);
        CreateDocumentTests.AssertValid(reopened);
    }

    [Fact]
    public void Generated_tags_can_be_filled_later()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 200, 50), "pendiente");
        var box = document.Pages[0].AddRectangle(new Rect(0, 100, 100, 100));
        document.Tags.TagPageItem(frame, "observaciones");
        document.Tags.TagPageItem(box, "foto");

        using var reopened = IdmlDocument.Open(document.ToArray());
        reopened.Fill(new TemplateData { Text = { ["observaciones"] = "Sin novedad" } });

        Assert.Equal("Sin novedad", reopened.Tags.FindElements("observaciones").Single().Text);
        Assert.Equal(box.Id, reopened.Tags.FindElements("foto").Single().ContentId);
        Assert.Contains("observaciones", reopened.Tags.Names);
        CreateDocumentTests.AssertValid(reopened);
    }

    [Fact]
    public void Real_files_read_and_validate()
    {
        using var article = IdmlDocument.Open(TestFiles.Article);
        using var editorial = IdmlDocument.Open(TestFiles.Editorial);

        Assert.True(IdmlValidator.Validate(article).IsValid, IdmlValidator.Validate(article).ToString());
        Assert.True(IdmlValidator.Validate(editorial).IsValid, IdmlValidator.Validate(editorial).ToString());
        Assert.Equal(2, editorial.Pages.Count);
        var image = article.AllPageItems.OfType<GraphicFrame>().Single(frame => frame.LinkUri is not null);
        Assert.EndsWith("default.jpg", image.LinkUri, StringComparison.Ordinal);
        Assert.Contains(editorial.Pages[0].PageItems, item => item is TextFrame);
    }

    [Fact]
    public void Validator_reports_frames_pointing_at_missing_stories()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddTextFrame(new Rect(0, 0, 10, 10));
        frame.Element.SetAttributeValue("ParentStory", "u-missing");
        frame.Element.SetAttributeValue("FillColor", "Color/DoesNotExist");

        var result = IdmlValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "missing-story");
        Assert.Contains(result.Issues, issue => issue.Code == "missing-resource");
    }
}
