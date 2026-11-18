/// Create, read and edit Adobe InDesign IDML files, through the Native AOT build of the CoreIdml library.
///
/// ```dart
/// final doc = IdmlDocument.open('template.idml');
/// try {
///   final story = (doc.info()['stories'] as List).first as String;
///   doc.writeText(story, doc.readText(story).replaceAll('Jean', 'Ana'));
///   doc.saveFile('out.idml');
/// } finally {
///   doc.close();
/// }
/// ```
library;

export 'src/document.dart' show IdmlDocument;
export 'src/ffi/bindings.g.dart' show CoreIdmlStatus, coreIdmlAbiVersion;
export 'src/library.dart' show CoreIdml, IdmlException;
