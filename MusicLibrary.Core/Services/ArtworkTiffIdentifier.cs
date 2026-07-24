using System.Buffers.Binary;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Identifies classic TIFF artwork that can be retained byte-for-byte without adding a secondary
/// image codec. Pixel-data ranges are validated so a truncated payload is never accepted as a
/// preservable image.
/// </summary>
internal static class ArtworkTiffIdentifier
{
    public static bool TryIdentify(
        ReadOnlySpan<byte> source,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (source.Length < 8)
            return false;

        bool littleEndian;
        if (source[0] == (byte)'I' && source[1] == (byte)'I')
            littleEndian = true;
        else if (source[0] == (byte)'M' && source[1] == (byte)'M')
            littleEndian = false;
        else
            return false;

        if (ReadUInt16(source[2..4], littleEndian) != 42)
            return false;
        uint firstIfd = ReadUInt32(source[4..8], littleEndian);
        if (firstIfd > source.Length - sizeof(ushort))
            throw new InvalidDataException("The TIFF artwork directory is truncated.");

        int directoryOffset = checked((int)firstIfd);
        ushort entryCount = ReadUInt16(
            source.Slice(directoryOffset, sizeof(ushort)),
            littleEndian);
        long directoryLength =
            sizeof(ushort) + (long)entryCount * 12;
        if (directoryOffset + directoryLength > source.Length)
            throw new InvalidDataException("The TIFF artwork directory is truncated.");

        uint[]? stripOffsets = null;
        uint[]? stripByteCounts = null;
        uint[]? tileOffsets = null;
        uint[]? tileByteCounts = null;
        uint[]? jpegOffsets = null;
        uint[]? jpegByteCounts = null;
        for (var index = 0; index < entryCount; index++)
        {
            ReadOnlySpan<byte> entry =
                source.Slice(directoryOffset + sizeof(ushort) + index * 12, 12);
            ushort tag = ReadUInt16(entry[..2], littleEndian);
            switch (tag)
            {
                case 256:
                case 257:
                {
                    uint[] values =
                        ReadUnsignedValues(source, entry, littleEndian);
                    if (values.Length != 1 ||
                        values[0] is 0 or > int.MaxValue)
                        throw new InvalidDataException(
                            "The TIFF artwork has invalid dimensions.");
                    if (tag == 256)
                        width = (int)values[0];
                    else
                        height = (int)values[0];
                    break;
                }
                case 273:
                    stripOffsets =
                        ReadUnsignedValues(source, entry, littleEndian);
                    break;
                case 279:
                    stripByteCounts =
                        ReadUnsignedValues(source, entry, littleEndian);
                    break;
                case 324:
                    tileOffsets =
                        ReadUnsignedValues(source, entry, littleEndian);
                    break;
                case 325:
                    tileByteCounts =
                        ReadUnsignedValues(source, entry, littleEndian);
                    break;
                case 513:
                    jpegOffsets =
                        ReadUnsignedValues(source, entry, littleEndian);
                    break;
                case 514:
                    jpegByteCounts =
                        ReadUnsignedValues(source, entry, littleEndian);
                    break;
            }
        }

        if (width <= 0 || height <= 0)
            throw new InvalidDataException(
                "The TIFF artwork does not declare valid dimensions.");
        if (!HasValidPayload(source.Length, stripOffsets, stripByteCounts) &&
            !HasValidPayload(source.Length, tileOffsets, tileByteCounts) &&
            !HasValidPayload(source.Length, jpegOffsets, jpegByteCounts))
            throw new InvalidDataException(
                "The TIFF artwork pixel payload is missing, truncated, or invalid.");
        return true;
    }

    private static uint[] ReadUnsignedValues(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> entry,
        bool littleEndian)
    {
        ushort type = ReadUInt16(entry[2..4], littleEndian);
        uint count = ReadUInt32(entry[4..8], littleEndian);
        int elementSize = type switch
        {
            3 => sizeof(ushort),
            4 => sizeof(uint),
            _ => throw new InvalidDataException(
                "The TIFF artwork uses an unsupported unsigned-value type."),
        };
        if (count is 0 or > 1_000_000)
            throw new InvalidDataException(
                "The TIFF artwork declares an invalid value count.");

        long byteCount = (long)count * elementSize;
        ReadOnlySpan<byte> values;
        if (byteCount <= sizeof(uint))
            values = entry.Slice(8, (int)byteCount);
        else
        {
            uint valueOffset = ReadUInt32(entry[8..12], littleEndian);
            if (valueOffset > source.Length ||
                byteCount > source.Length - valueOffset)
                throw new InvalidDataException(
                    "The TIFF artwork value data is truncated.");
            values = source.Slice(checked((int)valueOffset), (int)byteCount);
        }

        var result = new uint[count];
        for (var index = 0; index < result.Length; index++)
        {
            ReadOnlySpan<byte> value =
                values.Slice(index * elementSize, elementSize);
            result[index] = type == 3
                ? ReadUInt16(value, littleEndian)
                : ReadUInt32(value, littleEndian);
        }
        return result;
    }

    private static bool HasValidPayload(
        int sourceLength,
        uint[]? offsets,
        uint[]? byteCounts)
    {
        if (offsets is null ||
            byteCounts is null ||
            offsets.Length == 0 ||
            offsets.Length != byteCounts.Length)
            return false;
        for (var index = 0; index < offsets.Length; index++)
        {
            if (offsets[index] < 8 ||
                byteCounts[index] == 0 ||
                (ulong)offsets[index] + byteCounts[index] >
                (ulong)sourceLength)
                return false;
        }
        return true;
    }

    private static ushort ReadUInt16(
        ReadOnlySpan<byte> value,
        bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(value)
            : BinaryPrimitives.ReadUInt16BigEndian(value);

    private static uint ReadUInt32(
        ReadOnlySpan<byte> value,
        bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(value)
            : BinaryPrimitives.ReadUInt32BigEndian(value);
}
