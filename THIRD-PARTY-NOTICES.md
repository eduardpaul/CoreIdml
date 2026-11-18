# Third-party notices

## SimpleIDML

The blank-document skeleton embedded in the library (`src/CoreIdml/Blank/*.xml`) is derived from
`tests/regressiontests/IDML/magazineA-template.idml` of SimpleIDML, an InDesign CS5.5 export, trimmed of its
content. The test fixtures `tests/fixtures/article-1photo.idml`, `magazineA-edito.idml`,
`article-1photo_import-xml.idml` and `article-1photo_imported-nested-xml.idml` are copied unchanged from the same
directory, and `tests/fixtures/xml/*.xml` from `tests/regressiontests/XML`. Several tests in `XmlStructureTests.cs`
port SimpleIDML's `import_xml` regression tests and their expectations. The fixtures are used by the tests only and
are not part of any shipped package.

Source: https://github.com/Starou/SimpleIDML

```
Copyright (c) 2012 Stanislas Guerra <stanislas.guerra@gmail.com>
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.
3. The name of the author may not be used to endorse or promote products
   derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## idmlkit

No code is copied from idmlkit (MIT, https://github.com/jimprivate/idmlkit). Its design ideas are reused: untouched
package parts are written back byte for byte, and documents can be summarized into compact JSON.

## PdfPig (CoreIdml.Pdf, and the native and WebAssembly builds)

`src/CoreIdml.Pdf` references the NuGet package PdfPig 0.1.16 (https://github.com/UglyToad/PdfPig) to read PDFs, and
the native library and WebAssembly build include it. Apache License 2.0; the licence text ships in the package.

## PDFium (the coreidml command line only)

`src/CoreIdml.Cli` references the NuGet packages bblanchon.PDFium.Linux, .Win32 and .macOS 152.0.7961
(https://github.com/bblanchon/pdfium-binaries, packaging under Apache-2.0) to rasterize page previews
(`idml_render_page`, `coreidml render`), and its release archives carry the PDFium library next to the binary. They
contain prebuilt PDFium (https://pdfium.googlesource.com/pdfium/, BSD-3-Clause and Apache-2.0) and the third-party
code it bundles (FreeType, libjpeg-turbo, OpenJPEG, lcms2, zlib, ICU and others under their own permissive licences;
the full list ships in the pdfium-binaries release archives). No library package uses it.

## pdf_oxide (tests only)

`tests/CoreIdml.Pdf.Tests` references the NuGet package PdfOxide 0.3.78 (https://github.com/yfedoseev/pdf_oxide) to
rasterize PDFs for the visual comparison. MIT OR Apache-2.0. Not part of any shipped package.

## Fonts in the tests

`tests/fixtures/fonts/Liberation*.ttf`: Liberation fonts 2.x, SIL Open Font License 1.1
(`tests/fixtures/fonts/LICENSE-Liberation.txt`). The standard-font width tables in
`src/CoreIdml.Pdf/Fonts/StandardFontMetrics.cs` were measured from these metric-compatible fonts. No commercial font
is included anywhere.

Adobe and InDesign are trademarks of Adobe Inc., used only to describe file-format compatibility.
