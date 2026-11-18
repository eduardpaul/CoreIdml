using CoreIdml;
using CoreIdml.Dom;

namespace CoreIdml.Samples;

/// <summary>
/// The ficha (data sheet) demo: styles, CMYK swatches, frames, a table, an optional photo, a rule and a two-column
/// story threaded across two pages. Shared by the sample app and the PDF tests (linked into CoreIdml.Pdf.Tests).
/// </summary>
public static class FichaDemo
{
    public static IdmlDocument Build(string? photo)
    {
        var document = IdmlDocument.Create(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margins = Spacing.All(Units.Millimeters(15)),
            PageCount = 2,
        });

        var brand = document.Swatches.AddCmyk("Marca", 75, 45, 0, 10);
        var light = document.Swatches.AddCmyk("Marca claro", 10, 5, 0, 0);
        var title = document.Styles.AddParagraphStyle("Título", new ParagraphFormat
        {
            FontFamily = "Minion Pro",
            FontStyle = "Bold",
            PointSize = 22,
            Leading = 26,
            FillColor = Swatch.Paper, // white on the brand-coloured header band
            SpaceAfter = 6,
        });
        var body = document.Styles.AddParagraphStyle("Cuerpo", new ParagraphFormat { PointSize = 10, Leading = 13, Justification = Justification.LeftJustified });
        var label = document.Styles.AddParagraphStyle("Etiqueta", new ParagraphFormat { PointSize = 8, FontStyle = "Bold", Capitalization = Capitalization.AllCaps }, body);
        var strong = document.Styles.AddCharacterStyle("Destacado", new CharacterFormat { FontStyle = "Bold", FillColor = brand });

        var page = document.Pages[0];
        var content = page.ContentBounds;
        double Mm(double value) => Units.Millimeters(value);

        page.AddRectangle(new Rect(0, 0, page.Size.Width, Mm(32)), new ShapeStyle { Fill = brand });
        page.AddText(new Rect(content.X, Mm(10), content.Width, Mm(15)), "Ficha de inspección F-001", title, "titulo");

        var intro = page.AddTextFrame(new Rect(content.X, Mm(40), content.Width, Mm(25)), "resumen");
        intro.Story.AddParagraph(body)
            .AddText("Proyecto: ")
            .AddText("Edificio Central", strong)
            .AddText(". Inspección realizada el 17/11/2026 por el equipo de campo. El elemento se encuentra en ")
            .AddText("buen estado", strong)
            .AddText(" y no requiere intervención inmediata.");

        var data = page.AddTextFrame(new Rect(content.X, Mm(70), Mm(100), Mm(60)), "datos");
        var table = data.AddTable(4, 2, new TableOptions { HeaderRows = 1, HeaderFill = light, CellParagraphStyle = body, HeaderParagraphStyle = label, StrokeWeight = 0.5, CellInset = 3 });
        table.SetRow(0, "Campo", "Valor");
        table.SetRow(1, "Código", "F-001");
        table.SetRow(2, "Altura", "12,5 m");
        table.SetRow(3, "Estado", "Aprobado");
        table.AddRow("Revisión", "Anual");

        var imageBounds = new Rect(content.X + Mm(105), Mm(70), content.Width - Mm(105), Mm(60));
        if (photo is not null)
        {
            page.AddImage(imageBounds, photo, ImageFit.Fill, "foto");
        }
        else
        {
            page.AddRectangle(imageBounds, new ShapeStyle { Fill = light, Stroke = brand, StrokeWeight = 0.5 }, "foto");
        }

        page.AddLine(new Point(content.X, Mm(140)), new Point(content.Right, Mm(140)), new ShapeStyle { Stroke = brand, StrokeWeight = 0.75 });

        // Long text flowing from page 1 into page 2 through threaded frames.
        var notes = page.AddTextFrame(new Rect(content.X, Mm(145), content.Width, content.Bottom - Mm(145)), "notas");
        notes.Columns = 2;
        notes.ColumnGutter = Mm(5);
        var more = document.Pages[1].AddTextFrame(content, "notas-cont");
        more.Columns = 2;
        notes.ThreadTo(more);
        for (var i = 1; i <= 40; i++)
        {
            notes.Story.AddParagraph($"Observación {i}: texto de ejemplo para comprobar el enlazado de marcos y las columnas. Lorem ipsum dolor sit amet, consectetur adipiscing elit.", body);
        }

        var footer = document.Pages[1].AddTextFrame(new Rect(content.X, content.Bottom + Mm(3), content.Width, Mm(6)), "pie");
        footer.Story.AddParagraph(label).AddText("Página ").AddPageNumber();
        return document;
    }
}
