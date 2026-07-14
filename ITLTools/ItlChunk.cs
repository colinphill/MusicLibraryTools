using System.Buffers.Binary;
using System.Text;

namespace iTunes.Binary;

/// <summary>
/// Every structure inside the inflated body starts with a four character signature and the
/// length of its own header. The word at +8 means one of two things depending on the chunk:
/// on item chunks (msdh, mith, miah, mhoh, ...) it is the total length including children,
/// but on the list headers that introduce them (mhgh, mlth, mlah, mlph, ...) it is the number
/// of items that follow. Integers here are little-endian, the reverse of the outer envelope.
/// </summary>
public readonly record struct ItlChunk(string Signature, int Offset, int HeaderLength, int SizeOrCount, int Type)
{
    /// <summary>Only meaningful on item chunks. On a list header this is <see cref="ItemCount"/>.</summary>
    public int TotalLength => SizeOrCount;

    /// <summary>Only meaningful on list headers. On an item chunk this is <see cref="TotalLength"/>.</summary>
    public int ItemCount => SizeOrCount;

    public int BodyOffset => Offset + HeaderLength;
    public int BodyLength => TotalLength - HeaderLength;
    public int EndOffset => Offset + TotalLength;

    /// <summary>The first byte after this chunk's header, where a list header's items begin.</summary>
    public int HeaderEnd => Offset + HeaderLength;

    public static ItlChunk Read(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset > data.Length - 12)
            throw new InvalidDataException($"Truncated chunk header at {offset}.");

        string signature = Encoding.ASCII.GetString(data.Slice(offset, 4));
        int headerLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4));
        int sizeOrCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 8));

        if (headerLength < 12 || headerLength > data.Length - offset)
            throw new InvalidDataException($"Malformed '{signature}' chunk at {offset}: header={headerLength}.");

        int type = headerLength >= 16 ? BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 12, 4)) : 0;
        return new ItlChunk(signature, offset, headerLength, sizeOrCount, type);
    }

    /// <summary>Walks sibling item chunks from <paramref name="offset"/> until <paramref name="end"/>.</summary>
    public static IEnumerable<ItlChunk> Walk(byte[] data, int offset, int end)
    {
        while (offset < end)
        {
            ItlChunk chunk = Read(data, offset);
            if (chunk.TotalLength < chunk.HeaderLength || chunk.EndOffset < chunk.Offset || chunk.EndOffset > end)
                throw new InvalidDataException($"Malformed '{chunk.Signature}' chunk at {chunk.Offset}: total={chunk.TotalLength}, header={chunk.HeaderLength}.");
            yield return chunk;
            offset = chunk.EndOffset;
        }

        if (offset != end)
            throw new InvalidDataException($"Chunk chain ended at {offset}, expected {end}.");
    }
}

/// <summary>
/// An "mhoh" data object. These hang off tracks, albums, artists and playlists and carry
/// the variable-length values: strings, plists and blobs.
/// </summary>
public sealed class ItlDataObject
{
    public required int Type { get; init; }
    public required byte[] Raw { get; init; }

    /// <summary>True when the payload parses as a length-prefixed string rather than an opaque blob.</summary>
    public bool IsString => Text is not null;

    public string? Text { get; private init; }

    /// <summary>The encoding word from the string preamble; meaningless on blobs.</summary>
    public int Encoding { get; private init; }

    /// <summary>The undecoded string bytes, for diagnostics.</summary>
    public byte[] Payload { get; private init; } = [];

    public static ItlDataObject Parse(byte[] data, ItlChunk chunk)
    {
        int body = chunk.BodyOffset;
        int bodyLength = chunk.BodyLength;
        byte[] raw = data.AsSpan(body, bodyLength).ToArray();

        // String payloads carry a 16 byte preamble: [encoding][byteLength][reserved][reserved].
        // Blob payloads (raw plists) have no preamble, and give themselves away by a byteLength
        // that could not possibly fit in the chunk.
        string? text = null;
        int encoding = 0;
        byte[] payloadBytes = [];

        if (bodyLength >= 16)
        {
            encoding = BinaryPrimitives.ReadInt32LittleEndian(raw);
            int byteLength = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));

            if (encoding is 1 or 2 or 3 && byteLength >= 0 && byteLength <= bodyLength - 16)
            {
                payloadBytes = raw.AsSpan(16, byteLength).ToArray();

                // iTunes narrows each string to the cheapest encoding that can hold it: Latin-1
                // when every code point fits in a byte, otherwise UTF-8, and UTF-16LE for the rest.
                text = encoding switch
                {
                    1 when byteLength % 2 == 0 => System.Text.Encoding.Unicode.GetString(payloadBytes),
                    2 => System.Text.Encoding.UTF8.GetString(payloadBytes),
                    3 => System.Text.Encoding.Latin1.GetString(payloadBytes),
                    _ => null,
                };
            }
        }

        return new ItlDataObject
        {
            Type = chunk.Type,
            Raw = raw,
            Text = text,
            Encoding = encoding,
            Payload = payloadBytes,
        };
    }
}
