# Paragraph rules

Status: **drawn; authoring planned.** Rules above and below, paragraph borders and shading are read from the IDML and
drawn by the composer and renderer, measured against `08-para-rules` and `08-para-borders-shading` (every page of both
at 100% in the visual report, the rules' and boxes' positions, weights, colours and dashes equal to InDesign's in its
content stream). The DOM, spec and tools ([Design (authoring)](#design-authoring)) are not done. Overview, order,
milestones and open questions: [README.md](README.md).

## IDML

Checked in the two samples' `Resources/Styles.xml` (the defaults are `[No paragraph style]`'s).

**Rules**, on `ParagraphStyle` and `ParagraphStyleRange`:

- **Attributes:** `RuleAbove` (`"true"` turns it on), `RuleAboveLineWeight` (1), `RuleAboveTint` (−1: none),
  `RuleAboveOffset` (0), `RuleAboveLeftIndent`, `RuleAboveRightIndent` (0), `RuleAboveWidth`
  (`ColumnWidth`|`TextWidth`), `RuleAboveOverprint`, `RuleAboveGapTint` (−1), `RuleAboveGapOverprint` and
  `KeepRuleAboveInFrame` (`"false"`). The same set exists for `RuleBelow`, without `KeepRuleAboveInFrame`.
- **`Properties`:** `<RuleAboveColor type="string">Text Color</RuleAboveColor>` (the default) or a swatch,
  `<RuleBelowColor type="object">Color/Black</RuleBelowColor>`; `<RuleBelowType type="object">StrokeStyle/$ID/Dashed</RuleBelowType>`
  (`Solid`, `Dashed`, `Canned Dotted`, `Wavy`, `ThickThin` in the sample); `<RuleBelowGapColor type="object">Color/Yellow</RuleBelowGapColor>`
  (default `Swatch/None`).

**Shading:** `ParagraphShadingOn`, `ParagraphShadingTint` (20), `ParagraphShadingOverprint`, `ParagraphShadingWidth`
(`ColumnWidth`), `ParagraphShadingClipToFrame`, `ParagraphShadingSuppressPrinting`, `ParagraphShadingLeftOffset`,
`...RightOffset`, `...TopOffset`, `...BottomOffset` (0), `ParagraphShadingTopOrigin` (`AscentTopOrigin`),
`ParagraphShadingBottomOrigin` (`DescentBottomOrigin`), `ParagraphShadingTopLeftCornerOption` (`None`; the sample also
has `RoundedCorner`, `InverseRoundedCorner`, `BevelCorner`), `...TopLeftCornerRadius` (12) and the same for the other
three corners; `<ParagraphShadingColor type="object">Color/Black</ParagraphShadingColor>`.

**Borders:** `ParagraphBorderOn`, `ParagraphBorderTint` (−1), `ParagraphBorderOverprint`, `ParagraphBorderGapTint`,
`ParagraphBorderGapOverprint`, the four offsets, corner options and radii as for shading, `ParagraphBorderStrokeEndJoin`
(`MiterEndJoin`), `ParagraphBorderStrokeEndCap` (`ButtEndCap`), `ParagraphBorderWidth`, `ParagraphBorderTopOrigin`,
`ParagraphBorderBottomOrigin`, `ParagraphBorderTopLineWeight`, `...BottomLineWeight`, `...LeftLineWeight`,
`...RightLineWeight` (1), `ParagraphBorderDisplayIfSplits` (`"false"`) and `MergeConsecutiveParaBorders` (`"true"`);
`ParagraphBorderColor` (`Color/Black`), `ParagraphBorderGapColor` (`Swatch/None`) and `ParagraphBorderType`
(`StrokeStyle/$ID/Solid`) under `Properties`.

## Geometry, measured in the reference PDFs

**Rules** (08-para-rules):

- A rule is a stroked line of its weight. The rule above's **bottom edge** is its offset **above the first line's
  baseline** (its centre offset + weight/2 above it: offsets 0, 8 and 16 in p2; with offset 6 the rule
  crosses the 14 pt line's capitals, p1). The rule below's **top edge** is its offset **below the last line's
  baseline** (p1, p2).
- **Column width** spans the column from its edges (243.75 pt in a 250 pt frame with a 3.125 pt inset), less the rule's
  left and right indents (p3: 20 and 40). **Text width** spans the line's text (157.459 pt for "Mr. Bennet made no
  answer"), from where the text starts (p3: past a 30 pt paragraph indent), less the rule's indents. Whether a column
  rule honours paragraph indents is not in the sample; the shading of 08-para-borders-shading p1 does not, and rules are
  drawn alike.
- **No space is taken:** first baselines and the leading between paragraphs are the same with rules (p1, p5: space
  before 24 + leading 18 = 42 pt between two paragraphs with rules above; p6: 13 pt leading + 6 pt space after).
- **Keep in frame** (`KeepRuleAboveInFrame`, p5): at the top of the frame the first baseline moves down until the
  rule's top is at the frame's inset (a 3 pt rule 20 pt up: the baseline 23 pt below the inset instead of at the
  9.7 pt ascent). Without it the rule sits above the frame, unclipped.
- **Order:** every rule of a frame is painted before the frame's text (p6: the seven rules, then the text).
- **Types** (p4), drawn as underlines are (07-char-underline-strike): dashes `[11.607 11.607]` (whole dashes of about
  12 pt) over the gap colour painted solid first, round dots `[0 8.917]` as wide as the weight, a wave drawn at 0.265 ×
  the weight, thick-thin as a half-weight line a quarter weight up and a sixth-weight line 5/12 down. Tint: `0 0 0 0.5 K`
  for 50% black (p6).

**Borders and shading** (08-para-borders-shading):

- The box runs from the first line's ascent (the first-baseline Ascent: 7.627 pt for 11 pt Liberation Serif) to the
  last line's descent (2.374 pt), across the column, grown by the offsets (p1: 6 pt all round, 255.75 × 22 pt for a
  one-line paragraph in a 243.75 pt column).
- **Shading** is a filled rectangle (`re f`) in its colour at its tint (p1: cyan 40%). It spans the column whatever the
  paragraph's indents (p1: indents of 12, the shading still 243.75 pt from the column's edge). Shading alone is drawn
  **paragraph by paragraph** (p1: two one-line paragraphs, two rectangles, overlapping with offsets of 6).
- **Borders** are stroked centred on the box (p2: a 3 pt red border 4 pt out is a 251.75 × 32 pt rectangle at `3 w`).
  With left and right weights of 0, the top and bottom are two lines across the box (p2).
- **Merging:** consecutive paragraphs with the same border are **one box** from the first's ascent to the last's
  descent, the space between them included, and so is their shading (p2: two paragraphs, one rectangle; p4: three
  paragraphs in one box; the fourth, with another border colour and weight, in a box of its own that overlaps it).
- **Corners** (p3, radius 10): rounded corners are quarter circles, inverse rounded corners curves into the box with
  one control point at the corner's inner point (`v` curves), bevel corners straight cuts. The shading takes the same
  path, filled before the border is stroked.
- **No space is taken** (p1 to p4: the first baselines and the 14 pt leading are unchanged with offsets of 0 to 8 pt
  and 3 pt borders), and the text stays where it was in its column (p2: a 3 pt border does not move it in).
- **Order:** shading, border, then the text (p3, p4).

**Not in the samples**, implemented from memory and noted in the code: `Text Color` for rules (the fill of the
paragraph's first text for the rule above, its last for the rule below); a paragraph split across columns (p5's six
paragraphs fit in one column): a box per column, its edges at the break drawn only with Display Border if Paragraph
Splits; text width for borders and shading (the widest line); the Baseline and Leading top origins and the Baseline
bottom origin; Clip to Frame; Do Not Print or Export; border stroke types other than solid; inset and fancy corners; keep
in frame at the top of a later column.

## What is drawn, and where

- [ParagraphDecorations.cs](../../src/CoreIdml.Pdf/Layout/ParagraphDecorations.cs): the resolved `ParagraphRule`,
  `ParagraphBorder` and `ParagraphShading` (read in `StoryReader.FormatOf` into `ParagraphFormatting`) and the
  `ParagraphShadingOp` and `ParagraphBorderOp` draw ops. Rules reuse `TextRuleOp`.
- [TextComposer.ParagraphDecorations.cs](../../src/CoreIdml.Pdf/Layout/TextComposer.ParagraphDecorations.cs): once
  the story is composed (and vertically justified), each paragraph's lines give its rules and boxes, inserted at the
  start of each frame's ops. `LineInfo` records each line's paragraph, column, text start, ascent, descent and leading
  for this. Paragraphs holding a table get none.
- `KeepRuleAboveInFrame`: the first line's `LineMetrics.KeptRuleAbove`, which `Flow.Place` honours at the top of a
  column. Nothing else changes where lines go, and line breaking is untouched.
- [IdmlRenderer.ParagraphDecorations.cs](../../src/CoreIdml.Pdf/Rendering/IdmlRenderer.ParagraphDecorations.cs):
  shading and border paths, corners, per-side strokes. Rules go through `DrawTextRule` (stroke types and gap colour).

**Not done:** overprint (rules, gaps, borders, shading); multi-line stroke styles other than thick-thin on rules;
dashed and dotted borders measured against InDesign; rules and boxes of paragraphs holding a table.

## Design (authoring)

**DOM** (`Dom/Formats.cs`):

```csharp
public enum RuleWidth { Column, Text }
public sealed record ParagraphRule
{
    public bool On { get; init; } = true;          // false turns off a based-on style's rule
    public double? Weight { get; init; }           // pt
    public Swatch? Color { get; init; }            // null = "Text Color"
    public double? Tint { get; init; }
    public StrokeKind? Type { get; init; }         // solid, dashed, dotted (as ShapeStyle)
    public Swatch? GapColor { get; init; }
    public double? Offset { get; init; }           // pt
    public double? LeftIndent { get; init; }
    public double? RightIndent { get; init; }
    public RuleWidth? Width { get; init; }
    public bool? KeepInFrame { get; init; }        // rule above only
}
// ParagraphFormat
public ParagraphRule? RuleAbove { get; init; }
public ParagraphRule? RuleBelow { get; init; }
```

`ApplyTo` writes the attributes. It maps `Type` to `StrokeStyle/$ID/...` the way `ApplyStyle` already does. Colours
go in the `Properties` child: `type="object"` for a swatch, `type="string"` for `Text Color`.

**Spec.**

- **Paragraph-only fields** (paragraph styles and `paragraphs[]`; added to `ParagraphOnlyFields`):

```json
"ruleAbove": { "weight": 1, "color": "Brand", "tint": 100, "type": "solid", "offset": 4,
               "leftIndent": 0, "rightIndent": 0, "width": "column", "gapColor": "None", "keepInFrame": true },
"ruleBelow": { "weight": 0.5, "offset": 3, "width": "text" }
```

- **Units:** points, as for every typographic field.
- **Off switch:** an object turns the rule on; `{"on": false}` turns it off.
- **Checker:** a new `RuleFields` list checks the nested object.
- **Schema:** `SpecSchema` `format` gets both objects.
- **MCP:** `idml_define_style` lists `ruleAbove`/`ruleBelow` in its schema. It already applies through the spec.

Borders and shading would follow the same pattern (`border`, `shading` objects).

**Import.** Not planned. Telling a rule from a drawn line is unreliable.

## Tests

- **Composer and renderer** ([ParagraphRuleTests.cs](../../tests/CoreIdml.Pdf.Tests/ParagraphRuleTests.cs)): rule
  positions above and below against the baselines, column and text width with indents (and a centred line), no space
  taken, keep in frame, merged and broken borders, shading per paragraph across the column, a bordered paragraph split
  across two columns (open and closed at the break), and in the PDF the shading rectangle, the border's corner curve
  and per-side lines.
- **Visual:** `08-para-rules` and `08-para-borders-shading` in the report.
- **To add with authoring:** the style XML (attributes, `Properties` colour, stroke type), the spec, `CheckSpec`.
