using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class AdaptiveUiModernizationTests
{
    [AvaloniaFact]
    public void Adaptive_page_uses_content_width_and_height_thresholds_at_minus_one_exact_and_plus_one()
    {
        var page = new AdaptivePage
        {
            HorizontalAlignment =
                global::Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment =
                global::Avalonia.Layout.VerticalAlignment.Stretch,
        };
        var contentHost = new Border
        {
            HorizontalAlignment =
                global::Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment =
                global::Avalonia.Layout.VerticalAlignment.Top,
            Child = page,
        };
        var window = new Window
        {
            Width = 1400,
            Height = 1000,
            Content = contentHost,
        };
        int layoutModeChanges = 0;
        page.LayoutModeChanged +=
            (_, _) => layoutModeChanges++;
        try
        {
            window.Show();

            AssertMode(
                AdaptivePage.NarrowContentThreshold - 1,
                AdaptivePage.CompactHeightThreshold + 1,
                narrow: true,
                compactHeight: false,
                AdaptivePage.NarrowGutter);
            AssertMode(
                AdaptivePage.NarrowContentThreshold,
                AdaptivePage.CompactHeightThreshold + 1,
                narrow: false,
                compactHeight: false,
                AdaptivePage.WideGutter);
            AssertMode(
                AdaptivePage.NarrowContentThreshold + 1,
                AdaptivePage.CompactHeightThreshold + 1,
                narrow: false,
                compactHeight: false,
                AdaptivePage.WideGutter);
            AssertMode(
                AdaptivePage.NarrowContentThreshold + 1,
                AdaptivePage.CompactHeightThreshold - 1,
                narrow: false,
                compactHeight: true,
                AdaptivePage.CompactHeightGutter);
            AssertMode(
                AdaptivePage.NarrowContentThreshold + 1,
                AdaptivePage.CompactHeightThreshold,
                narrow: false,
                compactHeight: true,
                AdaptivePage.CompactHeightGutter);
            AssertMode(
                AdaptivePage.NarrowContentThreshold + 1,
                AdaptivePage.CompactHeightThreshold + 1,
                narrow: false,
                compactHeight: false,
                AdaptivePage.WideGutter);

            contentHost.Width =
                AdaptivePage
                    .NarrowContentThreshold -
                1;
            contentHost.Height =
                AdaptivePage
                    .CompactHeightThreshold +
                1;
            Render();
            int stableNarrowChanges =
                layoutModeChanges;
            for (int pass = 0;
                 pass < 5;
                 pass++)
                Render();
            Assert.Equal(
                stableNarrowChanges,
                layoutModeChanges);
            Assert.True(
                page.IsNarrow);

            double previousWidth =
                contentHost.Bounds.Width;
            contentHost.Width =
                AdaptivePage
                    .NarrowContentThreshold;
            Render();
            Assert.True(
                contentHost.Bounds.Width >=
                previousWidth);
            Assert.False(
                page.IsNarrow);
            int stableWideChanges =
                layoutModeChanges;
            for (int pass = 0;
                 pass < 5;
                 pass++)
                Render();
            Assert.Equal(
                stableWideChanges,
                layoutModeChanges);
            Assert.False(
                page.IsNarrow);
        }
        finally
        {
            window.Hide();
        }

        void AssertMode(
            double width,
            double height,
            bool narrow,
            bool compactHeight,
            double gutter)
        {
            contentHost.Width = width;
            contentHost.Height = height;
            Render();

            Assert.Equal(
                width,
                contentHost.Bounds.Width);
            Assert.Equal(
                height,
                contentHost.Bounds.Height);
            Assert.Equal(
                width,
                page.ContentWidth);
            Assert.Equal(
                narrow,
                page.Classes.Contains(
                    "narrow-content"));
            Assert.Equal(
                !narrow,
                page.Classes.Contains(
                    "wide-content"));
            Assert.Equal(
                compactHeight,
                page.Classes.Contains(
                    "compact-height"));
            Assert.Equal(
                new Thickness(gutter),
                page.Margin);
        }
    }

    [AvaloniaFact]
    public void Workbench_navigation_and_drawer_modes_follow_remaining_content_constraints()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        var view = new WorkbenchView();
        var window = new Window
        {
            Height = 800,
            Content = view,
        };
        try
        {
            window.Show();
            double railBreakpoint =
                WorkbenchView
                    .SectionRailActivationWidth(
                        compactHeight: false);
            double dockingBreakpoint =
                WorkbenchView
                    .DockedDrawerActivationWidth(
                        compactHeight: false);

            AssertRailMode(
                railBreakpoint - 1,
                railVisible: false);
            AssertRailMode(
                railBreakpoint,
                railVisible: true);
            AssertRailMode(
                railBreakpoint + 1,
                railVisible: true);

            Resize(
                dockingBreakpoint - 1);
            Button inspector =
                view.FindControl<Button>(
                    "WorkbenchInspectorToggle")!;
            if (!view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!
                .IsVisible)
            {
                inspector.RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
                Render();
            }
            Assert.False(
                view.FindControl<GridSplitter>(
                    "Splitter")!
                    .IsVisible);
            ContentPresenter compactDrawer =
                view.FindControl<ContentPresenter>(
                    "RightPresenter")!;
            Assert.InRange(
                compactDrawer.Bounds.Width,
                300,
                430);
            Assert.True(
                view.FindControl<Border>(
                    "WorkbenchHeaderScrim")!
                    .IsVisible);

            AssertDockingMode(
                dockingBreakpoint - 1,
                docked: false);
            AssertDockingMode(
                dockingBreakpoint,
                docked: true);
            AssertDockingMode(
                dockingBreakpoint + 1,
                docked: true);
        }
        finally
        {
            window.Hide();
        }

        void AssertDockingMode(
            double width,
            bool docked)
        {
            Resize(width);
            Assert.Equal(
                docked,
                view.FindControl<GridSplitter>(
                    "Splitter")!
                    .IsVisible);
            Assert.Equal(
                !docked,
                view.FindControl<Border>(
                    "WorkbenchHeaderScrim")!
                    .IsVisible);
            if (docked)
            {
                Assert.True(
                    view.FindControl<Carousel>(
                            "WorkbenchTabs")!
                        .Bounds.Width >=
                    WorkbenchView
                        .MinimumDockedTaskWidth,
                    "A docked drawer must leave at least 760 px inside the central Workbench task frame.");
            }
        }

        void AssertRailMode(
            double width,
            bool railVisible)
        {
            Resize(width);
            Assert.Equal(
                railVisible,
                view.FindControl<Border>(
                    "WorkbenchSectionRail")!
                    .IsVisible);
            Assert.Equal(
                !railVisible,
                view.FindControl<ComboBox>(
                    "WorkbenchSectionPicker")!
                    .IsVisible);
            if (railVisible)
            {
                Assert.True(
                    view.FindControl<Carousel>(
                            "WorkbenchTabs")!
                        .Bounds.Width >=
                    WorkbenchView
                        .MinimumSectionTaskWidth);
            }
        }

        void Resize(double width)
        {
            window.Width = width;
            Render();
            view.ApplyResponsiveLayout(
                compact: false);
            Render();
            Assert.Equal(
                width,
                view.Bounds.Width);
        }
    }

    [AvaloniaFact]
    public async Task Workbench_emphasizes_only_actionable_changes_and_empty_session_source_action()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        var view = new WorkbenchView();
        var window = new Window
        {
            Width = 1200,
            Height = 700,
            Content = view,
        };
        try
        {
            window.Show();
            Render();
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            Button changes =
                view.FindControl<Button>(
                    "WorkbenchPendingChangesButton")!;
            SplitButton addFiles =
                view.FindControl<SplitButton>(
                    "AddWorkbenchSourceButton")!;

            Assert.DoesNotContain(
                "primary",
                changes.Classes);
            Assert.Contains(
                "primary",
                addFiles.Classes);

            await view.AddDroppedSourcesAsync(
                ["loaded.flac"]);
            Render();
            Assert.DoesNotContain(
                "primary",
                addFiles.Classes);

            model.PendingChanges.Add(
                new(
                    "loaded.flac",
                    "Title",
                    "Before",
                    "After"));
            Render();
            Assert.Contains(
                "primary",
                changes.Classes);

            model.PendingChanges.Clear();
            Render();
            Assert.DoesNotContain(
                "primary",
                changes.Classes);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Reviewed_file_operation_intent_enters_and_leaves_the_unified_pending_drawer()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        var view = new WorkbenchView();
        var window = new Window
        {
            Width = 1200,
            Height = 700,
            Content = view,
        };
        try
        {
            window.Show();
            Render();
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            string source =
                Path.GetFullPath(
                    "pending-source.flac");
            string destination =
                Path.GetFullPath(
                    "pending-renamed.flac");
            var request =
                new ReviewedFileOperationRequest(
                    [source],
                    ReviewedFileOperationKind.Rename,
                    null,
                    "pending-renamed{Extension}",
                    false,
                    ReviewedFileCollisionPolicy.Stop);
            var item =
                new ReviewedFileOperationItem(
                    source,
                    destination,
                    FileMutationKind.Move,
                    []);
            var mutationPlan =
                new FileMutationPlan(
                    "reviewed-test",
                    Path.GetDirectoryName(
                        destination)!,
                    Path.Combine(
                        Path.GetDirectoryName(
                            destination)!,
                        "recovery"),
                    [new(
                        FileMutationKind.Move,
                        source,
                        destination,
                        null,
                        null)],
                    [],
                    DateTimeOffset.UtcNow);
            var plan =
                new ReviewedFileOperationPlan(
                    request,
                    [item],
                    mutationPlan);

            bool accepted =
                await model.AddPendingMutationAsync(
                    ReviewedFileOperationMutationIntent
                        .Create(plan));
            Render();

            Assert.True(accepted);
            MetadataPreviewRow pending =
                Assert.Single(
                    model.PendingChanges);
            Assert.Equal(
                Path.GetFileName(source),
                pending.File);
            Assert.Contains(
                "operation",
                pending.Field,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "primary",
                view.FindControl<Button>(
                    "WorkbenchPendingChangesButton")!
                    .Classes);
            Assert.True(
                model.ApplyCommand
                    .CanExecute(null));

            await model.RevertPendingChangesCommand
                .ExecuteAsync(null);
            Render();

            Assert.Empty(
                model.PendingChanges);
            Assert.DoesNotContain(
                "primary",
                view.FindControl<Button>(
                    "WorkbenchPendingChangesButton")!
                    .Classes);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Shell_applies_live_density_persists_rail_choice_and_routes_search()
    {
        var settings = new TestSettings();
        AppearancePreferences.SetDensity(
            settings,
            UiDensity.Compact);
        using ServiceProvider services =
            BuildServices(settings);
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        try
        {
            window.Width = 1440;
            window.Height = 900;
            window.Show();
            Render();

            Assert.Contains(
                "density-compact",
                window.Classes);
            Assert.Equal(
                220,
                window.FindControl<Grid>(
                    "BodyGrid")!
                    .ColumnDefinitions[0]
                    .ActualWidth);

            AppearancePreferences.SetDensity(
                settings,
                UiDensity.Standard);
            Render();
            Assert.Contains(
                "density-standard",
                window.Classes);
            Assert.DoesNotContain(
                "density-compact",
                window.Classes);

            window.FindControl<Button>(
                    "NavigationRailToggle")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Render();
            Assert.False(
                AppearancePreferences
                    .GetShellRailExpanded(
                        settings));
            Assert.Equal(
                64,
                window.FindControl<Grid>(
                    "BodyGrid")!
                    .ColumnDefinitions[0]
                    .ActualWidth);

            INavigationService navigation =
                services.GetRequiredService<
                    INavigationService>();
            LibraryViewModel library =
                services.GetRequiredService<
                    LibraryViewModel>();
            TextBox search =
                window.FindControl<TextBox>(
                    "SearchBox")!;
            navigation.Navigate(
                ShellDestination.Library);
            search.Text = "Aurora";
            Render();
            Assert.Equal(
                "Aurora",
                library.FilterText);

            navigation.Navigate(
                ShellDestination.Workbench);
            search.Text = "Harbor";
            Render();
            Assert.Equal(
                "Aurora",
                library.FilterText);
            search.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Enter,
                });
            Render();
            Assert.Equal(
                ShellDestination.Library,
                navigation.Current);
            Assert.Equal(
                "Harbor",
                library.FilterText);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Shell_expanded_rail_uses_a_full_width_overlay_below_the_safe_docking_threshold()
    {
        var settings = new TestSettings();
        using ServiceProvider services =
            BuildServices(settings);
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        try
        {
            window.Height = 760;
            window.Show();
            Render();

            Grid body =
                window.FindControl<Grid>(
                    "BodyGrid")!;
            Border rail =
                window.FindControl<Border>(
                    "NavigationRail")!;
            Border scrim =
                window.FindControl<Border>(
                    "NavigationScrim")!;
            Button toggle =
                window.FindControl<Button>(
                    "NavigationRailToggle")!;
            StackPanel brand =
                window.FindControl<StackPanel>(
                    "BrandCopy")!;

            AssertWidth(
                1121,
                expectedDockedWidth: 220,
                overlayVisible: false);
            AssertWidth(
                1120,
                expectedDockedWidth: 220,
                overlayVisible: false);
            AssertWidth(
                1119,
                expectedDockedWidth: 64,
                overlayVisible: true);

            double previousContentWidth =
                DestinationWidth();
            AssertWidth(
                1120,
                expectedDockedWidth: 64,
                overlayVisible: true);
            Assert.True(
                DestinationWidth() >=
                previousContentWidth,
                "Growing through the rail breakpoint must not reduce the destination width.");
            previousContentWidth =
                DestinationWidth();
            AssertWidth(
                1121,
                expectedDockedWidth: 64,
                overlayVisible: true);
            Assert.True(
                DestinationWidth() >=
                previousContentWidth,
                "The overlay presentation must remain monotonic above the breakpoint until the user changes it.");

            window.FindControl<Button>(
                    "HomeNav")!
                .Focus();
            window.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Escape,
                });
            Render();
            Assert.False(
                scrim.IsEffectivelyVisible);
            Assert.Equal(
                64,
                body.ColumnDefinitions[0]
                    .ActualWidth);
            Assert.Same(
                toggle,
                window.FocusManager!
                    .GetFocusedElement());
            Assert.True(
                AppearancePreferences
                    .GetShellRailExpanded(
                        settings));

            toggle.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Render();
            Assert.Equal(
                220,
                body.ColumnDefinitions[0]
                    .ActualWidth);
            Assert.False(
                scrim.IsEffectivelyVisible);

            toggle.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Render();
            Assert.False(
                AppearancePreferences
                    .GetShellRailExpanded(
                        settings));
            SetShellWidth(1119);
            toggle.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Render();
            Assert.True(
                scrim.IsEffectivelyVisible);
            Assert.Equal(
                64,
                body.ColumnDefinitions[0]
                    .ActualWidth);
            Assert.Equal(
                220,
                rail.Bounds.Width);

            Point scrimPoint =
                scrim.TranslatePoint(
                    new Point(
                        Math.Max(
                            2,
                            scrim.Bounds.Width / 2),
                        Math.Max(
                            2,
                            scrim.Bounds.Height / 2)),
                    window)!.Value;
            window.MouseDown(
                scrimPoint,
                MouseButton.Left,
                RawInputModifiers.None);
            window.MouseUp(
                scrimPoint,
                MouseButton.Left,
                RawInputModifiers.None);
            Render();
            Assert.False(
                scrim.IsEffectivelyVisible);
            Assert.Same(
                toggle,
                window.FocusManager!
                    .GetFocusedElement());

            void AssertWidth(
                double shellWidth,
                double expectedDockedWidth,
                bool overlayVisible)
            {
                SetShellWidth(shellWidth);
                Assert.InRange(
                    body.Bounds.Width,
                    shellWidth - 0.1,
                    shellWidth + 0.1);
                Assert.Equal(
                    expectedDockedWidth,
                    body.ColumnDefinitions[0]
                        .ActualWidth);
                Assert.Equal(
                    overlayVisible,
                    scrim.IsEffectivelyVisible);
                Assert.Equal(
                    overlayVisible
                        ? 220
                        : expectedDockedWidth,
                    rail.Bounds.Width);
                Assert.Equal(
                    overlayVisible ||
                    expectedDockedWidth == 220,
                    brand.IsEffectivelyVisible);
            }

            void SetShellWidth(
                double shellWidth)
            {
                window.Width +=
                    shellWidth -
                    body.Bounds.Width;
                Render();
                double correction =
                    shellWidth -
                    body.Bounds.Width;
                if (Math.Abs(correction) >
                    0.01)
                {
                    window.Width +=
                        correction;
                    Render();
                }
            }

            double DestinationWidth() =>
                body.ColumnDefinitions[1]
                    .ActualWidth;
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Compact_density_reduces_semantic_form_geometry_without_shrinking_navigation_targets()
    {
        var contentStack = new StackPanel();
        contentStack.Classes.Add(
            "content-stack");
        var firstField = CreateField(
            "Output folder",
            "C:\\Music\\Exports");
        var secondField = CreateField(
            "Naming template",
            "{Name}{Extension}");
        var supporting = new TextBlock
        {
            Text =
                "Supporting guidance remains readable while using less vertical space.",
            TextWrapping =
                global::Avalonia.Media
                    .TextWrapping.Wrap,
        };
        supporting.Classes.Add(
            "supporting");
        contentStack.Children.Add(
            firstField);
        contentStack.Children.Add(
            secondField);
        contentStack.Children.Add(
            supporting);

        var card = new Border
        {
            Child = contentStack,
        };
        card.Classes.Add("card");
        card.Classes.Add("dense-card");
        var navigation = new Button
        {
            Content = "Library",
        };
        navigation.Classes.Add("nav");
        var icon = new Button
        {
            Content = "\u22ef",
        };
        icon.Classes.Add("app");
        icon.Classes.Add("icon");
        var root = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                navigation,
                icon,
                card,
            },
        };
        var window = new Window
        {
            Width = 520,
            Height = 500,
            Content = root,
        };
        window.Classes.Add(
            "density-standard");
        try
        {
            window.Show();
            Render();

            double standardCardHeight =
                card.Bounds.Height;
            double standardStackSpacing =
                contentStack.Spacing;
            double standardFieldSpacing =
                firstField.Spacing;
            double standardSupportingFont =
                supporting.FontSize;
            double standardNavigationHeight =
                navigation.Bounds.Height;
            Size standardIconSize =
                icon.Bounds.Size;

            window.Classes.Remove(
                "density-standard");
            window.Classes.Add(
                "density-compact");
            Render();

            Assert.True(
                card.Bounds.Height <
                standardCardHeight,
                "Compact density should measurably reduce rendered form height.");
            Assert.True(
                contentStack.Spacing <
                standardStackSpacing);
            Assert.True(
                firstField.Spacing <
                standardFieldSpacing);
            Assert.True(
                supporting.FontSize <
                standardSupportingFont);
            Assert.Equal(
                standardNavigationHeight,
                navigation.Bounds.Height);
            Assert.Equal(
                standardIconSize,
                icon.Bounds.Size);
            Assert.Equal(
                44,
                navigation.Bounds.Height);
            Assert.Equal(
                new Size(36, 36),
                icon.Bounds.Size);
        }
        finally
        {
            window.Hide();
        }

        static StackPanel CreateField(
            string label,
            string value)
        {
            var field = new StackPanel();
            field.Classes.Add("field");
            var labelText = new TextBlock
            {
                Text = label,
            };
            labelText.Classes.Add(
                "field-label");
            var input = new TextBox
            {
                Text = value,
            };
            input.Classes.Add("app");
            field.Children.Add(labelText);
            field.Children.Add(input);
            return field;
        }
    }

    [AvaloniaFact]
    public void Library_contextual_setup_state_suppresses_duplicate_footer_guidance()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        var view = new LibraryView();
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = view,
        };
        try
        {
            window.Show();
            LibraryViewModel model =
                services.GetRequiredService<
                    LibraryViewModel>();
            model.Rows = [];
            model.PageState =
                LibraryPageState
                    .NoConfiguration;
            Render();

            Assert.True(
                view.FindControl<Border>(
                    "LibraryEmptyState")!
                    .IsEffectivelyVisible);
            Assert.False(
                view.FindControl<StackPanel>(
                    "LibraryFooterGuidance")!
                    .IsEffectivelyVisible);
            Assert.False(
                view.FindControl<TextBlock>(
                    "LibraryIndexingFooterGuidance")!
                    .IsEffectivelyVisible);

            model.PageState =
                LibraryPageState.Error;
            Render();

            Assert.True(
                view.FindControl<StackPanel>(
                    "LibraryFooterGuidance")!
                    .IsEffectivelyVisible);
            Assert.True(
                view.FindControl<TextBlock>(
                    "LibraryIndexingFooterGuidance")!
                    .IsEffectivelyVisible);
        }
        finally
        {
            window.Hide();
        }
    }

    private static ServiceProvider BuildServices() =>
        BuildServices(new TestSettings());

    private static ServiceProvider BuildServices(
        TestSettings settings) =>
        Composition.BuildServices(collection =>
        {
            collection.AddSingleton<IAppSettings>(
                settings);
            collection.AddSingleton<IWorkbenchService>(
                new TestWorkbenchService());
        });

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class TestSettings : IAppSettings
    {
        private readonly Dictionary<string, string>
            _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration => null;
        public event EventHandler? ConfigurationChanged;

        public AppConfigurationSnapshot GetSnapshot() =>
            new(null, null, 0);

        public void LoadConfig(string path) =>
            ConfigurationChanged?.Invoke(
                this,
                EventArgs.Empty);

        public string? GetRememberedConfigPath() => null;
        public IReadOnlyList<string> RecentConfigPaths => [];
        public void ClearRecentConfigs()
        {
        }

        public string? GetPreference(string key) =>
            _preferences.GetValueOrDefault(key);

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
                            new MediaDocument(
                                Path.GetFullPath(
                                    source),
                                [],
                                [],
                                null,
                                new(
                                    Path.GetFullPath(
                                        source),
                                    10,
                                    DateTime.UtcNow,
                                    "snapshot"),
                                true)),
                ],
                []));
        }
    }
}
