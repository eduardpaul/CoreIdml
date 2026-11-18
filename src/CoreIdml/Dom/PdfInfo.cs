using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreIdml.Dom;

/// <summary>Reads the first page's MediaBox from a PDF (enough to size a placed page; no full PDF parser).</summary>
internal static partial class PdfInfo
{
    public static Size? ReadMediaBox(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[(int)Math.Min(stream.Length, 4 << 20)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        // Latin-1 keeps byte offsets and never fails on binary streams.
        var text = Encoding.Latin1.GetString(buffer, 0, read);
        if (!text.StartsWith("%PDF", StringComparison.Ordinal))
        {
            throw new IdmlException(IdmlErrorKind.InvalidFormat, $"'{path}' is not a PDF file.");
        }
        var match = MediaBox().Match(text);
        if (!match.Success)
        {
            return null;
        }
        double Value(int group) => double.Parse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        var width = Math.Abs(Value(3) - Value(1));
        var height = Math.Abs(Value(4) - Value(2));
        return width > 0 && height > 0 ? new Size(width, height) : null;
    }

    [GeneratedRegex(@"/MediaBox\s*\[\s*(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s*\]")]
    private static partial Regex MediaBox();
}
