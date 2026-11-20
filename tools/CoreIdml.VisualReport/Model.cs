using System.Text.Json.Serialization;

namespace CoreIdml.VisualReport;

/// <summary>One run of the tool: <c>results.json</c>.</summary>
internal sealed record RunResult
{
    /// <summary>Bumped when a field changes meaning; <c>compare</c> refuses runs of another schema.</summary>
    public int Schema { get; init; } = RunResult.CurrentSchema;

    public required RunInfo Run { get; init; }

    public required List<SampleResult> Samples { get; init; }

    public const int CurrentSchema = 1;
}

/// <summary>Where and how a run was made.</summary>
/// <param name="Label">Free text (a release, a PR); defaults to the commit.</param>
/// <param name="Commit">Short commit hash of the working tree, "-dirty" when it had changes; null outside git.</param>
/// <param name="RendererVersion">Informational version of CoreIdml.Pdf.</param>
internal sealed record RunInfo(string Label, string? Commit, string RendererVersion, DateTimeOffset CreatedUtc, string SamplesRoot);

/// <summary>One sample folder: its export and one <see cref="CaseResult"/> per page.</summary>
internal sealed record SampleResult
{
    public required string Name { get; init; }

    public int Dpi { get; init; }

    public int Tolerance { get; init; }

    public int ReferencePages { get; init; }

    public int OurPages { get; init; }

    public double ExportMilliseconds { get; init; }

    /// <summary>What the export reported, normalized so runs on different machines compare: "font: ...", "missing image: name", "overset story: id", "warning: ...".</summary>
    public List<string> Issues { get; init; } = [];

    /// <summary>The export or the measurement failed; no cases.</summary>
    public string? Error { get; init; }

    /// <summary>Not measured (sample.json <c>skip</c>, or a PDF-only import sample).</summary>
    public string? Skipped { get; init; }

    public List<CaseResult> Cases { get; init; } = [];
}

/// <summary>
/// One page of a sample: one case, named by the label InDesign printed on it. All ratios are 0..1.
/// </summary>
internal sealed record CaseResult
{
    public required int Page { get; init; }

    /// <summary>The case text of the page label ("&lt;sample&gt; · pN · &lt;case&gt;"), null when the page has none.</summary>
    public string? Label { get; init; }

    /// <summary>Our PDF has no such page.</summary>
    public bool MissingPage { get; init; }

    /// <summary>Share of pixels whose largest channel difference exceeds the tolerance.</summary>
    public double PixelDiff { get; init; }

    /// <summary>Mean absolute channel difference over all pixels.</summary>
    public double MeanError { get; init; }

    public bool SizeMismatch { get; init; }

    /// <summary>Differing share of the reference's text area (glyph boxes).</summary>
    public double TextAreaDiff { get; init; }

    /// <summary>Differing share of the reference's image area.</summary>
    public double ImageAreaDiff { get; init; }

    /// <summary>Differing share of everything else (vector art and paper).</summary>
    public double OtherAreaDiff { get; init; }

    public int ReferenceWords { get; init; }

    /// <summary>Reference words found in our page (same text, within 60 pt).</summary>
    public int MatchedWords { get; init; }

    /// <summary>Reference words found in our page within 1 pt of InDesign's position.</summary>
    public int WordsWithin1Pt { get; init; }

    public double MedianDx { get; init; }

    public double MedianDy { get; init; }

    /// <summary>90th percentile of the distance between matched words, in points.</summary>
    public double P90Distance { get; init; }

    /// <summary>1 − <see cref="PixelDiff"/>.</summary>
    public double PixelSimilarity { get; init; }

    /// <summary><see cref="WordsWithin1Pt"/> / <see cref="ReferenceWords"/>; null for a page without words.</summary>
    public double? TextAccuracy { get; init; }

    /// <summary>Mean of <see cref="PixelSimilarity"/> and <see cref="TextAccuracy"/> (pixel similarity alone without words).</summary>
    public double Score { get; init; }
}

internal enum CaseStatus
{
    Unchanged,
    Improved,
    Regressed,
    /// <summary>One measure better, the other worse.</summary>
    Mixed,
    New,
    Removed,
    /// <summary>The page now holds another case (the sample was regenerated): not compared.</summary>
    Relabelled,
}

/// <summary>A case in two runs. Deltas are in percentage points, positive = more similar.</summary>
internal sealed record CaseChange(string Sample, int Page, string? Label, CaseStatus Status, CaseResult? Before, CaseResult? After)
{
    public double? ScoreDelta => Before is null || After is null ? null : (After.Score - Before.Score) * 100;

    public double? PixelDelta => Before is null || After is null ? null : (After.PixelSimilarity - Before.PixelSimilarity) * 100;

    public double? TextDelta => Before?.TextAccuracy is { } b && After?.TextAccuracy is { } a ? (a - b) * 100 : null;
}

/// <summary>Sample-level differences: reported issues and export errors.</summary>
internal sealed record SampleChange(string Sample, List<string> NewIssues, List<string> ResolvedIssues, string? NewError, string? ResolvedError);

/// <summary><c>comparison.json</c>.</summary>
internal sealed record Comparison
{
    public int Schema { get; init; } = RunResult.CurrentSchema;

    public required RunInfo Baseline { get; init; }

    public required RunInfo Current { get; init; }

    /// <summary>Smallest change, in percentage points of pixel similarity, that counts as better or worse.</summary>
    public double PixelMargin { get; init; }

    /// <summary>Smallest change, in percentage points of text accuracy, that counts as better or worse.</summary>
    public double TextMargin { get; init; }

    public required List<CaseChange> Cases { get; init; }

    public required List<SampleChange> Samples { get; init; }

    public int Count(CaseStatus status) => Cases.Count(c => c.Status == status);

    /// <summary>Anything that got worse: regressed or mixed cases, new export errors, new issues.</summary>
    public bool HasRegressions => Cases.Any(c => c.Status is CaseStatus.Regressed or CaseStatus.Mixed)
        || Samples.Any(s => s.NewError is not null || s.NewIssues.Count > 0);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunResult))]
[JsonSerializable(typeof(Comparison))]
internal sealed partial class ReportJson : JsonSerializerContext;
