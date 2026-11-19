import 'dart:async';
import 'dart:convert';
import 'dart:ffi';
import 'dart:isolate';
import 'dart:typed_data';

import 'package:ffi/ffi.dart';

import 'ffi/bindings.g.dart';
import 'library.dart';

part 'background.dart';

/// A native progress callback, or [nullptr] for none.
typedef _NativeProgress = Pointer<NativeFunction<CoreIdmlProgressNative>>;

/// An InDesign document held by the native library.
///
/// Build one from a spec with [create], open a template with [open] or [fromBytes], or rebuild one from a PDF with
/// [importPdf]; call [close] when done. A document that is forgotten is released by a native finalizer. Calls are
/// synchronous, and the library serializes calls on one document; for long PDF work in a Flutter app, see
/// [IdmlBackground].
final class IdmlDocument implements Finalizable {
  IdmlDocument._(this._handle) {
    _finalizer.attach(this, Pointer<Void>.fromAddress(_handle), detach: this);
  }

  static final NativeFinalizer _finalizer = NativeFinalizer(CoreIdml.bindings.documentCloseFinalizer);

  int _handle;

  CoreIdmlBindings get _native => CoreIdml.bindings;

  // ---- Creating and opening ----------------------------------------------------------------------------------

  /// Creates a document from a document spec: a `Map` (encoded as JSON) or JSON text. Without one, a blank A4 page.
  ///
  /// The spec describes the page size, colours, styles and pages of text frames, images, tables and shapes; see the
  /// README.
  static IdmlDocument create([Object? spec]) {
    final native = CoreIdml.bindings;
    return using((arena) {
      final handle = arena<IntPtr>();
      _check(native, native.documentCreate(_utf8(_json(spec), arena), handle));
      return IdmlDocument._(handle.value);
    });
  }

  /// Opens an .idml file.
  static IdmlDocument open(String path) {
    final native = CoreIdml.bindings;
    return using((arena) {
      final handle = arena<IntPtr>();
      _check(native, native.documentOpenFile(path.toNativeUtf8(allocator: arena), handle));
      return IdmlDocument._(handle.value);
    });
  }

  /// Opens the bytes of an .idml file. They are not kept after the call.
  static IdmlDocument fromBytes(Uint8List bytes) {
    final native = CoreIdml.bindings;
    return using((arena) {
      final handle = arena<IntPtr>();
      _check(native, native.documentOpen(_copy(arena, bytes), bytes.length, handle));
      return IdmlDocument._(handle.value);
    });
  }

  /// Rebuilds an editable document from a PDF file: text frames with inferred styles, image frames, shapes.
  ///
  /// Returns the document and the import report: `frames`, `images`, `fonts` (and whether each is available),
  /// `warnings`. [options]: `pages` (1-based), `imageDirectory` (where extracted images are written, since IDML links
  /// its images), `preserveLineBreaks`, `importShapes`, `fontDirectories`.
  ///
  /// [progress] is called after each page with the pages done and the pages in total; returning false cancels the
  /// import, which then throws an [IdmlException] with [CoreIdmlStatus.cancelled].
  static (IdmlDocument document, Map<String, dynamic> report) importPdf(String path,
      {Map<String, Object?>? options, bool Function(int done, int total)? progress}) {
    final (handle, report) = _withProgress(progress, (callback) => _importPdf(CoreIdml.bindings, path, null, _json(options), callback));
    return (IdmlDocument._(handle), report);
  }

  /// [importPdf] from the bytes of a PDF. They are not kept after the call; extracted images still go to
  /// `options['imageDirectory']`.
  static (IdmlDocument document, Map<String, dynamic> report) importPdfBytes(Uint8List bytes,
      {Map<String, Object?>? options, bool Function(int done, int total)? progress}) {
    final (handle, report) = _withProgress(progress, (callback) => _importPdf(CoreIdml.bindings, null, bytes, _json(options), callback));
    return (IdmlDocument._(handle), report);
  }

  /// Whether [close] was called.
  bool get closed => _handle == 0;

  // ---- The package -------------------------------------------------------------------------------------------

  /// The parts of the package and the manifest: `domVersion`, `parts`, `spreads`, `masterSpreads`, `stories`.
  Map<String, dynamic> info() => _jsonCall((native, json) => native.documentInfo(_open(), json))!;

  /// The bytes of one part (e.g. `Stories/Story_u188.xml`) as they would be saved now.
  Uint8List readPart(String path) {
    final native = _native;
    return using((arena) {
      final data = arena<Pointer<Uint8>>();
      final length = arena<Size>();
      _check(native, native.documentReadPart(_open(), path.toNativeUtf8(allocator: arena), data, length));
      return _takeBytes(native, data.value, length.value);
    });
  }

  /// One part as text (parts are UTF-8 XML, except images a template may embed).
  String readText(String path) => utf8.decode(readPart(path));

  /// Adds or replaces one part.
  void writePart(String path, Uint8List data) {
    final native = _native;
    using((arena) {
      _check(native, native.documentWritePart(_open(), path.toNativeUtf8(allocator: arena), _copy(arena, data), data.length));
    });
  }

  /// Adds or replaces one part with text, written as UTF-8.
  void writeText(String path, String text) => writePart(path, utf8.encode(text));

  /// The document as the bytes of an .idml file. Parts that were not changed are written byte for byte.
  Uint8List save() {
    final native = _native;
    return using((arena) {
      final data = arena<Pointer<Uint8>>();
      final length = arena<Size>();
      _check(native, native.documentSave(_open(), data, length));
      return _takeBytes(native, data.value, length.value);
    });
  }

  /// Saves the document as an .idml file.
  void saveFile(String path) {
    final native = _native;
    using((arena) => _check(native, native.documentSaveFile(_open(), path.toNativeUtf8(allocator: arena))));
  }

  // ---- Reading -----------------------------------------------------------------------------------------------

  /// A summary for programs and agents: pages, frames with positions and text, stories, links, styles, swatches,
  /// tags.
  ///
  /// [options]: `maxTextLength` (characters of text per frame and story), `includeRuns` (every story's indexed
  /// content runs, for [setRunText]), `includeResources`, `pages` (0-based indices), `sections` (any of `pages`,
  /// `stories`, `links`, `offPage`, `resources`).
  Map<String, dynamic> summary([Map<String, Object?>? options]) => using((arena) =>
      _jsonCall((native, json) => native.documentSummary(_open(), _utf8(_json(options), arena), json))!);

  /// One story in full: its text, frames, and every content run with its `index`, styles and scope.
  Map<String, dynamic> story(String storyId) => using((arena) =>
      _jsonCall((native, json) => native.documentStory(_open(), storyId.toNativeUtf8(allocator: arena), json))!);

  /// The XML structure as a tree of `tag`, `id`, `path`, `boundTo`, `attributes`, `text`, `children`; null when the
  /// document has none.
  Map<String, dynamic>? xmlStructure() => _jsonCall((native, json) => native.documentXmlStructure(_open(), json));

  /// The XML structure, or the subtree at the path [at] (e.g. `/Root/article[2]`), as XML.
  String exportXml({String? at}) {
    final native = _native;
    return using((arena) {
      final xml = arena<Pointer<Utf8>>();
      _check(native, native.documentExportXml(_open(), _utf8(at, arena), xml));
      return _takeString(native, xml.value);
    });
  }

  /// Checks references, ids, layers, threads and XML bindings: `{isValid: true, issues: [...]}`.
  Map<String, dynamic> validate() => _jsonCall((native, json) => native.documentValidate(_open(), json))!;

  /// What changed from this document to [other]: stories run by run, links, page count, page items.
  Map<String, dynamic> diff(IdmlDocument other) =>
      _jsonCall((native, json) => native.documentDiff(_open(), other._open(), json))!;

  // ---- Editing -----------------------------------------------------------------------------------------------

  /// Adds the colours, styles and pages of a document spec (a `Map` or JSON text, as for [create]); its pages are
  /// appended.
  void apply(Object spec) {
    final native = _native;
    using((arena) => _check(native, native.documentApply(_open(), _utf8(_json(spec), arena))));
  }

  /// Fills a template: `{'text': {key: value}, 'images': {key: '/path/photo.jpg'}}`. Keys match frame names, script
  /// labels, XML tags and `{{placeholders}}`. Returns the report: `filled` (places per key) and `missing` (keys that
  /// matched nothing).
  Map<String, dynamic> fill(Map<String, Object?> data) => using((arena) =>
      _jsonCall((native, json) => native.documentFill(_open(), _utf8(_json(data), arena), json))!);

  /// Replaces [find] with [replacement] in every story; returns the number of replacements.
  int replaceText(String find, String replacement) {
    final native = _native;
    return using((arena) {
      final count = arena<Int32>();
      _check(native, native.documentReplaceText(_open(), find.toNativeUtf8(allocator: arena), replacement.toNativeUtf8(allocator: arena), count));
      return count.value;
    });
  }

  /// Sets the text of content run [runIndex] of a story (indices from [story], or [summary] with `includeRuns`),
  /// keeping its formatting. A run holding special characters, such as a page number, is only overwritten with
  /// [force].
  void setRunText(String storyId, int runIndex, String text, {bool force = false}) {
    _requireInt32(runIndex, 'runIndex');
    final native = _native;
    using((arena) => _check(native, native.documentSetRunText(
        _open(), storyId.toNativeUtf8(allocator: arena), runIndex, text.toNativeUtf8(allocator: arena), force ? 1 : 0)));
  }

  /// Points a link (an id from `summary()['links']`) at another file: a URI or a local path. With [refit] and a
  /// readable local JPEG or PNG, the image is measured again and fitted to its frame.
  void relink(String linkId, String uriOrPath, {bool refit = false}) {
    final native = _native;
    using((arena) => _check(native, native.documentRelink(
        _open(), linkId.toNativeUtf8(allocator: arena), uriOrPath.toNativeUtf8(allocator: arena), refit ? 1 : 0)));
  }

  /// Imports XML onto the structure element at the path [at] (the root when null), with SimpleIDML's semantics:
  /// `simpleidml-setcontent`, `-ignorecontent`, `-forcecontent`, and `href` for images.
  ///
  /// [options]: `fit`, `characterStyles` (tag to character style, e.g. `{'b': 'Bold'}`), `relinkOnly`. Returns the
  /// report: `updated`, `added`, `deleted`, `unmatched`, `warnings`.
  Map<String, dynamic> importXml(String xml, {String? at, Map<String, Object?>? options}) => using((arena) =>
      _jsonCall((native, json) => native.documentImportXml(
          _open(), xml.toNativeUtf8(allocator: arena), _utf8(at, arena), _utf8(_json(options), arena), json))!);

  /// Appends pages of [source] (0-based [pages]; all of them when empty) to this document, remapping every id;
  /// styles, swatches and tags are merged once. [source] may be this document. Returns the number of pages added.
  int importPages(IdmlDocument source, [List<int> pages = const []]) {
    for (final page in pages) {
      _requireInt32(page, 'pages');
    }
    final native = _native;
    return using((arena) {
      final indices = arena<Int32>(pages.isEmpty ? 1 : pages.length);
      indices.asTypedList(pages.length).setAll(0, pages);
      final added = arena<Int32>();
      _check(native, native.documentImportPages(_open(), source._open(), indices, pages.length, added));
      return added.value;
    });
  }

  // ---- PDF ---------------------------------------------------------------------------------------------------

  /// Renders the document to a PDF file and returns the report: `pageCount`, `missingFonts`, `missingImages`,
  /// `oversetStories`, `warnings`.
  ///
  /// [options]: `pages` (1-based), `fontDirectories`, `useSystemFonts` (default true), `substitutions` (family to
  /// family), `linkDirectories` and `linkRemap` (where linked images are), `documentPath` (to resolve relative
  /// links), `maxImageDpi`, `jpegQuality`.
  ///
  /// [progress] is called after each page with the pages done and the pages in total; returning false cancels the
  /// export, which then throws an [IdmlException] with [CoreIdmlStatus.cancelled]. In a Flutter app, prefer
  /// [IdmlBackground.exportPdfInBackground], which keeps the UI isolate free.
  Map<String, dynamic> exportPdf(String path, {Map<String, Object?>? options, bool Function(int done, int total)? progress}) =>
      _withProgress(progress, (callback) => _exportPdf(_native, _open(), path, _json(options), callback));

  /// Renders the document to a PDF in memory; returns its bytes and the same report as [exportPdf]. Set
  /// `options['documentPath']` so that image links relative to the .idml file resolve.
  (Uint8List pdf, Map<String, dynamic> report) exportPdfBytes({Map<String, Object?>? options, bool Function(int done, int total)? progress}) =>
      _withProgress(progress, (callback) => _exportPdfBytes(_native, _open(), _json(options), callback));

  // ---- Lifetime ----------------------------------------------------------------------------------------------

  /// Releases the document. Calling it again does nothing.
  void close() {
    if (_handle != 0) {
      final handle = _handle;
      _handle = 0;
      _finalizer.detach(this);
      _native.documentClose(handle);
    }
  }

  int _open() {
    if (_handle == 0) {
      throw IdmlException('The document is closed.', CoreIdmlStatus.invalidHandle);
    }
    return _handle;
  }

  /// Makes a call that hands out a JSON string, and decodes it (null for JSON `null`).
  Map<String, dynamic>? _jsonCall(int Function(CoreIdmlBindings native, Pointer<Pointer<Utf8>> json) call) {
    final native = _native;
    return using((arena) {
      final json = arena<Pointer<Utf8>>();
      _check(native, call(native, json));
      return _takeJson(native, json.value);
    });
  }

  // The PDF calls take the bindings and a handle rather than a document, so that a background isolate can make them.

  static Map<String, dynamic> _exportPdf(CoreIdmlBindings native, int handle, String path, String? options, _NativeProgress progress) =>
      using((arena) {
        final report = arena<Pointer<Utf8>>();
        _check(native, native.documentExportPdf(handle, path.toNativeUtf8(allocator: arena), _utf8(options, arena), progress, nullptr, report));
        return _takeJson(native, report.value) ?? {};
      });

  static (Uint8List, Map<String, dynamic>) _exportPdfBytes(CoreIdmlBindings native, int handle, String? options, _NativeProgress progress) =>
      using((arena) {
        final data = arena<Pointer<Uint8>>();
        final length = arena<Size>();
        final report = arena<Pointer<Utf8>>();
        _check(native, native.documentExportPdfBytes(handle, _utf8(options, arena), progress, nullptr, data, length, report));
        final pdf = _takeBytes(native, data.value, length.value);
        return (pdf, _takeJson(native, report.value) ?? {});
      });

  /// Imports a PDF file ([path]) or PDF bytes ([bytes]); returns the new handle, which the caller wraps.
  static (int, Map<String, dynamic>) _importPdf(CoreIdmlBindings native, String? path, Uint8List? bytes, String? options, _NativeProgress progress) =>
      using((arena) {
        final handle = arena<IntPtr>();
        final report = arena<Pointer<Utf8>>();
        final status = bytes != null
            ? native.pdfImport(_copy(arena, bytes), bytes.length, _utf8(options, arena), progress, nullptr, handle, report)
            : native.pdfImportFile(path!.toNativeUtf8(allocator: arena), _utf8(options, arena), progress, nullptr, handle, report);
        _check(native, status);
        try {
          return (handle.value, _takeJson(native, report.value) ?? {});
        } catch (_) {
          native.documentClose(handle.value);
          rethrow;
        }
      });

  /// Runs [body] with a native callback for [progress] (or none). The library calls it on this thread, during the
  /// call. An exception thrown by [progress] cancels the call and is rethrown in place of the cancellation.
  static T _withProgress<T>(bool Function(int done, int total)? progress, T Function(_NativeProgress callback) body) {
    if (progress == null) return body(nullptr);
    Object? failure;
    StackTrace? failureStack;
    final callable = NativeCallable<CoreIdmlProgressNative>.isolateLocal(
      (int done, int total, Pointer<Void> _) {
        try {
          return progress(done, total) ? 0 : 1;
        } catch (error, stack) {
          failure ??= error;
          failureStack ??= stack;
          return 1;
        }
      },
      exceptionalReturn: 1,
    );
    try {
      return body(callable.nativeFunction);
    } on IdmlException catch (error) {
      final thrown = failure;
      if (thrown != null && error.code == CoreIdmlStatus.cancelled) Error.throwWithStackTrace(thrown, failureStack!);
      rethrow;
    } finally {
      callable.close();
    }
  }

  /// Throws the [IdmlException] of a failed call; the message is read on the thread that made the call.
  static void _check(CoreIdmlBindings native, int status) {
    if (status == CoreIdmlStatus.ok) return;
    final message = native.lastErrorMessage();
    throw IdmlException(message == nullptr ? 'The call failed with status $status.' : message.toDartString(), status);
  }

  /// A 32-bit signed integer, as the C API takes: a larger Dart int would be truncated silently.
  static void _requireInt32(int value, String name) {
    if (value < -0x80000000 || value > 0x7fffffff) {
      throw IdmlException('$name must be a 32-bit integer.', CoreIdmlStatus.invalidArgument);
    }
  }

  /// JSON text for a `Map` (or any value `jsonEncode` accepts); a `String` is taken to be JSON already.
  static String? _json(Object? value) => value == null || value is String ? value as String? : jsonEncode(value);

  /// A native UTF-8 copy of [text] released with the arena, or NULL.
  static Pointer<Utf8> _utf8(String? text, Arena arena) => text == null ? nullptr : text.toNativeUtf8(allocator: arena);

  /// A native copy of [bytes], released with the arena. One byte is allocated for empty data so the pointer is valid.
  static Pointer<Uint8> _copy(Arena arena, Uint8List bytes) {
    final buffer = arena<Uint8>(bytes.isEmpty ? 1 : bytes.length);
    buffer.asTypedList(bytes.length).setAll(0, bytes);
    return buffer;
  }

  /// Copies a NUL-terminated UTF-8 string the library handed out, and releases it.
  static String _takeString(CoreIdmlBindings native, Pointer<Utf8> text) {
    try {
      return text == nullptr ? '' : text.toDartString();
    } finally {
      native.free(text.cast());
    }
  }

  /// Decodes a JSON object the library handed out, and releases it; null for JSON `null` or no string.
  static Map<String, dynamic>? _takeJson(CoreIdmlBindings native, Pointer<Utf8> json) {
    final text = _takeString(native, json);
    return text.isEmpty ? null : jsonDecode(text) as Map<String, dynamic>?;
  }

  /// Copies a buffer the library handed out, and releases it.
  static Uint8List _takeBytes(CoreIdmlBindings native, Pointer<Uint8> data, int length) {
    try {
      return data == nullptr || length == 0 ? Uint8List(0) : Uint8List.fromList(data.asTypedList(length));
    } finally {
      native.free(data.cast());
    }
  }
}
