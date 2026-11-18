/// Create, read and edit Adobe InDesign IDML files, and render them to PDF, through the Native AOT build of the
/// CoreIdml library.
///
/// ```dart
/// final doc = IdmlDocument.open('template.idml');
/// try {
///   doc.fill({'text': {'name': 'Ana Pérez'}});
///   doc.saveFile('out.idml');
///   doc.exportPdf('out.pdf');
/// } finally {
///   doc.close();
/// }
/// ```
library;

export 'src/document.dart' show IdmlBackground, IdmlCancellation, IdmlDocument;
export 'src/ffi/bindings.g.dart' show CoreIdmlStatus, coreIdmlAbiVersion;
export 'src/library.dart' show CoreIdml, IdmlException;
