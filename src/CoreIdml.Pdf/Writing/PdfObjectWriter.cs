using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace CoreIdml.Pdf.Writing;

/// <summary>
/// Writes PDF objects to a stream and finishes the file with a cross-reference table. Object numbers are reserved
/// first and written in any order. Output is deterministic: no dates or random ids unless the caller adds them.
/// </summary>
internal sealed class PdfObjectWriter
{
    private readonly Stream output;
    private readonly Dictionary<int, long> offsets = [];
    private long position;
    private int nextObject = 1;

    public PdfObjectWriter(Stream output)
    {
        this.output = output;
        WriteRaw("%PDF-1.7\n%âãÏÓ\n", latin1: true);
    }

    public int Reserve() => nextObject++;

    public void WriteObject(int number, string body)
    {
        Begin(number);
        WriteRaw(body);
        WriteRaw("\nendobj\n");
    }

    /// <summary>Writes a stream object; <paramref name="dictionary"/> holds entries without Length/Filter.</summary>
    public void WriteStream(int number, string dictionary, ReadOnlySpan<byte> data, bool compress, string? filter = null)
    {
        if (!compress)
        {
            WriteStreamObject(number, dictionary, filter, data);
            return;
        }
        using var buffer = Compress(data);
        WriteCompressedStream(number, dictionary, buffer);
    }

    /// <summary>
    /// Compresses stream data the way <see cref="WriteStream"/> does (zlib, optimal level). Thread-safe, so large
    /// images can be compressed ahead on other threads with the same result.
    /// </summary>
    public static MemoryStream Compress(ReadOnlySpan<byte> data)
    {
        var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }
        return buffer;
    }

    /// <summary>Writes a stream object whose data came from <see cref="Compress"/> (from its buffer, no copy).</summary>
    public void WriteCompressedStream(int number, string dictionary, MemoryStream compressed) =>
        WriteStreamObject(number, dictionary, "/FlateDecode", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));

    private void WriteStreamObject(int number, string dictionary, string? filter, ReadOnlySpan<byte> payload)
    {
        Begin(number);
        WriteRaw("<<" + dictionary + (filter is null ? string.Empty : " /Filter " + filter) + " /Length " + payload.Length.ToString(CultureInfo.InvariantCulture) + ">>\nstream\n");
        output.Write(payload);
        position += payload.Length;
        WriteRaw("\nendstream\nendobj\n");
    }

    public void Finish(int catalog, int info)
    {
        var xref = position;
        var count = nextObject;
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {count}\n0000000000 65535 f \n");
        for (var number = 1; number < count; number++)
        {
            builder.Append(offsets.TryGetValue(number, out var offset)
                ? string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n")
                : "0000000000 65535 f \n");
        }
        builder.Append(CultureInfo.InvariantCulture, $"trailer\n<</Size {count} /Root {catalog} 0 R /Info {info} 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        WriteRaw(builder.ToString());
        output.Flush();
    }

    private void Begin(int number)
    {
        offsets[number] = position;
        WriteRaw(number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
    }

    private void WriteRaw(string text, bool latin1 = false)
    {
        var bytes = latin1 ? Encoding.Latin1.GetBytes(text) : Encoding.ASCII.GetBytes(text);
        output.Write(bytes);
        position += bytes.Length;
    }

    // ---- value formatting ------------------------------------------------------------------------------

    public static string Number(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }
        var rounded = Math.Round(value, 4);
        if (rounded == 0)
        {
            return "0";
        }
        return rounded.ToString("0.####", CultureInfo.InvariantCulture);
    }

    public static string Ref(int number) => number.ToString(CultureInfo.InvariantCulture) + " 0 R";

    /// <summary>A PDF name with #xx escapes for delimiters and non-ASCII.</summary>
    public static string Name(string value)
    {
        var builder = new StringBuilder("/");
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is > 0x20 and < 0x7F && b is not ((byte)'#' or (byte)'/' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'%'))
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"#{b:X2}");
            }
        }
        return builder.ToString();
    }

    /// <summary>A text string: literal when ASCII, else UTF-16BE hex with a byte-order mark.</summary>
    public static string TextString(string value)
    {
        if (value.All(c => c is >= ' ' and < (char)0x7F))
        {
            return "(" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal) + ")";
        }
        var builder = new StringBuilder("<FEFF");
        foreach (var b in Encoding.BigEndianUnicode.GetBytes(value))
        {
            builder.Append(CultureInfo.InvariantCulture, $"{b:X2}");
        }
        return builder.Append('>').ToString();
    }
}
