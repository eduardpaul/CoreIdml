# @coreidml/node

**Read, edit and write Adobe InDesign IDML files from Node.js, without InDesign.**

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles). CoreIdml opens
it, lets you read and change any part, and writes it back so that InDesign opens the result as if nothing but your
edit had happened.

```sh
npm install @coreidml/node
```

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

## Why CoreIdml

- **Surgical edits.** A part you do not touch is written back byte for byte. Change one story, and the designer's
  file differs in exactly that story.
- **Output InDesign accepts.** `mimetype` first and uncompressed, InDesign's own formatting for what you change.
- **Reproducible files.** Untouched parts keep their timestamps and compression, so an unchanged document saves to
  the same bytes every time.
- **Safe with uploads.** Checksums verified on read, document type declarations refused, typed errors.
- **Nothing to compile.** The engine is a prebuilt native library, installed with the package and called through
  [koffi](https://koffi.dev). No node-gyp, no .NET runtime, no InDesign.
- **In process.** Native code behind synchronous calls: no child process, no service to run.

TypeScript declarations are included. The package is an ES module.

## API

```ts
const idml = await CoreIdml.load();   // loads the native library once; later calls return the same instance
idml.version;                         // version of the engine
```

`CoreIdml.load()` returns the instance directly in this package and a promise in
[`@coreidml/wasm`](https://www.npmjs.com/package/@coreidml/wasm); with `await`, the same code runs on both.

### Opening

```ts
const fromFile = idml.open('brochure.idml');        // a path or a file: URL
const fromBytes = idml.open(uint8Array);            // a Uint8Array, Buffer or ArrayBuffer; not kept after the call
```

### `IdmlDocument`

| Member | |
| --- | --- |
| `info()` | The parts of the package and the manifest; see below. |
| `readPart(path)` | The bytes of one part, as a `Uint8Array`, exactly as they would be saved now. |
| `readText(path)` | The same as text. Parts are UTF-8 XML. |
| `writePart(path, data)` | Adds or replaces a part with a string (written as UTF-8) or a `Uint8Array`. |
| `save()` | The document as the bytes of an .idml file. |
| `saveFile(path)` | Writes the .idml file. |
| `close()` | Releases the document. `using doc = idml.open(...)` does it for you where `using` is available. |
| `closed` | Whether it was closed. |

```ts
doc.info();
// {
//   domVersion: '10.0',
//   parts: [{ path: 'designmap.xml', length: 16154, modified: false }, ...],
//   spreads: ['Spreads/Spread_ud6.xml'],
//   masterSpreads: ['MasterSpreads/MasterSpread_ua5.xml'],
//   stories: ['Stories/Story_u1db.xml', 'Stories/Story_u19f.xml', 'Stories/Story_u188.xml']
// }
```

A document that is never closed is released when it is garbage collected, but closing it yourself frees the memory
at once.

### Errors

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
| `notFound` | A part you named does not exist. |
| `invalidArgument` | An argument is missing or malformed. |
| `invalidHandle` | The document was closed. |
| `io` | Reading or writing a file failed. |
| `invalidOperation` | The call is not valid right now, such as loading a second copy of the library. |
| `failed` | Anything unexpected; the message names it. |

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

- Calls are synchronous and run on the thread that makes them.
- A process uses one copy of the library; `load()` with a different path afterwards is an error.
- **Scope of 0.1.** The library works at the package level: every part as XML or bytes, and full control over what
  is written. A typed document model on top of it (pages, frames, stories, styles) is in development.
- In the browser, or where native code is not an option, use
  [`@coreidml/wasm`](https://www.npmjs.com/package/@coreidml/wasm): the same API on WebAssembly.

Source, other languages (.NET, Python, Dart/Flutter, C) and issues:
[github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).

MIT licensed. CoreIdml is an independent project and is not affiliated with Adobe.
