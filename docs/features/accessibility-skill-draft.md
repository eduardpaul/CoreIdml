---
name: idml-accessibility
description: Review and fix the accessibility of Adobe InDesign documents saved as IDML (.idml) so the tagged PDF exported from them passes Section 508, WCAG 2.x AA and PDF/UA screen-reader checks - alt text, decorative artifacts, reading order (Articles, inline anchors), heading and export tags, table header rows, document language and title, contrast. Use when the user mentions accessibility, 508, WCAG, ADA, PDF/UA, screen readers, alt text, tagged PDF, reading order or remediation of an InDesign/IDML document or of the PDF made from it.
---

<!--
DRAFT. Ships as plugins/coreidml/skills/idml-accessibility/SKILL.md in phase 5 of accessibility-plan.md.
It uses tools that do not exist yet: idml_accessibility_check, idml_set_alt_text, idml_set_export_tag,
idml_reading_order, idml_set_reading_order, idml_anchor_item, idml_set_metadata, idml_set_language,
idml_check_pdf, the setHeaderRows/setFooterRows ops of idml_edit_table and the overlays of idml_render_page.
Adjust names and arguments to what ships.
-->


# Accessibility review of IDML documents

Follow the `idml-documents` skill as well: absolute paths, never overwrite the user's file, batches with a dry run,
validate and look before reporting. This skill adds what accessibility needs.

## What the IDML can and cannot do

The IDML does not contain the tagged PDF. It holds the **inputs** InDesign uses to tag the PDF when the user exports
with *Create Tagged PDF*: alt text and artifact flags on objects, export tags on paragraph styles, Articles for
reading order, anchored objects, table header rows, languages and the title. Fix those here and the export comes out
right. Two things to tell the user plainly:

- **Export settings are not in the file.** The user (or InDesign Server) must export with the settings in "Handing
  over" below.
- **Complex tables cannot be finished in InDesign.** InDesign knows header *rows* only: no row headers, scopes or
  header-cell associations. Report such tables and say they need the Acrobat table editor or a remediation tool after
  export (or CoreIdml's tagged export, when the user exports with CoreIdml).

## 1. Check

1. `idml_accessibility_check` on the file, with `includeReadingOrder: true`. On long documents run it per page range.
2. `idml_render_page` with `overlay: "readingOrder"` on the pages with reading-order or figure issues, at 100 dpi or
   less, and look at it: the badges show the order a screen reader will follow.
3. Present the report grouped by what the user must decide, most serious first. Name the WCAG criterion of each
   issue (1.1.1 alt text, 1.3.1 structure, 1.3.2 order, 3.1.x language, 2.4.2 title, 1.4.3 contrast) and say whether
   it is fixable here, needs the user's judgement, or needs post-processing after export.

When the user only asked for a review, stop here and do not change the file.

## 2. Decide with the user, never alone

These are content decisions. Propose, then ask; apply only what the user confirmed.

- **Alt text.** Look at the image (render its page, crop to it if needed) and the text around it, and draft alt text
  that conveys its *purpose* in this document: under ~150 characters, no "image of", not the file name, not a copy of
  the caption. Charts and diagrams need the data or the conclusion: ask for the source data when you cannot read the
  values from the image. Never invent facts the image does not show.
- **Decorative or meaningful.** Background photos, ornaments, rules and brand bands are usually decorative
  (`decorative: true` makes them artifacts). Logos are meaningful (alt text: the organisation's name). When in doubt,
  ask.
- **Heading levels.** Propose a map from the document's styles to H1–H6 (one H1 per document or chapter, no skipped
  levels), from the style names and sizes the check reports. Running heads and folios typed on document pages map
  to Artifact.
- **Reading order.** Show the predicted order with the overlay, and the order you propose (columns top to bottom,
  sidebars after the main text they belong to, captions right after their figure). Confirm before writing Articles.
- **Languages.** Confirm the document language, and that a suspicious language (a few ranges in an unrelated
  language) is a mistake and not a real quotation.
- **Rewriting text** (link text, removing typed bullets or empty paragraphs) changes the user's copy: ask first.

## 3. Fix

Write to a new file (`brochure.idml` → `brochure-accessible.idml`) and put related fixes in one `idml_batch` with
`dryRun: true` first. In this order, because later checks depend on earlier fixes:

1. **Title and language:** `idml_set_metadata` (title, document language); `idml_set_language` for wrong ranges.
2. **Headings and semantics:** `idml_set_export_tag` per style. Where headings are formatted by hand on body
   styles, apply the heading styles first (`idml_apply_style`); an export tag on an unused style changes nothing.
3. **Alt text and artifacts:** `idml_set_alt_text` with `altText`, or `decorative: true`. It sets the source to
   custom, which InDesign needs for the text to be used.
4. **Figures in the flow:** `idml_anchor_item` to anchor each figure (and its caption) in the story, after the
   paragraph that introduces it, where the layout allows. Anchoring changes the layout: dry run, render, confirm.
5. **Reading order:** `idml_set_reading_order` with Articles for what is still out of order (it turns on *Use for
   reading order in tagged PDF*). Include every page item with content; leave out artifacts.
6. **Tables:** `idml_edit_table` with `setHeaderRows` (and `setFooterRows`) on data tables. Leave layout tables alone
   and report them.
7. **Contrast:** report failing colours with their ratios and the nearest passing tint; change swatches only when the
   user agrees (it is a brand decision).

Then `idml_check_fit` (anchoring and restyling move text), `idml_validate`, `idml_accessibility_check` again until
only accepted or post-processing items remain, and `idml_render_page` with both overlays on the changed pages.

## 4. Handing over

Tell the user to export from InDesign with *File > Export > Adobe PDF (Print or Interactive)* and these settings:
*Create Tagged PDF* on, *Use Structure for Tab Order* on, *Hyperlinks* and *Bookmarks* on, *Create Acrobat Layers*
off, *Advanced > Display Title: Document Title*, *Advanced > Language:* the document language.

When the user has the exported PDF, run `idml_check_pdf` on it (with the `.idml` for comparison) and report what the
export lost. Say that this check is a subset: sign-off needs a full PDF/UA validator (PAC, veraPDF or Acrobat's
Accessibility Full Check) and a pass with a screen reader (NVDA, JAWS or VoiceOver), plus the manual work on complex
tables.

## Reporting back

- The path of every file written, and that the original is untouched.
- What was fixed, by WCAG criterion, and the decisions the user made.
- What remains, grouped as: needs the user's input; needs post-processing after export (complex tables, anything
  InDesign cannot express); needs a human check (alt text quality, reading order on pages you did not render).
- Never claim the document is "508 compliant" or "PDF/UA conformant": CoreIdml's checks are necessary, not
  sufficient. Say which validator and test confirm it.
