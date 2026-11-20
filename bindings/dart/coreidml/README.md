# coreidml

**Create, fill and edit Adobe InDesign IDML files from Dart and Flutter, and turn them into PDF, without InDesign.**

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles). CoreIdml builds
documents from a few lines of data, fills a designer's template, edits text run by run, imports XML, renders PDF and
rebuilds editable documents from PDF. In a Flutter app that means producing a print-ready file on the device, offline.

```dart
import 'package:coreidml/coreidml.dart';

final doc = IdmlDocument.open('brochure.idml');
try {
  doc.fill({'text': {'customer': 'Ana Pérez', 'year': '2026'}});
  doc.saveFile('brochure-ana.idml');
  final report = doc.exportPdf('brochure-ana.pdf', options: {'documentPath': 'brochure.idml'});
  print('${report['pageCount']} pages');
} finally {
  doc.close();
}
```

## Why CoreIdml

- **Surgical edits.** A part you do not touch is written back byte for byte. Change one story, and the designer's
  file differs in exactly that story.
- **Output InDesign accepts.** `mimetype` first and uncompressed, ids remapped when pages are copied, InDesign's own
  formatting for what you change, and `validate()` to check references, threads and XML bindings.
- **PDF both ways.** Render with the fonts you ship, with a report of every substitution; rebuild text frames,
  images and shapes from a PDF.
- **Safe with files from anywhere.** Checksums verified on read, document type declarations refused, typed errors.
- **On the device.** The engine is a native library called through `dart:ffi`: no server, no InDesign, no platform
  channel. Long PDF work runs on a background isolate, with progress and cancellation. Android libraries are 16 KB
  page-aligned, as Google Play requires.

## Install

The package is used from this repository:

```yaml
dependencies:
  coreidml:
    git:
      url: https://github.com/eduardpaul/CoreIdml.git
      path: bindings/dart/coreidml
```

It needs the native library next to your app:

| Platform | Library | Where it goes |
| --- | --- | --- |
| Android | `libcoreidml.so` for `arm64-v8a` and `x86_64` | `android/app/src/main/jniLibs/<abi>/` |
| Linux desktop | `libcoreidml.so` | the bundle's `lib/` folder (an `install` rule in `linux/CMakeLists.txt`) |
| Dart on the command line | any of the above, or `coreidml.dll` on Windows | the `COREIDML_LIBRARY` environment variable, or `CoreIdml.load(path)` |

The [sample app](https://github.com/eduardpaul/CoreIdml/tree/main/samples/flutter_app) shows the Android and Linux
setup end to end, with an integration test that runs on the device. The libraries are built from this repository;
see [CONTRIBUTING.md](https://github.com/eduardpaul/CoreIdml/blob/main/CONTRIBUTING.md).

## API

Structured results (summaries, reports) are `Map<String, dynamic>` decoded from the library's JSON, and options are
`Map`s with the same camelCase keys. Leave options out for the defaults.

### Getting a document

```dart
final blank = IdmlDocument.create();                 // one blank A4 page
final built = IdmlDocument.create({                  // a spec: a Map, or the same as JSON text
  'unit': 'mm',
  'page': {'size': 'A4'},
  'paragraphStyles': [{'name': 'Title', 'size': 20, 'fontStyle': 'Bold'}],
  'pages': [
    {
      'items': [
        {'type': 'text', 'name': 'title', 'x': 15, 'y': 15, 'width': 180, 'height': 15, 'text': 'Sheet {{code}}', 'style': 'Title'},
        {'type': 'table', 'x': 15, 'y': 40, 'width': 180, 'height': 30, 'headerRows': 1, 'rows': [['Field', 'Value'], ['Height', '12 m']]},
      ],
    },
  ],
});
final fromFile = IdmlDocument.open(path);
final fromBytes = IdmlDocument.fromBytes(bytes);   // a Uint8List, for example an asset; not kept after the call
final (fromPdf, report) = IdmlDocument.importPdf('flyer.pdf', options: {'imageDirectory': '/data/links'});
```

A spec describes page size, colours, styles and pages of text frames, images, tables and shapes; the
[CoreIdml README](https://github.com/eduardpaul/CoreIdml#readme) documents every field.

### `IdmlDocument`

| Member | |
| --- | --- |
| `summary([options])` | Pages, frames with positions and text, stories, links, styles, swatches, tags: what a program or an agent needs to know about the document. |
| `story(storyId)` | One story in full, with every content run: its `index`, text, styles and scope (`body`, `cell 0:1`...). |
| `xmlStructure()` | The XML structure as a tree (`tag`, `path`, `boundTo`, `attributes`, `text`, `children`); null without one. |
| `exportXml({at})` | The XML structure, or the subtree at a path such as `/Root/article[2]`, as XML. |
| `validate()` | `{isValid, issues}`: references, ids, layers, threads, XML bindings. |
| `diff(other)` | What changed from this document to `other`: stories run by run, links, page count, page items. |
| `fill(data)` | Fills a template; see below. Returns `{filled, missing}`. |
| `replaceText(find, replacement)` | Replaces text in every story; returns the number of replacements. |
| `setRunText(storyId, runIndex, text, {force})` | Rewrites one content run, keeping its formatting. Runs with special characters (a page number) need `force: true`. |
| `relink(linkId, uriOrPath, {refit})` | Points a link from `summary()['links']` at another file; `refit` measures a local JPEG or PNG again and fits it to its frame. |
| `importXml(xml, {at, options})` | Imports XML onto the structure, with SimpleIDML's semantics. Returns `{updated, added, deleted, unmatched, warnings}`. |
| `importPages(source, [pages])` | Appends pages of another document, or of this one, remapping every id; styles, swatches and tags are merged once. Returns the number of pages added. |
| `apply(spec)` | Adds the colours, styles and pages of a spec; its pages are appended. |
| `exportPdf(path, {options, progress})` | Renders to a PDF file; returns the report. |
| `exportPdfBytes({options, progress})` | Renders to memory: `(Uint8List pdf, Map report)`. |
| `info()` | The parts of the package and the manifest. |
| `readPart(path)` / `readText(path)` | One part, as bytes or text, exactly as it would be saved now. |
| `writePart(path, bytes)` / `writeText(path, text)` | Adds or replaces a part. |
| `save()` / `saveFile(path)` | The .idml file, as bytes or written to disk. Unchanged parts are written byte for byte. |
| `close()` / `closed` | Releases the document; whether it was released. |

A document that is never closed is released by a native finalizer, but closing it yourself frees the memory at
once.

### Filling a template

Keys match frame names (InDesign's Layers panel), script labels, XML tags and `{{placeholders}}` in the
text. Images are local JPEG or PNG files, placed in the frames their keys name.

```dart
final report = doc.fill({
  'text': {'customer': 'ACME', 'code': 'F-001'},
  'images': {'photo': '/data/photos/f-001.jpg'},
});
report['filled'];   // {customer: 1, code: 2, photo: 1}: the places each key filled
report['missing'];  // keys that matched nothing
```

### Summary and run-level edits

```dart
final summary = doc.summary({'includeRuns': true, 'maxTextLength': 80, 'sections': ['pages', 'stories']});
final story = doc.story('u188');
// {id: u188, text: THE HEADLINE HERE, frames: [u185],
//  runs: [{index: 0, text: THE HEADLINE HERE, paragraphStyle: [Basic Paragraph], characterStyle: ..., scope: body}]}
doc.setRunText('u188', 0, 'A new headline');
```

Summary options: `maxTextLength` (characters of text per frame and story), `includeRuns`, `includeResources`
(styles, swatches, layers, tags, fonts), `pages` (0-based indices), `sections` (any of `pages`, `offPage`, `stories`,
`links`, `resources`).

### XML

```dart
final report = doc.importXml(xml, at: '/Root/module[1]', options: {'characterStyles': {'b': 'Bold'}});
doc.exportXml(at: '/Root/module/headline');   // <headline>The Life Aquatic</headline>
```

The XML follows SimpleIDML's conventions: elements match the tags of the structure, `simpleidml-setcontent`,
`-ignorecontent` and `-forcecontent` control what is replaced, and `href` relinks images. Options: `fit` (how images
fit their frames), `characterStyles` (inline tag to character style), `relinkOnly` (relink `href` images without
measuring them).

## PDF

```dart
final report = doc.exportPdf('out.pdf', options: {
  'fontDirectories': ['/data/fonts'],
  'useSystemFonts': false,
  'substitutions': {'Minion Pro': 'Liberation Serif'},
  'documentPath': '/data/brochure.idml',
});
report['missingFonts'];
// [{requestedFamily: Minion Pro, requestedStyle: Bold, usedFont: Liberation Serif Bold,
//   reason: family not installed, stories: [u188], pages: [1]}, ...]
```

| Option | |
| --- | --- |
| `pages` | 1-based page numbers to render; all of them by default. |
| `fontDirectories` | Folders of .ttf, .otf and .ttc files searched first. On a phone, ship the fonts your templates use and point here. |
| `useSystemFonts` | Whether the fonts of the machine are searched too (default true). Turn it off for output that is the same everywhere. |
| `substitutions` | Family to family, for fonts you know you do not have. |
| `linkDirectories` | Folders searched, by file name, for linked images whose path does not exist. |
| `linkRemap` | Link URI or file name to local file, checked first. |
| `documentPath` | Where the .idml is, so that relative links and its `Links` folder resolve. |
| `maxImageDpi`, `jpegQuality` | Downsampling and compression of images. |
| `includeBleed` | Pages with the document's bleed around the trim size (TrimBox inside the MediaBox); default false. |

The report lists `pageCount`, `missingFonts` (each with the font that replaced it), `missingImages`,
`oversetStories` and `warnings`. Without any font for a family, a standard PDF font is used and reported.

**From PDF.** `IdmlDocument.importPdf(path)` and `importPdfBytes(bytes)` rebuild an editable document: text frames
with inferred paragraph styles, image frames and shapes. IDML links its images, so extracted images are written to
`imageDirectory`. The report lists `textFrames`, `imageFrames`, `shapes`, `images` (the files written), `fonts` (each
family and style the document uses, with its pages) and `warnings`. Options: `pages`, `imageDirectory`,
`preserveLineBreaks`, `importShapes`, `fontDirectories`.

### Progress and cancellation

Every PDF call takes `progress`, called after each page with the pages done and the pages in total. Return `false`
to stop: the call throws an `IdmlException` with `CoreIdmlStatus.cancelled`. An exception thrown by `progress` also
stops it and is rethrown as is.

```dart
final (pdf, report) = doc.exportPdfBytes(progress: (done, total) {
  print('$done / $total');
  return !userPressedStop;
});
```

### Background isolates

The calls above are synchronous. In a Flutter app, run PDF work on a background isolate so the UI keeps drawing:

```dart
final cancellation = IdmlCancellation();   // e.g. wired to a Cancel button: cancellation.cancel()

final report = await doc.exportPdfInBackground('out.pdf',
    options: {'fontDirectories': [fonts]},
    progress: (done, total) => setState(() => this.progress = done / total),
    cancellation: cancellation);

final (pdf, _) = await doc.exportPdfBytesInBackground(options: {'pages': [1]});
final (imported, importReport) = await IdmlBackground.importPdf(path, options: {'imageDirectory': links});
final (fromBytes, _) = await IdmlBackground.importPdfBytes(bytes, cancellation: cancellation);
```

`progress` runs on your isolate. `cancel()` is seen by the background isolate while it renders: the work stops after
the current page and the future fails with `CoreIdmlStatus.cancelled`. A `progress` that throws stops the work the
same way, and the future fails with what it threw. The document stays usable, and stays yours:
the library serializes calls on one document, so a call made meanwhile waits for the export. Do not close a document
while background work on it is running. Documents imported in the background belong to your isolate like any other.

## Errors

Every failure is an `IdmlException` with a `code`; compare it with `CoreIdmlStatus`. The `message` says what went
wrong, in words you can show or log.

```dart
try {
  doc.setRunText('u188', 7, 'x');
} on IdmlException catch (error) {
  if (error.code == CoreIdmlStatus.notFound) {
    // error.message: Story "u188" has 1 content run(s); run index 7 does not exist.
  }
}
```

| `CoreIdmlStatus` | Meaning |
| --- | --- |
| `invalidFormat` | The input is not a usable IDML package. |
| `notFound` | Something you named does not exist: a part, story, run, link, style, page or XML path. |
| `invalidArgument` | An argument is missing or malformed, such as options of the wrong type or JSON that does not parse. |
| `invalidOperation` | The call is not valid for the document as it is, such as XML that is not well-formed or a document without XML structure. |
| `cancelled` | A progress callback or an `IdmlCancellation` stopped the call. |
| `unsupported` | The document uses something the library does not handle. |
| `invalidHandle` | The document was closed. |
| `io` | Reading or writing a file failed, such as an image or font that is not there. |
| `failed` | Anything unexpected; the message names it. |

A failed call does not close the document: you can carry on with it.

## Good to know

- Tested with the Dart SDK on Windows, and in Flutter apps on Android (arm64-v8a, x86_64) and Linux desktop through
  the sample app. Flutter web is not supported: the package uses `dart:ffi`.
- Fonts are the one thing a PDF needs from outside the document. Phones have few, and none of the ones designers
  use: ship the fonts of your templates with the app and pass their folder in `fontDirectories`.
- `example/main.dart` fills a template, saves it and renders it to PDF in the background.

Source, other languages (.NET, Node.js, the browser, Python, C) and issues:
[github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).

MIT licensed. CoreIdml is an independent project and is not affiliated with Adobe.
