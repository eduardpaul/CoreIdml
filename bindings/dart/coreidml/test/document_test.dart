// Runs against the native library in artifacts/native/<rid> (dotnet publish src/CoreIdml.Native -c Release -r <rid>)
// and the fixtures every binding shares. From bindings/dart/coreidml:  dart pub get && dart test
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:coreidml/coreidml.dart';
import 'package:test/test.dart';

import 'support.dart';

const story = 'Stories/Story_u188.xml';

void main() {
  final article = fixture('article-1photo.idml');
  final bytes = article.readAsBytesSync();

  test('the library loads and reports its version', () {
    expect(CoreIdml.version, matches(RegExp(r'^\d+\.\d+\.\d+')));
    expect(coreIdmlAbiVersion, 1);
    expect(File(CoreIdml.libraryPath!).existsSync(), isTrue);
    expect(() => CoreIdml.load('${Directory.systemTemp.path}/another-coreidml.dll'), throwsIdml(CoreIdmlStatus.invalidOperation));
  });

  test('a document is opened and described', () {
    final doc = IdmlDocument.open(article.path);
    addTearDown(doc.close);

    final info = doc.info();

    expect(info['domVersion'], '10.0');
    expect(info['spreads'], ['Spreads/Spread_ud6.xml']);
    expect(info['masterSpreads'], ['MasterSpreads/MasterSpread_ua5.xml']);
    expect(info['stories'], hasLength(3));
    expect(info['parts'], hasLength(16));
    expect((info['parts'] as List).first, {'path': 'mimetype', 'length': 43, 'modified': false});
  });

  test('a part is read, replaced and saved; the other parts stay byte for byte', () {
    final doc = IdmlDocument.fromBytes(bytes);
    addTearDown(doc.close);
    final original = doc.readText(story);
    final edited = original.replaceFirst('<Story ', '<Story Marker="edited" ');
    expect(edited, isNot(original));

    doc.writeText(story, edited);
    final saved = doc.save();

    final modified = (doc.info()['parts'] as List).where((part) => part['modified'] == true).map((part) => part['path']);
    expect(modified, [story]);
    final before = IdmlDocument.fromBytes(bytes);
    final after = IdmlDocument.fromBytes(saved);
    addTearDown(before.close);
    addTearDown(after.close);
    expect(after.readText(story), edited);
    for (final part in before.info()['parts'] as List) {
      final path = part['path'] as String;
      if (path != story) {
        expect(after.readPart(path), before.readPart(path), reason: '$path changed');
      }
    }
  });

  test('a document saves to a file with a name that is not ASCII', () {
    final folder = tempFolder();
    final path = '${folder.path}${Platform.pathSeparator}ficha-ñ-日本.idml';
    final doc = IdmlDocument.fromBytes(bytes);
    addTearDown(doc.close);

    doc.saveFile(path);

    final saved = IdmlDocument.open(path);
    addTearDown(saved.close);
    expect(saved.info()['parts'], hasLength(16));
  });

  test('binary parts and empty parts are accepted', () {
    final doc = IdmlDocument.fromBytes(bytes);
    addTearDown(doc.close);
    final data = Uint8List.fromList([0, 1, 2, 0, 255]);

    doc.writePart('Extra/data.bin', data);
    doc.writePart('Extra/empty.bin', Uint8List(0));

    expect(doc.readPart('Extra/data.bin'), data);
    expect(doc.readPart('Extra/empty.bin'), isEmpty);
  });

  test('failures are IdmlExceptions with the status codes of the library', () {
    expect(
      () => IdmlDocument.fromBytes(utf8.encode('this is not a zip file')),
      throwsA(isA<IdmlException>()
          .having((error) => error.code, 'code', CoreIdmlStatus.invalidFormat)
          // The text of .NET's own message, not a resource key such as "CDCorrupt".
          .having((error) => error.message, 'message', matches(RegExp(r'^Not an IDML package: \S+ \S+')))),
    );
    expect(() => IdmlDocument.open('${Directory.systemTemp.path}/no-such-file.idml'), throwsIdml(CoreIdmlStatus.io));

    final doc = IdmlDocument.fromBytes(bytes);
    addTearDown(doc.close);
    expect(() => doc.readPart('Stories/Nope.xml'), throwsIdml(CoreIdmlStatus.notFound));
    expect(() => doc.writeText(r'bad\path.xml', 'x'), throwsIdml(CoreIdmlStatus.invalidArgument));
    // A failure does not poison the document.
    expect(doc.info()['parts'], hasLength(16));
  });

  test('a closed document refuses calls, and closing twice is harmless', () {
    final doc = IdmlDocument.fromBytes(bytes);

    doc.close();
    doc.close();

    expect(doc.closed, isTrue);
    expect(doc.info, throwsIdml(CoreIdmlStatus.invalidHandle));
  });
}
