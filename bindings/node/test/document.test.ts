import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, test } from 'node:test';
import { pathToFileURL } from 'node:url';

import { ABI_VERSION, CoreIdml, IdmlDocument, IdmlError, Status } from '../src/index.ts';
import { findLibrary } from '../src/library.ts';
import { SPEC, article, editorial, fixtures, importXmlTemplate, tempDirectory } from './helpers.ts';

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

const temp = tempDirectory();

const failsWith =
  (code: number) =>
  (error: unknown): boolean =>
    error instanceof IdmlError && error.code === code && error.message.length > 0;

describe('creating and editing', () => {
  test('create, fill, summary, story, setRunText, replaceText, validate, save, diff, importPages', () => {
    let bytes: Uint8Array;
    const path = join(temp, 'ficha.idml');
    {
      using doc = idml.create(SPEC);
      const report = doc.fill({ text: { codigo: 'F-001', cliente: 'Ñandú S.A.', nada: '?' } });
      assert.equal(report.filled.codigo, 1);
      assert.deepEqual(report.missing, ['nada']);

      const summary = doc.summary({ pages: [0], sections: ['pages', 'stories'] });
      const texts = summary.pages!.at(0)!.items.map((item) => item.text);
      assert.ok(texts.includes('Ficha F-001'));
      assert.ok(texts.includes('Ñandú S.A.'));
      assert.equal(summary.links, undefined);

      const storyId = summary.pages!.at(0)!.items.find((item) => item.name === 'cliente')!.storyId!;
      doc.setRunText(storyId, 0, '43°16′38″N');
      const story = doc.story(storyId);
      assert.equal(story.text, '43°16′38″N');
      assert.equal(story.runs!.at(0)!.index, 0);
      assert.equal(story.runs!.at(0)!.scope, 'body');

      assert.equal(doc.replaceText('Ficha', 'Hoja'), 1);
      assert.equal(doc.replaceText('no aparece', 'x'), 0);
      const validation = doc.validate();
      assert.equal(validation.isValid, true);
      assert.deepEqual(validation.issues, []);

      doc.saveFile(path);
      bytes = doc.save();
      assert.equal(Buffer.from(bytes.subarray(0, 2)).toString('latin1'), 'PK');
    }
    using copy = idml.open(bytes);
    using saved = idml.open(pathToFileURL(path));
    assert.deepEqual(copy.diff(saved), { identical: true, changes: [] });
    assert.equal(saved.importPages(copy, [1]), 1);
    assert.equal(saved.summary().pageCount, 3);
    assert.equal(saved.importPages(copy), 2);
    assert.equal(saved.importPages(saved, [0]), 1);
    assert.equal(saved.summary({ maxTextLength: 0, includeResources: false }).pageCount, 6);
    const diff = copy.diff(saved);
    assert.equal(diff.identical, false);
    assert.ok(diff.changes.some((change) => change.kind === 'page-count'), JSON.stringify(diff.changes));
  });

  test('a blank document, apply, summary with runs, a spec given as JSON', () => {
    using doc = idml.create();
    assert.equal(doc.summary().pageCount, 1);
    doc.apply({
      colors: [{ name: 'Marca', cmyk: [0, 90, 85, 0] }],
      pages: [{ items: [{ type: 'text', x: 10, y: 10, width: 100, height: 20, text: 'Añadida', fill: 'Marca' }] }],
    });
    const summary = doc.summary({ includeRuns: true });
    assert.equal(summary.pageCount, 2);
    assert.ok(summary.swatches!.includes('Marca'));
    const story = summary.stories!.find((entry) => entry.text === 'Añadida')!;
    assert.ok(story.runs!.length >= 1);

    doc.apply('{"pages":[{}]}');
    assert.equal(doc.summary({ sections: ['pages'] }).pageCount, 3);
    using fromJson = idml.create(JSON.stringify(SPEC));
    assert.equal(fromJson.summary({ sections: [] }).pageCount, 2);
  });

  test('a bytes round trip keeps the package byte for byte', () => {
    const original = new Uint8Array(readFileSync(article));
    using doc = idml.open(original);
    const again = doc.save();
    using reopened = idml.open(again);
    assert.equal(doc.diff(reopened).identical, true);
    assert.deepEqual(reopened.save(), again);
  });

  test('XML structure, export and import', () => {
    const spec = { pages: [{ items: [{ type: 'text', tag: 'titulo', x: 10, y: 10, width: 200, height: 20, text: 'Antes' }] }] };
    using doc = idml.create(spec);
    assert.equal(doc.xmlStructure()!.children!.at(0)!.tag, 'titulo');
    assert.match(doc.exportXml(), /<titulo>Antes<\/titulo>/);
    assert.match(doc.exportXml({ at: '/Root/titulo' }), /^<titulo>Antes<\/titulo>$/);
    const report = doc.importXml('<Root><titulo>Después</titulo></Root>');
    assert.ok(report.updated >= 1);
    assert.match(doc.exportXml(), /Después/);
  });

  test('XML import with options onto a real InDesign export', () => {
    using doc = idml.open(article);
    doc.apply({ characterStyles: [{ name: 'Negrita', fontStyle: 'Bold' }] });
    const report = doc.importXml('<module><headline>Nuevo <b>titular</b></headline></module>', {
      at: '/Root/module',
      characterStyles: { b: 'Negrita' },
    });
    assert.ok(report.updated >= 1, JSON.stringify(report));
    assert.match(doc.exportXml({ at: '/Root/module/headline' }), /Nuevo/);
  });

  test('XML import of the SimpleIDML data onto its template', () => {
    using doc = idml.open(importXmlTemplate);
    assert.ok(doc.xmlStructure());
    const xml = readFileSync(join(fixtures, 'xml', 'article-1photo_import-xml.xml'), 'utf8');
    const report = doc.importXml(xml, { relinkOnly: true });
    assert.ok(report.updated + report.added >= 1, JSON.stringify(report));
    assert.equal(doc.validate().isValid, true);
  });

  test('relink a link by id', () => {
    using doc = idml.open(article);
    const link = doc.summary({ sections: ['links'] }).links!.at(0)!;
    doc.relink(link.id, join(temp, 'foto nueva.jpg'));
    const relinked = doc.summary({ sections: ['links'] }).links!.find((entry) => entry.id === link.id)!;
    assert.notEqual(relinked.uri, link.uri);
    assert.match(decodeURIComponent(relinked.uri), /foto nueva\.jpg$/);

    doc.relink(link.id, pathToFileURL(join(temp, 'otra.jpg')));
    assert.match(doc.summary({ sections: ['links'] }).links!.at(0)!.uri, /otra\.jpg$/);
  });

  test('the pages of an InDesign editorial are imported into another document', () => {
    using target = idml.open(article);
    using source = idml.open(editorial);
    const pages = source.summary({ sections: ['pages'] }).pageCount;
    assert.equal(target.importPages(source), pages);
    assert.equal(target.summary({ sections: ['pages'] }).pageCount, 1 + pages);
  });


  test('the same page imported twice in one call gets ids of its own each time', () => {
    using doc = idml.create(SPEC);
    assert.equal(doc.importPages(doc, [0, 0]), 2);
    const ids = doc.summary({ sections: ['pages'] }).pages!.flatMap((page) => page.items.map((item) => item.id));
    assert.equal(new Set(ids).size, ids.length, ids.join(','));
    assert.equal(doc.validate().isValid, true, JSON.stringify(doc.validate().issues));
  });

  test('XML that is not well formed is an invalid format', () => {
    using doc = idml.open(article);
    assert.throws(() => doc.importXml('<module><headline>open'), failsWith(Status.invalidFormat));
  });
});

describe('errors of the document model', () => {
  test('carry the code and the native message', () => {
    assert.throws(() => idml.create({ pages: 'not a list' } as never), failsWith(Status.invalidArgument));
    assert.throws(() => idml.create('{ not json'), failsWith(Status.invalidArgument));
    assert.throws(() => idml.create(42 as unknown as string), failsWith(Status.invalidArgument));

    using doc = idml.create(SPEC);
    assert.throws(() => doc.setRunText('nope', 0, 'x'), failsWith(Status.notFound));
    assert.throws(() => doc.setRunText('nope', 0.5, 'x'), failsWith(Status.invalidArgument));
    assert.throws(() => doc.replaceText('', 'x'), failsWith(Status.invalidArgument));
    assert.throws(() => doc.story('nope'), failsWith(Status.notFound));
    assert.throws(() => doc.summary({ pages: [7] }), failsWith(Status.notFound));
    assert.throws(() => doc.importXml(''), failsWith(Status.invalidArgument));
    assert.throws(() => doc.saveFile(''), failsWith(Status.invalidArgument));
    assert.throws(() => doc.importPages(doc, [99]), failsWith(Status.notFound));
    assert.throws(() => doc.importPages({} as IdmlDocument), failsWith(Status.invalidArgument));
    assert.throws(() => doc.diff(null as unknown as IdmlDocument), failsWith(Status.invalidArgument));
    assert.throws(() => doc.fill(null as never), failsWith(Status.invalidArgument));
    assert.throws(() => doc.relink('nope', 'x.jpg'), failsWith(Status.notFound));
    // The document still works after errors.
    assert.equal(doc.summary().pageCount, 2);
  });

  test('a closed document: every call throws invalidHandle, also as the other document of a call', () => {
    const doc = idml.create();
    using other = idml.create();
    assert.ok(doc.handle > 0);
    doc.close();
    assert.equal(doc.handle, 0);
    for (const call of [
      () => doc.summary(),
      () => doc.save(),
      () => doc.saveFile(join(temp, 'x.idml')),
      () => doc.fill({}),
      () => doc.exportPdfBytes(),
      () => other.diff(doc),
      () => other.importPages(doc),
    ]) {
      assert.throws(call, failsWith(Status.invalidHandle));
    }
  });
});

describe('lifetime', () => {
  test('using closes the document at the end of the block', () => {
    let kept: IdmlDocument;
    {
      using doc = idml.create();
      kept = doc;
      assert.equal(doc.closed, false);
    }
    assert.equal(kept.closed, true);
  });

  test('using closes the document when the block throws', () => {
    let kept: IdmlDocument | undefined;
    assert.throws(() => {
      using doc = idml.create();
      kept = doc;
      throw new Error('boom');
    }, /boom/);
    assert.equal(kept!.closed, true);
  });
});
