using System.Buffers;
using System.Text;

namespace CoreIdml.Pdf.Layout;

/// <summary>Paragraph hyphenation settings (InDesign's Hyphenation panel).</summary>
internal sealed record HyphenationSettings(bool Enabled, int AfterFirst, int BeforeLast, int WordsLongerThan, bool Capitalized, double Zone = 36);

/// <summary>The hyphenation rules a language (InDesign's <c>AppliedLanguage</c>) gets.</summary>
internal enum HyphenationLanguage
{
    /// <summary>Not hyphenated: no data for the language, or "[No Language]".</summary>
    None,

    /// <summary>Any "English: ..." variant: Liang's algorithm over the TeX hyph-en-us patterns.</summary>
    English,

    /// <summary>Spanish: syllabification rules.</summary>
    Spanish,
}

/// <summary>
/// Hyphenation points, by language. InDesign hyphenates with its own dictionaries, which are not available; CoreIdml
/// approximates them:
/// <list type="bullet">
/// <item>English (USA, UK and the other "English: ..." languages): Liang's algorithm with the TeX hyph-en-us patterns
/// (<c>Hyphenation/hyph-en-us.tex</c>, Gerard Kuiken's ushyphmax with Knuth's patterns and exceptions). Of the
/// hyphenated line ends in InDesign 20's reference PDFs of the doc-samples (re-turned, accom-plished,
/// neighbour-hood, pos-ses-sion, immedi-ately, fami-lies, insuf-ficient, uncondi-tional...), the patterns allow all
/// but two proper names: InDesign's En-gland and Neth-erfield, where the patterns give Eng-land and Nether-field. In
/// 07-char-language-nobreak p1 InDesign breaks the same text at the same points for English: USA and English: UK,
/// so every English variant uses these patterns.</item>
/// <item>Spanish: syllabification rules. Consonant clusters (bl, br, cl, cr, dr, fl, fr, gl, gr, pl, pr, tr, ch, ll,
/// rr) stay together, diphthongs stay together, hiatus splits.</item>
/// </list>
/// Other languages are not hyphenated.
/// </summary>
internal static class Hyphenator
{
    private const string StrongVowels = "aeoáéóíúAEOÁÉÓÍÚ";
    private const string WeakVowels = "iuüIUÜ";

    private static readonly HashSet<string> Clusters =
        ["bl", "br", "cl", "cr", "dr", "fl", "fr", "gl", "gr", "kl", "kr", "pl", "pr", "tr", "ch", "ll", "rr", "tl", "qu", "gu"];

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ClusterLookup = Clusters.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The rules for an InDesign language name ("$ID/English: USA", "$ID/Spanish: Castilian").</summary>
    public static HyphenationLanguage LanguageOf(string? language) => language switch
    {
        null => HyphenationLanguage.None,
        _ when language.Contains("English", StringComparison.OrdinalIgnoreCase) => HyphenationLanguage.English,
        _ when language.Contains("Spanish", StringComparison.OrdinalIgnoreCase) => HyphenationLanguage.Spanish,
        _ => HyphenationLanguage.None,
    };

    /// <summary>Indices i such that a hyphen may go after word[i].</summary>
    public static IReadOnlyList<int> Points(string word, string? language, HyphenationSettings settings)
    {
        var points = new List<int>();
        AddPoints(word, LanguageOf(language), settings, 0, points);
        return points;
    }

    /// <summary>
    /// Adds <paramref name="offset"/> + i, ascending, for each index i such that a hyphen may go after word[i]
    /// (the composer passes words in place, without copying them out of their run).
    /// </summary>
    public static void AddPoints(ReadOnlySpan<char> word, HyphenationLanguage language, HyphenationSettings settings, int offset, List<int> output)
    {
        if (language == HyphenationLanguage.None || !settings.Enabled || word.Length <= settings.WordsLongerThan
            || (!settings.Capitalized && char.IsUpper(word[0])))
        {
            return;
        }
        Span<char> buffer = word.Length <= 128 ? stackalloc char[word.Length] : new char[word.Length];
        // Most words are ASCII: lower-case those without the culture-aware (ICU) path, which gives the same result.
        if (Ascii.ToLower(word, buffer, out _) != OperationStatus.Done)
        {
            word.ToLowerInvariant(buffer);
        }
        if (language == HyphenationLanguage.English)
        {
            AddEnglishPoints(word, buffer, settings, offset, output);
        }
        else
        {
            AddSpanishPoints(word, buffer, settings, offset, output);
        }
    }

    private static void AddEnglishPoints(ReadOnlySpan<char> word, ReadOnlySpan<char> lower, HyphenationSettings settings, int offset, List<int> output)
    {
        Span<bool> after = lower.Length <= 128 ? stackalloc bool[lower.Length] : new bool[lower.Length];
        English.Patterns.Hyphenate(lower, after);
        for (var i = 0; i < after.Length; i++)
        {
            if (after[i])
            {
                Add(word, i, settings, offset, output);
            }
        }
    }

    private static void AddSpanishPoints(ReadOnlySpan<char> word, ReadOnlySpan<char> lower, HyphenationSettings settings, int offset, List<int> output)
    {
        var first = output.Count;
        var i = 0;
        // Walk vowel nuclei; between two nuclei decide where the consonants split.
        while (i < lower.Length)
        {
            var nucleusEnd = NucleusEnd(lower, i);
            if (nucleusEnd < 0)
            {
                break;
            }
            // Hiatus inside a vowel run: split between strong vowels.
            for (var v = i; v < nucleusEnd; v++)
            {
                if (IsVowel(lower, v) && IsVowel(lower, v + 1) && IsHiatus(lower[v], lower[v + 1]))
                {
                    Add(word, v, settings, offset, output);
                }
            }
            var next = nucleusEnd + 1;
            var consonants = 0;
            while (next < lower.Length && !IsVowel(lower, next))
            {
                next++;
                consonants++;
            }
            if (next >= lower.Length)
            {
                break;
            }
            var start = nucleusEnd + 1;
            var split = consonants switch
            {
                0 => -1,
                1 => nucleusEnd,
                2 => IsCluster(lower.Slice(start, 2)) ? nucleusEnd : start,
                3 => IsCluster(lower.Slice(start + 1, 2)) ? start : start + 1,
                _ => start + 1,
            };
            // "qu"/"gu" + e/i: the u is silent, part of the consonant.
            if (split >= 0)
            {
                Add(word, split, settings, offset, output);
            }
            i = next;
            while (i < lower.Length && !IsVowel(lower, i))
            {
                i++;
            }
        }
        // Ascending and without duplicates (hiatus and consonant splits can meet).
        output.Sort(first, output.Count - first, comparer: null);
        for (var k = output.Count - 1; k > first; k--)
        {
            if (output[k] == output[k - 1])
            {
                output.RemoveAt(k);
            }
        }
    }

    /// <summary>Keeps a point that leaves enough letters on both sides and falls between two letters.</summary>
    private static void Add(ReadOnlySpan<char> word, int p, HyphenationSettings settings, int offset, List<int> output)
    {
        if (p + 1 >= settings.AfterFirst && word.Length - (p + 1) >= settings.BeforeLast && char.IsLetter(word[p]) && char.IsLetter(word[p + 1]))
        {
            output.Add(offset + p);
        }
    }

    private static bool IsCluster(ReadOnlySpan<char> pair) => ClusterLookup.Contains(pair);

    /// <summary>The English patterns, parsed on first use (a document without English text never loads them).</summary>
    private static class English
    {
        public static readonly TexPatterns Patterns = TexPatterns.FromResource("CoreIdml.Pdf.Hyphenation.hyph-en-us.tex");
    }

    /// <summary>The index of the last vowel of the vowel group starting at or after <paramref name="from"/>.</summary>
    private static int NucleusEnd(ReadOnlySpan<char> word, int from)
    {
        var i = from;
        while (i < word.Length && !IsVowel(word, i))
        {
            i++;
        }
        if (i >= word.Length)
        {
            return -1;
        }
        while (i + 1 < word.Length && IsVowel(word, i + 1))
        {
            i++;
        }
        return i;
    }

    private static bool IsVowel(ReadOnlySpan<char> word, int index)
    {
        var c = word[index];
        if (StrongVowels.Contains(c, StringComparison.Ordinal) || WeakVowels.Contains(c, StringComparison.Ordinal))
        {
            // The u of "que/qui/gue/gui" is not a vowel nucleus.
            if (c == 'u' && index > 0 && word[index - 1] is 'q' or 'g' && index + 1 < word.Length && word[index + 1] is 'e' or 'i' or 'é' or 'í')
            {
                return false;
            }
            return true;
        }
        // Final y acts as a vowel ("rey") but it closes the syllable; treat it as a consonant for splitting.
        return false;
    }

    private static bool IsHiatus(char a, char b)
    {
        static bool Strong(char c) => "aeoáéóíú".Contains(c, StringComparison.Ordinal);
        return Strong(a) && Strong(b);
    }
}
