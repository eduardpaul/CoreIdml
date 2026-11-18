using System.Globalization;
using UglyToad.PdfPig;

namespace CoreIdml.Pdf.Tests;

/// <summary>One word of the reference matched to the same word in our output.</summary>
internal sealed record WordMatch(string Text, double RefX, double RefBaseline, double DX, double DY, double RefSize, double Size, string RefFont = "", double WidthRatio = 1);

/// <summary>
/// Matches words between a reference PDF page and ours (same text, nearest position) and measures how far each
/// moved: a pixel-independent view of text layout error (line breaks, baselines, fonts).
/// </summary>
internal sealed record TextPositionReport(int Page, int ReferenceWords, int Matched, double MedianDX, double MedianDY, double P90Distance, IReadOnlyList<WordMatch> Matches)
{
    /// <summary>Median width of our words relative to the reference, per reference font (1 = same advance widths).</summary>
    public string WidthByFont => string.Join(", ", Matches.Where(m => m.Text.Length >= 4).GroupBy(m => m.RefFont).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} {g.Select(m => m.WidthRatio).Order().ElementAt(g.Count() / 2):0.000}")));

    public double MatchRatio => ReferenceWords == 0 ? 1 : (double)Matched / ReferenceWords;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"page {Page}: {Matched}/{ReferenceWords} words matched ({MatchRatio:P0}), median dx {MedianDX:0.00} pt, median dy {MedianDY:0.00} pt, 90% within {P90Distance:0.0} pt");

    public static TextPositionReport Compare(byte[] reference, byte[] ours, int referencePage, int ourPage, double maxDistance = 60)
    {
        using var a = PdfDocument.Open(reference);
        using var b = PdfDocument.Open(ours);
        var refWords = a.GetPage(referencePage).GetWords().Where(w => w.Text.Trim().Length > 0).ToList();
        var ourWords = b.GetPage(ourPage).GetWords().Where(w => w.Text.Trim().Length > 0).ToList();
        var available = ourWords.GroupBy(w => w.Text).ToDictionary(g => g.Key, g => g.ToList());
        var matches = new List<WordMatch>();
        foreach (var word in refWords.OrderBy(w => -w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            if (!available.TryGetValue(word.Text, out var candidates) || candidates.Count == 0)
            {
                continue;
            }
            var best = candidates.MinBy(c => Distance(word, c))!;
            if (Distance(word, best) > maxDistance)
            {
                continue;
            }
            candidates.Remove(best);
            var refSize = word.Letters[0].PointSize;
            matches.Add(new WordMatch(word.Text, word.BoundingBox.Left, word.Letters[0].StartBaseLine.Y,
                best.Letters[0].StartBaseLine.X - word.Letters[0].StartBaseLine.X,
                -(best.Letters[0].StartBaseLine.Y - word.Letters[0].StartBaseLine.Y), refSize, best.Letters[0].PointSize,
                word.Letters[0].FontName is { } font && font.IndexOf('+', StringComparison.Ordinal) == 6 ? font[7..] : word.Letters[0].FontName ?? string.Empty,
                word.BoundingBox.Width > 0 ? best.BoundingBox.Width / word.BoundingBox.Width : 1));
        }
        double Median(IEnumerable<double> values)
        {
            var list = values.Order().ToList();
            return list.Count == 0 ? 0 : list[list.Count / 2];
        }
        var distances = matches.Select(m => Math.Sqrt((m.DX * m.DX) + (m.DY * m.DY))).Order().ToList();
        return new TextPositionReport(referencePage, refWords.Count, matches.Count, Median(matches.Select(m => m.DX)), Median(matches.Select(m => m.DY)),
            distances.Count == 0 ? 0 : distances[(int)(distances.Count * 0.9)], matches);
    }

    private static double Distance(UglyToad.PdfPig.Content.Word a, UglyToad.PdfPig.Content.Word b)
    {
        var dx = a.Letters[0].StartBaseLine.X - b.Letters[0].StartBaseLine.X;
        var dy = a.Letters[0].StartBaseLine.Y - b.Letters[0].StartBaseLine.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
