using System.Buffers.Binary;
using System.Text;

namespace iTunes.Binary;

/// <summary>The image encodings observed in Windows iTunes 12 artwork-cache ITC2 records.</summary>
public enum Itc2ImageEncoding
{
    Unknown,
    Bgra,
    Argb,
    Jpeg,
    Png,
}

/// <summary>
/// One length-delimited <c>item</c> in an iTunes artwork-cache (<c>.itc2</c>) file.
/// Numeric fields are big-endian, unlike decoded ITL chunks.
/// </summary>
public sealed class Itc2Item
{
    internal Itc2Item(
        string sourcePath,
        long recordOffset,
        int recordLength,
        int headerLength,
        uint word12,
        uint word16,
        uint word20,
        uint sourceKind,
        ulong libraryPersistentId,
        ulong artworkPersistentId,
        string originTag,
        uint pixelFormatCode,
        string pixelFormatTag,
        int width,
        int height,
        int storedWidth,
        int storedHeight,
        byte[] payloadPrefix)
    {
        SourcePath = sourcePath;
        RecordOffset = recordOffset;
        RecordLength = recordLength;
        HeaderLength = headerLength;
        Word12 = word12;
        Word16 = word16;
        Word20 = word20;
        SourceKind = sourceKind;
        LibraryPersistentId = libraryPersistentId;
        ArtworkPersistentId = artworkPersistentId;
        OriginTag = originTag;
        PixelFormatCode = pixelFormatCode;
        PixelFormatTag = pixelFormatTag;
        Width = width;
        Height = height;
        StoredWidth = storedWidth;
        StoredHeight = storedHeight;
        PayloadPrefix = payloadPrefix;
    }

    public string SourcePath { get; }
    public long RecordOffset { get; }
    public int RecordLength { get; }
    public int HeaderLength { get; }

    /// <summary>Unresolved header words retained for differential research.</summary>
    public uint Word12 { get; }
    public uint Word16 { get; }
    public uint Word20 { get; }

    /// <summary>Observed values: 0 for <c>locl</c>, 2 for <c>CLPU</c>.</summary>
    public uint SourceKind { get; }
    public ulong LibraryPersistentId { get; }
    public ulong ArtworkPersistentId { get; }
    public string OriginTag { get; }
    public uint PixelFormatCode { get; }
    public string PixelFormatTag { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// Secondary dimensions at record offsets +76/+80. They equal the dimensions for observed raw
    /// local BGRA items and are zero for observed compressed cloud JPEG items.
    /// </summary>
    public int StoredWidth { get; }
    public int StoredHeight { get; }

    public long PayloadOffset => RecordOffset + HeaderLength;
    public int PayloadLength => RecordLength - HeaderLength;
    public byte[] PayloadPrefix { get; }

    public Itc2ImageEncoding Encoding => PixelFormatTag switch
    {
        "bGRA" => Itc2ImageEncoding.Bgra,
        "ARGb" => Itc2ImageEncoding.Argb,
        _ when PayloadPrefix.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) => Itc2ImageEncoding.Jpeg,
        _ when PayloadPrefix.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) =>
            Itc2ImageEncoding.Png,
        _ => Itc2ImageEncoding.Unknown,
    };

    public string SuggestedExtension => Encoding switch
    {
        Itc2ImageEncoding.Bgra or Itc2ImageEncoding.Argb => ".bmp",
        Itc2ImageEncoding.Jpeg => ".jpg",
        Itc2ImageEncoding.Png => ".png",
        _ => ".bin",
    };

    /// <summary>Extracts the payload losslessly. Raw pixel records are wrapped in a 32-bit BMP.</summary>
    public void Extract(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using FileStream input = OpenSource();
        input.Position = PayloadOffset;
        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        switch (Encoding)
        {
            case Itc2ImageEncoding.Bgra:
                WriteBitmap(input, output, argb: false);
                break;
            case Itc2ImageEncoding.Argb:
                WriteBitmap(input, output, argb: true);
                break;
            default:
                CopyExactly(input, output, PayloadLength);
                break;
        }
    }

    private FileStream OpenSource() =>
        new(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private void WriteBitmap(Stream input, Stream output, bool argb)
    {
        long expected = checked((long)Width * Height * 4);
        if (Width <= 0 || Height <= 0 || PayloadLength != expected)
            throw new InvalidDataException(
                $"Raw {PixelFormatTag} payload has {PayloadLength:N0} bytes; {Width}x{Height} requires {expected:N0}.");

        const int fileHeaderLength = 14;
        const int dibHeaderLength = 40;
        int pixelOffset = fileHeaderLength + dibHeaderLength;
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(checked(pixelOffset + PayloadLength));
            writer.Write(0u);
            writer.Write(pixelOffset);
            writer.Write(dibHeaderLength);
            writer.Write(Width);
            writer.Write(-Height); // ITC2 rows are top-down; a negative BMP height preserves them.
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0u); // BI_RGB; byte order is B,G,R,A.
            writer.Write(PayloadLength);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0u);
            writer.Write(0u);
        }

        if (!argb)
        {
            CopyExactly(input, output, PayloadLength);
            return;
        }

        byte[] buffer = new byte[64 * 1024];
        int remaining = PayloadLength;
        while (remaining > 0)
        {
            int count = Math.Min(remaining, buffer.Length);
            ReadExactly(input, buffer.AsSpan(0, count));
            for (int offset = 0; offset < count; offset += 4)
            {
                byte alpha = buffer[offset];
                byte red = buffer[offset + 1];
                buffer[offset] = buffer[offset + 3];
                buffer[offset + 1] = buffer[offset + 2];
                buffer[offset + 2] = red;
                buffer[offset + 3] = alpha;
            }
            output.Write(buffer, 0, count);
            remaining -= count;
        }
    }

    private static void CopyExactly(Stream input, Stream output, int byteCount)
    {
        byte[] buffer = new byte[64 * 1024];
        int remaining = byteCount;
        while (remaining > 0)
        {
            int count = Math.Min(remaining, buffer.Length);
            ReadExactly(input, buffer.AsSpan(0, count));
            output.Write(buffer, 0, count);
            remaining -= count;
        }
    }

    private static void ReadExactly(Stream input, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            int read = input.Read(destination);
            if (read == 0)
                throw new EndOfStreamException("The ITC2 payload ended unexpectedly.");
            destination = destination[read..];
        }
    }
}

/// <summary>A validated, metadata-only view of an iTunes artwork-cache file.</summary>
public sealed class Itc2File
{
    public const int ObservedContainerHeaderLength = 284;
    public const int ObservedItemHeaderLength = 196;

    private Itc2File(string path, int headerLength, uint version8, uint word12, uint word16,
        IReadOnlyList<Itc2Item> items)
    {
        Path = path;
        HeaderLength = headerLength;
        Version8 = version8;
        Word12 = word12;
        Word16 = word16;
        Items = items;
    }

    public string Path { get; }
    public int HeaderLength { get; }
    public uint Version8 { get; }
    public uint Word12 { get; }
    public uint Word16 { get; }
    public IReadOnlyList<Itc2Item> Items { get; }

    public static Itc2File Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Read(stream, fullPath);
    }

    internal static Itc2File Read(Stream stream, string sourcePath)
    {
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("An ITC2 stream must be readable and seekable.", nameof(stream));
        if (stream.Length < 28)
            throw new InvalidDataException("The ITC2 file is shorter than its minimum container header.");

        byte[] prefix = ReadAt(stream, 0, 28);
        int headerLength = CheckedInt(U32(prefix, 0), "container header length");
        if (headerLength < prefix.Length || headerLength > stream.Length)
            throw new InvalidDataException($"Invalid ITC2 container header length {headerLength:N0}.");
        RequireTag(prefix, 4, "itch");
        RequireTag(prefix, 24, "artw");

        var items = new List<Itc2Item>();
        long offset = headerLength;
        while (offset < stream.Length)
        {
            if (stream.Length - offset < 12)
                throw new InvalidDataException($"Truncated ITC2 item header at 0x{offset:X}.");

            byte[] first = ReadAt(stream, offset, 12);
            int recordLength = CheckedInt(U32(first, 0), "item record length");
            int itemHeaderLength = CheckedInt(U32(first, 8), "item header length");
            if (recordLength < 64 || recordLength > stream.Length - offset)
                throw new InvalidDataException($"Invalid ITC2 item length {recordLength:N0} at 0x{offset:X}.");
            if (itemHeaderLength < 64 || itemHeaderLength > recordLength)
                throw new InvalidDataException($"Invalid ITC2 item header length {itemHeaderLength:N0} at 0x{offset:X}.");

            byte[] header = ReadAt(stream, offset, itemHeaderLength);
            RequireTag(header, 4, "item");
            RequireTag(header, itemHeaderLength - 4, "data");
            int payloadLength = recordLength - itemHeaderLength;
            byte[] payloadPrefix = ReadAt(stream, offset + itemHeaderLength, Math.Min(payloadLength, 16));

            var item = new Itc2Item(
                sourcePath,
                offset,
                recordLength,
                itemHeaderLength,
                U32(header, 12),
                U32(header, 16),
                U32(header, 20),
                U32(header, 24),
                U64(header, 28),
                U64(header, 36),
                FourCc(header, 44),
                U32(header, 48),
                FourCc(header, 48),
                CheckedInt(U32(header, 56), "image width"),
                CheckedInt(U32(header, 60), "image height"),
                itemHeaderLength >= 84 ? CheckedInt(U32(header, 76), "stored width") : 0,
                itemHeaderLength >= 84 ? CheckedInt(U32(header, 80), "stored height") : 0,
                payloadPrefix);
            if (item.Width <= 0 || item.Height <= 0)
                throw new InvalidDataException($"Invalid ITC2 image dimensions {item.Width}x{item.Height} at 0x{offset:X}.");
            if (item.Encoding is Itc2ImageEncoding.Bgra or Itc2ImageEncoding.Argb)
            {
                long expected = checked((long)item.Width * item.Height * 4);
                if (payloadLength != expected)
                    throw new InvalidDataException(
                        $"Raw {item.PixelFormatTag} payload at 0x{offset:X} has {payloadLength:N0} bytes; " +
                        $"{item.Width}x{item.Height} requires {expected:N0}.");
            }
            items.Add(item);

            offset += recordLength;
        }

        if (items.Count == 0)
            throw new InvalidDataException("The ITC2 container has no item records.");

        return new Itc2File(sourcePath, headerLength, U32(prefix, 8), U32(prefix, 12), U32(prefix, 16), items);
    }

    private static int CheckedInt(uint value, string field) => value <= int.MaxValue
        ? (int)value
        : throw new InvalidDataException($"ITC2 {field} {value:N0} exceeds the supported range.");

    private static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));

    private static ulong U64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset));

    private static string FourCc(byte[] bytes, int offset) => Encoding.ASCII.GetString(bytes, offset, 4);

    private static void RequireTag(byte[] bytes, int offset, string expected)
    {
        string actual = FourCc(bytes, offset);
        if (actual != expected)
            throw new InvalidDataException($"Expected ITC2 tag '{expected}' at +0x{offset:X}, found '{Printable(actual)}'.");
    }

    private static string Printable(string value) => string.Concat(value.Select(character =>
        character is >= ' ' and <= '~' ? character.ToString() : $"\\x{(int)character:X2}"));

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        byte[] result = new byte[count];
        stream.Position = offset;
        int position = 0;
        while (position < result.Length)
        {
            int read = stream.Read(result, position, result.Length - position);
            if (read == 0)
                throw new EndOfStreamException($"The ITC2 file ended at 0x{offset + position:X}.");
            position += read;
        }
        return result;
    }
}
