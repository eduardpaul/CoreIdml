using System.Globalization;
using System.Xml.Linq;
using CoreIdml.Dom;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// PDF → IDML scored against ground truth: for every real sample with an .idml and InDesign's PDF of it (opt-in, see
/// Samples/README.md), the PDF is imported and compared with the original IDML: word recall, frame positions, styles.
/// </summary>
public sealed class ImportGroundTruthTests(ITestOutputHelper output)
{
    [Fact]
    public void Rebuilt_IDML_matches_the_original_text_positions_and_styles()
    {
        var samples = VisualSample.Discover().Where(s => s.Idml is not null && s.ReferencePdf is not null && s.Skip is null).ToList();
        if (samples.Count == 0)
        {
            output.WriteLine("SKIPPED: no real samples with an .idml and its reference.pdf. Set COREIDML_PDF_SAMPLES_DIR (see Samples/README.md).");
            return;
        }
        foreach (var sample in samples)
        {
            Score(sample);
        }
    }

    private void Score(VisualSample sample)
    {
        var outDir = TestSupport.OutputDirectory(Path.Combine("import", sample.Name));
        var result = IdmlPdf.Import(File.OpenRead(sample.ReferencePdf!), new PdfImportOptions { ImageDirectory = Path.Combine(outDir, "links"), Fonts = sample.FontOptions() });
        using var imported = result.Document;
        imported.Save(Path.Combine(outDir, "rebuilt.idml"));
        using var original = IdmlDocument.Open(sample.Idml!);
        var name = sample.Name;
        output.WriteLine($"[{name}] rebuilt: {result.TextFrames} text frames, {result.ImageFrames} images, {result.Shapes} shapes");
        foreach (var font in result.Fonts)
        {
            output.WriteLine($"[{name}] font needed: {font.Family} {font.Style} (from {font.PdfFontName}) pages {string.Join(",", font.Pages)} available={font.Available}");
        }

        // Text recall: words of every story shown on a page (spread or master frames) found in the rebuilt document.
        static IEnumerable<string> Words(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('.', ',', ':', ';', '(', ')').ToLowerInvariant()).Where(w => w.Length >= 2);
        var shownStories = original.Spreads.SelectMany(s => s.AllPageItems).Concat(original.MasterSpreads.SelectMany(m => m.AllPageItems))
            .OfType<TextFrame>().Select(f => f.StoryId).Distinct().Select(original.FindStory).OfType<Story>().ToList();
        var originalWords = shownStories.SelectMany(s => Words(AllText(s.Element))).ToHashSet(StringComparer.Ordinal);
        var rebuiltWords = imported.Stories.SelectMany(s => Words(AllText(s.Element))).ToHashSet(StringComparer.Ordinal);
        var recall = originalWords.Count(rebuiltWords.Contains) / (double)Math.Max(1, originalWords.Count);
        var precision = rebuiltWords.Count(originalWords.Contains) / (double)Math.Max(1, rebuiltWords.Count);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{name}] distinct words: recall {recall:P1} ({originalWords.Count} in the original), precision {precision:P1}"));

        // Frame positions and styles: original spread frames matched to rebuilt frames by their opening words.
        var resolver = new AttributeResolver(original);
        var rebuiltResolver = new AttributeResolver(imported);
        var deltas = new List<double>();
        int matched = 0, strict = 0, candidates = 0, sizeMatches = 0, familyMatches = 0;
        var pages = original.Pages.ToList();
        for (var p = 0; p < pages.Count && p < imported.Pages.Count; p++)
        {
            var rebuiltFrames = imported.Pages[p].PageItems.OfType<TextFrame>().Select(f => (Frame: f, Text: string.Join(' ', Words(AllText(f.Story.Element))))).ToList();
            foreach (var frame in pages[p].PageItems.OfType<TextFrame>())
            {
                var firstContent = frame.Story.Element.Descendants("Content").FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Value) && !c.Ancestors("Table").Any());
                if (firstContent is null || frame.SpreadTransform.B != 0)
                {
                    continue;
                }
                var opening = string.Join(' ', Words(firstContent.Value).Take(3));
                if (opening.Length < 4)
                {
                    continue;
                }
                candidates++;
                var match = rebuiltFrames.FirstOrDefault(f => f.Text.StartsWith(opening, StringComparison.Ordinal));
                var isStrict = match.Frame is not null;
                if (match.Frame is null)
                {
                    match = rebuiltFrames.FirstOrDefault(f => f.Text.Contains(opening, StringComparison.Ordinal));
                }
                if (match.Frame is null)
                {
                    continue;
                }
                matched++;
                var inset = resolver.FramePreferenceSource(frame.Element, "InsetSpacing")?.Element("Properties")?.Element("InsetSpacing")?.Elements("ListItem")
                    .Select(i => double.Parse(i.Value, CultureInfo.InvariantCulture)).ToList();
                deltas.Add(Math.Abs(match.Frame.Bounds.X - (frame.Bounds.X + (inset is { Count: 4 } ? inset[1] : 0))));
                if (!isStrict)
                {
                    continue;
                }
                strict++;
                var rebuiltContent = match.Frame.Story.Element.Descendants("Content").First(c => !string.IsNullOrWhiteSpace(c.Value));
                var originalSize = double.Parse(resolver.Text(firstContent, "PointSize") ?? "12", CultureInfo.InvariantCulture);
                var rebuiltSize = double.Parse(rebuiltResolver.Text(rebuiltContent, "PointSize") ?? "12", CultureInfo.InvariantCulture);
                if (Math.Abs(originalSize - rebuiltSize) <= 0.25)
                {
                    sizeMatches++;
                }
                var originalFamily = (resolver.Text(firstContent, "AppliedFont") ?? string.Empty).Replace(" (TT)", string.Empty, StringComparison.Ordinal).Replace(" (OTF)", string.Empty, StringComparison.Ordinal);
                if (string.Equals(originalFamily, rebuiltResolver.Text(rebuiltContent, "AppliedFont"), StringComparison.OrdinalIgnoreCase))
                {
                    familyMatches++;
                }
            }
        }
        deltas.Sort();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{name}] text frames: {matched}/{candidates} matched by opening words ({strict} at the frame start); left edge |dx| median {(deltas.Count == 0 ? 0 : deltas[deltas.Count / 2]):0.00} pt, within 3 pt: {deltas.Count(d => d <= 3) / (double)Math.Max(1, deltas.Count):P0}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{name}] styles on frames matched at their start: point size {sizeMatches / (double)Math.Max(1, strict):P0}, font family {familyMatches / (double)Math.Max(1, strict):P0}"));

        Assert.True(recall > 0.9, $"{name}: recall {recall:P1}");
        Assert.True(matched >= candidates * 0.5, $"{name}: {matched}/{candidates} frames matched");
    }

    private static string AllText(XElement element) =>
        string.Join(' ', element.Descendants("Content").Select(c => c.Value));
}
