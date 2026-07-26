using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class
    WorkbenchDrawerAndEmptyStateUiTests
{
    [AvaloniaFact]
    public void Output_and_automate_sections_remove_the_inspector_command_and_restore_its_preference()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    1800,
                    900);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            ILocalizationService localization =
                services.GetRequiredService<
                    ILocalizationService>();
            Button toggle =
                view.FindControl<Button>(
                    "WorkbenchInspectorToggle")!;
            MenuFlyout more =
                Assert.IsType<MenuFlyout>(
                    view.FindControl<Button>(
                            "WorkbenchMoreButton")!
                        .Flyout);
            MenuItem menuInspector =
                Assert.IsType<MenuItem>(
                    more.Items[0]);
            Control drawer =
                view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!;

            Assert.True(model.IsInspectorOpen);
            Assert.True(drawer.IsVisible);
            Assert.True(toggle.IsVisible);
            Assert.True(toggle.IsEnabled);
            Assert.True(menuInspector.IsVisible);
            Assert.True(menuInspector.IsEnabled);
            Assert.Equal(
                localization.Get(
                    "Library.Action.InspectorTooltip"),
                ToolTip.GetTip(toggle)?.ToString());

            foreach (WorkbenchSection section in
                     new[]
                     {
                         WorkbenchSection.Reports,
                         WorkbenchSection.Playlists,
                         WorkbenchSection.Tools,
                         WorkbenchSection.Shortcuts,
                     })
            {
                model.SelectedSection = section;
                Render();

                Assert.True(
                    model.IsInspectorOpen);
                Assert.False(drawer.IsVisible);
                Assert.False(toggle.IsVisible);
                Assert.False(toggle.IsEnabled);
                Assert.False(
                    menuInspector.IsVisible);
                Assert.False(
                    menuInspector.IsEnabled);
            }

            model.SelectedSection =
                WorkbenchSection.AllFields;
            Render();

            Assert.True(model.IsInspectorOpen);
            Assert.True(drawer.IsVisible);
            Assert.True(toggle.IsVisible);
            Assert.True(toggle.IsEnabled);
            Assert.True(menuInspector.IsVisible);
            Assert.True(menuInspector.IsEnabled);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void All_fields_and_file_operations_show_reachable_localized_source_setup_states()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    900,
                    600);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            ILocalizationService localization =
                services.GetRequiredService<
                    ILocalizationService>();

            AssertSetupState(
                WorkbenchSection.AllFields,
                "AllFieldsEmptyState",
                "AllFieldsEmptyContext",
                "AllFieldsEmptyTitle",
                "AllFieldsEmptyDescription",
                "AllFieldsAddFilesButton",
                "Workbench.AllFields.Title");
            Assert.False(
                view.FindControl<Button>(
                        "AllFieldsNewButton")!
                    .IsEffectivelyEnabled);
            AssertSetupState(
                WorkbenchSection.Files,
                "FileOperationsEmptyState",
                "FileOperationsEmptyContext",
                "FileOperationsEmptyTitle",
                "FileOperationsEmptyDescription",
                "FileOperationsAddFilesButton",
                "Workbench.Section.FilesAutomation");

            void AssertSetupState(
                WorkbenchSection section,
                string stateName,
                string contextName,
                string titleName,
                string descriptionName,
                string actionName,
                string contextResource)
            {
                model.SelectedSection = section;
                Render();

                Border state =
                    view.FindControl<Border>(
                        stateName)!;
                TextBlock context =
                    view.FindControl<TextBlock>(
                        contextName)!;
                TextBlock title =
                    view.FindControl<TextBlock>(
                        titleName)!;
                TextBlock description =
                    view.FindControl<TextBlock>(
                        descriptionName)!;
                Button action =
                    view.FindControl<Button>(
                        actionName)!;
                Assert.True(
                    state.IsEffectivelyVisible);
                Assert.Equal(
                    localization.Get(
                        contextResource),
                    context.Text);
                Assert.Equal(
                    localization.Get(
                        "Workbench.Session.EmptyTitle"),
                    title.Text);
                Assert.Equal(
                    localization.Get(
                        "Workbench.Session.EmptyDescription"),
                    description.Text);
                Assert.Equal(
                    localization.Get(
                        "Workbench.Action.AddFiles"),
                    action.Content?.ToString());
                Assert.True(
                    action.IsEffectivelyVisible);
                Assert.True(
                    action.IsEffectivelyEnabled);
                Assert.True(
                    state.Bounds.Width <=
                    view.Bounds.Width + 1);
                Assert.True(
                    state.Bounds.Height <=
                    view.Bounds.Height + 1);
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Workbench_inspector_keeps_one_scroll_owner_and_its_footer_reachable_while_scrolling()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            WorkbenchTrackViewModel track =
                Track("inspector-scroll.flac");
            model.Files.Add(track);
            model.SelectedFile = track;
            model.SetSelectedFiles([track]);
            await model.Inspector!.LoadAsync(
                new SelectionContext(
                    [track.Path],
                    ReadArtworkDirectly: false));

            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    1440,
                    700);
            Control drawer =
                view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!;
            ScrollViewer scroll =
                view.FindControl<ScrollViewer>(
                    "InspectorContent")!;
            Border footer =
                view.FindControl<Border>(
                    "InspectorStickyFooter")!;
            Button review =
                view.FindControl<Button>(
                    "InspectorReviewChangesButton")!;
            Button discard =
                view.FindControl<Button>(
                    "InspectorDiscardEditsButton")!;

            Assert.True(drawer.IsVisible);
            Assert.True(scroll.IsEffectivelyVisible);
            Assert.True(footer.IsEffectivelyVisible);
            Assert.True(review.IsEffectivelyVisible);
            Assert.True(discard.IsEffectivelyVisible);
            Assert.Single(
                drawer
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>(),
                candidate =>
                    candidate.Name ==
                    "InspectorContent");

            Point footerTopBefore =
                footer.TranslatePoint(
                    new Point(0, 0),
                    drawer) ??
                throw new InvalidOperationException(
                    "The Inspector footer was not attached.");
            scroll.Offset =
                new Vector(
                    0,
                    Math.Max(
                        0,
                        scroll.Extent.Height -
                        scroll.Viewport.Height));
            Render();
            Point footerTopAfter =
                footer.TranslatePoint(
                    new Point(0, 0),
                    drawer) ??
                throw new InvalidOperationException(
                    "The Inspector footer was not attached after scrolling.");
            Point footerBottom =
                footer.TranslatePoint(
                    new Point(
                        0,
                        footer.Bounds.Height),
                    drawer) ??
                throw new InvalidOperationException(
                    "The Inspector footer bounds were unavailable.");

            Assert.InRange(
                Math.Abs(
                    footerTopAfter.Y -
                    footerTopBefore.Y),
                0,
                0.5);
            Assert.InRange(
                footerBottom.Y,
                0,
                drawer.Bounds.Height + 1);
        }
        finally
        {
            window.Hide();
        }
    }

    private static WorkbenchView ShowWorkbench(
        MainWindow window,
        IServiceProvider services,
        double width,
        double height)
    {
        window.Show();
        window.WindowState =
            WindowState.Normal;
        window.Width = width;
        window.Height = height;
        services
            .GetRequiredService<
                INavigationService>()
            .Navigate(
                ShellDestination.Workbench);
        Render();
        return Assert.IsType<WorkbenchView>(
            window.FindControl<ContentControl>(
                "ContentHost")!.Content);
    }

    private static WorkbenchTrackViewModel Track(
        string path)
    {
        string fullPath =
            Path.GetFullPath(path);
        return new(
            new MediaDocument(
                fullPath,
                [],
                [],
                null,
                new(
                    fullPath,
                    10,
                    DateTime.UtcNow,
                    "hash"),
                true));
    }

    private static ServiceProvider
        BuildServices()
    {
        var settings =
            new TestSettings();
        settings.SetPreference(
            AppearancePreferences
                .ShellRailExpandedPreference,
            bool.FalseString);
        return Composition.BuildServices(
            services =>
            {
                services.AddSingleton<
                    IAppSettings>(
                    settings);
                services.AddSingleton<
                    ILocalizationService>(
                    new ResourceLocalizationService(
                        settings));
            });
    }

    private static void Render()
    {
        Avalonia.Threading.Dispatcher
            .UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Avalonia.Threading.Dispatcher
            .UIThread.RunJobs();
    }

    private sealed class TestSettings :
        IAppSettings
    {
        private readonly Dictionary<
            string,
            string> _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration?
            Configuration => null;
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
            _preferences
                .GetValueOrDefault(key);

        public void SetPreference(
            string key,
            string? value)
        {
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] =
                    value;
        }
    }
}
