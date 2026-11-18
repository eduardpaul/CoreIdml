import 'package:coreidml/coreidml.dart';
import 'package:flutter/services.dart';
import 'package:material_ui/material_ui.dart';

void main() {
  runApp(const CoreIdmlApp());
}

/// What the app shows: read from a document that was opened, edited and saved by the native library.
class Report {
  Report(this.version, this.domVersion, this.parts, this.stories, this.modified, this.savedBytes);

  final String version;
  final String? domVersion;
  final int parts;
  final List<String> stories;
  final List<String> modified;
  final int savedBytes;
}

/// Opens the bundled template, marks its first story, saves it and reopens what was saved.
Future<Report> roundTrip() async {
  final template = (await rootBundle.load('assets/article-1photo.idml')).buffer.asUint8List();
  final document = IdmlDocument.fromBytes(template);
  try {
    final info = document.info();
    final stories = (info['stories'] as List).cast<String>();
    document.writeText(stories.first, document.readText(stories.first).replaceFirst('<Story ', '<Story Marker="flutter" '));
    final saved = document.save();

    final reopened = IdmlDocument.fromBytes(saved);
    try {
      if (!reopened.readText(stories.first).contains('Marker="flutter"')) {
        throw StateError('The edit did not survive the round trip.');
      }
    } finally {
      reopened.close();
    }
    final modified = (document.info()['parts'] as List).where((part) => part['modified'] == true).map((part) => part['path'] as String).toList();
    return Report(CoreIdml.version, info['domVersion'] as String?, (info['parts'] as List).length, stories, modified, saved.length);
  } finally {
    document.close();
  }
}

class CoreIdmlApp extends StatelessWidget {
  const CoreIdmlApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'CoreIdml',
      home: Scaffold(
        appBar: AppBar(title: const Text('CoreIdml')),
        body: Padding(
          padding: const EdgeInsets.all(16),
          child: FutureBuilder<Report>(
            future: roundTrip(),
            builder: (context, snapshot) {
              if (snapshot.hasError) {
                return Text('Failed: ${snapshot.error}', key: const Key('error'));
              }
              final report = snapshot.data;
              if (report == null) {
                return const CircularProgressIndicator();
              }
              return Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('Library ${report.version}', key: const Key('version')),
                  Text('DOM version ${report.domVersion}', key: const Key('dom')),
                  Text('${report.parts} parts, ${report.stories.length} stories', key: const Key('counts')),
                  Text('Changed: ${report.modified.join(', ')}', key: const Key('modified')),
                  Text('Saved ${report.savedBytes} bytes', key: const Key('saved')),
                ],
              );
            },
          ),
        ),
      ),
    );
  }
}
