namespace CoreIdml;

/// <summary>
/// Why an operation failed. The values map one to one onto the status codes of the C API and of every language
/// binding, so a failure reads the same in .NET, Node, Python, Dart and the browser.
/// </summary>
public enum IdmlErrorKind
{
    /// <summary>The input is not a usable IDML package: not a zip, no designmap.xml, damaged data, malformed XML.</summary>
    InvalidFormat,

    /// <summary>Something the caller named (a part, a story, an id) does not exist in the document.</summary>
    NotFound,

    /// <summary>The document is valid but uses something this library does not handle.</summary>
    Unsupported,

    /// <summary>The operation is not valid for the document as it is now.</summary>
    InvalidOperation,
}

/// <summary>A failure the caller can act on: bad input, a missing part, an unsupported document.</summary>
public sealed class IdmlException : Exception
{
    /// <summary>Creates the exception for an operation the document cannot satisfy (<see cref="IdmlErrorKind.InvalidOperation"/>).</summary>
    public IdmlException()
        : this(IdmlErrorKind.InvalidOperation, "The operation is not valid for this document.")
    {
    }

    /// <summary>Creates the exception for an operation the document cannot satisfy (<see cref="IdmlErrorKind.InvalidOperation"/>).</summary>
    public IdmlException(string message)
        : this(IdmlErrorKind.InvalidOperation, message)
    {
    }

    /// <summary>Creates the exception for an operation the document cannot satisfy (<see cref="IdmlErrorKind.InvalidOperation"/>).</summary>
    public IdmlException(string message, Exception innerException)
        : this(IdmlErrorKind.InvalidOperation, message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public IdmlException(IdmlErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Why the operation failed.</summary>
    public IdmlErrorKind Kind { get; }
}
