using System.Text;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class ApeV2FormatFamilyTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwC" +
        "AAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Theory]
    [InlineData("sample.mpc", "Musepack", CodecType.Lossy, 44100, 2, 16)]
    [InlineData("sample.tta", "TTA", CodecType.Lossless, 44100, 2, 16)]
    [InlineData("sample.tak", "TAK", CodecType.Lossless, 44100, 2, 16)]
    [InlineData("sample.ofr", "OptimFROG", CodecType.Lossless, 44100, 2, 16)]
    [InlineData("sample.ofs", "OptimFROG DualStream", CodecType.Lossy, 44100, 2, 16)]
    [InlineData("sample.off", "OptimFROG Float", CodecType.Lossless, 44100, 2, 32)]
    public void CodecAndApeMetadataAreProjected(
        string fixture,
        string codecName,
        CodecType codecType,
        uint sampleRate,
        uint channels,
        uint bitsPerSample)
    {
        IMediaFile file = MediaFile.GetFile(
            MediaFixtures.Path_(fixture));
        ICodecProvider codec = Assert.Single(file.Codecs);
        IMetadataProvider tag = Assert.Single(file.Tags);

        Assert.Equal(codecName, codec.CodecName);
        Assert.Equal(codecType, codec.CodecType);
        Assert.Equal(sampleRate, codec.Samplerate);
        Assert.Equal(channels, codec.Channels);
        Assert.Equal(bitsPerSample, codec.BitsPerSample);
        Assert.True(codec.DurationInFrames > 0);
        Assert.True(codec.AverageBitrate > 0);
        Assert.Equal("APE", tag.TagType);
        Assert.Equal("TestTitle", tag.Title);
        Assert.IsAssignableFrom<IMetadataWriter>(tag);
    }

    [Theory]
    [InlineData("sample.ofr", "OFR ")]
    [InlineData("sample.ofs", "OFR ")]
    [InlineData("sample.off", "OFRX")]
    public void OfficialOptimFrogFixturesExposeNativeChunkChains(
        string fixture,
        string signature)
    {
        byte[] bytes = File.ReadAllBytes(
            MediaFixtures.Path_(fixture));
        Assert.Equal(signature, Encoding.ASCII.GetString(bytes, 0, 4));
        int headerSize = checked((int)BitConverter.ToUInt32(bytes, 4));
        Assert.Equal(
            "HEAD",
            Encoding.ASCII.GetString(bytes, 8 + headerSize, 4));

        OptimFrogFile file = Assert.IsType<OptimFrogFile>(
            MediaFile.GetFile(MediaFixtures.Path_(fixture)));
        Assert.Equal(5100u, file.EncoderVersion);
    }

    [Theory]
    [InlineData("sample.mpc")]
    [InlineData("sample.tta")]
    [InlineData("sample.tak")]
    [InlineData("sample.ofr")]
    [InlineData("sample.ofs")]
    [InlineData("sample.off")]
    public void MetadataArtworkRepeatedAndStagedSavesPreservePayload(
        string fixture)
    {
        using var source = MediaFixtures.Copy(fixture);
        string output = Path.Combine(
            Path.GetTempPath(),
            $"apev2_{Guid.NewGuid():N}" +
            Path.GetExtension(fixture));
        byte[] original = File.ReadAllBytes(source.Path);
        byte[] payload = ReadPayload(source.Path);
        try
        {
            IMediaFile file = MediaFile.GetFile(
                source.Path, readOnly: false);
            var writer = Assert.IsAssignableFrom<IMetadataWriter>(
                file);
            writer.SetField(TagFields.Title, "First title");
            Assert.IsAssignableFrom<IUserStringMetadata>(file)
                .SetUserString("CUSTOM_NOTE", "Remember");
            Assert.IsAssignableFrom<IArtworkWriter>(file)
                .SetFrontCover(Png, "image/png");
            file.SaveTags(output);

            Assert.Equal(original, File.ReadAllBytes(source.Path));
            Assert.Equal(payload, ReadPayload(output));
            IMediaFile staged = MediaFile.GetFile(
                output, readOnly: false, readArtwork: true);
            Assert.Equal("First title", staged.Tags.First().Title);
            Assert.Equal(
                Png,
                Assert.Single(
                    staged.Tags.First().GetImageMetadata()).Data);

            Assert.IsAssignableFrom<IMetadataWriter>(staged)
                .SetField(TagFields.Title, "Second title");
            staged.SaveTags();
            Assert.Equal(
                "Second title",
                MediaFile.GetFile(output).Tags.First().Title);
            Assert.Equal(payload, ReadPayload(output));
        }
        finally
        {
            try { File.Delete(output); } catch { }
        }
    }

    [Fact]
    public void MusepackSv8StreamHeaderIsParsed()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"musepack8_{Guid.NewGuid():N}.mpc");
        try
        {
            WriteMusepackSv8(path);

            var file = Assert.IsType<MusepackFile>(
                MediaFile.GetFile(path));
            Assert.Equal(8, file.StreamVersion);
            Assert.Equal(44100u, file.Samplerate);
            Assert.Equal(2u, file.Channels);
            Assert.Equal(22u, file.DurationInFrames);
            Assert.Equal("SV8 title", file.Tags.First().Title);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData("sample.mpc")]
    [InlineData("sample.tta")]
    [InlineData("sample.tak")]
    [InlineData("sample.ofr")]
    public void InvalidSignatureIsRejected(string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);
        byte[] bytes = File.ReadAllBytes(media.Path);
        bytes[0] ^= 0x7f;
        File.WriteAllBytes(media.Path, bytes);

        Assert.Throws<InvalidDataException>(
            () => MediaFile.GetFile(media.Path));
    }

    [Fact]
    public void RegistryReportsPerCodecTransformBoundaries()
    {
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;
        foreach (string extension in new[]
                 {
                     ".mpc", ".tta", ".tak",
                     ".ofr", ".ofs", ".off",
                 })
        {
            Assert.True(registry.SupportsExtension(
                extension,
                MediaFormatCapabilities.LibraryIndex |
                MediaFormatCapabilities.ReadMetadata |
                MediaFormatCapabilities.WriteMetadata |
                MediaFormatCapabilities.ReadArtwork |
                MediaFormatCapabilities.WriteArtwork));
        }

        Assert.True(registry.SupportsExtension(
            ".tta",
            MediaFormatCapabilities.TranscodeSource |
            MediaFormatCapabilities.TranscodeDestination |
            MediaFormatCapabilities.Remux));
        Assert.True(registry.SupportsExtension(
            ".mpc", MediaFormatCapabilities.TranscodeSource));
        Assert.True(registry.SupportsExtension(
            ".tak", MediaFormatCapabilities.TranscodeSource));
        Assert.False(registry.SupportsExtension(
            ".mpc", MediaFormatCapabilities.TranscodeDestination));
        Assert.False(registry.SupportsExtension(
            ".tak", MediaFormatCapabilities.Remux));
        foreach (string extension in new[]
                 {
                     ".ofr", ".ofs", ".off",
                 })
            Assert.False(registry.SupportsExtension(
                extension,
                MediaFormatCapabilities.TranscodeSource));
    }

    private static byte[] ReadPayload(string path)
    {
        using var stream = File.OpenRead(path);
        var tag = new APETag();
        long length = tag.ReadTag(
            stream,
            onlyAtEnd: true,
            readArtwork: false,
            knownLength: stream.Length)
            ? tag.AudioEndOffset
            : stream.Length;
        byte[] payload = new byte[checked((int)length)];
        stream.Position = 0;
        stream.ReadExactly(payload);
        return payload;
    }

    private static void WriteMusepackSv8(string path)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("MPCK"u8);
        byte[] samples = EncodeVariableLength(13230);
        byte[] silence = EncodeVariableLength(0);
        byte[] payload =
        [
            1, 2, 3, 4,
            8,
            .. samples,
            .. silence,
            0,
            0x10,
        ];
        writer.Write("SH"u8);
        writer.Write(EncodeVariableLength(
            checked((ulong)payload.Length + 3)));
        writer.Write(payload);
        writer.Write(Enumerable.Range(0, 64)
            .Select(index => (byte)(index * 19))
            .ToArray());
        var tag = new APETag();
        tag.SetField(TagFields.Title, "SV8 title");
        writer.Write(tag.ToByteArray());
        writer.Flush();
        File.WriteAllBytes(path, stream.ToArray());
    }

    private static byte[] EncodeVariableLength(ulong value)
    {
        Span<byte> bytes = stackalloc byte[10];
        int index = bytes.Length;
        do
        {
            bytes[--index] = (byte)(value & 0x7f);
            value >>= 7;
        }
        while (value != 0);
        for (int current = index;
             current < bytes.Length - 1;
             current++)
            bytes[current] |= 0x80;
        return bytes[index..].ToArray();
    }
}
