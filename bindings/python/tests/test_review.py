"""Regressions for the defects found by the code review of this binding.

    python -m unittest discover -s bindings/python/tests
"""

import os
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if os.environ.get("COREIDML_TEST_INSTALLED") != "1":  # else: the installed package and its bundled library
    sys.path.insert(0, str(ROOT / "bindings" / "python" / "src"))

from coreidml import Document, IdmlError, Status  # noqa: E402
from coreidml._native import native  # noqa: E402

ARTICLE = ROOT / "tests" / "fixtures" / "article-1photo.idml"


class ErrorBeforeRelease(unittest.TestCase):
    """coreidml.h: the error message is valid only until the next call on the thread, coreidml_free included."""

    def setUp(self) -> None:
        self.lib = native()
        self.calls: list[str] = []
        self.free, self.message = self.lib.coreidml_free, self.lib.coreidml_last_error_message

        def free(pointer):  # type: ignore[no-untyped-def]
            self.calls.append("free")
            return self.free(pointer)

        def message():  # type: ignore[no-untyped-def]
            self.calls.append("message")
            return self.message()

        self.lib.coreidml_free, self.lib.coreidml_last_error_message = free, message

    def tearDown(self) -> None:
        self.lib.coreidml_free, self.lib.coreidml_last_error_message = self.free, self.message

    def assert_message_first(self) -> None:
        self.assertIn("message", self.calls)
        self.assertNotIn("free", self.calls[: self.calls.index("message")], self.calls)

    def test_a_failed_export_to_bytes_reads_its_error_first(self) -> None:
        with Document.open(ARTICLE) as document:
            with self.assertRaises(IdmlError) as raised:
                document.export_pdf_bytes({"jpegQuality": "high"})
        self.assertEqual(raised.exception.code, Status.INVALID_ARGUMENT)
        self.assert_message_first()

    def test_a_failed_export_to_a_file_reads_its_error_first(self) -> None:
        with Document.open(ARTICLE) as document:
            with self.assertRaises(IdmlError):
                document.export_pdf(ROOT / "artifacts" / "never-written.pdf", {"jpegQuality": "high"})
        self.assert_message_first()

    def test_a_failed_import_reads_its_error_first(self) -> None:
        with self.assertRaises(IdmlError) as raised:
            Document.import_pdf(b"not a PDF at all")
        self.assertEqual(raised.exception.code, Status.INVALID_FORMAT)
        self.assert_message_first()

    def test_successful_calls_still_release_what_they_return(self) -> None:
        with Document.open(ARTICLE) as document:
            pdf, report = document.export_pdf_bytes({"useSystemFonts": False, "fontDirectories": [str(ROOT / "tests" / "fixtures" / "fonts")]})
        self.assertTrue(pdf.startswith(b"%PDF-"))
        self.assertEqual(report["pageCount"], 1)
        self.assertEqual(self.calls.count("free"), 2)


if __name__ == "__main__":
    unittest.main()
