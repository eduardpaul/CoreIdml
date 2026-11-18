"""Finds and loads the native library, and turns its status codes into exceptions."""

from __future__ import annotations

import ctypes
import os
import platform
import sys
from pathlib import Path
from typing import Optional, Union

from ._abi import ABI_VERSION, SIGNATURES, Status

#: Environment variable with the full path of the native library; it wins over every other location.
LIBRARY_ENV = "COREIDML_LIBRARY"

_lib: Optional[ctypes.CDLL] = None
_path: Optional[str] = None


class IdmlError(Exception):
    """A failed call into the library. ``code`` is a ``Status``, the same in every CoreIdml binding."""

    def __init__(self, message: str, code: Status = Status.FAILED):
        super().__init__(message)
        self.code = code


def library_file_name() -> str:
    """File name of the native library on this platform."""
    if sys.platform == "win32":
        return "coreidml.dll"
    if sys.platform == "darwin":
        return "libcoreidml.dylib"
    return "libcoreidml.so"


def _runtime_identifier() -> Optional[str]:
    """The .NET runtime identifier of this platform (the folder name under artifacts/native), if there is one."""
    system = {"win32": "win", "darwin": "osx", "linux": "linux"}.get(sys.platform)
    machine = {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine().lower())
    return f"{system}-{machine}" if system and machine else None


def _candidates() -> list[tuple[str, str]]:
    """Where to look, in order: (path, where it came from)."""
    name = library_file_name()
    here = Path(__file__).resolve().parent
    found: list[tuple[str, str]] = []

    packaged = here / "_lib" / name  # a platform wheel carries the library here
    if packaged.is_file():
        found.append((str(packaged), "the installed package"))

    # bindings/python/src/coreidml -> the repository root, when running from a source checkout
    root = here.parents[3] if len(here.parents) > 3 else None
    rid = _runtime_identifier()
    if root is not None and rid and (root / "include" / "coreidml.h").is_file():
        local = root / "artifacts" / "native" / rid / name
        if local.is_file():
            found.append((str(local), "the local build"))

    found.append((name, "the operating system's library search path"))
    return found


def load(path: Union[str, "os.PathLike[str]", None] = None) -> ctypes.CDLL:
    """Loads the native library and checks its ABI. Called on first use; call it first to choose the library.

    Without ``path`` the library is searched for: the ``COREIDML_LIBRARY`` environment variable, the installed
    package, a local build in ``artifacts/native`` of a source checkout, then the system's library search path.
    A path or the environment variable is the only place tried: if that file does not load, that is the error.
    """
    global _lib, _path
    explicit = os.fspath(path) if path is not None else os.environ.get(LIBRARY_ENV)
    if _lib is not None:
        if explicit is not None and os.path.abspath(explicit) != os.path.abspath(_path or ""):
            raise IdmlError(f"The native library is already loaded from {_path}; a process can use only one.", Status.INVALID_OPERATION)
        return _lib

    candidates = [(explicit, "the given path" if path is not None else f"the {LIBRARY_ENV} environment variable")] if explicit else _candidates()
    failures = []
    for candidate, source in candidates:
        try:
            library = ctypes.CDLL(candidate)
        except OSError as error:
            failures.append(f"  - {source}: {candidate} ({error})")
            continue
        _bind(library, candidate, source)
        _lib, _path = library, candidate
        return library

    rid = _runtime_identifier() or "this platform"
    raise IdmlError(
        f"Could not load the CoreIdml native library ({library_file_name()}). Tried:\n" + "\n".join(failures) + "\n"
        f"Set {LIBRARY_ENV} to its full path, or build it: dotnet publish src/CoreIdml.Native -c Release -r {rid}"
    )


def _bind(library: ctypes.CDLL, path: str, source: str) -> None:
    """Checks the ABI, then gives every function its signature (from the generated table)."""
    try:
        abi = library.coreidml_abi_version
    except AttributeError as error:
        raise IdmlError(f"{path} (from {source}) is not the CoreIdml native library: it has no coreidml_abi_version.") from error
    abi.restype, abi.argtypes = SIGNATURES["coreidml_abi_version"]
    if abi() != ABI_VERSION:
        raise IdmlError(
            f"The native library at {path} (from {source}) has ABI version {abi()}; this package needs {ABI_VERSION}. "
            "Use the library and the package from the same release."
        )
    for name, (restype, argtypes) in SIGNATURES.items():
        try:
            function = getattr(library, name)
        except AttributeError as error:
            raise IdmlError(f"The native library at {path} (from {source}) has no {name}; it is older than this package.") from error
        function.restype = restype
        function.argtypes = argtypes


def native() -> ctypes.CDLL:
    """The loaded library (loads it if needed)."""
    return _lib if _lib is not None else load()


def version() -> str:
    """Version of the native library."""
    text: bytes = native().coreidml_version()
    return text.decode("utf-8")


def library_path() -> str:
    """Path the native library was loaded from."""
    native()
    return _path or ""


def check(status: int) -> None:
    """Raises the ``IdmlError`` of a failed call; the message is read on the thread that made the call."""
    if status == Status.OK:
        return
    message = native().coreidml_last_error_message()
    try:
        code = Status(status)
    except ValueError:  # a status newer than this package
        code = Status.FAILED
    raise IdmlError(message.decode("utf-8") if message else f"The call failed with status {status}.", code)


def take_bytes(pointer: ctypes.c_void_p, length: int) -> bytes:
    """Copies a buffer the library handed out, and releases it."""
    try:
        return ctypes.string_at(pointer.value, length) if pointer.value and length else b""
    finally:
        native().coreidml_free(pointer)


def take_text(pointer: ctypes.c_void_p) -> str:
    """Copies a NUL-terminated UTF-8 string the library handed out, and releases it."""
    try:
        return ctypes.string_at(pointer.value).decode("utf-8") if pointer.value else ""
    finally:
        native().coreidml_free(pointer)
