using BenchmarkDotNet.Running;
using CoreIdml.Benchmarks;

switch (args)
{
    // golden <out-dir>: writes every scenario's output and manifest.sha256 (compare two runs with diff).
    case ["golden", var output]:
        return Golden.Run(output);

    // loop <benchmark-class> <method> [param] [count]: runs one benchmark in a loop, to attach a profiler
    // (dotnet-trace collect -- dotnet CoreIdml.Benchmarks.dll loop PdfExportBenchmarks Export ficha-real 20).
    case ["loop", var type, var method, ..]:
        return LoopRunner.Run(type, method, args.Length > 3 ? args[3] : null, args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 10);

    default:
        BenchmarkSwitcher.FromAssembly(typeof(Corpus).Assembly).Run(args);
        return 0;
}
