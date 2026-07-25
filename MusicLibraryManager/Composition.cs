using Microsoft.Extensions.DependencyInjection;
using MusicLibraryManager.Presentation;
using MusicLibrary.Core;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Services;
using LegacyDialogs = MusicLibraryManager.Presentation.IDialogService;
using LegacyFiles = MusicLibraryManager.Presentation.IFileDialogService;

namespace MusicLibraryManager;

public static class Composition
{
    public static ServiceProvider BuildServices(Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        services.AddSingleton<WindowContext>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IActivityService>(sp =>
            new AppActivityService(
                sp.GetRequiredService<
                    ILocalizationService>(),
                SynchronizationContext.Current));
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogCoordinator>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<IFieldsEditorService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<FilePickerService>();
        services.AddSingleton<IFilePickerService>(sp => sp.GetRequiredService<FilePickerService>());
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<IPlatformService, PlatformService>();
        services.AddSingleton<IWindowStateService, WindowStateService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ILocalizationService>(sp =>
            new ResourceLocalizationService(
                sp.GetRequiredService<IAppSettings>()));
        services.AddSingleton<
            AvaloniaLocalizationResourceBridge>();
        services.AddSingleton<GridStateService>();
        services.AddSingleton<SplitStateService>();
        services.AddSingleton<DropService>();
        services.AddSingleton<LegacyFiles, WorkflowFileService>();
        services.AddSingleton<LegacyDialogs, WorkflowDialogService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IndexingViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SelectionInspectorViewModel>();
        services.AddSingleton<WorkbenchSelectionInspectorViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<WorkbenchViewModel>();
        services.AddSingleton<
            IWorkbenchPendingChangeCoordinator>(
            sp => sp.GetRequiredService<
                WorkbenchViewModel>());
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AnalyzerViewModel>();
        services.AddSingleton<IngestViewModel>();
        services.AddSingleton<IIngestSourceHandoff>(
            sp => sp.GetRequiredService<IngestViewModel>());
        services.AddSingleton<OrganizeViewModel>();
        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<OperationsViewModel>();
        services.AddSingleton<WorkflowIntegrationService>();
        services.AddSingleton<MainWindow>();
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
