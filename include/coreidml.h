/*
 * coreidml.h - C API of the CoreIdml library (the Native AOT build of src/CoreIdml.Native).
 *
 * Create, read and edit Adobe InDesign IDML files without InDesign.
 *
 * This header is the source of truth for the ABI. The low-level tables of the Node, Python and Dart bindings are
 * generated from it (tools/CoreIdml.AbiGen), and tests/CoreIdml.Abi.Tests fails when it and the exports of the
 * library disagree. To add a function: declare it here, export it in src/CoreIdml.Native/NativeExports.cs, then run
 *   dotnet run --project tools/CoreIdml.AbiGen
 *
 * Conventions
 *   - Functions return COREIDML_OK or a negative COREIDML_ERR_* code; results come back through out parameters,
 *     which are set to NULL / 0 on failure. After a failure, coreidml_last_error_message() explains.
 *   - Strings are UTF-8 and NUL-terminated. Text that is not valid UTF-8 is refused (COREIDML_ERR_INVALID_ARGUMENT).
 *   - Buffers are a pointer and a length in bytes.
 *   - Strings and buffers handed out through out parameters belong to the caller: release them with
 *     coreidml_free(). Strings returned directly (version, last error message) belong to the library.
 *   - A document handle may be used from any thread; calls on one document are serialized. Handles are never
 *     reused, so closing one twice is harmless.
 *   - Structured results are JSON with camelCase names. A JSON argument described as "options" may be NULL or "":
 *     the defaults then apply.
 *   - Long operations (PDF) take a coreidml_progress_callback, which may be NULL.
 */
#ifndef COREIDML_H
#define COREIDML_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define COREIDML_API __declspec(dllimport)
#else
#define COREIDML_API
#endif

/* Bumped on any breaking change to this header. Additions keep it. */
#define COREIDML_ABI_VERSION 1

#define COREIDML_OK 0
/* An unexpected failure; the message names it. */
#define COREIDML_ERR_FAILED (-1)
/* The handle is not an open document. */
#define COREIDML_ERR_INVALID_HANDLE (-2)
/* An argument is missing or malformed. */
#define COREIDML_ERR_INVALID_ARGUMENT (-3)
/* The caller cancelled the operation. */
#define COREIDML_ERR_CANCELLED (-4)
/* The input is not a usable IDML package. */
#define COREIDML_ERR_INVALID_FORMAT (-5)
/* Something the caller named does not exist in the document. */
#define COREIDML_ERR_NOT_FOUND (-6)
/* The document uses something the library does not handle. */
#define COREIDML_ERR_UNSUPPORTED (-7)
/* The operation is not valid for the document as it is now. */
#define COREIDML_ERR_INVALID_OPERATION (-8)
/* Reading or writing a file failed. */
#define COREIDML_ERR_IO (-9)

/* Opaque document handle; 0 is never a document. */
typedef intptr_t coreidml_document;

/* Called on the calling thread after each page with the pages done, the pages in total and the user_data given to
   the call. Return 0 to continue; anything else cancels, and the call fails with COREIDML_ERR_CANCELLED. */
typedef int32_t (*coreidml_progress_callback)(int32_t done, int32_t total, void* user_data);

/* ---- Library ---------------------------------------------------------------------------------------------- */

/* ABI version of the loaded library; compare with COREIDML_ABI_VERSION before calling anything else. */
COREIDML_API int32_t coreidml_abi_version(void);

/* Version of the library, e.g. "0.1.0". Owned by the library. */
COREIDML_API const char* coreidml_version(void);

/* Code of the last call on this thread: COREIDML_OK unless it failed. */
COREIDML_API int32_t coreidml_last_error_code(void);

/* Why the last call on this thread failed, or NULL if it did not. Owned by the library; valid until the next
   call on this thread. */
COREIDML_API const char* coreidml_last_error_message(void);

/* Releases a string or buffer the library handed out. NULL is ignored. */
COREIDML_API void coreidml_free(void* pointer);

/* ---- Documents -------------------------------------------------------------------------------------------- */

/* Opens an .idml file held in memory. The bytes are not kept after the call. */
COREIDML_API int32_t coreidml_document_open(const uint8_t* data, size_t length, coreidml_document* document);

/* Opens an .idml file. */
COREIDML_API int32_t coreidml_document_open_file(const char* path, coreidml_document* document);

/* Describes the document as JSON:
     {"domVersion":"7.5", "parts":[{"path":"designmap.xml","length":16154,"modified":false}, ...],
      "spreads":["Spreads/Spread_ud6.xml"], "masterSpreads":[...], "stories":[...]} */
COREIDML_API int32_t coreidml_document_info(coreidml_document document, char** json);

/* Reads one part of the package (e.g. "Stories/Story_u188.xml") as it would be written now. */
COREIDML_API int32_t coreidml_document_read_part(coreidml_document document, const char* path, uint8_t** data, size_t* length);

/* Adds or replaces one part of the package. The bytes are copied. */
COREIDML_API int32_t coreidml_document_write_part(coreidml_document document, const char* path, const uint8_t* data, size_t length);

/* Saves the document as the bytes of an .idml file. Parts that were not changed are written byte for byte. */
COREIDML_API int32_t coreidml_document_save(coreidml_document document, uint8_t** data, size_t* length);

/* Saves the document as an .idml file. */
COREIDML_API int32_t coreidml_document_save_file(coreidml_document document, const char* path);

/* Releases a document. Closing a handle twice, or 0, does nothing. */
COREIDML_API void coreidml_document_close(coreidml_document document);

/* ---- Creating documents ----------------------------------------------------------------------------------- */

/* Creates a document from a JSON document spec (page size, colours, styles, pages of text, images, tables and
   shapes; see the README). NULL or "" creates one blank A4 page. */
COREIDML_API int32_t coreidml_document_create(const char* spec_json, coreidml_document* document);

/* Adds the colours, styles and pages of a JSON document spec to the document; its pages are appended. */
COREIDML_API int32_t coreidml_document_apply(coreidml_document document, const char* spec_json);

/* ---- Reading ---------------------------------------------------------------------------------------------- */

/* A JSON summary for programs and agents: pages, frames with positions and text, stories, links, styles, swatches,
   tags. Options: {"maxTextLength":200, "includeRuns":false, "includeResources":true, "pages":[0,2],
   "sections":["pages","stories","links","offPage","resources"]}. */
COREIDML_API int32_t coreidml_document_summary(coreidml_document document, const char* options_json, char** json);

/* One story in full as JSON: its text, frames, and every content run with its index, styles and scope. */
COREIDML_API int32_t coreidml_document_story(coreidml_document document, const char* story_id, char** json);

/* The XML structure as a JSON tree (tag, id, path, boundTo, attributes, text, children); "null" without one. */
COREIDML_API int32_t coreidml_document_xml_structure(coreidml_document document, char** json);

/* Exports the XML structure, or the subtree at the path at (e.g. "/Root/article[2]"; NULL for all), as XML. */
COREIDML_API int32_t coreidml_document_export_xml(coreidml_document document, const char* at, char** xml);

/* Checks references, ids, layers, threads and XML bindings: {"isValid":true, "issues":[...]}. */
COREIDML_API int32_t coreidml_document_validate(coreidml_document document, char** json);

/* What changed between two documents, as JSON: stories run by run, links, page count. */
COREIDML_API int32_t coreidml_document_diff(coreidml_document before, coreidml_document after, char** json);

/* ---- Editing ---------------------------------------------------------------------------------------------- */

/* Fills a template from JSON fill data: {"text":{"key":"value"}, "images":{"key":"/path/photo.jpg"}}. Keys match
   frame names, script labels, XML tags and {{placeholders}}. Receives a JSON report (filled, missing). */
COREIDML_API int32_t coreidml_document_fill(coreidml_document document, const char* data_json, char** report_json);

/* Replaces text in every story; count receives the number of replacements. */
COREIDML_API int32_t coreidml_document_replace_text(coreidml_document document, const char* find, const char* replacement, int32_t* count);

/* Sets the text of content run run_index of a story (indices from coreidml_document_story), keeping its formatting.
   A run holding special characters (a page number) is only overwritten when force is not 0. */
COREIDML_API int32_t coreidml_document_set_run_text(coreidml_document document, const char* story_id, int32_t run_index, const char* text, int32_t force);

/* Points the link link_id (from the summary's "links") at another file: a URI or a local path. With refit not 0
   and a readable local JPEG or PNG, the image is measured again and fitted to its frame. */
COREIDML_API int32_t coreidml_document_relink(coreidml_document document, const char* link_id, const char* uri_or_path, int32_t refit);

/* Imports XML onto the structure element at the path at (NULL for the root), with SimpleIDML's semantics
   (simpleidml-setcontent, -ignorecontent, -forcecontent, href). Options: {"fit":"fill",
   "characterStyles":{"b":"Bold"}, "relinkOnly":false}. Receives a JSON report (updated, added, deleted,
   unmatched, warnings). */
COREIDML_API int32_t coreidml_document_import_xml(coreidml_document document, const char* xml, const char* at, const char* options_json, char** report_json);

/* Appends pages of source to the document, remapping every id; styles, swatches and tags are merged once.
   pages lists 0-based page indices (count 0: every page). added receives the number of pages added.
   source may be the document itself. */
COREIDML_API int32_t coreidml_document_import_pages(coreidml_document document, coreidml_document source, const int32_t* pages, size_t count, int32_t* added);

/* ---- PDF -------------------------------------------------------------------------------------------------- */

/* Renders the document to a PDF file. Options: {"pages":[1,2], "fontDirectories":["/fonts"], "useSystemFonts":true,
   "substitutions":{"Minion Pro":"Liberation Serif"}, "linkDirectories":["/links"], "linkRemap":{"a.jpg":"/b.jpg"},
   "documentPath":"/docs/a.idml", "maxImageDpi":150, "jpegQuality":85}. Receives a JSON report: pageCount,
   missingFonts, missingImages, oversetStories, warnings. */
COREIDML_API int32_t coreidml_document_export_pdf(coreidml_document document, const char* path, const char* options_json, coreidml_progress_callback progress, void* user_data, char** report_json);

/* Renders the document to a PDF in memory. Options and report as for coreidml_document_export_pdf. */
COREIDML_API int32_t coreidml_document_export_pdf_bytes(coreidml_document document, const char* options_json, coreidml_progress_callback progress, void* user_data, uint8_t** data, size_t* length, char** report_json);

/* Builds an editable document from a PDF held in memory: text frames with inferred styles, image frames, shapes.
   Options: {"pages":[1], "imageDirectory":"/links", "preserveLineBreaks":false, "importShapes":true,
   "fontDirectories":["/fonts"]}. Images are written to imageDirectory, since IDML links images. Receives a JSON
   report: frames, images, fonts (and whether each is available), warnings. The bytes are not kept. */
COREIDML_API int32_t coreidml_pdf_import(const uint8_t* data, size_t length, const char* options_json, coreidml_progress_callback progress, void* user_data, coreidml_document* document, char** report_json);

/* coreidml_pdf_import from a PDF file. */
COREIDML_API int32_t coreidml_pdf_import_file(const char* path, const char* options_json, coreidml_progress_callback progress, void* user_data, coreidml_document* document, char** report_json);

#ifdef __cplusplus
}
#endif

#endif /* COREIDML_H */
