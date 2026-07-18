using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(MusicLibraryManager.UI.Tests.TestAppBuilder))]

namespace MusicLibraryManager.UI.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<MusicLibraryManager.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
            ShouldRenderOnUIThread = true,
        })
        .UseSkia();
}
