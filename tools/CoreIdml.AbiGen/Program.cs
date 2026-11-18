using CoreIdml.AbiGen;

// dotnet run --project tools/CoreIdml.AbiGen [-- --check]
//   (no arguments)  writes the generated binding files that are out of date
//   --check         writes nothing; exit code 1 if a file is out of date

var check = args.Contains("--check");
try
{
    var root = Repository.FindRoot(Directory.GetCurrentDirectory());
    var abi = Repository.ReadAbi(root);
    var generated = Emitters.Generate(abi);
    var stale = Repository.FindStale(root, generated);

    Console.WriteLine($"coreidml.h: ABI version {abi.Version}, {abi.Functions.Count} functions, {abi.Statuses.Count} status codes.");
    foreach (var path in generated.Keys)
    {
        Console.WriteLine($"  {(stale.Contains(path) ? (check ? "STALE  " : "written") : "current")}  {path}");
    }
    if (check)
    {
        if (stale.Count > 0)
        {
            Console.Error.WriteLine("Generated binding files are out of date. Run: dotnet run --project tools/CoreIdml.AbiGen");
            return 1;
        }
        return 0;
    }
    foreach (var path in stale)
    {
        var file = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, generated[path]);
    }
    return 0;
}
catch (Exception exception) when (exception is FormatException or NotSupportedException or IOException)
{
    Console.Error.WriteLine($"abigen: {exception.Message}");
    return 2;
}
