# The document spec (`idml_create`, `idml_apply`)

`idml_create` builds a new `.idml` from this JSON; `idml_apply` adds its colours, styles and pages to an existing
document. Every field is optional; names are camelCase (case-insensitive); comments and trailing commas are allowed.

Both return `warnings` for ignored fields: unknown ones (`fontSize`: did you mean `size`?) or ones that do not apply
there (`rows` on a text frame, `leading` on a run). Fix them: each is a design decision that did not happen. An
undeclared colour or style, or a disallowed value (`"fit": "cover"`), is an error naming the allowed values.

## Units and coordinates

- `unit` (top level): `pt` (default), `mm`, `cm` or `in`, for page sizes, margins, bleed, gutters, item positions
  and sizes, `x2`/`y2`, `cornerRadius`, `inset`, `columnWidths`, `rowHeight`, `cellInset`.
- **Always points**: font `size`, `leading`, `spaceBefore`, `spaceAfter`, indents, `strokeWeight`. `tracking`:
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
  "characterStyles": [ ... ],
  "paragraphStyles": [ ... ],
  "pages": [ ... ]
}
```

Applied in this order: colours, character styles, paragraph styles, pages. A colour or style must be declared (in
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

## `colors`

`{ "name": "Brand", "cmyk": [75, 45, 0, 10] }` (percent), or `"rgb": [0, 102, 204]` (0-255), or `"hex": "#0066CC"`;
exactly one. `Black`, `Paper`, `Registration` and `None` always exist.

## `paragraphStyles` and `characterStyles`

`{ "name": "Body", "basedOn": "Base", ...format }`. Paragraph styles take every format field below; character
styles (and runs) only `font`, `fontStyle`, `size`, `color`, `tint`, `underline`, `strikeThrough`, `tracking`,
`caps`, `position`. An existing style name is updated.

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
| `list` | `bullet`, `numbered` or `none`. Paragraphs and paragraph styles only; see [Lists](#lists). |
| `bullet`, `bulletAfter` | The bullet (one character, default `•`) and the text after it (default a tab; `^m` em space, `^>` en space). |
| `numberFormat` | `1` (default), `01`, `001`, `0001`, `a`, `A`, `i` or `I`. |
| `numberText` | The number's text: `^#` is the number, `^1` to `^9` that level's number, `\t` a tab. Default `^#.\t`; also `^#)\t`, `(^#)\t`, `^1.^#\t`. |
| `listLevel` | 1 to 9. A level restarts after any paragraph of a higher level (a lower number). |
| `startAt` | On a paragraph: restart the numbering here, at this number. On a style: the first number of its lists. |

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

By `type`:

- **`text`**: a text frame.
  - `text`: plain text; `"\n"` starts a new paragraph. `style`: the paragraph style for it.
  - `paragraphs`: mixed formatting, after `text`. Each has `text`, `style` (paragraph style), format fields and
    `runs`; each run has `text`, `style` (a **character** style) and character format fields.
  - `columns`, `columnGutter`, `inset` (a margins object), `verticalAlign` (`top`, `center`, `bottom`, `justify`).
  - `continueFrom`: `name` of an earlier text frame whose story continues here (threaded); no text of its own.
- **`rectangle`**, **`ellipse`**: shapes, styled with `fill` and `stroke`.
- **`line`**: from `x`, `y` to `x2`, `y2` (default `x + width`, `y + height`). Black 1 pt unless `stroke`,
  `strokeWeight` say otherwise.
- **`image`**: `path` is the **absolute** path of a JPEG or PNG, linked (not embedded). `fit`: `fill` (default,
  crops), `fit` (whole image), `stretch`, `center` or `original`.
- **`table`**: a text frame holding a table.
  - `rows`: list of rows, each a list of cell texts.
  - `headerRows`, `columnWidths` (in `unit`), `rowHeight` (default 18 pt), `cellInset`.
  - `cellStyle`, `headerStyle`: paragraph styles. `headerFill`: a swatch.
  - `stroke` and `strokeWeight` draw the cell borders; `fill` is the frame's background.

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
`fillTint`, `stroke`, `strokeWeight`, `strokeTint`, `strokeType`, `endCap`, `cornerRadius`, `opacity`, `rotateBy`, and
a text frame's `columns`, `columnGutter`, `inset`, `verticalAlign`. Put several in one `idml_batch`.

## Not supported

Master pages, paragraph rules, drop caps, polygons and free paths, gradients, effects other than opacity, text wrap
(the preview and PDF do not draw the last three). Repeat a header on each page instead of a master; draw rules
with a `line`.
