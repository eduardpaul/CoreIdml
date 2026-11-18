# Real-sample tests (opt-in, never committed)

Real InDesign exports supplied by the user are tested outside git. Put them in a folder **outside the repository** and
point the tests at it:

```sh
COREIDML_PDF_SAMPLES_DIR=/path/to/samples dotnet test tests/CoreCoreIdml.Pdf.Tests --logger "console;verbosity=detailed"
```

Without the variable, the tests look in this folder (everything here except this README and `.gitignore` is
ignored by git); when no sample is found they report "no real samples" and do nothing. Committed tests use only
synthetic documents.

## Layout (drop-in, no code changes)

```
<samples dir>/<name>/
  source.idml            the document (any single .idml); with reference.pdf it is an export case
  reference.pdf          the PDF InDesign exported from it (without an .idml: a PDF to import)
  reference-<n>.png      optional instead of reference.pdf: page n rendered at `dpi`
  fonts/                 optional: fonts for this sample
  links/                 optional: linked images/PDFs, found by file name (a symlink to the real Links folder works)
  sample.json            optional settings (paths relative to the sample folder)
```

* **Export case** (`.idml` + `reference.pdf`): the IDML is rendered with CoreIdml.Pdf; both PDFs are rasterized with
  pdf_oxide at `dpi` and compared page by page. The same pair also runs the import check against ground truth
  (`ImportGroundTruthTests`): the reference PDF is imported and scored against the original IDML (word recall,
  frame positions, styles).
* **Import case** (only a PDF): PDF → IDML → PDF, compared with the original PDF.

`sample.json` (all fields optional):

```jsonc
{
  "dpi": 96,                 // rasterization resolution
  "tolerance": 64,           // 0-255: a pixel differs when a channel differs by more than this (absorbs anti-aliasing)
  "threshold": 0.05,         // fail when more than this fraction of a page's pixels differ
  "pages": [1, 2],           // 1-based pages to compare
  "systemFonts": false,      // also search the machine's fonts
  "fontDirectory": "../shared-fonts",     // extra font folder, relative to the sample
  "linkDirectories": ["links-extra"],     // extra link folders (relative or absolute)
  "substitutions": { "Some Family": "Liberation Serif" },
  "skip": "reason"           // keep the sample but do not run it
}
```

Free fonts for samples can be fetched with `idml/tools/fetch-free-fonts.sh` (OFL fonts from github.com/google/fonts,
instanced to static weights).

Output per sample goes to `tests/CoreIdml.Pdf.Tests/bin/<config>/net10.0/out/visual/<name>/` (ignored by git):
`ours.pdf`, `ours-<n>.png`, `reference-<n>.png` and `diff-<n>.png` (a heatmap: orange to red over a faded
reference). The test log prints per page: the pixel metric, where the differences are (text / images / other, from
the reference PDF's content), word-position statistics, word-width ratio per font, missing fonts and links, and
overset stories. `COREIDML_PDF_WORDS=<page>` lists every matched word with its offset.
