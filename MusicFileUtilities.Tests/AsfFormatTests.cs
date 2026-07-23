using System.Buffers.Binary;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class AsfFormatTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwC" +
        "AAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void RealWmaFixtureProjectsCodecAndMetadata()
    {
        IMediaFile media = MediaFile.GetFile(
            MediaFixtures.Path_("sample.wma"));
        AsfFile file = Assert.IsType<AsfFile>(media);
        IMetadataProvider tag = Assert.Single(file.Tags);
        Dictionary<TagFields, string> known = Known(tag);

        Assert.Equal("Windows Media Audio 2", file.CodecName);
        Assert.Equal(CodecType.Lossy, file.CodecType);
        Assert.Equal(44100u, file.Samplerate);
        Assert.Equal(2u, file.Channels);
        Assert.Equal(16u, file.BitsPerSample);
        Assert.Equal(64000u, file.AverageBitrate);
        Assert.True(file.DurationInFrames > 0);
        Assert.Equal("ASF", tag.TagType);
        Assert.Equal("TestTitle", tag.Title);
        Assert.Equal("TestArtist", tag.Artist);
        Assert.Equal("TestAlbum", tag.Album);
        Assert.Equal("2021", tag.ReleaseDate);
        Assert.Equal(3, tag.TrackNumber);
        Assert.Equal("Rock", known[TagFields.Genre]);
    }

    [Fact]
    public void MetadataArtworkAndStagedSavesPreserveDataObject()
    {
        using var source = MediaFixtures.Copy("sample.wma");
        string output = Path.Combine(
            Path.GetTempPath(), $"asf_{Guid.NewGuid():N}.wma");
        byte[] original = File.ReadAllBytes(source.Path);
        byte[] payload = ReadPostHeader(source.Path);
        byte[] largeCover =
            Png.Concat(new byte[70_000]).ToArray();
        try
        {
            AsfFile file = Assert.IsType<AsfFile>(
                MediaFile.GetFile(
                    source.Path, readOnly: false, readArtwork: true));
            file.SetField(TagFields.Title, "First ASF title");
            file.SetField(TagFields.AlbumArtist, "Album ensemble");
            file.SetField(TagFields.TotalTracks, "12");
            file.SetUserString("X-Custom", "Remember me");
            file.SetImages(
            [
                new(
                    ID3v2Util.APICType.FrontCover,
                    "image/png",
                    "Large cover",
                    largeCover),
                new(
                    ID3v2Util.APICType.BackCover,
                    "image/png",
                    "Back",
                    Png),
            ]);
            file.SaveTags(output);

            Assert.Equal(original, File.ReadAllBytes(source.Path));
            Assert.Equal(payload, ReadPostHeader(output));
            Assert.Equal(
                new FileInfo(output).Length,
                checked((long)ReadFileSize(output)));

            AsfFile staged = Assert.IsType<AsfFile>(
                MediaFile.GetFile(
                    output, readOnly: false, readArtwork: true));
            IMetadataProvider tag = Assert.Single(staged.Tags);
            Dictionary<TagFields, string> known = Known(tag);
            Assert.Equal("First ASF title", tag.Title);
            Assert.Equal("Album ensemble", tag.AlbumArtist);
            Assert.Equal("3", known[TagFields.TrackNumber]);
            Assert.Equal("12", known[TagFields.TotalTracks]);
            Assert.Contains(
                staged.GetUserStrings(),
                item => item.Key == "X-Custom" &&
                        item.Value == "Remember me");
            IMetadataImage[] images =
                tag.GetImageMetadata().ToArray();
            Assert.Equal(2, images.Length);
            Assert.Equal(largeCover, images[0].Data);
            Assert.Equal("FrontCover", images[0].Category);
            Assert.Equal(1, images[0].Width);
            Assert.Equal(1, images[0].Height);
            Assert.Equal(Png, images[1].Data);
            Assert.Equal("BackCover", images[1].Category);

            staged.SetField(TagFields.Title, "Second ASF title");
            staged.SaveTags();
            AsfFile repeated = Assert.IsType<AsfFile>(
                MediaFile.GetFile(output, readArtwork: true));
            Assert.Equal(
                "Second ASF title",
                repeated.Tags.Single().Title);
            Assert.Equal(
                largeCover,
                repeated.Tags.Single()
                    .GetImageMetadata().First().Data);
            Assert.Equal(payload, ReadPostHeader(output));
        }
        finally
        {
            try { File.Delete(output); } catch { }
        }
    }

    [Fact]
    public void MetadataOnlyReadStillPreservesDeferredArtwork()
    {
        using var media = MediaFixtures.Copy("sample.wma");
        AsfFile first = Assert.IsType<AsfFile>(
            MediaFile.GetFile(media.Path, readArtwork: true));
        first.SetFrontCover(Png, "image/png");
        first.SaveTags();

        AsfFile metadataOnly = Assert.IsType<AsfFile>(
            MediaFile.GetFile(
                media.Path, readOnly: false, readArtwork: false));
        Assert.Empty(metadataOnly.Tags.Single().GetImageMetadata());
        metadataOnly.SetField(TagFields.Album, "Deferred art album");
        metadataOnly.SaveTags();

        AsfFile reloaded = Assert.IsType<AsfFile>(
            MediaFile.GetFile(media.Path, readArtwork: true));
        Assert.Equal("Deferred art album", reloaded.Tags.Single().Album);
        Assert.Equal(
            Png,
            Assert.Single(
                reloaded.Tags.Single().GetImageMetadata()).Data);
    }

    [Fact]
    public void RemoveImagesPreservesTextAndAudio()
    {
        using var media = MediaFixtures.Copy("sample.wma");
        byte[] payload = ReadPostHeader(media.Path);
        AsfFile file = Assert.IsType<AsfFile>(
            MediaFile.GetFile(media.Path, readArtwork: true));
        file.SetFrontCover(Png, "image/png");
        file.SaveTags();

        AsfFile withArt = Assert.IsType<AsfFile>(
            MediaFile.GetFile(media.Path, readArtwork: true));
        withArt.RemoveImages();
        withArt.SaveTags();

        AsfFile clean = Assert.IsType<AsfFile>(
            MediaFile.GetFile(media.Path, readArtwork: true));
        Assert.Empty(clean.Tags.Single().GetImageMetadata());
        Assert.Equal("TestTitle", clean.Tags.Single().Title);
        Assert.Equal(payload, ReadPostHeader(media.Path));
    }

    [Theory]
    [InlineData(".wma", true)]
    [InlineData(".asf", false)]
    [InlineData(".wmv", false)]
    public void RegistryReportsNativeAsfCapabilities(
        string extension,
        bool indexed)
    {
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;
        Assert.True(registry.SupportsExtension(
            extension,
            MediaFormatCapabilities.ReadMetadata |
            MediaFormatCapabilities.WriteMetadata |
            MediaFormatCapabilities.ReadArtwork |
            MediaFormatCapabilities.WriteArtwork |
            MediaFormatCapabilities.TranscodeSource |
            MediaFormatCapabilities.Remux));
        Assert.Equal(
            indexed,
            registry.SupportsExtension(
                extension, MediaFormatCapabilities.LibraryIndex));
    }

    [Fact]
    public void InvalidHeaderAndObjectLengthsAreRejected()
    {
        using var media = MediaFixtures.Copy("sample.wma");
        byte[] invalidSignature = File.ReadAllBytes(media.Path);
        invalidSignature[0] ^= 0x7f;
        File.WriteAllBytes(media.Path, invalidSignature);
        Assert.Throws<InvalidDataException>(
            () => MediaFile.GetFile(media.Path));

        File.Copy(
            MediaFixtures.Path_("sample.wma"),
            media.Path,
            overwrite: true);
        byte[] invalidObject = File.ReadAllBytes(media.Path);
        BinaryPrimitives.WriteUInt64LittleEndian(
            invalidObject.AsSpan(46, 8), ulong.MaxValue);
        File.WriteAllBytes(media.Path, invalidObject);
        Assert.Throws<InvalidDataException>(
            () => MediaFile.GetFile(media.Path));
    }

    private static byte[] ReadPostHeader(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        ulong headerSize =
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16, 8));
        return bytes[checked((int)headerSize)..];
    }

    private static Dictionary<TagFields, string> Known(
        IMetadataProvider tag)
    {
        var result = new Dictionary<TagFields, string>();
        foreach (KeyValuePair<TagFields, string> item in
                 tag.GetKnownMetadata())
            result[item.Key] = item.Value;
        return result;
    }

    private static ulong ReadFileSize(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int offset = 30;
        while (offset + 24 <= bytes.Length)
        {
            Guid id = new(bytes.AsSpan(offset, 16));
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(
                bytes.AsSpan(offset + 16, 8));
            if (id == new Guid(
                    "8cabdca1-a947-11cf-8ee4-00c00c205365"))
                return BinaryPrimitives.ReadUInt64LittleEndian(
                    bytes.AsSpan(offset + 40, 8));
            offset = checked(offset + (int)size);
        }
        throw new InvalidDataException(
            "File Properties Object is missing.");
    }
}
