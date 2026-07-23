using System.Buffers.Binary;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class MonkeysAudioFormatTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwC" +
        "AAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void DescriptorHeaderExposesCodecAndBaselineApeMetadata()
    {
        var file = Assert.IsType<MonkeysAudioFile>(
            MediaFile.GetFile(MediaFixtures.Path_("sample.ape")));
        ICodecProvider codec = Assert.Single(file.Codecs);

        Assert.Equal("Monkey's Audio", codec.CodecName);
        Assert.Equal(CodecType.Lossless, codec.CodecType);
        Assert.Equal(3990, file.FileVersion);
        Assert.Equal(2000, file.CompressionLevel);
        Assert.Equal(44100u, codec.Samplerate);
        Assert.Equal(2u, codec.Channels);
        Assert.Equal(16u, codec.BitsPerSample);
        Assert.Equal(22u, codec.DurationInFrames);
        Assert.True(codec.AverageBitrate > 0);
        Assert.Equal("TestTitle", Assert.Single(file.Tags).Title);
        Assert.Equal("APE", Assert.Single(file.Tags).TagType);
        Assert.IsAssignableFrom<IMetadataWriter>(Assert.Single(file.Tags));
    }

    [Fact]
    public void MetadataArtworkAndRepeatedSavesPreserveCodecPayload()
    {
        using var media = MediaFixtures.Copy("sample.ape");
        byte[] payload = ReadPayload(media.Path);
        var file = Assert.IsType<MonkeysAudioFile>(
            MediaFile.GetFile(media.Path, readOnly: false));

        file.SetField(TagFields.Title, "First title");
        file.SetUserString("CUSTOM_NOTE", "Remember");
        file.SetFrontCover(Png, "image/png");
        file.SaveTags();
        file.SetField(TagFields.Title, "Second title");
        file.SaveTags();

        var reloaded = Assert.IsType<MonkeysAudioFile>(
            MediaFile.GetFile(media.Path, readArtwork: true));
        IMetadataProvider tag = Assert.Single(reloaded.Tags);
        Assert.Equal("Second title", tag.Title);
        Assert.Contains(
            Assert.IsAssignableFrom<IUserStringMetadata>(tag)
                .GetUserStrings(),
            item => item.Key == "CUSTOM_NOTE" &&
                    item.Value == "Remember");
        Assert.Equal(
            Png,
            Assert.Single(tag.GetImageMetadata()).Data);
        Assert.Equal(payload, ReadPayload(media.Path));
    }

    [Fact]
    public void SaveToSeparateOutputLeavesSourceUntouched()
    {
        using var source = MediaFixtures.Copy("sample.ape");
        string output = Path.Combine(
            Path.GetTempPath(),
            $"monkeys_{Guid.NewGuid():N}.ape");
        byte[] original = File.ReadAllBytes(source.Path);
        try
        {
            var file = Assert.IsType<MonkeysAudioFile>(
                MediaFile.GetFile(source.Path, readOnly: false));
            file.SetField(TagFields.Title, "Staged title");
            file.SaveTags(output);

            Assert.Equal(original, File.ReadAllBytes(source.Path));
            Assert.Equal(
                "Staged title",
                MediaFile.GetFile(output).Tags.First().Title);
            Assert.Equal(
                ReadPayload(source.Path),
                ReadPayload(output));
        }
        finally
        {
            try { File.Delete(output); } catch { }
        }
    }

    [Fact]
    public void LeadingId3v2IsPreservedButApeRemainsTheMetadataLayer()
    {
        using var source = MediaFixtures.Copy("sample.ape");
        string path = Path.Combine(
            Path.GetTempPath(),
            $"monkeys_id3_{Guid.NewGuid():N}.ape");
        byte[] original = File.ReadAllBytes(source.Path);
        byte[] id3 =
        [
            (byte)'I', (byte)'D', (byte)'3',
            3, 0, 0, 0, 0, 0, 0,
        ];
        try
        {
            File.WriteAllBytes(path, [.. id3, .. original]);
            var file = Assert.IsType<MonkeysAudioFile>(
                MediaFile.GetFile(path, readOnly: false));
            Assert.Equal("TestTitle", Assert.Single(file.Tags).Title);

            file.SetField(TagFields.Title, "APE after ID3");
            file.SaveTags();

            byte[] rewritten = File.ReadAllBytes(path);
            Assert.True(rewritten.AsSpan(0, id3.Length)
                .SequenceEqual(id3));
            Assert.Equal(
                "APE after ID3",
                MediaFile.GetFile(path).Tags.First().Title);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void LegacyHeaderComputesTechnicalProperties()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"monkeys_legacy_{Guid.NewGuid():N}.ape");
        try
        {
            WriteLegacyFixture(path);

            var file = Assert.IsType<MonkeysAudioFile>(
                MediaFile.GetFile(path));
            ICodecProvider codec = Assert.Single(file.Codecs);
            Assert.Equal(3970, file.FileVersion);
            Assert.Equal(4000, file.CompressionLevel);
            Assert.Equal(48000u, codec.Samplerate);
            Assert.Equal(2u, codec.Channels);
            Assert.Equal(16u, codec.BitsPerSample);
            Assert.Equal(462u, codec.DurationInFrames);
            Assert.True(codec.AverageBitrate > 0);
            Assert.Equal("Legacy title", file.Tags.First().Title);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData(0, (byte)'X')]
    [InlineData(4, byte.MaxValue)]
    public void InvalidSignatureOrVersionIsRejected(
        int offset,
        byte value)
    {
        using var media = MediaFixtures.Copy("sample.ape");
        byte[] bytes = File.ReadAllBytes(media.Path);
        bytes[offset] = value;
        File.WriteAllBytes(media.Path, bytes);

        Assert.Throws<InvalidDataException>(
            () => MediaFile.GetFile(media.Path));
    }

    [Theory]
    [InlineData(8, 51u)]
    [InlineData(16, 0u)]
    [InlineData(24, uint.MaxValue)]
    public void InvalidDescriptorBoundsAreRejected(
        int offset,
        uint value)
    {
        using var media = MediaFixtures.Copy("sample.ape");
        byte[] bytes = File.ReadAllBytes(media.Path);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(offset, 4), value);
        File.WriteAllBytes(media.Path, bytes);

        Assert.Throws<InvalidDataException>(
            () => MediaFile.GetFile(media.Path));
    }

    [Fact]
    public void RegistryReleasesMonkeyAudioForIndexingAndSourceTranscoding()
    {
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;

        Assert.True(registry.TryGetByExtension(
            ".ape", out MediaFormatDefinition format));
        Assert.Equal(MediaFormatFamily.MonkeysAudio, format.Family);
        Assert.True(format.Supports(
            MediaFormatCapabilities.LibraryIndex |
            MediaFormatCapabilities.ReadMetadata |
            MediaFormatCapabilities.WriteMetadata |
            MediaFormatCapabilities.ReadArtwork |
            MediaFormatCapabilities.WriteArtwork |
            MediaFormatCapabilities.TranscodeSource));
        Assert.False(format.CanTranscodeTo);
        Assert.False(format.CanRemux);
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

    private static void WriteLegacyFixture(string path)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("MAC "u8);
        writer.Write((ushort)3970);
        writer.Write((ushort)4000);
        writer.Write((ushort)32);
        writer.Write((ushort)2);
        writer.Write(48000u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(2u);
        writer.Write(1000u);
        writer.Write(40u);
        writer.Write(80u);
        writer.Write(Enumerable.Range(0, 96)
            .Select(index => (byte)(index * 17))
            .ToArray());
        var tag = new APETag();
        tag.SetField(TagFields.Title, "Legacy title");
        writer.Write(tag.ToByteArray());
        writer.Flush();
        File.WriteAllBytes(path, stream.ToArray());
    }
}
