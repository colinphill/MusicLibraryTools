using System.Buffers.Binary;
using System.Text;

namespace iTunes.Binary;

/// <summary>
/// Describes one fixed-size list item whose word at +8 is data rather than a byte length.
/// The only confirmed instance is an <c>mprh</c> item inside an <c>mlrh</c> list.
/// </summary>
public readonly record struct ItlFixedItem(string Signature, int Offset, int Length)
{
    public int EndOffset => Offset + Length;
}

/// <summary>Format-aware traversal for the two list conventions observed in iTunes 12.13.</summary>
public static class ItlTraversal
{
    public const int MprhLength = 24;

    public static bool IsFixedSizeList(ItlChunk list) => list.Signature == "mlrh";

    /// <summary>
    /// Walks ordinary length-delimited records. Fixed-size lists return false instead of being
    /// misinterpreted as malformed chunks.
    /// </summary>
    public static bool TryWalkChunkItems(
        byte[] body,
        ItlChunk list,
        int end,
        out IReadOnlyList<ItlChunk> items,
        out string? unsupportedReason)
    {
        if (IsFixedSizeList(list))
        {
            items = [];
            unsupportedReason = $"'{list.Signature}' contains fixed-size records";
            return false;
        }

        items = [.. ItlChunk.Walk(body, list.HeaderEnd, end)];
        unsupportedReason = null;
        return true;
    }

    /// <summary>
    /// Walks the confirmed fixed 24-byte <c>mprh</c> records in an <c>mlrh</c> list. Native code
    /// maintains this as the de-duplicated, newest-ten Windows "Resume Playing" Jump List history,
    /// but traversal accepts the declared count so older or newer variants remain inspectable.
    /// </summary>
    public static IReadOnlyList<ItlFixedItem> WalkFixedItems(byte[] body, ItlChunk list, int end)
    {
        if (!IsFixedSizeList(list))
            throw new ArgumentException($"'{list.Signature}' is not a fixed-size list.", nameof(list));

        int bytes = checked(list.ItemCount * MprhLength);
        if (list.ItemCount < 0 || list.HeaderEnd + bytes != end)
            throw new InvalidDataException(
                $"'{list.Signature}' declares {list.ItemCount} fixed records but spans {end - list.HeaderEnd} data bytes.");

        var items = new List<ItlFixedItem>(list.ItemCount);
        for (int i = 0, offset = list.HeaderEnd; i < list.ItemCount; i++, offset += MprhLength)
        {
            string signature = Encoding.ASCII.GetString(body, offset, 4);
            int headerLength = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset + 4));
            if (signature != "mprh" || headerLength != MprhLength)
                throw new InvalidDataException(
                    $"Malformed fixed record {i} in '{list.Signature}' at {offset}: signature='{signature}', header={headerLength}.");
            items.Add(new ItlFixedItem(signature, offset, MprhLength));
        }

        return items;
    }
}
