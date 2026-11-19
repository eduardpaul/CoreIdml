// Messages between the main thread and the pooled workers that run the PDF exports and imports (see background.ts).

import type { PdfSource } from './calls.ts';

export type WorkerOperation =
  | { kind: 'exportPdfFile'; handle: number; path: string; optionsJson: string | null }
  | { kind: 'exportPdfBytes'; handle: number; optionsJson: string | null }
  | { kind: 'importPdf'; source: PdfSource; optionsJson: string | null };

export interface WorkerJob {
  libraryPath: string;
  librarySource: string;
  operation: WorkerOperation;
  /** Int32 flag: the main thread stores 1 to cancel; the worker's progress callback reads it after each page. */
  cancel: SharedArrayBuffer;
  /** Post a progress message after each page. */
  reportProgress: boolean;
  /** Pass a progress callback even without progress messages, so that the cancel flag is read. */
  cancellable: boolean;
}

export type WorkerMessage =
  | { type: 'progress'; done: number; total: number }
  | { type: 'result'; report: string; pdf?: Uint8Array; handle?: number }
  | { type: 'error'; code: number; message: string; idml: boolean };
