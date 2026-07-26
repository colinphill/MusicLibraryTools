using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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

public sealed class LibraryDrawerInteractionTests
{
    [AvaloniaFact]
    public void
        Localized_pending_actions_wrap_inside_the_narrow_library_flyout()
    {
        CultureInfo previousUICulture =
            CultureInfo.CurrentUICulture;
        var settings = new MemorySettings();
        settings.SetPreference(
            LocalizationPreferences.DisplayLanguage,
            "de-DE");
        settings.SetPreference(
            AppearancePreferences.ShellRailExpandedPreference,
            bool.FalseString);
        using ServiceProvider services =
            Composition.BuildServices(collection =>
                collection.AddSingleton<IAppSettings>(
                    settings));
        App.UseServicesForTests(services);
        var view = new LibraryView();
        var constrainedHost = new Border
        {
            Width = 344,
            HorizontalAlignment =
                global::Avalonia.Layout.HorizontalAlignment.Left,
            Child = view,
        };
        var window = new Window
        {
            Width = 900,
            Height = 600,
            FontSize = 18,
            Content = constrainedHost,
        };
        try
        {
            window.WindowState = WindowState.Normal;
            window.Show();
            Render();

            LibraryViewModel model =
                services.GetRequiredService<LibraryViewModel>();
            model.PendingChanges.Add(
                new MetadataPreviewRow(
                    @"C:\Music\Fixture.flac",
                    "Titel",
                    "Vorher",
                    "Nachher"));
            view.FindControl<Button>(
                    "LibraryPendingChangesButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Render();

            Border surface =
                view.FindControl<Border>(
                    "LibraryPendingChangesSurface")!;
            Button discard =
                view.FindControl<Button>(
                    "LibraryRevertPendingChangesButton")!;
            Button apply =
                view.FindControl<Button>(
                    "LibraryApplyPendingChangesButton")!;
            Assert.True(
                view.FindControl<Popup>(
                    "LibraryPendingChangesPopover")!.IsOpen);
            Assert.InRange(
                surface.Bounds.Width,
                319.5,
                320.5);
            WrapPanel actionPanel =
                Assert.IsType<WrapPanel>(
                discard.Parent);
            Assert.Same(
                actionPanel,
                apply.Parent);
            AssertFullyVisible(
                surface,
                discard);
            AssertFullyVisible(
                surface,
                apply);
            Point? discardOrigin =
                discard.TranslatePoint(
                    default,
                    actionPanel);
            Point? applyOrigin =
                apply.TranslatePoint(
                    default,
                    actionPanel);
            Assert.NotNull(
                discardOrigin);
            Assert.NotNull(
                applyOrigin);
            Assert.True(
                applyOrigin.Value.Y >=
                discardOrigin.Value.Y +
                discard.Bounds.Height - 1,
                "The long German Discard action and Apply action did not wrap onto separate rows in the 320 px flyout.");
        }
        finally
        {
            window.Hide();
            CultureInfo.CurrentUICulture =
                previousUICulture;
        }

        static void AssertFullyVisible(
            Control surface,
            Control action)
        {
            Assert.True(
                UiViewportReachability
                    .TryGetFullyVisibleBounds(
                        surface,
                        action,
                        out Rect bounds,
                        out string detail),
                $"{action.Name} was clipped in the 320 px German pending-changes flyout: {bounds}. {detail}");
        }
    }

    [AvaloniaFact]
    public void Grid_context_menu_invocation_is_scoped_to_the_focused_library_grid()
    {
        var settings = new MemorySettings();
        using ServiceProvider services =
            Composition.BuildServices(collection =>
                collection.AddSingleton<IAppSettings>(
                    settings));
        App.UseServicesForTests(services);
        var view = new LibraryView();
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

            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "LibraryGrid")!;
            ContextMenu menu =
                Assert.IsType<ContextMenu>(
                    grid.ContextMenu);
            grid.Focus();
            Render();

            AssertOpensFromGrid(
                Key.F10,
                KeyModifiers.Shift);
            AssertOpensFromGrid(
                Key.Apps,
                KeyModifiers.None);

            var contextRequested =
                new ContextRequestedEventArgs
                {
                    RoutedEvent =
                        InputElement
                            .ContextRequestedEvent,
                };
            grid.RaiseEvent(
                contextRequested);
            Render();
            Assert.True(
                contextRequested.Handled);
            Assert.True(
                menu.IsOpen);
            Assert.NotEmpty(
                menu.ItemsSource!
                    .Cast<object>());
            menu.Close();

            Button outsideGrid =
                view.FindControl<Button>(
                    "LibraryPendingChangesButton")!;
            outsideGrid.Focus();
            Render();
            AssertDoesNotOpenOutsideGrid(
                Key.F10,
                KeyModifiers.Shift);
            AssertDoesNotOpenOutsideGrid(
                Key.Apps,
                KeyModifiers.None);

            void AssertOpensFromGrid(
                Key key,
                KeyModifiers modifiers)
            {
                var args = new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = key,
                    KeyModifiers = modifiers,
                };
                grid.RaiseEvent(args);
                Render();

                Assert.True(args.Handled);
                Assert.True(menu.IsOpen);
                Assert.NotEmpty(
                    menu.ItemsSource!
                        .Cast<object>());
                menu.Close();
            }

            void AssertDoesNotOpenOutsideGrid(
                Key key,
                KeyModifiers modifiers)
            {
                var args = new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = key,
                    KeyModifiers = modifiers,
                };
                outsideGrid.RaiseEvent(args);
                Render();

                Assert.False(args.Handled);
                Assert.False(menu.IsOpen);
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Inspector_docking_uses_the_actual_split_host_threshold_and_preserves_the_central_minimum()
    {
        var settings = new MemorySettings();
        using ServiceProvider services =
            Composition.BuildServices(collection =>
                collection.AddSingleton<IAppSettings>(
                    settings));
        App.UseServicesForTests(services);
        var view = new LibraryView();
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = view,
        };
        try
        {
            services.GetRequiredService<
                    LibraryViewModel>()
                .SetInspectorPreference(
                    LibraryInspectorPreference
                        .Pinned);
            window.Show();
            Render();

            PersistedSplitView split =
                view.FindControl<PersistedSplitView>(
                    "WorkspaceSplit")!;
            ContentPresenter left =
                split.FindControl<ContentPresenter>(
                    "LeftPresenter")!;
            ContentPresenter right =
                split.FindControl<ContentPresenter>(
                    "RightPresenter")!;
            GridSplitter splitter =
                split.FindControl<GridSplitter>(
                    "Splitter")!;
            double windowChrome =
                window.Bounds.Width -
                split.Bounds.Width;

            AssertMode(
                1089,
                docked: false);
            AssertMode(
                1090,
                docked: true);
            AssertMode(
                1091,
                docked: true);

            void AssertMode(
                double splitWidth,
                bool docked)
            {
                window.Width =
                    splitWidth +
                    windowChrome;
                Render();
                double correction =
                    splitWidth -
                    split.Bounds.Width;
                if (Math.Abs(correction) >
                    0.01)
                {
                    window.Width +=
                        correction;
                    Render();
                }

                Assert.InRange(
                    split.Bounds.Width,
                    splitWidth - 0.1,
                    splitWidth + 0.1);
                Assert.Equal(
                    docked,
                    splitter.IsEffectivelyVisible);
                Assert.Equal(
                    docked,
                    right.IsEffectivelyVisible);
                if (docked)
                {
                    Assert.True(
                        left.Bounds.Width >=
                        760,
                        $"Docking at a {split.Bounds.Width:0.0} px split host left only {left.Bounds.Width:0.0} px for the central Library task.");
                }
                else
                {
                    Assert.True(
                        view.FindControl<Button>(
                                "InspectorToggle")!
                            .IsEffectivelyVisible);
                }
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Automatic_inspector_follows_selection_while_closed_and_pinned_preferences_override_it()
    {
        var settings = new MemorySettings();
        using ServiceProvider services =
            Composition.BuildServices(collection =>
                collection.AddSingleton<IAppSettings>(
                    settings));
        App.UseServicesForTests(services);
        var view = new LibraryView();
        var window = new Window
        {
            Width = 1300,
            Height = 800,
            Content = view,
        };
        try
        {
            LibraryViewModel model =
                services.GetRequiredService<
                    LibraryViewModel>();
            var row = new LibraryRow(
                new TrackRecord
                {
                    Path =
                        Path.GetFullPath(
                            "auto-inspector.flac"),
                    Title = "Automatic inspector",
                });
            model.Rows = [row];
            model.PageState =
                LibraryPageState.Ready;
            model.SetInspectorPreference(
                LibraryInspectorPreference.Auto);

            window.Show();
            Render();

            PersistedSplitView split =
                view.FindControl<PersistedSplitView>(
                    "WorkspaceSplit")!;
            ContentPresenter right =
                split.FindControl<ContentPresenter>(
                    "RightPresenter")!;
            Assert.False(
                right.IsEffectivelyVisible);

            Assert.True(
                await model.SelectAsync([row]));
            Render();
            Assert.True(
                right.IsEffectivelyVisible);
            Assert.True(
                split.FindControl<GridSplitter>(
                        "Splitter")!
                    .IsEffectivelyVisible);

            Assert.True(
                await model.SelectAsync([]));
            Render();
            Assert.False(
                right.IsEffectivelyVisible);

            model.SetInspectorPreference(
                LibraryInspectorPreference.Pinned);
            Render();
            Assert.True(
                right.IsEffectivelyVisible);

            Assert.True(
                await model.SelectAsync([row]));
            model.SetInspectorPreference(
                LibraryInspectorPreference.Closed);
            Render();
            Assert.False(
                right.IsEffectivelyVisible);
            Assert.True(
                view.FindControl<Button>(
                        "InspectorToggle")!
                    .IsEffectivelyVisible);

            model.SetInspectorPreference(
                LibraryInspectorPreference.Auto);
            Render();
            Assert.True(
                right.IsEffectivelyVisible);

            window.Width = 900;
            Render();
            Assert.False(
                right.IsEffectivelyVisible);
            Assert.True(
                view.FindControl<Button>(
                        "InspectorToggle")!
                    .IsEffectivelyVisible);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Columns_and_visual_filter_overlays_clamp_to_the_library_viewport()
    {
        using ServiceProvider services =
            Composition.BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.MinWidth = 0;
            window.MinHeight = 0;
            window.Width = 700;
            window.Height = 600;
            window.Show();
            window.WindowState =
                WindowState.Normal;
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Library);
            Render();

            LibraryView view =
                Assert.IsType<LibraryView>(
                    window.FindControl<ContentControl>(
                        "ContentHost")!.Content);
            Border columns =
                view.FindControl<Border>(
                    "LibraryColumnsSurface")!;
            Border visualFilter =
                view.FindControl<Border>(
                    "LibraryVisualFilterSurface")!;
            double availableWidth =
                Math.Max(
                    320,
                    view.Bounds.Width - 24);
            double availableHeight =
                Math.Max(
                    320,
                    view.Bounds.Height - 32);

            Assert.Equal(
                Math.Min(
                    650,
                    availableWidth),
                columns.Width);
            Assert.Equal(
                Math.Min(
                    610,
                    availableHeight),
                columns.MaxHeight);
            Assert.Equal(
                Math.Min(
                    720,
                    availableWidth),
                visualFilter.Width);
            Assert.Equal(
                Math.Min(
                    620,
                    availableHeight),
                visualFilter.MaxHeight);

            view.FindControl<Button>(
                    "ColumnsButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Render();
            Assert.True(
                view.FindControl<Popup>(
                    "ColumnPopover")!.IsOpen);
            Assert.True(
                columns.Bounds.Width <=
                availableWidth);

            view.FindControl<Button>(
                    "CloseColumnsButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            view.FindControl<Button>(
                    "VisualFilterButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Render();
            Assert.True(
                view.FindControl<Popup>(
                    "VisualFilterPopover")!
                    .IsOpen);
            Assert.True(
                visualFilter.Bounds.Width <=
                availableWidth);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Compact_inspector_supports_pointer_escape_close_and_focus_restoration()
    {
        using ServiceProvider services =
            Composition.BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.WindowState =
                WindowState.Normal;
            window.Width = 900;
            window.Height = 600;
            window.Activate();
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Library);
            Render();

            LibraryView view =
                Assert.IsType<LibraryView>(
                    window.FindControl<ContentControl>(
                        "ContentHost")!.Content);
            LibraryViewModel model =
                services.GetRequiredService<
                    LibraryViewModel>();
            Button toggle =
                view.FindControl<Button>(
                    "InspectorToggle")!;
            Border scrim =
                view.FindControl<Border>(
                    "InspectorScrim")!;
            Control close =
                view.FindControl<
                        SelectionInspectorView>(
                        "InspectorView")!
                    .CloseButton;
            PersistedSplitView split =
                view.FindControl<PersistedSplitView>(
                    "WorkspaceSplit")!;
            ContentPresenter right =
                split.FindControl<ContentPresenter>(
                    "RightPresenter")!;

            Assert.True(toggle.IsVisible);
            Assert.False(
                scrim.IsVisible,
                "The compact inspector scrim should start closed.");

            OpenDrawer(toggle);
            Assert.True(scrim.IsVisible);
            Assert.True(right.IsVisible);
            Assert.Same(
                close,
                Focused(view));

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
            IInputElement? hit =
                window.InputHitTest(
                    scrimPoint);
            Assert.True(
                hit is Control hitControl &&
                (ReferenceEquals(
                     hitControl,
                     scrim) ||
                 hitControl.GetVisualAncestors()
                     .Contains(scrim)),
                $"Expected the Library inspector scrim at {scrimPoint}, but hit {(hit as Control)?.Name ?? hit?.GetType().Name ?? "nothing"} within {string.Join("/", (hit as Control)?.GetVisualAncestors().OfType<Control>().Select(control => control.Name ?? control.GetType().Name) ?? [])}.");
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
                scrim.IsVisible,
                "Scrim dismissal should hide the inspector scrim.");
            Assert.False(
                right.IsVisible,
                "Scrim dismissal should hide the compact inspector presenter.");
            Assert.True(model.IsInspectorOpen);
            Assert.Same(
                toggle,
                Focused(view));

            OpenDrawer(toggle);
            view.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Escape,
                });
            Render();

            Assert.False(
                scrim.IsVisible,
                "Escape should hide the inspector scrim.");
            Assert.False(
                right.IsVisible,
                "Escape should hide the compact inspector presenter.");
            Assert.True(model.IsInspectorOpen);
            Assert.Same(
                toggle,
                Focused(view));

            OpenDrawer(toggle);
            close.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Render();

            Assert.False(
                scrim.IsVisible,
                "The inspector close action should hide its scrim.");
            Assert.False(
                right.IsVisible,
                "The inspector close action should hide its presenter.");
            Assert.False(
                model.IsInspectorOpen,
                "The inspector close action should persist the closed preference.");
            Assert.Same(
                toggle,
                Focused(view));
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Compact_inspector_traps_tab_within_effectively_available_drawer_controls()
    {
        using ServiceProvider services =
            Composition.BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.WindowState =
                WindowState.Normal;
            window.Width = 900;
            window.Height = 600;
            window.Activate();
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Library);
            Render();

            LibraryView view =
                Assert.IsType<LibraryView>(
                    window.FindControl<ContentControl>(
                        "ContentHost")!.Content);
            SelectionInspectorView inspector =
                view.FindControl<
                    SelectionInspectorView>(
                    "InspectorView")!;
            inspector.DataContext =
                new InspectorFocusFixture();
            Render();
            OpenDrawer(
                view.FindControl<Button>(
                    "InspectorToggle")!);

            Control[] allFocusable =
            [
                .. inspector
                    .GetVisualDescendants()
                    .OfType<Control>()
                    .Where(control =>
                        control.Focusable),
            ];
            Control[] effectiveFocusable =
            [
                .. allFocusable.Where(control =>
                    control.IsEffectivelyEnabled &&
                    control.IsEffectivelyVisible),
            ];
            Assert.True(
                effectiveFocusable.Length >= 3,
                "The populated inspector fixture must exercise more than a one-control focus loop.");
            Assert.Contains(
                allFocusable,
                control =>
                    !control.IsEffectivelyEnabled ||
                    !control.IsEffectivelyVisible);

            AssertWraps(
                effectiveFocusable[^1],
                KeyModifiers.None,
                effectiveFocusable[0]);
            AssertWraps(
                effectiveFocusable[0],
                KeyModifiers.Shift,
                effectiveFocusable[^1]);

            Button outside =
                view.FindControl<Button>(
                    "InspectorToggle")!;
            AssertWraps(
                outside,
                KeyModifiers.None,
                effectiveFocusable[0]);

            void AssertWraps(
                Control startingControl,
                KeyModifiers modifiers,
                Control expected)
            {
                startingControl.Focus();
                Render();
                view.RaiseEvent(
                    new KeyEventArgs
                    {
                        RoutedEvent =
                            InputElement.KeyDownEvent,
                        Key = Key.Tab,
                        KeyModifiers = modifiers,
                    });
                Render();

                Assert.Same(
                    expected,
                    Focused(view));
                Assert.True(
                    expected.IsEffectivelyEnabled);
                Assert.True(
                    expected.IsEffectivelyVisible);
            }
        }
        finally
        {
            window.Hide();
        }
    }

    private static void OpenDrawer(
        Button toggle)
    {
        if (TopLevel.GetTopLevel(toggle) is
                MainWindow window &&
            window.FindControl<Border>(
                "NavigationScrim") is
                { IsEffectivelyVisible: true })
        {
            window.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Escape,
                });
            Render();
        }
        toggle.Focus();
        Render();
        toggle.RaiseEvent(
            new RoutedEventArgs(
                Button.ClickEvent));
        Render();
    }

    private static IInputElement? Focused(
        Control control) =>
        TopLevel.GetTopLevel(control)!
            .FocusManager!
            .GetFocusedElement();

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
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

    private sealed class InspectorFocusFixture
    {
        public bool HasSelection => true;
        public bool HasUnsavedChanges => false;
        public string UnsavedChangesSummary => "";
        public string SelectionSummary =>
            "Two selected tracks";
        public string Overview =>
            "Editable metadata";
        public bool IsBusy => false;
        public InspectorFieldFixture[] Fields { get; } =
        [
            new(
                "Title",
                "Aurora"),
            new(
                "Artist",
                "The Fixtures"),
        ];
        public bool HasStatusMessage => false;
        public bool HasStatusDiagnosticDetail =>
            false;
        public bool IsStatusInfo => true;
        public bool IsStatusSuccess => false;
        public bool IsStatusWarning => false;
        public bool IsStatusError => false;
        public string StatusIcon => "";
        public string StatusMessage => "";
        public string StatusDiagnosticDetail => "";
        public string ArtworkSummary =>
            "No embedded artwork";
        public bool IsArtworkMixed => false;
        public object[] ArtworkItems => [];
        public object[] ArtworkTypeChoices => [];
        public int ArtworkMaxDimension => 600;
    }

    private sealed record InspectorFieldFixture(
        string Label,
        string Value)
    {
        public string PlaceholderText => "";
        public string VerificationMessage => "";
        public bool IsUnverified => false;
    }
}
