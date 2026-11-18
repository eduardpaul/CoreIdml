// Runs inside the app, on the device: the native library is loaded from the app's own bundle.
//
//   bash tool/prepare.sh
//   flutter test integration_test -d <device>      (an Android device or emulator, or linux)
import 'dart:convert';

import 'package:coreidml/coreidml.dart';
import 'package:coreidml_flutter_app/main.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('the app opens, edits and saves a document through the bundled library', (tester) async {
    await tester.pumpWidget(const CoreIdmlApp());
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('error')), findsNothing);
    expect(find.text('DOM version 10.0'), findsOneWidget);
    expect(find.text('16 parts, 3 stories'), findsOneWidget);
    expect(find.text('Changed: Stories/Story_u1db.xml'), findsOneWidget);
    expect(find.textContaining(RegExp(r'^Library \d+\.\d+\.\d+')), findsOneWidget);
  });

  testWidgets('the binding behaves in the app as it does everywhere else', (tester) async {
    final bytes = (await rootBundle.load('assets/article-1photo.idml')).buffer.asUint8List();
    expect(coreIdmlAbiVersion, 1);

    final document = IdmlDocument.fromBytes(bytes);
    addTearDown(document.close);
    document.writePart('Extra/data.bin', Uint8List.fromList([0, 1, 2, 0, 255]));
    expect(document.readPart('Extra/data.bin'), [0, 1, 2, 0, 255]);
    expect(document.info()['parts'], hasLength(17));

    Matcher failsWith(int code) => throwsA(isA<IdmlException>().having((error) => error.code, 'code', code));
    expect(() => document.readPart('Stories/Nope.xml'), failsWith(CoreIdmlStatus.notFound));
    expect(
      () => IdmlDocument.fromBytes(utf8.encode('this is not a zip file')),
      throwsA(isA<IdmlException>()
          .having((error) => error.code, 'code', CoreIdmlStatus.invalidFormat)
          .having((error) => error.message, 'message', matches(RegExp(r'^Not an IDML package: \S+ \S+')))),
    );

    document.close();
    expect(document.info, failsWith(CoreIdmlStatus.invalidHandle));
  });
}
