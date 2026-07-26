using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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

namespace MusicLibraryManager.UI.Tests;

public sealed class
    CrossApplicationPresentationMatrixTests
{
    private static readonly (
        int Width,
        int Height)[] Viewports =
    [
        (900, 600),
        (1200, 700),
        (1440, 900),
    ];

    private static readonly (
        string Id,
        ThemeVariant Theme)[] Themes =
    [
        ("light", ThemeVariant.Light),
        ("dark", ThemeVariant.Dark),
    ];

    private static readonly (
        string Id,
        string Culture,
        bool IsExpandedPseudo)[]
        Presentations =
    [
        ("en-US", "en-US", false),
        ("de-DE", "de-DE", false),
        ("ja-JP", "ja-JP", false),
        ("zh-CN", "zh-CN", false),
        ("pseudo-expanded-40", "en-US", true),
    ];

    private static readonly UiDensity[]
        Densities =
    [
        UiDensity.Standard,
        UiDensity.Compact,
    ];

    private static readonly double[] FontSizes =
    [
        14,
        18,
    ];

    [AvaloniaFact]
    public async Task
        Every_shell_destination_satisfies_the_complete_presentation_matrix()
    {
        var settings = new MatrixSettings();
        settings.SetPreference(
            LocalizationPreferences
                .DisplayLanguage,
            "en-US");
        settings.SetPreference(
            AppearancePreferences
                .ShellRailExpandedPreference,
            bool.FalseString);
        var neutral =
            new ResourceLocalizationService(
                settings);
        var localization =
            new TestPseudoLocalizationService(
                neutral,
                expanded: false);
        using ServiceProvider services =
            Composition.BuildServices(
                collection =>
                {
                    collection.AddSingleton<
                        IAppSettings>(
                        settings);
                    collection.AddSingleton<
                        ILocalizationService>(
                        localization);
                });
        App.UseServicesForTests(services);

        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        INavigationService navigation =
            services.GetRequiredService<
                INavigationService>();
        ContentControl contentHost =
            window.FindControl<ContentControl>(
                "ContentHost")!;
        Border navigationScrim =
            window.FindControl<Border>(
                "NavigationScrim")!;
        Grid body =
            window.FindControl<Grid>(
                "BodyGrid")!;
        ThemeVariant? previousTheme =
            Application.Current!
                .RequestedThemeVariant;
        CultureInfo previousUICulture =
            CultureInfo.CurrentUICulture;
        double previousFontSize =
            window.FontSize;
        string? captureDirectory =
            Environment.GetEnvironmentVariable(
                "MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        var captureNames =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        var caseIdentities =
            new HashSet<string>(
                StringComparer.Ordinal);
        var neutralTextLengths =
            new Dictionary<
                NeutralPresentationKey,
                int>();
        var englishTitles =
            Enum.GetValues<
                    ShellDestination>()
                .ToDictionary(
                    destination =>
                        destination,
                    destination =>
                        neutral.Get(
                            TitleResourceKey(
                                destination)));
        var cachedViews =
            new Dictionary<
                ShellDestination,
                Control>();
        IReadOnlySet<string>?
            expandedCatalogValues = null;
        int presentationCount = 0;

        try
        {
            window.WindowState =
                WindowState.Normal;
            window.Show();

            foreach ((
                         string presentationId,
                         string culture,
                         bool isExpandedPseudo)
                     in Presentations)
            {
                localization.SetCulture(culture);
                localization.SetExpanded(
                    isExpandedPseudo);
                expandedCatalogValues =
                    isExpandedPseudo
                        ? localization.Snapshot()
                            .Values
                            .ToHashSet(
                                StringComparer
                                    .Ordinal)
                        : null;

                foreach (UiDensity density in
                         Densities)
                {
                    AppearancePreferences
                        .SetDensity(
                            settings,
                            density);
                    string densityId =
                        density.ToString()
                            .ToLowerInvariant();

                    foreach (double fontSize in
                             FontSizes)
                    {
                        window.FontSize =
                            fontSize;

                        foreach ((
                                     string themeId,
                                     ThemeVariant theme)
                                 in Themes)
                        {
                            Application.Current
                                    .RequestedThemeVariant =
                                theme;
                            var responsiveRuns =
                                Enum.GetValues<
                                        ShellDestination>()
                                    .ToDictionary(
                                        destination =>
                                            destination,
                                        _ =>
                                            new List<
                                                ResponsiveSnapshot>(
                                                Viewports
                                                    .Length));

                            foreach ((
                                         int width,
                                         int height)
                                     in Viewports)
                            {
                                bool dockWideRail =
                                    width > 900;
                                AppearancePreferences
                                    .SetShellRailExpanded(
                                        settings,
                                        dockWideRail);
                                window.Width = width;
                                window.Height = height;
                                Render();

                                Assert.False(
                                    navigationScrim
                                        .IsVisible,
                                    $"{presentationId}/{densityId}/{fontSize:0}/{themeId}/{width}x{height}: the navigation scrim obscured the destination.");
                                AssertShellRail(
                                    body,
                                    width,
                                    dockWideRail);

                                foreach (
                                    ShellDestination
                                        destination in
                                    Enum.GetValues<
                                        ShellDestination>())
                                {
                                    navigation.Navigate(
                                        destination);
                                    Render();
                                    if (contentHost.Content is
                                        DevicesView devices)
                                    {
                                        await devices
                                            .InitialDeviceDiscovery;
                                        Render();
                                        DevicesViewModel deviceModel =
                                            Assert.IsType<
                                                DevicesViewModel>(
                                                devices
                                                    .DataContext);
                                        Button[] visibleLifecycle =
                                        [
                                            .. new[]
                                            {
                                                "InitializeButton",
                                                "PreviewButton",
                                                "ApplyButton",
                                            }
                                            .Select(name =>
                                                devices
                                                    .FindControl<
                                                        Button>(
                                                        name)!)
                                            .Where(button =>
                                                button
                                                    .IsEffectivelyVisible),
                                        ];
                                        string? expectedLifecycle =
                                            deviceModel.ApplyCommand
                                                .CanExecute(null)
                                                ? "ApplyButton"
                                                : deviceModel
                                                    .PreviewCommand
                                                    .CanExecute(null)
                                                    ? "PreviewButton"
                                                    : deviceModel
                                                        .InitializeCommand
                                                        .CanExecute(null)
                                                        ? "InitializeButton"
                                                        : null;
                                        if (expectedLifecycle is null)
                                        {
                                            Assert.Empty(
                                                visibleLifecycle);
                                        }
                                        else
                                        {
                                            Button button =
                                                Assert.Single(
                                                    visibleLifecycle);
                                            Assert.Equal(
                                                expectedLifecycle,
                                                button.Name);
                                            Assert.True(
                                                button
                                                    .IsEffectivelyEnabled,
                                                $"Devices exposed disabled lifecycle primary {button.Name}; " +
                                                $"loading={deviceModel.IsLoadingDevices}, " +
                                                $"initialize={deviceModel.InitializeCommand.CanExecute(null)}, " +
                                                $"preview={deviceModel.PreviewCommand.CanExecute(null)}, " +
                                                $"apply={deviceModel.ApplyCommand.CanExecute(null)}.");
                                            Assert.Contains(
                                                "primary",
                                                button
                                                    .Classes);
                                            Assert.True(
                                                Application
                                                    .Current!
                                                    .TryGetResource(
                                                        "AppAccentBrush",
                                                        theme,
                                                        out object?
                                                            accent));
                                            Assert.Equal(
                                                accent?
                                                    .ToString(),
                                                Assert
                                                    .Single(
                                                        button
                                                            .GetVisualDescendants()
                                                            .OfType<
                                                                ContentPresenter>())
                                                    .Background?
                                                    .ToString());
                                        }
                                    }
                                    presentationCount++;

                                    string context =
                                        $"{presentationId}/{densityId}/{fontSize:0}/{themeId}/{width}x{height}/{destination}";
                                    Assert.True(
                                        caseIdentities.Add(
                                            context),
                                        $"The matrix produced the duplicate case identity '{context}'.");
                                    Assert.Equal(
                                        destination,
                                        navigation
                                            .Current);
                                    Assert.False(
                                        navigationScrim
                                            .IsVisible,
                                        $"{context}: the navigation scrim obscured the capture.");

                                    Control activeView =
                                        Assert
                                            .IsAssignableFrom<
                                                Control>(
                                                contentHost
                                                    .Content);
                                    AssertActiveViewFits(
                                        contentHost,
                                        activeView,
                                        context);
                                    AssertNoPageHorizontalOverflow(
                                        activeView,
                                        context);
                                    Control[]
                                        workflowActions =
                                            FindWorkflowActions(
                                                activeView);
                                    AssertWorkflowActionCoverage(
                                        destination,
                                        workflowActions,
                                        context);
                                    AssertWorkflowActionsReachable(
                                        activeView,
                                        workflowActions,
                                        context);
                                    AssertWorkflowActionsDoNotOverlap(
                                        activeView,
                                        workflowActions,
                                        context);
                                    AssertSequentialWorkflowActionsDoNotOverlap(
                                        activeView,
                                        workflowActions,
                                        context);
                                    AssertAtMostOnePrimaryPerActionRegion(
                                        activeView,
                                        context);

                                    VisibleTextMetrics
                                        textMetrics =
                                            ReadVisibleTextMetrics(
                                                activeView);
                                    AssertLocalizedTitle(
                                        activeView,
                                        destination,
                                        localization,
                                        englishTitles[
                                            destination],
                                        presentationId,
                                        isExpandedPseudo,
                                        cachedViews,
                                        context);
                                    var neutralKey =
                                        new NeutralPresentationKey(
                                            density,
                                            fontSize,
                                            themeId,
                                            width,
                                            height,
                                            destination);
                                    if (isExpandedPseudo)
                                    {
                                        AssertPseudoExpansion(
                                            textMetrics,
                                            neutralTextLengths[
                                                neutralKey],
                                            expandedCatalogValues!,
                                            context);
                                    }
                                    else
                                    {
                                        AssertNoMissingKeys(
                                            textMetrics,
                                            context);
                                        if (culture ==
                                            "en-US")
                                        {
                                            neutralTextLengths[
                                                    neutralKey] =
                                                textMetrics
                                                    .TotalCharacters;
                                        }
                                    }

                                    responsiveRuns[
                                            destination].Add(
                                            ReadResponsiveSnapshot(
                                                destination,
                                                activeView,
                                                contentHost,
                                                width));

                                    Capture(
                                        window,
                                        captureDirectory,
                                        captureNames,
                                        presentationId,
                                        densityId,
                                        fontSize,
                                        themeId,
                                        width,
                                        height,
                                        destination);
                                }
                            }

                            foreach ((
                                         ShellDestination
                                             destination,
                                         List<
                                             ResponsiveSnapshot>
                                             run) in
                                     responsiveRuns)
                            {
                                AssertResponsivePresentationNeverMovesBackward(
                                    run,
                                    $"{presentationId}/{densityId}/{fontSize:0}/{themeId}/{destination}");
                            }
                        }
                    }
                }
            }

            Assert.Equal(
                1200,
                presentationCount);
            Assert.Equal(
                presentationCount,
                caseIdentities.Count);
            if (!string.IsNullOrWhiteSpace(
                    captureDirectory))
            {
                Assert.Equal(
                    presentationCount,
                    captureNames.Count);
            }
        }
        finally
        {
            window.Hide();
            window.FontSize =
                previousFontSize;
            Application.Current
                    .RequestedThemeVariant =
                previousTheme;
            CultureInfo.CurrentUICulture =
                previousUICulture;
        }
    }

    private static void AssertShellRail(
        Grid body,
        int width,
        bool dockWideRail)
    {
        double expectedRailWidth =
            dockWideRail ? 220 : 64;
        double actualRailWidth =
            body.ColumnDefinitions[0]
                .ActualWidth;
        Assert.InRange(
            actualRailWidth,
            expectedRailWidth - 1,
            expectedRailWidth + 1);
        if (dockWideRail)
        {
            Assert.True(
                width - actualRailWidth >=
                900,
                $"{width}: the docked navigation rail left only {width - actualRailWidth:0} px for the destination.");
        }
    }

    private static void AssertActiveViewFits(
        ContentControl contentHost,
        Control activeView,
        string context)
    {
        Assert.True(
            contentHost.Bounds.Width > 0 &&
            contentHost.Bounds.Height > 0,
            $"{context}: the content host had no rendered size ({contentHost.Bounds.Size}).");
        Assert.InRange(
            activeView.Bounds.Width,
            contentHost.Bounds.Width - 1,
            contentHost.Bounds.Width + 1);
        Assert.InRange(
            activeView.Bounds.Height,
            contentHost.Bounds.Height - 1,
            contentHost.Bounds.Height + 1);
        Assert.True(
            UiViewportReachability
                .TryGetFullyVisibleBounds(
                    contentHost,
                    activeView,
                    out Rect bounds,
                    out string detail),
            $"{context}: the active view did not fit the content host. View={bounds}; host={contentHost.Bounds.Size}. {detail}");
    }

    private static void
        AssertNoPageHorizontalOverflow(
            Control root,
            string context)
    {
        foreach (ScrollViewer scroll in
                 root.GetVisualDescendants()
                     .OfType<ScrollViewer>()
                     .Where(control =>
                         control
                             .IsEffectivelyVisible)
                     .Where(control =>
                         !IsApprovedHorizontalSurface(
                             control)))
        {
            Assert.True(
                scroll.Viewport.Width > 0,
                $"{context}: visible page ScrollViewer {scroll.Name ?? scroll.GetType().Name} had a zero-width viewport.");
            Assert.True(
                scroll.Extent.Width <=
                scroll.Viewport.Width + 1,
                $"{context}: page-level horizontal overflow in {scroll.Name ?? scroll.GetType().Name} was {scroll.Extent.Width:0.0}/{scroll.Viewport.Width:0.0}; bounds={scroll.Bounds.Size}; ancestors={VisualPath(scroll, root)}.");
        }
    }

    private static string VisualPath(
        Control control,
        Control root) =>
        string.Join(
            " <- ",
            control.GetVisualAncestors()
                .TakeWhile(ancestor =>
                    !ReferenceEquals(
                        ancestor,
                        root))
                .OfType<Control>()
                .Select(ancestor =>
                    $"{ancestor.GetType().Name}({ancestor.Name ?? "-"},{ancestor.Bounds.Width:0}x{ancestor.Bounds.Height:0})"));

    private static bool
        IsApprovedHorizontalSurface(
        ScrollViewer scroll) =>
        scroll.GetVisualAncestors()
            .Any(ancestor =>
                ancestor is
                    Carousel or
                    AppDataGrid or
                    DataGrid or
                    TextBox or
                    ComboBox or
                    ListBox or
                    TreeView or
                    NumericUpDown);

    private static Control[]
        FindWorkflowActions(
            Control activeView) =>
    [
        .. activeView
            .GetVisualDescendants()
            .OfType<Control>()
            .Where(control =>
                control
                    .IsEffectivelyVisible)
            .Where(control =>
                control is Button or
                    SplitButton)
            .Where(control =>
                control.TemplatedParent is
                    null)
            .Where(control =>
                !control
                    .GetVisualAncestors()
                    .Any(IsDataSurface)),
    ];

    private static bool IsDataSurface(
        Visual ancestor) =>
        ancestor is
            AppDataGrid or
            DataGrid or
            ListBox or
            TreeView;

    private static void
        AssertWorkflowActionCoverage(
            ShellDestination destination,
            IReadOnlyCollection<Control>
                actions,
            string context)
    {
        if (destination ==
            ShellDestination.About)
        {
            // About is intentionally informational until a license expander
            // is opened; its collapsed initial state has no workflow action.
            return;
        }

        Assert.NotEmpty(
            actions);
        Assert.Contains(
            actions,
            action =>
                action.IsEffectivelyEnabled ||
                action.Classes.Contains(
                    "overflow") ||
                action is SplitButton);
    }

    private static void
        AssertWorkflowActionsReachable(
            Control activeView,
            IReadOnlyList<Control> actions,
            string context)
    {
        foreach (Control action in actions)
        {
            UiActionReachabilityResult result =
                UiViewportReachability
                    .VerifyAction(
                        activeView,
                        action,
                        Render);
            Assert.True(
                result.IsReachable,
                $"{context}: {ActionIdentity(action)} was neither visible nor vertically reachable. {result.Detail}");
        }
    }

    private static void
        AssertSequentialWorkflowActionsDoNotOverlap(
            Control activeView,
            IReadOnlyCollection<Control>
                actions,
            string context)
    {
        var actionSet =
            actions.ToHashSet(
                ReferenceEqualityComparer
                    .Instance);
        // StackPanel and WrapPanel are sequential action layouts. Grids are
        // deliberately excluded here because several workflows overlay
        // mutually-exclusive stage buttons in one cell; the global visible
        // action overlap check above still catches an accidental simultaneous
        // overlay. Closed flyouts, drawers, and data templates are excluded by
        // FindWorkflowActions' effective-visibility/ownership filters.
        foreach (Panel panel in
                 activeView
                     .GetVisualDescendants()
                     .OfType<Panel>()
                     .Where(panel =>
                         panel
                             .IsEffectivelyVisible &&
                         panel is
                             StackPanel or
                             WrapPanel))
        {
            Control[] directActions =
            [
                .. panel.Children
                    .OfType<Control>()
                    .Where(actionSet
                        .Contains),
            ];
            for (int firstIndex = 0;
                 firstIndex <
                 directActions.Length;
                 firstIndex++)
            {
                Rect first =
                    BoundsRelativeTo(
                        directActions[
                            firstIndex],
                        panel);
                for (int secondIndex =
                         firstIndex + 1;
                     secondIndex <
                     directActions.Length;
                     secondIndex++)
                {
                    Rect second =
                        BoundsRelativeTo(
                            directActions[
                                secondIndex],
                            panel);
                    Assert.False(
                        RectanglesOverlap(
                            first,
                            second),
                        $"{context}: sequential action siblings {ActionIdentity(directActions[firstIndex])} and {ActionIdentity(directActions[secondIndex])} overlap in {panel.Name ?? panel.GetType().Name}.");
                }
            }
        }
    }

    private static bool RectanglesOverlap(
        Rect first,
        Rect second) =>
        Math.Min(
            first.Right,
            second.Right) -
        Math.Max(
            first.Left,
            second.Left) > 1 &&
        Math.Min(
            first.Bottom,
            second.Bottom) -
        Math.Max(
            first.Top,
            second.Top) > 1;

    private static void
        AssertWorkflowActionsDoNotOverlap(
            Control activeView,
            IReadOnlyList<Control> actions,
            string context)
    {
        Control[] simultaneouslyVisible =
        [
            .. actions.Where(action =>
                UiViewportReachability
                    .TryGetFullyVisibleBounds(
                        activeView,
                        action,
                        out _,
                        out _)),
        ];
        for (int firstIndex = 0;
             firstIndex <
             simultaneouslyVisible.Length;
             firstIndex++)
        {
            Control first =
                simultaneouslyVisible[
                    firstIndex];
            Rect firstBounds =
                BoundsRelativeTo(
                    first,
                    activeView);
            for (int secondIndex =
                     firstIndex + 1;
                 secondIndex <
                 simultaneouslyVisible.Length;
                 secondIndex++)
            {
                Control second =
                    simultaneouslyVisible[
                        secondIndex];
                if (first
                        .GetVisualAncestors()
                        .Contains(second) ||
                    second
                        .GetVisualAncestors()
                        .Contains(first))
                {
                    continue;
                }

                Rect secondBounds =
                    BoundsRelativeTo(
                        second,
                        activeView);
                Assert.False(
                    RectanglesOverlap(
                        firstBounds,
                        secondBounds),
                    $"{context}: workflow actions {ActionIdentity(first)} {firstBounds} and {ActionIdentity(second)} {secondBounds} overlap.");
            }
        }
    }

    private static Rect BoundsRelativeTo(
        Control control,
        Control ancestor)
    {
        Point? origin =
            control.TranslatePoint(
                default,
                ancestor);
        Assert.NotNull(origin);
        return new Rect(
            origin.Value,
            control.Bounds.Size);
    }

    private static string ActionIdentity(
        Control action)
    {
        if (!string.IsNullOrWhiteSpace(
                action.Name))
            return action.Name;
        if (action is ContentControl
                {
                    Content: string content,
                } &&
            !string.IsNullOrWhiteSpace(
                content))
            return content;
        return action.GetType().Name;
    }

    private static void
        AssertAtMostOnePrimaryPerActionRegion(
            Control root,
            string context)
    {
        Control[] regions =
        [
            .. EnumerateSelfAndDescendants(
                    root)
                .OfType<Control>()
                .Where(control =>
                    control
                        .IsEffectivelyVisible &&
                    IsActionRegion(control)),
        ];

        foreach (Control region in regions)
        {
            Control[] primaryActions =
            [
                .. region
                    .GetVisualDescendants()
                    .OfType<Control>()
                    .Where(control =>
                        control
                            .IsEffectivelyVisible &&
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
                $"{context}: {region.Name ?? region.GetType().Name} contains {primaryActions.Length} primary actions: {string.Join(", ", primaryActions.Select(ActionIdentity))}.");
        }
    }

    private static IEnumerable<Visual>
        EnumerateSelfAndDescendants(
            Visual root)
    {
        yield return root;
        foreach (Visual descendant in
                 root.GetVisualDescendants())
            yield return descendant;
    }

    private static bool IsActionRegion(
        Control control) =>
        control is WrapPanel or
            PageHeader ||
        control.Classes.Contains(
            "sticky-footer") ||
        control.Classes.Contains(
            "quiet-toolbar") ||
        control.Classes.Contains(
            "toolbar");

    private static Control?
        NearestActionRegion(
            Control control,
            IReadOnlyCollection<Control>
                regions) =>
        control.GetVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(
                regions.Contains);

    private static void AssertLocalizedTitle(
        Control activeView,
        ShellDestination destination,
        ILocalizationService localization,
        string englishTitle,
        string presentation,
        bool isExpandedPseudo,
        IDictionary<
            ShellDestination,
            Control> cachedViews,
        string context)
    {
        if (cachedViews.TryGetValue(
                destination,
                out Control? cached))
        {
            Assert.Same(
                cached,
                activeView);
        }
        else
        {
            cachedViews[destination] =
                activeView;
        }

        TextBlock title =
            Assert.Single(
                activeView
                    .GetVisualDescendants()
                    .OfType<TextBlock>(),
                text =>
                    text
                        .IsEffectivelyVisible &&
                    text.Classes.Contains(
                        "page-title"));
        string expected =
            localization.Get(
                TitleResourceKey(
                    destination));
        Assert.Equal(
            expected,
            title.Text);
        if (!isExpandedPseudo &&
            (presentation == "de-DE" ||
             presentation == "ja-JP" ||
             presentation == "zh-CN"))
        {
            Assert.NotEqual(
                englishTitle,
                title.Text);
        }

        Assert.False(
            string.IsNullOrWhiteSpace(
                title.Text),
            $"{context}: the localized destination title was blank.");
    }

    private static string TitleResourceKey(
        ShellDestination destination) =>
        destination switch
        {
            ShellDestination.Home =>
                "Home.Title",
            ShellDestination.Library =>
                "Library.Title",
            ShellDestination.Workbench =>
                "Workbench.Title",
            ShellDestination.Health =>
                "Health.Title",
            ShellDestination.Ingest =>
                "Ingest.Title",
            ShellDestination.Organize =>
                "Organize.Title",
            ShellDestination.Devices =>
                "Devices.Title",
            ShellDestination.Operations =>
                "Operations.Title",
            ShellDestination.Settings =>
                "Settings.Title",
            ShellDestination.About =>
                "About.Title",
            _ =>
                throw new
                    ArgumentOutOfRangeException(
                        nameof(destination),
                        destination,
                        "Unsupported shell destination."),
        };

    private static VisibleTextMetrics
        ReadVisibleTextMetrics(
            Control root)
    {
        string[] values =
        [
            .. root.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text =>
                    text.IsEffectivelyVisible &&
                    !string.IsNullOrWhiteSpace(
                        text.Text))
                .Select(text =>
                    text.Text!),
        ];
        return new(
            values,
            values.Sum(value =>
                value.Length),
            values.Count(value =>
                value.Contains('\u27E6') &&
                value.Contains('\u27E7')),
            values.Sum(value =>
                value.Count(character =>
                    character == '\u02D0')));
    }

    private static void AssertNoMissingKeys(
        VisibleTextMetrics metrics,
        string context)
    {
        Assert.DoesNotContain(
            metrics.Values,
            value =>
                value.Contains('\u27E6') ||
                value.Contains('\u27E7'));
        Assert.True(
            metrics.TotalCharacters > 0,
            $"{context}: the presentation exposed no visible text.");
    }

    private static void AssertPseudoExpansion(
        VisibleTextMetrics metrics,
        int neutralCharacterCount,
        IReadOnlySet<string>
            expandedCatalogValues,
        string context)
    {
        Assert.True(
            metrics.PseudoLocalizedValues > 0,
            $"{context}: no visible localized text used the expanded pseudo presentation.");
        Assert.True(
            metrics.ExpansionMarkers > 0,
            $"{context}: the pseudo presentation did not include expansion markers.");
        Assert.True(
            metrics.TotalCharacters >=
            neutralCharacterCount * 1.15,
            $"{context}: visible pseudo text expanded by less than 15% ({metrics.TotalCharacters}/{neutralCharacterCount}).");
        Assert.DoesNotContain(
            metrics.Values.Where(value =>
                value.Contains('\u27E6')),
            value =>
                !value.Contains('\u27E7'));

        string[] exactResourceValues =
        [
            .. metrics.Values
                .Where(value =>
                    value.Contains('\u27E6') &&
                    expandedCatalogValues
                        .Contains(value))
                .Distinct(
                    StringComparer.Ordinal),
        ];
        Assert.NotEmpty(
            exactResourceValues);
        foreach (string value in
                 exactResourceValues)
        {
            int sourceCharacters =
                CountPseudoSourceCharacters(
                    value);
            int expectedMarkers =
                sourceCharacters * 2 / 5;
            int actualMarkers =
                value.Count(character =>
                    character == '\u02D0');
            Assert.Equal(
                expectedMarkers,
                actualMarkers);
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

    private static ResponsiveSnapshot
        ReadResponsiveSnapshot(
            ShellDestination destination,
            Control activeView,
            ContentControl contentHost,
            int windowWidth)
    {
        CentralTaskDescriptor descriptor =
            ReadCentralTaskDescriptor(
                destination,
                activeView);
        Control surface =
            FindNamedDescendant(
                activeView,
                descriptor.SurfaceName) ??
            throw new InvalidOperationException(
                $"{destination} did not expose its named central task surface '{descriptor.SurfaceName}'.");
        Rect surfaceBounds =
            BoundsRelativeTo(
                surface,
                activeView);
        Assert.True(
            surface.Bounds.Width >=
            descriptor.MinimumWidth,
            $"{destination}: central task surface {descriptor.SurfaceName} was only {surface.Bounds.Width:0}px wide; expected at least {descriptor.MinimumWidth:0}px.");
        Assert.True(
            surfaceBounds.Left >= -1 &&
            surfaceBounds.Right <=
            activeView.Bounds.Width + 1,
            $"{destination}: central task surface {descriptor.SurfaceName} overflowed horizontally: {surfaceBounds} within {activeView.Bounds.Size}.");
        if (descriptor.HasStableRank)
        {
            Assert.Equal(
                0,
                descriptor.AdaptiveRank);
        }

        return new(
            windowWidth,
            contentHost.Bounds.Width,
            activeView.Bounds.Width,
            descriptor.SurfaceName,
            surface.Bounds.Width,
            descriptor.MinimumWidth,
            descriptor.AdaptiveRank,
            descriptor.HasStableRank);
    }

    private static CentralTaskDescriptor
        ReadCentralTaskDescriptor(
            ShellDestination destination,
        Control activeView)
    {
        // Library and Organize deliberately keep one structural presentation:
        // their virtualized grids resize continuously instead of crossing a
        // picker/rail or stacked/split mode. Every other destination records
        // the rank of its actual central-task breakpoint.
        return destination switch
        {
            ShellDestination.Home =>
                new(
                    "SetupLayout",
                    280,
                    GridHasWideColumns(
                        activeView,
                        "SetupLayout",
                        2),
                    false),
            ShellDestination.Library =>
                new(
                    "LibraryGrid",
                    500,
                    0,
                    true),
            ShellDestination.Workbench =>
                new(
                    "WorkbenchTabs",
                    720,
                    RailPresentationRank(
                        activeView,
                        "WorkbenchSectionRail",
                        "WorkbenchSectionPicker"),
                    false),
            ShellDestination.Health =>
                ReadHealthTaskDescriptor(
                    activeView),
            ShellDestination.Ingest =>
                new(
                    "PreviewGrid",
                    500,
                    GridHasWideColumns(
                        activeView,
                        "SourcePickerLayout",
                        3),
                    false),
            ShellDestination.Organize =>
                ReadOrganizeTaskDescriptor(
                    activeView),
            ShellDestination.Devices =>
                new(
                    "DeviceResultsPane",
                    300,
                    GridHasWideColumns(
                        activeView,
                        "DevicesContentLayout",
                        3),
                    false),
            ShellDestination.Operations =>
                new(
                    "JobDetailsScroll",
                    300,
                    VisibilityRank(
                        activeView,
                        "JobListPane",
                        "JobPicker"),
                    false),
            ShellDestination.Settings =>
                new(
                    "SettingsTabs",
                    600,
                    RailPresentationRank(
                        activeView,
                        "SettingsCategoryRail",
                        "SettingsCategoryPicker"),
                    false),
            ShellDestination.About =>
                new(
                    "AboutScroll",
                    500,
                    GridHasWideColumns(
                        activeView,
                        "PackageGrid",
                        3),
                    false),
            _ =>
                throw new
                    ArgumentOutOfRangeException(
                        nameof(destination),
                        destination,
                        "Unsupported shell destination."),
        };
    }

    private static CentralTaskDescriptor
        ReadHealthTaskDescriptor(
            Control activeView)
    {
        Control setup =
            FindNamedDescendant(
                activeView,
                "HealthSetupCard") ??
            throw new InvalidOperationException(
                "Health did not expose its setup state.");
        if (setup.IsEffectivelyVisible)
        {
            // The unconfigured matrix intentionally presents one stable,
            // contextual setup task instead of constructing hidden result
            // navigation. Populated Health states have their own matrix.
            return new(
                "HealthSetupCard",
                500,
                0,
                true);
        }

        return new(
            "HealthResultsHost",
            500,
            GridHasWideColumns(
                activeView,
                "HealthActionLayout",
                2),
            false);
    }

    private static CentralTaskDescriptor
        ReadOrganizeTaskDescriptor(
            Control activeView)
    {
        Control setup =
            FindNamedDescendant(
                activeView,
                "OrganizeSetupCard") ??
            throw new InvalidOperationException(
                "Organize did not expose its setup state.");
        return setup.IsEffectivelyVisible
            ? new(
                "OrganizeSetupCard",
                500,
                0,
                true)
            : new(
                "MovesGrid",
                500,
                0,
                true);
    }

    private static int GridHasWideColumns(
        Control activeView,
        string gridName,
        int expectedWideColumns)
    {
        Grid grid =
            Assert.IsType<Grid>(
                FindNamedDescendant(
                    activeView,
                    gridName));
        return grid.ColumnDefinitions
                   .Count >=
               expectedWideColumns &&
               grid.ColumnDefinitions
                   .Count(column =>
                       column.ActualWidth > 1) >=
               expectedWideColumns
            ? 1
            : 0;
    }

    private static int RailPresentationRank(
        Control activeView,
        string railName,
        string pickerName) =>
        VisibilityRank(
            activeView,
            railName,
            pickerName);

    private static int VisibilityRank(
        Control activeView,
        string wideName,
        string compactName)
    {
        Control wide =
            FindNamedDescendant(
                activeView,
                wideName) ??
            throw new InvalidOperationException(
                $"Missing responsive surface '{wideName}'.");
        Control compact =
            FindNamedDescendant(
                activeView,
                compactName) ??
            throw new InvalidOperationException(
                $"Missing responsive surface '{compactName}'.");
        Assert.NotEqual(
            wide.IsVisible,
            compact.IsVisible);
        return wide.IsVisible ? 1 : 0;
    }

    private static Control? FindNamedDescendant(
        Control root,
        string name) =>
        root.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control =>
                control.Name == name);

    private static void
        AssertResponsivePresentationNeverMovesBackward(
            IReadOnlyList<ResponsiveSnapshot> run,
            string context)
    {
        Assert.Equal(
            Viewports.Length,
            run.Count);
        for (int index = 1;
             index < run.Count;
             index++)
        {
            ResponsiveSnapshot previous =
                run[index - 1];
            ResponsiveSnapshot current =
                run[index];
            Assert.True(
                current.WindowWidth >
                previous.WindowWidth);
            Assert.True(
                current.ContentHostWidth >=
                previous.ContentHostWidth - 1,
                $"{context}: increasing the window from {previous.WindowWidth} to {current.WindowWidth} reduced the destination host from {previous.ContentHostWidth:0} to {current.ContentHostWidth:0}.");
            Assert.True(
                current.ActiveViewWidth >=
                previous.ActiveViewWidth - 1,
                $"{context}: increasing the window from {previous.WindowWidth} to {current.WindowWidth} reduced the active presentation from {previous.ActiveViewWidth:0} to {current.ActiveViewWidth:0}.");
            Assert.True(
                current.AdaptiveRank >=
                previous.AdaptiveRank,
                $"{context}: increasing the window from {previous.WindowWidth} to {current.WindowWidth} moved {current.CentralTaskSurface} from rank {previous.AdaptiveRank} back to {current.AdaptiveRank}.");
            Assert.Equal(
                previous.CentralTaskSurface,
                current.CentralTaskSurface);
            Assert.Equal(
                previous.HasStableRank,
                current.HasStableRank);
            if (current.AdaptiveRank ==
                previous.AdaptiveRank)
            {
                Assert.True(
                    current.CentralTaskWidth >=
                    previous.CentralTaskWidth - 1,
                    $"{context}: increasing the window from {previous.WindowWidth} to {current.WindowWidth} reduced same-rank central task surface {current.CentralTaskSurface} from {previous.CentralTaskWidth:0} to {current.CentralTaskWidth:0}.");
            }
            else
            {
                Assert.True(
                    current.CentralTaskWidth >=
                    current.MinimumCentralTaskWidth,
                    $"{context}: the wider rank left {current.CentralTaskSurface} only {current.CentralTaskWidth:0}px wide.");
            }
        }
    }

    private static void Capture(
        MainWindow window,
        string? captureDirectory,
        ISet<string> captureNames,
        string presentation,
        string density,
        double fontSize,
        string theme,
        int width,
        int height,
        ShellDestination destination)
    {
        if (string.IsNullOrWhiteSpace(
                captureDirectory))
            return;

        string fileName =
            $"cross-app-matrix-{presentation}-{density}-{fontSize:0}px-{theme}-{width}x{height}-{destination}.png";
        Assert.True(
            captureNames.Add(fileName),
            $"The matrix produced the duplicate capture name '{fileName}'.");
        Directory.CreateDirectory(
            captureDirectory);
        // The first destination in a new viewport follows About from the
        // preceding viewport. Flush the render invalidations once more so a
        // logically active Home view cannot be saved with About's prior
        // framebuffer.
        Render();
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

    private static void Render() =>
        RenderTicks(2);

    private static void RenderTicks(
        int timerTicks)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(
                timerTicks);
        Dispatcher.UIThread.RunJobs();
    }

    private readonly record struct
        NeutralPresentationKey(
            UiDensity Density,
            double FontSize,
            string Theme,
            int Width,
            int Height,
            ShellDestination Destination);

    private readonly record struct
        ResponsiveSnapshot(
            int WindowWidth,
            double ContentHostWidth,
            double ActiveViewWidth,
            string CentralTaskSurface,
            double CentralTaskWidth,
            double MinimumCentralTaskWidth,
            int AdaptiveRank,
            bool HasStableRank);

    private readonly record struct
        CentralTaskDescriptor(
            string SurfaceName,
            double MinimumWidth,
            int AdaptiveRank,
            bool HasStableRank);

    private sealed record VisibleTextMetrics(
        IReadOnlyList<string> Values,
        int TotalCharacters,
        int PseudoLocalizedValues,
        int ExpansionMarkers);

    private sealed class MatrixSettings :
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
