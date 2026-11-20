import assert from 'node:assert/strict';
import { before, test } from 'node:test';

import { CoreIdml, FONTS_DIRECTORY, IdmlError, LINKS_DIRECTORY, Status } from '../src/index.ts';
import { fixture, fixtureFonts, loadEngine, png, startsWith } from './helpers.ts';

// PDF export and import. The engine sees no system fonts in WebAssembly: the Liberation fonts of the fixtures go
// into the in-memory file system, where PDF calls look by default.
let idml: CoreIdml;
const article = fixture('article-1photo.idml');

/** Three pages of text in Liberation Serif's stand-in "Minion Pro", and a picture on the first. */
function createReport(engine: CoreIdml) {
  return engine.create({
    pages: [
      {
        items: [
          { type: 'image', name: 'picture', x: 36, y: 36, width: 240, height: 180, path: `${LINKS_DIRECTORY}/picture.png` },
          { type: 'text', name: 'caption', x: 36, y: 230, width: 400, height: 60, text: 'A picture and its caption' },
        ],
      },
      { items: [{ type: 'text', x: 36, y: 36, width: 400, height: 60, text: 'Page two' }] },
      { items: [{ type: 'text', x: 36, y: 36, width: 400, height: 60, text: 'Page three' }] },
    ],
  });
}

before(async () => {
  idml = await loadEngine();
  idml.addFonts(fixtureFonts());
  idml.addLinks({ 'picture.png': png(160, 120) });
});

test('a document is rendered with the fonts of the in-memory file system', () => {
  using doc = idml.open(article);

  const { pdf, report } = doc.exportPdfBytes({ useSystemFonts: false });

  assert.ok(startsWith(pdf, '%PDF-'), 'a PDF');
  assert.ok(pdf.length > 10_000);
  assert.equal(report.pageCount, 1);
  // The template asks for Minion Pro; the Liberation fonts added to /fonts stand in for it.
  assert.ok(report.missingFonts.length > 0);
  for (const font of report.missingFonts) {
    assert.equal(font.requestedFamily, 'Minion Pro');
    assert.match(font.usedFont, /^Liberation /);
  }
  // Whether the template's stories fit depends on the line breaking and on the substitute fonts; the test below
  // checks the overset report with a story that cannot fit.
  assert.ok(Array.isArray(report.oversetStories));
  // The template's photo is on a disk this engine does not have.
  assert.equal(report.missingImages.length, 1);
});

test('a story that does not fit its frame is reported as overset', () => {
  using doc = idml.create({
    pages: [{ items: [{ type: 'text', x: 36, y: 36, width: 60, height: 14, text: 'This text cannot fit in a frame one line high and sixty points wide.' }] }],
  });

  const { report } = doc.exportPdfBytes({ useSystemFonts: false });

  assert.equal(report.oversetStories.length, 1);
});

test('placed images are found in the links folder; pages can be chosen', () => {
  using doc = createReport(idml);

  const all = doc.exportPdfBytes({ useSystemFonts: false, title: 'Report' });
  const second = doc.exportPdfBytes({ useSystemFonts: false, pages: [2] });

  assert.equal(all.report.pageCount, 3);
  assert.deepEqual(all.report.missingImages, []);
  assert.equal(second.report.pageCount, 1);
  assert.ok(second.pdf.length < all.pdf.length);
});

test('options name other folders, and leaving the defaults out shows what they do', () => {
  using doc = createReport(idml);
  idml.writeFile('/elsewhere/picture.png', png(16, 16));

  const noFonts = doc.exportPdfBytes({ useSystemFonts: false, fontDirectories: ['/nothing-here'], linkDirectories: ['/elsewhere'] });

  assert.deepEqual(noFonts.report.missingImages, []);
  // Without fonts, a standard PDF font stands in, not embedded.
  assert.ok(noFonts.report.missingFonts.length > 0);
  assert.ok(noFonts.report.missingFonts.every((font) => /standard PDF font/.test(font.reason)), JSON.stringify(noFonts.report.missingFonts));
});

test('progress is reported after each page', () => {
  using doc = createReport(idml);
  const calls: [number, number][] = [];

  doc.exportPdfBytes({ useSystemFonts: false, onProgress: (done, total) => void calls.push([done, total]) });

  assert.deepEqual(calls, [
    [1, 3],
    [2, 3],
    [3, 3],
  ]);
});

test('onProgress returning false cancels; the document stays usable', () => {
  using doc = createReport(idml);
  const calls: number[] = [];

  assert.throws(
    () => doc.exportPdfBytes({ useSystemFonts: false, onProgress: (done) => (calls.push(done), done < 2) }),
    (error: unknown) => error instanceof IdmlError && error.code === Status.cancelled,
  );

  assert.deepEqual(calls, [1, 2]);
  assert.equal(doc.exportPdfBytes({ useSystemFonts: false }).report.pageCount, 3);
});

test('an exception thrown by onProgress cancels and is the cause of the error', () => {
  using doc = createReport(idml);
  const problem = new Error('the user closed the dialog');

  assert.throws(
    () =>
      doc.exportPdfBytes({
        useSystemFonts: false,
        onProgress: () => {
          throw problem;
        },
      }),
    (error: unknown) => error instanceof IdmlError && error.code === Status.cancelled && error.cause === problem,
  );
  assert.throws(() => doc.exportPdfBytes({ onProgress: 'yes' as never }), { name: 'IdmlError', code: Status.invalidArgument });
});

test('importPdf rebuilds a document and returns the images it extracted', () => {
  using source = createReport(idml);
  const { pdf } = source.exportPdfBytes({ useSystemFonts: false });
  const progress: number[] = [];

  const imported = idml.importPdf(pdf, { onProgress: (done) => void progress.push(done) });
  using doc = imported.document;

  assert.deepEqual(progress, [1, 2, 3]);
  assert.equal(imported.report.imageFrames, 1);
  assert.ok(imported.report.textFrames >= 3);
  assert.match(imported.imageDirectory, /^\/import\/\d+$/);
  const names = Object.keys(imported.images);
  assert.equal(names.length, 1);
  assert.deepEqual(imported.report.images, [`${imported.imageDirectory}/${names[0]}`]);
  assert.ok(startsWith(imported.images[names[0]!]!, '\x89PNG') || startsWith(imported.images[names[0]!]!, '\xff\xd8'), 'a PNG or a JPEG');
  assert.deepEqual(idml.listFiles(imported.imageDirectory), names);
  // The fonts of the PDF are checked against /fonts.
  assert.ok(imported.report.fonts.length > 0);
  assert.ok(imported.report.fonts.every((font) => font.available === true), JSON.stringify(imported.report.fonts));

  const summary = doc.summary({ maxTextLength: -1 });
  assert.equal(summary.pageCount, 3);
  assert.equal(summary.links[0].uri, `file:${imported.report.images[0]}`);
  assert.ok(summary.stories.some((story: { text: string }) => story.text.includes('A picture and its caption')));
  // The links point into the in-memory file system, so the rebuilt document renders with its image.
  assert.deepEqual(doc.exportPdfBytes({ useSystemFonts: false }).report.missingImages, []);
});

test('importPdf writes the images where it is told, and each import gets its own folder', () => {
  using source = createReport(idml);
  const { pdf } = source.exportPdfBytes({ useSystemFonts: false, pages: [1] });

  const first = idml.importPdf(pdf);
  const second = idml.importPdf(pdf.buffer.slice(pdf.byteOffset, pdf.byteOffset + pdf.byteLength) as ArrayBuffer);
  const chosen = idml.importPdf(pdf, { imageDirectory: '/my-images', importShapes: false });

  assert.notEqual(first.imageDirectory, second.imageDirectory);
  assert.equal(chosen.imageDirectory, '/my-images');
  assert.deepEqual(idml.listFiles('/my-images'), Object.keys(chosen.images));
  for (const result of [first, second, chosen]) {
    result.document.close();
  }
});

test('importPdf cancels like an export, and refuses what is not a PDF', () => {
  using source = createReport(idml);
  const { pdf } = source.exportPdfBytes({ useSystemFonts: false });

  assert.throws(() => idml.importPdf(pdf, { onProgress: () => false }), { name: 'IdmlError', code: Status.cancelled });
  assert.throws(() => idml.importPdf(new Uint8Array()), { name: 'IdmlError', code: Status.invalidArgument });
  assert.throws(() => idml.importPdf(new TextEncoder().encode('not a PDF')), { name: 'IdmlError', code: Status.invalidFormat });
});

test('an AbortSignal cancels too: checked before the call and between pages', () => {
  using doc = createReport(idml);
  const before = AbortSignal.abort(new Error('too late'));
  const controller = new AbortController();
  const reason = new Error('stop');

  assert.throws(
    () => doc.exportPdfBytes({ signal: before }),
    (error: unknown) => error instanceof IdmlError && error.code === Status.cancelled && error.cause === before.reason,
  );
  assert.throws(
    () => doc.exportPdfBytes({ useSystemFonts: false, signal: controller.signal, onProgress: (done) => void (done === 2 && controller.abort(reason)) }),
    (error: unknown) => error instanceof IdmlError && error.code === Status.cancelled && error.cause === reason,
  );
  assert.equal(doc.exportPdfBytes({ useSystemFonts: false, signal: new AbortController().signal }).report.pageCount, 3);
});

test('fonts and links live in their folders of the in-memory file system', () => {
  assert.equal(FONTS_DIRECTORY, '/fonts');
  assert.deepEqual(idml.listFiles(FONTS_DIRECTORY), Object.keys(fixtureFonts()).sort());
  assert.deepEqual(idml.listFiles(LINKS_DIRECTORY), ['picture.png']);
  assert.throws(() => idml.addFonts({ '': new Uint8Array(1) }), { name: 'IdmlError', code: Status.invalidArgument });
  assert.throws(() => idml.addLinks({ 'x.png': 'bytes' as never }), { name: 'IdmlError', code: Status.invalidArgument });
});
