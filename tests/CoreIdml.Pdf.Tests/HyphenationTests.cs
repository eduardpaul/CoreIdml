using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Hyphenation points: English by Liang's algorithm over the TeX hyph-en-us patterns, Spanish by syllabification,
/// chosen by the text's language and limited by the paragraph's hyphenation settings.
/// </summary>
public sealed class HyphenationTests
{
    // InDesign's defaults: words longer than 5 letters, 2 letters before and after a hyphen, capitalized words too.
    private static readonly HyphenationSettings Defaults = new(true, 2, 2, 5, true);

    private static string Hyphenate(string word, string? language, HyphenationSettings? settings = null)
    {
        var points = Hyphenator.Points(word, language, settings ?? Defaults);
        return string.Concat(word.Select((c, i) => points.Contains(i) ? c + "-" : c.ToString()));
    }

    [Theory]
    // Liang's own examples (The TeXbook, appendix H) and points InDesign 20 chose in the doc-samples references.
    [InlineData("hyphenation", "hy-phen-ation")]
    [InlineData("computer", "com-put-er")]
    [InlineData("returned", "re-turned")]
    [InlineData("accomplished", "ac-com-plished")]
    [InlineData("neighbourhoods", "neigh-bour-hoods")]
    [InlineData("possession", "pos-ses-sion")]
    [InlineData("immediately", "im-me-di-ate-ly")]
    [InlineData("objection", "ob-jec-tion")]
    [InlineData("unconditional", "un-con-di-tion-al")]
    // Knuth's exception list: "present" and "project" (noun or verb) are never hyphenated.
    [InlineData("present", "present")]
    [InlineData("projects", "projects")]
    [InlineData("associates", "as-so-ciates")]
    public void English_words_break_at_the_TeX_patterns_points(string word, string expected) =>
        Assert.Equal(expected, Hyphenate(word, Languages.EnglishUs));

    [Fact]
    public void Exceptions_and_patterns_keep_the_case_of_the_word()
    {
        Assert.Equal("Hy-phen-ation", Hyphenate("Hyphenation", Languages.EnglishUs));
        Assert.Equal("Present", Hyphenate("Present", Languages.EnglishUs));
    }

    [Fact]
    public void Letters_before_and_after_the_hyphen_follow_the_settings()
    {
        // 08-para-hyphenation-justification p2: "after first 4 / before last 4" keeps "re-turned" whole.
        var four = Defaults with { AfterFirst = 4, BeforeLast = 4 };
        Assert.Equal("returned", Hyphenate("returned", Languages.EnglishUs, four));
        Assert.Equal("hyphen-ation", Hyphenate("hyphenation", Languages.EnglishUs, four));
        // Two letters at the end are allowed by default ("im-me-di-ate-ly").
        Assert.Equal("im-me-di-ate-ly", Hyphenate("immediately", Languages.EnglishUs, Defaults with { BeforeLast = 2 }));
        Assert.Equal("im-me-di-ately", Hyphenate("immediately", Languages.EnglishUs, Defaults with { BeforeLast = 3 }));
    }

    [Fact]
    public void Short_capitalized_and_unhyphenated_words_stay_whole()
    {
        // "Words longer than 10": "returned" (8 letters) is not hyphenated (08-para-hyphenation-justification p2).
        Assert.Equal("returned", Hyphenate("returned", Languages.EnglishUs, Defaults with { WordsLongerThan = 10 }));
        Assert.Equal("Computer", Hyphenate("Computer", Languages.EnglishUs, Defaults with { Capitalized = false }));
        Assert.Equal("com-put-er", Hyphenate("computer", Languages.EnglishUs, Defaults with { Capitalized = false }));
        Assert.Equal("computer", Hyphenate("computer", Languages.EnglishUs, Defaults with { Enabled = false }));
    }

    [Theory]
    [InlineData("$ID/English: USA", "English")]
    [InlineData("$ID/English: UK", "English")]
    [InlineData("$ID/English: Canadian", "English")]
    [InlineData("$ID/Spanish: Castilian", "Spanish")]
    [InlineData("$ID/French", "None")]
    [InlineData("$ID/German: Reformed", "None")]
    [InlineData("[No Language]", "None")]
    [InlineData(null, "None")]
    public void The_language_picks_the_rules(string? language, string expected) =>
        Assert.Equal(expected, Hyphenator.LanguageOf(language).ToString());

    [Fact]
    public void English_and_Spanish_break_the_same_word_differently()
    {
        Assert.Equal("ca-pi-tal", Hyphenate("capital", Languages.Spanish, Defaults with { WordsLongerThan = 0 }));
        Assert.Equal("cap-i-tal", Hyphenate("capital", Languages.EnglishUs, Defaults with { WordsLongerThan = 0 }));
        Assert.Equal("capital", Hyphenate("capital", "$ID/French", Defaults with { WordsLongerThan = 0 }));
    }

    [Fact]
    public void Patterns_parse_digits_and_exceptions()
    {
        // Liang's example: "hy-phen-ation" from the patterns hy3ph, he2n, hen5at, 1na, n2at, 1tio, 2io, o2n.
        var patterns = TexPatterns.Parse("""
            % comment
            \patterns{ % the patterns
            hy3ph he2n hena4 hen5at 1na n2at 1tio 2io o2n
            }
            \hyphenation{
            ta-ble
            }
            """);
        var after = new bool[11];
        patterns.Hyphenate("hyphenation", after);
        Assert.Equal([1, 5], Enumerable.Range(0, 11).Where(i => after[i]));
        var table = new bool[5];
        patterns.Hyphenate("table", table);
        Assert.Equal([1], Enumerable.Range(0, 5).Where(i => table[i]));
    }
}
