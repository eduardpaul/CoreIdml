# coreidml

**Create, read, edit and print Adobe InDesign IDML files from Python, without InDesign.**

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles). CoreIdml builds
documents from a JSON-like spec, fills templates, edits stories, links and XML, merges pages, renders PDFs and turns
PDFs back into editable documents, and writes files that InDesign opens as if nothing but your edit had happened.

```sh
pip install coreidml
```

```python
import coreidml

with coreidml.Document.open("brochure.idml") as doc:
    doc.fill({"text": {"customer": "Ana Pérez", "date": "18 Nov 2026"}})
    doc.replace_text("2025", "2026")
    doc.save_file("brochure-ana.idml")
    report = doc.export_pdf("brochure-ana.pdf", {"fontDirectories": ["fonts"]})
    print(report["pageCount"], "pages;", len(report["missingFonts"]), "fonts substituted")
```

## Why CoreIdml

- **Surgical edits.** A part you do not touch is written back byte for byte. Change one story, and the designer's
  file differs in exactly that story. (Unzipping and re-zipping with `zipfile` rewrites every entry.)
- **Output InDesign accepts.** `mimetype` first and uncompressed, InDesign's own formatting for what you change.
- **Built for programs and agents.** A size-controlled JSON summary of pages, frames, stories and links; indexed
  text runs to edit; a validator and a diff to check the result.
- **PDF both ways, no InDesign.** Its own PDF writer with font embedding, and a PDF importer that rebuilds editable
  frames and styles.
- **Safe with files from anywhere.** Checksums verified on read, document type declarations refused, typed errors.
- **Nothing to compile.** The engine is a prebuilt native library inside the wheel, called with `ctypes`. No
  dependencies, no .NET runtime, no InDesign.

Type hints are included (`py.typed`).

## API

### Getting a document

```python
from coreidml import Document

doc = Document.open("brochure.idml")       # a str or os.PathLike
doc = Document.open(data)                  # bytes, bytearray or memoryview; not kept after the call
doc = Document.create(spec)                # from a document spec (below); Document.create() is one blank A4 page
doc, report = Document.import_pdf("flyer.pdf", {"imageDirectory": "flyer-links"})   # see PDF
```

A document is a context manager; outside a `with` block, call `doc.close()` when you are done. One that is
forgotten is released when it is garbage collected.

Options, specs and fill data are `dict`s (or the same as JSON text) with the camelCase keys shown here; results are
`dict`s. Paths are `str` or `os.PathLike`.

### `Document`

| Member | |
| --- | --- |
| **Reading** | |
| `summary(options=None)` | Pages, frames with positions and text, stories, links, styles, swatches, layers, tags, fonts. |
| `story(story_id)` | One story in full: its text, frames and every content run with its index and styles. |
| `xml_structure()` | The XML structure as a tree of `dict`s, or `None` when there is none. |
| `export_xml(at=None)` | The XML structure as XML, or the subtree at a path such as `"/Root/article[2]"`. |
| `validate()` | Checks references, ids, layers, threads and XML bindings: `{"isValid": True, "issues": []}`. |
| `diff(other)` | What changed from this document to `other`: stories run by run, links, page count. |
| **Editing** | |
| `fill(data)` | Fills a template; returns which keys were `filled` and which are `missing`. |
| `replace_text(find, replacement)` | Replaces text in every story; returns how many times. |
| `set_run_text(story_id, run_index, text, force=False)` | Sets one run's text and keeps its formatting. |
| `relink(link_id, uri_or_path, refit=False)` | Points an image link at another file. |
| `import_xml(xml, at=None, options=None)` | Imports XML onto the structure; returns a report. |
| `import_pages(source, pages=())` | Appends pages of another document, or of itself; returns how many. |
| `apply(spec)` | Adds the colours, styles and pages of a spec to the document. |
| **Saving** | |
| `save()` | The document as the `bytes` of an .idml file. |
| `save_file(path)` | Writes the .idml file. |
| `export_pdf(path, options=None, progress=None)` | Renders a PDF file; returns a report. |
| `export_pdf_bytes(options=None, progress=None)` | Renders a PDF in memory; returns `(pdf_bytes, report)`. |
| **Package parts** | |
| `info()` | The parts of the package and the manifest (`domVersion`, `parts`, `spreads`, `masterSpreads`, `stories`). |
| `read_part(path)`, `read_text(path)` | One part (e.g. `"Stories/Story_u188.xml"`) as `bytes` or `str`, exactly as it would be saved now. |
| `write_part(path, data)` | Adds or replaces a part with a `str` (written as UTF-8) or bytes. |
| `close()`, `closed` | Releases the document; whether it was released. |

### Creating documents

A spec describes the page, colours, styles and pages of items: text, images, tables, rectangles and lines.

```python
spec = {
    "unit": "mm",                                    # pt (default), mm, cm or in
    "page": {"size": "A4", "margins": {"all": 15}},
    "colors": [{"name": "Brand", "cmyk": [75, 45, 0, 10]}, {"name": "Web", "hex": "#2E86C1"}],
    "paragraphStyles": [{"name": "Title", "size": 22, "fontStyle": "Bold", "color": "Brand"}],
    "characterStyles": [{"name": "Strong", "fontStyle": "Bold"}],
    "pages": [{"items": [
        {"type": "text", "name": "title", "x": 15, "y": 15, "width": 180, "height": 15,
         "text": "Order {{number}}", "style": "Title"},
        {"type": "text", "x": 15, "y": 35, "width": 180, "height": 10,
         "paragraphs": [{"runs": [{"text": "Status: "}, {"text": "paid", "style": "Strong"}]}]},
        {"type": "table", "x": 15, "y": 50, "width": 120, "height": 30, "headerRows": 1,
         "rows": [["Item", "Qty"], ["Poster A2", "3"]]},
        {"type": "rectangle", "x": 0, "y": 287, "width": 210, "height": 10, "fill": "Brand"},
        {"type": "line", "x": 15, "y": 45, "x2": 195, "y2": 45, "stroke": "Brand", "strokeWeight": 0.5},
    ]}],
}

with Document.create(spec) as doc:
    doc.fill({"text": {"number": "F-001"}})
    doc.save_file("order.idml")
```

Images are `{"type": "image", "path": "/photos/a.jpg", "fit": "fill", ...}`; IDML links images, so ship the files
with the document. Every item also takes `name`, `label`, `tag` (to appear in the XML structure), `layer`,
`fill`, `stroke`, `strokeWeight` and `rotation`. `doc.apply(spec)` adds a spec's styles and pages to an existing
document.

### Templates and text

```python
with Document.open("certificate.idml") as doc:
    report = doc.fill({
        "text": {"name": "Ana Pérez", "course": "Typography"},     # frame names, script labels, XML tags, {{placeholders}}
        "images": {"photo": "/photos/ana.jpg"},                    # the file must exist: it is measured to fit
    })
    # {"filled": {"name": 1, "course": 1, "photo": 1}, "missing": []}

    summary = doc.summary({"pages": [0], "sections": ["pages", "stories"], "maxTextLength": 80})
    for item in summary["pages"][0]["items"]:
        print(item["kind"], item.get("name"), item.get("text"))

    story_id = summary["stories"][0]["id"]
    for run in doc.story(story_id)["runs"]:
        print(run["index"], run["characterStyle"], run["text"])
    doc.set_run_text(story_id, 0, "Ana Pérez García")              # keeps the run's formatting
```

Summary options: `maxTextLength` (200), `includeRuns` (`False`), `includeResources` (`True`), `pages` (0-based
indices) and `sections` (any of `"pages"`, `"stories"`, `"links"`, `"offPage"`, `"resources"`). A run holding special
characters, such as a page number, is only overwritten with `force=True`.

### Links

```python
for link in doc.summary({"sections": ["links"]})["links"]:
    print(link["id"], link["uri"], link["effectivePpi"])

doc.relink("u21a", "/photos/new-cover.jpg", refit=True)   # a local path or a URI
```

With `refit=True` and a readable local JPEG or PNG, the image is measured again and fitted to its frame.

### XML

Tagged templates work as in SimpleIDML: `simpleidml-setcontent`, `-ignorecontent`, `-forcecontent` and `href`.

```python
with Document.open("article.idml") as doc:
    doc.xml_structure()          # {"tag": "Root", "path": "/Root", "children": [...]}
    report = doc.import_xml(xml_text, at="/Root/module", options={"characterStyles": {"b": "Bold"}})
    # {"updated": 6, "added": 0, "deleted": 0, "unmatched": [], "warnings": []}
    print(doc.export_xml("/Root/module/headline"))
```

`xml` is a `str` or UTF-8 bytes. Import options: `fit`, `characterStyles` (an inline tag to a character style of
the document, here `<b>` to `Bold`) and `relinkOnly`.

### Merging pages, checking the result

```python
with Document.open("catalog.idml") as doc, Document.open("insert.idml") as insert:
    doc.import_pages(insert)            # every page; styles, swatches and tags are merged once
    doc.import_pages(doc, [0])          # a copy of its own first page, at the end
    assert doc.validate()["isValid"]
    with Document.open("catalog.idml") as before:
        print(before.diff(doc)["changes"])
```

### PDF

```python
with Document.open("brochure.idml") as doc:
    report = doc.export_pdf("brochure.pdf", {
        "fontDirectories": ["fonts"],                          # .ttf/.otf files the document uses
        "substitutions": {"Minion Pro": "Liberation Serif"},   # family replacements
        "linkDirectories": ["photos"],                         # linked images are found by file name
    })
    pdf, report = doc.export_pdf_bytes({"pages": [1], "useSystemFonts": False})
```

The report says what to check: `pageCount`, `missingFonts` (each with the font used instead, its pages and
stories), `missingImages`, `oversetStories` and `warnings`.

- **Fonts.** Fonts are embedded (TrueType as subsets). They are searched in `fontDirectories` and, unless
  `useSystemFonts` is `False`, in the fonts installed on the machine. Without any match, a standard PDF font stands
  in, not embedded. For the same output on every machine, ship the fonts and pass `"useSystemFonts": False`.
- **Links.** IDML links images. They are found at their recorded location, by file name in `linkDirectories`, or
  through `linkRemap` (`{"old.jpg": "/new/path.jpg"}`). `documentPath`, where the .idml file lives, resolves relative
  links and adds that folder and its `Links` subfolder to the search.
  `maxImageDpi` and `jpegQuality` keep the PDF small.
- **Pages.** `pages` lists 1-based page numbers.

The other way, `Document.import_pdf` rebuilds an editable document from a PDF path or bytes: text frames with
inferred paragraph and character styles, image frames and shapes. Images are written to `imageDirectory`.

```python
doc, report = Document.import_pdf(pdf_bytes, {"imageDirectory": "imported-links", "fontDirectories": ["fonts"]})
with doc:
    doc.save_file("imported.idml")
# report: textFrames, imageFrames, shapes, images, fonts (and whether each is available), warnings
```

Import options: `pages` (1-based), `imageDirectory`, `preserveLineBreaks`, `importShapes` and `fontDirectories`.

#### Progress and cancelling

The PDF calls take `progress(done, total)`, called after each page on the calling thread. Return `False` to cancel;
the call then raises `IdmlError` with `Status.CANCELLED`. An exception raised in the callback also cancels, and is
raised again from the call.

```python
def progress(done, total):
    print(f"page {done} of {total}")
    return not stop_requested.is_set()

pdf, report = doc.export_pdf_bytes(progress=progress)
```

### Errors

Every failure is a `coreidml.IdmlError` with a `code`, a member of `coreidml.Status`, and a message that says what
went wrong:

```python
from coreidml import Document, IdmlError, Status

try:
    doc.set_run_text(story_id, 3, "New text")
except IdmlError as error:
    if error.code == Status.NOT_FOUND:
        ...  # str(error): 'Story "u10a" has 1 content run(s); run index 3 does not exist.'
```

| `Status` | Meaning |
| --- | --- |
| `INVALID_FORMAT` | The input is not a usable IDML package. |
| `NOT_FOUND` | Something you named does not exist: a part, story, run, link, page or XML element. |
| `INVALID_ARGUMENT` | An argument is missing or malformed, such as an option that is not valid JSON. |
| `INVALID_HANDLE` | The document was closed. |
| `CANCELLED` | A progress callback cancelled the call. |
| `IO` | Reading or writing a file failed. |
| `INVALID_OPERATION` | The call is not valid right now, such as importing XML that is not well-formed. |
| `UNSUPPORTED` | The document uses something the library does not handle. |
| `FAILED` | Anything unexpected; the message names it. |

### The library

```python
coreidml.__version__      # version of this package, as installed
coreidml.version()        # version of the engine
coreidml.library_path()   # where the native library was loaded from
```

## Platforms

Wheels carry the native library for Windows x64 and Linux x64 (glibc).

Python 3.10 or later; the wheels are not tied to a Python version. x64 processors need x86-64-v2 (anything from
the last decade).

On another platform, or to use a library you built yourself, point the package at it before first use:

```sh
COREIDML_LIBRARY=/path/to/libcoreidml.so python app.py
```

```python
coreidml.load("/path/to/libcoreidml.so")
```

## Good to know

- A document may be used from several threads; calls on one document are serialized. The engine does its work
  outside the GIL.
- A process uses one copy of the library.
- The PDF export is not InDesign: the line breaker is greedy (InDesign's default is the paragraph composer), and a
  missing font changes line breaks. Check `missingFonts` and `oversetStories` in the report.
- Images are linked, not embedded, as IDML always does: send generated files together with their images.

Source, other languages (.NET, Node.js, the browser, Dart/Flutter, C) and issues:
[github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).

MIT licensed. CoreIdml is an independent project and is not affiliated with Adobe.
