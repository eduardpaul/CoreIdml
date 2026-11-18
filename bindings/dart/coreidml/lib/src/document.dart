import 'dart:convert';
import 'dart:ffi';
import 'dart:typed_data';

import 'package:ffi/ffi.dart';

import 'ffi/bindings.g.dart';
import 'library.dart';

/// An InDesign document held by the native library.
///
/// Open one with [open] or [fromBytes] and call [close] when done; a document that is forgotten is released by a
/// native finalizer. Calls are synchronous, and the library serializes calls on one document.
final class IdmlDocument implements Finalizable {
  IdmlDocument._(this._handle) {
    _finalizer.attach(this, Pointer<Void>.fromAddress(_handle), detach: this);
  }

  static final NativeFinalizer _finalizer = NativeFinalizer(CoreIdml.bindings.documentCloseFinalizer);

  int _handle;

  CoreIdmlBindings get _native => CoreIdml.bindings;

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

  /// Whether [close] was called.
  bool get closed => _handle == 0;

  /// The parts of the package and the manifest: `domVersion`, `parts`, `spreads`, `masterSpreads`, `stories`.
  Map<String, dynamic> info() {
    final native = _native;
    return using((arena) {
      final json = arena<Pointer<Utf8>>();
      _check(native, native.documentInfo(_open(), json));
      return jsonDecode(_takeString(native, json.value)) as Map<String, dynamic>;
    });
  }

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

  /// Throws the [IdmlException] of a failed call; the message is read on the thread that made the call.
  static void _check(CoreIdmlBindings native, int status) {
    if (status == CoreIdmlStatus.ok) return;
    final message = native.lastErrorMessage();
    throw IdmlException(message == nullptr ? 'The call failed with status $status.' : message.toDartString(), status);
  }

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

  /// Copies a buffer the library handed out, and releases it.
  static Uint8List _takeBytes(CoreIdmlBindings native, Pointer<Uint8> data, int length) {
    try {
      return data == nullptr || length == 0 ? Uint8List(0) : Uint8List.fromList(data.asTypedList(length));
    } finally {
      native.free(data.cast());
    }
  }
}
