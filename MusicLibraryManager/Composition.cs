using Microsoft.Extensions.DependencyInjection;
using MusicLibraryManager.Presentation;
using MusicLibrary.Core;
using MusicLibraryManager.Services;
using LegacyDialogs = MusicLibraryManager.Presentation.IDialogService;
using LegacyFiles = MusicLibraryManager.Presentation.IFileDialogService;

namespace MusicLibraryManager;

public static class Composition
{
    public static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        services.AddSingleton<WindowContext>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IActivityService, AppActivityService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogCoordinator>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<IFieldsEditorService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<FilePickerService>();
        services.AddSingleton<IFilePickerService>(sp => sp.GetRequiredService<FilePickerService>());
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<IPlatformService, PlatformService>();
        services.AddSingleton<IWindowStateService, WindowStateService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<GridStateService>();
        services.AddSingleton<SplitStateService>();
        services.AddSingleton<DropService>();
        services.AddSingleton<LegacyFiles, WorkflowFileService>();
        services.AddSingleton<LegacyDialogs, WorkflowDialogService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IndexingViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SelectionInspectorViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AnalyzerViewModel>();
        services.AddSingleton<IngestViewModel>();
        services.AddSingleton<OrganizeViewModel>();
        services.AddSingleton<OperationsViewModel>();
        services.AddSingleton<WorkflowIntegrationService>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }
}
