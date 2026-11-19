# @coreidml/wasm

**Create, read, edit and print Adobe InDesign IDML files in the browser, without InDesign and without a server.**

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles). CoreIdml builds
documents from a JSON spec, fills templates, edits stories, links and XML, merges pages, renders PDFs and turns PDFs
back into editable documents, and writes files that InDesign opens as if nothing but your edit had happened. This
package is the engine compiled to WebAssembly: the files never leave the user's machine.

```sh
npm install @coreidml/wasm
```

```js
import { IdmlWorker } from '@coreidml/wasm/worker';

const idml = await IdmlWorker.start();                        // the engine, in a Web Worker
await idml.addFonts({ 'Lato-Regular.ttf': latoBytes });       // a browser has no system fonts: bring yours
await idml.addLinks({ 'photo.jpg': photoBytes });             // placed images, found by file name

const doc = await idml.open(await file.arrayBuffer());
await doc.fill({ text: { customer: 'Ana Pérez' }, images: { photo: '/links/photo.jpg' } });
const idmlBytes = await doc.save();
const { pdf, report } = await doc.exportPdfBytes({
  onProgress: (done, total) => (progressBar.value = done / total),   // on the page, after each page
});
await doc.close();

download.href = URL.createObjectURL(new Blob([pdf], { type: 'application/pdf' }));
```

## Why CoreIdml

- **Client-side.** Templates and customer documents are processed in the tab. Nothing to upload, nothing to host.
- **Surgical edits.** A part you do not touch is written back byte for byte. Change one story, and the designer's
  file differs in exactly that story.
- **Output InDesign accepts.** `mimetype` first and uncompressed, InDesign's own formatting for what you change.
- **Built for programs and agents.** A size-controlled JSON summary of pages, frames, stories and links; indexed
  text runs to edit; a validator and a diff to check the result.
- **PDF both ways, in the tab.** Its own PDF writer with font embedding, and a PDF importer that rebuilds editable
  frames and styles. In a worker, with progress, so the page stays responsive.
- **Safe with files from anywhere.** Checksums verified on read, document type declarations refused, typed errors.
- **One API, two packages.** [`@coreidml/node`](https://www.npmjs.com/package/@coreidml/node) has the same document
  API over a native library. This package has no native code, and runs in Node.js too.

TypeScript declarations are included. The package is an ES module with no dependencies.

## Two ways to run it

| | |
| --- | --- |
| `IdmlWorker` from `@coreidml/wasm/worker` | The engine in a Web Worker. Every call returns a promise; the page never blocks. The way to go in a page. |
| `CoreIdml` from `@coreidml/wasm` | The engine on the current thread; every call is synchronous. For Node.js, for a worker of your own, or for quick calls on a page (a PDF blocks the page while it renders). |

Both have the same documents and the same methods: what `CoreIdml` returns, `IdmlWorker` resolves to.

```js
import { CoreIdml } from '@coreidml/wasm';

const idml = await CoreIdml.load();       // downloads and starts the engine once; later calls return the same instance
const doc = idml.open(bytes);             // then every call is synchronous
console.log(doc.summary().pageCount);
doc.close();
```

## Loading

The engine is the folder `dist/_framework` of this package: 40 files, **14.5 MB, 3.7 MB with Brotli** (5.4 MB with
gzip). That includes the PDF writer and importer. It is downloaded once per page (or worker), and cached like any
static file; on a local server it starts in about 300 ms.

By default it is loaded from next to the module, which is where it is when the package is served as it is (native
ES modules, a CDN, Node.js). A bundler does not know about that folder. Copy
`node_modules/@coreidml/wasm/dist/_framework` into your static assets (with Vite, into `public/`), and say where it
ends up:

```js
const idml = await CoreIdml.load({ frameworkUrl: '/coreidml/_framework/' });
const worker = await IdmlWorker.start({ frameworkUrl: '/coreidml/_framework/' });
```

`IdmlWorker.start()` starts `dist/worker-thread.js` as a module worker, written as
`new Worker(new URL('./worker-thread.js', import.meta.url), { type: 'module' })`, which Vite and webpack follow; to
serve it from elsewhere, pass `workerUrl`. Serve `.wasm` files as `application/wasm`, and let the server compress
them.

## API

### `CoreIdml` and `IdmlWorker`

| Member | |
| --- | --- |
| `CoreIdml.load({ frameworkUrl? })` | Starts the engine on this thread (once per page or worker). |
| `IdmlWorker.start({ frameworkUrl?, workerUrl? })` | Starts a Web Worker and the engine in it. Start several for parallel work. |
| `open(bytes)` | Opens an .idml file from a `Uint8Array` or an `ArrayBuffer`; the bytes are copied, not kept. |
| `create(spec?)` | A document from a document spec (below; an object or JSON text); without one, a blank A4 page. |
| `importPdf(bytes, options?)` | Rebuilds an editable document from a PDF: `{ document, report, imageDirectory, images }`. See PDF. |
| `addFonts({ name: bytes })` | Writes font files into `/fonts` of the engine's file system. |
| `addLinks({ name: bytes })` | Writes placed images (and PDFs) into `/links`. |
| `writeFile(path, data)`, `readFile(path)` | A file of the engine's file system, as bytes (text is written as UTF-8). |
| `listFiles(folder)`, `remove(path)` | The names in a folder; removes a file or a folder with everything in it. |
| `version` | Version of the engine. |
| `IdmlWorker` only: `terminate()`, `terminated`, `IdmlWorker.canCancel` | Stops the worker. See Progress and cancelling. |

### `IdmlDocument` (and `WorkerDocument`, the same with promises)

| Member | |
| --- | --- |
| **Reading** | |
| `summary(options?)` | Pages, frames with positions and text, stories, links, styles, swatches, layers, tags, fonts. |
| `story(storyId)` | One story in full: its text, frames and every content run with its index and styles. |
| `xmlStructure()` | The XML structure as a tree, or `null` when there is none. |
| `exportXml({ at? })` | The XML structure as XML, or the subtree at a path such as `'/Root/article[2]'`. |
| `validate()` | Checks references, ids, layers, threads and XML bindings: `{ isValid: true, issues: [] }`. |
| `diff(other)` | What changed from this document to `other`: stories run by run, links, page count. |
| **Editing** | |
| `fill(data)` | Fills a template; returns which keys were `filled` and which are `missing`. |
| `replaceText(find, replacement)` | Replaces text in every story; returns how many times. |
| `setRunText(storyId, runIndex, text, { force? })` | Sets one run's text and keeps its formatting. |
| `relink(linkId, uriOrPath, { refit? })` | Points an image link at another file. |
| `importXml(xml, { at?, ...options })` | Imports XML onto the structure; returns a report. |
| `importPages(source, pages?)` | Appends pages of another document, or of itself; returns how many. |
| `apply(spec)` | Adds the colours, styles and pages of a spec to the document. |
| **Saving** | |
| `save()` | The document as the bytes of an .idml file. |
| `exportPdfBytes(options?)` | Renders a PDF: `{ pdf, report }`. |
| **Package parts** | |
| `info()` | The parts of the package and the manifest (`domVersion`, `parts`, `spreads`, `masterSpreads`, `stories`). |
| `readPart(path)`, `readText(path)` | One part (e.g. `'Stories/Story_u188.xml'`) as bytes or text, exactly as it would be saved now. |
| `writePart(path, data)` | Adds or replaces a part with a string (written as UTF-8) or a `Uint8Array`. |
| `close()`, `closed` | Releases the document; whether it was released. `using` (or `await using` for a `WorkerDocument`) does it for you. |

A document that is forgotten is released when it is garbage collected. Options, specs, fill data and reports are
plain objects with the camelCase keys shown here.

### The engine's file system

A page has bytes, not paths, so this package has no file-path variants of `open`, `save` or the PDF export. The
engine keeps an in-memory file system instead, and every path in options and data is a path there: fonts, placed
images, images to fill or relink, and the images a PDF import extracts.

```js
idml.addFonts({ 'Lato-Regular.ttf': lato, 'Lato-Bold.ttf': latoBold });   // -> /fonts/...
idml.addLinks({ 'cover.jpg': cover });                                     // -> /links/cover.jpg
idml.writeFile('/photos/ana.jpg', photo);                                  // anywhere else
idml.listFiles('/fonts');                                                  // ['Lato-Bold.ttf', 'Lato-Regular.ttf']
```

It lives as long as the engine (the page, or the worker) and is not shared: each `IdmlWorker` has its own.

### Creating documents

A spec describes the page, colours, styles and pages of items: text, images, tables, rectangles and lines.

```js
const doc = idml.create({
  unit: 'mm',                                                  // pt (default), mm, cm or in
  page: { size: 'A4', margins: { all: 15 } },
  colors: [{ name: 'Brand', cmyk: [75, 45, 0, 10] }, { name: 'Web', hex: '#2E86C1' }],
  paragraphStyles: [{ name: 'Title', size: 22, fontStyle: 'Bold', color: 'Brand' }],
  characterStyles: [{ name: 'Strong', fontStyle: 'Bold' }],
  pages: [{ items: [
    { type: 'text', name: 'title', x: 15, y: 15, width: 180, height: 15, text: 'Order {{number}}', style: 'Title' },
    { type: 'text', x: 15, y: 35, width: 180, height: 10,
      paragraphs: [{ runs: [{ text: 'Status: ' }, { text: 'paid', style: 'Strong' }] }] },
    { type: 'table', x: 15, y: 50, width: 120, height: 30, headerRows: 1, rows: [['Item', 'Qty'], ['Poster A2', '3']] },
    { type: 'image', x: 140, y: 50, width: 55, height: 40, path: '/links/cover.jpg', fit: 'fill' },
    { type: 'rectangle', x: 0, y: 287, width: 210, height: 10, fill: 'Brand' },
  ] }],
});
doc.fill({ text: { number: 'F-001' } });
```

Every item also takes `name`, `label`, `tag` (to appear in the XML structure), `layer`, `fill`, `stroke`,
`strokeWeight` and `rotation`. Images must be in the engine's file system, since they are measured to fit.

### Templates, text, links and XML

```js
const report = doc.fill({
  text: { name: 'Ana Pérez', course: 'Typography' },   // frame names, script labels, XML tags, {{placeholders}}
  images: { photo: '/photos/ana.jpg' },                 // a JPEG or PNG in the engine's file system
});
// { filled: { name: 1, course: 1, photo: 1 }, missing: [] }

const summary = doc.summary({ pages: [0], sections: ['pages', 'stories', 'links'], maxTextLength: 80 });
const storyId = summary.stories[0].id;
for (const run of doc.story(storyId).runs) console.log(run.index, run.characterStyle, run.text);
doc.setRunText(storyId, 0, 'Ana Pérez García');        // keeps the run's formatting

doc.relink(summary.links[0].id, '/photos/new-cover.jpg', { refit: true });

doc.importXml(xmlText, { at: '/Root/module', characterStyles: { b: 'Bold' } });
// { updated: 6, added: 0, deleted: 0, unmatched: [], warnings: [] }
```

Summary options: `maxTextLength` (200), `includeRuns` (`false`), `includeResources` (`true`), `pages` (0-based
indices) and `sections` (any of `'pages'`, `'stories'`, `'links'`, `'offPage'`, `'resources'`). A run holding
special characters, such as a page number, is only overwritten with `{ force: true }`. Tagged templates work as in
SimpleIDML (`simpleidml-setcontent`, `-ignorecontent`, `-forcecontent`, `href`); import options are `fit`,
`characterStyles` and `relinkOnly`. `doc.importPages(other)` merges documents, and `before.diff(after)` and
`doc.validate()` check the result.

### PDF

```js
idml.addFonts(fonts);                                  // the fonts the document uses
idml.addLinks(images);                                 // its placed images, by file name

const { pdf, report } = doc.exportPdfBytes({
  substitutions: { 'Minion Pro': 'Lato' },             // family replacements
  pages: [1, 2],                                       // 1-based; default every page
  maxImageDpi: 150,                                    // keeps the PDF small
});
```

`fontDirectories` and `linkDirectories` default to `/fonts` and `/links` once those folders have files; name other
folders of the engine's file system to use those instead. The report says what to check: `pageCount`,
`missingFonts` (each with the font used instead, its pages and stories), `missingImages`, `oversetStories` and
`warnings`.

- **Fonts.** Fonts are embedded (TrueType as subsets). The engine sees no system fonts in WebAssembly, so a font you
  do not add is replaced by a standard PDF font, not embedded, and reported. Check the font licences of what you add.
- **Links.** IDML links images: they are found by file name in `linkDirectories`, at their recorded path, or through
  `linkRemap` (`{ 'old.psd': '/links/new.jpg' }`). `maxImageDpi` and `jpegQuality` (85) keep the PDF small.

The other way, `importPdf` rebuilds an editable document from the bytes of a PDF: text frames with inferred
paragraph and character styles, image frames and shapes. IDML links its images, so the extracted images are written
to `imageDirectory` (by default a new `/import/<n>` folder, removed again when the document is closed), where the
document's links point, and returned as bytes too:

```js
const { document, report, imageDirectory, images } = idml.importPdf(pdfBytes);
// report: textFrames, imageFrames, shapes, images, fonts (and whether each is in /fonts), warnings
// images: { 'page1-image1.png': Uint8Array, ... }
```

Import options: `pages` (1-based), `imageDirectory`, `preserveLineBreaks`, `importShapes` and `fontDirectories`.

### Progress and cancelling

`exportPdfBytes` and `importPdf` take `onProgress(done, total)`, called after each page, and an AbortSignal,
`signal`. Return `false` (or abort the signal) to cancel; the call then fails with `IdmlError` and
`Status.cancelled`. An exception thrown in the callback also cancels, and is the error's `cause`, as is the abort
reason. With `CoreIdml`, the callback runs on the same thread, between pages.

With `IdmlWorker`, `onProgress` runs on the page while the worker goes on, which is where a signal comes in:

```js
const controller = new AbortController();
cancelButton.onclick = () => controller.abort();
const { pdf } = await doc.exportPdfBytes({ signal: controller.signal, onProgress: (done, total) => show(done, total) });
```

Stopping a call that is running in the worker needs memory shared with it, and browsers share memory only with
**cross-origin isolated** pages: those served with `Cross-Origin-Opener-Policy: same-origin` and
`Cross-Origin-Embedder-Policy: require-corp`. `IdmlWorker.canCancel` says which case applies:

- **Isolated** (`canCancel` is `true`): the worker stops after the page it is rendering, the call rejects with
  `Status.cancelled`, and the worker and its documents stay usable.
- **Not isolated**: a WebAssembly call in progress cannot be interrupted, so cancelling **terminates the worker**.
  The call still rejects with `Status.cancelled`, every other pending call rejects with `Status.invalidOperation`,
  and the worker's documents and files are gone: start a new `IdmlWorker`. Progress is reported either way.

A signal that is already aborted cancels the call before it starts, in both cases.

### Errors

Every failure is an `IdmlError` with a `code`; compare it with `Status`. With `IdmlWorker`, promises reject with
the same errors.

```js
import { IdmlError, Status } from '@coreidml/wasm';

try {
  doc.setRunText(storyId, 3, 'New text');
} catch (error) {
  if (error instanceof IdmlError && error.code === Status.notFound) {
    // error.message: 'Story "u10a" has 1 content run(s); run index 3 does not exist.'
  }
}
```

| `Status` | Meaning |
| --- | --- |
| `invalidFormat` | The input is not usable: not an IDML package or a PDF, damaged data, malformed XML. |
| `notFound` | Something you named does not exist: a part, story, run, link, page or XML element. |
| `invalidArgument` | An argument is missing or malformed. |
| `invalidHandle` | The document was closed. |
| `cancelled` | A progress callback or an abort signal cancelled the call. |
| `io` | Reading or writing a file of the engine's file system failed, such as a file that is not there. |
| `invalidOperation` | The call is not valid right now, such as loading the engine from a second URL, or calling a terminated worker. |
| `unsupported` | The document uses something the library does not handle. |
| `failed` | Anything unexpected; the message names it. |

## Good to know

- Tested in Chromium-based browsers (as a page and in a worker, with and without cross-origin isolation) and in
  Node.js 22 and later.
- The engine is single-threaded: one call at a time per engine. A worker runs its calls in order; start more workers
  to work in parallel, each with its own engine, memory and file system.
- WebAssembly memory grows to the peak of the largest call and does not shrink. Terminating a worker gives it back.
- The PDF export is not InDesign: the line breaker is greedy (InDesign's default is the paragraph composer), and a
  missing font changes line breaks. Check `missingFonts` and `oversetStories` in the report.
- Images are linked, not embedded, as IDML always does: send generated .idml files together with their images.

Source, other languages (.NET, Node.js, Python, Dart/Flutter, C) and issues:
[github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).

MIT licensed. CoreIdml is an independent project and is not affiliated with Adobe.
