# coreidml

**A command line and an MCP server for Adobe InDesign IDML files: summaries for AI agents, surgical edits,
templates, layout changes, PDF export and page previews. No InDesign needed.**

```sh
dotnet tool install -g CoreIdml.Cli          # needs the .NET 11 runtime
```

Or download a single native binary (no .NET needed) for Windows, Linux or macOS from the
[releases](https://github.com/eduardpaul/CoreIdml/releases): `coreidml` plus the PDFium library it uses for previews.

```sh
coreidml summary brochure.idml --pages 0 --sections pages,stories --max-text 80
coreidml find brochure.idml --tag headline
coreidml set-text brochure.idml u188 0 "New headline" -o edited.idml
coreidml check-fit edited.idml --fonts ./fonts                  # exact overset: lines and points per story
coreidml render edited.idml --page 0 --dpi 100 -o page1.png     # page preview
coreidml to-pdf edited.idml -o edited.pdf --fonts ./fonts --max-dpi 150
coreidml batch brochure.idml ops.json -o edited.idml --dry-run  # several edits, all or nothing
coreidml merge cover.idml sheet.idml sheet-2.idml@0,2 -o book.idml
cat brochure.idml | coreidml set-text - u188 0 "Hello" -o - > edited.idml   # '-' is stdin or stdout
```

Output is JSON in UTF-8 on every platform (`--pretty` indents it). Exit codes: 0 ok, 1 failed, 2 invalid input or
usage, 3 not found.

**Edits never overwrite silently.** Every editing command needs `-o <file>` (a new file) or `--in-place` (written
atomically through a temporary file). `--dry-run` reports the result, the text and link diff and the package parts
that would change, and saves nothing.

## Commands

| Area | Commands |
| --- | --- |
| Read | `summary`, `story`, `find`, `validate`, `structure`, `export-xml`, `diff`, `fonts` |
| Text and data | `set-text`, `replace`, `fill`, `relink`, `relink-folder`, `import-xml`, `apply`, `merge` |
| Layout | `edit-item` (move, resize, layer, name), `delete-item`, `duplicate-item`, `add-page`, `delete-page`, `move-page`, `apply-style` |
| Several at once | `batch`: edits in order on one document; all or nothing; `--dry-run` returns the diff |
| PDF and files | `to-pdf`, `from-pdf`, `render` (PNG preview), `check-fit`, `package` (the .idml with its links, like InDesign's Package), `create` |

`coreidml tools` lists the 33 tools behind the commands, and `coreidml call <tool> '<json>'` runs any of them with
JSON arguments (the tools that define styles and colours or edit tables are reached this way).

## MCP server

`coreidml mcp` serves the same tools to Claude and other MCP clients (JSON-RPC over stdio; protocol 2025-11-25 down
to 2024-11-05, negotiated):

```json
{ "mcpServers": { "idml": { "command": "coreidml", "args": ["mcp", "--root", "/path/to/documents"] } } }
```

- Tools have titles, `readOnlyHint`, `destructiveHint` and `idempotentHint` annotations (clients can auto-approve the
  read-only ones), `outputSchema` and `structuredContent` for their JSON results.
- Server instructions describe the usual workflow: summarize, find or get a story, edit, check the fit, validate,
  preview. Three prompts are included: `fill_template`, `review_document`, `adjust_layout`.
- Editing tools take `outPath`, `inPlace: true` or `dryRun: true`, like the command line.
- **Paths must be absolute.** A relative path is refused with the path it would have resolved to, unless the server
  runs with `--root <dir>` or the client announces MCP roots (the first one is then the base).

### Claude Desktop

Each release has a one-click extension (an [MCP Bundle](https://github.com/modelcontextprotocol/mcpb)) per platform:
`coreidml-<version>-win-x64.mcpb`, `-win-arm64`, `-osx-arm64`, `-osx-x64`, `-linux-x64` or `-linux-arm64`. In Claude Desktop open
Settings, Extensions and drop the file in (or Advanced settings, Install Extension). It carries the binary and
PDFium; there is nothing to configure. Give Claude files by their full path. The extension is installed from the
releases: Claude's directory no longer lists MCP Bundles.

## Page previews

Previews (`render`, `idml_render_page`) are drawn by [PDFium](https://pdfium.googlesource.com/pdfium/), Chromium's
PDF renderer, which ships next to the binary and inside the .NET tool. Without it, `render` exports the page to PDF
and says why. Nothing else depends on it.

MIT licensed. PDFium is BSD-3-Clause/Apache-2.0. CoreIdml is an independent project and is not affiliated with Adobe.
Library, other languages and source: [github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).
