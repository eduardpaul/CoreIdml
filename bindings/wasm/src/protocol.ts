// Messages between the page (worker.ts, IdmlWorker) and the Web Worker that runs the engine (worker-thread.ts).

import type { DocumentSpec } from './types.ts';

/** The document methods the page may call by name; the others have operations of their own. */
export const documentMethods = [
  'info',
  'readPart',
  'readText',
  'writePart',
  'save',
  'summary',
  'story',
  'xmlStructure',
  'exportXml',
  'validate',
  'apply',
  'fill',
  'replaceText',
  'setRunText',
  'relink',
  'importXml',
] as const;

export type DocumentMethod = (typeof documentMethods)[number];

export type Operation =
  | { op: 'load'; frameworkUrl?: string }
  | { op: 'create'; spec?: DocumentSpec | string }
  | { op: 'open'; bytes: Uint8Array | ArrayBuffer }
  | { op: 'importPdf'; bytes: Uint8Array | ArrayBuffer; options: object }
  | { op: 'call'; handle: number; method: DocumentMethod; args: unknown[] }
  | { op: 'diff'; handle: number; other: number }
  | { op: 'importPages'; handle: number; source: number; pages: number[] }
  | { op: 'exportPdfBytes'; handle: number; options: object }
  | { op: 'close'; handle: number }
  | { op: 'addFonts'; files: Record<string, Uint8Array | ArrayBuffer> }
  | { op: 'addLinks'; files: Record<string, Uint8Array | ArrayBuffer> }
  | { op: 'writeFile'; path: string; data: Uint8Array | ArrayBuffer | string }
  | { op: 'readFile'; path: string }
  | { op: 'listFiles'; directory: string }
  | { op: 'remove'; path: string };

export type Request = Operation & {
  id: number;
  /** Progress is posted to the page when set. */
  progress?: boolean;
  /** An Int32Array over a SharedArrayBuffer (cross-origin isolated pages only): [0] != 0 cancels at the next page. */
  cancel?: Int32Array;
};

export type Response =
  | { id: number; kind: 'result'; value: unknown }
  | { id: number; kind: 'error'; message: string; code: number; cause?: string }
  | { id: number; kind: 'progress'; done: number; total: number };
