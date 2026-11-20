using System.Globalization;
using System.Net;
using System.Text;

namespace CoreIdml.VisualReport;

/// <summary>Writes report.md (for a PR comment or a release note) and report.html (to look at the pages).</summary>
internal static class Reports
{
    public static string Markdown(RunResult run, Comparison? comparison, int top)
    {
        var md = new StringBuilder();
        var cases = run.Samples.SelectMany(s => s.Cases).ToList();
        md.AppendLine("# CoreIdml vs InDesign: visual report").AppendLine();
        md.AppendLine(Invariant($"**{run.Run.Label}** · commit `{run.Run.Commit ?? "unknown"}` · CoreIdml.Pdf {run.Run.RendererVersion} · {run.Run.CreatedUtc:yyyy-MM-dd HH:mm} UTC")).AppendLine();
        md.AppendLine(Invariant($"{run.Samples.Count(s => s.Skipped is null)} samples, {cases.Count} cases (pages). Mean score **{Pct(Mean(cases.Select(c => c.Score)))}**, pixel similarity {Pct(Mean(cases.Select(c => c.PixelSimilarity)))}, text accuracy {Pct(Mean(cases.Where(c => c.TextAccuracy is not null).Select(c => c.TextAccuracy!.Value)))}."));
        var errors = run.Samples.Where(s => s.Error is not null).ToList();
        if (errors.Count > 0)
        {
            md.AppendLine().AppendLine(Invariant($"**{errors.Count} sample(s) failed to export:** ") + string.Join("; ", errors.Select(e => $"`{e.Name}`: {e.Error}")));
        }
        md.AppendLine();

        if (comparison is not null)
        {
            md.AppendLine(Invariant($"## Compared with {comparison.Baseline.Label} (`{comparison.Baseline.Commit ?? "unknown"}`)")).AppendLine();
            md.AppendLine(Invariant($"| Improved | Regressed | Mixed | Unchanged | New | Removed | Relabelled |"));
            md.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            md.AppendLine(Invariant($"| {comparison.Count(CaseStatus.Improved)} | {comparison.Count(CaseStatus.Regressed)} | {comparison.Count(CaseStatus.Mixed)} | {comparison.Count(CaseStatus.Unchanged)} | {comparison.Count(CaseStatus.New)} | {comparison.Count(CaseStatus.Removed)} | {comparison.Count(CaseStatus.Relabelled)} |")).AppendLine();
            md.AppendLine(Invariant($"A case changed when pixel similarity moved by more than {comparison.PixelMargin} or text accuracy by more than {comparison.TextMargin} percentage points.")).AppendLine();
            ChangeTable(md, "Regressed and mixed", comparison.Cases.Where(c => c.Status is CaseStatus.Regressed or CaseStatus.Mixed).OrderBy(c => c.ScoreDelta).Take(top).ToList());
            ChangeTable(md, "Improved", comparison.Cases.Where(c => c.Status == CaseStatus.Improved).OrderByDescending(c => c.ScoreDelta).Take(top).ToList());
            if (comparison.Samples.Count > 0)
            {
                md.AppendLine("### Reported issues").AppendLine();
                foreach (var sample in comparison.Samples)
                {
                    var parts = sample.NewIssues.Select(i => "➕ " + i).Concat(sample.ResolvedIssues.Select(i => "✔ " + i)).ToList();
                    if (sample.NewError is not null)
                    {
                        parts.Insert(0, "❌ export failed: " + sample.NewError);
                    }
                    if (sample.ResolvedError is not null)
                    {
                        parts.Insert(0, "✔ exports again");
                    }
                    md.AppendLine(Invariant($"- `{sample.Sample}`: {string.Join("; ", parts)}"));
                }
                md.AppendLine();
            }
        }

        md.AppendLine("## Samples").AppendLine();
        md.AppendLine("| Sample | Cases | Score | Worst case | Text within 1 pt | Issues |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var sample in run.Samples)
        {
            if (sample.Skipped is not null)
            {
                md.AppendLine(Invariant($"| `{sample.Name}` | – | skipped | {Escape(sample.Skipped)} | | |"));
                continue;
            }
            if (sample.Error is not null)
            {
                md.AppendLine(Invariant($"| `{sample.Name}` | – | **error** | {Escape(sample.Error)} | | |"));
                continue;
            }
            var worst = sample.Cases.MinBy(c => c.Score);
            var words = sample.Cases.Sum(c => c.ReferenceWords);
            var within = sample.Cases.Sum(c => c.WordsWithin1Pt);
            md.AppendLine(Invariant($"| `{sample.Name}` | {sample.Cases.Count} | {Pct(Mean(sample.Cases.Select(c => c.Score)))} | {(worst is null ? "" : $"p{worst.Page} {Pct(worst.Score)} {Escape(Short(worst.Label))}")} | {(words == 0 ? "–" : Pct((double)within / words))} | {sample.Issues.Count} |"));
        }
        md.AppendLine().AppendLine("Score = mean of pixel similarity (share of pixels within tolerance) and text accuracy (share of InDesign's words found within 1 pt of their position). Details per page in report.html and results.json.");
        return md.ToString();
    }

    private static void ChangeTable(StringBuilder md, string title, List<CaseChange> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }
        md.AppendLine(Invariant($"### {title}")).AppendLine();
        md.AppendLine("| Case | Score | Pixels | Text within 1 pt |");
        md.AppendLine("| --- | --- | --- | --- |");
        foreach (var c in changes)
        {
            md.AppendLine(Invariant($"| `{c.Sample}` p{c.Page} {Escape(Short(c.Label))} | {Pct(c.Before!.Score)} → {Pct(c.After!.Score)} ({Pp(c.ScoreDelta)}) | {Pct(c.Before.PixelSimilarity)} → {Pct(c.After.PixelSimilarity)} | {OptPct(c.Before.TextAccuracy)} → {OptPct(c.After.TextAccuracy)} |"));
        }
        md.AppendLine();
    }

    public static string Html(RunResult run, Comparison? comparison)
    {
        var status = comparison?.Cases.ToDictionary(c => (c.Sample, c.Page)) ?? [];
        var issues = comparison?.Samples.ToDictionary(s => s.Sample, StringComparer.Ordinal) ?? [];
        var html = new StringBuilder();
        var cases = run.Samples.SelectMany(s => s.Cases).ToList();
        html.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>CoreIdml visual report</title>
            <style>
            :root { --bg:#fff; --fg:#1d1d1f; --muted:#6b6b70; --line:#e3e3e8; --card:#f6f6f8; --good:#1a7f37; --bad:#c62828; --mixed:#b26a00; }
            @media (prefers-color-scheme: dark) { :root { --bg:#141416; --fg:#ececf0; --muted:#9a9aa2; --line:#2c2c31; --card:#1d1d21; --good:#4cc26b; --bad:#ff6b6b; --mixed:#ffb74d; } }
            body { margin:0; padding:24px 16px; background:var(--bg); color:var(--fg); font:14px/1.45 system-ui, sans-serif; }
            main { max-width:1200px; margin:0 auto; }
            h1 { font-size:22px; margin:0 0 4px; } h2 { font-size:17px; margin:28px 0 8px; }
            .muted { color:var(--muted); }
            .cards { display:flex; flex-wrap:wrap; gap:10px; margin:16px 0; }
            .card { background:var(--card); border-radius:8px; padding:10px 14px; min-width:110px; }
            .card b { display:block; font-size:20px; }
            .filters button { font:inherit; border:1px solid var(--line); background:var(--card); color:var(--fg); border-radius:6px; padding:4px 10px; margin:0 6px 6px 0; cursor:pointer; }
            .filters button.on { border-color:var(--fg); }
            table { border-collapse:collapse; width:100%; }
            th, td { text-align:left; padding:6px 8px; border-bottom:1px solid var(--line); vertical-align:top; }
            th { font-weight:600; color:var(--muted); font-size:12px; }
            td.num { font-variant-numeric:tabular-nums; white-space:nowrap; }
            .Improved .st { color:var(--good); } .Regressed .st { color:var(--bad); } .Mixed .st { color:var(--mixed); }
            .up { color:var(--good); } .down { color:var(--bad); }
            .thumbs a { display:inline-block; margin-right:4px; } .thumbs img { width:110px; border:1px solid var(--line); background:#fff; }
            .issues { font-size:12px; color:var(--muted); margin:4px 0 0; }
            body[data-filter="changed"] tr.Unchanged, body[data-filter="changed"] tr.none { display:none; }
            body[data-filter="regressed"] tr:not(.Regressed):not(.Mixed):not(.head) { display:none; }
            body[data-filter="improved"] tr:not(.Improved):not(.head) { display:none; }
            </style></head>
            <body data-filter="all"><main>
            <h1>CoreIdml vs InDesign</h1>
            """);
        html.Append(Invariant($"<p class=\"muted\">{Encode(run.Run.Label)} · commit <code>{Encode(run.Run.Commit ?? "unknown")}</code> · CoreIdml.Pdf {Encode(run.Run.RendererVersion)} · {run.Run.CreatedUtc:yyyy-MM-dd HH:mm} UTC</p>"));
        html.Append("<div class=\"cards\">");
        Card(html, "cases", cases.Count.ToString(CultureInfo.InvariantCulture));
        Card(html, "mean score", Pct(Mean(cases.Select(c => c.Score))));
        Card(html, "pixel similarity", Pct(Mean(cases.Select(c => c.PixelSimilarity))));
        Card(html, "text accuracy", Pct(Mean(cases.Where(c => c.TextAccuracy is not null).Select(c => c.TextAccuracy!.Value))));
        if (comparison is not null)
        {
            Card(html, "improved", comparison.Count(CaseStatus.Improved).ToString(CultureInfo.InvariantCulture));
            Card(html, "regressed", (comparison.Count(CaseStatus.Regressed) + comparison.Count(CaseStatus.Mixed)).ToString(CultureInfo.InvariantCulture));
        }
        html.Append("</div>");
        if (comparison is not null)
        {
            html.Append(Invariant($"<p class=\"muted\">Compared with {Encode(comparison.Baseline.Label)} (<code>{Encode(comparison.Baseline.Commit ?? "unknown")}</code>).</p>"));
            html.Append("<div class=\"filters\"><button data-f=\"all\" class=\"on\">All</button><button data-f=\"changed\">Changed</button><button data-f=\"regressed\">Regressed</button><button data-f=\"improved\">Improved</button></div>");
        }
        foreach (var sample in run.Samples)
        {
            html.Append(Invariant($"<h2 id=\"{Encode(sample.Name)}\">{Encode(sample.Name)}</h2>"));
            if (sample.Skipped is not null || sample.Error is not null)
            {
                html.Append(Invariant($"<p class=\"muted\">{(sample.Skipped is not null ? "Skipped: " + Encode(sample.Skipped) : "Export failed: " + Encode(sample.Error!))}</p>"));
                continue;
            }
            if (sample.Issues.Count > 0 || issues.ContainsKey(sample.Name))
            {
                var lines = sample.Issues.Select(i => Encode(i)).ToList();
                if (issues.TryGetValue(sample.Name, out var change))
                {
                    lines.AddRange(change.NewIssues.Select(i => "<span class=\"down\">new:</span> " + Encode(i)));
                    lines.AddRange(change.ResolvedIssues.Select(i => "<span class=\"up\">resolved:</span> " + Encode(i)));
                }
                html.Append("<p class=\"issues\">").Append(string.Join("<br>", lines)).Append("</p>");
            }
            html.Append("<table><tr class=\"head\"><th>Page</th><th>Case</th><th>Status</th><th>Score</th><th>Pixels</th><th>Text ≤ 1 pt</th><th>P90</th><th>Reference · ours · diff</th></tr>");
            foreach (var c in sample.Cases)
            {
                status.TryGetValue((sample.Name, c.Page), out var change);
                var st = change?.Status.ToString() ?? "none";
                html.Append(Invariant($"<tr class=\"{st}\"><td class=\"num\">p{c.Page}</td><td>{Encode(c.Label ?? "")}{(c.MissingPage ? " <b class=\"down\">(page missing in ours)</b>" : "")}</td>"));
                html.Append(Invariant($"<td class=\"st\">{(change is null ? "" : st)}</td>"));
                html.Append(Invariant($"<td class=\"num\">{Pct(c.Score)}{Delta(change?.ScoreDelta)}</td><td class=\"num\">{Pct(c.PixelSimilarity)}{Delta(change?.PixelDelta)}</td>"));
                html.Append(Invariant($"<td class=\"num\">{OptPct(c.TextAccuracy)}{Delta(change?.TextDelta)}</td><td class=\"num\">{c.P90Distance:0.0} pt</td><td class=\"thumbs\">"));
                foreach (var kind in (string[])["reference", "ours", "diff"])
                {
                    var file = Invariant($"samples/{sample.Name}/p{c.Page}-{kind}.png");
                    html.Append(Invariant($"<a href=\"{file}\"><img loading=\"lazy\" src=\"{file}\" alt=\"{kind}\" onerror=\"this.parentNode.remove()\"></a>"));
                }
                html.Append("</td></tr>");
            }
            html.Append("</table>");
        }
        html.Append("""
            <p class="muted">Score = mean of pixel similarity (share of pixels within tolerance) and text accuracy (share of InDesign's words found within 1 pt of their position). P90 = 90th percentile of the distance of matched words.</p>
            </main>
            <script>
            document.querySelectorAll('.filters button').forEach(b => b.addEventListener('click', () => {
              document.body.dataset.filter = b.dataset.f;
              document.querySelectorAll('.filters button').forEach(x => x.classList.toggle('on', x === b));
            }));
            </script></body></html>
            """);
        return html.ToString();
    }

    private static void Card(StringBuilder html, string name, string value) =>
        html.Append(Invariant($"<div class=\"card\"><span class=\"muted\">{Encode(name)}</span><b>{Encode(value)}</b></div>"));

    private static string Delta(double? delta) => delta is not { } d || Math.Abs(d) < 0.005 ? ""
        : Invariant($" <span class=\"{(d > 0 ? "up" : "down")}\">{d:+0.00;-0.00}</span>");

    private static double Mean(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0 : list.Average();
    }

    private static string Pct(double ratio) => Invariant($"{ratio * 100:0.00}%");

    private static string OptPct(double? ratio) => ratio is { } r ? Pct(r) : "–";

    private static string Pp(double? delta) => delta is { } d ? Invariant($"{d:+0.00;-0.00} pp") : "";

    private static string Short(string? label) => label is null ? "" : label.Length <= 60 ? label : label[..57] + "...";

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
