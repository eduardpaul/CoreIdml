// The PDF calls, shared by IdmlDocument (main thread) and the worker of the async variants. Each reads the error
// message on the thread that made the call (the library keeps it per thread) and releases every string and buffer
// it receives exactly once, also when the call fails.

import { Status, type Out, type Pointer, type ProgressCallback as NativeProgress } from './abi.generated.ts';
import { IdmlError, check, freeAll, optionalString, takeBytes, takeString, type Native } from './native.ts';

export type { NativeProgress };

/** Where a PDF to import comes from. */
export type PdfSource = { path: string } | { bytes: Uint8Array };

/** coreidml_document_export_pdf: returns the report JSON. */
export function exportPdfFile(native: Native, handle: number, path: string, optionsJson: string | null, progress: NativeProgress | null): string {
  const report: Out<Pointer> = [null];
  const status = native.documentExportPdf(handle, path, optionalString(optionsJson), progress, null, report);
  try {
    check(native, status);
  } catch (error) {
    freeAll(native, report[0]);
    throw error;
  }
  return takeString(native, report[0]);
}

/** coreidml_document_export_pdf_bytes: returns the PDF and the report JSON. */
export function exportPdfBytes(
  native: Native,
  handle: number,
  optionsJson: string | null,
  progress: NativeProgress | null,
): { pdf: Uint8Array; report: string } {
  const data: Out<Pointer> = [null];
  const length: Out<number | bigint> = [0];
  const report: Out<Pointer> = [null];
  const status = native.documentExportPdfBytes(handle, optionalString(optionsJson), progress, null, data, length, report);
  try {
    check(native, status);
  } catch (error) {
    freeAll(native, data[0], report[0]);
    throw error;
  }
  let pdf: Uint8Array;
  try {
    pdf = takeBytes(native, data[0], length[0]); // releases the buffer, also when copying fails
  } catch (error) {
    freeAll(native, report[0]);
    throw error;
  }
  return { pdf, report: takeString(native, report[0]) };
}

/** coreidml_pdf_import / coreidml_pdf_import_file: returns the new document's handle and the report JSON. */
export function importPdf(native: Native, source: PdfSource, optionsJson: string | null, progress: NativeProgress | null): { handle: number; report: string } {
  const document: Out<number | bigint> = [0];
  const report: Out<Pointer> = [null];
  let status: number;
  if ('path' in source) {
    status = native.pdfImportFile(source.path, optionalString(optionsJson), progress, null, document, report);
  } else {
    if (!(source.bytes instanceof Uint8Array)) {
      throw new IdmlError('source must be a path, a file: URL, or the bytes of a PDF.', Status.invalidArgument);
    }
    status = native.pdfImport(source.bytes, source.bytes.length, optionalString(optionsJson), progress, null, document, report);
  }
  const handle = Number(document[0]);
  try {
    check(native, status);
  } catch (error) {
    freeAll(native, report[0]);
    native.documentClose(handle);
    throw error;
  }
  try {
    return { handle, report: takeString(native, report[0]) };
  } catch (error) {
    native.documentClose(handle);
    throw error;
  }
}

/**
 * A C progress callback over a JavaScript one. koffi passes a function as a transient callback, valid while the call
 * runs and called on the thread that made it, which is what coreidml.h promises ("called on the calling thread").
 * An exception must not reach native code, so it cancels instead and is kept as the cause of the error.
 */
export function progressCallback(shouldContinue: (done: number, total: number) => boolean): { callback: NativeProgress; cause: () => unknown } {
  let cause: unknown;
  const callback: NativeProgress = (done, total) => {
    try {
      return shouldContinue(done, total) ? 0 : 1;
    } catch (error) {
      cause ??= error;
      return 1;
    }
  };
  return { callback, cause: () => cause };
}
