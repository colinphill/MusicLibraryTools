using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MetadataCaching;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicFileUtilities;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryManager.Views.WorkbenchSections;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class UiControlTests
{
    [AvaloniaFact]
    public void Library_and_workbench_expose_pending_change_flyouts()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            INavigationService navigation =
                services.GetRequiredService<INavigationService>();

            navigation.Navigate(ShellDestination.Library);
            Dispatcher.UIThread.RunJobs();
            LibraryView library = Assert.IsType<LibraryView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            library.FindControl<Button>(
                    "LibraryPendingChangesButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(library.FindControl<Popup>(
                "LibraryPendingChangesPopover")!.IsOpen);
            Assert.NotNull(library.FindControl<ListBox>(
                "LibraryPendingChangesGrid"));
            Assert.Equal(
                "Discard all pending changes",
                Assert.IsType<TextBlock>(
                    library.FindControl<Button>(
                        "LibraryRevertPendingChangesButton")!
                        .Content)
                    .Text);
            Assert.Equal(
                "Apply",
                library.FindControl<Button>(
                    "LibraryApplyPendingChangesButton")!.Content);
            Assert.False(
                library.FindControl<Button>(
                    "LibraryRevertPendingChangesButton")!
                    .IsEnabled);
            Assert.False(
                services.GetRequiredService<
                    LibraryViewModel>()
                    .RevertPendingChangesCommand
                    .CanExecute(null));

            navigation.Navigate(ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            WorkbenchView workbench = Assert.IsType<WorkbenchView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            workbench.FindControl<Button>(
                    "WorkbenchPendingChangesButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(workbench.FindControl<Control>(
                "WorkbenchPendingChangesDrawer")!.IsVisible);
            Assert.False(workbench.FindControl<Control>(
                "WorkbenchInspectorDrawer")!.IsVisible);
            Assert.False(workbench.FindControl<Control>(
                "WorkbenchColumnsDrawer")!.IsVisible);
            Assert.NotNull(workbench.FindControl<ListBox>(
                "WorkbenchPendingChangesGrid"));
            Assert.Equal(
                "Discard all pending changes",
                Assert.IsType<TextBlock>(
                    workbench.FindControl<Button>(
                        "WorkbenchRevertPendingChangesButton")!
                        .Content)
                    .Text);
            Assert.Equal(
                "Apply",
                workbench.FindControl<Button>(
                    "WorkbenchApplyPendingChangesButton")!.Content);

            WorkbenchViewModel workbenchModel =
                services.GetRequiredService<WorkbenchViewModel>();
            Assert.False(
                workbenchModel.HasPendingChanges);
            Assert.False(
                workbenchModel
                    .RevertPendingChangesCommand
                    .CanExecute(null));
            Assert.False(
                workbench.FindControl<Button>(
                    "WorkbenchRevertPendingChangesButton")!
                    .IsEnabled);
            workbenchModel.PreviewChanges.Add(
                new("song.flac", "Title", "Before", "After"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(
                workbenchModel.HasPendingChanges);
            Assert.True(
                workbench.FindControl<Button>(
                    "WorkbenchRevertPendingChangesButton")!
                    .IsEnabled);
            workbenchModel.RevertPendingChangesCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(workbenchModel.PreviewChanges);
            Assert.False(
                workbenchModel.HasPendingChanges);
            Assert.False(
                workbench.FindControl<Button>(
                    "WorkbenchRevertPendingChangesButton")!
                    .IsEnabled);
            Assert.Contains("reverted", workbenchModel.StatusText);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Shared_command_surfaces_use_bounded_accessible_overflow_menus()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        ILocalizationService localization =
            services.GetRequiredService<
                ILocalizationService>();
        MainWindow window = services.GetRequiredService<MainWindow>();
        INavigationService navigation =
            services.GetRequiredService<INavigationService>();
        try
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Width = 900;
            window.Height = 600;
            window.Activate();
            Render();

            navigation.Navigate(ShellDestination.Library);
            Render();
            LibraryView library = Assert.IsType<LibraryView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            Assert.Equal(
                ["Reload cached library"],
                MenuHeaders(library.FindControl<Button>("LibraryMoreButton")!));

            Button openOperations =
                library.FindControl<Button>("LibraryWorkbenchButton")!;
            Assert.True(
                openOperations.IsEffectivelyEnabled,
                "Workbench handoff should be available in the empty-library view.");
            Assert.Equal(
                "Choose a Workbench destination and result scope",
                AutomationProperties.GetName(openOperations));
            Render();
            Assert.Null(
                library.FindControl<Popup>(
                    "LibraryOperationsPopover"));

            library.FindControl<Button>("LibraryPendingChangesButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render();
            Assert.Null(library.FindControl<Popup>(
                "LibraryOperationsPopover"));
            Assert.True(
                library.FindControl<Popup>(
                    "LibraryPendingChangesPopover")!.IsOpen,
                "Library pending-changes popover did not open.");
            Border pendingSurface = library.FindControl<Border>(
                "LibraryPendingChangesSurface")!;
            Assert.True(
                pendingSurface.Width <= library.Bounds.Width - 20,
                $"Pending width {pendingSurface.Width} exceeds Library width {library.Bounds.Width}.");
            Assert.True(
                pendingSurface.Height <= library.Bounds.Height,
                $"Pending height {pendingSurface.Height} exceeds Library height {library.Bounds.Height}.");
            library.FindControl<Button>("CloseLibraryPendingChangesButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            navigation.Navigate(ShellDestination.Health);
            Render();
            HealthView health = Assert.IsType<HealthView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            Assert.Equal(7,
                MenuHeaders(health.FindControl<Button>(
                    "RunHealthAuditButton")!).Length);
            Assert.Equal(7,
                MenuHeaders(health.FindControl<Button>(
                    "PrepareHealthRepairsButton")!).Length);

            navigation.Navigate(ShellDestination.Ingest);
            Render();
            IngestView ingest = Assert.IsType<IngestView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            Assert.Equal(["Run preflight only"],
                MenuHeaders(ingest.FindControl<Button>(
                    "IngestMoreButton")!));

            navigation.Navigate(ShellDestination.Operations);
            Render();
            OperationsView operations = Assert.IsType<OperationsView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            TabControl operationsTabs = operations.GetVisualDescendants()
                .OfType<TabControl>()
                .Single();
            operationsTabs.SelectedIndex = 2;
            Render();
            Button maintenance = operations.GetVisualDescendants()
                .OfType<Button>()
                .Single(button =>
                    button.Name == "RecoveryMaintenanceButton");
            Assert.True(maintenance.IsEffectivelyVisible);
            Assert.Contains(
                operations,
                maintenance.GetVisualAncestors());
            Assert.Equal(
                [
                    localization.Get(
                        "Operations.PreviewRetentionPurge"),
                    localization.Get(
                        "Operations.PurgeReviewed"),
                ],
                MenuHeaders(maintenance));
            MenuItem purge = Assert.IsType<MenuFlyout>(
                    maintenance.Flyout)
                .Items.OfType<MenuItem>().Last();
            Assert.Contains("danger", purge.Classes);

            Assert.True(
                Application.Current!.TryGetResource(
                    "AppControlHeight", ThemeVariant.Dark, out object? height),
                "AppControlHeight was not available in the dark theme.");
            Assert.Equal(36d, Assert.IsType<double>(height));
            Assert.True(
                Application.Current.TryGetResource(
                    "AppIconButtonSize", ThemeVariant.Light, out object? iconSize),
                "AppIconButtonSize was not available in the light theme.");
            Assert.Equal(36d, Assert.IsType<double>(iconSize));
        }
        finally
        {
            window.Hide();
        }

        static string[] MenuHeaders(Button button)
        {
            MenuFlyout flyout =
                Assert.IsType<MenuFlyout>(button.Flyout);
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            string[] headers = flyout.Items
                .OfType<MenuItem>()
                .Select(item =>
                    item.Header?.ToString() ?? "")
                .ToArray();
            flyout.Hide();
            Dispatcher.UIThread.RunJobs();
            return headers;
        }

        static void Render()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        }
    }

    [AvaloniaFact]
    public void Reviewed_file_operation_editor_routes_preview_to_review_changes()
    {
        using ServiceProvider services =
            BuildIsolatedServices();
        App.UseServicesForTests(services);
        var view =
            new ReviewedFileOperationEditorView();
        var window = new Window { Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                "Preview destinations",
                view.FindControl<Button>(
                    "PreviewReviewedFileOperationButton")!
                    .Content);
            Assert.Null(
                view.FindControl<Button>(
                    "ApplyReviewedFileOperationButton"));
            Assert.NotNull(
                view.FindControl<AppDataGrid>(
                    "ReviewedFileOperationPreviewGrid"));
            Assert.Equal(
                "File operation",
                AutomationProperties.GetName(
                    view.FindControl<ComboBox>(
                        "ReviewedFileOperationKind")!));
            Assert.Equal(
                "File operation destination",
                AutomationProperties.GetName(
                    view.FindControl<TextBox>(
                        "ReviewedFileOperationDestination")!));
            Assert.Equal(
                "File name template",
                AutomationProperties.GetName(
                    view.FindControl<TextBox>(
                        "ReviewedFileNameTemplate")!));
            Assert.Equal(
                "Reviewed file operation preview",
                AutomationProperties.GetName(
                    view.FindControl<AppDataGrid>(
                        "ReviewedFileOperationPreviewGrid")!));
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Shell_keyboard_search_and_navigation_restore_predictable_focus()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();

            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.K,
                KeyModifiers = KeyModifiers.Control,
            });
            Dispatcher.UIThread.RunJobs();

            Assert.Same(
                window.FindControl<TextBox>("SearchBox"),
                window.FocusManager?.GetFocusedElement());

            services.GetRequiredService<INavigationService>()
                .Navigate(ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            WorkbenchView view = Assert.IsType<WorkbenchView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            PageHeader heading = view.GetVisualDescendants()
                .OfType<PageHeader>()
                .Single();
            Assert.False(heading.Focusable);
            Assert.NotSame(
                heading,
                window.FocusManager?.GetFocusedElement());
            Assert.Equal(
                "Workbench",
                AutomationProperties.GetName(heading));
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Shell_navigation_keys_move_focus_selection_and_destination_across_groups()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Activate();
            RenderUi();

            INavigationService navigation =
                services.GetRequiredService<
                    INavigationService>();
            ListBox primary =
                window.FindControl<ListBox>(
                    "PrimaryNavigation")!;
            ListBox secondary =
                window.FindControl<ListBox>(
                    "SecondaryNavigation")!;
            Button home =
                window.FindControl<Button>(
                    "HomeNav")!;
            Button library =
                window.FindControl<Button>(
                    "LibraryNav")!;
            Button health =
                window.FindControl<Button>(
                    "HealthNav")!;
            Button operations =
                window.FindControl<Button>(
                    "OperationsNav")!;
            Button settings =
                window.FindControl<Button>(
                    "SettingsNav")!;
            Button about =
                window.FindControl<Button>(
                    "AboutNav")!;

            AssertNavigation(
                home,
                Key.Down,
                library,
                ShellDestination.Library);
            AssertNavigation(
                operations,
                Key.Down,
                settings,
                ShellDestination.Settings);
            AssertNavigation(
                settings,
                Key.Up,
                operations,
                ShellDestination.Operations);
            AssertNavigation(
                health,
                Key.Home,
                home,
                ShellDestination.Home);
            AssertNavigation(
                health,
                Key.End,
                about,
                ShellDestination.About);

            void AssertNavigation(
                Button startingButton,
                Key key,
                Button expectedButton,
                ShellDestination expectedDestination)
            {
                startingButton.Focus();
                RenderUi();
                Assert.Same(
                    startingButton,
                    window.FocusManager?
                        .GetFocusedElement());

                startingButton.RaiseEvent(
                    new KeyEventArgs
                    {
                        RoutedEvent =
                            InputElement.KeyDownEvent,
                        Key = key,
                    });
                RenderUi();

                Assert.Equal(
                    expectedDestination,
                    navigation.Current);
                Assert.Same(
                    expectedButton,
                    window.FocusManager?
                        .GetFocusedElement());
                bool secondaryDestination =
                    expectedDestination is
                        ShellDestination.Settings or
                        ShellDestination.About;
                Assert.Same(
                    secondaryDestination
                        ? null
                        : expectedButton,
                    primary.SelectedItem);
                Assert.Same(
                    secondaryDestination
                        ? expectedButton
                        : null,
                    secondary.SelectedItem);
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Top_search_focus_is_drawn_by_the_full_composite_control()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Border chrome = window.FindControl<Border>("SearchChrome")!;
            TextBox search = window.FindControl<TextBox>("SearchBox")!;
            search.Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Thickness(2), chrome.BorderThickness);
            Border inputBorder = search.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "PART_BorderElement");
            Assert.Equal(Colors.Transparent,
                Assert.IsAssignableFrom<ISolidColorBrush>(inputBorder.Background).Color);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Settings_exposes_profile_summary_and_root_write_permissions()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            services.GetRequiredService<INavigationService>().Navigate(ShellDestination.Settings);
            Dispatcher.UIThread.RunJobs();

            SettingsView settings = Assert.IsType<SettingsView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(settings.DataContext);
            await viewModel.NewConfigurationCommand.ExecuteAsync(null);
            TabControl tabs = settings.FindControl<TabControl>("SettingsTabs")!;
            tabs.SelectedIndex = 5;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            settings.FindControl<Expander>(
                    "RootPolicyEditorExpander")!
                .IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            ComboBox profilePicker = settings.FindControl<ComboBox>("ProfilePresetPicker")!;
            Assert.Equal(LibraryProfilePresets.CatalogOnlyId,
                Assert.IsType<LibraryProfile>(profilePicker.SelectedItem).Id);
            Assert.Contains("catalog only (read-only)",
                settings.FindControl<TextBlock>("EffectivePolicySummaryText")!.Text!,
                StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(viewModel.AdvancedProfile);
            Assert.Contains(tabs.Items.OfType<TabItem>(),
                item => Equals(item.Header, "Root and naming policy"));
            Assert.Contains(tabs.Items.OfType<TabItem>(),
                item => Equals(item.Header, "Ingest policy"));
            Assert.Contains(settings.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "New"));
            Assert.Contains(settings.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Duplicate"));
            Button libraryProfileMore =
                settings.FindControl<Button>(
                    "LibraryProfileMoreButton")!;
            MenuFlyout libraryProfileMenu =
                Assert.IsType<MenuFlyout>(
                    libraryProfileMore.Flyout);
            libraryProfileMenu.ShowAt(
                libraryProfileMore);
            Dispatcher.UIThread.RunJobs();
            MenuItem deleteLibraryProfile =
                Assert.Single(
                    libraryProfileMenu.Items
                        .OfType<MenuItem>());
            Assert.Same(
                viewModel.DeleteLibraryProfileCommand,
                deleteLibraryProfile.Command);
            Assert.Contains(
                "danger",
                deleteLibraryProfile.Classes);
            libraryProfileMenu.Hide();
            tabs.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Cross-set comparison extensions");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Formats to index");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Include patterns");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Exclude patterns");
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text is "Ingest role" or "Representation role");
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<CheckBox>(),
                checkBox => checkBox.Content as string is
                    "iTunes canonical naming" or "Sync target");
            string[] permissionLabels =
                ["Metadata", "Artwork", "Organize files", "Ingest output", "Sync output"];
            CheckBox[] permissions = settings.GetVisualDescendants().OfType<CheckBox>()
                .Where(checkBox => permissionLabels.Contains(checkBox.Content as string))
                .ToArray();
            Assert.Equal(permissionLabels.Length, permissions.Length);
            Assert.All(permissions, checkBox => Assert.False(checkBox.IsChecked));

            Assert.True(viewModel.StartGuidedSetupCommand.CanExecute(null));
            viewModel.AddPlaylistSourceCommand.Execute(null);
            viewModel.AddPlaylistTargetCommand.Execute(null);
            viewModel.AddExportProfileCommand.Execute(null);
            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "File playlist sources");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Export profiles");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Cross-library sync target");

            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "WavPack");
            Assert.Equal("wavpack", viewModel.WavpackPath);
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text is "Path length limit" or "Disc number length limit" or
                    "AAC encoder" or "AAC bitrate (kbps)");

            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 5;
            Dispatcher.UIThread.RunJobs();
            settings.FindControl<Expander>(
                    "RootPolicyAdvancedExpander")!
                .IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Metadata fidelity");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Profile name");
            Assert.Contains(settings.GetVisualDescendants().OfType<CheckBox>(),
                checkBox => Equals(checkBox.Content, "iTunes canonical naming"));
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<CheckBox>(),
                checkBox => Equals(checkBox.Content, "Preserve disc tags"));
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text?.StartsWith("The disc strategy is the destination representation",
                    StringComparison.Ordinal) == true);
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text ==
                    services.GetRequiredService<
                            ILocalizationService>()
                        .Get(
                            "Settings.RootPolicy.ComponentLimit"));
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text ==
                    services.GetRequiredService<
                            ILocalizationService>()
                        .Get(
                            "Settings.RootPolicy.DiscAlbumLimit"));
            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 6;
            Dispatcher.UIThread.RunJobs();
            settings.FindControl<Expander>(
                    "IngestProfileEditorExpander")!
                .IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);
            Assert.Contains(settings.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Add recipe"));
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text is "Legacy destination role" or "Output representation");

            int mappingCount = viewModel.FieldMappings.Count;
            viewModel.AddFieldMappingCommand.Execute(null);
            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 8;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Canonical-to-native field mappings");
            Assert.Equal(mappingCount + 1, viewModel.FieldMappings.Count);
            viewModel.SaveFieldMappingsCommand.Execute(null);
            Assert.StartsWith("Saved ", viewModel.FieldMappingStatus);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Settings_tab_switching_preserves_required_choice_fields()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            services.GetRequiredService<INavigationService>().Navigate(ShellDestination.Settings);
            Dispatcher.UIThread.RunJobs();

            SettingsView settings = Assert.IsType<SettingsView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(settings.DataContext);
            await viewModel.NewConfigurationCommand.ExecuteAsync(null);

            IndexTargetEditorRow root = Assert.Single(viewModel.IndexTargets);
            root.Path = @"C:\Music";
            root.AllowIngestOutput = true;

            viewModel.AddPlaylistSourceCommand.Execute(null);
            PlaylistSourceEditorRow source = Assert.Single(viewModel.PlaylistSources);
            source.Location = @"C:\Playlists";

            viewModel.AddPlaylistTargetCommand.Execute(null);
            PlaylistTargetEditorRow target = Assert.Single(viewModel.PlaylistTargets);
            target.Target = @"C:\Playlist Exports";
            target.Type = "wpl";
            target.PathStyle = "relative";
            target.Encoding = "utf-16";
            target.LineEnding = "lf";
            target.FileNameTransform = "sanitize";
            target.CollisionPolicy = LibraryPathCollisionPolicy.Hash;

            viewModel.AddExportProfileCommand.Execute(null);
            ExportProfileEditorRow export = Assert.Single(viewModel.ExportProfiles);
            export.SelectionKind = ExportSelectionKind.Playlists;
            export.TransformMode = ExportTransformMode.Remux;
            export.CollisionPolicy = LibraryPathCollisionPolicy.Suffix;
            export.ArtworkMode = ExportArtworkMode.Sidecar;
            export.PlaylistFormat = "m3u";
            export.PlaylistEncoding = "utf-16";
            export.PlaylistLineEnding = "lf";
            export.ExtraFileDisposition = ExportExtraFileDisposition.Quarantine;

            LibraryProfileEditorRow advanced = Assert.IsType<LibraryProfileEditorRow>(
                viewModel.AdvancedProfile);
            advanced.UnicodeNormalization = LibraryUnicodeNormalization.FormKD;
            advanced.DiscStrategy = LibraryDiscStrategy.DiscFolder;
            advanced.TrackTotalScope = LibraryTrackTotalScope.Album;
            IngestProfileEditorRow advancedIngest = Assert.IsType<IngestProfileEditorRow>(
                viewModel.AdvancedIngestProfile);
            advancedIngest.SourceDisposition = LibrarySourceDisposition.Quarantine;
            advanced.ArtworkStorage = LibraryArtworkStorage.Both;
            advanced.ArtworkRoles = LibraryArtworkRoleSelection.AllRoles;
            advanced.ArtworkEncoding = LibraryArtworkEncoding.Png;
            advanced.UnknownSidecarDisposition = LibrarySidecarDisposition.Quarantine;

            viewModel.AddIngestRecipeCommand.Execute(null);
            IngestRecipeEditorRow recipe = Assert.Single(advancedIngest.Recipes);
            recipe.Action = LibraryIngestAction.Transcode;
            recipe.AddToMediaCatalog = true;
            recipe.CollisionPolicy = LibraryPathCollisionPolicy.Suffix;
            recipe.InputChannelChoice = SettingsChoiceLists.ChannelChoice(
                LibraryChannelSelection.Multi);
            recipe.OutputChannelChoice = SettingsChoiceLists.ChannelChoice(
                LibraryChannelSelection.Stereo);
            recipe.AlbumCondition = LibraryIngestAlbumCondition.HasHighResolution;
            recipe.SourceSelection = LibraryIngestSourceSelection.PreferCdQuality;
            recipe.RequireFallbackApproval = true;
            recipe.TranscodeFormatId =
                AudioTranscodeFormatIds.Flac;
            recipe.TranscodeEncoderId =
                AudioTranscodeEncoderIds.Automatic;
            recipe.TranscodeRateMode =
                AudioTranscodeRateMode.Lossless;
            recipe.DestinationRootChoice = Assert.Single(
                recipe.DestinationRootChoices, choice => choice.Id == root.Id);

            viewModel.AddSidecarRuleCommand.Execute(null);
            SidecarRuleEditorRow sidecar = advanced.SidecarRules[^1];
            sidecar.Disposition = LibrarySidecarDisposition.Quarantine;
            HealthRuleEditorRow health = advanced.HealthRules[0];
            health.Severity = LibraryHealthSeverity.Error;

            Dispatcher.UIThread.RunJobs();
            string? validationBefore = viewModel.ValidationSummary;
            TabControl tabs = settings.FindControl<TabControl>("SettingsTabs")!;

            ActivateTab(tabs, 1);
            AssertVisibleChoicesSelected(settings);
            ActivateTab(tabs, 2);
            AssertVisibleChoicesSelected(settings);
            ActivateTab(tabs, 5);
            ExpandRootPolicy(settings);
            AssertVisibleChoicesSelected(settings);
            ActivateTab(tabs, 6);
            ExpandIngestPolicy(settings);
            AssertVisibleChoicesSelected(settings);
            ActivateTab(tabs, 7);
            ActivateTab(tabs, 2);
            AssertVisibleChoicesSelected(settings);

            Assert.Equal("m3u", source.Type);
            Assert.Equal("wpl", target.Type);
            Assert.Equal("relative", target.PathStyle);
            Assert.Equal("utf-16", target.Encoding);
            Assert.Equal("lf", target.LineEnding);
            Assert.Equal("sanitize", target.FileNameTransform);
            Assert.Equal(ExportTransformMode.Remux, export.TransformMode);
            Assert.Equal("m3u", export.PlaylistFormat);
            Assert.Equal("utf-16", export.PlaylistEncoding);
            Assert.Equal("lf", export.PlaylistLineEnding);

            ActivateTab(tabs, 5);
            ExpandRootPolicy(settings);
            AssertVisibleChoicesSelected(settings);
            Assert.Same(advanced, viewModel.AdvancedProfile);
            Assert.Equal(LibraryUnicodeNormalization.FormKD, advanced.UnicodeNormalization);
            Assert.Equal(LibraryDiscStrategy.DiscFolder, advanced.DiscStrategy);
            Assert.Equal(LibraryTrackTotalScope.Album, advanced.TrackTotalScope);
            Assert.Equal(LibrarySourceDisposition.Quarantine,
                advancedIngest.SourceDisposition);
            ActivateTab(tabs, 6);
            ExpandIngestPolicy(settings);
            AssertVisibleChoicesSelected(settings);
            Assert.Equal(LibraryIngestAction.Transcode, recipe.Action);
            Assert.Equal(["Stereo", "Multi"],
                SettingsChoiceLists.ChannelChoices.Select(choice => choice.Label));
            Assert.Equal(LibraryChannelSelection.Multi, recipe.InputChannelChoice.Value);
            Assert.Equal(LibraryChannelSelection.Stereo, recipe.OutputChannelChoice.Value);
            Assert.Equal(LibraryIngestAlbumCondition.HasHighResolution,
                recipe.AlbumCondition);
            Assert.Equal(LibraryIngestSourceSelection.PreferCdQuality,
                recipe.SourceSelection);
            Assert.True(recipe.RequireFallbackApproval);
            Assert.Equal(
                AudioTranscodeFormatIds.Flac,
                recipe.TranscodeFormatId);
            Assert.Equal(
                AudioTranscodeEncoderIds.Automatic,
                recipe.TranscodeEncoderId);
            Assert.Equal(
                AudioTranscodeRateMode.Lossless,
                recipe.TranscodeRateMode);
            Assert.True(recipe.AddToMediaCatalog);
            Assert.DoesNotContain(
                settings.GetVisualDescendants().OfType<TextBox>(),
                textBox =>
                    AutomationProperties.GetName(textBox) ==
                    "Extra FFmpeg options");
            Assert.Contains(
                settings.GetVisualDescendants().OfType<ComboBox>(),
                combo => ReferenceEquals(
                    combo.ItemsSource,
                    recipe.TranscodeFormatChoices));
            Assert.Equal(root.Id, recipe.DestinationRootId);
            Assert.Equal(@"C:\Music", recipe.DestinationRootChoice?.Label);
            ComboBox destinationRootPicker = Assert.Single(
                settings.GetVisualDescendants().OfType<ComboBox>(), combo =>
                    ReferenceEquals(combo.ItemsSource, recipe.DestinationRootChoices));
            Assert.Equal(recipe.DestinationRootChoice,
                destinationRootPicker.SelectedItem);
            Assert.Equal(LibraryHealthSeverity.Error, health.Severity);
            Assert.Equal(LibrarySidecarDisposition.Quarantine, sidecar.Disposition);

            ActivateTab(tabs, 1);
            AssertVisibleChoicesSelected(settings);
            Assert.Equal(validationBefore, viewModel.ValidationSummary);
        }
        finally
        {
            window.Hide();
        }

        static void ActivateTab(TabControl tabs, int index)
        {
            tabs.SelectedIndex = index;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
        }

        static void AssertVisibleChoicesSelected(SettingsView settings)
        {
            ComboBox[] choices = settings.GetVisualDescendants().OfType<ComboBox>().ToArray();
            Assert.NotEmpty(choices);
            Assert.All(choices, choice => Assert.NotNull(choice.ItemsSource));
            Assert.All(choices, choice => Assert.True(
                choice.SelectedItem is not null,
                $"ComboBox selection was empty (data context: {choice.DataContext?.GetType().Name ?? "null"}; " +
                $"selected value: {choice.SelectedValue ?? "null"}; " +
                $"items: {choice.ItemCount})."));
        }

        static void ExpandRootPolicy(
            SettingsView settings)
        {
            settings.FindControl<Expander>(
                    "RootPolicyEditorExpander")!
                .IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            settings.FindControl<Expander>(
                    "RootPolicyAdvancedExpander")!
                .IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
        }

        static void ExpandIngestPolicy(
            SettingsView settings)
        {
            settings.FindControl<Expander>(
                    "IngestProfileEditorExpander")!
                .IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);
            foreach (Expander recipeEditor in
                     settings.GetVisualDescendants()
                         .OfType<Expander>()
                         .Where(expander =>
                             expander.Name ==
                             "IngestRecipeEditorExpander"))
            {
                recipeEditor.IsExpanded = true;
            }
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);
            foreach (Expander advanced in
                     settings.GetVisualDescendants()
                         .OfType<Expander>()
                         .Where(expander =>
                             expander.Name ==
                             "IngestRecipeAdvancedExpander"))
            {
                advanced.IsExpanded = true;
            }
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Settings_can_duplicate_both_policy_types_while_pickers_are_bound()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            services.GetRequiredService<INavigationService>().Navigate(
                ShellDestination.Settings);
            Dispatcher.UIThread.RunJobs();

            SettingsView settings = Assert.IsType<SettingsView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(
                settings.DataContext);
            await viewModel.NewConfigurationCommand.ExecuteAsync(null);
            TabControl tabs = settings.FindControl<TabControl>("SettingsTabs")!;

            tabs.SelectedIndex = 5;
            Dispatcher.UIThread.RunJobs();
            int namingCount = viewModel.LibraryProfiles.Count;
            viewModel.DuplicateLibraryProfileCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(namingCount + 1, viewModel.LibraryProfiles.Count);
            Assert.Equal(LibraryProfilePreset.Custom,
                viewModel.SelectedLibraryProfile?.Preset);

            tabs.SelectedIndex = 6;
            Dispatcher.UIThread.RunJobs();
            int ingestCount = viewModel.IngestProfiles.Count;
            viewModel.DuplicateIngestProfileCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ingestCount + 1, viewModel.IngestProfiles.Count);
            Assert.StartsWith("ingest-", viewModel.SelectedIngestProfile?.Id);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Settings_navigation_and_forms_adapt_at_900_by_600_with_large_text()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.WindowState = WindowState.Normal;
            window.Width = 900;
            window.Height = 600;
            window.FontSize = 18;
            window.Show();
            services.GetRequiredService<INavigationService>().Navigate(
                ShellDestination.Settings);
            Render();

            SettingsView settings = Assert.IsType<SettingsView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(
                settings.DataContext);
            Border categoryRail =
                settings.FindControl<Border>("SettingsCategoryRail")!;
            ComboBox categoryPicker =
                settings.FindControl<ComboBox>("SettingsCategoryPicker")!;
            TabControl tabs = settings.FindControl<TabControl>("SettingsTabs")!;

            Assert.InRange(settings.Bounds.Width, 800, 900);
            Assert.False(categoryRail.IsVisible);
            Assert.True(categoryPicker.IsEffectivelyVisible);
            Assert.Equal(10, categoryPicker.Items.Count);
            Assert.Equal(0, tabs.SelectedIndex);
            TextBlock[] visibleLegacyHeaders = tabs.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text =>
                    text.IsEffectivelyVisible &&
                    text.Text == "Configuration")
                .ToArray();
            Assert.Empty(visibleLegacyHeaders);

            categoryPicker.SelectedIndex = 5;
            Render();

            Assert.Equal(5, tabs.SelectedIndex);
            Assert.Equal(5, viewModel.SelectedTabIndex);
            Grid[] responsiveForms = settings.GetVisualDescendants()
                .OfType<Grid>()
                .Where(grid =>
                    grid.Classes.Contains("responsive-form") &&
                    grid.IsEffectivelyVisible &&
                    grid.GetVisualAncestors().Contains(tabs))
                .ToArray();
            Assert.NotEmpty(responsiveForms);
            Assert.All(responsiveForms,
                grid => Assert.InRange(grid.ColumnDefinitions.Count, 1, 2));
            Assert.All(responsiveForms, grid =>
            {
                ScrollViewer viewport = grid.GetVisualAncestors()
                    .OfType<ScrollViewer>()
                    .First();
                Point? left = grid.TranslatePoint(new Point(0, 0), viewport);
                Point? right = grid.TranslatePoint(
                    new Point(grid.Bounds.Width, 0), viewport);
                Assert.NotNull(left);
                Assert.NotNull(right);
                Assert.True(left.Value.X >= -1,
                    $"Responsive settings form began outside its viewport: {left.Value.X:0}.");
                Assert.True(right.Value.X <= viewport.Bounds.Width + 1,
                    $"Responsive settings form exceeded its viewport: {right.Value.X:0}/{viewport.Bounds.Width:0}.");
            });

            PageHeader settingsHeader =
                settings.GetVisualDescendants()
                    .OfType<PageHeader>()
                    .Single();
            Grid commandBar =
                settingsHeader.FindControl<Grid>(
                    "CommandBar")!;
            double actionTop = commandBar.TranslatePoint(
                new Point(0, 0), settings)!.Value.Y;
            ScrollViewer activeScroll = responsiveForms[0]
                .GetVisualAncestors()
                .OfType<ScrollViewer>()
                .First();
            activeScroll.Offset = new Vector(0, 400);
            Render();
            Assert.Equal(
                actionTop,
                commandBar.TranslatePoint(new Point(0, 0), settings)!.Value.Y,
                precision: 1);

            categoryPicker.SelectedIndex = 9;
            Render();
            UniformGrid themeGrid = settings.GetVisualDescendants()
                .OfType<UniformGrid>()
                .Single(grid => grid.Classes.Contains("responsive-theme-grid"));
            Assert.Equal(2, themeGrid.Columns);

            Point? pickerCorner = categoryPicker.TranslatePoint(
                new Point(categoryPicker.Bounds.Width, categoryPicker.Bounds.Height),
                settings);
            Assert.NotNull(pickerCorner);
            Assert.InRange(pickerCorner.Value.X, 0, settings.Bounds.Width + 1);
            Assert.InRange(pickerCorner.Value.Y, 0, settings.Bounds.Height + 1);

            window.Width = 1440;
            Render();
            ListBox categoryList =
                settings.FindControl<ListBox>("SettingsCategoryList")!;
            Assert.True(categoryRail.IsEffectivelyVisible);
            Assert.False(categoryPicker.IsVisible);
            categoryList.SelectedIndex = 5;
            Render();
            Assert.Equal(5, tabs.SelectedIndex);
            Assert.All(
                settings.GetVisualDescendants()
                    .OfType<Grid>()
                    .Where(grid =>
                        grid.Classes.Contains("responsive-form") &&
                        grid.IsEffectivelyVisible &&
                        grid.GetVisualAncestors().Contains(tabs)),
                grid => Assert.InRange(
                    grid.ColumnDefinitions.Count, 1, 4));
        }
        finally
        {
            window.Hide();
        }

        static void Render()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Settings_category_list_and_picker_keep_keyboard_selection_synchronized()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.WindowState = WindowState.Normal;
            window.Width = 1440;
            window.Height = 900;
            window.Show();
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Settings);
            RenderUi();

            SettingsView settings =
                Assert.IsType<SettingsView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!.Content);
            SettingsViewModel model =
                Assert.IsType<SettingsViewModel>(
                    settings.DataContext);
            ListBox categoryList =
                settings.FindControl<ListBox>(
                    "SettingsCategoryList")!;
            ComboBox categoryPicker =
                settings.FindControl<ComboBox>(
                    "SettingsCategoryPicker")!;
            TabControl tabs =
                settings.FindControl<TabControl>(
                    "SettingsTabs")!;

            Assert.True(
                categoryList.IsEffectivelyVisible);
            categoryList.SelectedIndex = 4;
            AssertSelection(4);
            SendKey(categoryList, Key.Down);
            AssertSelection(5);
            SendKey(categoryList, Key.Up);
            AssertSelection(4);
            SendKey(categoryList, Key.End);
            AssertSelection(
                categoryList.ItemCount - 1);
            SendKey(categoryList, Key.Home);
            AssertSelection(0);

            window.Width = 900;
            window.Height = 600;
            RenderUi();
            Assert.False(
                categoryList.IsEffectivelyVisible);
            Assert.True(
                categoryPicker.IsEffectivelyVisible);

            categoryPicker.SelectedIndex = 4;
            AssertSelection(4);
            SendKey(categoryPicker, Key.Down);
            AssertSelection(5);
            SendKey(categoryPicker, Key.Up);
            AssertSelection(4);

            void SendKey(
                Control control,
                Key key)
            {
                control.Focus();
                RenderUi();
                control.RaiseEvent(
                    new KeyEventArgs
                    {
                        RoutedEvent =
                            InputElement.KeyDownEvent,
                        Key = key,
                    });
                RenderUi();
            }

            void AssertSelection(
                int expected)
            {
                RenderUi();
                Assert.Equal(
                    expected,
                    model.SelectedTabIndex);
                Assert.Equal(
                    expected,
                    tabs.SelectedIndex);
                Assert.Equal(
                    expected,
                    categoryList.SelectedIndex);
                Assert.Equal(
                    expected,
                    categoryPicker.SelectedIndex);
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Settings_forms_stack_to_one_column_below_600_content_pixels()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var settings = new SettingsView();
        var window = new Window
        {
            // The picker presentation reserves 24 px around the page.
            // A 623 px view therefore gives the form 599 px.
            Width = 623,
            Height = 720,
            FontSize = 18,
            Content = settings,
        };
        try
        {
            window.Show();
            Render();

            ComboBox categoryPicker =
                settings.FindControl<ComboBox>("SettingsCategoryPicker")!;
            categoryPicker.SelectedIndex = 5;
            Render();

            Assert.True(settings.Bounds.Width < 624);
            Assert.True(categoryPicker.IsEffectivelyVisible);
            Grid[] responsiveForms = settings.GetVisualDescendants()
                .OfType<Grid>()
                .Where(grid =>
                    grid.Classes.Contains("responsive-form") &&
                    grid.IsEffectivelyVisible &&
                    grid.GetVisualAncestors().OfType<TabControl>().Any())
                .ToArray();
            Assert.NotEmpty(responsiveForms);
            Assert.All(responsiveForms,
                grid => Assert.Single(grid.ColumnDefinitions));

            categoryPicker.SelectedIndex = 9;
            Render();
            UniformGrid themeGrid = settings.GetVisualDescendants()
                .OfType<UniformGrid>()
                .Single(grid => grid.Classes.Contains("responsive-theme-grid"));
            Assert.Equal(1, themeGrid.Columns);
        }
        finally
        {
            window.Hide();
        }

        static void Render()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Settings_header_keeps_save_visible_and_places_secondary_actions_in_more()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        ILocalizationService localization =
            services.GetRequiredService<
                ILocalizationService>();
        var settings = new SettingsView();
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = settings,
        };
        try
        {
            window.Show();
            SettingsViewModel viewModel =
                Assert.IsType<SettingsViewModel>(
                    settings.DataContext);
            viewModel.DatabaseFile += ".changed";
            RenderUi();

            PageHeader header = Assert.Single(
                settings.GetVisualDescendants()
                    .OfType<PageHeader>());
            Button[] visibleHeaderButtons = header
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(button =>
                    button.IsEffectivelyVisible)
                .ToArray();
            Assert.Contains(
                visibleHeaderButtons,
                button => Equals(
                    button.Content,
                    localization.Get(
                        "Settings.Action.Save")));
            Assert.Contains(
                visibleHeaderButtons,
                button => Equals(
                    button.Content,
                    localization.Get(
                        "Workbench.Action.More")));
            Assert.DoesNotContain(
                visibleHeaderButtons,
                button => Equals(
                    button.Content,
                    localization.Get(
                        "Settings.Action.SaveAs")) ||
                          Equals(
                              button.Content,
                              localization.Get(
                                  "Settings.Action.Discard")));

            Button more = settings.FindControl<Button>(
                "SettingsMoreButton")!;
            MenuFlyout menu =
                Assert.IsType<MenuFlyout>(
                    more.Flyout);
            menu.ShowAt(more);
            RenderUi();

            MenuItem saveAs = Assert.Single(
                menu.Items.OfType<MenuItem>(),
                item => Equals(
                    item.Header,
                    localization.Get(
                        "Settings.Action.SaveAs")));
            MenuItem discard = Assert.Single(
                menu.Items.OfType<MenuItem>(),
                item => Equals(
                    item.Header,
                    localization.Get(
                        "Settings.Action.Discard")));
            Assert.NotNull(saveAs.Command);
            Assert.True(discard.IsVisible);
            Assert.Contains(
                "danger",
                discard.Classes);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Settings_export_profiles_use_summary_cards_and_grouped_advanced_editors()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var settings = new SettingsView();
        var window = new Window
        {
            Width = 1200,
            Height = 760,
            Content = settings,
        };
        try
        {
            window.Show();
            SettingsViewModel viewModel =
                Assert.IsType<SettingsViewModel>(
                    settings.DataContext);
            await viewModel.NewConfigurationCommand
                .ExecuteAsync(null);
            viewModel.AddExportProfileCommand.Execute(
                null);
            ExportProfileEditorRow original =
                Assert.Single(
                    viewModel.ExportProfiles);
            original.Name = "Living room";
            TabControl tabs = settings.FindControl<TabControl>(
                "SettingsTabs")!;
            tabs.SelectedIndex = 2;
            RenderUi();

            Border card = Assert.Single(
                settings.GetVisualDescendants()
                    .OfType<Border>(),
                border =>
                    border.Classes.Contains(
                        "export-profile-card"));
            Button edit = Assert.Single(
                card.GetVisualDescendants()
                    .OfType<Button>(),
                button =>
                    button is not ToggleButton &&
                    Equals(
                    button.Content,
                    "Edit"));
            Button duplicate = Assert.Single(
                card.GetVisualDescendants()
                    .OfType<Button>(),
                button =>
                    button is not ToggleButton &&
                    Equals(
                    button.Content,
                    "Duplicate"));
            Assert.Contains(
                card.GetVisualDescendants()
                    .OfType<Button>(),
                button => Equals(
                    button.Content,
                    "More"));
            Expander editor = Assert.Single(
                card.GetVisualDescendants()
                    .OfType<Expander>(),
                expander =>
                    expander.Name ==
                    "ExportProfileEditorExpander");
            Assert.False(editor.IsExpanded);

            edit.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            RenderUi();
            Assert.True(editor.IsExpanded);
            Expander advanced = Assert.Single(
                card.GetVisualDescendants()
                    .OfType<Expander>(),
                expander =>
                    expander.Name ==
                    "ExportProfileAdvancedExpander");
            advanced.IsExpanded = true;
            RenderUi();

            string[] expectedGroups =
            [
                "Selection",
                "Transcode audio",
                "Naming and artwork",
                "Generated playlists and transport",
                "Reconciliation",
            ];
            string[] visibleTitles = card
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text =>
                    text.IsEffectivelyVisible)
                .Select(text => text.Text ?? "")
                .ToArray();
            Assert.All(
                expectedGroups,
                expected =>
                    Assert.Contains(
                        expected,
                        visibleTitles));

            Assert.NotNull(
                duplicate.Command);
            Assert.Same(
                original,
                duplicate.CommandParameter);
            duplicate.Command!.Execute(
                duplicate.CommandParameter);
            RenderUi();
            Assert.Equal(
                2,
                viewModel.ExportProfiles.Count);
            ExportProfileEditorRow copy =
                viewModel.ExportProfiles[1];
            Assert.NotEqual(
                original.Id,
                copy.Id);
            Assert.Equal(
                "Living room copy",
                copy.Name);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Settings_profile_summary_edit_actions_open_the_selected_profile_editor()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var settings = new SettingsView();
        var window = new Window
        {
            Width = 1000,
            Height = 700,
            Content = settings,
        };
        try
        {
            window.Show();
            TabControl tabs = settings.FindControl<TabControl>(
                "SettingsTabs")!;

            tabs.SelectedIndex = 5;
            RenderUi();
            Expander libraryEditor =
                settings.FindControl<Expander>(
                    "RootPolicyEditorExpander")!;
            Assert.False(
                libraryEditor.IsExpanded);
            settings.FindControl<Button>(
                    "EditLibraryProfileButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            RenderUi();
            Assert.True(
                libraryEditor.IsExpanded);

            tabs.SelectedIndex = 6;
            RenderUi();
            Expander ingestEditor =
                settings.FindControl<Expander>(
                    "IngestProfileEditorExpander")!;
            Assert.False(
                ingestEditor.IsExpanded);
            settings.FindControl<Button>(
                    "EditIngestProfileButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            RenderUi();
            Assert.True(
                ingestEditor.IsExpanded);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Settings_effective_policy_renders_bullets_and_collapsed_provenance()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var settings = new SettingsView();
        var window = new Window
        {
            Width = 1000,
            Height = 700,
            Content = settings,
        };
        try
        {
            window.Show();
            SettingsViewModel viewModel =
                Assert.IsType<SettingsViewModel>(
                    settings.DataContext);
            await viewModel.NewConfigurationCommand
                .ExecuteAsync(null);
            settings.FindControl<TabControl>(
                    "SettingsTabs")!
                .SelectedIndex = 7;
            RenderUi();

            ItemsControl summary =
                settings.FindControl<ItemsControl>(
                    "EffectivePolicySummaryList")!;
            Assert.Equal(
                viewModel.EffectivePolicySummaryItems
                    .Count,
                summary.ItemCount);
            Assert.True(
                summary.ItemCount >= 5);
            Assert.All(
                viewModel.EffectivePolicySummaryItems,
                item =>
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(
                            item));
                    Assert.DoesNotContain(
                        Environment.NewLine,
                        item);
                });
            Expander provenance = Assert.Single(
                settings.GetVisualDescendants()
                    .OfType<Expander>(),
                expander => Equals(
                    expander.Header,
                    "Technical provenance"));
            Assert.False(
                provenance.IsExpanded);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Settings_field_mappings_are_labeled_single_column_cards_at_narrow_width()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var settings = new SettingsView();
        var window = new Window
        {
            Width = 700,
            Height = 720,
            Content = settings,
        };
        try
        {
            window.Show();
            SettingsViewModel viewModel =
                Assert.IsType<SettingsViewModel>(
                    settings.DataContext);
            viewModel.AddFieldMappingCommand.Execute(
                null);
            settings.FindControl<TabControl>(
                    "SettingsTabs")!
                .SelectedIndex = 8;
            RenderUi();

            Grid fields = Assert.Single(
                settings.GetVisualDescendants()
                    .OfType<Grid>(),
                grid =>
                    grid.IsEffectivelyVisible &&
                    grid.Classes.Contains(
                        "field-mapping-fields"));
            Assert.Single(
                fields.ColumnDefinitions);
            Control[] inputs = fields
                .GetVisualDescendants()
                .OfType<Control>()
                .Where(control =>
                    control is ComboBox ||
                    control is TextBox &&
                    !control.GetVisualAncestors()
                        .OfType<ComboBox>()
                        .Any())
                .ToArray();
            Assert.Equal(
                3,
                inputs.Length);
            Assert.All(
                inputs,
                input =>
                {
                    StackPanel field = Assert.Single(
                        input.GetVisualAncestors()
                            .OfType<StackPanel>(),
                        panel =>
                            panel.Classes.Contains(
                                "field"));
                    Assert.Contains(
                        field.GetVisualDescendants()
                            .OfType<TextBlock>(),
                        label =>
                            label.Classes.Contains(
                                "field-label") &&
                            !string.IsNullOrWhiteSpace(
                                label.Text));
                });
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Data_grid_preserves_app_metrics_and_column_contract()
    {
        var grid = new AppDataGrid();
        grid.ConfigureColumns([
            new AppGridColumnDefinition("Title", "Title", "Title", 280, 140),
            new AppGridColumnDefinition("Hidden", "Hidden", "Hidden", 120, 80, false),
            new AppGridColumnDefinition("Path", "Path", "Path", 420, 180),
        ]);

        Assert.Equal(38, grid.RowHeight);
        Assert.Equal(39, grid.ColumnHeaderHeight);
        Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode);
        Assert.Equal(2, grid.Columns.Count);
        Assert.Equal("Title", grid.KeyFor(grid.Columns[0]));
        Assert.Equal(280, grid.CaptureColumnLayout()[0].Width);
    }

    [AvaloniaFact]
    public void Grid_layout_capture_reorders_visible_columns_without_moving_hidden_columns()
    {
        AppGridColumnDefinition[] definitions =
        [
            new("File", "File", "File", 220),
            new("Codec", "Codec", "Codec", 110, Visible: false),
            new("Title", "Title", "Title", 220),
            new("Artwork", "Artwork", "Artwork", 85, Visible: false),
            new("Artist", "Artist", "Artist", 190),
        ];
        LibraryColumnState[] visibleLayout =
        [
            new("Artist", 205, 0, true),
            new("File", 235, 1, true),
            new("Title", 245, 2, true),
        ];

        IReadOnlyList<LibraryColumnState> captured =
            PersistedGridLayout.CaptureSnapshotColumns(
                definitions,
                visibleLayout);

        Assert.Equal(
            ["Artist", "Codec", "File", "Artwork", "Title"],
            captured.Select(column => column.Key));
        Assert.Equal(
            [true, false, true, false, true],
            captured.Select(column => column.Visible));
        Assert.Equal(
            [0, 1, 2, 3, 4],
            captured.Select(column => column.DisplayIndex));
        Assert.Equal(
            [205, 110, 235, 85, 245],
            captured.Select(column =>
                column.Width!.Value));
    }

    [AvaloniaFact]
    public void Data_grid_applies_a_typed_persisted_sort_state()
    {
        LibraryRow[] rows =
        [
            new LibraryRow(new TrackRecord { Path = @"C:\b.flac", Title = "B" }),
            new LibraryRow(new TrackRecord { Path = @"C:\a.flac", Title = "A" }),
        ];
        var view = new global::Avalonia.Collections.DataGridCollectionView(rows);
        var grid = new AppDataGrid
        {
            ItemsSource = view,
        };
        grid.ConfigureColumns([
            new AppGridColumnDefinition("Title", "Title", "Title", 280, 140),
        ]);

        Assert.True(grid.ApplySort(new LibrarySortState("Title", true)));
        Assert.Equal("Title", grid.CurrentSortKey);
        Assert.True(grid.CurrentSortDescending);
        Assert.Equal(["B", "A"], view.Cast<LibraryRow>().Select(row => row.Title));
        Assert.False(grid.ApplySort(new LibrarySortState("Missing", false)));
    }

    [AvaloniaFact]
    public void Data_grid_binds_dynamic_metadata_dictionary_values()
    {
        string valueKey = MetadataGridValueKey.For(
            MetadataFieldKey.Custom("DJ_SET"));
        var row = new LibraryRow(new TrackRecord
        {
            Path = "song.flac",
            Metadata = new Dictionary<string, string[]>
            {
                [CachedMetadataKeys.Custom("DJ_SET")] =
                    ["Morning", "Evening"],
            },
        });
        var grid = new AppDataGrid
        {
            ItemsSource = new[] { row },
        };
        grid.ConfigureColumns(
        [
            new(
                "Metadata.test",
                "DJ set",
                $"MetadataValues[{valueKey}]",
                220),
        ]);
        var window = new Window
        {
            Width = 420,
            Height = 220,
            Content = grid,
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            Assert.Contains(
                grid.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Morning; Evening");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Persisted_grid_layout_restores_widths_and_keeps_keys_isolated()
    {
        var settings = new FakeSettings();
        var state = new GridStateService(settings);
        AppGridColumnDefinition[] definitions =
        [
            new("Path", "Track", "Path", 380, 220),
            new("Reason", "Reason", "Reason", 420, 220),
        ];
        var findings = new AppDataGrid();
        PersistedGridLayout.Configure(findings, state, "health.findings", definitions);
        findings.Columns[0].Width = new DataGridLength(515);

        var restored = new AppDataGrid();
        PersistedGridLayout.Configure(restored, state, "health.findings", definitions);
        var separate = new AppDataGrid();
        PersistedGridLayout.Configure(separate, state, "health.metadata-repairs", definitions);
        IReadOnlyList<AppGridColumnDefinition> withHidden =
            PersistedGridLayout.ApplySnapshot(
                definitions,
                new GridSnapshot(
                [
                    new("Path", 515, 0, false),
                    new("Reason", 420, 1, true),
                ],
                null));

        Assert.Equal(515, restored.Columns[0].Width.Value);
        Assert.Equal(380, separate.Columns[0].Width.Value);
        Assert.False(withHidden[0].Visible);
        Assert.True(withHidden[1].Visible);
    }

    [AvaloniaFact]
    public void Page_header_exposes_native_title_subtitle_and_actions()
    {
        var action = new Button { Content = "Run" };
        var header = new PageHeader
        {
            Title = "Health",
            Subtitle = "Audit the collection.",
            Actions = action,
        };

        Assert.Equal("Health", header.Title);
        Assert.Equal("Audit the collection.", header.Subtitle);
        Assert.Same(action, header.Actions);
    }

    [AvaloniaFact]
    public void Page_header_measures_commands_before_choosing_the_inline_layout()
    {
        var actions = new WrapPanel();
        foreach (string label in new[]
                 {
                     "Index library",
                     "Pending changes (0)",
                     "Hide inspector",
                     "More",
                 })
        {
            actions.Children.Add(
                new Button
                {
                    Content = label,
                    MinWidth = 105,
                });
        }
        var header = new PageHeader
        {
            Title = "Library",
            Subtitle =
                "Browse the cache instantly, even when music roots are offline.",
            Actions = actions,
        };
        var window = new Window
        {
            Width = 812,
            Height = 240,
            Content = header,
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            ContentPresenter presenter =
                header.FindControl<ContentPresenter>(
                    "ActionsPresenter")!;
            Assert.Equal(1, Grid.GetRow(presenter));
            Assert.True(
                presenter.Bounds.Width >=
                actions.DesiredSize.Width - 1);

            double[] buttonTops =
            [
                .. actions.Children
                    .OfType<Button>()
                    .Select(button =>
                        button.TranslatePoint(
                            new Point(),
                            actions)!.Value.Y),
            ];
            Assert.True(
                buttonTops.Max() -
                buttonTops.Min() <= 1,
                "Header commands wrapped even though the stacked action row had enough width.");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void App_buttons_center_content_while_navigation_remains_stretched()
    {
        var button = new Button { Content = "Run" };
        button.Classes.Add("app");
        var navigation = new Button { Content = "Library" };
        navigation.Classes.Add("nav");
        var window = new Window { Content = new StackPanel { Children = { button, navigation } } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Center, button.HorizontalContentAlignment);
            Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, button.VerticalContentAlignment);
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Stretch, navigation.HorizontalContentAlignment);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Split_width_persists_when_the_view_is_recreated()
    {
        var settings = new FakeSettings();
        var state = new SplitStateService(settings);
        state.Save("round-trip", 360);

        var split = new PersistedSplitView(state)
        {
            PersistenceKey = "round-trip",
            InitialLeftWidth = 300,
            MinLeftWidth = 200,
            MaxLeftWidth = 700,
            MinRightWidth = 160,
            Left = new Border(),
            Right = new Border(),
        };
        var window = new Window { Width = 900, Height = 600, Content = split };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(360, split.CurrentLeftWidth);

            split.CommitLeftWidth(384);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(384, state.Load("round-trip"));
        }
        finally
        {
            window.Hide();
        }

        var restored = new PersistedSplitView(state)
        {
            PersistenceKey = "round-trip",
            InitialLeftWidth = 300,
            MinLeftWidth = 200,
            MaxLeftWidth = 700,
            MinRightWidth = 160,
            Left = new Border(),
            Right = new Border(),
        };
        var restoredWindow = new Window { Width = 900, Height = 600, Content = restored };
        try
        {
            restoredWindow.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(384, restored.CurrentLeftWidth);
        }
        finally
        {
            restoredWindow.Hide();
        }
    }

    [AvaloniaFact]
    public void Split_restores_preferred_width_after_a_compact_viewport()
    {
        var settings = new FakeSettings();
        var state = new SplitStateService(settings);
        state.Save(
            "responsive-round-trip",
            700);
        var split = new PersistedSplitView(state)
        {
            PersistenceKey = "responsive-round-trip",
            InitialLeftWidth = 700,
            MinLeftWidth = 200,
            MaxLeftWidth = 900,
            MinRightWidth = 300,
            Left = new Border(),
            Right = new Border(),
        };
        var window = new Window
        {
            Width = 1_200,
            Height = 600,
            Content = split,
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(700, split.CurrentLeftWidth);

            window.Width = 650;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);
            Assert.True(
                split.CurrentLeftWidth < 700);

            split.SetCompact(true);
            window.Width = 1_200;
            split.SetCompact(false);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);

            Assert.Equal(700, split.CurrentLeftWidth);
            Assert.Equal(
                700,
                state.Load(
                    "responsive-round-trip"));
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void
        Responsive_split_minimum_clamps_only_rendered_width_and_round_trips_the_user_preference()
    {
        var settings = new FakeSettings();
        var state =
            new SplitStateService(
                settings);
        state.Save(
            "responsive-minimum",
            360);
        var split =
            new PersistedSplitView(state)
            {
                PersistenceKey =
                    "responsive-minimum",
                InitialLeftWidth = 300,
                MinLeftWidth = 200,
                MaxLeftWidth = 900,
                MinRightWidth = 160,
                Left = new Border(),
                Right = new Border(),
            };
        var window = new Window
        {
            Width = 1_200,
            Height = 600,
            Content = split,
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                360,
                split.CurrentLeftWidth);
            Assert.Equal(
                360,
                split.PreferredLeftWidth);

            split.SetResponsiveMinimumLeftWidth(
                520);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                200,
                split.MinLeftWidth);
            Assert.Equal(
                520,
                split
                    .EffectiveMinimumLeftWidth);
            Assert.Equal(
                520,
                split.CurrentLeftWidth);
            Assert.Equal(
                360,
                split.PreferredLeftWidth);
            Assert.Equal(
                360,
                state.Load(
                    "responsive-minimum"));

            split.SetResponsiveMinimumLeftWidth(
                0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                360,
                split.CurrentLeftWidth);
            Assert.Equal(
                360,
                split.PreferredLeftWidth);

            split.SetResponsiveMinimumLeftWidth(
                520);
            split.CommitLeftWidth(
                440);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                520,
                split.CurrentLeftWidth);
            Assert.Equal(
                440,
                split.PreferredLeftWidth);
            Assert.Equal(
                440,
                state.Load(
                    "responsive-minimum"));
        }
        finally
        {
            window.Hide();
        }

        var restored =
            new PersistedSplitView(state)
            {
                PersistenceKey =
                    "responsive-minimum",
                InitialLeftWidth = 300,
                MinLeftWidth = 200,
                MaxLeftWidth = 900,
                MinRightWidth = 160,
                Left = new Border(),
                Right = new Border(),
            };
        var restoredWindow = new Window
        {
            Width = 1_200,
            Height = 600,
            Content = restored,
        };
        try
        {
            restoredWindow.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                440,
                restored.CurrentLeftWidth);
            Assert.Equal(
                440,
                restored.PreferredLeftWidth);
        }
        finally
        {
            restoredWindow.Hide();
        }
    }

    [AvaloniaFact]
    public void Splitter_keyboard_commands_resize_to_steps_and_bounds()
    {
        var split = new PersistedSplitView(new SplitStateService(new FakeSettings()))
        {
            PersistenceKey = "keyboard",
            Label = "Resize test panes",
            InitialLeftWidth = 360,
            MinLeftWidth = 200,
            MaxLeftWidth = 700,
            MinRightWidth = 160,
            Left = new Border(),
            Right = new Border(),
        };
        var window = new Window
        {
            Width = 1_000,
            Height = 600,
            Content = split,
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            GridSplitter splitter =
                split.FindControl<GridSplitter>("Splitter")!;
            Assert.Equal(
                "Resize test panes",
                AutomationProperties.GetName(splitter));

            RaiseKey(splitter, Key.Right);
            Assert.Equal(384, split.CurrentLeftWidth);
            RaiseKey(splitter, Key.Home);
            Assert.Equal(200, split.CurrentLeftWidth);
            RaiseKey(splitter, Key.End);
            Assert.Equal(700, split.CurrentLeftWidth);
        }
        finally
        {
            window.Hide();
        }

        static void RaiseKey(Control control, Key key)
        {
            control.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
            });
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Light_and_dark_palette_tokens_match_the_app_contract()
    {
        Assert.True(Application.Current!.TryGetResource("AppAccentBrush", ThemeVariant.Dark, out object? darkAccent));
        Assert.True(Application.Current.TryGetResource("AppCanvasBrush", ThemeVariant.Dark, out object? darkCanvas));
        Assert.True(Application.Current.TryGetResource("AppAccentBrush", ThemeVariant.Light, out object? lightAccent));
        Assert.True(Application.Current.TryGetResource("AppCanvasBrush", ThemeVariant.Light, out object? lightCanvas));

        Assert.Equal(Color.Parse("#2CC7BC"), Assert.IsType<SolidColorBrush>(darkAccent).Color);
        Assert.Equal(Color.Parse("#0D1417"), Assert.IsType<SolidColorBrush>(darkCanvas).Color);
        Assert.Equal(Color.Parse("#087F8C"), Assert.IsType<SolidColorBrush>(lightAccent).Color);
        Assert.Equal(Color.Parse("#EEF4F3"), Assert.IsType<SolidColorBrush>(lightCanvas).Color);
    }

    [AvaloniaFact]
    public void Steel_blue_theme_overrides_dark_surfaces_and_restores_the_dark_palette()
    {
        Application app = Application.Current!;
        ThemeVariant? previousTheme = app.RequestedThemeVariant;
        var themes = new ThemeService();
        try
        {
            themes.Apply(ThemeService.SteelBlueTheme);

            Assert.Equal(ThemeService.SteelBlueTheme, themes.Current);
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.True(app.TryGetResource("AppCanvasBrush", ThemeVariant.Dark, out object? canvasValue));
            Assert.True(app.TryGetResource("AppRaisedBrush", ThemeVariant.Dark, out object? raisedValue));
            Assert.True(app.TryGetResource("AppAccentBrush", ThemeVariant.Dark, out object? accentValue));
            Assert.True(app.TryGetResource("AppFaintBrush", ThemeVariant.Dark, out object? faintValue));
            Assert.Equal(Color.Parse("#101C2A"), Assert.IsType<SolidColorBrush>(canvasValue).Color);
            Assert.Equal(Color.Parse("#1D3043"), Assert.IsType<SolidColorBrush>(raisedValue).Color);
            Assert.Equal(Color.Parse("#3AAFB8"), Assert.IsType<SolidColorBrush>(accentValue).Color);
            Assert.True(ContrastRatio(
                Assert.IsType<SolidColorBrush>(faintValue).Color,
                Assert.IsType<SolidColorBrush>(raisedValue).Color) >= 4.5);

            themes.Apply("Dark");
            Assert.True(app.TryGetResource("AppCanvasBrush", ThemeVariant.Dark, out canvasValue));
            Assert.True(app.TryGetResource("AppAccentBrush", ThemeVariant.Dark, out accentValue));
            Assert.Equal(Color.Parse("#0D1417"), Assert.IsType<SolidColorBrush>(canvasValue).Color);
            Assert.Equal(Color.Parse("#2CC7BC"), Assert.IsType<SolidColorBrush>(accentValue).Color);
        }
        finally
        {
            themes.Apply("System");
            app.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public void Faint_text_meets_wcag_contrast_on_app_surfaces()
    {
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.True(Application.Current!.TryGetResource("AppFaintBrush", theme, out object? faintValue));
            Color faint = Assert.IsType<SolidColorBrush>(faintValue).Color;
            foreach (string surfaceKey in new[]
                     {
                         "AppCanvasBrush", "AppPanelBrush", "AppRaisedBrush", "AppInsetBrush",
                     })
            {
                Assert.True(Application.Current.TryGetResource(surfaceKey, theme, out object? surfaceValue));
                Color surface = Assert.IsType<SolidColorBrush>(surfaceValue).Color;
                Assert.True(ContrastRatio(faint, surface) >= 4.5,
                    $"{theme} faint text contrast on {surfaceKey} was {ContrastRatio(faint, surface):0.00}:1");
            }
        }
    }

    [AvaloniaFact]
    public void Larger_text_and_logical_dpi_keep_minimum_header_commands_in_bounds()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        INavigationService navigation = services.GetRequiredService<INavigationService>();
        try
        {
            window.FontSize = 18;
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Width = 900;
            window.Height = 600;
            window.Activate();

            int headersChecked = 0;
            foreach (ShellDestination destination in Enum.GetValues<ShellDestination>())
            {
                navigation.Navigate(destination);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Control activeView = Assert.IsAssignableFrom<Control>(
                    window.FindControl<ContentControl>("ContentHost")!.Content);
                PageHeader? header = activeView.GetVisualDescendants().OfType<PageHeader>().SingleOrDefault();
                if (header is null)
                    continue;
                headersChecked++;
                foreach (Button button in header.GetVisualDescendants().OfType<Button>()
                             .Where(button => button.IsEffectivelyVisible))
                {
                    Point? corner = button.TranslatePoint(
                        new Point(button.Bounds.Width, button.Bounds.Height), header);
                    Assert.NotNull(corner);
                    Assert.InRange(corner.Value.X, 0, header.Bounds.Width + 1);
                    Assert.InRange(corner.Value.Y, 0, header.Bounds.Height + 1);
                }
            }
            Assert.True(headersChecked >= 7);

            using var frame = window.GetLastRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(window.RenderScaling > 0);
            Assert.Equal(window.RenderScaling,
                frame.PixelSize.Width / window.Bounds.Width, precision: 3);
            Assert.Equal(window.RenderScaling,
                frame.PixelSize.Height / window.Bounds.Height, precision: 3);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Advanced_output_editors_are_consolidated_in_workbench()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            INavigationService navigation =
                services.GetRequiredService<INavigationService>();

            navigation.Navigate(ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            WorkbenchView workbench = Assert.IsType<WorkbenchView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            WorkbenchViewModel workbenchModel =
                services.GetRequiredService<WorkbenchViewModel>();
            Carousel sections =
                workbench.FindControl<Carousel>(
                    "WorkbenchTabs")!;
            Assert.Collection(
                sections.Items
                    .OfType<Control>(),
                item => Assert.IsType<
                    WorkbenchSessionSectionView>(
                    item),
                item => Assert.IsType<
                    WorkbenchBulkOperationSectionView>(
                    item),
                item => Assert.IsType<
                    WorkbenchAllFieldsSectionView>(
                    item),
                item => Assert.IsType<
                    WorkbenchFilesSectionView>(
                    item),
                item => Assert.IsType<
                    WorkbenchOnlineMetadataSectionView>(
                    item),
                item => Assert.IsType<
                    WorkbenchReportsSectionView>(
                    item),
                item => Assert.IsType<
                    WorkbenchPlaylistsSectionView>(
                    item),
                item => Assert.IsType<
                    WorkbenchToolsSectionView>(
                    item),
                item => Assert.IsType<
                    WorkbenchShortcutsSectionView>(
                    item));
            Assert.NotNull(
                workbenchModel.FileOperations);
            Assert.Equal(
                9,
                workbenchModel.SectionOptions
                    .Count);

            navigation.Navigate(ShellDestination.Library);
            Dispatcher.UIThread.RunJobs();
            LibraryView library = Assert.IsType<LibraryView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            Assert.Null(library.FindControl<AppDataGrid>(
                "LibraryPlaylistOutputGrid"));
            Assert.Null(library.FindControl<Button>(
                "PreviewLibraryPlaylistButton"));
            Assert.Null(library.FindControl<Button>(
                "ImportLibraryDelimitedMetadataButton"));
            Assert.Null(library.FindControl<Button>(
                "CopyLibraryMetadataFieldButton"));
            Assert.Null(library.FindControl<Button>(
                "PasteLibraryMetadataFieldButton"));
            Assert.Null(library.FindControl<AppDataGrid>(
                "LibraryExternalToolInvocationGrid"));
            Assert.Null(library.FindControl<Button>(
                "PreviewLibraryExternalToolButton"));
            Assert.NotNull(library.FindControl<ListBox>(
                "LibraryMetadataColumnList"));
            Assert.Null(library.FindControl<Border>(
                "LibraryRepresentativePreview"));
            Assert.Null(
                library.FindControl<
                    ReviewedFileOperationEditorView>(
                    "LibraryFileOperationEditor"));
            Assert.Null(library.FindControl<Button>(
                "UndoLibraryOperationButton"));
            Assert.Null(library.FindControl<Button>(
                "RedoLibraryOperationButton"));
            Assert.Null(library.FindControl<Button>(
                "RepeatLibraryRecipeButton"));
            Assert.NotNull(library.FindControl<Button>(
                "LibraryWorkbenchButton"));
            Button visualFilter = library.FindControl<Button>(
                "VisualFilterButton")!;
            Popup visualFilterPopover =
                library.FindControl<Popup>(
                    "VisualFilterPopover")!;
            visualFilter.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            Assert.True(visualFilterPopover.IsOpen);
            Assert.NotNull(library.FindControl<ListBox>(
                "VisualFilterConditionList"));
            Assert.Equal(
                "Apply visual filter",
                library.FindControl<Button>(
                    "ApplyVisualFilterButton")!.Content);

        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Library_handoff_captures_scope_and_preserves_library_state()
    {
        TrackRecord[] records =
        [
            new()
            {
                Path = Path.GetFullPath(
                    "handoff-aurora.flac"),
                Title = "Aurora",
                Artist = "The Fixtures",
                CodecName = "FLAC",
            },
            new()
            {
                Path = Path.GetFullPath(
                    "handoff-harbor.flac"),
                Title = "Harbor",
                Artist = "The Fixtures",
                CodecName = "FLAC",
            },
        ];
        var workbench =
            new RecordingWorkbenchService();
        using ServiceProvider services =
            BuildIsolatedServices(
                records,
                collection =>
                    collection.AddSingleton<
                        IWorkbenchService>(
                        workbench));
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            INavigationService navigation =
                services.GetRequiredService<
                    INavigationService>();
            navigation.Navigate(
                ShellDestination.Library);
            Dispatcher.UIThread.RunJobs();
            LibraryView library =
                Assert.IsType<LibraryView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!
                        .Content);
            LibraryViewModel libraryModel =
                services.GetRequiredService<
                    LibraryViewModel>();
            await libraryModel.ReloadAsync();
            libraryModel.FilterText = "Aurora";
            await libraryModel
                .ApplyFilterNowAsync();
            LibraryRow selected =
                Assert.Single(
                    libraryModel.Rows);
            Assert.True(
                await libraryModel.SelectAsync(
                    [selected]));
            string filterBefore =
                libraryModel.FilterText!;
            string[] selectedBefore =
            [
                .. libraryModel.SelectedPaths,
            ];

            await libraryModel
                .EditSelectedInWorkbenchCommand
                .ExecuteAsync(
                    nameof(
                        WorkbenchSection.Files));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                ShellDestination.Workbench,
                navigation.Current);
            Assert.Equal(
                [records[0].Path],
                Assert.Single(
                    workbench.Requests)
                    .Sources);
            WorkbenchViewModel workbenchModel =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            Assert.Equal(
                WorkbenchSection.Files,
                workbenchModel.SelectedSection);
            Assert.Equal(
                [records[0].Path],
                workbenchModel.SelectedFiles
                    .Select(file => file.Path));
            Assert.Equal(
                filterBefore,
                libraryModel.FilterText);
            Assert.Equal(
                selectedBefore,
                libraryModel.SelectedPaths);
            Assert.Single(
                libraryModel.Rows);
            Assert.Same(
                selected,
                libraryModel.Rows[0]);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Workbench_grid_multi_selection_projects_mixed_metadata()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            INavigationService navigation =
                services.GetRequiredService<INavigationService>();
            navigation.Navigate(ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            WorkbenchView view = Assert.IsType<WorkbenchView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<WorkbenchViewModel>();
            model.Files.Add(Track("first.flac", "First artist"));
            model.Files.Add(Track("second.flac", "Second artist"));
            Dispatcher.UIThread.RunJobs();

            AppDataGrid grid =
                view.FindControl<AppDataGrid>("WorkbenchGrid")!;
            grid.SelectedItems.Add(model.Files[0]);
            grid.SelectedItems.Add(model.Files[1]);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, model.SelectedFileCount);
            Assert.Equal("2 files selected", model.FieldSelectionSummary);
            WorkbenchMetadataFieldRow artist =
                model.MetadataFields.Single(row =>
                    row.Field.KnownField == TagFields.Artist);
            Assert.True(artist.IsMixed);
            Assert.Equal("2/2 files", artist.Coverage);
            Assert.Empty(model.FieldValuesText ?? "");

            static WorkbenchTrackViewModel Track(
                string path,
                string artist) =>
                new(new MediaDocument(
                    path,
                    [new(
                        "VorbisComment",
                        [new(
                            MetadataFieldKey.Known(
                                TagFields.Artist),
                            [artist])],
                        true,
                        true,
                        true,
                        true)],
                    [],
                    null,
                    new(
                        path,
                        10,
                        DateTime.UtcNow,
                        "hash"),
                    true));
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Workbench_sections_persist_and_compact_layout_uses_a_drawer()
    {
        var settings = new FakeSettings();
        using (ServiceProvider services =
               BuildIsolatedServices(
                   configureServices: collection =>
                       collection.AddSingleton<
                           IAppSettings>(settings)))
        {
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
                services.GetRequiredService<
                        INavigationService>()
                    .Navigate(
                        ShellDestination.Workbench);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform
                    .ForceRenderTimerTick(2);

                WorkbenchView view =
                    Assert.IsType<WorkbenchView>(
                        window.FindControl<
                            ContentControl>(
                            "ContentHost")!.Content);
                WorkbenchViewModel model =
                    services.GetRequiredService<
                        WorkbenchViewModel>();
                Assert.True(
                    view.FindControl<ComboBox>(
                        "WorkbenchSectionPicker")!
                        .IsVisible);
                Assert.False(
                    view.FindControl<Border>(
                        "WorkbenchSectionRail")!
                        .IsVisible);

                model.SelectedSection =
                    WorkbenchSection.Reports;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(
                    (int)WorkbenchSection.Reports,
                    view.FindControl<Carousel>(
                        "WorkbenchTabs")!
                        .SelectedIndex);
                Assert.False(
                    view.FindControl<Button>(
                        "WorkbenchInspectorToggle")!
                        .IsVisible);
                ContentPresenter inactiveDrawerHost =
                    view.FindControl<
                            PersistedSplitView>(
                            "WorkbenchSplit")!
                        .FindControl<
                            ContentPresenter>(
                            "RightPresenter")!;
                Assert.True(
                    inactiveDrawerHost.IsVisible);
                Assert.False(
                    inactiveDrawerHost
                        .IsHitTestVisible);
                Assert.Equal(
                    0,
                    inactiveDrawerHost.Opacity);

                model.SelectedSection =
                    WorkbenchSection.Session;
                model.IsInspectorOpen = true;
                Dispatcher.UIThread.RunJobs();
                Button inspector =
                    view.FindControl<Button>(
                        "WorkbenchInspectorToggle")!;
                Assert.True(inspector.IsVisible);
                inspector.RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.True(
                    view.FindControl<Border>(
                        "WorkbenchInspectorScrim")!
                        .IsVisible);
                view.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Escape,
                });
                Assert.False(
                    view.FindControl<Border>(
                        "WorkbenchInspectorScrim")!
                        .IsVisible);
                Assert.True(
                    view.FindControl<AppDataGrid>(
                        "WorkbenchGrid")!
                        .Bounds.Height >= 220);

                view.FindControl<Button>(
                        "WorkbenchPendingChangesButton")!
                    .RaiseEvent(
                        new RoutedEventArgs(
                            Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Border pendingPane =
                    view.FindControl<Border>(
                        "WorkbenchDrawerPane")!;
                Assert.True(
                    pendingPane.Width <=
                        view.Bounds.Width - 20);
                Assert.True(
                    view.FindControl<Control>(
                        "WorkbenchPendingChangesDrawer")!
                        .IsVisible);
                view.FindControl<Button>(
                        "WorkbenchPendingChangesButton")!
                    .RaiseEvent(
                        new RoutedEventArgs(
                            Button.ClickEvent));

                model.SelectedSection =
                    WorkbenchSection.Tools;
                model.IsInspectorOpen = false;
            }
            finally
            {
                window.Hide();
            }
        }

        using ServiceProvider restoredServices =
            BuildIsolatedServices(
                configureServices: collection =>
                    collection.AddSingleton<
                        IAppSettings>(settings));
        WorkbenchViewModel restored =
            restoredServices.GetRequiredService<
                WorkbenchViewModel>();
        Assert.Equal(
            WorkbenchSection.Tools,
            restored.SelectedSection);
        Assert.False(restored.IsInspectorOpen);
    }

    [AvaloniaFact]
    public void Workbench_commands_and_bulk_fields_are_contextual()
    {
        using ServiceProvider services =
            BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Width = 1440;
            window.Height = 900;
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            WorkbenchView view =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();

            Assert.True(
                view.FindControl<Border>(
                    "WorkbenchSectionRail")!
                    .IsVisible);
            Assert.False(
                view.FindControl<ComboBox>(
                    "WorkbenchSectionPicker")!
                    .IsVisible);
            Button workbenchMoreButton =
                view.FindControl<Button>(
                    "WorkbenchMoreButton")!;
            MenuFlyout workbenchMore =
                Assert.IsType<MenuFlyout>(
                    workbenchMoreButton.Flyout);
            Assert.Equal(
                5,
                workbenchMore.Items.Count);
            workbenchMore.ShowAt(
                workbenchMoreButton);
            RenderUi();
            Assert.Contains(
                workbenchMore.Items
                    .OfType<MenuItem>(),
                item => Equals(
                    item.Header,
                    view.FindControl<Button>(
                        "WorkbenchInspectorToggle")!
                        .Content));
            workbenchMore.Hide();
            SplitButton addSources =
                view.FindControl<SplitButton>(
                    "AddWorkbenchSourceButton")!;
            Flyout addSourcesFlyout =
                Assert.IsType<Flyout>(
                    addSources.Flyout);
            Control addSourcesContent =
                Assert.IsAssignableFrom<Control>(
                    addSourcesFlyout.Content);
            Assert.Contains(
                addSourcesContent
                    .GetLogicalDescendants()
                    .OfType<Button>(),
                button =>
                    button.Name ==
                        "ChooseWorkbenchFilesButton");
            Assert.Contains(
                addSourcesContent
                    .GetLogicalDescendants()
                    .OfType<Button>(),
                button =>
                    button.Name ==
                        "ChooseWorkbenchFolderButton");
            Assert.Contains(
                addSourcesContent
                    .GetLogicalDescendants()
                    .OfType<Button>(),
                button =>
                    button.Name ==
                        "AddRecentWorkbenchSourceButton");
            Assert.Contains(
                addSourcesContent
                    .GetLogicalDescendants()
                    .OfType<CheckBox>(),
                checkBox =>
                    checkBox.Name ==
                        "WorkbenchIncludeSubfoldersCheckBox");
            ContextMenu sessionMenu =
                Assert.IsType<ContextMenu>(
                    view.FindControl<AppDataGrid>(
                        "WorkbenchGrid")!
                        .ContextMenu);
            view.FindControl<AppDataGrid>(
                    "WorkbenchGrid")!
                .Focus();
            RenderUi();
            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent =
                    InputElement.KeyDownEvent,
                Key = Key.F10,
                KeyModifiers =
                    KeyModifiers.Shift,
            });
            Dispatcher.UIThread.RunJobs();
            Assert.True(sessionMenu.IsOpen);
            sessionMenu.Close();

            model.SelectedSection =
                WorkbenchSection.BulkOperation;
            Dispatcher.UIThread.RunJobs();
            Button stepActions =
                view.FindControl<Button>(
                    "WorkbenchRecipeStepActionsButton")!;
            Assert.Equal(
                5,
                Assert.IsType<MenuFlyout>(
                    stepActions.Flyout)
                    .Items.Count);
            Button recipeMore =
                view.FindControl<Button>(
                    "WorkbenchRecipeMoreButton")!;
            Assert.Equal(
                4,
                Assert.IsType<MenuFlyout>(
                    recipeMore.Flyout)
                    .Items.Count);
            model.OperationEditor
                .SelectedOperation =
                model.OperationEditor
                    .OperationDescriptors
                    .Single(operation =>
                        operation.Kind ==
                        MetadataOperationKind.Assign);
            Dispatcher.UIThread.RunJobs();
            Assert.True(
                view.FindControl<StackPanel>(
                    "WorkbenchOperationValuePanel")!
                    .IsVisible);
            Assert.False(
                view.FindControl<StackPanel>(
                    "WorkbenchOperationDestinationPanel")!
                    .IsVisible);

            model.OperationEditor
                .SelectedOperation =
                model.OperationEditor
                    .OperationDescriptors
                    .Single(operation =>
                        operation.Kind ==
                        MetadataOperationKind.Copy);
            Dispatcher.UIThread.RunJobs();
            Assert.True(
                view.FindControl<StackPanel>(
                    "WorkbenchOperationDestinationPanel")!
                    .IsVisible);
            Assert.False(
                view.FindControl<StackPanel>(
                    "WorkbenchOperationValuePanel")!
                    .IsVisible);

            model.OperationEditor
                .SelectedOperation =
                model.OperationEditor
                    .OperationDescriptors
                    .Single(operation =>
                        operation.Kind ==
                        MetadataOperationKind.ReplaceText);
            Dispatcher.UIThread.RunJobs();
            Assert.True(
                view.FindControl<StackPanel>(
                    "WorkbenchOperationFindPanel")!
                    .IsVisible);
            Assert.True(
                view.FindControl<StackPanel>(
                    "WorkbenchOperationReplacementPanel")!
                    .IsVisible);

            model.OperationEditor
                .SelectedOperation =
                model.OperationEditor
                    .OperationDescriptors
                    .Single(operation =>
                        operation.Kind ==
                        MetadataOperationKind
                            .ExtractPathComponent);
            Dispatcher.UIThread.RunJobs();
            Assert.True(
                view.FindControl<StackPanel>(
                    "WorkbenchOperationPathPanel")!
                    .IsVisible);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Workbench_transient_surfaces_share_one_owner_and_restore_focus()
    {
        using ServiceProvider services =
            BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.WindowState =
                WindowState.Normal;
            window.Width = 900;
            window.Height = 640;
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);

            WorkbenchView view =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!.Content);
            Button inspector =
                view.FindControl<Button>(
                    "WorkbenchInspectorToggle")!;
            Button pending =
                view.FindControl<Button>(
                    "WorkbenchPendingChangesButton")!;
            Button columns =
                view.FindControl<Button>(
                    "WorkbenchColumnsButton")!;
            Control inspectorDrawer =
                view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!;
            Control pendingDrawer =
                view.FindControl<Control>(
                    "WorkbenchPendingChangesDrawer")!;
            Control columnsDrawer =
                view.FindControl<Control>(
                    "WorkbenchColumnsDrawer")!;
            Border drawerPane =
                view.FindControl<Border>(
                    "WorkbenchDrawerPane")!;
            Border inspectorScrim =
                view.FindControl<Border>(
                    "WorkbenchInspectorScrim")!;

            inspector.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(inspectorScrim.IsVisible);
            Assert.True(inspectorDrawer.IsVisible);
            Assert.Same(
                view.FindControl<Button>(
                    "InspectorCloseButton"),
                TopLevel.GetTopLevel(view)!
                    .FocusManager!
                    .GetFocusedElement());

            pending.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(pendingDrawer.IsVisible);
            Assert.False(columnsDrawer.IsVisible);
            Assert.False(inspectorDrawer.IsVisible);
            Assert.True(inspectorScrim.IsVisible);
            Assert.True(drawerPane.Width <= 430);
            Assert.Same(
                view.FindControl<Button>(
                    "WorkbenchPendingChangesCloseButton"),
                TopLevel.GetTopLevel(view)!
                    .FocusManager!
                    .GetFocusedElement());

            columns.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(pendingDrawer.IsVisible);
            Assert.True(columnsDrawer.IsVisible);
            Assert.False(inspectorDrawer.IsVisible);
            Assert.True(inspectorScrim.IsVisible);
            Assert.True(drawerPane.Width <= 430);
            Assert.Same(
                view.FindControl<Button>(
                    "WorkbenchColumnsCloseButton"),
                TopLevel.GetTopLevel(view)!
                    .FocusManager!
                    .GetFocusedElement());

            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent =
                    InputElement.KeyDownEvent,
                Key = Key.Escape,
            });
            Dispatcher.UIThread.RunJobs();
            Assert.False(columnsDrawer.IsVisible);
            Assert.True(inspectorDrawer.IsVisible);
            Assert.True(inspectorScrim.IsVisible);
            Assert.Same(
                columns,
                TopLevel.GetTopLevel(view)!
                    .FocusManager!
                    .GetFocusedElement());

            pending.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            view.FindControl<Button>(
                    "WorkbenchPendingChangesCloseButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(pendingDrawer.IsVisible);
            Assert.True(inspectorDrawer.IsVisible);
            Assert.True(inspectorScrim.IsVisible);
            Assert.Same(
                pending,
                TopLevel.GetTopLevel(view)!
                    .FocusManager!
                    .GetFocusedElement());
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Workbench_online_metadata_uses_scope_provider_and_advanced_search()
    {
        using ServiceProvider services =
            BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Width = 1440;
            window.Height = 900;
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();

            WorkbenchView view =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            model.SelectedSection =
                WorkbenchSection.OnlineMetadata;
            Dispatcher.UIThread.RunJobs();

            ComboBox scope =
                view.FindControl<ComboBox>(
                    "OnlineMetadataDiscoveryScopePicker")!;
            ComboBox provider =
                view.FindControl<ComboBox>(
                    "OnlineMetadataProviderPicker")!;
            Button discover =
                view.FindControl<Button>(
                    "OnlineMetadataDiscoverButton")!;
            Button search =
                view.FindControl<Button>(
                    "OnlineMetadataSearchButton")!;
            Expander advanced =
                view.FindControl<Expander>(
                    "OnlineMetadataAdvancedSearch")!;
            Button artworkActions =
                view.FindControl<Button>(
                    "StagedArtworkActionsButton")!;

            Assert.Equal(
                2,
                Assert.IsAssignableFrom<
                        IEnumerable<
                            WorkbenchOnlineMetadataScopeOption>>(
                        scope.ItemsSource)
                    .Count());
            Assert.Equal(
                2,
                Assert.IsAssignableFrom<
                        IEnumerable<
                            WorkbenchOnlineMetadataProviderOption>>(
                        provider.ItemsSource)
                    .Count());
            Assert.Same(
                model.DiscoverOnlineAudioCommand,
                discover.Command);
            Assert.Same(
                model.SearchOnlineReleasesCommand,
                search.Command);
            Assert.False(advanced.IsExpanded);
            Assert.Equal(
                6,
                Assert.IsType<MenuFlyout>(
                    artworkActions.Flyout)
                    .Items.Count);
            Assert.DoesNotContain(
                view.GetVisualDescendants()
                    .OfType<Button>(),
                button =>
                    Equals(
                        button.Content,
                        "Selected file") ||
                    Equals(
                        button.Content,
                        "All Workbench files"));

            model.SelectedOnlineMetadataProvider =
                model.OnlineMetadataProviderOptions
                    .Single(option =>
                        option.Provider ==
                        WorkbenchOnlineMetadataProvider
                            .Discogs);
            Dispatcher.UIThread.RunJobs();
            Assert.True(
                model.IsDiscogsOnlineMetadataProvider);
            Assert.False(
                model.IsMusicBrainzOnlineMetadataProvider);
            TabControl results =
                view.FindControl<TabControl>(
                    "OnlineMetadataResultsTabs")!;
            Assert.Equal(
                5,
                results.Items.Count);
            model.SelectedOnlineMetadataResultStep =
                WorkbenchOnlineMetadataResultStep
                    .DiscogsReleases;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                (int)WorkbenchOnlineMetadataResultStep
                    .DiscogsReleases,
                results.SelectedIndex);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Online_metadata_diagnostic_tooltips_follow_async_and_recycled_rows()
    {
        using ServiceProvider services =
            BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            INavigationService navigation =
                services.GetRequiredService<
                    INavigationService>();

            navigation.Navigate(
                ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            WorkbenchView workbench =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            model.SelectedSection =
                WorkbenchSection.OnlineMetadata;
            model.SelectedOnlineMetadataResultStep =
                WorkbenchOnlineMetadataResultStep
                    .Artwork;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);
            AssertDiagnosticToolTipBinding(
                workbench.FindControl<AppDataGrid>(
                    "ReleaseArtworkGrid")!);
        }
        finally
        {
            window.Hide();
        }

        static void AssertDiagnosticToolTipBinding(
            AppDataGrid grid)
        {
            DataGridTemplateColumn statusColumn =
                Assert.IsType<DataGridTemplateColumn>(
                    grid.Columns.Single(column =>
                        grid.KeyFor(column) ==
                        "Status"));
            CoverArtCandidateRow first =
                CreateArtworkRow("cover-1");
            TextBlock cell =
                Assert.IsType<TextBlock>(
                    statusColumn.CellTemplate!
                        .Build(first));
            cell.DataContext = first;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(
                ToolTip.GetTip(cell));

            first.ThumbnailDiagnosticDetail =
                "HTTP 503 for cover-1";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                first.ThumbnailDiagnosticDetail,
                ToolTip.GetTip(cell));

            CoverArtCandidateRow second =
                CreateArtworkRow("cover-2");
            second.ThumbnailDiagnosticDetail =
                "HTTP 404 for cover-2";
            cell.DataContext = second;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                second.ThumbnailDiagnosticDetail,
                ToolTip.GetTip(cell));
        }

        static CoverArtCandidateRow CreateArtworkRow(
            string id) =>
            new(
                new CoverArtArchiveCandidate(
                    Guid.NewGuid(),
                    id,
                    new Uri(
                        $"https://example.test/{id}.jpg"),
                    null,
                    [],
                    IsFront: false,
                    IsBack: false,
                    Approved: false,
                    Comment: null));
    }

    [AvaloniaFact]
    public void Workbench_sections_are_extracted_and_narrow_forms_stack()
    {
        using ServiceProvider services =
            BuildIsolatedServices();
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
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);

            WorkbenchView view =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            Carousel sections =
                view.FindControl<Carousel>(
                    "WorkbenchTabs")!;
            Assert.Collection(
                sections.Items,
                item => Assert.IsType<
                    WorkbenchSessionSectionView>(item),
                item => Assert.IsType<
                    WorkbenchBulkOperationSectionView>(item),
                item => Assert.IsType<
                    WorkbenchAllFieldsSectionView>(item),
                item => Assert.IsType<
                    WorkbenchFilesSectionView>(item),
                item => Assert.IsType<
                    WorkbenchOnlineMetadataSectionView>(item),
                item => Assert.IsType<
                    WorkbenchReportsSectionView>(item),
                item => Assert.IsType<
                    WorkbenchPlaylistsSectionView>(item),
                item => Assert.IsType<
                    WorkbenchToolsSectionView>(item),
                item => Assert.IsType<
                    WorkbenchShortcutsSectionView>(item));

            model.SelectedSection =
                WorkbenchSection.Reports;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);
            WorkbenchReportsSectionView reports =
                Assert.IsType<
                    WorkbenchReportsSectionView>(
                    sections.SelectedItem);
            Grid reportLayout =
                reports.FindControl<Grid>(
                    "SectionLayout")!;
            Grid reportResults =
                reports.FindControl<Grid>(
                    "ReviewedPanel")!;
            Assert.Single(
                reportLayout.ColumnDefinitions);
            Assert.Equal(
                2,
                Grid.GetRow(reportResults));

            model.SelectedSection =
                WorkbenchSection.Tools;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);
            WorkbenchToolsSectionView tools =
                Assert.IsType<
                    WorkbenchToolsSectionView>(
                    sections.SelectedItem);
            Grid toolsLayout =
                tools.FindControl<Grid>(
                    "SectionLayout")!;
            Assert.Single(
                toolsLayout.ColumnDefinitions);
            Assert.Equal(
                2,
                Grid.GetRow(
                    tools.FindControl<Grid>(
                        "ReviewedPanel")!));
            Button savedToolMore =
                tools.FindControl<Button>(
                    "SavedToolMoreButton")!;
            MenuFlyout savedToolMenu =
                Assert.IsType<MenuFlyout>(
                    savedToolMore.Flyout);
            Assert.Collection(
                savedToolMenu.Items,
                item => Assert.IsType<MenuItem>(
                    item),
                item => Assert.IsType<MenuItem>(
                    item),
                item => Assert.IsType<
                    Separator>(item),
                item => Assert.IsType<MenuItem>(
                    item));

            PageHeader header =
                view.FindControl<PageHeader>(
                    "WorkbenchHeader")!;
            Assert.Equal(
                string.Empty,
                header.Subtitle);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Workbench_navigation_and_tag_actions_expose_context()
    {
        using ServiceProvider services =
            BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Width = 1440;
            window.Height = 900;
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();

            WorkbenchView view =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!.Content);
            ListBox navigation =
                view.FindControl<ListBox>(
                    "WorkbenchSectionNavigation")!;
            WorkbenchSectionOption session =
                Assert.IsType<
                    WorkbenchSectionOption>(
                    navigation.SelectedItem);
            Assert.Equal(
                WorkbenchSection.Session,
                session.Section);
            Assert.Equal(
                0,
                navigation.SelectedIndex);

            WorkbenchSectionOption bulk =
                Assert.Single(
                    navigation.Items
                        .OfType<
                            WorkbenchSectionOption>(),
                    option =>
                        option.Section ==
                        WorkbenchSection
                            .BulkOperation);
            navigation.SelectedItem = bulk;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                WorkbenchSection.BulkOperation,
                services.GetRequiredService<
                        WorkbenchViewModel>()
                    .SelectedSection);
            Assert.Equal(
                bulk,
                navigation.SelectedItem);

            navigation.Focus();
            navigation.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.End,
                });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                navigation.ItemCount - 1,
                navigation.SelectedIndex);
            navigation.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Home,
                });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                0,
                navigation.SelectedIndex);

            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            var tagDocument =
                new MediaDocument(
                    "tag-layers.mp3",
                    [],
                    [],
                    null,
                    new(
                        "tag-layers.mp3",
                        10,
                        DateTime.UtcNow,
                        "tag-layers"),
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
                            true,
                            true,
                            false),
                    ],
                };
            var tagFile =
                new WorkbenchTrackViewModel(
                    tagDocument);
            model.Files.Add(tagFile);
            model.SetSelectedFiles(
                [tagFile]);
            Dispatcher.UIThread.RunJobs();
            Button inspectorToggle =
                view.FindControl<Button>(
                    "WorkbenchInspectorToggle")!;
            if (!view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!
                .IsVisible)
            {
                inspectorToggle.RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            }
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);
            view.FindControl<Expander>(
                    "WorkbenchTagToolsExpander")!
                .IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform
                .ForceRenderTimerTick(2);

            Control inspectorDrawer =
                view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!;
            string[] tagActionNames =
                inspectorDrawer
                    .GetLogicalDescendants()
                    .OfType<Button>()
                    .Select(
                        AutomationProperties.GetName)
                    .Where(name =>
                        !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToArray();
            Assert.Contains(
                "ID3v2 actions",
                tagActionNames);
            Assert.Contains(
                "APEv2 actions",
                tagActionNames);
            Assert.Contains(
                "ID3v1 actions",
                tagActionNames);
            AssertLayerMenu(
                "ID3v2 actions",
                "Add ID3v2 tag layer");
            AssertLayerMenu(
                "APEv2 actions",
                "Add APEv2 tag layer");
            AssertLayerMenu(
                "ID3v1 actions",
                "Add ID3v1 tag layer",
                "Remove ID3v1 tag layer");

            void AssertLayerMenu(
                string launcherName,
                params string[] expectedActions)
            {
                Button launcher =
                    inspectorDrawer
                        .GetLogicalDescendants()
                        .OfType<Button>()
                        .Single(button =>
                            AutomationProperties
                                .GetName(button) ==
                            launcherName);
                MenuFlyout flyout =
                    Assert.IsType<MenuFlyout>(
                        launcher.Flyout);
                flyout.ShowAt(launcher);
                Dispatcher.UIThread.RunJobs();
                string[] actions =
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
                foreach (string expected in
                         expectedActions)
                    Assert.Contains(
                        expected,
                        actions);
                flyout.Hide();
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Workbench_column_order_round_trips_through_grid_state()
    {
        var settings = new FakeSettings();
        using ServiceProvider services = BuildIsolatedServices(
            configureServices: collection =>
                collection.AddSingleton<IAppSettings>(
                    settings));
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            services.GetRequiredService<INavigationService>()
                .Navigate(ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            WorkbenchView view = Assert.IsType<WorkbenchView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "WorkbenchGrid")!;
            DataGridColumn album = grid.Columns.Single(column =>
                grid.KeyFor(column) == "Album");

            album.DisplayIndex = 0;
            Dispatcher.UIThread.RunJobs();

            view.FindControl<Button>(
                    "WorkbenchColumnsButton")!
                .RaiseEvent(new RoutedEventArgs(
                    Button.ClickEvent));
            CheckBox formatVisibility =
                view.FindControl<StackPanel>(
                        "WorkbenchColumnOptions")!
                    .GetVisualDescendants()
                    .OfType<CheckBox>()
                    .Single(check =>
                        Equals(check.Tag, "Format"));
            formatVisibility.IsChecked = false;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                "Album",
                grid.KeyFor(grid.Columns
                    .OrderBy(column =>
                        column.DisplayIndex)
                    .First()));

            GridSnapshot snapshot = Assert.IsType<GridSnapshot>(
                services.GetRequiredService<GridStateService>()
                    .Load("workbench.session"));
            Assert.Equal(
                "Album",
                snapshot.Columns
                    .OrderBy(column => column.DisplayIndex)
                    .First().Key);

            var restored = new WorkbenchView();
            var restoredHost = new Window
            {
                Width = 1200,
                Height = 760,
                Content = restored,
            };
            try
            {
                restoredHost.Show();
                Dispatcher.UIThread.RunJobs();
                AppDataGrid restoredGrid =
                    restored.FindControl<AppDataGrid>(
                        "WorkbenchGrid")!;
                Assert.Equal(
                    "Album",
                    restoredGrid.KeyFor(
                        restoredGrid.Columns
                            .OrderBy(column =>
                                column.DisplayIndex)
                            .First()));
            }
            finally
            {
                restoredHost.Hide();
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Workbench_pending_drawer_keeps_reviewed_preview_and_later_grid_draft()
    {
        var loader = new RecordingWorkbenchService();
        using ServiceProvider services = BuildIsolatedServices(
            configureServices: collection =>
                collection.AddSingleton<IWorkbenchService>(
                    loader));
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Width = 1200;
            window.Height = 700;
            services.GetRequiredService<INavigationService>()
                .Navigate(ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            WorkbenchView view = Assert.IsType<WorkbenchView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<WorkbenchViewModel>();
            string path = Path.GetFullPath(
                "composed-pending.flac");
            await model.AddSourcesAsync([path]);
            WorkbenchTrackViewModel file =
                Assert.Single(model.Files);
            MetadataFieldKey title =
                MetadataFieldKey.Known(
                    TagFields.Title);
            var plan = new MetadataOperationPlan(
                Guid.NewGuid(),
                "Bulk preview",
                [
                    new(
                        path,
                        file.Document.Snapshot,
                        [
                            new(
                                title,
                                ["composed-pending"],
                                ["Bulk title"]),
                        ],
                        [
                            new(
                                title,
                                ["Bulk title"]),
                        ],
                        []),
                ],
                DateTimeOffset.UtcNow);
            Assert.True(await model.AddPendingMutationAsync(
                ReviewedMetadataMutationIntent.Create(
                    plan),
                TestContext.Current.CancellationToken));

            file.Artist = "Grid artist";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, model.PendingChanges.Count);
            Button review = view.FindControl<Button>(
                "WorkbenchPendingChangesButton")!;
            review.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            ListBox pending = view.FindControl<ListBox>(
                "WorkbenchPendingChangesGrid")!;
            Assert.Equal(2, pending.ItemCount);

            await model.RevertPendingChangesCommand
                .ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(model.PendingChanges);
            Assert.Contains(
                "0",
                ((TextBlock)review.Content!).Text);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Workbench_controls_load_recent_and_dropped_sources()
    {
        var loader = new RecordingWorkbenchService();
        using ServiceProvider services = BuildIsolatedServices(
            configureServices: collection =>
                collection.AddSingleton<IWorkbenchService>(loader));
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Width = 1440;
            window.Height = 900;
            services.GetRequiredService<INavigationService>()
                .Navigate(ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            WorkbenchView view = Assert.IsType<WorkbenchView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<WorkbenchViewModel>();
            string recentPath = Path.GetFullPath("recent-source.flac");
            model.Recursive = false;
            model.RecentLocations.Add(recentPath);
            SplitButton addSources =
                view.FindControl<SplitButton>(
                    "AddWorkbenchSourceButton")!;
            addSources.Flyout!.ShowAt(addSources);
            Dispatcher.UIThread.RunJobs();
            view.FindControl<ComboBox>("RecentLocationsBox")!
                .SelectedItem = recentPath;
            Dispatcher.UIThread.RunJobs();

            Button addRecent = view.FindControl<Button>(
                "AddRecentWorkbenchSourceButton")!;
            Assert.NotNull(addRecent.Command);
            addRecent.Command.Execute(addRecent.CommandParameter);
            await WaitForUiAsync(() => model.Files.Count == 1);

            WorkbenchLoadRequest recentRequest =
                Assert.Single(loader.Requests);
            Assert.Equal([recentPath], recentRequest.Sources);
            Assert.False(recentRequest.Recursive);
            Assert.Equal(recentPath, model.Files[0].Path);

            model.Files[0].Title = "Intermediate title";
            model.Files[0].Title = "Pending title";

            MetadataPreviewRow pending =
                Assert.Single(model.PendingChanges);
            Assert.Equal(
                Path.GetFileName(recentPath),
                pending.File);
            Assert.Equal("Title", pending.Field);
            Assert.Equal(
                "recent-source",
                pending.Before);
            Assert.Equal("Pending title", pending.After);

            await model.RevertPendingChangesCommand
                .ExecuteAsync(null);

            Assert.Empty(model.PendingChanges);
            Assert.Equal(
                "recent-source",
                model.Files[0].Title);

            string droppedPath =
                Path.GetFullPath("dropped-source.flac");
            await view.AddDroppedSourcesAsync(
                [null, "", " ", droppedPath]);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, loader.Requests.Count);
            Assert.Equal(
                [droppedPath],
                loader.Requests[1].Sources);
            Assert.Equal(
                [recentPath, droppedPath],
                model.Files.Select(file => file.Path));
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Workbench_controls_edit_fields_build_recipes_and_guard_navigation()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Width = 1440;
            window.Height = 900;
            var navigation = Assert.IsType<NavigationService>(
                services.GetRequiredService<INavigationService>());
            navigation.Navigate(ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            WorkbenchView view = Assert.IsType<WorkbenchView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<WorkbenchViewModel>();
            var track = new WorkbenchTrackViewModel(
                new MediaDocument(
                    "editable.flac",
                    [new(
                        "VorbisComment",
                        [new(
                            MetadataFieldKey.Known(TagFields.Title),
                            ["Original title"])],
                        true,
                        true,
                        true,
                        true)],
                    [],
                    null,
                    new(
                        "editable.flac",
                        10,
                        DateTime.UtcNow,
                        "hash"),
                    true));
            model.Files.Add(track);
            model.SelectedFile = track;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            AppDataGrid grid =
                view.FindControl<AppDataGrid>("WorkbenchGrid")!;
            DataGridColumn titleColumn = grid.Columns.Single(
                column => Equals(column.Header, "Title"));
            grid.SelectedItem = track;
            grid.CurrentColumn = titleColumn;
            grid.ScrollIntoView(track, titleColumn);
            Control? editingElement = null;
            grid.PreparingCellForEdit += (_, eventArgs) =>
                editingElement = eventArgs.EditingElement;
            Assert.True(grid.BeginEdit());
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
            TextBox titleEditor =
                Assert.IsType<TextBox>(editingElement);

            titleEditor.Text = "Edited title";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Edited title", track.Title);
            Assert.True(track.HasChanges);
            Assert.True(model.HasUnsavedChanges);

            model.SelectedSection =
                WorkbenchSection.BulkOperation;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                1,
                view.FindControl<Carousel>(
                    "WorkbenchTabs")!.SelectedIndex);
            ComboBox operationPicker =
                view.FindControl<ComboBox>(
                    "WorkbenchOperationPicker")!;
            operationPicker.SelectedItem =
                model.OperationEditor.OperationChoices.Single(
                    choice =>
                        choice.Value.Kind ==
                        MetadataOperationKind.Assign);
            view.FindControl<ComboBox>(
                    "WorkbenchOperationFieldPicker")!
                .SelectedItem =
                model.OperationEditor.Fields.Single(
                    field => field.Field == TagFields.Title);
            TextBox valueEditor =
                view.FindControl<TextBox>(
                    "WorkbenchOperationValueEditor")!;
            valueEditor.Text = "Reviewed value";
            Button addRecipeStep = view.FindControl<Button>(
                "AddWorkbenchRecipeStepButton")!;
            addRecipeStep.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(addRecipeStep.Command);
            addRecipeStep.Command.Execute(
                addRecipeStep.CommandParameter);
            Dispatcher.UIThread.RunJobs();

            MetadataRecipeStepViewModel step =
                Assert.Single(model.OperationEditor.Steps);
            var assignment =
                Assert.IsType<AssignFieldOperation>(
                    step.Operation);
            Assert.Equal(
                TagFields.Title,
                assignment.Field.KnownField);
            Assert.Equal("Reviewed value", assignment.Value);
            Assert.Single(
                view.FindControl<ListBox>(
                    "WorkbenchRecipeSteps")!.Items);

            Task rejectedNavigation =
                navigation.NavigateAsync(
                    ShellDestination.Library);
            await WaitForUiAsync(() =>
                services.GetRequiredService<DialogService>()
                    .Current is ConfirmRequest);
            ConfirmRequest request = Assert.IsType<ConfirmRequest>(
                services.GetRequiredService<DialogService>()
                    .Current);
            Assert.Equal("Leave the Workbench?", request.Title);
            services.GetRequiredService<DialogService>()
                .Complete(false);
            await rejectedNavigation;

            Assert.Equal(
                ShellDestination.Workbench,
                navigation.Current);
            Assert.Same(
                view,
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);

            Task acceptedNavigation =
                navigation.NavigateAsync(
                    ShellDestination.Library);
            await WaitForUiAsync(() =>
                services.GetRequiredService<DialogService>()
                    .Current is ConfirmRequest);
            services.GetRequiredService<DialogService>()
                .Complete(true);
            await acceptedNavigation;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                ShellDestination.Library,
                navigation.Current);
            Assert.IsType<LibraryView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Workbench_stages_and_reorders_complete_embedded_artwork_set()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            services.GetRequiredService<INavigationService>()
                .Navigate(ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            WorkbenchView view = Assert.IsType<WorkbenchView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<WorkbenchViewModel>();
            var track = new WorkbenchTrackViewModel(
                new MediaDocument(
                    "artwork.flac",
                    [],
                    [
                        new ArtworkModel
                        {
                            Category = "FrontCover",
                            Description = "Front scan",
                            ImageType = "image/jpeg",
                            Width = 1200,
                            Height = 1200,
                            Size = 3,
                            Data = [1, 2, 3],
                        },
                        new ArtworkModel
                        {
                            Category = "BackCover",
                            Description = "Rear scan",
                            ImageType = "image/png",
                            Width = 900,
                            Height = 880,
                            Size = 3,
                            Data = [4, 5, 6],
                        },
                    ],
                    null,
                    new(
                        "artwork.flac",
                        10,
                        DateTime.UtcNow,
                        "hash"),
                    true));
            model.Files.Add(track);
            model.SelectedFile = track;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, model.StagedArtworkItems.Count);
            Assert.Equal(
                ID3v2Util.APICType.FrontCover,
                model.StagedArtworkItems[0].Type);
            Assert.Equal(
                "Rear scan",
                model.StagedArtworkItems[1].Description);
            Assert.NotNull(view.FindControl<ListBox>(
                "StagedArtworkList"));
            Assert.NotNull(view.FindControl<ComboBox>(
                "StagedArtworkTypePicker"));
            Assert.Equal(
                "Add image",
                view.FindControl<Button>(
                    "AddStagedArtworkButton")!.Content);
            Assert.Equal(
                "Preview artwork set",
                view.FindControl<Button>(
                    "PreviewStagedArtworkButton")!.Content);
            model.SelectedStagedArtwork =
                model.StagedArtworkItems[0];
            Assert.True(
                model.MoveStagedArtworkDownCommand
                    .CanExecute(null),
                $"Selected index: {model.StagedArtworkItems.IndexOf(model.SelectedStagedArtwork!)}, busy: {model.IsBusy}");

            model.MoveStagedArtworkDownCommand.Execute(null);

            Assert.Equal(
                ID3v2Util.APICType.BackCover,
                model.StagedArtworkItems[0].Type);
            Assert.Equal(
                ID3v2Util.APICType.FrontCover,
                model.StagedArtworkItems[1].Type);
            Assert.True(
                model.HasUnsavedChanges,
                "Reordering the staged set should mark the Workbench draft dirty.");
            Assert.True(
                model.MoveStagedArtworkUpCommand
                    .CanExecute(null),
                $"Selected index after move: {model.StagedArtworkItems.IndexOf(model.SelectedStagedArtwork!)}, busy: {model.IsBusy}");

            var second = new WorkbenchTrackViewModel(
                new MediaDocument(
                    "second.flac",
                    [],
                    [],
                    null,
                    new(
                        "second.flac",
                        10,
                        DateTime.UtcNow,
                        "second-hash"),
                    true));
            model.Files.Add(second);
            model.SelectedFile = second;
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(model.StagedArtworkItems);
            Assert.True(model.HasUnsavedChanges);

            model.SelectedFile = track;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, model.StagedArtworkItems.Count);
            Assert.Equal(
                ID3v2Util.APICType.BackCover,
                model.StagedArtworkItems[0].Type);
            Assert.Equal(
                ID3v2Util.APICType.FrontCover,
                model.StagedArtworkItems[1].Type);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Workbench_copy_command_emits_tag_aware_custom_values()
    {
        var clipboard = new RecordingClipboardService();
        using ServiceProvider services = BuildIsolatedServices(
            configureServices: collection =>
                collection.AddSingleton<IPlatformService>(
                    clipboard));
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            services.GetRequiredService<INavigationService>()
                .Navigate(ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            WorkbenchViewModel model =
                services.GetRequiredService<WorkbenchViewModel>();
            MetadataFieldKey field =
                MetadataFieldKey.Custom("DJ_SET");
            var track = new WorkbenchTrackViewModel(
                new MediaDocument(
                    "set.flac",
                    [new(
                        "VorbisComment",
                        [new(field, ["Warmup", "Peak"])],
                        true,
                        true,
                        true,
                        true)],
                    [],
                    null,
                    new(
                        "set.flac",
                        10,
                        DateTime.UtcNow,
                        "hash"),
                    true));
            model.Files.Add(track);
            model.SelectedFile = track;
            model.SelectedMetadataField =
                model.MetadataFields.Single(row =>
                    row.Field == field);

            await model.CopyMetadataFieldCommand
                .ExecuteAsync(null);

            Assert.True(MetadataClipboardCodec.TryDecode(
                clipboard.Text,
                out MetadataClipboardPayload? payload));
            Assert.Equal("DJ_SET", payload!.Field.CustomName);
            Assert.Equal(["Warmup", "Peak"], payload.Values);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Native_shell_constructs_and_routes_every_destination()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        ILocalizationService localization =
            services.GetRequiredService<
                ILocalizationService>();
        MainWindow window = services.GetRequiredService<MainWindow>();
        ContentControl host = window.FindControl<ContentControl>("ContentHost")!;
        INavigationService navigation = services.GetRequiredService<INavigationService>();
        var destinations = new (ShellDestination Destination, Type View)[]
        {
            (ShellDestination.Home, typeof(HomeView)),
            (ShellDestination.Library, typeof(LibraryView)),
            (ShellDestination.Health, typeof(HealthView)),
            (ShellDestination.Ingest, typeof(IngestView)),
            (ShellDestination.Organize, typeof(OrganizeView)),
            (ShellDestination.Devices, typeof(DevicesView)),
            (ShellDestination.Operations, typeof(OperationsView)),
            (ShellDestination.Settings, typeof(SettingsView)),
            (ShellDestination.About, typeof(AboutView)),
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

        Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
        Assert.True(window.CanResize);
        Assert.Equal(900, window.MinWidth);
        Assert.Equal(600, window.MinHeight);

        foreach ((ShellDestination destination, Type view) in destinations)
        {
            navigation.Navigate(destination);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Assert.IsType(view, host.Content);
            Button navigationButton = window.FindControl<Button>($"{destination}Nav")!;
            Assert.Equal("Selected", global::Avalonia.Automation.AutomationProperties.GetItemStatus(
                navigationButton));
            Assert.Equal(destination.ToString(),
                global::Avalonia.Automation.AutomationProperties.GetName(navigationButton));
            Assert.NotNull(ToolTip.GetTip(navigationButton));
            if (host.Content is HomeView home)
            {
                Grid indexingBanner = home.FindControl<Grid>("IndexingBannerLayout")!;
                Assert.Equal(2, indexingBanner.ColumnDefinitions.Count);
                Assert.Empty(indexingBanner.Children.OfType<Border>());
            }
            else if (host.Content is LibraryView library)
            {
                AppDataGrid grid = library.FindControl<AppDataGrid>("LibraryGrid")!;
                Assert.InRange(grid.Columns.Count, 8, 13);
                Assert.Equal("Artwork", grid.KeyFor(grid.Columns[0]));
                Popup columnPopover = library.FindControl<Popup>("ColumnPopover")!;
                Button columnsButton = library.FindControl<Button>("ColumnsButton")!;
                Button closeColumnsButton = library.FindControl<Button>("CloseColumnsButton")!;
                Assert.Null(
                    library.FindControl<TextBox>("FilterBox"));
                Assert.Equal(
                    "Search the library",
                    global::Avalonia.Automation.AutomationProperties.GetName(
                        window.FindControl<TextBox>("SearchBox")!));
                Assert.True(columnPopover.IsLightDismissEnabled);
                columnsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(columnPopover.IsOpen);
                library.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Escape,
                });
                Assert.False(columnPopover.IsOpen);
                columnsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                closeColumnsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(columnPopover.IsOpen);
            }
            else if (host.Content is HealthView health)
            {
                TabControl resultTabs =
                    health.FindControl<TabControl>(
                        "HealthResultsTabs")!;
                resultTabs.IsVisible = true;
                Assert.Equal(0, resultTabs.SelectedIndex);
                Assert.NotNull(
                    health.FindControl<ListBox>(
                        "HealthResultNavigation"));
            }
            else if (host.Content is IngestView ingest)
            {
                ComboBox recentFolders = ingest.FindControl<ComboBox>("RecentFoldersCombo")!;
                Assert.Equal("Recent folders", recentFolders.PlaceholderText);
                Assert.Equal("Recent ingest folders",
                    global::Avalonia.Automation.AutomationProperties.GetName(recentFolders));
                Assert.Single(ingest.FindControl<StackPanel>("SetupPanel")!.Children.OfType<Grid>());
                Assert.DoesNotContain(ingest.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text?.Contains("Preset", StringComparison.OrdinalIgnoreCase) == true);
                Assert.DoesNotContain(ingest.GetVisualDescendants().OfType<Button>(),
                    button => button.Content is string content &&
                        content.Contains("preset", StringComparison.OrdinalIgnoreCase));
            }
            else if (host.Content is OrganizeView organize)
            {
                Grid summary = organize.FindControl<Grid>("SummaryLayout")!;
                TextBlock plannedCount = organize.FindControl<TextBlock>("PlannedCount")!;
                Assert.Same(summary, plannedCount.Parent);
                Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center,
                    plannedCount.VerticalAlignment);
                Assert.Equal(FontWeight.SemiBold, plannedCount.FontWeight);
                Assert.Contains("summary-label", plannedCount.Classes);
            }
            else if (host.Content is DevicesView devices)
            {
                AppDataGrid grid = devices.FindControl<AppDataGrid>("ActionsGrid")!;
                Button restore = devices.FindControl<Button>("RestoreButton")!;
                ComboBox deviceSelector = devices.FindControl<ComboBox>("DeviceSelector")!;
                Button refreshDevices = devices.FindControl<Button>("RefreshDevicesButton")!;
                StackPanel configuration = devices.FindControl<StackPanel>("ConfigurationPanel")!;
                DevicesViewModel viewModel = Assert.IsType<DevicesViewModel>(devices.DataContext);
                Assert.Equal(5, grid.Columns.Count);
                Assert.Equal("Status", grid.KeyFor(grid.Columns[0]));
                Assert.Equal("Kind", grid.KeyFor(grid.Columns[1]));
                Assert.Equal(
                    localization.Get(
                        "Devices.Action.Restore"),
                    restore.Content);
                Assert.Equal("Android device",
                    global::Avalonia.Automation.AutomationProperties.GetName(deviceSelector));
                Assert.Equal("Refresh Android devices",
                    global::Avalonia.Automation.AutomationProperties.GetName(refreshDevices));
                Assert.NotNull(deviceSelector.ItemTemplate);
                viewModel.IsBusy = false;
                viewModel.IsLoadingDevices = false;
                Dispatcher.UIThread.RunJobs();
                Assert.True(configuration.IsEnabled);
                viewModel.IsBusy = true;
                Dispatcher.UIThread.RunJobs();
                Assert.False(configuration.IsEnabled);
                viewModel.IsBusy = false;
                Dispatcher.UIThread.RunJobs();
                Assert.True(configuration.IsEnabled);
            }
        }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task About_destination_renders_product_identity_licenses_and_copies_complete_agreements()
    {
        var clipboard = new RecordingClipboardService();
        using ServiceProvider services = BuildIsolatedServices(
            configureServices: collection =>
                collection.AddSingleton<IPlatformService>(
                    clipboard));
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Width = 1440;
            window.Height = 900;
            window.Show();
            services.GetRequiredService<INavigationService>()
                .Navigate(ShellDestination.About);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            AboutView about = Assert.IsType<AboutView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            Assert.True(
                about.FindControl<BrandMark>(
                    "AboutBrandMark")!.IsVisible);
            Assert.Equal(
                "Music Library Manager",
                about.FindControl<TextBlock>(
                    "AboutProductName")!.Text);
            Assert.Equal(
                "Copyright (c) 2010-2026 Colin Hill",
                about.FindControl<TextBlock>(
                    "CopyrightText")!.Text);
            Assert.Equal(
                "Avalonia UI",
                about.FindControl<TextBlock>(
                    "AvaloniaPackageName")!.Text);
            Assert.Equal(
                "SkiaSharp",
                about.FindControl<TextBlock>(
                    "SkiaSharpPackageName")!.Text);
            Assert.Equal("12.1.0", about.AvaloniaVersion);
            Assert.Equal("4.150.1", about.SkiaSharpVersion);

            about.FindControl<Expander>(
                    "AvaloniaLicenseExpander")!
                .IsExpanded = true;
            about.FindControl<Expander>(
                    "SkiaSharpLicenseExpander")!
                .IsExpanded = true;
            about.FindControl<Expander>(
                    "SkiaSharpNoticesExpander")!
                .IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            SelectableTextBlock avaloniaLicense =
                about.FindControl<SelectableTextBlock>(
                    "AvaloniaLicenseBody")!;
            SelectableTextBlock skiaSharpLicense =
                about.FindControl<SelectableTextBlock>(
                    "SkiaSharpLicenseBody")!;
            SelectableTextBlock skiaSharpNotices =
                about.FindControl<SelectableTextBlock>(
                    "SkiaSharpNoticesBody")!;
            Assert.True(avaloniaLicense.IsVisible);
            Assert.True(skiaSharpLicense.IsVisible);
            Assert.True(skiaSharpNotices.IsVisible);
            Assert.Equal(
                about.AvaloniaLicenseText,
                avaloniaLicense.Text);
            Assert.Equal(
                about.SkiaSharpLicenseText,
                skiaSharpLicense.Text);
            Assert.Equal(
                about.SkiaSharpThirdPartyNoticesText,
                skiaSharpNotices.Text);
            Assert.Contains(
                "Permission is hereby granted, free of charge",
                avaloniaLicense.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "THE SOFTWARE IS PROVIDED \"AS IS\"",
                avaloniaLicense.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "Copyright (c) 2015-2016 Xamarin, Inc.",
                skiaSharpLicense.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "Permission is hereby granted, free of charge",
                skiaSharpLicense.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "THE SOFTWARE IS PROVIDED \"AS IS\"",
                skiaSharpLicense.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "THIRD-PARTY SOFTWARE NOTICES AND INFORMATION",
                skiaSharpNotices.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "Do not translate or localize",
                skiaSharpNotices.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "SkiaSharp and HarfBuzzSharp incorporate third party material",
                skiaSharpNotices.Text,
                StringComparison.Ordinal);

            about.FindControl<Button>(
                    "CopyAvaloniaLicenseButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            await WaitForUiAsync(
                () => clipboard.Text is not null);
            Assert.Equal(
                about.AvaloniaLicenseText,
                clipboard.Text);

            about.FindControl<Button>(
                    "CopySkiaSharpLicenseButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            await WaitForUiAsync(
                () => string.Equals(
                    clipboard.Text,
                    about.SkiaSharpLicenseText,
                    StringComparison.Ordinal));
            Assert.Equal(
                about.SkiaSharpLicenseText,
                clipboard.Text);

            about.FindControl<Button>(
                    "CopySkiaSharpNoticesButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            await WaitForUiAsync(
                () => string.Equals(
                    clipboard.Text,
                    about.SkiaSharpThirdPartyNoticesText,
                    StringComparison.Ordinal));
            Assert.Equal(
                about.SkiaSharpThirdPartyNoticesText,
                clipboard.Text);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void About_destination_reflows_package_cards_with_content_width()
    {
        using ServiceProvider services =
            BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Width = 1440;
            window.Height = 900;
            window.Show();
            services.GetRequiredService<INavigationService>()
                .Navigate(ShellDestination.About);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            AboutView about = Assert.IsType<AboutView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            Grid packages =
                about.FindControl<Grid>("PackageGrid")!;
            Border avalonia =
                about.FindControl<Border>(
                    "AvaloniaPackageCard")!;
            Border skiaSharp =
                about.FindControl<Border>(
                    "SkiaSharpPackageCard")!;
            Assert.Equal(3, packages.ColumnDefinitions.Count);
            Assert.Equal(0, Grid.GetRow(avalonia));
            Assert.Equal(0, Grid.GetRow(skiaSharp));
            Assert.Equal(2, Grid.GetColumn(skiaSharp));

            window.Width = 900;
            window.Height = 600;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Assert.Single(packages.ColumnDefinitions);
            Assert.Equal(3, packages.RowDefinitions.Count);
            Assert.Equal(0, Grid.GetColumn(skiaSharp));
            Assert.Equal(2, Grid.GetRow(skiaSharp));
            Assert.True(
                about.FindControl<ScrollViewer>(
                    "AboutScroll")!.Bounds.Height > 0);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void About_destination_localizes_live_without_replacing_legal_content()
    {
        CultureInfo previousUICulture =
            CultureInfo.CurrentUICulture;
        var settings = new FakeSettings();
        settings.SetPreference(
            LocalizationPreferences.DisplayLanguage,
            "en-US");
        var localization =
            new ResourceLocalizationService(settings);
        using ServiceProvider services =
            BuildIsolatedServices(
                configureServices: collection =>
                {
                    collection.AddSingleton<IAppSettings>(
                        settings);
                    collection.AddSingleton<
                        ILocalizationService>(
                        localization);
                });
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            services.GetRequiredService<INavigationService>()
                .Navigate(ShellDestination.About);
            Dispatcher.UIThread.RunJobs();

            AboutView about = Assert.IsType<AboutView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            PageHeader header =
                about.FindControl<PageHeader>(
                    "AboutHeader")!;
            Expander avaloniaExpander =
                about.FindControl<Expander>(
                    "AvaloniaLicenseExpander")!;
            Expander skiaSharpExpander =
                about.FindControl<Expander>(
                    "SkiaSharpLicenseExpander")!;
            Expander skiaSharpNoticesExpander =
                about.FindControl<Expander>(
                    "SkiaSharpNoticesExpander")!;
            avaloniaExpander.IsExpanded = true;
            skiaSharpExpander.IsExpanded = true;
            skiaSharpNoticesExpander.IsExpanded = true;
            string englishTitle = header.Title;
            string avaloniaLicense =
                about.AvaloniaLicenseText;
            string skiaSharpLicense =
                about.SkiaSharpLicenseText;
            string skiaSharpNotices =
                about.SkiaSharpThirdPartyNoticesText;

            localization.SetCulture("de-DE");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                localization.Get("About.Title"),
                header.Title);
            Assert.NotEqual(
                englishTitle,
                header.Title);
            Assert.Equal(
                "Copyright (c) 2010-2026 Colin Hill",
                about.ProductCopyright);
            Assert.Equal(
                avaloniaLicense,
                about.AvaloniaLicenseText);
            Assert.Equal(
                skiaSharpLicense,
                about.SkiaSharpLicenseText);
            Assert.Equal(
                skiaSharpNotices,
                about.SkiaSharpThirdPartyNoticesText);
            Assert.True(avaloniaExpander.IsExpanded);
            Assert.True(skiaSharpExpander.IsExpanded);
            Assert.True(skiaSharpNoticesExpander.IsExpanded);
            Assert.Equal(
                ShellDestination.About,
                services.GetRequiredService<
                    INavigationService>().Current);

            string germanTitle = header.Title;
            localization.SetCulture("ja-JP");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                localization.Get("About.Title"),
                header.Title);
            Assert.NotEqual(
                germanTitle,
                header.Title);
            Assert.Same(
                about,
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            Assert.Equal(
                avaloniaLicense,
                about.FindControl<SelectableTextBlock>(
                    "AvaloniaLicenseBody")!.Text);
            Assert.Equal(
                skiaSharpLicense,
                about.FindControl<SelectableTextBlock>(
                    "SkiaSharpLicenseBody")!.Text);
            Assert.Equal(
                skiaSharpNotices,
                about.FindControl<SelectableTextBlock>(
                    "SkiaSharpNoticesBody")!.Text);
            Assert.True(avaloniaExpander.IsExpanded);
            Assert.True(skiaSharpExpander.IsExpanded);
            Assert.True(skiaSharpNoticesExpander.IsExpanded);
        }
        finally
        {
            window.Hide();
            CultureInfo.CurrentUICulture =
                previousUICulture;
        }
    }

    [AvaloniaFact]
    public async Task Unknown_shell_destination_is_contained_without_a_migration_placeholder()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        var navigation = Assert.IsType<NavigationService>(
            services.GetRequiredService<INavigationService>());
        ContentControl host =
            window.FindControl<ContentControl>("ContentHost")!;
        Control original = Assert.IsType<HomeView>(host.Content);

        await navigation.NavigateAsync((ShellDestination)int.MaxValue);

        Assert.IsType<ArgumentOutOfRangeException>(
            navigation.LastError);
        Assert.Equal(ShellDestination.Home, navigation.Current);
        Assert.Same(original, host.Content);
    }

    [AvaloniaFact]
    public void Health_long_result_lists_realize_only_the_visible_containers()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var health = new HealthView();
        var viewModel = Assert.IsType<AnalyzerViewModel>(health.DataContext);
        DuplicateGroup[] groups = Enumerable.Range(0, 2_000)
            .Select(index => new DuplicateGroup($"Duplicate {index:N0}",
            [
                new TrackRecord
                {
                    Path = $@"C:\Music\Track {index:N0}.flac",
                    Title = $"Track {index:N0}",
                },
            ]))
            .ToArray();
        AnalysisRunViewModel run = AnalysisRunViewModel.ForDuplicates(
            "Duplicates", groups, "2,000 duplicate groups");
        viewModel.Runs.Add(run);
        viewModel.SelectedRun = run;
        var window = new Window { Width = 900, Height = 600, Content = health };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            ListBox list = health.FindControl<ListBox>("DuplicateResultsList")!;
            var panel = Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
            Assert.InRange(panel.Children.Count, 1, 100);
            Assert.True(panel.Children.Count < groups.Length);
            Assert.Empty(list.GetVisualAncestors().OfType<ScrollViewer>());
            Assert.Same(groups[0], list.SelectedItem);
            ListBox detail =
                health.FindControl<ListBox>("DuplicateTrackList")!;
            Assert.Same(groups[0].Tracks, detail.ItemsSource);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Health_interactive_artwork_results_use_hierarchy_and_selected_detail()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var health = new HealthView();
        var viewModel = Assert.IsType<AnalyzerViewModel>(health.DataContext);
        ArtworkRepairItemViewModel[] repairs = Enumerable.Range(0, 2_000)
            .Select(index => new ArtworkRepairItemViewModel(
                ArtworkRepairKind.NormalizeFile,
                $"Track {index:N0}.flac",
                "Normalize artwork",
                [$@"C:\Music\Track {index:N0}.flac"],
                [], false, 128_000, 800, "Test item"))
            .ToArray();
        AnalysisRunViewModel run = AnalysisRunViewModel.ForArtwork(
            new AnalysisReport("Artwork health", []), [], repairs,
            "2,000 artwork repairs");
        viewModel.Runs.Add(run);
        viewModel.SelectedRun = run;
        viewModel.ActiveResultIndex = 8;
        var window = new Window { Width = 1100, Height = 700, Content = health };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Grid details =
                health.FindControl<Grid>("ArtworkRepairDetailsLayout")!;
            Assert.Same(repairs[0], details.DataContext);
            Assert.Null(
                health.FindControl<ListBox>(
                    "ArtworkRepairResultsList"));
            Assert.NotNull(health.GetVisualDescendants().OfType<TreeView>().FirstOrDefault(tree =>
                ReferenceEquals(tree.ItemsSource, viewModel.ArtworkRepairGroups)));
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Health_large_artwork_candidate_sets_use_one_virtualized_detail_scroll_owner()
    {
        using ServiceProvider services =
            BuildIsolatedServices([]);
        App.UseServicesForTests(services);
        var health = new HealthView();
        var viewModel =
            Assert.IsType<AnalyzerViewModel>(
                health.DataContext);
        ILibraryService library =
            services.GetRequiredService<
                ILibraryService>();
        ArtworkRepairCandidateViewModel[]
            candidates =
            CreateArtworkCandidates(
                library,
                2_000);
        var repair =
            new ArtworkRepairItemViewModel(
                ArtworkRepairKind.NormalizeAlbum,
                "Large candidate set",
                "Choose the canonical artwork.",
                [@"C:\Music\Album\Track 01.flac"],
                candidates,
                true,
                128_000,
                800);
        AnalysisRunViewModel run =
            AnalysisRunViewModel.ForArtwork(
                new AnalysisReport(
                    "Artwork health",
                    []),
                [],
                [repair],
                "One artwork repair");
        viewModel.Runs.Add(run);
        viewModel.SelectedRun = run;
        viewModel.ActiveResultIndex = 8;
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = health,
        };
        try
        {
            window.Show();
            RenderUi();

            ListBox candidateList =
                health.FindControl<ListBox>(
                    "ArtworkRepairCandidateList")!;
            var panel =
                Assert.IsType<VirtualizingStackPanel>(
                    candidateList.ItemsPanelRoot);
            ScrollViewer owner =
                health.FindControl<ScrollViewer>(
                    "ArtworkRepairDetailsScroll")!;
            Assert.InRange(
                panel.Children.Count,
                1,
                20);
            Assert.True(
                panel.Children.Count <
                candidates.Length);
            Assert.Same(
                candidates[0],
                candidateList.SelectedItem);
            Assert.Contains(
                owner,
                candidateList
                    .GetVisualAncestors()
                    .OfType<ScrollViewer>());
            Assert.Empty(
                candidateList
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>());
            ScrollViewer[] effectiveOwners =
            [
                .. health
                    .FindControl<Grid>(
                        "ArtworkRepairMasterDetailLayout")!
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .Where(viewer =>
                        viewer.IsEffectivelyVisible &&
                        viewer
                            .VerticalScrollBarVisibility !=
                        ScrollBarVisibility.Disabled),
            ];
            Assert.Single(effectiveOwners);
            Assert.Same(owner, effectiveOwners[0]);
            Assert.True(
                owner.Extent.Height >
                owner.Viewport.Height,
                $"The candidate surface did not overflow its single owner: extent={owner.Extent}, viewport={owner.Viewport}.");

            owner.Offset = new Vector(
                0,
                owner.Extent.Height -
                owner.Viewport.Height);
            RenderUi();

            Assert.InRange(
                panel.Children.Count,
                1,
                20);
            Assert.True(
                panel.Children.Count <
                candidates.Length);
            Assert.Contains(
                panel.Children,
                child =>
                    ReferenceEquals(
                        child.DataContext,
                        candidates[^1]));
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Health_long_artwork_detail_text_is_reachable_through_its_single_scroll_owner_at_900_by_600()
    {
        using ServiceProvider services =
            BuildIsolatedServices([]);
        App.UseServicesForTests(services);
        var health = new HealthView();
        var viewModel =
            Assert.IsType<AnalyzerViewModel>(
                health.DataContext);
        ILibraryService library =
            services.GetRequiredService<
                ILibraryService>();
        string description =
            string.Join(
                " ",
                Enumerable.Repeat(
                    "The selected canonical artwork must be reviewed against every source before normalization can continue.",
                    12));
        string blockingReason =
            string.Join(
                " ",
                Enumerable.Repeat(
                    "The source image could not be decoded and the affected files remain blocked until another candidate is selected.",
                    12));
        string diagnostic =
            string.Join(
                " ",
                Enumerable.Repeat(
                    @"C:\Music\Album\Track 01.flac failed while writing the reviewed artwork because the destination rejected the metadata update.",
                    12));
        var repair =
            new ArtworkRepairItemViewModel(
                ArtworkRepairKind.NormalizeAlbum,
                "Long artwork detail",
                description,
                [@"C:\Music\Album\Track 01.flac"],
                CreateArtworkCandidates(
                    library,
                    24),
                true,
                128_000,
                800,
                blockingReason)
            {
                ResultText =
                    "The reviewed artwork repair could not be completed.",
                ResultDiagnosticDetail =
                    diagnostic,
            };
        AnalysisRunViewModel run =
            AnalysisRunViewModel.ForArtwork(
                new AnalysisReport(
                    "Artwork health",
                    []),
                [],
                [repair],
                "One artwork repair");
        viewModel.Runs.Add(run);
        viewModel.SelectedRun = run;
        viewModel.ActiveResultIndex = 8;
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = health,
        };
        try
        {
            window.Show();
            RenderUi();

            ScrollViewer owner =
                health.FindControl<ScrollViewer>(
                    "ArtworkRepairDetailsScroll")!;
            Control descriptionText =
                health.FindControl<Control>(
                    "ArtworkRepairDescriptionText")!;
            Control blockingText =
                health.FindControl<Control>(
                    "ArtworkRepairBlockingReasonText")!;
            Control resultText =
                health.FindControl<Control>(
                    "ArtworkRepairResultText")!;
            Control diagnosticText =
                health.FindControl<Control>(
                    "ArtworkRepairResultDiagnosticText")!;
            ScrollViewer[] effectiveOwners =
            [
                .. health
                    .FindControl<Grid>(
                        "ArtworkRepairMasterDetailLayout")!
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .Where(viewer =>
                        viewer.IsEffectivelyVisible &&
                        viewer
                            .VerticalScrollBarVisibility !=
                        ScrollBarVisibility.Disabled),
            ];
            Assert.Single(effectiveOwners);
            Assert.Same(owner, effectiveOwners[0]);
            Assert.True(
                owner.Extent.Height >
                owner.Viewport.Height,
                $"Long artwork detail text did not overflow: extent={owner.Extent}, viewport={owner.Viewport}.");

            AssertReachable(descriptionText);
            AssertReachable(blockingText);
            CaptureConfiguredHealthState(
                window,
                "artwork-long-detail-single-scroll");
            AssertReachable(resultText);
            AssertReachable(diagnosticText);
            Assert.True(
                owner.Offset.Y > 0,
                $"The result diagnostic did not move into the detail viewport: offset={owner.Offset}, extent={owner.Extent}, viewport={owner.Viewport}.");

            void AssertReachable(
                Control control)
            {
                control.BringIntoView();
                RenderUi();
                Point origin =
                    Assert.NotNull(
                        control.TranslatePoint(
                            default,
                            owner));
                Rect bounds =
                    new(
                        origin,
                        control.Bounds.Size);
                Assert.True(
                    bounds.Bottom >= -1 &&
                    bounds.Top <=
                    owner.Viewport.Height + 1,
                    $"{control.Name} could not be brought into the artwork detail viewport: control={bounds}, offset={owner.Offset}, extent={owner.Extent}, viewport={owner.Viewport}.");
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Health_artwork_candidate_detail_supports_wheel_and_keyboard_at_900_by_600()
    {
        using ServiceProvider services =
            BuildIsolatedServices([]);
        App.UseServicesForTests(services);
        var health = new HealthView();
        var viewModel =
            Assert.IsType<AnalyzerViewModel>(
                health.DataContext);
        ILibraryService library =
            services.GetRequiredService<
                ILibraryService>();
        ArtworkRepairCandidateViewModel[]
            candidates =
            CreateArtworkCandidates(
                library,
                250);
        var repair =
            new ArtworkRepairItemViewModel(
                ArtworkRepairKind.NormalizeAlbum,
                "Keyboard and wheel candidates",
                "Choose the canonical artwork.",
                [@"C:\Music\Album\Track 01.flac"],
                candidates,
                true,
                128_000,
                800);
        AnalysisRunViewModel run =
            AnalysisRunViewModel.ForArtwork(
                new AnalysisReport(
                    "Artwork health",
                    []),
                [],
                [repair],
                "One artwork repair");
        viewModel.Runs.Add(run);
        viewModel.SelectedRun = run;
        viewModel.ActiveResultIndex = 8;
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = health,
        };
        try
        {
            window.Show();
            RenderUi();

            ListBox candidateList =
                health.FindControl<ListBox>(
                    "ArtworkRepairCandidateList")!;
            TabControl resultTabs =
                health.FindControl<TabControl>(
                    "HealthResultsTabs")!;
            Assert.Equal(
                8,
                resultTabs.SelectedIndex);
            Assert.True(
                candidateList.IsEffectivelyVisible,
                $"Candidate list is not visible: candidate={candidateList.Bounds}, details={health.FindControl<Grid>("ArtworkRepairDetailsLayout")!.Bounds}.");
            Grid details =
                candidateList
                    .GetVisualAncestors()
                    .OfType<Grid>()
                    .Single(grid =>
                        grid.Name ==
                        "ArtworkRepairDetailsLayout");
            Assert.Same(
                repair,
                details.DataContext);
            ScrollViewer owner =
                health.FindControl<ScrollViewer>(
                    "ArtworkRepairDetailsScroll")!;
            Assert.Contains(
                owner,
                candidateList
                    .GetVisualAncestors()
                    .OfType<ScrollViewer>());
            Assert.Empty(
                candidateList
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>());
            Assert.True(
                candidateList.Bounds.Width > 0,
                $"Candidate list has no width: {candidateList.Bounds}.");
            Assert.True(
                candidateList.Bounds.Height >= 80,
                $"Candidate list is too short: candidate={candidateList.Bounds}, details={details.Bounds}.");
            Point origin =
                Assert.NotNull(
                    candidateList.TranslatePoint(
                        default,
                        health));
            Assert.InRange(
                origin.X,
                -1,
                health.Bounds.Width + 1);
            Assert.True(
                origin.Y >= -1 &&
                origin.Y <=
                health.Bounds.Height + 1,
                $"Candidate origin={origin}, candidate={candidateList.Bounds}, details={details.Bounds}, health={health.Bounds}, tabs={resultTabs.Bounds}. Ancestors: {string.Join(" | ", candidateList.GetVisualAncestors().OfType<Control>().Select(control => $"{control.GetType().Name}:{control.Name}:{control.Bounds}"))}");
            Assert.True(
                origin.X +
                candidateList.Bounds.Width <=
                health.Bounds.Width + 1,
                $"Candidate right edge is clipped: origin={origin}, candidate={candidateList.Bounds}, health={health.Bounds}.");
            Assert.True(
                origin.Y <
                health.Bounds.Height &&
                origin.Y +
                Math.Min(
                    candidateList.Bounds.Height,
                    owner.Viewport.Height) >
                0,
                $"The candidate extent does not intersect its visible outer viewport: origin={origin}, candidate={candidateList.Bounds}, owner={owner.Bounds}, viewport={owner.Viewport}, health={health.Bounds}.");

            candidateList.Focus();
            candidateList.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Down,
                    PhysicalKey =
                        PhysicalKey.ArrowDown,
                });
            RenderUi();
            Assert.Same(
                candidates[1],
                candidateList.SelectedItem);
            Assert.Same(
                candidates[1],
                repair.SelectedCandidate);

            Point listPoint =
                Assert.NotNull(
                    owner.TranslatePoint(
                        new Point(
                            owner.Viewport.Width /
                            2,
                            owner.Viewport.Height /
                            2),
                        window));
            double beforeWheel =
                owner.Offset.Y;
            window.MouseWheel(
                listPoint,
                new Vector(0, -6),
                RawInputModifiers.None);
            RenderUi();
            Assert.True(
                owner.Offset.Y > beforeWheel,
                $"The candidate list did not scroll: {beforeWheel:0.##} -> {owner.Offset.Y:0.##}.");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Health_expanded_artwork_paths_have_one_reachable_virtualized_scroll_owner_at_900_by_600()
    {
        using ServiceProvider services =
            BuildIsolatedServices([]);
        App.UseServicesForTests(services);
        var health = new HealthView();
        var viewModel =
            Assert.IsType<AnalyzerViewModel>(
                health.DataContext);
        ILibraryService library =
            services.GetRequiredService<
                ILibraryService>();
        ArtworkRepairCandidateViewModel[]
            candidates =
            CreateArtworkCandidates(
                library,
                250);
        string[] paths = Enumerable.Range(0, 2_000)
            .Select(index =>
                $@"C:\Music\An intentionally very long artist name {index:N0}\An intentionally very long album name {index:N0}\Disc 01\Track {index:N0} with an intentionally long title.flac")
            .ToArray();
        var repair =
            new ArtworkRepairItemViewModel(
                ArtworkRepairKind.NormalizeAlbum,
                "Large affected-file set",
                "Choose the canonical artwork.",
                paths,
                candidates,
                true,
                128_000,
                800);
        AnalysisRunViewModel run =
            AnalysisRunViewModel.ForArtwork(
                new AnalysisReport(
                    "Artwork health",
                    []),
                [],
                [repair],
                "One artwork repair");
        viewModel.Runs.Add(run);
        viewModel.SelectedRun = run;
        viewModel.ActiveResultIndex = 8;
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = health,
        };
        try
        {
            window.Show();
            RenderUi();

            Expander affectedExpander =
                health.FindControl<Expander>(
                    "ArtworkAffectedFilesExpander")!;
            affectedExpander.IsExpanded = true;
            RenderUi();

            ListBox candidateList =
                health.FindControl<ListBox>(
                    "ArtworkRepairCandidateList")!;
            ListBox pathList =
                health.FindControl<ListBox>(
                    "ArtworkRepairAffectedPathList")!;
            Grid details =
                health.FindControl<Grid>(
                    "ArtworkRepairDetailsLayout")!;
            Assert.False(
                candidateList.IsEffectivelyVisible,
                $"Candidate list remained visible after expanding affected files: candidate={candidateList.Bounds}, expander={affectedExpander.Bounds}, details={details.Bounds}.");
            Assert.True(
                pathList.IsEffectivelyVisible,
                $"Affected-path list is not visible: pathList={pathList.Bounds}, expander={affectedExpander.Bounds}, details={details.Bounds}.");
            Assert.Same(
                repair.AffectedPaths,
                pathList.ItemsSource);

            var panel =
                Assert.IsType<VirtualizingStackPanel>(
                    pathList.ItemsPanelRoot);
            Assert.InRange(
                panel.Children.Count,
                1,
                80);
            Assert.True(
                panel.Children.Count <
                repair.AffectedPaths.Count,
                $"Affected-path virtualization realized every item: children={panel.Children.Count}, paths={repair.AffectedPaths.Count}, list={pathList.Bounds}.");
            Assert.Contains(
                health.FindControl<ScrollViewer>(
                    "ArtworkRepairDetailsScroll")!,
                pathList
                    .GetVisualAncestors()
                    .OfType<ScrollViewer>());
            Assert.Empty(
                pathList
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>());
            ScrollViewer owner =
                Assert.Single(
                    health
                        .FindControl<Grid>(
                            "ArtworkRepairMasterDetailLayout")!
                        .GetVisualDescendants()
                        .OfType<ScrollViewer>(),
                    viewer =>
                        viewer.IsEffectivelyVisible &&
                        viewer
                            .VerticalScrollBarVisibility !=
                        ScrollBarVisibility.Disabled);
            Assert.Contains(
                owner,
                pathList
                    .GetVisualAncestors()
                    .OfType<ScrollViewer>());
            Assert.True(
                pathList.Bounds.Height >= 80,
                $"Affected-path list is too short: pathList={pathList.Bounds}, expander={affectedExpander.Bounds}, details={details.Bounds}.");
            Point origin =
                Assert.NotNull(
                    pathList.TranslatePoint(
                        default,
                        health));
            Assert.InRange(
                origin.X,
                -1,
                health.Bounds.Width + 1);
            Assert.InRange(
                origin.Y,
                -1,
                health.Bounds.Height + 1);
            Assert.True(
                origin.X +
                pathList.Bounds.Width <=
                health.Bounds.Width + 1,
                $"Affected-path right edge is clipped: origin={origin}, pathList={pathList.Bounds}, health={health.Bounds}.");
            Assert.True(
                origin.Y <
                health.Bounds.Height &&
                origin.Y +
                Math.Min(
                    pathList.Bounds.Height,
                    owner.Viewport.Height) >
                0,
                $"The affected-path extent does not intersect its visible outer viewport: origin={origin}, pathList={pathList.Bounds}, owner={owner.Bounds}, viewport={owner.Viewport}, health={health.Bounds}.");
            Assert.True(
                owner.Extent.Width <=
                owner.Viewport.Width + 1,
                $"Affected paths introduced horizontal overflow: extent={owner.Extent}, viewport={owner.Viewport}.");
            CaptureConfiguredHealthState(
                window,
                "artwork-expanded-paths-virtualized");

            pathList.Focus();
            pathList.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.End,
                    PhysicalKey =
                        PhysicalKey.End,
                });
            RenderUi();

            Assert.Same(
                repair.AffectedPaths[^1],
                pathList.SelectedItem);
            Assert.True(
                owner.Offset.Y > 0,
                $"The last affected path was not brought into view: offset={owner.Offset}, extent={owner.Extent}, viewport={owner.Viewport}.");
        }
        finally
        {
            window.Hide();
        }
    }

    private static void CaptureConfiguredHealthState(
        Window window,
        string state)
    {
        string? captureDirectory =
            Environment.GetEnvironmentVariable(
                "MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(
                captureDirectory))
        {
            return;
        }

        window.InvalidateVisual();
        RenderUi();
        using var frame =
            window.GetLastRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(
            900,
            frame.PixelSize.Width);
        Assert.Equal(
            600,
            frame.PixelSize.Height);
        Directory.CreateDirectory(
            captureDirectory);
        frame.Save(
            Path.Combine(
                captureDirectory,
                $"configured-health-{state}-900x600.png"),
            PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public void Health_without_configuration_shows_one_contextual_setup_action()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var health = new HealthView();
        var navigation = Assert.IsType<NavigationService>(
            services.GetRequiredService<INavigationService>());
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = health,
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Assert.True(
                health.FindControl<Border>("HealthSetupCard")!
                    .IsEffectivelyVisible);
            Assert.False(
                health.FindControl<Border>("HealthActionCard")!
                    .IsEffectivelyVisible);
            Assert.False(
                health.FindControl<PersistedSplitView>("HealthResultsHost")!
                    .IsEffectivelyVisible);
            Button setup =
                health.FindControl<Button>("HealthSetupAction")!;
            Assert.False(
                string.IsNullOrWhiteSpace(
                    AutomationProperties.GetName(setup)));

            setup.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));

            Assert.Equal(
                ShellDestination.Settings,
                navigation.Current);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Health_high_cardinality_master_detail_stacks_narrow_and_keeps_keyboard_selection()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var health = new HealthView();
        var viewModel =
            Assert.IsType<AnalyzerViewModel>(
                health.DataContext);
        DuplicateGroup[] groups =
            Enumerable.Range(0, 250)
                .Select(index =>
                    new DuplicateGroup(
                        $"Duplicate {index:N0}",
                        [
                            new TrackRecord
                            {
                                Path =
                                    $@"C:\Music\Track {index:N0}.flac",
                                Title =
                                    $"Track {index:N0}",
                            },
                        ]))
                .ToArray();
        AnalysisRunViewModel run =
            AnalysisRunViewModel.ForDuplicates(
                "Duplicates",
                groups,
                "250 duplicate groups");
        viewModel.Runs.Add(run);
        viewModel.SelectedRun = run;
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = health,
        };
        try
        {
            window.Show();
            Render();

            Grid layout =
                health.FindControl<Grid>(
                    "DuplicateMasterDetailLayout")!;
            Assert.Single(
                layout.ColumnDefinitions);
            Assert.Equal(
                2,
                layout.RowDefinitions.Count);

            ListBox master =
                health.FindControl<ListBox>(
                    "DuplicateResultsList")!;
            Assert.False(
                string.IsNullOrWhiteSpace(
                    AutomationProperties.GetName(
                        master)));
            Assert.Same(
                groups[0],
                master.SelectedItem);
            ListBoxItem firstRow =
                Assert.IsType<VirtualizingStackPanel>(
                        master.ItemsPanelRoot)
                    .Children
                    .OfType<ListBoxItem>()
                    .First();
            Assert.Empty(
                firstRow.GetVisualDescendants()
                    .OfType<ComboBox>());

            master.Focus();
            master.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Down,
                });
            Render();
            Assert.Same(
                groups[1],
                master.SelectedItem);
            Assert.Same(
                groups[1],
                viewModel.SelectedDuplicate);

            window.Width = 1440;
            Render();
            Assert.Equal(
                2,
                layout.ColumnDefinitions.Count);
            Assert.Single(
                layout.RowDefinitions);
            Assert.Empty(
                master.GetVisualAncestors()
                    .OfType<ScrollViewer>());
        }
        finally
        {
            window.Hide();
        }

        static void Render()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        }
    }

    [AvaloniaFact]
    public void Health_dropdowns_are_not_nested_inside_other_click_targets()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var health = new HealthView();
        var viewModel = Assert.IsType<AnalyzerViewModel>(health.DataContext);
        var artistGroup = new ArtistGroupViewModel(new SimilarArtistGroup(
        [
            new ArtistVariant("Canonical", [@"C:\Music\one.flac"]),
            new ArtistVariant("Canoncial", [@"C:\Music\two.flac"]),
        ]));
        AnalysisRunViewModel run = AnalysisRunViewModel.ForArtists(
            "Similar artists", [artistGroup], "Similar artists");
        viewModel.Runs.Add(run);
        viewModel.SelectedRun = run;
        var window = new Window { Width = 1100, Height = 700, Content = health };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            ComboBox findingRoot = health.FindControl<ComboBox>("FindingRootDisposition")!;
            Assert.Empty(findingRoot.GetVisualAncestors().OfType<Button>());

            ComboBox variantDisposition = health.GetVisualDescendants().OfType<ComboBox>()
                .First(combo => string.Equals(
                    global::Avalonia.Automation.AutomationProperties.GetName(combo),
                    "Disposition for artist spelling variant", StringComparison.Ordinal));
            Assert.Empty(variantDisposition.GetVisualAncestors().OfType<Expander>());
            Assert.Empty(variantDisposition.GetVisualAncestors().OfType<ListBoxItem>());
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Confirmation_dialog_defaults_to_cancel_and_restores_focus_on_escape()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        DialogService dialogs = services.GetRequiredService<DialogService>();
        Task<bool>? pending = null;
        try
        {
            window.Show();
            window.Width = 900;
            window.Height = 600;
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            TextBox search = window.FindControl<TextBox>("SearchBox")!;
            search.Focus();

            pending = dialogs.ConfirmAsync(
                "Permanently remove files?",
                "Remove 12 files. No recovery is available.",
                "Remove",
                DialogTone.Danger);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            DialogHost host = window.GetVisualDescendants().OfType<DialogHost>().Single();
            Border card = host.FindControl<Border>("DialogCard")!;
            Button cancel = host.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Cancel"));
            StackPanel buttons = host.FindControl<StackPanel>("DialogButtons")!;
            Assert.True(host.IsVisible);
            Assert.True(card.Bounds.Width <= 560);
            Assert.True(card.Bounds.Height <= window.Bounds.Height - 48);
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(card));
            Assert.Equal(["Cancel", "Remove"],
                buttons.Children.OfType<Button>().Select(button => button.Content?.ToString()));
            Assert.Same(cancel, window.FocusManager?.GetFocusedElement());

            host.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Escape,
            });
            Dispatcher.UIThread.RunJobs();

            Assert.False(await pending);
            Assert.False(host.IsVisible);
            Assert.Same(search, window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            if (pending is { IsCompleted: false })
                dialogs.Complete(false);
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Native_close_cancels_a_dismissible_dialog_without_closing_its_owner()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        DialogService dialogs = services.GetRequiredService<DialogService>();
        Task<bool>? pending = null;
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();

            pending = dialogs.ConfirmAsync(
                "Apply reviewed changes?",
                "Apply one reviewed change.",
                "Apply");
            Dispatcher.UIThread.RunJobs();
            DialogHost host = window.GetVisualDescendants().OfType<DialogHost>().Single();
            Assert.True(host.IsVisible);

            window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsVisible);
            Assert.False(await pending);
            Assert.Null(dialogs.Current);
            Assert.False(host.IsVisible);
        }
        finally
        {
            if (pending is { IsCompleted: false })
                dialogs.Complete(false);
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Native_close_preserves_a_dirty_fields_dialog()
    {
        using ServiceProvider services = BuildIsolatedServices(
            configureServices: collection =>
                collection.AddSingleton<IMetadataDocumentService>(
                    new FieldsDialogDocumentService()));
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        DialogService dialogs = services.GetRequiredService<DialogService>();
        Task<bool>? pending = null;
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();

            pending = dialogs.ShowAsync([@"C:\Music\Track.flac"]);
            Dispatcher.UIThread.RunJobs();
            FieldsRequest request = Assert.IsType<FieldsRequest>(dialogs.Current);
            FieldRow title = Assert.Single(request.ViewModel.Rows,
                row => row.Field == TagFields.Title);
            title.Value = "Unsaved title";
            Assert.False(request.DismissalPolicy.CanDismissFromCloseButton);

            DialogHost host = window.GetVisualDescendants().OfType<DialogHost>().Single();
            Assert.True(host.IsVisible);
            window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsVisible);
            Assert.True(host.IsVisible);
            Assert.Same(request, dialogs.Current);
            Assert.False(pending.IsCompleted);
            Assert.Equal("Unsaved title", title.Value);
        }
        finally
        {
            if (pending is { IsCompleted: false })
            {
                dialogs.Complete(false);
                Assert.False(await pending);
            }
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Persistent_activity_strip_routes_and_cancels_at_minimum_size()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        IActivityService activities = services.GetRequiredService<IActivityService>();
        bool cancelled = false;
        Guid activity = activities.Start(
            "Preview operation", "Computing a recoverable plan", ShellDestination.Operations,
            () => cancelled = true);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Width = 900;
            window.Height = 600;
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Border banner = window.FindControl<Border>("ActivityBanner")!;
            Assert.True(banner.IsVisible);
            Assert.InRange(banner.Bounds.Height, 36, 56);
            Button open = banner.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Open"));
            Button cancel = banner.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Cancel"));

            Assert.True(open.Command!.CanExecute(open.CommandParameter));
            open.Command.Execute(open.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<OperationsView>(window.FindControl<ContentControl>("ContentHost")!.Content);

            Assert.True(cancel.Command!.CanExecute(cancel.CommandParameter));
            cancel.Command.Execute(cancel.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.True(cancelled);
            Assert.False(cancel.IsEffectivelyEnabled);

            activities.Finish(activity, "Preview cancelled", AppActivityState.Cancelled);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("warning", banner.Classes);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Completed_activity_strip_removes_stale_progress_and_cancel_action()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        IActivityService activities = services.GetRequiredService<IActivityService>();
        Guid activity = activities.Start(
            "Index library", "Indexing fixture tracks", ShellDestination.Library,
            () => { });
        activities.Report(activity, "Indexed 2 fixture tracks", 0.75);

        MainWindow window = services.GetRequiredService<MainWindow>();
        ThemeVariant? previousTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            window.Width = 2048;
            window.Height = 900;
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Grid progress = window.FindControl<Grid>("ActivityProgressHost")!;
            Button cancel = window.FindControl<Button>("ActivityCancelButton")!;
            Button dismiss = window.FindControl<Button>("ActivityDismissButton")!;
            TextBlock state = window.FindControl<TextBlock>("ActivityStateLabel")!;
            Assert.True(progress.IsVisible);
            Assert.True(progress.ClipToBounds);
            Assert.True(cancel.IsVisible);
            Assert.False(dismiss.IsVisible);
            Assert.Equal("In progress", state.Text);
            Assert.True(progress.Bounds.Right <= state.Bounds.Left,
                $"Progress ended at {progress.Bounds.Right}, but the state label began at {state.Bounds.Left}.");

            activities.Finish(activity, "Index complete: 2 unchanged.");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Assert.False(progress.IsVisible);
            Assert.False(cancel.IsVisible);
            Assert.True(dismiss.IsVisible);
            Assert.Equal("Completed", state.Text);
            Assert.Contains("success", state.Classes);

            string? captureDirectory = Environment.GetEnvironmentVariable(
                "MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                using var frame = window.GetLastRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(captureDirectory, "activity-completed-wide.png"),
                    PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Hide();
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public void Every_destination_completes_a_headless_1440_by_900_render_pass()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        INavigationService navigation = services.GetRequiredService<INavigationService>();
        string? captureDirectory = Environment.GetEnvironmentVariable("MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        bool isCapturing = !string.IsNullOrWhiteSpace(captureDirectory);
        ThemeVariant? previousTheme = Application.Current!.RequestedThemeVariant;
        if (isCapturing)
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Width = 1440;
        window.Height = 900;
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        if (isCapturing)
            Directory.CreateDirectory(captureDirectory!);

        Button homeNavigation = window.FindControl<Button>("HomeNav")!;
        Grid navigationContent = Assert.IsType<Grid>(homeNavigation.Content);
        Viewbox navigationIcon = navigationContent.Children.OfType<Viewbox>()
            .Single(icon => icon.Classes.Contains("nav-icon"));
        TextBlock navigationLabel = navigationContent.Children.OfType<TextBlock>()
            .Single(text => text.Classes.Contains("nav-label"));
        Assert.Equal(44, homeNavigation.Bounds.Height);
        Assert.True(homeNavigation.Bounds.Width >= 190,
            $"Navigation selection did not fill the rail. Width={homeNavigation.Bounds.Width:0}");
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, homeNavigation.VerticalContentAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, navigationContent.VerticalAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, navigationIcon.VerticalAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, navigationLabel.VerticalAlignment);
        Assert.Equal(20, navigationIcon.Width);
        Assert.Equal(20, navigationIcon.Height);
        Assert.Equal(15, navigationLabel.FontSize);
        Border navigationMarker = navigationContent.Children.OfType<Border>()
            .Single(marker => marker.Classes.Contains("nav-marker"));
        Assert.Equal(1, navigationMarker.Opacity);
        Assert.Equal(3, navigationMarker.Width);
        Assert.Equal(22, navigationMarker.Height);
        Assert.Equal(
            global::Avalonia.Layout.VerticalAlignment.Center,
            window.FindControl<TextBox>("SearchBox")!.VerticalContentAlignment);
        Assert.Equal(WindowDecorations.Full, window.WindowDecorations);

        try
        {
            foreach (ShellDestination destination in Enum.GetValues<ShellDestination>())
            {
                navigation.Navigate(destination);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Control activeView = Assert.IsAssignableFrom<Control>(window.FindControl<ContentControl>("ContentHost")!.Content);
                Assert.True(activeView.Bounds.Width >= 1150,
                    $"{destination} did not fill the content host. Width={activeView.Bounds.Width:0}");
                if (destination == ShellDestination.Library)
                {
                    LibraryView library = Assert.IsType<LibraryView>(window.FindControl<ContentControl>("ContentHost")!.Content);
                    AppDataGrid grid = library.FindControl<AppDataGrid>("LibraryGrid")!;
                    if (!isCapturing)
                    {
                        grid.ItemsSource = new[]
                        {
                            new LibraryRow(new TrackRecord
                            {
                                Path = @"C:\Music\Wild Fire.flac",
                                Title = "Wild Fire",
                                Artist = "The Avalonians",
                            }),
                        };
                    }
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    PersistedSplitView split = library.FindControl<PersistedSplitView>("WorkspaceSplit")!;
                    ContentPresenter leftPresenter = split.FindControl<ContentPresenter>("LeftPresenter")!;
                    string bounds = string.Join(" -> ", new Control[] { grid }.Concat(grid.GetVisualAncestors().OfType<Control>())
                        .Select(control => $"{control.GetType().Name}:{control.Bounds.Width:0}x{control.Bounds.Height:0}"));
                    Assert.True(grid.Bounds.Width >= 500, $"Library grid width collapsed. Left={split.Left?.GetType().Name ?? "null"}; Presented={leftPresenter.Content?.GetType().Name ?? "null"}; Split={split.Bounds.Width:0}x{split.Bounds.Height:0}; Presenter={leftPresenter.Bounds.Width:0}x{leftPresenter.Bounds.Height:0}; {bounds}");
                    Assert.True(grid.Bounds.Height >= 300, $"Library grid height collapsed. {bounds}");
                    ContentPresenter inspectorPresenter =
                        split.FindControl<ContentPresenter>(
                            "RightPresenter")!;
                    Assert.False(
                        inspectorPresenter.IsEffectivelyVisible,
                        "An empty, unpinned Library inspector docked without a selection.");
                    Assert.InRange(grid.Columns.Count, 8, 13);
                    Assert.Contains(grid.GetVisualDescendants(), visual => visual.GetType().Name == "DataGridColumnHeader");
                    if (!isCapturing)
                        Assert.NotNull(grid.ItemsSource);
                    SelectionInspectorView inspector =
                        library.FindControl<
                            SelectionInspectorView>(
                            "InspectorView")!;
                    Assert.True(inspector.FindControl<Border>("EmptyState")!.IsVisible);
                    Assert.False(inspector.FindControl<ScrollViewer>("InspectorContent")!.IsVisible);
                    Button inspectorReview =
                        inspector.FindControl<Button>(
                            "InspectorReviewChangesButton")!;
                    Assert.False(inspectorReview.IsEnabled);
                    Assert.NotNull(inspector.FindControl<Button>(
                        "InspectorDiscardEditsButton"));
                    Assert.DoesNotContain(
                        inspector.GetVisualDescendants()
                            .OfType<Button>(),
                        button =>
                            string.Equals(
                                button.Content?.ToString(),
                                "Save tags",
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(
                                button.Content?.ToString(),
                                "All fields...",
                                StringComparison.OrdinalIgnoreCase));
                }
                else if (destination == ShellDestination.Settings)
                {
                    SettingsView settings = Assert.IsType<SettingsView>(window.FindControl<ContentControl>("ContentHost")!.Content);
                    Assert.True(settings.FindControl<Border>(
                        "SettingsCategoryRail")!.IsEffectivelyVisible);
                    Assert.False(settings.FindControl<ComboBox>(
                        "SettingsCategoryPicker")!.IsVisible);
                    Assert.Equal(10, settings.FindControl<ListBox>(
                        "SettingsCategoryList")!.Items.Count);
                    TabControl settingsTabs =
                        settings.FindControl<TabControl>("SettingsTabs")!;
                    settingsTabs.SelectedIndex = 5;
                    Dispatcher.UIThread.RunJobs();
                    Assert.All(
                        settings.GetVisualDescendants()
                            .OfType<Grid>()
                            .Where(grid =>
                                grid.Classes.Contains("responsive-form") &&
                                grid.IsEffectivelyVisible &&
                                grid.GetVisualAncestors().Contains(settingsTabs)),
                        grid => Assert.InRange(
                            grid.ColumnDefinitions.Count, 1, 4));
                    settingsTabs.SelectedIndex = 0;
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    ScrollViewer scroll = settings.FindControl<ScrollViewer>("ConfigurationSettingsScroll")!;
                    StackPanel content = settings.FindControl<StackPanel>("ConfigurationSettingsContent")!;
                    Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Stretch, scroll.HorizontalContentAlignment);
                    Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
                    Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Stretch, content.HorizontalAlignment);
                    Assert.InRange(content.Bounds.Width, 700, 1040);
                    Assert.True(content.Bounds.Width <= scroll.Bounds.Width,
                        $"Settings content exceeded its viewport. Content={content.Bounds.Width:0}; Scroll={scroll.Bounds.Width:0}");
                }
                else if (destination == ShellDestination.Health)
                {
                    HealthView health = Assert.IsType<HealthView>(window.FindControl<ContentControl>("ContentHost")!.Content);
                    Assert.NotNull(
                        health.FindControl<TabControl>(
                            "HealthResultsTabs"));
                    Assert.NotNull(
                        health.FindControl<ListBox>(
                            "HealthResultNavigation"));
                }
                using var frame = window.GetLastRenderedFrame();
                Assert.NotNull(frame);
                Assert.Equal(1440, frame.PixelSize.Width);
                Assert.Equal(900, frame.PixelSize.Height);
                if (isCapturing)
                    frame.Save(Path.Combine(captureDirectory!, $"{destination}.png"),
                        PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Hide();
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public async Task Every_destination_renders_at_the_900_by_600_minimum_in_light_and_dark()
    {
        string? captureDirectory = Environment.GetEnvironmentVariable("MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        ThemeVariant? previousTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach ((string name, ThemeVariant theme) in new[]
                     {
                         ("light", ThemeVariant.Light),
                         ("dark", ThemeVariant.Dark),
                     })
            {
                Application.Current.RequestedThemeVariant = theme;
                using ServiceProvider services = BuildIsolatedServices();
                App.UseServicesForTests(services);
                ILocalizationService localization =
                    services.GetRequiredService<
                        ILocalizationService>();
                services.GetRequiredService<IAppSettings>()
                    .SetPreference(
                        AppearancePreferences
                            .ShellRailExpandedPreference,
                        bool.FalseString);
                MainWindow window = services.GetRequiredService<MainWindow>();
                INavigationService navigation = services.GetRequiredService<INavigationService>();
                try
                {
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Width = 900;
                    window.Height = 600;
                    window.Activate();
                    Dispatcher.UIThread.RunJobs();

                    Assert.False(
                        window.FindControl<Border>(
                                "NavigationScrim")!
                            .IsVisible,
                        "The shell navigation overlay obscured the minimum-size destination capture.");
                    Assert.Equal(64, window.FindControl<Grid>("BodyGrid")!.ColumnDefinitions[0].ActualWidth);
                    Assert.False(window.FindControl<TextBlock>("ConfigurationChipText")!.IsVisible);
                    Assert.False(window.FindControl<Border>("SearchShortcut")!.IsVisible);

                    foreach (ShellDestination destination in Enum.GetValues<ShellDestination>())
                    {
                        navigation.Navigate(destination);
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

                        Assert.Equal(destination, navigation.Current);
                        Control activeView = Assert.IsAssignableFrom<Control>(
                            window.FindControl<ContentControl>("ContentHost")!.Content);
                        Assert.True(activeView.Bounds.Width >= 800,
                            $"{destination} did not fill the compact content host. Width={activeView.Bounds.Width:0}");
                        Assert.True(activeView.Bounds.Height >= 500,
                            $"{destination} did not fill the compact content host. Height={activeView.Bounds.Height:0}");

                        if (destination == ShellDestination.Library)
                        {
                            LibraryView library = Assert.IsType<LibraryView>(activeView);
                            Button inspectorToggle = library.FindControl<Button>("InspectorToggle")!;
                            Border inspectorScrim = library.FindControl<Border>("InspectorScrim")!;
                            Assert.True(inspectorToggle.IsVisible);
                            Assert.False(inspectorScrim.IsVisible);
                            inspectorToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Dispatcher.UIThread.RunJobs();
                            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                            Assert.True(inspectorScrim.IsVisible);
                            PersistedSplitView split = library.FindControl<PersistedSplitView>("WorkspaceSplit")!;
                            ContentPresenter left = split.FindControl<ContentPresenter>("LeftPresenter")!;
                            ContentPresenter right = split.FindControl<ContentPresenter>("RightPresenter")!;
                            Assert.True(left.Bounds.Width >= split.Bounds.Width - 1,
                                $"Compact Library pane was clipped: {left.Bounds.Width:0}/{split.Bounds.Width:0}");
                            Assert.InRange(right.Bounds.Width, 319, 321);
                            Assert.True(right.Bounds.Right <= split.Bounds.Width + 1,
                                $"Inspector drawer exceeded its host: right={right.Bounds.Right:0}, host={split.Bounds.Width:0}");
                        }
                        else if (destination == ShellDestination.Settings)
                        {
                            SettingsView settings = Assert.IsType<SettingsView>(activeView);
                            SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(settings.DataContext);
                            string originalDatabaseFile =
                                viewModel.DatabaseFile;
                            viewModel.DatabaseFile += ".changed";
                            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 4;
                            Dispatcher.UIThread.RunJobs();
                            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                            foreach (NumericUpDown input in new[]
                                     {
                                         settings.FindControl<NumericUpDown>("ArtworkSizeThresholdInput")!,
                                         settings.FindControl<NumericUpDown>("ArtworkDimensionThresholdInput")!,
                                         settings.FindControl<NumericUpDown>("ArtworkRepairSizeTargetInput")!,
                                         settings.FindControl<NumericUpDown>("ArtworkRepairDimensionTargetInput")!,
                                     })
                            {
                                Assert.True(input.IsEffectivelyVisible);
                                Point? corner = input.TranslatePoint(
                                    new Point(input.Bounds.Width, input.Bounds.Height), settings);
                                Assert.NotNull(corner);
                                Assert.InRange(corner.Value.X, 0, settings.Bounds.Width + 1);
                                Assert.True(corner.Value.Y >= 0);
                                Assert.Equal(
                                    ScrollBarVisibility.Auto,
                                    input.GetVisualAncestors()
                                        .OfType<ScrollViewer>()
                                        .First()
                                        .VerticalScrollBarVisibility);
                            }
                            PageHeader header = settings.GetVisualDescendants().OfType<PageHeader>().Single();
                            Button settingsMore = settings.FindControl<Button>(
                                "SettingsMoreButton")!;
                            MenuFlyout settingsMenu =
                                Assert.IsType<MenuFlyout>(
                                    settingsMore.Flyout);
                            settingsMenu.ShowAt(
                                settingsMore);
                            Dispatcher.UIThread.RunJobs();
                            MenuItem discard =
                                Assert.Single(
                                    settingsMenu.Items
                                        .OfType<MenuItem>(),
                                    item => Equals(
                                        item.Header,
                                        localization.Get(
                                            "Settings.Action.Discard")));
                            Assert.True(discard.IsVisible);
                            foreach (Button button in header.GetVisualDescendants().OfType<Button>()
                                         .Where(button => button.IsEffectivelyVisible))
                            {
                                Point? corner = button.TranslatePoint(
                                    new Point(button.Bounds.Width, button.Bounds.Height), header);
                                Assert.NotNull(corner);
                                Assert.InRange(corner.Value.X, 0, header.Bounds.Width + 1);
                                Assert.InRange(corner.Value.Y, 0, header.Bounds.Height + 1);
                            }

                            Task discardTask =
                                viewModel.DiscardChangesCommand
                                    .ExecuteAsync(null);
                            Dispatcher.UIThread.RunJobs();
                            DialogService dialogs =
                                services.GetRequiredService<
                                    DialogService>();
                            Assert.NotNull(dialogs.Current);
                            dialogs.Complete(true);
                            await discardTask;
                            Dispatcher.UIThread.RunJobs();
                            Assert.False(
                                viewModel.HasUnsavedChanges);
                            Assert.Equal(
                                originalDatabaseFile,
                                viewModel.DatabaseFile);
                            Assert.False(
                                discard.IsVisible);
                        }
                        else if (destination ==
                                 ShellDestination.About)
                        {
                            AboutView about =
                                Assert.IsType<AboutView>(
                                    activeView);
                            Grid packages =
                                about.FindControl<Grid>(
                                    "PackageGrid")!;
                            Border skiaSharp =
                                about.FindControl<Border>(
                                    "SkiaSharpPackageCard")!;
                            ScrollViewer scroll =
                                about.FindControl<ScrollViewer>(
                                    "AboutScroll")!;
                            Assert.Single(
                                packages.ColumnDefinitions);
                            Assert.Equal(
                                3,
                                packages.RowDefinitions.Count);
                            Assert.Equal(
                                0,
                                Grid.GetColumn(skiaSharp));
                            Assert.Equal(
                                2,
                                Grid.GetRow(skiaSharp));
                            Assert.True(
                                about.FindControl<BrandMark>(
                                    "AboutBrandMark")!
                                    .IsEffectivelyVisible);
                            Assert.True(
                                scroll.Bounds.Width <=
                                about.Bounds.Width);
                            Assert.True(
                                scroll.Bounds.Height > 0);
                        }

                        using var frame = window.GetLastRenderedFrame();
                        Assert.NotNull(frame);
                        Assert.Equal(900, frame.PixelSize.Width);
                        Assert.Equal(600, frame.PixelSize.Height);
                        if (!string.IsNullOrWhiteSpace(captureDirectory))
                        {
                            Directory.CreateDirectory(captureDirectory);
                            frame.Save(Path.Combine(captureDirectory,
                                $"{name}-900x600-{destination}.png"),
                                PngBitmapEncoderOptions.Default);
                        }
                    }
                }
                finally
                {
                    window.Hide();
                }
            }
        }
        finally
        {
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public void Populated_health_nested_panes_fit_at_the_900_by_600_minimum()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        ILocalizationService localization =
            services.GetRequiredService<
                ILocalizationService>();
        MainWindow window = services.GetRequiredService<MainWindow>();
        AnalyzerViewModel analyzer = services.GetRequiredService<AnalyzerViewModel>();
        INavigationService navigation = services.GetRequiredService<INavigationService>();
        var record = new TrackRecord
        {
            Path = @"X:\Fixture\Artist\Album\Track.flac",
            Artist = "Fixture Artist",
            Album = "Fixture Album",
            Title = "Fixture Track",
        };
        AnalysisRunViewModel findings = AnalysisRunViewModel.ForFindings(
            new AnalysisReport("Fixture findings",
                [new AnalysisFinding(record.Path, "Fixture inconsistency", "Metadata")]),
            [record],
            "One fixture finding.");
        var repair = new AnalysisTagRepair(
            record.Path,
            TagFields.AlbumArtist,
            null,
            record.Artist!,
            "Fill the missing album artist",
            1,
            DateTime.UnixEpoch);
        AnalysisRunViewModel repairs = AnalysisRunViewModel.ForRepairs(
            new AnalysisRepairPlan("Fixture repairs", [repair]),
            [new AnalysisRepairItemViewModel(repair)],
            [record],
            "One fixture metadata repair.");
        analyzer.Runs.Add(findings);
        analyzer.Runs.Add(repairs);
        analyzer.SelectedRun = repairs;

        try
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Width = 900;
            window.Height = 600;
            window.Activate();
            navigation.Navigate(ShellDestination.Health);
            Render();

            HealthView health = Assert.IsType<HealthView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            Border compactPicker =
                health.FindControl<Border>(
                    "HealthResultPickerHost")!;
            Border resultRail =
                health.FindControl<Border>(
                    "HealthResultNavigationRail")!;
            ComboBox resultPicker =
                health.FindControl<ComboBox>(
                    "HealthResultPicker")!;
            ListBox resultNavigation =
                health.FindControl<ListBox>(
                    "HealthResultNavigation")!;
            TabControl resultContent =
                health.FindControl<TabControl>(
                    "HealthResultsTabs")!;

            Assert.False(
                string.IsNullOrWhiteSpace(
                    AutomationProperties.GetName(
                        resultPicker)));
            Assert.False(
                string.IsNullOrWhiteSpace(
                    AutomationProperties.GetName(
                        resultNavigation)));

            analyzer.ActiveResultIndex = 2;
            Render();
            TextBox artistThreshold =
                health.FindControl<TextBox>(
                    "ArtistThresholdInput")!;
            Assert.Equal(
                localization.Get(
                    "Health.Artist.ThresholdAutomation"),
                AutomationProperties.GetName(
                    artistThreshold));
            artistThreshold.Text = "0.13";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                0.13,
                analyzer.ArtistThreshold,
                precision: 3);
            artistThreshold.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                0,
                analyzer.ArtistThreshold);
            Assert.NotNull(
                health.FindControl<ComboBox>(
                    "ArtistRootDisposition"));
            Assert.NotNull(
                health.FindControl<ComboBox>(
                    "ArtworkRepairRootDisposition"));
            Assert.DoesNotContain(
                health.GetVisualDescendants()
                    .OfType<Button>(),
                button => string.Equals(
                    button.Content as string,
                    "Merge",
                    StringComparison.Ordinal));

            analyzer.ActiveResultIndex = 3;
            Render();
            AppDataGrid repairGrid =
                health.FindControl<AppDataGrid>(
                    "RepairGrid")!;
            DataGridColumn before =
                repairGrid.Columns.Single(column =>
                    repairGrid.KeyFor(column) ==
                    "Before");
            DataGridColumn after =
                repairGrid.Columns.Single(column =>
                    repairGrid.KeyFor(column) ==
                    "After");
            DataGridColumn result =
                repairGrid.Columns.Single(column =>
                    repairGrid.KeyFor(column) ==
                    "Result");
            Assert.IsType<DataGridTemplateColumn>(
                before);
            Assert.IsType<DataGridTemplateColumn>(
                after);
            Assert.IsType<DataGridTextColumn>(
                result);

            var repairItem =
                new AnalysisRepairItemViewModel(
                    new AnalysisTagRepair(
                        @"Z:\Music\Track.flac",
                        TagFields.Title,
                        "Mix\u00A0One",
                        "Mix One",
                        "Normalize whitespace.",
                        100,
                        DateTime.UtcNow));
            var beforeColumn =
                Assert.IsType<DataGridTemplateColumn>(
                    before);
            TextBlock beforeCell =
                Assert.IsType<TextBlock>(
                    beforeColumn.CellTemplate!
                        .Build(repairItem));
            Assert.Contains(
                beforeCell.Inlines!.OfType<Run>(),
                run => run.Classes.Contains(
                    "text-difference"));
            Assert.Contains(
                "U+00A0 NO-BREAK SPACE",
                Assert.IsType<string>(
                    ToolTip.GetTip(beforeCell)));

            Assert.True(
                compactPicker.IsEffectivelyVisible);
            Assert.False(
                resultRail.IsEffectivelyVisible);
            Assert.Equal(
                "3",
                Assert.IsType<ComboBoxItem>(
                    resultPicker.SelectedItem).Tag);
            Assert.Equal(
                3,
                resultContent.SelectedIndex);
            Assert.DoesNotContain(
                resultContent.GetVisualDescendants()
                    .OfType<TabStrip>(),
                tabStrip =>
                    tabStrip.IsEffectivelyVisible);
            AssertNestedPaneFits(health, "health-metadata-repairs");
            Assert.DoesNotContain(
                health.GetVisualDescendants()
                    .OfType<PersistedSplitView>(),
                split =>
                    split.PersistenceKey ==
                    "health-findings");

            window.Width = 1440;
            Render();
            Assert.False(
                compactPicker.IsEffectivelyVisible);
            Assert.True(
                resultRail.IsEffectivelyVisible);
            Assert.Equal(
                "3",
                Assert.IsType<ListBoxItem>(
                    resultNavigation.SelectedItem).Tag);
            string[] visibleResultTags =
                resultNavigation.Items
                    .OfType<ListBoxItem>()
                    .Where(item =>
                        item.Tag is not null &&
                        item.IsVisible)
                    .Select(item =>
                        item.Tag!.ToString()!)
                    .ToArray();
            Assert.True(
                visibleResultTags.SequenceEqual(
                    ["0", "3"]),
                $"Visible tags: {string.Join(",", visibleResultTags)}; " +
                $"duplicates={analyzer.HasDuplicateSection}, artists={analyzer.HasArtistSection}, " +
                $"repairs={analyzer.HasRepairSection}, file={analyzer.HasRepresentationSection}, " +
                $"conflicts={analyzer.HasConflictSection}, matrix={analyzer.HasMatrixSection}, " +
                $"itl={analyzer.HasItlRepairSection}, artwork={analyzer.HasArtworkRepairSection}; " +
                $"items={string.Join(";",
                    resultNavigation.Items
                        .OfType<ListBoxItem>()
                        .Where(item => item.Tag is not null)
                        .Select(item =>
                            $"{item.Tag}:{item.IsVisible}/{item.IsEffectivelyVisible}/" +
                            $"{item.DataContext?.GetType().Name ?? "<null>"}"))}.");
            Assert.All(
                resultNavigation.Items
                    .OfType<ListBoxItem>()
                    .Where(item =>
                        item.Tag is not null &&
                        item.IsVisible),
                item =>
                    Assert.False(
                        string.IsNullOrWhiteSpace(
                            AutomationProperties.GetName(
                                item))));
            Assert.Equal(
                3,
                resultContent.SelectedIndex);
            AssertNestedPaneFits(
                health,
                "health-metadata-repairs");
            ListBoxItem findingsNavigation =
                resultNavigation.Items
                    .OfType<ListBoxItem>()
                    .Single(item =>
                        item.Tag?.ToString() == "0");
            ListBoxItem repairNavigation =
                resultNavigation.Items
                    .OfType<ListBoxItem>()
                    .Single(item =>
                        item.Tag?.ToString() == "3");
            resultNavigation.SelectedItem =
                findingsNavigation;
            Render();
            Assert.Equal(
                0,
                analyzer.ActiveResultIndex);
            Assert.Equal(
                0,
                resultContent.SelectedIndex);
            resultNavigation.SelectedItem =
                repairNavigation;
            Render();
            Assert.Equal(
                3,
                analyzer.ActiveResultIndex);
            Assert.Equal(
                3,
                resultContent.SelectedIndex);

            window.Width = 900;
            Render();
            analyzer.SelectedRun = findings;
            Render();
            Assert.True(
                compactPicker.IsEffectivelyVisible);
            Assert.False(
                resultRail.IsEffectivelyVisible);
            Assert.Equal(
                "0",
                Assert.IsType<ComboBoxItem>(
                    resultPicker.SelectedItem).Tag);
            Assert.Equal(
                ["0"],
                resultPicker.Items
                    .OfType<ComboBoxItem>()
                    .Where(item =>
                        item.IsVisible)
                    .Select(item =>
                        item.Tag!.ToString()!)
                    .ToArray());
            Assert.Equal(
                0,
                resultContent.SelectedIndex);
            AssertNestedPaneFits(health, "health-findings");
            Assert.DoesNotContain(
                health.GetVisualDescendants()
                    .OfType<PersistedSplitView>(),
                split =>
                    split.PersistenceKey ==
                    "health-metadata-repairs");

            using var frame = window.GetLastRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(900, frame.PixelSize.Width);
            Assert.Equal(600, frame.PixelSize.Height);
        }
        finally
        {
            window.Hide();
        }

        static void Render()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        }

        static void AssertNestedPaneFits(HealthView health, string persistenceKey)
        {
            PersistedSplitView split = health.GetVisualDescendants()
                .OfType<PersistedSplitView>()
                .Single(candidate => candidate.PersistenceKey == persistenceKey);
            ContentPresenter left = split.FindControl<ContentPresenter>("LeftPresenter")!;
            ContentPresenter right = split.FindControl<ContentPresenter>("RightPresenter")!;

            Assert.True(split.Bounds.Width >= split.MinLeftWidth + 10 + split.MinRightWidth,
                $"{persistenceKey} cannot satisfy its pane minima: {split.Bounds.Width:0}px.");
            Assert.True(left.Bounds.Right <= split.Bounds.Width + 1,
                $"{persistenceKey} hierarchy exceeded its host: {left.Bounds.Right:0}/{split.Bounds.Width:0}.");
            Assert.True(right.Bounds.Right <= split.Bounds.Width + 1,
                $"{persistenceKey} results exceeded its host: {right.Bounds.Right:0}/{split.Bounds.Width:0}.");
            Assert.True(right.Bounds.Width >= split.MinRightWidth - 1,
                $"{persistenceKey} results collapsed below minimum: {right.Bounds.Width:0}px.");
        }
    }

    [AvaloniaFact]
    public async Task Clearing_library_health_chip_clears_the_originating_disposition()
    {
        var record = new TrackRecord
        {
            Path = @"X:\Fixture\Lossy.mp3",
            Artist = "Fixture Artist",
            AlbumArtist = "Fixture Artist",
            Album = "Fixture Album",
            Title = "Lossy Fixture",
            CodecName = "MP3",
            CodecType = CodecType.Lossy,
        };
        using ServiceProvider services = BuildIsolatedServices([record]);
        WorkflowIntegrationService integration =
            services.GetRequiredService<WorkflowIntegrationService>();
        AnalyzerViewModel health = services.GetRequiredService<AnalyzerViewModel>();
        LibraryViewModel library = services.GetRequiredService<LibraryViewModel>();
        integration.Start();

        await health.RunLossyCommand.ExecuteAsync(null);
        AnalysisFindingViewModel finding = Assert.Single(
            Assert.Single(Assert.Single(health.FindingGroups).Artists).Albums).Findings[0];
        finding.Disposition = AnalysisFindingDisposition.Filter;

        Assert.True(library.HasHealthFilter);
        Assert.Equal([record.Path], health.FilteredPaths);

        library.ClearHealthFilterCommand.Execute(null);

        Assert.False(library.HasHealthFilter);
        Assert.Empty(health.FilteredPaths);
        Assert.Equal(AnalysisFindingDisposition.None, finding.Disposition);
    }

    [AvaloniaFact]
    public async Task Representative_page_states_render_at_both_sizes_in_light_and_dark()
    {
        string? captureDirectory = Environment.GetEnvironmentVariable("MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        ThemeVariant? previousTheme = Application.Current!.RequestedThemeVariant;
        LibraryRow[] fixture =
        [
            new(new TrackRecord
            {
                Path = @"X:\Fixture\Aurora.flac",
                Title = "Aurora",
                Artist = "The Fixtures",
                Album = "Deterministic Data",
                CodecName = "FLAC",
            }),
            new(new TrackRecord
            {
                Path = @"X:\Fixture\Harbor.mp3",
                Title = "Harbor",
                Artist = "The Fixtures",
                Album = "Deterministic Data",
                CodecName = "MP3",
            }),
        ];

        try
        {
            foreach ((string themeName, ThemeVariant theme) in new[]
                     {
                         ("light", ThemeVariant.Light),
                         ("dark", ThemeVariant.Dark),
                     })
            foreach ((int width, int height) in new[] { (1440, 900), (900, 600) })
            {
                Application.Current.RequestedThemeVariant = theme;
                using ServiceProvider services = BuildIsolatedServices(
                    fixture.Select(row => row.Record).ToArray());
                App.UseServicesForTests(services);
                if (width == 900)
                {
                    services.GetRequiredService<
                            IAppSettings>()
                        .SetPreference(
                            AppearancePreferences
                                .ShellRailExpandedPreference,
                            bool.FalseString);
                }
                MainWindow window = services.GetRequiredService<MainWindow>();
                INavigationService navigation = services.GetRequiredService<INavigationService>();
                IActivityService activities = services.GetRequiredService<IActivityService>();
                DialogService dialogs = services.GetRequiredService<DialogService>();
                try
                {
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Width = width;
                    window.Height = height;
                    window.Activate();
                    navigation.Navigate(ShellDestination.Library);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(
                        window.FindControl<Border>(
                                "NavigationScrim")!
                            .IsVisible,
                        "The shell navigation overlay obscured the representative state capture.");

                    LibraryView view = Assert.IsType<LibraryView>(
                        window.FindControl<ContentControl>("ContentHost")!.Content);
                    LibraryViewModel viewModel = Assert.IsType<LibraryViewModel>(view.DataContext);
                    viewModel.IsInspectorOpen = false;

                    viewModel.Rows = [];
                    viewModel.StatusText = "Choose a fixture configuration.";
                    viewModel.PageState = LibraryPageState.NoConfiguration;
                    RenderAndCapture("empty");
                    Assert.True(view.FindControl<Border>("LibraryEmptyState")!.IsVisible);

                    await viewModel.ReloadAsync();
                    viewModel.Indexing.StatusText = "Fixture index is current.";
                    RenderAndCapture("populated");
                    Assert.Equal(2, view.FindControl<AppDataGrid>("LibraryGrid")!
                        .ItemsSource!.Cast<object>().Count());

                    Guid activity = activities.Start(
                        "Index fixture library", "Reading 2 deterministic tracks",
                        ShellDestination.Library, () => { });
                    viewModel.IsBusy = true;
                    viewModel.PageState = LibraryPageState.Loading;
                    viewModel.Indexing.StatusText = "Indexing fixture library…";
                    RenderAndCapture("busy");
                    Assert.True(window.FindControl<Border>("ActivityBanner")!.IsVisible);
                    viewModel.IsBusy = false;
                    activities.Finish(activity, "Fixture index complete", AppActivityState.Completed);
                    activities.Dismiss(activity);

                    viewModel.Rows = [];
                    viewModel.StatusText = "The fixture cache could not be opened.";
                    viewModel.PageState = LibraryPageState.Error;
                    viewModel.Indexing.StatusText = "Last fixture index failed.";
                    RenderAndCapture("error");
                    Assert.Contains("could not be opened", viewModel.EmptyStateMessage);

                    Task<bool> pending = dialogs.ConfirmAsync(
                        "Apply fixture changes?",
                        "Move 2 files. A recovery journal will be retained for 30 days.",
                        "Apply");
                    Dispatcher.UIThread.RunJobs();
                    DialogHost dialogHost = window.GetVisualDescendants().OfType<DialogHost>().Single();
                    Assert.True(dialogHost.IsVisible);
                    dialogHost.InvalidateMeasure();
                    dialogHost.InvalidateArrange();
                    dialogHost.InvalidateVisual();
                    window.InvalidateVisual();
                    RenderAndCapture("dialog");
                    dialogs.Complete(false);
                    Assert.False(await pending);

                    void RenderAndCapture(string state)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                        using var frame = window.GetLastRenderedFrame();
                        Assert.NotNull(frame);
                        Assert.Equal(width, frame.PixelSize.Width);
                        Assert.Equal(height, frame.PixelSize.Height);
                        if (string.IsNullOrWhiteSpace(captureDirectory))
                            return;
                        Directory.CreateDirectory(captureDirectory);
                        frame.Save(Path.Combine(captureDirectory,
                            $"{themeName}-{width}x{height}-state-{state}.png"),
                            PngBitmapEncoderOptions.Default);
                    }
                }
                finally
                {
                    window.Hide();
                }
            }
        }
        finally
        {
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public async Task Launch_time_search_loads_artwork_for_every_realized_library_row()
    {
        TrackRecord[] records = Enumerable.Range(0, 40).Select(index => new TrackRecord
        {
            Path = $@"X:\Fixture\Track {index:00}.flac",
            Title = $"Track {index:00}",
            Artist = "The Fixtures",
            Album = "Launch Search",
            CodecName = "FLAC",
        }).ToArray();
        using ServiceProvider services = BuildIsolatedServices(records);
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        LibraryViewModel viewModel = services.GetRequiredService<LibraryViewModel>();
        try
        {
            window.Show();
            window.Width = 1440;
            window.Height = 900;
            window.Activate();

            // This is the launch race: filtering navigates to Library while its first cached-row
            // load and the DataGrid's initial layout are both still being scheduled.
            viewModel.SetGlobalFilter("Track");
            for (int attempt = 0; attempt < 40 && viewModel.Rows.Count != records.Length; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                await Task.Delay(5);
            }

            LibraryView library = Assert.IsType<LibraryView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            AppDataGrid grid = library.FindControl<AppDataGrid>("LibraryGrid")!;
            LibraryRow[] realized = [];
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                realized = grid.GetVisualDescendants().OfType<DataGridRow>()
                    .Select(row => row.DataContext).OfType<LibraryRow>().Distinct().ToArray();
                if (realized.Length > 1 && realized.All(row => row.ThumbnailLoaded))
                    break;
                await Task.Delay(5);
            }

            Assert.True(realized.Length > 1);
            Assert.All(realized, row => Assert.True(row.ThumbnailLoaded, row.Path));
        }
        finally
        {
            window.Hide();
        }
    }

    private static ServiceProvider BuildIsolatedServices(
        IReadOnlyList<TrackRecord>? records = null,
        Action<IServiceCollection>? configureServices = null) =>
        Composition.BuildServices(services =>
        {
            services.AddSingleton<IAppSettings>(new FakeSettings());
            if (records is not null)
                services.AddSingleton<ILibraryService>(new FixtureLibraryService(records));
            configureServices?.Invoke(services);
        });

    private static ArtworkRepairCandidateViewModel[]
        CreateArtworkCandidates(
            ILibraryService library,
            int count) =>
        [
            .. Enumerable.Range(
                    0,
                    count)
                .Select(index =>
                    new ArtworkRepairCandidateViewModel(
                        $@"C:\Music\Album\Candidate {index:N0}.flac",
                        $"Candidate {index:N0}",
                        $"hash-{index:N0}",
                        $"{800 + index % 5} × {800 + index % 5} · {128 + index % 7} KiB",
                        800 + index % 5,
                        800 + index % 5,
                        128_000 + index,
                        library,
                        null)),
        ];

    private static void RenderUi()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitForUiAsync(
        Func<bool> condition)
    {
        for (int attempt = 0;
             attempt < 80 && !condition();
             attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(condition());
    }

    private static double ContrastRatio(Color first, Color second)
    {
        double firstLuminance = RelativeLuminance(first);
        double secondLuminance = RelativeLuminance(second);
        double lighter = Math.Max(firstLuminance, secondLuminance);
        double darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        double value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private sealed class FakeSettings : IAppSettings
    {
        private readonly Dictionary<string, string> _preferences = [];
        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration => null;
        public event EventHandler? ConfigurationChanged;
        public AppConfigurationSnapshot GetSnapshot() => new(null, null, 0);
        public void LoadConfig(string path) => ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        public string? GetRememberedConfigPath() => null;
        public IReadOnlyList<string> RecentConfigPaths => [];
        public void ClearRecentConfigs() { }
        public string? GetPreference(string key) => _preferences.GetValueOrDefault(key);
        public void SetPreference(string key, string? value)
        {
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] = value;
        }
    }

    private sealed class RecordingClipboardService :
        IPlatformService
    {
        public string? Text { get; private set; }

        public Task CopyTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }

        public Task<string?> ReadTextAsync() =>
            Task.FromResult(Text);

        public void RevealFile(string path)
        {
        }
    }

    private sealed class RecordingWorkbenchService :
        IWorkbenchService
    {
        public List<WorkbenchLoadRequest> Requests { get; } = [];

        public Task<WorkbenchLoadResult> LoadAsync(
            WorkbenchLoadRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new WorkbenchLoadResult(
                [.. request.Sources.Select(CreateDocument)],
                []));
        }

        private static MediaDocument CreateDocument(
            string source)
        {
            string path = Path.GetFullPath(source);
            return new(
                path,
                [new(
                    "VorbisComment",
                    [new(
                        MetadataFieldKey.Known(TagFields.Title),
                        [Path.GetFileNameWithoutExtension(path)])],
                    true,
                    true,
                    true,
                    true)],
                [],
                null,
                new(
                    path,
                    10,
                    DateTime.UtcNow,
                    $"snapshot:{path}"),
                true);
        }
    }

    private sealed class FieldsDialogDocumentService :
        IMetadataDocumentService
    {
        public Task<MediaDocument> LoadAsync(
            string path,
            bool includeArtwork = true,
            CancellationToken ct = default) =>
            Task.FromResult(new MediaDocument(
                path,
                [new(
                    "VorbisComment",
                    [new(
                        MetadataFieldKey.Known(
                            TagFields.Title),
                        ["Original title"])],
                    true,
                    true,
                    true,
                    true)],
                [],
                null,
                new(
                    path,
                    10,
                    DateTime.UtcNow,
                    "hash"),
                true));
    }

    private sealed class FixtureLibraryService(IReadOnlyList<TrackRecord> records) : ILibraryService
    {
        public bool IsReady => true;

        public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult((records.Count, 0, 0, 0));

        public Task<LibrarySnapshot> BuildSnapshotAsync(
            LibraryGrouping grouping = LibraryGrouping.AlbumArtist, CancellationToken ct = default) =>
            Task.FromException<LibrarySnapshot>(new NotSupportedException());

        public Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default) =>
            Task.FromResult(records);

        public Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default) =>
            Task.FromException<AnalysisReport>(new NotSupportedException());

        public Task<FileDetails?> GetFileDetailsAsync(
            string path, bool includeArtwork, CancellationToken ct = default) =>
            Task.FromResult<FileDetails?>(null);

        public Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<byte[]?>>(paths.Select(_ => (byte[]?)null).ToArray());

        public Task<IReadOnlyList<string>> GetImageSignaturesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(paths.Select(_ => "").ToArray());
    }
}

internal static class WorkbenchViewTestExtensions
{
    public static T? FindControl<T>(
        this WorkbenchView root,
        string name)
        where T : Control
    {
        T? logical =
            root.GetLogicalDescendants()
                .OfType<T>()
                .FirstOrDefault(control =>
                    control.Name == name);
        T? visual =
            root.GetVisualDescendants()
                .OfType<T>()
                .FirstOrDefault(control =>
                    control.Name == name);
        if (logical is not null ||
            visual is not null)
            return logical ?? visual;

        foreach (Flyout flyout in
                 root.GetLogicalDescendants()
                     .OfType<SplitButton>()
                     .Select(button =>
                         button.Flyout)
                     .OfType<Flyout>())
        {
            if (flyout.Content is not Control content)
                continue;
            if (content is T candidate &&
                candidate.Name == name)
                return candidate;
            T? nested =
                content.GetLogicalDescendants()
                    .OfType<T>()
                    .FirstOrDefault(control =>
                        control.Name == name);
            if (nested is not null)
                return nested;
        }
        return null;
    }
}
