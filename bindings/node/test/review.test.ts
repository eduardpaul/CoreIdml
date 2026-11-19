import assert from 'node:assert/strict';
import { relative } from 'node:path';
import { test } from 'node:test';

import { idleWorkers } from '../src/background.ts';
import { CoreIdml, Status } from '../src/index.ts';
import { article, pdfFonts } from './helpers.ts';

// Regressions for the defects found by the code review of this binding.
const idml = CoreIdml.load();

test('the loaded library given as a relative path (or in another case on Windows) is the same library', () => {
  assert.equal(CoreIdml.load({ libraryPath: relative(process.cwd(), idml.libraryPath) }), idml);
  if (process.platform === 'win32') {
    assert.equal(CoreIdml.load({ libraryPath: idml.libraryPath.toUpperCase() }), idml);
  }
  assert.throws(() => CoreIdml.load({ libraryPath: `${idml.libraryPath}.other` }), { code: Status.invalidOperation });
});

test('indices outside 32 bits are refused instead of wrapping around', () => {
  using doc = idml.open(article);
  using source = idml.open(article);

  assert.throws(() => doc.importPages(source, [2 ** 32]), { code: Status.invalidArgument });
  assert.throws(() => doc.importPages(source, [-(2 ** 31) - 1]), { code: Status.invalidArgument });
  assert.throws(() => doc.setRunText('u188', 2 ** 31, 'x'), { code: Status.invalidArgument });
  assert.equal(doc.importPages(source, [0]), 1);
});

test('async PDF calls reuse their worker threads', async () => {
  using doc = idml.open(article);

  await doc.exportPdfBytesAsync(pdfFonts);
  const afterFirst = idleWorkers();
  await doc.exportPdfBytesAsync(pdfFonts);
  await Promise.all([doc.exportPdfBytesAsync(pdfFonts), doc.exportPdfBytesAsync(pdfFonts)]);

  assert.equal(afterFirst, 1);
  assert.equal(idleWorkers(), 2);
  // A failure answered by the worker leaves it usable.
  await assert.rejects(doc.exportPdfBytesAsync({ ...pdfFonts, jpegQuality: 'high' as never }), { code: Status.invalidArgument });
  assert.equal(idleWorkers(), 2);
  assert.equal((await doc.exportPdfBytesAsync(pdfFonts)).report.pageCount, 1);
});

test('bytes are taken as a Uint8Array, a Buffer or an ArrayBuffer, and nothing else', async () => {
  const bytes = new Uint8Array(await import('node:fs').then((fs) => fs.readFileSync(article)));
  using fromBytes = idml.open(bytes);
  using fromBuffer = idml.open(Buffer.from(bytes));
  using fromArrayBuffer = idml.open(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer);

  assert.equal(fromBytes.info().stories.length, fromBuffer.info().stories.length);
  assert.equal(fromArrayBuffer.info().stories.length, 3);
  assert.throws(() => idml.open(42 as never), { code: Status.invalidArgument });
  assert.throws(() => idml.importPdf(42 as never), { code: Status.invalidArgument });
  await assert.rejects(idml.importPdfAsync(42 as never), { code: Status.invalidArgument });
});
