"""Create, read and edit Adobe InDesign IDML files, through the Native AOT build of the CoreIdml library.

The C API (include/coreidml.h) is called with ctypes, so there is nothing to compile::

    import coreidml

    with coreidml.Document.open("template.idml") as doc:
        story = doc.info()["stories"][0]
        doc.write_part(story, doc.read_text(story).replace("Jean", "Ana"))
        doc.save_file("out.idml")

The native library is found on its own (see ``coreidml.load``); ``COREIDML_LIBRARY`` names another one.
"""

from ._abi import ABI_VERSION, Status
from ._document import Document
from ._native import IdmlError, library_path, load, version

__all__ = ["ABI_VERSION", "Document", "IdmlError", "Status", "library_path", "load", "version"]
