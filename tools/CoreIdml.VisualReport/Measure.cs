using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CoreIdml.Pdf;
using CoreIdml.Pdf.Imaging;
using CoreIdml.Pdf.Tests;

namespace CoreIdml.VisualReport;

/// <summary>Settings of a run that apply to every sample.</summary>
/// <param name="FallbackFonts">Fonts searched after the sample's own (the repository's Liberation fonts), or null.</param>
/// <param name="Dpi">Overrides sample.json and the default (96) when set.</param>
/// <param name="Tolerance">Overrides sample.json and the default (64) when set.</param>
/// <param name="KeepImages">Decides, per measured case, whether its PNGs stay on disk.</param>
internal sealed record MeasureSettings(string? FallbackFonts, int? Dpi, int? Tolerance, Func<string, CaseResult, bool> KeepImages);

/// <summary>Exports one sample with CoreIdml and measures every page against InDesign's reference.pdf.</summary>
internal static partial class Measure
{
    public static SampleResult Sample(VisualSample sample, MeasureSettings settings, string outDirectory)
    {
        var dpi = settings.Dpi ?? sample.Dpi;
        var tolerance = settings.Tolerance ?? sample.Tolerance;
        if (sample.Skip is not null)
        {
            return new SampleResult { Name = sample.Name, Dpi = dpi, Tolerance = tolerance, Skipped = sample.Skip };
        }
        if (sample.Idml is null || sample.ReferencePdf is null)
        {
            return new SampleResult { Name = sample.Name, Dpi = dpi, Tolerance = tolerance, Skipped = "no .idml and reference.pdf pair (import samples are not measured)" };
        }
        Directory.CreateDirectory(outDirectory);
        var referencePdf = File.ReadAllBytes(sample.ReferencePdf);
        byte[] ours;
        PdfExportResult export;
        var watch = Stopwatch.StartNew();
        try
        {
            using var document = IdmlDocument.Open(sample.Idml);
            var options = new PdfExportOptions { Fonts = sample.FontOptions(settings.FallbackFonts), DocumentPath = sample.Idml, Pages = sample.Pages };
            options.LinkDirectories.Add(Path.Combine(sample.Directory, "links"));
            foreach (var link in sample.LinkDirectories)
            {
                options.LinkDirectories.Add(Path.IsPathRooted(link) ? link : Path.Combine(sample.Directory, link));
            }
            using var stream = new MemoryStream();
            export = IdmlPdf.Export(document, stream, options);
            ours = stream.ToArray();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or IdmlException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new SampleResult { Name = sample.Name, Dpi = dpi, Tolerance = tolerance, Error = $"export: {exception.GetType().Name}: {exception.Message}" };
        }
        var exportMs = watch.Elapsed.TotalMilliseconds;
        File.WriteAllBytes(Path.Combine(outDirectory, "ours.pdf"), ours);

        var referencePages = Rasterizer.PageCount(referencePdf);
        var ourPages = Rasterizer.PageCount(ours);
        var pageNumbers = sample.Pages ?? Enumerable.Range(1, referencePages).ToList();
        var labels = Labels(referencePdf, sample.Name);
        var cases = new List<CaseResult>();
        for (var i = 0; i < pageNumbers.Count; i++)
        {
            var page = pageNumbers[i];
            labels.TryGetValue(page, out var label);
            var result = i < ourPages
                ? Page(referencePdf, ours, page, i, dpi, tolerance, label, outDirectory)
                : new CaseResult { Page = page, Label = label, MissingPage = true, PixelDiff = 1, MeanError = 1, Score = 0 };
            if (!settings.KeepImages(sample.Name, result))
            {
                foreach (var kind in (string[])["reference", "ours", "diff"])
                {
                    File.Delete(Path.Combine(outDirectory, string.Create(CultureInfo.InvariantCulture, $"p{page}-{kind}.png")));
                }
            }
            cases.Add(result);
        }
        return new SampleResult
        {
            Name = sample.Name,
            Dpi = dpi,
            Tolerance = tolerance,
            ReferencePages = referencePages,
            OurPages = ourPages,
            ExportMilliseconds = Math.Round(exportMs, 1),
            Issues = Issues(export),
            Cases = cases,
        };
    }

    private static CaseResult Page(byte[] referencePdf, byte[] ours, int page, int ourIndex, int dpi, int tolerance, string? label, string outDirectory)
    {
        var reference = Rasterizer.Render(referencePdf, page - 1, dpi);
        var actual = Rasterizer.Render(ours, ourIndex, dpi);
        string Png(string kind) => Path.Combine(outDirectory, string.Create(CultureInfo.InvariantCulture, $"p{page}-{kind}.png"));
        File.WriteAllBytes(Png("reference"), PngCodec.Encode(reference));
        File.WriteAllBytes(Png("ours"), PngCodec.Encode(actual));
        var regions = VisualDiff.RegionMap(referencePdf, page - 1, reference.Width, reference.Height);
        var diff = VisualDiff.Compare(page, reference, actual, tolerance, Png("diff"), regions);
        double Area(string name) => diff.Regions.TryGetValue(name, out var r) && r.Area > 0 ? (double)r.Differing / r.Area : 0;

        var text = TextPositionReport.Compare(referencePdf, ours, page, ourIndex + 1);
        var within = text.Matches.Count(m => Math.Sqrt((m.DX * m.DX) + (m.DY * m.DY)) <= 1.0);
        double? accuracy = text.ReferenceWords == 0 ? null : (double)within / text.ReferenceWords;
        var similarity = 1 - diff.DiffRatio;
        return new CaseResult
        {
            Page = page,
            Label = label,
            PixelDiff = Round(diff.DiffRatio),
            MeanError = Round(diff.MeanError),
            SizeMismatch = diff.SizeMismatch,
            TextAreaDiff = Round(Area("text")),
            ImageAreaDiff = Round(Area("images")),
            OtherAreaDiff = Round(Area("other")),
            ReferenceWords = text.ReferenceWords,
            MatchedWords = text.Matched,
            WordsWithin1Pt = within,
            MedianDx = Math.Round(text.MedianDX, 2),
            MedianDy = Math.Round(text.MedianDY, 2),
            P90Distance = Math.Round(text.P90Distance, 2),
            PixelSimilarity = Round(similarity),
            TextAccuracy = accuracy is { } a ? Round(a) : null,
            Score = Round(accuracy is { } t ? (similarity + t) / 2 : similarity),
        };
    }

    private static double Round(double ratio) => Math.Round(ratio, 6);

    /// <summary>The case text of each page's label, "&lt;sample&gt; · pN · &lt;case&gt;", read from the reference PDF.</summary>
    internal static Dictionary<int, string> Labels(byte[] referencePdf, string sampleName)
    {
        var labels = new Dictionary<int, string>();
        using var document = UglyToad.PdfPig.PdfDocument.Open(referencePdf);
        foreach (var page in document.GetPages())
        {
            var words = page.GetWords().ToList();
            var start = words.FirstOrDefault(w => w.Text.StartsWith(sampleName, StringComparison.Ordinal));
            if (start is null)
            {
                continue;
            }
            var line = words.Where(w => Math.Abs(w.BoundingBox.Bottom - start.BoundingBox.Bottom) < 1 && w.BoundingBox.Left >= start.BoundingBox.Left - 0.5)
                .OrderBy(w => w.BoundingBox.Left)
                .Select(w => w.Text);
            var match = LabelPattern().Match(string.Join(' ', line));
            if (match.Success)
            {
                labels[page.Number] = match.Groups["case"].Value.Trim();
            }
        }
        return labels;
    }

    /// <summary>Issues as stable strings: link URIs reduced to the file name, so runs on different machines compare.</summary>
    private static List<string> Issues(PdfExportResult export)
    {
        var issues = new List<string>();
        issues.AddRange(export.FontSubstitutions.Select(s => $"font: {s.RequestedFamily} {s.RequestedStyle} -> {s.UsedFont} ({s.Reason})"));
        issues.AddRange(export.MissingImages.Select(uri => $"missing image: {FileName(uri)}"));
        issues.AddRange(export.OversetStories.Select(story => $"overset story: {story}"));
        issues.AddRange(export.Warnings.Select(warning => $"warning: {UriPattern().Replace(warning, m => FileName(m.Value))}"));
        return issues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private static string FileName(string uri)
    {
        var cut = uri.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? uri[(cut + 1)..] : uri;
    }

    [GeneratedRegex(@"^\S+\s*·\s*p\d+\s*·\s*(?<case>.*)$")]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"file:[^\s)]+")]
    private static partial Regex UriPattern();
}
