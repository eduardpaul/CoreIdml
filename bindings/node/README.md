# @coreidml/node

**Create, read, fill and edit Adobe InDesign IDML files from Node.js, and turn them into PDF, without InDesign.**

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles). CoreIdml opens
it, describes it, fills and edits it, renders it to PDF, and writes it back so that InDesign opens the result as if
nothing but your edit had happened.

```sh
npm install @coreidml/node
```

```js
import { CoreIdml } from '@coreidml/node';

const idml = await CoreIdml.load();
const doc = idml.open('brochure.idml');
try {
  doc.fill({ text: { title: 'Spring 2026', price: '€ 49' }, images: { cover: 'photos/cover.jpg' } });
  doc.saveFile('brochure-2026.idml');
  const report = await doc.exportPdfAsync('brochure-2026.pdf', { fontDirectories: ['fonts'] });
  for (const font of report.missingFonts) console.warn(`${font.requestedFamily} was drawn with ${font.usedFont}`);
} finally {
  doc.close();
}
```

## Why CoreIdml

- **Surgical edits.** A part you do not touch is written back byte for byte. Change one story, and the designer's
  file differs in exactly that story.
- **Output InDesign accepts.** `mimetype` first and uncompressed, InDesign's own formatting for what you change.
- **Templates for programs and agents.** A compact JSON summary of pages, frames, stories and links, with stable ids
  for everything an edit can target.
- **PDF built in.** Text, images, tables and shapes rendered with the fonts you choose, and a report of every font
  and image that was missing. PDFs can be turned back into editable documents too.
- **Keeps your server responsive.** The PDF calls have async variants that run on a worker thread, with progress and
  cancellation through an `AbortSignal`.
- **Safe with uploads.** Checksums verified on read, document type declarations refused, typed errors.
- **Nothing to compile.** The engine is a prebuilt native library, installed with the package and called through
  [koffi](https://koffi.dev). No node-gyp, no .NET runtime, no InDesign.

TypeScript declarations are included, with types for every option and report. The package is an ES module.

## API

```ts
const idml = await CoreIdml.load();   // loads the native library once; later calls return the same instance
idml.version;                         // version of the engine
```

`CoreIdml.load()` returns the instance directly in this package and a promise in
[`@coreidml/wasm`](https://www.npmjs.com/package/@coreidml/wasm); with `await`, the same code runs on both.

### Opening and creating

```ts
const fromFile = idml.open('brochure.idml');        // a path or a file: URL
const fromBytes = idml.open(uint8Array);            // a Uint8Array, Buffer or ArrayBuffer; not kept after the call

const blank = idml.create();                        // one blank A4 page
const sheet = idml.create({
  unit: 'mm',
  page: { size: 'A5', margins: { all: 12 } },
  colors: [{ name: 'Brand', cmyk: [0, 90, 85, 0] }],
  paragraphStyles: [{ name: 'Title', font: 'Liberation Serif', size: 18, color: 'Brand' }],
  pages: [
    {
      items: [
        { type: 'text', name: 'title', x: 12, y: 12, width: 124, height: 14, text: 'Sheet {{code}}', style: 'Title' },
        { type: 'image', name: 'photo', x: 12, y: 32, width: 124, height: 90, path: 'photos/placeholder.jpg' },
        { type: 'table', x: 12, y: 130, width: 124, height: 40, rows: [['Size', 'Price'], ['M', '€ 49']], headerRows: 1 },
      ],
    },
  ],
});
```

A spec describes page setup, colours, paragraph and character styles, and pages of text frames (plain text,
paragraphs or styled runs, columns, threading), images, tables and shapes. `DocumentSpec` in the type declarations
lists every field, and the [document spec reference](https://github.com/eduardpaul/CoreIdml/blob/main/plugins/coreidml/skills/idml-documents/references/document-spec.md) gives
their units, allowed values and defaults. A JSON string is accepted too.

### `IdmlDocument`

Reading:

| Member | |
| --- | --- |
| `summary(options?)` | Pages, frames with page-relative positions and text, stories, links, styles, swatches, layers, tags and fonts. |
| `story(storyId)` | One story in full: text, frames, and every content run with its index, styles and scope. |
| `xmlStructure()` | The XML structure as a tree of `{ tag, id, path, boundTo, attributes, text, children }`, or `null`. |
| `exportXml({ at }?)` | The XML structure, or the subtree at a path such as `/Root/article[2]`, as XML. |
| `validate()` | `{ isValid, issues }`: references, ids, layers, threads and XML bindings. |
| `diff(other)` | What changed from this document to `other`: stories run by run, links, page count. |

Editing:

| Member | |
| --- | --- |
| `fill({ text, images, fit })` | Fills a template: keys match frame names, script labels, XML tags, then `{{placeholders}}`. Returns `{ filled, missing }`. |
| `replaceText(find, replacement)` | Replaces text in every story; returns the number of replacements. |
| `setRunText(storyId, runIndex, text, { force }?)` | Sets the text of one content run, keeping its formatting. |
| `relink(linkId, uriOrPath, { refit }?)` | Points a placed image at another file; `refit` measures it again and fits it to its frame. |
| `importXml(xml, { at, fit, characterStyles, relinkOnly }?)` | Imports XML onto the structure with SimpleIDML's semantics. Returns `{ updated, added, deleted, unmatched, warnings }`. |
| `importPages(source, pages?)` | Appends pages of another document (or of this one), remapping ids and merging styles; returns the pages added. |
| `apply(spec)` | Adds the colours, styles and pages of a spec; its pages are appended. |

The package, saving and lifetime:

| Member | |
| --- | --- |
| `info()` | The parts of the package and the manifest: spreads, master spreads, stories. |
| `readPart(path)`, `readText(path)` | One part, as bytes or as text, exactly as it would be saved now. |
| `writePart(path, data)` | Adds or replaces a part with a string (written as UTF-8) or a `Uint8Array`. |
| `save()`, `saveFile(path)` | The document as the bytes of an .idml file, or written to a file. |
| `close()` | Releases the document. `using doc = idml.open(...)` does it for you where `using` is available. |
| `closed`, `handle` | Whether it was closed; the native handle, for passing to other C code. |

Summaries can be large, so they can be trimmed:

```ts
const { pages } = doc.summary({ pages: [0], sections: ['pages'], maxTextLength: 80 });
for (const item of pages[0].items) console.log(item.kind, item.name, item.x, item.y, item.text);

// Edit one run of a story without touching its formatting.
const story = doc.story(pages[0].items[0].storyId);
doc.setRunText(story.id, story.runs[0].index, 'New headline');
```

`summary({ includeRuns: true })` adds every story's runs. A run that holds special characters, such as a page
number, is marked `special` and only overwritten with `{ force: true }`.

A document that is never closed is released when it is garbage collected, but closing it yourself frees the memory
at once.

## PDF

```ts
const report = doc.exportPdf('sheet.pdf', {
  fontDirectories: ['fonts'],            // searched before the fonts installed on the machine
  linkDirectories: ['photos'],           // placed images are also looked for here by file name
});
report.pageCount;                        // and missingFonts, missingImages, oversetStories, warnings

const { pdf, report } = doc.exportPdfBytes({ pages: [1], documentPath: 'templates/sheet.idml' });
```

**Fonts.** Text is drawn with the fonts in `fontDirectories` (.ttf, .otf, .ttc) and, unless `useSystemFonts: false`,
the fonts installed on the machine. A font that is not found is replaced and reported in `missingFonts`, with the
family and style asked for, the font used and the pages it is on. `substitutions` chooses the replacement
(`{ 'Minion Pro': 'Liberation Serif' }`); `fallbackFamilies` lists the families tried, in order, after that and for
characters a font lacks (by default, common serif, sans and mono families). Font folders are searched recursively.
For output that is the same on every machine, pass your fonts and `useSystemFonts: false`.

**Placed images.** Links are resolved from their path, then by file name in `linkDirectories`; `linkRemap` points a
link (by file name or URI) at a file of your choice. Relative links resolve against the .idml's folder, so for a
document opened from bytes, give `documentPath`. Images not found are listed in `missingImages`
(`markMissingImages: true` draws a box where they belong). `maxImageDpi` downsamples large images (re-encoded at
`jpegQuality`).

The other options: `pages` (1-based), `includeMasterItems` (default `true`), `title`. `PdfExportOptions` in the type
declarations lists them all.

### Progress and cancellation

```ts
doc.exportPdf('catalogue.pdf', {
  onProgress: (done, total) => {
    console.log(`page ${done} of ${total}`);
    // return false to stop
  },
});
```

`onProgress` is called after each page. Returning `false` (or throwing) cancels the export after that page, and the
call fails with an `IdmlError` whose `code` is `Status.cancelled`; an exception thrown by `onProgress` is its
`cause`. The document stays usable.

### Async: on a worker thread

A large export takes seconds of CPU, which would block the event loop. `exportPdfAsync`, `exportPdfBytesAsync` and
`idml.importPdfAsync` do the same work on a `worker_threads` worker, so your server keeps answering:

```ts
const controller = new AbortController();
cancelButton.onclick = () => controller.abort();

try {
  const { pdf, report } = await doc.exportPdfBytesAsync({
    fontDirectories: ['fonts'],
    signal: controller.signal,                             // or AbortSignal.timeout(30_000)
    onProgress: (done, total) => bar.update(done / total), // runs on your thread; return false to cancel
  });
} catch (error) {
  if (error instanceof IdmlError && error.code === Status.cancelled) console.log('cancelled');
  else throw error;
}
```

- The calls run on a small pool of worker threads, which use the library already loaded in the process. A worker is
  kept for the next call, so only the first call pays for starting one; idle workers never keep the process alive.
  The PDF is moved back, not copied.
- Several calls can run at once, on the same document or on different ones; calls on one document take turns.
- Aborting the `signal` (or `onProgress` returning `false`) cancels at the next page boundary; the promise rejects
  with `Status.cancelled` and the signal's reason as `cause`. A signal that is already aborted rejects at once.
- A document is kept alive while an async call runs on it. A synchronous call on the same document waits for the
  async one (and blocks the event loop meanwhile), so await the async call first.

The synchronous calls take a `signal` too; during a synchronous call no other JavaScript runs, so it only takes
effect when it is already aborted or is aborted from `onProgress`.

### From PDF to IDML

```ts
const { document, report } = await idml.importPdfAsync('flyer.pdf', {
  imageDirectory: 'flyer-links',          // IDML links images, so extracted images are written here
  fontDirectories: ['fonts'],             // the report says which of the PDF's fonts are available
});
using flyer = document;
report.textFrames; report.imageFrames; report.shapes; report.fonts; report.warnings;
flyer.saveFile('flyer.idml');
```

`importPdf` (synchronous) and `importPdfAsync` take a path, a file: URL or the PDF's bytes, and the options `pages`
(1-based), `imageDirectory`, `preserveLineBreaks`, `importShapes` (default `true`), `fontDirectories`,
`useSystemFonts`, `onProgress` and `signal`. Text frames come back with inferred paragraph styles.

## Errors

Every failure is an `IdmlError` with a `code`; compare it with `Status`:

```ts
import { IdmlError, Status } from '@coreidml/node';

try {
  idml.open(upload);
} catch (error) {
  if (error instanceof IdmlError && error.code === Status.invalidFormat) {
    // Not a zip, no designmap.xml, a damaged entry: error.message says which.
  }
}
```

| `Status` | Meaning |
| --- | --- |
| `invalidFormat` | The input is not a usable IDML package. |
| `notFound` | Something you named does not exist: a part, a story, a link, a page. |
| `invalidArgument` | An argument is missing or malformed, such as a spec that does not match its schema. |
| `cancelled` | A PDF call was cancelled by `onProgress` or its `signal`; `cause` says why. |
| `unsupported` | The document uses something the library does not handle. |
| `invalidHandle` | The document was closed. |
| `io` | Reading or writing a file failed. |
| `invalidOperation` | The call is not valid right now, such as loading a second copy of the library. |
| `failed` | Anything unexpected; the message names it. |

A failed call leaves the document as it was, and it can still be used.

## Platforms

Prebuilt libraries are installed automatically, as optional dependencies named `@coreidml/node-<platform>-<arch>`:

| Platform | Package |
| --- | --- |
| Windows x64 | `@coreidml/node-win32-x64` |
| Linux x64 (glibc) | `@coreidml/node-linux-x64` |

Node.js 22 or later. x64 processors need x86-64-v2 (anything from the last decade).

On another platform, or to use a library you built yourself, point the package at it:

```sh
COREIDML_LIBRARY=/path/to/libcoreidml.so node app.js
```

```ts
const idml = await CoreIdml.load({ libraryPath: '/path/to/libcoreidml.so' });
```

The library is looked for in this order: the `libraryPath` option, `COREIDML_LIBRARY`, the platform package, the
system's library search path. If it cannot be loaded, the error lists every place that was tried.

## Good to know

- Calls are synchronous and run on the thread that makes them, except the `*Async` PDF calls.
- Paths may be strings or file: URLs, and may hold any Unicode characters.
- A process uses one copy of the library; `load()` with a different path afterwards is an error.
- `require('@coreidml/node')` works too, on Node versions with `require(esm)`.
- In the browser, or where native code is not an option, use
  [`@coreidml/wasm`](https://www.npmjs.com/package/@coreidml/wasm): the same API on WebAssembly.

Source, other languages (.NET, Python, Dart/Flutter, C) and issues:
[github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).

MIT licensed. CoreIdml is an independent project and is not affiliated with Adobe.
