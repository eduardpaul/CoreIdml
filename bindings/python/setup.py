"""Makes the wheel a platform wheel when it carries the native library.

Everything else is declared in pyproject.toml. The package itself is pure Python (ctypes), so the same wheel works on
every Python 3 of a platform: the tag is py3-none-<platform>. Without a library in coreidml/_lib/ the wheel is a
plain py3-none-any one, for which COREIDML_LIBRARY names the library.

Build platform wheels with tools/build_wheel.py.
"""

import os
from pathlib import Path

from setuptools import setup
from setuptools.command.bdist_wheel import bdist_wheel

LIBRARY_FOLDER = Path(__file__).parent / "src" / "coreidml" / "_lib"


def has_native_library() -> bool:
    return LIBRARY_FOLDER.is_dir() and any(LIBRARY_FOLDER.glob("*coreidml*"))


class PlatformWheel(bdist_wheel):
    def finalize_options(self):
        super().finalize_options()
        if has_native_library():
            self.root_is_pure = False

    def get_tag(self):
        python, abi, platform = super().get_tag()
        if not has_native_library():
            return python, abi, platform
        # COREIDML_WHEEL_PLATFORM: the tag of the platform the library was built for (e.g. manylinux_2_34_x86_64),
        # when it is not simply the platform the wheel is built on.
        return "py3", "none", os.environ.get("COREIDML_WHEEL_PLATFORM") or platform


setup(cmdclass={"bdist_wheel": PlatformWheel})
