using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.App.Services;
using MusicLibrary.App.ViewModels;
using MusicLibrary.App.Views;

namespace MusicLibrary.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = Composition.BuildServiceProvider();

        // Wire the details-grid thumbnail loader (a static attached property) to its provider.
        Views.ThumbnailLoader.Init(Services.GetRequiredService<IThumbnailProvider>());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };

            // Give the file-dialog and dialog services a window to attach to.
            Services.GetRequiredService<FileDialogService>().Owner = window;
            Services.GetRequiredService<DialogService>().Owner = window;

            window.Opened += (_, _) => vm.OnLoaded();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
