using CoreIdml;
using CoreIdml.Dom;

namespace CoreIdml.Samples;

/// <summary>
/// The sample files of the Claude plugin (plugins/coreidml/samples), for its README's example prompts:
/// a two-page brochure whose body text overflows its frame, and a data sheet template with a JSON file to fill it.
/// </summary>
public static class PluginSamples
{
    private const string Font = "Arial";

    public static void Write(string outDir)
    {
        Directory.CreateDirectory(outDir);
        using (var brochure = Brochure())
        {
            Save(brochure, Path.Combine(outDir, "brochure.idml"));
        }
        using (var template = DatasheetTemplate())
        {
            Save(template, Path.Combine(outDir, "datasheet-template.idml"));
        }
        File.WriteAllText(Path.Combine(outDir, "datasheet-data.json"), """
            {
              "text": {
                "product": "Trail Lamp X200",
                "price": "EUR 89.00",
                "description": "A rechargeable camping lamp with three light modes, a magnetic base and a hook. One charge lasts up to 40 hours on the lowest setting.",
                "date": "1 March 2027"
              }
            }

            """.ReplaceLineEndings("\n"));
    }

    private static double Mm(double value) => Units.Millimeters(value);

    private static IdmlDocument Brochure()
    {
        var document = IdmlDocument.Create(new DocumentOptions
        {
            PageSize = PageSizes.A4,
            Margins = Spacing.All(Mm(15)),
            PageCount = 2,
        });
        var accent = document.Swatches.AddCmyk("Forest", 80, 20, 70, 20);
        var pale = document.Swatches.AddCmyk("Forest light", 15, 0, 15, 0);
        var headline = document.Styles.AddParagraphStyle("Headline", new ParagraphFormat { FontFamily = Font, FontStyle = "Bold", PointSize = 28, Leading = 32, FillColor = accent, SpaceAfter = 4 });
        var body = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = Font, PointSize = 11, Leading = 15, SpaceAfter = 6 });
        var caption = document.Styles.AddParagraphStyle("Caption", new ParagraphFormat { FontFamily = Font, FontStyle = "Italic", PointSize = 9, Leading = 12 });

        var first = document.Pages[0];
        var content = first.ContentBounds;
        first.AddText(new Rect(content.X, content.Y, content.Width, Mm(25)), "Autumn Walks 2026", headline, "headline");
        first.AddRectangle(new Rect(content.X, Mm(45), content.Width, Mm(90)), new ShapeStyle { Fill = pale, Stroke = accent, StrokeWeight = 0.75 }, "photo");
        first.AddText(new Rect(content.X, Mm(137), content.Width, Mm(8)), "Photo: the river path at Low Bridge, early October.", caption, "caption");

        // Deliberately too much text for its frame, so the prompts have overset text to find and fix.
        var intro = first.AddTextFrame(new Rect(content.X, Mm(150), content.Width, Mm(45)), "intro");
        string[] paragraphs =
        [
            "Six guided walks through the valley this autumn, from an easy riverside stroll to a full day on the ridge. Every walk starts at the visitor centre and is led by one of our volunteer rangers.",
            "Bring sturdy shoes, a waterproof jacket and something to drink. Dogs on a lead are welcome on every walk except the ridge route, which crosses grazing land.",
            "Walks run in all weathers. If a route has to change because of flooding or high wind, we will tell you at the start and take the sheltered path through the woods instead.",
            "Places are free but limited to twenty people a walk, so please book at the visitor centre or by phone. Children under twelve walk with an adult.",
            "After each walk, the café at the visitor centre offers walkers a hot drink at half price. Show your booking card at the counter.",
        ];
        foreach (var paragraph in paragraphs)
        {
            intro.Story.AddParagraph(paragraph, body);
        }

        var second = document.Pages[1];
        second.AddText(new Rect(content.X, content.Y, content.Width, Mm(15)), "Programme", headline, "programme-title");
        var programme = second.AddTextFrame(new Rect(content.X, Mm(35), content.Width, Mm(120)), "programme");
        var table = programme.AddTable(7, 3, new TableOptions { HeaderRows = 1, HeaderFill = pale, CellParagraphStyle = body, HeaderParagraphStyle = body, StrokeWeight = 0.5, CellInset = 3 });
        table.SetRow(0, "Date", "Walk", "Length");
        table.SetRow(1, "4 October", "Riverside stroll", "4 km");
        table.SetRow(2, "11 October", "Old mill and orchards", "7 km");
        table.SetRow(3, "18 October", "Beech woods", "6 km");
        table.SetRow(4, "25 October", "Ridge route", "14 km");
        table.SetRow(5, "1 November", "Lakes loop", "9 km");
        table.SetRow(6, "8 November", "Lantern walk (evening)", "3 km");
        second.AddRectangle(new Rect(content.Right - Mm(40), content.Bottom - Mm(25), Mm(40), Mm(25)), new ShapeStyle { Stroke = accent, StrokeWeight = 1 }, "logo");
        second.AddText(new Rect(content.X, content.Bottom - Mm(25), Mm(110), Mm(25)), "Valley Visitor Centre\nOpen every day from 9:00 to 17:00", body, "contact");
        return document;
    }

    private static IdmlDocument DatasheetTemplate()
    {
        // As a designer would make it: named frames, a tagged frame, and a {{placeholder}} inside running text.
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A5, Margins = Spacing.All(Mm(12)) });
        var title = document.Styles.AddParagraphStyle("Product", new ParagraphFormat { FontFamily = Font, FontStyle = "Bold", PointSize = 20, Leading = 24 });
        var body = document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = Font, PointSize = 10, Leading = 14 });
        var price = document.Styles.AddParagraphStyle("Price", new ParagraphFormat { FontFamily = Font, FontStyle = "Bold", PointSize = 14, Leading = 18 });

        var page = document.Pages[0];
        var content = page.ContentBounds;
        page.AddText(new Rect(content.X, content.Y, content.Width, Mm(12)), "PRODUCT NAME", title, "product");
        page.AddRectangle(new Rect(content.X, Mm(28), content.Width, Mm(70)), new ShapeStyle { Stroke = Swatch.Black, StrokeWeight = 0.5 }, "photo");
        var description = page.AddText(new Rect(content.X, Mm(102), content.Width, Mm(40)), "Product description goes here.", body);
        document.Tags.TagPageItem(description, "description");
        page.AddText(new Rect(content.X, Mm(146), content.Width, Mm(10)), "PRICE", price, "price");
        page.AddText(new Rect(content.X, content.Bottom - Mm(8), content.Width, Mm(8)), "Prices valid from {{date}}.", body, "validity");
        return document;
    }

    private static void Save(IdmlDocument document, string path)
    {
        var validation = IdmlValidator.Validate(document);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"{path}: {validation}");
        }
        document.Save(path);
        Console.WriteLine($"{path}: valid");
    }
}
