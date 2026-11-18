using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CoreIdml.Dom;
using CoreIdml.Json;
using CoreIdml.Pdf;

namespace CoreIdml.Benchmarks;

/// <summary>
/// Writes the outputs of every scenario (IDML saves, PDFs, measurements) and a manifest of their SHA-256 hashes, so a
/// change can be checked for byte-identical output: run it before and after, then compare the manifests. Nothing is
/// kept in the repository (the studio sample must not be committed, nor anything derived from it).
/// IDML zips carry the save time in every entry, so they are recorded entry by entry (name, sizes, CRC, compressed
/// bytes) without timestamps.
/// </summary>
public static partial class Golden
{
    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var manifest = new StringBuilder();

        void Record(string name, byte[] data)
        {
            File.WriteAllBytes(Path.Combine(outputDirectory, name), data);
            manifest.Append(Convert.ToHexStringLower(SHA256.HashData(data))).Append("  ").Append(name).Append('\n');
            Console.WriteLine($"{name,-50} {data.Length,12:N0} bytes");
        }

        foreach (var (name, path) in Corpus.Documents)
        {
            var data = File.ReadAllBytes(path);
            using (var document = IdmlDocument.Open(data))
            {
                Record(name + ".unchanged.idml.txt", DescribeZip(document.ToArray()));
            }
            using (var document = IdmlDocument.Open(data))
            {
                Corpus.Edit(document);
                Record(name + ".edited.idml.txt", DescribeZip(document.ToArray()));
                Record(name + ".edited.pdf", Pdf(document, Corpus.ExportOptions(path)));
            }
            using (var document = IdmlDocument.Open(data))
            {
                Record(name + ".summary.json", Encoding.UTF8.GetBytes(IdmlJson.SummarizeToJson(document, new SummaryOptions { IncludeRuns = true })));
                Record(name + ".pdf", Pdf(document, Corpus.ExportOptions(path)));
                Record(name + ".system-fonts.pdf", Pdf(document, Corpus.ExportOptions(path, systemFonts: true)));
                Record(name + ".max-dpi.pdf", Pdf(document, Capped(Corpus.ExportOptions(path))));
            }
        }

        using (var demo = Corpus.FichaDemo())
        {
            Record("ficha-demo.idml.txt", DescribeZip(demo.ToArray()));
            Record("ficha-demo.pdf", Pdf(demo, Corpus.ExportOptions()));
            var uncompressed = Corpus.ExportOptions();
            uncompressed.CompressContent = false;
            Record("ficha-demo.uncompressed.pdf", Pdf(demo, uncompressed));
        }

        using (var report = Corpus.Report(Corpus.Photos.Count > 0 ? 4 : 2, Corpus.Fonts()))
        {
            Record("report.idml.txt", DescribeZip(report.ToArray()));
            Record("report.pdf", Pdf(report, Corpus.ExportOptions()));
            Record("report.max-dpi.pdf", Pdf(report, Capped(Corpus.ExportOptions())));
        }

        if (Corpus.Logo is not null)
        {
            // A fixed folder: the paths end up in the PDF.
            using var images = ImageCases(Path.Combine(Path.GetTempPath(), "idml-golden-image-cases"));
            Record("image-cases.pdf", Pdf(images, Corpus.ExportOptions()));
        }

        Record("measures.txt", Encoding.UTF8.GetBytes(Measures()));
        File.WriteAllText(Path.Combine(outputDirectory, "manifest.sha256"), manifest.ToString());
        Console.WriteLine($"Manifest: {Path.Combine(outputDirectory, "manifest.sha256")}");
        return 0;
    }

    /// <summary>The opt-in image cap (re-encoded photos): its output may change, unlike the defaults, if it looks the same.</summary>
    private static PdfExportOptions Capped(PdfExportOptions options)
    {
        options.MaxImageDpi = Corpus.CappedDpi;
        return options;
    }

    private static byte[] Pdf(IdmlDocument document, PdfExportOptions options)
    {
        using var output = new MemoryStream();
        var result = IdmlPdf.Export(document, output, options);
        // The report is part of the output too.
        var report = new StringBuilder();
        report.Append(CultureInfo.InvariantCulture, $"\n%pages {result.PageCount}\n");
        foreach (var substitution in result.FontSubstitutions)
        {
            report.Append(CultureInfo.InvariantCulture, $"%font {substitution} [{string.Join(',', substitution.Stories)}] [{string.Join(',', substitution.Pages)}]\n");
        }
        foreach (var line in result.MissingImages.Concat(result.OversetStories).Concat(result.Warnings))
        {
            report.Append("%note ").Append(line).Append('\n');
        }
        output.Write(Encoding.UTF8.GetBytes(report.ToString()));
        return MaskBuild(output.ToArray());
    }

    /// <summary>The producer names the library build ("0.1.0+&lt;commit&gt;"): blank the commit, keeping the length.</summary>
    private static byte[] MaskBuild(byte[] pdf)
    {
        var version = IdmlDocument.LibraryVersion;
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return pdf;
        }
        var at = pdf.AsSpan().IndexOf(Encoding.ASCII.GetBytes("CoreIdml.Pdf " + version));
        if (at >= 0)
        {
            pdf.AsSpan(at + "CoreIdml.Pdf ".Length + plus + 1, version.Length - plus - 1).Fill((byte)'0');
        }
        return pdf;
    }

    /// <summary>Every entry of an IDML zip in order: name, method, sizes, CRC and the compressed bytes' hash.</summary>
    private static byte[] DescribeZip(byte[] zip)
    {
        var builder = new StringBuilder();
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var content = new MemoryStream();
            stream.CopyTo(content);
            var bytes = content.ToArray();
            var text = Encoding.UTF8.GetString(bytes);
            if (Timestamp().IsMatch(text))
            {
                // Placing an image records the current time in its link: compare the part without it.
                var normalized = Timestamp().Replace(text, "0000-00-00T00:00:00");
                builder.Append(CultureInfo.InvariantCulture, $"{entry.FullName} (timestamped) {entry.Length} {Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))}\n");
                continue;
            }
            builder.Append(CultureInfo.InvariantCulture, $"{entry.FullName} {entry.CompressedLength} {entry.Length} {entry.Crc32:X8} {Convert.ToHexStringLower(SHA256.HashData(bytes))}\n");
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Placed images that fail in different ways (truncated PNG, a JPEG named .png, garbage, a missing file) between
    /// good ones, twice each: what is written and in which order must not depend on how images are read.
    /// </summary>
    private static IdmlDocument ImageCases(string directory)
    {
        Directory.CreateDirectory(directory);
        var good = File.ReadAllBytes(Corpus.Logo!);
        var cases = new List<(string Name, byte[]? Data)>
        {
            ("good.png", good),
            ("truncated.png", good[..(good.Length / 2)]),
            ("garbage.png", [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3]),
            ("missing.png", null),
        };
        if (Corpus.Photos.Count > 0)
        {
            cases.Add(("photo-named.png", File.ReadAllBytes(Corpus.Photos[0])));
        }
        // Placed while every file is a good PNG (placing reads the image size), then damaged.
        var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4, PageCount = 2 });
        for (var page = 0; page < 2; page++)
        {
            for (var i = 0; i < cases.Count; i++)
            {
                var path = Path.Combine(directory, cases[i].Name);
                File.WriteAllBytes(path, good);
                document.Pages[page].AddImage(new Rect(20 + (i * 60), 20, 50, 50), path, ImageFit.Fit);
            }
        }
        foreach (var (name, data) in cases)
        {
            var path = Path.Combine(directory, name);
            if (data is null)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllBytes(path, data);
            }
        }
        return document;
    }

    /// <summary>TextMeasurer results for texts of every length at several widths, as round-trip numbers.</summary>
    private static string Measures()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var styles = new[]
        {
            document.Styles.AddParagraphStyle("Valor", new ParagraphFormat { FontFamily = "Raleway", PointSize = 7.5, Leading = 9, Justification = Justification.LeftJustified, Hyphenation = true, Language = Languages.Spanish }),
            document.Styles.AddParagraphStyle("Título", new ParagraphFormat { FontFamily = "Montserrat", FontStyle = "SemiBold", PointSize = 14, Capitalization = Capitalization.AllCaps }),
            document.Styles.AddParagraphStyle("Serif", new ParagraphFormat { FontFamily = "Minion Pro", PointSize = 10, Justification = Justification.FullyJustified }),
        };
        var scratch = document.Pages[0].AddTextFrame(new Rect(0, 0, 100, 20));
        using var measurer = new TextMeasurer(document, Corpus.Fonts());
        var builder = new StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            var text = Corpus.Field(i / 24, (i / 6) % 4, i % 6);
            var style = styles[i % styles.Length];
            var width = 40 + (i % 9 * 30);
            scratch.SetBounds(new Rect(0, 0, width, 20));
            scratch.Inset = Spacing.All(i % 3);
            scratch.Story.SetText(text, style);
            var measure = measurer.Measure(scratch);
            builder.Append(CultureInfo.InvariantCulture, $"{i} {measure.Width:R} {measure.Height:R} {measure.Lines}\n");
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d")]
    private static partial Regex Timestamp();
}
