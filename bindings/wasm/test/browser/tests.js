// The scenarios of test/document.test.ts, in a real browser, against the compiled package (dist/), which loads the
// WebAssembly build from dist/_framework over HTTP as an application would. Results go to the page and are posted
// to the server that scripts/browser-test.mjs runs.
import { ABI_VERSION, CoreIdml, IdmlError, Status } from '/dist/index.js';

const story = 'Stories/Story_u188.xml';
const results = [];

function assert(condition, message) {
  if (!condition) {
    throw new Error(`assertion failed: ${message}`);
  }
}

function sameBytes(a, b) {
  return a.length === b.length && a.every((value, index) => value === b[index]);
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

async function test(name, body) {
  try {
    await body();
    results.push({ name, ok: true });
  } catch (error) {
    results.push({ name, ok: false, error: String(error?.stack ?? error) });
  }
}

let idml;
let fixture;
const started = performance.now();

await test('the build loads over HTTP and reports its version', async () => {
  fixture = new Uint8Array(await (await fetch('/fixtures/article-1photo.idml')).arrayBuffer());
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

const report = { userAgent: navigator.userAgent, loadMilliseconds, results };
document.getElementById('log').textContent = results.map((r) => `${r.ok ? 'ok    ' : 'FAILED'} ${r.name}${r.ok ? '' : `\n${r.error}`}`).join('\n');
await fetch('/results', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(report) });
