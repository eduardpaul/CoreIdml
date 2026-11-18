---
name: idml-documents
description: Work on Adobe InDesign documents saved as IDML (.idml) with the CoreIdml tools - read them, fill templates, change text and layout, find overset text, validate, preview pages and export PDF. Use when the user mentions an .idml file, an InDesign document or template (brochure, datasheet, catalog, flyer, magazine page), text that does not fit its frame, or turning a layout into a PDF or a PDF back into an editable layout.
---

# Working on IDML files with CoreIdml

The `coreidml` MCP server (tools named `idml_*`) sends its own short workflow when it starts. This skill adds what
keeps the user's files safe and the results checkable. Follow both.

## Before the first call

- **Absolute paths only.** Every `path`, `outPath` and folder argument must be absolute. Turn what the user gives
  ("brochure.idml", "the template in docs/") into a full path from the working directory before calling a tool. A
  relative path is refused with the path it would have meant; use that path if it is right.
- **IDML, not INDD.** CoreIdml reads `.idml`, InDesign's interchange format, not the native `.indd`. If the user only
  has an `.indd`, ask them to save a copy from InDesign with File > Save As > InDesign CS4 or later (IDML). A PDF can
  be rebuilt as an editable document with `idml_from_pdf`, which is an approximation.
- **Fonts decide line breaks.** `idml_check_fit`, `idml_render_page` and `idml_to_pdf` compose text with real fonts.
  When the document's fonts are not installed, `idml_fonts` lists them as missing: ask where the fonts are and pass
  that folder as `fonts` (or `fontDirectories` for `idml_package`), and say that results with substituted fonts can
  differ from InDesign.

## Making changes

1. **Look first.** `idml_summarize` with `pages` and `sections` on anything longer than a few pages, or `idml_find`
   by frame name, script label, XML tag or text. Use the ids the tools return (stories such as `u188`, items, links);
   never guess one.
2. **Read before writing text.** `idml_get_story` gives the run indices that `idml_set_text` and `idml_apply_style`
   need. `idml_set_text` keeps the run's formatting; `idml_replace_text` changes every story; `idml_fill` fills a
   template by frame names, labels, tags and `{{placeholders}}`.
3. **Never overwrite the user's file.** Write to a new file next to the original (`brochure.idml` becomes
   `brochure-edited.idml`, or a name the user chose) and make later edits on that new file. Use `inPlace: true` only
   when the user asked for the original to change.
4. **Several edits: one batch, dry run first.** Put related edits in one `idml_batch` (all or nothing). For layout
   changes or anything the user did not spell out, run it with `dryRun: true`, tell the user what would change, then
   run it for real.
5. **Check the fit after every text change.** `idml_check_fit` on the new file. For each story that overflows, either
   shorten the text (ask the user before cutting their copy) or enlarge the frame with `idml_edit_item`, then check
   again until nothing overflows or the user accepts it.
6. **Validate.** `idml_validate` on the final file; report errors, and warnings that matter.
7. **Look at the result.** `idml_render_page` on each page you changed, at 100 dpi or less, and look at the image
   before you describe it. Export with `idml_to_pdf` when the user wants a PDF, and pass on its report of missing
   fonts, missing images and overset text.

## Reporting back

- The full path of every file you wrote, and that the original is untouched (or that it changed, if asked).
- What changed, from the tools' own diff, and what still does not fit, is missing or could not be done.
- CoreIdml's PDF is an approximation of InDesign's (greedy line breaking; no gradients, effects or text wrap). For
  print, the `.idml` is the deliverable: the user opens it in InDesign and exports from there.

## Ready-made tasks

The server also has three prompts that run the whole sequence: `fill_template`, `review_document` (reports problems
without changing the file) and `adjust_layout`. In Claude Code the user finds them among the slash commands, under
the coreidml server's name; mention them when a request matches one.
