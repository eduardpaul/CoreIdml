---
name: idml-documents
description: Work on Adobe InDesign documents saved as IDML (.idml) with the CoreIdml tools - read them, fill templates, change text and layout, create new layouts from scratch, find overset text, validate, preview pages and export PDF. Use when the user mentions an .idml file, an InDesign document or template (brochure, datasheet, catalog, flyer, magazine page), text that does not fit its frame, or turning a layout into a PDF or a PDF back into an editable layout.
---

# Working on IDML files with CoreIdml

The `coreidml` MCP server (`idml_*` tools) sends its own workflow. This skill adds the rules that keep the user's
files safe and the results checked. Follow both.

## Before the first call

- **Absolute paths only** for every `path`, `outPath` and folder argument: resolve what the user gives against the
  working directory. A refused relative path comes back with the path it would have meant.
- **IDML, not INDD.** For an `.indd`, ask the user to save a copy from InDesign (File > Save As > InDesign CS4 or
  later (IDML)). `idml_from_pdf` rebuilds a PDF as an approximate editable document.
- **Fonts decide line breaks** in `idml_check_fit`, `idml_render_page` and `idml_to_pdf`. If `idml_fonts` lists the
  document's fonts as missing, ask where they are, pass that folder as `fonts` (`fontDirectories` for
  `idml_package`), and say that results with substituted fonts can differ from InDesign.

## Making changes

1. **Look first**: `idml_summarize` with `pages` and `sections` on long documents, or `idml_find`. Use the ids the
   tools return (stories such as `u188`, items, links); never guess one.
2. **Read before writing text**: `idml_get_story` gives the run indices for `idml_set_text` and `idml_apply_style`.
   `idml_set_text` keeps the run's formatting; `idml_replace_text` changes every story; `idml_fill` fills a template
   by frame names, labels, tags and `{{placeholders}}`.
3. **Never overwrite the user's file.** Write to a new file next to it (`brochure.idml` -> `brochure-edited.idml`,
   or the user's name) and make later edits on that file. `inPlace: true` only when the user asked for it.
4. **Several edits: one `idml_batch`** (all or nothing). For layout changes or anything the user did not spell out,
   run it with `dryRun: true` first, tell the user what would change, then run it for real.
5. **Check the fit after every text change**: `idml_check_fit` on the new file. Per overflowing story, shorten the
   text (ask the user before cutting their copy) or enlarge the frame with `idml_edit_item`; recheck until nothing
   overflows or the user accepts it.
6. **Validate** with `idml_validate`; report errors and warnings that matter.
7. **Look at the result**: `idml_render_page` on each changed page at 100 dpi or less; look at the image before
   describing it. For a PDF, `idml_to_pdf`, and pass on its report (missing fonts and images, overset text).

## Creating a document

`idml_create` builds a document from a **document spec**; `idml_apply` adds colours, styles, pages or items to an
existing one. Read [references/document-spec.md](references/document-spec.md) before writing a spec.

Where tools load on demand (Claude Code), load together: `idml_create`, `idml_list_fonts`, `idml_render_page`,
`idml_check_fit`, `idml_edit_item`, `idml_batch`, `idml_validate`, `idml_to_pdf` (and `idml_apply`).

1. **Fonts that exist**: take `font` and `fontStyle` from `idml_list_fonts` (filter with `query`); others are
   substituted.
2. **Create** with `"checkFit": true`. Fix every entry of `warnings` (ignored fields, with the one probably meant);
   `fit` names frames whose text does not fit. Keep the returned item ids and names.
3. **Look at every page** with `idml_render_page` (100 dpi or less): overlaps, alignment, colours, white space.
4. **Fix in place**: one `idml_batch` of `idml_edit_item` calls (geometry, fill, stroke, tints, corners, opacity,
   rotation, text wrap, text frame columns, inset, vertical alignment, auto-size). Resend the spec only for
   structural changes. Render and check the fit again.
5. **Validate** (`idml_validate`), then `idml_to_pdf` if the user wants a PDF.

Common mistakes:

- `unit` (default `pt`; `mm`, `cm`, `in`) covers positions and sizes. Font sizes, leading, spacing, indents and
  stroke weights are always pt, and so is everything `idml_edit_item` takes. `x`/`y` are from the page's top-left,
  y down; margins are not added.
- Declare colours, then character styles, then paragraph styles, before anything uses them.
- `idml_apply` **appends** each page without `index`; `"index": N` (0-based) draws on an existing page.
- Name every frame (`name`): `idml_fill`, `continueFrom`, the fit report and `idml_edit_item` find frames by name.
- Image `path`s are absolute; images are linked, not embedded.
- Shapes have no outline unless `stroke` is given.
- Lists: a paragraph style with `"list": "bullet"` or `"numbered"` and a hanging indent (`leftIndent` 14,
  `firstLineIndent` -14). InDesign draws the bullet or number; never type `•` or `1.` into the text.
- Columns of text (contents pages, price lists): `tabs` on the paragraph style, a `\t` in the text, never runs of
  spaces. A right stop at the frame's inner width with `"leader": "."` makes a table of contents line.
- Headings in threaded or multi-column text: `"keepWithNext": 1` on the heading style, so no heading ends a column
  alone; `"startParagraph": "nextPage"` starts chapters on a new page.
- Typographic touches belong on the paragraph style, not in the text: `dropCapLines` with a `dropCapStyle`, a bold
  lead-in with `nestedStyles` (`{"style": "Lead", "through": "words", "count": 3}`), coloured prices or numbers with
  `grepStyles`, a headline across a multi-column frame with `"spanColumns": 0`, columns that line up with
  `page.baselineGrid` and `"alignToBaselineGrid": true`, rules under headings (`ruleBelow...`), boxed or shaded
  notes (`border`, `shading` with `"shadingTint": 100`). Declare the character styles they name first.
- Repeated looks (cards, captions, sidebars) belong in `objectStyles`, applied with `objectStyle` on items;
  `gradients` are swatches like colours (`fill`, `stroke`, text `color`), turned with `gradientAngle`.

For print: set `page.bleed` (3 mm usual) and extend edge-to-edge backgrounds into it (`x: -3`, `y: -3` in mm, 6 mm
wider and taller than the page); use `cmyk` colours; keep text inside the margins. `idml_to_pdf` with
`includeBleed: true` exports pages with that bleed around the trim (TrimBox), as InDesign's Use Document Bleed Settings.

The preview and PDF engine draws opacity, blend modes, shadows, glows and feathers (not bevel, emboss or satin, and
shadows of text leave the text out); the spec writes only opacity and has no master pages or free paths. Design
without them. When an existing document uses bevel, emboss or satin, say the preview leaves them out and InDesign
will show them (gradients and text wrap in a document are drawn; a wrap that
follows an image's detected edges or Photoshop path is not).

## Reporting back

- The full path of every file written, and that the original is untouched (or changed, if asked).
- What changed (from the tools' diff), and what still does not fit, is missing or could not be done.
- CoreIdml's PDF approximates InDesign's (no bevel, emboss or satin; line breaks can differ). For print, the
  `.idml` is the deliverable: the user exports from InDesign.

## Ready-made tasks

Server prompts `fill_template`, `review_document` (reports, changes nothing) and `adjust_layout` run the whole
sequence; in Claude Code they are slash commands under the coreidml server's name. Mention them when a request
matches.
