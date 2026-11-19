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
| Bleed and slug | `DocumentBleed*`, `Slug*` | 🟡 | ❌ **[new]** | `bleed` is written, but the PDF page is the trim size ([PdfDocumentBuilder.cs:65](../../src/CoreIdml.Pdf/Writing/PdfDocumentBuilder.cs#L65)): items crossing the edge are cut, no BleedBox/TrimBox, no printer's marks. |
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
| Inline anchored objects | item inside a story, `AnchoredPosition=InlinePosition` | ❌ | ✅ | Laid out as a glyph, with `AnchorYoffset`. |
| Above-line and custom anchored objects **[new]** | `AnchoredObjectSetting@AnchoredPosition` | ❌ | ❌ | Not drawn at all ([StoryReader.cs:111](../../src/CoreIdml.Pdf/Layout/StoryReader.cs#L111)): sidebars and pull quotes anchored in text disappear from the preview. |
| Visible, locked | `Visible`, `Locked` | ✅ | ✅ | `idml_edit_item`. Hidden items are not drawn. |
| Non-printing | `Nonprinting` | ❌ | ✅ | Skipped, as in InDesign's export. |
| Duplicate, delete | — | ✅ | — | `idml_duplicate_item`, `idml_delete_item` (stories and threads handled). |
| Object styles | `ObjectStyle`, `AppliedObjectStyle` | ❌ | 🟡 | Fill, stroke, corners and text frame options resolve through the object style chain. Its transparency does not (only the item's own `TransparencySetting` is read, [IdmlRenderer.cs:442](../../src/CoreIdml.Pdf/Rendering/IdmlRenderer.cs#L442)). Not listed by `idml_summarize`. |
| Text wrap **[plan]** | `TextWrapPreference` | ❌ | ❌ | Text runs under wrapped items. |

## Colour, fill and stroke

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Process colours (CMYK, RGB) | `Color@Space`, `ColorValue` | ✅ | ✅ | `idml_add_color`, spec `colors` (`cmyk`, `rgb`, `hex`). |
| Lab colours | `Color@Space="LAB"` | ❌ | ✅ | Converted to sRGB (approximate). |
| Spot colours | `Color@Model="Spot"` | ❌ | 🟡 | Drawn with their process values; no Separation colour space in the PDF. |
| Tint swatches, tints | `Tint`, `FillTint`, `StrokeTint` | ✅ | ✅ | `fillTint`, `strokeTint`, text `tint`. |
| Mixed inks **[new]** | `MixedInk`, `MixedInkGroup` | ❌ | ❌ | Resolve to nothing: the item is drawn without fill. |
| Gradients **[plan]** | `Gradient`, `GradientFill*`, `GradientStroke*` | ❌ | ❌ | Warns "Gradient ... is not rendered" ([IdmlRenderer.cs:452](../../src/CoreIdml.Pdf/Rendering/IdmlRenderer.cs#L452)); the item has no fill. |
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
| Object opacity | `TransparencySetting/BlendingSetting@Opacity` | ✅ | ✅ | `opacity`. From the item only, not its object style. |
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
| Placed PDF, placed InDesign page | `PDF`, `ImportedPage` | DOM only | ✅ | Vector, clipped, crop box honoured. The spec and tools accept JPEG and PNG only. |
| TIFF, PSD, EPS, AI **[new]** | `Image`, `EPS` | ❌ | ❌ | Warns "Placed ... graphics are not rendered". TIFF and PSD are the usual print formats in real InDesign files. |
| Image clipping paths, alpha channels | `ClippingPathSettings` | ❌ | ❌ | The image is clipped to its frame only. |
| Relink, relink to folder, package | `Link@LinkResourceURI` | ✅ | — | `idml_relink_image`, `idml_relink_folder`, `idml_package`. Summary reports actual and effective ppi. |
| Missing links | — | ✅ | ✅ | Reported by `idml_to_pdf` and `idml_render_page`; optional grey cross. |

## Text frames

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Text frame, threading | `TextFrame@ParentStory`, `NextTextFrame` | ✅ | ✅ | Spec `text`, `continueFrom`. |
| Columns, gutter, fixed column width | `TextFramePreference` | ✅ | ✅ | `columns`, `columnGutter`. |
| Inset spacing | `TextFramePreference/InsetSpacing` | ✅ | ✅ | `inset`. |
| Vertical justification | `VerticalJustification` | ✅ | ✅ | `verticalAlign`: top, center, bottom, justify. |
| First baseline offset | `FirstBaselineOffset`, `MinimumFirstBaselineOffset` | ❌ | ✅ | |
| Overset detection | — | ✅ | ✅ | `idml_check_fit` (exact, from the composer); summary `likelyOverset` (estimate). |
| Auto-size frames **[plan]** | `TextFramePreference@AutoSizingType` | ❌ | ❌ † | The frame keeps its stored size; text that InDesign would show by growing the frame is reported overset. See [responsive-layout-plan.md](responsive-layout-plan.md), phase 1. |
| Balance columns **[new]** | `TextFramePreference@VerticalBalanceColumns` | ❌ | ❌ † | |
| Span and split columns **[new]** | `SpanColumnType`, `SpanSplitColumnCount` | ❌ | ❌ † | Headlines that span columns are composed in one column. |
| Text in non-rectangular frames **[new]** | text in `Oval`, `Polygon` | ❌ | 🟡 | Composed in the bounding box ([IdmlRenderer.cs:225](../../src/CoreIdml.Pdf/Rendering/IdmlRenderer.cs#L225)), so lines run outside the shape. |
| Text on a path | `TextPath` | ❌ | ❌ † | |
| Baseline grid | `GridPreference`, `AlignToBaseline` | ❌ | ❌ † | Lines are not snapped: body text in grid-aligned documents drifts from InDesign's. |

## Character formatting

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Font family and style | `AppliedFont`, `FontStyle` | ✅ | ✅ | Missing fonts are substituted and reported (`idml_fonts`, `idml_list_fonts`). |
| Size, leading, auto leading | `PointSize`, `Leading`, `AutoLeading` | ✅ | ✅ | Agent: fixed leading only. |
| Colour and tint | `FillColor`, `FillTint` | ✅ | ✅ | |
| Tracking | `Tracking` | ✅ | ✅ | |
| Kerning: metrics | `KerningMethod` | — | ✅ | |
| Kerning: optical, manual pairs **[new]** | `KerningMethod="Optical"`, `KerningValue` | ❌ | ❌ † | Optical is drawn as metrics; manual kerning between two letters is ignored. |
| Underline, strikethrough | `Underline`, `StrikeThru` | ✅ | 🟡 | Default weight, offset and colour only. |
| Underline and strikethrough options **[new]** | `UnderlineWeight`, `UnderlineOffset`, `UnderlineColor`, ... | ❌ | ❌ † | Highlighter-style underlines (thick, coloured, offset) are drawn as plain underlines. |
| All caps, small caps | `Capitalization` | ✅ | ✅ | Small caps are synthesised by scaling. |
| Superscript, subscript | `Position` | ✅ | ✅ | Also the OpenType variants. |
| Baseline shift, horizontal and vertical scale | `BaselineShift`, `HorizontalScale`, `VerticalScale` | DOM only | ✅ | Not in the spec or `idml_define_style`. |
| Skew (false italic) **[new]** | `Skew` | ❌ | ❌ † | |
| Text stroke (outlined text) **[new]** | `StrokeColor`, `StrokeWeight` on text | ❌ | ❌ | Glyphs are only filled ([IdmlRenderer.cs:904](../../src/CoreIdml.Pdf/Rendering/IdmlRenderer.cs#L904)). Outlined display type renders as solid. |
| Gradient text | `FillColor="Gradient/..."` on text | ❌ | ❌ | Part of the gradients plan (stretch goal). |
| Standard ligatures | `Ligatures` | — | ✅ | |
| Other OpenType features **[new]** | `OTFFigureStyle`, `OTFFraction`, `OTFDiscretionaryLigature`, `OTFSwash`, `OTFContextualAlternate`, ... | ❌ | ❌ † | Old-style and tabular figures, fractions and swashes fall back to the default glyphs. |
| No break | `NoBreak` | ❌ | ❌ † | Words joined with No Break can still be split. |
| Language | `AppliedLanguage` | DOM only | ✅ | Picks the hyphenation patterns. |

## Paragraph formatting

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Alignment and justification | `Justification` | ✅ | ✅ | `align`. "Towards spine" is drawn as left, "away from spine" as right on every page. |
| Indents | `LeftIndent`, `RightIndent`, `FirstLineIndent`, `LastLineIndent` | ✅ | ✅ | Agent: all but last line. |
| Space before and after | `SpaceBefore`, `SpaceAfter` | ✅ | ✅ | |
| Tab stops: left, centre, right; right-indent tab | `TabList` | ❌ | ✅ | |
| Decimal and align-on tabs, tab leaders **[new]** | `TabList/ListItem/Alignment`, `Leader` | ❌ | ❌ | Decimal tabs are drawn as left tabs; dot leaders (price lists, tables of contents) are missing. |
| Hyphenation | `Hyphenation`, `Hyphenate*` | DOM only | ✅ | For the languages `Hyphenator` supports. |
| Paragraph composer | `Composer` | — | 🟡 | Greedy single-line composer only: line breaks can differ from InDesign's default Paragraph Composer. |
| Word and letter spacing limits | `MinimumWordSpacing`, ... | ❌ | 🟡 | Justification stretches word spaces; the 133% limit is fixed, letter and glyph spacing are not used. |
| Keep options, widows and orphans **[new]** | `KeepWithNext`, `KeepLinesTogether`, `KeepAllLinesTogether`, `KeepFirstLines`, `KeepLastLines` | DOM only | ❌ | The DOM writes them but the composer ignores them: a heading can end a column with its text in the next. |
| Start in next column, frame or page **[new]** | `StartParagraph` | ❌ | ❌ † | |
| Paragraph rules **[plan]** | `RuleAbove*`, `RuleBelow*` | ❌ | ❌ | |
| Paragraph borders and shading | `ParagraphBorder*`, `ParagraphShading*` | ❌ | ❌ † | Listed as a follow-up of paragraph rules. |
| Drop caps **[plan]** | `DropCapLines`, `DropCapCharacters` | ❌ | ❌ | |
| Bullets and numbering **[new]** | `BulletsAndNumberingListType`, `BulletChar`, `NumberingFormat`, ... | ❌ | ❌ | The bullet or number is generated by InDesign, not stored as text: **bulleted and numbered lists lose their markers** in the preview. Common in every kind of document. |
| Nested, line and GREP styles **[new]** | `NestedStyle`, `NestedLineStyle`, `NestedGrepStyle` | ❌ | ❌ † | Run-in heads ("**Bold lead-in.** Body text") lose their character style. |
| Next style | `NextStyle` | DOM only | — | Only matters while typing in InDesign. |
| Optical margin alignment, hanging punctuation | `StoryPreference@OpticalMarginAlignment` | ❌ | ❌ † | |
| Right-to-left and CJK composition | `ParagraphDirection`, Japanese composer | ❌ | ❌ † | |

## Styles

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Paragraph and character styles | `ParagraphStyle`, `CharacterStyle`, `BasedOn` | ✅ | ✅ | `idml_define_style`, `idml_apply_style`, spec styles. Local overrides kept. |
| Object styles | `ObjectStyle` | ❌ | 🟡 | See page items. |
| Cell styles | `CellStyle` | ❌ | ✅ | Resolved for fill, edges, insets, vertical alignment. (The spec's `cellStyle` is a *paragraph* style.) |
| Table styles **[new]** | `TableStyle`, `*RegionCellStyle` | ❌ | 🟡 | The table style's own attributes resolve; its header, body, footer and column region cell styles are not applied to cells. |

## Tables

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Create a table | `Table` in a story | ✅ | ✅ | Spec `table`: `rows`, `headerRows`, `columnWidths`, `rowHeight`, `cellInset`, `headerFill`, borders. |
| Cell text | `Cell` | ✅ | ✅ | `idml_edit_table` `setCell`, `setRow`; `idml_get_story` lists table runs. |
| Add rows | `Row` | ✅ | ✅ | `addRow`. |
| Delete rows, insert or delete columns **[new]** | `Row`, `Column` | ❌ | — | No operation. |
| Column width, row height | `SingleColumnWidth`, `SingleRowHeight`, `AutoGrow` | ✅ | ✅ | Rows grow to fit unless at exactly. |
| Header rows, repeated after breaks | `HeaderRowCount` | ✅ | ✅ | |
| Footer rows **[new]** | `FooterRowCount` | ❌ | 🟡 | Drawn once at the end; not repeated at each frame or column break. |
| Merged cells | `RowSpan`, `ColumnSpan` | ❌ | ✅ | Merged blocks never split between frames. |
| Table across columns and frames | — | — | ✅ | |
| Cell fill | `Cell@FillColor`, `FillTint` | 🟡 | ✅ | Agent: header rows only (`headerFill`); the DOM sets any cell (`TableCell.SetFill`). |
| Cell borders | `*EdgeStrokeWeight`, `*EdgeStrokeColor`, `*EdgeStrokeTint` | 🟡 | ✅ | Agent: one stroke for the whole table. Shared edges drawn once, heavier wins. |
| Cell border types **[new]** | `*EdgeStrokeType`, `*EdgeStrokeGapColor` | ❌ | ❌ † | Dashed and dotted cell borders drawn solid. |
| Cell inset, vertical alignment | `TextTopInset`, ..., `VerticalJustification` | 🟡 | ✅ | Agent: one inset for every cell; vertical alignment DOM only. |
| Alternating fills and strokes **[new]** | `StartRowFillColor`, `EndRowFillColor`, `*RowFillCount`, `StartRowStrokeColor`, ... | ❌ | ❌ † | Zebra-striped tables are drawn plain. Very common in data sheets and price lists. |
| Diagonal lines **[new]** | `DiagonalLine*` | ❌ | ❌ † | |
| Cell rotation **[new]** | `RotationAngle` on cells | ❌ | ❌ † | Rotated column headers are drawn horizontal. |
| Graphic cells | cell holding a `Rectangle` with an image | ❌ | ❌ † | |
| Table space before and after | `SpaceBefore`, `SpaceAfter` | ❌ | ✅ | |

## Long documents and special text

| Feature | IDML | Agent | Preview | Notes |
| --- | --- | --- | --- | --- |
| Current page number | `<?ACE 18?>` | ❌ | ✅ | Per page on masters. |
| Next and previous page number, section marker **[new]** | other `ACE` markers | ❌ | ❌ † | "Continued on page N" lines are empty. |
| Text variables **[new]** | `TextVariableInstance@ResultText` | ❌ | ❌ | Running heads, dates, file names and chapter numbers are blank. Partly in the masters plan ("text variables' `ResultText`"). |
| Footnotes **[new]** | `Footnote` in a story | 🟡 | ❌ | Agent can read and change their text (`scope: footnote`); neither the reference number nor the footnote is drawn. Endnotes likewise. |
| Hyperlinks, cross-references | `HyperlinkTextSource`, `Hyperlink` | ❌ | 🟡 | The text is drawn; the PDF has no link annotations. |
| Bookmarks | `Bookmark` | ❌ | ❌ | No PDF outline. |
| Table of contents | generated story | 🟡 | ✅ | Drawn as the stored text; no tool regenerates it. |
| Index markers | `PageReference` | — | — | Invisible in InDesign too. |
| Conditional text **[plan]** | `Condition`, `AppliedConditions` | ❌ | ❌ † | Text under a hidden condition is still drawn. See [responsive-layout-plan.md](responsive-layout-plan.md), phase 1. |
| Tracked changes **[new]** | `Change@ChangeType` | ❌ | ❌ | Text inside `Change` is always drawn ([StoryReader.cs:14](../../src/CoreIdml.Pdf/Layout/StoryReader.cs#L14)), so **deleted text appears** in the preview; InDesign's layout shows only the accepted text. |
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
| Bleed, trim and crop marks | ❌ | ❌ | See bleed above. |
| PDF/X, output intents, colour management | ❌ | ❌ | CMYK and RGB written as device colours; CMYK previews use a naive conversion. |
| Spreads as one PDF page | ❌ | ❌ | One PDF page per document page. |
| Tagged PDF, accessibility **[plan]** | ❌ | ❌ | Untagged: no structure tree, `/Lang`, alt text or artifacts. The IDML's accessibility settings (alt text, export tags, Articles) are kept but not authorable. See [accessibility-plan.md](accessibility-plan.md). |

## Gaps not in the plan

Ordered by how often each feature appears in real InDesign documents and how visible the error is in a preview.
Effort uses the plan's scale (S up to 2 days, M up to 1 week, L 1 to 3 weeks).

| # | Gap | Preview today | Effort | Notes |
| --- | --- | --- | --- | --- |
| 1 | **Bullets and numbering** | Markers missing | M | Bullet char, number formats, list counters across frames and stories, tab after marker. Add `bullets`/`numbering` to the spec and `idml_define_style`. |
| 2 | **Tracked changes** | Deleted text drawn | S | Skip `Change[@ChangeType="DeletedText"]` in `StoryReader`; also exclude it from `idml_get_story` text and fit checks. A correctness bug, not a feature. |
| 3 | **Table alternating fills, table style region cell styles** | Plain tables | S–M | Both resolved in `DrawCell`; the agent side can expose `alternateFill` on the spec table. |
| 4 | **Keep options, widows and orphans, start in next frame** | Headings orphaned | M | In `Flow.Place`; needs look-ahead of N lines. Changes overset results. |
| 5 | **Text variables, next/previous page number** | Blank | S | Draw `ResultText`; compute page markers like the current page number. |
| 6 | **Tab leaders, decimal tabs** | Missing dots, misaligned numbers | S | In `ApplyTabAlignment`. |
| 7 | **Footnotes** | Missing | L | Reference numbers inline, footnote area at the bottom of the column shrinking the column. |
| 8 | **Text stroke, skew, underline options** | Solid, upright, thin | S | Text render mode 2, a skew in `Tm`, underline weight/offset/colour. |
| 9 | **TIFF and PSD images** | Missing | M | TIFF decoder (uncompressed, LZW, deflate) for the common case; PSD composite image. |
| 10 | **Nested and GREP styles** | Run-in heads plain | M | Applied when reading the story into runs. |
| 11 | **Auto-size frames, span columns, balance columns** | Overset or one column | M | All in frame-area construction. Auto-size is now in [responsive-layout-plan.md](responsive-layout-plan.md). |
| 12 | **Above-line and custom anchored objects** | Missing | M | Custom position relative to frame, page or margin. |
| 13 | **Cell border types, footer row repeat, cell rotation, diagonal lines** | Simplified | S each | |
| 14 | **Bleed in the PDF** | Cut at trim | S | MediaBox = trim + bleed, TrimBox and BleedBox. |
| 15 | **Mixed inks, OpenType features, optical kerning, baseline grid** | Approximated or missing | M each | Lower priority. |

Agent-only gaps (the preview already draws them, the tools cannot author them), cheapest first: stroke alignment,
join; baseline shift, scale and language in styles; tab stops; cell fill, inset and vertical alignment per cell;
merged cells; delete rows and add or delete columns; groups; placed PDFs; object styles. Most are a field in
`SpecModel.cs`, `SpecChecker.cs`, `SpecSchema.cs` and `document-spec.md` over DOM code that partly exists.

## How to check a row

1. Make (or ask someone with InDesign to make) a small document with the feature, and export IDML plus PDF.
2. `idml_render_page` the IDML and compare with the PDF page. Fonts first: see the comparison notes in
   [docs/reference](../reference).
3. `idml_to_pdf` warnings name some unsupported features (gradients, placed EPS/AI); most rows marked ❌ are silent.

When a row changes, update this table with the code, and the "Not supported" section of
`plugins/coreidml/skills/idml-documents/references/document-spec.md`.
