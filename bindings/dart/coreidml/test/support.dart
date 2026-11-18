// Shared by the tests: the fixtures every binding uses, and small helpers.
import 'dart:io';
import 'dart:typed_data';

import 'package:coreidml/coreidml.dart';
import 'package:test/test.dart';

/// `tests/fixtures` of the repository, found above the current directory.
final Directory fixtures = () {
  for (var directory = Directory.current; directory.parent.path != directory.path; directory = directory.parent) {
    final candidate = Directory('${directory.path}/tests/fixtures');
    if (File('${candidate.path}/article-1photo.idml').existsSync()) return candidate;
  }
  throw StateError('tests/fixtures/article-1photo.idml not found above ${Directory.current.path}');
}();

/// A fixture by its path under `tests/fixtures`.
File fixture(String name) => File('${fixtures.path}/$name');

/// The Liberation fonts of the fixtures, and nothing from the machine, so that PDF output is the same everywhere.
Map<String, Object?> get liberationFonts => {
      'useSystemFonts': false,
      'fontDirectories': [fixture('fonts').path],
    };

/// A temporary folder deleted after the test.
Directory tempFolder() {
  final folder = Directory.systemTemp.createTempSync('coreidml-');
  addTearDown(() => folder.deleteSync(recursive: true));
  return folder;
}

Matcher throwsIdml(int code) => throwsA(isA<IdmlException>().having((error) => error.code, 'code', code));

/// A document whose pages each hold one text frame, "Página 1", "Página 2"...
IdmlDocument pages(int count) => IdmlDocument.create({
      'pages': [
        for (var i = 1; i <= count; i++)
          {
            'items': [
              {'type': 'text', 'x': 50, 'y': 50, 'width': 300, 'height': 40, 'text': 'Página $i'},
            ],
          },
      ],
    });

/// All the text of a document's stories.
List<String> storyTexts(IdmlDocument document) =>
    [for (final story in document.summary({'maxTextLength': -1})['stories'] as List) story['text'] as String];

/// A small solid-colour RGB PNG, written without any image package.
Uint8List png(int width, int height) {
  int crc(List<int> bytes) {
    var c = 0xffffffff;
    for (final byte in bytes) {
      c ^= byte;
      for (var k = 0; k < 8; k++) {
        c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
      }
    }
    return c ^ 0xffffffff;
  }

  List<int> u32(int value) => [value >> 24 & 255, value >> 16 & 255, value >> 8 & 255, value & 255];
  List<int> chunk(String type, List<int> data) {
    final body = [...type.codeUnits, ...data];
    return [...u32(data.length), ...body, ...u32(crc(body))];
  }

  final pixels = [
    for (var y = 0; y < height; y++) ...[0, for (var x = 0; x < width; x++) ...[200, 30, 30]],
  ];
  return Uint8List.fromList([
    137, 80, 78, 71, 13, 10, 26, 10, //
    ...chunk('IHDR', [...u32(width), ...u32(height), 8, 2, 0, 0, 0]),
    ...chunk('IDAT', ZLibCodec().encode(pixels)),
    ...chunk('IEND', []),
  ]);
}
