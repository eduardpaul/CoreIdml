import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';

import { ABI_VERSION, CoreIdml, IdmlError, Status } from '../src/index.ts';
import { findLibrary } from '../src/library.ts';

// Runs against the native library in artifacts/native/<rid> (dotnet publish src/CoreIdml.Native -c Release -r <rid>)
// and the fixtures every binding shares.
const root = new URL('../../../', import.meta.url);
const fixtureUrl = new URL('tests/fixtures/article-1photo.idml', root);
const fixture = new Uint8Array(readFileSync(fixtureUrl));
const story = 'Stories/Story_u188.xml';

const idml = await CoreIdml.load();

test('the library loads once and reports its version', () => {
  assert.match(idml.version, /^\d+\.\d+\.\d+/);
  assert.equal(ABI_VERSION, 1);
  assert.ok(existsSync(idml.libraryPath), idml.libraryPath);
  assert.equal(CoreIdml.load(), idml);
  assert.throws(() => CoreIdml.load({ libraryPath: join(tmpdir(), 'another-coreidml.dll') }), { name: 'IdmlError', code: Status.invalidOperation });
});

test('a document is opened and described', () => {
  using doc = idml.open(fixtureUrl);

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

test('a document saves to a file with a name that is not ASCII', () => {
  const folder = mkdtempSync(join(tmpdir(), 'coreidml-'));
  try {
    const path = join(folder, 'ficha-ñ-日本.idml');
    using doc = idml.open(fixture);

    doc.saveFile(path);

    using saved = idml.open(path);
    assert.equal(saved.info().parts.length, 16);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test('an ArrayBuffer, binary parts and empty parts are accepted', () => {
  using doc = idml.open(fixture.buffer.slice(fixture.byteOffset, fixture.byteOffset + fixture.byteLength));
  const bytes = new Uint8Array([0, 1, 2, 0, 255]);

  doc.writePart('Extra/data.bin', bytes);
  doc.writePart('Extra/empty.bin', new Uint8Array(0));

  assert.deepEqual(doc.readPart('Extra/data.bin'), bytes);
  assert.deepEqual(doc.readPart('Extra/empty.bin'), new Uint8Array(0));
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
  assert.throws(() => idml.open(join(tmpdir(), 'no-such-file.idml')), { name: 'IdmlError', code: Status.io });

  using doc = idml.open(fixture);
  assert.throws(() => doc.readPart('Stories/Nope.xml'), { name: 'IdmlError', code: Status.notFound });
  assert.throws(() => doc.writePart('bad\\path.xml', 'x'), { name: 'IdmlError', code: Status.invalidArgument });
  assert.throws(() => doc.writePart(story, 42 as unknown as string), { name: 'IdmlError', code: Status.invalidArgument });
  assert.throws(() => idml.open(42 as unknown as string), { name: 'IdmlError', code: Status.invalidArgument });
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

test('the library is looked for in a fixed order', () => {
  const none = { resolvePackage: () => undefined, exists: () => false, env: {}, platform: 'linux' as const, arch: 'x64' };

  assert.deepEqual(findLibrary({ ...none, explicit: '/x/lib.so' }).candidates, [{ path: '/x/lib.so', source: 'the libraryPath option' }]);
  assert.deepEqual(findLibrary({ ...none, env: { COREIDML_LIBRARY: '/env/lib.so' } }).candidates.map((c) => c.path), ['/env/lib.so']);

  const all = findLibrary({ ...none, resolvePackage: (name) => `/pkgs/${name}`, sourceRoot: '/repo', exists: () => true });
  assert.deepEqual(
    all.candidates.map((c) => c.path.replaceAll('\\', '/')),
    ['/pkgs/@coreidml/node-linux-x64/libcoreidml.so', '/repo/artifacts/native/linux-x64/libcoreidml.so', 'libcoreidml.so'],
  );

  const missing = findLibrary({ ...none, sourceRoot: '/repo' });
  assert.deepEqual(missing.candidates.map((c) => c.path), ['libcoreidml.so']);
  assert.equal(missing.skipped.length, 3);
});
