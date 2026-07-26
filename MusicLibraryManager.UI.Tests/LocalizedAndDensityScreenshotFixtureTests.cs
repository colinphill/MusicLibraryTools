using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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

public sealed class LocalizedAndDensityScreenshotFixtureTests
{
    [AvaloniaFact]
    public void Remaining_shipping_locales_render_every_workbench_section_at_minimum_size()
    {
        var settings = new MemorySettings();
        settings.SetPreference(
            LocalizationPreferences.DisplayLanguage,
            "en-US");
        settings.SetPreference(
            AppearancePreferences
                .ShellRailExpandedPreference,
            bool.FalseString);
        var localization =
            new ResourceLocalizationService(
                settings);
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    settings);
                collection.AddSingleton<
                    ILocalizationService>(
                    localization);
            });
        App.UseServicesForTests(services);

        ThemeVariant? previousTheme =
            Application.Current!
                .RequestedThemeVariant;
        CultureInfo previousUICulture =
            CultureInfo.CurrentUICulture;
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        string[] cultures =
        [
            "en-US",
            "es-ES",
            "fr-FR",
            "it-IT",
            "pt-BR",
        ];
        try
        {
            window.Width = 900;
            window.Height = 600;
            window.FontSize = 18;
            window.WindowState =
                WindowState.Normal;
            window.Show();
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);

            foreach (string culture in
                     cultures)
            {
                localization.SetCulture(
                    culture);
                foreach ((string themeName,
                             ThemeVariant theme) in
                         new[]
                         {
                             ("light",
                                 ThemeVariant.Light),
                             ("dark",
                                 ThemeVariant.Dark),
                         })
                {
                    Application.Current
                        .RequestedThemeVariant =
                        theme;
                    Render();
                    Assert.False(
                        window.FindControl<Border>(
                                "NavigationScrim")!
                            .IsVisible,
                        "The shell navigation overlay obscured the localized Workbench capture.");
                    WorkbenchView view =
                        Assert.IsType<WorkbenchView>(
                            window.FindControl<
                                ContentControl>(
                                "ContentHost")!
                                .Content);
                    WorkbenchViewModel model =
                        services.GetRequiredService<
                            WorkbenchViewModel>();
                    Carousel sections =
                        view.FindControl<Carousel>(
                            "WorkbenchTabs")!;

                    foreach (
                        WorkbenchSection section in
                        Enum.GetValues<
                            WorkbenchSection>())
                    {
                        model.SelectedSection =
                            section;
                        Render();
                        Assert.Equal(
                            (int)section,
                            sections.SelectedIndex);
                        AssertWorkbenchGeometry(
                            view,
                            section,
                            $"{culture}-{themeName}");
                        Assert.DoesNotContain(
                            view.GetVisualDescendants()
                                .OfType<TextBlock>()
                                .Where(text =>
                                    text
                                        .IsEffectivelyVisible)
                                .Select(text =>
                                    text.Text ?? ""),
                            text =>
                                text.Contains(
                                    '\u27E6'));
                        Capture(
                            window,
                            $"workbench-{culture}-18-{themeName}-900x600-{section}.png");
                    }
                }
            }
        }
        finally
        {
            window.Hide();
            Application.Current
                .RequestedThemeVariant =
                previousTheme;
            CultureInfo.CurrentUICulture =
                previousUICulture;
        }
    }

    [AvaloniaFact]
    public async Task Standard_and_compact_density_have_visible_geometry_deltas_in_workbench_and_settings()
    {
        var settings = new MemorySettings();
        settings.SetPreference(
            LocalizationPreferences.DisplayLanguage,
            "en-US");
        settings.SetPreference(
            AppearancePreferences
                .ShellRailExpandedPreference,
            bool.FalseString);
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
        var standardRows =
            new Dictionary<
                (int Width, string Theme),
                double>();
        var standardSettingsPadding =
            new Dictionary<
                (int Width, string Theme),
                double>();
        try
        {
            window.WindowState =
                WindowState.Normal;
            window.Show();
            WorkbenchViewModel workbenchModel =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            Render();
            WorkbenchView initialWorkbench =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!
                        .Content);
            await initialWorkbench
                .AddDroppedSourcesAsync(
                    [FixtureDocument().Path]);
            Assert.Single(
                workbenchModel.Files);

            foreach ((int width, int height) in
                     new[]
                     {
                         (900, 600),
                         (1440, 900),
                     })
            foreach ((string themeName,
                         ThemeVariant theme) in
                     new[]
                     {
                         ("light",
                             ThemeVariant.Light),
                         ("dark",
                             ThemeVariant.Dark),
                     })
            foreach (UiDensity density in
                     new[]
                     {
                         UiDensity.Standard,
                         UiDensity.Compact,
                     })
            {
                window.Width = width;
                window.Height = height;
                Application.Current
                    .RequestedThemeVariant =
                    theme;
                AppearancePreferences.SetDensity(
                    settings,
                    density);
                Render();
                Assert.False(
                    window.FindControl<Border>(
                            "NavigationScrim")!
                        .IsVisible,
                    "The shell navigation overlay obscured the density capture.");
                string densityName =
                    density.ToString()
                        .ToLowerInvariant();
                Assert.Contains(
                    $"density-{densityName}",
                    window.Classes);

                services.GetRequiredService<
                        INavigationService>()
                    .Navigate(
                        ShellDestination.Workbench);
                workbenchModel.SelectedSection =
                    WorkbenchSection.Session;
                Render();
                WorkbenchView workbench =
                    Assert.IsType<WorkbenchView>(
                        window.FindControl<
                            ContentControl>(
                            "ContentHost")!
                            .Content);
                DataGridRow row =
                    Assert.Single(
                        workbench.FindControl<
                                AppDataGrid>(
                                "WorkbenchGrid")!
                            .GetVisualDescendants()
                            .OfType<DataGridRow>());
                AssertWorkbenchGeometry(
                    workbench,
                    WorkbenchSection.Session,
                    $"{densityName}-{themeName}-{width}");
                Assert.DoesNotContain(
                    workbench
                        .GetVisualDescendants()
                        .OfType<Border>(),
                    border =>
                        border.Classes.Contains(
                            "empty-state") &&
                        border
                            .IsEffectivelyVisible);
                Capture(
                    window,
                    $"density-{densityName}-{themeName}-{width}x{height}-workbench-session.png");

                var metricKey =
                    (width, themeName);
                if (density ==
                    UiDensity.Standard)
                {
                    standardRows[metricKey] =
                        row.Bounds.Height;
                    Assert.InRange(
                        row.Bounds.Height,
                        37.5,
                        38.5);
                }
                else
                {
                    Assert.InRange(
                        row.Bounds.Height,
                        31.5,
                        32.5);
                    Assert.True(
                        row.Bounds.Height <
                        standardRows[metricKey],
                        $"Compact Workbench rows did not shrink at {width}x{height} {themeName}.");
                }

                services.GetRequiredService<
                        INavigationService>()
                    .Navigate(
                        ShellDestination.Settings);
                Render();
                SettingsView settingsView =
                    Assert.IsType<SettingsView>(
                        window.FindControl<
                            ContentControl>(
                            "ContentHost")!
                            .Content);
                Border statusBanner =
                    settingsView
                        .GetVisualDescendants()
                        .OfType<Border>()
                        .First(border =>
                            border.Classes.Contains(
                                "status-banner") &&
                            border
                                .IsEffectivelyVisible);
                AssertNoPageHorizontalOverflow(
                    settingsView,
                    $"settings-{densityName}-{themeName}-{width}");
                Capture(
                    window,
                    $"density-{densityName}-{themeName}-{width}x{height}-settings.png");

                if (density ==
                    UiDensity.Standard)
                {
                    standardSettingsPadding[
                        metricKey] =
                        statusBanner.Padding.Left;
                    Assert.Equal(
                        12,
                        statusBanner.Padding.Left);
                }
                else
                {
                    Assert.Equal(
                        8,
                        statusBanner.Padding.Left);
                    Assert.True(
                        statusBanner.Padding.Left <
                        standardSettingsPadding[
                            metricKey],
                        $"Compact Settings banners did not reduce padding at {width}x{height} {themeName}.");
                }
            }
        }
        finally
        {
            window.Hide();
            Application.Current
                .RequestedThemeVariant =
                previousTheme;
        }
    }

    private static MediaDocument
        FixtureDocument()
    {
        string path =
            Path.GetFullPath(
                @"X:\Fixture\Density Fixture.flac");
        return new(
            path,
            [],
            [],
            null,
            new(
                path,
                2048,
                new DateTime(
                    2026,
                    1,
                    2,
                    3,
                    4,
                    5,
                    DateTimeKind.Utc),
                "density-fixture"),
            true);
    }

    private static void AssertWorkbenchGeometry(
        WorkbenchView view,
        WorkbenchSection section,
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
            $"{state}: Workbench section host overflowed.");
        Assert.True(
            active.Bounds.Width <=
            sections.Bounds.Width + 1,
            $"{state}: {section} overflowed its host.");
        AssertNoPageHorizontalOverflow(
            active,
            $"{state}-{section}");
    }

    private static void
        AssertNoPageHorizontalOverflow(
            Control root,
            string state)
    {
        foreach (ScrollViewer scroll in
                 root.GetVisualDescendants()
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
    }

    private static void Capture(
        MainWindow window,
        string fileName)
    {
        Render();
        using var frame =
            window.GetLastRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(
            (int)window.Bounds.Width,
            frame.PixelSize.Width);
        Assert.Equal(
            (int)window.Bounds.Height,
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
                fileName),
            PngBitmapEncoderOptions.Default);
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
                            MediaDocument fixture =
                                FixtureDocument();
                            return fixture with
                            {
                                Path =
                                    Path.GetFullPath(
                                        source),
                            };
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
