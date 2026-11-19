import assert from 'node:assert/strict';
import { before, test } from 'node:test';

import { CoreIdml, LINKS_DIRECTORY, Status } from '../src/index.ts';
import { fixture, fixtureFonts, frameworkUrl, loadEngine, png } from './helpers.ts';

// Regressions for the defects found by the code review of this binding.
let idml: CoreIdml;

before(async () => {
  idml = await loadEngine();
  idml.addFonts(fixtureFonts());
  idml.addLinks({ 'picture.png': png(160, 120) });
});

test('frameworkUrl may be relative, without the final slash, and a bad one rejects instead of throwing', async () => {
  const slashless = frameworkUrl.href.replace(/\/$/, '');

  assert.equal(await CoreIdml.load({ frameworkUrl: slashless }), idml);
  // In Node there is no page location: a relative URL is relative to the module, bindings/wasm/src/index.ts.
  assert.equal(await CoreIdml.load({ frameworkUrl: '../../../artifacts/wasm/_framework' }), idml);
  assert.equal(await CoreIdml.load({ frameworkUrl: `${frameworkUrl.href}#part` }), idml);
  const bad = CoreIdml.load({ frameworkUrl: 'http://[' });
  await assert.rejects(bad, { name: 'IdmlError', code: Status.invalidArgument });
  // A query is kept apart from the folder: another build, refused while this one is loaded.
  await assert.rejects(CoreIdml.load({ frameworkUrl: `${slashless}?v=7` }), (error: Error) => /_framework\/\?v=7/.test(error.message));
});

test('an argument that never reaches the library is not reported with the last failure of another call', () => {
  using doc = idml.open(fixture('article-1photo.idml'));
  assert.throws(() => doc.story('nope'), { code: Status.notFound });

  assert.throws(() => doc.exportXml({ at: 5 as unknown as string }), (error: Error & { code: number }) =>
    error.code === Status.invalidArgument && !/nope/.test(error.message));
  assert.throws(() => doc.importXml('<Root/>', { at: 5 as unknown as string }), { code: Status.invalidArgument });
  assert.throws(() => doc.fill(null as never), { code: Status.invalidArgument });
  assert.equal(doc.fill().filled !== undefined, true);
});

test('options naming a folder as undefined keep the folders addFonts and addLinks fill', () => {
  using doc = idml.create({
    pages: [{ items: [
      { type: 'image', name: 'picture', x: 36, y: 36, width: 240, height: 180, path: `${LINKS_DIRECTORY}/picture.png` },
      { type: 'text', x: 36, y: 230, width: 400, height: 60, text: 'Caption' },
    ] }],
  });

  const plain = doc.exportPdfBytes({ useSystemFonts: false });
  const undefinedFolders = doc.exportPdfBytes({ useSystemFonts: false, fontDirectories: undefined, linkDirectories: undefined });

  assert.deepEqual(undefinedFolders.report.missingFonts, plain.report.missingFonts);
  assert.deepEqual(undefinedFolders.report.missingImages, []);
  assert.ok(undefinedFolders.report.missingFonts.every((font) => !/standard PDF font/.test(font.reason)));
});

test('the folder an import made goes with its document, and with a failed import', () => {
  using source = idml.create({ pages: [{ items: [
    { type: 'image', name: 'picture', x: 36, y: 36, width: 240, height: 180, path: `${LINKS_DIRECTORY}/picture.png` },
  ] }] });
  const { pdf } = source.exportPdfBytes({ useSystemFonts: false });
  const folders = () => idml.listFiles('/import');

  const imported = idml.importPdf(pdf);
  assert.ok(idml.listFiles(imported.imageDirectory).length > 0);
  imported.document.close();
  assert.deepEqual(idml.listFiles(imported.imageDirectory), []);

  const before = folders();
  assert.throws(() => idml.importPdf(pdf, { onProgress: () => false }), { code: Status.cancelled });
  assert.deepEqual(folders(), before);

  // A folder the caller names is the caller's.
  const named = idml.importPdf(pdf, { imageDirectory: '/kept-images' });
  named.document.close();
  assert.ok(idml.listFiles('/kept-images').length > 0);
  idml.remove('/kept-images');
});
