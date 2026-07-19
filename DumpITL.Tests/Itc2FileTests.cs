using System.Buffers.Binary;
using System.Text;
using iTunes.Binary;
using Xunit;

namespace DumpITL.Tests;

public sealed class Itc2FileTests
{
    [Fact]
    public void ParsesMultipleBigEndianItemsAndExtractsRawBgraLosslessly()
    {
        byte[] pixels = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];
        string path = TemporaryPath(".itc2");
        string bitmap = TemporaryPath(".bmp");
        try
        {
            File.WriteAllBytes(path, BuildFile(
                BuildItem(0, 0x1122334455667788, 0xAABBCCDDEEFF0100, "locl", "bGRA", 2, 1, pixels),
                BuildItem(2, 0x1122334455667788, 0x0123456789ABCDEF, "CLPU", "\0\0\0\r", 600, 600, jpeg)));

            Itc2File file = Itc2File.Load(path);

            Assert.Equal(Itc2File.ObservedContainerHeaderLength, file.HeaderLength);
            Assert.Equal(2, file.Items.Count);
            Itc2Item raw = file.Items[0];
            Assert.Equal(Itc2ImageEncoding.Bgra, raw.Encoding);
            Assert.Equal(0x1122334455667788UL, raw.LibraryPersistentId);
            Assert.Equal(0xAABBCCDDEEFF0100UL, raw.ArtworkPersistentId);
            Assert.Equal(2, raw.Width);
            Assert.Equal(1, raw.Height);
            Assert.Equal((2, 1), (raw.StoredWidth, raw.StoredHeight));

            Itc2Item compressed = file.Items[1];
            Assert.Equal(Itc2ImageEncoding.Jpeg, compressed.Encoding);
            Assert.Equal(13u, compressed.PixelFormatCode);
            Assert.Equal(600, compressed.Width);
            Assert.Equal(".jpg", compressed.SuggestedExtension);

            raw.Extract(bitmap);
            byte[] bmp = File.ReadAllBytes(bitmap);
            Assert.Equal((byte)'B', bmp[0]);
            Assert.Equal((byte)'M', bmp[1]);
            Assert.Equal(54 + pixels.Length, bmp.Length);
            Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22)));
            Assert.True(bmp.AsSpan(54).SequenceEqual(pixels));
        }
        finally
        {
            File.Delete(path);
            File.Delete(bitmap);
        }
    }

    [Fact]
    public void RejectsARecordThatRunsPastTheEndOfTheFile()
    {
        byte[] file = BuildFile(BuildItem(0, 1, 2, "locl", "bGRA", 1, 1, [0, 0, 0, 0]));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(Itc2File.ObservedContainerHeaderLength), uint.MaxValue);
        string path = TemporaryPath(".itc2");
        try
        {
            File.WriteAllBytes(path, file);
            Assert.Throws<InvalidDataException>(() => Itc2File.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsRawPixelLengthThatDoesNotMatchDimensions()
    {
        string path = TemporaryPath(".itc2");
        try
        {
            File.WriteAllBytes(path, BuildFile(
                BuildItem(0, 1, 2, "locl", "bGRA", 2, 2, [0, 0, 0, 0])));
            Assert.Throws<InvalidDataException>(() => Itc2File.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0x000UL, "00", "00", "00")]
    [InlineData(0x100UL, "00", "00", "01")]
    [InlineData(0x201UL, "01", "00", "02")]
    [InlineData(0xFEDUL, "13", "14", "15")]
    public void ComputesLeastSignificantNibbleFirstShards(ulong id, string first, string second, string third)
    {
        Assert.Equal([first, second, third], Itc2CacheAnalyzer.ShardDirectories(id));
    }

    private static string TemporaryPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"dumpitl-itc2-{Guid.NewGuid():N}{extension}");

    private static byte[] BuildFile(params byte[][] items)
    {
        byte[] result = new byte[Itc2File.ObservedContainerHeaderLength + items.Sum(item => item.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(result, Itc2File.ObservedContainerHeaderLength);
        Encoding.ASCII.GetBytes("itch").CopyTo(result, 4);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), 2);
        Encoding.ASCII.GetBytes("artw").CopyTo(result, 24);
        int offset = Itc2File.ObservedContainerHeaderLength;
        foreach (byte[] item in items)
        {
            item.CopyTo(result, offset);
            offset += item.Length;
        }
        return result;
    }

    private static byte[] BuildItem(
        uint source,
        ulong libraryId,
        ulong artworkId,
        string origin,
        string format,
        int width,
        int height,
        byte[] payload)
    {
        byte[] result = new byte[Itc2File.ObservedItemHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.ASCII.GetBytes("item").CopyTo(result, 4);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), Itc2File.ObservedItemHeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(24), source);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(28), libraryId);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(36), artworkId);
        Encoding.ASCII.GetBytes(origin).CopyTo(result, 44);
        Encoding.ASCII.GetBytes(format).CopyTo(result, 48);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(56), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(60), (uint)height);
        if (format == "bGRA" || format == "ARGb")
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(76), (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(80), (uint)height);
        }
        Encoding.ASCII.GetBytes("data").CopyTo(result, Itc2File.ObservedItemHeaderLength - 4);
        payload.CopyTo(result, Itc2File.ObservedItemHeaderLength);
        return result;
    }
}
