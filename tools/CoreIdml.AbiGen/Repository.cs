namespace CoreIdml.AbiGen;

/// <summary>Where things are in the repository.</summary>
public static class Repository
{
    /// <summary>Path of the C header, relative to the repository root.</summary>
    public const string HeaderPath = "include/coreidml.h";

    /// <summary>The repository root: the closest folder at or above <paramref name="start"/> that holds CoreIdml.slnx.</summary>
    /// <exception cref="DirectoryNotFoundException">No such folder.</exception>
    public static string FindRoot(string? start = null)
    {
        var first = start ?? AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(first); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CoreIdml.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException($"No CoreIdml.slnx at or above {first}.");
    }

    /// <summary>The ABI as declared by the header in the repository at <paramref name="root"/>.</summary>
    public static AbiDefinition ReadAbi(string root) => HeaderParser.Parse(File.ReadAllText(Path.Combine(root, HeaderPath)));

    /// <summary>
    /// The generated files whose content on disk differs from what the header gives now (missing files included),
    /// compared with line ends normalized.
    /// </summary>
    public static IReadOnlyList<string> FindStale(string root, IReadOnlyDictionary<string, string> generated)
    {
        ArgumentNullException.ThrowIfNull(generated);
        List<string> stale = [];
        foreach (var (path, content) in generated)
        {
            var file = Path.Combine(root, path);
            if (!File.Exists(file) || File.ReadAllText(file).ReplaceLineEndings("\n") != content)
            {
                stale.Add(path);
            }
        }
        return stale;
    }
}
