// Fills the plugin's data sheet template with the published @coreidml/wasm package, in a Web Worker, and checks the
// result. Served by serve.mjs, which serves the repository so the page can fetch the template and the fonts.
import { IdmlWorker } from './node_modules/@coreidml/wasm/dist/worker.js';

const log = document.getElementById('log');
const failures = [];
const lines = [];
const print = (line) => {
  lines.push(line);
  log.textContent = lines.join('\n');
};
const check = (condition, what) => {
  print(`${condition ? 'ok   ' : 'FAIL '} ${what}`);
  if (!condition) failures.push(what);
};
const fetchBytes = async (path) => {
  const response = await fetch(path);
  if (!response.ok) throw new Error(`${path}: HTTP ${response.status}`);
  return new Uint8Array(await response.arrayBuffer());
};
const link = (bytes, name, type) => {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([bytes], { type }));
  a.download = name;
  a.textContent = `Download ${name}`;
  document.getElementById('links').append(a);
};

async function run() {
  const samples = '/plugins/coreidml/samples';
  const started = performance.now();
  const idml = await IdmlWorker.start();
  print(`@coreidml/wasm ${idml.version ?? ''} started in ${Math.round(performance.now() - started)} ms`);

  // A browser has no system fonts: the page brings its own (Liberation Sans has Arial's metrics).
  const fonts = '/tests/fixtures/fonts';
  await idml.addFonts({
    'LiberationSans-Regular.ttf': await fetchBytes(`${fonts}/LiberationSans-Regular.ttf`),
    'LiberationSans-Bold.ttf': await fetchBytes(`${fonts}/LiberationSans-Bold.ttf`),
  });
  const data = await (await fetch(`${samples}/datasheet-data.json`)).json();

  // Fill the template, validate it and save it.
  const template = await idml.open(await fetchBytes(`${samples}/datasheet-template.idml`));
  const filled = await template.fill(data);
  check(Object.keys(filled.filled).sort().join() === Object.keys(data.text).sort().join() && filled.missing.length === 0, `fill: ${JSON.stringify(filled)}`);
  check((await template.validate()).isValid, 'the filled document is valid');
  const saved = await template.save();
  await template.close();
  link(saved, 'datasheet.idml', 'application/octet-stream');

  // Open the saved bytes again: the data is in them. The PDF renders in the worker, with progress on the page.
  const doc = await idml.open(saved);
  const texts = ((await doc.summary()).stories ?? []).map((story) => story.text ?? '');
  check(texts.some((text) => text.includes('Trail Lamp X200')), 'the saved file has the product name');
  check(texts.some((text) => text.includes('1 March 2027')), 'and the {{date}} placeholder filled');
  const progress = document.getElementById('progress');
  const { pdf, report } = await doc.exportPdfBytes({
    substitutions: { Arial: 'Liberation Sans' },
    onProgress: (done, total) => (progress.value = done / total),
  });
  await doc.close();
  check(report.pageCount === 1 && report.oversetStories.length === 0, `PDF: ${report.pageCount} page, nothing overset`);
  check(new TextDecoder().decode(pdf.subarray(0, 5)) === '%PDF-', `datasheet.pdf is a PDF (${pdf.length} bytes)`);
  link(pdf, 'datasheet.pdf', 'application/pdf');

  // And the PDF back into an editable document.
  const { document: imported, report: importReport } = await idml.importPdf(pdf);
  check((await imported.summary()).pageCount === 1, `PDF import: ${importReport.textFrames} text frames`);
  await imported.close();
  idml.terminate();
}

try {
  await run();
} catch (error) {
  failures.push(String(error));
  print(`FAIL  ${error?.stack ?? error}`);
}
print(failures.length === 0 ? 'all checks passed' : `${failures.length} checks failed`);
// serve.mjs --check waits for this.
if (new URLSearchParams(location.search).has('check')) {
  await fetch('/results', { method: 'POST', body: JSON.stringify({ lines, failures, userAgent: navigator.userAgent }) });
}
