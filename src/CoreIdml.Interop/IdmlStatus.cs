namespace CoreIdml.Interop;

/// <summary>
/// Result of a call across a language boundary. The numbers are part of the ABI: they are the
/// <c>COREIDML_OK</c> / <c>COREIDML_ERR_*</c> values of <c>include/coreidml.h</c> and the status constants generated
/// into every binding. A value is never renumbered or reused; new ones are added at the end.
/// </summary>
public enum IdmlStatus
{
    /// <summary>The call succeeded.</summary>
    Ok = 0,

    /// <summary>An unexpected failure; the message names the exception.</summary>
    Failed = -1,

    /// <summary>The handle is not an open document (never was, or was closed).</summary>
    InvalidHandle = -2,

    /// <summary>An argument is missing or malformed.</summary>
    InvalidArgument = -3,

    /// <summary>The caller cancelled the operation.</summary>
    Cancelled = -4,

    /// <summary>The input is not a usable IDML package.</summary>
    InvalidFormat = -5,

    /// <summary>Something the caller named does not exist in the document.</summary>
    NotFound = -6,

    /// <summary>The document uses something the library does not handle.</summary>
    Unsupported = -7,

    /// <summary>The operation is not valid for the document as it is now.</summary>
    InvalidOperation = -8,

    /// <summary>Reading or writing a file failed.</summary>
    Io = -9,
}
