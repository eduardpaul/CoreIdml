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
bindings/                node, wasm, python, dart
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

Versions: `Directory.Build.props` (`VersionPrefix`) is the one source. The manifests of the language packages repeat
it; `node eng/set-version.mjs --check` fails when they disagree, and CI runs it.

A release is a tag. Bump `VersionPrefix` (and the manifests: `node eng/set-version.mjs 1.2.0`) in a commit, then push
`v1.2.0`. `.github/workflows/release.yml` then:

1. builds the native library, the command line and a Python wheel on each platform (Windows x64 and arm64, Linux x64
   and arm64 in AlmaLinux 8 so they need only glibc 2.28, macOS arm64 and x64), the Android libraries and the
   WebAssembly build, and tests each where it was built (C smoke test, the wheel installed in a fresh environment,
   the command line, the JavaScript package in Node and headless Chrome);
2. packs everything once: NuGet packages, the npm packages (one per platform plus `@coreidml/node` and
   `@coreidml/wasm`), the wheels, and archives of the native libraries, command lines, Android libraries and
   WebAssembly engine, the Claude Desktop extensions (`eng/pack-mcpb.mjs`: the command line as an MCP Bundle for
   Windows and macOS, from `bindings/mcpb/manifest.json`) with a `SHA256SUMS`, and checks that the staged npm
   package installs and runs;
3. publishes to NuGet, npm and PyPI, then creates the GitHub release with every file attached.

Every push to `main` runs steps 1 and 2 as a dry run (version `<prefix>-ci.<run>`), so the release path is always
tested, and its publish steps are rehearsed against throwaway registries (Verdaccio, pypiserver, a local NuGet feed
and a draft GitHub release that is deleted again).

Every tag creates the GitHub release with all the files. Each registry is published to only once it is switched on
with a repository variable, after its one-time trusted-publishing setup:

- **NuGet**: on nuget.org, add a trusted publishing policy for this repository and `release.yml`, then set the
  repository variable `NUGET_USER` to the nuget.org account name (this switches NuGet on).
- **npm**: own the `@coreidml` scope. A package must exist before it can trust a workflow, so the first release needs
  an automation token in the `NPM_TOKEN` secret; then add this repository and `release.yml` as trusted publisher of
  each package and delete the secret. Switch it on with the repository variable `PUBLISH_NPM` = `true`.
- **PyPI**: add a (pending) trusted publisher for the project `coreidml`: this repository, workflow `release.yml`,
  environment `release`. Switch it on with the repository variable `PUBLISH_PYPI` = `true`.
- **GitHub**: create the environment `release` (Settings, Environments), optionally with required reviewers.

The same packages can be built locally:

```sh
dotnet pack src/CoreIdml -c Release -p:CoreIdmlRelease=true     # likewise CoreIdml.Pdf and CoreIdml.Cli

node eng/stage-npm.mjs                                          # artifacts/npm/: @coreidml/node, its platform
                                                                # packages, and @coreidml/wasm, ready for npm publish

python bindings/python/tools/build_wheel.py                     # artifacts/python/: the wheel of this platform
```

`eng/stage-npm.mjs` and `build_wheel.py` take what is already built in `artifacts/`; their headers list the steps.
Build the native library and the WebAssembly build for a release with `-p:CoreIdmlRelease=true` as well, so that
the version they report is the release version. The `package.json` files in the repository are marked private on
purpose: publish the staged copies.
