/*
 * C smoke test of the native library: proves that include/coreidml.h compiles as C, that a C program links against
 * the library (the import library on Windows, the shared object elsewhere) and that the calls behave.
 *
 *   Linux/macOS:  cc tests/native/smoke.c -Iinclude -Lartifacts/native/<rid> -lcoreidml -o smoke
 *                 LD_LIBRARY_PATH=artifacts/native/<rid> ./smoke tests/fixtures/article-1photo.idml [out-dir]
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

/* Counts its calls; cancels after the first page when *user_data is not 0. */
static int progress_calls = 0;

static int32_t on_progress(int32_t done, int32_t total, void* user_data)
{
    progress_calls++;
    return (user_data != NULL && *(int*)user_data != 0 && done >= 1 && total >= 1) ? 1 : 0;
}

static int contains(const char* text, const char* part)
{
    return text != NULL && strstr(text, part) != NULL;
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
        printf("usage: smoke <file.idml> [out-dir]\n");
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

    /* ---- Creating, filling and editing ---------------------------------------------------------------------- */

    const char* out_dir = argc > 2 ? argv[2] : ".";
    char path[1024];
    snprintf(path, sizeof path, "%s/smoke.idml", out_dir);

    const char* spec =
        "{ \"unit\": \"mm\", \"page\": { \"size\": \"A5\" },"
        "  \"colors\": [ { \"name\": \"Brand\", \"cmyk\": [0, 90, 85, 0] } ],"
        "  \"paragraphStyles\": [ { \"name\": \"Title\", \"size\": 18, \"color\": \"Brand\" } ],"
        "  \"pages\": [ { \"items\": ["
        "    { \"type\": \"text\", \"name\": \"title\", \"x\": 10, \"y\": 10, \"width\": 128, \"height\": 12, \"text\": \"Sheet {{code}}\", \"style\": \"Title\" },"
        "    { \"type\": \"text\", \"name\": \"client\", \"x\": 10, \"y\": 30, \"width\": 128, \"height\": 12, \"text\": \"-\" }"
        "  ] } ] }";
    coreidml_document doc = 0;
    check(coreidml_document_create(spec, &doc) == COREIDML_OK && doc != 0, "create from a JSON spec");
    coreidml_document blank = 0;
    check(coreidml_document_create(NULL, &blank) == COREIDML_OK && blank != 0, "create a blank document");
    coreidml_document_close(blank);

    char* report = NULL;
    check(coreidml_document_fill(doc, "{ \"text\": { \"code\": \"F-001\", \"client\": \"ACME\" } }", &report) == COREIDML_OK
          && contains(report, "\"missing\":[]"), "fill reports nothing missing");
    coreidml_free(report);

    int32_t count = 0;
    check(coreidml_document_replace_text(doc, "ACME", "ACME Inc.", &count) == COREIDML_OK && count == 1, "replace text counts one replacement");

    char* summary = NULL;
    check(coreidml_document_summary(doc, NULL, &summary) == COREIDML_OK && contains(summary, "Sheet F-001") && contains(summary, "ACME Inc."), "the summary shows the filled text");
    char story_id[64] = {0};
    const char* found = summary ? strstr(summary, "\"storyId\":\"") : NULL;
    if (found)
    {
        sscanf(found + 11, "%63[^\"]", story_id);
    }
    coreidml_free(summary);

    check(coreidml_document_summary(doc, "{\"maxTextLength\":0,\"includeResources\":false,\"includeRuns\":true}", &summary) == COREIDML_OK
          && contains(summary, "\"runs\"") && !contains(summary, "\"paragraphStyles\""), "summary options are honoured");
    coreidml_free(summary);

    check(coreidml_document_validate(doc, &json) == COREIDML_OK && contains(json, "\"isValid\":true"), "the document validates");
    coreidml_free(json);

    check(coreidml_document_story(doc, story_id, &json) == COREIDML_OK && contains(json, "\"index\":0"), "a story lists its indexed runs");
    coreidml_free(json);
    check(coreidml_document_set_run_text(doc, story_id, 0, "Sheet edited", 0) == COREIDML_OK, "set the text of one run");
    check(coreidml_document_set_run_text(doc, story_id, 99, "x", 0) == COREIDML_ERR_NOT_FOUND
          && contains(coreidml_last_error_message(), "run index 99"), "a bad run index is NOT_FOUND and says which");
    check(coreidml_document_relink(doc, "u-none", "/x.jpg", 0) == COREIDML_ERR_NOT_FOUND, "an unknown link is NOT_FOUND");

    check(coreidml_document_save_file(doc, path) == COREIDML_OK, "save to a file");
    coreidml_document from_file = 0;
    check(coreidml_document_open_file(path, &from_file) == COREIDML_OK, "open the saved file");

    /* SimpleIDML-style XML: a tagged frame, then import and export. */
    check(coreidml_document_apply(doc, "{\"pages\":[{\"items\":[{\"type\":\"text\",\"tag\":\"note\",\"x\":36,\"y\":36,\"width\":200,\"height\":40,\"text\":\"-\"}]}]}") == COREIDML_OK,
          "apply a spec with a tagged frame");
    check(coreidml_document_import_xml(doc, "<Root><note>Imported</note></Root>", NULL, NULL, &report) == COREIDML_OK
          && contains(report, "\"unmatched\":[]"), "import XML");
    coreidml_free(report);
    char* xml = NULL;
    check(coreidml_document_export_xml(doc, "/Root/note", &xml) == COREIDML_OK && xml != NULL && strcmp(xml, "<note>Imported</note>") == 0, "export an XML subtree");
    coreidml_free(xml);
    check(coreidml_document_xml_structure(doc, &json) == COREIDML_OK && contains(json, "\"path\":\"/Root/note\""), "the XML structure as a tree");
    coreidml_free(json);

    /* Composition and diff. */
    int32_t added = 0;
    check(coreidml_document_import_pages(doc, from_file, NULL, 0, &added) == COREIDML_OK && added == 1, "import every page of another document");
    int32_t first_page[] = {0};
    check(coreidml_document_import_pages(doc, doc, first_page, 1, &added) == COREIDML_OK && added == 1, "import a chosen page of the document itself");
    check(coreidml_document_diff(from_file, doc, &json) == COREIDML_OK && contains(json, "page-count"), "the diff reports the added pages");
    coreidml_free(json);
    coreidml_document_close(from_file);

    check(coreidml_document_apply(doc, "{ \"pages\": [ { \"items\": [ { \"type\": \"text\", \"text\": \"x\", \"style\": \"Nope\" } ] } ] }") == COREIDML_ERR_NOT_FOUND
          && contains(coreidml_last_error_message(), "Nope"), "an unknown style is NOT_FOUND and named");
    check(coreidml_document_create("{ not json", &blank) == COREIDML_ERR_INVALID_ARGUMENT && blank == 0, "malformed JSON is INVALID_ARGUMENT");
    check(coreidml_document_open_file("/does/not/exist.idml", &blank) == COREIDML_ERR_IO, "a missing file is IO");

    /* ---- PDF -------------------------------------------------------------------------------------------------- */

    char pdf_path[1024];
    snprintf(pdf_path, sizeof pdf_path, "%s/smoke.pdf", out_dir);
    check(coreidml_document_export_pdf(doc, pdf_path, "{\"useSystemFonts\":false}", NULL, NULL, &report) == COREIDML_OK
          && contains(report, "\"pageCount\":"), "export a PDF file");
    check(contains(report, "\"missingFonts\":[{"), "the export reports the missing fonts");
    coreidml_free(report);

    coreidml_document from_pdf = 0;
    check(coreidml_pdf_import_file(pdf_path, NULL, NULL, NULL, &from_pdf, &report) == COREIDML_OK && from_pdf != 0, "import a PDF file");
    check(contains(report, "\"fonts\":[{"), "the import reports the fonts it needs");
    coreidml_free(report);
    check(coreidml_document_summary(from_pdf, NULL, &summary) == COREIDML_OK && contains(summary, "Sheet"), "the imported text is there");
    coreidml_free(summary);
    coreidml_document_close(from_pdf);
    check(coreidml_document_export_pdf(doc, "", NULL, NULL, NULL, &report) == COREIDML_ERR_INVALID_ARGUMENT && report == NULL, "an empty PDF path is INVALID_ARGUMENT");

    uint8_t* pdf = NULL;
    size_t pdf_length = 0;
    progress_calls = 0;
    check(coreidml_document_export_pdf_bytes(doc, "{\"useSystemFonts\":false}", on_progress, NULL, &pdf, &pdf_length, &report) == COREIDML_OK
          && pdf_length > 1000 && memcmp(pdf, "%PDF", 4) == 0, "export a PDF to memory");
    check(progress_calls >= 1, "the progress callback is called after each page");
    coreidml_free(report);
    check(coreidml_pdf_import(pdf, pdf_length, NULL, NULL, NULL, &from_pdf, &report) == COREIDML_OK && from_pdf != 0 && report != NULL, "import a PDF from memory");
    coreidml_free(report);
    coreidml_document_close(from_pdf);
    coreidml_free(pdf);

    int cancel = 1;
    pdf = NULL;
    check(coreidml_document_export_pdf_bytes(doc, NULL, on_progress, &cancel, &pdf, &pdf_length, &report) == COREIDML_ERR_CANCELLED
          && pdf == NULL && report == NULL && coreidml_last_error_code() == COREIDML_ERR_CANCELLED, "a progress callback can cancel");
    check(coreidml_document_export_pdf(doc, pdf_path, "{\"useSystemFonts\":false,\"maxImageDpi\":150}", NULL, NULL, &report) == COREIDML_OK, "export with options and no callback");
    coreidml_free(report);

    coreidml_document_close(doc);
    check(coreidml_document_save_file(doc, path) == COREIDML_ERR_INVALID_HANDLE, "the closed document is INVALID_HANDLE");

    printf("%d checks, %d failed\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
