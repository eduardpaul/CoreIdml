# Contributing to CoreIdml

Everything is built from this repository: the .NET library, the native library, the WebAssembly build and the
package of each language. This page is the map.

## How it fits together

```
                    ┌──────────────────────────────────────────────┐
                    │ CoreIdml            the library (BCL only)   │   runs on CoreCLR, Native AOT and Mono/wasm
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
src/CoreIdml             the library
src/CoreIdml.Interop     the binding contract
src/CoreIdml.Native      C ABI transport, Native AOT        -> artifacts/native/<rid>/
src/CoreIdml.Wasm        JavaScript transport, browser-wasm -> artifacts/wasm/_framework/
include/coreidml.h       the C API
tools/CoreIdml.AbiGen    header -> generated binding tables
bindings/                node, wasm, python, dart
samples/flutter_app      a Flutter app with an on-device integration test
tests/                   .NET tests, the C smoke test, fixtures shared by every language
eng/                     validation, Android and packaging scripts
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

## Ground rules for the code

- The library and the interop layer must run on Mono in the browser. The build enforces what it can: trimming and
  AOT analysis, browser platform analysis (calling an API the browser lacks is a build error), and ordinary async
  instead of runtime async in those assemblies. `Directory.Build.targets` explains why.
- No dependencies outside the BCL in `src/CoreIdml`, and no reflection.
- Status codes are part of the ABI: never renumbered, new ones added at the end.
- Errors quote .NET's own messages, so resource keys stay off in the native and WebAssembly builds.

## Packages

```sh
dotnet pack src/CoreIdml -c Release -p:CoreIdmlRelease=true     # the NuGet package, without the -dev suffix

node eng/stage-npm.mjs                                          # artifacts/npm/: @coreidml/node, its platform
                                                                # packages, and @coreidml/wasm, ready for npm publish

python bindings/python/tools/build_wheel.py                     # artifacts/python/: the wheel of this platform
```

`eng/stage-npm.mjs` and `build_wheel.py` take what is already built in `artifacts/`; their headers list the steps.
Build the native library and the WebAssembly build for a release with `-p:CoreIdmlRelease=true` as well, so that
the version they report is the release version. The `package.json` files in the repository are marked private on
purpose: publish the staged copies.
