using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryTools;
using Xunit;
using System.Globalization;

namespace MusicLibraryManager.UI.Tests;

public sealed class WorkbenchResponsiveMatrixTests
{
    private static readonly (int Width, int Height)[]
        Viewports =
        [
            (900, 600),
            (1200, 700),
            (1440, 900),
        ];

    private static readonly (string Name, ThemeVariant Theme)[]
        Themes =
        [
            ("light", ThemeVariant.Light),
            ("dark", ThemeVariant.Dark),
        ];

    [AvaloniaFact]
    public void Every_workbench_destination_fits_the_required_responsive_matrix()
    {
        var settings = new MatrixSettings();
        var neutral = new ResourceLocalizationService(
            settings);
        var localization =
            new TestPseudoLocalizationService(
                neutral,
                expanded: false);
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
            Application.Current!.RequestedThemeVariant;
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        string? captureDirectory =
            Environment.GetEnvironmentVariable(
                "MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        try
        {
            window.Show();
            window.WindowState =
                WindowState.Normal;
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);

            foreach (bool expanded in
                     new[] { false, true })
            {
                localization.SetExpanded(expanded);
                foreach (double fontSize in
                         new[] { 14d, 18d })
                {
                    window.FontSize = fontSize;
                    foreach ((string themeName,
                                 ThemeVariant theme) in
                             Themes)
                    {
                        Application.Current
                            .RequestedThemeVariant =
                            theme;
                        foreach ((int width,
                                     int height) in
                                 Viewports)
                        {
                            window.Width = width;
                            window.Height = height;
                            Render();

                            WorkbenchView view =
                                Assert.IsType<
                                    WorkbenchView>(
                                    window.FindControl<
                                        ContentControl>(
                                        "ContentHost")!
                                        .Content);
                            WorkbenchViewModel model =
                                services
                                    .GetRequiredService<
                                        WorkbenchViewModel>();
                            Carousel sections =
                                view.FindControl<
                                    Carousel>(
                                    "WorkbenchTabs")!;

                            AssertNavigationMode(
                                view,
                                width);
                            foreach (
                                WorkbenchSection section
                                in Enum.GetValues<
                                    WorkbenchSection>())
                            {
                                model.SelectedSection =
                                    section;
                                Render();

                                Assert.Equal(
                                    (int)section,
                                    sections
                                        .SelectedIndex);
                                AssertSectionFits(
                                    view,
                                    sections,
                                    section,
                                    width,
                                    height);
                                AssertAtMostOnePrimaryPerToolbar(
                                    view,
                                    section);

                                if (section ==
                                        WorkbenchSection
                                            .Session &&
                                    width == 900 &&
                                    height == 600)
                                {
                                    Assert.True(
                                        view.FindControl<
                                                AppDataGrid>(
                                                "WorkbenchGrid")!
                                            .Bounds.Height >=
                                        220,
                                        "The compact Session grid must retain at least 220 px of usable height.");
                                }

                                CaptureRepresentativeFrame(
                                    window,
                                    captureDirectory,
                                    expanded,
                                    fontSize,
                                    themeName,
                                    width,
                                    height,
                                    section);
                            }
                        }
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
        }
    }

    [AvaloniaFact]
    public void GermanAndCjkLocalesFitEveryWorkbenchDestinationAtMinimumSize()
    {
        var settings = new MatrixSettings();
        var localization = new ResourceLocalizationService(settings);
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(settings);
                collection.AddSingleton<ILocalizationService>(localization);
            });
        App.UseServicesForTests(services);

        ThemeVariant? previousTheme =
            Application.Current!.RequestedThemeVariant;
        CultureInfo previousUICulture = CultureInfo.CurrentUICulture;
        MainWindow window = services.GetRequiredService<MainWindow>();
        string? captureDirectory =
            Environment.GetEnvironmentVariable(
                "MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        string[] cultures =
            ["de-DE", "ja-JP", "ko-KR", "zh-CN", "zh-TW"];
        try
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Width = 900;
            window.Height = 600;
            window.FontSize = 18;
            services.GetRequiredService<INavigationService>()
                .Navigate(ShellDestination.Workbench);

            foreach (string culture in cultures)
            {
                localization.SetCulture(culture);
                foreach ((string themeName, ThemeVariant theme) in Themes)
                {
                    Application.Current.RequestedThemeVariant = theme;
                    Render();
                    WorkbenchView view = Assert.IsType<WorkbenchView>(
                        window.FindControl<ContentControl>("ContentHost")!.Content);
                    WorkbenchViewModel model =
                        services.GetRequiredService<WorkbenchViewModel>();
                    Carousel sections =
                        view.FindControl<Carousel>("WorkbenchTabs")!;
                    AssertNavigationMode(view, 900);

                    foreach (WorkbenchSection section in
                             Enum.GetValues<WorkbenchSection>())
                    {
                        model.SelectedSection = section;
                        Render();
                        Assert.Equal((int)section, sections.SelectedIndex);
                        AssertSectionFits(view, sections, section, 900, 600);
                        AssertAtMostOnePrimaryPerToolbar(view, section);
                        Assert.DoesNotContain(
                            view.GetVisualDescendants()
                                .OfType<TextBlock>()
                                .Where(text => text.IsEffectivelyVisible)
                                .Select(text => text.Text ?? ""),
                            text => text.Contains('\u27E6'));
                        if (section == WorkbenchSection.Session)
                            Assert.True(
                                view.FindControl<AppDataGrid>("WorkbenchGrid")!
                                    .Bounds.Height >= 220);
                        CaptureLocalizedFrame(
                            window,
                            captureDirectory,
                            culture,
                            themeName,
                            section);
                    }
                }
            }
        }
        finally
        {
            window.Hide();
            Application.Current.RequestedThemeVariant = previousTheme;
            CultureInfo.CurrentUICulture = previousUICulture;
        }
    }

    private static void AssertNavigationMode(
        WorkbenchView view,
        int windowWidth)
    {
        SplitButton addSources =
            view.FindControl<SplitButton>(
                "AddWorkbenchSourceButton")!;
        Assert.Contains(
            "app",
            addSources.Classes);
        Assert.Contains(
            "primary",
            addSources.Classes);
        Assert.Equal(
            36,
            addSources.MinHeight);
        Assert.NotNull(
            addSources.Background);

        bool compact =
            windowWidth == Viewports[0].Width;
        Assert.Equal(
            compact,
            view.FindControl<ComboBox>(
                    "WorkbenchSectionPicker")!
                .IsVisible);
        Assert.Equal(
            !compact,
            view.FindControl<Border>(
                    "WorkbenchSectionRail")!
                .IsVisible);
    }

    private static void AssertSectionFits(
        WorkbenchView view,
        Carousel sections,
        WorkbenchSection section,
        int width,
        int height)
    {
        Control active =
            Assert.IsAssignableFrom<Control>(
                sections.SelectedItem);
        Assert.True(
            sections.Bounds.Width <=
            view.Bounds.Width + 1,
            $"{section} carousel overflowed at {width}x{height}: {sections.Bounds.Width:0}/{view.Bounds.Width:0}.");
        Assert.True(
            active.Bounds.Width <=
            sections.Bounds.Width + 1,
            $"{section} overflowed its page at {width}x{height}: {active.Bounds.Width:0}/{sections.Bounds.Width:0}.");

        foreach (ScrollViewer scroll in
                 active.GetVisualDescendants()
                     .OfType<ScrollViewer>()
                     .Where(control =>
                         control.IsEffectivelyVisible)
                     .Where(control =>
                         !control.GetVisualAncestors()
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
                $"{section} has page-level horizontal overflow at {width}x{height}: extent {scroll.Extent.Width:0}, viewport {scroll.Viewport.Width:0}.");
        }

        string subtitle =
            view.FindControl<PageHeader>(
                    "WorkbenchHeader")!
                .Subtitle ??
            "";
        if (height <= 700)
            Assert.Equal(string.Empty, subtitle);
        else
            Assert.False(
                string.IsNullOrWhiteSpace(
                    subtitle));
    }

    private static void
        AssertAtMostOnePrimaryPerToolbar(
            WorkbenchView view,
            WorkbenchSection section)
    {
        foreach (WrapPanel toolbar in
                 view.GetVisualDescendants()
                     .OfType<WrapPanel>()
                     .Where(panel =>
                         panel.IsEffectivelyVisible))
        {
            Control[] primaryActions =
                toolbar.Children
                    .OfType<Control>()
                    .Where(control =>
                        control.IsEffectivelyVisible &&
                        control.Classes.Contains(
                            "primary"))
                    .ToArray();
            Assert.True(
                primaryActions.Length <= 1,
                $"{section} exposes {primaryActions.Length} primary actions in one toolbar: {string.Join(", ", primaryActions.Select(action => action.Name ?? action.GetType().Name))}.");
        }
    }

    private static void CaptureRepresentativeFrame(
        MainWindow window,
        string? captureDirectory,
        bool expanded,
        double fontSize,
        string themeName,
        int width,
        int height,
        WorkbenchSection section)
    {
        using var frame =
            window.GetLastRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(
            width,
            frame.PixelSize.Width);
        Assert.Equal(
            height,
            frame.PixelSize.Height);

        if (string.IsNullOrWhiteSpace(
                captureDirectory) ||
            !expanded ||
            fontSize != 18 ||
            themeName != "dark")
            return;

        Directory.CreateDirectory(
            captureDirectory);
        frame.Save(
            Path.Combine(
                captureDirectory,
                $"workbench-pseudo-18-dark-{width}x{height}-{section}.png"),
            PngBitmapEncoderOptions.Default);
    }

    private static void CaptureLocalizedFrame(
        MainWindow window,
        string? captureDirectory,
        string culture,
        string themeName,
        WorkbenchSection section)
    {
        using var frame = window.GetLastRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(900, frame.PixelSize.Width);
        Assert.Equal(600, frame.PixelSize.Height);
        if (string.IsNullOrWhiteSpace(captureDirectory))
            return;
        Directory.CreateDirectory(captureDirectory);
        frame.Save(
            Path.Combine(
                captureDirectory,
                $"workbench-{culture}-18-{themeName}-900x600-{section}.png"),
            PngBitmapEncoderOptions.Default);
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class MatrixSettings :
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
                _preferences[key] =
                    value;
        }
    }
}
