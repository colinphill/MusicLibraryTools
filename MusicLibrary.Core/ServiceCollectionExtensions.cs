using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Services;

namespace MusicLibrary.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Core service layer. Call from the app's composition root.</summary>
    public static IServiceCollection AddMusicLibraryCore(this IServiceCollection services)
    {
        services.AddSingleton<IAppSettings, AppSettings>();
        services.AddSingleton<IMediaFileService, MediaFileService>();
        services.AddSingleton<ITagWriteService, TagWriteService>();
        services.AddSingleton<IArtworkService, ArtworkService>();
        services.AddSingleton<IArtistReconciler, ArtistReconciler>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryService>(sp => sp.GetRequiredService<LibraryService>());
        services.AddSingleton<ILibraryOrganizer>(sp => sp.GetRequiredService<LibraryService>());
        return services;
    }
}
