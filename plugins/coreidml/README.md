# CoreIdml for Claude

Work on Adobe InDesign documents saved as IDML with Claude, without InDesign: read and summarize them, fill templates
with data, change text, styles, frames and pages, find text that does not fit its frame, validate the file, look at
page previews and export PDFs. Every change goes to a new file unless you ask for the original to change.

The plugin adds two things:

- **The CoreIdml MCP server**, with 33 tools (`idml_summarize`, `idml_fill`, `idml_set_text`, `idml_edit_item`,
  `idml_check_fit`, `idml_validate`, `idml_render_page`, `idml_to_pdf` and more) and three prompts (`fill_template`,
  `review_document`, `adjust_layout`). It is the `coreidml` command line, which runs on your computer.
- **A skill**, `idml-documents`, that tells Claude how to use those tools safely: absolute paths, never overwriting
  your file, dry runs before larger edits, checking the fit, validating and looking at the result before reporting.

## Install

In Claude Code:

```text
/plugin marketplace add eduardpaul/CoreIdml
/plugin install coreidml@coreidml
```

You need [Node.js](https://nodejs.org) 18 or later, because the server is started with `npx`. It runs on Windows
(x64, arm64), macOS (Apple silicon, Intel) and Linux (x64, arm64, glibc 2.28 or later).

## Use it

Give Claude the path of an `.idml` file and say what you want, for example:

- "Summarize ~/work/brochure.idml and tell me which frames overflow."
- "Fill templates/datasheet.idml with the data in x200.json and export a PDF."
- "Move the logo on page 2 of catalog.idml 10 mm to the right and make the headline frame taller."

Claude answers with the path of each file it wrote, what changed, and what still needs attention. For InDesign's
native `.indd` files, save a copy as IDML from InDesign first (File > Save As > InDesign CS4 or later (IDML)).

## Where it works

The server is a local MCP server: Claude starts it as a program on your computer. It loads in **Claude Code** and in
**Cowork** sessions that run on your computer. Chat on claude.ai and in the Claude apps does not start local
servers, so there the plugin adds only the skill. For Claude Desktop chat, install the CoreIdml Claude Desktop
extension instead: the `coreidml-<version>-<platform>.mcpb` file from the
[releases](https://github.com/eduardpaul/CoreIdml/releases) (Settings, Extensions, drop the file in).

## What it runs, reads, writes and sends

- **What it runs.** Claude Code starts `npx -y @coreidml/cli@<version> mcp`, with the exact version pinned in this
  plugin's `.mcp.json`. The first time, and after each plugin update, npx downloads the package `@coreidml/cli` and
  one platform package with the native program for your system (`@coreidml/cli-<platform>-<arch>`, about 10 MB) from
  the npm registry (registry.npmjs.org, or the registry your npm is configured to use) and keeps them in npm's cache.
  `@coreidml/cli` is a short launcher script that starts that program. Source: this repository
  ([launcher](https://github.com/eduardpaul/CoreIdml/tree/main/bindings/cli),
  [program](https://github.com/eduardpaul/CoreIdml/tree/main/src/CoreIdml.Cli)).
- **What it reads.** The files and folders that you or Claude name in a tool call, the images those documents link
  to, and the fonts installed on your computer (to measure and draw text, unless a tool call turns that off).
- **What it writes.** Only the output files named in a tool call (a new `.idml`, a PDF, a PNG preview, a package
  zip), and temporary files in your system's temporary folder while it renders a preview.
- **What it sends.** Nothing. The server makes no network connections; its results go only to Claude, over the
  standard input and output of the process Claude Code started. Apart from the npm download above, the plugin uses
  no network.

## License and source

MIT; see [LICENSE](LICENSE). The native program includes PDFium (BSD-3-Clause and Apache-2.0) for page previews.
Source, documentation and issues: [github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).
CoreIdml is an independent project and is not affiliated with Adobe. Adobe and InDesign are trademarks of Adobe.
