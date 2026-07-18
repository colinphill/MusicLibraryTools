using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;
using MusicLibraryManager.Studio.Services;
using LegacyDialogs = MusicLibrary.App.Services.IDialogService;
using LegacyFiles = MusicLibrary.App.Services.IFileDialogService;

namespace MusicLibraryManager.Studio;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            Services = BuildServices();
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            GetService<WorkflowIntegrationService>().Start();

            var window = new MainWindow();
            GetService<StudioWindowContext>().Window = window;
            MainWindow = window;
            GetService<IThemeService>().Apply(GetService<SettingsViewModel>().SelectedTheme);
            window.Show();
            GetService<ShellViewModel>().RestoreConfiguration();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), error.ToString());
            MessageBox.Show(error.Message, "Music Library Manager — Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
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
        return services.BuildServiceProvider();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = GetService<StudioDialogService>().ShowMessageAsync("Unexpected error", e.Exception.Message);
    }
}
