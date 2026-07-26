using System.Globalization;
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
using MusicLibraryManager.Services;
using MusicLibraryManager.Views;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

/// <summary>
/// Exercises the representative application states as complete visual
/// presentations. Each state row owns an isolated configured application and
/// window, while its 120 presentation changes reuse that state deterministically.
/// </summary>
public sealed class RepresentativeStateVisualMatrixTests
{
    private const int ExpectedPresentationCount = 1320;
    private const int PresentationsPerState = 120;

    private static readonly (int Width, int Height)[] Viewports =
    [
        (900, 600),
        (1200, 700),
        (1440, 900),
    ];

    private static readonly (string Name, ThemeVariant Value)[] Themes =
    [
        ("light", ThemeVariant.Light),
        ("dark", ThemeVariant.Dark),
    ];

    private static readonly UiDensity[] Densities =
    [
        UiDensity.Standard,
        UiDensity.Compact,
    ];

    private static readonly double[] FontSizes =
    [
        14,
        18,
    ];

    private static readonly Presentation[] Presentations =
    [
        new("en-US", "en-US", false),
        new("de-DE", "de-DE", false),
        new("ja-JP", "ja-JP", false),
        new("zh-CN", "zh-CN", false),
        new("pseudo-expanded", "en-US", true),
    ];

    private static readonly RepresentativeState[] States =
        Enum.GetValues<RepresentativeState>();

    [Fact]
    public void Matrix_definition_has_every_required_state_and_1320_presentations()
    {
        RepresentativeState[] expected =
        [
            RepresentativeState.ConfiguredEmpty,
            RepresentativeState.Populated,
            RepresentativeState.Selected,
            RepresentativeState.DirtyPending,
            RepresentativeState.Busy,
            RepresentativeState.ValidationError,
            RepresentativeState.UnavailableConfiguration,
            RepresentativeState.UnavailableTool,
            RepresentativeState.MenuOpen,
            RepresentativeState.DrawerOpen,
            RepresentativeState.Dialog,
        ];

        Assert.Equal(
            expected,
            States);
        Assert.Equal(
            PresentationsPerState,
            Presentations.Length *
            Densities.Length *
            FontSizes.Length *
            Themes.Length *
            Viewports.Length);
        Assert.Equal(
            ExpectedPresentationCount,
            States.Length *
            PresentationsPerState);

        string[] captureIdentities =
        [
            .. from state in States
               from presentation in Presentations
               from density in Densities
               from fontSize in FontSizes
               from theme in Themes
               from viewport in Viewports
               select CaseName(
                   state,
                   presentation.Name,
                   density,
                   fontSize,
                   theme.Name,
                   viewport.Width,
                   viewport.Height),
        ];
        Assert.Equal(
            ExpectedPresentationCount,
            captureIdentities.Length);
        Assert.Equal(
            ExpectedPresentationCount,
            captureIdentities
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [AvaloniaTheory]
    [InlineData(RepresentativeState.ConfiguredEmpty)]
    [InlineData(RepresentativeState.Populated)]
    [InlineData(RepresentativeState.Selected)]
    [InlineData(RepresentativeState.DirtyPending)]
    [InlineData(RepresentativeState.Busy)]
    [InlineData(RepresentativeState.ValidationError)]
    [InlineData(RepresentativeState.UnavailableConfiguration)]
    [InlineData(RepresentativeState.UnavailableTool)]
    [InlineData(RepresentativeState.MenuOpen)]
    [InlineData(RepresentativeState.DrawerOpen)]
    [InlineData(RepresentativeState.Dialog)]
    public async Task Representative_state_fits_every_required_presentation(
        RepresentativeState state)
    {
        using var settings = new MatrixSettings();
        settings.SetPreference(
            LocalizationPreferences.DisplayLanguage,
            "en-US");
        settings.SetPreference(
            AppearancePreferences.ShellRailExpandedPreference,
            bool.FalseString);
        var neutral = new ResourceLocalizationService(settings);
        var localization = new TestPseudoLocalizationService(
            neutral,
            expanded: false);
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(settings);
                collection.AddSingleton<ILocalizationService>(
                    localization);
                collection.AddSingleton<IWorkbenchService>(
                    new MatrixWorkbenchService());
            });
        App.UseServicesForTests(services);

        ThemeVariant? priorTheme =
            Application.Current!.RequestedThemeVariant;
        CultureInfo priorUICulture =
            CultureInfo.CurrentUICulture;
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        var context = new StateContext(
            services,
            settings,
            localization,
            window);
        string? captureDirectory =
            Environment.GetEnvironmentVariable(
                "MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        var captureNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        int presentationsExercised = 0;

        try
        {
            window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            Render();

            await context.EnterAsync(state);

            foreach (Presentation presentation in Presentations)
            foreach (UiDensity density in Densities)
            foreach (double fontSize in FontSizes)
            foreach ((string themeName, ThemeVariant theme) in Themes)
            foreach ((int width, int height) in Viewports)
            {
                string caseName = CaseName(
                    state,
                    presentation.Name,
                    density,
                    fontSize,
                    themeName,
                    width,
                    height);
                await context.CloseTransientSurfaceAsync();
                ApplyPresentation(
                    settings,
                    localization,
                    window,
                    presentation,
                    density,
                    fontSize,
                    theme,
                    width,
                    height);
                await context.RefreshPresentationAsync(state);
                Render();

                AssertShellPresentation(
                    window,
                    density,
                    width,
                    caseName);
                context.AssertState(state, caseName);
                AssertPageFits(
                    window.FindControl<ContentControl>(
                        "ContentHost")!,
                    context.ActivePage,
                    caseName);
                AssertActiveActions(
                    context.ActiveActionSurface,
                    caseName);
                AssertLocalization(
                    context.ActiveTextSurface,
                    neutral,
                    localization,
                    presentation,
                    caseName);
                Capture(
                    window,
                    captureDirectory,
                    captureNames,
                    caseName,
                    width,
                    height);
                presentationsExercised++;
            }

            await context.CloseTransientSurfaceAsync();
        }
        finally
        {
            await context.CloseTransientSurfaceAsync();
            window.Hide();
            Application.Current.RequestedThemeVariant =
                priorTheme;
            CultureInfo.CurrentUICulture =
                priorUICulture;
        }

        Assert.Equal(
            PresentationsPerState,
            presentationsExercised);
        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Assert.Equal(
                PresentationsPerState,
                captureNames.Count);
        }
    }

    private static void ApplyPresentation(
        MatrixSettings settings,
        TestPseudoLocalizationService localization,
        MainWindow window,
        Presentation presentation,
        UiDensity density,
        double fontSize,
        ThemeVariant theme,
        int width,
        int height)
    {
        if (localization.IsExpanded &&
            !presentation.Expanded)
        {
            localization.SetExpanded(false);
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                localization.CurrentUICulture.Name,
                presentation.Culture))
        {
            localization.SetCulture(
                presentation.Culture);
        }

        if (localization.IsExpanded !=
            presentation.Expanded)
        {
            localization.SetExpanded(
                presentation.Expanded);
        }
        AppearancePreferences.SetDensity(
            settings,
            density);
        AppearancePreferences.SetShellRailExpanded(
            settings,
            width >= 1200);
        Application.Current!.RequestedThemeVariant =
            theme;
        window.FontSize = fontSize;
        window.Width = width;
        window.Height = height;
        Render();
    }

    private static void AssertShellPresentation(
        MainWindow window,
        UiDensity density,
        int width,
        string caseName)
    {
        Border navigationScrim =
            window.FindControl<Border>(
                "NavigationScrim")!;
        Assert.False(
            navigationScrim.IsVisible,
            $"{caseName}: NavigationScrim obstructed the active page.");

        string densityClass =
            $"density-{density.ToString().ToLowerInvariant()}";
        Assert.Contains(
            densityClass,
            window.Classes);

        Grid body = window.FindControl<Grid>(
            "BodyGrid")!;
        ContentControl host =
            window.FindControl<ContentControl>(
                "ContentHost")!;
        if (width == 900)
        {
            Assert.InRange(
                body.ColumnDefinitions[0].ActualWidth,
                63.5,
                64.5);
        }
        else
        {
            Assert.InRange(
                body.ColumnDefinitions[0].ActualWidth,
                219.5,
                220.5);
            Assert.True(
                host.Bounds.Width >= 760,
                $"{caseName}: the safely docked wide navigation rail left only {host.Bounds.Width:0.0}px for page content.");
        }
    }

    private static void AssertPageFits(
        ContentControl contentHost,
        Control page,
        string caseName)
    {
        Assert.True(
            page.Bounds.Width > 0 &&
            page.Bounds.Height > 0,
            $"{caseName}: the active page had no rendered size.");
        Assert.InRange(
            page.Bounds.Width,
            contentHost.Bounds.Width - 1,
            contentHost.Bounds.Width + 1);
        Assert.InRange(
            page.Bounds.Height,
            contentHost.Bounds.Height - 1,
            contentHost.Bounds.Height + 1);
        Assert.True(
            UiViewportReachability
                .TryGetFullyVisibleBounds(
                    contentHost,
                    page,
                    out Rect pageBounds,
                    out string pageDetail),
            $"{caseName}: the active page did not fully fit ContentHost. Page={pageBounds}; host={contentHost.Bounds.Size}. {pageDetail}");

        foreach (ScrollViewer scroll in
                 page.GetVisualDescendants()
                     .OfType<ScrollViewer>()
                     .Where(scroll =>
                         scroll.IsEffectivelyVisible)
                     .Where(scroll =>
                         !IsApprovedDataSurface(scroll)))
        {
            Assert.True(
                scroll.Viewport.Width > 0,
                $"{caseName}: visible page ScrollViewer {scroll.Name ?? scroll.GetType().Name} had a zero-width viewport.");
            Assert.True(
                scroll.Extent.Width <=
                scroll.Viewport.Width + 1,
                $"{caseName}: page-level horizontal overflow in {scroll.Name ?? scroll.GetType().Name} was {scroll.Extent.Width:0.0}/{scroll.Viewport.Width:0.0}.");
        }

        if (page is WorkbenchView workbench)
        {
            Carousel sections =
                workbench.FindControl<Carousel>(
                    "WorkbenchTabs")!;
            Control active =
                Assert.IsAssignableFrom<Control>(
                    sections.SelectedItem);
            Assert.True(
                sections.Bounds.Width <=
                workbench.Bounds.Width + 1,
                $"{caseName}: the Workbench section host overflowed its page.");
            Assert.True(
                active.Bounds.Width <=
                sections.Bounds.Width + 1,
                $"{caseName}: the active Workbench section overflowed its host.");
        }
    }

    private static bool IsApprovedDataSurface(
        ScrollViewer scroll) =>
        scroll.GetVisualAncestors().Any(ancestor =>
            ancestor is Carousel or
                AppDataGrid or
                DataGrid or
                TextBox or
                ComboBox or
                ListBox or
                TreeView or
                NumericUpDown);

    private static void AssertActiveActions(
        Control surface,
        string caseName)
    {
        AssertAtMostOnePrimaryPerActionRegion(
            surface,
            caseName);

        Control[] actions =
        [
            .. surface.GetVisualDescendants()
                .OfType<Control>()
                .Where(control =>
                    control.IsEffectivelyVisible)
                .Where(control =>
                    control is Button or
                        SplitButton)
                .Where(control =>
                    control.TemplatedParent is null)
                .Where(control =>
                    !control.GetVisualAncestors()
                        .Any(IsDataSurface))
                .Distinct(),
        ];

        foreach (Control action in actions)
        {
            UiActionReachabilityResult result =
                UiViewportReachability.VerifyAction(
                    surface,
                    action,
                    Render);
            Assert.True(
                result.IsReachable,
                $"{caseName}: {action.Name ?? action.GetType().Name} was not fully visible or vertically reachable. {result.Detail}");
        }

        Control[] simultaneouslyVisible =
        [
            .. actions.Where(action =>
                UiViewportReachability
                    .TryGetFullyVisibleBounds(
                        surface,
                        action,
                        out _,
                        out _)),
        ];
        for (int leftIndex = 0;
             leftIndex <
             simultaneouslyVisible.Length;
             leftIndex++)
        for (int rightIndex = leftIndex + 1;
             rightIndex <
             simultaneouslyVisible.Length;
             rightIndex++)
        {
            Control left =
                simultaneouslyVisible[leftIndex];
            Control right =
                simultaneouslyVisible[rightIndex];
            if (left.GetVisualAncestors()
                    .Contains(right) ||
                right.GetVisualAncestors()
                    .Contains(left))
            {
                continue;
            }

            if (!TryBoundsIn(
                    left,
                    surface,
                    out Rect leftBounds) ||
                !TryBoundsIn(
                    right,
                    surface,
                    out Rect rightBounds))
            {
                continue;
            }

            Rect overlap =
                leftBounds.Intersect(rightBounds);
            Assert.True(
                overlap.Width <= 1 ||
                overlap.Height <= 1,
                $"{caseName}: {left.Name ?? left.GetType().Name} overlapped {right.Name ?? right.GetType().Name} at {overlap}.");
        }
    }

    private static bool IsDataSurface(
        Visual ancestor) =>
        ancestor is AppDataGrid or
            DataGrid or
            ListBox or
            TreeView;

    private static void
        AssertAtMostOnePrimaryPerActionRegion(
            Control root,
            string caseName)
    {
        Control[] regions =
        [
            .. EnumerateSelfAndDescendants(
                    root)
                .OfType<Control>()
                .Where(control =>
                    control.IsEffectivelyVisible &&
                    (control is WrapPanel ||
                     control is PageHeader ||
                     control.Classes.Contains(
                         "sticky-footer") ||
                     control.Classes.Contains(
                         "quiet-toolbar") ||
                     control.Classes.Contains(
                         "toolbar"))),
        ];

        foreach (Control region in regions)
        {
            Control[] primaryActions =
            [
                .. region.GetVisualDescendants()
                    .OfType<Control>()
                    .Where(control =>
                        control.IsEffectivelyVisible &&
                        control.Classes.Contains(
                            "primary"))
                    .Where(control =>
                        ReferenceEquals(
                            NearestActionRegion(
                                control,
                                regions),
                            region)),
            ];
            Assert.True(
                primaryActions.Length <= 1,
                $"{caseName}: {region.Name ?? region.GetType().Name} contains {primaryActions.Length} primary actions: {string.Join(", ", primaryActions.Select(action => action.Name ?? action.GetType().Name))}.");
        }
    }

    private static Control?
        NearestActionRegion(
            Control control,
            IReadOnlyCollection<Control>
                regions) =>
        control.GetVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(
                regions.Contains);

    private static IEnumerable<Visual>
        EnumerateSelfAndDescendants(
            Visual root)
    {
        yield return root;
        foreach (Visual descendant in
                 root.GetVisualDescendants())
        {
            yield return descendant;
        }
    }

    private static bool TryBoundsIn(
        Control control,
        Visual root,
        out Rect bounds)
    {
        Point? topLeft =
            control.TranslatePoint(default, root);
        Point? bottomRight =
            control.TranslatePoint(
                new(
                    control.Bounds.Width,
                    control.Bounds.Height),
                root);
        if (topLeft is null ||
            bottomRight is null)
        {
            bounds = default;
            return false;
        }

        bounds = new(
            Math.Min(
                topLeft.Value.X,
                bottomRight.Value.X),
            Math.Min(
                topLeft.Value.Y,
                bottomRight.Value.Y),
            Math.Abs(
                bottomRight.Value.X -
                topLeft.Value.X),
            Math.Abs(
                bottomRight.Value.Y -
                topLeft.Value.Y));
        return true;
    }

    private static void AssertLocalization(
        Control textSurface,
        ResourceLocalizationService neutral,
        TestPseudoLocalizationService localization,
        Presentation presentation,
        string caseName)
    {
        string[] visibleText =
        [
            .. textSurface
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text =>
                    text.IsEffectivelyVisible)
                .Select(text =>
                    text.Text ?? ""),
            .. textSurface
                .GetVisualDescendants()
                .OfType<ContentControl>()
                .Where(control =>
                    control.IsEffectivelyVisible &&
                    control.Content is string)
                .Select(control =>
                    (string)control.Content!),
        ];

        if (!presentation.Expanded)
        {
            Assert.DoesNotContain(
                visibleText,
                text =>
                    text.Contains('\u27E6') ||
                    text.Contains('\u27E7'));
            Assert.DoesNotContain(
                localization.Snapshot(),
                entry =>
                    StringComparer.Ordinal.Equals(
                        entry.Value,
                        $"\u27E6{entry.Key}\u27E7"));
            return;
        }

        string neutralTitle =
            neutral.Get("Workbench.Title");
        string expandedTitle =
            localization.Get("Workbench.Title");
        Assert.StartsWith(
            "\u27E6",
            expandedTitle,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "\u27E7",
            expandedTitle,
            StringComparison.Ordinal);
        Assert.True(
            expandedTitle.Length >=
            neutralTitle.Length * 1.4,
            $"{caseName}: the pseudo-localized title was not expanded by at least 40%.");
        Assert.Contains(
            visibleText,
            text =>
                text.Contains('\u27E6') &&
                text.Contains('\u27E7'));

        IReadOnlySet<string> expandedCatalogValues =
            localization.Snapshot()
                .Values
                .ToHashSet(
                    StringComparer.Ordinal);
        string[] exactVisibleResources =
        [
            .. visibleText
                .Where(value =>
                    value.Contains('\u27E6') &&
                    value.Contains('\u27E7') &&
                    expandedCatalogValues
                        .Contains(value))
                .Distinct(
                    StringComparer.Ordinal),
        ];
        Assert.NotEmpty(
            exactVisibleResources);
        foreach (string value in
                 exactVisibleResources)
        {
            int sourceCharacters =
                CountPseudoSourceCharacters(
                    value);
            int expectedFillers =
                sourceCharacters * 2 / 5;
            int actualFillers =
                value.Count(character =>
                    character == '\u02D0');
            Assert.Equal(
                expectedFillers,
                actualFillers);
        }
    }

    private static int
        CountPseudoSourceCharacters(
            string value)
    {
        int count = 0;
        for (int index = 0;
             index < value.Length;
             index++)
        {
            char current =
                value[index];
            if (current is
                '\u27E6' or
                '\u27E7' or
                '\u02D0')
            {
                continue;
            }

            if (current == '{')
            {
                int closing =
                    value.IndexOf(
                        '}',
                        index + 1);
                if (closing >= 0)
                {
                    index = closing;
                    continue;
                }
            }

            count++;
        }

        return count;
    }

    private static void Capture(
        MainWindow window,
        string? captureDirectory,
        ISet<string> captureNames,
        string caseName,
        int width,
        int height)
    {
        if (string.IsNullOrWhiteSpace(captureDirectory))
            return;

        string fileName =
            $"representative-state-{caseName}.png";
        Assert.True(
            captureNames.Add(fileName),
            $"The capture name '{fileName}' collided with another matrix presentation.");
        Directory.CreateDirectory(
            captureDirectory);
        using var frame =
            window.GetLastRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(
            width,
            frame.PixelSize.Width);
        Assert.Equal(
            height,
            frame.PixelSize.Height);
        frame.Save(
            Path.Combine(
                captureDirectory,
                fileName),
            PngBitmapEncoderOptions.Default);
    }

    private static string CaseName(
        RepresentativeState state,
        string presentation,
        UiDensity density,
        double fontSize,
        string theme,
        int width,
        int height) =>
        string.Join(
            "-",
            Kebab(state.ToString()),
            presentation,
            density.ToString().ToLowerInvariant(),
            fontSize.ToString(
                "0",
                CultureInfo.InvariantCulture),
            theme,
            $"{width}x{height}");

    private static string Kebab(string value) =>
        string.Concat(
            value.Select((character, index) =>
                index > 0 &&
                char.IsUpper(character)
                    ? "-" +
                      char.ToLowerInvariant(
                          character)
                    : char.ToLowerInvariant(
                        character).ToString()));

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class StateContext
    {
        private readonly IServiceProvider _services;
        private readonly MatrixSettings _settings;
        private readonly TestPseudoLocalizationService
            _localization;
        private readonly MainWindow _window;
        private readonly INavigationService _navigation;
        private readonly WorkbenchViewModel _workbench;
        private readonly LibraryViewModel _library;
        private readonly DialogService _dialogs;
        private WorkbenchView? _workbenchView;
        private WorkbenchTrackViewModel? _track;
        private MenuFlyout? _openMenu;
        private Task<bool>? _openDialog;

        public StateContext(
            IServiceProvider services,
            MatrixSettings settings,
            TestPseudoLocalizationService localization,
            MainWindow window)
        {
            _services = services;
            _settings = settings;
            _localization = localization;
            _window = window;
            _navigation =
                services.GetRequiredService<
                    INavigationService>();
            _workbench =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            _library =
                services.GetRequiredService<
                    LibraryViewModel>();
            _dialogs =
                services.GetRequiredService<
                    DialogService>();
        }

        public Control ActivePage =>
            Assert.IsAssignableFrom<Control>(
                _window.FindControl<ContentControl>(
                    "ContentHost")!.Content);

        public Control ActiveActionSurface
        {
            get
            {
                DialogHost dialog =
                    _window.FindControl<DialogHost>(
                        "RootDialogHost")!;
                if (dialog.IsEffectivelyVisible)
                    return dialog;
                if (_workbenchView is not null &&
                    _workbenchView
                        .FindControl<Control>(
                            "WorkbenchPendingChangesDrawer")!
                        .IsEffectivelyVisible)
                {
                    return _workbenchView
                        .FindControl<Border>(
                            "WorkbenchDrawerPane")!;
                }

                return ActivePage;
            }
        }

        public Control ActiveTextSurface =>
            ActiveActionSurface;

        public async Task EnterAsync(
            RepresentativeState state)
        {
            await CloseTransientSurfaceAsync();
            switch (state)
            {
                case RepresentativeState.ConfiguredEmpty:
                    _settings.SetConfigured(true);
                    NavigateToWorkbench();
                    _workbench.SelectedSection =
                        WorkbenchSection.Session;
                    Assert.Empty(_workbench.Files);
                    break;

                case RepresentativeState.Populated:
                    await EnsureTrackAsync();
                    ResetTrackSelection();
                    break;

                case RepresentativeState.Selected:
                    await EnsureTrackAsync();
                    SelectTrack();
                    break;

                case RepresentativeState.DirtyPending:
                    await EnsureTrackAsync();
                    SelectTrack();
                    _track!.Title =
                        "Pending fixture title";
                    Render();
                    Assert.NotEmpty(
                        _workbench.PendingChanges);
                    break;

                case RepresentativeState.Busy:
                    await EnsureTrackAsync();
                    ResetPendingChanges();
                    _workbench.IsBusy = true;
                    _workbench.IsProgressIndeterminate =
                        true;
                    break;

                case RepresentativeState.ValidationError:
                    await EnsureTrackAsync();
                    _workbench.IsBusy = false;
                    _workbench.SelectedSection =
                        WorkbenchSection.Shortcuts;
                    Render();
                    _workbench.ShortcutEditor
                        .NewShortcutCommand
                        .Execute(null);
                    _workbench.ShortcutEditor
                        .GestureText = "Ctrl+";
                    Render();
                    Assert.True(
                        _workbench.ShortcutEditor
                            .HasGestureValidationError);
                    break;

                case RepresentativeState.UnavailableConfiguration:
                    _workbench.IsBusy = false;
                    ResetPendingChanges();
                    _settings.SetConfigured(false);
                    _navigation.Navigate(
                        ShellDestination.Library);
                    _library.Rows = [];
                    _library.PageState =
                        LibraryPageState
                            .NoConfiguration;
                    _library.IsInspectorOpen = false;
                    Render();
                    break;

                case RepresentativeState.UnavailableTool:
                    await EnsureTrackAsync();
                    ConfigureUnavailableTool();
                    break;

                case RepresentativeState.MenuOpen:
                    await EnsureTrackAsync();
                    _workbench.ExternalToolEditor
                        .NewToolCommand
                        .Execute(null);
                    _workbench.SelectedSection =
                        WorkbenchSection.Tools;
                    Render();
                    break;

                case RepresentativeState.DrawerOpen:
                    await EnsureTrackAsync();
                    SelectTrack();
                    _track!.Title =
                        "Drawer review fixture title";
                    Render();
                    Assert.NotEmpty(
                        _workbench.PendingChanges);
                    break;

                case RepresentativeState.Dialog:
                    await EnsureTrackAsync();
                    ResetPendingChanges();
                    _workbench.SelectedSection =
                        WorkbenchSection.Session;
                    Render();
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(state),
                        state,
                        null);
            }

            Render();
        }

        public Task RefreshPresentationAsync(
            RepresentativeState state)
        {
            switch (state)
            {
                case RepresentativeState.Busy:
                    _workbench.ProgressText =
                        _localization.Get(
                            "Index.Progress.Scanning");
                    break;

                case RepresentativeState.ValidationError:
                    Button newShortcut =
                        _workbenchView!.FindControl<Button>(
                            "NewShortcutEmptyButton")!;
                    if (newShortcut
                        .IsEffectivelyVisible)
                    {
                        newShortcut.RaiseEvent(
                            new RoutedEventArgs(
                                Button.ClickEvent));
                        _workbench.ShortcutEditor
                            .GestureText = "Ctrl+";
                    }
                    break;

                case RepresentativeState.MenuOpen:
                    OpenToolMenu();
                    break;

                case RepresentativeState.DrawerOpen:
                    OpenPendingDrawer();
                    break;

                case RepresentativeState.Dialog:
                    OpenConfirmationDialog();
                    break;
            }

            Render();
            return Task.CompletedTask;
        }

        public async Task
            CloseTransientSurfaceAsync()
        {
            if (_openMenu is not null)
            {
                _openMenu.Hide();
                _openMenu = null;
            }

            if (_dialogs.Current is not null)
                _dialogs.Complete(false);
            if (_openDialog is not null)
            {
                bool result = await _openDialog;
                Assert.False(result);
                _openDialog = null;
            }

            if (_workbenchView is not null)
            {
                Control drawer =
                    _workbenchView.FindControl<Control>(
                        "WorkbenchPendingChangesDrawer")!;
                if (drawer.IsEffectivelyVisible)
                {
                    _workbenchView
                        .FindControl<Button>(
                            "WorkbenchPendingChangesCloseButton")!
                        .RaiseEvent(
                            new RoutedEventArgs(
                                Button.ClickEvent));
                }
            }

            Render();
        }

        public void AssertState(
            RepresentativeState state,
            string caseName)
        {
            switch (state)
            {
                case RepresentativeState.ConfiguredEmpty:
                    Assert.NotNull(
                        _settings.ConfigPath);
                    Assert.NotNull(
                        _settings.Configuration);
                    Assert.Empty(
                        _workbench.Files);
                    Assert.Equal(
                        WorkbenchSection.Session,
                        _workbench.SelectedSection);
                    Assert.Contains(
                        _workbenchView!
                            .GetVisualDescendants()
                            .OfType<Border>(),
                        border =>
                            border.Classes.Contains(
                                "empty-state") &&
                            border
                                .IsEffectivelyVisible);
                    break;

                case RepresentativeState.Populated:
                    Assert.Single(
                        _workbench.Files);
                    Assert.Empty(
                        _workbench.SelectedFiles);
                    Assert.DoesNotContain(
                        _workbenchView!
                            .GetVisualDescendants()
                            .OfType<Border>(),
                        border =>
                            border.Classes.Contains(
                                "empty-state") &&
                            border
                                .IsEffectivelyVisible);
                    break;

                case RepresentativeState.Selected:
                    Assert.Single(
                        _workbench.SelectedFiles);
                    Assert.Contains(
                        _track!,
                        WorkbenchGrid
                            .SelectedItems
                            .Cast<
                                WorkbenchTrackViewModel>());
                    break;

                case RepresentativeState.DirtyPending:
                    Assert.NotEmpty(
                        _workbench.PendingChanges);
                    Assert.Contains(
                        "primary",
                        _workbenchView!
                            .FindControl<Button>(
                                "WorkbenchPendingChangesButton")!
                            .Classes);
                    break;

                case RepresentativeState.Busy:
                    Assert.True(
                        _workbench.IsBusy);
                    Assert.True(
                        _workbenchView!
                            .FindControl<StackPanel>(
                                "WorkbenchProgressPanel")!
                            .IsEffectivelyVisible);
                    break;

                case RepresentativeState.ValidationError:
                    Assert.True(
                        _workbench.ShortcutEditor
                            .HasGestureValidationError);
                    Assert.True(
                        _workbenchView!
                            .FindControl<TextBlock>(
                                "GestureValidationMessage")!
                            .IsEffectivelyVisible);
                    break;

                case RepresentativeState.UnavailableConfiguration:
                    Assert.Null(
                        _settings.ConfigPath);
                    Assert.Null(
                        _settings.Configuration);
                    Assert.Equal(
                        LibraryPageState
                            .NoConfiguration,
                        _library.PageState);
                    LibraryView library =
                        Assert.IsType<LibraryView>(
                            ActivePage);
                    Assert.True(
                        library.FindControl<Border>(
                                "LibraryEmptyState")!
                            .IsEffectivelyVisible);
                    break;

                case RepresentativeState.UnavailableTool:
                    Assert.False(
                        _workbench
                            .RunExternalToolCommand
                            .CanExecute(null));
                    Assert.Empty(
                        _workbench
                            .ExternalToolInvocations);
                    Assert.False(
                        string.IsNullOrWhiteSpace(
                            _workbench.StatusText));
                    Assert.True(
                        _workbenchView!
                            .FindControl<Button>(
                                "PreviewExternalToolButton")!
                            .IsEffectivelyVisible);
                    break;

                case RepresentativeState.MenuOpen:
                    Assert.NotNull(
                        _openMenu);
                    Assert.All(
                        _openMenu!.Items
                            .OfType<MenuItem>(),
                        item =>
                            Assert.NotNull(
                                TopLevel.GetTopLevel(
                                    item)));
                    break;

                case RepresentativeState.DrawerOpen:
                    Assert.NotEmpty(
                        _workbench.PendingChanges);
                    Assert.True(
                        _workbenchView!
                            .FindControl<Control>(
                                "WorkbenchPendingChangesDrawer")!
                            .IsEffectivelyVisible);
                    Assert.True(
                        _workbenchView!
                            .FindControl<Border>(
                                "WorkbenchDrawerPane")!
                            .Bounds.Width <= 430);
                    Assert.True(
                        _workbenchView!
                            .FindControl<Button>(
                                "WorkbenchApplyPendingChangesButton")!
                            .IsEffectivelyEnabled);
                    break;

                case RepresentativeState.Dialog:
                    Assert.IsType<ConfirmRequest>(
                        _dialogs.Current);
                    DialogHost host =
                        _window.FindControl<DialogHost>(
                            "RootDialogHost")!;
                    Assert.True(
                        host.IsEffectivelyVisible);
                    Assert.Contains(
                        host.GetVisualDescendants()
                            .OfType<Button>(),
                        button =>
                            button.IsEffectivelyVisible &&
                            button.Classes.Contains(
                                "danger"));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(state),
                        state,
                        caseName);
            }
        }

        private AppDataGrid WorkbenchGrid =>
            _workbenchView!.FindControl<AppDataGrid>(
                "WorkbenchGrid")!;

        private void NavigateToWorkbench()
        {
            _navigation.Navigate(
                ShellDestination.Workbench);
            Render();
            _workbenchView =
                Assert.IsType<WorkbenchView>(
                    _window.FindControl<
                            ContentControl>(
                            "ContentHost")!
                        .Content);
        }

        private async Task EnsureTrackAsync()
        {
            _settings.SetConfigured(true);
            NavigateToWorkbench();
            _workbench.IsBusy = false;
            _workbench.SelectedSection =
                WorkbenchSection.Session;
            if (_workbench.Files.Count == 0)
            {
                await _workbenchView!
                    .AddDroppedSourcesAsync(
                        [_settings.FixturePath]);
                Render();
            }

            _track = Assert.Single(
                _workbench.Files);
        }

        private void ResetTrackSelection()
        {
            WorkbenchGrid.SelectedItems.Clear();
            _workbench.SetSelectedFiles([]);
            Render();
        }

        private void SelectTrack()
        {
            WorkbenchGrid.SelectedItems.Clear();
            WorkbenchGrid.SelectedItems.Add(
                _track!);
            _workbench.SetSelectedFiles(
                [_track!]);
            Render();
        }

        private void ResetPendingChanges()
        {
            if (_track is null)
                return;
            _track.RevertPendingChanges();
            Render();
            Assert.Empty(
                _workbench.PendingChanges);
        }

        private void ConfigureUnavailableTool()
        {
            _workbench.SelectedSection =
                WorkbenchSection.Tools;
            _workbench.ExternalToolEditor
                .NewToolCommand
                .Execute(null);
            _workbench.ExternalToolEditor.Executable =
                Path.Combine(
                    _settings.TempDirectory,
                    "missing-encoder.exe");
            Render();
            Assert.True(
                _workbench
                    .PreviewExternalToolCommand
                    .CanExecute(null));
            _workbench
                .PreviewExternalToolCommand
                .Execute(null);
            Render();
            Assert.False(
                _workbench
                    .RunExternalToolCommand
                    .CanExecute(null));
        }

        private void OpenToolMenu()
        {
            Button launcher =
                _workbenchView!.FindControl<Button>(
                    "SavedToolMoreButton")!;
            _openMenu =
                Assert.IsType<MenuFlyout>(
                    launcher.Flyout);
            _openMenu.ShowAt(launcher);
            Render();
        }

        private void OpenPendingDrawer()
        {
            Control drawer =
                _workbenchView!.FindControl<Control>(
                    "WorkbenchPendingChangesDrawer")!;
            if (!drawer.IsEffectivelyVisible)
            {
                _workbenchView!
                    .FindControl<Button>(
                        "WorkbenchPendingChangesButton")!
                    .RaiseEvent(
                        new RoutedEventArgs(
                            Button.ClickEvent));
                Render();
            }
        }

        private void OpenConfirmationDialog()
        {
            _openDialog =
                _dialogs.ConfirmDestructiveAsync(
                    _localization.Get(
                        "Workbench.PendingChanges.Title"),
                    _localization.Get(
                        "Workbench.PendingChanges.Description"),
                    _localization.Get(
                        "Common.Apply"));
            Render();
        }
    }

    private sealed class MatrixSettings :
        IAppSettings,
        IDisposable
    {
        private readonly Dictionary<string, string>
            _preferences = [];
        private readonly LibraryConfiguration
            _configuration;
        private readonly string _configFilePath;
        private bool _configured = true;

        public MatrixSettings()
        {
            TempDirectory = Path.Combine(
                Path.GetTempPath(),
                "mlm-representative-state-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(
                TempDirectory);
            _configFilePath = Path.Combine(
                TempDirectory,
                "fixture-library.xml");
            File.WriteAllText(
                _configFilePath,
                "<LibraryConfiguration />");
            FixturePath = Path.Combine(
                TempDirectory,
                "Representative Fixture.flac");
            File.WriteAllBytes(
                FixturePath,
                new byte[64]);
            _configuration =
                new LibraryConfiguration(
                    _configFilePath);
        }

        public string TempDirectory { get; }
        public string FixturePath { get; }
        public string? ConfigPath =>
            _configured
                ? _configFilePath
                : null;

        public LibraryConfiguration? Configuration =>
            _configured
                ? _configuration
                : null;

        public event EventHandler?
            ConfigurationChanged;

        public AppConfigurationSnapshot GetSnapshot() =>
            new(
                _configured
                    ? _configFilePath
                    : null,
                Configuration,
                _configured
                    ? 1
                    : 0);

        public void SetConfigured(bool configured) =>
            _configured = configured;

        public void LoadConfig(string path)
        {
            _configured = true;
            ConfigurationChanged?.Invoke(
                this,
                EventArgs.Empty);
        }

        public string? GetRememberedConfigPath() =>
            _configFilePath;

        public IReadOnlyList<string>
            RecentConfigPaths =>
            [
                _configFilePath,
            ];

        public void ClearRecentConfigs()
        {
        }

        public string? GetPreference(string key) =>
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

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(TempDirectory))
                {
                    Directory.Delete(
                        TempDirectory,
                        recursive: true);
                }
            }
            catch (IOException)
            {
                // The test service provider owns the SQLite cache, but its
                // native handle can outlive managed disposal briefly on
                // Windows. Cleanup is best-effort and must not hide a matrix
                // assertion.
            }
            catch (UnauthorizedAccessException)
            {
                // See the native-handle note above.
            }
        }
    }

    private sealed class MatrixWorkbenchService :
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
                                    new FileInfo(path)
                                        .Length,
                                    File.GetLastWriteTimeUtc(
                                        path),
                                    "representative-state-fixture"),
                                true);
                        }),
                ],
                []));
        }
    }

    private sealed record Presentation(
        string Name,
        string Culture,
        bool Expanded);

    public enum RepresentativeState
    {
        ConfiguredEmpty,
        Populated,
        Selected,
        DirtyPending,
        Busy,
        ValidationError,
        UnavailableConfiguration,
        UnavailableTool,
        MenuOpen,
        DrawerOpen,
        Dialog,
    }
}
