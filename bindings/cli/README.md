# @coreidml/cli

**The `coreidml` command line and MCP server for Adobe InDesign IDML files, installed from npm: summaries for AI
agents, surgical edits, templates, layout changes, PDF export and page previews. No InDesign, no .NET.**

```sh
npx @coreidml/cli summary brochure.idml --sections pages,stories
npm install -g @coreidml/cli && coreidml check-fit brochure.idml --fonts ./fonts
```

The package holds a small launcher; the program itself is a native binary (Native AOT, with PDFium for page previews)
in a platform package that npm installs next to it as an optional dependency, only the one for your machine:

| Platform | Package |
| --- | --- |
| Windows x64, arm64 | `@coreidml/cli-win32-x64`, `@coreidml/cli-win32-arm64` |
| macOS Apple silicon, Intel | `@coreidml/cli-darwin-arm64`, `@coreidml/cli-darwin-x64` |
| Linux x64, arm64 (glibc 2.28 or later) | `@coreidml/cli-linux-x64`, `@coreidml/cli-linux-arm64` |

The launcher passes arguments, standard input and output, the exit code and signals straight through. If npm skipped
optional dependencies (`--omit=optional`, or a lockfile made on another platform), it says so and exits with 1.

## As an MCP server

`coreidml mcp` serves 33 tools to Claude and other MCP clients over stdio. Pin the version so every start runs the
same build:

```json
{ "mcpServers": { "coreidml": { "command": "npx", "args": ["-y", "@coreidml/cli@<version>", "mcp"] } } }
```

In Claude Code and Cowork, the CoreIdml plugin sets this up for you:

```text
/plugin marketplace add eduardpaul/CoreIdml
/plugin install coreidml@coreidml
```

The program runs on your machine and works on the files you name; it makes no network connections. npx downloads
the package from the npm registry the first time.

## Documentation

The commands, the MCP tools and their options are in the
[command line's README](https://github.com/eduardpaul/CoreIdml/blob/main/src/CoreIdml.Cli/README.md). The same
program is also a .NET tool (`dotnet tool install -g CoreIdml.Cli`), a download from the
[releases](https://github.com/eduardpaul/CoreIdml/releases), and a Claude Desktop extension.

MIT licensed. PDFium is BSD-3-Clause/Apache-2.0. CoreIdml is an independent project and is not affiliated with Adobe.
