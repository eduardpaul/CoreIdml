/*
 * C smoke test of the native library: proves that include/coreidml.h compiles as C, that a C program links against
 * the library (the import library on Windows, the shared object elsewhere) and that the calls behave.
 *
 *   Linux/macOS:  cc tests/native/smoke.c -Iinclude -Lartifacts/native/<rid> -lcoreidml -o smoke
 *                 LD_LIBRARY_PATH=artifacts/native/<rid> ./smoke tests/fixtures/article-1photo.idml
 *   Windows:      cl tests\native\smoke.c /Iinclude artifacts\native\win-x64\coreidml.lib
 *                 (run it with coreidml.dll next to smoke.exe or on the PATH)
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "coreidml.h"

static int checks = 0;
static int failures = 0;

static void check(int condition, const char* what)
{
    checks++;
    if (!condition)
    {
        failures++;
        const char* message = coreidml_last_error_message();
        printf("FAILED: %s (last error: %s)\n", what, message ? message : "none");
    }
}

static uint8_t* read_file(const char* path, size_t* length)
{
    FILE* file = fopen(path, "rb");
    if (!file)
    {
        return NULL;
    }
    fseek(file, 0, SEEK_END);
    long size = ftell(file);
    fseek(file, 0, SEEK_SET);
    uint8_t* data = (uint8_t*)malloc((size_t)size);
    *length = fread(data, 1, (size_t)size, file);
    fclose(file);
    return data;
}

int main(int argc, char** argv)
{
    if (argc < 2)
    {
        printf("usage: smoke <file.idml>\n");
        return 2;
    }

    check(coreidml_abi_version() == COREIDML_ABI_VERSION, "ABI version matches the header");
    check(coreidml_version() != NULL && strlen(coreidml_version()) > 0, "the library has a version");

    size_t length = 0;
    uint8_t* file = read_file(argv[1], &length);
    check(file != NULL && length > 0, "the fixture is readable");

    coreidml_document document = 0;
    check(coreidml_document_open(file, length, &document) == COREIDML_OK && document != 0, "open from memory");
    free(file);

    char* json = NULL;
    check(coreidml_document_info(document, &json) == COREIDML_OK && json != NULL, "info");
    check(json != NULL && strstr(json, "\"domVersion\":\"10.0\"") != NULL, "info reports the DOM version");
    check(json != NULL && strstr(json, "\"stories\":[") != NULL, "info lists the stories");
    coreidml_free(json);

    uint8_t* part = NULL;
    size_t part_length = 0;
    check(coreidml_document_read_part(document, "mimetype", &part, &part_length) == COREIDML_OK, "read a part");
    check(part_length == 43 && part != NULL && memcmp(part, "application/vnd.adobe.indesign-idml-package", 43) == 0, "the part has its content");
    coreidml_free(part);

    const char* note = "hello from C";
    check(coreidml_document_write_part(document, "Extra/note.txt", (const uint8_t*)note, strlen(note)) == COREIDML_OK, "write a part");

    uint8_t* saved = NULL;
    size_t saved_length = 0;
    check(coreidml_document_save(document, &saved, &saved_length) == COREIDML_OK && saved_length > 0, "save to memory");

    coreidml_document reopened = 0;
    check(coreidml_document_open(saved, saved_length, &reopened) == COREIDML_OK, "open what was saved");
    coreidml_free(saved);
    check(coreidml_document_read_part(reopened, "Extra/note.txt", &part, &part_length) == COREIDML_OK
          && part_length == strlen(note) && memcmp(part, note, part_length) == 0, "the written part survived the round trip");
    coreidml_free(part);
    coreidml_document_close(reopened);

    check(coreidml_document_read_part(document, "Stories/Nope.xml", &part, &part_length) == COREIDML_ERR_NOT_FOUND, "a missing part is NOT_FOUND");
    check(part == NULL && part_length == 0, "out parameters are cleared on failure");
    check(coreidml_last_error_code() == COREIDML_ERR_NOT_FOUND && coreidml_last_error_message() != NULL, "the failure is explained");
    check(coreidml_document_info(document, &json) == COREIDML_OK && coreidml_last_error_code() == COREIDML_OK, "a success clears the last error");
    coreidml_free(json);

    check(coreidml_document_open((const uint8_t*)"nope", 4, &reopened) == COREIDML_ERR_INVALID_FORMAT && reopened == 0, "garbage is INVALID_FORMAT");
    check(coreidml_document_open_file(NULL, &reopened) == COREIDML_ERR_INVALID_ARGUMENT, "a NULL path is INVALID_ARGUMENT");
    check(coreidml_document_open_file("\xff\xfe.idml", &reopened) == COREIDML_ERR_INVALID_ARGUMENT, "a path that is not UTF-8 is INVALID_ARGUMENT");

    coreidml_document_close(document);
    coreidml_document_close(document);
    check(coreidml_document_info(document, &json) == COREIDML_ERR_INVALID_HANDLE, "a closed handle is INVALID_HANDLE");
    coreidml_free(NULL);

    printf("%d checks, %d failed\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
