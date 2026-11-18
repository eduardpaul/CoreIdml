// The studio sample (an InDesign export with large placed images): FICHA_CORESCEM_AP-ML.idml and .pdf in the folder
// named by COREIDML_SAMPLES_DIR, or in real_samples_fichas/ at the repository root. It is never committed, so these
// tests skip when it is missing.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, test } from 'node:test';

import { CoreIdml } from '../src/index.ts';
import { fontDirectory, sampleIdml, samplePdf, samplesDirectory, samplesPresent, tempDirectory } from './helpers.ts';

const skip = !samplesPresent && `the real sample is not in ${samplesDirectory} (set COREIDML_SAMPLES_DIR)`;

describe('studio sample', { skip }, () => {
  const idml = CoreIdml.load();
  const temp = samplesPresent ? tempDirectory() : '';

  test('summary, validation and a bytes round trip', () => {
    using doc = idml.open(sampleIdml);
    const summary = doc.summary({ maxTextLength: 40 });
    assert.ok(summary.pageCount > 0);
    assert.ok(summary.links!.length > 0);
    using copy = idml.open(doc.save());
    assert.equal(doc.diff(copy).identical, true);
    assert.equal(doc.validate().isValid, true);
  });

  test('async export with placed images, progress and a free event loop', async () => {
    using doc = idml.open(sampleIdml);
    const pages = doc.summary({ sections: ['pages'], maxTextLength: 0 }).pageCount;
    let ticks = 0;
    const timer = setInterval(() => ticks++, 10);
    const progress: number[] = [];
    const { pdf, report } = await doc.exportPdfBytesAsync({
      documentPath: sampleIdml,
      linkDirectories: [join(samplesDirectory, 'Links')],
      fontDirectories: [fontDirectory],
      onProgress: (done) => void progress.push(done),
    });
    clearInterval(timer);
    assert.equal(Buffer.from(pdf.subarray(0, 5)).toString('latin1'), '%PDF-');
    assert.equal(report.pageCount, pages);
    assert.equal(progress.length, pages);
    assert.deepEqual(report.missingImages, []);
    assert.ok(ticks > 3, `${ticks} timer ticks during the export`);
  });

  test('import of the sample PDF from bytes on a worker', async () => {
    const { document, report } = await idml.importPdfAsync(new Uint8Array(readFileSync(samplePdf)), {
      imageDirectory: join(temp, 'links'),
      fontDirectories: [fontDirectory],
    });
    using imported = document;
    assert.ok(imported.summary({ sections: ['pages'], maxTextLength: 0 }).pageCount > 0);
    assert.ok(report.textFrames > 0);
  });
});
