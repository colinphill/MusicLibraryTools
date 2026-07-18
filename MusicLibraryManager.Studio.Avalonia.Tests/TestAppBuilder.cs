using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(MusicLibraryManager.Studio.Avalonia.Tests.TestAppBuilder))]

namespace MusicLibraryManager.Studio.Avalonia.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<MusicLibraryManager.Studio.Avalonia.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
            ShouldRenderOnUIThread = true,
        })
        .UseSkia();
}
