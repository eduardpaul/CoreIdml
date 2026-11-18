// PDF work on a background isolate: the document handle and the cancellation flag cross isolates, progress comes back.
import 'dart:async';
import 'dart:io';

import 'package:coreidml/coreidml.dart';
import 'package:test/test.dart';

import 'support.dart';

void main() {
  test('a PDF is exported on a background isolate, to a file and to bytes, with progress', () async {
    final doc = pages(3);
    addTearDown(doc.close);
    final path = '${tempFolder().path}/background.pdf';
    final progress = <(int, int)>[];

    final report = await doc.exportPdfInBackground(path, options: liberationFonts, progress: (done, total) => progress.add((done, total)));

    expect(report['pageCount'], 3);
    expect(File(path).lengthSync(), greaterThan(1000));
    // Every report arrives before the result.
    expect(progress, [(1, 3), (2, 3), (3, 3)]);

    final (bytes, bytesReport) = await doc.exportPdfBytesInBackground(options: {...liberationFonts, 'pages': [1, 3]});
    expect(String.fromCharCodes(bytes.take(5)), '%PDF-');
    expect(bytesReport['pageCount'], 2);
    // The document is still usable on this isolate.
    expect(doc.summary()['pageCount'], 3);
  });

  test('a PDF is imported on a background isolate, from a file and from bytes', () async {
    final doc = pages(2);
    addTearDown(doc.close);
    final folder = tempFolder();
    final path = '${folder.path}/source.pdf';
    doc.exportPdf(path, options: liberationFonts);
    final progress = <int>[];

    final (fromFile, fileReport) = await IdmlBackground.importPdf(path,
        options: {'imageDirectory': '${folder.path}/links'}, progress: (done, total) => progress.add(done));
    addTearDown(fromFile.close);
    final (fromBytes, _) = await IdmlBackground.importPdfBytes(File(path).readAsBytesSync(), options: {'imageDirectory': '${folder.path}/links'});
    addTearDown(fromBytes.close);

    expect(progress, [1, 2]);
    expect(fileReport['textFrames'], 2);
    expect(storyTexts(fromFile), ['Página 1', 'Página 2']);
    expect(storyTexts(fromBytes), ['Página 1', 'Página 2']);
    // The documents belong to this isolate like any other.
    expect(fromFile.replaceText('Página', 'Page'), 2);
  });

  test('the calling isolate keeps running while the work is in the background', () async {
    final doc = IdmlDocument.open(fixture('magazineA-edito.idml').path);
    addTearDown(doc.close);
    var ticks = 0;
    final timer = Timer.periodic(Duration.zero, (_) => ticks++);
    addTearDown(timer.cancel);

    final future = doc.exportPdfBytesInBackground(options: liberationFonts);
    final ticksBefore = ticks;
    await Future<void>.delayed(const Duration(milliseconds: 1));
    final (pdf, _) = await future;

    expect(pdf, isNotEmpty);
    expect(ticks, greaterThan(ticksBefore));
  });

  test('a cancellation stops background work after the current page', () async {
    final doc = pages(3);
    addTearDown(doc.close);
    final cancellation = IdmlCancellation();
    expect(cancellation.isCancelled, isFalse);
    cancellation.cancel();
    expect(cancellation.isCancelled, isTrue);
    final progress = <int>[];

    await expectLater(
      doc.exportPdfInBackground('${tempFolder().path}/never.pdf', progress: (done, total) => progress.add(done), cancellation: cancellation),
      throwsIdml(CoreIdmlStatus.cancelled),
    );
    expect(progress, [1]);
    await expectLater(doc.exportPdfBytesInBackground(cancellation: cancellation), throwsIdml(CoreIdmlStatus.cancelled));

    final (pdf, _) = doc.exportPdfBytes(options: liberationFonts);
    await expectLater(IdmlBackground.importPdfBytes(pdf, cancellation: cancellation), throwsIdml(CoreIdmlStatus.cancelled));
    // The document is still usable afterwards, in the background too.
    expect(doc.validate()['isValid'], isTrue);
    expect((await doc.exportPdfBytesInBackground(options: liberationFonts, cancellation: IdmlCancellation())).$2['pageCount'], 3);
  });

  test('a cancellation reaches work that is already running', () async {
    final doc = pages(40);
    addTearDown(doc.close);
    final cancellation = IdmlCancellation();
    final progress = <int>[];

    final export = doc.exportPdfBytesInBackground(
      options: liberationFonts,
      cancellation: cancellation,
      progress: (done, total) {
        progress.add(done);
        cancellation.cancel();
      },
    );

    // The background isolate may have rendered a few more pages before it saw the flag, but not all of them.
    await expectLater(export, throwsIdml(CoreIdmlStatus.cancelled));
    expect(progress.length, lessThan(40));
  });

  test('background failures are IdmlExceptions with the status of the library', () async {
    final doc = pages(1);
    addTearDown(doc.close);

    await expectLater(doc.exportPdfInBackground('${tempFolder().path}/no/such/folder/x.pdf'), throwsIdml(CoreIdmlStatus.io));
    await expectLater(IdmlBackground.importPdf('${tempFolder().path}/missing.pdf'), throwsIdml(CoreIdmlStatus.io));
    doc.close();
    await expectLater(doc.exportPdfInBackground('x.pdf'), throwsIdml(CoreIdmlStatus.invalidHandle));
  });
}
