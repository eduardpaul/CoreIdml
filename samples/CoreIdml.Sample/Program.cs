using CoreIdml;
using CoreIdml.Dom;
using CoreIdml.Samples;

// Generates two files to check by hand in InDesign:
//   ficha-demo.idml     a data sheet built in code (styles, swatches, frames, table, image, two pages)
//   ficha-template.idml a template with named/tagged slots, then ficha-filled.idml after filling it
var outDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var photo = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
Directory.CreateDirectory(outDir);

using (var demo = FichaDemo.Build(photo))
{
    Save(demo, Path.Combine(outDir, "ficha-demo.idml"));
}

BuildAndFillTemplate(outDir, photo);
return;

static void BuildAndFillTemplate(string outDir, string? photo)
{
    // A template as a designer would make it: named frames, a tag, and a {{placeholder}} inside running text.
    var templatePath = Path.Combine(outDir, "ficha-template.idml");
    using (var template = IdmlDocument.Create())
    {
        var page = template.Pages[0];
        var style = template.Styles.AddParagraphStyle("Campo", new ParagraphFormat { PointSize = 14, FontStyle = "Bold" });
        page.AddText(new Rect(50, 50, 400, 30), "NOMBRE", style, "nombre");
        var observations = page.AddText(new Rect(50, 90, 400, 60), "observaciones aquí");
        template.Tags.TagPageItem(observations, "observaciones");
        page.AddText(new Rect(50, 160, 400, 30), "Fecha de inspección: {{fecha}}");
        page.AddRectangle(new Rect(50, 200, 300, 200), new ShapeStyle { Stroke = Swatch.Black, StrokeWeight = 1 }, "foto");
        Save(template, templatePath);
    }

    using var document = IdmlDocument.Open(templatePath);
    var data = new TemplateData
    {
        Text = { ["nombre"] = "Ana Pérez", ["observaciones"] = "Sin novedad.\nSegunda línea.", ["fecha"] = "17/11/2026" },
    };
    if (photo is not null)
    {
        data.Images["foto"] = photo;
    }
    var result = document.Fill(data);
    Console.WriteLine($"fill: {string.Join(", ", result.Filled.Select(pair => $"{pair.Key}={pair.Value}"))}; missing: {string.Join(", ", result.Missing)}");
    Save(document, Path.Combine(outDir, "ficha-filled.idml"));
}

static void Save(IdmlDocument document, string path)
{
    var validation = IdmlValidator.Validate(document);
    document.Save(path);
    Console.WriteLine($"{path}: {(validation.IsValid ? "valid" : validation.ToString())}");
}
