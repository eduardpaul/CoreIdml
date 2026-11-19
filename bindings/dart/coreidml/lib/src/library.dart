import 'dart:ffi';
import 'dart:io';

import 'package:ffi/ffi.dart';

import 'ffi/bindings.g.dart';

/// A failed call into the library. [code] is a [CoreIdmlStatus] value, the same in every CoreIdml binding.
class IdmlException implements Exception {
  IdmlException(this.message, [this.code = CoreIdmlStatus.failed]);

  final String message;
  final int code;

  @override
  String toString() => 'IdmlException($code): $message';
}

/// The native library: loads it once and checks its ABI.
abstract final class CoreIdml {
  /// Environment variable with the full path of the native library; it wins over every other location.
  static const libraryEnvironmentVariable = 'COREIDML_LIBRARY';

  static CoreIdmlBindings? _bindings;
  static String? _path;

  /// Loads the native library. Called on first use; call it first to choose the library.
  ///
  /// Without [path] the library is searched for: the `COREIDML_LIBRARY` environment variable, the bundle of a
  /// Flutter desktop app, a local build in `artifacts/native` when running inside a source checkout, then the
  /// name the platform loads it by (`libcoreidml.so` on Android and Linux, `libcoreidml.dylib` on macOS,
  /// `coreidml.dll` on Windows; on iOS the library is linked into the app).
  static CoreIdmlBindings load([String? path]) {
    final loaded = _bindings;
    if (loaded != null) {
      if (path != null && !_sameFile(path, _path)) {
        throw IdmlException('The native library is already loaded from $_path; a process can use only one.', CoreIdmlStatus.invalidOperation);
      }
      return loaded;
    }
    final location = path ?? _find();
    final DynamicLibrary library;
    try {
      library = location == null ? DynamicLibrary.process() : DynamicLibrary.open(location);
    } on ArgumentError catch (error) {
      throw IdmlException('Could not load the CoreIdml native library from ${location ?? 'the app itself'}: $error');
    }
    // The ABI version first, on its own: binding every function would fail on the first one an older library lacks,
    // and the reason (another release) would be lost.
    final int abi;
    try {
      abi = library.lookupFunction<Int32 Function(), int Function()>('coreidml_abi_version')();
    } on ArgumentError catch (error) {
      throw IdmlException('The library at ${location ?? 'the app itself'} is not the CoreIdml native library: $error');
    }
    if (abi != coreIdmlAbiVersion) {
      throw IdmlException('The native library at $location has ABI version $abi; this package needs $coreIdmlAbiVersion. '
          'Use the library and the package from the same release.');
    }
    final CoreIdmlBindings bindings;
    try {
      bindings = CoreIdmlBindings(library);
    } on ArgumentError catch (error) {
      throw IdmlException('The native library at $location lacks a function of ABI version $abi: $error');
    }
    _path = location;
    return _bindings = bindings;
  }

  /// Whether two paths name the same file: links resolved, made absolute, and case ignored where file names ignore it.
  static bool _sameFile(String a, String? b) {
    if (b == null) return false;
    if (a == b) return true;
    String canonical(String path) {
      try {
        return File(path).resolveSymbolicLinksSync();
      } on FileSystemException {
        return File(path).absolute.path;
      }
    }

    final (x, y) = (canonical(a), canonical(b));
    return Platform.isWindows || Platform.isMacOS ? x.toLowerCase() == y.toLowerCase() : x == y;
  }

  /// The loaded library (loads it if needed).
  static CoreIdmlBindings get bindings => _bindings ?? load();

  /// The path the library was opened from (null on iOS, where it is part of the app), so that another isolate
  /// can open the same one.
  static String? get libraryPath {
    load();
    return _path;
  }

  /// Version of the native library.
  static String get version => bindings.version().toDartString();

  /// File name of the native library on this platform.
  static String get libraryFileName {
    if (Platform.isWindows) return 'coreidml.dll';
    if (Platform.isMacOS) return 'libcoreidml.dylib';
    return 'libcoreidml.so';
  }

  /// Where the library is, or null when it is linked into the app (iOS).
  static String? _find() {
    final override = Platform.environment[libraryEnvironmentVariable];
    if (override != null && override.isNotEmpty) return override;
    if (Platform.isIOS) return null;
    return _bundled() ?? _localBuild() ?? libraryFileName;
  }

  /// The library inside a Flutter desktop bundle, where a bare file name is not always searched for:
  /// `lib/` next to the program on Linux, the program's folder on Windows, `Frameworks/` of the app on macOS.
  static String? _bundled() {
    if (!(Platform.isLinux || Platform.isWindows || Platform.isMacOS)) return null;
    final separator = Platform.pathSeparator;
    final program = File(Platform.resolvedExecutable).parent;
    final folder = Platform.isLinux
        ? '${program.path}${separator}lib'
        : Platform.isMacOS
            ? '${program.parent.path}${separator}Frameworks'
            : program.path;
    final library = File('$folder$separator$libraryFileName');
    return library.existsSync() ? library.path : null;
  }

  /// The build in `artifacts/native/<rid>` when the current directory is inside a source checkout (tests, examples).
  static String? _localBuild() {
    final rid = switch (Abi.current()) {
      Abi.windowsX64 => 'win-x64',
      Abi.windowsArm64 => 'win-arm64',
      Abi.linuxX64 => 'linux-x64',
      Abi.linuxArm64 => 'linux-arm64',
      Abi.macosX64 => 'osx-x64',
      Abi.macosArm64 => 'osx-arm64',
      _ => null,
    };
    if (rid == null) return null;
    for (var directory = Directory.current; directory.parent.path != directory.path; directory = directory.parent) {
      final separator = Platform.pathSeparator;
      if (File('${directory.path}${separator}include${separator}coreidml.h').existsSync()) {
        final library = File('${directory.path}${separator}artifacts${separator}native$separator$rid$separator$libraryFileName');
        return library.existsSync() ? library.path : null;
      }
    }
    return null;
  }
}
