import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after } from 'node:test';
import { fileURLToPath } from 'node:url';
import { crc32, deflateSync } from 'node:zlib';

import type { DocumentSpec } from '../src/index.ts';

/** The repository root (bindings/node/test is three levels below it). */
export const repositoryRoot = fileURLToPath(new URL('../../../', import.meta.url));
export const packageRoot = join(repositoryRoot, 'bindings', 'node');

/** Fixtures every binding shares. */
export const fixtures = join(repositoryRoot, 'tests', 'fixtures');
export const article = join(fixtures, 'article-1photo.idml');
export const importXmlTemplate = join(fixtures, 'article-1photo_import-xml.idml');
export const editorial = join(fixtures, 'magazineA-edito.idml');

/** The Liberation fonts (SIL OFL) used wherever PDF output has to be reproducible. */
export const fontDirectory = join(fixtures, 'fonts');

/** PDF options that use only the repository's fonts, so output does not depend on the machine. */
export const pdfFonts = { fontDirectories: [fontDirectory], useSystemFonts: false };

/**
 * A real studio document that is never committed: FICHA_CORESCEM_AP-ML.idml (and its PDF) in the folder named by
 * COREIDML_SAMPLES_DIR, or in real_samples_fichas/ at the repository root. Tests that need it skip without it.
 */
export const samplesDirectory = process.env.COREIDML_SAMPLES_DIR || join(repositoryRoot, 'real_samples_fichas');
export const sampleIdml = join(samplesDirectory, 'FICHA_CORESCEM_AP-ML.idml');
export const samplePdf = join(samplesDirectory, 'FICHA_CORESCEM_AP-ML.pdf');
export const samplesPresent = existsSync(sampleIdml) && existsSync(samplePdf);

/** A temporary folder for one test file, removed when it finishes. */
export function tempDirectory(): string {
  const directory = mkdtempSync(join(tmpdir(), 'coreidml-node-'));
  after(() => rmSync(directory, { recursive: true, force: true }));
  return directory;
}

/** Two pages, a style, a placeholder and a frame name to fill. */
export const SPEC: DocumentSpec = {
  unit: 'mm',
  page: { size: 'A5' },
  paragraphStyles: [{ name: 'Título', font: 'Liberation Serif', size: 18 }],
  pages: [
    {
      items: [
        { type: 'text', name: 'titulo', x: 10, y: 10, width: 128, height: 12, text: 'Ficha {{codigo}}', style: 'Título' },
        { type: 'text', name: 'cliente', x: 10, y: 30, width: 128, height: 12, text: '-' },
      ],
    },
    { items: [{ type: 'text', x: 10, y: 10, width: 128, height: 12, text: 'Segunda página' }] },
  ],
};

/** A document with `pages` pages of justified text in the repository's fonts: slow enough to watch an export run. */
export function manyPagesSpec(pages: number, paragraphs = 6): DocumentSpec {
  const paragraph = 'Ñandú, cigüeña y pingüino comparten párrafo con texto justificado y guiones. '.repeat(14);
  return {
    unit: 'mm',
    paragraphStyles: [{ name: 'Cuerpo', font: 'Liberation Serif', size: 9, align: 'justify' }],
    pages: Array.from({ length: pages }, (_, index) => ({
      items: [
        {
          type: 'text',
          x: 15,
          y: 15,
          width: 180,
          height: 260,
          columns: 2,
          paragraphs: Array.from({ length: paragraphs }, () => ({ style: 'Cuerpo', runs: [{ text: `${index + 1}. ${paragraph}` }] })),
        },
      ],
    })),
  };
}

/** A valid solid-colour RGB PNG. */
export function png(width: number, height: number): Buffer {
  const chunk = (type: string, data: Buffer): Buffer => {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'latin1'), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));
    return Buffer.concat([length, body, crc]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8; // bit depth
  header[9] = 2; // RGB
  const row = Buffer.alloc(1 + width * 3, 0x80);
  row[0] = 0; // no filter
  const pixels = deflateSync(Buffer.concat(Array.from({ length: height }, () => row)));
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', header), chunk('IDAT', pixels), chunk('IEND', Buffer.alloc(0))]);
}

export function timed(action: () => unknown): number {
  const started = performance.now();
  action();
  return performance.now() - started;
}
