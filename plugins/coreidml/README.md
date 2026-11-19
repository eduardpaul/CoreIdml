# CoreIdml for Claude

Work on Adobe InDesign documents saved as IDML with Claude, without InDesign: read and summarize them, fill templates
with data, change text, styles, frames and pages, find text that does not fit its frame, validate the file, look at
page previews and export PDFs. Every change goes to a new file unless you ask for the original to change.

The plugin adds two things:

- **The CoreIdml MCP server**, with 34 tools (`idml_summarize`, `idml_fill`, `idml_set_text`, `idml_edit_item`,
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

Give Claude the path of an `.idml` file and say what you want. Claude answers with the path of each file it wrote,
what changed, and what still needs attention. For InDesign's native `.indd` files, save a copy as IDML from InDesign
first (File > Save As > InDesign CS4 or later (IDML)).

### Try it on the sample files

The [samples](samples) folder has two small documents to try the plugin on, made with CoreIdml for this purpose:

- `brochure.idml`: a two-page A4 brochure. On page 1, the body text frame (`intro`) is too short for its text.
- `datasheet-template.idml` and `datasheet-data.json`: an A5 product data sheet template with four fields, and the
  data to fill it.

Get them with `git clone https://github.com/eduardpaul/CoreIdml`, start Claude Code in `CoreIdml/plugins/coreidml`,
and ask:

1. "Summarize samples/brochure.idml and tell me which text frames overflow."
   Claude describes the two pages and reports that the `intro` frame on page 1 overflows by 3 lines (about 42 pt).
2. "Make the intro frame in samples/brochure.idml tall enough for its text, save the result as
   samples/brochure-fixed.idml, check that nothing overflows any more, and show me page 1."
   Claude makes the frame taller in a new file, checks the fit, and shows a preview of page 1.
3. "Fill samples/datasheet-template.idml with the data in samples/datasheet-data.json and export it as a PDF."
   Claude fills the four fields (`product`, `price`, `description` and the `{{date}}` placeholder), reports none
   missing, and writes the filled `.idml` and its PDF.

The original sample files are left as they are.

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

## Troubleshooting

- **The `coreidml` server does not connect** (`/mcp` shows it as failed). Check that `node --version` prints 18 or
  later. Then run the command Claude Code runs, in a terminal: `npx -y @coreidml/cli@<version> tools`, with the
  version from this plugin's `.mcp.json`. It should list 34 tools; if it does not, the error it prints says why
  (no network or a blocked npm registry, a proxy that npm is not set up for, or an unsupported system).
- **"The package @coreidml/cli-…, which holds the program for this machine, is not installed".** The native program
  exists for Windows, macOS and Linux with glibc on x64 and arm64. Alpine and other musl-based Linux systems are not supported. If npm is set to skip optional
  dependencies (`omit=optional` in `.npmrc`), it does not download the program; remove that setting.
- **The first start times out.** The first start downloads about 10 MB. If your connection is slow, run the `npx`
  command above once to fill npm's cache, or start Claude Code with a longer server start timeout, for example
  `MCP_TIMEOUT=60000 claude`.
- **"File not found".** Give the full path of the file, or a path relative to the folder you started Claude Code in.
- **Wrong fonts in previews and PDFs.** CoreIdml uses the fonts installed on your computer and substitutes missing
  ones; the tool results list which fonts are missing. Install them, or tell Claude the folder they are in.
- **Missing images.** Linked images are looked up where the document says they are. Tell Claude the folder they are
  in now, and it can relink them or use that folder for previews and PDFs.

If none of this helps, [open an issue](https://github.com/eduardpaul/CoreIdml/issues) with what you asked, what
Claude or the tool answered, and your system. Report security problems privately, as [SECURITY.md](../../SECURITY.md)
explains.

## License and source

MIT; see [LICENSE](LICENSE). The native program includes PDFium (BSD-3-Clause and Apache-2.0) for page previews.
Source, documentation and issues: [github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).
CoreIdml is an independent project and is not affiliated with Adobe. Adobe and InDesign are trademarks of Adobe.
