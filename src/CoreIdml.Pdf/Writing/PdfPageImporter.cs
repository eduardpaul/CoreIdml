using System.Globalization;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;
using static CoreIdml.Pdf.Writing.PdfObjectWriter;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace CoreIdml.Pdf.Writing;

/// <summary>A page of another PDF, copied as a Form XObject (vector content stays vector).</summary>
internal sealed record FormResource(string Name, int ObjectNumber, double X0, double Y0, double X1, double Y1);

/// <summary>
/// Copies pages of placed PDFs into the output: the page's content streams (decoded and joined) become a Form
/// XObject whose resources and every object they reference are copied once per source file, with new object numbers.
/// Read with PdfPig; managed only.
/// </summary>
internal sealed class PdfPageImporter : IDisposable
{
    private readonly PdfObjectWriter writer;
    private readonly bool compress;
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);

    public PdfPageImporter(PdfObjectWriter writer, bool compress)
    {
        this.writer = writer;
        this.compress = compress;
    }

    public FormResource Import(string path, int pageNumber, string crop, string name)
    {
        if (!sources.TryGetValue(path, out var source))
        {
            source = new Source(PdfSource.Open(File.ReadAllBytes(path), "Placed PDF " + PdfSource.Describe(path)));
            sources[path] = source;
        }
        var document = source.Document;
        if (pageNumber < 1 || pageNumber > document.NumberOfPages)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} has no page {pageNumber}.");
        }
        var page = document.GetPage(pageNumber).Dictionary;

        IToken? Inherited(string key)
        {
            var current = page;
            for (var depth = 0; current is not null && depth < 32; depth++)
            {
                if (current.TryGet(NameToken.Create(key), out var value))
                {
                    return Resolve(value, document);
                }
                current = current.TryGet(NameToken.Parent, out var parent) ? Resolve(parent, document) as DictionaryToken : null;
            }
            return null;
        }

        double[] Box(string key) => Inherited(key) is ArrayToken { Data.Count: 4 } array
            ? array.Data.Select(t => Resolve(t, document) is NumericToken n ? n.Double : 0).ToArray()
            : [];
        var media = Box("MediaBox") is { Length: 4 } m ? m : [0, 0, 612, 792];
        var cropBox = Box("CropBox") is { Length: 4 } c ? c : media;
        var box = crop switch
        {
            "CropArt" => Box("ArtBox") is { Length: 4 } a ? a : cropBox,
            "CropTrim" => Box("TrimBox") is { Length: 4 } t ? t : cropBox,
            "CropBleed" => Box("BleedBox") is { Length: 4 } b ? b : cropBox,
            "CropMedia" => media,
            _ => cropBox,
        };
        var (x0, y0, x1, y1) = (Math.Min(box[0], box[2]), Math.Min(box[1], box[3]), Math.Max(box[0], box[2]), Math.Max(box[1], box[3]));

        // Content: one stream or an array of streams, decoded and joined.
        using var content = new MemoryStream();
        var contents = Inherited("Contents");
        var streams = contents switch
        {
            StreamToken single => [single],
            ArrayToken array => array.Data.Select(t => Resolve(t, document)).OfType<StreamToken>().ToList(),
            _ => new List<StreamToken>(),
        };
        foreach (var stream in streams)
        {
            content.Write(stream.Decode(document.Structure.FilterProvider).ToArray());
            content.WriteByte((byte)'\n');
        }

        var resources = Inherited("Resources") is { } resourceToken ? Serialize(resourceToken, source) : "<<>>";
        var number = writer.Reserve();
        writer.WriteStream(number, $"/Type /XObject /Subtype /Form /BBox [{Number(x0)} {Number(y0)} {Number(x1)} {Number(y1)}] /Resources {resources}", content.ToArray(), compress);
        WritePending(source);
        return new FormResource(name, number, x0, y0, x1, y1);
    }

    private static IToken? Resolve(IToken token, PdfPigDocument document)
    {
        for (var depth = 0; token is IndirectReferenceToken reference && depth < 16; depth++)
        {
            token = document.Structure.GetObject(reference.Data).Data;
        }
        return token;
    }

    /// <summary>PDF syntax for a token; indirect references are renumbered and their objects queued for copying.</summary>
    private string Serialize(IToken token, Source source)
    {
        switch (token)
        {
            case IndirectReferenceToken reference:
                if (!source.Numbers.TryGetValue(reference.Data, out var number))
                {
                    number = writer.Reserve();
                    source.Numbers[reference.Data] = number;
                    source.Pending.Enqueue(reference.Data);
                }
                return Ref(number);
            case DictionaryToken dictionary:
                var builder = new StringBuilder("<<");
                foreach (var (key, value) in dictionary.Data)
                {
                    builder.Append(Name(key)).Append(' ').Append(Serialize(value, source)).Append(' ');
                }
                return builder.Append(">>").ToString();
            case ArrayToken array:
                return "[" + string.Join(' ', array.Data.Select(item => Serialize(item, source))) + "]";
            case NameToken name:
                return Name(name.Data);
            case NumericToken numeric:
                return numeric.HasDecimalPlaces ? Number(numeric.Double) : numeric.Long.ToString(CultureInfo.InvariantCulture);
            case BooleanToken boolean:
                return boolean.Data ? "true" : "false";
            case StringToken text:
                return Hex(text.GetBytes());
            case HexToken hex:
                return Hex(hex.Bytes.ToArray());
            default:
                return "null";
        }
    }

    private static string Hex(byte[] bytes)
    {
        var builder = new StringBuilder("<", (bytes.Length * 2) + 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.Append('>').ToString();
    }

    private void WritePending(Source source)
    {
        while (source.Pending.Count > 0)
        {
            var reference = source.Pending.Dequeue();
            var number = source.Numbers[reference];
            var token = source.Document.Structure.GetObject(reference).Data;
            if (token is StreamToken stream)
            {
                var dictionary = new StringBuilder();
                foreach (var (key, value) in stream.StreamDictionary.Data)
                {
                    if (key is not ("Length" or "Length1" or "Length2" or "Length3" or "DL"))
                    {
                        dictionary.Append(Name(key)).Append(' ').Append(Serialize(value, source)).Append(' ');
                    }
                    else if (key != "Length" && Resolve(value, source.Document) is NumericToken length)
                    {
                        dictionary.Append(Name(key)).Append(' ').Append(length.Long.ToString(CultureInfo.InvariantCulture)).Append(' ');
                    }
                }
                writer.WriteStream(number, dictionary.ToString(), stream.Data.Span, compress: false);
            }
            else
            {
                writer.WriteObject(number, Serialize(token, source));
            }
        }
    }

    public void Dispose()
    {
        foreach (var source in sources.Values)
        {
            source.Document.Dispose();
        }
        sources.Clear();
    }

    private sealed class Source(PdfPigDocument document)
    {
        public PdfPigDocument Document { get; } = document;

        public Dictionary<IndirectReference, int> Numbers { get; } = [];

        public Queue<IndirectReference> Pending { get; } = new();
    }
}
