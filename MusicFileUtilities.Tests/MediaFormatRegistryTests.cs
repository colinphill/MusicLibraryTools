using MetadataCaching;
using MusicFileUtilities;
using MusicLibraryTools;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class MediaFormatRegistryTests
{
    [Fact]
    public void DefaultRegistryReportsReleasedLibraryIndexFormats()
    {
        string[] expected =
            [".dsf", ".m4a", ".mp3", ".flac", ".ogg", ".opus", ".spx", ".wv"];
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;

        Assert.Equal(expected, registry.GetExtensions(MediaFormatCapabilities.LibraryIndex));
        Assert.Equal(expected.Order(), MetadataExtensions.ValidExtensions.Order());
        Assert.Equal(expected.Order(), MetadataCache.ValidExtensions.Order());
    }

    [Fact]
    public void DefaultRegistryIncludesDirectlyEditableMp4VariantsWithoutIndexingThem()
    {
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;

        foreach (string extension in new[]
                 {
                     ".mp4", ".m4p", ".m4r", ".m4b", ".m4v",
                 })
        {
            Assert.True(registry.SupportsExtension(extension, MediaFormatCapabilities.ReadMetadata));
            Assert.True(registry.SupportsExtension(extension, MediaFormatCapabilities.WriteMetadata));
            Assert.True(registry.SupportsExtension(extension, MediaFormatCapabilities.ReadArtwork));
            Assert.True(registry.SupportsExtension(extension, MediaFormatCapabilities.WriteArtwork));
            Assert.False(registry.SupportsExtension(extension, MediaFormatCapabilities.LibraryIndex));
        }
    }

    [Theory]
    [InlineData(".m4b")]
    [InlineData(".m4v")]
    public void ReusedMp4VariantsRoundTripMetadataArtworkAndPayload(
        string extension)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"format_{Guid.NewGuid():N}{extension}");
        File.Copy(MediaFixtures.Path_("sample_aac.m4a"), path);
        byte[] payloadBefore = ReadMdat(path);
        byte[] cover = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwC" +
            "AAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        try
        {
            var media = Assert.IsType<MP4File>(
                MediaFile.GetFile(path, readOnly: false));
            media.SetField(TagFields.Title, "Alias title");
            Assert.IsAssignableFrom<IArtworkWriter>(media)
                .SetFrontCover(cover, "image/png");
            media.SaveTags();

            IMediaFile reloaded = MediaFile.GetFile(
                path,
                readOnly: true,
                readArtwork: true);
            Assert.Equal("Alias title", reloaded.Tags.First().Title);
            Assert.Equal(
                cover,
                Assert.Single(
                    reloaded.Tags.First().GetImageMetadata()).Data);
            Assert.Equal(payloadBefore, ReadMdat(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void DefaultRegistryReportsTransformCapabilities()
    {
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;

        Assert.True(registry.SupportsExtension(".flac",
            MediaFormatCapabilities.TranscodeSource |
            MediaFormatCapabilities.TranscodeDestination));
        Assert.True(registry.SupportsExtension(".m4a",
            MediaFormatCapabilities.TranscodeSource |
            MediaFormatCapabilities.TranscodeDestination));
        Assert.True(registry.SupportsExtension(".mp3",
            MediaFormatCapabilities.TranscodeSource));
        Assert.False(registry.SupportsExtension(".mp3",
            MediaFormatCapabilities.TranscodeDestination));
        Assert.True(registry.SupportsExtension(".wv",
            MediaFormatCapabilities.TranscodeDestination));
        Assert.Contains(".m4a", registry.GetExtensions(MediaFormatCapabilities.Remux));
        Assert.Contains(".mp4", registry.GetExtensions(MediaFormatCapabilities.Remux));
        Assert.True(registry.TryGetByExtension(".m4a", out MediaFormatDefinition m4a));
        Assert.True(registry.TryGetByExtension(".mp4", out MediaFormatDefinition mp4));
        Assert.Equal(m4a.Family, mp4.Family);
    }

    [Fact]
    public void ExtensionLookupNormalizesPickerPatternsAndCase()
    {
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;

        Assert.True(registry.TryGetByExtension("*.FLAC", out MediaFormatDefinition format));
        Assert.Equal(".flac", format.Extension);
        Assert.Equal(MediaFormatFamily.Flac, format.Family);
        Assert.True(registry.SupportsPath(@"C:\\Music\\TRACK.FLAC",
            MediaFormatCapabilities.ReadMetadata | MediaFormatCapabilities.ReadArtwork));
        Assert.False(registry.SupportsPath("readme.txt", MediaFormatCapabilities.ReadMetadata));
    }

    [Fact]
    public void RegistryRejectsContradictoryOrDuplicateDefinitions()
    {
        Assert.Throws<ArgumentException>(() => new MediaFormatRegistry(
        [
            new(".one", "One", MediaFormatFamily.Flac, MediaFormatCapabilities.WriteMetadata),
        ]));

        Assert.Throws<ArgumentException>(() => new MediaFormatRegistry(
        [
            new(".one", "One", MediaFormatFamily.Flac, MediaFormatCapabilities.ReadMetadata),
            new("ONE", "Duplicate", MediaFormatFamily.Flac, MediaFormatCapabilities.ReadMetadata),
        ]));
    }

    [Fact]
    public void RegistrySupportsProfileSpecificCapabilityViews()
    {
        var registry = new MediaFormatRegistry(
        [
            new("custom", "Custom", MediaFormatFamily.Flac,
                MediaFormatCapabilities.ReadMetadata |
                MediaFormatCapabilities.ReadArtwork |
                MediaFormatCapabilities.WriteArtwork),
        ]);

        Assert.True(registry.SupportsPath("track.CUSTOM", MediaFormatCapabilities.ReadMetadata));
        Assert.False(registry.SupportsPath("track.custom", MediaFormatCapabilities.WriteMetadata));
        Assert.True(registry.SupportsPath("track.custom", MediaFormatCapabilities.WriteArtwork));
        Assert.Empty(registry.GetExtensions(MediaFormatCapabilities.LibraryIndex));
    }

    [Fact]
    public void MediaFileDispatchUsesRegistryFamilyInsteadOfAnotherExtensionList()
    {
        string path = Path.Combine(Path.GetTempPath(), $"format_{Guid.NewGuid():N}.audio");
        File.Copy(MediaFixtures.Path_("sample.flac"), path);
        try
        {
            var registry = new MediaFormatRegistry(
            [
                new(".audio", "FLAC alias", MediaFormatFamily.Flac,
                    MediaFormatCapabilities.ReadMetadata),
            ]);

            IMediaFile media = MediaFile.GetFile(path, readOnly: true,
                readArtwork: false, formatRegistry: registry);

            Assert.IsType<FLACFile>(media);
            Assert.Equal("TestTitle", media.Tags.First().Title);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MusicFileEnumeratorUsesInjectedIndexCapability()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"formats_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "track.audio"), []);
            File.WriteAllBytes(Path.Combine(directory, "notes.txt"), []);
            var registry = new MediaFormatRegistry(
            [
                new(".audio", "Custom index format", MediaFormatFamily.Flac,
                    MediaFormatCapabilities.LibraryIndex |
                    MediaFormatCapabilities.ReadMetadata),
            ]);

            using var enumerator = new MusicFileEnumerator(
                directory, recurse: false, formats: registry);
            var entries = enumerator.ToDictionary(
                entry => Path.GetFileName(entry.Name), entry => entry.FileType);

            Assert.Equal(MFEType.MusicFile, entries["track.audio"]);
            Assert.Equal(MFEType.Other, entries["notes.txt"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] ReadMdat(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] header = new byte[8];
        for (long position = 0; position + header.Length <= stream.Length;)
        {
            stream.Position = position;
            stream.ReadExactly(header);
            ulong size = ((ulong)header[0] << 24) |
                         ((ulong)header[1] << 16) |
                         ((ulong)header[2] << 8) |
                         header[3];
            string type = System.Text.Encoding.ASCII.GetString(
                header, 4, 4);
            int headerSize = 8;
            if (size == 1)
            {
                byte[] extended = new byte[8];
                stream.ReadExactly(extended);
                size = 0;
                foreach (byte value in extended)
                    size = (size << 8) | value;
                headerSize = 16;
            }
            else if (size == 0)
                size = checked((ulong)(stream.Length - position));
            if (size < (ulong)headerSize)
                throw new InvalidDataException("Invalid MP4 atom size.");
            if (type == "mdat")
            {
                byte[] payload = new byte[checked((int)size - headerSize)];
                stream.Position = position + headerSize;
                stream.ReadExactly(payload);
                return payload;
            }
            position += checked((long)size);
        }
        throw new InvalidDataException("The MP4 container has no mdat atom.");
    }
}
