using System.Buffers.Binary;
using System.Text;

namespace iTunes.Binary;

/// <summary>Anything that can be written back into the library body.</summary>
public abstract class ItlNode
{
    public abstract int Length { get; }
    public abstract void WriteTo(Span<byte> destination);

    protected static void PatchLength(Span<byte> header, int length) =>
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], length);
}

/// <summary>
/// An "mhoh" data object: a 24-byte header plus a payload that is either a length-prefixed
/// string or an opaque blob. Held as bytes so blobs survive a round trip untouched.
/// </summary>
public sealed class ItlField : ItlNode
{
    public const int HeaderLength = 24;
    private const int PreambleLength = 16;

    private ItlField(byte[] header, byte[] payload)
    {
        Header = header;
        Payload = payload;
    }

    public byte[] Header { get; }
    public byte[] Payload { get; private set; }

    public int Type => BinaryPrimitives.ReadInt32LittleEndian(Header.AsSpan(12));
    public override int Length => HeaderLength + Payload.Length;

    public static ItlField Read(byte[] body, ItlChunk chunk) => new(
        body.AsSpan(chunk.Offset, HeaderLength).ToArray(),
        body.AsSpan(chunk.BodyOffset, chunk.BodyLength).ToArray());

    public ItlField Clone() => new((byte[])Header.Clone(), (byte[])Payload.Clone());

    /// <summary>Creates a string field of the given type, encoded the way iTunes would.</summary>
    public static ItlField CreateString(int type, string value)
    {
        byte[] header = new byte[HeaderLength];
        Encoding.ASCII.GetBytes("mhoh").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), HeaderLength);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), type);

        var field = new ItlField(header, new byte[PreambleLength]);
        field.SetText(value, preferred: value.Any(c => c > 0xFF) ? 2 : 3);
        return field;
    }

    private bool HasStringPreamble(out int encoding, out int byteLength)
    {
        encoding = 0;
        byteLength = 0;
        if (Payload.Length < PreambleLength)
            return false;

        encoding = BinaryPrimitives.ReadInt32LittleEndian(Payload);
        byteLength = BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(4));
        return byteLength > 0 && byteLength <= Payload.Length - PreambleLength && encoding is 1 or 2 or 3;
    }

    public string? Text
    {
        get
        {
            if (!HasStringPreamble(out int encoding, out int byteLength))
                return null;

            ReadOnlySpan<byte> data = Payload.AsSpan(PreambleLength, byteLength);
            return encoding switch
            {
                1 when byteLength % 2 == 0 => Encoding.Unicode.GetString(data),
                2 => Encoding.UTF8.GetString(data),
                3 => Encoding.Latin1.GetString(data),
                _ => null,
            };
        }
    }

    /// <summary>
    /// Replaces the text, keeping the encoding iTunes chose unless the new value will not fit:
    /// Latin-1 only holds code points below 256, and UTF-16LE holds anything.
    /// </summary>
    public void SetText(string value, int? preferred = null)
    {
        int encoding = preferred ?? (HasStringPreamble(out int existing, out _) ? existing : 3);
        if (encoding == 3 && value.Any(c => c > 0xFF))
            encoding = 1;

        byte[] data = encoding switch
        {
            1 => Encoding.Unicode.GetBytes(value),
            2 => Encoding.UTF8.GetBytes(value),
            _ => Encoding.Latin1.GetBytes(value),
        };

        byte[] payload = new byte[PreambleLength + data.Length];
        // Preserve the two reserved words of the old preamble; only encoding and length change.
        if (Payload.Length >= PreambleLength)
            Payload.AsSpan(8, 8).CopyTo(payload.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(payload, encoding);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), data.Length);
        data.CopyTo(payload, PreambleLength);

        Payload = payload;
    }

    public override void WriteTo(Span<byte> destination)
    {
        Header.CopyTo(destination);
        PatchLength(destination, Length);
        Payload.CopyTo(destination[HeaderLength..]);
    }
}

/// <summary>An "mtph" playlist membership entry: a fixed 84-byte header naming one track.</summary>
public sealed class ItlEntry : ItlNode
{
    private ItlEntry(byte[] header) => Header = header;

    public byte[] Header { get; }
    public override int Length => Header.Length;

    public static ItlEntry Read(byte[] body, ItlChunk chunk) =>
        new(body.AsSpan(chunk.Offset, chunk.TotalLength).ToArray());

    public ItlEntry Clone() => new((byte[])Header.Clone());

    /// <summary>The track this entry points at.</summary>
    public int TrackId
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(Header.AsSpan(24));
        set => BinaryPrimitives.WriteInt32LittleEndian(Header.AsSpan(24), value);
    }

    /// <summary>A library-wide counter, unique per entry and increasing within each playlist.</summary>
    public uint EntryId
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(16));
        set => BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(16), value);
    }

    /// <summary>
    /// An ordering key, unique per entry. Its exact meaning is unknown: it is usually but not
    /// always increasing within a playlist, and never matches between playlists for one track.
    /// </summary>
    public uint OrderKey
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(32));
        set => BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(32), value);
    }

    public ulong PersistentId
    {
        get => BinaryPrimitives.ReadUInt64LittleEndian(Header.AsSpan(68));
        set => BinaryPrimitives.WriteUInt64LittleEndian(Header.AsSpan(68), value);
    }

    public override void WriteTo(Span<byte> destination)
    {
        Header.CopyTo(destination);
        PatchLength(destination, Length);
    }
}

/// <summary>A child chunk we do not model; preserved byte for byte.</summary>
public sealed class ItlOpaque : ItlNode
{
    private ItlOpaque(byte[] bytes) => Bytes = bytes;

    public byte[] Bytes { get; }
    public override int Length => Bytes.Length;

    public static ItlOpaque Read(byte[] body, ItlChunk chunk) =>
        new(body.AsSpan(chunk.Offset, chunk.TotalLength).ToArray());

    public ItlOpaque Clone() => new((byte[])Bytes.Clone());

    public override void WriteTo(Span<byte> destination) => Bytes.CopyTo(destination);
}

/// <summary>
/// A record: "mith" (track), "miah" (album), "miih" (artist) or "miph" (playlist). Its header
/// caches the number of "mhoh" children at +12, and for playlists the number of "mtph" at +16.
/// </summary>
public sealed class ItlRecord : ItlNode
{
    private const int FieldCountOffset = 12;
    private const int EntryCountOffset = 16;

    private ItlRecord(string signature, byte[] header, List<ItlNode> children)
    {
        Signature = signature;
        Header = header;
        Children = children;
    }

    public string Signature { get; }
    public byte[] Header { get; }
    public List<ItlNode> Children { get; }

    public IEnumerable<ItlField> Fields => Children.OfType<ItlField>();
    public IEnumerable<ItlEntry> Entries => Children.OfType<ItlEntry>();

    public override int Length => Header.Length + Children.Sum(c => c.Length);

    public static ItlRecord Read(byte[] body, ItlChunk chunk)
    {
        var children = new List<ItlNode>();
        foreach (ItlChunk child in ItlChunk.Walk(body, chunk.BodyOffset, chunk.EndOffset))
        {
            children.Add(child.Signature switch
            {
                "mhoh" => ItlField.Read(body, child),
                "mtph" => ItlEntry.Read(body, child),
                _ => ItlOpaque.Read(body, child),
            });
        }

        return new ItlRecord(chunk.Signature, body.AsSpan(chunk.Offset, chunk.HeaderLength).ToArray(), children);
    }

    public ItlRecord Clone() => new(Signature, (byte[])Header.Clone(),
    [
        .. Children.Select<ItlNode, ItlNode>(c => c switch
        {
            ItlField f => f.Clone(),
            ItlEntry e => e.Clone(),
            ItlOpaque o => o.Clone(),
            _ => throw new InvalidOperationException(),
        })
    ]);

    public ItlField? Field(int type) => Fields.FirstOrDefault(f => f.Type == type);

    /// <summary>Sets a string field, creating it if the record does not have one yet.</summary>
    public void SetField(int type, string value)
    {
        ItlField? field = Field(type);
        if (field is not null)
        {
            field.SetText(value);
            return;
        }

        // New fields go after the existing ones but before any mtph entries.
        int index = Children.FindLastIndex(c => c is ItlField) + 1;
        Children.Insert(index, ItlField.CreateString(type, value));
    }

    public void RemoveField(int type)
    {
        if (Field(type) is { } field)
            Children.Remove(field);
    }

    public override void WriteTo(Span<byte> destination)
    {
        Header.CopyTo(destination);
        PatchLength(destination, Length);

        // Keep the header's cached child counts honest.
        BinaryPrimitives.WriteInt32LittleEndian(destination[FieldCountOffset..], Children.Count(c => c is ItlField));
        if (Signature == "miph")
            BinaryPrimitives.WriteInt32LittleEndian(destination[EntryCountOffset..], Children.Count(c => c is ItlEntry));

        int position = Header.Length;
        foreach (ItlNode child in Children)
        {
            child.WriteTo(destination[position..]);
            position += child.Length;
        }
    }
}

/// <summary>A list header ("mlth", "mlah", "mlih", "mlph") whose word at +8 is an item count.</summary>
public sealed class ItlList : ItlNode
{
    private ItlList(byte[] header, List<ItlRecord> records)
    {
        Header = header;
        Records = records;
    }

    public byte[] Header { get; }
    public List<ItlRecord> Records { get; }

    public override int Length => Header.Length + Records.Sum(r => r.Length);

    public static ItlList Read(byte[] body, ItlChunk list, int end)
    {
        var records = new List<ItlRecord>();
        foreach (ItlChunk record in ItlChunk.Walk(body, list.HeaderEnd, end))
            records.Add(ItlRecord.Read(body, record));

        if (list.ItemCount != records.Count)
            throw new InvalidDataException($"'{list.Signature}' declares {list.ItemCount} items but {records.Count} were read.");

        return new ItlList(body.AsSpan(list.Offset, list.HeaderLength).ToArray(), records);
    }

    public override void WriteTo(Span<byte> destination)
    {
        Header.CopyTo(destination);

        // A list header stores its item count at +8, where an item chunk would store a length.
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], Records.Count);

        int position = Header.Length;
        foreach (ItlRecord record in Records)
        {
            record.WriteTo(destination[position..]);
            position += record.Length;
        }
    }
}

/// <summary>
/// A top-level "msdh" section. Only the four sections we understand are parsed into records;
/// everything else (the library-info blob, the XML plist, the cloud track list) is kept verbatim.
/// </summary>
public sealed class ItlSectionNode : ItlNode
{
    private ItlSectionNode(byte[] header, ItlList? list, byte[]? raw)
    {
        Header = header;
        List = list;
        Raw = raw;
    }

    public byte[] Header { get; }
    public ItlList? List { get; }
    public byte[]? Raw { get; }

    public int Type => BinaryPrimitives.ReadInt32LittleEndian(Header.AsSpan(12));
    public override int Length => Header.Length + (List?.Length ?? Raw!.Length);

    public static ItlSectionNode Read(byte[] body, ItlChunk section, bool structured)
    {
        byte[] header = body.AsSpan(section.Offset, section.HeaderLength).ToArray();

        if (!structured)
            return new ItlSectionNode(header, null, body.AsSpan(section.BodyOffset, section.BodyLength).ToArray());

        ItlChunk list = ItlChunk.Read(body, section.BodyOffset);
        return new ItlSectionNode(header, ItlList.Read(body, list, section.EndOffset), null);
    }

    public override void WriteTo(Span<byte> destination)
    {
        Header.CopyTo(destination);
        PatchLength(destination, Length);

        if (List is not null)
            List.WriteTo(destination[Header.Length..]);
        else
            Raw!.CopyTo(destination[Header.Length..]);
    }
}
