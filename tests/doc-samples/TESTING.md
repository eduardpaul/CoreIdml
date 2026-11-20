# Testing a feature against the InDesign samples

How to prove that CoreIdml handles an InDesign feature, using the reference package in this folder: which checks to
run, what counts as a pass for each sample, and the traps in the data. Read this before you open a sample to work on a
feature, and before you trust a pixel diff.

The package itself (what is in each folder, how it was generated, the PDF preset) is described in
[README.md](README.md). The request it answers is [docs/features/sample-request.md](../../docs/features/sample-request.md),
and the feature list it serves is [docs/features/feature-matrix.md](../../docs/features/feature-matrix.md).

> The package is **internal and never committed**: `.gitignore` ignores everything in this folder except this file.
> This file is not part of the generated package; commit it, and restore it from git if replacing the package deletes
> it.

## 1. What the package is, and what it is not

74 IDML + PDF pairs exported by **InDesign 20.0.1 (2025)**, plus two notes-only folders (`01-doc-flex`,
`08-para-rtl-cjk`). `05-image-formats` was removed because its reference PDF embedded a commercial font (see
[Font licences](#font-licences-reviewed-20-november-2026)). Every page carries a label (`<folder> · p<N> · <case>`, Liberation Sans 7 pt, top left). The
**label is the definition of the case**: the generator script interprets the request, so read the labels before
writing an expectation:

```sh
pdftotext -enc UTF-8 -layout 08-para-tabs/reference.pdf - | grep -o "08-para-tabs · p.*"
```

### Review of the delivery (second round, 20 November 2026)

Checked by unzipping every `source.idml`, extracting the PDF text and running the visual harness (section 7).
Every sample folder has one IDML, `reference.pdf`, fonts and links. The second round fixed almost every gap of the
first: the three-page spread and `reference-spreads.pdf`, Hide Master Items, a one-page master and a primary text
frame, real `Polygon` elements, tint swatches and mixed inks, `ImportedPage` and six PDF crop modes, a PSD with a
hidden layer, graphic cells, a multi-state object and a video, threaded jump lines, `source.xml`, two Articles.

What remains:

| Folder | What is missing or different | Impact |
| --- | --- | --- |
| all | InDesign **20**, not 22. | No Flex Layout. IDML is as InDesign 20 writes it; check the 22 form before shipping anything Flex- or 2026-specific. |
| all | Stories that did not fit were **trimmed** by the script, not left overset. | Good for fit parity (section 3.F). |
| all | The generator scripts (`tools/indesign-refs/`, named in the README) are **not in the repository**. | We cannot regenerate or extend a sample. Ask for them. |
| all with links | `LinkResourceURI` values are absolute paths on the generating machine (`file:C:/Users/usuario/Downloads/...`). | Links resolve only by file name through `links/`. That is the realistic case; keep it. |
| `00-diag` | Leftover diagnostic folder (one image, no IDML). The README says to delete it. | Ignored by the harness; delete it. |
| `10-table-cells` | p6: the three graphic cells (`GraphicTypeCell`, one with Clip Contents to Cell) are **empty**: InDesign scripting cannot place into a graphic cell. The image is in a text cell next to them. | Graphic-cell geometry and clipping are testable; an image inside a graphic cell is not. |
| `10-table-structure` | Header and footer repeat modes and Skip First/Last left at their defaults: no sample writes a repeat attribute. | Only the default repeat is tested. The other modes are read as `BreakHeaders`/`BreakFooters` and `SkipFirstHeader`/`SkipLastFooter` (names and values from memory) and covered by unit tests (`TableBreakTests`) only. |
| `05-image-formats` | **Removed** (its `reference.pdf` embedded Arial Bold). It covered JPEG, PNG, TIFF, PSD, EPS, `.ai`, multi-page and cropped PDFs, a placed `.indd`, GIF, BMP, SVG and WebP. | No sample for image formats until it is re-made with the placed test PDFs embedding Liberation Sans Bold. |
| `01-doc-bleed-slug`, `12-pdf-output` | `reference-bleed-marks.pdf` and `reference-marks.pdf` **removed** (Minion Pro embedded in the marks). | Printer's marks have no reference; re-export with Page Information off to get them back. |
| `03-color-swatches` | "PANTONE 185 C" is a spot swatch defined by script with a Lab value, not loaded from the colour book. | Same XML shape as a book colour, except the book reference. |
| `12-pdf-accessibility` | Use Structure for Tab Order is not scriptable, so `reference-tagged.pdf` does not set it. | Tab order comes from the Articles. |
| `01-doc-facing-spreads` | Page 7 is a right page (odd pages are right-hand in a facing document). | None. |
| `01-doc-sections-numbering` | Not rewritten in the second round: arabic, roman, "A-" prefix with upper alpha, start at 100. | Fine for prefixes and styles. |

Inconsistencies in the package text, worth fixing at the source: the README calls the PDF/X-4 export
"PDF/X-4:2010" while its `notes.txt` says the [PDF/X-4:2008] preset (the file only says `PDF/X-4`); the README's
`11-long-interactive` note says the multi-state object is not in `reference.pdf`, but its active state is (section 4).

### Changes made to the delivered package (20 November 2026)

The package README describes the package as delivered; this folder differs from it:

- **No `.indd` files.** The 75 `source.indd` were deleted, and so was `05-image-formats/links/placed.indd` (before
  that sample was removed). The README still lists `source.indd` in every folder.
- **Fonts once, in `_shared/fonts`.** The per-sample `fonts/` folders (215 byte-identical copies) were deleted, the
  five Raleway styles that existed only there (Bold, Light, Light Italic, Medium, SemiBold) were added to
  `_shared/fonts`, and every sample has a `sample.json` with `"fontDirectory": "../_shared/fonts"`. Re-measured
  against the run before the change: all 373 cases identical, no new font substitution. `_shared/fonts` also holds the
  licence of each family (`LICENSE-Liberation.txt`, `OFL-Raleway.txt`, `OFL-SourceSerif4.txt`).
- **Commercial font data removed**: `05-image-formats` and the two marks PDFs (next section).
- **Size: 47 MB** (was 236 MB as delivered).

`00-diag` (a leftover generator folder) is still there; it holds no IDML, so the harness and the report tool ignore it.

### Font licences (reviewed 20 November 2026)

**Font files.** The 28 distinct font files are all under the **SIL Open Font License 1.1**, as each file's own name
table states (copyright, licence description and licence URL), and all allow installable embedding (`fsType` 0):

| Family | Files | Copyright | Version |
| --- | --- | --- | --- |
| Liberation Sans, Liberation Serif | 8 | Google (2010), Red Hat (2012) | 2.1.5 |
| Raleway | 10 | The Raleway Project Authors; Reserved Font Name "Raleway" | 4.026 (Regular, Italic, Bold, Bold Italic, Medium Italic, SemiBold Italic) and 3.000g (Light, Light Italic, Medium, SemiBold) |
| Source Serif 4 | 10, two of them variable | Adobe (2014–2023); Reserved Font Name "Source" | 4.005 |

The OFL allows sharing them, unmodified, as long as the copyright and licence travel with the files (the name tables
carry them, and `_shared/fonts` now has a licence file per family) and the fonts are not sold on their own. The
Reserved Font Names only matter for modified versions. Raleway mixes two releases (4.026 and 3.000g), which is fine
for licensing but means its weights do not share one set of metrics.

**Fonts embedded in the PDFs.** Every remaining PDF embeds subsets of the same OFL fonts only. Three PDFs embedded
commercial fonts and were **removed**:

| PDF | Font | Why it was there |
| --- | --- | --- |
| `05-image-formats/reference.pdf` (whole sample removed) | Arial Bold (© Monotype), subset | The placed test PDFs `fmt-boxes.pdf` and `fmt-multi.pdf` use Helvetica Bold without embedding it; InDesign substituted the machine's Arial Bold. |
| `01-doc-bleed-slug/reference-bleed-marks.pdf`, `12-pdf-output/reference-marks.pdf` | Minion Pro Regular (Adobe), subset | InDesign sets the page information line of printer's marks in Minion Pro. |

To get them back without commercial fonts: re-make `fmt-boxes.pdf` and `fmt-multi.pdf` with Liberation Sans Bold
embedded and re-export `05-image-formats`; export the marks PDFs with Page Information off. The IDMLs still name
Minion Pro, Myriad Pro and Kozuka Mincho Pr6N in default styles: names only, no font data.

`docs/reference/Fonts` holds commercial fonts (Minion Pro, Futura Md BT, Montserrat), but `docs/reference/` is
git-ignored, so they are not committed.

## 2. Running the harness

**To track progress per case** (release to release, PR to main), use the report tool,
[tools/CoreIdml.VisualReport](../../tools/CoreIdml.VisualReport/README.md): it measures every page with the same code
as the harness below, writes `results.json`, `report.md` and `report.html`, and compares a run with a baseline
(improved, regressed, mixed or unchanged per case):

```sh
dotnet run -c Release --project tools/CoreIdml.VisualReport -- run tests/doc-samples --out visual-report \
  --baseline baseline/results.json --images changed --jobs 4
```

The xUnit harness below is the pass/fail check with fixed thresholds.

The visual harness (`tests/CoreIdml.Pdf.Tests/VisualTests.cs`) treats every subfolder of
`COREIDML_PDF_SAMPLES_DIR` that holds an `.idml` and a PDF as an export case. Folders without them (`_shared`,
`00-diag`, `01-doc-flex`, `08-para-rtl-cjk`) are ignored. The test project runs on Microsoft.Testing.Platform
(xUnit v3), so the `--filter`/`--logger` options in the Samples README do **not** work; use:

```powershell
$env:COREIDML_PDF_SAMPLES_DIR = "C:\src\CoreIdml\tests\doc-samples"
dotnet test --project tests/CoreIdml.Pdf.Tests -f net10.0 `
  --filter-method "*Drop_in_samples_match_their_references" --output Detailed --show-live-output on
```

For each sample it exports the IDML with the fonts named in its `sample.json` (`_shared/fonts`) and its `links/`, rasterizes both PDFs with pdf_oxide at
96 dpi, and writes to `artifacts/bin/CoreIdml.Pdf.Tests/debug_net10.0/out/visual/<sample>/`: `ours.pdf`,
`ours-<n>.png`, `reference-<n>.png` and `diff-<n>.png` (heatmap). The log prints, per page: the share of differing
pixels, where they are (text, images, other), word-position statistics, word width ratio per font, missing fonts and
links, and overset stories. The same pairs also run `ImportGroundTruthTests` (the reference PDF imported and scored
against the IDML). The whole package takes about 90 seconds.

**One sample at a time.** There is no name filter. Point the variable at a folder holding a junction to the sample:

```powershell
New-Item -ItemType Directory -Force $env:TEMP\one | Out-Null
New-Item -ItemType Junction -Path $env:TEMP\one\08-para-tabs -Target C:\src\CoreIdml\tests\doc-samples\08-para-tabs
$env:COREIDML_PDF_SAMPLES_DIR = "$env:TEMP\one"
```

**Thresholds.** A page fails when more than 5% of its pixels differ by more than 64 (of 255) in any channel.
Both values are set per sample in `sample.json` (`threshold`, `tolerance`; also `dpi`, `pages`, `skip`). The run is
red today (section 7) until the failing samples have a `sample.json` holding their current numbers as a ceiling.
Treat ceilings as a ratchet: lower them when a feature lands, never raise them without a reason in the commit.

**A green run proves little.** At 5% a page passes with whole features missing: `08-para-bullets-numbering` passes
with no list markers drawn, and `11-long-tracked-changes` passed while it drew deleted text. The pixel threshold catches
regressions in what already works; it does not show that a feature works. Section 3 says what does.

## 3. The checks, and which one proves what

A whole-page pixel ratio is the weakest evidence: a missing bullet is 0.01% of a Letter page, while one changed line
break can reflow a column and fail the page. Use the check that matches the feature.

### A. Evidence in the IDML (before writing any code)

Confirm the sample contains the feature, and copy the **exact XML** into the feature's plan (replacing every
"(memory)" fact). Watch out for where IDML puts a value: simple values are attributes
(`BulletsAndNumberingListType="NumberedList"`), but object and list values are child elements under `<Properties>`
(`<UnderlineColor type="object">Color/Red</UnderlineColor>`, `<AppliedFont type="string">`, `<AllNestedStyles>`,
`<TabList>`, `<PageNumberStyle>`). A search for the attribute alone will report the feature missing when it is there.

### B. Model test (`tests/CoreIdml.Tests`)

The DOM reads the value, through the style chain (paragraph style `BasedOn`, character style, object style, table
style regions, cell style) and local overrides, and round-trips it. Samples are not in the repository, so either make
the test skip when the folder is missing (as `VisualTests` does), or rebuild the few elements it needs as a synthetic
fixture. Committed tests must pass without the samples.

### C. Pixels: whole page, then the region of the case

Read `diff-<n>.png` first. The whole-page ratio is only a regression ceiling. To prove a decoration feature (rules,
borders, arrowheads, corners, strokes, diagonal lines, underline options), compare the region of the item: take the
item's bounds from the IDML (page-relative, in points), scale by `dpi / 72`, pad by 2 px, and require that region to
match within a tight ratio (0.5% or less). The harness does not do regions yet; adding a `regions` entry to
`sample.json` (page → list of `[x, y, w, h]` in points) is the obvious extension, and the `RegionMap` already in
`VisualDiff.cs` shows how to classify pixels.

### D. Text positions (`TextPositionReport`)

Words of the reference matched to ours: match ratio, median dx and dy, 90th percentile distance, width ratio per
font. This ignores anti-aliasing and is the right check for composition: tabs, indents, alignment, span columns,
first baseline, baseline grid, drop caps, keeps, hyphenation, footnotes, text on a path. Two uses:

- **Generated text exists** (bullets and numbers, page numbers, jump lines, section markers, variables, footnote
  numbers): the word (`•`, `1.`, `iii`, `Part A`, `of 5`, `6`) must be in our page at the reference position, within
  1 pt. A missing marker shows as an unmatched word, not as a pixel ratio.
- **Lines start where InDesign starts them**: for a composition feature, the first word of each line within 0.5 pt in
  x. `COREIDML_PDF_WORDS=<page>` prints every matched word.

### E. PDF structure (PdfPig, already a test dependency)

For what pixels cannot show: page count and sizes, MediaBox/TrimBox/BleedBox, link annotations and their targets,
the outline (bookmarks), fonts embedded, `/Separation` and `/DeviceN` colour spaces, overprint flags (`/OP`, `/op`,
`/OPM` in ExtGState), transparency groups and their `/CS`, the structure tree and `/Lang` of a tagged PDF.

### F. Fit parity

InDesign's Preflight found **no overset text in any sample except `10-table-cells`**, where an overset cell is the
case (Preflight: 1 cell, 2 text frames). So `TextFit` / `idml_check_fit` must report exactly that and nothing else.
Any other overset we report is composer drift (font metrics, leading, keeps, composer), and a cheap, strong
regression test that needs no rasterizing. Today it fails both ways: the export reports 1 overset story InDesign
does not have (in `06-frame-basics`; with the paragraph composer the others fit, and auto-sizing frames grow to their text), and does **not** report the overset cell of
`10-table-cells`.

### G. Round trip

Open and save without edits: every part byte-identical. Open, make one unrelated edit, save: the feature's elements
and attributes unchanged, `idml_validate` clean, and InDesign opens the file (manual, once per feature).

## 4. Gotchas

**Text composition dominates the pixel diff.** Every sample uses the Adobe Paragraph Composer (except the
single-line case in `08-para-hyphenation-justification` p5); CoreIdml has a paragraph composer whose costs are fitted to
these references, and its own hyphenation patterns. A paragraph can still break differently (narrow justified columns,
proper names hyphenated differently) and push everything below it. Do not
judge a non-composition feature by the page ratio of a text-heavy page: use check D for the lines that match and
check C on the decoration's region.

**Fonts are identical, so width errors are ours.** `_shared/fonts` holds the exact files InDesign embedded.
`WidthByFont` should read 1.000; if it does not, suspect font loading, synthesis (fake small caps, superscript sizing)
or OpenType features, not the font.

**Fonts referenced but unused.** InDesign's default styles name Minion Pro, Myriad Pro and Kozuka Mincho Pr6N; no text
uses them, so they are not in `_shared/fonts`. A "missing font" report for them is noise (the harness maps Minion Pro and
Myriad Pro to Liberation anyway). Assert on fonts used by text, not on every font named.

**Variable font instances fall back to Regular.** `07-char-opentype` p5 uses named instances of Source Serif 4
Variable (`SmText Light`, `SmText Bold`, `SmText Black`, ...); CoreIdml reports each as "style not installed" and draws
the default instance. The font file is there: what is missing is instance selection.

**"Missing image" also means "cannot decode".** When the link file is found but CoreIdml cannot decode it, the
renderer still adds it to `MissingImages` (`IdmlRenderer.cs`, the `image is null` branch). So TIFF, PSD, GIF, BMP and
WebP files that are present in `links/` are reported as missing (`05-image-links` p1 shows it with a TIFF). Before treating a missing-image report as a link
problem, check that the file really is absent; better, report unsupported formats separately.

**Colour.** References use No Colour Conversion: CMYK stays DeviceCMYK, RGB stays DeviceRGB, and the rasterizer
converts both PDFs the same way, so process colours compare fairly. Three exceptions:

- **Blend space is Document CMYK** in every document. InDesign writes transparency groups in CMYK; blend modes and
  opacity over CMYK colours composited in RGB give visibly different results. Emit the group with `/CS /DeviceCMYK`
  before blaming the blend math.
- **Spot colours and mixed inks** are `/Separation` and `/DeviceN` in the reference, drawn through their alternate.
  Lab-defined spots convert through Lab, so allow a larger colour tolerance on them (about 24).
- **Tolerance 64 hides tint errors.** A 20% tint drawn at 25% passes. For colour features (tints, gradients, opacity)
  lower `tolerance` to about 8 inside the item's region.

**Overprint does not show in either PDF.** `reference.pdf` keeps overprint as flags; pdf_oxide, like most viewers, does
not simulate it, so the reference looks knocked out exactly like ours. Test the flags structurally (check E), and the
look against `reference-overprint.pdf` (Acrobat 4, flattened with Simulate Overprint).

**Effects are images in the reference.** InDesign writes drop shadows, glows and feathers as soft-masked images. They
will never match pixel for pixel: compare the item's region at low resolution (24 dpi, or blurred) with a loose
tolerance, and check extent and position (shadow offset and size) rather than exact values.

**Gradients** are PDF shadings in the reference; the midpoint becomes a stitching function. Expect banding noise;
compare in the region with a moderate tolerance and check the colours at a few sample points along the gradient
axis.

**What the reference deliberately leaves out.** Hidden and non-printing layers and items, guides, baseline grids,
notes, form fields, conditional text that is hidden (which leaves a double space), deleted tracked text (inserted text
is shown, with no change marks). `08-para-keeps-breaks` p8 is blank by design (odd-page start). In
`06-frame-baseline-grid`, the cyan hairlines are drawn items marking where the grid lines are, not the grid.

**Interactive objects.** `11-long-interactive` has a multi-state object (state 2 active), a button and a video. In
`reference.pdf` (Interactive Elements: Do Not Include) only the **active state** of the multi-state object prints; the
button prints nothing and the video frame shows its stroke without the poster. CoreIdml matches this today (0.00%).
`reference-appearance.pdf` (Include Appearance) is the target only if CoreIdml ever offers an appearance option.

**The missing link.** `05-image-links` p4 links a file that is not in `links/`. InDesign exported the preview it had
stored, which rasterizes as a colour bar over vertical stripes; CoreIdml draws nothing (or a grey cross with
`MarkMissingImages`). That frame can never match: mask it in the pixel check and assert the missing-link report instead.

**Page markers share one code.** Current, next and previous page numbers are all `<?ACE 18?>`; next and previous
are told apart only by `PageNumberType="NextPageNumber"` or `"PreviousPageNumber"` on the enclosing
`CharacterStyleRange`. `<?ACE 19?>` is the section marker. A renderer that ignores `PageNumberType` prints the current
page: `11-long-markers-variables` now catches it, with jump lines that read "Continued on page 4" and "Continued on
page 6" (p2, p4) and "Continued from page 2" and "Continued from page 4" (p4, p6). The next page is the page of the
story's next frame, not page + 1. The jump lines are frames of their own stories that overlap the threaded frame by
1 pt; the marker follows the thread of the frame it touches.

**Stored text that is not the printed text.** `TextVariableInstance@ResultText` is what InDesign last composed. On
master pages, running-header variables store a placeholder (`first: <Running Header First>`), not the text of any
page, and the last page number stores `section: 1` where every page prints `section: 6`; both are computed per page.
A page with no heading repeats the previous page's header (p3 shows p2's, p5 shows p4's); p1, before any heading,
prints nothing for the variable, not even its text before (the PDF has a zero-width U+FEFF there). Output Date and
Modification Date carry the generation date (20 November 2026), so drawing their `ResultText` matches. File Name
is `source.indd`. InDesign never breaks a variable: on p1 the file path does not fit, and InDesign squeezes the whole
line (word spaces down to 0.1 pt, letters 0.4 pt closer) instead of breaking before it.

**Liquid-resized documents.** In the `01-doc-liquid-to-*` folders some labels were moved off the page by the rule
(p2 or p3 have no label). Test the resize engine by item: our resize of `01-doc-liquid` against InDesign's resized
IDML, item by item through `Self`, bounds within 0.5 pt. Render each resized IDML as an ordinary sample too.

**Extra PDFs are not drop-in references.** The harness compares only `reference.pdf`. The variants have different
page sizes or colour (bleed, marks, spreads, PDF/X with CMYK conversion, downsampling, tagging, interactive
appearance). Test them with check E; for pixels, compare only inside the trim or bleed box.

**The labels are a free registration check.** Every page has its label at the same place. If the label's words
are off by more than 0.5 pt, the page, master or spread transform is wrong, whatever the feature under test.

## 5. Feature by feature

**Checks** refers to section 3. "Pass" is the condition to meet before marking the feature-matrix row ✅.

### 01 Document and pages

| Sample | Checks | Pass when | Watch out |
| --- | --- | --- | --- |
| `01-doc-page-sizes` | E, C, labels | Each page's MediaBox matches the reference (612×792, 792×612, A4, A5, 400×250, tall custom); the inset frame matches. | Per-page sizes from the Page tool: master items map through the page's own transform. |
| `01-doc-margins-guides` | A, B | Margins, columns (including custom `ColumnsPositions`) and the ruler guides read correctly. | Nothing prints; p6 boxes are snapped to guides, so their pixels confirm guide positions indirectly. |
| `01-doc-facing-spreads` | E, C, labels | Items crossing the spine split across pages; the rectangle over the three-page spread appears on all three; pasteboard items absent. Spreads: one page per spread, as `reference-spreads.pdf`. | `AllowPageShuffle="false"` on the three-page spread. |
| `01-doc-facing-start-left` | E, labels | Page 1 is a left page (section starts at 2); folios on the outside. | |
| `01-doc-bleed-slug` | E, C | `reference.pdf`: trim-size pages, items cut at trim (the default export, as the report compares). With `includeBleed`: MediaBox and BleedBox = trim + bleed, TrimBox = trim, compared with `reference-bleed.pdf`. | Done: the default export matches `reference.pdf` (boxes equal, 100%); `includeBleed` gives InDesign's boxes on all 3 pages (630 × 810, TrimBox [9 9 621 801]) and pixels within 0.003% of `reference-bleed.pdf` (pdf_oxide, 96 dpi). The marks PDF was removed (Minion Pro). |
| `01-doc-sections-numbering` | D | Folio and body page-number words equal the reference on all 7 pages. | `PageNumberStyle` is a child element; prefix included only where the section says so. |
| `01-doc-masters` | C, D, labels | 12 pages: master items above or below page items per layer; B inherits A; one-page C-Master; overridden item moved and recoloured; overridden then deleted (absent); detached (drawn once); Hide Master Items spread (no master items); primary text frame text. | Overrides are matched through `OverrideList`; a double-drawn item shows only as darker anti-aliasing. `ShowMasterItems="false"` is a known preview gap. |
| `01-doc-layers` | C, B | Stacking order matches; hidden and non-printing layers absent; locked layer prints. | |
| `01-doc-liquid` + 4 resized | B, item geometry, C | Our resize of the source within 0.5 pt of InDesign's per item; each resized IDML renders like its reference. | Missing labels on some resized pages are expected. |
| `01-doc-alternate-layout` | A, B, E | Two layouts read as sections with `AlternateLayout`; `LinkedStoryOption` read; 4 pages render. | Record the linked-story XML in the responsive plan. |

### 02 Page items

| Sample | Checks | Pass when | Watch out |
| --- | --- | --- | --- |
| `02-item-shapes-transforms` | C | Every page under 0.5% in each item's region. | Non-uniform scale and shear live in `ItemTransform`; the stroke must distort with it. |
| `02-item-paths` | C | Open paths (stroked, and filled as InDesign fills them: as if closed), polygons and stars, compound holes (even-odd), Convert Shape results, Create Outlines. | 4 `Polygon` elements plus paths on rectangles and ovals. |
| `02-item-arrowheads` | C (region per line) | Every arrowhead shape, weight, scale (50–400%) and alignment matches. | Alignment `InsidePath` shortens the path; `OutsidePath` extends past the end point. |
| `02-item-corners` | C | All five corner shapes, per-corner options, radius larger than half a side, corners on a hexagon. | The preview draws inset as bevel and fancy as rounded today. |
| `02-item-groups-paste-into` | C | Group opacity as one layer (overlaps not visible through each other), nested clipping. | Group opacity applied per child shows as darker overlaps. |
| `02-item-anchored` | D, C | Inline objects on the baseline with offsets; above-line alignment; custom anchored objects at the reference position (within 1 pt). | Custom positions are relative to frame, column, margin or page edge. Done: every box within 0.001 pt (measure by the visible bounds: the 1 pt stroke of `[Normal Graphics Frame]` counts), p2, p3 and p5 match pixel for pixel. An above-line object sits 0.8274 em above its line's baseline, which keeps its leading below it; a positive X offset from a left edge moves the object left (p4: "x -60" in the label, -90 in the XML, ends the box 90 pt inside the column). p4: text wrap around the custom box is not done (8 words). |
| `02-item-hidden-locked-nonprinting` | C | Hidden and non-printing items absent, locked items present, group flags inherited. | |
| `02-item-object-styles` | B, C | Fill, stroke, corners, insets and **transparency/shadow** resolved through the style; local overrides win; disabled categories leave the item's own values. | Opacity from the style is resolved (the sample has none, only a drop shadow, which CoreIdml does not draw). p1: the text frame's 2 pt stroke moves the text 1 pt in and narrows the columns to 255 pt; the remaining misses are one line break of InDesign's paragraph composer ("may" moved to the next line). |
| `02-item-text-wrap` | D, C | Lines avoid the wrap area per mode and side; contour wraps follow the shape and the alpha channel; inverted wrap flows inside. | Large composer effects; judge by line starts and line lengths. A line is tested from the baseline before it to its own; shape edges are truncated to whole points from the column's left edge, box edges are exact. every page at 97 to 100% (the rest paragraph-composer choices in narrow stretches); p8's alpha channel is the pixels whose alpha reaches 230. |

### 03 Colour, 04 Effects

| Sample | Checks | Pass when | Watch out |
| --- | --- | --- | --- |
| `03-color-swatches` | C (tolerance 8), E | p1 CMYK, RGB, Lab, unnamed colours, [Paper], [Registration]; p2 tint swatch and colour group; p3 spots; p4 mixed ink and mixed ink group. | Lab and spot convert through Lab: allow about 24. Mixed inks are `/DeviceN` in the reference and resolve to nothing in CoreIdml today. |
| `03-color-gradients` | C (moderate), sample points | Linear angles, 4 stops, midpoints, radial, gradient stroke, gradient with tints and spots. | Gradient Tool start/length (`GradientFillStart`, `GradientFillLength`) are relative to the item. Drawn since the gradients work: p1, p2, p3 and p5 match (mean error under 0.05%). p4's rectangle stroke and text fill are shading patterns: pdf_oxide paints a pattern stroke black, in the reference too (PDFium draws the gradient), and so does the stroked "Gradient Stroke" text (drawn since text strokes are, a pattern spanning the frame as in the reference; 100%). |
| `03-color-strokes` | C | Weights, canned and custom dashes, gap colour, caps, joins, miter limit, alignment. | Custom dashes scale differently from canned ones. |
| `03-color-overprint` | E, then C against `reference-overprint.pdf` | Overprint flags written for fill, stroke, text, spot. | Invisible in `reference.pdf` (section 4). |
| `04-fx-opacity-blend` | C (tolerance 8), E | Opacity values, blend modes, fill/stroke/content opacity, group opacity, isolate and knockout, image opacity. | Blend space CMYK. |
| `04-fx-shadows-glows` | C at 24 dpi | Shadow offset, size and colour; inner shadow, glows; bevel/emboss and satin present at least as "not drawn" warnings. | Raster effects; never pixel exact. |
| `04-fx-feathers-other` | C at 24 dpi | Basic, directional and gradient feather extents. | Same as above. |

### 05 Images

| Sample | Checks | Pass when | Watch out |
| --- | --- | --- | --- |
| `05-image-formats` | | **Removed** (commercial font in its reference PDF). | Re-make it with the placed test PDFs embedding Liberation Sans Bold (section 1). |
| `05-image-fitting` | C | Fitting modes, crop amounts, content scale and frames of every shape. | |
| `05-image-clipping` | C | Detect Edges at three thresholds, invert, alpha channel clipping. | The clipping path is stored in the IDML: draw the stored path, do not recompute it. |
| `05-image-links` | E, report | Linked, embedded and twice-placed images drawn; effective ppi reported (300, 600, 72); p4 reported missing. | p4 masked (section 4). |

### 06 Text frames

| Sample | Checks | Pass when | Watch out |
| --- | --- | --- | --- |
| `06-frame-basics` | D | Columns, insets, vertical justification, every first-baseline option, fixed column width, ignore text wrap, rotated and sheared frames. | Fixed column width changes the frame width. |
| `06-frame-autosize` | D, C | Frame edges (fill or stroke) and line positions match for every auto-size type and reference point. | InDesign stored the grown size, so the stored bounds already match: the test is that we do not overset or re-grow them wrongly, and the resize logic matches on a text edit. Every auto-size frame of the 74 samples gets InDesign's stored size back from our composer within 0.05 pt (proportional within 0.3 pt, kept), except p6's 2-column frame, which grows a line because "Neth-erfield" breaks differently (`FrameColumnTests` for edits). |
| `06-frame-span-balance` | D | Headlines span all or 2 columns; split columns; balanced columns end at the same height. | p1 to p4: every line where InDesign puts it but for a paragraph that the paragraph composer breaks differently ("lady to / him … Neth-"; p4's second sub-column); p5, p6 match. |
| `06-frame-shapes` | D | Lines stay inside ovals, hexagons, stars, triangles and curved frames, with insets. | The first line moves down 1 pt at a time until its first word fits; edges are truncated to whole points from the column's left edge, so an edge within 0.15 pt of a whole point can land a point off (p5). p2 100%, p1, p3, p4, p5 93 to 97%. |
| `06-frame-text-on-path` | D, C | Glyph positions along the path for each effect, alignment, flip. | PdfPig words on curved text may split; match by glyph. |
| `06-frame-baseline-grid` | D | Baselines on the grid (14 pt) where aligned; first-line-only; custom frame grid. | The cyan hairlines are drawn items. InDesign starts text in a stroked frame half the stroke weight in (0.125 pt here), frame grids too, so our baselines read 0.12 pt high outside the document grid. |

### 07 Characters

| Sample | Checks | Pass when | Watch out |
| --- | --- | --- | --- |
| `07-char-basics` | D (widths), C | All families and styles, sizes 6–72, tracking, metrics/optical/manual kerning, scales, baseline shift, skew, colours. | Optical kerning is drawn as metrics today: compare word positions on the kerning line only. Skew (p5) and the text strokes of p6 match; p5's remaining pixels are the 50% and 150% horizontal scales and the +6 baseline shift. |
| `07-char-underline-strike` | C (regions) | Underline and strikethrough weight, offset, colour, tint, type and gap colour. | `UnderlineWeight="-9999"` means "auto". Done: every page 100% at tolerance 64 and under 0.2% of pixels at tolerance 16; the rules' operators match InDesign's (auto weight and offset from the post table, strikethrough a third of the size up, dashes, dots, wave, thick-thin, gap colour under the dashes). |
| `07-char-case-position` | D | All caps, small caps (real and synthesised), superscript/subscript (synthesised and OpenType) at the reference size and position. | |
| `07-char-outlined-text` | C | Stroked glyphs (fill none), stroke over fill; live text next to outlines (the outlines are paths, a good control). | Done: every page 100% (p2 99.99%). pdf_oxide ignores text render mode 1 and fills stroked text, in the reference too: check outlines with PDFium (`render`), and compare `w`, `Tr` and their order with the reference's content stream. |
| `07-char-opentype` | D, glyph check | Ligatures, figure styles, fractions, ordinals, stylistic sets and the variable instances give the reference's glyphs. | Compare glyph IDs or ToUnicode text: a fraction may extract as `½` or `1/2`. Variable instances fall back today (section 4). |
| `07-char-language-nobreak` | D | Hyphenation points per language; No Break phrases unsplit; discretionary and non-breaking hyphens. | Our hyphenation patterns differ from InDesign's dictionaries; count matching break points, not pixels. French and German are not hyphenated by CoreIdml, so p1's 90 pt justified columns break differently; p2 matches every line; p3: a discretionary hyphen is the only break point of its word, a missing non-breaking hyphen glyph prints as the hyphen. |
| `07-char-special-characters` | D | Width of each fixed space, indent to here, right indent tab, forced line break, column and frame breaks, markers. | Flush space only in a justified last line. |

### 08 Paragraphs

| Sample | Checks | Pass when | Watch out |
| --- | --- | --- | --- |
| `08-para-alignment-indents` | D | Line starts and ends per alignment and indent; balance ragged lines. | |
| `08-para-tabs` | D | Tab positions within 0.5 pt; decimal and character alignment on `.`, `,`, `=`; leaders (`.`, `-`, `_`, `·`, `ab`) present between the right words. | Leaders extract as words: a missing leader is a set of unmatched words, and a leader glyph short or extra changes the word. Now drawn: every page 100% within 1 pt. |
| `08-para-hyphenation-justification` | D | p5 single-line composer: same line breaks as InDesign (the fair page for our composer); p1–4: settings read (B), lines compared. | English hyphenation points match InDesign's (re-turned, accom-plished, neighbour-hoods). p5: both columns break every line as InDesign (single-line: line 2 at 90% word spacing to take "all", line 3 "ac-" at 91%). p4: word and letter spacing and glyph scaling follow the settings (Justification.cs). The Paragraph Composer pages (p1 to p4) are the hardest case for it: 120 pt columns of long words, lines at 200–700% word spacing; p1 and p3 now match line for line (a justified line costs more once its stretching adds more than 13% of its width, so the looseness is spread as InDesign spreads it); p2 matches in columns 1 and 4; in columns 2 and 3 (words longer than 10, 4/4 letters) ours hyphenates "Extraordi-narily", which InDesign does in none of these references, and compresses line 2 below the minimum instead (63% of words). |
| `08-para-keeps-breaks` | D, F | Keep with next, keep together, keep first/last lines, start in next column, frame, page, odd page: the same paragraph starts each column and page. | p8 blank by design; p6 and p9 are continuations. Now honoured: every paragraph starts in the column and page InDesign puts it in, 100% of words within 1 pt on every page (p1 and p3 needed the paragraph composer: InDesign breaks a ragged line one word earlier to even the rag). |
| `08-para-rules` | C (regions), D | Weights, offsets, width (column/text), indents, types, above and below together, 2-column frame. | Rules are thin: the page ratio was 99.2% with none drawn. Compare the painted paths (PdfPig `page.Paths`: line width, colour, dash, bounds) with the reference's. Done: every page 100% (no pixel over the tolerance), every rule's position, length, weight, colour and dash as in the reference (the dotted rule is one dashed line where InDesign draws the end dots apart; same dots), and p5's kept-in-frame rule moves the first line 13.3 pt down as InDesign does (every word within 1 pt). |
| `08-para-borders-shading` | C (regions) | Shading, borders, rounded corners, split across columns, merged consecutive borders. | Done: every page 100% (from 4 of 5 pages over 5%), every box's bounds, colour, weight and corner curves as in the reference. p5's six paragraphs fit in the first column: nothing breaks across columns, so a split box and Display If Splits are untested; so are text width, Clip to Frame and the other origins. The p1 label says the indented paragraph's shading "follows the text area"; InDesign's PDF spans the column. |
| `08-para-drop-caps` | D, C | Drop cap size and position (2–5 lines, 1–3 characters), character style, detail flags, indents, baseline grid. | Done: every drop cap glyph within 0.13 pt of InDesign's (size, baseline, x) on all 6 pages, and the first N lines start where InDesign starts them. The character style is a nested style "through 1 `Dropcap`"; `DropcapDetail="1"` is Align Left Edge (p4's "descenders on/off" labels actually toggle that; no paragraph scales for descenders). p6's styles have `GridAlignment="None"`: the baseline grid is not exercised. Remaining differences: line breaks where the paragraph composer chooses other lines than InDesign (p1: the paragraph beside the 5-line drop cap; p2 fits story u270 now (InDesign sets line 2 of its third column at 66% word spacing, below the 80% minimum); p1 and p6 compress "rightful property of some one or other of their daughters." to 75% as InDesign does), and the first line of p5's centred paragraph (InDesign starts it at the drop cap, 3.5 pt left of ours). |
| `08-para-bullets-numbering` | D | Every marker word (`•`, `–`, `▪`, `►`, `○`, `■`, `1.`, `A`, `iii`, `01`, `(1)`, `Step 1:`, `Article 1.`, `1.1.1`) at the reference position; start at 5; list continued across frames; right-aligned two-digit numbers. | Markers are generated, not stored as text. Now drawn: 100% of words within 1 pt on all 7 pages. Right-aligned numbers in a hanging indent sit where left-aligned ones do (p2, p7); list A is two stories (p6). |
| `08-para-nested-grep-styles` | D (font per word), C | Run-in heads, word sequences, line styles and GREP matches drawn in the right style. | Check the font and colour of matched words, not only their positions. Now drawn: every run has the reference's font and colour on all 5 pages (compared run by run with PdfPig); 100% of words within 1 pt on every page (p4 needed the paragraph composer, which moves "2" to the next line as InDesign does). The labels disagree with the XML in two places: p3 says "first 2 lines Strong" but `LineCount` is 1 (the reference follows the XML), and p5 promises "a local character style" that no story has, so the order of an applied character style against nested/GREP styles is not measured. |
| `08-para-optical-margin` | D | Hanging punctuation by size (8, 14, 24 pt). | |

### 09 Styles, 10 Tables

| Sample | Checks | Pass when | Watch out |
| --- | --- | --- | --- |
| `09-style-text-styles` | B, D | Based-on chains, next style (B only), character styles, overrides and clearing, object style carrying a paragraph style. | |
| `09-style-table-cell-styles` | C, B | Cell styles per cell; table style regions (header, body, footer, left/right column) including across a frame break; local overrides. | Done: p3 repeats the header and footer rows (`CS Head`, `CS Foot`) in both frames, three body rows in the first. The footer text is 1 pt lower than InDesign's in both frames, as the footer at the end of the table already was. |
| `10-table-structure` | D, C | Header and footer rows, row heights (at least / exactly), merged cells, table across two frames with the header repeated, table in running text, nested table. | Done: p4 repeats the header and footer rows in both frames and starts row 3 in the next frame (its `StartRow`), every word within 1 pt. |
| `10-table-cells` | C (regions), F | Fills, per-edge strokes and types, insets and vertical alignment, rotated text, diagonal lines in front and behind; p6 empty graphic cells with their insets and clipping, the overset cell reported. | Edge types are drawn solid today. The overset cell is the one expected overset in the package. |
| `10-table-alternating` | C (regions) | Alternating row and column fills and strokes with counts, from the table and from a table style, with merged cells. | |

### 11 Long documents, 12 PDF output

| Sample | Checks | Pass when | Watch out |
| --- | --- | --- | --- |
| `11-long-markers-variables` | D, B | Every variable and marker word as in the reference: dates, custom text, chapter, last page, file name, running headers per page, character-style header, live caption (image name and metadata), jump lines (4, 6 / 2, 4), section marker. | Running headers on masters are computed, not `ResultText`; next/previous follow the story's frames (section 4). Now drawn: p2-p6 every generated word within 1 pt (the rest of p2, p4, p6 is body composition); p1 misses only the squeezed file-path line. |
| `11-long-footnotes-endnotes` | D, F | Reference numbers in the text, footnotes at the column foot (including 2 columns), restart per page, endnotes in their frame. | Footnotes shrink the column: everything after them moves until this works. |
| `11-long-links-bookmarks` | E | Link annotations with the URL or page/anchor destination over the right text; outline with the bookmarks of the reference. | Text already renders; this is structure only. |
| `11-long-toc-index` | D | TOC and index stories render as stored. | Stored text; no regeneration expected. |
| `11-long-conditional-text` | D | Hidden conditions absent with the double space; visible ones present; the "Web" condition set. | `reference-allvisible.pdf` is the reference after making every condition visible in a copy. |
| `11-long-tracked-changes` | D | Inserted text present, deleted text absent, moved text at its new place; accepted/rejected changes. | Done: deleted text is skipped (in the preview, fit checks and story text), inserted and moved text drawn; accepted or rejected changes are plain text in the IDML. Every page matches (p4: the paragraph composer hyphenates `fami-lies.must` as InDesign does, ignoring the hyphenation zone). |
| `11-long-notes` | D | No note text in our page. | |
| `11-long-xml-structure` | B | `idml_xml_structure` lists Root, Article, Title, Body (with nested Emphasis), Image and 4 attributes; `idml_export_xml` matches `source.xml` in elements and text. | |
| `11-long-data-merge`, `-result` | B, D | Placeholders `<<Name>>`, `<<Residence>>`, `<<Remark>>` found (template); the result renders 3 records. | Placeholders are stored as text with `DataMerge` options; `data.csv` is in `links/`. |
| `11-long-interactive` | C | Multi-state object: only the active state drawn; no button; video frame stroke without poster. | Matches today; keep it as a regression check. `clip.mp4` and its poster are in `links/`. |
| `12-pdf-output` | E, C | `reference.pdf` pixels as usual; variants by structure (PDF/X version and OutputIntent, boxes, spreads page size, image ppi in `reference-smallest.pdf`; the marks PDF was removed). | Boxes: no remaining variant was exported with the bleed (MediaBox = BleedBox = TrimBox, 612 × 792, a spread wide in `reference-spreads.pdf`), as the default export writes them. |
| `12-pdf-accessibility` | E | When tagged export exists: structure tree roles H1, H2, P, Figure with alt text (custom and from an object style), artifact, TH cells, `/Lang` with a French paragraph, title, reading order from the two Articles, against `reference-tagged.pdf`. | The role map maps style names to standard roles. p4 (inline and above-line images) matches `reference.pdf`: the anchor marker of the above-line image kerns with the "A" after it as a space would (0.58 pt). |

## 6. Working on a feature, step by step

1. Read the labels and the IDML of its sample (check A). Put the exact XML in the plan.
2. Run the harness on that sample alone; keep `diff-<n>.png` and the log as "before".
3. Write the model test (check B) and the check that proves the feature (C, D or E, per section 5).
4. Implement. Rerun the sample, then the whole package: the feature's sample improves, no other sample gets worse
   (ratchet ceilings), and fit parity (F) still holds.
5. Lower the sample's `sample.json` ceilings, update the row in `feature-matrix.md` and the "Not supported" section of
   `document-spec.md`.

## 7. Baseline

Second round, run at commit `717f8bf` (net10.0, 96 dpi, tolerance 64, threshold 5%, no `sample.json`).
**Pages failing** is the count above 5%; **Worst page** the highest pixel ratio; **Words matched** the share of
reference words found in our page, all pages together; **Worst P90** the largest 90th-percentile word distance on any
page (0.5 pt or less means text lands where InDesign puts it; tens of points mean lines broke differently);
**Reported** what the export reported, other than the Minion Pro substitution most samples log (an unused default
style, section 4).

42 of 373 pages fail, in 20 samples (after `05-image-formats` was removed; the table no longer lists it). Words-matched under 90% points at composition differences (wrap, shapes, span,
text on a path, drop caps, hyphenation, optical margins) or at generated text we do not draw (bullets, tracked
changes, variables).

| Sample | Pages failing (>5%) | Worst page | Words matched | Worst P90 | Reported |
| --- | --- | --- | --- | --- | --- |
| `01-doc-alternate-layout` | 0/4 | p1 0.13% | 100% | 0.5 pt |  |
| `01-doc-bleed-slug` | 0/3 | p1 0.44% | 98% | 26.1 pt |  |
| `01-doc-facing-spreads` | 0/7 | p1 0.00% | 100% | 0.0 pt |  |
| `01-doc-facing-start-left` | 0/3 | p1 0.36% | 99% | 0.5 pt |  |
| `01-doc-layers` | 0/5 | p1 0.00% | 100% | 0.0 pt |  |
| `01-doc-liquid` | 0/5 | p2 0.13% | 100% | 0.5 pt |  |
| `01-doc-liquid-to-1080x1080` | 0/5 | p1 0.07% | 100% | 0.6 pt |  |
| `01-doc-liquid-to-1920x1080` | 0/5 | p1 0.04% | 100% | 0.6 pt |  |
| `01-doc-liquid-to-a5` | 0/5 | p2 0.23% | 100% | 0.5 pt |  |
| `01-doc-liquid-to-letter` | 0/5 | p2 0.14% | 100% | 0.5 pt |  |
| `01-doc-margins-guides` | 0/6 | p5 0.48% | 98% | 22.8 pt |  |
| `01-doc-masters` | 2/12 | p10 5.15% | 94% | 15.1 pt |  |
| `01-doc-page-sizes` | 0/6 | p6 0.26% | 100% | 5.8 pt |  |
| `01-doc-sections-numbering` | 0/7 | p1 0.15% | 99% | 0.5 pt |  |
| `02-item-anchored` | 0/5 | p2 2.96% | 91% | 42.6 pt |  |
| `02-item-arrowheads` | 0/5 | p2 0.76% | 100% | 0.0 pt |  |
| `02-item-corners` | 0/4 | p1 0.61% | 98% | 7.9 pt |  |
| `02-item-groups-paste-into` | 0/4 | p3 0.07% | 100% | 0.0 pt |  |
| `02-item-hidden-locked-nonprinting` | 0/5 | p1 0.00% | 100% | 0.0 pt |  |
| `02-item-object-styles` | 0/3 | p1 1.55% | 99% | 20.2 pt |  |
| `02-item-paths` | 0/5 | p1 3.87% | 100% | 0.0 pt |  |
| `02-item-shapes-transforms` | 0/6 | p5 0.13% | 100% | 0.5 pt |  |
| `02-item-text-wrap` | 0/9 | p5 3.46% | 54% | 50.0 pt |  |
| `03-color-gradients` | 4/5 | p1 27.90% | 96% | 0.0 pt |  |
| `03-color-overprint` | 1/5 | p5 10.60% | 100% | 0.0 pt |  |
| `03-color-strokes` | 0/5 | p2 1.01% | 100% | 0.0 pt |  |
| `03-color-swatches` | 2/4 | p4 32.98% | 100% | 0.0 pt |  |
| `04-fx-feathers-other` | 3/5 | p4 15.77% | 100% | 0.5 pt |  |
| `04-fx-opacity-blend` | 2/6 | p5 9.08% | 100% | 0.5 pt |  |
| `04-fx-shadows-glows` | 3/5 | p2 19.05% | 100% | 0.0 pt |  |
| `05-image-clipping` | 3/3 | p2 25.41% | 100% | 0.0 pt |  |
| `05-image-fitting` | 0/4 | p3 1.31% | 100% | 0.0 pt |  |
| `05-image-links` | 1/4 | p4 6.20% | 100% | 0.0 pt | missing image fmt-lzw.tif; missing image fmt-missing.jpg |
| `06-frame-autosize` | 0/6 | p2 2.40% | 90% | 35.2 pt | overset story u168; overset story u48d |
| `06-frame-baseline-grid` | 1/6 | p3 6.90% | 87% | 56.6 pt |  |
| `06-frame-basics` | 0/7 | p1 3.57% | 94% | 39.8 pt | overset story u13a; overset story u16a; overset story u49f |
| `06-frame-shapes` | 1/5 | p4 5.46% | 53% | 51.7 pt |  |
| `06-frame-span-balance` | 1/6 | p2 5.02% | 73% | 41.0 pt |  |
| `06-frame-text-on-path` | 0/5 | p3 1.50% | 18% | 0.0 pt |  |
| `07-char-basics` | 0/8 | p5 2.65% | 98% | 26.4 pt |  |
| `07-char-case-position` | 0/3 | p1 1.63% | 83% | 36.1 pt |  |
| `07-char-language-nobreak` | 0/3 | p1 3.59% | 85% | 43.4 pt |  |
| `07-char-opentype` | 0/5 | p5 3.80% | 95% | 6.2 pt | 6 variable font instances not installed (fell back to Regular) |
| `07-char-outlined-text` | 1/4 | p1 6.94% | 90% | 0.0 pt |  |
| `07-char-special-characters` | 0/5 | p4 0.80% | 89% | 5.0 pt |  |
| `07-char-underline-strike` | 0/5 | p4 1.03% | 100% | 0.0 pt |  |
| `08-para-alignment-indents` | 0/5 | p1 2.73% | 98% | 21.1 pt |  |
| `08-para-borders-shading` | 4/5 | p4 11.94% | 100% | 0.5 pt |  |
| `08-para-bullets-numbering` | 0/7 | p1 2.61% | 87% | 48.1 pt |  |
| `08-para-drop-caps` | 0/6 | p1 3.84% | 70% | 56.4 pt |  |
| `08-para-hyphenation-justification` | 0/5 | p3 2.33% | 78% | 50.0 pt |  |
| `08-para-keeps-breaks` | 0/9 | p3 2.27% | 88% | 24.3 pt |  |
| `08-para-nested-grep-styles` | 0/5 | p3 2.67% | 89% | 21.1 pt |  |
| `08-para-optical-margin` | 1/3 | p3 6.21% | 75% | 56.4 pt |  |
| `08-para-rules` | 0/6 | p5 1.04% | 100% | 0.2 pt |  |
| `08-para-tabs` | 0/5 | p2 0.44% | 96% | 9.9 pt |  |
| `09-style-table-cell-styles` | 1/5 | p3 6.50% | 94% | 22.4 pt |  |
| `09-style-text-styles` | 0/5 | p4 1.24% | 97% | 53.9 pt |  |
| `10-table-alternating` | 0/7 | p6 2.51% | 99% | 0.7 pt |  |
| `10-table-cells` | 1/6 | p2 5.44% | 95% | 4.2 pt |  |
| `10-table-structure` | 1/6 | p4 5.96% | 83% | 1.6 pt |  |
| `11-long-conditional-text` | 0/5 | p3 0.31% | 92% | 12.5 pt |  |
| `11-long-data-merge` | 0/1 | p1 0.00% | 100% | 0.0 pt |  |
| `11-long-data-merge-result` | 0/3 | p2 0.04% | 100% | 0.5 pt |  |
| `11-long-footnotes-endnotes` | 0/6 | p1 0.74% | 76% | 17.5 pt |  |
| `11-long-interactive` | 0/1 | p1 0.00% | 100% | 0.0 pt |  |
| `11-long-links-bookmarks` | 0/4 | p1 0.19% | 100% | 0.5 pt |  |
| `11-long-markers-variables` | 0/6 | p4 1.58% | 90% | 4.4 pt |  |
| `11-long-notes` | 0/3 | p1 0.14% | 100% | 0.5 pt |  |
| `11-long-toc-index` | 0/6 | p1 0.44% | 97% | 0.5 pt |  |
| `11-long-tracked-changes` | 0/5 | p3 0.60% | 76% | 60.0 pt |  |
| `11-long-xml-structure` | 0/1 | p1 0.00% | 100% | 0.0 pt |  |
| `12-pdf-accessibility` | 0/5 | p4 1.17% | 89% | 37.3 pt |  |
| `12-pdf-output` | 2/4 | p3 5.91% | 100% | 0.0 pt |  |
