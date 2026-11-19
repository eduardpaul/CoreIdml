// Fills the plugin's data sheet template with the published @coreidml/node package from npm, and checks the result.
//
//   npm install && node demo.mjs
//
// Writes out/datasheet.idml and out/datasheet.pdf, and exits with 1 when anything is not as expected.
import { mkdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { CoreIdml } from '@coreidml/node';

const here = dirname(fileURLToPath(import.meta.url));
const samples = join(here, '..', '..', '..', 'plugins', 'coreidml', 'samples');
const out = join(here, 'out');
const failures = [];
const check = (condition, what) => {
  console.log(`${condition ? 'ok   ' : 'FAIL '} ${what}`);
  if (!condition) failures.push(what);
};

mkdirSync(out, { recursive: true });
const idml = await CoreIdml.load();
console.log('@coreidml/node', idml.version);
const data = JSON.parse(readFileSync(join(samples, 'datasheet-data.json'), 'utf8'));

// Fill the template, validate it and save it as a new file.
{
  using doc = idml.open(join(samples, 'datasheet-template.idml'));
  const filled = doc.fill(data);
  check(Object.keys(filled.filled).sort().join() === Object.keys(data.text).sort().join() && filled.missing.length === 0, `fill: ${JSON.stringify(filled)}`);
  check(doc.validate().isValid, 'the filled document is valid');
  doc.saveFile(join(out, 'datasheet.idml'));
}

// Open the saved file again: the data is in it. The PDF is rendered on a worker thread.
let pdf;
{
  using doc = idml.open(join(out, 'datasheet.idml'));
  const texts = (doc.summary().stories ?? []).map((story) => story.text ?? '');
  check(texts.some((text) => text.includes('Trail Lamp X200')), 'the saved file has the product name');
  check(texts.some((text) => text.includes('1 March 2027')), 'and the {{date}} placeholder filled');
  const progress = [];
  const report = await doc.exportPdfAsync(join(out, 'datasheet.pdf'), { onProgress: (done, total) => void progress.push(`${done}/${total}`) });
  check(report.pageCount === 1 && report.oversetStories.length === 0, `PDF: ${report.pageCount} page, nothing overset, progress ${progress.join(' ')}`);
  pdf = readFileSync(join(out, 'datasheet.pdf'));
  check(pdf.subarray(0, 5).toString('latin1') === '%PDF-', `datasheet.pdf is a PDF (${pdf.length} bytes)`);
}

// And the PDF back into an editable document.
{
  const { document, report } = await idml.importPdfAsync(pdf);
  using imported = document;
  check(imported.summary().pageCount === 1, `PDF import: ${report.textFrames} text frames`);
}

console.log(failures.length === 0 ? 'all checks passed' : `${failures.length} checks failed`);
process.exit(failures.length === 0 ? 0 : 1);
