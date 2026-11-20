# Drop caps

Status: **drawn by the preview and PDF export; not authorable yet** (DOM, spec and tools below are still a plan).
The rendering is in [TextComposer.DropCaps.cs](../../src/CoreIdml.Pdf/Layout/TextComposer.DropCaps.cs), measured
against the doc-samples `08-para-drop-caps` package (InDesign 20). Overview, order, milestones and open questions:
[README.md](README.md).


## IDML

Checked in `08-para-drop-caps` (`Resources/Styles.xml`):

- **Attributes** on paragraph styles and ranges: `DropCapLines="3"` (0 = off), `DropCapCharacters="1"`, and
  `DropcapDetail` (lower-case "c"; `"1"` on `[No paragraph style]` and in `TextDefault` there, `"0"` on one style).
- **`DropcapDetail` bit 0 is Align Left Edge**: with `"1"` the "g" of p4 has its outline at the text's left edge,
  with `"0"` its origin is there (2.31 pt further right). Scale for Descenders is presumably another bit (memory); no
  paragraph in the sample sets it, and neither of p4's paragraphs (labelled "descenders on/off") scales the "g".
- **The drop cap's character style** is not a `DropCapStyle` attribute: InDesign stores it as the paragraph's first
  nested style, `<Delimiter type="enumeration">Dropcap</Delimiter>`, `Repetition` 1, `Inclusive` true.

## Measured behaviour

- **Size**: the drop cap's cap height (its own font's OS/2 cap height) reaches from the first line's cap height to
  line N's baseline: size = S + (N − 1) × leading / capHeight, S being the characters' own size. 11/14 Liberation
  Serif: 32.38, 53.76, 75.14, 96.52 pt for 2 to 5 lines; 51.70 pt in Liberation Sans Bold, 50.44 pt in Raleway Italic.
- **Baseline** on line N, also when the paragraph has fewer lines (p6, a 2-line paragraph under a 4-line drop cap).
- **Horizontal**: at the first line's start (left and first-line indents, p5); the text of lines 1 to N starts after
  its advances (no kerning with the next letter); lines 2 to N also take the first-line indent (p5: 20 pt).
- **Spaces after the drop cap** take no room: with two characters "It", the "is" after the space starts right after
  the "t" (p2); with three, the space is part of the drop cap and its width counts.
- **Centred paragraph** (p5): lines 2 to N are centred between the drop cap and the right edge, but line 1 starts
  right after the drop cap. Not reproduced (3.5 pt).
- **Baseline grid** (p6): the sample's styles have `GridAlignment="None"`, so the grid case is not exercised; the code
  drops the drop cap N − 1 grid increments (leading rounded up to the grid) when every line snaps.
- **Not measured**: whether the next paragraph is pushed below a drop cap that is deeper than its paragraph (memory: it
  is not; CoreIdml does not push it), list markers before a drop cap (CoreIdml leaves the marker in the first line).

## Implementation

- **Reading.** `StoryReader.FormatOf` reads `ParagraphFormatting.DropCap` (lines, characters, Align Left Edge). The
  nested style "through 1 drop cap" covers exactly the drop cap's characters (`NestedStyles`), so they come shaped in
  its font and colour.
- **Composer.** A narrow hook in `TextComposer.LayoutParagraph`: `TakeDropCap` takes the characters out of the shaped
  paragraph and scales them; `DropCapIndent` adds the extra left indent (and so the narrower width) of lines 1 to N
  to `LineIndent`, and the paragraph composer plans those N lines at their own widths (`steadyLines`);
  `EmitDropCap` draws the glyphs as their own `GlyphRunOp`s once line 1 is placed, at line 1's baseline plus N − 1 leadings.
  Line 1's metrics do not include the drop cap.
- **Fonts.** `OpenTypeFont.LeftSideBearing` reads `hmtx` for Align Left Edge.
- **Tests.** `tests/CoreIdml.Pdf.Tests/DropCapTests.cs`.
- **Not done:** Scale for Descenders; a drop cap whose line N falls in the next column (it is drawn N − 1 leadings
  below line 1 regardless); the centred first line above; outlined drop caps (p3: text stroke is not drawn at all).
- **Import:** not planned. The grouping (`Import/TextGrouping.cs`) makes the drop cap a separate small block; a
  heuristic could rejoin it later.

## Still a plan: authoring

**DOM:** `ParagraphFormat.DropCap` as `DropCap(int Lines, int Characters = 1, CharacterStyle? Style = null)`;
`ApplyTo` writes the attributes and, for the style, the "through 1 `Dropcap`" nested style InDesign writes.

**Spec**

- Paragraph-only field `"dropCap": { "lines": 3, "characters": 1, "style": "Initial" }`.
- The style is a character style; its font and colour are used, and its size is ignored.
- Checker, schema and the `idml_define_style` schema as in [paragraph rules](paragraph-rules.md).
