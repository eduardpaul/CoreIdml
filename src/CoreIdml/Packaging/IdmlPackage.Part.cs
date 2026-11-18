using System.Xml.Linq;

namespace CoreIdml.Packaging;

public sealed partial class IdmlPackage
{
    /// <summary>
    /// What the package holds for a part. A closed hierarchy (C# 15): the three states are the only ones, the
    /// compiler checks that every switch covers them, and no state can carry a combination that makes no sense
    /// (bytes that no longer match their XML, say).
    /// </summary>
    private closed record PartState;

    /// <summary>The bytes as read or as given; never parsed.</summary>
    private sealed record RawPart(ReadOnlyMemory<byte> Bytes) : PartState;

    /// <summary>Parsed and not changed since: the original bytes are still what gets written.</summary>
    private sealed record ParsedPart(ReadOnlyMemory<byte> Bytes, XDocument Xml) : PartState;

    /// <summary>XML that was changed or supplied by the caller: serialized when written.</summary>
    private sealed record EditedPart(XDocument Xml, bool TrailingNewline) : PartState;

    private sealed class Part(PartState state, bool modified, DateTimeOffset? archiveTimestamp = null, bool stored = false)
    {
        public PartState State { get; private set; } = state;

        public bool IsModified { get; private set; } = modified;

        /// <summary>Whether the part is written uncompressed, as it was in the archive it came from.</summary>
        public bool Stored { get; } = stored;

        /// <summary>The timestamp to write: the original one while the part is untouched.</summary>
        public DateTimeOffset TimestampOr(DateTimeOffset now) => IsModified || archiveTimestamp is null ? now : archiveTimestamp.Value;

        public ReadOnlyMemory<byte> GetBytes() => State switch
        {
            RawPart raw => raw.Bytes,
            ParsedPart parsed => parsed.Bytes,
            EditedPart edited => Serialize(edited.Xml, edited.TrailingNewline),
        };

        public XDocument GetXml(string path) => State switch
        {
            RawPart raw => Track(Parse(path, raw.Bytes), raw.Bytes),
            ParsedPart parsed => parsed.Xml,
            EditedPart edited => edited.Xml,
        };

        private XDocument Track(XDocument xml, ReadOnlyMemory<byte> bytes)
        {
            xml.Changed += OnChanged;
            State = new ParsedPart(bytes, xml);
            return xml;
        }

        private void OnChanged(object? sender, XObjectChangeEventArgs e)
        {
            if (State is not ParsedPart parsed)
            {
                return;
            }
            parsed.Xml.Changed -= OnChanged;
            // Keep the original ending (InDesign writes none) so a changed part differs only where it was edited.
            State = new EditedPart(parsed.Xml, TrailingNewline: parsed.Bytes.Span is [.., (byte)'\n']);
            IsModified = true;
        }
    }
}
