// Creating, reading and editing documents through the document model of the library.
import 'dart:io';

import 'package:coreidml/coreidml.dart';
import 'package:test/test.dart';

import 'support.dart';

void main() {
  group('creating', () {
    test('a document is built from a spec, filled and saved', () {
      final doc = IdmlDocument.create({
        'unit': 'mm',
        'page': {'size': 'A4'},
        'paragraphStyles': [
          {'name': 'Título', 'size': 20, 'fontStyle': 'Bold'},
        ],
        'pages': [
          {
            'items': [
              {'type': 'text', 'name': 'titulo', 'x': 15, 'y': 15, 'width': 180, 'height': 15, 'text': 'Ficha {{codigo}}', 'style': 'Título'},
              {'type': 'text', 'name': 'cliente', 'x': 15, 'y': 35, 'width': 180, 'height': 10, 'text': '-'},
              {
                'type': 'table', 'x': 15, 'y': 50, 'width': 180, 'height': 40, 'headerRows': 1,
                'rows': [
                  ['Campo', 'Valor'],
                  ['Altura', '12 m'],
                ],
              },
            ],
          },
        ],
      });
      addTearDown(doc.close);

      final report = doc.fill({
        'text': {'codigo': 'F-001', 'cliente': 'ACME', 'nada': '?'},
      });

      expect(report['filled'], {'codigo': 1, 'cliente': 1, 'nada': 0});
      expect(report['missing'], ['nada']);
      final summary = doc.summary();
      final items = (summary['pages'] as List).single['items'] as List;
      expect(items.map((item) => item['text']), containsAll(['Ficha F-001', 'ACME']));
      expect(summary['paragraphStyles'], contains('Título'));
      expect(doc.validate(), {'isValid': true, 'issues': <Object?>[]});

      final path = '${tempFolder().path}/ficha.idml';
      doc.saveFile(path);
      final reopened = IdmlDocument.open(path);
      addTearDown(reopened.close);
      expect(reopened.replaceText('ACME', 'ACME S.A.'), 1);
      expect(reopened.replaceText('nowhere to be found', 'x'), 0);

      final copy = IdmlDocument.fromBytes(reopened.save());
      addTearDown(copy.close);
      expect(storyTexts(copy), contains('ACME S.A.'));
    });

    test('without a spec a document is one blank A4 page, and a spec may be JSON text', () {
      final blank = IdmlDocument.create();
      addTearDown(blank.close);
      final two = IdmlDocument.create('{"pages": [{}, {}]}');
      addTearDown(two.close);

      final summary = blank.summary();
      expect(summary['pageCount'], 1);
      expect(summary['pageWidth'], closeTo(595.28, 0.01));
      expect(summary['pageHeight'], closeTo(841.89, 0.01));
      expect((summary['pages'] as List).single['items'], isEmpty);
      expect(two.summary()['pageCount'], 2);
    });

    test('apply adds styles and appends pages', () {
      final doc = pages(1);
      addTearDown(doc.close);

      doc.apply({
        'paragraphStyles': [
          {'name': 'Nota', 'size': 8},
        ],
        'pages': [
          {
            'items': [
              {'type': 'text', 'x': 50, 'y': 50, 'width': 300, 'height': 40, 'text': 'Añadida', 'style': 'Nota'},
            ],
          },
        ],
      });
      doc.apply('{"pages": [{}]}');

      final summary = doc.summary();
      expect(summary['pageCount'], 3);
      expect(summary['paragraphStyles'], contains('Nota'));
      expect(storyTexts(doc), ['Página 1', 'Añadida']);
    });

    test('a spec the library refuses is an IdmlException with its message', () {
      expect(
        () => IdmlDocument.create({
          'pages': [
            {
              'items': [
                {'type': 'text', 'text': 'x', 'style': 'Nope'},
              ],
            },
          ],
        }),
        throwsA(isA<IdmlException>()
            .having((error) => error.code, 'code', CoreIdmlStatus.notFound)
            .having((error) => error.message, 'message', contains('Nope'))),
      );
      expect(() => IdmlDocument.create('{not json'), throwsIdml(CoreIdmlStatus.invalidArgument));
      expect(() => IdmlDocument.create([1, 2]), throwsIdml(CoreIdmlStatus.invalidArgument));
      final doc = IdmlDocument.create();
      addTearDown(doc.close);
      expect(() => doc.apply('{not json'), throwsIdml(CoreIdmlStatus.invalidArgument));
      expect(doc.summary()['pageCount'], 1);
    });

    test('fill places images by frame name', () {
      final folder = tempFolder();
      final first = File('${folder.path}/first.png')..writeAsBytesSync(png(40, 30));
      final second = File('${folder.path}/second.png')..writeAsBytesSync(png(30, 40));
      final doc = IdmlDocument.create({
        'pages': [
          {
            'items': [
              {'type': 'image', 'name': 'foto', 'path': first.path, 'x': 10, 'y': 10, 'width': 100, 'height': 100},
            ],
          },
        ],
      });
      addTearDown(doc.close);

      final report = doc.fill({
        'images': {'foto': second.path},
      });

      expect(report['filled'], {'foto': 1});
      expect(((doc.summary()['links'] as List).single as Map)['uri'], endsWith('second.png'));
      expect(() => doc.fill({'images': {'foto': '${folder.path}/missing.png'}}), throwsIdml(CoreIdmlStatus.io));
    });
  });

  group('reading', () {
    test('summary options choose what is described', () {
      final doc = IdmlDocument.open(fixture('article-1photo.idml').path);
      addTearDown(doc.close);

      final full = doc.summary();
      expect(full.keys, containsAll(['pageCount', 'pages', 'offPage', 'stories', 'links', 'paragraphStyles', 'swatches', 'tags']));
      expect((full['stories'] as List).first, isNot(contains('runs')));

      final short = doc.summary({'maxTextLength': 5});
      final headline = (short['stories'] as List).singleWhere((story) => story['id'] == 'u19f');
      expect((headline['text'] as String).length, lessThanOrEqualTo(6));

      final runs = doc.summary({'includeRuns': true, 'includeResources': false});
      expect(runs.containsKey('paragraphStyles'), isFalse);
      expect((runs['stories'] as List).first, contains('runs'));

      final stories = doc.summary({'sections': ['stories']});
      expect(stories.containsKey('pages'), isFalse);
      expect(stories.containsKey('links'), isFalse);
      expect(stories['stories'], hasLength(3));

      expect(() => doc.summary({'maxTextLength': 'long'}), throwsIdml(CoreIdmlStatus.invalidArgument));
    });

    test('summary pages picks pages by index', () {
      final doc = pages(3);
      addTearDown(doc.close);

      final summary = doc.summary({'pages': [0, 2]});

      expect((summary['pages'] as List).map((page) => page['index']), [0, 2]);
      expect(summary['pageCount'], 3);
    });

    test('story describes every content run; setRunText edits one and keeps its formatting', () {
      final doc = IdmlDocument.open(fixture('article-1photo.idml').path);
      addTearDown(doc.close);

      final story = doc.story('u188');
      expect(story['text'], 'THE HEADLINE HERE');
      expect(story['frames'], ['u185']);
      final run = (story['runs'] as List).single as Map;
      expect(run, {'index': 0, 'text': 'THE HEADLINE HERE', 'paragraphStyle': '[Basic Paragraph]', 'characterStyle': '[No character style]', 'scope': 'body'});

      doc.setRunText('u188', 0, 'Un titular nuevo');
      expect(((doc.story('u188')['runs'] as List).single as Map)['text'], 'Un titular nuevo');
      expect(((doc.story('u188')['runs'] as List).single as Map)['paragraphStyle'], '[Basic Paragraph]');
      doc.setRunText('u188', 0, 'Forzado', force: true);
      expect(doc.story('u188')['text'], 'Forzado');

      expect(() => doc.story('u-none'), throwsIdml(CoreIdmlStatus.notFound));
      expect(
        () => doc.setRunText('u188', 7, 'x'),
        throwsA(isA<IdmlException>()
            .having((error) => error.code, 'code', CoreIdmlStatus.notFound)
            .having((error) => error.message, 'message', contains('run index 7'))),
      );
      expect(() => doc.setRunText('u-none', 0, 'x'), throwsIdml(CoreIdmlStatus.notFound));
    });

    test('table cells are runs with their own scope', () {
      final doc = IdmlDocument.create({
        'pages': [
          {
            'items': [
              {
                'type': 'table', 'x': 36, 'y': 36, 'width': 300, 'height': 60,
                'rows': [
                  ['A', 'B'],
                ],
              },
            ],
          },
        ],
      });
      addTearDown(doc.close);
      final storyId = ((doc.summary()['stories'] as List).single as Map)['id'] as String;

      final runs = doc.story(storyId)['runs'] as List;

      expect(runs.map((run) => run['text']), ['A', 'B']);
      expect(runs.map((run) => run['scope']), everyElement(startsWith('cell ')));
    });

    test('validate reports a sound document as valid', () {
      final doc = IdmlDocument.open(fixture('magazineA-edito.idml').path);
      addTearDown(doc.close);

      final report = doc.validate();

      expect(report['isValid'], isTrue);
      expect(report['issues'], isEmpty);
    });

    test('diff compares two documents run by run', () {
      final before = IdmlDocument.open(fixture('article-1photo.idml').path);
      addTearDown(before.close);
      final after = IdmlDocument.open(fixture('article-1photo.idml').path);
      addTearDown(after.close);
      expect(before.diff(after), {'identical': true, 'changes': <Object?>[]});

      after.setRunText('u188', 0, 'A new headline');
      after.importPages(after);
      final diff = before.diff(after);

      expect(diff['identical'], isFalse);
      final changes = (diff['changes'] as List).cast<Map<String, dynamic>>();
      expect(changes, contains(allOf(containsPair('kind', 'run-changed'), containsPair('id', 'u188'), containsPair('after', 'A new headline'))));
      expect(changes, contains(allOf(containsPair('kind', 'page-count'), containsPair('before', '1'), containsPair('after', '2'))));
    });
  });

  group('links', () {
    test('relink points a link at another file, and refits a local image', () {
      final doc = IdmlDocument.open(fixture('article-1photo.idml').path);
      addTearDown(doc.close);
      final link = (doc.summary()['links'] as List).single as Map;
      expect(link['id'], 'u21a');
      expect(link['uri'], endsWith('default.jpg'));

      doc.relink('u21a', 'file:///photos/new.jpg');
      expect(((doc.summary()['links'] as List).single as Map)['uri'], 'file:///photos/new.jpg');

      final photo = File('${tempFolder().path}/photo.png')..writeAsBytesSync(png(40, 30));
      doc.relink('u21a', photo.path, refit: true);
      final relinked = (doc.summary()['links'] as List).single as Map;
      expect(relinked['uri'], endsWith('photo.png'));
      expect(relinked['format'], contains('PNG'));
    });

    test('relinking a link that does not exist fails with notFound', () {
      final doc = IdmlDocument.open(fixture('article-1photo.idml').path);
      addTearDown(doc.close);

      expect(() => doc.relink('u-none', '/x.jpg'), throwsIdml(CoreIdmlStatus.notFound));
    });
  });

  group('XML', () {
    test('tagged frames are filled from XML, exported and edited run by run', () {
      final doc = IdmlDocument.create({
        'pages': [
          {
            'items': [
              {'type': 'text', 'tag': 'nombre', 'x': 36, 'y': 36, 'width': 300, 'height': 30, 'text': '-'},
            ],
          },
        ],
      });
      addTearDown(doc.close);

      final report = doc.importXml('<Root><nombre>Ana Pérez</nombre></Root>');

      expect(report['unmatched'], isEmpty);
      expect(report['warnings'], isEmpty);
      expect(doc.exportXml(at: '/Root/nombre'), '<nombre>Ana Pérez</nombre>');
      expect((doc.xmlStructure()!['children'] as List).single['path'], '/Root/nombre');

      final summary = doc.summary({'includeRuns': true, 'includeResources': false});
      expect(summary.containsKey('paragraphStyles'), isFalse);
      final storyId = ((summary['pages'] as List).single['items'] as List).single['storyId'] as String;
      expect((doc.story(storyId)['runs'] as List).single['text'], 'Ana Pérez');
      doc.setRunText(storyId, 0, 'Luis');
      expect(doc.exportXml(at: '/Root/nombre'), '<nombre>Luis</nombre>');
      expect(doc.exportXml(), '<Root><nombre>Luis</nombre></Root>');
    });

    test('SimpleIDML XML is imported onto a template element', () {
      final doc = IdmlDocument.open(fixture('article-1photo_import-xml.idml').path);
      addTearDown(doc.close);
      final xml = fixture('xml/article-1photo_import-xml.xml').readAsStringSync();

      final report = doc.importXml(xml, at: '/Root/module[1]');

      expect(report['unmatched'], isEmpty);
      expect(report['added'], 3); // <bold>, <italique>, <italique>
      expect(doc.exportXml(at: '/Root/module/headline'), '<headline>The Life Aquatic with Steve Zissou</headline>');
      final structure = doc.xmlStructure()!;
      expect(structure['tag'], 'Root');
      final module = (structure['children'] as List).single as Map;
      expect((module['children'] as List).map((child) => child['tag']), ['main_picture', 'headline', 'Story']);
      expect(((module['children'] as List).first as Map)['attributes'], {'href': 'file:../../IDML/media/bouboune.jpg'});
    });

    test('import options are passed on: relinkOnly relinks href images without measuring them', () {
      final doc = IdmlDocument.open(fixture('article-1photo_import-xml.idml').path);
      addTearDown(doc.close);
      final xml = fixture('xml/article-1photo_import-xml.xml').readAsStringSync();

      final report = doc.importXml(xml, at: '/Root/module[1]', options: {'relinkOnly': true, 'fit': 'fit'});

      expect(report['unmatched'], isEmpty);
      expect(((doc.summary()['links'] as List).single as Map)['uri'], 'file:../../IDML/media/bouboune.jpg');
      expect(() => doc.importXml(xml, at: '/Root/module[1]', options: {'relinkOnly': 'yes'}), throwsIdml(CoreIdmlStatus.invalidArgument));
    });

    test('XML errors name what is wrong', () {
      final doc = IdmlDocument.create();
      addTearDown(doc.close);

      expect(() => doc.importXml('<Root><nombre>'), throwsA(isA<IdmlException>().having((error) => error.message, 'message', contains('not well-formed'))));
      expect(() => doc.importXml('<x/>', at: '/Root/zzz'), throwsIdml(CoreIdmlStatus.notFound));
      expect(() => doc.exportXml(at: '/Root/zzz'), throwsIdml(CoreIdmlStatus.notFound));
    });

    test('xmlStructure is null for a document without XML structure', () {
      final doc = IdmlDocument.create();
      addTearDown(doc.close);
      expect(doc.xmlStructure(), {'tag': 'Root', 'id': 'di2', 'path': '/Root'});
      expect(doc.exportXml(), '<Root />');

      const backingStory = 'XML/BackingStory.xml';
      doc.writeText(backingStory, doc.readText(backingStory).replaceAll(RegExp(r'<XMLElement[^>]*/>'), ''));

      expect(doc.xmlStructure(), isNull);
    });
  });

  group('importing pages', () {
    test('pages come from another document, or from the document itself', () {
      final doc = pages(2);
      addTearDown(doc.close);
      final other = pages(3);
      addTearDown(other.close);

      expect(doc.importPages(other, [2]), 1);
      expect(doc.importPages(other), 3);
      expect(doc.importPages(doc, [0, 1]), 2);

      expect(doc.summary()['pageCount'], 8);
      expect(storyTexts(doc), ['Página 1', 'Página 2', 'Página 3', 'Página 1', 'Página 2', 'Página 3', 'Página 1', 'Página 2']);
      expect(doc.validate(), {'isValid': true, 'issues': <Object?>[]});
      final ids = (doc.summary()['stories'] as List).map((story) => story['id']);
      expect(ids.toSet(), hasLength(ids.length), reason: 'imported stories get their own ids');
    });

    test('a page listed twice is imported twice', () {
      final doc = pages(1);
      addTearDown(doc.close);

      expect(doc.importPages(doc, [0, 0]), 2);

      expect(storyTexts(doc), ['Página 1', 'Página 1', 'Página 1']);
      expect(doc.validate(), {'isValid': true, 'issues': <Object?>[]});
    });

    test('a page index that does not exist fails with notFound', () {
      final doc = pages(1);
      addTearDown(doc.close);

      expect(() => doc.importPages(doc, [5]), throwsIdml(CoreIdmlStatus.notFound));
      expect(doc.summary()['pageCount'], 1);
    });

    test('a closed source is refused', () {
      final doc = pages(1);
      addTearDown(doc.close);
      final source = pages(1)..close();

      expect(() => doc.importPages(source), throwsIdml(CoreIdmlStatus.invalidHandle));
      expect(() => doc.diff(source), throwsIdml(CoreIdmlStatus.invalidHandle));
    });
  });
}
