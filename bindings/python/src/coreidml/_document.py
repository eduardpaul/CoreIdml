"""The document API over the C functions."""

from __future__ import annotations

import ctypes
import json
import os
from typing import Any, Union

from ._abi import Status
from ._native import IdmlError, check, native, take_bytes, take_text

_Bytes = Union[bytes, bytearray, memoryview]


class Document:
    """An InDesign document held by the library.

    Open one with ``Document.open`` and close it with ``close()`` or a ``with`` block; a document that is forgotten
    is released when it is garbage collected. A document may be used from any thread; calls on it are serialized.
    """

    def __init__(self, handle: int):
        self._handle = handle

    @classmethod
    def open(cls, source: Union[str, os.PathLike, _Bytes]) -> "Document":
        """Opens an .idml file from a path, or from its bytes (which are not kept)."""
        lib = native()
        handle = ctypes.c_ssize_t(0)
        if isinstance(source, (bytes, bytearray, memoryview)):
            data = bytes(source)
            check(lib.coreidml_document_open(data, len(data), ctypes.byref(handle)))
        elif isinstance(source, (str, os.PathLike)):
            check(lib.coreidml_document_open_file(os.fspath(source).encode("utf-8"), ctypes.byref(handle)))
        else:
            raise IdmlError("source must be a path or the bytes of an .idml file.", Status.INVALID_ARGUMENT)
        return cls(handle.value)

    @property
    def closed(self) -> bool:
        """Whether ``close()`` was called."""
        return self._handle == 0

    def info(self) -> dict[str, Any]:
        """The parts of the package and the manifest: ``domVersion``, ``parts``, ``spreads``, ``masterSpreads``, ``stories``."""
        text = ctypes.c_void_p()
        check(native().coreidml_document_info(self._open_handle(), ctypes.byref(text)))
        return json.loads(take_text(text))

    def read_part(self, path: str) -> bytes:
        """The bytes of one part (e.g. ``Stories/Story_u188.xml``) as they would be saved now."""
        data, length = ctypes.c_void_p(), ctypes.c_size_t(0)
        check(native().coreidml_document_read_part(self._open_handle(), _path(path), ctypes.byref(data), ctypes.byref(length)))
        return take_bytes(data, length.value)

    def read_text(self, path: str) -> str:
        """One part as text (parts are UTF-8 XML, except images a template may embed)."""
        return self.read_part(path).decode("utf-8")

    def write_part(self, path: str, data: Union[str, _Bytes]) -> None:
        """Adds or replaces one part. Text is written as UTF-8."""
        if isinstance(data, str):
            content = data.encode("utf-8")
        elif isinstance(data, (bytes, bytearray, memoryview)):
            content = bytes(data)
        else:
            raise IdmlError("data must be text or bytes.", Status.INVALID_ARGUMENT)
        check(native().coreidml_document_write_part(self._open_handle(), _path(path), content, len(content)))

    def save(self) -> bytes:
        """The document as the bytes of an .idml file. Parts that were not changed are written byte for byte."""
        data, length = ctypes.c_void_p(), ctypes.c_size_t(0)
        check(native().coreidml_document_save(self._open_handle(), ctypes.byref(data), ctypes.byref(length)))
        return take_bytes(data, length.value)

    def save_file(self, path: Union[str, os.PathLike]) -> None:
        """Saves the document as an .idml file."""
        check(native().coreidml_document_save_file(self._open_handle(), os.fspath(path).encode("utf-8")))

    def close(self) -> None:
        """Releases the document. Calling it again does nothing."""
        handle, self._handle = self._handle, 0
        if handle:
            native().coreidml_document_close(handle)

    def _open_handle(self) -> int:
        if not self._handle:
            raise IdmlError("The document is closed.", Status.INVALID_HANDLE)
        return self._handle

    def __enter__(self) -> "Document":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:  # the interpreter may be shutting down
            pass


def _path(path: str) -> bytes:
    if not isinstance(path, str):
        raise IdmlError("path must be a string.", Status.INVALID_ARGUMENT)
    return path.encode("utf-8")
