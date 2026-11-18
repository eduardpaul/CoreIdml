using System.Text;
using CoreIdml.Cli;

// coreidml <command> ...: the command line (Cli) and the MCP server (McpServer) over the same tools.
// All text goes out as UTF-8, whatever the console's code page: JSON piped into another program must survive
// characters such as ″ or ° (Windows would otherwise write them in the OEM code page, or best-fit them to ").
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var stderr = Console.OpenStandardError();

if (args is ["mcp", ..])
{
    var root = args.Length >= 3 && args[1] == "--root" ? args[2] : null;
    if (args.Length > 1 && root is null)
    {
        Console.Error.WriteLine("usage: coreidml mcp [--root <dir>]");
        return 2;
    }
    if (root is not null && !Directory.Exists(root))
    {
        Console.Error.WriteLine($"error: --root folder not found: {Path.GetFullPath(root)}");
        return 3;
    }
    var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    using var input = new StreamReader(stdin, utf8);
    using var output = new StreamWriter(stdout, utf8) { AutoFlush = false };
    return await new McpServer(new McpServerOptions { Root = root }).RunAsync(input, output).ConfigureAwait(false);
}

// An interactive Windows console shows bytes in its own code page: switch it to UTF-8 while we print.
var restoreEncoding = OperatingSystem.IsWindows() && !Console.IsOutputRedirected ? ConsoleEncoding.SwitchToUtf8() : null;
try
{
    return Cli.Run(args, new CliConsole(stdin, stdout, stderr));
}
finally
{
    restoreEncoding?.Invoke();
}

/// <summary>Switches an interactive console to UTF-8 output and back.</summary>
internal static class ConsoleEncoding
{
    public static Action? SwitchToUtf8()
    {
        try
        {
            var previous = Console.OutputEncoding;
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return () =>
            {
                try
                {
                    Console.OutputEncoding = previous;
                }
                catch (IOException)
                {
                    // The console went away; nothing to restore.
                }
            };
        }
        catch (IOException)
        {
            return null;
        }
    }
}
