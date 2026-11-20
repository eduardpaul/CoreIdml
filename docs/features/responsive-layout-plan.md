# Responsive layouts and editions (Flex Layout, Liquid rules, alternate layouts, conditional text): analysis and extension plan

> Status: **plan, not implemented.** Combined work plan: [accessibility-and-variants.md](accessibility-and-variants.md). Goal: a skill plus the tools it needs so that one
> master IDML produces **several formats** (A4 and US Letter, square and 16:9 social, banners) and **several
> editions** (languages, markets, product variants) with the layout adapting itself, without opening InDesign for
> every variant.
>
> Verdict: **the library does not have what the skill needs.** CoreIdml keeps every rule an InDesign file carries
> (untouched parts are written back byte for byte), but it reads or writes none of them, cannot resize a page, and
> has no engine that applies a rule. One gap is also **a defect today, independent of this feature:** the PDF composer
> ignores conditional text, so hidden text is drawn and measured. A second is a **compatibility risk:** InDesign
> 2026 files with Flex Layout may hide their frames from CoreIdml. The skill is drafted in
> [responsive-layout-skill-draft.md](responsive-layout-skill-draft.md).

## 1. How InDesign makes a layout adapt

InDesign has four separate mechanisms, from different eras. A master file usually needs more than one.

| Mechanism | Since | What it adapts | Where it lives in IDML | Who computes the result |
| --- | --- | --- | --- | --- |
| Liquid Layout rules | CS6 (2012) | page items when a **page** changes size | `Page@LayoutRule`, `Page@SnapshotBlendingMode`, `PageItem@HorizontalLayoutConstraints` / `VerticalLayoutConstraints`, liquid guides, snapshots | InDesign, when the page is resized or an alternate layout is created |
| Alternate Layouts | CS6 | several page sizes **in one document**, stories optionally linked | `Section@AlternateLayout`, `@AlternateLayoutLength`; `Page@AppliedAlternateLayout`; `LinkedStoryOption`, `LinkedPageItemOption` | InDesign (*Create Alternate Layout*, which applies the liquid rules) |
| Auto-size text frames, flexible columns, frame fitting | CS6 / CC | a **frame** to its text, an image to its frame | `TextFramePreference@AutoSizingType`, `@AutoSizingReferencePoint`, minimums, `@UseFlexibleColumnWidth`; `FrameFittingOption` | the composer, every time text changes |
| Flex Layout | InDesign 2026 (v21), reworked in 22.0 (Nov 2026) | children of a **container**, in rows or columns, like CSS Flexbox | not documented (see 1.4) | InDesign, continuously |
| Conditional text | CS4 | **which text exists** in an edition | `Condition`, `ConditionSet`, `ConditionalTextPreference@ActiveConditionSet`; `CharacterStyleRange@AppliedConditions` | InDesign hides runs; the composer reflows |

The rules are **stored** in the IDML, but the **result** of a resize is computed only by InDesign. A master that has
rules does not contain its A5 or 16:9 version. Making those without InDesign means CoreIdml must run the rules
itself. That engine is most of this plan.

### 1.1 Liquid Layout: page rules and object constraints

`Page@LayoutRule` (scripting `LayoutRuleOptions`) decides what happens to a page's items when the page is resized:

| IDML value | Behaviour |
| --- | --- |
| `Off` | nothing moves: items keep their coordinates (default; every page of the reference file) |
| `Scale` | everything is scaled uniformly (text too), as a picture of the page |
| `Recenter` | content keeps its size and is centred on the new page |
| `GuideBased` | items crossed by a **liquid guide** stretch along it; the others keep their size and are moved |
| `ObjectBased` | each item follows its own constraints (below) |
| `UseMaster` | the rule of the page's master |
| `PreserveExisting` | only when creating alternate layouts: keep each page's rule |

**Object constraints** (used by `ObjectBased`): `HorizontalLayoutConstraints` and `VerticalLayoutConstraints`,
three tokens each, for *[left margin, width, right margin]* and *[top margin, height, bottom margin]*. Each token is
`FixedDimension` or `FlexibleDimension`. In the UI, a pin to an edge is a fixed margin and *Auto-resize* is a
flexible size. The default, `FlexibleDimension FixedDimension FlexibleDimension` (the value on all 544 items in the
reference file), means "no pins, fixed size": the item keeps its size and its position scales with the page. The
model behind it is simple. Along each axis the three lengths add up to the page size; when the page grows by Δ,
the fixed lengths stay and the flexible ones share Δ in proportion to their lengths. This is InDesign's documented
behaviour stated as an algorithm; phase 0 checks it against InDesign's own output.

What follows from a resize:
- **text frames** reflow, with auto-size and flexible columns (`UseFlexibleColumnWidth`, maximum column width)
  adding columns as a frame widens;
- **images** follow the frame's `FrameFittingOption` (auto-fit, fill proportionally, centre);
- **masters** have their own rule, and alternate layouts get their own masters.

**Snapshots** (`SnapshotBlendingMode`: `IgnoreLayoutSnapshots`, `UseNearestSnapshot`, `LimitedSnapshotBlending`,
`FullSnapshotBlending`; methods `snapshotCurrentLayout`, `deleteLayoutSnapshot`) let a designer record how a page
should look at another size and blend between snapshots. Their IDML form is not known yet. None of our files has one.

### 1.2 Alternate Layouts

Every document has at least one alternate layout: the reference file's single section is `AlternateLayout="A4 V"`
(size and orientation), and its 12 pages (6 document pages, 6 master pages) point to it through
`AppliedAlternateLayout`. *Create Alternate Layout* (scripting `createAlternateLayout(spreads, name, width,
height, createTextStyles, linkTextStories, layoutRule)`) copies the pages into a new section with a new size, applies
the liquid rules, can **link** the copied stories to the originals (edits flow from parent to child, warnings on
edited children), and can copy the styles into a style group per layout so each size gets its own type sizes. The
IDML form of linked stories (a `Link` to a story, presumably) must be confirmed in phase 0.

Alternate layouts keep all sizes in one file, which suits digital publishing (Publish Online, EPUB fixed layout).
For print and social deliverables, one file per size is more common; a skill should offer both.

### 1.3 Auto-size text frames

`TextFramePreference@AutoSizingType` (`Off`, `HeightOnly`, `WidthOnly`, `HeightAndWidth`,
`HeightAndWidthProportionally`) with a reference point (`AutoSizingReferencePoint`: the corner or edge that
stays put), minimum height and width, and *No line breaks*. This is what absorbs text expansion between languages
(German and Finnish are often 20–35 % longer than English) and between data records. All 250 text frames of the
reference file have it `Off`.

### 1.4 Flex Layout (InDesign 2026 and later)

Introduced in InDesign 2026 (v21) and reworked in 22.0, the current version. A **FlexObject** is a container page
item (like a group) whose children are placed by a Flexbox model. Its scripting DOM:

- `flexDirection`, `flexWrap`;
- `justifyContent`, `alignItems`, `alignContent`;
- `flexPaddingTop/Right/Bottom/Left`, `flexGapRow`, `flexGapColumn` (0–720 pt);
- `flexWidthMode` / `flexHeightMode` (auto or fixed);
- insertion methods (`addPageItemAt`, `addPageItemBefore` …), and nesting.

A container can be anchored in text. Known behaviour:
- In 21.x, auto-sizing text frames lost their auto-size inside a container; 22.0 makes text frames in a container
  resize to their content.
- Converting existing objects detects their arrangement.
- IDML export of an anchored Flex Layout lost content until 21.6.

**What we do not know:** how a FlexObject is written in IDML (element and attribute names), what older InDesign
versions make of it, and whether child items carry their own flex properties (grow, shrink, align-self). No public
document says. This matters now:
- CoreIdml treats an element it does not know as "not a page item", so it skips its children
  (`PageItem.IsPageItemKind` / `TryWrap` in `Dom/PageItems.cs`).
- If InDesign writes `<FlexObject>` around its children, then fill, find, summaries, fit checks and the PDF all miss
  those frames. That is wrong content in a filled template, with no error.

### 1.5 Conditional text

**Conditions** (`Name`, `Visible`, indicator method, colour and appearance) and **condition sets** (each a list
of *condition = visible/hidden*; one is the `ActiveConditionSet` of `ConditionalTextPreference`) are document
resources. A run lists its conditions in `CharacterStyleRange@AppliedConditions` (space-separated references).

The rule: text with no condition is always shown. **Text with several conditions is shown if any one of them is
visible** (OR, not AND; conditions cannot override one another). Hidden text stays in the IDML, so CoreIdml can see
and switch every edition. Anchored objects in hidden text disappear with it. Hidden text is neither printed nor
exported.

Conditional text is the tool for **small** differences inside one layout: currency and units per market, legal lines
per country, a short label in two languages, a product variant's specs. It is a poor tool for full translations: a
frame sized for English overflows in German, and translators need the whole story, not fragments. For
translations, editions are separate files (or alternate layouts), with the text swapped and the layout adapted.

### 1.6 Localized editions beyond conditions

What a translated edition needs, besides text:
- `AppliedLanguage` per run (hyphenation, spelling, and accessibility: see the accessibility plan);
- room for expansion (auto-size, flexible frames, or Flex containers, plus a fit check);
- fonts that cover the script;
- for Arabic, Hebrew and Indic scripts: the World-Ready composer, `ParagraphDirection` / `CharacterDirection`, and
  mirrored layouts.

Translation vendors want the text out and back in, usually as XLIFF, with stable ids.

## 2. What the reference file shows

`docs/reference/InDesign/FICHA_CORESCEM_AP-ML.idml` (InDesign 20.0, A4 portrait, 6 pages) is not responsive at all,
which is typical:

| Finding | Evidence | Consequence |
| --- | --- | --- |
| No liquid rules | `LayoutRule="Off"` on all 12 pages (document and master), `SnapshotBlendingMode="IgnoreLayoutSnapshots"` | resizing to US Letter in InDesign moves nothing; the bottom of every A4 page falls off |
| No constraints | all 544 items at the default `FlexibleDimension FixedDimension FlexibleDimension` | even with `ObjectBased` nothing would be pinned or stretch |
| No auto-size | 250 `TextFramePreference`s with `AutoSizingType="Off"` | translated text overflows rather than growing the frame |
| One layout | one section, `AlternateLayout="A4 V"` | no other sizes |
| No conditions | `ConditionalTextPreference ActiveConditionSet="n"`, no `Condition` | one edition |
| No Flex | InDesign 20.0 predates it | — |

So no master is responsive by accident. The skill must first **make a master responsive** (choose rules, pins,
auto-size, conditions) and only then **produce variants**. The first step is the one that needs a designer's
judgement and the agent's help.

## 3. What CoreIdml has today

| Area | Library | MCP tools | PDF / composer |
| --- | --- | --- | --- |
| Page size | `Page.Size` read only; document size at creation | none | follows `GeometricBounds` |
| Liquid rules, constraints, guides, snapshots | none (kept byte for byte) | none | — |
| Alternate layouts | `Section` written only when pages are added | none | all pages drawn |
| Auto-size | none | none | the PDF export and `idml_check_fit` size the last frame of a story to its text (phase 1); the model and spec cannot set it yet |
| Flexible columns, frame fitting | `ImageFit` when placing an image | `idml_relink_image` refit | columns fixed |
| Flex Layout | **unknown elements are not page items**: children invisible | — | children not drawn |
| Conditions | none | none | **`AppliedConditions` ignored: hidden text is drawn, measured and filled** |
| Moving and resizing items | `SetBounds`, `MovePageItem`, duplicate, layers | `idml_edit_item`, `idml_duplicate_item` | — |
| Editions by data | `Fill`, `ReplaceText`, XML import, `ImportPages` (one document per record) | `idml_fill`, `idml_replace_text`, `idml_import_xml`, `idml_merge` | — |
| Language | `CharacterFormat.Language` | none | hyphenation per language |
| Complex scripts | — | — | **no bidi, no shaping beyond `liga`**: Arabic, Hebrew and Indic previews are wrong |
| Fit and preview | `TextFit.Estimate` | `idml_check_fit`, `idml_render_page`, `idml_to_pdf` | greedy composer |

The fit check, previews and duplication are what a variant workflow needs to *verify* its output. Those exist.
What is missing is everything that *produces* a variant.

## 4. Extension plan

### Phase 0: ground truth from InDesign 22.0 (blocks phases 1–2)

Make these in the current InDesign and save them as IDML (`tests/fixtures/responsive/`):

1. `liquid.idml`: one page per rule (`Scale`, `Recenter`, `GuideBased` with two liquid guides, `ObjectBased` with
   every pin combination, `UseMaster`), auto-fit image frames, a text frame with flexible columns, one page with a
   snapshot. **Plus InDesign's own result:** the same file resized to US Letter, A5, 1080×1080 px and 1920×1080 px
   (Document Setup with *Adjust Layout* / *Liquid Page Rule*), each saved as IDML. These are the expected outputs
   our engine is tested against, item by item, with a tolerance.
2. `alternate.idml`: an alternate layout made with *Link Stories* and *Create Text Styles* on, one child story
   edited afterwards.
3. `conditions.idml`: three conditions, one hidden, two condition sets, runs with one and with two conditions,
   an anchored image inside conditional text.
4. `autosize.idml`: every `AutoSizingType` and reference point, with overflowing and short text.
5. `flex.idml`: row and column containers, wrap, gaps, padding, every justify and align value, auto and fixed size,
   a nested container, an anchored one, text frames that auto-resize. Also the same file saved from 21.6 if
   available, and opened in InDesign 20 (what happens to the containers).

Record the exact XML, especially for Flex, snapshots and linked stories, in this document.

### Phase 1: correctness fixes, independent of the feature (small, ship first)

1. **Conditional text:** a helper `TextVisibility.IsHidden(element)` (applied conditions all hidden) used by
   `StoryReader` (composer and PDF), `TextFit`, `TextOps.GetText` / summaries (with an option to include hidden
   text), `Fill` and `ReplaceText` (report matches in hidden text separately rather than silently editing an edition
   the user cannot see). Tests with `conditions.idml`.
2. **Flex containers:** once phase 0 gives the element name, make it a page item kind that holds items (a `Group`
   subclass `FlexContainer`), so its children are found, filled and drawn at their stored positions. Until then,
   `IdmlValidator` warns on any unknown element under a spread that contains page items, so a user learns about the
   problem instead of getting silently wrong content.
3. ~~**Auto-size in the composer:**~~ done: the last frame of a story with `AutoSizingType` grows or shrinks (around
   the reference point, within the minimum height) in `CoreIdml.Pdf` before it is drawn and before overset is computed,
   so `idml_check_fit` agrees with InDesign (`IdmlRenderer.AutoSize.cs`; see the feature matrix row).

### Phase 2: model (CoreIdml), size M

| API | Notes |
| --- | --- |
| `Page.LayoutRule`, `Page.SnapshotBlendingMode`; `MasterSpread` pages likewise | enum `LayoutRule` with the IDML values of 1.1 |
| `PageItem.Constraints` → `LayoutConstraints(Horizontal: (Fixed\|Flexible)×3, Vertical: …)`, with helpers `PinLeft/Right/Top/Bottom`, `ResizeWidth/Height` | edits the two attributes |
| `Guide` wrapper with `IsLiquid` | only what `GuideBased` needs |
| `TextFrame.AutoSize` (`Type`, `ReferencePoint`, minimums, `NoLineBreaks`), `TextFrame.FlexibleColumns` (`MaxColumnWidth`) | `TextFramePreference` |
| `GraphicFrame.Fitting` (`AutoFit`, fitting on empty frame, crop) | `FrameFittingOption` |
| `IdmlDocument.AlternateLayouts` (name, pages, size); `Page.AlternateLayout` | sections; linked stories read only until phase 0 shows the format |
| `IdmlDocument.Conditions` (`Add`, `Find`, `Visible`, indicator), `ConditionSets` (`Add`, `Set`, `Activate`), `Story.ApplyCondition(range, condition)` / `RemoveCondition` | applying to a range splits runs: reuse the run splitting in `StoryStyling` |
| `FlexContainer` (`Direction`, `Wrap`, `JustifyContent`, `AlignItems`, `AlignContent`, `Padding`, `Gap`, `WidthMode`, `HeightMode`, `Children`, `Insert`/`Move`) | after phase 0; read first, write second |
| JSON spec and summary: `layoutRule`, `constraints`, `autoSize`, `conditions`, `appliedConditions`, `flex` | so every binding gets them through `DocumentApply` and `DocumentSummary` |

### Phase 3: engines, size L

1. **Resize engine** (`CoreIdml`, pure geometry; `LayoutAdapter.Resize(page or document, newSize, options)`):
   - applies `Scale`, `Recenter` and `ObjectBased` with the model of 1.1, recursively for groups;
   - handles `GuideBased` for the common case (items crossed by a liquid guide stretch, others move);
   - margins, columns and ruler guides follow; masters are resized with their own rules (or new masters for the new
     size);
   - text reflows later (composer); image frames refit by their fitting options;
   - snapshots: `IgnoreLayoutSnapshots` only at first, reported when a page has them.

   It returns a report: each item moved or resized, with before and after values; items that left the page; items
   now overlapping that did not before. Tested against phase 0's InDesign results, with a tolerance and the
   differences listed.
2. **Variant builder:** one master to many outputs, in two modes:
   - **as separate files:** `ResizeTo(size)` on a copy;
   - **as alternate layouts** in the same file: new section, pages duplicated (`DuplicatePageItem`), stories copied
     (linking later, if phase 0 shows it is safe to write), optional style group copy.

   Then the composer pass: auto-size frames, check fit, report.
3. **Flex solver** (after phase 0): CSS Flexbox subset (direction, wrap, justify, align items and content, gaps,
   padding, auto and fixed container size) over child bounds. Text children are measured with the `CoreIdml.Pdf`
   composer through a measuring callback, so the solver itself stays in `CoreIdml`. Used after any edit that
   changes a container's content (fill, set text) and by the resize engine for containers with a fixed size that
   changes.
4. **Editions:**
   - `Edition.Apply(conditionSet | visibility map, language?)` switches the visible conditions (the file stays
     complete);
   - `Edition.Flatten()` writes a copy without hidden text and without conditions, for vendors or tools that do not
     understand conditions;
   - `Strings.Export(doc, format: json | xliff2)` / `Strings.Import(...)` exports and imports translatable segments
     with stable ids (story, paragraph, run). It skips hidden text and master folios, keeps inline tags as XLIFF
     placeholders, and sets `AppliedLanguage` on import;
   - `PseudoLocalize(doc, expansion: 0.3)` lengthens every segment, with accents, to stress-test a master before
     translation.

### Phase 4: MCP tools (CoreIdml.Cli), size M

| Tool | Access | Does |
| --- | --- | --- |
| `idml_layout_rules` | Read | per page: rule, snapshot mode, liquid guides; per item: constraints, auto-size, fitting, flex container; a "responsiveness" summary (pages with `Off`, items without pins near edges, frames without auto-size) |
| `idml_set_layout_rules` | Edit | page rule(s); item constraints (`pin: ["left","bottom"]`, `resize: ["width"]`); auto-size and fitting per frame or for all frames of a kind |
| `idml_resize` | Write | new size (named: A4, Letter, A5, `1080x1080px`, `1920x1080px`, or points/mm/px), as a new file or `asAlternateLayout`; `dryRun` returns the report of phase 3.1 without writing |
| `idml_flex` | Edit | read and change a container's properties and child order (after phase 0) |
| `idml_conditions` | Edit | list, create, show/hide, apply to a story range, remove; condition sets: define, activate |
| `idml_edition` | Write | from a master: activate a set and/or language, flatten optionally, write the edition |
| `idml_export_strings` / `idml_import_strings` | Read / Write | translation round trip (JSON, XLIFF 2.0) |
| `idml_pseudo_localize` | Write | a stress-test copy |

All edit tools work inside `idml_batch` and with `dryRun`. New prompts: `make_responsive` (analyse the master, propose
rules, apply after confirmation) and `make_variants` (a matrix of sizes × editions to files, with a fit report and a
contact sheet of page previews). Update the tool counts and docs as in the accessibility plan.
Bindings: `DocumentResize`, `DocumentEdition`, `DocumentStringsExport/Import` ABI calls (AbiGen), wrappers and tests in
node, python, dart and wasm; the rest goes through `DocumentApply`.

### Phase 5: the skill, sample and docs, size S

- `plugins/coreidml/skills/idml-variants/SKILL.md` from [the draft](responsive-layout-skill-draft.md).
- `plugins/coreidml/samples/campaign-master.idml`: an A4 flyer with rules, pins, auto-size and two condition sets
  (EN/ES prices), and the README walkthrough ("Make Letter, square and 16:9 versions in English and Spanish").
- Library, CLI and plugin docs; keywords (`responsive`, `liquid-layout`, `flex-layout`, `localization`, `variants`).

### Order and dependencies

```
1 fixes (conditions, auto-size; flex after 0) ─► ship on their own
0 fixtures ─► 2 model ─► 3.1 resize ─► 3.2 variants ─► 4 tools ─► 5 skill
                    ├─► 3.4 editions, strings, pseudo-localization ─┘
                    └─► 3.3 flex solver (needs 0's Flex format; can come after the first release)
```

The first useful release is phases 1, 2, 3.1, 3.2, 3.4, 4 and 5 without Flex. Flex reading and drawing comes with
the fixes as soon as the format is known; the Flex solver follows.

## 5. Limits to state plainly

- CoreIdml's resize engine **approximates** InDesign's Adjust Layout and Liquid Layout. A designer reviews each
  variant, and the IDML (opened in InDesign) remains the deliverable for print. Phase 0's comparison shows how close
  it gets; publish those numbers.
- Previews and PDFs of variants share the composer's limits: greedy line breaking, no text wrap, no effects, and
  **no right-to-left or complex scripts**. Arabic, Hebrew and Indic editions can be produced as IDML and checked in
  InDesign, but CoreIdml's fit check and previews are not reliable for them. Supporting them is a separate project
  (bidi, shaping).
- Snapshots, linked story updates and Flex writing depend on formats we have not seen yet.

## 6. Decisions needed

1. **Variant output:** separate files per size, alternate layouts in one file, or both (the plan does both; separate
   files first)?
2. **Localization route:** conditions for small market differences, translated editions through a strings round
   trip, or both? Is XLIFF needed (translation vendors, TMS), or is JSON enough?
3. **Flex Layout:** reading and drawing only (no content lost: needed anyway), or the full solver too?
4. **Complex scripts** (Arabic, Hebrew, Indic): in scope for previews and fit checks? That is a composer project.
5. **Phase 0** needs InDesign 22.0 (and 20 for the compatibility check) once. Who makes the fixtures?
