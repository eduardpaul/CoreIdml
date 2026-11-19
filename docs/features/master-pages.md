# Master pages

Status: **plan, not implemented.** Written against commit `691f87a`; the files it then marked **(wip)** were committed in `dc5bbf4` (1.1.0), so their line numbers may have moved. Overview, order, milestones and open questions: [README.md](README.md).


## IDML

- **The master spread.** Each `MasterSpreads/MasterSpread_<id>.xml` is listed in `designmap.xml`. The
  `<MasterSpread>` element carries `Self`, `Name` ("A-Master"), `NamePrefix` ("A"), `BaseName` ("Master"),
  `PageCount`, `ShowMasterItems`, `PrimaryTextFrame` and `OverriddenPageItemProps`. Its `Page` children are built
  like a spread's.
- **Based on.** A master based on another master shows it as `AppliedMaster` on the master's own pages. FICHA's
  masters have `AppliedMaster="n"`.
- **Document pages** carry three attributes:
  - `AppliedMaster`: the master's id, or `n`;
  - `MasterPageTransform`;
  - `OverrideList`: the ids of the master items this page overrides.
- **Overrides (memory):** the overriding copy sits on the document spread and links back to its master item. Check
  the attribute name in phase 0.
- **Hide Master Items** is `Spread@ShowMasterItems="false"`.
- **Current page number** is `<Content><?ACE 18?></Content>`. Text variables are `<TextVariableInstance ...
  ResultText="12" AssociatedTextVariable="..."/>` (memory). FICHA's `designmap.xml` defines them (`LastPageNumberType`,
  `MatchParagraphStyleType`, ...).

## Current state

**DOM**

- `IdmlDocument.MasterSpreads` (`IdmlDocument.cs:83-84`).
- `MasterSpread` (`Dom/Spread.cs:121`) exposes only its `Name`.
- `Page.AppliedMaster` has a getter only (`Dom/Page.cs:37-44`); there is also `MasterPageTransform` (47).
- `AddMasterSpread` is private (`IdmlDocument.cs:400-430`) and called once, by `Create` (128). Every new document
  therefore has exactly one master, "A-Master".
- `AddPage` (181-199) and `InsertPage` (`PageOperations.cs:11`) always apply `MasterSpreads[0]`.
- Items can already be added to a master, because `Page` methods work on any spread:
  `master.Pages[0].AddText(...)` is used in `tests/CoreIdml.Pdf.Tests/LayoutTests.cs:325-330`.

**Renderer: done**

- `IdmlRenderer.RenderPage` (289-292) calls `DrawMaster` (304-335), which:
  - picks the left or right master page;
  - paints based-on masters first (depth at most 4);
  - skips items listed in `OverrideList`;
  - maps master space to the page through `MasterPageTransform`.
- `MasterFrameOps` (197-223) composes master stories once per page, so `ACE 18` prints each page's name.
- `RenderedSpreads` (730-748) adds masters to image prefetch.
- The FICHA comparison in `docs/reference/Comparison` already exercises this.
- **Gaps:**
  - `Spread@ShowMasterItems` is ignored (no reference in `CoreIdml.Pdf`).
  - `TextVariableInstance` is in `StoryReader.Transparent` (`StoryReader.cs:15`) and has no children, so variables
    such as "Last Page Number" print nothing.

**Summary, validation and tools**

- The summary has no masters, and `PageSummary` (`SummaryModel.cs:69-84`) has no master field. Links do report
  location `"master"` (`IdmlJsonRead.cs:151`).
- Validation reports `missing-master` (`Validation.cs:93-100`).
- MCP tools find items through `document.AllPageItems`, which covers spreads only: `RequireItem`
  (`LayoutTools.cs:407`, wip), `Finder.cs:46` and `FindPageItem` (`IdmlDocument.cs:205`). Master items therefore
  cannot be found, edited or deleted.
- `idml_add_page` always uses the first master.

**Composition and import**

- `ImportPages` does not copy masters (`Composition.cs:13-14`).
- PDF import has no master support.

## Design

**DOM** (`IdmlDocument.cs`, `Dom/Spread.cs`, `Dom/Page.cs`):

```csharp
// IdmlDocument
public MasterSpread AddMasterSpread(string name, MasterSpread? basedOn = null);  // "B-Chapter" → prefix B, base name Chapter;
                                                                                // "Chapter" → next free prefix; pages: 2 if FacingPages else 1
public MasterSpread? FindMasterSpread(string nameOrPrefix);                     // "B-Chapter" or "B"
public Page InsertPage(int index, MasterSpread? master);                        // overload; null applies none
public IEnumerable<PageItem> AllPageItemsWithMasters { get; }                   // for tools and FindPageItem(s, includeMasters)

public sealed class MasterSpread : Spread
{
    public string Prefix { get; set; }                     // keeps Name = Prefix + "-" + BaseName
    public string BaseName { get; set; }
    public MasterSpread? BasedOn { get; set; }             // sets AppliedMaster on its pages; rejects cycles
    public IReadOnlyList<Page> AppliedTo { get; }          // document pages using it
    public void Remove(MasterSpread? replacement = null);  // pages get the replacement (or none); its stories go too
}

// Page
public MasterSpread? AppliedMaster { get; set; }           // setter: id or "n", MasterPageTransform = identity
public Page? MasterPage { get; }                           // the master page drawn behind this one (left/right)
public IReadOnlyList<PageItem> MasterItems { get; }        // items its master chain shows, minus OverrideList
// Spread
public bool ShowMasterItems { get; set; }
```

**Move side selection into the core.** `Page.MasterPage` takes the "left or right master page" logic out of
`IdmlRenderer.DrawMaster` (312-316). The renderer, the summary and text wrap (feature 3, wraps on master items) then
share it, along with a `Page.MasterToPage` matrix: `Invert(masterPage.Transform) · MasterPageTransform ·
page.Transform`.

**Later (not in M1):** `Page.OverrideMasterItem(PageItem)` copies the item to the page and adds its id to
`OverrideList`. It is blocked on the phase-0 override format.

**Spec** (`SpecModel.cs`, wip). New fields:

- `DocumentSpec.Masters` (`List<MasterSpec>`);
- `PageSpec.Master` (`string`: a master name, or `"none"`);
- `RunSpec.Marker` (`string`, only `"pageNumber"` for now).

`Render` handles them in this order: colours, styles, masters, then pages.

```json
{
  "masters": [
    { "name": "A-Master",
      "margins": { "all": 15 },
      "items": [                                   // on every page of the master (both sides of a facing master)
        { "type": "line", "x": 15, "y": 12, "x2": 195, "y2": 12, "stroke": "Brand" } ],
      "left":  [ { "type": "text", "x": 15,  "y": 285, "width": 30, "height": 6,
                   "paragraphs": [ { "style": "Folio", "runs": [ { "marker": "pageNumber" } ] } ] } ],
      "right": [ { "type": "text", "x": 165, "y": 285, "width": 30, "height": 6, "paragraphs": [ { "style": "Folio", "align": "right", "runs": [ { "marker": "pageNumber" } ] } ] } ] },
    { "name": "B-Opener", "basedOn": "A-Master", "items": [ ] }
  ],
  "pages": [ { "master": "B-Opener", "items": [ ] }, { "items": [ ] }, { "index": 4, "master": "none" } ]
}
```

- **Coordinates** are relative to the master page, as for pages.
- **`left` and `right`** are valid only with `facingPages`; `SpecChecker` warns when a single-sided document uses them.
- **The default master.** A spec master whose name (or prefix "A") matches the document's existing "A-Master" fills
  it in instead of adding a second one. So the common case, one master, needs no `pages[].master`, and new pages keep
  defaulting to the first master. This is open question 2.
- **`marker`.** A run with `marker` writes `Paragraph.AddPageNumber` (`Dom/Story.cs:163-169`); its `text` is ignored.
  (Alternative: a text token. See open question 2.)
- **`SpecChecker`:** new `TopFields` entry `masters`, plus `MasterFields` (`name`, `basedOn`, `margins`, `items`,
  `left`, `right`). `PageFields` gets `master` and `RunFields` gets `marker`.
- **`SpecSchema`:** a `master` definition.
- **Result:** `idml_create`/`idml_apply` add a `masters` array with the created items.

**MCP**

- New `idml_set_master` {`pages`: [indices] or `pageIndex`, `master`: name or `"none"`}.
- `idml_add_page` gets a `master` argument.
- Masters are created through `idml_apply`'s spec. Agents already use that route for styles, and it avoids a
  parallel item-creation API.
- `RequireItem`, `Finder` and `FindPageItem(s)` include master items, and their results say `master: "A-Master"`.
  `idml_edit_item`, `idml_delete_item` and `idml_duplicate_item` then work on masters.
- Optional: `idml_duplicate_item` with `toMaster`, to move a repeated header from a page into a master.

**Summary.** A new section `"masters"` (added to `SummaryOptions.SectionNames`, `SummaryModel.cs:31`):
`[{ name, basedOn, usedBy: [page indices], pages: [{ side, items: [ItemSummary] }] }]`. `PageSummary` gets `master`.
The section stays off by default when `sections` is given, so summaries stay small.

**Validation**

- `master-cycle` (error): a based-on loop.
- `master-override-missing` (warning): an `OverrideList` id that is not on the master chain.
- `master-page-count` (warning): a facing document with a one-page master.

**Renderer fixes**

- Skip masters when `page.Spread.ShowMasterItems` is false (`IdmlRenderer.cs:289`).
- `StoryReader` reads `TextVariableInstance@ResultText` as text; this is InDesign's last value, a fair approximation.
- Optional: compute `LastPageNumberType` from the page count.

**PDF import (optional, M).** Add `PdfImportOptions.DetectMasters` (default off). After pages are built, any item
found on at least two-thirds of the pages, with the same geometry and content, moves to "A-Master". Text that differs
only by a number equal to the page number becomes an `ACE 18` marker.

**Composition (optional, S–M).** `ImportPages` copies the source masters a page uses, matched by name, instead of
applying the target's first master.

## Tests

- **Core:**
  - `AddMasterSpread` writes valid XML: names, prefix, page count, page transforms as in FICHA.
  - `BasedOn` refuses cycles.
  - The `AppliedMaster` setter works.
  - Save and reopen round trip.
  - Spec: masters, `left`/`right`, `marker` (asserts `<?ACE 18?>`), reusing "A-Master".
  - `CheckSpec` warnings.
  - Schema and model field sync.
- **MCP:** `idml_set_master`; editing a master item by id; the summary `masters` section.
- **PDF:**
  - A folio on a master renders as "1", "2", "3" (text from `LineInfo`).
  - `ShowMasterItems=false` hides master items.
  - The text variable prints its `ResultText`.
  - Phase-0 masters sample in `VisualTests`. The FICHA comparison must not change.

**Effort: M** (about 7 to 8 days): DOM 2, spec and checker and schema 2, MCP and summary 2, renderer fixes and
validation 1, docs 1. Import detection is another M; composition is another S.

**Risks**

- The override format (memory).
- Masters whose page size differs from the document's. InDesign scales them through `MasterPageTransform`; CoreIdml
  should keep the matrix and not write new values.
- Whether InDesign accepts a one-page master in a facing document. It does in the UI; check that it imports.
