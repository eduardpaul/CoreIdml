// Regressions for the defects found by the code review of this binding.
import 'dart:io';
import 'dart:isolate';

import 'package:coreidml/coreidml.dart';
import 'package:test/test.dart';

import 'support.dart';

void main() {
  test('a library that is not CoreIdml is reported as such, not as a missing symbol', () async {
    final other = Platform.isWindows
        ? 'kernel32.dll'
        : Platform.isMacOS
            ? '/usr/lib/libSystem.B.dylib'
            : 'libc.so.6';
    // A fresh isolate has loaded nothing yet.
    final error = await Isolate.run(() {
      try {
        CoreIdml.load(other);
        return 'loaded';
      } on IdmlException catch (error) {
        return 'IdmlException: ${error.message}';
      } on Object catch (error) {
        return '${error.runtimeType}: $error';
      }
    });
    expect(error, startsWith('IdmlException: '));
    expect(error, contains('not the CoreIdml native library'));
  });

  test('the loaded library named another way is the same library', () {
    final path = CoreIdml.libraryPath!;
    final file = File(path).absolute;
    expect(CoreIdml.load(file.path), same(CoreIdml.bindings));
    if (Platform.isWindows) expect(CoreIdml.load(file.path.toUpperCase()), same(CoreIdml.bindings));
    expect(() => CoreIdml.load('${file.path}.other'), throwsIdml(CoreIdmlStatus.invalidOperation));
  });

  test('indices outside 32 bits are refused instead of being truncated', () {
    final doc = IdmlDocument.open(fixture('article-1photo.idml').path);
    addTearDown(doc.close);

    expect(() => doc.importPages(doc, [1 << 32]), throwsIdml(CoreIdmlStatus.invalidArgument));
    expect(() => doc.importPages(doc, [-(1 << 31) - 1]), throwsIdml(CoreIdmlStatus.invalidArgument));
    expect(() => doc.setRunText('u188', 1 << 31, 'x'), throwsIdml(CoreIdmlStatus.invalidArgument));
    expect(doc.importPages(doc, [0]), 1);
  });

  test('a progress callback that throws in the background cancels the work and fails its future', () async {
    final doc = pages(4);
    addTearDown(doc.close);
    final shared = IdmlCancellation();
    final calls = <int>[];

    final work = doc.exportPdfBytesInBackground(
        options: liberationFonts,
        cancellation: shared,
        progress: (done, total) {
          calls.add(done);
          throw StateError('stop at $done');
        });

    await expectLater(work, throwsA(isA<StateError>().having((error) => error.message, 'message', 'stop at 1')));
    expect(calls, [1]);
    expect(calls.length, lessThan(4));
    // The caller's cancellation, which other operations may share, is not touched.
    expect(shared.isCancelled, isFalse);
    // The document is still usable.
    final (_, report) = await doc.exportPdfBytesInBackground(options: liberationFonts);
    expect(report['pageCount'], 4);
  });
}
