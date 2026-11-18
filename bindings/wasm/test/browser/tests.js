// The scenarios of the Node tests, in a real browser, against the compiled package (dist/), which loads the
// WebAssembly build from dist/_framework over HTTP as an application would: first the synchronous API on the page,
// then IdmlWorker, which runs the engine in a Web Worker. scripts/browser-test.mjs opens this page twice, with and
// without cross-origin isolation, since cancelling works differently in each. Results go to the page and are posted
// to the server that the script runs.
import { ABI_VERSION, CoreIdml, IdmlError, Status } from '../../dist/index.js';
import { IdmlWorker, WorkerDocument } from '../../dist/worker.js';

const story = 'Stories/Story_u188.xml';
const fonts = ['LiberationSerif-Regular.ttf', 'LiberationSerif-Bold.ttf', 'LiberationSerif-Italic.ttf', 'LiberationSans-Regular.ttf'];
const results = [];
const notes = [];

function assert(condition, message) {
  if (!condition) {
    throw new Error(`assertion failed: ${message}`);
  }
}

function sameBytes(a, b) {
  return a.length === b.length && a.every((value, index) => value === b[index]);
}

function startsWith(bytes, text) {
  return [...text].every((char, index) => bytes[index] === char.charCodeAt(0));
}

function throwsWith(code, action) {
  try {
    action();
  } catch (error) {
    assert(error instanceof IdmlError, `an IdmlError was expected, got ${error}`);
    assert(error.code === code, `status ${code} was expected, got ${error.code} (${error.message})`);
    return error;
  }
  throw new Error(`a failure with status ${code} was expected`);
}

async function rejectsWith(code, promise) {
  try {
    await promise;
  } catch (error) {
    assert(error instanceof IdmlError, `an IdmlError was expected, got ${error}`);
    assert(error.code === code, `status ${code} was expected, got ${error.code} (${error.message})`);
    return error;
  }
  throw new Error(`a rejection with status ${code} was expected`);
}

async function test(name, body) {
  try {
    await body();
    results.push({ name, ok: true });
  } catch (error) {
    results.push({ name, ok: false, error: String(error?.stack ?? error) });
  }
}

async function bytesOf(url) {
  const response = await fetch(url);
  assert(response.ok, `${url}: ${response.status}`);
  return new Uint8Array(await response.arrayBuffer());
}

async function picture(width, height) {
  const canvas = new OffscreenCanvas(width, height);
  const context = canvas.getContext('2d');
  const gradient = context.createLinearGradient(0, 0, width, height);
  gradient.addColorStop(0, '#0055aa');
  gradient.addColorStop(1, '#ffcc00');
  context.fillStyle = gradient;
  context.fillRect(0, 0, width, height);
  return new Uint8Array(await (await canvas.convertToBlob({ type: 'image/png' })).arrayBuffer());
}

/** A document of `pages` pages of text; the first has the picture of /links/picture.png. */
function reportSpec(pages) {
  return {
    pages: Array.from({ length: pages }, (_, index) => ({
      items: [
        ...(index === 0 ? [{ type: 'image', name: 'picture', x: 36, y: 36, width: 240, height: 180, path: '/links/picture.png' }] : []),
        { type: 'text', x: 36, y: 240, width: 400, height: 300, text: `Page ${index + 1}. ${'Some text to set. '.repeat(40)}` },
      ],
    })),
  };
}

let idml;
let fixture;
const started = performance.now();

await test('the build loads over HTTP and reports its version', async () => {
  fixture = await bytesOf('../../fixtures/article-1photo.idml');
  idml = await CoreIdml.load();
  assert(/^\d+\.\d+\.\d+/.test(idml.version), `version ${idml.version}`);
  assert(ABI_VERSION === 1, 'ABI version');
  assert((await CoreIdml.load()) === idml, 'load() returns the same instance');
});
const loadMilliseconds = Math.round(performance.now() - started);

await test('a document is opened and described', () => {
  const doc = idml.open(fixture);
  const info = doc.info();
  doc.close();
  assert(info.domVersion === '10.0', `domVersion ${info.domVersion}`);
  assert(info.stories.length === 3 && info.spreads.length === 1 && info.masterSpreads.length === 1, 'manifest');
  assert(info.parts.length === 16 && info.parts[0].path === 'mimetype' && info.parts[0].length === 43, 'parts');
});

await test('a part is read, replaced and saved; the other parts stay byte for byte', () => {
  const doc = idml.open(fixture);
  const original = doc.readText(story);
  const edited = original.replace('<Story ', '<Story Marker="edited" ');
  assert(edited !== original, 'the edit changes the text');
  doc.writePart(story, edited);
  const saved = doc.save();
  const modified = doc.info().parts.filter((part) => part.modified).map((part) => part.path);
  doc.close();
  assert(modified.length === 1 && modified[0] === story, `modified parts: ${modified}`);

  const before = idml.open(fixture);
  const after = idml.open(saved);
  assert(after.readText(story) === edited, 'the edit was saved');
  for (const part of before.info().parts.filter((part) => part.path !== story)) {
    assert(sameBytes(after.readPart(part.path), before.readPart(part.path)), `${part.path} changed`);
  }
  before.close();
  after.close();
});

await test('an ArrayBuffer and binary parts are accepted', () => {
  const doc = idml.open(fixture.buffer.slice(0));
  const bytes = new Uint8Array([0, 1, 2, 0, 255]);
  doc.writePart('Extra/data.bin', bytes);
  assert(sameBytes(doc.readPart('Extra/data.bin'), bytes), 'binary round trip');
  doc.close();
});

await test('failures are IdmlErrors with the status codes of the library', () => {
  const error = throwsWith(Status.invalidFormat, () => idml.open(new TextEncoder().encode('this is not a zip file')));
  assert(/^Not an IDML package: \S+ \S+/.test(error.message), `message: ${error.message}`);
  const doc = idml.open(fixture);
  throwsWith(Status.notFound, () => doc.readPart('Stories/Nope.xml'));
  throwsWith(Status.invalidArgument, () => doc.writePart('bad\\path.xml', 'x'));
  assert(doc.info().parts.length === 16, 'a failure does not poison the document');
  doc.close();
  doc.close();
  throwsWith(Status.invalidHandle, () => doc.info());
});

await test('on the page: a template is filled and rendered with fonts of the in-memory file system', async () => {
  const fontFiles = {};
  for (const name of fonts) {
    fontFiles[name] = await bytesOf(`../../fixtures/fonts/${name}`);
  }
  idml.addFonts(fontFiles);
  const doc = idml.open(fixture);
  const filled = doc.fill({ text: { headline: 'Rendered in the browser' } });
  const calls = [];
  const { pdf, report } = doc.exportPdfBytes({ useSystemFonts: false, onProgress: (done, total) => void calls.push(`${done}/${total}`) });
  doc.close();
  assert(filled.filled.headline === 1, JSON.stringify(filled));
  assert(startsWith(pdf, '%PDF-'), 'a PDF');
  assert(report.pageCount === 1 && calls.join() === '1/1', `${JSON.stringify(report)} ${calls}`);
  assert(report.missingFonts.every((font) => font.usedFont.startsWith('Liberation')), JSON.stringify(report.missingFonts));
});

// ---- IdmlWorker ---------------------------------------------------------------------------------------------------

let worker;
let fontFiles;
let png;

await test('worker: starts and loads the engine in a Web Worker', async () => {
  const begin = performance.now();
  worker = await IdmlWorker.start();
  notes.push(`worker started in ${Math.round(performance.now() - begin)} ms; canCancel=${IdmlWorker.canCancel}; crossOriginIsolated=${globalThis.crossOriginIsolated}`);
  assert(worker.version === idml.version, `version ${worker.version}`);
  assert(!worker.terminated, 'running');
  fontFiles = {};
  for (const name of fonts) {
    fontFiles[name] = await bytesOf(`../../fixtures/fonts/${name}`);
  }
  png = await picture(320, 240);
});

await test('worker: open, describe, edit, fill and save', async () => {
  const doc = await worker.open(fixture);
  assert(doc instanceof WorkerDocument, 'a WorkerDocument');
  const info = await doc.info();
  assert(info.parts.length === 16, 'parts');
  assert((await doc.readText(story)).includes('THE HEADLINE HERE'), 'readText');
  const report = await doc.fill({ text: { headline: 'From the worker', nothing: 'x' } });
  assert(report.filled.headline === 1 && report.missing.join() === 'nothing', JSON.stringify(report));
  assert((await doc.replaceText('Lorem', 'LOREM')) === 2, 'replaceText');
  const runs = (await doc.story('u19f')).runs;
  await doc.setRunText('u19f', runs[0].index, 'First run.');
  assert((await doc.validate()).isValid, 'valid');
  const saved = await doc.save();
  await doc.close();
  await doc.close();
  assert(doc.closed, 'closed');
  await rejectsWith(Status.invalidHandle, doc.summary());

  const reopened = idml.open(saved);
  const summary = reopened.summary({ maxTextLength: -1 });
  reopened.close();
  assert(summary.stories.some((s) => s.text === 'From the worker'), 'the fill was saved');
  assert(summary.stories.some((s) => s.text.startsWith('First run.')), 'the run edit was saved');
});

await test('worker: create, apply, XML, diff and importPages', async () => {
  const doc = await worker.create();
  await doc.apply({ pages: [{ items: [{ type: 'text', name: 'title', x: 36, y: 36, width: 300, height: 40, text: 'Title' }] }] });
  const source = await worker.open(fixture);
  assert((await doc.importPages(source)) === 1, 'importPages');
  assert((await doc.summary({ includeResources: false })).pageCount === 3, 'three pages');
  assert((await source.xmlStructure()).tag === 'Root', 'xmlStructure');
  assert((await source.exportXml({ at: '/Root/module[1]/headline[1]' })) === '<headline>THE HEADLINE HERE</headline>', 'exportXml');
  const template = await worker.open(await bytesOf('../../fixtures/article-1photo_import-xml.idml'));
  const xml = new TextDecoder().decode(await bytesOf('../../fixtures/xml/article-1photo_import-xml.xml'));
  const imported = await template.importXml(xml, { at: '/Root/module[1]' });
  assert(imported.added === 3 && imported.unmatched.length === 0, JSON.stringify(imported));
  const copy = await worker.open(fixture);
  await copy.setRunText('u188', 0, 'Changed');
  const diff = await source.diff(copy);
  assert(!diff.identical && diff.changes[0].after === 'Changed', JSON.stringify(diff));
  await rejectsWith(Status.notFound, source.story('nope'));
  for (const d of [doc, source, template, copy]) {
    await d.close();
  }
});

await test('worker: fonts and links go into its in-memory file system', async () => {
  await worker.addFonts(fontFiles);
  await worker.addLinks({ 'picture.png': png });
  await worker.writeFile('/notes/readme.txt', 'hello');
  assert((await worker.listFiles('/fonts')).join() === [...fonts].sort().join(), 'fonts listed');
  assert((await worker.listFiles('/links')).join() === 'picture.png', 'links listed');
  assert(sameBytes(await worker.readFile('/links/picture.png'), png), 'read back');
  assert(new TextDecoder().decode(await worker.readFile('/notes/readme.txt')) === 'hello', 'text');
  await worker.remove('/notes');
  assert((await worker.listFiles('/notes')).length === 0, 'removed');
  await rejectsWith(Status.io, worker.readFile('/notes/readme.txt'));
});

let exported;

await test('worker: exportPdfBytes reports progress on the page while the worker renders', async () => {
  const doc = await worker.create(reportSpec(6));
  const calls = [];
  let ticks = 0;
  const timer = setInterval(() => ticks++, 1);
  const begin = performance.now();
  exported = await doc.exportPdfBytes({ useSystemFonts: false, onProgress: (done, total) => void calls.push(`${done}/${total}`) });
  const milliseconds = Math.round(performance.now() - begin);
  clearInterval(timer);
  await doc.close();
  notes.push(`worker export of 6 pages: ${milliseconds} ms, ${exported.pdf.length} bytes, ${ticks} page timer ticks meanwhile`);
  assert(startsWith(exported.pdf, '%PDF-'), 'a PDF');
  assert(exported.report.pageCount === 6, JSON.stringify(exported.report));
  assert(exported.report.missingImages.length === 0, 'the picture was found in /links');
  assert(calls.join() === '1/6,2/6,3/6,4/6,5/6,6/6', `progress ${calls}`);
});

await test('worker: importPdf returns the document, its report and the extracted images', async () => {
  const progress = [];
  const { document, report, imageDirectory, images } = await worker.importPdf(exported.pdf, { onProgress: (done) => void progress.push(done) });
  const names = Object.keys(images);
  assert(progress.length === 6, `progress ${progress}`);
  assert(report.imageFrames === 1 && names.length === 1, JSON.stringify(report));
  assert(images[names[0]] instanceof Uint8Array && images[names[0]].length > 100, 'image bytes');
  assert((await worker.listFiles(imageDirectory)).join() === names.join(), 'the images stay in the file system');
  assert(report.fonts.every((font) => font.available === true), JSON.stringify(report.fonts));
  const summary = await document.summary({ maxTextLength: -1 });
  assert(summary.pageCount === 6 && summary.stories.some((s) => s.text.startsWith('Page 1.')), 'rebuilt text');
  const again = await document.exportPdfBytes({ useSystemFonts: false, pages: [1] });
  assert(again.report.pageCount === 1 && again.report.missingImages.length === 0, JSON.stringify(again.report));
  await document.close();
});

await test('worker: failures reject with IdmlErrors and leave the worker running', async () => {
  await rejectsWith(Status.invalidFormat, worker.open(new TextEncoder().encode('not a zip')));
  const doc = await worker.create();
  const other = await IdmlWorker.start();
  const foreign = await other.create();
  try {
    await doc.diff(foreign);
    throw new Error('a document of another worker was accepted');
  } catch (error) {
    assert(error instanceof IdmlError && error.code === Status.invalidArgument, String(error));
  }
  other.terminate();
  await rejectsWith(Status.invalidOperation, other.create());
  assert(!worker.terminated && (await doc.validate()).isValid, 'still running');
  await doc.close();
});

await test('worker: cancelling a PDF export', async () => {
  const doc = await worker.create(reportSpec(60));
  if (IdmlWorker.canCancel) {
    // Cross-origin isolated: the worker stops after the current page and stays usable.
    const controller = new AbortController();
    const calls = [];
    const error = await rejectsWith(
      Status.cancelled,
      doc.exportPdfBytes({ useSystemFonts: false, signal: controller.signal, onProgress: (done) => (calls.push(done), done === 1 && controller.abort()) }),
    );
    notes.push(`isolated cancel: stopped after ${calls.length} of 60 pages (${error.message})`);
    assert(calls.length < 60, `pages rendered after the abort: ${calls.length}`);
    await rejectsWith(Status.cancelled, doc.exportPdfBytes({ useSystemFonts: false, onProgress: () => false }));
    assert(!worker.terminated, 'the worker keeps running');
    assert((await doc.exportPdfBytes({ useSystemFonts: false, pages: [1] })).report.pageCount === 1, 'and the document is usable');
    await doc.close();
  } else {
    // Without isolation a running call cannot be interrupted: aborting terminates the worker.
    const controller = new AbortController();
    const reason = new Error('stop');
    const exporting = doc.exportPdfBytes({ useSystemFonts: false, signal: controller.signal, onProgress: (done) => void (done === 1 && controller.abort(reason)) });
    const other = worker.listFiles('/'); // queued behind the export
    const error = await rejectsWith(Status.cancelled, exporting);
    assert(error.cause === reason, `the abort reason is the cause, got ${error.cause}`);
    assert(worker.terminated, 'the worker was terminated');
    await rejectsWith(Status.invalidOperation, other);
    await rejectsWith(Status.invalidOperation, worker.listFiles('/'));

    // onProgress returning false does the same, and rejects with Status.cancelled.
    const second = await IdmlWorker.start();
    await second.addFonts(fontFiles);
    await second.addLinks({ 'picture.png': png });
    const big = await second.create(reportSpec(60));
    await rejectsWith(Status.cancelled, big.exportPdfBytes({ useSystemFonts: false, onProgress: () => false }));
    assert(second.terminated, 'terminated as well');
  }
  // A signal aborted before the call rejects at once, whatever the isolation.
  const fresh = await IdmlWorker.start();
  const blank = await fresh.create();
  const aborted = AbortSignal.abort(new Error('already'));
  const early = await rejectsWith(Status.cancelled, blank.exportPdfBytes({ signal: aborted }));
  assert(early.cause === aborted.reason, String(early.cause));
  assert(!fresh.terminated, 'nothing was running, nothing was terminated');
  fresh.terminate();
});

const report = { userAgent: navigator.userAgent, isolated: globalThis.crossOriginIsolated === true, loadMilliseconds, notes, results };
document.getElementById('log').textContent = [...notes, ...results.map((r) => `${r.ok ? 'ok    ' : 'FAILED'} ${r.name}${r.ok ? '' : `\n${r.error}`}`)].join('\n');
await fetch('/results', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(report) });
