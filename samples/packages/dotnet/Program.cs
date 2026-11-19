// Fills the plugin's data sheet template with the published CoreIdml and CoreIdml.Pdf packages from nuget.org, and
// checks the result: dotnet run. Writes out/datasheet.idml and out/datasheet.pdf, and exits with 1 when anything is
// not as expected.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CoreIdml;
using CoreIdml.Pdf;

var here = Folder();
var samples = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "plugins", "coreidml", "samples"));
var output = Path.Combine(here, "out");
Directory.CreateDirectory(output);
var failures = new List<string>();
void Check(bool condition, string what)
{
    Console.WriteLine($"{(condition ? "ok   " : "FAIL ")} {what}");
    if (!condition)
    {
        failures.Add(what);
    }
}

Console.WriteLine($"CoreIdml {typeof(IdmlDocument).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(samples, "datasheet-data.json")));
var data = new TemplateData();
foreach (var field in json.RootElement.GetProperty("text").EnumerateObject())
{
    data.Text[field.Name] = field.Value.GetString()!;
}

// Fill the template, validate it and save it as a new file.
using (var template = IdmlDocument.Open(Path.Combine(samples, "datasheet-template.idml")))
{
    var filled = template.Fill(data);
    Check(filled.Filled.Keys.Order().SequenceEqual(data.Text.Keys.Order()) && filled.Missing.Count == 0,
        $"fill: {string.Join(", ", filled.Filled.Select(pair => $"{pair.Key}={pair.Value}"))}; missing: {filled.Missing.Count}");
    Check(IdmlValidator.Validate(template).IsValid, "the filled document is valid");
    template.Save(Path.Combine(output, "datasheet.idml"));
}

// Open the saved file again: the data is in it.
var pdfPath = Path.Combine(output, "datasheet.pdf");
using (var saved = IdmlDocument.Open(Path.Combine(output, "datasheet.idml")))
{
    Check(saved.Stories.Any(story => story.Text.Contains("Trail Lamp X200", StringComparison.Ordinal)), "the saved file has the product name");
    Check(saved.Stories.Any(story => story.Text.Contains("1 March 2027", StringComparison.Ordinal)), "and the {{date}} placeholder filled");
    var report = IdmlPdf.Export(saved, pdfPath);
    Check(report.PageCount == 1 && report.OversetStories.Count == 0, $"PDF: {report.PageCount} page, nothing overset");
}

var pdf = File.ReadAllBytes(pdfPath);
Check(pdf.AsSpan().StartsWith("%PDF-"u8), $"datasheet.pdf is a PDF ({pdf.Length} bytes)");

// And the PDF back into an editable document.
using (var stream = File.OpenRead(pdfPath))
{
    var imported = IdmlPdf.Import(stream);
    using var document = imported.Document;
    Check(document.Pages.Count == 1, $"PDF import: {imported.TextFrames} text frames");
}

Console.WriteLine(failures.Count == 0 ? "all checks passed" : $"{failures.Count} checks failed");
return failures.Count == 0 ? 0 : 1;

static string Folder([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
