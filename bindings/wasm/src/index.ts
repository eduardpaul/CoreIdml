import { ABI_VERSION, Status, type StatusCode } from './abi.generated.ts';
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
  PdfExportReport,
  PdfImportOptions,
  PdfImportReport,
  ProgressOptions,
  StorySummary,
  SummaryOptions,
  ValidationReport,
  XmlNode,
} from './types.ts';

export { ABI_VERSION, Status };
export type { StatusCode };
export type * from './types.ts';

/** Where {@link CoreIdml.addFonts} writes, in the engine's in-memory file system. */
export const FONTS_DIRECTORY = '/fonts';
/** Where {@link CoreIdml.addLinks} writes. */
export const LINKS_DIRECTORY = '/links';

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

/** What {@link CoreIdml.importPdf} returns: the document, its report, and the images it extracted. */
export interface PdfImport {
  document: IdmlDocument;
  report: PdfImportReport;
  /** The folder of the in-memory file system the images were written to; the document's links point there. */
  imageDirectory: string;
  /** The extracted images by file name. */
  images: Record<string, Uint8Array>;
}

type Progress = ((done: number, total: number) => boolean) | null;

/** The [JSExport] functions of src/CoreIdml.Wasm/WasmExports.cs. */
interface WasmExports {
  AbiVersion(): number;
  Version(): string;
  LastErrorCode(): number;
  LastErrorMessage(): string | null;
  TakeReport(): string | null;
  DocumentOpen(data: Uint8Array): number;
  DocumentInfo(document: number): string;
  DocumentReadPart(document: number, path: string): Uint8Array;
  DocumentWritePart(document: number, path: string, data: Uint8Array): void;
  DocumentSave(document: number): Uint8Array;
  DocumentClose(document: number): void;
  DocumentCreate(spec: string | null): number;
  DocumentApply(document: number, spec: string): void;
  DocumentSummary(document: number, options: string | null): string;
  DocumentStory(document: number, storyId: string): string;
  DocumentXmlStructure(document: number): string;
  DocumentExportXml(document: number, at: string | null): string;
  DocumentValidate(document: number): string;
  DocumentDiff(before: number, after: number): string;
  DocumentFill(document: number, data: string): string;
  DocumentReplaceText(document: number, find: string, replacement: string): number;
  DocumentSetRunText(document: number, storyId: string, runIndex: number, text: string, force: boolean): void;
  DocumentRelink(document: number, linkId: string, uriOrPath: string, refit: boolean): void;
  DocumentImportXml(document: number, xml: string, at: string | null, options: string | null): string;
  DocumentImportPages(document: number, source: number, pages: number[]): number;
  DocumentExportPdfBytes(document: number, options: string | null, progress: Progress): Uint8Array;
  PdfImport(data: Uint8Array, options: string | null, progress: Progress): number;
  FileWrite(path: string, data: Uint8Array): void;
  FileRead(path: string): Uint8Array;
  FileList(directory: string): string[];
  FileExists(path: string): boolean;
  FileDelete(path: string): void;
}

// One .NET runtime per JavaScript realm: loading it twice is not supported, and there is nothing to gain.
let loading: { url: string; library: Promise<CoreIdml> } | undefined;

// Documents that are never closed are released when they are garbage collected. Handles are never reused by the
// library, so a late finalizer cannot close another document.
const finalizer = new FinalizationRegistry<{ exports: WasmExports; handle: number; folder: string | null }>(({ exports, handle, folder }) => {
  try {
    exports.DocumentClose(handle);
  } catch {
    // Nothing useful to do while finalizing.
  }
  if (folder !== null) {
    removeQuietly(exports, folder);
  }
});

/** Removes a folder of the in-memory file system made for a document; a failure leaves it (it is only memory). */
function removeQuietly(exports: WasmExports, folder: string): void {
  try {
    exports.FileDelete(folder);
  } catch {
    // Nothing useful to do.
  }
}

/**
 * The loaded engine. Get it with {@link CoreIdml.load}, once; then every call is synchronous and runs on the thread
 * that loaded it. In a page, run long calls (PDF) in a worker: `IdmlWorker` from `@coreidml/wasm/worker` does that.
 */
export class CoreIdml {
  readonly #exports: WasmExports;
  #imports = 0;

  /** Version of the library. */
  readonly version: string;

  private constructor(exports: WasmExports) {
    this.#exports = exports;
    this.version = exports.Version();
  }

  /** Downloads and starts the WebAssembly build. Later calls return the same instance. */
  static load(options: LoadOptions = {}): Promise<CoreIdml> {
    let base: URL;
    try {
      base = frameworkBase(options.frameworkUrl);
    } catch (error) {
      return Promise.reject(error);
    }
    const url = base.href;
    if (loading) {
      if (loading.url !== url) {
        return Promise.reject(new IdmlError(`CoreIdml is already loaded from ${loading.url}, not ${url}; a page can use only one build.`, Status.invalidOperation));
      }
      return loading.library;
    }
    const library = CoreIdml.#start(base);
    loading = { url, library };
    // A failed load may be retried (a network error, a wrong URL).
    library.catch(() => {
      if (loading?.library === library) {
        loading = undefined;
      }
    });
    return library;
  }

  static async #start(base: URL): Promise<CoreIdml> {
    const url = base.href;
    // dotnet.js inside the folder, keeping the folder URL's query (a cache-busting version, a signed URL).
    const script = new URL('dotnet.js', base);
    script.search = base.search;
    let exports: WasmExports;
    try {
      const { dotnet } = await import(/* webpackIgnore: true */ /* @vite-ignore */ script.href);
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

  // ---- documents ------------------------------------------------------------------------------------------------

  /** Opens the bytes of an .idml file. They are copied; the document does not keep them. */
  open(data: Uint8Array | ArrayBuffer): IdmlDocument {
    const bytes = toBytes(data, 'data must be a Uint8Array or an ArrayBuffer holding an .idml file.');
    return createDocument(this.#exports, call(this.#exports, () => this.#exports.DocumentOpen(bytes)));
  }

  /**
   * A new document from a document spec (page size, colours, styles, pages of items), as an object or JSON text;
   * without one, a blank A4 page.
   */
  create(spec?: DocumentSpec | string): IdmlDocument {
    const json = spec === undefined || spec === null ? null : specJson(spec);
    return createDocument(this.#exports, call(this.#exports, () => this.#exports.DocumentCreate(json)));
  }

  /**
   * Rebuilds an editable document from the bytes of a PDF: text frames with inferred styles, image frames, shapes.
   * IDML links its images, so the extracted images are written to `imageDirectory`, where the document's links point,
   * and returned as bytes. The default, a new `/import/<n>` folder, is removed when the document is closed.
   */
  importPdf(data: Uint8Array | ArrayBuffer, options: PdfImportOptions = {}): PdfImport {
    const bytes = toBytes(data, 'data must be a Uint8Array or an ArrayBuffer holding a PDF file.');
    const exports = this.#exports;
    let imageDirectory = options.imageDirectory;
    // A folder made here belongs to the document: it is removed when the document is closed or collected, or at once
    // when the import fails. A folder the caller named is the caller's.
    let owned: string | null = null;
    if (imageDirectory === undefined) {
      do {
        imageDirectory = `/import/${++this.#imports}`;
      } while (exports.FileExists(imageDirectory));
      owned = imageDirectory;
    }
    const json = optionsJson({ ...options, ...folderDefaults(exports, options, false), imageDirectory });
    let handle: number;
    try {
      handle = withProgress(options, (progress) => call(exports, () => exports.PdfImport(bytes, json, progress)));
    } catch (error) {
      if (owned !== null) {
        removeQuietly(exports, owned);
      }
      throw error;
    }
    const document = createDocument(exports, handle, owned);
    const report = JSON.parse(exports.TakeReport() ?? '{}') as PdfImportReport;
    const images: Record<string, Uint8Array> = {};
    for (const path of report.images ?? []) {
      images[baseName(path)] = call(exports, () => exports.FileRead(path));
    }
    return { document, report, imageDirectory, images };
  }

  // ---- the in-memory file system ----------------------------------------------------------------------------------

  /**
   * Font files (TrueType, OpenType, collections) by file name, into `/fonts`. PDF calls look there unless their
   * options name `fontDirectories`. A browser has no system fonts: give the engine the fonts your documents use.
   */
  addFonts(fonts: Record<string, Uint8Array | ArrayBuffer>): void {
    this.#addFiles(FONTS_DIRECTORY, fonts);
  }

  /** Placed images and PDFs by file name, into `/links`. PDF export finds a document's links there by file name. */
  addLinks(files: Record<string, Uint8Array | ArrayBuffer>): void {
    this.#addFiles(LINKS_DIRECTORY, files);
  }

  /** Writes a file of the in-memory file system, creating its folders. Text is written as UTF-8. */
  writeFile(path: string, data: Uint8Array | ArrayBuffer | string): void {
    requireString(path, 'path');
    const bytes = typeof data === 'string' ? new TextEncoder().encode(data) : toBytes(data, 'data must be a Uint8Array, an ArrayBuffer or a string.');
    call(this.#exports, () => this.#exports.FileWrite(path, bytes));
  }

  /** Reads a file of the in-memory file system. */
  readFile(path: string): Uint8Array {
    requireString(path, 'path');
    return call(this.#exports, () => this.#exports.FileRead(path));
  }

  /** The names in a folder of the in-memory file system, sorted; empty when it does not exist. */
  listFiles(directory: string): string[] {
    requireString(directory, 'directory');
    return call(this.#exports, () => this.#exports.FileList(directory));
  }

  /** Removes a file, or a folder with everything in it. Nothing happens when it does not exist. */
  remove(path: string): void {
    requireString(path, 'path');
    call(this.#exports, () => this.#exports.FileDelete(path));
  }

  #addFiles(directory: string, files: Record<string, Uint8Array | ArrayBuffer>): void {
    if (files === null || typeof files !== 'object') {
      throw new IdmlError('files must be an object of file names to bytes.', Status.invalidArgument);
    }
    for (const [name, data] of Object.entries(files)) {
      this.writeFile(`${directory}/${baseName(name)}`, toBytes(data, `${name}: the bytes must be a Uint8Array or an ArrayBuffer.`));
    }
  }

}

/**
 * The framework folder as an absolute URL whose path ends in '/', its query kept. A relative URL is relative to the
 * page or worker (as in IdmlWorker.start), or to this module where there is no location (Node).
 */
function frameworkBase(frameworkUrl: string | URL | undefined): URL {
  if (frameworkUrl === undefined || frameworkUrl === null) {
    return new URL('./_framework/', import.meta.url);
  }
  let url: URL;
  try {
    url = new URL(frameworkUrl, globalThis.location?.href ?? import.meta.url);
  } catch (error) {
    throw new IdmlError(`frameworkUrl is not a valid URL: ${String(frameworkUrl)}`, Status.invalidArgument, { cause: error });
  }
  url.hash = '';
  if (!url.pathname.endsWith('/')) {
    url.pathname += '/';
  }
  return url;
}

/** The folders addFonts (and, for an export, addLinks) fill, when they have files and the options name none. */
function folderDefaults(exports: WasmExports, options: { fontDirectories?: unknown; linkDirectories?: unknown }, links: boolean): object {
  return {
    ...(options.fontDirectories === undefined && exports.FileList(FONTS_DIRECTORY).length > 0 ? { fontDirectories: [FONTS_DIRECTORY] } : {}),
    ...(links && options.linkDirectories === undefined && exports.FileList(LINKS_DIRECTORY).length > 0 ? { linkDirectories: [LINKS_DIRECTORY] } : {}),
  };
}

// Documents are created by CoreIdml only; IdmlDocument hands its private constructor to this module.
let createDocument: (exports: WasmExports, handle: number, folder?: string | null) => IdmlDocument;

/**
 * An InDesign document held by the engine. Close it with {@link close} or `using`; a document that is forgotten
 * is released when it is garbage collected.
 */
export class IdmlDocument implements Disposable {
  readonly #exports: WasmExports;
  #handle: number;
  /** A folder of the in-memory file system that goes with the document (the images of a PDF import). */
  readonly #folder: string | null;

  private constructor(exports: WasmExports, handle: number, folder: string | null) {
    this.#exports = exports;
    this.#handle = handle;
    this.#folder = folder;
    finalizer.register(this, { exports, handle, folder }, this);
  }

  static {
    createDocument = (exports, handle, folder = null) => new IdmlDocument(exports, handle, folder);
  }

  /** Whether {@link close} was called. */
  get closed(): boolean {
    return this.#handle === 0;
  }

  // ---- parts ------------------------------------------------------------------------------------------------------

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

  // ---- reading ----------------------------------------------------------------------------------------------------

  /** A JSON summary for programs and agents: pages, frames with positions and text, stories, links, styles, swatches. */
  summary(options: SummaryOptions = {}): DocumentSummary {
    const json = optionsJson(options);
    return JSON.parse(this.#call((handle) => this.#exports.DocumentSummary(handle, json))) as DocumentSummary;
  }

  /** One story in full: its text, frames, and every content run with its index, styles and scope. */
  story(storyId: string): StorySummary {
    requireString(storyId, 'storyId');
    return JSON.parse(this.#call((handle) => this.#exports.DocumentStory(handle, storyId))) as StorySummary;
  }

  /** The XML structure as a tree (tag, id, path, boundTo, attributes, text, children); `null` without one. */
  xmlStructure(): XmlNode | null {
    return JSON.parse(this.#call((handle) => this.#exports.DocumentXmlStructure(handle))) as XmlNode | null;
  }

  /** The XML structure, or the subtree at `at` (e.g. `/Root/article[2]`), as XML. */
  exportXml(options: { at?: string } = {}): string {
    if (options.at !== undefined && options.at !== null) {
      requireString(options.at, 'at');
    }
    const at = options.at ?? null;
    return this.#call((handle) => this.#exports.DocumentExportXml(handle, at));
  }

  /** Checks references, ids, layers, threads and XML bindings. */
  validate(): ValidationReport {
    return JSON.parse(this.#call((handle) => this.#exports.DocumentValidate(handle))) as ValidationReport;
  }

  /** What changed from this document to `other`: stories run by run, links, page count. */
  diff(other: IdmlDocument): DiffReport {
    const after = IdmlDocument.#handleOf(other, 'other');
    return JSON.parse(this.#call((handle) => this.#exports.DocumentDiff(handle, after))) as DiffReport;
  }

  // ---- editing ----------------------------------------------------------------------------------------------------

  /** Adds the colours, styles and pages of a document spec (an object or JSON text); its pages are appended. */
  apply(spec: DocumentSpec | string): void {
    const json = specJson(spec);
    this.#call((handle) => this.#exports.DocumentApply(handle, json));
  }

  /** Fills a template: keys match frame names, script labels, XML tags and `{{placeholders}}`. */
  fill(data: FillData = {}): FillReport {
    if (data === null || typeof data !== 'object') {
      throw new IdmlError('data must be an object such as { text: { key: "value" } }.', Status.invalidArgument);
    }
    const json = JSON.stringify(data);
    return JSON.parse(this.#call((handle) => this.#exports.DocumentFill(handle, json))) as FillReport;
  }

  /** Replaces text in every story; returns the number of replacements. */
  replaceText(find: string, replacement: string): number {
    requireString(find, 'find');
    requireString(replacement, 'replacement');
    return this.#call((handle) => this.#exports.DocumentReplaceText(handle, find, replacement));
  }

  /**
   * Sets the text of one content run (indices from {@link story} or `summary({ includeRuns: true })`), keeping its
   * formatting. A run holding special characters (a page number) is only overwritten with `force`.
   */
  setRunText(storyId: string, runIndex: number, text: string, options: { force?: boolean } = {}): void {
    requireString(storyId, 'storyId');
    requireString(text, 'text');
    requireInteger(runIndex, 'runIndex');
    this.#call((handle) => this.#exports.DocumentSetRunText(handle, storyId, runIndex, text, options.force === true));
  }

  /**
   * Points a link (an id from the summary's `links`) at another file: a URI or a path of the in-memory file system.
   * With `refit` and a JPEG or PNG there, the image is measured again and fitted to its frame.
   */
  relink(linkId: string, uriOrPath: string, options: { refit?: boolean } = {}): void {
    requireString(linkId, 'linkId');
    requireString(uriOrPath, 'uriOrPath');
    this.#call((handle) => this.#exports.DocumentRelink(handle, linkId, uriOrPath, options.refit === true));
  }

  /** Imports XML onto the structure (at the root, or at `at`) with SimpleIDML's semantics. */
  importXml(xml: string, options: ImportXmlOptions = {}): ImportXmlReport {
    requireString(xml, 'xml');
    const { at, ...rest } = options;
    if (at !== undefined && at !== null) {
      requireString(at, 'at');
    }
    const json = optionsJson(rest);
    return JSON.parse(this.#call((handle) => this.#exports.DocumentImportXml(handle, xml, at ?? null, json))) as ImportXmlReport;
  }

  /**
   * Appends pages of `source` (0-based indices; default every page), remapping every id; styles, swatches and tags
   * are merged once. `source` may be this document. Returns the number of pages added.
   */
  importPages(source: IdmlDocument, pages: readonly number[] = []): number {
    const from = IdmlDocument.#handleOf(source, 'source');
    if (!Array.isArray(pages)) {
      throw new IdmlError('pages must be an array of page indices.', Status.invalidArgument);
    }
    pages.forEach((page) => requireInteger(page, 'pages'));
    return this.#call((handle) => this.#exports.DocumentImportPages(handle, from, [...pages]));
  }

  // ---- PDF --------------------------------------------------------------------------------------------------------

  /**
   * Renders the document to a PDF. Fonts and placed images come from the in-memory file system: by default from
   * the folders {@link CoreIdml.addFonts} and {@link CoreIdml.addLinks} fill. `onProgress` is called after each
   * page; returning `false` (or an aborted `signal`) cancels, and the call fails with `Status.cancelled`.
   */
  exportPdfBytes(options: PdfExportOptions = {}): PdfExport {
    const exports = this.#exports;
    const json = optionsJson({ ...options, ...folderDefaults(exports, options ?? {}, true) });
    const pdf = withProgress(options ?? {}, (progress) => this.#call((handle) => exports.DocumentExportPdfBytes(handle, json, progress)));
    const report = JSON.parse(exports.TakeReport() ?? '{}') as PdfExportReport;
    return { pdf, report };
  }

  // ---- lifetime ---------------------------------------------------------------------------------------------------

  /** Releases the document. Calling it again does nothing. */
  close(): void {
    if (this.#handle !== 0) {
      const handle = this.#handle;
      this.#handle = 0;
      finalizer.unregister(this);
      try {
        this.#exports.DocumentClose(handle);
      } finally {
        if (this.#folder !== null) {
          removeQuietly(this.#exports, this.#folder);
        }
      }
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

  static #handleOf(document: IdmlDocument, name: string): number {
    if (!(document instanceof IdmlDocument)) {
      throw new IdmlError(`${name} must be an IdmlDocument.`, Status.invalidArgument);
    }
    if (document.#handle === 0) {
      throw new IdmlError(`${name}: the document is closed.`, Status.invalidHandle);
    }
    return document.#handle;
  }
}

/**
 * Runs a call into the library; a failure becomes an {@link IdmlError}. A failure the library recorded arrives with
 * the library's message and gets its status code. Anything else (an argument JavaScript could not pass) never reached
 * the library, whose last error then belongs to an earlier call: it is reported as it is, with `Status.failed`.
 */
function call<T>(exports: WasmExports, action: () => T): T {
  try {
    return action();
  } catch (error) {
    if (error instanceof IdmlError) {
      throw error;
    }
    const code = exports.LastErrorCode();
    const message = exports.LastErrorMessage();
    if (code !== Status.ok && message !== null && error instanceof Error && error.message === message) {
      throw new IdmlError(message, code as StatusCode, { cause: error });
    }
    throw new IdmlError(messageOf(error), Status.failed, { cause: error });
  }
}

/**
 * Runs a PDF call with a progress function when there is a callback or a signal. The callback returning false,
 * throwing, or the signal aborting cancels; the call then fails with `Status.cancelled`, and an exception thrown by
 * the callback (or the abort reason) is the error's cause. A signal aborted before the call cancels it at once.
 */
function withProgress<T>(control: ProgressOptions, action: (progress: Progress) => T): T {
  const { onProgress, signal } = control;
  if (onProgress !== undefined && onProgress !== null && typeof onProgress !== 'function') {
    throw new IdmlError('onProgress must be a function.', Status.invalidArgument);
  }
  if (signal?.aborted) {
    throw new IdmlError('The operation was aborted before it started.', Status.cancelled, { cause: signal.reason });
  }
  if (!onProgress && !signal) {
    return action(null);
  }
  let thrown: { error: unknown } | undefined;
  const progress = (done: number, total: number): boolean => {
    try {
      if (onProgress?.(done, total) === false) {
        return false;
      }
      if (signal?.aborted) {
        throw signal.reason;
      }
      return true;
    } catch (error) {
      thrown ??= { error };
      return false;
    }
  };
  try {
    return action(progress);
  } catch (error) {
    if (thrown && error instanceof IdmlError && error.code === Status.cancelled) {
      throw new IdmlError(error.message, error.code, { cause: thrown.error });
    }
    throw error;
  }
}

/** A document spec as JSON: objects are serialized, text is passed as it is. */
function specJson(spec: DocumentSpec | string): string {
  if (typeof spec === 'string') {
    return spec;
  }
  if (spec === null || typeof spec !== 'object') {
    throw new IdmlError('spec must be an object or JSON text.', Status.invalidArgument);
  }
  return JSON.stringify(spec);
}

/** The options sent to the library: everything but the callbacks and signals that only JavaScript understands. */
function optionsJson(options: object | undefined | null): string | null {
  if (options === undefined || options === null) {
    return null;
  }
  if (typeof options !== 'object') {
    throw new IdmlError('options must be an object.', Status.invalidArgument);
  }
  const { onProgress: _onProgress, signal: _signal, ...rest } = options as Record<string, unknown>;
  const defined = Object.fromEntries(Object.entries(rest).filter(([, value]) => value !== undefined));
  return Object.keys(defined).length === 0 ? null : JSON.stringify(defined);
}

function toBytes(data: Uint8Array | ArrayBuffer, message: string): Uint8Array {
  const bytes = data instanceof ArrayBuffer ? new Uint8Array(data) : data;
  if (!(bytes instanceof Uint8Array)) {
    throw new IdmlError(message, Status.invalidArgument);
  }
  return bytes;
}

function baseName(name: string): string {
  const base = String(name).slice(Math.max(name.lastIndexOf('/'), name.lastIndexOf('\\')) + 1);
  if (base.length === 0 || base === '.' || base === '..') {
    throw new IdmlError(`Not a file name: ${name}`, Status.invalidArgument);
  }
  return base;
}

function requireString(value: unknown, name: string): void {
  if (typeof value !== 'string') {
    throw new IdmlError(`${name} must be a string.`, Status.invalidArgument);
  }
}

function requireInteger(value: unknown, name: string): void {
  if (!Number.isInteger(value) || (value as number) < -0x8000_0000 || (value as number) > 0x7fff_ffff) {
    throw new IdmlError(`${name} must be a 32-bit integer.`, Status.invalidArgument);
  }
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
