// The Web Worker that owns the engine. Every call runs here, synchronously, so the page never blocks; documents live
// here and the page holds their handles. Started by IdmlWorker.start (worker.ts) as a module worker.

import { CoreIdml, IdmlDocument, IdmlError, Status } from './index.ts';
import { documentMethods, type Request, type Response } from './protocol.ts';

// The worker's global scope (typed by hand: this package compiles against the DOM library, not WebWorker).
declare const self: {
  onmessage: ((event: MessageEvent<Request>) => void) | null;
  postMessage(message: Response, transfer?: Transferable[]): void;
};

let engine: CoreIdml | undefined;
const documents = new Map<number, IdmlDocument>();
let nextHandle = 0;
const methods = new Set<string>(documentMethods);

// One call at a time, in the order they were posted (loading is the only call that waits).
let queue: Promise<void> = Promise.resolve();

self.onmessage = (event: MessageEvent<Request>) => {
  const request = event.data;
  queue = queue.then(async () => {
    try {
      const value = await run(request);
      post({ id: request.id, kind: 'result', value }, transferables(value));
    } catch (error) {
      const idml = error instanceof IdmlError ? error : undefined;
      post({
        id: request.id,
        kind: 'error',
        message: error instanceof Error ? error.message : String(error),
        code: idml?.code ?? Status.failed,
        ...(idml?.cause === undefined ? {} : { cause: messageOf(idml.cause) }),
      });
    }
  });
};

async function run(request: Request): Promise<unknown> {
  if (request.op === 'load') {
    engine ??= await CoreIdml.load(request.frameworkUrl === undefined ? {} : { frameworkUrl: request.frameworkUrl });
    return { version: engine.version };
  }
  if (!engine) {
    throw new IdmlError('The engine is not loaded.', Status.invalidOperation);
  }
  const onProgress = progress(request);
  switch (request.op) {
    case 'create':
      return register(engine.create(request.spec));
    case 'open':
      return register(engine.open(request.bytes));
    case 'importPdf': {
      const { document, ...rest } = engine.importPdf(request.bytes, { ...request.options, ...(onProgress ? { onProgress } : {}) });
      return { handle: register(document), ...rest };
    }
    case 'call': {
      if (!methods.has(request.method)) {
        throw new IdmlError(`Unknown document method ${request.method}.`, Status.invalidArgument);
      }
      const document = find(request.handle) as unknown as Record<string, (...args: unknown[]) => unknown>;
      return document[request.method]!(...request.args);
    }
    case 'diff':
      return find(request.handle).diff(find(request.other));
    case 'importPages':
      return find(request.handle).importPages(find(request.source), request.pages);
    case 'exportPdfBytes':
      return find(request.handle).exportPdfBytes({ ...request.options, ...(onProgress ? { onProgress } : {}) });
    case 'close':
      documents.get(request.handle)?.close();
      documents.delete(request.handle);
      return undefined;
    case 'addFonts':
      return engine.addFonts(request.files);
    case 'addLinks':
      return engine.addLinks(request.files);
    case 'writeFile':
      return engine.writeFile(request.path, request.data);
    case 'readFile':
      return engine.readFile(request.path);
    case 'listFiles':
      return engine.listFiles(request.directory);
    case 'remove':
      return engine.remove(request.path);
  }
}

/** Progress goes to the page; the shared flag, when the page could make one, cancels at the next page. */
function progress(request: Request): ((done: number, total: number) => boolean) | undefined {
  const { id, cancel } = request;
  if (!request.progress && !cancel) {
    return undefined;
  }
  return (done, total) => {
    if (request.progress) {
      post({ id, kind: 'progress', done, total });
    }
    return !(cancel && Atomics.load(cancel, 0) !== 0);
  };
}

function register(document: IdmlDocument): number {
  const handle = ++nextHandle;
  documents.set(handle, document);
  return handle;
}

function find(handle: number): IdmlDocument {
  const document = documents.get(handle);
  if (!document) {
    throw new IdmlError('The document is closed.', Status.invalidHandle);
  }
  return document;
}

function post(message: Response, transfer: Transferable[] = []): void {
  self.postMessage(message, transfer);
}

/** The buffers of the bytes in a result (PDF, IDML, images): moved to the page instead of copied. */
function transferables(value: unknown): Transferable[] {
  const found: Transferable[] = [];
  const visit = (item: unknown, depth: number) => {
    if (item instanceof Uint8Array) {
      if (item.buffer instanceof ArrayBuffer && item.byteOffset === 0 && item.byteLength === item.buffer.byteLength && !found.includes(item.buffer)) {
        found.push(item.buffer);
      }
    } else if (item && typeof item === 'object' && depth < 2) {
      for (const child of Object.values(item)) {
        visit(child, depth + 1);
      }
    }
  };
  visit(value, 0);
  return found;
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
