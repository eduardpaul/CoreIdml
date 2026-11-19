# CoreIdml

**Create, fill, edit and export Adobe InDesign IDML files, without InDesign.**
One engine with the same behaviour in .NET, Node.js, the browser, Python, Dart/Flutter and C, plus a command line and
an MCP server for AI agents.

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles, the manifest that
ties them together). CoreIdml gives you a document model over it (pages, frames, stories, tables, styles, swatches,
links, the XML structure), fills templates designed in InDesign, renders PDFs, and writes files that InDesign opens as
if nothing but your edit had happened.

```csharp
using var template = IdmlDocument.Open("datasheet.idml");      // designed in InDesign
template.Fill(new TemplateData
{
    Text = { ["product"] = "Model X-200", ["price"] = "€ 1,290" },  // frame names, script labels, XML tags, {{placeholders}}
    Images = { ["photo"] = "/data/photos/x200.jpg" },
});
template.Save("x200.idml");                                    // untouched parts are written back byte for byte
IdmlPdf.Export(template, "x200.pdf", new PdfExportOptions { DocumentPath = "x200.idml" });
```

## Why CoreIdml

- **Surgical edits.** A part you do not touch is written back byte for byte, and an edited part keeps its attribute
  values exactly. Change one story, and the designer's file differs in exactly that story.
- **Templates the way designers make them.** Keys match frame names, script labels, XML tags and `{{placeholders}}`,
  even when InDesign has split a placeholder across formatting runs. SimpleIDML-style XML import fills tagged
  structures.
- **Documents from scratch.** Pages, text, tables, images, shapes, styles and colours, in code or from one JSON spec
  that every language shares. New documents open in every InDesign since CS5.5.
- **PDF in, PDF out.** Export with embedded font subsets, images, tables, threads and hyphenation; rebuild editable
  IDML from a PDF; measure text exactly to find overset stories. All in managed code, on the device.
- **Built for programs and agents.** Size-controlled JSON summaries with stable ids, indexed text runs, diffs,
  validation, and an MCP server with 33 tools.
- **Runs where your code runs.** A server, a desktop app, a phone, a browser tab. No InDesign Server, no service to
  call; the native builds need no .NET runtime.

## Install

| You write | Package | Install |
| --- | --- | --- |
| .NET 10 or 11 | [`CoreIdml`](src/CoreIdml/README.md), [`CoreIdml.Pdf`](src/CoreIdml.Pdf/README.md) | `dotnet add package CoreIdml.Pdf` |
| Node.js | [`@coreidml/node`](bindings/node/README.md) | `npm install @coreidml/node` |
| Browser, or any JavaScript runtime | [`@coreidml/wasm`](bindings/wasm/README.md) | `npm install @coreidml/wasm` |
| Python | [`coreidml`](bindings/python/README.md) | `pip install coreidml` |
| Dart and Flutter | [`coreidml`](bindings/dart/coreidml/README.md) | git dependency; see its README |
| C, C++, Rust, Swift, Kotlin... | [`coreidml.h`](include/coreidml.h) | the native library from the [releases](https://github.com/eduardpaul/CoreIdml/releases) |
| A shell, CI, Claude and other MCP clients | [`coreidml`](src/CoreIdml.Cli/README.md) | `dotnet tool install -g CoreIdml.Cli`, or a native binary from the releases |
| A shell or an MCP client, with Node.js | [`@coreidml/cli`](bindings/cli/README.md) | `npx @coreidml/cli <command>`, or `npm install -g @coreidml/cli` |
| Nothing: Claude Code and Cowork | [plugin](plugins/coreidml/README.md) | `/plugin marketplace add eduardpaul/CoreIdml`, then `/plugin install coreidml@coreidml` |
| Nothing: Claude Desktop | [extension](src/CoreIdml.Cli/README.md#claude-desktop) | `coreidml-<version>-<platform>.mcpb` from the releases: Settings, Extensions, drop it in |

## The same task in every language

Fill a template and export it to PDF.

**JavaScript** (Node.js; in the browser, `@coreidml/wasm` has the same API over bytes)

```js
import { CoreIdml } from '@coreidml/node';

const idml = CoreIdml.load();
using doc = idml.open('datasheet.idml');
doc.fill({ text: { product: 'Model X-200' }, images: { photo: '/data/photos/x200.jpg' } });
doc.saveFile('x200.idml');
const { pdf, report } = await doc.exportPdfBytesAsync({ fontDirectories: ['/data/fonts'] });   // on a worker thread
```

**Python**

```python
import coreidml

with coreidml.Document.open("datasheet.idml") as doc:
    doc.fill({"text": {"product": "Model X-200"}, "images": {"photo": "/data/photos/x200.jpg"}})
    doc.save_file("x200.idml")
    report = doc.export_pdf("x200.pdf", {"fontDirectories": ["/data/fonts"]})
```

**Dart and Flutter**

```dart
final doc = IdmlDocument.open('datasheet.idml');
try {
  doc.fill({'text': {'product': 'Model X-200'}, 'images': {'photo': photoPath}});
  doc.saveFile(outputPath);
  final report = await doc.exportPdfInBackground(pdfPath);    // keeps the UI isolate free
} finally {
  doc.close();
}
```

**Command line**

```sh
coreidml fill datasheet.idml data.json -o x200.idml
coreidml to-pdf x200.idml -o x200.pdf --fonts /data/fonts
```

## What you can do

Every language has the whole set; the names follow each language's conventions.

| | |
| --- | --- |
| Create | from a JSON spec (or the .NET model); open a file or bytes; rebuild from a PDF |
| Read | summary for programs and agents, one story with indexed runs, XML structure, the package's parts, validation, diff |
| Fill and edit | fill a template, replace text, set one run, relink an image, apply a spec, import XML, import pages from other documents |
| PDF | export to a file or to memory, with fonts, links, page selection, image downsampling, progress and cancellation |
| Save | to a file or bytes; untouched parts byte for byte |

The .NET library adds the full document model (pages, frames, tables, styles, swatches, layers, geometry, page and
item operations, text measurement), and the command line adds layout edits, batches, page previews and packaging.

Failures are one error type per language (`IdmlException` in .NET and Dart, `IdmlError` in JavaScript and Python)
with one set of codes:

| Code | Meaning |
| --- | --- |
| `invalidFormat` | The input is not usable: not an IDML package or a PDF, damaged data, malformed XML. |
| `notFound` | Something you named does not exist: a part, story, run, page, link or style. |
| `invalidArgument` | An argument is missing or malformed. |
| `cancelled` | A progress callback (or an abort signal) stopped the operation. |
| `invalidHandle` | The document was closed. |
| `io` | Reading or writing a file failed. |
| `unsupported` | The document uses something CoreIdml does not handle. |
| `invalidOperation` | The call is not valid for the document as it is now. |
| `failed` | Anything unexpected; the message names it. |

## Where it runs

| | Verified on |
| --- | --- |
| .NET library | .NET 10 and 11 on Windows, Linux and macOS; trimming, Native AOT and Blazor WebAssembly safe |
| Native library (Node.js, Python, Dart, C) and command line | Windows x64 and arm64, Linux x64 and arm64 (glibc 2.28 or later), macOS arm64 and x64, Android arm64-v8a and x86_64 |
| WebAssembly build | Chromium-based browsers and Node.js |
| Flutter | Android and Linux desktop ([sample app](samples/flutter_app/README.md)) |

x64 processors need x86-64-v2, as .NET 11 itself does.

## Fidelity

CoreIdml writes IDML, so what InDesign renders is what the designer made. Its own PDF export approximates InDesign:
the line breaker is greedy (InDesign defaults to the paragraph composer), hyphenation is Spanish only, and gradients,
effects, text wrap, placed EPS/AI graphics and objects anchored above the line or at a custom position are not drawn
(inline ones are). Missing fonts change line breaks, so give the export the document's fonts.

## Building from source, contributing

[CONTRIBUTING.md](CONTRIBUTING.md) has the layout of the repository, how to build and test every target, how a
function is added to all languages at once, and how releases are made.

## License

[MIT](LICENSE). Third-party notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

CoreIdml is an independent project and is not affiliated with Adobe. Adobe and InDesign are trademarks of Adobe.
