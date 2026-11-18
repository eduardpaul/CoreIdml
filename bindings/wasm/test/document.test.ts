import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { before, test } from 'node:test';
import { fileURLToPath } from 'node:url';

import { ABI_VERSION, CoreIdml, IdmlError, Status } from '../src/index.ts';

// Runs against the WebAssembly build in artifacts/wasm (dotnet publish src/CoreIdml.Wasm -c Release) and the
// fixtures every binding shares.
const root = new URL('../../../', import.meta.url);
const frameworkUrl = new URL('artifacts/wasm/_framework/', root);
const fixture = new Uint8Array(readFileSync(new URL('tests/fixtures/article-1photo.idml', root)));
const story = 'Stories/Story_u188.xml';

let idml: CoreIdml;

before(async () => {
  assert.ok(existsSync(fileURLToPath(frameworkUrl)), `No WebAssembly build at ${frameworkUrl}. Run: npm run build:wasm`);
  idml = await CoreIdml.load({ frameworkUrl });
});

test('the build loads once and reports its version', async () => {
  assert.match(idml.version, /^\d+\.\d+\.\d+/);
  assert.equal(ABI_VERSION, 1);
  assert.equal(await CoreIdml.load({ frameworkUrl }), idml);
  await assert.rejects(CoreIdml.load({ frameworkUrl: 'https://example.invalid/_framework/' }), { name: 'IdmlError', code: Status.invalidOperation });
});

test('a document is opened and described', () => {
  using doc = idml.open(fixture);

  const info = doc.info();

  assert.equal(info.domVersion, '10.0');
  assert.deepEqual(info.spreads, ['Spreads/Spread_ud6.xml']);
  assert.deepEqual(info.masterSpreads, ['MasterSpreads/MasterSpread_ua5.xml']);
  assert.equal(info.stories.length, 3);
  assert.equal(info.parts.length, 16);
  assert.deepEqual(info.parts[0], { path: 'mimetype', length: 43, modified: false });
});

test('a part is read, replaced and saved; the other parts stay byte for byte', () => {
  using doc = idml.open(fixture);
  const original = doc.readText(story);
  const edited = original.replace('<Story ', '<Story Marker="edited" ');
  assert.notEqual(edited, original);

  doc.writePart(story, edited);
  const saved = doc.save();

  assert.deepEqual(doc.info().parts.filter((part) => part.modified).map((part) => part.path), [story]);
  using before = idml.open(fixture);
  using after = idml.open(saved);
  assert.equal(after.readText(story), edited);
  for (const part of before.info().parts.filter((part) => part.path !== story)) {
    assert.deepEqual(after.readPart(part.path), before.readPart(part.path), `${part.path} changed`);
  }
});

test('an ArrayBuffer and binary parts are accepted', () => {
  using doc = idml.open(fixture.buffer.slice(fixture.byteOffset, fixture.byteOffset + fixture.byteLength));
  const bytes = new Uint8Array([0, 1, 2, 0, 255]);

  doc.writePart('Extra/data.bin', bytes);

  assert.deepEqual(doc.readPart('Extra/data.bin'), bytes);
});

test('failures are IdmlErrors with the status codes of the library', () => {
  assert.throws(
    () => idml.open(new TextEncoder().encode('this is not a zip file')),
    (error: unknown) => {
      assert.ok(error instanceof IdmlError);
      assert.equal(error.code, Status.invalidFormat);
      // The text of .NET's own message, not a resource key such as "CDCorrupt".
      assert.match(error.message, /^Not an IDML package: \S+ \S+/);
      return true;
    },
  );

  using doc = idml.open(fixture);
  assert.throws(() => doc.readPart('Stories/Nope.xml'), { name: 'IdmlError', code: Status.notFound });
  assert.throws(() => doc.writePart('bad\\path.xml', 'x'), { name: 'IdmlError', code: Status.invalidArgument });
  assert.throws(() => doc.writePart(story, 42 as unknown as string), { name: 'IdmlError', code: Status.invalidArgument });
  assert.throws(() => idml.open('nope' as unknown as Uint8Array), { name: 'IdmlError', code: Status.invalidArgument });
  // A failure does not poison the document.
  assert.equal(doc.info().parts.length, 16);
});

test('a closed document refuses calls, and closing twice is harmless', () => {
  const doc = idml.open(fixture);

  doc.close();
  doc.close();

  assert.equal(doc.closed, true);
  assert.throws(() => doc.info(), { name: 'IdmlError', code: Status.invalidHandle });
});
