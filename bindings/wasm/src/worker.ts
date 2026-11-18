// The page side of the engine in a Web Worker: every call is async and runs in the worker (worker-thread.ts), so the
// page stays responsive while a PDF is rendered. Documents live in the worker; the page holds handles to them.

import { IdmlError, Status, type DocumentInfo, type StatusCode } from './index.ts';
import type { DocumentMethod, Operation, Request, Response } from './protocol.ts';
import type {
  DiffReport,
  DocumentSpec,
  DocumentSummary,
  FillData,
  FillReport,
  ImportXmlOptions,
  ImportXmlReport,
  PdfExport,
  PdfExportOptions,
  PdfImportOptions,
  PdfImportReport,
  ProgressCallback,
  ProgressOptions,
  StorySummary,
  SummaryOptions,
  ValidationReport,
  XmlNode,
} from './types.ts';

export { IdmlError, Status };
export type { DocumentInfo, StatusCode };
export type * from './types.ts';

export interface StartOptions {
  /** URL of the worker script. Default: `worker-thread.js` next to this module. */
  workerUrl?: string | URL;
  /** URL of the engine's `_framework` folder, as for `CoreIdml.load`. Default: next to the worker script. */
  frameworkUrl?: string | URL;
}

/** What {@link IdmlWorker.importPdf} resolves to: the document, its report, and the images it extracted. */
export interface WorkerPdfImport {
  document: WorkerDocument;
  report: PdfImportReport;
  /** The folder of the worker's file system the images were written to; the document's links point there. */
  imageDirectory: string;
  /** The extracted images by file name. */
  images: Record<string, Uint8Array>;
}

interface Pending {
  resolve: (value: any) => void;
  reject: (error: unknown) => void;
  onProgress?: ProgressCallback;
  /** The shared flag that stops the call in the worker (cross-origin isolated pages only). */
  cancel?: Int32Array;
  /** Why the call was cancelled (what onProgress threw, the abort reason): the cause of the rejection. */
  cause?: { value: unknown };
}

/**
 * The engine in a Web Worker. Start one with {@link IdmlWorker.start}; documents opened through it live in the worker
 * until closed. Calls run one at a time, in order; the page stays responsive meanwhile. Start several workers to
 * work in parallel (each loads its own engine).
 *
 * Cancelling a running PDF call (an `AbortSignal`, or `onProgress` returning false) needs memory shared with the
 * worker, which browsers only allow on cross-origin isolated pages. There the worker stops after the current page,
 * the call rejects with `Status.cancelled`, and the worker and its documents stay usable. Elsewhere a synchronous
 * WebAssembly call cannot be interrupted: cancelling terminates the worker. The call still rejects with
 * `Status.cancelled`; every other pending call rejects with `Status.invalidOperation`, and the worker's documents and
 * files are gone. {@link IdmlWorker.canCancel} says which applies.
 */
export class IdmlWorker {
  readonly #worker: Worker;
  readonly #pending = new Map<number, Pending>();
  #nextId = 0;
  #terminated: IdmlError | undefined;
  #version = '';

  private constructor(worker: Worker) {
    this.#worker = worker;
  }

  /** Starts a module worker and loads the engine in it. */
  static async start(options: StartOptions = {}): Promise<IdmlWorker> {
    const worker =
      options.workerUrl === undefined
        ? new Worker(new URL('./worker-thread.js', import.meta.url), { type: 'module', name: 'coreidml' })
        : new Worker(options.workerUrl, { type: 'module', name: 'coreidml' });
    const client = new IdmlWorker(worker);
    client.#listen();
    try {
      // A relative frameworkUrl is relative to the page, not to the worker script.
      const frameworkUrl = options.frameworkUrl === undefined ? undefined : new URL(options.frameworkUrl, globalThis.location?.href).href;
      const loaded = await client.#call<{ version: string }>({ op: 'load', ...(frameworkUrl === undefined ? {} : { frameworkUrl }) });
      client.#version = loaded.version;
    } catch (error) {
      client.terminate(error);
      throw error;
    }
    return client;
  }

  /**
   * Whether a running PDF call can be cancelled without terminating the worker: true on cross-origin isolated pages
   * (served with `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp`).
   */
  static get canCancel(): boolean {
    return typeof SharedArrayBuffer === 'function' && (globalThis as { crossOriginIsolated?: boolean }).crossOriginIsolated === true;
  }

  /** Version of the library in the worker. */
  get version(): string {
    return this.#version;
  }

  /** Whether the worker was terminated (by {@link terminate}, a cancellation that needed it, or a crash). */
  get terminated(): boolean {
    return this.#terminated !== undefined;
  }

  /**
   * Stops the worker; its documents and files are gone. Pending calls, and every later call, reject with an
   * `IdmlError` (`Status.invalidOperation`) whose cause is `reason`. Calling it again does nothing.
   */
  terminate(reason?: unknown): void {
    if (this.#terminated !== undefined) {
      return;
    }
    this.#terminated =
      reason instanceof IdmlError && reason.code === Status.invalidOperation
        ? reason
        : new IdmlError('The CoreIdml worker was terminated.', Status.invalidOperation, reason === undefined ? undefined : { cause: reason });
    this.#worker.terminate();
    for (const pending of this.#pending.values()) {
      pending.reject(this.#terminated);
    }
    this.#pending.clear();
  }

  // ---- documents ------------------------------------------------------------------------------------------------

  /** A new document from a document spec (an object or JSON text); without one, a blank A4 page. */
  async create(spec?: DocumentSpec | string): Promise<WorkerDocument> {
    return createDocument(this, await this.#call<number>({ op: 'create', ...(spec === undefined ? {} : { spec }) }));
  }

  /** Opens the bytes of an .idml file. They are copied to the worker; the caller keeps them. */
  async open(data: Uint8Array | ArrayBuffer): Promise<WorkerDocument> {
    return createDocument(this, await this.#call<number>({ op: 'open', bytes: data }));
  }

  /** Rebuilds an editable document from the bytes of a PDF; returns it with its report and the images it extracted. */
  async importPdf(data: Uint8Array | ArrayBuffer, options: PdfImportOptions = {}): Promise<WorkerPdfImport> {
    const { onProgress, signal, ...rest } = options;
    const { handle, ...result } = await this.#call<Omit<WorkerPdfImport, 'document'> & { handle: number }>(
      { op: 'importPdf', bytes: data, options: rest },
      { onProgress, signal },
    );
    return { document: createDocument(this, handle), ...result };
  }

  // ---- the in-memory file system of the worker ----------------------------------------------------------------------

  /** Font files by file name, into `/fonts`; PDF calls look there unless their options name `fontDirectories`. */
  addFonts(fonts: Record<string, Uint8Array | ArrayBuffer>): Promise<void> {
    return this.#call({ op: 'addFonts', files: fonts });
  }

  /** Placed images and PDFs by file name, into `/links`; PDF export finds a document's links there by file name. */
  addLinks(files: Record<string, Uint8Array | ArrayBuffer>): Promise<void> {
    return this.#call({ op: 'addLinks', files });
  }

  /** Writes a file, creating its folders. Text is written as UTF-8. */
  writeFile(path: string, data: Uint8Array | ArrayBuffer | string): Promise<void> {
    return this.#call({ op: 'writeFile', path, data });
  }

  readFile(path: string): Promise<Uint8Array> {
    return this.#call({ op: 'readFile', path });
  }

  /** The names in a folder, sorted; empty when it does not exist. */
  listFiles(directory: string): Promise<string[]> {
    return this.#call({ op: 'listFiles', directory });
  }

  /** Removes a file, or a folder with everything in it. */
  remove(path: string): Promise<void> {
    return this.#call({ op: 'remove', path });
  }

  // ---- messaging --------------------------------------------------------------------------------------------------

  /** @internal Used by WorkerDocument. */
  static send<T>(worker: IdmlWorker, operation: Operation, control: ProgressOptions = {}): Promise<T> {
    return worker.#call<T>(operation, control);
  }

  #call<T>(operation: Operation, control: ProgressOptions = {}): Promise<T> {
    if (this.#terminated !== undefined) {
      return Promise.reject(this.#terminated);
    }
    const { onProgress, signal } = control;
    if (onProgress !== undefined && onProgress !== null && typeof onProgress !== 'function') {
      return Promise.reject(new IdmlError('onProgress must be a function.', Status.invalidArgument));
    }
    if (signal?.aborted) {
      return Promise.reject(new IdmlError('The operation was aborted before it started.', Status.cancelled, { cause: signal.reason }));
    }
    const id = ++this.#nextId;
    const cancel = (onProgress || signal) && IdmlWorker.canCancel ? new Int32Array(new SharedArrayBuffer(4)) : undefined;
    return new Promise<T>((resolve, reject) => {
      const pending: Pending = { resolve, reject, ...(onProgress ? { onProgress } : {}), ...(cancel ? { cancel } : {}) };
      this.#pending.set(id, pending);
      if (signal) {
        const abort = () => this.#cancel(id, signal.reason);
        signal.addEventListener('abort', abort, { once: true });
        const settle = () => signal.removeEventListener('abort', abort);
        pending.resolve = (value) => (settle(), resolve(value));
        pending.reject = (error) => (settle(), reject(error));
      }
      const request: Request = { ...operation, id, ...(onProgress ? { progress: true } : {}), ...(cancel ? { cancel } : {}) };
      try {
        // Input bytes are copied (structured clone), not transferred: the caller keeps its arrays.
        this.#worker.postMessage(request);
      } catch (error) {
        this.#pending.delete(id);
        pending.reject(new IdmlError(`The call could not be sent to the worker: ${error instanceof Error ? error.message : String(error)}`, Status.invalidArgument, { cause: error }));
      }
    });
  }

  /**
   * Cancels a running call: through the shared flag when there is one (the worker then answers with
   * `Status.cancelled`), otherwise by rejecting it and terminating the worker.
   */
  #cancel(id: number, cause: unknown): void {
    const pending = this.#pending.get(id);
    if (!pending) {
      return;
    }
    pending.cause ??= { value: cause };
    if (pending.cancel) {
      Atomics.store(pending.cancel, 0, 1);
      return;
    }
    this.#pending.delete(id);
    pending.reject(new IdmlError('The operation was cancelled; the worker was terminated to stop it.', Status.cancelled, { cause: pending.cause.value }));
    this.terminate(new IdmlError('The CoreIdml worker was terminated to cancel a call.', Status.invalidOperation, { cause }));
  }

  #listen(): void {
    this.#worker.onmessage = (event: MessageEvent<Response>) => {
      const message = event.data;
      const pending = this.#pending.get(message.id);
      if (!pending) {
        return;
      }
      if (message.kind === 'progress') {
        let keepGoing = true;
        let thrown: { value: unknown } | undefined;
        try {
          keepGoing = pending.onProgress?.(message.done, message.total) !== false;
        } catch (error) {
          thrown = { value: error };
          keepGoing = false;
        }
        if (!keepGoing) {
          this.#cancel(message.id, thrown?.value);
        }
        return;
      }
      this.#pending.delete(message.id);
      if (message.kind === 'result') {
        pending.resolve(message.value);
      } else {
        const cause = message.code === Status.cancelled && pending.cause?.value !== undefined ? pending.cause.value : message.cause;
        pending.reject(new IdmlError(message.message, message.code as StatusCode, cause === undefined ? undefined : { cause }));
      }
    };
    this.#worker.onerror = (event: ErrorEvent) => {
      event.preventDefault();
      this.terminate(new IdmlError(`The CoreIdml worker failed: ${event.message || 'it could not be started'}`, Status.invalidOperation));
    };
    this.#worker.onmessageerror = () => {
      this.terminate(new IdmlError('A message from the CoreIdml worker could not be read.', Status.invalidOperation));
    };
  }
}

// Documents that are never closed are closed in the worker when they are garbage collected.
const finalizer = new FinalizationRegistry<{ worker: IdmlWorker; handle: number }>(({ worker, handle }) => {
  if (!worker.terminated) {
    IdmlWorker.send(worker, { op: 'close', handle }).catch(() => {});
  }
});

// Documents are created by IdmlWorker only; WorkerDocument hands its private constructor to this module.
let createDocument: (worker: IdmlWorker, handle: number) => WorkerDocument;

/**
 * A document in the worker: the methods of `IdmlDocument`, returning promises. Close it with {@link close} or
 * `await using`; a document that is forgotten is closed when it is garbage collected.
 */
export class WorkerDocument implements AsyncDisposable {
  readonly #worker: IdmlWorker;
  #handle: number;

  private constructor(worker: IdmlWorker, handle: number) {
    this.#worker = worker;
    this.#handle = handle;
    finalizer.register(this, { worker, handle }, this);
  }

  static {
    createDocument = (worker, handle) => new WorkerDocument(worker, handle);
  }

  /** Whether {@link close} was called. A document of a terminated worker is gone, but not closed. */
  get closed(): boolean {
    return this.#handle === 0;
  }

  info(): Promise<DocumentInfo> {
    return this.#method('info');
  }

  readPart(path: string): Promise<Uint8Array> {
    return this.#method('readPart', path);
  }

  readText(path: string): Promise<string> {
    return this.#method('readText', path);
  }

  writePart(path: string, data: Uint8Array | string): Promise<void> {
    return this.#method('writePart', path, data);
  }

  /** The document as the bytes of an .idml file. */
  save(): Promise<Uint8Array> {
    return this.#method('save');
  }

  summary(options: SummaryOptions = {}): Promise<DocumentSummary> {
    return this.#method('summary', options);
  }

  story(storyId: string): Promise<StorySummary> {
    return this.#method('story', storyId);
  }

  xmlStructure(): Promise<XmlNode | null> {
    return this.#method('xmlStructure');
  }

  exportXml(options: { at?: string } = {}): Promise<string> {
    return this.#method('exportXml', options);
  }

  validate(): Promise<ValidationReport> {
    return this.#method('validate');
  }

  /** What changed from this document to `other`, a document of the same worker. */
  async diff(other: WorkerDocument): Promise<DiffReport> {
    return this.#send({ op: 'diff', handle: this.#open(), other: this.#other(other, 'other') });
  }

  apply(spec: DocumentSpec | string): Promise<void> {
    return this.#method('apply', spec);
  }

  fill(data: FillData = {}): Promise<FillReport> {
    return this.#method('fill', data);
  }

  replaceText(find: string, replacement: string): Promise<number> {
    return this.#method('replaceText', find, replacement);
  }

  setRunText(storyId: string, runIndex: number, text: string, options: { force?: boolean } = {}): Promise<void> {
    return this.#method('setRunText', storyId, runIndex, text, options);
  }

  relink(linkId: string, uriOrPath: string, options: { refit?: boolean } = {}): Promise<void> {
    return this.#method('relink', linkId, uriOrPath, options);
  }

  importXml(xml: string, options: ImportXmlOptions = {}): Promise<ImportXmlReport> {
    return this.#method('importXml', xml, options);
  }

  /** Appends pages of `source`, a document of the same worker; returns the number of pages added. */
  async importPages(source: WorkerDocument, pages: readonly number[] = []): Promise<number> {
    return this.#send({ op: 'importPages', handle: this.#open(), source: this.#other(source, 'source'), pages: [...pages] });
  }

  /**
   * Renders the document to a PDF in the worker. `onProgress(done, total)` runs on the page after each page;
   * returning false, or aborting `signal`, cancels (see {@link IdmlWorker} for what cancelling takes).
   */
  async exportPdfBytes(options: PdfExportOptions = {}): Promise<PdfExport> {
    const { onProgress, signal, ...rest } = options;
    return this.#send({ op: 'exportPdfBytes', handle: this.#open(), options: rest }, { onProgress, signal });
  }

  /** Releases the document in the worker. Calling it again does nothing. */
  async close(): Promise<void> {
    if (this.#handle === 0) {
      return;
    }
    const handle = this.#handle;
    this.#handle = 0;
    finalizer.unregister(this);
    if (!this.#worker.terminated) {
      await IdmlWorker.send(this.#worker, { op: 'close', handle });
    }
  }

  [Symbol.asyncDispose](): Promise<void> {
    return this.close();
  }

  async #method<T>(method: DocumentMethod, ...args: unknown[]): Promise<T> {
    return this.#send({ op: 'call', handle: this.#open(), method, args });
  }

  #send<T>(operation: Operation, control?: ProgressOptions): Promise<T> {
    return IdmlWorker.send<T>(this.#worker, operation, control);
  }

  #open(): number {
    if (this.#handle === 0) {
      throw new IdmlError('The document is closed.', Status.invalidHandle);
    }
    return this.#handle;
  }

  #other(document: WorkerDocument, name: string): number {
    if (!(document instanceof WorkerDocument) || document.#worker !== this.#worker) {
      throw new IdmlError(`${name} must be a document of the same IdmlWorker.`, Status.invalidArgument);
    }
    if (document.#handle === 0) {
      throw new IdmlError(`${name}: the document is closed.`, Status.invalidHandle);
    }
    return document.#handle;
  }
}
