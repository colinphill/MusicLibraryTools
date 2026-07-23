using System.Buffers.Binary;
using System.Text;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class AacFormatTests
{
    private static readonly byte[] Cover = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwC" +
        "AAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Theory]
    [InlineData("none", 1, "ID3v23")]
    [InlineData("id3", 1, "ID3v23")]
    [InlineData("ape", 1, "APE")]
    [InlineData("both", 2, "ID3v23")]
    public void LayerMatrixRoundTripsPrimaryMetadataArtworkAndAudio(
        string layers,
        int expectedLayerCount,
        string expectedPrimaryType)
    {
        string path = TempPath();
        byte[] audio = WriteFixture(path, layers);
        try
        {
            var media = Assert.IsType<AACFile>(
                MediaFile.GetFile(path, readOnly: false, readArtwork: true));
            Assert.Equal(expectedLayerCount, media.Tags.Count());
            Assert.Equal(expectedPrimaryType, media.Tags.First().TagType);
            Assert.Equal(44100u, media.Samplerate);
            Assert.Equal(2u, media.Channels);
            Assert.True(media.DurationInFrames > 0);
            Assert.True(media.AverageBitrate > 0);

            media.SetField(TagFields.Title, "Primary replacement");
            media.SetFrontCover(Cover, "image/png");
            media.SaveTags();

            var reloaded = Assert.IsType<AACFile>(
                MediaFile.GetFile(path, readOnly: true, readArtwork: true));
            Assert.Equal("Primary replacement", reloaded.Tags.First().Title);
            Assert.Equal(
                Cover,
                Assert.Single(
                    reloaded.Tags.First().GetImageMetadata()).Data);
            Assert.Equal(audio, ReadAudio(path));
            Assert.Equal(
                layers == "ape" ? "APE" : "ID3v23",
                reloaded.Tags.First().TagType);

            if (layers == "both")
            {
                IMetadataProvider ape = Assert.Single(
                    reloaded.Tags, tag => tag.TagType == "APE");
                Assert.Equal("APE title", ape.Title);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void SecondaryApeLayerCanBeEditedWithoutFlatteningId3()
    {
        string path = TempPath();
        byte[] audio = WriteFixture(path, "both");
        try
        {
            var media = Assert.IsType<AACFile>(
                MediaFile.GetFile(path, readOnly: false));
            IMetadataWriter ape = Assert.IsAssignableFrom<IMetadataWriter>(
                Assert.Single(media.Tags, tag => tag.TagType == "APE"));
            ape.SetField(TagFields.Album, "APE album");
            ape.Save();

            AACFile reloaded = Assert.IsType<AACFile>(
                MediaFile.GetFile(path));
            IMetadataProvider id3 = reloaded.Tags.First();
            IMetadataProvider apeReloaded = reloaded.Tags.Last();
            Assert.Equal("ID3 title", id3.Title);
            Assert.Equal("APE title", apeReloaded.Title);
            Assert.Equal("APE album", apeReloaded.Album);
            Assert.Equal(audio, ReadAudio(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData("id3", TagLayerKind.ApeV2, "APE")]
    [InlineData("ape", TagLayerKind.Id3v2, "ID3v23")]
    public void AddTagLayerCopiesPrimaryMetadataAndPreservesAudio(
        string initialLayer,
        TagLayerKind addedKind,
        string addedTagType)
    {
        string path = TempPath();
        byte[] audio = WriteFixture(path, initialLayer);
        try
        {
            var media = Assert.IsType<AACFile>(
                MediaFile.GetFile(path, readOnly: false));
            ITagLayerEditor editor = media;

            editor.AddTagLayer(addedKind, TagLayerCopyMode.CopyPrimary);
            media.SaveTags();

            var reloaded = Assert.IsType<AACFile>(MediaFile.GetFile(path));
            Assert.All(reloaded.EditableTagLayers, layer =>
                Assert.True(layer.IsPresent));
            IMetadataProvider added = Assert.Single(
                reloaded.Tags, tag => tag.TagType == addedTagType);
            Assert.Equal(
                initialLayer == "id3" ? "ID3 title" : "APE title",
                added.Title);
            Assert.Equal(audio, ReadAudio(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData(TagLayerKind.Id3v2, "APE", "APE title")]
    [InlineData(TagLayerKind.ApeV2, "ID3v23", "ID3 title")]
    public void RemoveSpecificTagLayerPreservesTheOtherLayerAndAudio(
        TagLayerKind removedKind,
        string remainingTagType,
        string remainingTitle)
    {
        string path = TempPath();
        byte[] audio = WriteFixture(path, "both");
        try
        {
            var media = Assert.IsType<AACFile>(
                MediaFile.GetFile(path, readOnly: false));

            media.RemoveTagLayer(removedKind);
            media.SaveTags();

            var reloaded = Assert.IsType<AACFile>(MediaFile.GetFile(path));
            IMetadataProvider remaining = Assert.Single(reloaded.Tags);
            Assert.Equal(remainingTagType, remaining.TagType);
            Assert.Equal(remainingTitle, remaining.Title);
            Assert.Equal(audio, ReadAudio(path));
            Assert.False(Assert.Single(
                reloaded.EditableTagLayers,
                layer => layer.Kind == removedKind).IsPresent);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData("id3", TagLayerKind.Id3v2)]
    [InlineData("ape", TagLayerKind.ApeV2)]
    public void RemoveLastTagLayerProducesDeliberatelyUntaggedAac(
        string initialLayer,
        TagLayerKind removedKind)
    {
        string path = TempPath();
        byte[] audio = WriteFixture(path, initialLayer);
        try
        {
            var media = Assert.IsType<AACFile>(
                MediaFile.GetFile(path, readOnly: false));

            media.RemoveTagLayer(removedKind);
            media.SaveTags();

            Assert.Equal(audio, File.ReadAllBytes(path));
            var reloaded = Assert.IsType<AACFile>(MediaFile.GetFile(path));
            Assert.All(reloaded.EditableTagLayers, layer =>
                Assert.False(layer.IsPresent));
            Assert.Empty(reloaded.Tags.First().GetKnownMetadata());
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void EmptyLayerCreationDoesNotDuplicatePrimaryMetadata()
    {
        string path = TempPath();
        byte[] audio = WriteFixture(path, "id3");
        try
        {
            var media = Assert.IsType<AACFile>(
                MediaFile.GetFile(path, readOnly: false));

            media.AddTagLayer(
                TagLayerKind.ApeV2, TagLayerCopyMode.Empty);
            media.SaveTags();

            var reloaded = Assert.IsType<AACFile>(MediaFile.GetFile(path));
            Assert.Equal("ID3 title", reloaded.Tags.First().Title);
            Assert.Equal("", reloaded.Tags.Last().Title);
            Assert.Equal(audio, ReadAudio(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void CopiedLayerIncludesCustomTextAndArtwork()
    {
        string path = TempPath();
        byte[] audio = WriteFixture(path, "id3");
        try
        {
            var media = Assert.IsType<AACFile>(
                MediaFile.GetFile(path, readOnly: false));
            media.SetUserString("DJ_SET", "Night");
            media.SetImages(
            [
                new(
                    ID3v2Util.APICType.BackCover,
                    "image/png",
                    "back",
                    Cover),
            ]);

            media.AddTagLayer(
                TagLayerKind.ApeV2, TagLayerCopyMode.CopyPrimary);
            media.SaveTags();

            var reloaded = Assert.IsType<AACFile>(
                MediaFile.GetFile(path, readArtwork: true));
            IMetadataProvider ape = reloaded.Tags.Last();
            Assert.Contains(
                Assert.IsAssignableFrom<IUserStringMetadata>(ape)
                    .GetUserStrings(),
                value => value.Key == "DJ_SET" &&
                    value.Value == "Night");
            IMetadataImage image = Assert.Single(
                ape.GetImageMetadata());
            Assert.Equal(Cover, image.Data);
            Assert.Equal(audio, ReadAudio(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void LayerEditorRejectsImpossibleTransitionsBeforeWriting()
    {
        string path = TempPath();
        WriteFixture(path, "id3");
        try
        {
            var media = Assert.IsType<AACFile>(
                MediaFile.GetFile(path, readOnly: false));

            Assert.Throws<InvalidOperationException>(
                () => media.AddTagLayer(TagLayerKind.Id3v2));
            Assert.Throws<InvalidOperationException>(
                () => media.RemoveTagLayer(TagLayerKind.ApeV2));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData(ID3v2Version.V22, "ID3v22")]
    [InlineData(ID3v2Version.V24, "ID3v24")]
    public void Id3LayerVersionConversionPreservesApeAndAudio(
        ID3v2Version targetVersion,
        string expectedTagType)
    {
        string path = TempPath();
        byte[] audio = WriteFixture(path, "both");
        try
        {
            var media = Assert.IsType<AACFile>(
                MediaFile.GetFile(path, readOnly: false));
            ID3v2Tag id3 = Assert.IsAssignableFrom<ID3v2Tag>(
                media.Tags.First());

            id3.ChangeVersion(targetVersion);
            media.SaveTags();

            var reloaded = Assert.IsType<AACFile>(MediaFile.GetFile(path));
            Assert.Equal(expectedTagType, reloaded.Tags.First().TagType);
            Assert.Equal("ID3 title", reloaded.Tags.First().Title);
            Assert.Equal("APE title", reloaded.Tags.Last().Title);
            Assert.Equal(audio, ReadAudio(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void SaveToSeparatePathLeavesBothSourceLayersUntouched()
    {
        string source = TempPath();
        string output = TempPath();
        byte[] audio = WriteFixture(source, "both");
        byte[] original = File.ReadAllBytes(source);
        try
        {
            var media = Assert.IsType<AACFile>(
                MediaFile.GetFile(source, readOnly: false));
            media.SetField(TagFields.Title, "Staged AAC title");
            media.SaveTags(output);

            Assert.Equal(original, File.ReadAllBytes(source));
            AACFile staged = Assert.IsType<AACFile>(
                MediaFile.GetFile(output));
            Assert.Equal("Staged AAC title", staged.Tags.First().Title);
            Assert.Equal("APE title", staged.Tags.Last().Title);
            Assert.Equal(audio, ReadAudio(output));
        }
        finally
        {
            try { File.Delete(source); } catch { }
            try { File.Delete(output); } catch { }
        }
    }

    [Fact]
    public void ParserCountsCrcProtectedFramesAndRawDataBlocks()
    {
        string path = TempPath();
        byte[] first = BuildAdtsFrame(
            [1, 2, 3, 4], crcAbsent: false, rawBlocks: 2);
        byte[] second = BuildAdtsFrame(
            [5, 6, 7], crcAbsent: true, rawBlocks: 1);
        File.WriteAllBytes(path, first.Concat(second).ToArray());
        try
        {
            AACFile media = Assert.IsType<AACFile>(
                MediaFile.GetFile(path));
            Assert.Equal(
                (uint)((3UL * 1024 * 75) / 44100),
                media.DurationInFrames);
            Assert.Equal(first.Concat(second), ReadAudio(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void InvalidAdtsFrameIsRejected()
    {
        string path = TempPath();
        File.WriteAllBytes(path, [0xFF, 0xF1, 0, 0, 0, 0, 0]);
        try
        {
            Assert.Throws<InvalidDataException>(
                () => MediaFile.GetFile(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void RawAacAdvertisesReleasedNativeCapabilities()
    {
        Assert.True(MediaFormatRegistry.Default.SupportsExtension(
            ".aac",
            MediaFormatCapabilities.LibraryIndex |
            MediaFormatCapabilities.ReadMetadata |
            MediaFormatCapabilities.WriteMetadata |
            MediaFormatCapabilities.ReadArtwork |
            MediaFormatCapabilities.WriteArtwork |
            MediaFormatCapabilities.Remux |
            MediaFormatCapabilities.TranscodeSource));
        Assert.False(MediaFormatRegistry.Default.SupportsExtension(
            ".aac",
            MediaFormatCapabilities.TranscodeDestination));
    }

    [Fact]
    public void RealEncoderFixtureRemainsReadableAfterFirstTagWrite()
    {
        using var media = MediaFixtures.Copy("sample.aac");
        byte[] audio = ReadAudio(media.Path);

        AACFile file = Assert.IsType<AACFile>(
            MediaFile.GetFile(media.Path, readOnly: false));
        file.SetField(TagFields.Title, "Real AAC fixture");
        file.SaveTags();

        AACFile reloaded = Assert.IsType<AACFile>(
            MediaFile.GetFile(media.Path));
        Assert.Equal("Real AAC fixture", reloaded.Tags.First().Title);
        Assert.Equal(audio, ReadAudio(media.Path));
    }

    private static string TempPath() => Path.Combine(
        Path.GetTempPath(),
        $"aac_{Guid.NewGuid():N}.aac");

    private static byte[] WriteFixture(string path, string layers)
    {
        byte[] audio =
        [
            .. BuildAdtsFrame([1, 2, 3, 4, 5]),
            .. BuildAdtsFrame([6, 7, 8]),
            .. BuildAdtsFrame([9, 10, 11, 12]),
        ];
        byte[] id3 = layers is "id3" or "both"
            ? BuildId3("ID3 title")
            : [];
        byte[] ape = [];
        if (layers is "ape" or "both")
        {
            var tag = new APETag();
            tag.SetField(TagFields.Title, "APE title");
            ape = tag.ToByteArray();
        }
        File.WriteAllBytes(path, [.. id3, .. audio, .. ape]);
        return audio;
    }

    private static byte[] BuildAdtsFrame(
        byte[] payload,
        bool crcAbsent = true,
        int rawBlocks = 1)
    {
        int headerLength = crcAbsent ? 7 : 9;
        int frameLength = headerLength + payload.Length;
        byte[] frame = new byte[frameLength];
        frame[0] = 0xFF;
        frame[1] = (byte)(0xF0 | (crcAbsent ? 1 : 0));
        frame[2] = 0x50; // AAC LC, 44.1 kHz, channel_configuration high bit 0
        frame[3] = (byte)(0x80 | ((frameLength >> 11) & 3)); // stereo
        frame[4] = (byte)(frameLength >> 3);
        frame[5] = (byte)(((frameLength & 7) << 5) | 0x1F);
        frame[6] = (byte)(0xFC | (rawBlocks - 1));
        if (!crcAbsent)
        {
            frame[7] = 0x12;
            frame[8] = 0x34;
        }
        payload.CopyTo(frame, headerLength);
        return frame;
    }

    private static byte[] BuildId3(string title)
    {
        byte[] value = Encoding.Latin1.GetBytes(title);
        byte[] frame = new byte[11 + value.Length];
        "TIT2"u8.CopyTo(frame);
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(4), checked((uint)value.Length + 1));
        frame[10] = 0;
        value.CopyTo(frame, 11);
        byte[] tag = new byte[10 + frame.Length];
        "ID3"u8.CopyTo(tag);
        tag[3] = 3;
        int size = frame.Length;
        tag[6] = (byte)((size >> 21) & 0x7F);
        tag[7] = (byte)((size >> 14) & 0x7F);
        tag[8] = (byte)((size >> 7) & 0x7F);
        tag[9] = (byte)(size & 0x7F);
        frame.CopyTo(tag, 10);
        return tag;
    }

    private static byte[] ReadAudio(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int start = 0;
        if (file.Length >= 10 && file.AsSpan(0, 3).SequenceEqual("ID3"u8))
        {
            int body =
                (file[6] << 21) |
                (file[7] << 14) |
                (file[8] << 7) |
                file[9];
            start = 10 + body +
                (file[3] == 4 && (file[5] & 0x10) != 0 ? 10 : 0);
        }
        int end = file.Length;
        if (end - start >= 32 &&
            file.AsSpan(end - 32, 8).SequenceEqual("APETAGEX"u8))
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                file.AsSpan(end - 20, 4));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(
                file.AsSpan(end - 12, 4));
            end -= checked((int)size +
                ((flags & 0x80000000u) != 0 ? 32 : 0));
        }
        return file.AsSpan(start, end - start).ToArray();
    }
}
