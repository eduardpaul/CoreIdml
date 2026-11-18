# @coreidml/wasm

**Read, edit and write Adobe InDesign IDML files in the browser, without InDesign and without a server.**

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles). CoreIdml opens
it, lets you read and change any part, and writes it back so that InDesign opens the result as if nothing but your
edit had happened. This package is the engine compiled to WebAssembly: the file never leaves the user's machine.

```sh
npm install @coreidml/wasm
```

```js
import { CoreIdml } from '@coreidml/wasm';

const idml = await CoreIdml.load();

input.addEventListener('change', async () => {
  const file = input.files[0];
  const doc = idml.open(await file.arrayBuffer());
  try {
    for (const story of doc.info().stories) {
      const xml = doc.readText(story);
      if (xml.includes('2025')) doc.writePart(story, xml.replaceAll('2025', '2026'));
    }
    const edited = new Blob([doc.save()], { type: 'application/vnd.adobe.indesign-idml-package' });
    download.href = URL.createObjectURL(edited);
  } finally {
    doc.close();
  }
});
```

## Why CoreIdml

- **Client-side.** Templates and customer documents are processed in the tab. Nothing to upload, nothing to host.
- **Surgical edits.** A part you do not touch is written back byte for byte. Change one story, and the designer's
  file differs in exactly that story.
- **Output InDesign accepts.** `mimetype` first and uncompressed, InDesign's own formatting for what you change.
- **Safe with files from anywhere.** Checksums verified on read, document type declarations refused, typed errors.
- **One API, two packages.** [`@coreidml/node`](https://www.npmjs.com/package/@coreidml/node) has the same API
  over a native library; code written against one runs on the other.
- **No native code.** The same package runs in Node.js, for tests and server-side rendering.

TypeScript declarations are included. The package is an ES module with no dependencies.

## Loading

`CoreIdml.load()` downloads and starts the engine once; later calls return the same instance. After that, every
call is synchronous.

The engine is the folder `dist/_framework` of this package: about 6.6 MB of `.wasm` and `.js` files, **2 MB with
Brotli**. By default it is loaded from next to the module, which is where it is when the package is served as it
is (native ES modules, a CDN, Node.js).

A bundler does not know about that folder. Copy `node_modules/@coreidml/wasm/dist/_framework` into your static
assets (with Vite, into `public/`), and say where it ends up:

```js
const idml = await CoreIdml.load({ frameworkUrl: '/coreidml/_framework/' });
```

Serve `.wasm` files as `application/wasm`, and let the server compress them.

## API

### Opening

```ts
const doc = idml.open(bytes);   // a Uint8Array or an ArrayBuffer holding an .idml file; not kept after the call
```

### `IdmlDocument`

| Member | |
| --- | --- |
| `info()` | The parts of the package and the manifest; see below. |
| `readPart(path)` | The bytes of one part, as a `Uint8Array`, exactly as they would be saved now. |
| `readText(path)` | The same as text. Parts are UTF-8 XML. |
| `writePart(path, data)` | Adds or replaces a part with a string (written as UTF-8) or a `Uint8Array`. |
| `save()` | The document as the bytes of an .idml file. |
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

### Errors

Every failure is an `IdmlError` with a `code`; compare it with `Status`:

```ts
import { IdmlError, Status } from '@coreidml/wasm';

try {
  idml.open(bytes);
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
| `invalidOperation` | The call is not valid right now, such as loading the engine from a second URL. |
| `failed` | Anything unexpected; the message names it. |

## Good to know

- Tested in Chromium-based browsers and in Node.js 22 and later.
- Calls are synchronous and run on the thread that loaded the engine.
- A page uses one copy of the engine.
- **Scope of 0.1.** The library works at the package level: every part as XML or bytes, and full control over what
  is written. A typed document model on top of it (pages, frames, stories, styles) is in development.

Source, other languages (.NET, Python, Dart/Flutter, C) and issues:
[github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).

MIT licensed. CoreIdml is an independent project and is not affiliated with Adobe.
