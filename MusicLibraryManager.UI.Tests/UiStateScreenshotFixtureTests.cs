using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class UiStateScreenshotFixtureTests
{
    private const int FixtureWidth = 1200;
    private const int FixtureHeight = 700;

    [AvaloniaFact]
    public async Task Workbench_representative_states_render_without_clipped_actions_or_page_overflow()
    {
        var settings = new MemorySettings();
        settings.SetPreference(
            LocalizationPreferences.DisplayLanguage,
            "en-US");
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    settings);
                collection.AddSingleton<
                    IWorkbenchService,
                    TestWorkbenchService>();
            });
        App.UseServicesForTests(services);

        ThemeVariant? previousTheme =
            Application.Current!
                .RequestedThemeVariant;
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        try
        {
            Application.Current
                .RequestedThemeVariant =
                ThemeVariant.Dark;
            window.Width = FixtureWidth;
            window.Height = FixtureHeight;
            window.WindowState =
                WindowState.Normal;
            window.Show();
            window.Activate();
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            Render();

            WorkbenchView view =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            model.SelectedSection =
                WorkbenchSection.Session;

            await view.AddDroppedSourcesAsync(
                [Path.GetFullPath(
                    @"X:\Fixture\Selected Fixture.flac")]);
            WorkbenchTrackViewModel track =
                Assert.Single(model.Files);
            model.SetSelectedFiles(
                [track]);
            Render();

            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "WorkbenchGrid")!;
            grid.SelectedItems.Add(track);
            Render();
            Assert.Contains(
                track,
                grid.SelectedItems.Cast<
                    WorkbenchTrackViewModel>());
            Assert.DoesNotContain(
                view.GetVisualDescendants()
                    .OfType<Border>(),
                border =>
                    border.Classes.Contains(
                        "empty-state") &&
                    border
                        .IsEffectivelyVisible);
            CaptureState(
                window,
                view,
                "selected-session-row");

            track.Title =
                "Intermediate fixture title";
            track.Title =
                "Pending fixture title";
            Render();
            Assert.NotEmpty(
                model.PendingChanges);
            Assert.Contains(
                "primary",
                view.FindControl<Button>(
                        "WorkbenchPendingChangesButton")!
                    .Classes);
            CaptureState(
                window,
                view,
                "dirty-pending-changes");

            view.FindControl<Button>(
                    "WorkbenchPendingChangesButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Render();
            Assert.True(
                view.FindControl<Control>(
                        "WorkbenchPendingChangesDrawer")!
                    .IsEffectivelyVisible);
            Assert.True(
                view.FindControl<Border>(
                        "WorkbenchDrawerPane")!
                    .Bounds.Width <=
                430);
            CaptureState(
                window,
                view,
                "pending-review-drawer-open");

            view.FindControl<Button>(
                    "WorkbenchPendingChangesCloseButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            model.SelectedSection =
                WorkbenchSection.Shortcuts;
            Render();
            view.FindControl<Button>(
                    "NewShortcutEmptyButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Render();
            model.ShortcutEditor.GestureText =
                "Ctrl+";
            Render();
            ScrollViewer shortcutEditor =
                view.FindControl<ScrollViewer>(
                    "EditorScroll")!;
            Assert.True(
                shortcutEditor
                    .IsEffectivelyVisible);
            shortcutEditor.Offset =
                new Vector(
                    0,
                    shortcutEditor
                        .Extent.Height);
            Render();
            Assert.Contains(
                "modifier",
                model.ShortcutEditor
                    .GestureValidationMessage,
                StringComparison.OrdinalIgnoreCase);
            CaptureState(
                window,
                view,
                "validation-error");

            model.SelectedSection =
                WorkbenchSection.Tools;
            model.ExternalToolEditor.Executable =
                Path.GetFullPath(
                    @"X:\Missing\not-installed.exe");
            Render();
            Assert.True(
                model.PreviewExternalToolCommand
                    .CanExecute(null));
            model.PreviewExternalToolCommand
                .Execute(null);
            Render();
            Assert.Contains(
                "blocker",
                model.StatusText,
                StringComparison.OrdinalIgnoreCase);
            CaptureState(
                window,
                view,
                "unavailable-external-tool");

            Button menuLauncher =
                view.FindControl<Button>(
                    "SavedToolMoreButton")!;
            MenuFlyout menu =
                Assert.IsType<MenuFlyout>(
                    menuLauncher.Flyout);
            menu.ShowAt(
                menuLauncher);
            Render();
            Assert.All(
                menu.Items
                    .OfType<MenuItem>(),
                item =>
                    Assert.NotNull(
                        TopLevel.GetTopLevel(
                            item)));
            CaptureState(
                window,
                view,
                "external-tool-menu-open");
            menu.Hide();
        }
        finally
        {
            window.Hide();
            Application.Current
                .RequestedThemeVariant =
                previousTheme;
        }
    }

    private static void CaptureState(
        MainWindow window,
        WorkbenchView view,
        string state)
    {
        Render();
        AssertGeometry(
            view,
            state);

        using var frame =
            window.GetLastRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(
            FixtureWidth,
            frame.PixelSize.Width);
        Assert.Equal(
            FixtureHeight,
            frame.PixelSize.Height);

        string? captureDirectory =
            Environment.GetEnvironmentVariable(
                "MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(
                captureDirectory))
            return;
        Directory.CreateDirectory(
            captureDirectory);
        frame.Save(
            Path.Combine(
                captureDirectory,
                $"ui-state-dark-{FixtureWidth}x{FixtureHeight}-{state}.png"),
            PngBitmapEncoderOptions.Default);
    }

    private static void AssertGeometry(
        WorkbenchView view,
        string state)
    {
        Carousel sections =
            view.FindControl<Carousel>(
                "WorkbenchTabs")!;
        Control active =
            Assert.IsAssignableFrom<Control>(
                sections.SelectedItem);
        Assert.True(
            sections.Bounds.Width <=
            view.Bounds.Width + 1,
            $"{state}: the section host overflowed the Workbench page.");
        Assert.True(
            active.Bounds.Width <=
            sections.Bounds.Width + 1,
            $"{state}: the active section overflowed its host.");

        foreach (ScrollViewer scroll in
                 active.GetVisualDescendants()
                     .OfType<ScrollViewer>()
                     .Where(control =>
                         control
                             .IsEffectivelyVisible)
                     .Where(control =>
                         !control
                             .GetVisualAncestors()
                             .Any(ancestor =>
                                 ancestor is
                                     AppDataGrid or
                                     TextBox or
                                     ComboBox or
                                     ListBox or
                                     NumericUpDown)))
        {
            Assert.True(
                scroll.Extent.Width <=
                scroll.Viewport.Width + 1,
                $"{state}: page-level horizontal overflow was {scroll.Extent.Width:0.0}/{scroll.Viewport.Width:0.0}.");
        }

        foreach (Control action in
                 view.GetVisualDescendants()
                     .OfType<Control>()
                     .Where(control =>
                         control
                             .IsEffectivelyVisible &&
                         (control.Classes.Contains(
                              "primary") ||
                          control.Classes.Contains(
                              "danger")) &&
                         control.Bounds.Width > 0 &&
                         control.Bounds.Height > 0))
        {
            Point? topLeft =
                action.TranslatePoint(
                    new Point(),
                    view);
            Point? bottomRight =
                action.TranslatePoint(
                    new(
                        action.Bounds.Width,
                        action.Bounds.Height),
                    view);
            Assert.NotNull(topLeft);
            Assert.NotNull(bottomRight);
            bool intersectsViewport =
                bottomRight.Value.X >= 0 &&
                bottomRight.Value.Y >= 0 &&
                topLeft.Value.X <=
                view.Bounds.Width &&
                topLeft.Value.Y <=
                view.Bounds.Height;
            if (!intersectsViewport)
                continue;

            string actionName =
                action.Name ??
                action.GetType().Name;
            Assert.True(
                topLeft.Value.X >= -1 &&
                topLeft.Value.Y >= -1 &&
                bottomRight.Value.X <=
                view.Bounds.Width + 1 &&
                bottomRight.Value.Y <=
                view.Bounds.Height + 1,
                $"{state}: {actionName} was clipped ({topLeft.Value.X:0.0},{topLeft.Value.Y:0.0})-({bottomRight.Value.X:0.0},{bottomRight.Value.Y:0.0}) inside {view.Bounds.Width:0.0}x{view.Bounds.Height:0.0}.");
        }
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class TestWorkbenchService :
        IWorkbenchService
    {
        public Task<WorkbenchLoadResult> LoadAsync(
            WorkbenchLoadRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new WorkbenchLoadResult(
                [
                    .. request.Sources.Select(
                        source =>
                        {
                            string path =
                                Path.GetFullPath(
                                    source);
                            return new MediaDocument(
                                path,
                                [],
                                [],
                                null,
                                new(
                                    path,
                                    1024,
                                    new DateTime(
                                        2026,
                                        1,
                                        2,
                                        3,
                                        4,
                                        5,
                                        DateTimeKind.Utc),
                                    "fixture"),
                                true);
                        }),
                ],
                []));
        }
    }

    private sealed class MemorySettings :
        IAppSettings
    {
        private readonly Dictionary<string, string>
            _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration =>
            null;
        public event EventHandler?
            ConfigurationChanged;

        public AppConfigurationSnapshot
            GetSnapshot() =>
            new(null, null, 0);

        public void LoadConfig(
            string path) =>
            ConfigurationChanged?.Invoke(
                this,
                EventArgs.Empty);

        public string? GetRememberedConfigPath() =>
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
