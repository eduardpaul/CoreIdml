# Layout features: master pages, paragraph rules, text wrap, paths, gradients and effects, drop caps

Implementation plan, no code yet. These are the six features that
`plugins/coreidml/skills/idml-documents/references/document-spec.md` ("Not supported") and the READMEs say CoreIdml
lacks. For each one the plan covers how IDML stores it, what CoreIdml already does, what each layer needs, the tests,
the effort and the risks. It ends with the order, the milestones and the questions the maintainer needs to answer.

**Line references** point at commit `691f87a`. Files marked **(wip)** were uncommitted then and are in `dc5bbf4`
(1.1.0) now, so their line numbers may have moved; they add `ShapeStyle.EndCap`/`Opacity`, the spec checker and the spec JSON Schema:
`src/CoreIdml/Dom/PageItems.cs`, `src/CoreIdml/Json/SpecModel.cs`, `SpecChecker.cs`, `IdmlJson.cs`,
`src/CoreIdml.Cli/Tools/SpecSchema.cs`, `LayoutTools.cs`, and `document-spec.md`.

**"(memory)"** marks IDML facts that come from my knowledge of the IDML format, not from a file in the repository.
Each one must be checked against a real InDesign export (phase 0, below) before the code that depends on it ships.

**Effort:** S is up to 2 days, M up to 1 week, L 1 to 3 weeks, XL more than 3 weeks. Each covers one engineer and
includes tests and docs.

| Feature | Plan |
| --- | --- |
| Master pages | [master-pages.md](master-pages.md) |
| Paragraph rules | [paragraph-rules.md](paragraph-rules.md) (drawn, with paragraph borders and shading; authoring still a plan) |
| Text wrap | [text-wrap.md](text-wrap.md) (drawn, with text in shaped frames; authoring still a plan) |
| Polygons and paths | [polygons-and-paths.md](polygons-and-paths.md) |
| Gradients and effects | [gradients-and-effects.md](gradients-and-effects.md) |
| Drop caps | [drop-caps.md](drop-caps.md) (drawn; authoring still a plan) |

Two further plans, for new plugin skills rather than layout fidelity, have their own work plan in
[accessibility-and-variants.md](accessibility-and-variants.md):

| Feature | Plan | Skill draft |
| --- | --- | --- |
| Accessibility review (Section 508, WCAG, PDF/UA) | [accessibility-plan.md](accessibility-plan.md) | [accessibility-skill-draft.md](accessibility-skill-draft.md) |
| Responsive layouts and editions (Liquid rules, alternate layouts, Flex Layout, conditional text, localization) | [responsive-layout-plan.md](responsive-layout-plan.md) | [responsive-layout-skill-draft.md](responsive-layout-skill-draft.md) |

For every other InDesign feature, what the MCP tools can author and what the preview draws, see
[feature-matrix.md](feature-matrix.md). Its "Gaps not in the plan" section lists features the six plans do not cover.

## What the fixtures show, and phase 0

All five IDMLs (`tests/fixtures/*.idml` and `docs/reference/InDesign/FICHA_CORESCEM_AP-ML.idml`) were unzipped and
searched:

- **Masters are real in FICHA.** It has 6 master spreads (`MasterSpreads/MasterSpread_u14a9.xml` and others), each
  one page, with text frames, rectangles and polygons, and each document spread applies a different master. No page
  overrides a master item: every `OverrideList` is `""`. The other four fixtures have empty two-page "A-Master"s.
- **Polygons are real.** FICHA has `Polygon` elements whose `PathGeometry` holds several *open* `GeometryPathType`s
  (compound open paths). Across the fixtures there are 14 `Polygon`, 30 `GraphicLine` and 55 `Oval` elements.
- **Everything else appears only at its defaults:**
  - All 587 `TextWrapPreference` elements are `TextWrapMode="None"`.
  - `RuleAbove`/`RuleBelow` are `"false"` and `DropCapLines` is `"0"` everywhere.
  - The only gradient is one unnamed `Gradient` swatch (`Name="$ID/"`, `Visible="false"`) in `magazineA-edito`'s
    `Graphic.xml`, and nothing uses it.
  - The full set of effect elements (`DropShadowSetting` ... `GradientFeatherSetting`) appears only in
    `Preferences.xml`, under `TransparencyDefaultContainerObject` (FICHA `Resources/Preferences.xml:31-79`).

**Phase 0 (1 to 2 days, needs InDesign):** build one small InDesign document per feature and export each as IDML
plus a PDF (PDF/X-4, so transparency stays live). Commit the IDMLs to `tests/fixtures/`. Put the PDFs in the visual
samples folder (`COREIDML_PDF_SAMPLES_DIR`, see `tests/CoreIdml.Pdf.Tests/VisualTests.cs`). What each document needs:

- **Masters:** facing pages, a master based on another master, one overridden master item, folios, a "Last Page
  Number" text variable, and a spread with Hide Master Items.
- **Paragraph rules:** rule above and below, column and text width, indents, offsets, a dashed rule with a gap
  colour, "Keep in frame", and a rule on an empty paragraph.
- **Text wrap:** every mode and side, offsets, an inverse wrap, a wrap on a master item, a rotated item, "Text wrap
  only affects text beneath", and a frame with "Ignore text wrap".
- **Paths:** a pen-tool open path with more than 2 points, a closed polygon, a star, a compound path with a hole,
  and arrowheads.
- **Gradients:** linear and radial, angle, start and length set with the Gradient tool, a midpoint, CMYK and RGB
  stops, a gradient stroke, and gradient text.
- **Effects:** drop shadow, inner shadow, outer and inner glow, basic feather, gradient feather, blend modes, and
  fill/stroke/content opacity.
- **Drop caps:** 3 lines by 1 character, 2 characters, with a character style, both detail flags, and a paragraph
  shorter than the drop cap.

Every "(memory)" item below maps to one of these documents.

## Summary

| Feature | IDML model | DOM | Spec / MCP | Render (PDF) | Composer | PDF import | Effort |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1. Master pages | Known; real data in FICHA | Read only. Items can be added via `master.Pages[i]`; no create or apply | None | **Done** (left/right, based-on, OverrideList, per-page folios). Gaps: `ShowMasterItems`, text variables | Page numbers done | None | **M** |
| 2. Paragraph rules | Known; measured in `08-para-rules` and `08-para-borders-shading` | None (kept on round trip) | None | **Done**: rules above and below (widths, indents, stroke types, gap colour), paragraph borders and shading (corners, merged consecutive borders) | **Done**: drawn from the composed lines, `KeepRuleAboveInFrame` | Not planned | **S–M** |
| 3. Text wrap | Known; measured in `02-item-text-wrap` | None (read by the renderer) | None | **Done** (`TextArea`): box, object shape, jump, next column, sides, inverse, ignore wrap, shaped frames. Gaps: detect edges and Photoshop paths; anchored objects wrap only the lines after them | **Done**: line stretches, baseline probing, the paragraph composer plans in the stretches | n/a | **L** |
| 4. Polygons and paths | Known; real data | Reads contours. Creates only rectangles, ovals and 2-point lines | rectangle, ellipse, line | Béziers and compound paths done. Gaps: open paths not filled, no arrowheads | n/a | Rectangles and 2-point lines only | **M** |
| 5a. Gradients | Known and checked against `03-color-gradients` | `ResolveGradient`, `AddGradient` | None | **Done**: linear and radial fills and strokes (shadings, shading patterns), midpoints, spot stops. Gap: radial highlight | Gradient text fill done; text stroke not drawn | None | **M** + **M** PDF infra |
| 5b. Effects | Known; measured in `04-fx-opacity-blend`, `04-fx-shadows-glows`, `04-fx-feathers-other` | Opacity only | Opacity only | **Done**: transparency groups (opacity, blend modes, isolate, knockout; object, fill, stroke, content, group), drop and inner shadows, glows, basic, directional and gradient feathers as soft masks. Gaps: text in effect shapes, noise, bevel and emboss, satin (reported) | n/a | None | Tier 1 **S–M**, tier 2 **L** |
| 6. Drop caps | Attributes known (defaults) | None | None | Through glyph ops (nothing new) | **None.** Per-line indents, sizing | Not planned | **M** |

**What every feature touches (checklist):**

- **DOM:** `src/CoreIdml/Dom`.
- **Spec, in five places:**
  - `SpecModel.cs` (wip);
  - `IdmlJson.cs` (wip) `Render` (117-180), `RenderItem` (182-315) and `ToParagraphFormat` (382-401);
  - the field lists in `SpecChecker.cs:7-21` (wip);
  - the schema text in `SpecSchema.cs:19-83` (wip);
  - `document-spec.md`.
- **MCP:**
  - `LayoutTools.cs` (wip): `LookProperties` (276-284), `ApplyLook` (287) and `idml_define_style`'s hand-written
    schema (192-199);
  - the summary (`Json/SummaryModel.cs`, `Json/IdmlJsonRead.cs`).
- **Bindings:**
  - The C ABI, Native, Interop, Python and Dart pass spec JSON through, so they need no change.
  - The TypeScript spec types do need updating: `bindings/node/src/types.ts` (`ItemSpec` at 84, `DocumentSpec` at
    147) and `bindings/wasm/src/types.ts`.
- **Docs:**
  - `README.md:145-146` (wip);
  - `src/CoreIdml.Pdf/README.md:35,59-61`;
  - `plugins/coreidml/skills/idml-documents/SKILL.md:71-79` (wip);
  - `document-spec.md`, "Not supported" (167-171).
- **Tests:**
  - `DocumentSpecTests` (`CheckSpec`);
  - `CliAndMcpTests`, which checks that the schema and model field names match;
  - `CreateDocumentTests`, `LayoutTests`, `ExportTests`;
  - `VisualTests` and `TextPositionDiff` against the phase-0 PDFs.

**Round trip.** Unchanged parts are written back byte for byte (`Packaging/IdmlPackage.cs:11`). An edited part keeps
every element and attribute through `XDocument`. So all six features already survive editing; what is missing is
authoring them and drawing them.

## Dependencies and recommended order

```
            ┌──────────────┐
            │ 1 Masters    │──(Page.MasterToPage)──────────────┐
            └──────────────┘                                   ▼
 ┌────────────┐   EmitLine extents   ┌─────────────────┐   ┌────────────┐
 │ 2 Rules    │─────────────────────►│ slot refactor   │──►│ 3 Wrap     │
 └────────────┘                      │ (+ 6 Drop caps) │   └────────────┘
                                     └─────────────────┘         ▲
 ┌──────────────────┐  PathFlattener ─────────────────────────────┘
 │ 4 Paths / SVG    │──────────────────────────────┐
 └──────────────────┘                              ▼
 ┌──────────────┐   ┌──────────────┐   ┌─────────────────┐   ┌──────────────────┐
 │ 6a PDF infra │──►│ 6b Gradients │──►│ 6c tier 1 (fade)│──►│ 6c tier 2 raster │
 └──────────────┘   └──────────────┘   └─────────────────┘   └──────────────────┘
```

**Recommended order:**

1. **Masters.** The most value for people designing through MCP: a header rule and a folio written once. The
   renderer is already done, so this is authoring, spec, tools and summary work with low risk. Wrap on master items
   needs `Page.MasterToPage`.
2. **Paragraph rules.** Small and common in designs. The composer change is contained (ops plus one placement rule),
   and it adds the `EmitLine` extents the slot refactor reuses.
3. **Polygons and paths.** No composer impact. SVG `d` paths are something agents produce well. It provides the
   flattener for wrap and effects. PDF import of paths fits here.
4. **Drop caps.** Introduces the line-slot refactor on the simplest case: a fixed blocked interval on the first N
   lines.
5. **Text wrap.** The biggest fidelity gap for existing InDesign documents (text drawn over images) and the
   riskiest change. It builds on slots, the flattener and masters.
6. **PDF infrastructure, gradients and tier-1 effects.** Independent of the composer.
7. **Tier-2 effects** (shadows, glows, feather). Last: the highest cost and the lowest fidelity.

**Parallel tracks.** Two engineers can run them side by side:

- **Text track:** 2 → 4 → 5.
- **Graphics track:** 3 → 6 → 7.

Masters go first, on either track.

## Milestones (each a minor release; the C ABI is unchanged because the spec is JSON)

| Release | Content | Effort |
| --- | --- | --- |
| **1.1 Masters and rules** | Phase 0. Masters: DOM, spec (`masters`, `pages[].master`, `marker`), `idml_set_master`, `master` on `idml_add_page`, master items findable and editable, summary `masters`, validation, `ShowMasterItems`, text variables' `ResultText`. Paragraph rules: DOM, spec, `idml_define_style`, composer and renderer. Docs and TS types. | ~2.5 weeks |
| **1.2 Shapes** | `PathData` and the SVG parser, `polygon`/`path` spec types, `viewBox`, arrowheads, the open-path fill fix, polygon resizing, image `shape`, PDF import of paths. | ~2 weeks |
| **1.3 Text flow** | Slot refactor, drop caps, text wrap (box, object shape, jump, next column, inverse, masters, document preferences), non-rectangular text frames (stretch), `wrap`/`ignoreWrap` in spec and tools. | ~4 weeks |
| **1.4 Colour** | PDF infrastructure, gradient swatches, fills and strokes, `idml_add_gradient`, blend modes, fill/stroke/content opacity, group transparency, gradient feather (`fade`). Stretch: gradient text. | ~2.5–3 weeks |
| **1.5 Effects** | Drop shadow, outer glow, inner shadow and glow, basic feather, `EffectResolution`. | ~2.5 weeks |
| optional | PDF import detects masters; `ImportPages` copies masters; `Page.OverrideMasterItem`. | M each |

Each release updates the spec reference's "Not supported" section, the skill (`SKILL.md:71-79`) and the
"approximates InDesign" paragraphs in both READMEs.

## Out of scope

**Masters**

- Primary text frames and smart text reflow.
- Master page resizing (authoring `MasterPageTransform`).
- Authoring overrides before phase 0 confirms the format.
- Alternate layouts (see [responsive-layout-plan.md](responsive-layout-plan.md)).

**Rules and paragraphs**

- ~~Paragraph borders and shading (`ParagraphBorder*`, `ParagraphShading*`)~~: drawn with [paragraph rules](paragraph-rules.md) (authoring is not).
- Multi-line stroke styles drawn exactly.
- Overprint.

**Wrap**

- Anchored objects that wrap the lines before their anchor, or in their object shape (they wrap the lines after it, around their box).- Contours from Photoshop paths or edge detection (such an item does not wrap; alpha channels are read).
- Tables that wrap.
- The paragraph composer.

**Paths**

- Text on a path.
- Pathfinder operations.
- Corner effects on polygons.
- Exact arrowhead outlines beyond phase-0 measurements.

**Gradients and effects**

- Gradient mesh.
- Radial highlights (`Hilite*`).
- Bevel and emboss, satin (reported as not drawn), noise.
- Text in the shapes of shadows, glows and feathers (no glyph outlines: the text is left out, with a warning).
- Effects on text runs.
- Importing gradients or effects from PDF.

**Drop caps**

- Nested and GREP styles.
- "Align left edge" and "scale for descenders" (until glyph bounds are read).
- Detecting drop caps on import.

## Open questions for the maintainer

1. **Phase-0 fixtures.** Can someone with InDesign produce the per-feature IDML and PDF documents ([What the fixtures show, and phase 0](#what-the-fixtures-show-and-phase-0)), and may the
   IDMLs be committed to `tests/fixtures`? Most "(memory)" items, and every visual test, depend on them.
2. **Spec shapes for masters:**
   - Is `items` + `left`/`right` the right structure?
   - Should a spec master named "A-Master" (or prefix "A") fill the default master instead of adding a second?
   - Should the page number be a run `{"marker": "pageNumber"}`, or a text token such as `{page}`? The token is
     easier for agents but collides with literal text.
3. **Text-wrap fidelity target.** Is "never overlaps, close to InDesign" acceptable, or must line breaks match
   InDesign's? And with wraps in a column, should vertical justification be skipped (proposed) or attempted?
4. **Raster effects.** Are rasterized shadows and glows acceptable in a vector PDF pipeline, at a default of
   150 dpi? Are unblurred text shadows acceptable? Or should tier 2 be dropped in favour of tier 1 only?
5. **Effect naming in the spec.** CSS-like (`shadow { x, y, blur, spread }`, `fade`) or InDesign's (`dropShadow
   { xOffset, yOffset, size }`, `gradientFeather`)? Agents know CSS; designers know InDesign.
6. **Master items in summaries and tools.** Should the `masters` summary section and master-item lookup be on by
   default (bigger summaries), or opt-in?
7. **PDF import defaults.** Should vector paths be imported by default (with a cap of 2000 per page)? Should master
   detection be opt-in?
8. **Composition.** Should `idml_merge` and `ImportPages` copy the source masters (changing today's documented
   behaviour, `Composition.cs:13-14`)?
