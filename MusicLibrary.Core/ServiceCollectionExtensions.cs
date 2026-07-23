using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.Core.Services;

namespace MusicLibrary.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Core service layer. Call from the app's composition root.</summary>
    public static IServiceCollection AddMusicLibraryCore(this IServiceCollection services)
    {
        services.AddSingleton<IMediaFormatRegistry>(MediaFormatRegistry.Default);
        foreach (IHealthRule rule in BuiltInHealthRules.All)
            services.AddSingleton(rule);
        services.AddSingleton<ILibraryHealthPolicyService, LibraryHealthPolicyService>();
        services.AddSingleton<IAppSettings, AppSettings>();
        services.AddSingleton<IFileMutationCoordinator, FileMutationCoordinator>();
        services.AddSingleton<IItunesMediaMutationService, ItunesMediaMutationService>();
        services.AddSingleton<IMediaCatalogIntegration, ItunesMediaCatalogIntegration>();
        services.AddSingleton<IFileInventoryService, FileInventoryService>();
        services.AddSingleton<IFileMutationPlanExecutor, FileMutationPlanExecutor>();
        services.AddSingleton<IMetadataDocumentService, MetadataDocumentService>();
        services.AddSingleton<IWorkbenchService, WorkbenchService>();
        services.AddSingleton<IMetadataOperationCatalog, MetadataOperationCatalog>();
        services.AddSingleton<IOperationRecipeStore, OperationRecipeStore>();
        services.AddSingleton<IEditHistoryService, EditHistoryService>();
        services.AddSingleton<IMetadataOperationService, MetadataOperationService>();
        services.AddSingleton<IExportTransport, LocalFileSystemExportTransport>();
        services.AddSingleton<IConfiguredExportService, ConfiguredExportService>();
        services.AddSingleton<ILibraryOperationContextFactory, LibraryOperationContextFactory>();
        services.AddSingleton<IItlMetadataRepairService>(sp => new ItlMetadataRepairService(
            sp.GetRequiredService<ILibraryOperationContextFactory>(),
            sp.GetRequiredService<IAppSettings>()));
        services.AddSingleton<ICrossLibrarySyncService, CrossLibrarySyncService>();
        services.AddSingleton<IPlaylistWriter, M3uPlaylistWriter>();
        services.AddSingleton<IPlaylistWriter, WplPlaylistWriter>();
        services.AddSingleton<IPlaylistExportService, PlaylistExportService>();
        services.AddSingleton<IPlaylistSource, M3uPlaylistSource>();
        services.AddSingleton<IItunesValidationService, ItunesValidationService>();
        services.AddSingleton<IRedundancyAnalysisService, RedundancyAnalysisService>();
        services.AddSingleton<IMediaFileService, MediaFileService>();
        services.AddSingleton<ITagWriteService, TagWriteService>();
        services.AddSingleton<IArtworkService, ArtworkService>();
        services.AddSingleton<IArtworkNormalizationService>(sp =>
            new ArtworkNormalizationService(
                sp.GetRequiredService<IAppSettings>(),
                sp.GetRequiredService<IReindexService>(),
                sp.GetRequiredService<IFileMutationCoordinator>()));
        services.AddSingleton<ISyncerClientAdapter, SyncerClientAdapter>();
        services.AddSingleton<IDeviceSyncService, DeviceSyncService>();
        services.AddSingleton<ISmartStorageLibraryLoader, SmartStorageLibraryLoader>();
        services.AddSingleton<ISmartStorageService, SmartStorageService>();
        services.AddSingleton<ICarCardService, CarCardService>();
        services.AddSingleton<IArtistReconciler, ArtistReconciler>();
        services.AddSingleton<IAnalysisRepairService, AnalysisRepairService>();
        services.AddSingleton<IOperationJournalService, OperationJournalService>();
        foreach (ILibraryOperationProvider provider in BuiltInLibraryOperationProviders.All)
            services.AddSingleton(provider);
        services.AddSingleton<IUnifiedJobService, UnifiedJobService>();
        services.AddSingleton<IIndexBenchmarkService, IndexBenchmarkService>();
        services.AddSingleton<IFfmpegRunner, FfmpegRunner>();
        services.AddSingleton<IWavpackRunner, WavpackRunner>();
        services.AddSingleton<IDecodedAudioVerificationService, DecodedAudioVerificationService>();
        services.AddSingleton<IRepresentationRepairService, RepresentationRepairService>();
        services.AddSingleton<IIngestMusicService, IngestMusicService>();
        services.AddSingleton<IIngestPreflightService, IngestPreflightService>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryService>(sp => sp.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryOrganizer>(sp => sp.GetRequiredService<LibraryService>());
        services.AddSingleton<IReindexService>(sp => sp.GetRequiredService<LibraryService>());
        return services;
    }
}
