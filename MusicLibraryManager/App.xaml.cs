using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;

namespace MusicLibraryManager;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Services = BuildServices();
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        GetService<WorkflowIntegrationService>().Start();

        var window = new MainWindow();
        GetService<WindowContext>().Window = window;
        MainWindow = window;
        GetService<IThemeService>().Apply(GetService<SettingsViewModel>().SelectedTheme);
        window.Show();
        GetService<ShellViewModel>().RestoreConfiguration();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (Services as IDisposable)?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        services.AddSingleton<WindowContext>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IActivityService, AppActivityService>();
        services.AddSingleton<IFilePickerService, WpfFilePickerService>();
        services.AddSingleton<IDialogCoordinator, WpfDialogCoordinator>();
        services.AddSingleton<IFieldsEditorService, WpfFieldsEditorService>();
        services.AddSingleton<IThumbnailService, WpfThumbnailService>();
        services.AddSingleton<IPlatformService, WindowsPlatformService>();
        services.AddSingleton<IWindowStateService, SettingsWindowStateService>();
        services.AddSingleton<IThemeService, WpfThemeService>();
        services.AddSingleton<MusicLibrary.App.Services.IFileDialogService, WorkflowFileDialogService>();
        services.AddSingleton<MusicLibrary.App.Services.IDialogService, WorkflowDialogService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IndexingViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SelectionInspectorViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MusicLibrary.App.ViewModels.AnalyzerViewModel>();
        services.AddSingleton<MusicLibrary.App.ViewModels.IngestViewModel>();
        services.AddSingleton<MusicLibrary.App.ViewModels.OrganizeViewModel>();
        services.AddSingleton<MusicLibrary.App.ViewModels.OperationsViewModel>();
        services.AddSingleton<WorkflowIntegrationService>();
        return services.BuildServiceProvider();
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Music Library Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
