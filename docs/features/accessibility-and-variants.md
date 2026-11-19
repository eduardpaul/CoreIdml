# Plans: accessibility review and responsive variants

Status: **planned, not started.** Two new plugin skills, and the library and tool work they need.

| Feature | Detailed plan | Skill draft |
| --- | --- | --- |
| Accessibility review (Section 508, WCAG, PDF/UA) | [accessibility-plan.md](accessibility-plan.md) | [accessibility-skill-draft.md](accessibility-skill-draft.md) (`idml-accessibility`) |
| Responsive layouts and editions (Liquid rules, Flex Layout, conditional text, localization) | [responsive-layout-plan.md](responsive-layout-plan.md) | [responsive-layout-skill-draft.md](responsive-layout-skill-draft.md) (`idml-variants`) |

## Shared first step

- One InDesign 22.0 session making test files for both features: accessibility settings, layout rules, conditions
  and Flex Layout, saved as IDML, plus InDesign's own exported PDFs and resized versions to compare against.

## Accessibility (508 / WCAG)

1. Confirm the exact IDML format for heading tags, Articles (reading order) and alt text, using the test files.
2. Library: read and write alt text, decorative marking, heading tags, reading order, title, language, table header
   rows, and anchoring figures into the text.
3. Checker: about 17 rules (missing alt text, no headings, wrong reading order, tables without headers, wrong
   language, low contrast…), each tied to a WCAG criterion.
4. MCP tools: check, set alt text, set heading tags, set reading order, anchor item, set title and language, table
   header rows, a page preview showing the reading order.
5. PDF check: verify the PDF InDesign exports.
6. Optional, large: CoreIdml writes tagged, accessible PDFs itself.
7. Ship the `idml-accessibility` skill, a demo sample and docs.

## Responsive layouts and editions

1. Fix first, useful on their own:
   - hidden conditional text is currently drawn and measured;
   - frames inside a Flex Layout container may be invisible to CoreIdml;
   - fit checks ignore auto-size frames.
2. Confirm the IDML format for Flex Layout, linked stories and snapshots, using the test files.
3. Library: read and write page rules, pins, auto-size, image fitting, alternate layouts, conditions and condition
   sets, and Flex containers.
4. Engines:
   - resize a page or document following its rules;
   - build variants as separate files or alternate layouts;
   - switch and flatten editions;
   - export and import text for translation;
   - pseudo-localize to test longer text;
   - later: the Flex layout solver.
5. MCP tools: read rules, set rules, resize, conditions, editions, text export and import, pseudo-localize, Flex.
6. Ship the `idml-variants` skill, a demo sample and docs.

## Suggested order

- Shared test files → responsive fixes → both libraries → accessibility checker and tools → resize and editions →
  skills.
- Leave until later: tagged PDF written by CoreIdml, the Flex solver, and right-to-left / complex scripts.

## Open decisions

- Accessibility: who exports the final PDF (InDesign only, or CoreIdml too); target (WCAG 2.0 AA via 508, or
  2.1/2.2 AA too; PDF/UA-1 or 2); EPUB export tags now or later.
- Variants: separate files, alternate layouts, or both; conditions, translation round trip, or both (XLIFF or
  JSON); Flex read-and-draw only or the full solver; complex scripts in scope.
- Both: who makes the InDesign test files.

The skill drafts reference tools that do not exist yet; they move to `plugins/coreidml/skills/` in the last phase
of each plan.
