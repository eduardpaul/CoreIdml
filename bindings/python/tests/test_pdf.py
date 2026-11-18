"""PDF: export to a file and to bytes (fonts, links, progress and cancelling) and import from a file and from bytes.

    python -m unittest discover -s bindings/python/tests
"""

import os
import struct
import sys
import tempfile
import unittest
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if os.environ.get("COREIDML_TEST_INSTALLED") != "1":  # else: the installed package and its bundled library
    sys.path.insert(0, str(ROOT / "bindings" / "python" / "src"))

from coreidml import Document, IdmlError, Status  # noqa: E402

FIXTURES = ROOT / "tests" / "fixtures"
FONTS = FIXTURES / "fonts"
ARTICLE = FIXTURES / "article-1photo.idml"
MAGAZINE = FIXTURES / "magazineA-edito.idml"  # two pages, set in Minion Pro and Gill Sans
#: Only the Liberation fonts of the fixtures: the result is the same on every machine.
LIBERATION = {"useSystemFonts": False, "fontDirectories": [str(FONTS)]}

SPEC = {
    "unit": "mm",
    "page": {"size": "A5"},
    "pages": [
        {"items": [{"type": "text", "name": "titulo", "x": 10, "y": 10, "width": 128, "height": 12, "text": "Ficha F-001"}]},
        {"items": [{"type": "text", "x": 10, "y": 10, "width": 128, "height": 12, "text": "Segunda página"}]},
    ],
}


def png(width: int, height: int) -> bytes:
    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    rows = b"".join(b"\0" + b"\x1e\x64\xc8" * width for _ in range(height))
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")


class Stop(Exception):
    pass


class PdfTestCase(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.temp = Path(temp.name)

    def assertFails(self, code, call):
        with self.assertRaises(IdmlError) as raised:
            call()
        self.assertEqual(raised.exception.code, code, str(raised.exception))
        return raised.exception


class ExportTests(PdfTestCase):
    def test_pdf_to_and_from_memory_with_progress_and_cancel(self):  # the earlier binding's test
        with Document.create(SPEC) as doc:
            calls = []
            pdf, report = doc.export_pdf_bytes({"useSystemFonts": False}, progress=lambda done, total: calls.append((done, total)) or True)
            self.assertEqual(pdf[:4], b"%PDF")
            self.assertEqual(report["pageCount"], 2)
            self.assertEqual(calls, [(1, 2), (2, 2)])
            file_report = doc.export_pdf(self.temp / "ficha.pdf", {"useSystemFonts": False, "maxImageDpi": 150})
            self.assertEqual(file_report["pageCount"], 2)
            self.assertFails(Status.CANCELLED, lambda: doc.export_pdf_bytes(progress=lambda done, total: False))
        imported, import_report = Document.import_pdf(pdf, {"imageDirectory": str(self.temp / "links")})
        with imported:
            self.assertEqual(imported.summary()["pageCount"], 2)
            self.assertGreater(import_report["textFrames"], 0)
        from_file, _ = Document.import_pdf(self.temp / "ficha.pdf", {"imageDirectory": str(self.temp / "links2")}, progress=lambda done, total: True)
        from_file.close()

    def test_export_to_a_file_with_the_liberation_fonts(self):
        path = self.temp / "revista ñ.pdf"
        with Document.open(MAGAZINE) as doc:
            report = doc.export_pdf(path, LIBERATION)

        pdf = path.read_bytes()
        self.assertEqual(pdf[:5], b"%PDF-")
        self.assertIn(b"%%EOF", pdf[-32:])
        self.assertEqual(report["pageCount"], 2)
        self.assertEqual(report["missingImages"], [])
        # Minion Pro and Gill Sans are not in the font folder: the report says which font stood in, and it is embedded.
        used = {font["requestedFamily"]: font["usedFont"] for font in report["missingFonts"]}
        self.assertTrue(used["Minion Pro"].startswith("Liberation Serif"), used)
        self.assertTrue(used["Gill Sans"].startswith("Liberation Sans"), used)
        self.assertIn(b"LiberationSerif", pdf)

    def test_export_to_bytes_of_chosen_pages(self):
        with Document.open(MAGAZINE) as doc:
            pdf, report = doc.export_pdf_bytes({**LIBERATION, "pages": [2]})
            whole, whole_report = doc.export_pdf_bytes(LIBERATION)
        self.assertEqual(pdf[:5], b"%PDF-")
        self.assertEqual((report["pageCount"], whole_report["pageCount"]), (1, 2))
        self.assertLess(len(pdf), len(whole))

    def test_without_fonts_the_standard_pdf_fonts_stand_in(self):
        with Document.open(MAGAZINE) as doc:
            _, report = doc.export_pdf_bytes({"useSystemFonts": False})
        self.assertTrue(all("not embedded" in font["reason"] for font in report["missingFonts"]), report["missingFonts"])

    def test_missing_images_are_reported_and_found_through_links(self):
        photo = self.temp / "photo.png"
        photo.write_bytes(png(40, 20))
        with Document.open(ARTICLE) as doc:
            _, missing = doc.export_pdf_bytes(LIBERATION)
            _, remapped = doc.export_pdf_bytes({**LIBERATION, "linkRemap": {"default.jpg": str(photo)}})
            doc.relink("u21a", photo, refit=True)
            _, relinked = doc.export_pdf_bytes(LIBERATION)
        self.assertEqual(len(missing["missingImages"]), 1)
        self.assertIn("default.jpg", missing["missingImages"][0])
        self.assertEqual(remapped["missingImages"], [])
        self.assertEqual(relinked["missingImages"], [])

    def test_progress_is_called_after_every_page(self):
        calls = []
        with Document.open(MAGAZINE) as doc:
            doc.export_pdf(self.temp / "a.pdf", LIBERATION, progress=lambda done, total: calls.append((done, total)))
        self.assertEqual(calls, [(1, 2), (2, 2)])

    def test_returning_false_cancels(self):
        calls = []

        def first_page_only(done, total):
            calls.append(done)
            return done < 1

        with Document.open(MAGAZINE) as doc:
            error = self.assertFails(Status.CANCELLED, lambda: doc.export_pdf_bytes(LIBERATION, progress=first_page_only))
            self.assertIn("cancel", str(error))
            self.assertFails(Status.CANCELLED, lambda: doc.export_pdf(self.temp / "c.pdf", LIBERATION, progress=lambda d, t: False))
            # The document is still usable.
            self.assertEqual(doc.export_pdf_bytes(LIBERATION)[1]["pageCount"], 2)
        self.assertEqual(calls, [1])

    def test_an_exception_in_the_callback_cancels_and_is_raised_after_the_call(self):
        calls = []

        def fail(done, total):
            calls.append(done)
            raise Stop(f"page {done} of {total}")

        with Document.open(MAGAZINE) as doc:
            with self.assertRaises(Stop) as raised:
                doc.export_pdf_bytes(LIBERATION, progress=fail)
            self.assertEqual(str(raised.exception), "page 1 of 2")
            with self.assertRaises(Stop):
                doc.export_pdf(self.temp / "d.pdf", LIBERATION, progress=fail)
            self.assertEqual(calls, [1, 1])
            self.assertEqual(doc.export_pdf_bytes(LIBERATION)[1]["pageCount"], 2)

    def test_export_errors(self):
        with Document.open(MAGAZINE) as doc:
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.export_pdf_bytes("{bad"))
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.export_pdf_bytes(progress=42))
            self.assertFails(Status.IO, lambda: doc.export_pdf(self.temp / "no" / "such" / "folder.pdf", LIBERATION))
        doc.close()
        self.assertFails(Status.INVALID_HANDLE, lambda: doc.export_pdf_bytes())


class ImportTests(PdfTestCase):
    @classmethod
    def setUpClass(cls):
        with Document.open(MAGAZINE) as doc:
            cls.pdf, _ = doc.export_pdf_bytes(LIBERATION)

    def test_import_from_bytes(self):
        document, report = Document.import_pdf(bytearray(self.pdf), {"imageDirectory": str(self.temp / "links")})
        with document:
            summary = document.summary()
            self.assertEqual(summary["pageCount"], 2)
            self.assertTrue(document.validate()["isValid"])
            text = " ".join(item.get("text", "") for page in summary["pages"] for item in page["items"])
        self.assertGreater(report["textFrames"], 0)
        self.assertIn("Liberation Serif", {font["family"] for font in report["fonts"]})
        self.assertTrue(text.strip())

    def test_import_from_a_file_with_options_and_progress(self):
        path = self.temp / "revista.pdf"
        path.write_bytes(self.pdf)
        calls = []
        document, report = Document.import_pdf(
            path, {"pages": [1], "imageDirectory": self.temp / "links", "fontDirectories": [FONTS]},
            progress=lambda done, total: calls.append((done, total)))
        with document:
            self.assertEqual(document.summary()["pageCount"], 1)
            imported = document.save()
        self.assertEqual(calls, [(1, 1)])
        self.assertTrue(all(font["available"] for font in report["fonts"]), report["fonts"])
        with Document.open(imported) as reopened:
            self.assertEqual(reopened.summary()["pageCount"], 1)

    def test_import_is_cancelled_by_the_callback(self):
        self.assertFails(Status.CANCELLED, lambda: Document.import_pdf(self.pdf, progress=lambda done, total: False))

        def fail(done, total):
            raise Stop()

        with self.assertRaises(Stop):
            Document.import_pdf(self.pdf, progress=fail)

    def test_import_errors(self):
        self.assertFails(Status.IO, lambda: Document.import_pdf(self.temp / "nope.pdf"))
        self.assertFails(Status.INVALID_ARGUMENT, lambda: Document.import_pdf(42))
        with self.assertRaises(IdmlError):
            Document.import_pdf(b"not a pdf")


if __name__ == "__main__":
    unittest.main()
