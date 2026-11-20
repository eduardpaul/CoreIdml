# InDesign feature matrix: what an agent can edit, and what the preview draws

An inventory of InDesign features as IDML stores them, and for each one:

- **Agent**: whether an agent can create or change it through the MCP tools (`idml_create`/`idml_apply` spec,
  `idml_edit_item`, `idml_define_style`, `idml_edit_table`, ...). Features the .NET DOM can write but no tool exposes
  are marked "DOM only".
- **Preview**: whether `idml_render_page` and `idml_to_pdf` draw it. Both use the same renderer
  ([IdmlRenderer.cs](../../src/CoreIdml.Pdf/Rendering/IdmlRenderer.cs)) and composer
  ([TextComposer.cs](../../src/CoreIdml.Pdf/Layout/TextComposer.cs), [StoryReader.cs](../../src/CoreIdml.Pdf/Layout/StoryReader.cs)),
  so a "no" here means the agent cannot see the feature when it checks its work.

Every feature, supported or not, **survives a round trip**: unchanged parts are written back byte for byte, and
edited parts keep every element and attribute. "No" means the feature is not authored or not drawn, never that it is
lost.

Checked against commit `a7828e8` by reading the code. Rows marked † were inferred from what the code does not read
(the attribute appears nowhere in `src/`) rather than tested against an InDesign file.

**Legend:** ✅ yes · 🟡 partly (see notes) · ❌ no · — does not apply

Features with an implementation plan already (the six in [README.md](README.md), plus
[accessibility](accessibility-plan.md) and [responsive layouts and editions](responsive-layout-plan.md)) are marked
**[plan]**. Rows marked
**[new]** are gaps this inventory found that no plan covers yet; they are summarised in
[Gaps not in the plan](#gaps-not-in-the-plan).

## Document and pages

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Page size, orientation | `DocumentPreference@PageWidth/Height` | 🟡 | ✅ | Set when creating (`page.size`, `width`, `height`, `landscape`); no tool resizes an existing document. |
| Margins, margin columns | `MarginPreference` | ✅ | — | Guides only; nothing to draw. Per-page margins in the spec. |
| Facing pages, spreads | `DocumentPreference@FacingPages` | 🟡 | ✅ | `facingPages` at creation. Left/right pages pick the master's left/right page. |
| Bleed and slug | `DocumentBleed*`, `Slug*` | 🟡 | 🟡 | `bleed` is written. The PDF page is the trim size by default (MediaBox, BleedBox and TrimBox equal, items cut at the trim, as InDesign's `reference.pdf`); with `includeBleed` (`PdfExportOptions.IncludeBleed`, `idml_to_pdf`, `to-pdf --bleed`) it is the trim plus the document's bleed on each side (MediaBox and BleedBox), the trim the TrimBox, and items are drawn into the bleed; on facing pages the inside bleed is at the binding. `01-doc-bleed-slug` against `reference-bleed.pdf`: every box equal (630 × 810, TrimBox [9 9 621 801]) and every page within 0.003% of its pixels. Not done: the slug, printer's marks. |
| Add, delete, move pages | `Spread/Page` | ✅ | ✅ | `idml_add_page`, `idml_delete_page`, `idml_move_page`; threads, masters and sections follow. |
| Merge documents | — | ✅ | ✅ | `idml_merge` (styles and swatches merged; masters not copied). |
| Sections and page numbering | `Section` in designmap | 🟡 | 🟡 | Kept consistent when pages move; not authorable. Page-number markers print the page's `Name`, which already includes the section's style and prefix. |
| Master pages **[plan]** | `MasterSpreads/*.xml`, `Page@AppliedMaster`, `OverrideList` | ❌ | ✅ | Drawn: left/right, based-on masters, overridden items skipped, per-page folios. Not drawn: `ShowMasterItems=false` spreads. `idml_add_page` always applies the first master. |
| Layers | `Layer`, `ItemLayer` | ✅ | ✅ | Spec `layer` (created when missing), `idml_edit_item` layer, visibility, lock. Hidden and non-printing layers are skipped. |
| Alternate and liquid layouts **[plan]** | `Section@AlternateLayout`, `Page@LayoutRule`, `*LayoutConstraints` | ❌ | ❌ | Rules kept, never applied; no tool resizes a page. See [responsive-layout-plan.md](responsive-layout-plan.md). |
| Flex Layout (InDesign 2026+) **[plan]** | not documented yet | ❌ | ❌ † | If InDesign writes it as its own container element, its children are not page items for CoreIdml: not found, filled or drawn. See [responsive-layout-plan.md](responsive-layout-plan.md). |

## Page items and shapes

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Rectangle, ellipse | `Rectangle`, `Oval` | ✅ | ✅ | Spec `rectangle`, `ellipse`; move and resize with `idml_edit_item`. |
| Straight line | `GraphicLine` (2 points) | ✅ | ✅ | Spec `line` (`x2`, `y2`). Moves only; no resize. |
| Polygons, pen paths, Béziers **[plan]** | `Polygon`, `PathGeometry/GeometryPathType` | ❌ | 🟡 | Closed and compound paths drawn (even-odd with several contours). Open paths are stroked but not filled. |
| Arrowheads **[plan]** | `LeftLineEnd`, `RightLineEnd` | ❌ | ❌ | |
| Corner options | `*CornerOption`, `*CornerRadius` | 🟡 | 🟡 | Agent: rounded only (`cornerRadius`, all four corners). Preview: rounded and bevel; inset is drawn as bevel, fancy and inverse-rounded as rounded ([IdmlRenderer.cs:505](../../src/CoreIdml.Pdf/Rendering/IdmlRenderer.cs#L505)). Rectangles and text frames only. |
| Rotation | `ItemTransform` | ✅ | ✅ | `rotation` (spec), `rotateBy` (`idml_edit_item`). |
| Skew, flip, arbitrary transform | `ItemTransform` | ❌ | ✅ | The full matrix is applied. |
| Groups | `Group` | ❌ | 🟡 | No tool groups or ungroups. A group's opacity is applied to each child, so overlapping children show through each other. |
| Items pasted inside a frame | nested items | ❌ | ✅ | Clipped to the parent frame. |
| Inline anchored objects | item inside a story, `AnchoredPosition=InlinePosition` | ❌ | ✅ | Laid out as a glyph, with `AnchorYoffset` (`02-item-anchored` p1). |
| Above-line and custom anchored objects | `AnchoredObjectSetting@AnchoredPosition="AboveLine"`, `"Anchored"`; `AnchorPoint`, `HorizontalReferencePoint`, `HorizontalAlignment`, `VerticalReferencePoint`, `VerticalAlignment`, `AnchorXoffset`, `AnchorYoffset`, `AnchorSpaceAbove`, `PinPosition`, `SpineRelative` | ❌ | ✅ | Measured against `02-item-anchored` and `12-pdf-accessibility` p4, by the object's visible bounds (stroke included). **Above line**: the line holding the anchor moves down by Space Before + the object's height and keeps its leading below it (also at the top of a column, whatever the first-baseline offset); the object's bottom sits 0.8274 em of the line's size above that baseline (measured with Liberation Serif only, at 10 and 10.5 pt), aligned left, centred or right between the paragraph's indents (`TextAlign`: as the paragraph). **Custom**: the object's `AnchorPoint` goes on the reference point (the anchor marker, or the left/centre/right of the column, text frame, page margins or page; vertically the line's baseline, or the top/centre/bottom of those areas) moved by the offsets: Y down, X right, except from a left edge, where a positive X moves out to the left (p4: -90 from the column's left edge ends the box 90 pt inside it). `PinPosition` keeps a line-relative object within the column's top and bottom (p4). Custom objects take no room in their line and are drawn after the frame's text, as InDesign writes them; their text wrap goes round the lines placed after them (p4: 100%, around the box; an object-shape wrap is taken as the box); above-line and custom markers kern with the next letter as a space would (`12-pdf-accessibility` p4). Every box within 0.001 pt of the reference; p2, p3, p5 and `12-pdf-accessibility` p4 match pixel for pixel. From memory, unmeasured: Space After (`AnchorYoffset` of an above-line object), line references other than the baseline (`LineXheight`, `CapHeight`, `LineAscent`, `TopOfLeading`), relative to spine (left and right swap on a left page). Not done: a custom object in a table cell is placed from the cell's origin (a cell has no frame or page area). |
| Visible, locked | `Visible`, `Locked` | ✅ | ✅ | `idml_edit_item`. Hidden items are not drawn. |
| Non-printing | `Nonprinting` | ❌ | ✅ | Skipped, as in InDesign's export. |
| Duplicate, delete | — | ✅ | — | `idml_duplicate_item`, `idml_delete_item` (stories and threads handled). |
| Object styles | `ObjectStyle`, `AppliedObjectStyle` | ❌ | 🟡 | Fill, stroke, corners, text frame options (columns, inset, first baseline, vertical justification, frame baseline grid) and opacity resolve through the object style and its `BasedOn` chain; the item's own values win. A category the style leaves alone (`EnableFill`, `EnableStroke`, `EnableStrokeAndCornerOptions`, `EnableTextFrameGeneralOptions`, `EnableTextFrameBaselineOptions`, ..., and `EnableTransparency` of `ObjectStyleObjectEffectsCategorySettings`) keeps the item's own values; the most specific style that sets a flag decides for its chain. Measured against `02-item-object-styles`: p2 and p3 match, p1 78% of words within 1 pt (the rest is one line break of the paragraph composer). A paragraph style the object style applies (`AppliedParagraphStyle`) is not needed for rendering: InDesign writes it on the text (`09-style-text-styles` p5). Not done: drop shadows and other effects (not drawn at all), fill/stroke/content transparency, text wrap from the style (anchored object settings resolve through it, without its `EnableAnchoredObjectOptions` flag). `idml_summarize` lists object styles (`objectStyles`) and each item's non-default style (`objectStyle`); the spec cannot define or apply them. |
| Text wrap | `TextWrapPreference` (`TextWrapMode`, `TextWrapSide`, `TextWrapOffset`, `Inverse`, `ApplyToMasterPageOnly`, `ContourOption@ContourType`); `TextFramePreference@IgnoreWrap`; `TextPreference@ZOrderTextWrap` | ❌ | 🟡 | Measured against `02-item-text-wrap` (p1 to p7 and p9 at 97 to 100%) and `06-frame-basics` p6 (ignore wrap, 100%). A line is tested over the band from the baseline before it down to its own baseline (descenders do not count; a column's first line from the column top) and goes in each stretch the wraps leave, left to right, as separate lines of the paragraph composer, which plans with those widths; a stretch narrower than the line's first word stays empty, a baseline with none moves down a leading (the first line of a column 1 pt at a time); a new paragraph starts on a baseline of its own. **Bounding box**: the item's box with its stroke, grown by each offset, exact. **Object shape**: the path (with corner options) grown by the top offset and half the stroke, round at the corners; text fills every gap, also inside a star's notch; edges truncated to whole points from the column's left edge, as InDesign does. **Jump object**, **jump to next column**, **sides** (left, right, largest area, the left on a tie; towards/away from the spine from memory), **inverse** (text only in bands wholly inside the shape less its offset). Graphic frames take their image's wrap when they have none. **Alpha channel**: the pixels of a placed PNG whose alpha reaches 230 (p8: 99.5%; 220 to 230 fit every line, 25 is InDesign's default threshold from memory), each strip of rows from its first such pixel to its last. Items below the frame wrap unless "text wrap only affects text beneath"; hidden items do not; master items wrap document pages unless `ApplyToMasterPageOnly` (from memory, no sample). Not done: detect edges and Photoshop paths (the item does not wrap), inside edges of alpha channels, wraps of anchored objects on the lines before the anchor and in object shape, `AbutTextToTextWrap="false"`, `JustifyTextWraps`; tables ignore wraps; the spec and the tools cannot author a wrap. |

## Colour, fill and stroke

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Process colours (CMYK, RGB) | `Color@Space`, `ColorValue` | ✅ | ✅ | `idml_add_color`, spec `colors` (`cmyk`, `rgb`, `hex`). |
| Lab colours | `Color@Space="LAB"` | ❌ | ✅ | Converted to sRGB (approximate). |
| Spot colours | `Color@Model="Spot"` | ❌ | 🟡 | Drawn with their process values; no Separation colour space in the PDF. |
| Tint swatches, tints | `Tint`, `FillTint`, `StrokeTint` | ✅ | ✅ | `fillTint`, `strokeTint`, text `tint`. |
| Mixed inks **[new]** | `MixedInk`, `MixedInkGroup` | ❌ | ❌ | Resolve to nothing: the item is drawn without fill. |
| Gradients **[plan]** | `Gradient`, `GradientFill*`, `GradientStroke*` | DOM only | ✅ | Linear and radial fills (axial and radial shadings clipped to the path) and strokes (shading patterns), placed by start, length and angle in the item's inner coordinates; stops, midpoints, CMYK, RGB, Lab and spot stops ([GradientShading.cs](../../src/CoreIdml.Pdf/Writing/GradientShading.cs)), written as InDesign writes them. `03-color-gradients` matches its reference. `SwatchCatalog.AddGradient` creates one; the spec and the tools do not. Not drawn: the radial highlight (`GradientFillHilite*`). Spot stops are their process values (no DeviceN). |
| Stroke weight, colour | `StrokeWeight`, `StrokeColor` | ✅ | ✅ | |
| Stroke type: solid, dashed, dotted | `StrokeType` | ✅ | ✅ | Canned dashes scale with weight; custom `StrokeDashAndGap` read. |
| Other stroke styles **[new]** | `StrokeType` (Thick-Thin, Wavy, Striped, custom stroke styles) | ❌ | 🟡 | Drawn as a single solid line. |
| Stroke gap colour **[new]** | `GapColor`, `GapTint` | ❌ | ❌ | |
| Stroke alignment | `StrokeAlignment` | ❌ | ✅ | Inside and outside on closed paths. |
| End cap | `EndCap` | ✅ | ✅ | `endCap`. |
| Join, miter limit | `EndJoin`, `MiterLimit` | ❌ | ✅ | |
| Overprint | `OverprintFill`, `OverprintStroke` | ❌ | ❌ | |

## Transparency and effects

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Object opacity | `TransparencySetting/BlendingSetting@Opacity` | ✅ | ✅ | `opacity`. From the item, else its object style (unless the style leaves transparency alone). |
| Blend modes **[plan]** | `BlendingSetting@BlendMode` | ❌ | ❌ | Drawn as Normal. |
| Fill, stroke, content opacity **[plan]** | `FillTransparencySetting`, ... | ❌ | ❌ | |
| Drop shadow, inner shadow **[plan]** | `DropShadowSetting`, `InnerShadowSetting` | ❌ | ❌ | |
| Outer and inner glow **[plan]** | `OuterGlowSetting`, `InnerGlowSetting` | ❌ | ❌ | |
| Feather, gradient feather **[plan]** | `FeatherSetting`, `GradientFeatherSetting` | ❌ | ❌ | |
| Bevel and emboss, satin, directional feather | `BevelAndEmbossSetting`, ... | ❌ | ❌ | Out of scope in the plan. |

## Images and placed files

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Place JPEG and PNG | `Image`, `Link` | ✅ | ✅ | Spec `image`, `idml_fill`, `idml_relink_image`. Fit: `fill`, `fit`, `stretch`, `center`, `original`. |
| Crop, scale, offset in the frame | `Image@ItemTransform`, `GraphicBounds` | 🟡 | ✅ | Agent: through the fit modes only. |
| PNG transparency | PNG alpha | — | ✅ | Soft mask. |
| Placed PDF | `PDF` | DOM only | ✅ | Vector, clipped, crop box honoured. The spec and tools accept JPEG and PNG only. |
| Placed InDesign page | `ImportedPage` (link to an `.indd`) | DOM only | ❌ | The linked `.indd` is read as a PDF, fails ("not a readable PDF") and nothing is drawn. INDD is Adobe's closed binary format, so this can only be reported, not drawn; InDesign's own PDF draws the page from the `.indd`. |
| TIFF, PSD, EPS, AI **[new]** | `Image`, `EPS` | ❌ | ❌ | Warns "Placed ... graphics are not rendered". TIFF and PSD are the usual print formats in real InDesign files. |
| Image clipping paths, alpha channels | `ClippingPathSettings` | ❌ | ❌ | The image is clipped to its frame only. |
| Relink, relink to folder, package | `Link@LinkResourceURI` | ✅ | — | `idml_relink_image`, `idml_relink_folder`, `idml_package`. Summary reports actual and effective ppi. |
| Missing links | — | ✅ | ✅ | Reported by `idml_to_pdf` and `idml_render_page`; optional grey cross. |

## Text frames

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Text frame, threading | `TextFrame@ParentStory`, `NextTextFrame` | ✅ | ✅ | Spec `text`, `continueFrom`. |
| Columns, gutter, fixed column width | `TextFramePreference` | ✅ | ✅ | `columns`, `columnGutter`. |
| Inset spacing | `TextFramePreference/InsetSpacing` | ✅ | ✅ | `inset`. Text also stays clear of the part of a visible stroke inside the frame: half the weight for a centred stroke (measured in `02-item-object-styles` p1, `06-frame-autosize`, `06-frame-basics`: text 1, 0.5 and 0.25 pt further in for 2, 1 and 0.5 pt strokes; a stroke of [None] takes no room, `01-doc-masters`). Inside and outside strokes (whole weight, nothing) are not in the samples. |
| Vertical justification | `VerticalJustification` | ✅ | ✅ | `verticalAlign`: top, center, bottom, justify. |
| First baseline offset | `FirstBaselineOffset`, `MinimumFirstBaselineOffset` | ❌ | ✅ | |
| Overset detection | — | ✅ | ✅ | `idml_check_fit` (exact, from the composer); summary `likelyOverset` (estimate). |
| Auto-size frames | `TextFramePreference@AutoSizingType`, `AutoSizingReferencePoint`, `UseMinimumHeightForAutoSizing`, `UseNoLineBreaksForAutoSizing` | ❌ | ✅ | The last frame of a story grows or shrinks to its text before it is drawn and in `idml_check_fit` (`TextMeasurer`): height to the last baseline plus the bottom inset, columns balanced; width to the longest paragraph on one line; proportionally until the text fits; around the reference point, within the minimum height (counted with the stroke). A frame already the size of its text keeps it: on every auto-size frame of the 74 samples our size is InDesign's within 0.05 pt, except where our line breaks differ (`06-frame-autosize` p6 grows a line). Not done: minimum width and threaded and non-rectangular frames from memory only; anchored frames keep their size. See [responsive-layout-plan.md](responsive-layout-plan.md), phase 1. |
| Balance columns | `TextFramePreference@VerticalBalanceColumns` | ❌ | ✅ | When the story ends in the frame, its columns end at the lowest baseline that still holds the text, the first columns taking the extra line (`06-frame-span-balance` p5: 6, 6, 6; p6: 4, 4, 2 below a headline). |
| Span and split columns | `SpanColumnType`, `SpanSplitColumnCount`, `SplitColumnInsideGutter`, `SplitColumnOutsideGutter`, `SpanColumnMinSpaceBefore`/`After` | ❌ | ✅ | A spanning paragraph is set across all or the first N columns, below the frame's text above it, which is balanced first; the columns it spans start a leading below its last line and space after, the others where it started. A split paragraph is set in sub-columns of its column, balanced, the text going on below (`06-frame-span-balance` p1 to p4, within 1 pt but for paragraph-composer choices). Space before/after span from memory (0 in the samples). |
| Text in non-rectangular frames | text in `Oval`, `Polygon`, curved paths, rectangles with corner options | ❌ | ✅ | Lines stay inside the frame's path less its inset (a single `InsetSpacing` value) and half its stroke, by the rules of text wrap (an inverted object-shape wrap, edges truncated to whole points from the column's left edge); the first line moves down 1 pt at a time until its first word fits (the oval's first baseline 1 pt lower, the triangle's 15 pt), a story threads through shaped frames, a star's two legs take a line each. `06-frame-shapes` p2 100%, p1 96.7%, p3 92.8%, p4 95.5%, p5 93.9%; `02-item-corners` p4 100% (corners on a text frame). The rest: paragraph-composer choices in narrow stretches and edges that land within 0.15 pt of a whole point (ours on the other side of it, p5). Auto-size does not apply to shaped frames. |
| Text on a path | `TextPath` | ❌ | ❌ † | |
| Baseline grid | `GridPreference@BaselineStart`, `BaselineDivision`, `BaselineGridRelativeOption`; `BaselineFrameGridOption`; paragraph `GridAlignment="AlignBaseline"`, `GridAlignFirstLineOnly` | ❌ | ✅ | Measured against `06-frame-baseline-grid`: an aligned line moves down to the next grid line after leading, space before/after or the first-baseline offset, and the next line counts its leading from there; first line only snaps the first line of each paragraph; a custom frame grid (from the frame, its object style or the document default) starts its offset below the top of the frame, its inset, the page or the top margin. Every baseline on every page within 0.13 pt (a stroked frame starts half the stroke weight lower, see inset spacing). Not done: CJK grid alignments (`AlignEmCenter`, ...), text in table cells and anchored frames, page-relative grids for rotated or scaled frames; vertical justification other than top still moves aligned lines off the grid (not measured). |

## Character formatting

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Font family and style | `AppliedFont`, `FontStyle` | ✅ | ✅ | Missing fonts are substituted and reported (`idml_fonts`, `idml_list_fonts`). |
| Size, leading, auto leading | `PointSize`, `Leading`, `AutoLeading` | ✅ | ✅ | Agent: fixed leading only. |
| Colour and tint | `FillColor`, `FillTint` | ✅ | ✅ | |
| Tracking | `Tracking` | ✅ | ✅ | |
| Kerning: metrics | `KerningMethod` | — | ✅ | |
| Kerning: optical, manual pairs **[new]** | `KerningMethod="Optical"`, `KerningValue` | ❌ | ❌ † | Optical is drawn as metrics; manual kerning between two letters is ignored. |
| Underline, strikethrough | `Underline`, `StrikeThru` | ✅ | ✅ | Automatic weight and offset as InDesign takes them (`07-char-underline-strike`): an underline from the font's post table (`underlineThickness`, centred `underlinePosition` below the baseline), a strikethrough a third of the point size above the baseline, weighing OS/2 `yStrikeoutSize`. Underlines are painted under the line's text, strikethroughs over it. |
| Underline and strikethrough options **[new]** | `UnderlineWeight`, `UnderlineOffset`, `UnderlineColor`, ... | ❌ | ✅ | Weight and offset in points (−9999 is auto; an underline offset is down from the baseline, a strikethrough's up), colour (`Text Color` or a swatch) and tint, gap colour and tint, and the types Solid, Dashed (about 12 pt dashes and gaps fitted to the length), Canned Dotted, Wavy and Thick - Thin, measured in `07-char-underline-strike` (every page 100%); the canned dashes as on frame strokes; other stroke styles drawn solid. Overprint not written. |
| All caps, small caps | `Capitalization` | ✅ | ✅ | Small caps are synthesised by scaling. |
| Superscript, subscript | `Position` | ✅ | ✅ | Also the OpenType variants. |
| Baseline shift, horizontal and vertical scale | `BaselineShift`, `HorizontalScale`, `VerticalScale` | DOM only | ✅ | Not in the spec or `idml_define_style`. |
| Skew (false italic) **[new]** | `Skew` | ❌ | ✅ | A shear in the text matrix about the baseline, positive leaning right (`07-char-basics` p5: 15° written as `24 0 6.4308 24`). |
| Text stroke (outlined text) **[new]** | `StrokeColor`, `StrokeWeight`, `StrokeTint`, `StrokeAlignment`, `EndJoin`, `MiterLimit` on text | ❌ | ✅ | Text render mode 1 as InDesign writes it (`07-char-outlined-text`): with no fill, the stroke centred on the outline at its weight; under a fill, the default outside stroke first at twice its weight and the fill over it; joins and miter limit. A centred stroke over a fill is from memory (no sample). Gradient strokes are shading patterns spanning the frame (`03-color-gradients` p4). pdf_oxide ignores text render modes and fills stroked text in both PDFs; PDFium draws the outlines. |
| Gradient text | `FillColor="Gradient/..."` on text | ❌ | 🟡 | Text filled with a gradient is drawn through a shading pattern spanning the text frame, as in InDesign's PDF (`03-color-gradients` p4, `07-char-outlined-text` p4). A gradient text stroke is drawn the same way. |
| Standard ligatures | `Ligatures` | — | ✅ | From the font's GSUB `liga`; a font without `liga` that has the fi and fl characters (U+FB01, U+FB02) uses them, as InDesign does (Liberation in doc-samples: "fi" one glyph, "ff" two). Same ligatures as InDesign on every doc-samples page checked. Embedded subsets carry no `cmap` (as InDesign's): with one, pdf_oxide chose glyphs through the text and drew fi as f and i. |
| Other OpenType features **[new]** | `OTFFigureStyle`, `OTFFraction`, `OTFDiscretionaryLigature`, `OTFSwash`, `OTFContextualAlternate`, ... | ❌ | ❌ † | Old-style and tabular figures, fractions and swashes fall back to the default glyphs. |
| No break | `NoBreak` | ❌ | ❌ † | Words joined with No Break can still be split. |
| Language | `AppliedLanguage` | DOM only | ✅ | Picks the hyphenation patterns. |

## Paragraph formatting

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Alignment and justification | `Justification` | ✅ | ✅ | `align`. "Towards spine" is drawn as left, "away from spine" as right on every page. |
| Indents | `LeftIndent`, `RightIndent`, `FirstLineIndent`, `LastLineIndent` | ✅ | ✅ | Agent: all but last line. |
| Space before and after | `SpaceBefore`, `SpaceAfter` | ✅ | ✅ | |
| Tab stops: left, centre, right; right-indent tab | `TabList` | ✅ | ✅ | Spec `tabs` (`position`, `align`, `leader`, `alignOn`) on paragraphs and paragraph styles; DOM `ParagraphFormat.TabStops`. A hanging first line (first-line indent below the left indent) also stops at the left indent, as in InDesign. Right, centre and decimal stops reserve the text after them when lines are broken, so a right tab at the column's edge keeps its page number on the line. Each tab is written to the PDF as an invisible space glyph, as InDesign does, so extracted text has a word break there. |
| Decimal and align-on tabs, tab leaders | `TabList/ListItem/Alignment`, `AlignmentCharacter`, `Leader` | ✅ | ✅ | Measured against `08-para-tabs`: the alignment character's left edge on the stop, text without it ending there; leader copies on a grid of the leader's width from the column's left edge (leaders line up from line to line), from the first slot after the text to the last whose first glyph fits before the next text; leaders in the tab's formatting. Every page 100% of words within 1 pt. |
| Hyphenation | `Hyphenation`, `Hyphenate*` | DOM only | ✅ | English (every `English: ...` language) by Liang's algorithm with the TeX hyph-en-us patterns (embedded, see THIRD-PARTY-NOTICES.md); Spanish by syllabification rules; other languages are not hyphenated. Honoured: on/off, words longer than, after first / before last, capitalized words, zone (ragged text with the Single-line Composer only: InDesign's Paragraph Composer ignores it, `08-para-hyphenation-justification` p3 breaks alike with zone 0 and 60), ladder limit (Paragraph Composer). A word with a discretionary hyphen breaks only there. The patterns allow every hyphenation point InDesign chose in the doc-samples references except two proper names (`En-gland`, `Neth-erfield`). A ligature across a hyphenation point (`of-fice`) is drawn whole unless the line breaks there. Not read: hyphenate last word, across columns, hyphen weight. |
| Paragraph composer | `Composer` | — | ✅ | [TextComposer.Paragraph.cs](../../src/CoreIdml.Pdf/Layout/TextComposer.Paragraph.cs): the Adobe Paragraph Composer (`HL Composer`, the default) chooses the breaks of the whole paragraph by total fit (Knuth–Plass), the Single-line Composer (`HL Single`) one line at a time. Costs measured against the references: badness 100 r³ for justified lines (compression weighted 4×, stretching past the maximum capped, then 333 more per percent of the width the stretching adds past 13%, compression past the minimum costing about as much as stretching to 183%: InDesign sets lines at 66–75% with an 80% minimum rather than a line more or a very loose one), 100 × the room left in 1.75 em + 0.5% of the line width for ragged lines, linear rather than cubed (the hyphenation zone plays no part; InDesign leaves 21–24 pt rather than hyphenate the line before the last, and hyphenates it to avoid 24–56 pt; a 540 pt line keeps "Mr. Morris" whole and leaves 28.7 pt on the next), TeX's line, hyphen (115), consecutive-hyphen and penultimate-line-hyphen (3500) demerits, and the ladder limit. A line that moves to a column of another width is broken again at that width, and the rest of the paragraph is planned there. `08-para-hyphenation-justification` p5 and `08-para-keeps-breaks`, `08-para-nested-grep-styles`, `11-long-tracked-changes` now match line for line; `06-frame-basics` p1 fits story u13a as InDesign does. Paragraphs with nested line styles keep the single-line composer (each line's style sets its width). Not modelled: fitness classes, the hyphenation weight slider (`HyphenWeight`; the references break alike at 5 and 10), hyphenate last word. `08-para-hyphenation-justification` p1, p3 and p5 match line for line; `08-para-hyphenation-justification` p2 (columns 2 and 3) and `07-char-language-nobreak` p1 (90 pt, French and German not hyphenated) still break differently from InDesign. |
| Word and letter spacing limits | `MinimumWordSpacing`, `DesiredWordSpacing`, `MaximumWordSpacing`, `*LetterSpacing`, `*GlyphScaling`, `SingleWordJustification` | ❌ | ✅ | [Justification.cs](../../src/CoreIdml.Pdf/Layout/Justification.cs), measured against `08-para-hyphenation-justification` p4 and p5. The desired values set every paragraph's spacing; the minimum and maximum apply to justified text only (no ragged line in the references is compressed). A justified line may compress its word spaces to the minimum to take one more word, when that sets it closer to its desired spacing than breaking earlier (compression weighs 4 times as much as stretching: the references stretch five times as often as they compress); word and letter spacing then change together by the same share of their ranges, glyph scaling only once they are at their limits, and past the maximums word and letter spacing take the rest in proportion (word spaces alone when neither has a range). A justified line holding one word is letter spaced across (`FullyJustified`) or aligned as single-word justification says. Fixed spaces (em, en, thin, ...) keep their width. Compression past the minimum is left to the paragraph composer, at about the cost of stretching to 183% (InDesign: 26% with a 50% minimum in p4, 70% with 80% in `08-para-alignment-indents` p1, 75% in `08-para-drop-caps` p1 and p6). |
| Keep options, widows and orphans | `KeepWithNext`, `KeepLinesTogether`, `KeepAllLinesTogether`, `KeepFirstLines`, `KeepLastLines` | ✅ | ✅ | Spec `keepWithNext`, `keepTogether`, `keepFirstLines`, `keepLastLines`; DOM `ParagraphFormat`. Measured against `08-para-keeps-breaks` p1 to p3: a column break the options forbid moves to the latest allowed line before it in the same column (a heading goes to the next frame with its text, a kept paragraph moves whole, an orphan or widow takes a line more along); first and last lines count only with Keep Lines Together on; when no line of the column allows a break, the keep is broken rather than leave the column empty; keeps that cannot be met in the last frame push lines into overset, and fit checks report it. Paragraphs holding a table are left alone. |
| Start in next column, frame or page | `StartParagraph` | ✅ | ✅ | Spec `startParagraph`; DOM `ParagraphFormat.StartParagraph`. Measured against `08-para-keeps-breaks` p4 to p9: next column, next frame, and the story's first frame on the next (odd, even) page; a paragraph already at the top of a column, frame or page the story has not used stays. Odd and even come from the page number (the last digit of the page name). |
| Paragraph rules **[plan]** | `RuleAbove*`, `RuleBelow*`, `KeepRuleAboveInFrame` | ❌ | ✅ | Drawn in [TextComposer.ParagraphDecorations.cs](../../src/CoreIdml.Pdf/Layout/TextComposer.ParagraphDecorations.cs), measured against `08-para-rules` (every page 100%; each rule's position, length, weight, colour and dashes as in InDesign's content stream): the rule above's bottom edge its offset above the first baseline, the rule below's top edge its offset below the last; column width (paragraph indents ignored) or the line's text width, less the rule's indents; weight, colour or Text Color, tint, and the stroke types and gap colour of underlines (dashed, dotted, wavy, thick-thin); painted before the frame's text. Rules take no space; Keep in Frame moves the first line down until the rule is inside the column. Not done: overprint; Text Color is not in the sample (the paragraph's first or last text fill, from memory). Not authorable (spec, DOM): see [paragraph-rules.md](paragraph-rules.md). |
| Paragraph borders and shading | `ParagraphBorder*`, `ParagraphShading*` | ❌ | ❌ † | Listed as a follow-up of paragraph rules. |
| Drop caps **[plan]** | `DropCapLines`, `DropCapCharacters`, `DropcapDetail`; the drop cap's character style is a nested style "through 1 `Dropcap`" | ❌ | ✅ | Drawn in [TextComposer.DropCaps.cs](../../src/CoreIdml.Pdf/Layout/TextComposer.DropCaps.cs), measured against `08-para-drop-caps`: the first N characters scaled so their cap height (the drop cap font's own) spans from the first line's cap height to line N's baseline (size = S + (N − 1) × leading / cap height: 32.38, 53.76, 75.14, 96.52 pt for 2 to 5 lines of 11/14 Liberation Serif; 51.70 pt in Liberation Sans Bold), baseline on line N (also in a paragraph with fewer lines), at the first line's indents; Align Left Edge (`DropcapDetail` bit 0) moves it left by its left side bearing; lines 1 to N start after it, lines 2 to N with the first-line indent too; spaces right after it take no room. Every drop cap glyph within 0.13 pt of InDesign's. Not done: Scale for Descenders (not in the sample), line N in another column, the first line of a centred paragraph (InDesign starts it at the drop cap, we centre it: 3.5 pt). The outlined drop cap of p3 is drawn (text stroke). Not authorable (spec, DOM). |
| Bullets and numbering | `BulletsAndNumberingListType`, `BulletChar`, `NumberingFormat`, `NumberingExpression`, `NumberingLevel`, ... | ✅ | ✅ | Generated in [ListMarkers.cs](../../src/CoreIdml.Pdf/Layout/ListMarkers.cs): bullet character and font, text after with metacharacters, number formats (arabic, padded, roman, letters), levels 1-9 with `NumberingRestartPolicies`, `NumberingContinue`/`StartAt`, one count per `AppliedNumberingList` and level through the story, named lists with `ContinueNumbersAcrossStories` carried from story to story (by first frame: page, top, left), the marker in the first character's formatting under the list's character style; on a hanging first line the tab goes to the left indent, skipping default stops. Checked against `08-para-bullets-numbering`: every page 100% of words within 1 pt. Agent: spec `list`, `bullet`, `bulletAfter`, `numberFormat`, `numberText`, `listLevel`, `startAt`; `idml_get_story` marks list paragraphs. Not drawn: centre/right marker alignment where it would move the marker (in the sample's hanging indents InDesign draws right-aligned numbers where left-aligned ones go, and so do we), lists continued across documents, `GlyphWithFont` bullets (drawn as •). |
| Nested, line and GREP styles **[new]** | `AllNestedStyles`, `AllLineStyles`, `AllGREPStyles` | ❌ | ✅ | Applied in [NestedStyles.cs](../../src/CoreIdml.Pdf/Layout/NestedStyles.cs) when the story is read: nested styles in sequence up to or through the Nth delimiter (characters, words, sentences, tabs, letters, digits, forced line break, special spaces, inline graphic, page number, end nested style), `[Repeat]`; line styles per composed line (the composer restyles the rest of the paragraph at each line, so breaks use the line's widths); GREP styles in order, a later one winning the attributes it sets, through [GrepExpression.cs](../../src/CoreIdml.Pdf/Layout/GrepExpression.cs) (InDesign's `~` metacharacters, `\x{}`, `\u`/`\l`, `\<`/`\>`, `\K`, POSIX classes; unsupported or invalid expressions are skipped, matching has a 1 s timeout). Layers: applied character style, line, nested, GREP, then local formatting. `08-para-nested-grep-styles`: every run in the reference's font and colour. The `Dropcap` delimiter (through 1 drop cap) covers exactly the drop cap's characters. Not done: the order against an applied character style is from memory (the sample has none). |
| Next style | `NextStyle` | DOM only | — | Only matters while typing in InDesign. |
| Optical margin alignment, hanging punctuation | `StoryPreference@OpticalMarginAlignment` | ❌ | ❌ † | |
| Right-to-left and CJK composition | `ParagraphDirection`, Japanese composer | ❌ | ❌ † | |

## Styles

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Paragraph and character styles | `ParagraphStyle`, `CharacterStyle`, `BasedOn` | ✅ | ✅ | `idml_define_style`, `idml_apply_style`, spec styles. Local overrides kept. |
| Object styles | `ObjectStyle` | ❌ | 🟡 | See page items. |
| Cell styles | `CellStyle` | ❌ | ✅ | Resolved for fill, edges, insets, vertical alignment; a cell without one takes its table style's region cell style. (The spec's `cellStyle` is a *paragraph* style.) |
| Table styles **[new]** | `TableStyle`, `*RegionCellStyle` | ❌ | ✅ | The table style's attributes resolve (alternating fills and strokes, border); its header, footer, left and right column and body region cell styles apply to cells without a cell style of their own (header and footer first, then the columns). `09-style-table-cell-styles` p2. |

## Tables

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Create a table | `Table` in a story | ✅ | ✅ | Spec `table`: `rows`, `headerRows`, `columnWidths`, `rowHeight`, `cellInset`, `headerFill`, `alternateFill`, borders. |
| Cell text | `Cell` | ✅ | ✅ | `idml_edit_table` `setCell`, `setRow`; `idml_get_story` lists table runs. |
| Add rows | `Row` | ✅ | ✅ | `addRow`. |
| Delete rows, insert or delete columns **[new]** | `Row`, `Column` | ❌ | — | No operation. |
| Column width, row height | `SingleColumnWidth`, `SingleRowHeight`, `AutoGrow` | ✅ | ✅ | Rows grow to fit unless at exactly. |
| Header rows, repeated after breaks | `HeaderRowCount`, `BreakHeaders`, `SkipFirstHeader` | ✅ | ✅ | Repeated at the top of every part of a split table; a table starts in the next column unless its header, first body rows and footer fit together. `BreakHeaders` (once per frame or page) and `SkipFirstHeader` are from memory: the samples keep the defaults. Agent: `headerRows` only. |
| Footer rows **[new]** | `FooterRowCount`, `BreakFooters`, `SkipLastFooter` | ❌ | ✅ | Repeated at the bottom of every part of a split table, in the footer region's cell style, their height kept free when deciding which body rows fit (`09-style-table-cell-styles` p3, `10-table-structure` p4). `BreakFooters` (once per frame or page) and `SkipLastFooter` are from memory: the samples keep the defaults. |
| Merged cells | `RowSpan`, `ColumnSpan` | ❌ | ✅ | Merged blocks never split between frames. |
| Table across columns and frames | `Row@StartRow` | — | ✅ | A row's Start Row (next column, frame, page, odd or even page) begins a new part (`10-table-structure` p4). |
| Cell fill | `Cell@FillColor`, `FillTint` | 🟡 | ✅ | Agent: header rows only (`headerFill`); the DOM sets any cell (`TableCell.SetFill`). |
| Cell borders | `*EdgeStrokeWeight`, `*EdgeStrokeColor`, `*EdgeStrokeTint` | 🟡 | ✅ | Agent: one stroke for the whole table. Shared edges drawn once, heavier wins. |
| Cell border types **[new]** | `*EdgeStrokeType`, `*EdgeStrokeGapColor` | ❌ | ❌ † | Dashed and dotted cell borders drawn solid. |
| Cell inset, vertical alignment | `TextTopInset`, ..., `VerticalJustification` | 🟡 | ✅ | Agent: one inset for every cell; vertical alignment DOM only. |
| Alternating fills and strokes **[new]** | `StartRowFillColor`, `EndRowFillColor`, `*RowFillCount`, `StartRowStrokeColor`, ... | 🟡 | ✅ | Row and column patterns with counts and skipped rows or columns, from the table or its table style; body rows only. A cell's own fill colour takes the pattern's tint. Agent: `alternateFill` (every other body row). `10-table-alternating` matches InDesign on every page. |
| Diagonal lines **[new]** | `DiagonalLine*` | ❌ | ❌ † | |
| Cell rotation **[new]** | `RotationAngle` on cells | ❌ | ❌ † | Rotated column headers are drawn horizontal. |
| Graphic cells | cell holding a `Rectangle` with an image | ❌ | ❌ † | |
| Table space before and after | `SpaceBefore`, `SpaceAfter` | ❌ | ✅ | |

## Long documents and special text

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Current page number | `<?ACE 18?>` | ❌ | ✅ | Per page on masters. |
| Next and previous page number, section marker | `<?ACE 18?>` with `PageNumberType="NextPageNumber"`/`"PreviousPageNumber"`, `<?ACE 19?>` | ❌ | ✅ | Next/previous print the page of the next/previous frame of the thread: the frame's own, else the threaded frame it overlaps or touches (a jump line); with no thread to follow, the current page. The section marker prints `Section@Marker` of the page's section (masters: of the page they are drawn on). In [TextVariables.cs](../../src/CoreIdml.Pdf/Rendering/TextVariables.cs). Checked against `11-long-markers-variables` (jump lines 4, 6 / 2, 4; "Part A"), `01-doc-sections-numbering` and `07-char-special-characters` p5. |
| Text variables | `TextVariableInstance`, `TextVariable` in designmap | ❌ | ✅ | Running headers (paragraph or character style, first or last on page, text before/after, change case, delete end punctuation) and the last page number (document or section scope) are computed per page; a page without a match repeats the previous page's header, and before the first match nothing prints. Every other variable (dates, file name, custom text, chapter number, live captions, cross-reference variables) prints the `ResultText` InDesign stored, so Output Date is the date InDesign saved. A variable never breaks across lines. `11-long-markers-variables` p2-p6: every header, folio and jump-line word within 1 pt. Not done: InDesign squeezes a line whose variable does not fit (p1, the file path); we break before it. `TextMeasurer` measures stored `ResultText` and the current page for next/previous markers. |
| Footnotes **[new]** | `Footnote` in a story | 🟡 | ❌ | Agent can read and change their text (`scope: footnote`); neither the reference number nor the footnote is drawn. Endnotes likewise. |
| Hyperlinks, cross-references | `HyperlinkTextSource`, `Hyperlink` | ❌ | 🟡 | The text is drawn; the PDF has no link annotations. |
| Bookmarks | `Bookmark` | ❌ | ❌ | No PDF outline. |
| Table of contents | generated story | 🟡 | ✅ | Drawn as the stored text; no tool regenerates it. |
| Index markers | `PageReference` | — | — | Invisible in InDesign too. |
| Conditional text **[plan]** | `Condition`, `AppliedConditions` | ❌ | ❌ † | Text under a hidden condition is still drawn. See [responsive-layout-plan.md](responsive-layout-plan.md), phase 1. |
| Tracked changes | `Change@ChangeType` | 🟡 | ✅ | Drawn as InDesign exports them: the document's current text, without change marks. `DeletedText` is skipped by the composer, fit checks, story text, word counts, `idml_find`, `idml_replace_text`, paragraph indices and XML export ([TrackedChanges.cs](../../src/CoreIdml/Dom/TrackedChanges.cs)); `InsertedText` and `MovedText` (moved text at its new place; the old place is a deletion) are drawn. Accepted and rejected changes are plain text in the IDML. Checked against `11-long-tracked-changes`: every page 100% of words within 1 pt. Agent: `idml_get_story` runs carry `change` (inserted, deleted, moved; deleted runs keep their index); changes cannot be authored, accepted or rejected. |
| Notes | `Note` | — | ✅ | Skipped, as InDesign does. |
| Find and replace | — | ✅ | — | `idml_replace_text` (plain text, across runs). GREP is not offered. |
| Templates and data | names, labels, tags, `{{key}}` | ✅ | — | `idml_fill`, `idml_import_xml`. |
| XML structure | `XMLElement`, tags | ✅ | — | `idml_xml_structure`, `idml_export_xml`, `idml_import_xml`. |
| Buttons, forms, video, audio, animation | `Button`, `MultiStateObject`, ... | ❌ | ❌ | Print renderer. |

## PDF output

| Feature | Agent | Preview | Notes |
| --- | --- | --- | --- |
| Fonts embedded as subsets | — | ✅ | |
| Image downsampling | ✅ | ✅ | `maxImageDpi`, `jpegQuality`. |
| Page range | ✅ | ✅ | |
| Bleed, trim and crop marks | 🟡 | 🟡 | Bleed and TrimBox with `includeBleed` (see bleed above); no crop or other printer's marks. |
| PDF/X, output intents, colour management | ❌ | ❌ | CMYK and RGB written as device colours; CMYK previews use a naive conversion. |
| Spreads as one PDF page | ❌ | ❌ | One PDF page per document page. |
| Tagged PDF, accessibility **[plan]** | ❌ | ❌ | Untagged: no structure tree, `/Lang`, alt text or artifacts. The IDML's accessibility settings (alt text, export tags, Articles) are kept but not authorable. See [accessibility-plan.md](accessibility-plan.md). |

## Gaps not in the plan

Ordered by how often each feature appears in real InDesign documents and how visible the error is in a preview.
Effort uses the plan's scale (S up to 2 days, M up to 1 week, L 1 to 3 weeks).

| # | Gap | Preview today | Effort | Notes |
| --- | --- | --- | --- | --- |
| 1 | ~~**Bullets and numbering**~~ | Drawn | M | See the row under paragraph formatting; `08-para-bullets-numbering` matches InDesign on every page. |
| 2 | ~~**Tracked changes**~~ | Drawn | S | See the row under long documents; `11-long-tracked-changes` matches InDesign on every page. |
| 3 | ~~**Table alternating fills, table style region cell styles**~~ | Drawn | S–M | See the table and table style rows; spec `alternateFill`. |
| 4 | ~~**Keep options, widows and orphans, start in next frame**~~ | Drawn | M | See the rows under paragraph formatting; `08-para-keeps-breaks` places every paragraph in the column and page InDesign does. |
| 5 | ~~**Text variables, next/previous page number**~~ | Drawn | S | See the rows under long documents; `11-long-markers-variables` matches InDesign within 1 pt except the squeezed file-path line on p1. |
| 6 | ~~**Tab leaders, decimal tabs**~~ | Drawn | S | See the tab rows under paragraph formatting; `08-para-tabs` matches InDesign within 1 pt on every page. |
| 7 | **Footnotes** | Missing | L | Reference numbers inline, footnote area at the bottom of the column shrinking the column. |
| 8 | ~~**Text stroke, skew, underline options**~~ | Drawn | S | See the character formatting rows; `07-char-outlined-text` and `07-char-underline-strike` match InDesign on every page. |
| 9 | **TIFF and PSD images** | Missing | M | TIFF decoder (uncompressed, LZW, deflate) for the common case; PSD composite image. |
| 10 | ~~**Nested and GREP styles**~~ | Drawn | M | See the row under paragraph formatting (nested line styles too); `08-para-nested-grep-styles` matches InDesign's fonts and colours on every page. |
| 11 | ~~**Auto-size frames, span columns, balance columns**~~ | Drawn | M | See the text frame rows; `06-frame-span-balance` and `06-frame-autosize` match InDesign but for line breaks the paragraph composer chooses differently. |
| 12 | ~~**Above-line and custom anchored objects**~~ | Drawn | M | See the row under page items; `02-item-anchored` matches InDesign, the text wrap of p4 included. |
| 13 | **Cell border types, ~~footer row repeat~~, cell rotation, diagonal lines** | Simplified | S each | Footer rows now repeat at every break (see the table rows). |
| 14 | ~~**Bleed in the PDF**~~ | With `includeBleed` | S | See bleed and slug; `01-doc-bleed-slug` matches `reference-bleed.pdf`. Printer's marks remain. |
| 15 | **Mixed inks, OpenType features, optical kerning** | Approximated or missing | M each | Lower priority. |

### Pages the paragraph composer made worse

The paragraph composer, spacing limits and drop caps (`2f0438e`, `fb081d1`, `e960eb4`) raised the doc-samples mean
from 93.25% to 94.49% (59 pages better) and were merged with these 14 pages worse, each by a cause other than the
composer itself. They are the composer's follow-up list:

| Cause | Pages (score change) |
| --- | --- |
| Hyphenation points InDesign's dictionary has and the TeX patterns lack ("Neth-erfield", "En-gland"; the Hunspell en-US patterns give the same points as ours) | `06-frame-basics` p2 (−1.3) |
| Narrow justified columns of long words; French and German are not hyphenated | `08-para-hyphenation-justification` p2 (−10.0 left of −15.5), `07-char-language-nobreak` p1 (−3.5 left of −7.8) |
| A hyphen on the second-to-last line that InDesign refuses | `09-style-text-styles` p2 (−1.6), `11-long-markers-variables` p3 (−0.6) |
| Optical margin alignment not done | `08-para-optical-margin` p2 (−0.1) |

Won back since: `06-frame-autosize` p6 (the frame now auto-sizes: it grows by the line the missing "Neth-erfield" break adds, 86.69%); `01-doc-page-sizes` p4 and `01-doc-margins-guides` p2 (ragged badness linear in the room left: both 100%);
`01-doc-sections-numbering` p7, `11-long-footnotes-endnotes` p4 and `01-doc-margins-guides` p6 ("Mor-ris": a wide ragged
column tolerates a little more room, hyphen 115: 100%, 93.1%, 100%, as before the composer). Partly:
`08-para-hyphenation-justification` p2 and `07-char-language-nobreak` p1 (stretching that adds over 13% of a justified
line's width costs more; compression past the minimum costs less). `02-item-corners` p4 and `06-frame-shapes` p4 (text now stays inside shaped frames: 99.96%, 95.5%).

`08-para-drop-caps` p2 no longer reports story u270 overset.

Agent-only gaps (the preview already draws them, the tools cannot author them), cheapest first: stroke alignment,
join; baseline shift, scale and language in styles; cell fill, inset and vertical alignment per cell;
merged cells; delete rows and add or delete columns; groups; placed PDFs; object styles. Most are a field in
`SpecModel.cs`, `SpecChecker.cs`, `SpecSchema.cs` and `document-spec.md` over DOM code that partly exists.

## How to check a row

1. Make (or ask someone with InDesign to make) a small document with the feature, and export IDML plus PDF.
2. `idml_render_page` the IDML and compare with the PDF page. Fonts first: see the comparison notes in
   [docs/reference](../reference).
3. `idml_to_pdf` warnings name some unsupported features (placed EPS/AI); most rows marked ❌ are silent.

When a row changes, update this table with the code, and the "Not supported" section of
`plugins/coreidml/skills/idml-documents/references/document-spec.md`.
