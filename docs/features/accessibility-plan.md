# Accessibility (Section 508 / WCAG / PDF/UA): analysis and extension plan

> Status: **plan, not implemented.** Combined work plan: [accessibility-and-variants.md](accessibility-and-variants.md). Goal: an **accessibility review** feature for the
> CoreIdml plugin: a skill plus the tools it needs, so that a PDF made from an IDML document passes strict
> screen-reader checks (Section 508, which points to WCAG 2.0 AA; PDF/UA-1 / ISO 14289-1 for the PDF itself).
>
> Verdict: **the library does not have what the skill needs yet.** CoreIdml keeps every accessibility setting an
> InDesign file carries (untouched parts are written back byte for byte), but it can neither read nor write any of
> them through its API or the MCP tools, has no accessibility check, and its own PDF export is untagged. This
> document explains how accessibility works in IDML, what the reference file shows, and the plan to extend
> CoreIdml. The skill itself is drafted in [accessibility-skill-draft.md](accessibility-skill-draft.md), written
> against the tools planned here; it ships in phase 5.

## 1. How an IDML document becomes an accessible PDF

An IDML file does not contain a tagged PDF. It contains the **inputs** InDesign uses to build the PDF's structure
tree when the user exports with *Create Tagged PDF*. Everything below is what the IDML can carry; section 1.10
lists what it cannot carry and is only chosen at export time.

```
IDML (what CoreIdml can read and write)                 InDesign export (Create Tagged PDF)
─────────────────────────────────────────               ───────────────────────────────────
Articles + TaggedPDFPreference ──┐
XML structure (Tags, XMLElement) ┼─ reading order ────►  StructTreeRoot order
Stories, threads, anchors ───────┘
ParagraphStyle/StyleExportTagMap ── semantics ───────►  H1…H6, P, Artifact
Auto bullets / numbering ──────────────────────────►  L, LI, Lbl, LBody
Table HeaderRowCount, spans ───────────────────────►  Table, TR, TH, TD (+ RowSpan/ColSpan)
ObjectExportOption ── alt text, actual text, tag ──►  Figure /Alt, /ActualText, or Artifact
Master items, non-printing layers ─────────────────►  Artifact / omitted
AppliedLanguage per run ───────────────────────────►  /Lang on structure elements
META-INF/metadata.xml (XMP dc:title …) ────────────►  document title, XMP
Hyperlinks ────────────────────────────────────────►  Link + annotation
```

### 1.1 Reading order (WCAG 1.3.2 Meaningful Sequence, 2.4.3 Focus Order)

Three things decide the order a screen reader reads the tagged PDF in:

1. **Which source InDesign uses.** `designmap.xml` holds `<TaggedPDFPreference StructureOrder="…"/>`, the *Use for
   reading order in tagged PDF* checkbox of the Articles panel. The values are `UseXMLStructure` (the default:
   every file we have, and CoreIdml's blank template) and `UseArticles` (scripting enum
   `TaggedPDFStructureOrderOptions.USE_XML_STRUCTURE` / `USE_ARTICLES`).
2. **With `UseArticles`:** the **Articles** (`Article`, with `Name`, `ArticleExportStatus` (include in export) and
   ordered `ArticleMember`s, each pointing to one page item through `ItemRef`). Articles in panel order, members in
   member order. This is how accessibility practitioners set reading order in InDesign today.
3. **With `UseXMLStructure`:** the XML structure (`<XMLElement>` tree from `Root`), in Structure-pane order. When
   most content is untagged (the usual case: the reference file's structure is an empty `Root`), InDesign tags the
   untagged stories and graphics itself, in an order it derives from the layout, which is often wrong for
   multi-column pages, sidebars and pull quotes.

Inside one story the order is the text order. **Threaded frames are one story**, so threading fixes the order of
text that flows across frames. Separate, unthreaded frames are separate stories, ordered by the rule above.

### 1.2 Anchoring elements inline (1.3.2, 1.1.1)

A figure that floats on the page is placed in the reading order by the article or structure position, or by
InDesign's layout guesses. A figure **anchored in the text** (the page item lives inside the story, in a
`CharacterStyleRange`, with an `AnchoredObjectSetting` of `InlinePosition`, `AboveLine` or `Anchored` (custom)) is
read exactly where its anchor character sits: after the paragraph that introduces it, before its caption. The same
applies to sidebars and callouts. Anchoring is the most reliable way to fix the order of figures and captions without
building Articles for every page.

### 1.3 Semantics of text: mapping export tags (1.3.1 Info and Relationships, 2.4.6 Headings and Labels)

Each paragraph style can carry `StyleExportTagMap` entries (scripting: `paragraphStyle.styleExportTagMaps`, with
`exportType`, `exportTag`, `exportClass`, `exportAttributes`). Type `PDF` maps the style to a PDF tag: `H1`…`H6`, `P`,
`Artifact`, or *Automatic* (no map, which exports `P`). Type `EPUB` maps the same style for EPUB/HTML (h1, p, …).
Character styles carry EPUB maps only (span, em, strong). A style inherits its map through `BasedOn`. *Edit All
Export Tags* in the Paragraph Styles panel edits the same data.

This only works when the text **uses** the styles. A heading formatted by hand on `[Basic Paragraph]` exports as
`P` whatever the maps say. Fake structure has the same problem: empty paragraphs for spacing (announced as "blank"),
forced line breaks inside a paragraph, typed "•" bullets instead of automatic bullets, and tabs used to make columns.

**Lists:** paragraphs with InDesign's automatic bullets or numbering (`BulletsAndNumberingListType="BulletList"` /
`"NumberedList"`) export as `L` / `LI` / `Lbl` / `LBody`. Typed bullets export as plain `P`.

### 1.4 Alternative text (1.1.1 Non-text Content)

Every page item can have an `<ObjectExportOption>` child (scripting `objectExportOptions`; object styles have one
too, so a whole class of frames can share the setting). Relevant attributes, verified against the scripting DOM and
the reference file:

| Attribute | Values (IDML spelling) | Meaning |
| --- | --- | --- |
| `AltTextSourceType` | `SourceXMLStructure` (default), `SourceCustom`, `SourceXMPTitle`, `SourceXMPDescription`, `SourceXMPHeadline`, `SourceXMPOther` | where the alt text comes from |
| `CustomAltText` | text, `$ID/` when empty | used only when the source is `SourceCustom` |
| `ActualTextSourceType`, `CustomActualText` | same values | replacement text (for text set as graphics) |
| `ApplyTagType` | `TagFromStructure` (default), `TagArtifact`, `TagBasedOnObject` | *Apply Tag*: structure, artifact, or Figure/Story by object kind |
| `Properties/AltMetadataProperty` | `NamespacePrefix`, `PropertyPath` | which XMP property, for `SourceXMPOther` |

Two traps, both present in real files:

- **The default source is the XML structure,** which is empty in most documents. The result is a Figure with no
  `/Alt`, which fails PDF/UA and WCAG 1.1.1.
- **Custom text without the custom source does nothing.** `CustomAltText` is ignored unless `AltTextSourceType` is
  `SourceCustom`. Adobe forum reports confirm that both must be set.

The XMP sources read the placed image's own metadata (`dc:title`, `dc:description`, `photoshop:Headline`). That is
useful for photo libraries with good captions, and empty for most others.

### 1.5 Artifacts (1.3.1; PDF/UA 7.1: content is either tagged or an artifact)

Content that means nothing to a listener must be an artifact: decoration, rules, background shapes, running heads,
page numbers, and table headers repeated on continuation pages. InDesign makes artifacts from:

- **master page items:** automatically (folios, running heads, decorative bands on masters);
- **`ApplyTagType="TagArtifact"`** on a page item or its object style;
- **paragraph styles mapped to `Artifact`** (running heads or folios typed on document pages, not masters). One
  forum report says scripting rejected `Artefact` as an export tag; the correct spelling is `Artifact`, to be
  confirmed in phase 0;
- **non-printing layers and hidden items:** not exported at all.

Unfilled shapes and lines are usually artifacted by InDesign, but **decorative images are not**: they need
`TagArtifact` explicitly, or they become Figures without alt text.

### 1.6 Tables (1.3.1)

What IDML can express, and InDesign exports:

- **`HeaderRowCount`** (0–25): the first N rows are header rows and their cells export as `TH` (column headers).
  Header rows repeat on every frame a table flows into; the repeats are artifacts. `FooterRowCount` likewise.
- **Merged cells:** `Cell RowSpan` / `ColumnSpan` export as `RowSpan` / `ColSpan` attributes.
- A cell's `rowType` is `BodyRow`, `HeaderRow` or `FooterRow`. **There is nothing else:** no row headers (a first
  column of `TH`), no `Scope`, no `Headers`/`ID` associations, no table summary, no caption tied to the table.

So **complex tables cannot be made fully accessible in InDesign.** Tables with row headers, several header levels, or
headers that apply to a subset of columns need post-processing: the Acrobat table editor, a remediation tool, or
CoreIdml's own tagged export (phase 4b, which can do better than InDesign here). What the IDML side *can* do is
detect them, set header rows where they are missing, keep a merged cell from mixing a header with body cells, and
flag **layout tables** (tables used to position content), which should not be tables at all.

### 1.7 Language (3.1.1 Language of Page, 3.1.2 Language of Parts)

Every run has `AppliedLanguage` (`$ID/English: USA`, `$ID/Spanish: Castilian`, …), and `designmap.xml` lists the
`Language` resources. InDesign writes the language into the tagged PDF for text whose language differs from the
document language, which is chosen at export (*Advanced > Accessibility options > Language*). Wrong languages are
common in text pasted from Word, and they make the screen reader switch voice and pronunciation. Hyphenation and
spelling also follow `AppliedLanguage`, so fixing it is safe.

### 1.8 Title and metadata (2.4.2 Page Titled)

The document title is the XMP `dc:title` in `META-INF/metadata.xml` (InDesign's *File > File Info*). PDF/UA also
needs `DisplayDocTitle`, which is an export setting (*Display Title: Document Title*). Without a title the viewer
shows the file name.

### 1.9 Other things a review must cover

- **Links (2.4.4):** hyperlinks become `Link` elements with an annotation; link text should make sense on its own.
- **Contrast (1.4.3 / 1.4.11):** text colour against its background, 4.5:1 (3:1 for large text). Computable from
  swatches, tints and frame fills; the result is approximate over images and gradients.
- **Real text:** text converted to outlines or baked into images has no text (1.4.5 Images of Text). CoreIdml's
  fonts carry ToUnicode maps, so its own PDFs are already extractable.
- **Footnotes, notes, conditions:** footnotes are tagged `Note`; InDesign notes and hidden conditional text are not
  exported. Text a reader needs must not live in them.
- **Forms, bookmarks:** bookmarks come from a table of contents (2.4.5, useful in long documents). Forms need tooltips
  (out of scope for now).

### 1.10 Chosen only at export, not stored in IDML

Not in any IDML part, so a skill must tell the user to set them in InDesign's *Export Adobe PDF* dialog:
*Create Tagged PDF*; *Use Structure for Tab Order*; *Create Acrobat Layers* off; *Hyperlinks* and *Bookmarks* on;
*Advanced > Display Title: Document Title*; *Advanced > Language*; and PDF/UA conformance (InDesign 2024 and later
offer an accessibility-oriented preset). These are the same settings CoreIdml's own tagged export (4b) has to emit.

## 2. What the reference file shows

`docs/reference/InDesign/FICHA_CORESCEM_AP-ML.idml` (InDesign 20.0, 6 pages, Spanish) is a typical, uncorrected
document. Read without changing anything, it would fail almost every check:

| Finding | Evidence in the IDML | Fails |
| --- | --- | --- |
| No heading structure | Heading styles exist (`TITULOS TEXTO:TITULO 0–4`, `TITULO 1`) but 843 of 851 paragraph-style applications are `NormalParagraphStyle` or `[No paragraph style]`; no `StyleExportTagMap` anywhere | 1.3.1, 2.4.6 |
| No alt text | All 505 `ObjectExportOption`s use `SourceXMLStructure`; the structure is an empty `Root`. 16 images and 3 placed PDFs become Figures without `/Alt` (or must be marked decorative) | 1.1.1 |
| Reading order left to InDesign's guesses | No Articles, `StructureOrder="UseXMLStructure"` with an empty structure, 148 text frames | 1.3.2 |
| Tables without headers | 37 tables, only 4 with `HeaderRowCount="1"`; heavy merging (`RowSpan` up to 8, `ColumnSpan` up to 6), so several tables are complex | 1.3.1 |
| Wrong language | 103 ranges in `ta_IN` (Tamil) in a Spanish document, besides `Spanish: Castilian` and `English: UK` | 3.1.2 |
| No title | No `dc:title` in `META-INF/metadata.xml` | 2.4.2 |
| Fine | Automatic bullets and numbering in the list styles (`BulletList`, `NumberedList`), no typed bullets; one printable layer | |

This file is the first acceptance test of the checker: phase 2's report on it must contain these findings.

## 3. What CoreIdml has today

| Area | Library (CoreIdml) | MCP tools | Own PDF (CoreIdml.Pdf) |
| --- | --- | --- | --- |
| Reading order | XML structure: full API (`TagCatalog`: select, tag story/item, import, export). Threads. No Articles, no `TaggedPDFPreference` | `idml_xml_structure`, `idml_import_xml`, `idml_export_xml` | paint order only |
| Inline anchors | Read and composed (`StoryReader.InlineObject`, `IdmlRenderer.AnchoredFrameOps`); no API to anchor or unanchor an item | none | inline anchors drawn |
| Export tags | none (not read, not written) | none | none |
| Alt text / artifacts | none (`ObjectExportOption` kept byte for byte, never read) | none | none |
| Tables | `HeaderRowCount`/`FooterRowCount` read; header rows only at creation; `TableCell.RowSpan/ColumnSpan` read | `idml_edit_table`: cells, rows, sizes; no header rows | header rows repeat; no TH |
| Lists | not modelled in `ParagraphFormat` | none | drawn |
| Language | `CharacterFormat.Language` (write), `Languages` constants, read by the composer | none | no `/Lang` |
| Title / XMP | raw part access only (`Package.GetBytes("META-INF/metadata.xml")`) | none | `/Title` in the Info dictionary only |
| Contrast | `Swatches.ResolveColor` (process, spot, tints) | none | |
| Hyperlinks | not modelled | none | none |
| Tagged PDF | | | **none**: no StructTreeRoot, MarkInfo, `/Lang`, XMP, `DisplayDocTitle`, `/Tabs`; ToUnicode present |
| Checks | `IdmlValidator`: package integrity only | `idml_validate` | |

## 4. Extension plan

The work comes in two tracks, with a shared checker:

- **Track A, prepare the IDML** (phases 1–3): read, check and fix everything in section 1 in the IDML, so that
  InDesign's tagged export is right the first time. This is how 508 deliverables are made today, and it is what the
  skill needs first.
- **Track B, PDFs** (phase 4): check a PDF exported from InDesign (4a, to close the loop), then let CoreIdml write
  tagged PDF/UA itself (4b, for generated documents such as datasheets that never pass through InDesign).

### Phase 0: ground truth (small, blocks phase 1)

`StyleExportTagMap` and `Article`/`ArticleMember` are confirmed in the scripting DOM, but no file we have contains
them, so their **exact IDML serialization is still inferred**. Before writing them:

1. In InDesign, make `tests/fixtures/accessibility-indesign.idml`: two heading styles mapped to H1/H2 and one style
   mapped to Artifact (PDF and EPUB maps); two Articles with `Use for reading order in tagged PDF` on; one image with
   custom alt text, one marked Artifact, one object style with export options; an inline-anchored image and an
   above-line one; a table with 1 header row and 1 footer row; a hyperlink; automatic bullets; one paragraph in
   another language; *File Info* title. Save as IDML.
2. Export it from InDesign as a tagged PDF (`…-indesign.pdf`, with the settings of section 1.10). That is the reference
   for 4a and 4b.
3. Write down the exact XML (element names, where `Article` lives, the `Artifact` spelling, the `Self` id format) and
   fix sections 1.3–1.5 if they differ.

### Phase 1: library model (CoreIdml), size M

New wrappers over the live XML, following the existing pattern: read the effective value through style inheritance,
create elements with InDesign's default attributes (copied from a real `ObjectExportOption` in `Blank/`), and
touch only the parts that change.

| API | Notes |
| --- | --- |
| `PageItem.ExportOptions` → `ObjectExportOptions` (`AltText`, `AltTextSource`, `ActualText`, `ActualTextSource`, `TagAs` = `FromStructure`/`Artifact`/`BasedOnObject`, `EffectiveAltText`) | `SetAltText(text)` sets the source to `SourceCustom` **and** the text (trap 2 of 1.4). Also on a new minimal `ObjectStyle` wrapper; the effective value takes the item's object style into account |
| `ParagraphStyle.ExportTags` (`Pdf`, `Epub`, `ExportClass`) and `EffectiveExportTag(kind)` | through `BasedOn`; `null` = Automatic. `CharacterStyle.EpubTag` |
| `IdmlDocument.Articles` → `ArticleCatalog` (`Add(name)`, `Article.Add/Insert/Remove/Move(member)`, `ExportStatus`, order) | items deleted by `RemoveItems` must also leave their articles (update `ItemOperations`), and `ImportPages` must remap `ItemRef`s |
| `IdmlDocument.TaggedPdfReadingOrder` (`XmlStructure` / `Articles`) | `designmap.xml` |
| `IdmlDocument.Metadata` (`Title`, `Description`, `Language`) | edits `META-INF/metadata.xml` in place (XMP), creates `dc:title` when missing |
| `Table.SetHeaderRows(n)`, `SetFooterRows(n)` | header rows are the first N `Row`s: only the counts change, plus a check that no merged cell crosses the header/body boundary (refuse, with the cell) |
| `Story.Anchor(PageItem item, int paragraphIndex, AnchorPosition position)` and `PageItem.Unanchor()` | moves the item's element from the spread into a `CharacterStyleRange` (at the paragraph's end or start), converts its transform to frame-relative, writes `AnchoredObjectSetting`. **Highest risk:** must round-trip in InDesign; reuse the geometry that `IdmlRenderer.AnchoredFrameOps` already reads |
| `ParagraphFormat.ListType` (`None`, `Bullet`, `Numbered`), read and write | needed by the checker (typed bullets) and to fix them |
| `IdmlDocument.SetLanguage(from, to)` and per story/paragraph | returns the count; `Languages` gains the common InDesign names |
| JSON spec (`IdmlJson.Apply`/`CreateDocument`): `altText`, `decorative`, `exportTag` on styles, `articles`, `title`, `language`, `headerRows` for existing tables | so every binding gets it through the existing `DocumentApply` ABI call without new native exports |
| `SummaryModel`: `ItemSummary.AltText`, `ItemSummary.TagAs`, style export tags, articles | in `idml_summarize` |

Tests (`tests/CoreIdml.Tests`): round trips on the phase 0 fixture (read what InDesign wrote, write it back
unchanged, set and re-read), byte identity of untouched parts, and an InDesign open check of a written file (manual,
recorded in porting-plan.md's validation record).

### Phase 2: the accessibility checker (CoreIdml), size M

`AccessibilityReport IdmlAccessibility.Check(IdmlDocument, AccessibilityOptions)`, pure and platform independent
(it runs in every binding and in WebAssembly). Each issue has a rule id, severity, the WCAG criterion (and the
Matterhorn checkpoint where there is one), its location (page, item id, story id, paragraph index, table and cell)
and a fix hint naming the tool that fixes it. Rules:

| Rule | Detects | Criterion |
| --- | --- | --- |
| `alt-missing` | image, placed PDF/EPS, or graphic-content group that is not an artifact and whose effective alt text is empty (any source; XML source resolved when the structure binds it) | 1.1.1 |
| `alt-suspicious` | alt text that is a file name, "image", "photo", identical to the nearby caption, or very long (> 250 chars) | 1.1.1 |
| `decorative-candidate` | image or shape with no alt text that looks decorative (background, full bleed, tiny, behind text) — asks, never decides | 1.1.1 |
| `no-headings` / `heading-unmapped` | heading-like styles (name, size, weight) with no PDF export tag; documents with no H tags at all | 1.3.1, 2.4.6 |
| `heading-skip` | an H3 after an H1, and similar, in predicted reading order | 1.3.1 |
| `local-heading` | short, large or bold paragraphs on body styles (formatting instead of styles) | 1.3.1 |
| `fake-structure` | empty paragraphs used for spacing, typed bullets, tab-separated pseudo-tables, runs of forced line breaks | 1.3.1 |
| `reading-order` | `UseXMLStructure` with an empty or partial structure; Articles that miss page items with content; unthreaded frames that continue the same text | 1.3.2 |
| `float-figure` | figure and caption not anchored, or not adjacent in the article order | 1.3.2 |
| `table-no-header` | data tables (more than one row and column, text in the first row) with `HeaderRowCount` 0 | 1.3.1 |
| `table-complex` | row headers (first column styled as a header), multi-row headers, merged cells crossing the header boundary: needs post-processing, explained | 1.3.1 |
| `table-layout` | tables used for layout (one row or column, graphics in cells) | 1.3.1 |
| `language-unexpected` | languages that cover very little text or do not match the document language (the `ta_IN` case); no document language | 3.1.1, 3.1.2 |
| `title-missing` | no `dc:title` | 2.4.2 |
| `contrast` | text colour against the frame or page fill (tints resolved; images and gradients reported as "not checked") below 4.5:1 / 3:1 large | 1.4.3 |
| `artifact-candidate` | page numbers, running heads and decorative items on document pages instead of masters | PDF/UA 7.1 |
| `hidden-content` | text in notes, hidden conditions, or on non-printing layers that looks like content | 1.3.1 |

Besides the issues, the report returns the **predicted reading order** per page (articles, else structure, else an
approximation of InDesign's layout order, marked as an approximation). The agent shows it to the user to confirm.

Acceptance: the reference file's findings of section 2, plus no false positives on the phase 0 fixture after it is
fixed.

### Phase 3: MCP tools, prompt and bindings (CoreIdml.Cli), size M

| Tool | Access | Does |
| --- | --- | --- |
| `idml_accessibility_check` | Read | the phase 2 report; `pages`, `rules`, `minSeverity`, `includeReadingOrder` |
| `idml_set_alt_text` | Edit | `itemId`/`name`; `altText`, or `decorative: true` (artifact), or `source` (xmpTitle, xmpDescription…); `actualText` |
| `idml_set_export_tag` | Edit | `style` → `pdf` (H1–H6, P, Artifact, Automatic) and optionally `epub` |
| `idml_reading_order` | Read | predicted order, per page |
| `idml_set_reading_order` | Edit | `articles: [{ name, items: [ids] }]`, turns on `UseArticles`; `append` mode for adding one page at a time |
| `idml_anchor_item` | Edit | anchor an item into a story at a paragraph (inline or above line), or release it |
| `idml_set_metadata` | Edit | `title`, `description`, document `language` |
| `idml_set_language` | Edit | `from` → `to` everywhere, or for a story / paragraph range |
| `idml_edit_table` | Edit | new ops `setHeaderRows`, `setFooterRows` |
| `idml_render_page` | Read | new `overlay: "readingOrder"` (numbered badges on each element in reading order) and `"artifacts"` (greyed) to show the order and decisions to the user |

All edit tools work inside `idml_batch` and with `dryRun`. New prompt `accessibility_review` (report only), and a
fix variant `make_accessible` (check, propose, confirm, fix, re-check). Update the tool count (33 → 41) in
`plugin.json`, the plugin README, `McpPrompts.Instructions`, the CLI README, the `.mcpb` manifest and the catalog
tests. Bindings: one new ABI call `DocumentAccessibilityCheck(document, optionsJson, out json)` in
`CoreIdml.Interop` / `NativeExports` / WASM exports, generated with `tools/CoreIdml.AbiGen`, plus wrappers and tests
in node, python, dart and wasm; the edits go through `DocumentApply` (phase 1 JSON spec).

### Phase 4a: check a PDF (CoreIdml.Pdf), size M

`PdfAccessibility.Check(Stream pdf)` and tool `idml_check_pdf`, using the existing PDF reader (`PdfSource`, the
importer's content parser). It checks what can be decided without a full validator: tagged (`/MarkInfo /Marked`,
`StructTreeRoot`), `/Lang`, title with `DisplayDocTitle`, `/Tabs /S`, Figures without `/Alt`, tables without `TH`,
heading skips, empty tags, untagged marked content outside artifacts, link annotations outside the structure, fonts
not embedded or without ToUnicode, and PDF/UA identification in XMP. It compares with the IDML when both are given
(an image with alt text in the IDML has none in the PDF → the export setting was off). It says plainly that it is
a subset: sign-off is a full validator (veraPDF PDF/UA-1 in CI, using the JDK we already have; PAC or Acrobat's Full
Check by hand), and a test with a real screen reader (NVDA, JAWS, VoiceOver).

### Phase 4b: tagged PDF/UA from CoreIdml (CoreIdml.Pdf), size L

`PdfExportOptions.Tagged = true` (and `PdfUa = true`), built from the phase 1 model:

- **Structure tree** in reading order (Articles, else structure, else the predicted order): `Document` → `Sect`/`Art`
  per article or story → `H1`–`H6`/`P` from export tags; `L`/`LI`/`Lbl`/`LBody`; `Table`/`THead`/`TBody`/`TR`/`TH`
  (`Scope Column`)/`TD` with `RowSpan`/`ColSpan`; `Figure` with `/Alt` and `BBox`; `Link`; `Note`; `Span /Lang`.
- **Marked content** in `IdmlRenderer` / `PdfDocumentBuilder`: every draw op carries its structure element; text,
  images and paths are wrapped in `/Tag <</MCID n>> BDC … EMC` in paint order (the structure tree supplies the
  reading order, so paint order and reading order can differ); master items, decorative items, repeated header rows
  and hyphens at line ends are `/Artifact BMC … EMC`. `StructParents` on each page, `ParentTree` number tree,
  `RoleMap` if custom tags are used.
- **Document level:** `/MarkInfo <</Marked true>>`, `/Lang`, `/ViewerPreferences <</DisplayDocTitle true>>`, an XMP
  metadata stream with `dc:title` and `pdfuaid:part 1`, `/Tabs /S` on pages with annotations.
- **Beyond InDesign:** row headers and header scopes that IDML cannot store, taken from a CoreIdml key in the
  table's script label (`coreidml:rowHeaders=1`, ignored by InDesign). This answers the "complex table headers" case
  inside our own export.
- Validation: veraPDF PDF/UA-1 in `tests/CoreIdml.Pdf.Tests` (marked as a slow test), on the phase 0 fixture and on
  the reference file after the phase 3 fixes; manual NVDA pass recorded.

Limits stay as they are: CoreIdml's PDF is an approximation of InDesign's layout (greedy line breaks, no text wrap,
no effects). For print the deliverable remains InDesign's PDF; 4b targets generated documents.

### Phase 5: the skill, docs and demo, size S

- `plugins/coreidml/skills/idml-accessibility/SKILL.md` from [the draft](accessibility-skill-draft.md), adjusted to
  the tools as they ship. Mention it in `idml-documents/SKILL.md` ("for accessibility, follow idml-accessibility").
- `plugins/coreidml/samples/report-inaccessible.idml`: a small sample made for the demo, with one of each problem in
  section 2, and the README walkthrough ("Check samples/report-inaccessible.idml for accessibility and fix what you
  can").
- Public docs: an accessibility section in `src/CoreIdml/README.md`, `src/CoreIdml.Pdf/README.md` (4b) and the CLI
  README; `plugin.json` description and keywords (`accessibility`, `508`, `wcag`, `pdf-ua`).

### Order and dependencies

```
0 ground truth ─► 1 model ─► 2 checker ─► 3 tools + skill (phase 5 draft usable here) ─► release
                       └──────────────► 4a PDF check ───────────────────────────────────┘
                       └──────────────► 4b tagged export (after 2, reuses its reading-order logic)
```

Phases 0–3 plus 5 make the feature the user asked for (review and fix, with InDesign doing the export). 4a closes
the loop after the InDesign export and is cheap. 4b is a separate, larger decision.

## 5. Decisions needed

1. **Who exports the final PDF?** InDesign (phases 0–3, 4a, 5), or CoreIdml too (4b, a much bigger job)?
2. **Target:** WCAG 2.0 AA through Section 508 (the US federal baseline), or WCAG 2.1/2.2 AA as well (EN 301 549,
   European Accessibility Act)? PDF/UA-1 or PDF/UA-2? The checker's rules are mostly the same; the wording and the
   validator profile change.
3. **EPUB/HTML:** the same export maps carry EPUB tags. Include them in `idml_set_export_tag` now (cheap), or leave
   them for later?
4. **Phase 0 fixture:** it needs InDesign once. Who makes it?
