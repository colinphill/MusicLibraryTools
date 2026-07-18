using global::Avalonia;
using global::Avalonia.Controls.ApplicationLifetimes;
using global::Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;
using MusicLibraryManager.Studio.Services;

namespace MusicLibraryManager.Studio.Avalonia;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();
    internal static void UseServicesForTests(IServiceProvider services) => Services = services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var provider = Composition.BuildServices();
                Services = provider;
                var window = provider.GetRequiredService<MainWindow>();
                provider.GetRequiredService<StudioWindowContext>().Window = window;
                provider.GetRequiredService<WorkflowIntegrationService>().Start();
                provider.GetRequiredService<IThemeService>().Apply(
                    provider.GetRequiredService<SettingsViewModel>().SelectedTheme);
                desktop.MainWindow = window;
                desktop.Exit += (_, _) => provider.Dispose();
                window.Opened += (_, _) => provider.GetRequiredService<ShellViewModel>().RestoreConfiguration();
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), error.ToString());
                desktop.Shutdown(1);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
