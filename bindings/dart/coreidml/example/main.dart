// dart run example/main.dart <template.idml> <output.idml> [output.pdf]
//
// Fills a template designed in InDesign whose frames are named (Layers panel), labelled or tagged "name" and "date",
// or that holds {{name}} and {{date}} placeholders; saves it, and renders it to PDF on a background isolate.
import 'package:coreidml/coreidml.dart';

Future<void> main(List<String> args) async {
  if (args.length < 2) {
    print('usage: dart run example/main.dart <template.idml> <output.idml> [output.pdf]');
    return;
  }
  final doc = IdmlDocument.open(args[0]);
  try {
    final report = doc.fill({
      'text': {'name': 'Ana Pérez', 'date': '17/11/2026'},
    });
    print('filled: ${report['filled']}, missing: ${report['missing']}');
    doc.saveFile(args[1]);

    if (args.length > 2) {
      final pdf = await doc.exportPdfInBackground(args[2],
          options: {'documentPath': args[0]}, progress: (done, total) => print('page $done of $total'));
      for (final font in pdf['missingFonts'] as List) {
        print('${font['requestedFamily']} ${font['requestedStyle']} was replaced by ${font['usedFont']}');
      }
    }
  } on IdmlException catch (error) {
    print('failed (${error.code}): ${error.message}');
  } finally {
    doc.close();
  }
}
