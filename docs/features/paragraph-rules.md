# Paragraph rules

Status: **plan, not implemented.** Written against commit `691f87a`; the files it then marked **(wip)** were committed in `dc5bbf4` (1.1.0), so their line numbers may have moved. Overview, order, milestones and open questions: [README.md](README.md).


## IDML

On `ParagraphStyle` and `ParagraphStyleRange` (names from FICHA `Styles.xml` "[No paragraph style]" and
`Preferences.xml` `TextDefault`):

- **Attributes:** `RuleAbove`, `RuleAboveLineWeight`, `RuleAboveTint` (-1 = 100), `RuleAboveOffset`,
  `RuleAboveLeftIndent`, `RuleAboveRightIndent`, `RuleAboveWidth` (`ColumnWidth`|`TextWidth`),
  `RuleAboveOverprint`, `RuleAboveGapTint`, `RuleAboveGapOverprint` and `KeepRuleAboveInFrame`. The same set exists
  for `RuleBelow`, except `KeepRuleAboveInFrame`.
- **`Properties`:** `<RuleAboveColor type="string">Text Color</RuleAboveColor>` (or a swatch, with `type="object"`
  (memory)), `<RuleAboveType type="object">StrokeStyle/$ID/Solid</RuleAboveType>` and `RuleAboveGapColor`.
  `AttributeResolver.Own` (`Dom/Resolution.cs:31-35`) reads both attribute and `Properties` forms.
- **Geometry (memory, InDesign help):**
  - The offset of a rule above runs from the first line's baseline *up* to the rule's bottom edge; its weight grows
    upward.
  - A rule below runs from the last line's baseline *down* to its top edge.
  - Rules do not change spacing, and a rule above may stick out of the frame unless `KeepRuleAboveInFrame` is set.
  - `TextWidth` spans the text of that line.
  - Whether `ColumnWidth` honours paragraph indents must be checked in phase 0.
- **Adjacent (CC 2015+):** `ParagraphShading*` and `ParagraphBorder*`, the same family. Out of scope here ([Out of scope](README.md#out-of-scope)), but
  they would reuse this code.

## Current state

- Nothing reads the rule attributes.
- `ParagraphFormatting` (`Layout/LayoutModel.cs:63-82`) has no rules.
- The `LineOp` draw op (133-137) has a width and colour but no dash or cap. The renderer strokes it at
  `IdmlRenderer.cs:892-896`.
- `DashPattern` (609-623) works from a page-item element only.
- `ParagraphFormat` (`Dom/Formats.cs:130-170`) has no rule fields.

## Design

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

`ApplyTo` (Formats.cs:154) writes the attributes. It maps `Type` to `StrokeStyle/$ID/...` the way `ApplyStyle`
already does (wip `PageItems.cs:240-248`). For colours it writes the `Properties` child in the form phase 0 shows.

**Spec.**

- **Paragraph-only fields** (paragraph styles and `paragraphs[]`; added to `ParagraphOnlyFields`,
  `SpecChecker.cs:12` wip):

```json
"ruleAbove": { "weight": 1, "color": "Brand", "tint": 100, "type": "solid", "offset": 4,
               "leftIndent": 0, "rightIndent": 0, "width": "column", "gapColor": "None", "keepInFrame": true },
"ruleBelow": { "weight": 0.5, "offset": 3, "width": "text" }
```

- **Units:** points, as for every typographic field.
- **Off switch:** an object turns the rule on; `{"on": false}` turns it off.
- **Checker:** a new `RuleFields` list checks the nested object.
- **Schema:** `SpecSchema` `format` gets both objects.
- **MCP:** `idml_define_style` lists `ruleAbove`/`ruleBelow` in its schema (`LayoutTools.cs:196-199`, wip). It
  already applies through the spec.

**Composer.**

- `StoryReader.FormatOf` (`StoryReader.cs:288`) fills `ParagraphFormatting.RuleAbove/RuleBelow`, a resolved record
  with a colour, weight, dash, gap colour and so on. "Text Color" resolves to the fill of the first glyph (above) or
  the last glyph (below).
- `EmitLine` (`TextComposer.cs:503`) returns the line's left edge and width, which `TextWidth` needs.
- In `LayoutParagraph` (336-372):
  - After the first text line is placed, `flow.Add(new RuleOp(...))` at `baseline - offset - weight/2`.
  - After the last line, at `baseline + offset + weight/2`.
  - X runs from `column.X + leftIndent` to `column.Right - rightIndent` (plus paragraph indents, if phase 0 shows
    them), or the line extent for `TextWidth`.
  - The ops go through `flow.Add`, so `FinishColumn` (1058) moves them with vertical justification.
- Empty paragraphs get rules too. This is a common divider trick, and `Measure` already handles a line with no
  glyphs.
- Paragraphs that are only a table get no rules (simplification).
- `KeepRuleAboveInFrame`: in `Flow.Place` (978), at the top of a column, `baseline = max(baseline, column.Y + offset
  + weight)`.

**Renderer.**

- A new `RuleOp(Point From, Point To, double Weight, ResolvedColor? Color, double[]? Dash, ResolvedColor? Gap)`, or
  `LineOp` extended with those fields.
- `DrawOps` strokes the gap colour first (solid), then the dashed rule.
- `DashPattern` is refactored into `static double[] Dash(string strokeType, double weight, double[]? custom)`, shared
  by items and rules.
- Multi-line stroke styles (`ThinThick`, `Triple_Stroke`, ...) are drawn solid, with a warning.

**Import.** Not planned. Telling a rule from a drawn line is unreliable.

## Tests

- **Core:** the style XML (attributes, `Properties` colour, stroke type), the spec, `CheckSpec`.
- **Composer:**
  - `RuleOp` y for above and below against the line baseline.
  - Column versus text width.
  - Indents.
  - Vertical justification moves the rules.
  - `KeepInFrame` pushes the first baseline down.
  - A rule on an empty paragraph.
  - "Text Color" resolution.
- **PDF:**
  - A dashed rule with a gap colour.
  - The phase-0 sample in `VisualTests`.

**Effort: S–M** (4 to 5 days).

**Risks**

- The geometry conventions (memory), especially indents with `ColumnWidth` and the text-width extent on justified
  lines.
- Overprint is ignored.
