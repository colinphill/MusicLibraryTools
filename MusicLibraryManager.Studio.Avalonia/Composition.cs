using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;
using MusicLibraryManager.Studio.Services;
using LegacyDialogs = MusicLibrary.App.Services.IDialogService;
using LegacyFiles = MusicLibrary.App.Services.IFileDialogService;

namespace MusicLibraryManager.Studio.Avalonia;

public static class Composition
{
    public static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        services.AddSingleton<StudioWindowContext>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IActivityService, AppActivityService>();
        services.AddSingleton<StudioDialogService>();
        services.AddSingleton<IDialogCoordinator>(sp => sp.GetRequiredService<StudioDialogService>());
        services.AddSingleton<IFieldsEditorService>(sp => sp.GetRequiredService<StudioDialogService>());
        services.AddSingleton<StudioFilePickerService>();
        services.AddSingleton<IFilePickerService>(sp => sp.GetRequiredService<StudioFilePickerService>());
        services.AddSingleton<IThumbnailService, StudioThumbnailService>();
        services.AddSingleton<IPlatformService, StudioPlatformService>();
        services.AddSingleton<IWindowStateService, StudioWindowStateService>();
        services.AddSingleton<IThemeService, StudioThemeService>();
        services.AddSingleton<StudioGridStateService>();
        services.AddSingleton<StudioSplitStateService>();
        services.AddSingleton<StudioDropService>();
        services.AddSingleton<LegacyFiles, StudioWorkflowFileService>();
        services.AddSingleton<LegacyDialogs, StudioWorkflowDialogService>();
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
