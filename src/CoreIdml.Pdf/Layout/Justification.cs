namespace CoreIdml.Pdf.Layout;

/// <summary>
/// A paragraph's justification settings (Paragraph &gt; Justification): word spacing as a fraction of each space's
/// natural width (InDesign's 80/100/133% are 0.8, 1 and 1.33), letter spacing as a fraction of the space width of each
/// glyph's font (InDesign's percentages of the space band: 5% is 0.05), glyph scaling as a factor, and how a justified
/// line holding a single word is set (<c>FullyJustified</c>, <c>LeftAlign</c>, <c>CenterAlign</c>, <c>RightAlign</c>).
/// The desired values apply to every paragraph; the minimum and maximum only to justified ones.
/// </summary>
internal sealed record JustificationSettings(
    double MinimumWordSpacing, double DesiredWordSpacing, double MaximumWordSpacing,
    double MinimumLetterSpacing, double DesiredLetterSpacing, double MaximumLetterSpacing,
    double MinimumGlyphScaling, double DesiredGlyphScaling, double MaximumGlyphScaling,
    string SingleWordJustification)
{
    /// <summary>InDesign's defaults: word spacing 80/100/133%, letter spacing 0/0/0%, glyph scaling 100/100/100%.</summary>
    public static readonly JustificationSettings Default = new(0.8, 1, 1.33, 0, 0, 0, 1, 1, 1, "FullyJustified");

    /// <summary>
    /// The settings from InDesign's percentages, each range put in order around its desired value (InDesign's dialog
    /// does not accept a minimum above the desired value or a maximum below it).
    /// </summary>
    public static JustificationSettings FromPercentages(
        double minimumWord, double desiredWord, double maximumWord,
        double minimumLetter, double desiredLetter, double maximumLetter,
        double minimumGlyph, double desiredGlyph, double maximumGlyph, string singleWord)
    {
        var word = Math.Max(0, desiredWord / 100);
        var letter = desiredLetter / 100;
        var glyph = Math.Max(0.01, desiredGlyph / 100);
        return new JustificationSettings(
            Math.Clamp(minimumWord / 100, 0, word), word, Math.Max(word, maximumWord / 100),
            Math.Min(minimumLetter / 100, letter), letter, Math.Max(letter, maximumLetter / 100),
            Math.Clamp(minimumGlyph / 100, 0.01, glyph), glyph, Math.Max(glyph, maximumGlyph / 100), singleWord);
    }

    /// <summary>Whether the desired spacing differs from the natural one (shaping then changes the advances).</summary>
    public bool ChangesNaturalSpacing => DesiredWordSpacing != 1 || DesiredLetterSpacing != 0;
}

/// <summary>
/// What justification can change in a line, measured at the desired word and letter spacing and before glyph scaling:
/// its <paramref name="Width"/>, the natural (100%) width of its word spaces (<paramref name="WordSpace"/>, the base of
/// the word spacing percentages), and the space bands of the glyphs that letter spacing follows
/// (<paramref name="LetterSpace"/>: every glyph but the last).
/// </summary>
internal readonly record struct LineSpace(double Width, double WordSpace, double LetterSpace);

/// <summary>
/// How a line is spaced: each word space changes by <paramref name="Word"/> times its natural width, each glyph but the
/// last is followed by <paramref name="Letter"/> times its space band, and everything is scaled horizontally by
/// <paramref name="Scale"/>. <paramref name="Ratio"/> is how far the line is from its desired spacing, as a share of
/// the room its limits give (positive looser, negative tighter; beyond ±1 the line breaks the limits; infinite when a
/// line must stretch and nothing in it may).
/// </summary>
internal readonly record struct LineSpacing(double Word, double Letter, double Scale, double Ratio);

/// <summary>
/// Justification as InDesign does it, measured in doc-samples/08-para-hyphenation-justification (Liberation Serif 11 pt,
/// 120 pt columns):
/// <list type="bullet">
/// <item>Word and letter spacing change together, each by the same share of its range (p4, letter spacing -5/0/+5%:
/// a line set 8% past both maximums has word spaces at 135.6% and 5.4% letter spacing).</item>
/// <item>Glyph scaling comes only when word and letter spacing are at their limits (p4, glyph scaling 90/100/110%: a
/// line that fills the column with word spaces at 133% keeps its glyphs at 100%; one with word spaces at their 80%
/// minimum has its glyphs at 97.3%).</item>
/// <item>What the limits do not give a stretched line, word and letter spacing take past their maximums, again in
/// proportion (p4); with no range at all (100/100/100% word spacing, letter spacing 0/0/0%) the word spaces take it
/// alone (303% in p4), and a line without word spaces is letter spaced across (single-word justification
/// <c>FullyJustified</c>: "accomplished" alone in a line with 5.3 pt after each letter).</item>
/// <item>A line is compressed past the minimum only when the paragraph composer chooses it (p4, word spacing 50%
/// minimum: one line at 26%); word spaces never go below nothing.</item>
/// </list>
/// Glyph scaling changes the spaces too: a word space at 80% of a glyph scaled to 97.3% is 80% of the scaled space.
/// </summary>
internal static class Justifier
{
    private const double Epsilon = 1e-9;

    /// <summary>
    /// The tightest width <paramref name="line"/> can take when compressed: within its limits, or, with
    /// <paramref name="pastMinimum"/>, with its word spaces gone.
    /// </summary>
    public static double TightestWidth(LineSpace line, JustificationSettings settings, bool pastMinimum = false) =>
        (line.Width - (pastMinimum ? line.WordSpace * settings.DesiredWordSpacing
            + (line.LetterSpace * (settings.DesiredLetterSpacing - settings.MinimumLetterSpacing)) : Shrinkable(line, settings)))
        * settings.MinimumGlyphScaling;

    /// <summary>
    /// The spacing that sets <paramref name="line"/> to <paramref name="available"/>: stretched only when
    /// <paramref name="stretch"/>, compressed only when <paramref name="shrink"/>, otherwise at the desired spacing.
    /// </summary>
    public static LineSpacing Solve(LineSpace line, double available, JustificationSettings settings, bool stretch, bool shrink)
    {
        var scale = settings.DesiredGlyphScaling;
        var need = (available / scale) - line.Width;
        if (need > Epsilon && stretch)
        {
            return Stretch(line, available, settings);
        }
        if (need < -Epsilon && shrink)
        {
            return Shrink(line, available, settings);
        }
        return new LineSpacing(0, 0, scale, 0);
    }

    private static double Shrinkable(LineSpace line, JustificationSettings settings) =>
        (line.WordSpace * (settings.DesiredWordSpacing - settings.MinimumWordSpacing))
        + (line.LetterSpace * (settings.DesiredLetterSpacing - settings.MinimumLetterSpacing));

    private static LineSpacing Stretch(LineSpace line, double available, JustificationSettings settings)
    {
        var wordRange = settings.MaximumWordSpacing - settings.DesiredWordSpacing;
        var letterRange = settings.MaximumLetterSpacing - settings.DesiredLetterSpacing;
        var capacity = (line.WordSpace * wordRange) + (line.LetterSpace * letterRange);
        var scale = settings.DesiredGlyphScaling;
        var need = (available / scale) - line.Width;
        // The share of the room within all three limits, for the composer's choice of breaks.
        var scaleRoom = (line.Width + capacity) * (settings.MaximumGlyphScaling / scale - 1);
        var ratio = capacity + scaleRoom > Epsilon ? need / (capacity + scaleRoom) : double.PositiveInfinity;
        if (capacity > Epsilon && need <= capacity)
        {
            var share = need / capacity;
            return new LineSpacing(share * wordRange, share * letterRange, scale, ratio);
        }
        // Word and letter spacing at their maximums: then glyph scaling, then past the maximums.
        scale = Math.Min(settings.MaximumGlyphScaling, Math.Max(scale, available / Math.Max(Epsilon, line.Width + capacity)));
        need = (available / scale) - line.Width - capacity;
        if (need <= Epsilon)
        {
            return new LineSpacing(capacity > Epsilon ? wordRange : 0, capacity > Epsilon ? letterRange : 0, scale, Math.Min(ratio, 1));
        }
        if (capacity > Epsilon)
        {
            var share = 1 + (need / capacity);
            return new LineSpacing(share * wordRange, share * letterRange, scale, ratio);
        }
        if (line.WordSpace > Epsilon)
        {
            return new LineSpacing(need / line.WordSpace, 0, scale, ratio);
        }
        if (line.LetterSpace > Epsilon && settings.SingleWordJustification == "FullyJustified")
        {
            return new LineSpacing(0, need / line.LetterSpace, scale, ratio);
        }
        return new LineSpacing(0, 0, scale, ratio);
    }

    private static LineSpacing Shrink(LineSpace line, double available, JustificationSettings settings)
    {
        var wordRange = settings.DesiredWordSpacing - settings.MinimumWordSpacing;
        var letterRange = settings.DesiredLetterSpacing - settings.MinimumLetterSpacing;
        var capacity = Shrinkable(line, settings);
        var scale = settings.DesiredGlyphScaling;
        var need = line.Width - (available / scale);
        var scaleRoom = (line.Width - capacity) * (1 - settings.MinimumGlyphScaling / scale);
        var ratio = capacity + scaleRoom > Epsilon ? -need / (capacity + scaleRoom) : double.NegativeInfinity;
        if (capacity > Epsilon && need <= capacity)
        {
            var part = need / capacity;
            return new LineSpacing(-part * wordRange, -part * letterRange, scale, ratio);
        }
        scale = Math.Max(settings.MinimumGlyphScaling, Math.Min(scale, available / Math.Max(Epsilon, line.Width - capacity)));
        need = line.Width - capacity - (available / scale);
        if (need <= Epsilon)
        {
            return new LineSpacing(capacity > Epsilon ? -wordRange : 0, capacity > Epsilon ? -letterRange : 0, scale, Math.Max(ratio, -1));
        }
        if (capacity <= Epsilon)
        {
            // Nothing more may give: the line runs past the column.
            return new LineSpacing(0, 0, scale, ratio);
        }
        // Past the minimums, in proportion, until the word spaces are gone.
        var past = 1 + (need / capacity);
        if (wordRange > Epsilon && line.WordSpace > Epsilon)
        {
            past = Math.Min(past, settings.DesiredWordSpacing / wordRange);
        }
        return new LineSpacing(-past * wordRange, -past * letterRange, scale, ratio);
    }
}
