# CoreIdml.Pdf

**Export Adobe InDesign IDML documents to PDF, rebuild editable IDML from PDF, and measure text exactly, from .NET,
without InDesign.**

The PDF side of [CoreIdml](https://www.nuget.org/packages/CoreIdml). Its own PDF writer, font parser and subsetter,
JPEG and PNG codecs and text composer, all in managed code: no native dependency, safe for trimming, Native AOT
(Android and iOS apps included) and WebAssembly. PDF import reads PDFs with PdfPig.

```sh
dotnet add package CoreIdml.Pdf
```

```csharp
using CoreIdml;
using CoreIdml.Pdf;

using var doc = IdmlDocument.Open("datasheet.idml");

var options = new PdfExportOptions { DocumentPath = "datasheet.idml" };   // relative links resolve against it
options.Fonts.AddDirectory("/app/fonts");                                  // fonts in app storage (Android, iOS too)
options.Fonts.AddFont("Raleway-Light.ttf", ralewayBytes);                  // or fonts from bytes
options.Fonts.Substitutions["Minion Pro"] = "Liberation Serif";            // family replacements
options.LinkDirectories.Add("/app/links");                                 // missing links are found by file name

var report = IdmlPdf.Export(doc, "datasheet.pdf", options);
foreach (var font in report.FontSubstitutions)
{
    Console.WriteLine($"{font}  (pages {string.Join(", ", font.Pages)})");  // the fonts that were not there
}
```

`IdmlPdf.ListFonts(options.Fonts)` lists the families an export can use, each with its style names as InDesign names
them (`Arial`: `Regular`, `Italic`, `Bold`, `Bold Italic`), for choosing fonts that will not be substituted.

## IDML to PDF

What is rendered: master items, groups, rotation, Bézier paths and rounded corners; CMYK, RGB and Lab swatches with
tints and opacity; stroke alignment; JPEG and PNG images clipped to their frames, with EXIF orientation; placed PDF
pages as vector graphics; threaded frames, columns, leading, alignment and justification (word and letter spacing,
glyph scaling), tabs (left, right, centre
and decimal stops, leaders), tables (row heights,
spans, header rows, cell insets and edges), vertical justification, keep options (keep with next, keep lines
together, orphans and widows, start in the next column, frame or page), page numbers, bullets and numbering (bullet
characters, number formats, list levels and restarts, hanging indents) and English and Spanish hyphenation. TrueType
fonts are embedded as subsets, CFF fonts whole, and the fonts' embedding licence bits are honoured.

```csharp
var options = new PdfExportOptions
{
    Pages = [1, 2],                       // numbered from 1; all pages when not set
    MaxImageDpi = 150,                    // downsample photos above 150 dpi at their placed size (off by default)
    Progress = (done, total) => true,     // after each page; return false to cancel
    CancellationToken = cancellationToken,
};
using var output = new MemoryStream();
var result = IdmlPdf.Export(doc, output, options);
// result.PageCount, FontSubstitutions, MissingImages, OversetStories, Warnings
```

Writing to a path is atomic: a failed or cancelled export leaves no partial file behind.

It approximates InDesign rather than reproducing it: line breaks follow InDesign's Paragraph Composer (the whole
paragraph broken at once, with costs measured against InDesign's output rather than taken from its code),
hyphenation covers English (TeX patterns) and Spanish (rules) only, kerning is the font's pair kerning, and there are
no OpenType features, text wrap, effects or placed EPS/AI graphics. Missing fonts change line breaks, so give the
export the document's fonts.

## PDF to IDML

```csharp
var imported = IdmlPdf.Import("brochure.pdf", "brochure.idml");     // images go to brochure-links/
// imported.Fonts: every font the PDF uses, and whether it is available
```

Glyphs are grouped into lines, columns and paragraphs (rotated text too); paragraph and character styles are inferred
from font, size and colour; colours become swatches; images are extracted next to the IDML (IDML links images); and
rectangles and lines become shapes. `PdfImportOptions` chooses pages, the image folder, line breaks, shapes and fonts.

## Measuring text and checking for overset

`TextMeasurer` composes text with the export's own fonts and composer, so a program can size frames before it saves,
or find the stories that do not fit:

```csharp
using var measurer = new TextMeasurer(doc, options.Fonts);
TextMeasure size = measurer.Measure(frame);                         // width, height and lines the text takes
foreach (var fit in measurer.CheckFit(doc).Where(fit => fit.Overset))
{
    Console.WriteLine($"{fit.StoryId}: {fit.OverflowLines} line(s), {fit.OverflowPoints:0.#} pt too long");
}
```

## Good to know

- Requires .NET 10 or 11. Fonts are parsed once per process and shared between exports and measurers.
- Encrypted PDFs cannot be read inside the Native AOT library on Android (no system crypto there): they are refused
  with a clear error instead of stopping the app.
- The same engine for Node.js, the browser, Python, Dart/Flutter and C:
  [github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).

MIT licensed. CoreIdml is an independent project and is not affiliated with Adobe.
