namespace CoreIdml.Cli.Tools;

/// <summary>Why a tool call failed; the CLI turns it into an exit code (see <see cref="ToolException.ExitCode"/>).</summary>
internal enum ToolErrorKind
{
    /// <summary>The operation itself failed (exit code 1).</summary>
    Failed,

    /// <summary>Missing or malformed arguments (exit code 2).</summary>
    InvalidInput,

    /// <summary>A file, story, item, link, page or style that the arguments name does not exist (exit code 3).</summary>
    NotFound,
}

/// <summary>A tool failure with a message meant for the caller (an agent or a shell user) to act on.</summary>
internal sealed class ToolException : Exception
{
    public ToolException(ToolErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public ToolException()
        : this(ToolErrorKind.Failed, "Tool failed.")
    {
    }

    public ToolException(string message)
        : this(ToolErrorKind.Failed, message)
    {
    }

    public ToolException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = ToolErrorKind.Failed;
    }

    public ToolErrorKind Kind { get; }

    public static ToolException Invalid(string message) => new(ToolErrorKind.InvalidInput, message);

    public static ToolException NotFound(string message) => new(ToolErrorKind.NotFound, message);

    /// <summary>The kind of any exception a tool may throw.</summary>
    public static ToolErrorKind KindOf(Exception exception) => exception switch
    {
        ToolException tool => tool.Kind,
        IdmlException { Kind: IdmlErrorKind.InvalidFormat } => ToolErrorKind.InvalidInput,
        IdmlException { Kind: IdmlErrorKind.NotFound } => ToolErrorKind.NotFound,
        FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => ToolErrorKind.NotFound,
        ArgumentException or FormatException or System.Text.Json.JsonException or System.Xml.XmlException
            or InvalidDataException => ToolErrorKind.InvalidInput,
        _ => ToolErrorKind.Failed,
    };

    /// <summary>0 success, 1 failed, 2 invalid input, 3 not found.</summary>
    public static int ExitCode(ToolErrorKind kind) => kind switch
    {
        ToolErrorKind.InvalidInput => 2,
        ToolErrorKind.NotFound => 3,
        _ => 1,
    };

    /// <summary>The message shown to the caller: expected failures as they are, unexpected ones with their type.</summary>
    public static string Describe(Exception exception) =>
        // ZipArchive's message ("End of Central Directory record could not be found") doesn't say what is wrong.
        exception is InvalidDataException
            ? "Not a valid .idml or .pdf file (an .idml file is a zip package): " + exception.Message
            : exception is IdmlException { Kind: IdmlErrorKind.InvalidFormat, InnerException: InvalidDataException zip }
            ? "Not a valid .idml or .pdf file (an .idml file is a zip package): " + zip.Message
            : exception is ToolException or IdmlException or IOException or UnauthorizedAccessException or ArgumentException
            or KeyNotFoundException or System.Text.Json.JsonException or System.Xml.XmlException or FormatException
            ? exception.Message
            : exception.GetType().Name + ": " + exception.Message;
}
