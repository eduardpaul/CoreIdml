import assert from 'node:assert/strict';
import { before, test } from 'node:test';

import { CoreIdml, Status } from '../src/index.ts';
import { loadEngine } from './helpers.ts';

// The engine's in-memory file system: where fonts, placed images and PDF-import images live in WebAssembly.
let idml: CoreIdml;

before(async () => {
  idml = await loadEngine();
});

test('files are written with their folders, read back and listed', () => {
  const bytes = new Uint8Array([0, 1, 2, 254, 255]);

  idml.writeFile('/work/a/b/data.bin', bytes);
  idml.writeFile('/work/a/text.txt', 'héllo');
  idml.writeFile('/work/a/buffer.bin', bytes.buffer.slice(1, 3));

  assert.deepEqual(idml.readFile('/work/a/b/data.bin'), bytes);
  assert.equal(new TextDecoder().decode(idml.readFile('/work/a/text.txt')), 'héllo');
  assert.deepEqual(idml.readFile('/work/a/buffer.bin'), new Uint8Array([1, 2]));
  assert.deepEqual(idml.listFiles('/work/a'), ['b', 'buffer.bin', 'text.txt']);
  assert.deepEqual(idml.listFiles('/work/nothing'), []);
});

test('remove deletes a file or a whole folder, and a missing path is not an error', () => {
  idml.writeFile('/gone/x/one.txt', '1');
  idml.writeFile('/gone/two.txt', '2');

  idml.remove('/gone/two.txt');
  assert.deepEqual(idml.listFiles('/gone'), ['x']);
  idml.remove('/gone');
  assert.deepEqual(idml.listFiles('/gone'), []);
  idml.remove('/gone');
});

test('a file that is not there, or a bad argument, is an IdmlError', () => {
  assert.throws(() => idml.readFile('/nope/file.bin'), { name: 'IdmlError', code: Status.io });
  assert.throws(() => idml.readFile(''), { name: 'IdmlError', code: Status.invalidArgument });
  assert.throws(() => idml.writeFile(42 as never, 'x'), { name: 'IdmlError', code: Status.invalidArgument });
  assert.throws(() => idml.writeFile('/x.bin', 42 as never), { name: 'IdmlError', code: Status.invalidArgument });
  idml.writeFile('/folder/file.txt', 'x');
  assert.throws(() => idml.readFile('/folder'), { name: 'IdmlError' });
});
