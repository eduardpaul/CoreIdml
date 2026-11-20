namespace CoreIdml;

/// <summary>Setup of a new document (File &gt; New &gt; Document in InDesign). All lengths in points.</summary>
public sealed class DocumentOptions
{
    /// <summary>Page size; defaults to A4 portrait.</summary>
    public Size PageSize { get; init; } = PageSizes.A4;

    /// <summary>Page margins; defaults to 36 pt (0.5 in) on every side.</summary>
    public Spacing Margins { get; init; } = Spacing.All(36);

    /// <summary>Two-page spreads (left/right pages) instead of single pages.</summary>
    public bool FacingPages { get; init; }

    /// <summary>Number of pages created up front (at least one).</summary>
    public int PageCount { get; init; } = 1;

    public int Columns { get; init; } = 1;

    public double ColumnGutter { get; init; } = 12;

    /// <summary>Uniform bleed around the page.</summary>
    public double Bleed { get; init; }
}

/// <summary>
/// The document's baseline grid: lines every <paramref name="Increment"/> points (usually the body leading) from
/// <paramref name="Start"/> points below the top of the page, or below the top margin when
/// <paramref name="RelativeToTopMargin"/>.
/// </summary>
public sealed record BaselineGridOptions(double Start, double Increment, bool RelativeToTopMargin = false);
