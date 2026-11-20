namespace CoreIdml.Pdf.Layout;

/// <summary>
/// TeX hyphenation patterns (a <c>\patterns{...}</c> list and a <c>\hyphenation{...}</c> exception list, as in the
/// hyph-utf8 files) and Liang's algorithm over them (Liang, "Word Hy-phen-a-tion by Com-put-er", 1983; The TeXbook,
/// appendix H). Each pattern is a letter string with digits between its letters; every pattern found in
/// ".word." raises the values between its letters to its digits, and an odd value marks a hyphenation point.
/// </summary>
internal sealed class TexPatterns
{
    // Letters of a pattern -> the digit before each letter and after the last one (letters.Length + 1 values).
    private readonly Dictionary<string, byte[]>.AlternateLookup<ReadOnlySpan<char>> patterns;
    // Word -> the indices i such that a hyphen may go after word[i].
    private readonly Dictionary<string, int[]>.AlternateLookup<ReadOnlySpan<char>> exceptions;
    private readonly int longest;

    private TexPatterns(Dictionary<string, byte[]> patterns, Dictionary<string, int[]> exceptions)
    {
        this.patterns = patterns.GetAlternateLookup<ReadOnlySpan<char>>();
        this.exceptions = exceptions.GetAlternateLookup<ReadOnlySpan<char>>();
        longest = patterns.Count == 0 ? 0 : patterns.Keys.Max(key => key.Length);
    }

    /// <summary>Reads the patterns from an embedded resource of this assembly.</summary>
    public static TexPatterns FromResource(string name)
    {
        using var stream = typeof(TexPatterns).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded hyphenation patterns '{name}'.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parses a TeX pattern file: <c>%</c> comments, <c>\patterns{...}</c> and <c>\hyphenation{...}</c>.</summary>
    public static TexPatterns Parse(string text)
    {
        var patterns = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var exceptions = new Dictionary<string, int[]>(StringComparer.Ordinal);
        Dictionary<string, byte[]>? inPatterns = null;
        var inExceptions = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var comment = rawLine.IndexOf('%', StringComparison.Ordinal);
            var line = comment >= 0 ? rawLine[..comment] : rawLine;
            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (token)
                {
                    case @"\patterns{":
                        inPatterns = patterns;
                        continue;
                    case @"\hyphenation{":
                        inExceptions = true;
                        continue;
                    case "}":
                        inPatterns = null;
                        inExceptions = false;
                        continue;
                }
                if (inPatterns is not null)
                {
                    AddPattern(token, inPatterns);
                }
                else if (inExceptions)
                {
                    AddException(token, exceptions);
                }
            }
        }
        return new TexPatterns(patterns, exceptions);
    }

    private static void AddPattern(string pattern, Dictionary<string, byte[]> output)
    {
        var letters = new char[pattern.Length];
        var digits = new byte[pattern.Length + 1];
        var count = 0;
        foreach (var c in pattern)
        {
            if (c is >= '0' and <= '9')
            {
                digits[count] = (byte)(c - '0');
            }
            else
            {
                letters[count++] = c;
            }
        }
        output[new string(letters, 0, count)] = digits[..(count + 1)];
    }

    private static void AddException(string hyphenated, Dictionary<string, int[]> output)
    {
        var word = hyphenated.Replace("-", string.Empty, StringComparison.Ordinal);
        var points = new List<int>();
        var letters = 0;
        foreach (var c in hyphenated)
        {
            if (c == '-')
            {
                points.Add(letters - 1);
            }
            else
            {
                letters++;
            }
        }
        output[word] = [.. points];
    }

    /// <summary>
    /// Sets <c>after[i]</c> for each index i of the lower-case <paramref name="word"/> such that a hyphen may go
    /// after word[i] (the exception list first, then the patterns). Minimum letters before and after are the caller's.
    /// </summary>
    public void Hyphenate(ReadOnlySpan<char> word, Span<bool> after)
    {
        after[..word.Length].Clear();
        if (exceptions.TryGetValue(word, out var listed))
        {
            foreach (var point in listed)
            {
                after[point] = true;
            }
            return;
        }
        // ".word.": the dots match patterns anchored at the start or the end of a word.
        var length = word.Length + 2;
        Span<char> dotted = length <= 128 ? stackalloc char[length] : new char[length];
        dotted[0] = '.';
        word.CopyTo(dotted[1..]);
        dotted[^1] = '.';
        // values[k]: the value between dotted[k - 1] and dotted[k].
        Span<byte> values = length <= 128 ? stackalloc byte[length + 1] : new byte[length + 1];
        values.Clear();
        for (var start = 0; start < length; start++)
        {
            var max = Math.Min(longest, length - start);
            for (var count = 1; count <= max; count++)
            {
                if (!patterns.TryGetValue(dotted.Slice(start, count), out var digits))
                {
                    continue;
                }
                for (var k = 0; k < digits.Length; k++)
                {
                    if (digits[k] > values[start + k])
                    {
                        values[start + k] = digits[k];
                    }
                }
            }
        }
        // A hyphen after word[i] sits between dotted[i + 1] and dotted[i + 2].
        for (var i = 0; i + 1 < word.Length; i++)
        {
            after[i] = (values[i + 2] & 1) == 1;
        }
    }
}
