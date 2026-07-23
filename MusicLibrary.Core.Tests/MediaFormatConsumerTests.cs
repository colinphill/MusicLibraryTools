using MusicFileUtilities;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class MediaFormatConsumerTests
{
    [Theory]
    [InlineData("sample.dsf")]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.ogg")]
    [InlineData("sample.wv")]
    [InlineData("sample_aac.m4a")]
    [InlineData("sample_alac.m4a")]
    [InlineData("sample.ape")]
    [InlineData("sample.mpc")]
    [InlineData("sample.tta")]
    [InlineData("sample.tak")]
    [InlineData("sample.ofr")]
    [InlineData("sample.ofs")]
    [InlineData("sample.off")]
    [InlineData("sample.wma")]
    [InlineData("sample.mka")]
    public async Task MediaModelWritabilityComesFromRegistry(string fixture)
    {
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;
        string path = MediaFixtures.Path_(fixture);
        var service = new MediaFileService(formats: registry);

        var result = await service.LoadDirectAsync(path, includeArtwork: false);

        Assert.True(result.Success, result.Error);
        Assert.Equal(
            registry.SupportsPath(path, MediaFormatCapabilities.WriteMetadata),
            result.Value!.IsWritable);
    }

    [Fact]
    public void ArtworkServiceUsesInjectedRegistryCapabilities()
    {
        var registry = new MediaFormatRegistry(
        [
            new(".art", "Artwork test", MediaFormatFamily.Flac,
                MediaFormatCapabilities.ReadArtwork | MediaFormatCapabilities.WriteArtwork),
            new(".read", "Read-only test", MediaFormatFamily.Flac,
                MediaFormatCapabilities.ReadArtwork),
        ]);
        var service = new ArtworkService(formats: registry);

        Assert.True(service.SupportsWrite("cover.ART"));
        Assert.False(service.SupportsWrite("cover.read"));
        Assert.False(service.SupportsWrite("cover.flac"));
    }

    [Fact]
    public void ArtworkAndMetadataConsumersAgreeWithDefaultRegistry()
    {
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;
        var artwork = new ArtworkService(formats: registry);

        foreach (MediaFormatDefinition format in registry.Formats)
        {
            string path = "track" + format.Extension;
            Assert.Equal(
                format.Supports(MediaFormatCapabilities.WriteArtwork),
                artwork.SupportsWrite(path));
        }
        Assert.False(artwork.SupportsWrite("track.unknown"));
    }
}
