using CoreIdml.Dom;

namespace CoreIdml.Tests;

public class CreateDocumentTests
{
    [Fact]
    public void Blank_document_has_the_requested_pages_and_is_valid()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A5, PageCount = 3 });

        using var reopened = IdmlDocument.Open(document.ToArray());

        Assert.Equal(3, reopened.Pages.Count);
        Assert.Equal(["1", "2", "3"], reopened.Pages.Select(page => page.Name));
        Assert.All(reopened.Pages, page => Assert.Equal(PageSizes.A5.Width, page.Size.Width, 6));
        Assert.Single(reopened.MasterSpreads);
        AssertValid(reopened);
    }

    [Fact]
    public void Facing_pages_are_laid_out_like_InDesign_does()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { FacingPages = true, PageCount = 4 });

        var spreads = document.Spreads;

        // Page 1 alone on the right, then 2-3 as a spread, then 4 alone on the left.
        Assert.Equal([1, 2, 1], spreads.Select(spread => spread.Pages.Count));
        Assert.Equal(0, spreads[0].Pages[0].Transform.Tx);
        Assert.Equal(-document.PageSize.Width, spreads[1].Pages[0].Transform.Tx, 6);
        Assert.Equal(0, spreads[1].Pages[1].Transform.Tx);
        Assert.True(spreads[2].Pages[0].IsLeftPage);
        AssertValid(document);
    }

    [Fact]
    public void Text_frames_keep_text_styles_and_position_after_a_round_trip()
    {
        using var document = IdmlDocument.Create();
        var brand = document.Swatches.AddCmyk("Marca", 0, 90, 85, 0);
        var title = document.Styles.AddParagraphStyle("Título", new ParagraphFormat
        {
            FontFamily = "Arial",
            FontStyle = "Bold",
            PointSize = 24,
            Leading = 28,
            FillColor = brand,
            Justification = Justification.Center,
        });
        var strong = document.Styles.AddCharacterStyle("Fuerte", new CharacterFormat { FontStyle = "Bold" });
        var page = document.Pages[0];
        var bounds = new Rect(Units.Millimeters(20), Units.Millimeters(20), Units.Millimeters(170), Units.Millimeters(30));

        var frame = page.AddTextFrame(bounds, "cabecera");
        frame.Story.AddParagraph("Ficha de inspección", title);
        frame.Story.AddParagraph().AddText("Estado: ").AddText("aprobado", strong);

        using var reopened = IdmlDocument.Open(document.ToArray());
        var copy = Assert.IsType<TextFrame>(reopened.FindPageItem("cabecera"));
        Assert.Equal("Ficha de inspección\nEstado: aprobado", copy.Story.Text);
        Assert.Equal(bounds.X, copy.Bounds.X, 6);
        Assert.Equal(bounds.Y, copy.Bounds.Y, 6);
        Assert.Equal(bounds.Width, copy.Bounds.Width, 6);
        Assert.Equal(title.Reference, copy.Story.Runs[0].ParagraphStyle);
        Assert.Equal(strong.Reference, copy.Story.Runs[^1].CharacterStyle);
        Assert.NotNull(reopened.Styles.FindParagraphStyle("Título"));
        Assert.NotNull(reopened.Swatches.Find("Marca"));
        AssertValid(reopened);
    }

    [Fact]
    public void Tables_hold_cell_text_and_grow_rows()
    {
        using var document = IdmlDocument.Create();
        var frame = document.Pages[0].AddTextFrame(new Rect(36, 36, 400, 300));

        var table = frame.AddTable(2, 3, new TableOptions { HeaderRows = 1, HeaderFill = Swatch.Black });
        table.SetRow(0, "Campo", "Valor", "Nota");
        table.SetRow(1, "Altura", "12 m", "medida");
        var added = table.AddRow("Ancho", "4 m", string.Empty);

        Assert.Equal(2, added);
        Assert.Equal(3, table.RowCount);
        Assert.Equal("Ancho", table[2, 0].Text);
        Assert.Equal("12 m", table[1, 1].Text);
        Assert.Equal(400.0 / 3, double.Parse(table.Element.Elements("Column").First().Attribute("SingleColumnWidth")!.Value, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(string.Empty, frame.Story.Text);
        AssertValid(document);
    }

    [Fact]
    public void Images_are_scaled_from_their_real_pixel_size_and_resolution()
    {
        var path = TestFiles.TempPath(".png");
        File.WriteAllBytes(path, TestFiles.Png(400, 200, dpi: 144)); // 200 x 100 pt
        using var document = IdmlDocument.Create();

        var fill = document.Pages[0].AddImage(new Rect(0, 0, 100, 100), path, ImageFit.Fill, "foto");
        var fit = document.Pages[0].AddImage(new Rect(0, 200, 100, 100), path, ImageFit.Fit);

        var fillTransform = Matrix.Parse(fill.Graphic!.Attribute("ItemTransform")!.Value);
        Assert.Equal(1.0, fillTransform.A, 6);   // 100 pt high frame / 100 pt image height
        Assert.Equal(-100.0, fillTransform.Tx, 6); // 200 pt wide image centred on a 100 pt frame (inner origin at centre)
        var fitTransform = Matrix.Parse(fit.Graphic!.Attribute("ItemTransform")!.Value);
        Assert.Equal(0.5, fitTransform.A, 6);
        Assert.Equal(TestFiles.LinkUri(path), fill.LinkUri);
        Assert.EndsWith(".png", fill.LinkUri, StringComparison.Ordinal);
        AssertValid(document);
    }

    [Fact]
    public void Threaded_frames_share_one_story()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageCount = 2 });
        var first = document.Pages[0].AddTextFrame(new Rect(36, 36, 200, 100), "uno");
        var second = document.Pages[1].AddTextFrame(new Rect(36, 36, 200, 100), "dos");
        var storiesBefore = document.Stories.Count;

        first.ThreadTo(second);
        first.Story.SetText("Texto largo que continúa en la página dos.");

        Assert.Equal(storiesBefore - 1, document.Stories.Count);
        Assert.Equal(first.StoryId, second.StoryId);
        Assert.Equal(["uno", "dos"], first.Story.Frames.Select(frame => frame.Name));
        AssertValid(document);
    }

    [Fact]
    public void Shapes_lines_rotation_and_layers()
    {
        using var document = IdmlDocument.Create();
        var page = document.Pages[0];
        var notes = document.Layers.Add("Notas");

        var box = page.AddRectangle(new Rect(10, 10, 100, 50), new ShapeStyle { Fill = Swatch.Black, FillTint = 20, CornerRadius = 4 });
        var ellipse = page.AddEllipse(new Rect(200, 10, 80, 80), new ShapeStyle { Stroke = Swatch.Black, StrokeWeight = 2, Rotation = 90 });
        var line = page.AddLine(new Point(10, 300), new Point(300, 300));
        line.MoveToLayer(notes);

        Assert.Equal(100, box.Bounds.Width, 6);
        Assert.Equal(80, ellipse.Bounds.Width, 3); // rotating a circle keeps its box
        Assert.Equal(290, line.Bounds.Width, 6);
        Assert.Equal(notes.Id, line.LayerId);
        Assert.Equal(3, page.PageItems.Count);
        AssertValid(document);
    }

    [Fact]
    public void New_ids_never_collide_with_ids_in_an_opened_file()
    {
        using var document = IdmlDocument.Open(TestFiles.Editorial);

        var page = document.Pages[0];
        page.AddTextFrame(new Rect(0, 0, 50, 50), "nuevo");
        page.AddRectangle(new Rect(0, 60, 50, 50));
        document.AddPage();

        AssertValid(document);
    }

    internal static void AssertValid(IdmlDocument document)
    {
        var result = IdmlValidator.Validate(document);
        Assert.True(result.IsValid, result.ToString());
        var bytes = IdmlValidator.ValidatePackageBytes(document.ToArray());
        Assert.True(bytes.IsValid, bytes.ToString());
    }
}
