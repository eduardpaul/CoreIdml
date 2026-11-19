# Polygons and paths

Status: **plan, not implemented.** Written against commit `691f87a`; the files it then marked **(wip)** were committed in `dc5bbf4` (1.1.0), so their line numbers may have moved. Overview, order, milestones and open questions: [README.md](README.md).


## IDML

- **`Polygon`**: a `GraphicFrame` that can hold an image. Its `PathGeometry` has one or more `GeometryPathType`s,
  each with its own `PathOpen` (FICHA has compound open polygons).
- **`GraphicLine`**: an open path. Whether pen-tool open paths with more than 2 points are `GraphicLine` or `Polygon`
  must be checked in phase 0 (memory: `GraphicLine`).
- **`Rectangle` and `Oval`** can hold any points after a direct-selection edit.
- **Arrowheads (memory):** `LeftLineEnd`/`RightLineEnd` (`None`, `SimpleArrowHead`, `SimpleWideArrowHead`,
  `TriangleArrowHead`, `TriangleWideArrowHead`, `BarbedArrowHead`, `CurvedArrowHead`, `CircleArrowHead`,
  `CircleSolidArrowHead`, `SquareArrowHead`, `SquareSolidArrowHead`, `BarArrowHead`, ...). CC 2015 added scale and
  alignment attributes.
- **The polygon tool's** side count and star inset are preferences only. The geometry is stored as points.

## Current state

**Reading**

- `PathGeometry.Contours` (`Dom/PathGeometry.cs:55-84`), `PageItem.Contours` and `InnerBounds`.
- `TryWrap` maps `Polygon` to `GraphicFrame` (wip `PageItems.cs:381-389`).

**Creating**

- Only `PathGeometry.Rectangle`, `Ellipse` and `Line` (12-34), through `Page.AddRectangle`, `AddEllipse` and `AddLine`
  (`Dom/Page.cs:174-201`). `Build` (88-96) is private.
- `SetBounds` rejects polygons and lines (wip `PageItems.cs:311-332`).

**Rendering** (`AppendPath`, `IdmlRenderer.cs:472-502`)

- Draws Béziers and compound paths, even-odd when there is more than one contour (380). Images are clipped to any
  closed frame shape (659-663).
- **Gap: open contours are never filled.** Fill requires every contour to be closed (374, 377). InDesign fills open
  paths as if closed (memory, very likely).
- **Gap:** arrowheads are not drawn.

**Spec and import**

- The spec has `rectangle`, `ellipse` and `line` only (wip `IdmlJson.cs:238-250`).
- PDF import keeps rectangles and 2-point lines only (`TryRectangle`/`TryLine`, `PdfImporter.cs:586-704`). Other
  vector paths are dropped.

## Design

**DOM** (a new `Dom/PathData.cs`, public):

```csharp
public sealed class PathData                                  // page coordinates, points
{
    public PathData MoveTo(double x, double y);
    public PathData LineTo(double x, double y);
    public PathData CurveTo(double c1x, double c1y, double c2x, double c2y, double x, double y);
    public PathData QuadTo(double cx, double cy, double x, double y);       // stored as cubic
    public PathData Close();
    public static PathData FromSvg(string d);                               // M L H V C S Q T A Z, absolute and relative; arcs → cubics
    public static PathData Polygon(IEnumerable<Point> points, bool closed = true);
    public static PathData RegularPolygon(Rect bounds, int sides, double starInsetPercent = 0);
    public PathData Transform(Matrix matrix);
    public Rect Bounds { get; }
}
Page.AddPath(PathData path, ShapeStyle? style = null, string? name = null) → PageItem  // GraphicLine for one open contour, else Polygon
Page.AddPolygon(IEnumerable<Point> points, ShapeStyle? style = null, string? name = null) → GraphicFrame
PageItem.Path { get; set; }                                   // PathData in page coordinates; setter rewrites PathGeometry
ShapeStyle.StartArrow / EndArrow (ArrowHead enum)
```

- **Geometry storage:** anchors are stored relative to the path's bounding-box centre, with an `ItemTransform`
  translation, as `AddLine` does (`Page.cs:193-201`). `PathGeometry.Build` gains a multi-contour overload.
- **`SetBounds`:** extended to every kind by scaling anchors and handles about the bounds (no rebuild), so
  `idml_edit_item` can resize polygons.
- **`PathFlattener`** (internal, Béziers to polylines with a tolerance) is shared with text wrap ([text wrap](text-wrap.md)) and the effect
  rasterizer ([gradients and effects, 6c](gradients-and-effects.md#6c-effects-which-are-worth-doing)).

**Spec.**

- **`"type": "polygon"`**: either `"points": [[x, y], ...]` (in `unit`, page coordinates, `"closed": true` by
  default), or `"sides": 6, "starInset": 30` within `x`/`y`/`width`/`height`.
- **`"type": "path"`**: `"d": "M10 10 C ... Z"` (SVG path syntax, which agents write fluently). Optionally
  `"viewBox": [minX, minY, w, h]` maps the path into `x`/`y`/`width`/`height` (icons). Without `viewBox`, `d` is in
  page coordinates in `unit`.
- **`startArrow`/`endArrow`** on `line` and `path`: `none`, `simple`, `simpleWide`, `triangle`, `triangleWide`,
  `barbed`, `curved`, `circle`, `circleSolid`, `square`, `squareSolid`, `bar`.
- **Optional, S:** `"shape": "ellipse" | "polygon"` (with `points`/`d`) on `image` items, for round portraits. The
  renderer already clips to the frame path.
- **Checker and schema:** `PolygonItemFields` and `PathItemFields`, and the `type` enum adds `polygon` and `path`.
  This touches `CheckItem` (`SpecChecker.cs:84`, wip).

**MCP.**

- `idml_edit_item`: polygons resize; a new `d` argument replaces a path's geometry; `startArrow`/`endArrow` join
  `LookProperties`.
- The tool description drops "other items only move".

**Renderer.**

- **Fill open contours** by closing them implicitly for the fill only (`IdmlRenderer.cs:373-381`). The stroke stays
  open. Clipping images stays closed-only.
- **Arrowheads**, drawn in `DrawStroke`:
  - Take the end tangent from the last anchor and its handle.
  - Shorten the stroked path by the arrow's setback.
  - Fill the arrow shape in the stroke colour, scaled by the stroke weight (and the scale attributes when present).
- Shapes approximate InDesign's (phase 0 gives the outlines).

**PDF import** (`PdfImporter.ImportShapes`, 586-636):

- Subpaths that are neither rectangles nor lines become polygons or graphic lines, Béziers kept.
- One PDF path object becomes one compound polygon.
- Fill-only and stroke-only copies are rejoined by geometry key, as rectangles already are (589-623).
- New `PdfImportOptions.ImportPaths` (default true) and `MaxPathsPerPage` (default 2000). Over the cap, warn and
  suggest placing the page as PDF (`GraphicFrame.PlacePdf`).

## Tests

- **Core:**
  - The SVG parser: every command, relative coordinates, smooth curves, arcs (cubic error under 0.01 pt), bad input.
  - `AddPath` XML: centred anchors, `PathOpen` per contour, `GraphicLine` versus `Polygon`.
  - Round trip; `SetBounds` scaling; spec and checker.
- **Render:** the content stream holds `c`, `h`, `f` for a filled open path; arrow paths.
- **Import:** export an IDML with polygons and curves through our own exporter, import the PDF, and compare the
  points within 0.01 pt.
- **Visual:** the phase-0 paths sample.

**Effort: M** (about 7 to 8 days): DOM and SVG 3, spec/MCP/docs 1.5, renderer fill fix and arrowheads 2, import 2.

**Risks**

- `GraphicLine` versus `Polygon` for open paths (memory).
- The compound fill rule: even-odd today. InDesign follows path direction for holes (memory).
- Arrowhead shapes.
- Vector-heavy PDFs producing thousands of items (hence the cap).
