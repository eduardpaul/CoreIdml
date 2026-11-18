# CoreIdml

**Read, edit and write Adobe InDesign IDML files from .NET, without InDesign.**

IDML is InDesign's open interchange format: a zip package of XML parts (spreads, stories, styles, the manifest that
ties them together). CoreIdml opens that package, gives you every part as live LINQ to XML, and writes it back so
that InDesign opens the result as if nothing but your edit had happened.

```sh
dotnet add package CoreIdml
```

```csharp
using CoreIdml;

var document = IdmlDocument.Open("brochure.idml");

foreach (var path in document.StoryPaths)
{
    var story = document.Package.GetXml(path);                       // live LINQ to XML
    foreach (var content in story.Descendants("Content").Where(c => c.Value.Contains("2025")).ToList())
    {
        content.Value = content.Value.Replace("2025", "2026");
    }
}

document.Save("brochure-2026.idml");                                 // only the stories that changed are rewritten
```

## Why CoreIdml

- **Surgical edits.** A part you do not touch is written back byte for byte. Change one story, and the designer's
  file differs in exactly that story.
- **Output InDesign accepts.** `mimetype` first and uncompressed, InDesign's own formatting for edited parts, tabs
  and line breaks inside attribute values kept.
- **Reproducible files.** Untouched parts keep their timestamps and compression; give edits a fixed timestamp and
  an edited document saves to the same bytes every time.
- **Safe with files from anywhere.** Checksums verified on read, document type declarations refused, one exception
  type with a reason you can switch on.
- **No dependencies, no reflection.** Safe for trimming, Native AOT and Blazor WebAssembly; the analyzers for all
  three run on every build of the library.

## Using it

Open from a path, from memory or from a stream; save the same three ways.

```csharp
var fromFile = IdmlDocument.Open("brochure.idml");
var fromBytes = IdmlDocument.Open(bytes);                            // byte[] or ReadOnlyMemory<byte>, not copied first
var fromUpload = await IdmlDocument.OpenAsync(request.Body, cancellationToken);

document.Save("out.idml");
byte[] saved = document.ToArray();
await document.SaveAsync(response.Body, cancellationToken: cancellationToken);
```

See what a document holds. `designmap.xml`, the manifest, is read for you:

```csharp
Console.WriteLine($"DOM {document.DomVersion}: {document.SpreadPaths.Count} spreads, {document.StoryPaths.Count} stories");

foreach (var path in document.Package.PartPaths)
{
    Console.WriteLine($"{path}  {document.Package.GetBytes(path).Length:N0} bytes  modified: {document.Package.IsModified(path)}");
}
```

Work on any part. A part read with `GetXml` is tracked: changing it is all it takes for it to be saved.

```csharp
var package = document.Package;

XDocument styles = package.GetXml("Resources/Styles.xml");           // parsed on first use, whitespace kept
ReadOnlyMemory<byte> raw = package.GetBytes("Spreads/Spread_ud6.xml"); // exactly what would be written now

package.SetXml("Stories/Story_new.xml", new XDocument(new XElement("Story")));
package.SetBytes("Links/logo.png", pngBytes);
package.Remove("XML/Mapping.xml");
```

Make saves reproducible, for golden files and content-addressed storage:

```csharp
byte[] stable = document.ToArray(new IdmlSaveOptions { Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });
```

Handle what can go wrong:

```csharp
try
{
    var uploaded = IdmlDocument.Open(bytes);
}
catch (IdmlException exception) when (exception.Kind == IdmlErrorKind.InvalidFormat)
{
    // Not a zip, no designmap.xml, a damaged entry, malformed XML: exception.Message says which.
}
```

`IdmlErrorKind` is `InvalidFormat`, `NotFound`, `Unsupported` or `InvalidOperation`. Bad arguments are
`ArgumentException` and file problems are `IOException`, as elsewhere in .NET.

## Good to know

- Requires .NET 11.
- A document is not thread-safe; use one per thread, or lock.
- **Scope of 0.1.** The library works at the package level: every part as XML or bytes, and full control over what
  is written. A typed document model on top of it (pages, frames, stories, styles) is in development.
- The same engine is available for Node.js, the browser, Python and Dart/Flutter:
  [github.com/eduardpaul/CoreIdml](https://github.com/eduardpaul/CoreIdml).

MIT licensed. CoreIdml is an independent project and is not affiliated with Adobe.
