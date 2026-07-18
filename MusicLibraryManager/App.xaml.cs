using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using MusicLibrary.Core;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;

namespace MusicLibraryManager;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services = BuildServices();
            UnhandledException += App_UnhandledException;
            GetService<WorkflowIntegrationService>().Start();

            var window = new MainWindow();
            GetService<WindowContext>().Window = window;
            MainWindow = window;
            GetService<IThemeService>().Apply(GetService<SettingsViewModel>().SelectedTheme);
            window.Activate();
            GetService<ShellViewModel>().RestoreConfiguration();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), error.ToString());
            Environment.Exit(1);
        }
    }

    public static Window? MainWindow { get; private set; }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        services.AddSingleton<WindowContext>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IActivityService, AppActivityService>();
        services.AddSingleton<IFilePickerService, WinUiFilePickerService>();
        services.AddSingleton<IDialogCoordinator, WinUiDialogCoordinator>();
        services.AddSingleton<IFieldsEditorService, WinUiFieldsEditorService>();
        services.AddSingleton<IThumbnailService, WinUiThumbnailService>();
        services.AddSingleton<IPlatformService, WindowsPlatformService>();
        services.AddSingleton<IWindowStateService, SettingsWindowStateService>();
        services.AddSingleton<IThemeService, WinUiThemeService>();
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

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = GetService<IDialogCoordinator>().ShowMessageAsync("Music Library Manager", e.Exception.Message);
    }
}
