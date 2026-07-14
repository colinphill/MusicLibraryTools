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
    public const int StshHeaderLength = 96;
    public const int MlphHeaderLength = 92;
    public const int MlshHeaderLength = 44;
    public const int MsphHeaderLength = 48;

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

    /// <summary>
    /// Walks the optional global-state data objects in a type-23 <c>stsh</c> container. Unlike
    /// ordinary list headers, <c>stsh</c> keeps its child count at +12; +8 is zero in every
    /// observed iTunes 12.13 file. Native code emits at most one object of each opaque type 900
    /// and 901. Their application-level meanings are not yet proven.
    /// </summary>
    public static IReadOnlyList<ItlChunk> WalkStshDataObjects(byte[] body, ItlChunk stsh, int end)
    {
        if (stsh.Signature != "stsh")
            throw new ArgumentException($"'{stsh.Signature}' is not an stsh container.", nameof(stsh));
        if (stsh.HeaderLength != StshHeaderLength)
            throw new InvalidDataException(
                $"Unsupported 'stsh' header length {stsh.HeaderLength}; expected {StshHeaderLength}.");
        if (stsh.SizeOrCount != 0)
            throw new InvalidDataException($"Unsupported 'stsh' +8 word {stsh.SizeOrCount}; expected zero.");

        int declaredCount = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(stsh.Offset + 12));
        if (declaredCount < 0)
            throw new InvalidDataException($"'stsh' declares a negative child count {declaredCount}.");

        ItlChunk[] children = [.. ItlChunk.Walk(body, stsh.HeaderEnd, end)];
        if (children.Length != declaredCount)
            throw new InvalidDataException(
                $"'stsh' declares {declaredCount} data objects but contains {children.Length} chunks.");

        foreach (ItlChunk child in children)
        {
            if (child.Signature != "mhoh" || child.Type is not (900 or 901))
                throw new InvalidDataException(
                    $"'stsh' contains unsupported child '{child.Signature}' type {child.Type}.");
        }

        int[] duplicates = [.. children.GroupBy(child => child.Type)
            .Where(group => group.Count() > 1).Select(group => group.Key)];
        if (duplicates.Length > 0)
            throw new InvalidDataException(
                $"'stsh' contains duplicate data-object type(s): {string.Join(", ", duplicates)}.");

        return children;
    }

    /// <summary>
    /// Walks an <c>mlph</c> list without assigning playlist semantics to its owning section.
    /// Native iTunes invokes the same serializer for the ordinary type-2 playlist partition and
    /// the special type-14 partition, selecting the latter for internal object kinds 0x20/0x23.
    /// </summary>
    public static IReadOnlyList<ItlChunk> WalkMlphRecords(byte[] body, ItlChunk mlph, int end)
    {
        if (mlph.Signature != "mlph")
            throw new ArgumentException($"'{mlph.Signature}' is not an mlph list.", nameof(mlph));
        if (mlph.HeaderLength != MlphHeaderLength)
            throw new InvalidDataException(
                $"Unsupported 'mlph' header length {mlph.HeaderLength}; expected {MlphHeaderLength}.");
        if (mlph.ItemCount < 0)
            throw new InvalidDataException($"'mlph' declares a negative item count {mlph.ItemCount}.");

        ItlChunk[] records = [.. ItlChunk.Walk(body, mlph.HeaderEnd, end)];
        if (records.Length != mlph.ItemCount)
            throw new InvalidDataException(
                $"'mlph' declares {mlph.ItemCount} playlist records but contains {records.Length} chunks.");
        foreach (ItlChunk record in records)
        {
            if (record.Signature != "miph")
                throw new InvalidDataException($"'mlph' contains unsupported child '{record.Signature}'.");
        }
        return records;
    }

    /// <summary>
    /// Walks the type-21 podcast-station collection. Each station is an <c>msph</c> record with
    /// one type-800 <c>mhoh</c> XML settings plist; both count words are maintained by native code.
    /// </summary>
    public static IReadOnlyList<ItlChunk> WalkPodcastStations(byte[] body, ItlChunk mlsh, int end)
    {
        if (mlsh.Signature != "mlsh")
            throw new ArgumentException($"'{mlsh.Signature}' is not an mlsh list.", nameof(mlsh));
        if (mlsh.HeaderLength != MlshHeaderLength)
            throw new InvalidDataException(
                $"Unsupported 'mlsh' header length {mlsh.HeaderLength}; expected {MlshHeaderLength}.");
        if (mlsh.ItemCount < 0)
            throw new InvalidDataException($"'mlsh' declares a negative station count {mlsh.ItemCount}.");

        ItlChunk[] stations = [.. ItlChunk.Walk(body, mlsh.HeaderEnd, end)];
        if (stations.Length != mlsh.ItemCount)
            throw new InvalidDataException(
                $"'mlsh' declares {mlsh.ItemCount} stations but contains {stations.Length} chunks.");

        foreach (ItlChunk station in stations)
        {
            if (station.Signature != "msph" || station.HeaderLength != MsphHeaderLength)
                throw new InvalidDataException(
                    $"'mlsh' contains unsupported station '{station.Signature}' with header {station.HeaderLength}.");
            int declaredFields = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(station.Offset + 12));
            ItlChunk[] fields = [.. ItlChunk.Walk(body, station.BodyOffset, station.EndOffset)];
            if (declaredFields != fields.Length)
                throw new InvalidDataException(
                    $"'msph' declares {declaredFields} fields but contains {fields.Length} chunks.");
            if (fields.Length != 1 || fields[0].Signature != "mhoh" ||
                fields[0].Type != (int)ItlDataType.PodcastSettingsPlist)
                throw new InvalidDataException(
                    "'msph' must contain exactly one type-800 mhoh podcast settings plist.");
        }

        return stations;
    }
}
