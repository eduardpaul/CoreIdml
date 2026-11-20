using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CoreIdml.Pdf;
using CoreIdml.Pdf.Tests;
using CoreIdml.VisualReport;

const string Usage = """
    CoreIdml visual report: CoreIdml's PDF against InDesign's, per sample and per page (case).

    Usage:
      run <samples-dir> [options]          export and measure every sample, write the report
      compare <baseline.json> <current.json> [options]
                                           compare two results.json files without rendering

    Options:
      --out <dir>               where to write (run: ./visual-report; compare: the folder of current.json)
      --baseline <results.json> run: compare with an earlier run
      --only <names>            run: comma-separated sample names or prefixes (08-para,10-table-cells)
      --images all|changed|none run: which page PNGs to keep (default all; changed needs --baseline)
      --dpi <n>                 run: rasterization resolution (default: sample.json, else 96)
      --tolerance <n>           run: per-channel difference ignored, 0-255 (default: sample.json, else 64)
      --jobs <n>                run: samples measured in parallel (default 1)
      --fonts <dir>             run: fallback fonts (default: tests/fixtures/fonts of the repository)
      --label <text>            run: name of the run (default: the commit)
      --pixel-margin <pp>       smallest pixel-similarity change that counts, in percentage points (default 0.02)
      --text-margin <pp>        smallest text-accuracy change that counts, in percentage points (default 0.5)
      --top <n>                 rows per table of changes in report.md (default 25)
      --fail-on-regression      exit code 1 when a case regressed, a sample stopped exporting or reports a new issue
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

try
{
    var options = Options.Parse(args.Skip(1).ToArray());
    return args[0] switch
    {
        "run" => Run(options),
        "compare" => CompareFiles(options),
        _ => throw new ArgumentException($"unknown command '{args[0]}'"),
    };
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage);
    return 2;
}

static int Run(Options options)
{
    if (options.Positional.Count != 1)
    {
        throw new ArgumentException("run needs exactly one samples folder");
    }
    var root = Path.GetFullPath(options.Positional[0]);
    if (!Directory.Exists(root))
    {
        throw new ArgumentException($"samples folder not found: {root}");
    }
    var outDirectory = Path.GetFullPath(options.Value("out") ?? "visual-report");
    var only = options.Value("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    bool Selected(string name) => only is null || only.Any(o => name.StartsWith(o, StringComparison.Ordinal));
    var baseline = options.Value("baseline") is { } baselinePath ? Load(baselinePath) : null;
    if (baseline is not null && only is not null)
    {
        // Measuring a subset: the rest of the baseline is not "removed", just not measured this time.
        baseline = baseline with { Samples = baseline.Samples.Where(s => Selected(s.Name)).ToList() };
    }
    var imagesMode = options.Value("images") ?? "all";
    if (imagesMode is not ("all" or "changed" or "none"))
    {
        throw new ArgumentException("--images must be all, changed or none");
    }
    if (imagesMode == "changed" && baseline is null)
    {
        throw new ArgumentException("--images changed needs --baseline");
    }
    var pixelMargin = options.Number("pixel-margin") ?? 0.02;
    var textMargin = options.Number("text-margin") ?? 0.5;
    var baselineCases = baseline?.Samples.SelectMany(s => s.Cases.Select(c => (s.Name, Case: c))).ToDictionary(x => (x.Name, x.Case.Page), x => x.Case) ?? [];
    bool KeepImages(string sample, CaseResult result) => imagesMode switch
    {
        "none" => false,
        "all" => true,
        _ => Compare.Case(sample, result.Page, baselineCases.GetValueOrDefault((sample, result.Page)), result, pixelMargin, textMargin).Status != CaseStatus.Unchanged,
    };
    var settings = new MeasureSettings(options.Value("fonts") ?? FindRepositoryFonts(), (int?)options.Number("dpi"), (int?)options.Number("tolerance"), KeepImages);

    var samples = VisualSample.Discover(root).Where(s => Selected(s.Name)).ToList();
    if (samples.Count == 0)
    {
        throw new ArgumentException($"no samples in {root}" + (only is null ? "" : " matching --only"));
    }
    PrepareOutput(outDirectory);
    var commit = GitCommit();
    var info = new RunInfo(options.Value("label") ?? commit ?? "local run", commit, RendererVersion(), DateTimeOffset.UtcNow, root);
    Console.Error.WriteLine($"Measuring {samples.Count} sample(s) from {root}");

    var results = new ConcurrentDictionary<string, SampleResult>(StringComparer.Ordinal);
    var done = 0;
    var watch = Stopwatch.StartNew();
    Parallel.ForEach(samples, new ParallelOptions { MaxDegreeOfParallelism = (int)(options.Number("jobs") ?? 1) }, sample =>
    {
        var started = watch.Elapsed;
        SampleResult result;
        try
        {
            result = Measure.Sample(sample, settings, Path.Combine(outDirectory, "samples", sample.Name));
        }
#pragma warning disable CA1031 // One broken sample must not stop the run; the failure is recorded in its result.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            result = new SampleResult { Name = sample.Name, Error = $"measure: {exception.GetType().Name}: {exception.Message}" };
        }
        results[sample.Name] = result;
        var n = Interlocked.Increment(ref done);
        var summary = result.Skipped is not null ? "skipped"
            : result.Error is not null ? "ERROR " + result.Error
            : FormattableString.Invariant($"{result.Cases.Count} cases, score {(result.Cases.Count == 0 ? 0 : result.Cases.Average(c => c.Score)) * 100:0.00}%");
        Console.Error.WriteLine(FormattableString.Invariant($"[{n}/{samples.Count}] {sample.Name}: {summary} ({(watch.Elapsed - started).TotalSeconds:0.0} s)"));
    });

    var run = new RunResult { Run = info, Samples = results.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList() };
    File.WriteAllText(Path.Combine(outDirectory, "results.json"), JsonSerializer.Serialize(run, ReportJson.Default.RunResult));
    var comparison = baseline is null ? null : Compare.Runs(baseline, run, pixelMargin, textMargin);
    WriteReports(outDirectory, run, comparison, (int)(options.Number("top") ?? 25));
    Console.Error.WriteLine(FormattableString.Invariant($"Done in {watch.Elapsed.TotalSeconds:0} s: {Path.Combine(outDirectory, "report.html")}"));
    return options.Flag("fail-on-regression") && comparison?.HasRegressions == true ? 1 : 0;
}

static int CompareFiles(Options options)
{
    if (options.Positional.Count != 2)
    {
        throw new ArgumentException("compare needs a baseline results.json and a current results.json");
    }
    var baseline = Load(options.Positional[0]);
    var current = Load(options.Positional[1]);
    var outDirectory = Path.GetFullPath(options.Value("out") ?? Path.GetDirectoryName(Path.GetFullPath(options.Positional[1]))!);
    Directory.CreateDirectory(outDirectory);
    var comparison = Compare.Runs(baseline, current, options.Number("pixel-margin") ?? 0.02, options.Number("text-margin") ?? 0.5);
    WriteReports(outDirectory, current, comparison, (int)(options.Number("top") ?? 25));
    Console.Error.WriteLine(FormattableString.Invariant(
        $"{comparison.Count(CaseStatus.Improved)} improved, {comparison.Count(CaseStatus.Regressed)} regressed, {comparison.Count(CaseStatus.Mixed)} mixed, {comparison.Count(CaseStatus.Unchanged)} unchanged: {Path.Combine(outDirectory, "report.md")}"));
    return options.Flag("fail-on-regression") && comparison.HasRegressions ? 1 : 0;
}

static void WriteReports(string outDirectory, RunResult run, Comparison? comparison, int top)
{
    if (comparison is not null)
    {
        File.WriteAllText(Path.Combine(outDirectory, "comparison.json"), JsonSerializer.Serialize(comparison, ReportJson.Default.Comparison));
    }
    File.WriteAllText(Path.Combine(outDirectory, "report.md"), Reports.Markdown(run, comparison, top));
    File.WriteAllText(Path.Combine(outDirectory, "report.html"), Reports.Html(run, comparison));
}

static RunResult Load(string path)
{
    if (!File.Exists(path))
    {
        throw new ArgumentException($"results file not found: {path}");
    }
    var run = JsonSerializer.Deserialize(File.ReadAllText(path), ReportJson.Default.RunResult)
        ?? throw new ArgumentException($"not a results file: {path}");
    return run.Schema == RunResult.CurrentSchema ? run
        : throw new ArgumentException($"{path} has schema {run.Schema}; this tool reads schema {RunResult.CurrentSchema}");
}

// The output folder is either new, empty, or an earlier report (results.json): only then are its page images replaced.
static void PrepareOutput(string outDirectory)
{
    if (Directory.Exists(outDirectory) && Directory.EnumerateFileSystemEntries(outDirectory).Any())
    {
        if (!File.Exists(Path.Combine(outDirectory, "results.json")))
        {
            throw new ArgumentException($"{outDirectory} is not empty and holds no earlier report; choose another --out");
        }
        var samples = Path.Combine(outDirectory, "samples");
        if (Directory.Exists(samples))
        {
            Directory.Delete(samples, recursive: true);
        }
        File.Delete(Path.Combine(outDirectory, "comparison.json"));
    }
    Directory.CreateDirectory(outDirectory);
}

static string? FindRepositoryFonts()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var fonts = Path.Combine(directory.FullName, "tests", "fixtures", "fonts");
            if (Directory.Exists(fonts))
            {
                return fonts;
            }
        }
    }
    return null;
}

static string RendererVersion() =>
    typeof(IdmlPdf).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

static string? GitCommit()
{
    string? Git(string arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git", arguments) { WorkingDirectory = Directory.GetCurrentDirectory(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git not installed
        }
    }
    var hash = Git("rev-parse --short HEAD");
    if (hash is null)
    {
        return null;
    }
    return string.IsNullOrEmpty(Git("status --porcelain --untracked-files=no")) ? hash : hash + "-dirty";
}

/// <summary>Command-line options: positional arguments, <c>--name value</c> and <c>--flag</c>.</summary>
internal sealed class Options
{
    private static readonly HashSet<string> Flags = ["fail-on-regression"];
    private static readonly HashSet<string> Valued = ["out", "baseline", "only", "images", "dpi", "tolerance", "jobs", "fonts", "label", "pixel-margin", "text-margin", "top"];
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> flags = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = [];

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                options.Positional.Add(args[i]);
                continue;
            }
            var name = args[i][2..];
            if (Flags.Contains(name))
            {
                options.flags.Add(name);
            }
            else if (Valued.Contains(name))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"--{name} needs a value");
                }
                options.values[name] = args[++i];
            }
            else
            {
                throw new ArgumentException($"unknown option --{name}");
            }
        }
        return options;
    }

    public string? Value(string name) => values.GetValueOrDefault(name);

    public bool Flag(string name) => flags.Contains(name);

    public double? Number(string name) => Value(name) is not { } text ? null
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
        : throw new ArgumentException($"--{name} must be a number, not '{text}'");
}
