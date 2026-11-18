// The FinalizationRegistry safety net: a document that is never closed has its handle closed after garbage
// collection. Runs in a child process with --expose-gc, and checks the handle through the C API itself.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { test } from 'node:test';
import { pathToFileURL } from 'node:url';

import { packageRoot } from './helpers.ts';

test('a forgotten document is closed after garbage collection; a document in use is not', () => {
  const code = `
import koffi from 'koffi';
import { setTimeout as delay } from 'node:timers/promises';
import { CoreIdml } from ${JSON.stringify(pathToFileURL(join(packageRoot, 'src', 'index.ts')).href)};

const idml = CoreIdml.load();
const lib = koffi.load(idml.libraryPath);
const info = lib.func('int32_t coreidml_document_info(intptr_t document, _Out_ void **json)');
const free = lib.func('void coreidml_free(void *pointer)');
const state = (handle) => { const json = [null]; const status = info(handle, json); free(json[0]); return status === 0 ? 'open' : status; };

function forget() { return idml.create().handle; }
const kept = idml.create();
const forgotten = forget();
let forgottenState = state(forgotten);
for (let i = 0; i < 100 && forgottenState === 'open'; i++) { gc(); await delay(10); forgottenState = state(forgotten); }
console.log(JSON.stringify({ forgotten: forgottenState, kept: state(kept.handle) }));
kept.close();
`;
  const result = spawnSync(process.execPath, ['--expose-gc', '--input-type=module', '-e', code], { encoding: 'utf8', cwd: packageRoot });
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), { forgotten: -2, kept: 'open' });
});
