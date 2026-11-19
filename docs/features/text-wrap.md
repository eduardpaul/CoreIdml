# Text wrap

Status: **plan, not implemented.** Written against commit `691f87a`; the files it then marked **(wip)** were committed in `dc5bbf4` (1.1.0), so their line numbers may have moved. Overview, order, milestones and open questions: [README.md](README.md).


## IDML

**On every page item** (in spreads, masters and anchored items in stories), and in object styles and
`Preferences.xml`:

```xml
<TextWrapPreference Inverse="false" ApplyToMasterPageOnly="false" TextWrapSide="BothSides" TextWrapMode="None">
  <Properties><TextWrapOffset Top="0" Left="0" Bottom="0" Right="0" /></Properties>
  <ContourOption ContourType="SameAsClipping" IncludeInsideEdges="false" ContourPathName="$ID/" />
</TextWrapPreference>
```

**Values (memory):**

- `TextWrapMode`: `None`, `BoundingBoxTextWrap`, `Contour` (object shape), `JumpObjectTextWrap`,
  `NextColumnTextWrap`.
- `TextWrapSide`: `BothSides`, `LeftSide`, `RightSide`, `SideTowardsSpine`, `SideAwayFromSpine`, `LargestArea`.
- `ContourType`: `BoundingBox`, `PhotoshopPath`, `DetectEdges`, `AlphaChannel`, `GraphicFrame`, `SameAsClipping`,
  `UserModifiedPath`.
- Object-shape mode uses a single offset, `Top` (memory).

**Text frames:** `TextFramePreference@IgnoreWrap` (seen in fixtures). Layers: `Layer@IgnoreWrap` ("suppress text
wrap when layer is hidden", memory).

**Document:** `TextPreference@ZOrderTextWrap` ("Text wrap only affects text beneath"), `@AbutTextToTextWrap`
(default true; false = "skip by leading"), `@JustifyTextWraps` and `@HonourTextIndentsWithTextWrap` (all in FICHA
`Preferences.xml`).

**Masters:** wraps on master items push text on document pages unless `ApplyToMasterPageOnly` is set (memory).

## Current state

Nothing reads any of this.

**`FrameArea` is a list of rectangles** (`Layout/LayoutModel.cs:146`) built from `InnerBounds`
(`IdmlRenderer.cs:225-256`). An oval or polygon text frame is therefore laid out as a rectangle.

**The composer fits every line into one span:**

- `available = column.Width - indent - RightIndent` (`TextComposer.cs:356-358`).
- `FitLine` (374) breaks the line.
- Only afterwards does `Flow.Place` (978-1018) choose the baseline from the line's metrics.
- So a line's width is known before its vertical position. Wrap needs the reverse: the band the line occupies
  decides its width.

## Design

**DOM** (`Dom/PageItems.cs`; the preference element is resolved like `FramePreferenceSource`,
`Dom/Resolution.cs:96-106`):

```csharp
public enum TextWrapMode { None, BoundingBox, ObjectShape, JumpObject, NextColumn }
public enum TextWrapSide { Both, Left, Right, TowardsSpine, AwayFromSpine, LargestArea }
public sealed record TextWrap(TextWrapMode Mode, Spacing Offset = default, TextWrapSide Side = TextWrapSide.Both, bool Invert = false);

PageItem.TextWrap { get; set; }   // get: item, then object style; set: writes/updates the item's TextWrapPreference,
                                  // keeping ContourOption (ObjectShape → ContourType "SameAsClipping" (memory))
TextFrame.IgnoreWrap { get; set; }
```

**Spec.**

- **Item field** (any type): `"wrap": { "mode": "box" | "shape" | "jump" | "nextColumn" | "none", "offset": { "all":
  6 }, "side": "both" | "left" | "right" | "largest" | "towardsSpine" | "awayFromSpine", "invert": false }`.
  - `offset` is in `unit` and reuses the spacing object.
  - A string shorthand (`"wrap": "box"`) would need a custom converter in the source-generated context. Skip it.
- **Text items:** `"ignoreWrap": true`.
- **Checker:** `WrapFields`.
- **MCP:** `LookProperties` gets `wrap` and `ignoreWrap`.
- **Summary:** `ItemSummary.wrap` (the mode) when it is not `None`, so an agent can see why text moved.

**Wrap geometry** (new file `src/CoreIdml.Pdf/Layout/WrapRegions.cs`):

- **Inputs:** each wrapping item's contour, flattened (the flattener comes with feature 4), mapped into the text
  frame's inner coordinates through `Invert(frame.SpreadTransform)`.
- **`Blocked(y0, y1) → intervals`**, the x ranges blocked for a horizontal band:
  - **Bounding box:** the item's box plus the offsets.
  - **Object shape:** sample the band, extended by the offset, every 1 pt and at both edges. Take the minimum to
    maximum x inside the shape (holes count as filled unless `IncludeInsideEdges`) and widen it by the offset. This
    approximates a polygon offset well enough without a Minkowski sum.
  - **Sides:**
    - Left: block `[minX, ∞)`.
    - Right: block `(-∞, maxX]`.
    - Largest: per line, keep the wider side.
    - Towards or away from the spine: left or right, depending on which side of the spread the frame's page is.
  - **Jump:** the whole column across the item's height plus offsets.
  - **Next column:** the column closes at the item's top minus the offset.
  - **Inverse:** the complement of the inside range.
- **Unsupported contour types** (Photoshop path, alpha, detect edges) fall back to the frame shape, with a warning.

**Which items wrap.** `IdmlRenderer.FrameArea(frame, pageName)` builds the list:

- **Included:**
  - visible items on the frame's spread (`IsHidden`, 342-349), except the frame itself and the items it contains;
  - for frames on document pages, the master items of the page, through `Page.MasterToPage` (feature 1);
  - only items above the frame when `ZOrderTextWrap` is true.
- **Excluded:**
  - master items with `ApplyToMasterPageOnly`;
  - every item, when the frame has `IgnoreWrap`.
- **Not supported:** anchored objects.
- **Bonus:** a non-rectangular text frame (an oval, a polygon) adds its own shape as an inverse region, so text
  follows the frame's shape.
- **Storage:** `FrameArea` gets `IReadOnlyList<WrapRegion> Wraps = []`. Export, `TextMeasurer.Measure` and `CheckFit`
  (`TextMeasurer.cs:44-73`) all build areas through `FrameArea`, so the reports stay consistent with the export.
- **Caching:** flattened contours are cached per item and frame.

**Composer: the line-slot refactor** (`TextComposer.cs`):

1. **Slots.** A *slot* is `(baseline, segments[])`, each segment `(X, Width)`. With no wraps and no drop cap the slot
   is today's single segment, so existing layouts stay byte-identical (a test asserts this, [Dependencies and recommended order](README.md#dependencies-and-recommended-order)).
2. **`Flow.Peek(metrics)`** returns the baseline `Place` would choose, without committing.
   **`Flow.PlaceBelow(y)`** moves the next line below an obstacle.
3. **The line loop** in `LayoutParagraph`:
   - Estimate the metrics of the next line from its first word (`Measure` on the glyphs up to the first break).
   - Peek the baseline. The band is `[baseline - ascent, baseline + descent]` (memory: InDesign may use leading;
     check in phase 0).
   - The segments are the column, minus the blocked intervals, minus paragraph indents. Indents apply to the column
     edges unless `HonourTextIndentsWithTextWrap`.
   - Drop segments narrower than the first glyph cluster.
4. **When no segment fits:**
   - With `AbutTextToTextWrap`, place the line's top at the bottom of the lowest blocking region.
   - Otherwise, advance by the leading.
   - Then retry. A next-column region calls `Flow.NextColumn()`.
5. **Fitting and committing:**
   - Fit the glyphs into segment 1 with `FitLine`, then continue in segment 2 on the same baseline, and so on.
   - Measure the real metrics. If the band moved enough to change the segments, refit once. Two passes at most, so
     the result is deterministic.
   - `Place`, then call `EmitLine` once per segment. `EmitLine` takes the segment's `X`/`Width` instead of
     `column`/`indent`.
6. **Justification.** Each segment is justified as a line. `JustifyTextWraps` controls whether the last segment next
   to a wrap is justified.
7. **Tables** ignore wraps: the anchoring line is moved, the rows do not wrap. This is a simplification.
8. **Vertical justification:** `FinishColumn` (1058-1089) shifts finished lines. In a column with wraps, skip the
   shift and warn once, since shifted lines would cross the wrap. Open question 3.

## Tests

- **Composer** (`LayoutTests`, on documents built in the test):
  - A box-wrapped rectangle in the middle of a column gives two `LineInfo`s per baseline next to it, and full lines
    above and below.
  - Each side and largest area.
  - Jump and next column.
  - An object-shape wrap on an ellipse: segment widths vary smoothly.
  - Inverse (text inside an ellipse).
  - `ZOrderTextWrap`, `IgnoreWrap`, a hidden layer.
  - A wrap on a master item.
  - A rotated frame.
  - `AbutTextToTextWrap` on and off.
  - Wraps must not change existing layouts: every existing `LayoutTests` and `VisualTests` result stays identical.
- **Measurer:** `CheckFit` reports overset that only exists because of a wrap.
- **Visual:** the phase-0 wrap sample, using `TextPositionDiff` for line starts and widths.

**Effort: L** (about 3 weeks):

- DOM, spec, MCP and summary: 3 days;
- wrap geometry: 3 days;
- composer refactor and wrap loop: 7 to 8 days;
- tests and visual tuning: 4 days.

The slot refactor (about 3 of those days) is shared with drop caps. Whichever lands first pays for it.

**Risks**

- **The largest risk in the plan: regressions in the composer.** Mitigations: a fast path when there are no wraps,
  and the identical-output tests.
- InDesign's exact band, minimum segment width and justification next to wraps (memory). Expect a "never overlaps,
  close to InDesign" result, not an exact match.
- Wraps on anchored objects are not supported.
- Contours from image alpha or Photoshop paths are approximated.
