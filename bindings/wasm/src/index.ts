import { ABI_VERSION, Status, type StatusCode } from './abi.generated.ts';

export { ABI_VERSION, Status };
export type { StatusCode };

/** A failed call. `code` is one of the {@link Status} values, the same in every CoreIdml binding. */
export class IdmlError extends Error {
  readonly code: StatusCode;

  constructor(message: string, code: StatusCode = Status.failed, options?: ErrorOptions) {
    super(message, options);
    this.name = 'IdmlError';
    this.code = code;
  }
}

/** What {@link IdmlDocument.info} returns. */
export interface DocumentInfo {
  /** The InDesign DOM version the document was written with, if it says. */
  domVersion: string | null;
  /** Every part of the package, in package order. */
  parts: { path: string; length: number; modified: boolean }[];
  /** Paths of the spread parts, in document order. */
  spreads: string[];
  masterSpreads: string[];
  stories: string[];
}

export interface LoadOptions {
  /**
   * URL of the folder that holds `dotnet.js` and the .wasm files (the `_framework` folder of the build).
   * Default: `_framework/` next to this module, where the published package has it.
   */
  frameworkUrl?: string | URL;
}

/** The [JSExport] functions of src/CoreIdml.Wasm/WasmExports.cs. */
interface WasmExports {
  AbiVersion(): number;
  Version(): string;
  LastErrorCode(): number;
  LastErrorMessage(): string | null;
  DocumentOpen(data: Uint8Array): number;
  DocumentInfo(document: number): string;
  DocumentReadPart(document: number, path: string): Uint8Array;
  DocumentWritePart(document: number, path: string, data: Uint8Array): void;
  DocumentSave(document: number): Uint8Array;
  DocumentClose(document: number): void;
}

// One .NET runtime per JavaScript realm: loading it twice is not supported, and there is nothing to gain.
let loading: { url: string; library: Promise<CoreIdml> } | undefined;

// Documents that are never closed are released when they are garbage collected. Handles are never reused by the
// library, so a late finalizer cannot close another document.
const finalizer = new FinalizationRegistry<{ exports: WasmExports; handle: number }>(({ exports, handle }) => {
  try {
    exports.DocumentClose(handle);
  } catch {
    // Nothing useful to do while finalizing.
  }
});

/** The loaded library. Get it with {@link CoreIdml.load}, once; then every call is synchronous. */
export class CoreIdml {
  readonly #exports: WasmExports;

  /** Version of the library. */
  readonly version: string;

  private constructor(exports: WasmExports) {
    this.#exports = exports;
    this.version = exports.Version();
  }

  /** Downloads and starts the WebAssembly build. Later calls return the same instance. */
  static load(options: LoadOptions = {}): Promise<CoreIdml> {
    const url = new URL(options.frameworkUrl ?? new URL('./_framework/', import.meta.url)).href.replace(/\/?$/, '/');
    if (loading) {
      if (loading.url !== url) {
        return Promise.reject(new IdmlError(`CoreIdml is already loaded from ${loading.url}; a page can use only one build.`, Status.invalidOperation));
      }
      return loading.library;
    }
    const library = CoreIdml.#start(url);
    loading = { url, library };
    // A failed load may be retried (a network error, a wrong URL).
    library.catch(() => {
      if (loading?.library === library) {
        loading = undefined;
      }
    });
    return library;
  }

  static async #start(url: string): Promise<CoreIdml> {
    let exports: WasmExports;
    try {
      const { dotnet } = await import(/* webpackIgnore: true */ /* @vite-ignore */ `${url}dotnet.js`);
      const runtime = await dotnet.create();
      const assembly = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
      exports = assembly.CoreIdml.Wasm.WasmExports;
    } catch (error) {
      throw new IdmlError(`Could not load the CoreIdml WebAssembly build from ${url}: ${messageOf(error)}`, Status.failed, { cause: error });
    }
    const abi = exports.AbiVersion();
    if (abi !== ABI_VERSION) {
      throw new IdmlError(`The WebAssembly build at ${url} has ABI version ${abi}; this package needs ${ABI_VERSION}. Use both from the same release.`);
    }
    return new CoreIdml(exports);
  }

  /** Opens the bytes of an .idml file. They are copied; the document does not keep them. */
  open(data: Uint8Array | ArrayBuffer): IdmlDocument {
    const bytes = data instanceof ArrayBuffer ? new Uint8Array(data) : data;
    if (!(bytes instanceof Uint8Array)) {
      throw new IdmlError('data must be a Uint8Array or an ArrayBuffer holding an .idml file.', Status.invalidArgument);
    }
    return createDocument(this.#exports, call(this.#exports, () => this.#exports.DocumentOpen(bytes)));
  }
}

// Documents are created by CoreIdml.open only; IdmlDocument hands its private constructor to this module.
let createDocument: (exports: WasmExports, handle: number) => IdmlDocument;

/**
 * An InDesign document held by the library. Close it with {@link close} or `using`; a document that is forgotten
 * is released when it is garbage collected.
 */
export class IdmlDocument implements Disposable {
  readonly #exports: WasmExports;
  #handle: number;

  private constructor(exports: WasmExports, handle: number) {
    this.#exports = exports;
    this.#handle = handle;
    finalizer.register(this, { exports, handle }, this);
  }

  static {
    createDocument = (exports, handle) => new IdmlDocument(exports, handle);
  }

  /** Whether {@link close} was called. */
  get closed(): boolean {
    return this.#handle === 0;
  }

  /** The parts of the package and the manifest: spreads, master spreads, stories. */
  info(): DocumentInfo {
    return JSON.parse(this.#call((handle) => this.#exports.DocumentInfo(handle))) as DocumentInfo;
  }

  /** The bytes of one part (e.g. `Stories/Story_u188.xml`) as they would be saved now. */
  readPart(path: string): Uint8Array {
    requireString(path, 'path');
    return this.#call((handle) => this.#exports.DocumentReadPart(handle, path));
  }

  /** One part as text (parts are UTF-8 XML, except images a template may embed). */
  readText(path: string): string {
    return new TextDecoder('utf-8', { fatal: true }).decode(this.readPart(path));
  }

  /** Adds or replaces one part. Text is written as UTF-8. */
  writePart(path: string, data: Uint8Array | string): void {
    requireString(path, 'path');
    const bytes = typeof data === 'string' ? new TextEncoder().encode(data) : data;
    if (!(bytes instanceof Uint8Array)) {
      throw new IdmlError('data must be a Uint8Array or a string.', Status.invalidArgument);
    }
    this.#call((handle) => this.#exports.DocumentWritePart(handle, path, bytes));
  }

  /** The document as the bytes of an .idml file. Parts that were not changed are written byte for byte. */
  save(): Uint8Array {
    return this.#call((handle) => this.#exports.DocumentSave(handle));
  }

  /** Releases the document. Calling it again does nothing. */
  close(): void {
    if (this.#handle !== 0) {
      const handle = this.#handle;
      this.#handle = 0;
      finalizer.unregister(this);
      this.#exports.DocumentClose(handle);
    }
  }

  [Symbol.dispose](): void {
    this.close();
  }

  #call<T>(action: (handle: number) => T): T {
    if (this.#handle === 0) {
      throw new IdmlError('The document is closed.', Status.invalidHandle);
    }
    return call(this.#exports, () => action(this.#handle));
  }
}

/** Runs a call into the library; a failure becomes an {@link IdmlError} with the library's status code and message. */
function call<T>(exports: WasmExports, action: () => T): T {
  try {
    return action();
  } catch (error) {
    const code = exports.LastErrorCode();
    throw new IdmlError(exports.LastErrorMessage() ?? messageOf(error), (code === Status.ok ? Status.failed : code) as StatusCode, { cause: error });
  }
}

function requireString(value: unknown, name: string): void {
  if (typeof value !== 'string') {
    throw new IdmlError(`${name} must be a string.`, Status.invalidArgument);
  }
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
