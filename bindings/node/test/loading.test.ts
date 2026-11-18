// Loading the native library, as an application would: each case runs in a fresh Node process, since a process
// loads the library once.
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { after, describe, test } from 'node:test';
import { pathToFileURL } from 'node:url';

import { buildHint, dotnetRid, libraryFileName } from '../src/library.ts';
import { packageRoot, repositoryRoot, tempDirectory } from './helpers.ts';

const temp = tempDirectory();
const builtLibrary = join(repositoryRoot, 'artifacts', 'native', dotnetRid() ?? 'none', libraryFileName());
const sourceIndex = pathToFileURL(join(packageRoot, 'src', 'index.ts')).href;

/** Runs an ES module snippet in a fresh Node process with `env` added; returns what it printed. */
function runNode(code: string, env: Record<string, string | undefined> = {}, cwd = packageRoot): { status: number | null; out: string; err: string } {
  const result = spawnSync(process.execPath, ['--input-type=module', '-e', code], {
    env: { ...process.env, COREIDML_LIBRARY: undefined, ...env },
    encoding: 'utf8',
    cwd,
  });
  return { status: result.status, out: result.stdout, err: result.stderr };
}

const loadAndReport = (index: string): string => `
import { CoreIdml } from ${JSON.stringify(index)};
try { const idml = CoreIdml.load(); console.log(JSON.stringify({ path: idml.libraryPath, version: idml.version })); }
catch (error) { console.log(JSON.stringify({ name: error.name, code: error.code, message: error.message })); }
`;

function load(env: Record<string, string | undefined> = {}): { path?: string; version?: string; name?: string; code?: number; message?: string } {
  const result = runNode(loadAndReport(sourceIndex), env);
  assert.equal(result.status, 0, result.err);
  return JSON.parse(result.out);
}

describe('load errors', () => {
  test('COREIDML_LIBRARY pointing at a missing file names the variable and the path', () => {
    const missing = join(temp, 'nowhere', libraryFileName());
    const result = load({ COREIDML_LIBRARY: missing });
    assert.equal(result.name, 'IdmlError');
    assert.equal(result.code, -1);
    assert.ok(result.message!.includes(`the COREIDML_LIBRARY environment variable: ${missing} did not load`), result.message);
    assert.ok(result.message!.includes(buildHint()), result.message);
  });

  test('a file that is not a library is refused clearly', () => {
    const fake = join(temp, libraryFileName());
    writeFileSync(fake, 'not a library');
    const result = load({ COREIDML_LIBRARY: fake });
    assert.equal(result.name, 'IdmlError');
    assert.ok(result.message!.includes(fake), result.message);
    assert.match(result.message!, /did not load/);
  });

  // A system library that every machine has.
  const other = { linux: 'libc.so.6', win32: 'kernel32.dll', darwin: '/usr/lib/libSystem.B.dylib' }[process.platform as string];

  test('a library without the CoreIdml functions is refused clearly', { skip: !other && 'no known system library here' }, () => {
    const result = load({ COREIDML_LIBRARY: other });
    assert.equal(result.name, 'IdmlError');
    assert.match(result.message!, /has no coreidml_abi_version: it is not the CoreIdml native library/);
  });

  test('COREIDML_LIBRARY loads the real library', { skip: !existsSync(builtLibrary) && `no build at ${builtLibrary}` }, () => {
    const result = load({ COREIDML_LIBRARY: builtLibrary });
    assert.equal(result.path, builtLibrary);
    assert.match(result.version!, /^\d+\.\d+\.\d+/);
  });

  test('without any setting, the local build of this checkout is found', { skip: !existsSync(builtLibrary) && `no build at ${builtLibrary}` }, () => {
    assert.equal(load().path, builtLibrary);
  });
});

describe('the built package', { skip: !existsSync(builtLibrary) && `no build at ${builtLibrary}` }, () => {
  // Compiled like `npm run build`, into a folder of its own so that dist/ is left alone.
  const out = mkdtempSync(join(packageRoot, 'node_modules', '.coreidml-test-dist-'));
  after(() => rmSync(out, { recursive: true, force: true }));
  execFileSync(process.execPath, [join(packageRoot, 'node_modules', 'typescript', 'bin', 'tsc'), '-p', join(packageRoot, 'tsconfig.json'), '--outDir', out], { stdio: 'pipe' });
  const builtIndex = pathToFileURL(join(out, 'index.js')).href;

  test('has the worker entry point, and the async calls use it', () => {
    assert.ok(existsSync(join(out, 'worker.js')));
    assert.ok(existsSync(join(out, 'index.d.ts')));
    assert.ok(existsSync(join(out, 'types.d.ts')));
    const code = `
import { CoreIdml } from ${JSON.stringify(builtIndex)};
const idml = CoreIdml.load();
using doc = idml.create();
const { pdf, report } = await doc.exportPdfBytesAsync({ onProgress: () => {} });
const { document } = await idml.importPdfAsync(pdf);
console.log(JSON.stringify({ pages: report.pageCount, imported: document.summary().pageCount }));
document.close();
`;
    const result = runNode(code, { COREIDML_LIBRARY: builtLibrary });
    assert.equal(result.status, 0, result.err);
    assert.deepEqual(JSON.parse(result.out), { pages: 1, imported: 1 });
  });

  test('require() loads it (require(esm)): the package has no top-level await', { skip: !(process.features as { require_module?: boolean }).require_module && 'this Node has no require(esm)' }, () => {
    const code = `const { CoreIdml, IdmlDocument, IdmlError, Status } = require(${JSON.stringify(join(out, 'index.js'))}); console.log(typeof CoreIdml.load, typeof IdmlDocument, typeof IdmlError, Status.cancelled);`;
    const result = spawnSync(process.execPath, ['-e', code], { encoding: 'utf8', cwd: packageRoot });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout.trim(), 'function function function -4');
  });
});
