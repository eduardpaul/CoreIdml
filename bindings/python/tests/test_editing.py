"""Creating, reading and editing documents: specs, templates, stories, links, XML, pages, diff and validation.

    python -m unittest discover -s bindings/python/tests
"""

import json
import os
import re
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
ARTICLE = FIXTURES / "article-1photo.idml"
HEADLINE = "u188"  # the story of the headline of article-1photo.idml
PHOTO_LINK = "u21a"  # its only link, to a photo that is not on this machine

SPEC = {
    "unit": "mm",
    "page": {"size": "A5"},
    "paragraphStyles": [{"name": "Título", "size": 18}],
    "pages": [
        {"items": [
            {"type": "text", "name": "titulo", "x": 10, "y": 10, "width": 128, "height": 12, "text": "Ficha {{codigo}}", "style": "Título"},
            {"type": "text", "name": "cliente", "x": 10, "y": 30, "width": 128, "height": 12, "text": "-"},
        ]},
        {"items": [{"type": "text", "x": 10, "y": 10, "width": 128, "height": 12, "text": "Segunda página"}]},
    ],
}


def png(width: int, height: int) -> bytes:
    """A small solid-colour PNG, so that no image fixture is needed."""
    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    rows = b"".join(b"\0" + b"\xc8\x1e\x1e" * width for _ in range(height))
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")


def items(summary: dict, page: int = 0) -> list:
    return summary["pages"][page]["items"]


class TempTestCase(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.temp = Path(temp.name)

    def assertFails(self, code, call):
        with self.assertRaises(IdmlError) as raised:
            call()
        self.assertEqual(raised.exception.code, code, str(raised.exception))
        self.assertTrue(str(raised.exception))
        return raised.exception


class CreateAndFillTests(TempTestCase):
    def test_create_fill_edit_and_save(self):  # the earlier binding's end-to-end test
        with Document.create(SPEC) as doc:
            report = doc.fill({"text": {"codigo": "F-001", "cliente": "Ñandú S.A.", "nada": "?"}})
            self.assertEqual(report["filled"]["codigo"], 1)
            self.assertEqual(report["missing"], ["nada"])

            summary = doc.summary({"pages": [0], "sections": ["pages", "stories"]})
            texts = [item.get("text") for item in items(summary)]
            self.assertIn("Ficha F-001", texts)
            self.assertIn("Ñandú S.A.", texts)
            self.assertNotIn("links", summary)

            story_id = next(item["storyId"] for item in items(summary) if item.get("name") == "cliente")
            doc.set_run_text(story_id, 0, "43°16′38″N")
            self.assertEqual(doc.story(story_id)["text"], "43°16′38″N")
            self.assertEqual(doc.replace_text("Ficha", "Hoja"), 1)
            self.assertTrue(doc.validate()["isValid"])
            doc.save_file(self.temp / "ficha.idml")
            data = doc.save()

        with Document.open(data) as copy, Document.open(self.temp / "ficha.idml") as saved:
            self.assertTrue(copy.diff(saved)["identical"])
            self.assertEqual(saved.import_pages(copy, [1]), 1)
            self.assertEqual(saved.summary()["pageCount"], 3)

    def test_without_a_spec_the_document_is_one_blank_a4_page(self):
        with Document.create() as doc:
            summary = doc.summary()
        self.assertEqual(summary["pageCount"], 1)
        self.assertAlmostEqual(summary["pageWidth"], 595.28, places=1)
        self.assertAlmostEqual(summary["pageHeight"], 841.89, places=1)

    def test_a_spec_may_be_json_text_and_a_bad_one_is_refused(self):
        with Document.create(json.dumps(SPEC)) as doc:
            self.assertEqual(doc.summary()["pageCount"], 2)
        self.assertFails(Status.INVALID_ARGUMENT, lambda: Document.create("{not json"))
        self.assertFails(Status.INVALID_ARGUMENT, lambda: Document.create(42))
        self.assertFails(Status.INVALID_ARGUMENT, lambda: Document.create({"page": object()}))

    def test_apply_appends_the_pages_and_styles_of_a_spec(self):
        with Document.create(SPEC) as doc:
            doc.apply({"characterStyles": [{"name": "Nota", "italic": True}],
                       "pages": [{"items": [{"type": "text", "name": "extra", "x": 10, "y": 10, "width": 50, "height": 10, "text": "Añadida"}]}]})
            summary = doc.summary()
        self.assertEqual(summary["pageCount"], 3)
        self.assertEqual(items(summary, 2)[0]["text"], "Añadida")
        self.assertIn("Nota", summary["characterStyles"])

    def test_fill_takes_json_text_and_reports_every_key(self):
        with Document.open(ARTICLE) as doc:
            report = doc.fill('{"text": {"headline": "Titular nuevo", "nowhere": "x"}}')
            self.assertEqual(report["filled"]["headline"], 1)
            self.assertEqual(report["missing"], ["nowhere"])
            self.assertEqual(doc.story(HEADLINE)["text"], "Titular nuevo")
        with Document.create() as doc:
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.fill(None))

    def test_replace_text_counts_the_replacements(self):
        with Document.open(ARTICLE) as doc:
            self.assertEqual(doc.replace_text("HEADLINE", "TITULAR"), 1)
            self.assertEqual(doc.replace_text("HEADLINE", "TITULAR"), 0)
            self.assertEqual(doc.story(HEADLINE)["text"], "THE TITULAR HERE")
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.replace_text("", "x"))
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.replace_text("a\0b", "x"))


class ReadingTests(TempTestCase):
    def test_summary_options_limit_and_shape_it(self):
        with Document.open(ARTICLE) as doc:
            full = doc.summary()
            self.assertEqual(full["pageCount"], 1)
            self.assertEqual([link["id"] for link in full["links"]], [PHOTO_LINK])
            self.assertIn("MyBoldStyle", full["characterStyles"])
            self.assertIn("headline", full["tags"])

            short = doc.summary({"maxTextLength": 10, "includeResources": False, "sections": ["pages"]})
            self.assertEqual(set(short) & {"stories", "links", "characterStyles", "swatches"}, set())
            long_texts = [item["text"] for item in items(full) if len(item.get("text", "")) > 10]
            self.assertTrue(long_texts)
            for item in items(short):
                self.assertLessEqual(len(item.get("text", "")), 11)

            with_runs = doc.summary({"includeRuns": True, "sections": ["stories"]})
            story = next(story for story in with_runs["stories"] if story["id"] == HEADLINE)
            self.assertEqual(story["runs"][0]["text"], "THE HEADLINE HERE")

            self.assertEqual(doc.summary('{"sections": ["links"]}')["links"][0]["id"], PHOTO_LINK)
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.summary("{bad"))

    def test_story_lists_its_runs_and_an_unknown_story_is_not_found(self):
        with Document.open(ARTICLE) as doc:
            story = doc.story(HEADLINE)
            self.assertEqual(story["text"], "THE HEADLINE HERE")
            self.assertEqual(story["frames"], ["u185"])
            self.assertEqual([run["index"] for run in story["runs"]], [0])
            self.assertFails(Status.NOT_FOUND, lambda: doc.story("nope"))

    def test_validate_reports_a_valid_document(self):
        with Document.open(FIXTURES / "magazineA-edito.idml") as doc:
            self.assertEqual(doc.validate(), {"isValid": True, "issues": []})

    def test_diff_lists_the_changes_between_two_documents(self):
        with Document.open(ARTICLE) as before, Document.open(ARTICLE) as after:
            self.assertEqual(before.diff(after), {"identical": True, "changes": []})
            after.replace_text("HEADLINE", "TITULAR")
            after.relink(PHOTO_LINK, "file:///photos/new.jpg")

            diff = before.diff(after)
            self.assertFalse(diff["identical"])
            self.assertIn("link-changed", [change["kind"] for change in diff["changes"]])
            self.assertGreaterEqual(len(diff["changes"]), 2)
            self.assertFails(Status.INVALID_ARGUMENT, lambda: before.diff("after.idml"))


class StoryEditingTests(TempTestCase):
    def test_set_run_text_keeps_the_formatting_of_the_run(self):
        with Document.open(ARTICLE) as doc:
            before = doc.story(HEADLINE)["runs"][0]
            doc.set_run_text(HEADLINE, 0, "Ñandú, 43°16′38″N")
            after = doc.story(HEADLINE)["runs"][0]
            doc.set_run_text(HEADLINE, 0, "forced", force=True)
            forced = doc.story(HEADLINE)["text"]

        self.assertEqual(after["text"], "Ñandú, 43°16′38″N")
        self.assertEqual((after["paragraphStyle"], after["characterStyle"]), (before["paragraphStyle"], before["characterStyle"]))
        self.assertEqual(forced, "forced")

    def test_set_run_text_on_something_that_does_not_exist_is_not_found(self):
        with Document.open(ARTICLE) as doc:
            error = self.assertFails(Status.NOT_FOUND, lambda: doc.set_run_text("nope", 0, "x"))
            self.assertIn("nope", str(error))
            self.assertFails(Status.NOT_FOUND, lambda: doc.set_run_text(HEADLINE, 99, "x"))
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.set_run_text(HEADLINE, "0", "x"))
            self.assertEqual(doc.story(HEADLINE)["text"], "THE HEADLINE HERE")


class RelinkTests(TempTestCase):
    def test_relink_to_a_uri(self):
        with Document.open(ARTICLE) as doc:
            doc.relink(PHOTO_LINK, "file:///photos/new.jpg")
            self.assertEqual(doc.summary({"sections": ["links"]})["links"][0]["uri"], "file:///photos/new.jpg")

    def test_relink_to_a_local_image_and_refit_it(self):
        photo = self.temp / "foto ñ.png"
        photo.write_bytes(png(40, 20))
        with Document.open(ARTICLE) as doc:
            doc.relink(PHOTO_LINK, photo, refit=True)
            link = doc.summary({"sections": ["links"]})["links"][0]
        self.assertTrue(link["uri"].startswith("file:"), link["uri"])
        self.assertTrue(link["uri"].endswith("foto ñ.png") or link["uri"].endswith("foto%20%C3%B1.png"), link["uri"])
        self.assertIn("PNG", link["format"])

    def test_relink_errors(self):
        with Document.open(ARTICLE) as doc:
            self.assertFails(Status.NOT_FOUND, lambda: doc.relink("nope", "file:///photos/new.jpg"))
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.relink(PHOTO_LINK, ""))
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.relink(PHOTO_LINK, 42))
            self.assertIn("default.jpg", doc.summary({"sections": ["links"]})["links"][0]["uri"])


class XmlTests(TempTestCase):
    def test_xml_round_trip(self):  # the earlier binding's test
        spec = {"pages": [{"items": [{"type": "text", "tag": "titulo", "x": 10, "y": 10, "width": 200, "height": 20, "text": "Antes"}]}]}
        with Document.create(spec) as doc:
            self.assertEqual(doc.xml_structure()["children"][0]["tag"], "titulo")
            self.assertIn("<titulo>Antes</titulo>", doc.export_xml())
            report = doc.import_xml("<Root><titulo>Después</titulo></Root>")
            self.assertGreaterEqual(report["updated"], 1)
            self.assertIn("Después", doc.export_xml())

    def test_the_structure_of_a_template(self):
        with Document.open(ARTICLE) as doc:
            root = doc.xml_structure()
            module = root["children"][0]
            self.assertEqual((root["tag"], root["path"]), ("Root", "/Root"))
            self.assertEqual(module["path"], "/Root/module")
            headline = next(child for child in module["children"] if child["tag"] == "headline")
            self.assertEqual((headline["boundTo"], headline["text"]), ("story", "THE HEADLINE HERE"))

            self.assertEqual(doc.export_xml("/Root/module/headline"), "<headline>THE HEADLINE HERE</headline>")
            self.assertFails(Status.NOT_FOUND, lambda: doc.export_xml("/Root/nope"))

    def test_a_document_without_an_xml_structure(self):
        with Document.create() as doc:
            backing = doc.read_text("XML/BackingStory.xml")
            doc.write_part("XML/BackingStory.xml", re.sub(r"<XMLElement[^>]*/>", "", backing))
            self.assertIsNone(doc.xml_structure())
            with self.assertRaises(IdmlError):
                doc.export_xml()

    def test_import_xml_at_an_element_with_simpleidml_semantics(self):
        xml = (FIXTURES / "xml" / "article-1photo_import-xml.xml").read_text(encoding="utf-8")
        with Document.open(ARTICLE) as doc:
            report = doc.import_xml(xml, at="/Root/module")
            self.assertGreaterEqual(report["updated"], 1)
            self.assertEqual(report["warnings"], [])
            self.assertEqual(doc.story(HEADLINE)["text"], "The Life Aquatic with Steve Zissou")
            self.assertIn("<headline>The Life Aquatic with Steve Zissou</headline>", doc.export_xml())

    def test_import_xml_takes_bytes_and_options(self):
        with Document.open(ARTICLE) as doc:
            report = doc.import_xml("<headline>Hola <b>mundo</b></headline>".encode("utf-8"), "/Root/module/headline",
                                    {"characterStyles": {"b": "MyBoldStyle"}})
            runs = doc.story(HEADLINE)["runs"]
        self.assertEqual(report["unmatched"], [])
        self.assertEqual([(run["text"], run["characterStyle"]) for run in runs],
                         [("Hola ", "[No character style]"), ("mundo", "MyBoldStyle")])

    def test_import_xml_errors(self):
        with Document.open(ARTICLE) as doc:
            self.assertFails(Status.INVALID_FORMAT, lambda: doc.import_xml("<not xml"))
            self.assertFails(Status.NOT_FOUND, lambda: doc.import_xml("<x/>", at="/Root/nope"))
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.import_xml(None))


class ImportPagesTests(TempTestCase):
    def test_pages_of_another_document_are_appended(self):
        with Document.create(SPEC) as doc, Document.open(FIXTURES / "magazineA-edito.idml") as magazine:
            self.assertEqual(doc.import_pages(magazine), 2)
            self.assertEqual(doc.import_pages(magazine, (1,)), 1)
            summary = doc.summary()
            self.assertEqual(summary["pageCount"], 5)
            self.assertTrue(doc.validate()["isValid"])
            self.assertTrue(set(magazine.summary()["paragraphStyles"]) <= set(summary["paragraphStyles"]))

    def test_a_document_imports_its_own_pages(self):
        with Document.create(SPEC) as doc:
            self.assertEqual(doc.import_pages(doc, [1, 0]), 2)
            self.assertEqual(doc.import_pages(doc), 4)
            summary = doc.summary()
            self.assertEqual(summary["pageCount"], 8)
            self.assertEqual(items(summary, 2)[0]["text"], "Segunda página")
            self.assertTrue(doc.validate()["isValid"])

    def test_import_pages_errors(self):
        with Document.create(SPEC) as doc:
            other = Document.create()
            other.close()
            self.assertFails(Status.NOT_FOUND, lambda: doc.import_pages(doc, [99]))
            self.assertFails(Status.INVALID_HANDLE, lambda: doc.import_pages(other))
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.import_pages(doc, ["0"]))
            self.assertFails(Status.INVALID_ARGUMENT, lambda: doc.import_pages("other.idml"))
            self.assertEqual(doc.summary()["pageCount"], 2)


class ErrorTests(TempTestCase):
    def test_errors_carry_the_native_message_and_code(self):  # the earlier binding's test
        error = self.assertFails(Status.IO, lambda: Document.open(self.temp / "does-not-exist.idml"))
        self.assertIn("does-not-exist.idml", str(error))
        with Document.create() as doc:
            self.assertFails(Status.NOT_FOUND, lambda: doc.set_run_text("nope", 0, "x"))
        doc = Document.create()
        doc.close()
        doc.close()
        for call in (doc.summary, doc.validate, doc.xml_structure, lambda: doc.fill({}), lambda: doc.story(HEADLINE)):
            self.assertFails(Status.INVALID_HANDLE, call)

    def test_paths_may_be_strings_or_path_objects(self):
        with Document.open(str(ARTICLE)) as from_text, Document.open(ARTICLE) as from_path:
            from_text.save_file(str(self.temp / "a.idml"))
            from_path.save_file(self.temp / "b.idml")
        self.assertEqual((self.temp / "a.idml").read_bytes(), (self.temp / "b.idml").read_bytes())
        self.assertFails(Status.INVALID_ARGUMENT, lambda: Document.open(str(ARTICLE) + "\0"))  # would be cut at the NUL


if __name__ == "__main__":
    unittest.main()
