// PDF export and import: files and memory, fonts, progress, cancellation, and the async variants on worker threads.
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, test } from 'node:test';
import { setTimeout as delay } from 'node:timers/promises';
import { pathToFileURL } from 'node:url';

import { CoreIdml, IdmlError, Status } from '../src/index.ts';
import { SPEC, article, manyPagesSpec, pdfFonts, png, tempDirectory, timed } from './helpers.ts';

const idml = CoreIdml.load();
const temp = tempDirectory();

const isPdf = (bytes: Uint8Array): boolean => Buffer.from(bytes.subarray(0, 5)).toString('latin1') === '%PDF-';

const cancelled = (error: unknown): boolean => error instanceof IdmlError && error.code === Status.cancelled;

describe('synchronous', () => {
  test('export to bytes and to a file, with progress', () => {
    using doc = idml.create(SPEC);
    const calls: Array<[number, number]> = [];
    const { pdf, report } = doc.exportPdfBytes({ ...pdfFonts, onProgress: (done, total) => void calls.push([done, total]) });
    assert.ok(isPdf(pdf));
    assert.equal(report.pageCount, 2);
    assert.deepEqual(calls, [
      [1, 2],
      [2, 2],
    ]);
    assert.deepEqual(report.missingImages, []);
    assert.deepEqual(report.oversetStories, []);

    const path = join(temp, 'ficha ñ.pdf');
    const fileReport = doc.exportPdf(path, { ...pdfFonts, maxImageDpi: 150, pages: [2] });
    assert.equal(fileReport.pageCount, 1);
    assert.ok(isPdf(new Uint8Array(readFileSync(path))));
    assert.equal(doc.exportPdf(pathToFileURL(join(temp, 'url.pdf')), pdfFonts).pageCount, 2);
  });

  test('the same document and fonts give the same PDF size', () => {
    using doc = idml.create(SPEC);
    assert.equal(doc.exportPdfBytes(pdfFonts).pdf.length, doc.exportPdfBytes(pdfFonts).pdf.length);
  });

  test('missing fonts are reported with what replaced them; substitutions choose it', () => {
    using doc = idml.create({
      paragraphStyles: [{ name: 'Rara', font: 'Fuente Inexistente', size: 12 }],
      pages: [{ items: [{ type: 'text', x: 10, y: 10, width: 200, height: 20, text: 'Hola', style: 'Rara' }] }],
    });
    const { report } = doc.exportPdfBytes(pdfFonts);
    const missing = report.missingFonts.find((font) => font.requestedFamily === 'Fuente Inexistente');
    assert.ok(missing, JSON.stringify(report.missingFonts));
    assert.equal(typeof missing.usedFont, 'string');
    assert.deepEqual(missing.pages, ['1']);

    const substituted = doc.exportPdfBytes({ ...pdfFonts, substitutions: { 'Fuente Inexistente': 'Liberation Sans' } }).report;
    assert.equal(substituted.missingFonts.some((font) => font.requestedFamily === 'Fuente Inexistente' && !/Liberation Sans/.test(font.usedFont)), false);
  });

  test('placed images: missing ones are reported; linkRemap and linkDirectories find them', () => {
    // The InDesign export links an image on its author's machine.
    using doc = idml.open(article);
    const missing = doc.exportPdfBytes(pdfFonts).report;
    assert.equal(missing.missingImages.length, 1, JSON.stringify(missing));
    assert.match(missing.missingImages[0]!, /default\.jpg$/);
    assert.ok(missing.missingFonts.some((font) => font.requestedFamily === 'Minion Pro'));

    const links = join(temp, 'links');
    mkdirSync(links, { recursive: true });
    writeFileSync(join(links, 'photo.png'), png(40, 30));
    assert.deepEqual(doc.exportPdfBytes({ ...pdfFonts, linkRemap: { 'default.jpg': join(links, 'photo.png') } }).report.missingImages, []);
    const folder = join(temp, 'by-name');
    mkdirSync(folder, { recursive: true });
    writeFileSync(join(folder, 'default.jpg'), png(40, 30));
    assert.deepEqual(doc.exportPdfBytes({ ...pdfFonts, linkDirectories: [folder] }).report.missingImages, []);

    // A spec image and a filled frame link local files, which render; relink points the link elsewhere.
    using spec = idml.create({
      pages: [
        {
          items: [
            { type: 'image', x: 10, y: 10, width: 100, height: 80, path: join(links, 'photo.png') },
            { type: 'rectangle', name: 'foto', x: 10, y: 100, width: 100, height: 80 },
          ],
        },
      ],
    });
    assert.deepEqual(spec.fill({ images: { foto: join(links, 'photo.png') } }).filled, { foto: 1 });
    assert.deepEqual(spec.exportPdfBytes(pdfFonts).report.missingImages, []);
    writeFileSync(join(links, 'other.png'), png(10, 10));
    const link = spec.summary({ sections: ['links'] }).links!.at(0)!;
    spec.relink(link.id, join(links, 'other.png'), { refit: true });
    assert.match(spec.summary({ sections: ['links'] }).links!.at(0)!.uri, /other\.png$/);
    assert.deepEqual(spec.exportPdfBytes(pdfFonts).report.missingImages, []);
  });

  test('progress returning false cancels', () => {
    using doc = idml.create(SPEC);
    let calls = 0;
    assert.throws(() => doc.exportPdfBytes({ ...pdfFonts, onProgress: () => (++calls, false) }), cancelled);
    assert.equal(calls, 1);
    assert.throws(() => doc.exportPdf(join(temp, 'cancelled.pdf'), { ...pdfFonts, onProgress: () => false }), cancelled);
    // The document is still usable.
    assert.equal(doc.exportPdfBytes(pdfFonts).report.pageCount, 2);
  });

  test('an exception in progress cancels and becomes the cause', () => {
    using doc = idml.create(SPEC);
    const failure = new Error('progress failed');
    assert.throws(
      () =>
        doc.exportPdfBytes({
          ...pdfFonts,
          onProgress: () => {
            throw failure;
          },
        }),
      (error: unknown) => cancelled(error) && (error as IdmlError).cause === failure,
    );
  });

  test('an AbortSignal cancels: aborted before the call, or from inside the call', () => {
    using doc = idml.create(SPEC);
    const before = new AbortController();
    before.abort(new Error('not now'));
    assert.throws(() => doc.exportPdfBytes({ signal: before.signal }), (error: unknown) => cancelled(error) && (error as IdmlError).cause === before.signal.reason);

    const during = new AbortController();
    assert.throws(
      () => doc.exportPdfBytes({ ...pdfFonts, signal: during.signal, onProgress: () => during.abort('stop') }),
      (error: unknown) => cancelled(error) && (error as IdmlError).cause === 'stop',
    );
  });

  test('import from bytes and from a file, with progress and cancellation', () => {
    using doc = idml.create(SPEC);
    const pdfPath = join(temp, 'source.pdf');
    doc.exportPdf(pdfPath, pdfFonts);
    const { pdf } = doc.exportPdfBytes(pdfFonts);

    const fromBytes = idml.importPdf(pdf, { imageDirectory: join(temp, 'links-bytes'), fontDirectories: pdfFonts.fontDirectories });
    using imported = fromBytes.document;
    assert.equal(imported.summary().pageCount, 2);
    assert.ok(fromBytes.report.textFrames > 0);
    assert.ok(fromBytes.report.fonts.some((font) => font.family === 'Liberation Serif' && font.available === true), JSON.stringify(fromBytes.report.fonts));
    assert.match(JSON.stringify(imported.summary()), /Ficha \{\{codigo\}\}/);

    const calls: number[] = [];
    const fromFile = idml.importPdf(pdfPath, { imageDirectory: join(temp, 'links-file'), onProgress: (done) => void calls.push(done) });
    using fromFileDocument = fromFile.document;
    assert.deepEqual(calls, [1, 2]);
    assert.equal(fromFileDocument.summary().pageCount, 2);

    const onePage = idml.importPdf(pdf.slice().buffer as ArrayBuffer, { pages: [2] });
    using onePageDocument = onePage.document;
    assert.equal(onePageDocument.summary().pageCount, 1);

    assert.throws(() => idml.importPdf(pdf, { onProgress: () => false }), cancelled);
    assert.throws(() => idml.importPdf(join(temp, 'missing.pdf')), (error: unknown) => error instanceof IdmlError && error.code === Status.io);
    assert.throws(() => idml.importPdf(new Uint8Array([1, 2, 3])), IdmlError);
    assert.throws(() => idml.importPdf(new Uint8Array(0)), (error: unknown) => error instanceof IdmlError && error.code === Status.invalidArgument);
    assert.throws(() => idml.importPdf(42 as unknown as string), (error: unknown) => error instanceof IdmlError && error.code === Status.invalidArgument);
  });


  test('bytes that are not a PDF are an invalid format', () => {
    assert.throws(() => idml.importPdf(new Uint8Array([1, 2, 3])), (error: unknown) => error instanceof IdmlError && error.code === Status.invalidFormat);
  });

  test('a cancelled export to a file leaves no partial file', () => {
    using doc = idml.create(SPEC);
    const path = join(temp, 'partial.pdf');
    assert.throws(() => doc.exportPdf(path, { ...pdfFonts, onProgress: () => false }), cancelled);
    assert.equal(existsSync(path), false);
  });

  test('pages that do not exist are reported as a warning', () => {
    using doc = idml.create(SPEC);
    const { report } = doc.exportPdfBytes({ ...pdfFonts, pages: [1, 9] });
    assert.equal(report.pageCount, 1);
    assert.ok(report.warnings.some((warning) => warning.includes('9')), JSON.stringify(report.warnings));
  });
});

describe('asynchronous (worker thread)', () => {
  test('export to bytes and to a file, import from bytes and from a file', async () => {
    using doc = idml.create(SPEC);
    const calls: Array<[number, number]> = [];
    const { pdf, report } = await doc.exportPdfBytesAsync({ ...pdfFonts, onProgress: (done, total) => void calls.push([done, total]) });
    assert.ok(isPdf(pdf));
    assert.ok(pdf instanceof Uint8Array);
    assert.equal(report.pageCount, 2);
    assert.deepEqual(calls, [
      [1, 2],
      [2, 2],
    ]);
    assert.equal(pdf.length, doc.exportPdfBytes(pdfFonts).pdf.length);

    const path = join(temp, 'async.pdf');
    assert.equal((await doc.exportPdfAsync(path, pdfFonts)).pageCount, 2);
    assert.ok(isPdf(new Uint8Array(readFileSync(path))));

    const fromBytes = await idml.importPdfAsync(pdf, { imageDirectory: join(temp, 'async-links') });
    using imported = fromBytes.document;
    assert.equal(imported.summary().pageCount, 2);
    assert.ok(fromBytes.report.textFrames > 0);

    const progress: number[] = [];
    const fromFile = await idml.importPdfAsync(pathToFileURL(path), { imageDirectory: join(temp, 'async-links-2'), onProgress: (done) => void progress.push(done) });
    using fromFileDocument = fromFile.document;
    assert.deepEqual(progress, [1, 2]);
    assert.equal(fromFileDocument.summary().pageCount, 2);
    // The imported document can be exported again on another worker.
    assert.equal((await fromFileDocument.exportPdfBytesAsync(pdfFonts)).report.pageCount, 2);
  });

  test('errors come back with the native message and code', async () => {
    using doc = idml.create(SPEC);
    await assert.rejects(idml.importPdfAsync(join(temp, 'missing.pdf')), (error: unknown) => {
      assert.ok(error instanceof IdmlError);
      assert.equal(error.code, Status.io);
      assert.match(error.message, /missing\.pdf/);
      return true;
    });
    await assert.rejects(idml.importPdfAsync(new Uint8Array(0)), (error: unknown) => error instanceof IdmlError && error.code === Status.invalidArgument);
    await assert.rejects(doc.exportPdfAsync(join(temp, 'no-such-folder', 'x', 'y.pdf'), pdfFonts), IdmlError);
    // A closed document is refused before a worker starts.
    const closed = idml.create();
    closed.close();
    await assert.rejects(closed.exportPdfBytesAsync(), (error: unknown) => error instanceof IdmlError && error.code === Status.invalidHandle);
  });

  test('a document closed while its export runs: the export finishes, later calls get invalidHandle', async () => {
    const doc = idml.create(manyPagesSpec(4, 2));
    const pending = doc.exportPdfBytesAsync(pdfFonts);
    doc.close();
    const outcome = await pending.then(
      (result) => result.report.pageCount,
      (error: unknown) => (error as IdmlError).code,
    );
    // Either the worker's call started before the close (and finished), or it found the handle closed.
    assert.ok(outcome === 4 || outcome === Status.invalidHandle, String(outcome));
    assert.throws(() => doc.summary(), (error: unknown) => error instanceof IdmlError && error.code === Status.invalidHandle);
  });

  test('progress returning false cancels at the next page', async () => {
    using doc = idml.create(manyPagesSpec(12, 1));
    const seen: number[] = [];
    await assert.rejects(
      doc.exportPdfBytesAsync({
        ...pdfFonts,
        onProgress: (done) => {
          seen.push(done);
          return false;
        },
      }),
      cancelled,
    );
    // The worker never waits for this thread, so it may finish another page before it sees the flag; never all.
    assert.ok(seen.length >= 1 && seen.length < 12, `progress calls: ${seen.join(',')}`);
    assert.equal(seen[0], 1);
  });

  test('an exception in progress cancels and becomes the cause', async () => {
    using doc = idml.create(manyPagesSpec(12, 1));
    const failure = new Error('progress failed');
    await assert.rejects(
      doc.exportPdfBytesAsync({
        ...pdfFonts,
        onProgress: () => {
          throw failure;
        },
      }),
      (error: unknown) => cancelled(error) && (error as IdmlError).cause === failure,
    );
  });

  test('an exception in progress is not lost when the worker finished first, and nothing is left behind', async () => {
    // One page: the worker posts its only progress message and the result before this thread reads either.
    using doc = idml.create(manyPagesSpec(1, 1));
    const failure = new Error('progress failed');
    const onProgress = (): never => {
      throw failure;
    };
    const isCause = (error: unknown): boolean => cancelled(error) && (error as IdmlError).cause === failure;
    await assert.rejects(doc.exportPdfBytesAsync({ ...pdfFonts, onProgress }), isCause);
    const path = join(temp, 'finished-then-cancelled.pdf');
    await assert.rejects(doc.exportPdfAsync(path, { ...pdfFonts, onProgress }), isCause);
    assert.equal(existsSync(path), false);
    const pdf = doc.exportPdfBytes(pdfFonts).pdf;
    await assert.rejects(idml.importPdfAsync(pdf, { onProgress }), isCause);
  });

  test('an AbortSignal cancels: before the start, from progress, from a timer, and with AbortSignal.timeout', async () => {
    using doc = idml.create(manyPagesSpec(30));
    const before = new AbortController();
    before.abort();
    await assert.rejects(doc.exportPdfBytesAsync({ signal: before.signal }), cancelled);
    await assert.rejects(idml.importPdfAsync(new Uint8Array(10), { signal: before.signal }), cancelled);

    const fromProgress = new AbortController();
    await assert.rejects(
      doc.exportPdfBytesAsync({ ...pdfFonts, signal: fromProgress.signal, onProgress: (done) => void (done === 2 && fromProgress.abort(new Error('enough'))) }),
      (error: unknown) => cancelled(error) && ((error as IdmlError).cause as Error).message === 'enough',
    );

    // Without onProgress: the signal alone is watched by the worker's callback.
    const fromTimer = new AbortController();
    const started = performance.now();
    const pending = doc.exportPdfAsync(join(temp, 'aborted.pdf'), { ...pdfFonts, signal: fromTimer.signal });
    setTimeout(() => fromTimer.abort(), 60);
    await assert.rejects(pending, cancelled);
    const elapsed = performance.now() - started;
    const full = timed(() => doc.exportPdfBytes(pdfFonts));
    assert.ok(elapsed < full + 1000, `aborted after ${elapsed.toFixed(0)} ms; a full export takes ${full.toFixed(0)} ms`);

    await assert.rejects(doc.exportPdfBytesAsync({ ...pdfFonts, signal: AbortSignal.timeout(40) }), (error: unknown) => {
      assert.ok(cancelled(error));
      assert.equal(((error as IdmlError).cause as DOMException).name, 'TimeoutError');
      return true;
    });

    // Cancelled calls leave the document usable.
    assert.equal((await doc.exportPdfBytesAsync(pdfFonts)).report.pageCount, 30);
  });

  test('does not block the event loop: a timer keeps ticking during the export', async () => {
    using doc = idml.create(manyPagesSpec(120));
    const blocking = timed(() => doc.exportPdfBytes(pdfFonts));
    assert.ok(blocking > 150, `the synchronous export should take a while (took ${blocking.toFixed(0)} ms)`);

    const ticks: number[] = [];
    const timer = setInterval(() => ticks.push(performance.now()), 5);
    const started = performance.now();
    const progress: number[] = [];
    const { report } = await doc.exportPdfBytesAsync({ ...pdfFonts, onProgress: () => void progress.push(performance.now()) });
    const finished = performance.now();
    clearInterval(timer);

    assert.equal(report.pageCount, 120);
    const during = ticks.filter((time) => time >= started && time <= finished);
    const gaps = during.slice(1).map((time, index) => time - during[index]!);
    const longest = Math.max(...gaps);
    // Had the export run on this thread, no tick would land inside it: one gap as long as the whole export.
    assert.ok(during.length >= 10, `${during.length} ticks in ${(finished - started).toFixed(0)} ms`);
    assert.ok(longest < blocking / 2, `longest gap ${longest.toFixed(0)} ms; the export blocks for ${blocking.toFixed(0)} ms`);
    // Progress arrived while the export ran, not all at the end.
    assert.equal(progress.length, 120);
    assert.ok(progress[0]! < finished - 5, 'first progress before the end');
  });

  test('several exports run at once on separate workers', async () => {
    using first = idml.create(manyPagesSpec(8, 2));
    using second = idml.create(SPEC);
    const [a, b, c] = await Promise.all([first.exportPdfBytesAsync(pdfFonts), second.exportPdfBytesAsync(pdfFonts), first.exportPdfBytesAsync(pdfFonts)]);
    assert.equal(a.report.pageCount, 8);
    assert.equal(b.report.pageCount, 2);
    assert.equal(c.report.pageCount, 8);
    assert.equal(a.pdf.length, c.pdf.length);
  });

  test('a document is kept alive while its async call runs', async () => {
    const pending = idml.create(manyPagesSpec(6, 2)).exportPdfBytesAsync(pdfFonts);
    (globalThis as { gc?: () => void }).gc?.();
    await delay(0);
    assert.equal((await pending).report.pageCount, 6);
  });
});
