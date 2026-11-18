// Shared by the Node tests: where the build and the fixtures are, and a PNG made on the spot (the fixtures hold no
// image, and PDF import needs one to extract).
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { crc32, deflateSync } from 'node:zlib';

import { CoreIdml } from '../src/index.ts';

const root = new URL('../../../', import.meta.url);
export const frameworkUrl = new URL('artifacts/wasm/_framework/', root);

export async function loadEngine(): Promise<CoreIdml> {
  assert.ok(existsSync(fileURLToPath(frameworkUrl)), `No WebAssembly build at ${frameworkUrl}. Run: npm run build:wasm`);
  return CoreIdml.load({ frameworkUrl });
}

export function fixture(name: string): Uint8Array {
  return new Uint8Array(readFileSync(new URL(`tests/fixtures/${name}`, root)));
}

export function fixtureText(name: string): string {
  return readFileSync(new URL(`tests/fixtures/${name}`, root), 'utf8');
}

/** The Liberation fonts of tests/fixtures/fonts, by file name. */
export function fixtureFonts(): Record<string, Uint8Array> {
  const folder = new URL('tests/fixtures/fonts/', root);
  return Object.fromEntries(
    readdirSync(folder)
      .filter((name) => name.endsWith('.ttf'))
      .map((name) => [name, new Uint8Array(readFileSync(new URL(name, folder)))]),
  );
}

/** An RGB PNG of `width` x `height` pixels: a gradient, so it does not compress to nothing. */
export function png(width: number, height: number): Uint8Array {
  const raw = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++) {
    const row = y * (width * 3 + 1);
    for (let x = 0; x < width; x++) {
      raw[row + 1 + x * 3] = (x * 255) / width;
      raw[row + 2 + x * 3] = (y * 255) / height;
      raw[row + 3 + x * 3] = 128;
    }
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8); // 8 bits, RGB, deflate, no filter method, not interlaced
  const chunk = (type: string, data: Buffer) => {
    const typed = Buffer.concat([Buffer.from(type, 'latin1'), data]);
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(typed));
    return Buffer.concat([length, typed, crc]);
  };
  return new Uint8Array(
    Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', header), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]),
  );
}

/** Whether the bytes start with `text`, read one byte per character. */
export function startsWith(bytes: Uint8Array, text: string): boolean {
  return bytes.length >= text.length && [...text].every((char, index) => bytes[index] === char.charCodeAt(0));
}
