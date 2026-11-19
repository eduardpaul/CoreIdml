// Worker thread of the async PDF calls: kept in a small pool by background.ts, it binds the library once and then
// makes one blocking call into it per job it receives, posting the result of each.
//
// Everything that belongs to a call happens on this thread: the call itself, the progress callback (the library
// calls it on the calling thread, so koffi runs it here directly instead of queuing it to another event loop),
// reading the error message (kept per thread by the library) and releasing the returned strings and buffers.

import { parentPort } from 'node:worker_threads';

import { Status } from './abi.generated.ts';
import { exportPdfBytes, exportPdfFile, importPdf, type NativeProgress } from './calls.ts';
import { IdmlError, bindLibrary, messageOf, type Native } from './native.ts';
import type { WorkerJob, WorkerMessage } from './protocol.ts';

const port = parentPort;
if (port === null) {
  throw new Error('worker.js runs as a worker thread of @coreidml/node.');
}

function post(message: WorkerMessage, transfer: ArrayBuffer[] = []): void {
  port!.postMessage(message, transfer);
}

// The library is already loaded by the process; binding its functions for this thread is done once, for every job.
let bound: { path: string; native: Native } | undefined;

function run(job: WorkerJob): void {
  try {
    if (bound?.path !== job.libraryPath) {
      bound = { path: job.libraryPath, native: bindLibrary(job.libraryPath, job.librarySource) };
    }
    const native = bound.native;
    const cancel = new Int32Array(job.cancel);
    let progress: NativeProgress | null = null;
    if (job.reportProgress || job.cancellable) {
      // Never waits for the main thread: progress is posted, cancellation is a shared flag. Waiting here could
      // deadlock when the main thread makes a synchronous call on the same document (the library holds its lock).
      progress = (done, total) => {
        try {
          if (job.reportProgress) {
            post({ type: 'progress', done, total });
          }
          return Atomics.load(cancel, 0) === 0 ? 0 : 1;
        } catch {
          return 1;
        }
      };
    }
    const operation = job.operation;
    switch (operation.kind) {
      case 'exportPdfFile':
        post({ type: 'result', report: exportPdfFile(native, operation.handle, operation.path, operation.optionsJson, progress) });
        break;
      case 'exportPdfBytes': {
        const { pdf, report } = exportPdfBytes(native, operation.handle, operation.optionsJson, progress);
        post({ type: 'result', report, pdf }, [pdf.buffer as ArrayBuffer]);
        break;
      }
      case 'importPdf': {
        const { handle, report } = importPdf(native, operation.source, operation.optionsJson, progress);
        post({ type: 'result', handle, report });
        break;
      }
    }
  } catch (error) {
    post({
      type: 'error',
      code: error instanceof IdmlError ? error.code : Status.failed,
      message: messageOf(error),
      idml: error instanceof IdmlError,
    });
  }
}

port.on('message', (job: WorkerJob) => run(job));
