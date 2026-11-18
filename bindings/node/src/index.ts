import { fileURLToPath } from 'node:url';

import { ABI_VERSION, Status, type Out, type Pointer, type StatusCode } from './abi.generated.ts';
import { IdmlError, check, loadLibrary, takeBytes, takeString, type Native } from './native.ts';

export { ABI_VERSION, IdmlError, Status };
export type { StatusCode };

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
  /** Full path of the native library. Default: searched for; see findLibrary in library.ts. */
  libraryPath?: string | URL;
}

let loaded: CoreIdml | undefined;

// Documents that are never closed are released when they are garbage collected. Handles are never reused by the
// library, so a late finalizer cannot close another document.
const finalizer = new FinalizationRegistry<{ native: Native; handle: number }>(({ native, handle }) => {
  try {
    native.documentClose(handle);
  } catch {
    // Nothing useful to do while finalizing.
  }
});

/**
 * The loaded library. `CoreIdml.load()` has the same shape as in `@coreidml/wasm` (where it returns a promise), so
 * `const idml = await CoreIdml.load()` works with either package.
 */
export class CoreIdml {
  readonly #native: Native;

  /** Version of the library. */
  readonly version: string;

  private constructor(native: Native) {
    this.#native = native;
    this.version = native.version() ?? '';
  }

  /** Loads the native library, once; later calls return the same instance. A process can use only one library. */
  static load(options: LoadOptions = {}): CoreIdml {
    const wanted = options.libraryPath === undefined ? undefined : toPath(options.libraryPath);
    if (loaded) {
      if (wanted !== undefined && wanted !== loaded.libraryPath) {
        throw new IdmlError(`The native library is already loaded from ${loaded.libraryPath}; a process can use only one.`, Status.invalidOperation);
      }
      return loaded;
    }
    return (loaded = new CoreIdml(loadLibrary(wanted)));
  }

  /** Path the native library was loaded from. */
  get libraryPath(): string {
    return this.#native.path;
  }

  /** Opens an .idml file from a path (or file: URL), or from its bytes, which are not kept. */
  open(source: string | URL | Uint8Array | ArrayBuffer): IdmlDocument {
    const native = this.#native;
    const handle: Out<number | bigint> = [0];
    if (typeof source === 'string' || source instanceof URL) {
      check(native, native.documentOpenFile(toPath(source), handle));
    } else {
      const bytes = source instanceof ArrayBuffer ? new Uint8Array(source) : source;
      if (!(bytes instanceof Uint8Array)) {
        throw new IdmlError('source must be a path, a file: URL, or the bytes of an .idml file.', Status.invalidArgument);
      }
      check(native, native.documentOpen(bytes, bytes.length, handle));
    }
    return createDocument(native, Number(handle[0]));
  }
}

// Documents are created by CoreIdml.open only; IdmlDocument hands its private constructor to this module.
let createDocument: (native: Native, handle: number) => IdmlDocument;

/**
 * An InDesign document held by the native library. Close it with {@link close} or `using`; a document that is
 * forgotten is released when it is garbage collected. Calls are synchronous; a document may be used from any
 * thread (worker_threads), and the library serializes calls on it.
 */
export class IdmlDocument implements Disposable {
  readonly #native: Native;
  #handle: number;

  private constructor(native: Native, handle: number) {
    this.#native = native;
    this.#handle = handle;
    finalizer.register(this, { native, handle }, this);
  }

  static {
    createDocument = (native, handle) => new IdmlDocument(native, handle);
  }

  /** Whether {@link close} was called. */
  get closed(): boolean {
    return this.#handle === 0;
  }

  /** The parts of the package and the manifest: spreads, master spreads, stories. */
  info(): DocumentInfo {
    const json: Out<Pointer> = [null];
    check(this.#native, this.#native.documentInfo(this.#open(), json));
    return JSON.parse(takeString(this.#native, json[0])) as DocumentInfo;
  }

  /** The bytes of one part (e.g. `Stories/Story_u188.xml`) as they would be saved now. */
  readPart(path: string): Uint8Array {
    requireString(path, 'path');
    const data: Out<Pointer> = [null];
    const length: Out<number | bigint> = [0];
    check(this.#native, this.#native.documentReadPart(this.#open(), path, data, length));
    return takeBytes(this.#native, data[0], length[0]);
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
    check(this.#native, this.#native.documentWritePart(this.#open(), path, bytes, bytes.length));
  }

  /** The document as the bytes of an .idml file. Parts that were not changed are written byte for byte. */
  save(): Uint8Array {
    const data: Out<Pointer> = [null];
    const length: Out<number | bigint> = [0];
    check(this.#native, this.#native.documentSave(this.#open(), data, length));
    return takeBytes(this.#native, data[0], length[0]);
  }

  /** Saves the document as an .idml file (a path or a file: URL). */
  saveFile(path: string | URL): void {
    check(this.#native, this.#native.documentSaveFile(this.#open(), toPath(path)));
  }

  /** Releases the document. Calling it again does nothing. */
  close(): void {
    if (this.#handle !== 0) {
      const handle = this.#handle;
      this.#handle = 0;
      finalizer.unregister(this);
      this.#native.documentClose(handle);
    }
  }

  [Symbol.dispose](): void {
    this.close();
  }

  #open(): number {
    if (this.#handle === 0) {
      throw new IdmlError('The document is closed.', Status.invalidHandle);
    }
    return this.#handle;
  }
}

function toPath(value: string | URL): string {
  return value instanceof URL ? fileURLToPath(value) : value;
}

function requireString(value: unknown, name: string): void {
  if (typeof value !== 'string') {
    throw new IdmlError(`${name} must be a string.`, Status.invalidArgument);
  }
}
