import { rmSync } from 'node:fs';
import { availableParallelism } from 'node:os';
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
 * Starts a worker. It inherits the process's Node options, except `--input-type`: that one only applies to code
 * given with `--eval` or stdin, and a worker started from a file refuses to run with it. Only then are the options
 * passed explicitly (an explicit list is checked, and some process-wide options are refused in a worker).
 */
function startWorker(): Worker {
  const execArgv = process.execArgv;
  if (!execArgv.some((option) => option === '--input-type' || option.startsWith('--input-type='))) {
    return new Worker(workerUrl);
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
    return new Worker(workerUrl, { execArgv: kept });
  } catch (error) {
    if ((error as { code?: string }).code !== 'ERR_WORKER_INVALID_EXEC_ARGV') {
      throw error;
    }
    return new Worker(workerUrl, { execArgv: [] });
  }
}

// Workers are kept between calls: starting one (a new isolate, loading the modules, binding every function of the
// library) costs far more than a call to a warm one. A worker runs one call at a time; calls made together each get
// one. Idle workers are unreferenced, so they never keep the process alive, and at most a few are kept.
const idle: Worker[] = [];
const maxIdle = Math.max(1, Math.min(4, availableParallelism()));

function acquireWorker(): Worker {
  const reused = idle.pop();
  if (reused !== undefined) {
    reused.ref();
    return reused;
  }
  const worker = startWorker();
  // A worker that stops (an uncaught error, terminate) is never handed out again.
  worker.once('exit', () => {
    const index = idle.indexOf(worker);
    if (index >= 0) {
      idle.splice(index, 1);
    }
  });
  return worker;
}

function releaseWorker(worker: Worker): void {
  if (idle.length < maxIdle) {
    worker.unref();
    idle.push(worker);
  } else {
    void worker.terminate();
  }
}

/** How many workers wait for a call (for tests). */
export function idleWorkers(): number {
  return idle.length;
}

/** The error of a call whose signal was aborted before it started. */
export function abortedBeforeStart(signal: AbortSignal): IdmlError {
  return new IdmlError('The operation was aborted before it started.', Status.cancelled, { cause: signal.reason });
}

/**
 * Runs one blocking PDF call on a worker thread, so the event loop stays free.
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
    let worker: Worker;
    try {
      worker = acquireWorker();
    } catch (error) {
      reject(error);
      return;
    }

    const requestCancel = (reason: unknown): void => {
      cause ??= reason;
      Atomics.store(flag, 0, 1);
    };
    const onAbort = (): void => requestCancel(signal!.reason);
    signal?.addEventListener('abort', onAbort, { once: true });

    const onMessage = (message: WorkerMessage): void => {
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
          if (!settled && Atomics.load(flag, 0) !== 0) {
            // Cancelled on this thread after the worker had already finished (a short call can post every progress
            // message and its result before this thread reads the first one): the cancellation still wins, so an
            // exception thrown by onProgress is never lost. Nothing of the result is left behind.
            discard(message);
            finish(true, () => reject(new IdmlError('The operation was cancelled.', Status.cancelled, cause !== undefined ? { cause } : undefined)));
            break;
          }
          if (settled && message.handle !== undefined) {
            native.documentClose(message.handle); // nobody is waiting for this document any more
          }
          finish(true, () => resolve(message));
          break;
        case 'error':
          finish(true, () => {
            const code = message.code as StatusCode;
            reject(
              message.idml
                ? new IdmlError(message.message, code, code === Status.cancelled && cause !== undefined ? { cause } : undefined)
                : new Error(message.message),
            );
          });
          break;
      }
    };
    const onError = (error: Error): void => finish(false, () => reject(error));
    const onExit = (code: number): void =>
      finish(false, () => reject(new IdmlError(`The CoreIdml worker thread stopped (exit code ${code}) before the call finished.`)));

    /** Releases what a finished call produced: the imported document, or the file the export wrote. */
    function discard(message: Extract<WorkerMessage, { type: 'result' }>): void {
      if (message.handle !== undefined) {
        native.documentClose(message.handle);
      }
      if (operation.kind === 'exportPdfFile') {
        rmSync(operation.path, { force: true });
      }
    }

    /** Settles once; a worker that answered goes back to the pool, one that failed or stopped does not. */
    function finish(answered: boolean, action: () => void): void {
      if (settled) {
        return;
      }
      settled = true;
      signal?.removeEventListener('abort', onAbort);
      worker.off('message', onMessage);
      worker.off('error', onError);
      worker.off('exit', onExit);
      if (answered) {
        releaseWorker(worker);
      }
      action();
    }

    worker.on('message', onMessage);
    worker.on('error', onError);
    worker.on('exit', onExit);
    worker.postMessage(job);
  });
}
