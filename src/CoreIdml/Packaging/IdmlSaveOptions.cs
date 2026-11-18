namespace CoreIdml.Packaging;

/// <summary>How a package is written.</summary>
public sealed record IdmlSaveOptions
{
    /// <summary>
    /// Zip timestamp of the parts that were created or changed; the current time when <c>null</c>.
    /// Parts that were not touched keep the timestamp they were read with, so saving an unchanged package always
    /// gives the same bytes, and with a fixed timestamp so does saving an edited one.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }
}
