"""Fills the plugin's data sheet template with the published coreidml package from PyPI, and checks the result.

    python -m venv .venv && .venv/bin/pip install -r requirements.txt     (Windows: .venv\\Scripts\\pip)
    .venv/bin/python demo.py

Writes out/datasheet.idml and out/datasheet.pdf, and exits with 1 when anything is not as expected.
"""

import json
import sys
from pathlib import Path

import coreidml
from coreidml import Document

HERE = Path(__file__).resolve().parent
SAMPLES = HERE.parents[2] / "plugins" / "coreidml" / "samples"
OUT = HERE / "out"
failures = []


def check(condition, what):
    print(("ok    " if condition else "FAIL  ") + what)
    if not condition:
        failures.append(what)


OUT.mkdir(exist_ok=True)
print("coreidml", coreidml.version())
data = json.loads((SAMPLES / "datasheet-data.json").read_text(encoding="utf-8"))

# Fill the template, validate it and save it as a new file.
with Document.open(SAMPLES / "datasheet-template.idml") as doc:
    filled = doc.fill(data)
    check(sorted(filled["filled"]) == sorted(data["text"]) and not filled["missing"], f"fill: {filled}")
    check(doc.validate()["isValid"], "the filled document is valid")
    doc.save_file(OUT / "datasheet.idml")

# Open the saved file again: the data is in it.
with Document.open(OUT / "datasheet.idml") as doc:
    texts = [story.get("text", "") for story in doc.summary().get("stories", [])]
    check(any("Trail Lamp X200" in text for text in texts), "the saved file has the product name")
    check(any("1 March 2027" in text for text in texts), "and the {{date}} placeholder filled")
    report = doc.export_pdf(OUT / "datasheet.pdf")
    check(report["pageCount"] == 1 and not report["oversetStories"], f"PDF: {report['pageCount']} page, nothing overset")

pdf = (OUT / "datasheet.pdf").read_bytes()
check(pdf.startswith(b"%PDF-"), f"datasheet.pdf is a PDF ({len(pdf)} bytes)")

# And the PDF back into an editable document.
imported, import_report = Document.import_pdf(pdf)
with imported:
    check(imported.summary()["pageCount"] == 1, f"PDF import: {import_report['textFrames']} text frames")

print("all checks passed" if not failures else f"{len(failures)} checks failed")
sys.exit(1 if failures else 0)
