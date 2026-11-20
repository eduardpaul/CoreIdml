namespace CoreIdml.VisualReport;

/// <summary>Case by case, what changed between a baseline run and the current one.</summary>
internal static class Compare
{
    /// <param name="pixelMargin">Percentage points of pixel similarity below which a change is noise.</param>
    /// <param name="textMargin">Percentage points of text accuracy below which a change is noise.</param>
    public static Comparison Runs(RunResult baseline, RunResult current, double pixelMargin, double textMargin)
    {
        var before = baseline.Samples.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var after = current.Samples.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var cases = new List<CaseChange>();
        var samples = new List<SampleChange>();
        foreach (var name in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            before.TryGetValue(name, out var b);
            after.TryGetValue(name, out var a);
            // A sample skipped in either run is not compared: its cases are neither new nor removed.
            if (b?.Skipped is not null || a?.Skipped is not null)
            {
                continue;
            }
            var oldCases = b?.Cases.ToDictionary(c => c.Page) ?? [];
            var newCases = a?.Cases.ToDictionary(c => c.Page) ?? [];
            foreach (var page in oldCases.Keys.Union(newCases.Keys).Order())
            {
                oldCases.TryGetValue(page, out var o);
                newCases.TryGetValue(page, out var n);
                cases.Add(Case(name, page, o, n, pixelMargin, textMargin));
            }
            // Issues are compared only when both runs exported the sample: a new, removed or failing sample has no
            // meaningful issue diff (its cases and its error say what happened).
            var comparable = b is { Error: null } && a is { Error: null };
            var oldIssues = comparable ? b!.Issues : [];
            var newIssues = comparable ? a!.Issues : [];
            var change = new SampleChange(name,
                newIssues.Except(oldIssues, StringComparer.Ordinal).ToList(),
                oldIssues.Except(newIssues, StringComparer.Ordinal).ToList(),
                a?.Error is not null && a.Error != b?.Error ? a.Error : null,
                b?.Error is not null && a is not null && a.Error is null ? b.Error : null);
            if (change.NewIssues.Count > 0 || change.ResolvedIssues.Count > 0 || change.NewError is not null || change.ResolvedError is not null)
            {
                samples.Add(change);
            }
        }
        return new Comparison
        {
            Baseline = baseline.Run,
            Current = current.Run,
            PixelMargin = pixelMargin,
            TextMargin = textMargin,
            Cases = cases,
            Samples = samples,
        };
    }

    internal static CaseChange Case(string sample, int page, CaseResult? before, CaseResult? after, double pixelMargin, double textMargin)
    {
        if (before is null)
        {
            return new CaseChange(sample, page, after!.Label, CaseStatus.New, null, after);
        }
        if (after is null)
        {
            return new CaseChange(sample, page, before.Label, CaseStatus.Removed, before, null);
        }
        if (before.Label is not null && after.Label is not null && !string.Equals(before.Label, after.Label, StringComparison.Ordinal))
        {
            return new CaseChange(sample, page, after.Label, CaseStatus.Relabelled, before, after);
        }
        var pixel = (after.PixelSimilarity - before.PixelSimilarity) * 100;
        double? text = before.TextAccuracy is { } tb && after.TextAccuracy is { } ta ? (ta - tb) * 100 : null;
        var better = pixel > pixelMargin || text > textMargin || (before.MissingPage && !after.MissingPage);
        var worse = pixel < -pixelMargin || text < -textMargin || (!before.MissingPage && after.MissingPage);
        var status = (better, worse) switch
        {
            (true, true) => CaseStatus.Mixed,
            (true, false) => CaseStatus.Improved,
            (false, true) => CaseStatus.Regressed,
            _ => CaseStatus.Unchanged,
        };
        return new CaseChange(sample, page, after.Label ?? before.Label, status, before, after);
    }
}
