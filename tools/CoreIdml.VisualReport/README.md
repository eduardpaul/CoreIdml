# CoreIdml.VisualReport

Measures how close CoreIdml's PDF is to InDesign's, **per case** (one page of a sample), and compares a run with an
earlier one so a release or a pull request shows which cases got better or worse.

For every sample folder (`source.idml` + InDesign's `reference.pdf`, the layout of `tests/doc-samples`) it exports
the IDML with CoreIdml, rasterizes both PDFs, and records per page:

| Measure | Meaning |
| --- | --- |
| `pixelSimilarity` | Share of pixels whose largest channel difference is within the tolerance (64 of 255 by default). |
| `textAccuracy` | Share of InDesign's words found in our page within 1 pt of their position. Null on a page without text. |
| `score` | Mean of the two (pixel similarity alone on a page without text). The headline number. |
| `textAreaDiff`, `imageAreaDiff`, `otherAreaDiff` | Where the differing pixels are: share of the reference's glyph boxes, images, and everything else. |
| `matchedWords`, `medianDx`, `medianDy`, `p90Distance` | Word matching details, in points. |
| `label` | The case, read from the page label InDesign printed (`<sample> · pN · <case>`). |

Per sample it also records what the export reported (font substitutions, missing images, overset stories, warnings),
with link paths reduced to file names so runs on different machines compare.

The measurement code is the visual harness of `tests/CoreIdml.Pdf.Tests` (`VisualDiff.cs`, `TextPositionDiff.cs`,
`VisualSample.cs`, `Rasterizer.cs`), compiled into this tool, so the tool and the tests measure the same way.
`sample.json` in a sample folder is honoured (`dpi`, `tolerance`, `pages`, fonts, links, `skip`).

## Usage

```sh
# Measure every sample; write the report to ./visual-report
dotnet run -c Release --project tools/CoreIdml.VisualReport -- run tests/doc-samples --out visual-report --jobs 4

# Measure and compare with an earlier run; exit code 1 if anything got worse
dotnet run -c Release --project tools/CoreIdml.VisualReport -- run tests/doc-samples --out visual-report \
  --baseline baseline/results.json --images changed --jobs 4 --fail-on-regression

# Compare two existing runs without rendering
dotnet run -c Release --project tools/CoreIdml.VisualReport -- compare v1.1.1/results.json v1.2.0/results.json --out compare-1.2.0
```

Options (`--help` prints them):

| Option | Default | |
| --- | --- | --- |
| `--out <dir>` | `./visual-report` | Must be new, empty, or an earlier report (its page images are replaced). |
| `--baseline <results.json>` | none | Compare with that run. |
| `--only <names>` | all | Comma-separated sample names or prefixes: `--only 08-para,10-table-cells`. The baseline is filtered the same way. |
| `--images all\|changed\|none` | `all` | Which page PNGs to keep. `changed` (needs `--baseline`) keeps only cases whose status is not unchanged. |
| `--jobs <n>` | 1 | Samples measured in parallel. Results are identical to a serial run. |
| `--dpi`, `--tolerance` | `sample.json`, else 96 and 64 | Override for every sample. Runs with different values are not comparable. |
| `--fonts <dir>` | `tests/fixtures/fonts` | Fallback fonts after each sample's own `fonts/` folder. |
| `--label <text>` | the commit | Name of the run in the reports ("v1.2.0", "PR 123"). |
| `--pixel-margin <pp>` | 0.02 | Smallest change of pixel similarity, in percentage points, that counts as better or worse. |
| `--text-margin <pp>` | 0.5 | Smallest change of text accuracy, in percentage points, that counts. |
| `--top <n>` | 25 | Rows per table of changes in `report.md`. |
| `--fail-on-regression` | off | Exit code 1 when a case regressed or is mixed, a sample stopped exporting, or a sample reports a new issue. |

Exit codes: 0 done, 1 regression (with `--fail-on-regression`), 2 bad arguments.

## Output

```
visual-report/
  results.json         every measure of every case, plus the run (label, commit, CoreIdml.Pdf version, date)
  comparison.json      with a baseline: status and deltas per case, new and resolved issues per sample
  report.md            summary, regressions and improvements first, then one row per sample (fits a PR comment)
  report.html          every case with reference, ours and diff thumbnails; filters for changed/regressed/improved
  samples/<name>/      ours.pdf, and p<N>-reference.png, p<N>-ours.png, p<N>-diff.png (per --images)
```

`results.json` is the file to keep: store one per release (or per main-branch commit) and pass it as `--baseline`
later. Its `schema` is 1; `compare` refuses files of another schema.

## How a case is judged

Against the baseline, each case (sample + page) is:

| Status | When |
| --- | --- |
| improved | pixel similarity up by more than the pixel margin, or text accuracy up by more than the text margin, and nothing down |
| regressed | either measure down by more than its margin, and nothing up |
| mixed | one up and the other down |
| unchanged | both within their margins |
| new / removed | the page exists in one run only |
| relabelled | the page's label changed: the sample was regenerated, so the numbers describe different content and are not compared |

The export and the rasterizer are deterministic: two runs of the same code give identical numbers (checked on the
381 cases of `tests/doc-samples`), so the margins only absorb tiny shifts, not noise. Raise them if a change in font
loading or the rasterizer moves everything a little.

## Cost

On `tests/doc-samples` (75 samples, 381 cases), on a desktop: about 115 s serial, 40 s with `--jobs 4`. With
`--images all` the output is about 100 MB (PNG per page, three per case); `--images changed` keeps only the PDFs and
the changed cases (13 MB when nothing changed).

## What the numbers do not tell

- **A high score is not "the feature works".** A missing bullet or a deleted word drawn by mistake changes a tiny
  share of a page. Watch the direction of `textAccuracy` and the cases of the feature you work on, and read
  `tests/doc-samples/TESTING.md` for the check that proves each feature.
- **Line breaks dominate.** One paragraph that breaks differently moves every word below it: text accuracy falls for
  the whole page even when the feature under test is right.
- **Only `reference.pdf` is compared.** Variant PDFs (bleed, spreads, PDF/X, tagged) are not measured.
- **PDF-only folders** (import samples) and folders with `sample.json` `skip` are listed as skipped.
- **Samples change.** When the InDesign package is regenerated, pages whose label changed are "relabelled", and a
  new baseline should be made from the new package before comparing renderer changes.
