using MetadataCaching;
using MusicFileUtilities;
using MusicLibraryTools;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class MediaFormatRegistryTests
{
    [Fact]
    public void DefaultRegistryPreservesLegacyLibraryIndexFormats()
    {
        string[] expected = [".dsf", ".m4a", ".mp3", ".flac", ".ogg", ".wv"];
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;

        Assert.Equal(expected, registry.GetExtensions(MediaFormatCapabilities.LibraryIndex));
        Assert.Equal(expected.Order(), MetadataExtensions.ValidExtensions.Order());
        Assert.Equal(expected.Order(), MetadataCache.ValidExtensions.Order());
    }

    [Fact]
    public void DefaultRegistryIncludesDirectlyEditableMp4VariantsWithoutIndexingThem()
    {
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;

        foreach (string extension in new[] { ".mp4", ".m4p", ".m4r" })
        {
            Assert.True(registry.SupportsExtension(extension, MediaFormatCapabilities.ReadMetadata));
            Assert.True(registry.SupportsExtension(extension, MediaFormatCapabilities.WriteMetadata));
            Assert.True(registry.SupportsExtension(extension, MediaFormatCapabilities.ReadArtwork));
            Assert.True(registry.SupportsExtension(extension, MediaFormatCapabilities.WriteArtwork));
            Assert.False(registry.SupportsExtension(extension, MediaFormatCapabilities.LibraryIndex));
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
}
