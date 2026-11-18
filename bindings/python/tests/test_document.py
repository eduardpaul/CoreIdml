"""Runs against the native library in artifacts/native/<rid> (dotnet publish src/CoreIdml.Native -c Release -r <rid>)
and the fixtures every binding shares.

    python -m unittest discover -s bindings/python/tests

With COREIDML_TEST_INSTALLED=1 the tests import the installed package (a wheel and the library it carries) instead
of bindings/python/src; the fixtures are still read from this checkout.
"""

import os
import re
import sys
import tempfile
import threading
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
INSTALLED = os.environ.get("COREIDML_TEST_INSTALLED") == "1"
if not INSTALLED:
    sys.path.insert(0, str(ROOT / "bindings" / "python" / "src"))

import coreidml  # noqa: E402
from coreidml import Document, IdmlError, Status  # noqa: E402

FIXTURE = ROOT / "tests" / "fixtures" / "article-1photo.idml"
STORY = "Stories/Story_u188.xml"


class LibraryTests(unittest.TestCase):
    def test_the_library_loads_and_reports_its_version(self):
        self.assertRegex(coreidml.version(), r"^\d+\.\d+\.\d+")
        self.assertEqual(coreidml.ABI_VERSION, 1)
        self.assertTrue(os.path.isfile(coreidml.library_path()), coreidml.library_path())

    @unittest.skipUnless(INSTALLED and not os.environ.get("COREIDML_LIBRARY"), "testing the installed package")
    def test_the_installed_package_is_tested_with_the_library_it_carries(self):
        package = Path(coreidml.__file__).resolve().parent
        self.assertNotEqual(package, (ROOT / "bindings" / "python" / "src" / "coreidml").resolve())
        self.assertEqual(Path(coreidml.library_path()).resolve().parent, package / "_lib")

    def test_a_second_library_is_refused(self):
        with self.assertRaises(IdmlError) as raised:
            coreidml.load(os.path.join(tempfile.gettempdir(), "another-coreidml.dll"))
        self.assertEqual(raised.exception.code, Status.INVALID_OPERATION)


class DocumentTests(unittest.TestCase):
    def test_a_document_is_opened_and_described(self):
        with Document.open(FIXTURE) as doc:
            info = doc.info()

        self.assertEqual(info["domVersion"], "10.0")
        self.assertEqual(info["spreads"], ["Spreads/Spread_ud6.xml"])
        self.assertEqual(info["masterSpreads"], ["MasterSpreads/MasterSpread_ua5.xml"])
        self.assertEqual(len(info["stories"]), 3)
        self.assertEqual(len(info["parts"]), 16)
        self.assertEqual(info["parts"][0], {"path": "mimetype", "length": 43, "modified": False})

    def test_a_part_is_read_replaced_and_saved_and_the_other_parts_stay_byte_for_byte(self):
        with Document.open(FIXTURE.read_bytes()) as doc:
            original = doc.read_text(STORY)
            edited = original.replace("<Story ", '<Story Marker="edited" ')
            self.assertNotEqual(edited, original)

            doc.write_part(STORY, edited)
            saved = doc.save()
            modified = [part["path"] for part in doc.info()["parts"] if part["modified"]]

        self.assertEqual(modified, [STORY])
        with Document.open(FIXTURE) as before, Document.open(saved) as after:
            self.assertEqual(after.read_text(STORY), edited)
            for part in before.info()["parts"]:
                if part["path"] != STORY:
                    self.assertEqual(after.read_part(part["path"]), before.read_part(part["path"]), part["path"])

    def test_a_document_saves_to_a_file_with_a_name_that_is_not_ascii(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "ficha-ñ-日本.idml"
            with Document.open(FIXTURE) as doc:
                doc.save_file(path)
            with Document.open(path) as saved:
                self.assertEqual(len(saved.info()["parts"]), 16)

    def test_binary_parts_and_every_kind_of_bytes_are_accepted(self):
        data = bytes([0, 1, 2, 0, 255])
        with Document.open(bytearray(FIXTURE.read_bytes())) as doc:
            doc.write_part("Extra/data.bin", memoryview(data))
            doc.write_part("Extra/empty.bin", b"")

            self.assertEqual(doc.read_part("Extra/data.bin"), data)
            self.assertEqual(doc.read_part("Extra/empty.bin"), b"")

    def test_failures_are_idml_errors_with_the_status_codes_of_the_library(self):
        with self.assertRaises(IdmlError) as not_a_zip:
            Document.open(b"this is not a zip file")
        self.assertEqual(not_a_zip.exception.code, Status.INVALID_FORMAT)
        # The text of .NET's own message, not a resource key such as "CDCorrupt".
        self.assertTrue(re.match(r"^Not an IDML package: \S+ \S+", str(not_a_zip.exception)), str(not_a_zip.exception))

        with self.assertRaises(IdmlError) as missing_file:
            Document.open(Path(tempfile.gettempdir()) / "no-such-file.idml")
        self.assertEqual(missing_file.exception.code, Status.IO)

        with Document.open(FIXTURE) as doc:
            for call, code in [
                (lambda: doc.read_part("Stories/Nope.xml"), Status.NOT_FOUND),
                (lambda: doc.write_part("bad\\path.xml", "x"), Status.INVALID_ARGUMENT),
                (lambda: doc.write_part(STORY, 42), Status.INVALID_ARGUMENT),
                (lambda: Document.open(42), Status.INVALID_ARGUMENT),
            ]:
                with self.assertRaises(IdmlError) as raised:
                    call()
                self.assertEqual(raised.exception.code, code)
            # A failure does not poison the document.
            self.assertEqual(len(doc.info()["parts"]), 16)

    def test_a_closed_document_refuses_calls_and_closing_twice_is_harmless(self):
        doc = Document.open(FIXTURE)

        doc.close()
        doc.close()

        self.assertTrue(doc.closed)
        with self.assertRaises(IdmlError) as raised:
            doc.info()
        self.assertEqual(raised.exception.code, Status.INVALID_HANDLE)

    def test_a_document_is_usable_from_several_threads(self):
        errors = []
        with Document.open(FIXTURE) as doc:

            def work(index):
                try:
                    for n in range(25):
                        doc.write_part(f"Extra/{index}.txt", f"{index}:{n}")
                        doc.info()
                except Exception as error:  # reported by the assertion below
                    errors.append(error)

            threads = [threading.Thread(target=work, args=(index,)) for index in range(6)]
            for thread in threads:
                thread.start()
            for thread in threads:
                thread.join()

            self.assertEqual(errors, [])
            self.assertEqual(len(doc.info()["parts"]), 16 + 6)


if __name__ == "__main__":
    unittest.main()
