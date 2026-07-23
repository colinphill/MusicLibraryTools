using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.Core;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void CoreServicesResolveWithActiveConfigurationDependencies()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(MediaFormatRegistry.Default,
            provider.GetRequiredService<IMediaFormatRegistry>());
        Assert.IsType<LibraryHealthPolicyService>(
            provider.GetRequiredService<ILibraryHealthPolicyService>());
        Assert.Equal(BuiltInHealthRules.All.Count,
            provider.GetServices<IHealthRule>().Count());
        Assert.IsType<IngestMusicService>(provider.GetRequiredService<IIngestMusicService>());
        Assert.IsType<IngestPreflightService>(
            provider.GetRequiredService<IIngestPreflightService>());
        Assert.IsType<WavpackRunner>(provider.GetRequiredService<IWavpackRunner>());
        Assert.IsType<FpcalcRunner>(provider.GetRequiredService<IFpcalcRunner>());
        Assert.IsType<AudioFingerprintService>(
            provider.GetRequiredService<IAudioFingerprintService>());
        Assert.IsType<AcoustIdHttpTransport>(
            provider.GetRequiredService<IAcoustIdHttpTransport>());
        Assert.IsType<AcoustIdLookupService>(
            provider.GetRequiredService<IAcoustIdLookupService>());
        Assert.IsType<AcoustIdDiscoveryService>(
            provider.GetRequiredService<IAcoustIdDiscoveryService>());
        Assert.IsType<MusicBrainzHttpTransport>(
            provider.GetRequiredService<IMusicBrainzHttpTransport>());
        Assert.IsType<MusicBrainzMetadataProvider>(
            provider.GetRequiredService<IMusicBrainzMetadataProvider>());
        Assert.IsType<MusicBrainzReleaseCache>(
            provider.GetRequiredService<IMusicBrainzReleaseCache>());
        Assert.IsType<MusicBrainzReleaseMappingService>(
            provider.GetRequiredService<IMusicBrainzReleaseMappingService>());
        Assert.IsType<CoverArtArchiveProvider>(
            provider.GetRequiredService<ICoverArtArchiveProvider>());
        Assert.IsType<ArtworkDownloadCache>(
            provider.GetRequiredService<IArtworkDownloadCache>());
        IMetadataSourceCatalog metadataSources =
            provider.GetRequiredService<IMetadataSourceCatalog>();
        Assert.Equal(
            ["cover-art-archive", "musicbrainz"],
            metadataSources.Providers.Select(source => source.Descriptor.Id));
        Assert.Same(
            provider.GetRequiredService<IMusicBrainzMetadataProvider>(),
            metadataSources.Find("MUSICBRAINZ"));
        Assert.Same(
            provider.GetRequiredService<ICoverArtArchiveProvider>(),
            metadataSources.Find("cover-art-archive"));
        Assert.IsType<LibraryOperationContextFactory>(
            provider.GetRequiredService<ILibraryOperationContextFactory>());
        Assert.IsType<ItlMetadataRepairService>(
            provider.GetRequiredService<IItlMetadataRepairService>());
        Assert.IsType<M3uPlaylistSource>(
            provider.GetRequiredService<IPlaylistSource>());
        Assert.IsType<ItunesMediaCatalogIntegration>(
            provider.GetRequiredService<IMediaCatalogIntegration>());
        Assert.IsType<LocalFileSystemExportTransport>(
            provider.GetRequiredService<IExportTransport>());
        Assert.IsType<MetadataDocumentService>(
            provider.GetRequiredService<IMetadataDocumentService>());
        Assert.IsType<WorkbenchService>(
            provider.GetRequiredService<IWorkbenchService>());
        Assert.IsType<MetadataOperationCatalog>(
            provider.GetRequiredService<IMetadataOperationCatalog>());
        Assert.IsType<OperationRecipeStore>(
            provider.GetRequiredService<IOperationRecipeStore>());
        Assert.IsType<MetadataOperationService>(
            provider.GetRequiredService<IMetadataOperationService>());
        Assert.IsType<EditHistoryService>(
            provider.GetRequiredService<IEditHistoryService>());
        Assert.Equal(BuiltInLibraryOperationProviders.All.Count,
            provider.GetServices<ILibraryOperationProvider>().Count());
        Assert.Equal(["itunes-validation"],
            provider.GetRequiredService<IUnifiedJobService>().Catalog.Select(job => job.Id));
    }
}
