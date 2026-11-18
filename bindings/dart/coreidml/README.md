# coreidml

**Read, edit and write Adobe InDesign IDML files from Dart and Flutter, without InDesign.**

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles). CoreIdml opens
it, lets you read and change any part, and writes it back so that InDesign opens the result as if nothing but your
edit had happened. In a Flutter app that means filling a designer's template on the device, offline.

```dart
import 'package:coreidml/coreidml.dart';

final doc = IdmlDocument.open('brochure.idml');
try {
  for (final story in (doc.info()['stories'] as List).cast<String>()) {
    final xml = doc.readText(story);
    if (xml.contains('2025')) doc.writeText(story, xml.replaceAll('2025', '2026'));
  }
  doc.saveFile('brochure-2026.idml');
} finally {
  doc.close();
}
```

## Why CoreIdml

- **Surgical edits.** A part you do not touch is written back byte for byte. Change one story, and the designer's
  file differs in exactly that story.
- **Output InDesign accepts.** `mimetype` first and uncompressed, InDesign's own formatting for what you change.
- **Safe with files from anywhere.** Checksums verified on read, document type declarations refused, typed errors.
- **On the device.** The engine is a native library called through `dart:ffi`: no server, no InDesign, no platform
  channel. Android libraries are 16 KB page-aligned, as Google Play requires.

## Install

The package is used from this repository:

```yaml
dependencies:
  coreidml:
    git:
      url: https://github.com/eduardpaul/CoreIdml.git
      path: bindings/dart/coreidml
```

It needs the native library next to your app:

| Platform | Library | Where it goes |
| --- | --- | --- |
| Android | `libcoreidml.so` for `arm64-v8a` and `x86_64` | `android/app/src/main/jniLibs/<abi>/` |
| Linux desktop | `libcoreidml.so` | the bundle's `lib/` folder (an `install` rule in `linux/CMakeLists.txt`) |
| Dart on the command line | any of the above | the `COREIDML_LIBRARY` environment variable, or `CoreIdml.load(path)` |

The [sample app](https://github.com/eduardpaul/CoreIdml/tree/main/samples/flutter_app) shows the Android and Linux
setup end to end, with an integration test that runs on the device. The libraries are built from this repository;
see [CONTRIBUTING.md](https://github.com/eduardpaul/CoreIdml/blob/main/CONTRIBUTING.md).

## API

### Opening

```dart
final fromFile = IdmlDocument.open(path);
final fromBytes = IdmlDocument.fromBytes(bytes);   // a Uint8List, for example an asset; not kept after the call
```

### `IdmlDocument`

| Member | |
| --- | --- |
| `info()` | The parts of the package and the manifest, as a `Map`; see below. |
| `readPart(path)` | The bytes of one part, as a `Uint8List`, exactly as they would be saved now. |
| `readText(path)` | The same as a `String`. Parts are UTF-8 XML. |
| `writePart(path, bytes)` | Adds or replaces a part. |
| `writeText(path, text)` | The same with text, written as UTF-8. |
| `save()` | The document as the bytes of an .idml file. |
| `saveFile(path)` | Writes the .idml file. |
| `close()` | Releases the document. |
| `closed` | Whether it was closed. |

```dart
doc.info();
// {
//   domVersion: 10.0,
//   parts: [{path: designmap.xml, length: 16154, modified: false}, ...],
//   spreads: [Spreads/Spread_ud6.xml],
//   masterSpreads: [MasterSpreads/MasterSpread_ua5.xml],
//   stories: [Stories/Story_u1db.xml, Stories/Story_u19f.xml, Stories/Story_u188.xml]
// }
```

A document that is never closed is released by a native finalizer, but closing it yourself frees the memory at
once.

### Errors

Every failure is an `IdmlException` with a `code`; compare it with `CoreIdmlStatus`:

```dart
try {
  final doc = IdmlDocument.fromBytes(picked);
} on IdmlException catch (error) {
  if (error.code == CoreIdmlStatus.invalidFormat) {
    // Not a zip, no designmap.xml, a damaged entry: error.message says which.
  }
}
```

| `CoreIdmlStatus` | Meaning |
| --- | --- |
| `invalidFormat` | The input is not a usable IDML package. |
| `notFound` | A part you named does not exist. |
| `invalidArgument` | An argument is missing or malformed. |
| `invalidHandle` | The document was closed. |
| `io` | Reading or writing a file failed. |
| `invalidOperation` | The call is not valid right now, such as loading a second copy of the library. |
| `failed` | Anything unexpected; the message names it. |

## Good to know

- Verified in Flutter apps on Android (arm64-v8a, x86_64) and Linux desktop, and with the Dart SDK on Windows.
  Flutter web is not supported: the package uses `dart:ffi`.
- Calls are synchronous.
- **Scope of 0.1.** The library works at the package level: every part as XML or bytes, and full control over what
  is written. A typed document model on top of it (pages, frames, stories, styles) is in development.

Source, other languages (.NET, Node.js, the browser, Python, C) and issues:
[github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).

MIT licensed. CoreIdml is an independent project and is not affiliated with Adobe.
