# Contributing to CoreIdml

Everything is built from this repository: the .NET library, the native library, the WebAssembly build and the
package of each language. This page is the map.

## How it fits together

```
                    ┌──────────────────────────────────────────────┐
                    │ CoreIdml            the document model       │   BCL only; CoreCLR, Native AOT, Mono/wasm
                    │ CoreIdml.Pdf        PDF export and import    │   managed fonts, codecs, composer; PdfPig
                    └──────────────────────┬───────────────────────┘
                    ┌──────────────────────┴───────────────────────┐
                    │ CoreIdml.Interop    the binding contract     │   handles · status codes · JSON · no throws
                    └───────┬──────────────────────────────┬───────┘
          ┌─────────────────┴──────────┐        ┌──────────┴─────────────────┐
          │ CoreIdml.Native  C ABI     │        │ CoreIdml.Wasm  [JSExport]  │   transports: convert, never decide
          │ Native AOT shared library  │        │ browser-wasm               │
          └──┬──────────┬──────────┬───┘        └──────────┬─────────────────┘
           Node      Python      Dart                  JavaScript
          (koffi)   (ctypes)   (dart:ffi)            (browser, Node)
```

Three rules keep the languages consistent:

1. **Behaviour lives in one place.** `CoreIdml.Interop` is the whole binding contract in managed code: opaque
   handles that are never reused, a status code for every outcome, JSON for structured results, and no exception
   ever leaving it. The transports only convert arguments and results, so the contract is tested with plain
   `dotnet test`.
2. **The C ABI is written once.** `include/coreidml.h` is the source of truth. `tools/CoreIdml.AbiGen` generates
   the low-level table of each binding from it, and `tests/CoreIdml.Abi.Tests` fails if the header, the library's
   exports, the status codes or a generated file disagree. Only the idiomatic layer of each binding is hand-written.
3. **Every binding runs the same scenarios on the same fixtures** (`tests/fixtures`), against the binary that was
   just built.

```
src/CoreIdml             the document model: package, pages, stories, styles, templates, XML, composition, JSON
src/CoreIdml.Pdf         IDML to PDF, PDF to IDML, text measurement
src/CoreIdml.Cli         the coreidml command line and MCP server  -> artifacts/cli/<rid>/
src/CoreIdml.Interop     the binding contract
src/CoreIdml.Native      C ABI transport, Native AOT        -> artifacts/native/<rid>/
src/CoreIdml.Wasm        JavaScript transport, browser-wasm -> artifacts/wasm/_framework/
include/coreidml.h       the C API
tools/CoreIdml.AbiGen    header -> generated binding tables
bindings/                node, wasm, python, dart; cli (the npm launcher of the command line), mcpb
plugins/coreidml         the Claude plugin (Claude Code, Cowork); .claude-plugin/marketplace.json lists it
samples/CoreIdml.Sample  writes demo documents to open in InDesign
samples/flutter_app      a Flutter app with an on-device integration test
benchmarks/              BenchmarkDotNet suites and the golden-output check
tests/                   .NET tests, the C smoke test, fixtures shared by every language
eng/                     validation, Android, versioning and packaging scripts
```

## Building and testing

You need the .NET 11 SDK (`global.json` pins it). Native AOT also needs the platform's C toolchain: the Visual
Studio C++ build tools on Windows, clang on Linux, Xcode on macOS. The WebAssembly build needs no workload.

```sh
dotnet build CoreIdml.slnx -c Release               # warnings are errors
dotnet test --solution CoreIdml.slnx -c Release     # library, binding contract, ABI conformance

dotnet publish src/CoreIdml.Native -c Release -r win-x64    # or linux-x64, linux-arm64, osx-arm64...
dotnet publish src/CoreIdml.Wasm -c Release

python -m unittest discover -s bindings/python/tests
cd bindings/node && npm ci && npm test
cd bindings/wasm && npm ci && npm test && npm run test:browser   # Node, then headless Chrome or Edge
cd bindings/dart/coreidml && dart pub get && dart test
```

One command runs all of it and reports a missing toolchain as skipped rather than passed:

```sh
dotnet run eng/validate.cs
dotnet run eng/validate.cs -- --coreclr    # also the CoreCLR flavor of the WebAssembly build
```

The bindings find the native library on their own: the `COREIDML_LIBRARY` environment variable, then the library
packaged with them, then `artifacts/native/<rid>/` of this checkout.

Android (on Linux or macOS, with NDK r27d):

```sh
bash eng/build-android.sh       # artifacts/native/jniLibs/{arm64-v8a,x86_64}/libcoreidml.so
bash eng/android-smoke.sh       # the C smoke test on the attached device or emulator, once per ABI
```

The Flutter sample and its on-device test: [samples/flutter_app](samples/flutter_app/README.md).

## Adding a function to every language

1. Implement it in `CoreIdml`, with tests.
2. Expose it in `CoreIdml.Interop/IdmlInterop.cs` (a status code back, results through `out`, JSON for
   structures) and test the contract in `CoreIdml.Interop.Tests`.
3. Declare it in `include/coreidml.h`, export it in `CoreIdml.Native/NativeExports.cs`, add it to
   `CoreIdml.Wasm/WasmExports.cs`.
4. `dotnet run --project tools/CoreIdml.AbiGen` regenerates the binding tables.
5. Add the idiomatic method and the shared scenario to each binding, and to its README.

`dotnet test` fails until steps 3 and 4 agree. A parameter of a kind the ABI does not have yet is refused by the
generator until every emitter maps it, so no binding can fall behind silently.

## Benchmarks and the golden check

```sh
dotnet run -c Release --project benchmarks/CoreIdml.Benchmarks -- --filter '*TextMeasurer*' --job short
dotnet run -c Release --project benchmarks/CoreIdml.Benchmarks -- golden /tmp/golden-a   # every output + SHA-256 manifest
```

`golden` writes what every scenario produces (IDML saves described entry by entry, PDFs with their reports, summaries,
text measurements) and a manifest of hashes: run it before and after a change and compare the manifests. A change
that should not alter output must leave every hash the same. Real documents make the check far stronger: point
`COREIDML_SAMPLES_DIR` at a folder of them (they are never committed), and `COREIDML_BENCH_FONTS` at their fonts.

## Ground rules for the code

- The library and the interop layer must run on Mono in the browser. The build enforces what it can: trimming and
  AOT analysis, browser platform analysis (calling an API the browser lacks is a build error), and ordinary async
  instead of runtime async in those assemblies. `Directory.Build.targets` explains why.
- No dependencies outside the BCL in `src/CoreIdml`, and no reflection.
- Status codes are part of the ABI: never renumbered, new ones added at the end.
- Errors quote .NET's own messages, so resource keys stay off in the native and WebAssembly builds.

## Packages and releases

Versions: `Directory.Build.props` (`VersionPrefix`) holds the last release. The manifests of the language packages
repeat it, and so do the Claude plugin's `plugin.json`, its marketplace entry and the `@coreidml/cli@<version>` its
`.mcp.json` pins; `node eng/set-version.mjs --check` fails when they disagree, and CI runs it. Nobody edits them by
hand.

A release is a tag, and nothing else: `git tag v1.2.0 && git push origin v1.2.0` (or `v1.2.0-rc.1` for a
pre-release). The tag must not be older than the version on `main`. `.github/workflows/release.yml` then:

1. builds the native library, the command line and a Python wheel on each platform (Windows x64 and arm64, Linux x64
   and arm64 in AlmaLinux 8 so they need only glibc 2.28, macOS arm64 and x64), the Android libraries and the
   WebAssembly build, and tests each where it was built (C smoke test, the wheel installed in a fresh environment,
   the command line, the JavaScript package in Node and headless Chrome);
2. packs everything once: NuGet packages, the npm packages (`@coreidml/node` and `@coreidml/cli`, each with one
   package per platform, and `@coreidml/wasm`), the wheels, and archives of the native libraries, command lines, Android libraries and
   WebAssembly engine, the Claude Desktop extensions (`eng/pack-mcpb.mjs`: the command line as an MCP Bundle for
   each desktop platform, from `bindings/mcpb/manifest.json`) with a `SHA256SUMS`, and checks that the staged npm
   packages install and run;
3. publishes to NuGet, npm and PyPI, then creates the GitHub release with every file attached;
4. commits the version to `main` ("Release 1.2.0", by `github-actions[bot]`, with `eng/set-version.mjs --release`):
   `Directory.Build.props` and every manifest, so the plugin on `main` pins the `@coreidml/cli` that npm now has.
   Only after npm published it, and not for pre-releases. A push with the workflow's own token starts no workflow,
   so that commit shows no checks; it changes only version numbers.

When the release workflow has finished, `.github/workflows/packages.yml` (**Published packages**) waits until npm,
PyPI and nuget.org serve the new version, then runs the demos of `samples/packages` (Python, .NET, Node.js and the
browser) and the command line from npm and as a .NET tool, with the published packages, on Windows, macOS and
Linux, x64 and arm64. Run it by hand to check any published version.

Every build takes its version from the tag (`-p:Version`, and `eng/set-version.mjs <version>` before packaging), so
what is committed is never used to build a release. If a release fails, `main` is left as it was: fix the cause and
re-run the failed jobs, or push a new tag.

Every push to `main` runs steps 1 and 2 as a dry run (version `<next patch>-ci.<run>`), so the release path is always
tested, and its publish steps are rehearsed against throwaway registries (Verdaccio, pypiserver, a local NuGet feed
and a draft GitHub release that is deleted again).

Every tag creates the GitHub release with all the files. Each registry is published to only once it is switched on
with a repository variable, after its one-time trusted-publishing setup:

- **NuGet**: on nuget.org, add a trusted publishing policy for this repository and `release.yml`, then set the
  repository variable `NUGET_USER` to the nuget.org account name (this switches NuGet on).
- **npm**: the `coreidml` organization owns the `@coreidml` scope. Releases publish through OpenID Connect (trusted
  publishing), with provenance; no token. Trusted publishing is set up **per package** on npmjs.com (package
  settings, Trusted publishing: GitHub Actions, user `eduardpaul`, repository `CoreIdml`, workflow `release.yml`,
  environment `release`, publishing allowed). A package without it fails with nothing more than `ENEEDAUTH`. The
  variable `PUBLISH_NPM` = `true` switches npm on. The package names: `@coreidml/node`, `@coreidml/wasm`,
  `@coreidml/cli`, and the platform packages `@coreidml/node-<platform>` and `@coreidml/cli-<platform>` for
  `win32-x64`, `win32-arm64`, `darwin-arm64`, `darwin-x64`, `linux-x64` and `linux-arm64` (15 in all). npm lets a
  package trust a workflow only once it exists, so a package added later is published once by hand first (`npm
  publish --access public` of its tarball from the release run's `release` artifact, logged in as an owner of the
  scope), then given the trusted publisher.
- **PyPI**: add a (pending) trusted publisher for the project `coreidml`: this repository, workflow `release.yml`,
  environment `release`. Switch it on with the repository variable `PUBLISH_PYPI` = `true`.
- **GitHub**: create the environment `release` (Settings, Environments), optionally with required reviewers.

The same packages can be built locally:

```sh
dotnet pack src/CoreIdml -c Release -p:CoreIdmlRelease=true     # likewise CoreIdml.Pdf and CoreIdml.Cli

node eng/stage-npm.mjs                                          # artifacts/npm/: @coreidml/node, @coreidml/wasm,
                                                                # @coreidml/cli and their platform packages, ready
                                                                # for npm publish (--only cli: just the command line,
                                                                # from artifacts/cli/<rid>)

python bindings/python/tools/build_wheel.py                     # artifacts/python/: the wheel of this platform
```

`eng/stage-npm.mjs` and `build_wheel.py` take what is already built in `artifacts/`; their headers list the steps.
To try `@coreidml/cli` before a release, pack `artifacts/npm/cli` and the platform folder of your machine
(`npm pack`) and `npm install` both tarballs in an empty folder; `npx coreidml tools` then runs through the launcher.
Build the native library and the WebAssembly build for a release with `-p:CoreIdmlRelease=true` as well, so that
the version they report is the release version. The `package.json` files in the repository are marked private on
purpose: publish the staged copies.

### The Claude plugin

`plugins/coreidml` is a Claude plugin: the MCP server, started as `npx -y @coreidml/cli@<version> mcp`, and a skill
(`skills/idml-documents`) with the working conventions that complement the server's own instructions and prompts
(`src/CoreIdml.Cli/McpPrompts.cs`). `.claude-plugin/marketplace.json` at the repository root makes the repository a
marketplace, so users install it with `/plugin marketplace add eduardpaul/CoreIdml` and
`/plugin install coreidml@coreidml`. Check both with `claude plugin validate plugins/coreidml` and
`claude plugin validate .`.

The plugin's version and the npm version in `.mcp.json` move with every release, and that exact version of
`@coreidml/cli` must be on npm before anyone runs it, because the marketplace is read from `main`. The release
workflow takes care of it: it commits the new version to `main` only after npm has published it (step 4 above).

The plugin is listed in Anthropic's plugin directory from the developer portal (claude.ai/directory/manage, Submit
new, Plugin bundle): repository `eduardpaul/CoreIdml`, plugin path `plugins/coreidml`. The directory follows a
branch or tag and re-scans each new commit. Follow its pre-submission checklist: everything the plugin runs must be
inside its folder, no top-level `bin/`, a README of at least 40 words that says what it runs, reads and sends, a
license, and packages pinned to exact versions (an `npx` package is always held for a reviewer, by design). The
listing icon is read from the plugin the first time it is submitted and cannot be changed later.

The icon's source is `docs/icon/coreidml.svg`. `plugins/coreidml/icon.png` (1024×1024) and `bindings/mcpb/icon.png`
(512×512) are PNG renders of it with a transparent background; render both again if the SVG changes. The plugin's
sample files (`plugins/coreidml/samples`), which its README's example prompts use, are written by the sample app:
`dotnet run --project samples/CoreIdml.Sample -- plugin-samples plugins/coreidml/samples`. If you change them, check
that the README's prompts still give what it says they give.

The Claude Desktop extension (`bindings/mcpb`, `eng/pack-mcpb.mjs`) stays the way to use the server in Desktop chat,
where local MCP servers from plugins do not load.
