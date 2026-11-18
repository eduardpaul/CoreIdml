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
 *   - Structured results are JSON with camelCase names.
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

#ifdef __cplusplus
}
#endif

#endif /* COREIDML_H */
