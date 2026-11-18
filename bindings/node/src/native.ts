import koffi from 'koffi';

import { ABI_VERSION, CALLBACKS, PROTOTYPES, Status, type NativeFunctions, type Pointer, type StatusCode } from './abi.generated.ts';
import { buildHint, findLibrary, libraryFileName, resolveInstalledPackage, sourceCheckoutRoot } from './library.ts';

/** A failed call. `code` is one of the {@link Status} values, the same in every CoreIdml binding. */
export class IdmlError extends Error {
  readonly code: StatusCode;

  constructor(message: string, code: StatusCode = Status.failed, options?: ErrorOptions) {
    super(message, options);
    this.name = 'IdmlError';
    this.code = code;
  }
}

/** The bound C API, and where the library came from. */
export interface Native extends NativeFunctions {
  readonly path: string;
  readonly source: string;
}

// koffi keeps named types per JavaScript thread (Node-API environment): the main thread and every worker register
// the callback types once, before the prototypes that use them.
let callbacksRegistered = false;

function registerCallbacks(): void {
  if (!callbacksRegistered) {
    for (const prototype of Object.values(CALLBACKS)) {
      koffi.proto(prototype);
    }
    callbacksRegistered = true;
  }
}

/**
 * Loads the library at `path` and binds every function from its generated prototype. The ABI is checked first, so
 * a library from another release is reported as that, not as a missing function.
 */
export function bindLibrary(path: string, source: string): Native {
  registerCallbacks();
  const library = koffi.load(path);
  const bind = (prototype: string): unknown => {
    try {
      return library.func(prototype);
    } catch (error) {
      const symbol = /coreidml_\w+/.exec(prototype)?.[0];
      throw new IdmlError(
        `The library at ${path} (from ${source}) has no ${symbol}: it is not the CoreIdml native library, or it is older than this package.`,
        Status.failed,
        { cause: error },
      );
    }
  };
  const abi = (bind(PROTOTYPES.abiVersion) as () => number)();
  if (abi !== ABI_VERSION) {
    throw new IdmlError(
      `The native library at ${path} (from ${source}) has ABI version ${abi}; this package needs ${ABI_VERSION}. ` +
        'Use the library and the package from the same release.',
    );
  }
  const native: Record<string, unknown> = { path, source };
  for (const [name, prototype] of Object.entries(PROTOTYPES)) {
    native[name] = bind(prototype);
  }
  return native as unknown as Native;
}

/** Finds and loads the native library (see findLibrary for the order). The error lists every place that was tried. */
export function loadLibrary(explicit?: string): Native {
  const { candidates, skipped } = findLibrary({ explicit, resolvePackage: resolveInstalledPackage, sourceRoot: sourceCheckoutRoot() });
  const failures: string[] = [];
  for (const candidate of candidates) {
    try {
      return bindLibrary(candidate.path, candidate.source);
    } catch (error) {
      if (error instanceof IdmlError) {
        throw error; // found, but not usable
      }
      failures.push(`${candidate.source}: ${candidate.path} did not load (${messageOf(error)})`);
    }
  }
  const tried = [...skipped, ...failures].map((line) => `  - ${line}`).join('\n');
  throw new IdmlError(
    `Could not load the CoreIdml native library (${libraryFileName()}) for ${process.platform}-${process.arch}. Tried:\n${tried}\n` +
      `Set COREIDML_LIBRARY to its full path, or pass libraryPath to CoreIdml.load. ${buildHint()}`,
  );
}

/** Throws the {@link IdmlError} of a failed call; the message is read on the thread that made the call. */
export function check(native: Native, status: number): void {
  if (status !== Status.ok) {
    const known = (Object.values(Status) as number[]).includes(status);
    throw new IdmlError(native.lastErrorMessage() ?? `The call failed with status ${status}.`, (known ? status : Status.failed) as StatusCode);
  }
}

/** Copies a NUL-terminated UTF-8 string the library handed out, and releases it. */
export function takeString(native: Native, pointer: Pointer): string {
  try {
    return pointer == null ? '' : (koffi.decode(pointer, 'char', -1) as string);
  } finally {
    native.free(pointer);
  }
}

/**
 * Releases what the out parameters of a failed call may hold. The C API sets them to NULL on failure, so this is a
 * safety net; call it after {@link check} has read the error message.
 */
export function freeAll(native: Native, ...pointers: Pointer[]): void {
  for (const pointer of pointers) {
    if (pointer != null) {
      native.free(pointer);
    }
  }
}

/**
 * NULL for an optional `const char*` argument. The generated declarations type these as `string`; koffi passes
 * `null` as NULL, which the C API reads as "not given".
 */
export function optionalString(value: string | null | undefined): string {
  return (value ?? null) as unknown as string;
}

/** Copies a buffer the library handed out, and releases it. */
export function takeBytes(native: Native, pointer: Pointer, length: number | bigint): Uint8Array {
  try {
    const size = Number(length);
    return pointer == null || size <= 0 ? new Uint8Array(0) : new Uint8Array((koffi.view(pointer, size) as ArrayBuffer).slice(0));
  } finally {
    native.free(pointer);
  }
}

export function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
