using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Services;

namespace MusicLibrary.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Core service layer. Call from the app's composition root.</summary>
    public static IServiceCollection AddMusicLibraryCore(this IServiceCollection services)
    {
        services.AddSingleton<IAppSettings, AppSettings>();
        services.AddSingleton<IFileMutationCoordinator, FileMutationCoordinator>();
        services.AddSingleton<IItunesMediaMutationService, ItunesMediaMutationService>();
        services.AddSingleton<IFileInventoryService, FileInventoryService>();
        services.AddSingleton<IFileMutationPlanExecutor, FileMutationPlanExecutor>();
        services.AddSingleton<ILibraryOperationContextFactory, LibraryOperationContextFactory>();
        services.AddSingleton<ICrossLibrarySyncService, CrossLibrarySyncService>();
        services.AddSingleton<IPlaylistExportService, PlaylistExportService>();
        services.AddSingleton<IItunesValidationService, ItunesValidationService>();
        services.AddSingleton<IRedundancyAnalysisService, RedundancyAnalysisService>();
        services.AddSingleton<IMediaFileService, MediaFileService>();
        services.AddSingleton<ITagWriteService, TagWriteService>();
        services.AddSingleton<IArtworkService, ArtworkService>();
        services.AddSingleton<IArtworkNormalizationService, ArtworkNormalizationService>();
        services.AddSingleton<IFileTreeEndpointFactory, FileTreeEndpointFactory>();
        services.AddSingleton<IDeviceSyncService, DeviceSyncService>();
        services.AddSingleton<ISmartStorageLibraryLoader, SmartStorageLibraryLoader>();
        services.AddSingleton<ISmartStorageService, SmartStorageService>();
        services.AddSingleton<ICarCardService, CarCardService>();
        services.AddSingleton<IArtistReconciler, ArtistReconciler>();
        services.AddSingleton<IAnalysisRepairService, AnalysisRepairService>();
        services.AddSingleton<IOperationJournalService, OperationJournalService>();
        services.AddSingleton<IUnifiedJobService, UnifiedJobService>();
        services.AddSingleton<IIndexBenchmarkService, IndexBenchmarkService>();
        services.AddSingleton<IFfmpegRunner, FfmpegRunner>();
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
