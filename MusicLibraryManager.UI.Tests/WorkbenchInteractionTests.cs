using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
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

public sealed class WorkbenchInteractionTests
{
    [AvaloniaFact]
    public void Session_context_menu_keys_are_scoped_to_the_focused_grid()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 1200, 700);
            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "WorkbenchGrid")!;
            ContextMenu menu = Assert.IsType<ContextMenu>(
                grid.ContextMenu);
            grid.Focus();
            Render();

            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.F10,
                KeyModifiers = KeyModifiers.Shift,
            });
            Render();
            Assert.True(menu.IsOpen);
            menu.Close();

            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Apps,
            });
            Render();
            Assert.True(menu.IsOpen);
            menu.Close();

            Button outsideGrid =
                view.FindControl<Button>(
                    "WorkbenchPendingChangesButton")!;
            outsideGrid.Focus();
            Render();
            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Apps,
            });
            Render();
            Assert.False(menu.IsOpen);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task TranscodeCommandsOpenDrawerFromBothSelectionMenus()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 1200, 700);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            ILocalizationService localization =
                services.GetRequiredService<
                    ILocalizationService>();
            var first = Track("first.flac");
            var second = Track("second.flac");
            model.Files.Add(first);
            model.Files.Add(second);
            model.SetSelectedFiles([first, second]);
            Render();

            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "WorkbenchGrid")!;
            Control drawer =
                view.FindControl<Control>(
                    "WorkbenchTranscodeDrawer")!;
            ContextMenu contextMenu =
                Assert.IsType<ContextMenu>(
                    grid.ContextMenu);
            contextMenu.Open(grid);
            Render();
            MenuItem contextTranscode =
                FindTranscodeItem(
                    contextMenu.Items,
                    localization);

            contextTranscode.RaiseEvent(
                new RoutedEventArgs(
                    MenuItem.ClickEvent));
            await Task.Yield();
            Render();

            Assert.True(drawer.IsVisible);
            Assert.True(
                model.TranscodeEditor!.HasSelection);
            Assert.Contains(
                "2",
                model.TranscodeEditor
                    .CapturedSelectionSummary);
            contextMenu.Close();
            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent =
                    InputElement.KeyDownEvent,
                Key = Key.Escape,
            });
            Render();
            Assert.False(drawer.IsVisible);

            Button selectionActions =
                view.FindControl<Button>(
                    "WorkbenchSessionActionsButton")!;
            MenuFlyout flyout =
                Assert.IsType<MenuFlyout>(
                    selectionActions.Flyout);
            flyout.ShowAt(selectionActions);
            Render();
            MenuItem flyoutTranscode =
                FindTranscodeItem(
                    flyout.Items,
                    localization);

            flyoutTranscode.RaiseEvent(
                new RoutedEventArgs(
                    MenuItem.ClickEvent));
            await Task.Yield();
            Render();

            Assert.True(drawer.IsVisible);
            Assert.True(
                model.TranscodeEditor.HasSelection);
            Assert.Contains(
                "2",
                model.TranscodeEditor
                    .CapturedSelectionSummary);
            flyout.Hide();
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void RightClickPreservesSelectedRowsAndSelectsAnUnselectedRow()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 1200, 700);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            var first = Track("first.flac");
            var second = Track("second.flac");
            var third = Track("third.flac");
            model.Files.Add(first);
            model.Files.Add(second);
            model.Files.Add(third);
            Render();
            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "WorkbenchGrid")!;
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(first);
            grid.SelectedItems.Add(second);
            grid.SelectedItem = first;
            model.SetSelectedFiles([first, second]);
            Render();

            RightClickRow(window, grid, first);
            Render();

            Assert.Equal(
                2,
                grid.SelectedItems.Count);
            Assert.Equal(
                [first, second],
                model.SelectedFiles);
            grid.ContextMenu?.Close();

            RightClickRow(window, grid, third);
            Render();

            Assert.Same(
                third,
                Assert.Single(
                    grid.SelectedItems));
            Assert.Same(
                third,
                Assert.Single(
                    model.SelectedFiles));
            grid.ContextMenu?.Close();
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Pointer_press_on_scrim_closes_transient_drawer_resumes_inspector_and_restores_focus()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 900, 640);
            Button inspectorButton =
                view.FindControl<Button>(
                    "WorkbenchInspectorToggle")!;
            Button pendingButton =
                view.FindControl<Button>(
                    "WorkbenchPendingChangesButton")!;
            Control inspectorDrawer =
                view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!;
            Control pendingDrawer =
                view.FindControl<Control>(
                    "WorkbenchPendingChangesDrawer")!;
            Border scrim =
                view.FindControl<Border>(
                    "WorkbenchHeaderScrim")!;

            inspectorButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            Render();
            pendingButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            Render();
            Assert.True(pendingDrawer.IsVisible);
            Assert.False(inspectorDrawer.IsVisible);
            Assert.True(scrim.IsVisible);

            Point clickPoint =
                scrim.TranslatePoint(
                    new Point(
                        Math.Max(2, scrim.Bounds.Width / 2),
                        Math.Max(2, scrim.Bounds.Height / 2)),
                    window) ??
                throw new InvalidOperationException(
                    "The Workbench scrim was not attached.");
            window.MouseDown(
                clickPoint,
                MouseButton.Left,
                RawInputModifiers.None);
            window.MouseUp(
                clickPoint,
                MouseButton.Left,
                RawInputModifiers.None);
            Render();

            Assert.False(pendingDrawer.IsVisible);
            Assert.True(inspectorDrawer.IsVisible);
            Assert.True(scrim.IsVisible);
            Assert.Same(
                pendingButton,
                window.FocusManager!
                    .GetFocusedElement());
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Drawer_tab_cycle_excludes_hidden_drawer_surfaces()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 900, 640);
            view.FindControl<Button>(
                    "WorkbenchPendingChangesButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Render();

            Control pendingDrawer =
                view.FindControl<Control>(
                    "WorkbenchPendingChangesDrawer")!;
            Control hiddenInspector =
                view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!;
            Control[] visibleFocusCycle =
            [
                .. pendingDrawer
                    .GetVisualDescendants()
                    .OfType<Control>()
                    .Where(control =>
                        control.IsEffectivelyVisible &&
                        control.IsEffectivelyEnabled &&
                        control.Focusable),
            ];
            Assert.NotEmpty(visibleFocusCycle);

            AssertCyclesWithinVisibleDrawer(
                visibleFocusCycle[^1],
                KeyModifiers.None);
            AssertCyclesWithinVisibleDrawer(
                visibleFocusCycle[0],
                KeyModifiers.Shift);

            void AssertCyclesWithinVisibleDrawer(
                Control boundary,
                KeyModifiers modifiers)
            {
                boundary.Focus();
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

                Control focused =
                    Assert.IsAssignableFrom<Control>(
                        window.FocusManager!
                            .GetFocusedElement());
                Assert.True(
                    ReferenceEquals(
                        focused,
                        pendingDrawer) ||
                    focused.GetVisualAncestors()
                        .Contains(pendingDrawer));
                Assert.False(
                    ReferenceEquals(
                        focused,
                        hiddenInspector) ||
                    focused.GetVisualAncestors()
                        .Contains(hiddenInspector));
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Inspector_drawer_cycles_focus_forward_and_reverse()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 900, 640);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            WorkbenchTrackViewModel track =
                Track("inspector-focus-cycle.flac");
            model.Files.Add(track);
            Assert.True(
                await model.TrySetSelectedFilesAsync(
                    [track]));
            Render();

            view.FindControl<Button>(
                    "WorkbenchInspectorToggle")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Render();

            Control inspectorDrawer =
                view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!;
            Assert.True(
                inspectorDrawer.IsEffectivelyVisible);
            AssertDrawerCyclesForwardAndReverse(
                window,
                view,
                inspectorDrawer);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Columns_drawer_cycles_focus_forward_and_reverse()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 900, 640);
            view.FindControl<Button>(
                    "WorkbenchColumnsButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Render();

            Control columnsDrawer =
                view.FindControl<Control>(
                    "WorkbenchColumnsDrawer")!;
            Assert.True(
                columnsDrawer.IsEffectivelyVisible);
            AssertDrawerCyclesForwardAndReverse(
                window,
                view,
                columnsDrawer);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Every_bulk_operation_descriptor_exposes_exactly_its_contextual_panels()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 1440, 900);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            model.SelectedSection =
                WorkbenchSection.BulkOperation;
            Render();

            var panels = new Dictionary<string, Control>
            {
                ["Destination"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationDestinationPanel")!,
                ["Secondary"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationSecondaryPanel")!,
                ["Value"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationValuePanel")!,
                ["Find"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationFindPanel")!,
                ["Replacement"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationReplacementPanel")!,
                ["Case"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationCasePanel")!,
                ["Separator"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationSeparatorPanel")!,
                ["ValueOrder"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationValueOrderPanel")!,
                ["Sequence"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationSequencePanel")!,
                ["Path"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationPathPanel")!,
                ["ParentLevel"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationParentLevelPanel")!,
                ["ExtractionPattern"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationExtractionPatternPanel")!,
                ["CaptureGroup"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationCaptureGroupPanel")!,
                ["RegularExpression"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationRegularExpressionOption")!,
            };

            Assert.NotEmpty(
                model.OperationEditor.OperationDescriptors);
            foreach (MetadataOperationDescriptor descriptor in
                     model.OperationEditor.OperationDescriptors)
            {
                model.OperationEditor.SelectedOperation =
                    descriptor;
                Render();
                HashSet<string> expected =
                    ExpectedPanels(descriptor.Kind);
                foreach ((string name, Control panel) in panels)
                    Assert.True(
                        panel.IsVisible ==
                        expected.Contains(name),
                        $"{descriptor.Kind}: panel {name} was {(panel.IsVisible ? "visible" : "hidden")}.");
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Transcode_drawer_stays_in_bounds_and_restores_focus_after_escape()
    {
        using ServiceProvider services = BuildServices();
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
            var track = new WorkbenchTrackViewModel(
                new MediaDocument(
                    "transcode-source.flac",
                    [],
                    [],
                    null,
                    new(
                        "transcode-source.flac",
                        10,
                        DateTime.UtcNow,
                        "hash"),
                    true));
            model.Files.Add(track);
            model.SetSelectedFiles([track]);
            Button focusOwner =
                view.FindControl<Button>(
                    "WorkbenchSessionActionsButton")!;
            Control drawer =
                view.FindControl<Control>(
                    "WorkbenchTranscodeDrawer")!;
            Button closeButton =
                view.FindControl<Button>(
                    "WorkbenchTranscodeCloseButton")!;
            ComboBox format =
                view.FindControl<ComboBox>(
                    "WorkbenchTranscodeFormat")!;
            ComboBox encoder =
                view.FindControl<ComboBox>(
                    "WorkbenchTranscodeEncoder")!;
            ComboBox rateMode =
                view.FindControl<ComboBox>(
                    "WorkbenchTranscodeRateMode")!;

            foreach ((double width, double height) in
                     new[]
                     {
                         (900d, 600d),
                         (1200d, 700d),
                         (1440d, 900d),
                     })
            {
                window.Width = width;
                window.Height = height;
                Render();

                Assert.True(
                    await view
                        .OpenTranscodeDrawerAsync());
                Render();

                Assert.True(drawer.IsVisible);
                Assert.InRange(
                    drawer.Bounds.Width,
                    300,
                    430);
                Assert.True(
                    drawer.Bounds.Height <=
                    view.Bounds.Height + 1);
                Assert.Equal(
                    AudioTranscodeFormatIds.Flac,
                    model.TranscodeEditor!
                        .SelectedFormatId);
                Assert.Equal(
                    AudioTranscodeEncoderIds.Automatic,
                    model.TranscodeEditor
                        .SelectedEncoderId);
                Assert.Equal(
                    AudioTranscodeRateMode.Lossless,
                    model.TranscodeEditor
                        .SelectedRateMode);
                Assert.Equal(
                    AudioTranscodeFormatIds.Flac,
                    Assert.IsType<
                        LocalizedChoice<string>>(
                        format.SelectedItem)
                        .Value);
                Assert.Equal(
                    AudioTranscodeEncoderIds.Automatic,
                    Assert.IsType<
                        LocalizedChoice<string>>(
                        encoder.SelectedItem)
                        .Value);
                Assert.Equal(
                    AudioTranscodeRateMode.Lossless,
                    Assert.IsType<
                        LocalizedChoice<
                            AudioTranscodeRateMode>>(
                        rateMode.SelectedItem)
                        .Value);
                Assert.Same(
                    closeButton,
                    window.FocusManager!
                        .GetFocusedElement());
                CaptureTranscodeDrawer(
                    window,
                    (int)width,
                    (int)height);

                view.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Escape,
                });
                Render();

                Assert.False(drawer.IsVisible);
                Assert.Same(
                    focusOwner,
                    window.FocusManager!
                        .GetFocusedElement());
            }
        }
        finally
        {
            window.Hide();
        }
    }

    private static void CaptureTranscodeDrawer(
        MainWindow window,
        int width,
        int height)
    {
        string? captureDirectory =
            Environment.GetEnvironmentVariable(
                "MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(
                captureDirectory))
            return;
        using var frame =
            window.GetLastRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(
            captureDirectory);
        frame.Save(
            Path.Combine(
                captureDirectory,
                $"workbench-transcode-drawer-" +
                $"{width}x{height}.png"),
            PngBitmapEncoderOptions.Default);
    }

    private static void AssertDrawerCyclesForwardAndReverse(
        MainWindow window,
        WorkbenchView view,
        Control drawer)
    {
        Control[] focusable =
        [
            .. drawer
                .GetVisualDescendants()
                .OfType<Control>()
                .Where(control =>
                    control.IsEffectivelyVisible &&
                    control.IsEffectivelyEnabled &&
                    control.Focusable),
        ];
        Assert.True(
            focusable.Length >= 3,
            $"Expected a populated {drawer.Name} focus cycle, but found " +
            $"{focusable.Length} effective target(s).");

        AssertWraps(
            focusable[^1],
            KeyModifiers.None,
            focusable[0]);
        AssertWraps(
            focusable[0],
            KeyModifiers.Shift,
            focusable[^1]);

        void AssertWraps(
            Control startingControl,
            KeyModifiers modifiers,
            Control expected)
        {
            startingControl.Focus();
            Render();
            Assert.Same(
                startingControl,
                window.FocusManager!
                    .GetFocusedElement());

            view.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Tab,
                    KeyModifiers = modifiers,
                });
            Render();

            Control focused =
                Assert.IsAssignableFrom<Control>(
                    window.FocusManager!
                        .GetFocusedElement());
            Assert.Same(
                expected,
                focused);
            Assert.True(
                ReferenceEquals(
                    focused,
                    drawer) ||
                focused.GetVisualAncestors()
                    .Contains(drawer));
        }
    }

    private static HashSet<string> ExpectedPanels(
        MetadataOperationKind kind) => kind switch
        {
            MetadataOperationKind.Assign =>
                ["Value"],
            MetadataOperationKind.Copy =>
                ["Destination"],
            MetadataOperationKind.ReplaceText =>
                ["Find", "Replacement", "RegularExpression"],
            MetadataOperationKind.ChangeCase =>
                ["Case"],
            MetadataOperationKind.Sequence =>
                ["Sequence"],
            MetadataOperationKind.Combine =>
                ["Destination", "Secondary", "Separator"],
            MetadataOperationKind.Split =>
                ["Separator", "RegularExpression"],
            MetadataOperationKind.Join =>
                ["Separator"],
            MetadataOperationKind.Reorder =>
                ["ValueOrder"],
            MetadataOperationKind.ExtractPathComponent =>
            [
                "Path",
                "ParentLevel",
                "ExtractionPattern",
                "CaptureGroup",
            ],
            _ => [],
        };

    private static WorkbenchView ShowWorkbench(
        MainWindow window,
        IServiceProvider services,
        double width,
        double height)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Width = width;
        window.Height = height;
        services.GetRequiredService<INavigationService>()
            .Navigate(ShellDestination.Workbench);
        Render();
        return Assert.IsType<WorkbenchView>(
            window.FindControl<ContentControl>(
                "ContentHost")!.Content);
    }

    private static WorkbenchTrackViewModel Track(
        string path) =>
        new(
            new MediaDocument(
                Path.GetFullPath(path),
                [],
                [],
                null,
                new(
                    Path.GetFullPath(path),
                    10,
                    DateTime.UtcNow,
                    "hash"),
                true));

    private static MenuItem FindTranscodeItem(
        IEnumerable<object?> items,
        ILocalizationService localization) =>
        items
            .OfType<MenuItem>()
            .Single(item =>
                string.Equals(
                    item.Header?.ToString(),
                    localization.Get(
                        "Transcode.Action.Open"),
                    StringComparison.Ordinal));

    private static void RightClickRow(
        MainWindow window,
        AppDataGrid grid,
        WorkbenchTrackViewModel item)
    {
        DataGridRow row =
            grid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Single(candidate =>
                    ReferenceEquals(
                        candidate.DataContext,
                        item));
        Point point =
            row.TranslatePoint(
                new Point(
                    Math.Min(
                        12,
                        Math.Max(
                            2,
                            row.Bounds.Width / 2)),
                    Math.Max(
                        2,
                        row.Bounds.Height / 2)),
                window) ??
            throw new InvalidOperationException(
                "The Workbench row was not attached.");
        window.MouseDown(
            point,
            MouseButton.Right,
            RawInputModifiers.None);
        window.MouseUp(
            point,
            MouseButton.Right,
            RawInputModifiers.None);
    }

    private static ServiceProvider BuildServices()
    {
        var settings = new TestSettings();
        settings.SetPreference(
            AppearancePreferences
                .ShellRailExpandedPreference,
            bool.FalseString);
        return Composition.BuildServices(collection =>
        {
            collection.AddSingleton<IAppSettings>(
                settings);
            collection.AddSingleton<ILocalizationService>(
                new ResourceLocalizationService(
                    settings));
            collection.AddSingleton<
                IAudioTranscodeCapabilityService>(
                new FixedTranscodeCapabilities());
        });
    }

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

        public string? GetRememberedConfigPath() =>
            null;

        public IReadOnlyList<string>
            RecentConfigPaths => [];

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

    private sealed class FixedTranscodeCapabilities :
        IAudioTranscodeCapabilityService
    {
        public Task<AudioTranscodeCapabilitySnapshot>
            GetAsync(
                bool forceRefresh = false,
                CancellationToken ct = default) =>
            Task.FromResult(
                new AudioTranscodeCapabilitySnapshot(
                    [],
                    [
                        new(
                            AudioTranscodeFormatIds.Flac,
                            "flac",
                            "flac",
                            ".flac",
                            true,
                            [
                                AudioTranscodeEncoderIds
                                    .Ffmpeg("flac"),
                            ]),
                    ],
                    [
                        new(
                            AudioTranscodeEncoderIds
                                .Ffmpeg("flac"),
                            AudioTranscodeToolKind.Ffmpeg,
                            "flac",
                            AudioEncoderThreadingMode
                                .ThreadCountControllable,
                            [
                                new(
                                    AudioTranscodeRateMode
                                        .Lossless),
                            ],
                            [],
                            [16, 24]),
                    ],
                    DateTimeOffset.UtcNow,
                    1));

        public void Invalidate()
        {
        }
    }
}
