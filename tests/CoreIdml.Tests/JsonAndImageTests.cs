using System.Text.Json;
using CoreIdml.Dom;
using CoreIdml.Json;

namespace CoreIdml.Tests;

public class JsonAndImageTests
{
    [Fact]
    public void A_json_spec_builds_a_complete_valid_document()
    {
        var photo = TestFiles.TempPath(".png");
        File.WriteAllBytes(photo, TestFiles.Png(120, 80));
        var json = $$"""
        {
          "unit": "mm",
          "page": { "size": "A4", "margins": { "all": 15 } },
          "colors": [ { "name": "Marca", "hex": "#2E86C1" } ],
          "paragraphStyles": [
            { "name": "Título", "font": "Arial", "fontStyle": "Bold", "size": 20, "color": "Marca" },
            { "name": "Cuerpo", "size": 10, "leading": 13, "align": "justify" }
          ],
          "characterStyles": [ { "name": "Negrita", "fontStyle": "Bold" } ],
          "pages": [
            { "items": [
              { "type": "text", "name": "titulo", "x": 15, "y": 15, "width": 180, "height": 15, "text": "Ficha 001", "style": "Título" },
              { "type": "text", "x": 15, "y": 35, "width": 180, "height": 40,
                "paragraphs": [ { "style": "Cuerpo", "runs": [ { "text": "Estado: " }, { "text": "OK", "style": "Negrita", "color": "Marca" } ] } ] },
              { "type": "image", "name": "foto", "x": 15, "y": 80, "width": 90, "height": 60, "path": {{JsonSerializer.Serialize(photo)}}, "fit": "fit" },
              { "type": "rectangle", "x": 110, "y": 80, "width": 85, "height": 60, "fill": "Marca", "fillTint": 15, "cornerRadius": 3 },
              { "type": "line", "x": 15, "y": 145, "x2": 195, "y2": 145, "stroke": "Marca", "strokeWeight": 0.5 },
              { "type": "table", "name": "datos", "x": 15, "y": 150, "width": 180, "height": 60, "headerRows": 1, "headerFill": "Marca",
                "rows": [ ["Campo", "Valor"], ["Altura", "12 m"] ], "columnWidths": [60, 120] }
            ] },
            { "items": [ { "type": "text", "x": 15, "y": 15, "width": 50, "height": 10, "text": "Página 2" } ] }
          ]
        }
        """;

        using var document = IdmlJson.CreateDocument(json);
        var summary = IdmlJson.Summarize(document);

        Assert.Equal(2, summary.Pages!.Count);
        Assert.Equal(6, summary.Pages[0].Items.Count);
        var title = summary.Pages[0].Items.Single(item => item.Name == "titulo");
        Assert.Equal(Units.Millimeters(15), title.X, 3);
        Assert.Equal("Ficha 001", title.Text);
        Assert.Contains("Título", summary.ParagraphStyles!);
        Assert.Contains("Marca", summary.Swatches!);
        var table = ((TextFrame)document.FindPageItem("datos")!).Story.Tables.Single();
        Assert.Equal("12 m", table[1, 1].Text);
        CreateDocumentTests.AssertValid(document);
    }

    [Fact]
    public void Fill_json_reports_filled_and_missing_keys()
    {
        using var document = IdmlDocument.Create();
        document.Pages[0].AddText(new Rect(0, 0, 100, 20), "{{a}}");

        var report = JsonElement.Parse(IdmlJson.Fill(document, """{ "text": { "a": "1", "b": "2" } }"""));

        Assert.Equal(1, report.GetProperty("filled").GetProperty("a").GetInt32());
        Assert.Equal("b", report.GetProperty("missing")[0].GetString());
    }

    [Fact]
    public void Summary_and_validation_serialize_to_json()
    {
        using var document = IdmlDocument.Open(TestFiles.Article);

        var summary = JsonElement.Parse(IdmlJson.SummarizeToJson(document));
        var validation = JsonElement.Parse(IdmlJson.ValidateToJson(document));

        Assert.Equal(1, summary.GetProperty("pages").GetArrayLength());
        Assert.True(summary.GetProperty("stories").GetArrayLength() >= 3);
        Assert.True(validation.GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public void Bad_specs_fail_with_a_readable_message()
    {
        var exception = Assert.Throws<IdmlException>(() => IdmlJson.CreateDocument("""{ "pages": [ { "items": [ { "type": "text", "text": "x", "style": "Nope" } ] } ] }"""));

        Assert.Contains("Nope", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(72)]
    [InlineData(300)]
    public void Jpeg_headers_give_size_and_resolution(int dpi)
    {
        var image = ImageSource.FromBytes(TestFiles.JpegHeader(640, 480, dpi), "file:/x.jpg");

        Assert.Equal(640, image.PixelWidth);
        Assert.Equal(480, image.PixelHeight);
        Assert.Equal(dpi, image.DpiX);
        Assert.Equal(640 * 72.0 / dpi, image.WidthPoints, 6);
        Assert.Equal(ImageFormat.Jpeg, image.Format);
    }

    [Fact]
    public void Png_headers_give_size_and_resolution()
    {
        var image = ImageSource.FromBytes(TestFiles.Png(30, 20, dpi: 300), "file:/x.png");

        Assert.Equal(30, image.PixelWidth);
        Assert.Equal(300, image.DpiX, 0);
        Assert.Equal(ImageFormat.Png, image.Format);
    }

    [Fact]
    public void Unknown_image_formats_are_rejected()
    {
        Assert.Throws<IdmlException>(() => ImageSource.FromBytes("GIF89a....."u8, "file:/x.gif"));
    }
}
