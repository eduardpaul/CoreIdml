"""The document API over the C functions."""

from __future__ import annotations

import ctypes
import json
import os
from collections.abc import Iterable, Mapping
from typing import Any, Callable, Optional, Union, cast

from ._abi import PROGRESS_CALLBACK, Status
from ._native import IdmlError, check, native, release, take_bytes, take_text

_Bytes = Union[bytes, bytearray, memoryview]
_Path = Union[str, "os.PathLike[str]"]
#: A JSON argument: a dict (or list) that is serialized, or JSON text passed as it is.
_Json = Union[Mapping[str, Any], str]

#: ``progress(done, total)``, called after each page; returning ``False`` cancels the call.
ProgressCallback = Callable[[int, int], Optional[bool]]


class Document:
    """An InDesign document held by the library.

    Open one with ``Document.open``, ``Document.create`` or ``Document.import_pdf`` and close it with ``close()`` or a
    ``with`` block; a document that is forgotten is released when it is garbage collected. A document may be used
    from any thread; calls on it are serialized.
    """

    def __init__(self, handle: int):
        self._handle = handle

    # ---- Opening and creating ------------------------------------------------------------------------------------

    @classmethod
    def open(cls, source: Union[_Path, _Bytes]) -> "Document":
        """Opens an .idml file from a path, or from its bytes (which are not kept)."""
        lib = native()
        handle = ctypes.c_ssize_t(0)
        if isinstance(source, (bytes, bytearray, memoryview)):
            data = bytes(source)
            check(lib.coreidml_document_open(data, len(data), ctypes.byref(handle)))
        elif isinstance(source, (str, os.PathLike)):
            check(lib.coreidml_document_open_file(_file(source), ctypes.byref(handle)))
        else:
            raise IdmlError("source must be a path or the bytes of an .idml file.", Status.INVALID_ARGUMENT)
        return cls(handle.value)

    @classmethod
    def create(cls, spec: Optional[_Json] = None) -> "Document":
        """Creates a document from a document spec (a dict or its JSON text): page size, colours, styles, and pages of
        text, images, tables and shapes. Without one, the document is one blank A4 page."""
        handle = ctypes.c_ssize_t(0)
        check(native().coreidml_document_create(_json(spec, "spec"), ctypes.byref(handle)))
        return cls(handle.value)

    @classmethod
    def import_pdf(
        cls,
        source: Union[_Path, _Bytes],
        options: Optional[_Json] = None,
        progress: Optional[ProgressCallback] = None,
    ) -> tuple["Document", dict[str, Any]]:
        """Builds an editable document from a PDF (a path, or its bytes): text frames with inferred styles, image
        frames and shapes. Returns the document and the import report (frames, images, fonts, warnings).

        Options: ``pages`` (1-based), ``imageDirectory`` (where the images are written, since IDML links them),
        ``preserveLineBreaks``, ``importShapes``, ``fontDirectories``.
        """
        lib = native()
        handle, report = ctypes.c_ssize_t(0), ctypes.c_void_p()
        options_json = _json(options, "options")
        with _Progress(progress) as callback:
            if isinstance(source, (bytes, bytearray, memoryview)):
                data = bytes(source)
                status = lib.coreidml_pdf_import(data, len(data), options_json, callback.function, None, ctypes.byref(handle), ctypes.byref(report))
            elif isinstance(source, (str, os.PathLike)):
                status = lib.coreidml_pdf_import_file(_file(source), options_json, callback.function, None, ctypes.byref(handle), ctypes.byref(report))
            else:
                raise IdmlError("source must be a path or the bytes of a PDF file.", Status.INVALID_ARGUMENT)
            # The error message is read before anything else is called: it is valid only until the next call.
            try:
                callback.check(status)
            except BaseException:
                release(report)
                if handle.value:
                    lib.coreidml_document_close(handle.value)
                raise
            document = cls(handle.value)
            try:
                result = _take_json(report)
            except BaseException:
                document.close()
                raise
        return document, result

    @property
    def closed(self) -> bool:
        """Whether ``close()`` was called."""
        return self._handle == 0

    # ---- Package parts -------------------------------------------------------------------------------------------

    def info(self) -> dict[str, Any]:
        """The parts of the package and the manifest: ``domVersion``, ``parts``, ``spreads``, ``masterSpreads``, ``stories``."""
        text = ctypes.c_void_p()
        check(native().coreidml_document_info(self._open_handle(), ctypes.byref(text)))
        return _take_json(text)

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

    # ---- Reading -------------------------------------------------------------------------------------------------

    def summary(self, options: Optional[_Json] = None) -> dict[str, Any]:
        """A summary for programs and agents: pages, frames with positions and text, stories, links, styles, swatches,
        tags. Options: ``maxTextLength`` (200), ``includeRuns`` (False), ``includeResources`` (True), ``pages``
        (0-based indices) and ``sections`` (any of ``pages``, ``stories``, ``links``, ``offPage``, ``resources``)."""
        text = ctypes.c_void_p()
        check(native().coreidml_document_summary(self._open_handle(), _json(options, "options"), ctypes.byref(text)))
        return _take_json(text)

    def story(self, story_id: str) -> dict[str, Any]:
        """One story in full: its text, frames, and every content run with its index, styles and scope."""
        text = ctypes.c_void_p()
        check(native().coreidml_document_story(self._open_handle(), _text(story_id, "story_id"), ctypes.byref(text)))
        return _take_json(text)

    def xml_structure(self) -> Optional[dict[str, Any]]:
        """The XML structure as a tree of dicts (``tag``, ``id``, ``path``, ``boundTo``, ``attributes``, ``text``,
        ``children``), or ``None`` when the document has none."""
        text = ctypes.c_void_p()
        check(native().coreidml_document_xml_structure(self._open_handle(), ctypes.byref(text)))
        return cast("Optional[dict[str, Any]]", _take_json_value(text))

    def export_xml(self, at: Optional[str] = None) -> str:
        """The XML structure as XML, or only the subtree at a path such as ``/Root/article[2]``."""
        text = ctypes.c_void_p()
        check(native().coreidml_document_export_xml(self._open_handle(), _optional_text(at, "at"), ctypes.byref(text)))
        return take_text(text)

    def validate(self) -> dict[str, Any]:
        """Checks references, ids, layers, threads and XML bindings: ``{"isValid": True, "issues": [...]}``."""
        text = ctypes.c_void_p()
        check(native().coreidml_document_validate(self._open_handle(), ctypes.byref(text)))
        return _take_json(text)

    def diff(self, other: "Document") -> dict[str, Any]:
        """What changed from this document to ``other``: stories run by run, links, page count, page items."""
        text = ctypes.c_void_p()
        check(native().coreidml_document_diff(self._open_handle(), _document(other, "other"), ctypes.byref(text)))
        return _take_json(text)

    # ---- Editing -------------------------------------------------------------------------------------------------

    def apply(self, spec: _Json) -> None:
        """Adds the colours, styles and pages of a document spec (a dict or its JSON text); its pages are appended."""
        check(native().coreidml_document_apply(self._open_handle(), _json(spec, "spec", required=True)))

    def fill(self, data: _Json) -> dict[str, Any]:
        """Fills a template: ``{"text": {"key": "value"}, "images": {"key": "/path/photo.jpg"}}``. Keys match frame
        names, script labels, XML tags and ``{{placeholders}}``. Returns the report (``filled``, ``missing``)."""
        text = ctypes.c_void_p()
        check(native().coreidml_document_fill(self._open_handle(), _json(data, "data", required=True), ctypes.byref(text)))
        return _take_json(text)

    def replace_text(self, find: str, replacement: str) -> int:
        """Replaces text in every story; returns the number of replacements."""
        count = ctypes.c_int32(0)
        check(native().coreidml_document_replace_text(
            self._open_handle(), _text(find, "find"), _text(replacement, "replacement"), ctypes.byref(count)))
        return count.value

    def set_run_text(self, story_id: str, run_index: int, text: str, force: bool = False) -> None:
        """Sets the text of one content run of a story (indices from ``story()``), keeping its formatting. A run that
        holds special characters (a page number) is only overwritten with ``force``."""
        check(native().coreidml_document_set_run_text(
            self._open_handle(), _text(story_id, "story_id"), _int32(run_index, "run_index"), _text(text, "text"), 1 if force else 0))

    def relink(self, link_id: str, uri_or_path: _Path, refit: bool = False) -> None:
        """Points a link (an id from the summary's ``links``) at another file: a URI or a local path. With ``refit``
        and a readable local JPEG or PNG, the image is measured again and fitted to its frame."""
        target = os.fspath(uri_or_path) if isinstance(uri_or_path, os.PathLike) else uri_or_path
        check(native().coreidml_document_relink(
            self._open_handle(), _text(link_id, "link_id"), _text(target, "uri_or_path"), 1 if refit else 0))

    def import_xml(self, xml: Union[str, _Bytes], at: Optional[str] = None, options: Optional[_Json] = None) -> dict[str, Any]:
        """Imports XML onto the structure element at a path (the root without one), with SimpleIDML's semantics
        (``simpleidml-setcontent``, ``-ignorecontent``, ``-forcecontent``, ``href``). Options: ``fit``,
        ``characterStyles`` (tag -> style), ``relinkOnly``. Returns the report (``updated``, ``added``, ``deleted``,
        ``unmatched``, ``warnings``)."""
        if isinstance(xml, (bytes, bytearray, memoryview)):
            content = bytes(xml)
            if b"\0" in content:
                raise IdmlError("xml must not contain NUL characters.", Status.INVALID_ARGUMENT)
        else:
            content = _text(xml, "xml")
        text = ctypes.c_void_p()
        check(native().coreidml_document_import_xml(
            self._open_handle(), content, _optional_text(at, "at"), _json(options, "options"), ctypes.byref(text)))
        return _take_json(text)

    def import_pages(self, source: "Document", pages: Iterable[int] = ()) -> int:
        """Appends pages of ``source`` (0-based indices; none: every page), remapping every id; styles, swatches and
        tags are merged once. ``source`` may be this document. Returns the number of pages added."""
        if isinstance(pages, (str, bytes)) or not isinstance(pages, Iterable):
            raise IdmlError("pages must be an iterable of page indices.", Status.INVALID_ARGUMENT)
        indices = [_int32(page, "pages") for page in pages]
        array = (ctypes.c_int32 * len(indices))(*indices) if indices else None
        added = ctypes.c_int32(0)
        check(native().coreidml_document_import_pages(
            self._open_handle(), _document(source, "source"), array, len(indices), ctypes.byref(added)))
        return added.value

    # ---- Saving and PDF ------------------------------------------------------------------------------------------

    def save(self) -> bytes:
        """The document as the bytes of an .idml file. Parts that were not changed are written byte for byte."""
        data, length = ctypes.c_void_p(), ctypes.c_size_t(0)
        check(native().coreidml_document_save(self._open_handle(), ctypes.byref(data), ctypes.byref(length)))
        return take_bytes(data, length.value)

    def save_file(self, path: _Path) -> None:
        """Saves the document as an .idml file."""
        check(native().coreidml_document_save_file(self._open_handle(), _file(path)))

    def export_pdf(self, path: _Path, options: Optional[_Json] = None, progress: Optional[ProgressCallback] = None) -> dict[str, Any]:
        """Renders the document to a PDF file. Returns the report: ``pageCount``, ``missingFonts``, ``missingImages``,
        ``oversetStories``, ``warnings``.

        Options: ``pages`` (1-based), ``fontDirectories``, ``useSystemFonts`` (True), ``substitutions`` (font ->
        font), ``linkDirectories``, ``linkRemap``, ``documentPath``, ``maxImageDpi``, ``jpegQuality``.
        ``progress(done, total)`` is called after each page; returning ``False`` cancels (``Status.CANCELLED``).
        """
        handle, file, options_json = self._open_handle(), _file(path), _json(options, "options")
        report = ctypes.c_void_p()
        with _Progress(progress) as callback:
            status = native().coreidml_document_export_pdf(handle, file, options_json, callback.function, None, ctypes.byref(report))
            try:
                callback.check(status)  # reads the error message before anything is released
            except BaseException:
                release(report)
                raise
            return _take_json(report)

    def export_pdf_bytes(self, options: Optional[_Json] = None, progress: Optional[ProgressCallback] = None) -> tuple[bytes, dict[str, Any]]:
        """Renders the document to a PDF in memory. Returns ``(pdf, report)``; options and report as for
        ``export_pdf``."""
        handle, options_json = self._open_handle(), _json(options, "options")
        data, length, report = ctypes.c_void_p(), ctypes.c_size_t(0), ctypes.c_void_p()
        with _Progress(progress) as callback:
            status = native().coreidml_document_export_pdf_bytes(
                handle, options_json, callback.function, None, ctypes.byref(data), ctypes.byref(length), ctypes.byref(report))
            try:
                callback.check(status)  # reads the error message before anything is released
            except BaseException:
                release(data, report)
                raise
            try:
                pdf = take_bytes(data, length.value)
            except BaseException:
                release(report)
                raise
            return pdf, _take_json(report)

    # ---- Lifetime ------------------------------------------------------------------------------------------------

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


class _Progress:
    """The C progress callback of one call, kept alive by the ``with`` block around the call.

    An exception raised by the Python callback cannot cross the C frames: it cancels the call, and ``check`` raises it
    again once the call has returned. Without a callback the C function gets NULL.
    """

    def __init__(self, callback: Optional[ProgressCallback]):
        if callback is not None and not callable(callback):
            raise IdmlError("progress must be callable.", Status.INVALID_ARGUMENT)
        self._callback = callback
        self._error: Optional[BaseException] = None
        self.function = PROGRESS_CALLBACK(self._call) if callback is not None else PROGRESS_CALLBACK()

    def _call(self, done: int, total: int, _user_data: Optional[int]) -> int:
        if self._error is not None or self._callback is None:
            return 1
        try:
            return 1 if self._callback(done, total) is False else 0
        except BaseException as error:  # re-raised by check(), after the native call has unwound
            self._error = error
            return 1

    def check(self, status: int) -> None:
        if self._error is not None:
            error, self._error = self._error, None
            raise error
        check(status)

    def __enter__(self) -> "_Progress":
        return self

    def __exit__(self, *_: object) -> None:
        # Only now may the function pointer be collected: the library calls it until the call returns.
        self.function = PROGRESS_CALLBACK()
        self._callback = None


def _take_json(pointer: ctypes.c_void_p) -> dict[str, Any]:
    """Parses and releases a JSON object the library handed out (``{}`` when there is none: a failed call)."""
    return cast("dict[str, Any]", _take_json_value(pointer) or {})


def _take_json_value(pointer: ctypes.c_void_p) -> Any:
    """Parses and releases a JSON string the library handed out; ``None`` for JSON null or no string at all."""
    text = take_text(pointer)
    return json.loads(text) if text else None


def _json(value: Optional[_Json], name: str, required: bool = False) -> Optional[bytes]:
    if value is None:
        if required:
            raise IdmlError(f"{name} is required.", Status.INVALID_ARGUMENT)
        return None
    if isinstance(value, str):
        return _text(value, name)
    if not isinstance(value, (Mapping, list, tuple)):
        raise IdmlError(f"{name} must be a dict or JSON text.", Status.INVALID_ARGUMENT)
    try:
        text = json.dumps(value, ensure_ascii=False, default=_json_default)
    except (TypeError, ValueError) as error:
        raise IdmlError(f"{name} cannot be written as JSON: {error}", Status.INVALID_ARGUMENT) from error
    return _text(text, name)


def _json_default(value: object) -> Any:
    if isinstance(value, os.PathLike):  # paths in options (fontDirectories, imageDirectory, ...)
        return os.fspath(value)
    raise TypeError(f"{type(value).__name__} is not JSON serializable")


def _text(value: str, name: str) -> bytes:
    if not isinstance(value, str):
        raise IdmlError(f"{name} must be a string.", Status.INVALID_ARGUMENT)
    if "\0" in value:
        raise IdmlError(f"{name} must not contain NUL characters.", Status.INVALID_ARGUMENT)
    return value.encode("utf-8")


def _optional_text(value: Optional[str], name: str) -> Optional[bytes]:
    return None if value is None else _text(value, name)


def _path(path: str) -> bytes:
    return _text(path, "path")


def _file(path: _Path) -> bytes:
    if not isinstance(path, (str, os.PathLike)):
        raise IdmlError("path must be a str or os.PathLike.", Status.INVALID_ARGUMENT)
    return _text(os.fspath(path), "path")


def _int32(value: int, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not -(2**31) <= value < 2**31:
        raise IdmlError(f"{name} must be a 32-bit integer.", Status.INVALID_ARGUMENT)
    return value


def _document(value: "Document", name: str) -> int:
    if not isinstance(value, Document):
        raise IdmlError(f"{name} must be a Document.", Status.INVALID_ARGUMENT)
    return value._open_handle()
