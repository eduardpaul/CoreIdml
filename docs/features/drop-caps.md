# Drop caps

Status: **plan, not implemented.** Written against commit `691f87a`; the files it then marked **(wip)** were committed in `dc5bbf4` (1.1.0), so their line numbers may have moved. Overview, order, milestones and open questions: [README.md](README.md).


## IDML

- **Attributes** on paragraph styles and ranges: `DropCapLines` (0 = off), `DropCapCharacters`, and `DropcapDetail`
  (lower-case "c", seen in `TextDefault`).
- **`DropcapDetail`** holds the "Align left edge" and "Scale for descenders" flags (memory: bit flags, check).
- **The drop cap's character style** is a `DropCapStyle` reference (memory: a `Properties` child; check).
- **Nested styles** (`AllNestedStyles`) belong to the same dialog. Out of scope.

## Current state

- Nothing reads drop caps.
- `LayoutParagraph` has a per-line indent only for the first line (`TextComposer.cs:356-357`).
- The first line's metrics come from all of its glyphs (`Measure`, 467-501). A drop cap must not count there.

## Design

**DOM:** `ParagraphFormat.DropCap` as `DropCap(int Lines, int Characters = 1, CharacterStyle? Style = null)`;
`ApplyTo` writes the attributes and the style reference.

**Spec**

- Paragraph-only field `"dropCap": { "lines": 3, "characters": 1, "style": "Initial" }`.
- The style is a character style; its font and colour are used, and its size is ignored.
- Checker, schema and the `idml_define_style` schema as in [paragraph rules](paragraph-rules.md).

**Composer** (after the slot refactor in [text wrap](text-wrap.md)):

- **Reading.** `StoryReader.FormatOf` reads the drop cap and resolves the style chain into a `RunStyle` override.
  `Shape` takes the first M characters out of the paragraph and shapes them separately with that style. Characters
  include opening punctuation, per InDesign.
- **Size.**
  - The drop cap's top is at the cap height of the first line, and its baseline sits on line N's baseline.
  - Line N's baseline is predictable from the paragraph's leading: `size = (capHeight₁ + (N-1) × leading) /
    font.CapHeight`.
  - If line N ends up elsewhere (a column break), emit at the last line placed in that column.
- **Width.** The drop cap's width W is the sum of its advances plus tracking.
- **Lines 1 to N** get a blocked interval `[start, start + W]` in their slot: a local wrap. The first-line indent
  interaction must be checked in phase 0.
- **Emission.** A `GlyphRunOp` for the drop cap, once line N is placed, or once the paragraph ends if it is shorter.
  Short paragraphs do not push the next paragraph (memory).
- **Not done:** "Align left edge" and "scale for descenders" need glyph bounds that `OpenTypeFont` does not read
  yet. Optional: read `hmtx` left side bearings and the `glyf` bbox later.

**Renderer:** nothing new. Glyph runs already carry the font and size.

**Import:** not planned. Today the grouping (`Import/TextGrouping.cs`) would make the drop cap a separate small
block; a heuristic could rejoin it later.

## Tests

- Composer:
  - The first N lines start W to the right.
  - The drop cap's size and baseline equal line N's baseline.
  - Line 1's leading is unchanged.
  - Two characters; a style font; justified text.
  - A paragraph shorter than N.
  - A drop cap together with a wrap.
- `TextPositionDiff` against the phase-0 sample.

**Effort: M** (about 5 days with the slot refactor in place, 8 standalone).

**Risks**

- The size formula and the alignment details (memory).
- Kerning between the drop cap and the following text.
- Interaction with the first-line indent.
