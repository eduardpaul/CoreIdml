# CoreIdml

**Read, edit and write Adobe InDesign IDML files, without InDesign.**
One engine with the same behaviour in .NET, Node.js, the browser, Python and Dart/Flutter.

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles, the manifest that
ties them together). CoreIdml opens that package, lets you read and change any part, and writes it back so that
InDesign opens the result as if nothing but your edit had happened.

```csharp
var document = IdmlDocument.Open("brochure.idml");

foreach (var path in document.StoryPaths)
{
    var story = document.Package.GetXml(path);                       // live LINQ to XML
    foreach (var content in story.Descendants("Content").Where(c => c.Value.Contains("2025")).ToList())
    {
        content.Value = content.Value.Replace("2025", "2026");
    }
}

document.Save("brochure-2026.idml");                                 // only the stories that changed are rewritten
```

## Why CoreIdml

- **Surgical edits.** A part you do not touch is written back byte for byte. Change one story, and the designer's
  file differs in exactly that story.
- **Output InDesign accepts.** The `mimetype` entry comes first and uncompressed, edited parts are written the way
  InDesign formats them, and tabs and line breaks inside attribute values survive.
- **Reproducible files.** Untouched parts keep their timestamps and compression, so saving the same document twice
  gives the same bytes. Files diff, cache and deduplicate properly.
- **Safe with files from anywhere.** Checksums are verified on read, document type declarations are refused, and
  every failure is a typed error with the same code in every language.
- **Runs where your code runs.** A server, a desktop app, a phone, a browser tab. No InDesign, no InDesign Server,
  no service to call.
- **Self-contained.** No dependencies. The native builds need no .NET runtime; the browser build is about 2 MB
  over the wire.
- **Nothing hidden.** Every part of the package stays reachable, so anything IDML can express is within reach.

## Install

| You write | Package | Install |
| --- | --- | --- |
| .NET 11 | [`CoreIdml`](src/CoreIdml/README.md) | `dotnet add package CoreIdml` |
| Node.js | [`@coreidml/node`](bindings/node/README.md) | `npm install @coreidml/node` |
| Browser, or any JavaScript runtime | [`@coreidml/wasm`](bindings/wasm/README.md) | `npm install @coreidml/wasm` |
| Python | [`coreidml`](bindings/python/README.md) | `pip install coreidml` |
| Dart and Flutter | [`coreidml`](bindings/dart/coreidml/README.md) | git dependency; see its README |
| C, C++, Rust, Swift, Kotlin... | [`coreidml.h`](include/coreidml.h) | link the native library |

## The same task in every language

**JavaScript** (Node.js; in the browser import `@coreidml/wasm` and pass bytes)

```js
import { CoreIdml } from '@coreidml/node';

const idml = await CoreIdml.load();
const doc = idml.open('brochure.idml');
try {
  for (const story of doc.info().stories) {
    const xml = doc.readText(story);
    if (xml.includes('2025')) doc.writePart(story, xml.replaceAll('2025', '2026'));
  }
  doc.saveFile('brochure-2026.idml');
} finally {
  doc.close();
}
```

**Python**

```python
import coreidml

with coreidml.Document.open("brochure.idml") as doc:
    for story in doc.info()["stories"]:
        xml = doc.read_text(story)
        if "2025" in xml:
            doc.write_part(story, xml.replace("2025", "2026"))
    doc.save_file("brochure-2026.idml")
```

**Dart**

```dart
import 'package:coreidml/coreidml.dart';

final doc = IdmlDocument.open('brochure.idml');
try {
  for (final story in (doc.info()['stories'] as List).cast<String>()) {
    final xml = doc.readText(story);
    if (xml.contains('2025')) doc.writeText(story, xml.replaceAll('2025', '2026'));
  }
  doc.saveFile('brochure-2026.idml');
} finally {
  doc.close();
}
```

## What you can do

| | .NET | JavaScript | Python | Dart |
| --- | --- | --- | --- | --- |
| Open a file, bytes or a stream | `IdmlDocument.Open`, `OpenAsync` | `idml.open(pathOrBytes)` | `Document.open(path_or_bytes)` | `IdmlDocument.open`, `fromBytes` |
| See what is inside | `DomVersion`, `StoryPaths`, `SpreadPaths`, `Package.PartPaths` | `doc.info()` | `doc.info()` | `doc.info()` |
| Read a part | `Package.GetXml`, `GetBytes` | `readPart`, `readText` | `read_part`, `read_text` | `readPart`, `readText` |
| Change, add or replace a part | edit the XML in place, `SetXml`, `SetBytes` | `writePart` | `write_part` | `writePart`, `writeText` |
| Save to a file, bytes or a stream | `Save`, `SaveAsync`, `ToArray` | `save`, `saveFile` | `save`, `save_file` | `save`, `saveFile` |

`info()` describes the document the same way everywhere:

```json
{
  "domVersion": "10.0",
  "parts": [{ "path": "designmap.xml", "length": 16154, "modified": false }],
  "spreads": ["Spreads/Spread_ud6.xml"],
  "masterSpreads": ["MasterSpreads/MasterSpread_ua5.xml"],
  "stories": ["Stories/Story_u1db.xml", "Stories/Story_u19f.xml", "Stories/Story_u188.xml"]
}
```

Failures are one exception type per language (`IdmlException` in .NET and Dart, `IdmlError` in JavaScript and
Python) with one set of codes:

| Code | Meaning |
| --- | --- |
| `invalidFormat` | The input is not a usable IDML package: not a zip, no `designmap.xml`, damaged data, malformed XML. |
| `notFound` | A part you named does not exist. |
| `invalidArgument` | An argument is missing or malformed, such as a part path with a backslash. |
| `invalidHandle` | The document was closed. |
| `io` | Reading or writing a file failed. |
| `invalidOperation` | The call is not valid right now, such as loading a second copy of the library. |
| `failed` | Anything unexpected; the message names it. |

**Scope of 0.1.** CoreIdml works at the package level: every part as XML or bytes, and full control over what is
written. A typed document model on top of it (pages, frames, stories, styles) is in development.

## Where it runs

| | Verified on |
| --- | --- |
| .NET library | .NET 11 on Windows and Linux; trimming, Native AOT and Blazor WebAssembly safe |
| Native library (Node.js, Python, Dart, C) | Windows x64, Linux x64 (glibc), Android arm64-v8a and x86_64 |
| WebAssembly build | Chromium-based browsers and Node.js |
| Flutter | Android and Linux desktop ([sample app](samples/flutter_app/README.md)) |

Other platforms the .NET runtime supports (macOS, Linux arm64, Windows arm64) build from the same source; see
[CONTRIBUTING.md](CONTRIBUTING.md). x64 processors need x86-64-v2, as .NET 11 itself does.

## Building from source, contributing

[CONTRIBUTING.md](CONTRIBUTING.md) has the layout of the repository, how to build and test every target, and how a
function is added to all languages at once.

## License

[MIT](LICENSE). Third-party notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

CoreIdml is an independent project and is not affiliated with Adobe. Adobe and InDesign are trademarks of Adobe.
