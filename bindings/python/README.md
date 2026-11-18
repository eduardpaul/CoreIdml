# coreidml

**Read, edit and write Adobe InDesign IDML files from Python, without InDesign.**

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles). CoreIdml opens
it, lets you read and change any part, and writes it back so that InDesign opens the result as if nothing but your
edit had happened.

```sh
pip install coreidml
```

```python
import coreidml

with coreidml.Document.open("brochure.idml") as doc:
    for story in doc.info()["stories"]:
        xml = doc.read_text(story)
        if "2025" in xml:
            doc.write_part(story, xml.replace("2025", "2026"))
    doc.save_file("brochure-2026.idml")
```

## Why CoreIdml

- **Surgical edits.** A part you do not touch is written back byte for byte. Change one story, and the designer's
  file differs in exactly that story. (Unzipping and re-zipping with `zipfile` rewrites every entry.)
- **Output InDesign accepts.** `mimetype` first and uncompressed, InDesign's own formatting for what you change.
- **Reproducible files.** Untouched parts keep their timestamps and compression, so an unchanged document saves to
  the same bytes every time.
- **Safe with files from anywhere.** Checksums verified on read, document type declarations refused, typed errors.
- **Nothing to compile.** The engine is a prebuilt native library inside the wheel, called with `ctypes`. No
  dependencies, no .NET runtime, no InDesign.

Type hints are included (`py.typed`).

## API

### Opening

```python
doc = coreidml.Document.open("brochure.idml")      # a str or os.PathLike
doc = coreidml.Document.open(data)                 # bytes, bytearray or memoryview; not kept after the call
```

A document is a context manager; outside a `with` block, call `doc.close()` when you are done. One that is
forgotten is released when it is garbage collected.

### `Document`

| Member | |
| --- | --- |
| `info()` | The parts of the package and the manifest, as a `dict`; see below. |
| `read_part(path)` | The `bytes` of one part, exactly as they would be saved now. |
| `read_text(path)` | The same as `str`. Parts are UTF-8 XML. |
| `write_part(path, data)` | Adds or replaces a part with a `str` (written as UTF-8) or bytes. |
| `save()` | The document as the `bytes` of an .idml file. |
| `save_file(path)` | Writes the .idml file. |
| `close()` | Releases the document. |
| `closed` | Whether it was closed. |

```python
doc.info()
# {
#     "domVersion": "10.0",
#     "parts": [{"path": "designmap.xml", "length": 16154, "modified": False}, ...],
#     "spreads": ["Spreads/Spread_ud6.xml"],
#     "masterSpreads": ["MasterSpreads/MasterSpread_ua5.xml"],
#     "stories": ["Stories/Story_u1db.xml", "Stories/Story_u19f.xml", "Stories/Story_u188.xml"],
# }
```

Parts are XML, so they work with the parser you already use:

```python
import xml.etree.ElementTree as ET

with coreidml.Document.open("brochure.idml") as doc:
    for path in doc.info()["stories"]:
        story = ET.fromstring(doc.read_part(path))
        print(path, [content.text for content in story.iter("Content")])
```

### Errors

Every failure is a `coreidml.IdmlError` with a `code`, a member of `coreidml.Status`:

```python
from coreidml import Document, IdmlError, Status

try:
    doc = Document.open(upload)
except IdmlError as error:
    if error.code == Status.INVALID_FORMAT:
        ...  # Not a zip, no designmap.xml, a damaged entry: str(error) says which.
```

| `Status` | Meaning |
| --- | --- |
| `INVALID_FORMAT` | The input is not a usable IDML package. |
| `NOT_FOUND` | A part you named does not exist. |
| `INVALID_ARGUMENT` | An argument is missing or malformed. |
| `INVALID_HANDLE` | The document was closed. |
| `IO` | Reading or writing a file failed. |
| `INVALID_OPERATION` | The call is not valid right now, such as loading a second copy of the library. |
| `FAILED` | Anything unexpected; the message names it. |

### The library

```python
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
- **Scope of 0.1.** The library works at the package level: every part as XML or bytes, and full control over what
  is written. A typed document model on top of it (pages, frames, stories, styles) is in development.

Source, other languages (.NET, Node.js, the browser, Dart/Flutter, C) and issues:
[github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).

MIT licensed. CoreIdml is an independent project and is not affiliated with Adobe.
