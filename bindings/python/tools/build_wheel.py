"""Builds the wheel of one platform: the Python package with the native library of that platform inside.

    python bindings/python/tools/build_wheel.py                      (the platform this runs on)
    python bindings/python/tools/build_wheel.py --rid linux-x64 --plat-name manylinux_2_28_x86_64 --version 1.2.0-rc.1

The library is taken from artifacts/native/<rid>/ (dotnet publish src/CoreIdml.Native -c Release -r <rid>) and the
wheel is written to artifacts/python/. A Linux wheel for PyPI needs --plat-name: a manylinux tag that matches the
glibc the library was built against.
"""

from __future__ import annotations

import argparse
import os
import re
import platform
import shutil
import subprocess
import sys
from pathlib import Path

PACKAGE = Path(__file__).resolve().parents[1]
ROOT = PACKAGE.parents[1]
LIBRARY_FOLDER = PACKAGE / "src" / "coreidml" / "_lib"

LIBRARY_NAMES = {"win": "coreidml.dll", "linux": "libcoreidml.so", "osx": "libcoreidml.dylib"}
# The wheel tag of a runtime identifier when it is unambiguous; Linux and macOS depend on how the library was built.
PLATFORM_TAGS = {"win-x64": "win_amd64", "win-arm64": "win_arm64"}


def host_rid() -> str:
    system = {"win32": "win", "darwin": "osx", "linux": "linux"}[sys.platform]
    machine = {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}[platform.machine().lower()]
    return f"{system}-{machine}"


def python_version(version: str) -> str:
    """The release version (1.2.0, 1.2.0-rc.1, 1.2.0-ci.7) as Python spells it (PEP 440), as eng/set-version.mjs does."""
    match = re.fullmatch(r"(\d+\.\d+\.\d+)(?:-(alpha|beta|rc|ci)\.(\d+))?", version)
    if not match:
        raise SystemExit(f"--version {version}: expected major.minor.patch[-alpha|beta|rc|ci.N]")
    base, kind, number = match.groups()
    suffix = {None: "", "alpha": f"a{number}", "beta": f"b{number}", "rc": f"rc{number}", "ci": f".dev{number}"}[kind]
    return base + suffix


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--rid", default=host_rid(), help="the .NET runtime identifier of the library (default: this platform)")
    parser.add_argument("--plat-name", help="the wheel's platform tag (default: from --rid, or the platform this runs on)")
    parser.add_argument("--out", default=str(ROOT / "artifacts" / "python"), help="where the wheel is written")
    parser.add_argument("--version", help="the release version to stamp into the wheel (default: pyproject.toml's)")
    arguments = parser.parse_args()

    library = ROOT / "artifacts" / "native" / arguments.rid / LIBRARY_NAMES[arguments.rid.split("-")[0]]
    if not library.is_file():
        print(f"No library at {library}. Run: dotnet publish src/CoreIdml.Native -c Release -r {arguments.rid}", file=sys.stderr)
        return 1

    environment = dict(os.environ)
    tag = arguments.plat_name or PLATFORM_TAGS.get(arguments.rid)
    if tag:
        environment["COREIDML_WHEEL_PLATFORM"] = tag
    elif arguments.rid != host_rid():
        print(f"--plat-name is required for {arguments.rid} when building on {host_rid()}.", file=sys.stderr)
        return 1

    pyproject = PACKAGE / "pyproject.toml"
    original_pyproject = pyproject.read_bytes().decode("utf-8")
    if arguments.version:
        stamped, count = re.subn(r'(?m)^version = "[^"]+"$', f'version = "{python_version(arguments.version)}"', original_pyproject)
        if count != 1:
            print("pyproject.toml has no version line to stamp.", file=sys.stderr)
            return 1
        pyproject.write_bytes(stamped.encode("utf-8"))

    shutil.rmtree(LIBRARY_FOLDER, ignore_errors=True)
    LIBRARY_FOLDER.mkdir(parents=True)
    try:
        shutil.copy2(library, LIBRARY_FOLDER / library.name)
        # A stale build/ folder would carry the library of the previous platform into this wheel.
        shutil.rmtree(PACKAGE / "build", ignore_errors=True)
        subprocess.run(
            [sys.executable, "-m", "pip", "wheel", str(PACKAGE), "--no-deps", "--wheel-dir", arguments.out],
            check=True,
            env=environment,
        )
    finally:
        pyproject.write_bytes(original_pyproject.encode("utf-8"))
        # The source tree goes back to having no library: tests and other platforms must not pick this one up.
        shutil.rmtree(LIBRARY_FOLDER, ignore_errors=True)
        shutil.rmtree(PACKAGE / "build", ignore_errors=True)
    print(f"Wheel for {arguments.rid} -> {arguments.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
