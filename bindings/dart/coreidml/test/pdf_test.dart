// PDF export and import. The Liberation fonts of the fixtures stand in for the fonts the documents name, so the
// output does not depend on the fonts of the machine.
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:coreidml/coreidml.dart';
import 'package:test/test.dart';

import 'support.dart';

String head(List<int> pdf) => String.fromCharCodes(pdf.take(5));

void main() {
  group('export', () {
    test('a document renders to a PDF file with the fonts it is given', () {
      final doc = IdmlDocument.open(fixture('article-1photo.idml').path);
      addTearDown(doc.close);
      final path = '${tempFolder().path}/article.pdf';

      final report = doc.exportPdf(path, options: liberationFonts);

      expect(head(File(path).readAsBytesSync()), '%PDF-');
      expect(report['pageCount'], 1);
      final fonts = (report['missingFonts'] as List).cast<Map<String, dynamic>>();
      expect(fonts.map((font) => font['requestedFamily']), everyElement('Minion Pro'));
      expect(fonts.map((font) => font['usedFont']), everyElement(startsWith('Liberation Serif')));
      expect(report['missingImages'], [endsWith('default.jpg')]);
      expect(report['oversetStories'], contains('u19f'));
      expect(report['warnings'], isEmpty);
    });

    test('without fonts, the standard PDF fonts are used and reported', () {
      final doc = pages(1);
      addTearDown(doc.close);

      final (pdf, report) = doc.exportPdfBytes(options: {'useSystemFonts': false});

      expect(head(pdf), '%PDF-');
      expect((report['missingFonts'] as List).single['usedFont'], 'Times-Roman');
    });

    test('substitutions choose the font that replaces a family', () {
      final doc = pages(1);
      addTearDown(doc.close);

      final (_, report) = doc.exportPdfBytes(options: {
        ...liberationFonts,
        'substitutions': {'Minion Pro': 'Liberation Sans'},
      });

      expect((report['missingFonts'] as List).map((font) => font['usedFont']), everyElement(startsWith('Liberation Sans')));
    });

    test('linked images are found through linkRemap and linkDirectories', () {
      final doc = IdmlDocument.open(fixture('article-1photo.idml').path);
      addTearDown(doc.close);
      final folder = tempFolder();
      final photo = File('${folder.path}/photo.png')..writeAsBytesSync(png(40, 30));
      Directory('${folder.path}/links').createSync();
      File('${folder.path}/links/default.jpg').writeAsBytesSync(png(40, 30));

      final (missing, plain) = doc.exportPdfBytes(options: liberationFonts);
      final (remapped, byRemap) = doc.exportPdfBytes(options: {
        ...liberationFonts,
        'linkRemap': {'default.jpg': photo.path},
      });
      final (_, byDirectory) = doc.exportPdfBytes(options: {
        ...liberationFonts,
        'linkDirectories': ['${folder.path}/links'],
      });

      expect(plain['missingImages'], hasLength(1));
      expect(byRemap['missingImages'], isEmpty);
      expect(byDirectory['missingImages'], isEmpty);
      expect(remapped.length, greaterThan(missing.length));
    });

    test('pages selects the pages to render', () {
      final doc = pages(3);
      addTearDown(doc.close);

      final (pdf, report) = doc.exportPdfBytes(options: {...liberationFonts, 'pages': [2]});
      final (imported, _) = IdmlDocument.importPdfBytes(pdf, options: {'imageDirectory': tempFolder().path});
      addTearDown(imported.close);

      expect(report['pageCount'], 1);
      expect(storyTexts(imported), ['Página 2']);
    });

    test('progress is reported after each page', () {
      final doc = pages(3);
      addTearDown(doc.close);
      final calls = <(int, int)>[];

      final (pdf, report) = doc.exportPdfBytes(
        options: liberationFonts,
        progress: (done, total) {
          calls.add((done, total));
          return true;
        },
      );

      expect(head(pdf), '%PDF-');
      expect(report['pageCount'], 3);
      expect(calls, [(1, 3), (2, 3), (3, 3)]);

      calls.clear();
      doc.exportPdf('${tempFolder().path}/progress.pdf', options: liberationFonts, progress: (done, total) {
        calls.add((done, total));
        return true;
      });
      expect(calls, [(1, 3), (2, 3), (3, 3)]);
    });

    test('a progress callback that returns false cancels the export', () {
      final doc = pages(3);
      addTearDown(doc.close);
      final seen = <int>[];

      expect(
        () => doc.exportPdf('${tempFolder().path}/cancelled.pdf', options: liberationFonts, progress: (done, total) {
          seen.add(done);
          return done < 2;
        }),
        throwsIdml(CoreIdmlStatus.cancelled),
      );
      expect(seen, [1, 2]);
      expect(() => doc.exportPdfBytes(progress: (done, total) => false), throwsIdml(CoreIdmlStatus.cancelled));
      // The document is still usable afterwards.
      expect(doc.exportPdfBytes(options: liberationFonts).$2['pageCount'], 3);
    });

    test('a cancelled export leaves no partial file behind', () {
      final doc = pages(3);
      addTearDown(doc.close);
      final path = '${tempFolder().path}/cancelled.pdf';

      expect(() => doc.exportPdf(path, progress: (done, total) => false), throwsIdml(CoreIdmlStatus.cancelled));

      expect(File(path).existsSync(), isFalse);
    });

    test('an exception thrown by a progress callback cancels the export and is rethrown', () {
      final doc = pages(2);
      addTearDown(doc.close);

      expect(
        () => doc.exportPdfBytes(progress: (done, total) => throw StateError('stop at $done')),
        throwsA(isA<StateError>().having((error) => error.message, 'message', 'stop at 1')),
      );
      expect(doc.validate()['isValid'], isTrue);
    });

    test('export failures are IdmlExceptions', () {
      final doc = pages(1);
      addTearDown(doc.close);

      expect(() => doc.exportPdf('${tempFolder().path}/no/such/folder/x.pdf'), throwsIdml(CoreIdmlStatus.io));
      expect(() => doc.exportPdfBytes(options: {'pages': 'all'}), throwsIdml(CoreIdmlStatus.invalidArgument));
      doc.close();
      expect(() => doc.exportPdfBytes(), throwsIdml(CoreIdmlStatus.invalidHandle));
    });
  });

  group('import', () {
    test('a PDF file is rebuilt as an editable document, with the fonts it needs', () {
      final doc = IdmlDocument.create({
        'pages': [
          {
            'items': [
              {'type': 'text', 'x': 50, 'y': 50, 'width': 300, 'height': 40, 'text': 'Hola PDF'},
            ],
          },
        ],
      });
      final folder = tempFolder();
      final pdf = '${folder.path}/hola.pdf';
      final exported = doc.exportPdf(pdf, options: liberationFonts);
      doc.close();
      expect(exported['pageCount'], 1);

      final (imported, report) = IdmlDocument.importPdf(pdf, options: {'imageDirectory': '${folder.path}/links'});
      addTearDown(imported.close);

      expect(storyTexts(imported), ['Hola PDF']);
      expect(report['textFrames'], 1);
      final font = (report['fonts'] as List).single as Map;
      expect(font['family'], 'Liberation Serif');
      expect(font['pages'], [1]);
      expect(imported.validate()['isValid'], isTrue);
      // The rebuilt document is an ordinary document.
      expect(imported.replaceText('Hola', 'Adiós'), 1);
      expect(IdmlDocument.fromBytes(imported.save()).summary()['pageCount'], 1);
    });

    test('PDF bytes are imported with progress, and images are written to imageDirectory', () {
      final article = IdmlDocument.open(fixture('article-1photo.idml').path);
      addTearDown(article.close);
      final folder = tempFolder();
      final photo = File('${folder.path}/photo.png')..writeAsBytesSync(png(40, 30));
      article.relink('u21a', photo.path, refit: true);
      article.importPages(article);
      final (pdf, _) = article.exportPdfBytes(options: liberationFonts);
      final calls = <(int, int)>[];

      final (imported, report) = IdmlDocument.importPdfBytes(pdf, options: {'imageDirectory': '${folder.path}/images'}, progress: (done, total) {
        calls.add((done, total));
        return true;
      });
      addTearDown(imported.close);

      expect(calls, [(1, 2), (2, 2)]);
      expect(imported.summary()['pageCount'], 2);
      expect(report['imageFrames'], 2);
      expect(report['images'], hasLength(2));
      for (final image in (report['images'] as List).cast<String>()) {
        expect(File(image).existsSync(), isTrue, reason: image);
      }
      expect((imported.summary()['links'] as List), hasLength(2));
      expect(storyTexts(imported).join('\n'), contains('THE HEADLINE HERE'));
    });

    test('an import is cancelled by its progress callback', () {
      final doc = pages(3);
      addTearDown(doc.close);
      final (pdf, _) = doc.exportPdfBytes(options: liberationFonts);

      expect(() => IdmlDocument.importPdfBytes(pdf, progress: (done, total) => false), throwsIdml(CoreIdmlStatus.cancelled));
    });

    test('import failures are IdmlExceptions', () {
      expect(() => IdmlDocument.importPdf('${tempFolder().path}/missing.pdf'), throwsIdml(CoreIdmlStatus.io));
      expect(() => IdmlDocument.importPdfBytes(Uint8List.fromList(utf8.encode('not a PDF'))), throwsA(isA<IdmlException>()));
    });
  });
}
