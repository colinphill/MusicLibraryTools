using System.Buffers.Binary;
using System.Text;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class ChunkedAudioFormatTests
{
    private static readonly byte[] Cover = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwC" +
        "AAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Theory]
    [InlineData(".wav", typeof(WaveFile), false, false)]
    [InlineData(".rf64", typeof(WaveFile), true, false)]
    [InlineData(".aif", typeof(AiffFile), false, false)]
    [InlineData(".aifc", typeof(AiffFile), false, true)]
    public void RoundTripPreservesAudioAndUnknownChunks(
        string extension,
        Type expectedType,
        bool isRf64,
        bool isAifc)
    {
        string path = TempPath(extension);
        WriteFixture(path, extension, includeTag: true);
        byte[] audioBefore = ReadChunk(path, IsWave(extension) ? "data" : "SSND");
        byte[] unknownBefore = ReadChunk(path, IsWave(extension) ? "JUNK" : "ANNO");
        try
        {
            IMediaFile media = MediaFile.GetFile(
                path, readOnly: false, readArtwork: true);
            Assert.IsType(expectedType, media);
            var codec = Assert.IsAssignableFrom<ICodecProvider>(media);
            Assert.Equal(44100u, codec.Samplerate);
            Assert.Equal(2u, codec.Channels);
            Assert.Equal(16u, codec.BitsPerSample);
            Assert.Equal(CodecType.Lossless, codec.CodecType);
            Assert.Equal(isRf64, (media as WaveFile)?.IsRf64 ?? false);
            Assert.Equal(isAifc, (media as AiffFile)?.IsAifc ?? false);

            var writer = Assert.IsAssignableFrom<IMetadataWriter>(media);
            writer.SetField(TagFields.Title, "Replacement title");
            Assert.IsAssignableFrom<IArtworkWriter>(media)
                .SetFrontCover(Cover, "image/png");
            writer.Save();

            IMediaFile reloaded = MediaFile.GetFile(
                path, readOnly: true, readArtwork: true);
            Assert.Equal("Replacement title", reloaded.Tags.First().Title);
            Assert.Equal(
                Cover,
                Assert.Single(
                    reloaded.Tags.First().GetImageMetadata()).Data);
            Assert.Equal(audioBefore, ReadChunk(
                path, IsWave(extension) ? "data" : "SSND"));
            Assert.Equal(unknownBefore, ReadChunk(
                path, IsWave(extension) ? "JUNK" : "ANNO"));
            AssertContainerSizeIsCurrent(path);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData(".wav")]
    [InlineData(".aiff")]
    public void SaveToSeparatePathLeavesOriginalUntouched(string extension)
    {
        string source = TempPath(extension);
        string output = TempPath(extension);
        WriteFixture(source, extension, includeTag: true);
        byte[] original = File.ReadAllBytes(source);
        try
        {
            IMediaFile media = MediaFile.GetFile(source, readOnly: false);
            Assert.IsAssignableFrom<IMetadataWriter>(media)
                .SetField(TagFields.Title, "Staged title");
            media.SaveTags(output);

            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Equal(
                "Staged title",
                MediaFile.GetFile(output).Tags.First().Title);
            Assert.Equal(
                ReadChunk(source, IsWave(extension) ? "data" : "SSND"),
                ReadChunk(output, IsWave(extension) ? "data" : "SSND"));
        }
        finally
        {
            try { File.Delete(source); } catch { }
            try { File.Delete(output); } catch { }
        }
    }

    [Theory]
    [InlineData(".wav")]
    [InlineData(".aiff")]
    public void UntaggedContainerCanCreateItsFirstId3Chunk(string extension)
    {
        string path = TempPath(extension);
        WriteFixture(path, extension, includeTag: false);
        try
        {
            IMediaFile media = MediaFile.GetFile(path, readOnly: false);
            Assert.Empty(media.Tags.First().GetKnownMetadata());
            Assert.IsAssignableFrom<IMetadataWriter>(media)
                .SetField(TagFields.Title, "First tag");
            media.SaveTags();

            Assert.Equal(
                "First tag",
                MediaFile.GetFile(path).Tags.First().Title);
            Assert.Equal(
                IsWave(extension) ? "id3 " : "ID3 ",
                FindId3ChunkId(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData(".wav")]
    [InlineData(".rf64")]
    [InlineData(".aif")]
    [InlineData(".aiff")]
    [InlineData(".aifc")]
    public void ReleasedChunkFormatsAdvertiseNativeCapabilities(
        string extension)
    {
        Assert.True(MediaFormatRegistry.Default.SupportsExtension(
            extension,
            MediaFormatCapabilities.LibraryIndex |
            MediaFormatCapabilities.ReadMetadata |
            MediaFormatCapabilities.WriteMetadata |
            MediaFormatCapabilities.ReadArtwork |
            MediaFormatCapabilities.WriteArtwork |
            MediaFormatCapabilities.Remux |
            MediaFormatCapabilities.TranscodeSource |
            MediaFormatCapabilities.TranscodeDestination));
    }

    [Theory]
    [InlineData("sample.wav", "data")]
    [InlineData("sample.aiff", "SSND")]
    public void RealEncoderFixtureRemainsReadableAfterFirstId3Write(
        string fixture,
        string audioChunk)
    {
        using var media = MediaFixtures.Copy(fixture);
        byte[] audioBefore = ReadChunk(media.Path, audioChunk);

        IMediaFile file = MediaFile.GetFile(
            media.Path, readOnly: false, readArtwork: true);
        Assert.IsAssignableFrom<IMetadataWriter>(file)
            .SetField(TagFields.Title, "Real fixture title");
        file.SaveTags();

        Assert.Equal(
            "Real fixture title",
            MediaFile.GetFile(media.Path).Tags.First().Title);
        Assert.Equal(audioBefore, ReadChunk(media.Path, audioChunk));
        AssertContainerSizeIsCurrent(media.Path);
    }

    private static string TempPath(string extension) => Path.Combine(
        Path.GetTempPath(),
        $"chunked_{Guid.NewGuid():N}{extension}");

    private static bool IsWave(string extension) =>
        extension is ".wav" or ".rf64";

    private static void WriteFixture(
        string path,
        string extension,
        bool includeTag)
    {
        bool wave = IsWave(extension);
        bool rf64 = extension == ".rf64";
        bool aifc = extension == ".aifc";
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(
            wave ? (rf64 ? "RF64" : "RIFF") : "FORM"));
        WriteUInt32(stream, rf64 ? uint.MaxValue : 0, wave);
        stream.Write(Encoding.ASCII.GetBytes(
            wave ? "WAVE" : (aifc ? "AIFC" : "AIFF")));

        long ds64DataOffset = -1;
        if (rf64)
        {
            ds64DataOffset = stream.Position + 8;
            byte[] ds64 = new byte[28];
            BinaryPrimitives.WriteUInt64LittleEndian(ds64.AsSpan(8), 8);
            BinaryPrimitives.WriteUInt64LittleEndian(ds64.AsSpan(16), 2);
            WriteChunk(stream, "ds64", ds64, littleEndian: true);
        }

        if (wave)
        {
            WriteChunk(stream, "fmt ", WaveFormat(), littleEndian: true);
            WriteChunk(stream, "JUNK", [0x10, 0x20, 0x30], littleEndian: true);
            WriteChunk(
                stream,
                "data",
                [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08],
                littleEndian: true,
                declaredSize: rf64 ? uint.MaxValue : null);
            if (includeTag)
                WriteChunk(stream, "id3 ", BuildId3("Old title"), littleEndian: true);
        }
        else
        {
            WriteChunk(stream, "COMM", AiffCommon(aifc), littleEndian: false);
            WriteChunk(stream, "ANNO", [0x41, 0x42, 0x43], littleEndian: false);
            WriteChunk(
                stream,
                "SSND",
                [0, 0, 0, 0, 0, 0, 0, 0,
                 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08],
                littleEndian: false);
            if (includeTag)
                WriteChunk(stream, "ID3 ", BuildId3("Old title"), littleEndian: false);
        }

        byte[] file = stream.ToArray();
        if (rf64)
            BinaryPrimitives.WriteUInt64LittleEndian(
                file.AsSpan(checked((int)ds64DataOffset)),
                checked((ulong)file.Length - 8));
        else if (wave)
            BinaryPrimitives.WriteUInt32LittleEndian(
                file.AsSpan(4), checked((uint)file.Length - 8));
        else
            BinaryPrimitives.WriteUInt32BigEndian(
                file.AsSpan(4), checked((uint)file.Length - 8));
        File.WriteAllBytes(path, file);
    }

    private static byte[] WaveFormat()
    {
        byte[] data = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 176400);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 16);
        return data;
    }

    private static byte[] AiffCommon(bool aifc)
    {
        byte[] data = new byte[aifc ? 23 : 18];
        BinaryPrimitives.WriteUInt16BigEndian(data, 2);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6), 16);
        Convert.FromHexString("400EAC44000000000000").CopyTo(data, 8);
        if (aifc)
        {
            Encoding.ASCII.GetBytes("sowt").CopyTo(data, 18);
            data[22] = 0;
        }
        return data;
    }

    private static byte[] BuildId3(string title)
    {
        byte[] value = Encoding.Latin1.GetBytes(title);
        byte[] frame = new byte[11 + value.Length];
        Encoding.ASCII.GetBytes("TIT2").CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(4), checked((uint)value.Length + 1));
        frame[10] = 0;
        value.CopyTo(frame, 11);

        byte[] tag = new byte[10 + frame.Length];
        Encoding.ASCII.GetBytes("ID3").CopyTo(tag, 0);
        tag[3] = 3;
        int size = frame.Length;
        tag[6] = (byte)((size >> 21) & 0x7F);
        tag[7] = (byte)((size >> 14) & 0x7F);
        tag[8] = (byte)((size >> 7) & 0x7F);
        tag[9] = (byte)(size & 0x7F);
        frame.CopyTo(tag, 10);
        return tag;
    }

    private static void WriteChunk(
        Stream stream,
        string id,
        byte[] data,
        bool littleEndian,
        uint? declaredSize = null)
    {
        stream.Write(Encoding.ASCII.GetBytes(id));
        WriteUInt32(
            stream,
            declaredSize ?? checked((uint)data.Length),
            littleEndian);
        stream.Write(data);
        if ((data.Length & 1) != 0)
            stream.WriteByte(0x7E);
    }

    private static void WriteUInt32(
        Stream stream,
        uint value,
        bool littleEndian)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (littleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static byte[] ReadChunk(string path, string requestedId)
    {
        byte[] file = File.ReadAllBytes(path);
        bool littleEndian = file.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                            file.AsSpan(0, 4).SequenceEqual("RF64"u8);
        ulong? rf64DataSize = null;
        int offset = 12;
        while (offset + 8 <= file.Length)
        {
            string id = Encoding.ASCII.GetString(file, offset, 4);
            uint size32 = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 4))
                : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset + 4));
            int dataOffset = offset + 8;
            if (id == "ds64")
                rf64DataSize = BinaryPrimitives.ReadUInt64LittleEndian(
                    file.AsSpan(dataOffset + 8));
            ulong size = size32 == uint.MaxValue && id == "data"
                ? rf64DataSize ?? throw new InvalidDataException()
                : size32;
            if (size > int.MaxValue ||
                dataOffset + (long)size > file.Length)
                throw new InvalidDataException();
            if (id == requestedId)
                return file.AsSpan(dataOffset, (int)size).ToArray();
            offset = checked(dataOffset + (int)size + ((int)size & 1));
        }
        throw new InvalidDataException($"Chunk '{requestedId}' was not found.");
    }

    private static string FindId3ChunkId(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        bool littleEndian = file.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                            file.AsSpan(0, 4).SequenceEqual("RF64"u8);
        int offset = 12;
        while (offset + 8 <= file.Length)
        {
            string id = Encoding.ASCII.GetString(file, offset, 4);
            uint size = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 4))
                : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset + 4));
            if (id.Equals("id3 ", StringComparison.OrdinalIgnoreCase))
                return id;
            offset = checked(offset + 8 + (int)size + ((int)size & 1));
        }
        throw new InvalidDataException("ID3 chunk was not found.");
    }

    private static void AssertContainerSizeIsCurrent(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        string id = Encoding.ASCII.GetString(file, 0, 4);
        if (id == "RF64")
        {
            Assert.Equal(
                uint.MaxValue,
                BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
            Assert.Equal(
                checked((ulong)file.Length - 8),
                BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(20)));
        }
        else if (id == "RIFF")
        {
            Assert.Equal(
                checked((uint)file.Length - 8),
                BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
        }
        else
        {
            Assert.Equal("FORM", id);
            Assert.Equal(
                checked((uint)file.Length - 8),
                BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4)));
        }
    }
}
