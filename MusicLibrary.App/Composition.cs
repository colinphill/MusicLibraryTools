using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.App.Services;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core;

namespace MusicLibrary.App;

/// <summary>
/// The application composition root. Builds the DI container that wires the Core service layer,
/// app-level services, and ViewModels together.
/// </summary>
public static class Composition
{
    public static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddMusicLibraryCore();

        // App-level services.
        services.AddSingleton<FileDialogService>();
        services.AddSingleton<IFileDialogService>(sp => sp.GetRequiredService<FileDialogService>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<IThumbnailProvider, ThumbnailProvider>();
        services.AddSingleton<IWorkspaceStateService, WorkspaceStateService>();

        // ViewModels.
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<FileInspectorViewModel>();
        services.AddSingleton<TagEditorViewModel>();
        services.AddSingleton<AnalyzerViewModel>();
        services.AddSingleton<OrganizeViewModel>();
        services.AddSingleton<IngestViewModel>();
        services.AddSingleton<ArtworkViewModel>();
        services.AddSingleton<DetailsGridViewModel>();
        services.AddSingleton<OperationsViewModel>();

        return services.BuildServiceProvider();
    }
}
