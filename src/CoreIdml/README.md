# CoreIdml

**Create, read, fill and edit Adobe InDesign IDML files from .NET, without InDesign.**

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles, the manifest that
ties them together). CoreIdml gives you a typed document model over it (pages, frames, stories, tables, styles,
swatches, links, the XML structure) and writes the result so that InDesign opens it as if nothing but your edit had
happened. It does for `.idml` what the OpenXML SDK does for `.docx`.

```sh
dotnet add package CoreIdml
dotnet add package CoreIdml.Pdf      # PDF export and import, optional
```

```csharp
using CoreIdml;
using CoreIdml.Dom;

using var template = IdmlDocument.Open("datasheet.idml");      // designed in InDesign
var result = template.Fill(new TemplateData
{
    Text = { ["product"] = "Model X-200", ["price"] = "€ 1,290" },  // frame names, script labels, XML tags, {{placeholders}}
    Images = { ["photo"] = "/data/photos/x200.jpg" },
});
template.Save("x200.idml");                                    // every part you did not touch is written back byte for byte
```

## Why CoreIdml

- **Surgical edits.** A part you do not touch is written back byte for byte, and an edited part keeps its attribute
  values exactly (tabs, line breaks) and its original ending. Change one story, and the designer's file differs in
  exactly that story.
- **Templates the way designers make them.** Keys match frame names, script labels, XML tags and `{{placeholders}}`,
  even when InDesign has split a placeholder across formatting runs.
- **Documents from scratch.** Pages, text frames, tables, images, shapes, styles and colours, in code or from a JSON
  spec. New documents start from a real InDesign CS5.5 export, so every InDesign version since opens them.
- **Built for programs and agents.** Size-controlled JSON summaries with stable ids, indexed text runs, diffs and
  validation.
- **No dependencies, no reflection.** Safe for trimming, Native AOT and Blazor WebAssembly; the analyzers for all
  three run on every build of the library.

## Building a document

Coordinates are points (1/72 in) from the page's top-left, y pointing down; `Units` converts.

```csharp
using var doc = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4, Margins = Spacing.All(Units.Millimeters(15)) });
var brand = doc.Swatches.AddCmyk("Brand", 75, 45, 0, 10);
var title = doc.Styles.AddParagraphStyle("Title", new ParagraphFormat { FontFamily = "Minion Pro", FontStyle = "Bold", PointSize = 22, FillColor = brand });
var page = doc.Pages[0];
double Mm(double value) => Units.Millimeters(value);

page.AddText(new Rect(Mm(15), Mm(15), Mm(180), Mm(15)), "Datasheet D-001", title, name: "title");
var table = page.AddTextFrame(new Rect(Mm(15), Mm(40), Mm(100), Mm(40))).AddTable(2, 2, new TableOptions { HeaderRows = 1 });
table.SetRow(0, "Property", "Value");
table.SetRow(1, "Height", "12 m");
page.AddImage(new Rect(Mm(120), Mm(40), Mm(75), Mm(50)), "/data/photo.jpg", ImageFit.Fill, name: "photo");
doc.Save("datasheet.idml");
```

The same from JSON, which is also what every other language uses:

```csharp
using var fromSpec = IdmlJson.CreateDocument("""
    { "unit": "mm", "page": { "size": "A4", "margins": { "all": 15 } },
      "colors": [ { "name": "Brand", "cmyk": [75, 45, 0, 10] } ],
      "paragraphStyles": [ { "name": "Title", "font": "Minion Pro", "fontStyle": "Bold", "size": 22, "color": "Brand" } ],
      "pages": [ { "items": [
        { "type": "text", "name": "title", "x": 15, "y": 15, "width": 180, "height": 15, "text": "Datasheet {{code}}", "style": "Title" },
        { "type": "table", "x": 15, "y": 40, "width": 100, "height": 40, "headerRows": 1, "rows": [["Property", "Value"], ["Height", "12 m"]] },
        { "type": "image", "name": "photo", "x": 120, "y": 40, "width": 75, "height": 50, "path": "/data/photo.jpg", "fit": "fill" },
        { "type": "rectangle", "x": 0, "y": 0, "width": 210, "height": 10, "fill": "Brand" }
      ] } ] }
    """);
```

## Reading and editing

Every wrapper exposes its live `XElement` (`.Element`), so the full IDML vocabulary stays reachable.

```csharp
using var doc = IdmlDocument.Open("magazine.idml");

foreach (var story in doc.Stories)
{
    Console.WriteLine($"{story.Id}: {story.Text}");
}
doc.ReplaceText("2025", "2026");                                  // across every story

var summary = IdmlJson.Summarize(doc, new SummaryOptions { IncludeRuns = true, MaxTextLength = 200 });
doc.SetRunText("u188", runIndex: 3, "New headline");              // only that run changes, formatting kept
doc.RelinkImage("u1f2", "/data/photos/new.jpg", refit: ImageFit.Fill);
```

Pages and items: `AddPage`, `Page.AddTextFrame`, `AddRectangle`, `AddImage`; `PageItem.SetBounds` moves and resizes;
pages can be moved and deleted; styles and colours can be defined and applied to paragraphs and runs.

## XML structure (SimpleIDML-style templating)

```csharp
var structure = doc.Tags;                                         // the XML structure, stitched across stories
var headline = structure.SelectSingle("/Root/article[2]/headline");
structure.MapToCharacterStyle("b", doc.Styles.FindCharacterStyle("Bold")!);
var report = structure.ImportXml(File.ReadAllText("data.xml"), at: "/Root/article[2]");
XElement exported = structure.ExportXml();                        // like File > Export > XML
```

The import understands SimpleIDML's `simpleidml-setcontent`, `-ignorecontent` and `-forcecontent` flags and `href`
images, turns nested inline tags into character styles, and matches by looking ahead, so a source that omits an
element still fills the ones after it.

## Composition

One page per record, from a template, into one book:

```csharp
using var book = IdmlDocument.Open("cover.idml");
foreach (var record in records)
{
    using var sheet = IdmlDocument.Open("sheet.idml");
    sheet.Fill(new TemplateData { Text = record.Text, Images = record.Images });
    book.ImportPages(sheet);          // ids remapped; styles, swatches and tags merged once; layers matched by name
}
book.Save("book.idml");
```

## Checking and comparing

```csharp
var issues = IdmlValidator.Validate(doc);                         // references, ids, layers, threads, XML bindings
var changes = IdmlJson.Diff(before, after);                       // stories run by run, links, page count
var fit = TextFit.Estimate(story);                                // a quick overset estimate; CoreIdml.Pdf measures exactly
```

## The package underneath

Every part stays reachable as live LINQ to XML or as bytes, and you control exactly what is written:

```csharp
XDocument styles = doc.Package.GetXml("Resources/Styles.xml");    // parsed on first use; changing it marks it modified
ReadOnlyMemory<byte> raw = doc.Package.GetBytes("Spreads/Spread_ud6.xml");
doc.Package.SetBytes("META-INF/metadata.xml", metadata);

var fromUpload = await IdmlDocument.OpenAsync(request.Body, cancellationToken);
byte[] stable = doc.ToArray(new IdmlSaveOptions { Timestamp = DateTimeOffset.UnixEpoch.AddYears(56) });   // reproducible bytes
```

Untouched parts keep their timestamps and compression, so with a fixed timestamp an edited document saves to the same
bytes every time.

## Errors

```csharp
try
{
    using var uploaded = IdmlDocument.Open(bytes);
}
catch (IdmlException exception) when (exception.Kind == IdmlErrorKind.InvalidFormat)
{
    // Not a zip, no designmap.xml, a damaged entry, malformed XML: exception.Message says which.
}
```

`IdmlErrorKind` is `InvalidFormat`, `NotFound` (a story, run, page, link or style you named), `Unsupported` or
`InvalidOperation`. Bad arguments are `ArgumentException` and file problems `IOException`, as elsewhere in .NET.
Checksums are verified on read and document type declarations are refused, so files from anywhere are safe to open.

## Good to know

- Requires .NET 11. Runs on CoreCLR, Native AOT (including Android and iOS apps) and in the browser.
- A document is not thread-safe; use one per thread, or lock.
- Images are linked, not embedded, as IDML always does: ship generated files with their images, or use
  `DocumentPackager` (InDesign's File > Package).
- PDF export and import, exact text measurement and overset checks: [CoreIdml.Pdf](https://www.nuget.org/packages/CoreIdml.Pdf).
  A command line and an MCP server for AI agents: [CoreIdml.Cli](https://www.nuget.org/packages/CoreIdml.Cli).
- The same engine for Node.js, the browser, Python, Dart/Flutter and C:
  [github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).

MIT licensed. CoreIdml is an independent project and is not affiliated with Adobe.
