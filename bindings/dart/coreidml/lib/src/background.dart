part of 'document.dart';

/// Stops a PDF export or import started with an [IdmlBackground] helper.
///
/// The flag lives in native memory, so the background isolate sees [cancel] while it is busy in the library; the
/// operation stops after the page it is on and fails with [CoreIdmlStatus.cancelled]. One cancellation may serve
/// several operations; it is released by a finalizer.
final class IdmlCancellation implements Finalizable {
  IdmlCancellation() : _flag = calloc<Int32>() {
    _finalizer.attach(this, _flag.cast());
  }

  static final NativeFinalizer _finalizer = NativeFinalizer(calloc.nativeFree);

  final Pointer<Int32> _flag;

  /// Whether [cancel] was called.
  bool get isCancelled => _flag.value != 0;

  /// Asks the operations using this cancellation to stop after the current page.
  void cancel() => _flag.value = 1;
}

/// PDF export and import on a background isolate ([Isolate.run]), so that a Flutter UI isolate never waits for them.
///
/// The native handle of the document is used by the background isolate; the library serializes calls on one
/// document, so calls made meanwhile from the calling isolate wait for the export. Do not [IdmlDocument.close] the
/// document before the future completes. `progress` runs on the calling isolate with the pages done and the pages in
/// total; to stop the work, pass an [IdmlCancellation] and call [IdmlCancellation.cancel].
extension IdmlBackground on IdmlDocument {
  /// [IdmlDocument.exportPdf] on a background isolate; completes with the report.
  Future<Map<String, dynamic>> exportPdfInBackground(String path,
          {Map<String, Object?>? options, void Function(int done, int total)? progress, IdmlCancellation? cancellation}) async =>
      _inBackground(_exportPdfWork(CoreIdml.libraryPath, _open(), path, IdmlDocument._json(options)), progress, cancellation, this);

  /// [IdmlDocument.exportPdfBytes] on a background isolate; the bytes come back without a copy.
  Future<(Uint8List pdf, Map<String, dynamic> report)> exportPdfBytesInBackground(
          {Map<String, Object?>? options, void Function(int done, int total)? progress, IdmlCancellation? cancellation}) async =>
      _inBackground(_exportPdfBytesWork(CoreIdml.libraryPath, _open(), IdmlDocument._json(options)), progress, cancellation, this);

  /// [IdmlDocument.importPdf] on a background isolate.
  static Future<(IdmlDocument document, Map<String, dynamic> report)> importPdf(String path,
      {Map<String, Object?>? options, void Function(int done, int total)? progress, IdmlCancellation? cancellation}) async {
    final work = _importPdfWork(CoreIdml.libraryPath, path, null, IdmlDocument._json(options));
    final (handle, report) = await _inBackground(work, progress, cancellation, null);
    return (IdmlDocument._(handle), report);
  }

  /// [IdmlDocument.importPdfBytes] on a background isolate.
  static Future<(IdmlDocument document, Map<String, dynamic> report)> importPdfBytes(Uint8List bytes,
      {Map<String, Object?>? options, void Function(int done, int total)? progress, IdmlCancellation? cancellation}) async {
    final work = _importPdfWork(CoreIdml.libraryPath, null, bytes, IdmlDocument._json(options));
    final (handle, report) = await _inBackground(work, progress, cancellation, null);
    return (IdmlDocument._(handle), report);
  }
}

// The work is built in top-level functions so that the closures sent to the background isolate capture only
// sendable values (the library path, a handle, paths, JSON text, bytes), never an IdmlDocument. The background
// isolate loads the same library: the per-isolate state of CoreIdml starts empty there.

typedef _Work<T> = T Function(_NativeProgress callback);

_Work<Map<String, dynamic>> _exportPdfWork(String? library, int handle, String path, String? options) =>
    (callback) => IdmlDocument._exportPdf(CoreIdml.load(library), handle, path, options, callback);

_Work<(Uint8List, Map<String, dynamic>)> _exportPdfBytesWork(String? library, int handle, String? options) =>
    (callback) => IdmlDocument._exportPdfBytes(CoreIdml.load(library), handle, options, callback);

_Work<(int, Map<String, dynamic>)> _importPdfWork(String? library, String? path, Uint8List? bytes, String? options) =>
    (callback) => IdmlDocument._importPdf(CoreIdml.load(library), path, bytes, options, callback);

/// Runs [work] on a background isolate, relays its progress to [progress] and lets [cancellation] stop it.
///
/// [document] and [cancellation] are parameters of [Finalizable] types, so they stay reachable (the document is not
/// finalized, the flag is not freed) until the work is over.
Future<T> _inBackground<T>(_Work<T> work, void Function(int done, int total)? progress, IdmlCancellation? cancellation,
    IdmlDocument? document) async {
  final port = progress == null ? null : ReceivePort();
  port?.listen((message) {
    final (done, total) = message as (int, int);
    progress!(done, total);
  });
  try {
    return await _run(work, port?.sendPort, cancellation == null ? 0 : cancellation._flag.address);
  } finally {
    // Messages from the background isolate arrive in order, so every progress report was delivered before the result.
    port?.close();
  }
}

/// Kept apart from [_inBackground] so that the closure sent to the isolate captures only sendable values.
Future<T> _run<T>(_Work<T> work, SendPort? port, int flagAddress) => Isolate.run(debugName: 'coreidml', () {
      if (port == null && flagAddress == 0) return work(nullptr);
      final flag = Pointer<Int32>.fromAddress(flagAddress);
      return IdmlDocument._withProgress((done, total) {
        port?.send((done, total));
        return flagAddress == 0 || flag.value == 0;
      }, work);
    });
