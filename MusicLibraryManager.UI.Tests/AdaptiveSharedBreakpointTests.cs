using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

/// <summary>
/// Executable boundary evidence for responsive decisions which consume shared
/// symbolic constants instead of embedding a numeric literal in a view.
/// </summary>
public sealed class AdaptiveSharedBreakpointTests
{
    [AvaloniaFact]
    public void
        Library_workspace_resolution_uses_shared_gutters_at_every_boundary()
    {
        using ServiceProvider services =
            Composition.BuildServices(
                collection =>
                    collection.AddSingleton<
                        IAppSettings>(
                        new TestSettings()));
        App.UseServicesForTests(services);
        var library = new LibraryView();
        LibraryViewModel model =
            Assert.IsType<LibraryViewModel>(
                library.DataContext);
        model.SetInspectorPreference(
            LibraryInspectorPreference.Pinned);
        var host = new Border
        {
            Width = 1_200,
            Height = 760,
            HorizontalAlignment =
                HorizontalAlignment.Left,
            VerticalAlignment =
                VerticalAlignment.Top,
            Child = library,
        };
        var window = new Window
        {
            Width = 1_600,
            Height = 1_000,
            Content = host,
        };
        try
        {
            window.Show();
            Render();

            AssertGutter(
                AdaptivePage.NarrowContentThreshold -
                1,
                AdaptivePage.CompactHeightThreshold +
                1,
                AdaptivePage.NarrowGutter);
            AssertGutter(
                AdaptivePage.NarrowContentThreshold,
                AdaptivePage.CompactHeightThreshold +
                1,
                AdaptivePage.WideGutter);
            AssertGutter(
                AdaptivePage.NarrowContentThreshold +
                1,
                AdaptivePage.CompactHeightThreshold +
                1,
                AdaptivePage.WideGutter);

            AssertGutter(
                AdaptivePage.NarrowContentThreshold +
                1,
                AdaptivePage.CompactHeightThreshold -
                1,
                AdaptivePage.CompactHeightGutter);
            AssertGutter(
                AdaptivePage.NarrowContentThreshold +
                1,
                AdaptivePage.CompactHeightThreshold,
                AdaptivePage.CompactHeightGutter);
            AssertGutter(
                AdaptivePage.NarrowContentThreshold +
                1,
                AdaptivePage.CompactHeightThreshold +
                1,
                AdaptivePage.WideGutter);

            // The workspace comparison owns the compact/docked transition;
            // it delegates that presentation change to PersistedSplitView.
            AssertDocking(
                1_137,
                docked: false);
            AssertDocking(
                1_138,
                docked: true);
            AssertDocking(
                1_139,
                docked: true);
        }
        finally
        {
            window.Hide();
        }

        void AssertGutter(
            double width,
            double height,
            double expectedGutter)
        {
            Resize(
                width,
                height);
            PersistedSplitView split =
                library.FindControl<
                    PersistedSplitView>(
                    "WorkspaceSplit")!;
            Assert.Equal(
                width - expectedGutter * 2,
                split.Bounds.Width,
                precision: 2);
        }

        void AssertDocking(
            double width,
            bool docked)
        {
            Resize(
                width,
                760);
            PersistedSplitView split =
                library.FindControl<
                    PersistedSplitView>(
                    "WorkspaceSplit")!;
            GridSplitter splitter =
                split.FindControl<GridSplitter>(
                    "Splitter")!;
            ContentPresenter inspector =
                split.FindControl<
                    ContentPresenter>(
                    "RightPresenter")!;
            Assert.Equal(
                docked,
                splitter.IsVisible);
            Assert.Equal(
                docked,
                inspector.IsVisible);
        }

        void Resize(
            double width,
            double height)
        {
            host.Width = width;
            host.Height = height;
            Render();
            Assert.Equal(
                width,
                library.Bounds.Width,
                precision: 2);
            Assert.Equal(
                height,
                library.Bounds.Height,
                precision: 2);
        }
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class TestSettings :
        IAppSettings
    {
        private readonly Dictionary<string, string>
            _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration?
            Configuration => null;
        public event EventHandler?
            ConfigurationChanged;

        public AppConfigurationSnapshot
            GetSnapshot() =>
            new(null, null, 0);

        public void LoadConfig(string path) =>
            ConfigurationChanged?.Invoke(
                this,
                EventArgs.Empty);

        public string?
            GetRememberedConfigPath() =>
            null;

        public IReadOnlyList<string>
            RecentConfigPaths => [];

        public void ClearRecentConfigs()
        {
        }

        public string? GetPreference(
            string key) =>
            _preferences.GetValueOrDefault(
                key);

        public void SetPreference(
            string key,
            string? value)
        {
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] = value;
        }
    }
}
