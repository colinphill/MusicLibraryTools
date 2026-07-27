using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;
using MusicLibraryManager.Views;
using MusicLibraryManager.Views.WorkbenchSections;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class WorkbenchInteractionTests
{
    [AvaloniaFact]
    public async Task Transcode_preview_resumes_on_the_ui_dispatcher()
    {
        var transcodes =
            new WorkerThreadPreviewTranscodeService();
        using ServiceProvider services =
            BuildServices(
                transcodeService: transcodes);
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    1200,
                    700);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            WorkbenchTrackViewModel track =
                Track("worker-preview.flac");
            model.Files.Add(track);
            model.SetSelectedFiles([track]);

            Assert.True(
                await view
                    .OpenTranscodeDrawerAsync());
            Render();

            await model.TranscodeEditor!
                .PreviewCommand.ExecuteAsync(null);
            Render();

            Assert.True(
                transcodes.PreviewCompletedOnWorker);
            Assert.False(
                model.TranscodeEditor.IsBusy);
            Assert.Single(
                model.PendingChanges);
        }
        finally
        {
            window.Hide();
        }
    }

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
    public void SelectionActionsRemoveEverySelectedTrackFromTheSession()
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
                    1200,
                    700);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            ILocalizationService localization =
                services.GetRequiredService<
                    ILocalizationService>();
            var first = Track("remove-first.flac");
            var second = Track("remove-second.flac");
            var retained = Track("remove-retained.flac");
            model.Files.Add(first);
            model.Files.Add(second);
            model.Files.Add(retained);
            Render();
            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "WorkbenchGrid")!;
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(first);
            grid.SelectedItems.Add(second);
            grid.SelectedItem = first;
            model.SelectedFile = first;
            model.SetSelectedFiles(
                [first, second]);
            Render();

            Button selectionActions =
                view.FindControl<Button>(
                    "WorkbenchSessionActionsButton")!;
            MenuFlyout flyout =
                Assert.IsType<MenuFlyout>(
                    selectionActions.Flyout);
            flyout.ShowAt(selectionActions);
            Render();
            MenuItem remove =
                flyout.Items
                    .OfType<MenuItem>()
                    .Single(item =>
                        string.Equals(
                            item.Header?.ToString(),
                            localization.Get(
                                "Workbench.Session.Action.RemoveSelected"),
                            StringComparison.Ordinal));

            Assert.NotNull(remove.Command);
            Assert.True(
                remove.Command.CanExecute(
                    remove.CommandParameter));
            remove.RaiseEvent(
                new RoutedEventArgs(
                    MenuItem.ClickEvent));
            Render();

            Assert.Equal(
                [retained],
                model.Files);
            Assert.Equal(
                [retained],
                model.SelectedFiles);
            Assert.Same(
                retained,
                model.SelectedFile);
            Assert.Equal(
                [retained],
                grid.ItemsSource!
                    .Cast<WorkbenchTrackViewModel>());
            flyout.Hide();
            var final = Track("remove-final.flac");
            model.Files.Add(final);
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(retained);
            grid.SelectedItems.Add(final);
            grid.SelectedItem = retained;
            model.SelectedFile = retained;
            model.SetSelectedFiles(
                [retained, final]);
            Render();
            ContextMenu contextMenu =
                Assert.IsType<ContextMenu>(
                    grid.ContextMenu);
            contextMenu.Open(grid);
            Render();
            MenuItem contextRemove =
                contextMenu.Items
                    .OfType<MenuItem>()
                    .Single(item =>
                        string.Equals(
                            item.Header?.ToString(),
                            localization.Get(
                                "Workbench.Session.Action.RemoveSelected"),
                            StringComparison.Ordinal));

            Assert.NotNull(
                contextRemove.Command);
            Assert.True(
                contextRemove.Command.CanExecute(
                    contextRemove.CommandParameter));
            contextRemove.RaiseEvent(
                new RoutedEventArgs(
                    MenuItem.ClickEvent));
            Render();

            Assert.Empty(model.Files);
            Assert.Empty(
                model.SelectedFiles);
            Assert.Null(
                model.SelectedFile);
            contextMenu.Close();
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
    public async Task Populated_inspector_artwork_and_tag_layer_editors_remain_reachable_at_compact_drawer_bounds()
    {
        var settings = new TestSettings();
        string path =
            Path.GetFullPath(
                "inspector-artwork-tag-layers.mp3");
        var documents =
            new PopulatedInspectorDocumentService();
        using ServiceProvider services =
            BuildServices(
                settings,
                documents);
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    1920,
                    900);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            await view.AddDroppedSourcesAsync(
                [path]);
            WorkbenchTrackViewModel track =
                Assert.Single(
                    model.Files);
            Assert.True(
                model.HasFiles,
                "The representative Inspector fixture did not enter the populated Workbench state.");
            model.SelectedFile = track;
            Assert.True(
                await model.TrySetSelectedFilesAsync(
                    [track]),
                "The populated Inspector fixture selection was rejected.");
            Render();

            Control drawer =
                view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!;
            if (!drawer.IsEffectivelyVisible)
            {
                view.FindControl<Button>(
                        "WorkbenchInspectorToggle")!
                    .RaiseEvent(
                        new RoutedEventArgs(
                            Button.ClickEvent));
                Render();
            }
            PersistedSplitView split =
                view.FindControl<PersistedSplitView>(
                    "WorkbenchSplit")!;
            SelectionInspectorView inspector =
                drawer.FindControl<
                    SelectionInspectorView>(
                    "WorkbenchInspectorView")!;
            ScrollViewer scroll =
                inspector.FindControl<ScrollViewer>(
                    "InspectorContent")!;
            ListBox artworkList =
                inspector.FindControl<ListBox>(
                    "InspectorArtworkList")!;
            Expander tagTools =
                drawer.FindControl<Expander>(
                    "WorkbenchTagToolsExpander")!;
            ILocalizationService localization =
                services.GetRequiredService<
                    ILocalizationService>();

            Assert.True(
                drawer.IsEffectivelyVisible,
                "The populated Inspector drawer did not open.");
            Assert.Equal(
                2,
                model.Inspector!.ArtworkItems.Count);
            Assert.Equal(
                2,
                artworkList.ItemCount);
            artworkList.SelectedIndex = 0;
            Render();
            Assert.Same(
                model.Inspector.ArtworkItems[0],
                artworkList.SelectedItem);
            Assert.False(
                tagTools.IsExpanded);

            foreach (double expectedWidth in
                     new[] { 430d, 300d })
            {
                split.CommitLeftWidth(
                    split.Bounds.Width -
                    WorkbenchView
                        .SplitDividerAllocation -
                    expectedWidth);
                Render();

                Assert.InRange(
                    split.CurrentRightWidth,
                    expectedWidth - 1,
                    expectedWidth + 1);
                Assert.InRange(
                    drawer.Bounds.Width,
                    expectedWidth - 1,
                    expectedWidth + 1);
                Assert.True(
                    drawer.Bounds.Height <=
                    view.Bounds.Height + 1,
                    $"The {expectedWidth:0}px Inspector drawer exceeded the Workbench height.");

                artworkList.BringIntoView();
                Render();
                ListBoxItem[] artworkRows =
                [
                    .. artworkList
                        .GetVisualDescendants()
                        .OfType<ListBoxItem>(),
                ];
                Assert.Equal(
                    2,
                    artworkRows.Length);
                Assert.All(
                    artworkRows,
                    row => Assert.Contains(
                        row.GetVisualDescendants()
                            .OfType<Border>(),
                        preview =>
                            AutomationProperties
                                .GetName(preview) ==
                            localization.Get(
                                "Inspector.View.ArtworkPreview")));

                ArtworkPreviewItem selected =
                    Assert.IsType<ArtworkPreviewItem>(
                        artworkList.SelectedItem);
                ComboBox artworkType =
                    Assert.Single(
                        drawer
                            .GetVisualDescendants()
                            .OfType<ComboBox>(),
                        control =>
                            ReferenceEquals(
                                control.DataContext,
                                selected) &&
                            AutomationProperties
                                .GetName(control) ==
                            localization.Get(
                                "Inspector.View.ArtworkType"));
                TextBox artworkDescription =
                    Assert.Single(
                        drawer
                            .GetVisualDescendants()
                            .OfType<TextBox>(),
                        control =>
                            ReferenceEquals(
                                control.DataContext,
                                selected) &&
                            AutomationProperties
                                .GetName(control) ==
                            localization.Get(
                                "Inspector.View.ArtworkDescription"));
                AssertActionReachable(
                    drawer,
                    artworkType);
                AssertActionReachable(
                    drawer,
                    artworkDescription);

                Button addArtwork =
                    FindAutomationButton(
                        drawer,
                        localization.Get(
                            "Inspector.View.AddArtworkAutomation"));
                Button optimizeArtwork =
                    FindAutomationButton(
                        drawer,
                        localization.Get(
                            "Inspector.View.OptimizeAutomation"));
                Button artworkOverflow =
                    Assert.Single(
                        drawer
                            .GetVisualDescendants()
                            .OfType<Button>(),
                        button =>
                            ReferenceEquals(
                                button.DataContext,
                                model.Inspector) &&
                            AutomationProperties
                                .GetName(button) ==
                            localization.Get(
                                "Inspector.View.ArtworkActions"));
                AssertActionReachable(
                    drawer,
                    addArtwork);
                AssertActionReachable(
                    drawer,
                    optimizeArtwork);
                AssertActionReachable(
                    drawer,
                    artworkOverflow);

                Button rowOverflow =
                    Assert.Single(
                        drawer
                            .GetVisualDescendants()
                            .OfType<Button>(),
                        button =>
                            ReferenceEquals(
                                button.DataContext,
                                selected) &&
                            AutomationProperties
                                .GetName(button) ==
                            localization.Get(
                                "Inspector.View.ArtworkActions"));
                AssertActionReachable(
                    drawer,
                    rowOverflow);
                AssertMenuActions(
                    rowOverflow,
                    localization,
                    "Inspector.View.SaveArtworkToFileAutomation",
                    "Inspector.View.ReplaceArtworkAutomation",
                    "Inspector.View.RemoveArtworkAutomation");

                tagTools.IsExpanded = true;
                Render();
                AssertLayerRow(
                    "Workbench.Inspector.Id3v2Actions",
                    "Workbench.Inspector.LayerPresent",
                    "Workbench.Inspector.AddId3v2Automation",
                    "Workbench.Inspector.RemoveId3v2Automation",
                    expectAddEnabled: false,
                    expectRemoveEnabled: true);
                AssertLayerRow(
                    "Workbench.Inspector.ApeV2Actions",
                    "Workbench.Inspector.LayerMissing",
                    "Workbench.Inspector.AddApeV2Automation",
                    "Workbench.Inspector.RemoveApeV2Automation",
                    expectAddEnabled: true,
                    expectRemoveEnabled: false);
                AssertLayerRow(
                    "Workbench.Inspector.Id3v1Actions",
                    "Workbench.Inspector.LayerPresent",
                    "Workbench.Inspector.AddId3v1Automation",
                    "Workbench.Inspector.RemoveId3v1Automation",
                    expectAddEnabled: false,
                    expectRemoveEnabled: true);

                Expander advanced =
                    Assert.Single(
                        tagTools
                            .GetVisualDescendants()
                            .OfType<Expander>(),
                        expander =>
                            Equals(
                                expander.Header,
                                localization.Get(
                                    "Workbench.Common.Advanced")));
                Assert.False(
                    advanced.IsExpanded);
                Control[] collapsedAdvancedControls =
                [
                    .. advanced
                        .GetLogicalDescendants()
                        .OfType<Control>()
                        .Where(control =>
                            AutomationProperties
                                .GetName(control) is
                                string name &&
                            (name ==
                             localization.Get(
                                 "Workbench.Inspector.TargetId3VersionAutomation") ||
                             name ==
                             localization.Get(
                                 "Workbench.Inspector.Id3EncodingAutomation"))),
                ];
                Assert.Equal(
                    2,
                    collapsedAdvancedControls.Length);

                advanced.IsExpanded = true;
                Render();
                ComboBox targetVersion =
                    FindAutomationControl<
                        ComboBox>(
                        drawer,
                        localization.Get(
                            "Workbench.Inspector.TargetId3VersionAutomation"));
                ComboBox encoding =
                    FindAutomationControl<
                        ComboBox>(
                        drawer,
                        localization.Get(
                            "Workbench.Inspector.Id3EncodingAutomation"));
                Button previewConversion =
                    FindAutomationButton(
                        drawer,
                        localization.Get(
                            "Workbench.Inspector.PreviewConversionAutomation"));
                Button previewEncoding =
                    FindAutomationButton(
                        drawer,
                        localization.Get(
                            "Workbench.Inspector.PreviewEncodingAutomation"));
                AssertActionReachable(
                    drawer,
                    targetVersion);
                AssertActionReachable(
                    drawer,
                    previewConversion);
                AssertActionReachable(
                    drawer,
                    encoding);
                AssertActionReachable(
                    drawer,
                    previewEncoding);

                advanced.IsExpanded = false;
                if (expectedWidth > 300)
                {
                    tagTools.IsExpanded = false;
                    artworkList.BringIntoView();
                }
                else
                {
                    tagTools.IsExpanded = true;
                    tagTools.BringIntoView();
                }
                Render();
                CapturePopulatedInspector(
                    window,
                    (int)expectedWidth);
            }

            Assert.Equal(
                0,
                scroll.Offset.X);

            void AssertLayerRow(
                string launcherKey,
                string statusKey,
                string addKey,
                string removeKey,
                bool expectAddEnabled,
                bool expectRemoveEnabled)
            {
                Button launcher =
                    FindAutomationButton(
                        drawer,
                        localization.Get(
                            launcherKey));
                Grid row =
                    Assert.IsType<Grid>(
                        launcher.Parent);
                Assert.Contains(
                    row.Children
                        .OfType<TextBlock>(),
                    status =>
                        status
                            .IsEffectivelyVisible &&
                        status.Text ==
                        localization.Get(
                            statusKey));
                AssertActionReachable(
                    drawer,
                    launcher);
                launcher.BringIntoView();
                Render();

                MenuFlyout flyout =
                    Assert.IsType<MenuFlyout>(
                        launcher.Flyout);
                flyout.ShowAt(launcher);
                Render();
                MenuItem add =
                    Assert.Single(
                        flyout.Items
                            .OfType<MenuItem>(),
                        item =>
                            AutomationProperties
                                .GetName(item) ==
                            localization.Get(
                                addKey));
                MenuItem remove =
                    Assert.Single(
                        flyout.Items
                            .OfType<MenuItem>(),
                        item =>
                            AutomationProperties
                                .GetName(item) ==
                            localization.Get(
                                removeKey));
                Assert.Equal(
                    expectAddEnabled,
                    add.IsEffectivelyEnabled);
                Assert.Equal(
                    expectRemoveEnabled,
                    remove.IsEffectivelyEnabled);
                flyout.Hide();
                Render();
            }
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
    public void Columns_drawer_searches_reorders_persists_and_explains_editability()
    {
        var settings = new TestSettings();
        settings.SetPreference(
            LocalizationPreferences.DisplayLanguage,
            "de-DE");
        using ServiceProvider services =
            BuildServices(settings);
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 900, 640);
            WorkbenchSessionSectionView session =
                view.FindControl<
                    WorkbenchSessionSectionView>(
                    "WorkbenchSessionSection")!;
            view.FindControl<Button>(
                    "WorkbenchColumnsButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Render();

            TextBox search =
                view.FindControl<TextBox>(
                    "ColumnSearchBox")!;
            StackPanel options =
                view.FindControl<StackPanel>(
                    "WorkbenchColumnOptions")!;
            ILocalizationService localization =
                services.GetRequiredService<
                    ILocalizationService>();

            search.Text =
                localization.Get("Column.File");
            Render();
            ColumnRow[] localizedRows = Rows();
            Assert.NotEmpty(localizedRows);
            Assert.Contains(
                localizedRows,
                row =>
                    string.Equals(
                        row.Check.Tag as string,
                        "File",
                        StringComparison.Ordinal));
            Assert.All(
                localizedRows,
                row =>
                    Assert.Contains(
                        search.Text,
                        Assert.IsType<string>(
                            row.Check.Content),
                        StringComparison
                            .CurrentCultureIgnoreCase));

            search.Text = "CodecType";
            Render();
            Assert.Equal(
                "CodecType",
                Assert.Single(Rows()).Check.Tag);

            search.Text = "";
            Render();
            ColumnRow first = Rows()[0];
            string[] beforeFirstBoundary =
                CurrentOrder();
            first.Up.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Render();
            Assert.Equal(
                beforeFirstBoundary,
                CurrentOrder());

            ColumnRow last = Rows()[^1];
            string[] beforeLastBoundary =
                CurrentOrder();
            last.Down.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Render();
            Assert.Equal(
                beforeLastBoundary,
                CurrentOrder());

            string[] originalOrder =
                CurrentOrder();
            ColumnRow title = Rows()
                .Single(row =>
                    string.Equals(
                        row.Check.Tag as string,
                        "Title",
                        StringComparison.Ordinal));
            title.Down.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Render();
            Assert.Equal(
                ["File", "Artist", "Title"],
                session.ColumnDefinitions
                    .Take(3)
                    .Select(column => column.Key));

            title = Rows()
                .Single(row =>
                    string.Equals(
                        row.Check.Tag as string,
                        "Title",
                        StringComparison.Ordinal));
            title.Up.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Render();
            string[] restoredOrder =
                CurrentOrder();
            Assert.True(
                originalOrder.SequenceEqual(
                    restoredOrder),
                "Moving Title down and back up changed unrelated columns." +
                Environment.NewLine +
                $"Before: {string.Join(", ", originalOrder)}" +
                Environment.NewLine +
                $"After:  {string.Join(", ", restoredOrder)}");

            title = Rows()
                .Single(row =>
                    string.Equals(
                        row.Check.Tag as string,
                        "Title",
                        StringComparison.Ordinal));
            title.Down.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Render();
            GridSnapshot persisted =
                Assert.IsType<GridSnapshot>(
                    services.GetRequiredService<
                            GridStateService>()
                        .Load("workbench.session"));
            Assert.Equal(
                ["File", "Artist", "Title"],
                persisted.Columns
                    .OrderBy(column =>
                        column.DisplayIndex)
                    .Take(3)
                    .Select(column => column.Key));

            foreach (string key in
                     session.ColumnDefinitions
                         .Where(column =>
                             !string.Equals(
                                 column.Key,
                                 "File",
                                 StringComparison.Ordinal))
                         .Select(column => column.Key)
                         .ToArray())
                session.SetColumnVisibility(
                    key,
                    visible: false);
            Render();
            Assert.Equal(
                1,
                session.ColumnDefinitions.Count(
                    column => column.Visible));
            ColumnRow onlyVisible = Rows()
                .Single(row =>
                    string.Equals(
                        row.Check.Tag as string,
                        "File",
                        StringComparison.Ordinal));
            onlyVisible.Check.IsChecked = false;
            Render();
            Assert.True(
                session.ColumnDefinitions.Single(
                    column =>
                        string.Equals(
                            column.Key,
                            "File",
                            StringComparison.Ordinal))
                    .Visible);
            Assert.True(
                Rows()
                    .Single(row =>
                        string.Equals(
                            row.Check.Tag as string,
                            "File",
                            StringComparison.Ordinal))
                    .Check.IsChecked);

            onlyVisible = Rows()
                .Single(row =>
                    string.Equals(
                        row.Check.Tag as string,
                        "File",
                        StringComparison.Ordinal));
            onlyVisible.Check.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Render();
            Assert.True(
                view.FindControl<Border>(
                        "BuiltInColumnDetails")!
                    .IsEffectivelyVisible);
            Assert.True(
                view.FindControl<TextBlock>(
                        "BuiltInColumnEditingHelp")!
                    .IsEffectivelyVisible);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    view.FindControl<TextBlock>(
                            "BuiltInColumnEditingHelp")!
                        .Text));

            ColumnRow[] Rows() =>
            [
                .. options.Children
                    .OfType<Border>()
                    .Select(card =>
                    {
                        Grid row =
                            Assert.IsType<Grid>(
                                card.Child);
                        CheckBox check =
                            Assert.Single(
                                row.Children
                                    .OfType<CheckBox>());
                        Button[] buttons =
                        [
                            .. row.Children
                                .OfType<StackPanel>()
                                .Single()
                                .Children
                                .OfType<Button>(),
                        ];
                        Assert.Equal(2, buttons.Length);
                        return new ColumnRow(
                            check,
                            buttons[0],
                            buttons[1]);
                    }),
            ];

            string[] CurrentOrder() =>
                session.ColumnDefinitions
                    .Select(column => column.Key)
                    .ToArray();
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
                AssertDrawerCyclesForwardAndReverse(
                    window,
                    view,
                    drawer);
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

    private static void CapturePopulatedInspector(
        MainWindow window,
        int drawerWidth)
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
                "supplemental-workbench-inspector-" +
                "populated-artwork-tag-layers-" +
                $"{drawerWidth}px.png"),
            PngBitmapEncoderOptions.Default);
    }

    private static void AssertActionReachable(
        Control root,
        Control action)
    {
        UiActionReachabilityResult result =
            UiViewportReachability
                .VerifyAction(
                    root,
                    action,
                    Render);
        Assert.True(
            result.IsReachable,
            $"{AutomationProperties.GetName(action) ?? action.Name ?? action.GetType().Name} was not reachable. {result.Detail}");
    }

    private static T FindAutomationControl<T>(
        Control root,
        string name)
        where T : Control =>
        Assert.Single(
            root.GetVisualDescendants()
                .OfType<T>(),
            control =>
                AutomationProperties
                    .GetName(control) ==
                name);

    private static Button FindAutomationButton(
        Control root,
        string name) =>
        FindAutomationControl<Button>(
            root,
            name);

    private static void AssertMenuActions(
        Button launcher,
        ILocalizationService localization,
        params string[] actionKeys)
    {
        launcher.BringIntoView();
        Render();
        MenuFlyout flyout =
            Assert.IsType<MenuFlyout>(
                launcher.Flyout);
        flyout.ShowAt(launcher);
        Render();
        string[] names =
        [
            .. flyout.Items
                .OfType<MenuItem>()
                .Select(
                    AutomationProperties
                        .GetName)
                .Where(name =>
                    !string.IsNullOrWhiteSpace(
                        name))
                .Cast<string>(),
        ];
        foreach (string actionKey in
                 actionKeys)
        {
            Assert.Contains(
                localization.Get(
                    actionKey),
                names);
        }
        flyout.Hide();
        Render();
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

    private static ServiceProvider BuildServices(
        TestSettings? settings = null,
        IMetadataDocumentService?
            metadataDocuments = null,
        IAudioTranscodeService?
            transcodeService = null)
    {
        settings ??= new TestSettings();
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
            if (transcodeService is not null)
            {
                collection.AddSingleton(
                    transcodeService);
            }
            if (metadataDocuments is not null)
            {
                collection.AddSingleton<
                    IMetadataDocumentService>(
                    metadataDocuments);
                if (metadataDocuments is
                    IWorkbenchService
                    workbench)
                {
                    collection.AddSingleton<
                        IWorkbenchService>(
                        workbench);
                }
            }
        });
    }

    private sealed record ColumnRow(
        CheckBox Check,
        Button Up,
        Button Down);

    private sealed class
        PopulatedInspectorDocumentService :
        IMetadataDocumentService,
        IWorkbenchService
    {
        private static readonly byte[] ArtworkPng =
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        public Task<MediaDocument> LoadAsync(
            string path,
            bool includeArtwork = true,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                CreateDocument(
                    path,
                    includeArtwork));
        }

        Task<WorkbenchLoadResult>
            IWorkbenchService.LoadAsync(
                WorkbenchLoadRequest request,
                CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new WorkbenchLoadResult(
                [
                    .. request.Sources
                        .Select(source =>
                            CreateDocument(
                                Path.GetFullPath(
                                    source),
                                includeArtwork:
                                    true)),
                ],
                []));
        }

        public MediaDocument CreateDocument(
            string path,
            bool includeArtwork)
        {
            System.Collections.Immutable
                .ImmutableArray<ArtworkModel>
                artwork =
                includeArtwork
                    ?
                    [
                        new()
                        {
                            Category =
                                "FrontCover",
                            Description =
                                "Front cover",
                            ImageType =
                                "image/png",
                            Width = 1,
                            Height = 1,
                            Size =
                                ArtworkPng.Length,
                            Data =
                                [.. ArtworkPng],
                        },
                        new()
                        {
                            Category =
                                "BackCover",
                            Description =
                                "Back cover",
                            ImageType =
                                "image/png",
                            Width = 1,
                            Height = 1,
                            Size =
                                ArtworkPng.Length,
                            Data =
                                [.. ArtworkPng],
                        },
                    ]
                    : [];
            return new MediaDocument(
                path,
                [
                    new(
                        "ID3v2",
                        [
                            new(
                                MetadataFieldKey
                                    .Known(
                                        TagFields
                                            .Title),
                                ["Inspector fixture"]),
                            new(
                                MetadataFieldKey
                                    .Known(
                                        TagFields
                                            .Artist),
                                ["Fixture artist"]),
                        ],
                        true,
                        true,
                        true,
                        true),
                ],
                artwork,
                null,
                new(
                    path,
                    256,
                    DateTime.UnixEpoch,
                    "inspector-artwork-tag-layers"),
                true)
            {
                EditableTagLayers =
                [
                    new(
                        TagLayerKind.Id3v2,
                        "ID3v2",
                        true,
                        false,
                        true,
                        true),
                    new(
                        TagLayerKind.ApeV2,
                        "APEv2",
                        false,
                        true,
                        false,
                        false),
                    new(
                        TagLayerKind.Id3v1,
                        "ID3v1",
                        true,
                        false,
                        true,
                        false),
                ],
                Id3Version =
                    ID3v2Version.V24,
            };
        }
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

    private sealed class
        WorkerThreadPreviewTranscodeService :
        IAudioTranscodeService
    {
        public bool PreviewCompletedOnWorker
        {
            get;
            private set;
        }

        public async Task<AudioTranscodePlan>
            PreviewAsync(
                AudioTranscodeRequest request,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct = default)
        {
            await Task.Run(
                () =>
                {
                    ct.ThrowIfCancellationRequested();
                    PreviewCompletedOnWorker = true;
                },
                ct).ConfigureAwait(false);
            AudioTranscodeSettings settings =
                request.Settings;
            return new(
                Guid.NewGuid(),
                request,
                [
                    .. request.SourcePaths.Select(
                        sourcePath =>
                        {
                            string destinationPath =
                                Path.ChangeExtension(
                                    sourcePath,
                                    ".transcoded.flac");
                            return new
                                AudioTranscodePlanItem(
                                    Guid.NewGuid(),
                                    sourcePath,
                                    destinationPath,
                                    OperationPathSnapshot
                                        .Missing(
                                            sourcePath),
                                    OperationPathSnapshot
                                        .Missing(
                                            destinationPath),
                                    "",
                                    settings,
                                    []);
                        }),
                ],
                [],
                DateTimeOffset.UtcNow,
                1);
        }

        public Task<AudioTranscodeStageResult>
            StageAsync(
                AudioTranscodePlan plan,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AudioTranscodeStageResult>
            StageWithSourceOverridesAsync(
                AudioTranscodePlan plan,
                IReadOnlyDictionary<string, string>
                    sourceOverrides,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AudioTranscodeApplyResult>
            ApplyAsync(
                AudioTranscodeStageResult stage,
                IReadOnlySet<Guid>?
                    readyItemIds = null,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AudioTranscodeApplyResult>
            ApplyBatchAsync(
                IReadOnlyList<
                    AudioTranscodeStageResult>
                    stages,
                IReadOnlySet<Guid>?
                    readyItemIds = null,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DiscardStageAsync(
            AudioTranscodeStageResult stage,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
