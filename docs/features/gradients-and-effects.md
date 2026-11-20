# Gradients and effects

Status: **gradients (6a's shading and pattern part, 6b's rendering) done**, checked against `03-color-gradients`;
the facts marked "(memory)" below were checked there (start and length are in inner coordinates, radial is centred on
the start with radius = length; InDesign's midpoint encoding is in `Writing/GradientShading.cs`). Gradient authoring
(spec `gradients`, `gradientAngle`) is done. **Effects (6c tiers 1 and 2) are done**, checked against the
`04-fx-*` samples: what is drawn and what is not is in the feature matrix's "Transparency and effects" rows. Where
this plan's "(memory)" facts were wrong: the blur is a Gaussian with σ = size / 2, not a box blur of radius `Size`.
Not done: the radial highlight; bevel and emboss and satin (reported as not drawn); noise; the `EffectResolution`
option (effects are always 150 ppi); effect authoring (only `opacity`). The rest is the plan, written against commit `691f87a`; the files it then marked **(wip)** were committed in `dc5bbf4` (1.1.0), so their line numbers may have moved. Overview, order, milestones and open questions: [README.md](README.md).


## IDML

**Gradient swatch** (`Resources/Graphic.xml`, seen in `magazineA-edito`):

```xml
<Gradient Self="Gradient/u80" Type="Linear" Name="$ID/" ...>
  <GradientStop Self="u80GradientStop0" StopColor="Color/u81" Location="0"/>
  <GradientStop Self="u80GradientStop1" StopColor="Color/Black" Location="100" Midpoint="50"/>
</Gradient>
```

**On items.** `FillColor="Gradient/..."`, plus `GradientFillStart` ("x y"), `GradientFillLength`,
`GradientFillAngle`, `GradientFillHiliteLength` and `GradientFillHiliteAngle` (the radial highlight). The same set
exists for `Stroke`. These attributes are on every FICHA item.

- **Semantics (memory):** start and length are in the item's inner coordinates; a length of 0 or -1 means "across
  the bounding box at the given angle"; a radial gradient is centred at its start with radius = length. Check all
  of this in phase 0.
- **Text** can take a gradient fill too: `GradientFill*` appears in `TextDefault`.

**Effects** sit inside the item, in `<TransparencySetting>` (attribute names from FICHA `Preferences.xml:31-79`):

- `BlendingSetting` (`BlendMode`, `Opacity`, `KnockoutGroup`, `IsolateBlending`);
- `DropShadowSetting` (`Mode` Drop/None, `BlendMode`, `Opacity`, `XOffset`, `YOffset`, `Size`, `EffectColor`,
  `Noise`, `Spread`, `UseGlobalLight`, `KnockedOut`, `HonorOtherEffects`);
- `FeatherSetting` (`Mode` Standard/None, `Width`, `CornerType`, `Noise`, `ChokeAmount`);
- `InnerShadowSetting`, `OuterGlowSetting`, `InnerGlowSetting`, `BevelAndEmbossSetting`, `SatinSetting`,
  `DirectionalFeatherSetting` and `GradientFeatherSetting` (`Type`, `Angle`, `Length`, `GradientStart`, highlight;
  its stops' storage is memory).

**Part effects and globals:**

- The same children appear under `StrokeTransparencySetting`, `FillTransparencySetting` and
  `ContentTransparencySetting`.
- Object styles carry them too.
- Global light is `TransparencyPreference@GlobalLightAngle/Altitude` (memory).

## Current state

**Gradients**

- `SwatchCatalog` lists gradients (`Swatches.cs:21`). `ResolveColor` returns null for them (105-123).
- The renderer warns "Gradient ... is not rendered" and draws nothing (`IdmlRenderer.cs:452-470`).

**Opacity**

- wip `ShapeStyle.Opacity` writes `TransparencySetting/BlendingSetting@Opacity` (wip `PageItems.cs:267-304`).
- `ApplyOpacity` (`IdmlRenderer.cs:442-450`) reads only the item's own element, not its object style. It sets
  `ca`/`CA` through `PdfDocumentBuilder.Opacity` (86-98).
- A group's opacity is applied to each child (360). Overlapping children show through each other, which InDesign's
  transparency group prevents.

**PDF writer**

- One shared resource dictionary (`Finish`, 370-418) with `/Font`, `/XObject` and `/ExtGState`.
- No `/Shading`, no `/Pattern`, no soft-mask graphics states, no Form XObjects of our own (only imported PDF pages).
- `ContentBuilder` (`Writing/ContentBuilder.cs`) does not track the current transformation matrix (CTM).
- The only soft mask is PNG alpha (`WritePng`, 338-347).

**Elsewhere**

- **Preview:** `idml_render_page` rasterizes with PDFium (`Cli/Rendering/PageRasterizer.cs`), which handles shadings
  and soft masks. Previews come for free.
- **Import** reads only flat path colours (`PdfImporter.cs:597`).

## 6a. PDF writer infrastructure (prerequisite, M, about 5 days)

1. **`ContentBuilder` tracks the CTM** through `q`/`Q`/`cm` and exposes `CurrentMatrix`. Pattern matrices are given
   in the page's default space, not the current one.
2. **`PdfDocumentBuilder.Shading(GradientSpec, bool rgb) → name`.**
   - Type 2 (axial) or Type 3 (radial) shading.
   - Colours: a Type 3 stitching function of Type 2 segments.
   - A stop's midpoint m adds an exponent `N = ln 0.5 / ln m'` (m' = m rescaled between the two stops), so the
     colour is halfway at the midpoint. This approximates InDesign.
   - Colour space: CMYK when every stop is CMYK, else RGB (through `ResolvedColor.ToRgb`, `Dom/Resolution.cs:228`).
3. **`ShadingPattern(shading, Matrix) → name`** (PatternType 2), for strokes and text.
4. **`GraphicsState(ExtGState state) → name`** replaces `Opacity()`. The record holds fill and stroke alpha, `/BM`,
   and an optional `/SMask` (a luminosity group).
5. **`TransparencyGroup(ContentBuilder content, Rect bbox, bool isolated, bool knockout) → form name`**: a Form
   XObject with `/Group <</S /Transparency>>` that uses the shared resources object.
6. **`MaskedColor(ResolvedColor color, byte[] alpha, int w, int h) → image name`**: a 1×1 image whose `/SMask` is the
   alpha raster. PDF lets the soft-mask image differ in size from its parent; confirm in PDFium and Acrobat.
   Identical masks are deduplicated by hash.
7. **`Finish`** writes `/Shading` and `/Pattern` resources.

Tests parse the output with PdfPig (already referenced by `CoreIdml.Pdf`): object types, function bounds, colour
spaces. PDFium must render without errors.

## 6b. Gradients (M, about 5 days after 6a)

**DOM**

```csharp
public enum GradientType { Linear, Radial }
public sealed record GradientStop(Swatch Color, double Location, double Midpoint = 50);
SwatchCatalog.AddGradient(string name, GradientType type, IReadOnlyList<GradientStop> stops) → Swatch   // "Gradient/<name>"
SwatchCatalog.ResolveGradient(string reference) → ResolvedGradient?   // type + resolved stop colours (tints followed)
ShapeStyle.FillGradientAngle / StrokeGradientAngle (double?)          // GradientFillAngle; start/length left at default
```

**Spec**

- A top-level `"gradients": [{ "name": "Sunset", "type": "linear", "stops": [{ "color": "Brand", "at": 0 }, {
  "color": "Paper", "at": 100, "midpoint": 50 }] }]`, applied after `colors`.
- `fill` and `stroke` accept gradient names; `gradientAngle` and `strokeGradientAngle` are degrees.
- A gradient used as a text `color` is an error until text gradients are drawn.

**MCP**

- `idml_add_gradient` (mirrors `idml_add_color`, through the spec).
- `LookProperties` gets `gradientAngle`.
- The summary's `swatches` already lists gradients.

**Renderer**

- `ResolveColor` becomes `ResolvePaint` → `SolidPaint | GradientPaint`.
- **Fill:** `q <path> W n <gradient→inner matrix> cm /ShN sh Q`.
  - By default, linear runs along the angle across the projection of `InnerBounds` onto that direction, and radial
    is centred on the bounding box with radius = half the diagonal (memory; phase 0 decides).
  - `GradientFillStart`/`Length` override the default when the length is positive.
- **Stroke:** a shading pattern whose matrix is gradient space → inner coordinates → `CurrentMatrix`, then `/Pattern
  CS /Pn SCN`.
- Tints and highlight attributes are ignored, with a warning.
- **Stretch (S):** gradient text, by filling glyph runs with the same pattern.

**Tests**

- The matrix maths at 0, 90 and 45 degrees and for radial.
- The shading's stop colours and midpoints.
- A stroke pattern under a rotated item.
- The phase-0 gradient sample.

## 6c. Effects: which are worth doing

**Tier 1: vector and exact, S–M (3 to 4 days after 6a and 6b).** Do these with gradients.

- **Blend modes:** `BlendingSetting@BlendMode` maps to `/BM`. InDesign and PDF use the same 16 names.
- **Object-style resolution** of `TransparencySetting`. Today `ApplyOpacity` reads only the item.
- **Group opacity** as one transparency group. This fixes overlapping children.
- **Separate fill, stroke and content opacity** (`Fill`/`Stroke`/`ContentTransparencySetting` → `BlendingSetting`):
  one graphics state per part in `DrawItem`.
- **Gradient feather:** a soft mask that is a luminosity group holding a white-to-black shading along the angle and
  length. Exact and vector, and the effect designers use most after drop shadows (fading an image into the page).

**Tier 2: rasterized soft effects, L (2 to 3 weeks).** Worth it for the drop shadow, the most used effect, and then
outer glow, which uses the same algorithm.

- **Inner shadow and inner glow:** an inverted mask clipped to the shape.
- **Basic feather:** an eroded and blurred alpha, used as the item group's soft mask.
- **Implementation** (new `src/CoreIdml.Pdf/Rendering/EffectRaster.cs`):
  - an anti-aliased scanline rasterizer for flattened paths (non-zero and even-odd);
  - strokes rasterized by distance to segments;
  - `Spread` (percent) as a dilation, `ChokeAmount` as an erosion;
  - a three-pass box blur approximating a Gaussian whose radius is `Size` (memory);
  - `Noise` ignored.
- **Output:**
  - `MaskedColor` at `PdfExportOptions.EffectResolution`, default 150 dpi. This new option goes into `PdfJson.cs`
    and the TypeScript `PdfExportOptions`.
  - Drawn under the item through a graphics state with the effect's opacity and blend mode (Multiply by default).
  - `KnockedOut` clips the shadow out of the item's area when the item is translucent.
- **Shape sources:**
  - frame fill and stroke;
  - image frames: the frame shape (PNG alpha later);
  - text frames: the fill and stroke shape.
  - `FontFace` exposes no glyph outlines (`Fonts/FontFace.cs`), so shadows of frames that contain only text are
    drawn as an **unblurred** offset copy of the glyph runs, with a warning.
- **Not doing:** bevel and emboss, satin, directional feather, noise. They are rare in documents built through the
  tools, and expensive to imitate.

**DOM and spec for effects.** Names follow CSS where agents expect them, and InDesign units otherwise:

```csharp
public enum BlendMode { Normal, Multiply, Screen, Overlay, SoftLight, HardLight, ColorDodge, ColorBurn, Darken, Lighten,
                        Difference, Exclusion, Hue, Saturation, Color, Luminosity }
public sealed record Shadow(Swatch? Color = null, double Opacity = 75, double OffsetX = 7, double OffsetY = 7,
                            double Blur = 5, double Spread = 0, BlendMode Mode = BlendMode.Multiply) { public static readonly Shadow None; }
public sealed record Glow(Swatch? Color = null, double Opacity = 75, double Size = 7, double Spread = 0, BlendMode Mode = BlendMode.Screen);
public sealed record Fade(double Angle, double? Length = null);          // GradientFeatherSetting, linear
// ShapeStyle: BlendMode, Shadow, InnerShadow, OuterGlow, InnerGlow, Feather (double? width), Fade
```

- **XML:** written into `TransparencySetting` in InDesign's child order (as in `Preferences.xml`), only the children
  that were set. Whether InDesign accepts a partial `TransparencySetting` must be checked in phase 0 (memory: yes).
- **Spec item fields:**
  - `blendMode`;
  - `shadow: { color, opacity, x, y, blur, spread, blendMode }`: lengths in `unit`, `spread` in percent as in
    InDesign;
  - `innerShadow`, `glow`, `innerGlow`, `feather: { width }`, `fade: { angle, length }`.
- **Checker and schema:** nested field lists.
- **MCP:** the same fields in `LookProperties`.

**Effects not planned for import.** InDesign PDFs with drop shadows already import the shadow as an extra image. A
later improvement could skip images that are a flat colour with a soft mask.

**Effort:** 6a M + 6b M + tier 1 S–M (about 3 weeks) for gradients and vector effects. Tier 2 is L (2 to 3 weeks).

**Risks**

- The gradient coordinate semantics (memory).
- Blur fidelity compared with InDesign's.
- PDF size and export time with many shadows on WASM and mobile. Mitigated by deduplication and the resolution
  option.
- The blending colour space: InDesign's document blend space against an RGB or CMYK page group.
- Overprint is ignored.
