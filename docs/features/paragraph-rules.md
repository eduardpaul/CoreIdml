# Paragraph rules

Status: **drawn; authoring planned.** Rules above and below are read from the IDML and drawn by the composer and
renderer, measured against `08-para-rules` (every page at 100% in the visual report, each rule's position, length,
weight, colour and dashes equal to InDesign's in its content stream). The DOM, spec and tools
([Design (authoring)](#design-authoring)) are not done. Overview, order, milestones and open questions:
[README.md](README.md).

## IDML

Checked in the sample's `Resources/Styles.xml` (the defaults are `[No paragraph style]`'s).

**Rules**, on `ParagraphStyle` and `ParagraphStyleRange`:

- **Attributes:** `RuleAbove` (`"true"` turns it on), `RuleAboveLineWeight` (1), `RuleAboveTint` (−1: none),
  `RuleAboveOffset` (0), `RuleAboveLeftIndent`, `RuleAboveRightIndent` (0), `RuleAboveWidth`
  (`ColumnWidth`|`TextWidth`), `RuleAboveOverprint`, `RuleAboveGapTint` (−1), `RuleAboveGapOverprint` and
  `KeepRuleAboveInFrame` (`"false"`). The same set exists for `RuleBelow`, without `KeepRuleAboveInFrame`.
- **`Properties`:** `<RuleAboveColor type="string">Text Color</RuleAboveColor>` (the default) or a swatch,
  `<RuleBelowColor type="object">Color/Black</RuleBelowColor>`; `<RuleBelowType type="object">StrokeStyle/$ID/Dashed</RuleBelowType>`
  (`Solid`, `Dashed`, `Canned Dotted`, `Wavy`, `ThickThin` in the sample); `<RuleBelowGapColor type="object">Color/Yellow</RuleBelowGapColor>`
  (default `Swatch/None`).

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

**Not in the sample**, implemented from memory and noted in the code: `Text Color` (the fill of the paragraph's first
text for the rule above, its last for the rule below); keep in frame at the top of a later column.

## What is drawn, and where

- [ParagraphDecorations.cs](../../src/CoreIdml.Pdf/Layout/ParagraphDecorations.cs): the resolved `ParagraphRule`,
  read in `StoryReader.FormatOf` into `ParagraphFormatting`. Rules are drawn as `TextRuleOp`s.
- [TextComposer.ParagraphDecorations.cs](../../src/CoreIdml.Pdf/Layout/TextComposer.ParagraphDecorations.cs): once
  the story is composed (and vertically justified), each paragraph's first and last lines give its rules, inserted at
  the start of each frame's ops. `LineInfo` records each line's paragraph, column, text start and ascent for this.
  Paragraphs holding a table get none.
- `KeepRuleAboveInFrame`: the first line's `LineMetrics.KeptRuleAbove`, which `Flow.Place` honours at the top of a
  column. Nothing else changes where lines go, and line breaking is untouched.
- The renderer draws them with `DrawTextRule` (stroke types and gap colour), as underlines.

**Not done:** overprint (rules and gaps); multi-line stroke styles other than thick-thin; rules of paragraphs holding a
table. Paragraph borders and shading (`ParagraphBorder*`, `ParagraphShading*`) would reuse the same pass.

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

- **Composer** ([ParagraphRuleTests.cs](../../tests/CoreIdml.Pdf.Tests/ParagraphRuleTests.cs)): rule positions above
  and below against the baselines, column and text width with indents (and a centred line), no space taken, keep in
  frame.
- **Visual:** `08-para-rules` in the report.
- **To add with authoring:** the style XML (attributes, `Properties` colour, stroke type), the spec, `CheckSpec`.
