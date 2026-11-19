"""Create, read and edit Adobe InDesign IDML files, through the Native AOT build of the CoreIdml library.

The C API (include/coreidml.h) is called with ctypes, so there is nothing to compile::

    import coreidml

    with coreidml.Document.open("template.idml") as doc:
        doc.fill({"text": {"name": "Ana"}})
        doc.save_file("out.idml")
        doc.export_pdf("out.pdf", {"fontDirectories": ["fonts"]})

The native library is found on its own (see ``coreidml.load``); ``COREIDML_LIBRARY`` names another one.
"""

from importlib import metadata as _metadata

from ._abi import ABI_VERSION, Status
from ._document import Document, ProgressCallback
from ._native import IdmlError, library_path, load, version

try:
    __version__: str = _metadata.version("coreidml")
    """Version of this package, as installed (``version()`` is the version of the native library it calls)."""
except _metadata.PackageNotFoundError:  # imported from a source checkout, not installed
    __version__ = "0+unknown"

__all__ = ["ABI_VERSION", "Document", "IdmlError", "ProgressCallback", "Status", "library_path", "load", "version"]
