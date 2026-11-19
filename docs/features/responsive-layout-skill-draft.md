---
name: idml-variants
description: Make an Adobe InDesign document (.idml) responsive and produce variants from one master - other page sizes and aspect ratios (A4/Letter, square and 16:9 social, banners) with Liquid Layout rules, object constraints, auto-size frames and Flex Layout containers, and localized or market editions with conditional text, condition sets and translation round trips. Use when the user wants one InDesign master in several formats, sizes or languages, mentions liquid layout, alternate layouts, flex layout, adjust layout, conditional text, condition sets, editions, versioning, localization or translation of an IDML document.
---

<!--
DRAFT. Ships as plugins/coreidml/skills/idml-variants/SKILL.md in phase 5 of responsive-layout-plan.md.
It uses tools that do not exist yet: idml_layout_rules, idml_set_layout_rules, idml_resize, idml_flex,
idml_conditions, idml_edition, idml_export_strings, idml_import_strings, idml_pseudo_localize, and the prompts
make_responsive and make_variants. Adjust names and arguments to what ships.
-->

# Variants from one InDesign master

Follow the `idml-documents` skill as well: absolute paths, never overwrite the user's file, batches with a dry run,
check the fit, validate and look before reporting. This skill adds what variants need.

## How it works, and what to tell the user

An IDML master stores **rules**, not results: page rules and pins (Liquid Layout), auto-size frames, Flex Layout
containers, and conditions that hide or show text. CoreIdml applies these rules to make each size or edition. Its
result **approximates** what InDesign would do, so every variant gets a fit check, a preview and a designer's review.
For print, the `.idml` variants are the deliverables: the user opens them in InDesign and exports from there.

Choose the mechanism by the size of the difference:
- **another size or aspect ratio:** layout rules and a resize;
- **small text differences** in the same layout (prices, units, legal lines, a short bilingual label): conditional
  text and condition sets;
- **a full translation:** a separate edition, through a strings round trip, with frames that can grow.

## 1. Look at the master

1. `idml_layout_rules` on the master. Most real files have no rules at all (page rule `Off`, no pins, no auto-size):
   say so. A resize of such a file only moves things proportionally, and text does not adapt.
2. `idml_summarize` and `idml_render_page` on representative pages (cover, body, back), at 100 dpi or less, to see
   what each item is: background, header band, logo, headline, body, image, footer.

## 2. Make the master responsive (with the user)

Propose rules per page and per kind of item, in plain terms, and apply them only after the user agrees:

- **Backgrounds and bleed images:** pin all four edges and resize both ways (they fill any page).
- **Header and footer bands:** pin to the top (or bottom), left and right; resize the width, keep the height.
- **Logos and folios:** pin to their corner, keep their size.
- **Headlines and body text:** pin to the side margins, resize the width; auto-size the height (`HeightOnly`, from
  the top) so text can grow; flexible columns for long text.
- **Images in the content:** resize with the frame, fitting set to fill proportionally and centre.
- **Rows of cards, logos or specs:** a Flex container (row, wrap, gap) when the file uses Flex; otherwise pins and
  even spacing.

Write them with `idml_set_layout_rules` in one batch with `dryRun: true`. Then test them before producing anything:
`idml_resize` with `dryRun: true` to the most different target (portrait to landscape). Read the report (items that
left the page, new overlaps, frames that overflow), fix the rules, and repeat.

Rules are a design decision. When a layout cannot work in a very different aspect ratio (an A4 poster as a 16:9
banner), say so, and propose a separate master for that format rather than forcing it.

## 3. Produce the sizes

1. `idml_resize` once per target size, to a new file per size (`flyer-letter.idml`, `flyer-1080x1080.idml`), or with
   `asAlternateLayout: true` when the user wants all sizes in one document.
2. For each variant: `idml_check_fit`, then fix overflows (auto-size, a smaller style for that size, or shorter copy
   after asking), `idml_validate`, and `idml_render_page` on every page. Look at each preview before describing it.
3. Pixel targets (social, screens) are converted at 72 ppi (1080 px = 1080 pt), and the PDF or PNG export is at the
   size the channel needs.

## 4. Produce the editions

- **Conditions:** `idml_conditions` to create the conditions (one per market or language) and apply them to the text
  ranges that differ; define one condition set per edition. Remember the rule: text with several conditions is
  shown when **any** of them is visible. Then `idml_edition` per set (and `flatten: true` when the receiver should
  not get hidden text). Check the fit **per edition**: each one reflows differently.
- **Translations:** run `idml_pseudo_localize` on the master first (30 % longer text) and fix what overflows, before
  any translator starts. Then `idml_export_strings` (XLIFF for a translation vendor, JSON otherwise), and later
  `idml_import_strings` with the translation into a new file per language. Import sets each run's language. Then
  fit check, fixes, previews, as for sizes. Fonts must cover the language's characters: `idml_fonts` reports missing
  glyphs.
- **Right-to-left and complex scripts** (Arabic, Hebrew, Hindi, Thai …): the IDML can be produced, but CoreIdml's
  previews and fit checks are not reliable for them. Say so, and ask the user to check them in InDesign with the
  World-Ready composer.

For a matrix (sizes × editions), the `make_variants` prompt runs the whole sequence. Produce one variant completely
and get it approved before making the rest.

## Reporting back

- A table of every file written (size, edition, path), and that the master is untouched, or that rules were added to
  a copy of it.
- Per variant: what did not fit and how it was fixed, items that moved off the page, and anything that needs a
  designer's eye.
- What CoreIdml cannot do here (snapshots, linked story updates, complex scripts), and that InDesign's own *Adjust
  Layout* may differ in details.
