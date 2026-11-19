import { realpathSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { ABI_VERSION, Status, type Out, type Pointer, type StatusCode } from './abi.generated.ts';
import { abortedBeforeStart, runInWorker, type WorkerResult } from './background.ts';
import { exportPdfBytes, exportPdfFile, importPdf, progressCallback, type NativeProgress, type PdfSource } from './calls.ts';
import { IdmlError, check, loadLibrary, optionalString, takeBytes, takeString, type Native } from './native.ts';
import type { WorkerOperation } from './protocol.ts';
import type {
  DiffReport,
  DocumentSpec,
  DocumentSummary,
  FillData,
  FillReport,
  ImportXmlOptions,
  PdfExportOptions,
  PdfExportReport,
  PdfImportOptions,
  PdfImportReport,
  ProgressOptions,
  StorySummary,
  SummaryOptions,
  ValidationReport,
  ImportXmlReport,
  XmlNode,
} from './types.ts';

export { ABI_VERSION, IdmlError, Status };
export type { StatusCode };
export type * from './types.ts';

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

/** A document rebuilt from a PDF, and what the import found. */
export interface PdfImport {
  document: IdmlDocument;
  report: PdfImportReport;
}

/** A PDF rendered in memory, and the export report. */
export interface PdfExport {
  pdf: Uint8Array;
  report: PdfExportReport;
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

// Documents with an async call running stay reachable until it settles, so the finalizer cannot close them under it.
const busy = new Set<IdmlDocument>();

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
      if (wanted !== undefined && !sameFile(wanted, loaded.libraryPath)) {
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

  /**
   * A new document from a document spec: page size, colours, styles, and pages of text frames, images, tables and
   * shapes. A string is taken as the spec's JSON. Without a spec, one blank A4 page.
   */
  create(spec?: DocumentSpec | string): IdmlDocument {
    const native = this.#native;
    const handle: Out<number | bigint> = [0];
    check(native, native.documentCreate(optionalString(spec === undefined ? null : specJson(spec)), handle));
    return createDocument(native, Number(handle[0]));
  }

  /** Opens an .idml file from a path (or file: URL), or from its bytes, which are not kept. */
  open(source: string | URL | Uint8Array | ArrayBuffer): IdmlDocument {
    const native = this.#native;
    const handle: Out<number | bigint> = [0];
    if (typeof source === 'string' || source instanceof URL) {
      check(native, native.documentOpenFile(toPath(source), handle));
    } else {
      const bytes = toBytes(source, 'source must be a path, a file: URL, or the bytes of an .idml file.');
      check(native, native.documentOpen(bytes, bytes.length, handle));
    }
    return createDocument(native, Number(handle[0]));
  }

  /**
   * Rebuilds an editable document from a PDF (a path, a file: URL, or its bytes): text frames with inferred styles,
   * image frames, shapes. Extracted images are written to `options.imageDirectory`, since IDML links images.
   * `onProgress` returning false, or `signal`, cancels after the current page.
   */
  importPdf(source: string | URL | Uint8Array | ArrayBuffer, options: PdfImportOptions = {}): PdfImport {
    const native = this.#native;
    const pdf = pdfSource(source);
    const json = optionsJson(options);
    const { handle, report } = withProgress(options, (progress) => importPdf(native, pdf, json, progress));
    return { document: createDocument(native, handle), report: JSON.parse(report) as PdfImportReport };
  }

  /** {@link importPdf} on a worker thread: the event loop stays free. */
  async importPdfAsync(source: string | URL | Uint8Array | ArrayBuffer, options: PdfImportOptions = {}): Promise<PdfImport> {
    const native = this.#native;
    const result = await runInWorker(native, { kind: 'importPdf', source: pdfSource(source), optionsJson: optionsJson(options) }, options);
    return { document: createDocument(native, result.handle!), report: JSON.parse(result.report) as PdfImportReport };
  }
}

// Documents are created by CoreIdml only; IdmlDocument hands its private constructor to this module.
let createDocument: (native: Native, handle: number) => IdmlDocument;

/**
 * An InDesign document held by the native library. Close it with {@link close} or `using`; a document that is
 * forgotten is released when it is garbage collected.
 *
 * Calls are synchronous, except the `*Async` PDF calls, which run on a worker thread. The library serializes calls
 * on one document, so a synchronous call made while an async call runs on the same document waits for it (and
 * blocks the event loop meanwhile).
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

  /** The native handle (`coreidml_document` in coreidml.h), for passing to other C code; 0 once closed. */
  get handle(): number {
    return this.#handle;
  }

  // ---- the package -------------------------------------------------------------------------------------------------

  /** The parts of the package and the manifest: spreads, master spreads, stories. */
  info(): DocumentInfo {
    return this.#json((native, handle, json) => native.documentInfo(handle, json)) as DocumentInfo;
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

  // ---- reading -----------------------------------------------------------------------------------------------------

  /**
   * A summary for programs and agents: pages, frames with positions and text, stories, links, styles, swatches,
   * layers, tags and fonts. The options keep it small.
   */
  summary(options: SummaryOptions = {}): DocumentSummary {
    const json = jsonOrNull(options);
    return this.#json((native, handle, out) => native.documentSummary(handle, optionalString(json), out)) as DocumentSummary;
  }

  /** One story in full: its text, frames, and every content run with its index, styles and scope. */
  story(storyId: string): StorySummary {
    requireString(storyId, 'storyId');
    return this.#json((native, handle, out) => native.documentStory(handle, storyId, out)) as StorySummary;
  }

  /** The XML structure as a tree of `{ tag, id, path, boundTo, attributes, text, children }`; null without one. */
  xmlStructure(): XmlNode | null {
    return this.#json((native, handle, out) => native.documentXmlStructure(handle, out)) as XmlNode | null;
  }

  /** The XML structure, or the subtree at `at` (e.g. `/Root/article[2]`), as XML. */
  exportXml(options: { at?: string } = {}): string {
    if (options.at !== undefined) {
      requireString(options.at, 'at');
    }
    const xml: Out<Pointer> = [null];
    check(this.#native, this.#native.documentExportXml(this.#open(), optionalString(options.at), xml));
    return takeString(this.#native, xml[0]);
  }

  /** Checks references, ids, layers, threads and XML bindings. */
  validate(): ValidationReport {
    return this.#json((native, handle, out) => native.documentValidate(handle, out)) as ValidationReport;
  }

  /** What changed from this document to `other`: stories run by run, links, page count. */
  diff(other: IdmlDocument): DiffReport {
    const after = IdmlDocument.#handleOf(other, 'other');
    return this.#json((native, handle, out) => native.documentDiff(handle, after, out)) as DiffReport;
  }

  // ---- editing -----------------------------------------------------------------------------------------------------

  /** Adds the colours, styles and pages of a document spec (or its JSON); its pages are appended. */
  apply(spec: DocumentSpec | string): void {
    check(this.#native, this.#native.documentApply(this.#open(), specJson(spec)));
  }

  /**
   * Fills a template: text by frame name, script label, XML tag or `{{key}}` placeholder; images by the same keys.
   * Returns what was filled and the keys that matched nothing.
   */
  fill(data: FillData): FillReport {
    if (data === null || typeof data !== 'object') {
      throw new IdmlError('data must be an object such as { text: { key: "value" } }.', Status.invalidArgument);
    }
    const json = JSON.stringify(data);
    return this.#json((native, handle, out) => native.documentFill(handle, json, out)) as FillReport;
  }

  /** Replaces text in every story; returns the number of replacements. */
  replaceText(find: string, replacement: string): number {
    requireString(find, 'find');
    requireString(replacement, 'replacement');
    const count: Out<number> = [0];
    check(this.#native, this.#native.documentReplaceText(this.#open(), find, replacement, count));
    return count[0];
  }

  /**
   * Sets the text of one content run (indices from {@link story} or `summary({ includeRuns: true })`), keeping its
   * formatting. A run holding special characters (a page number) is only overwritten with `force`.
   */
  setRunText(storyId: string, runIndex: number, text: string, options: { force?: boolean } = {}): void {
    requireString(storyId, 'storyId');
    requireString(text, 'text');
    requireInt32(runIndex, 'runIndex');
    check(this.#native, this.#native.documentSetRunText(this.#open(), storyId, runIndex, text, options.force ? 1 : 0));
  }

  /**
   * Points a link (its id from `summary().links`) at another file: a URI, a local path or a file: URL. With
   * `refit` and a readable local JPEG or PNG, the image is measured again and fitted to its frame.
   */
  relink(linkId: string, uriOrPath: string | URL, options: { refit?: boolean } = {}): void {
    requireString(linkId, 'linkId');
    const target = uriOrPath instanceof URL ? (uriOrPath.protocol === 'file:' ? fileURLToPath(uriOrPath) : uriOrPath.href) : uriOrPath;
    requireString(target, 'uriOrPath');
    check(this.#native, this.#native.documentRelink(this.#open(), linkId, target, options.refit ? 1 : 0));
  }

  /**
   * Imports XML onto the XML structure, at the root or at the element `at`, with SimpleIDML's semantics
   * (`simpleidml-setcontent`, `-ignorecontent`, `-forcecontent`, `href` for images).
   */
  importXml(xml: string, options: ImportXmlOptions = {}): ImportXmlReport {
    requireString(xml, 'xml');
    const { at, ...rest } = options;
    if (at !== undefined) {
      requireString(at, 'at');
    }
    const json = jsonOrNull(rest);
    return this.#json((native, handle, out) => native.documentImportXml(handle, xml, optionalString(at), optionalString(json), out)) as ImportXmlReport;
  }

  /**
   * Appends pages of `source` (0-based indices; every page when none are given) to this document, remapping every
   * id; styles, swatches and tags are merged once. `source` may be this document. Returns the number of pages added.
   */
  importPages(source: IdmlDocument, pages: readonly number[] = []): number {
    const from = IdmlDocument.#handleOf(source, 'source');
    if (!Array.isArray(pages)) {
      throw new IdmlError('pages must be an array of 0-based page indices.', Status.invalidArgument);
    }
    pages.forEach((page) => requireInt32(page, 'pages'));
    const added: Out<number> = [0];
    const indices = pages.length === 0 ? null : Int32Array.from(pages);
    check(this.#native, this.#native.documentImportPages(this.#open(), from, indices, pages.length, added));
    return added[0];
  }

  // ---- saving ------------------------------------------------------------------------------------------------------

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

  // ---- PDF ---------------------------------------------------------------------------------------------------------

  /**
   * Renders the document to a PDF file (a path or a file: URL). Returns the report: page count, missing fonts and
   * what replaced them, missing images, overset stories, warnings. `onProgress` returning false, or `signal`,
   * cancels after the current page (an IdmlError with `Status.cancelled`).
   */
  exportPdf(path: string | URL, options: PdfExportOptions = {}): PdfExportReport {
    const native = this.#native;
    const handle = this.#open();
    const target = toPath(path);
    const json = optionsJson(options);
    return JSON.parse(withProgress(options, (progress) => exportPdfFile(native, handle, target, json, progress))) as PdfExportReport;
  }

  /** Renders the document to a PDF in memory. Set `documentPath` so that relative links resolve. */
  exportPdfBytes(options: PdfExportOptions = {}): PdfExport {
    const native = this.#native;
    const handle = this.#open();
    const json = optionsJson(options);
    const { pdf, report } = withProgress(options, (progress) => exportPdfBytes(native, handle, json, progress));
    return { pdf, report: JSON.parse(report) as PdfExportReport };
  }

  /**
   * {@link exportPdf} on a worker thread: the event loop stays free. `onProgress` runs on this thread; returning
   * false, or `signal`, cancels at the next page.
   */
  async exportPdfAsync(path: string | URL, options: PdfExportOptions = {}): Promise<PdfExportReport> {
    const result = await this.#inWorker({ kind: 'exportPdfFile', handle: this.#open(), path: toPath(path), optionsJson: optionsJson(options) }, options);
    return JSON.parse(result.report) as PdfExportReport;
  }

  /** {@link exportPdfBytes} on a worker thread; the PDF is moved, not copied, from the worker. */
  async exportPdfBytesAsync(options: PdfExportOptions = {}): Promise<PdfExport> {
    const result = await this.#inWorker({ kind: 'exportPdfBytes', handle: this.#open(), optionsJson: optionsJson(options) }, options);
    return { pdf: result.pdf ?? new Uint8Array(0), report: JSON.parse(result.report) as PdfExportReport };
  }

  // ---- lifetime ----------------------------------------------------------------------------------------------------

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

  /** Makes a call that hands out a JSON string, and parses it. */
  #json(call: (native: Native, handle: number, json: Out<Pointer>) => number): unknown {
    const json: Out<Pointer> = [null];
    check(this.#native, call(this.#native, this.#open(), json));
    return JSON.parse(takeString(this.#native, json[0]));
  }

  async #inWorker(operation: WorkerOperation, control: ProgressOptions): Promise<WorkerResult> {
    busy.add(this);
    try {
      return await runInWorker(this.#native, operation, control);
    } finally {
      busy.delete(this);
    }
  }

  static #handleOf(document: IdmlDocument, name: string): number {
    if (!(typeof document === 'object' && document !== null && #handle in document)) {
      throw new IdmlError(`${name} must be an IdmlDocument.`, Status.invalidArgument);
    }
    return document.#open();
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

function specJson(spec: DocumentSpec | string): string {
  if (typeof spec === 'string') {
    return spec;
  }
  if (spec === null || typeof spec !== 'object') {
    throw new IdmlError('spec must be a document spec object or its JSON.', Status.invalidArgument);
  }
  return JSON.stringify(spec);
}

function pdfSource(source: string | URL | Uint8Array | ArrayBuffer): PdfSource {
  if (typeof source === 'string' || source instanceof URL) {
    return { path: toPath(source) };
  }
  return { bytes: toBytes(source, 'source must be a path, a file: URL, or the bytes of a PDF.') };
}

/** Bytes given as a Uint8Array (a Buffer is one) or an ArrayBuffer; anything else is refused with `message`. */
function toBytes(data: unknown, message: string): Uint8Array {
  const bytes = data instanceof ArrayBuffer ? new Uint8Array(data) : data;
  if (!(bytes instanceof Uint8Array)) {
    throw new IdmlError(message, Status.invalidArgument);
  }
  return bytes;
}

/** Whether two paths name the same file: made absolute, links resolved, and case ignored where file names ignore it. */
function sameFile(a: string, b: string): boolean {
  const canonical = (path: string): string => {
    const full = resolve(path);
    try {
      return realpathSync.native(full);
    } catch {
      return full;
    }
  };
  const [x, y] = [canonical(a), canonical(b)];
  return process.platform === 'win32' || process.platform === 'darwin' ? x.toLowerCase() === y.toLowerCase() : x === y;
}

/** A 32-bit signed integer, as the C API takes (larger numbers would wrap around silently). */
function requireInt32(value: unknown, name: string): void {
  if (!Number.isInteger(value) || (value as number) < -0x8000_0000 || (value as number) > 0x7fff_ffff) {
    throw new IdmlError(`${name} must be a 32-bit integer.`, Status.invalidArgument);
  }
}

/** An options object as JSON, or null (the library's defaults) when it sets nothing. */
function jsonOrNull(options: object): string | null {
  const json = JSON.stringify(options);
  return json === '{}' ? null : json;
}

/** The PDF options sent to the library: everything except the JavaScript-only `onProgress` and `signal`. */
function optionsJson(options: ProgressOptions & Record<string, unknown>): string | null {
  const { onProgress: _onProgress, signal: _signal, ...rest } = options;
  return jsonOrNull(rest);
}

/**
 * Runs a synchronous PDF call, with a progress callback when one is needed. `onProgress` returning false or
 * throwing, or `signal` aborted (from inside `onProgress`: the only JavaScript that runs during the call), cancels.
 */
function withProgress<T>(control: ProgressOptions, call: (progress: NativeProgress | null) => T): T {
  const { onProgress, signal } = control;
  if (signal?.aborted) {
    throw abortedBeforeStart(signal);
  }
  if (!onProgress && !signal) {
    return call(null);
  }
  const progress = progressCallback((done, total) => {
    if (onProgress?.(done, total) === false) {
      return false;
    }
    if (signal?.aborted) {
      throw signal.reason;
    }
    return true;
  });
  try {
    return call(progress.callback);
  } catch (error) {
    const cause = progress.cause();
    if (error instanceof IdmlError && error.code === Status.cancelled && cause !== undefined) {
      throw new IdmlError(error.message, error.code, { cause });
    }
    throw error;
  }
}
