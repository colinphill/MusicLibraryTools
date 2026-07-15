using MusicLibrary.App.Services;
using MusicLibrary.Core.Services;
using Avalonia;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class WorkspaceStateServiceTests
{
    [Fact]
    public void WindowState_RoundTripsAcrossSettingsInstances()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "settings.json");
        var expected = new WindowWorkspaceState(
            WorkspaceStateService.CurrentVersion, 1440, 900, -1200, 80, true);
        new WorkspaceStateService(new AppSettings(path)).SaveWindowState(expected);

        var restored = new WorkspaceStateService(new AppSettings(path)).LoadWindowState();

        Assert.Equal(expected, restored);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"Version\":2,\"Width\":1200,\"Height\":800,\"X\":0,\"Y\":0,\"Maximized\":false}")]
    [InlineData("{\"Version\":1,\"Width\":10,\"Height\":10,\"X\":0,\"Y\":0,\"Maximized\":false}")]
    public void WindowState_IgnoresCorruptObsoleteOrUnsafeValues(string json)
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        settings.SetPreference("workspace.window.v1", json);

        Assert.Null(new WorkspaceStateService(settings).LoadWindowState());
    }

    [Fact]
    public void Placement_RecentersAnOffscreenWindow()
    {
        var state = new WindowWorkspaceState(1, 1200, 800, 5000, 4000, false);

        var placement = WindowWorkspacePlacementCalculator.Fit(
            state, new PixelRect(0, 0, 1920, 1080), 1, 760, 500);

        Assert.Equal(1200, placement.Width);
        Assert.Equal(800, placement.Height);
        Assert.Equal(360, placement.X);
        Assert.Equal(140, placement.Y);
    }

    [Fact]
    public void Placement_PreservesAVisibleNegativeSecondaryMonitorPosition()
    {
        var state = new WindowWorkspaceState(1, 1000, 700, -1800, 100, false);
        var area = new PixelRect(-1920, 0, 1920, 1080);

        var placement = WindowWorkspacePlacementCalculator.Fit(state, area, 1, 760, 500);

        Assert.True(WindowWorkspacePlacementCalculator.IsMeaningfullyVisible(state, area, 1));
        Assert.Equal(-1800, placement.X);
        Assert.Equal(100, placement.Y);
    }

    [Fact]
    public void Placement_ClampsLogicalSizeUsingScreenScaling()
    {
        var state = new WindowWorkspaceState(1, 1600, 1000, 100, 100, false);

        var placement = WindowWorkspacePlacementCalculator.Fit(
            state, new PixelRect(0, 0, 1920, 1080), 1.5, 760, 500);

        Assert.Equal(1280, placement.Width);
        Assert.Equal(720, placement.Height);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "workspace-state-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
