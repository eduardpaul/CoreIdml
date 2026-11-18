import { extname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Worker } from 'node:worker_threads';

import { Status, type StatusCode } from './abi.generated.ts';
import { IdmlError, type Native } from './native.ts';
import type { WorkerJob, WorkerMessage, WorkerOperation } from './protocol.ts';
import type { ProgressOptions } from './types.ts';

export type WorkerResult = Extract<WorkerMessage, { type: 'result' }>;

// worker.js next to this file once built; worker.ts when the sources run directly (the tests).
const workerUrl = new URL(`./worker${extname(fileURLToPath(import.meta.url))}`, import.meta.url);

/**
 * Starts the worker. It inherits the process's Node options, except `--input-type`: that one only applies to code
 * given with `--eval` or stdin, and a worker started from a file refuses to run with it. Only then are the options
 * passed explicitly (an explicit list is checked, and some process-wide options are refused in a worker).
 */
function startWorker(job: WorkerJob): Worker {
  const execArgv = process.execArgv;
  if (!execArgv.some((option) => option === '--input-type' || option.startsWith('--input-type='))) {
    return new Worker(workerUrl, { workerData: job });
  }
  const kept: string[] = [];
  for (let index = 0; index < execArgv.length; index++) {
    const option = execArgv[index]!;
    if (option === '--input-type') {
      index++; // and its value
    } else if (!option.startsWith('--input-type=')) {
      kept.push(option);
    }
  }
  try {
    return new Worker(workerUrl, { workerData: job, execArgv: kept });
  } catch (error) {
    if ((error as { code?: string }).code !== 'ERR_WORKER_INVALID_EXEC_ARGV') {
      throw error;
    }
    return new Worker(workerUrl, { workerData: job, execArgv: [] });
  }
}

/** The error of a call whose signal was aborted before it started. */
export function abortedBeforeStart(signal: AbortSignal): IdmlError {
  return new IdmlError('The operation was aborted before it started.', Status.cancelled, { cause: signal.reason });
}

/**
 * Runs one blocking PDF call on a new worker thread, so the event loop stays free.
 *
 * Why a worker and not koffi's `func.async`: the library keeps the last error per thread, and an async koffi call runs
 * on a libuv pool thread that JavaScript cannot reach afterwards, so the message of a failure would be lost. A long
 * export would also hold one of libuv's four pool threads (shared with fs, dns and zlib) for its whole duration, and
 * every progress callback would block it until the main event loop got to it. In a worker, the call, its callback,
 * the error and the releases all stay on one thread.
 *
 * Progress is posted to this thread; `onProgress` returning false (or throwing) and `signal` set a shared flag that
 * the worker's callback reads after each page. The worker never waits for this thread.
 */
export function runInWorker(native: Native, operation: WorkerOperation, control: ProgressOptions = {}): Promise<WorkerResult> {
  const { onProgress, signal } = control;
  if (signal?.aborted) {
    return Promise.reject(abortedBeforeStart(signal));
  }
  const cancel = new SharedArrayBuffer(4);
  const flag = new Int32Array(cancel);
  const job: WorkerJob = {
    libraryPath: native.path,
    librarySource: native.source,
    operation,
    cancel,
    reportProgress: onProgress !== undefined,
    cancellable: signal !== undefined,
  };

  return new Promise<WorkerResult>((resolve, reject) => {
    let settled = false;
    let cause: unknown;
    const worker = startWorker(job);

    const requestCancel = (reason: unknown): void => {
      cause ??= reason;
      Atomics.store(flag, 0, 1);
    };
    const onAbort = (): void => requestCancel(signal!.reason);
    signal?.addEventListener('abort', onAbort, { once: true });

    const finish = (action: () => void): void => {
      if (settled) {
        return;
      }
      settled = true;
      signal?.removeEventListener('abort', onAbort);
      action();
    };

    worker.on('message', (message: WorkerMessage) => {
      switch (message.type) {
        case 'progress':
          if (!settled && onProgress && Atomics.load(flag, 0) === 0) {
            try {
              if (onProgress(message.done, message.total) === false) {
                requestCancel(undefined);
              }
            } catch (error) {
              requestCancel(error);
            }
          }
          break;
        case 'result':
          if (settled && message.handle !== undefined) {
            native.documentClose(message.handle); // nobody is waiting for this document any more
          }
          finish(() => resolve(message));
          break;
        case 'error':
          finish(() => {
            const code = message.code as StatusCode;
            reject(
              message.idml
                ? new IdmlError(message.message, code, code === Status.cancelled && cause !== undefined ? { cause } : undefined)
                : new Error(message.message),
            );
          });
          break;
      }
    });
    worker.on('error', (error) => finish(() => reject(error)));
    worker.on('exit', (code) => finish(() => reject(new IdmlError(`The CoreIdml worker thread stopped (exit code ${code}) before the call finished.`))));
  });
}
