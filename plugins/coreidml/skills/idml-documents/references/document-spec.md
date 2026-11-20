# The document spec (`idml_create`, `idml_apply`)

`idml_create` builds a new `.idml` from this JSON; `idml_apply` adds its colours, styles and pages to an existing
document. Every field is optional; names are camelCase (case-insensitive); comments and trailing commas are allowed.

Both return `warnings` for ignored fields: unknown ones (`fontSize`: did you mean `size`?) or ones that do not apply
there (`rows` on a text frame, `leading` on a run). Fix them: each is a design decision that did not happen. An
undeclared colour or style, or a disallowed value (`"fit": "cover"`), is an error naming the allowed values.

## Units and coordinates

- `unit` (top level): `pt` (default), `mm`, `cm` or `in`, for page sizes, margins, bleed, gutters, item positions
  and sizes, `x2`/`y2`, `cornerRadius`, `inset`, `columnWidths`, `rowHeight`, `cellInset`, the baseline grid's
  `start`.
- **Always points**: font `size`, `leading`, `spaceBefore`, `spaceAfter`, indents, `strokeWeight`, the baseline
  grid's `increment`, `splitGutter`. `tracking`:
  thousandths of an em. `tint`, `fillTint`, `strokeTint`, `opacity`: percent (100 = solid). `rotation`: degrees,
  counter-clockwise.
- `x`/`y` is the item's top-left corner from the **page's** top-left, y **down**. Margins are not added: with
  `margins: {all: 15}` in mm, the content area starts at 15, 15.

## Top level

```json
{
  "unit": "mm",
  "page": { ... },
  "colors": [ ... ],
  "gradients": [ ... ],
  "objectStyles": [ ... ],
  "characterStyles": [ ... ],
  "paragraphStyles": [ ... ],
  "pages": [ ... ]
}
```

Applied in this order: colours, gradients, object styles, character styles, paragraph styles, pages. A colour or
style must be declared (in
the spec, or already in the document for `idml_apply`) before use.

## `page`: the page setup (`idml_create` only)

| Field | |
| --- | --- |
| `size` | `A3`, `A4` (default), `A5`, `Letter` or `Legal`. Ignored when `width` and `height` are both given. |
| `width`, `height` | Page size in `unit`. |
| `landscape` | `true` swaps a portrait size. |
| `margins` | `{ "all": 15 }`, or `top`, `right`, `bottom`, `left` (each falls back to `all`, then 0). Default 36 pt. |
| `facingPages` | `true` for spreads. Default `false`. |
| `columns`, `columnGutter` | Margin columns (guides). Default 1 column, 12 pt gutter. |
| `bleed` | Default 0. |
| `baselineGrid` | `{ "increment": 14, "start": 36, "relativeTo": "page" }`: grid lines every `increment` points (make it the body leading) from `start` (in `unit`, default 36 pt) below the top of the page, or of the top margin with `"relativeTo": "topMargin"`. Paragraphs with `alignToBaselineGrid` put their baselines on it. |

## `colors`

`{ "name": "Brand", "cmyk": [75, 45, 0, 10] }` (percent), or `"rgb": [0, 102, 204]` (0-255), or `"hex": "#0066CC"`;
exactly one. `Black`, `Paper`, `Registration` and `None` always exist.

## `gradients`

```json
"gradients": [
  { "name": "Sunrise", "stops": [ { "color": "Brand" }, { "color": "Paper" } ] },
  { "name": "Glow", "type": "radial", "stops": [ { "color": "Paper" }, { "color": "Brand", "location": 70, "midpoint": 40 } ] }
]
```

A gradient is a swatch: use its name as an item's `fill` or `stroke`, or as a text `color`. `type` is `linear`
(default) or `radial`; at least two `stops`, each a colour (declared in `colors`, or built in), its `location` in
percent (default spaced evenly) and the `midpoint` where it mixes half and half with the stop before (13 to 87, default
50). On items, `gradientAngle` turns a linear gradient (degrees counter-clockwise; 0 runs left to right, 90 bottom to
top); the gradient spans the item, and a radial one is centred on it.

## `objectStyles`

```json
"objectStyles": [
  { "name": "Card", "fill": "Brand light", "stroke": "Brand", "strokeWeight": 0.5, "cornerRadius": 3, "inset": { "all": 4 } },
  { "name": "Card faded", "basedOn": "Card", "opacity": 60 }
]
```

The look of frames and shapes in one place, applied with `"objectStyle": "Card"` on items (or `idml_edit_item`
`objectStyle`; `idml_define_style` with `kind: "object"` defines one). Fields, in the units of items: `fill`,
`fillTint`, `stroke`, `strokeWeight`, `strokeTint`, `strokeType`, `cornerRadius`, `opacity`, and for text frames
`columns`, `columnGutter`, `inset`, `verticalAlign`. A style controls only what it (or its `basedOn` style) sets; an
item keeps its own values elsewhere, and fields given on the item override the style.

## `paragraphStyles` and `characterStyles`

`{ "name": "Body", "basedOn": "Base", ...format }`. Paragraph styles take every format field below; character
styles (and runs) only the character fields: `font`, `fontStyle`, `size`, `color`, `tint`, `underline`,
`strikeThrough`, `tracking`, `caps`, `position`, `stroke`, `strokeWeight`, `strokeTint`, `skew` and the underline
and strikethrough options. An existing style name is updated.

Format fields:

| Field | |
| --- | --- |
| `font`, `fontStyle` | Exactly as `idml_list_fonts` lists them (`"Arial"`, `"Bold Italic"`); others are substituted. |
| `size`, `leading` | Points. |
| `color`, `tint` | Swatch name, and its tint. |
| `align` | `left`, `center`, `right`, `justify` (last line left) or `justifyAll`. |
| `spaceBefore`, `spaceAfter` | Points. |
| `firstLineIndent`, `leftIndent`, `rightIndent` | Points. |
| `underline`, `strikeThrough` | `true` or `false`. |
| `tracking` | Thousandths of an em. |
| `caps` | `normal`, `allCaps`, `smallCaps` or `capToSmallCap`. |
| `position` | `normal`, `superscript` or `subscript`. |
| `stroke`, `strokeWeight`, `strokeTint` | An outline around the letters (swatch, points, percent). With `"color": "None"` the letters are hollow; under a fill the outline sits outside the letters. `"stroke": "None"` removes it. |
| `skew` | False italic in degrees (-85 to 85), positive leaning right. Prefer a real italic `fontStyle`. |
| `underlineWeight`, `underlineOffset`, `underlineColor`, `underlineTint`, `underlineType` | With `underline: true`: the line's weight (points), its distance below the baseline (points), swatch, tint, and `solid`, `dashed`, `dotted`, `wavy` or `thickThin`. Unset: InDesign's automatic weight and offset in the text colour. A thick, tinted underline raised behind the letters (a negative offset) works as a highlighter: underlines are painted under the text. |
| `strikeThroughWeight`, `strikeThroughOffset`, `strikeThroughColor`, `strikeThroughTint`, `strikeThroughType` | The same for `strikeThrough: true`; the offset is above the baseline. |
| `list` | `bullet`, `numbered` or `none`. Paragraphs and paragraph styles only; see [Lists](#lists). |
| `bullet`, `bulletAfter` | The bullet (one character, default `•`) and the text after it (default a tab; `^m` em space, `^>` en space). |
| `numberFormat` | `1` (default), `01`, `001`, `0001`, `a`, `A`, `i` or `I`. |
| `numberText` | The number's text: `^#` is the number, `^1` to `^9` that level's number, `\t` a tab. Default `^#.\t`; also `^#)\t`, `(^#)\t`, `^1.^#\t`. |
| `listLevel` | 1 to 9. A level restarts after any paragraph of a higher level (a lower number). |
| `startAt` | On a paragraph: restart the numbering here, at this number. On a style: the first number of its lists. |
| `tabs` | Tab stops, replacing the style's (`[]` clears them); see [Tabs](#tabs). Paragraphs and paragraph styles only. |
| `keepWithNext` | Keep the last line with the next paragraph's first N lines (0 to 5). Give headings `1` so a heading never ends a column or frame alone. Paragraphs and paragraph styles only, like the keep fields below. |
| `keepTogether` | `true`: never break the paragraph across columns or frames (short boxes, captions, addresses). |
| `keepFirstLines`, `keepLastLines` | At least this many lines before and after a column break (orphans and widows; InDesign uses 2 and 2 when either is set). |
| `startParagraph` | `nextColumn`, `nextFrame`, `nextPage`, `nextOddPage` or `nextEvenPage` (default `anywhere`): chapter openers. "Page" means the next frame of the story on that page, so thread the story through frames on those pages. |
| `dropCapLines`, `dropCapCharacters`, `dropCapStyle` | A drop cap this many lines deep (0 removes it), of this many characters (default 1), in a character style (colour, font). See [Drop caps, nested and GREP styles](#drop-caps-nested-and-grep-styles). |
| `nestedStyles` | Character styles applied in sequence from the paragraph's start, each through or up to a delimiter: a bold lead-in, a run-in heading. `[]` turns them off. |
| `grepStyles` | Character styles on the text a GREP expression matches: prices, numbers, product names. `[]` turns them off. |
| `alignToBaselineGrid`, `gridFirstLineOnly` | `true`: every baseline (or only the first line's) moves down to the next line of the document's baseline grid (`page.baselineGrid`). Lines in neighbouring columns then line up. |
| `spanColumns` | A headline over multi-column text: `0` spans all the frame's columns, `N` the first N, `1` is a single column again. |
| `splitColumns`, `splitGutter` | Set the paragraph in N sub-columns of its own column (`1`: none), `splitGutter` points apart (default 6). |
| `ruleAbove`, `ruleBelow` and their `...Color`, `...Weight`, `...Offset`, `...Tint`, `...Type`, `...Width`, `...LeftIndent`, `...RightIndent` | A rule above or below the paragraph (points). Any `ruleAbove...` field turns the rule on; `"ruleAbove": false` turns a style's off. The offset runs up from the first baseline (above) or down from the last (below) to the rule's edge; `width` is `column` (default) or `text`; types `solid`, `dashed`, `dotted`, `wavy`, `thickThin`. Headings with a rule: `"ruleBelowWeight": 1, "ruleBelowOffset": 4`. |
| `border`, `borderWeight`, `borderTint`, `borderType`, `borderOffset`, `borderCornerRadius` | A box round the paragraph: swatch (`"None"` removes it), line weight (points, every side), tint, type, the space between the text and the box (a margins object, points) and rounded corners. |
| `shading`, `shadingTint`, `shadingOffset`, `shadingCornerRadius` | A background behind the paragraph: swatch (`"None"` removes it), tint (**InDesign's default is 20**: give 100 for the full colour), how far it reaches beyond the text (points), rounded corners. Callouts and notes: `"shading": "Brand light", "shadingTint": 100, "shadingOffset": { "all": 6 }`. |

### Drop caps, nested and GREP styles

Declare the character styles first (in `characterStyles`), then refer to them from the paragraph style:

```json
"characterStyles": [ { "name": "Drop", "font": "Arial", "fontStyle": "Bold", "color": "Brand" }, { "name": "Lead", "fontStyle": "Bold" }, { "name": "Price", "color": "Brand" } ],
"paragraphStyles": [
  { "name": "Opener", "basedOn": "Body", "dropCapLines": 3, "dropCapStyle": "Drop",
    "nestedStyles": [ { "style": "Lead", "through": "words", "count": 4 } ] },
  { "name": "Item", "basedOn": "Body", "nestedStyles": [ { "style": "Lead", "upTo": ":" } ],
    "grepStyles": [ { "style": "Price", "expression": "\\$[\\d.,]+" } ] }
]
```

- A drop cap covers the first `dropCapCharacters` characters and drops through `dropCapLines` lines; its size follows
  from the lines. `dropCapStyle` is stored as InDesign does, as a first nested style "through 1 drop cap".
- Each nested style starts where the previous one ended and runs `through` (including) or `upTo` (excluding) the
  `count`th delimiter: `words`, `characters`, `sentences`, `letters`, `digits`, `tabs`, `lineBreak`,
  `endNestedStyle`, or one character such as `":"` or `"."`. Give exactly one of `through` and `upTo`.
- GREP expressions are InDesign's (`\d`, `\w`, `[[:upper:]]`, `~=` en dash, ...); escape backslashes in JSON. Later
  GREP styles win the attributes both set.

### Tabs

`"tabs": [ { "position": 300, "align": "right", "leader": "." } ]`, and a tab character (`\t`) in the text where each
column starts. Each stop:

| Field | |
| --- | --- |
| `position` | Points from the frame's left edge, inside its inset. Required. |
| `align` | `left` (default): the text starts at the stop. `right`: it ends there. `center`. `decimal`: the `alignOn` character sits on the stop, and text without it ends there. |
| `leader` | Repeated across the tab's space: `.`, `-`, `_`, `·`, or up to 8 characters. Leaders line up from line to line. |
| `alignOn` | For `decimal`: the character, `.` by default (`,` for European prices). |

Without stops, tabs go to InDesign's default stops every 36 pt. Typical uses:

- **Table of contents:** a right stop at the frame's inner width with a `.` leader: `"Chapter 1\t3"`. Indent sub-entries
  with `leftIndent`; the leader dots still line up.
- **Price list:** `[{ "position": 200, "align": "right", "leader": "." }, { "position": 260, "align": "decimal" }]`
  for `"Item\tsize\t12.50"`.

Use tabs, not spaces, to line up columns; for a grid of cells with borders, use a `table` item.

### Lists

InDesign draws bullets and numbers itself: they are **not part of the text**, so never type `•` or `1.` into a list
paragraph. Make one paragraph style per kind of item and set `list` on it:

```json
"paragraphStyles": [
  { "name": "Bullet", "basedOn": "Body", "list": "bullet", "leftIndent": 14, "firstLineIndent": -14 },
  { "name": "Step", "basedOn": "Body", "list": "numbered", "leftIndent": 18, "firstLineIndent": -18 },
  { "name": "Sub step", "basedOn": "Body", "list": "numbered", "listLevel": 2, "numberFormat": "a", "numberText": "^#)\t", "leftIndent": 36, "firstLineIndent": -18 }
]
```

- **Hanging indent:** `leftIndent` N with `firstLineIndent` -N puts the marker at the margin and the text, including
  wrapped lines, at N: the tab after the marker stops at the left indent. Without them the text follows the marker
  at the next default tab (every 36 pt).
- **Counting:** numbers run through the whole story, per level, and continue past paragraphs that are not in the list
  (notes, figures). Every numbered style at the same level shares one count, so a second list continues the first:
  give its first paragraph `"startAt": 1`. Each story counts on its own.
- `idml_get_story` with `includeParagraphs` marks list paragraphs with `"list": "bullet"` or `"numbered"`; their
  `text` does not include the marker. `idml_render_page` shows the markers.

## `pages`

`{ "items": [ ... ] }` is one page. `idml_create` makes one page per entry without `index` (at least one);
`idml_apply` **appends** each one. `{ "index": 2, "items": [ ... ] }` draws on existing page 2 (0-based) with both.
A page can set its own `margins`.

## `items`

Every item has `type` (default `text`), `x`, `y`, `width`, `height`, and optionally:

| Field | |
| --- | --- |
| `name` | Layers panel name. `idml_fill`, `continueFrom` and `idml_edit_item` find frames by it: name every frame that receives data. |
| `label` | Script label; `idml_fill` matches it too. |
| `tag` | XML tag to bind (`idml_import_xml`, `idml_fill`). |
| `layer` | Layer name; created when it does not exist. |
| `fill`, `fillTint` | Background swatch and its tint. |
| `stroke`, `strokeWeight` | Outline swatch and weight (pt). **Rectangles and ellipses have no outline unless `stroke` is given**; lines default to Black, 1 pt. `"None"` removes one. |
| `strokeTint`, `strokeType` | Outline tint, and `solid`, `dashed` or `dotted`. |
| `endCap` | Ends of lines: `butt` (default), `round` or `projecting`. |
| `opacity` | Whole item, 0-100. |
| `cornerRadius`, `rotation` | Rounded corners; degrees. |
| `objectStyle` | An object style from `objectStyles`; the item's own fields override it. |
| `gradientAngle` | With a gradient `fill` or `stroke`: its angle in degrees, counter-clockwise. |
| `wrap` | Text wrap: the text of other frames goes around the item. `boundingBox` (its box), `objectShape` (its outline: ellipses, rounded corners), `jumpObject` (no text beside it), `nextColumn` (the text after it goes to the next column), `none`. See [Text wrap](#text-wrap). |
| `wrapOffset`, `wrapSide`, `wrapInverse` | The gap kept around the item (a margins object in `unit`; `objectShape` uses `top` all round); `both` (default), `left`, `right`, `towardsSpine`, `awayFromSpine` or `largest` (the wider side of each line); `true` keeps the text inside the item instead. |

By `type`:

- **`text`**: a text frame.
  - `text`: plain text; `"\n"` starts a new paragraph. `style`: the paragraph style for it.
  - `paragraphs`: mixed formatting, after `text`. Each has `text`, `style` (paragraph style), format fields and
    `runs`; each run has `text`, `style` (a **character** style) and character format fields.
  - `columns`, `columnGutter`, `inset` (a margins object), `verticalAlign` (`top`, `center`, `bottom`, `justify`).
  - `continueFrom`: `name` of an earlier text frame whose story continues here (threaded); no text of its own.
  - `autoSize`: the frame grows or shrinks to its text: `height` (down from its top: captions, boxes whose text
    varies), `width` (one line, from its left: labels, buttons), `heightAndWidth`, `proportional` or `off`.
    `autoSizeFrom` is the point that stays put (`topLeft`, `top`, `topRight`, `left`, `center`, `right`,
    `bottomLeft`, `bottom`, `bottomRight`; default `top` for height, `left` for width, else `topLeft`);
    `autoSizeMinHeight` a height it never goes below. The preview, PDF and `idml_check_fit` size it as InDesign does.
  - `balanceColumns`: `true` ends the columns at the same depth when the story ends in the frame.
  - `firstBaseline`: where the first baseline goes: `ascent` (default), `capHeight`, `leading`, `xHeight`, `emBox`
    or `fixed`; `firstBaselineMin` the least distance (in `unit`) from the top, inside the inset (all of it with
    `fixed`: the way to put baselines exactly on a grid).
  - `ignoreWrap`: `true` lets the frame's text run under every text wrap (a caption on a wrapped photo).
- **`rectangle`**, **`ellipse`**: shapes, styled with `fill` and `stroke`.
- **`line`**: from `x`, `y` to `x2`, `y2` (default `x + width`, `y + height`). Black 1 pt unless `stroke`,
  `strokeWeight` say otherwise.
- **`image`**: `path` is the **absolute** path of a JPEG or PNG, linked (not embedded). `fit`: `fill` (default,
  crops), `fit` (whole image), `stretch`, `center` or `original`.
- **`table`**: a text frame holding a table.
  - `rows`: list of rows, each a list of cell texts.
  - `headerRows`, `columnWidths` (in `unit`), `rowHeight` (default 18 pt), `cellInset`.
  - `cellStyle`, `headerStyle`: paragraph styles. `headerFill`: a swatch. `alternateFill`: a swatch for every other
    body row, from the first below the header (zebra stripes).
  - `stroke` and `strokeWeight` draw the cell borders; `fill` is the frame's background.

### Text wrap

Put `wrap` on the item the text should avoid, not on the text frame:

```json
{ "type": "image", "name": "photo", "path": "/abs/photo.jpg", "x": 15, "y": 40, "width": 60, "height": 45,
  "wrap": "boundingBox", "wrapOffset": { "all": 4 } }
```

Every text frame the item overlaps then sets its lines beside the item, `wrapOffset` away (in `unit`). An ellipse or
a frame with rounded corners wraps closer with `objectShape`. A frame whose text should cross the item anyway (a
caption on the photo) takes `"ignoreWrap": true`. Render the page to check the lines left beside a narrow item.

## Example

One A4 page in mm: coloured band with white title, empty photo frame for `idml_fill`, body text with a bold run,
a table.

```json
{
  "unit": "mm",
  "page": { "size": "A4", "margins": { "all": 15 } },
  "colors": [ { "name": "Brand", "cmyk": [75, 45, 0, 10] }, { "name": "Brand light", "hex": "#E6EEF8" } ],
  "characterStyles": [ { "name": "Strong", "fontStyle": "Bold", "color": "Brand" } ],
  "paragraphStyles": [
    { "name": "Body", "font": "Arial", "size": 10, "leading": 14, "spaceAfter": 4 },
    { "name": "Title", "basedOn": "Body", "fontStyle": "Bold", "size": 24, "leading": 28, "color": "Paper" },
    { "name": "Cell", "basedOn": "Body", "spaceAfter": 0 }
  ],
  "pages": [
    {
      "items": [
        { "type": "rectangle", "x": 0, "y": 0, "width": 210, "height": 35, "fill": "Brand" },
        { "type": "text", "name": "title", "x": 15, "y": 12, "width": 180, "height": 15, "text": "Trail Lamp X200", "style": "Title" },
        { "type": "rectangle", "name": "photo", "x": 15, "y": 45, "width": 85, "height": 60, "fill": "Brand light", "stroke": "Brand", "strokeWeight": 0.5 },
        {
          "type": "text", "name": "description", "x": 110, "y": 45, "width": 85, "height": 60,
          "paragraphs": [
            { "style": "Body", "runs": [ { "text": "Rechargeable", "style": "Strong" }, { "text": " camping lamp with three light modes." } ] },
            { "style": "Body", "text": "Up to 40 hours on one charge." }
          ]
        },
        {
          "type": "table", "name": "specs", "x": 15, "y": 115, "width": 180, "height": 40,
          "rows": [ ["Feature", "Value"], ["Battery", "5200 mAh"], ["Weight", "310 g"] ],
          "headerRows": 1, "columnWidths": [60, 120], "cellStyle": "Cell", "headerStyle": "Cell",
          "headerFill": "Brand light", "stroke": "Black", "strokeWeight": 0.5, "cellInset": 1.5
        }
      ]
    }
  ]
}
```

## Results and fixes

`idml_create` returns the validation report, each page's created items (`id`, `name`, `type`, text frames' `story`)
and `warnings`; `idml_apply` returns the items `added`. With `"checkFit": true` both return `fit`: stories that do
not fit, with their frames by name. Pass the same `fonts` as for the PDF (fonts decide line breaks).

Small fixes do not need the spec again: `idml_edit_item` moves and resizes (**in points**) and takes `fill`,
`fillTint`, `stroke`, `strokeWeight`, `strokeTint`, `strokeType`, `endCap`, `cornerRadius`, `opacity`, `rotateBy`,
`objectStyle`, `gradientAngle`, `wrap`, `wrapOffset`, `wrapSide`, `wrapInverse`, and a text frame's `columns`,
`columnGutter`, `inset`, `verticalAlign`, `autoSize`, `autoSizeFrom`, `autoSizeMinHeight`, `balanceColumns`,
`firstBaseline`, `firstBaselineMin`, `ignoreWrap`. Put several in one `idml_batch`.

## Not supported

Master pages (drawn when a document has them, but not authorable), polygons and free paths,
effects other than opacity. Repeat a header on each page instead of a master.
