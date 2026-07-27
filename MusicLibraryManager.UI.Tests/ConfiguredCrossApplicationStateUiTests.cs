using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.Immutable;
using MetadataCaching;
using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
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
/// Configured and populated-state evidence for cross-application destinations.
/// The broad presentation matrix deliberately uses an unconfigured application;
/// these tests exercise the workflow states that only exist after real data or a
/// reviewed preview is available.
/// </summary>
public sealed class ConfiguredCrossApplicationStateUiTests
{
    [AvaloniaFact]
    public async Task Configured_home_populates_metrics_and_keeps_actions_reachable_at_900_by_600()
    {
        using var fixture = new ConfiguredFixture();
        var library = new PopulatedLibraryService(
            fixture.RootPath);
        using ServiceProvider services = BuildServices(
            fixture.Settings,
            collection =>
                collection.AddSingleton<
                    ILibraryService>(library));
        App.UseServicesForTests(services);
        MainWindow window = OpenShell(
            services,
            ShellDestination.Home);
        try
        {
            HomeView view =
                ActiveView<HomeView>(window);
            HomeViewModel model =
                Assert.IsType<HomeViewModel>(
                    view.DataContext);
            await WaitForAsync(() =>
                !model.IsBusy &&
                model.TrackCount == "3");

            Assert.False(
                view.FindControl<Grid>(
                    "SetupLayout")!
                    .IsEffectivelyVisible);
            Assert.Equal("3", model.TrackCount);
            Assert.Equal("2", model.AlbumCount);
            Assert.Equal("2", model.ArtistCount);
            Assert.Equal("2", model.ArtworkCount);
            Assert.Equal("1", model.AttentionCount);
            Assert.Equal(
                LocalizedText.Get(
                    "Index.Status.CachedLibraryReady"),
                model.Indexing.StatusText);

            AdaptivePage page =
                view.FindControl<AdaptivePage>(
                    "PageScaffold")!;
            UniformGrid metrics =
                view.FindControl<UniformGrid>(
                    "MetricGrid")!;
            int expectedColumns =
                page.ContentWidth >= 1120
                    ? 4
                    : page.ContentWidth >= 620
                        ? 2
                        : 1;
            Assert.Equal(
                expectedColumns,
                metrics.Columns);

            Button openLibrary =
                FindBoundButton(
                    view,
                    model.OpenLibraryCommand);
            Button openHealth =
                FindBoundButton(
                    view,
                    model.OpenHealthCommand);
            Button refresh =
                FindBoundButton(
                    view,
                    model.RefreshCommand);
            AssertActionReachable(
                view,
                openLibrary);
            AssertActionReachable(
                view,
                openHealth);
            AssertActionReachable(
                view,
                refresh);

            ScrollViewer pageScroll =
                Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<ScrollViewer>(),
                    scroll =>
                        scroll.GetVisualAncestors()
                            .OfType<HomeView>()
                            .Any());
            AssertNoHorizontalOverflow(
                pageScroll,
                "Home");
            CaptureConfiguredState(
                window,
                "home");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Configured_ingest_keeps_preflight_and_populated_preview_reachable_at_900_by_600()
    {
        using var fixture = new ConfiguredFixture();
        var ingest = new PopulatedIngestService();
        var preflight =
            new PopulatedPreflightService();
        using ServiceProvider services = BuildServices(
            fixture.Settings,
            collection =>
            {
                collection.AddSingleton<
                    IIngestMusicService>(ingest);
                collection.AddSingleton<
                    IIngestPreflightService>(
                    preflight);
            });
        App.UseServicesForTests(services);
        MainWindow window = OpenShell(
            services,
            ShellDestination.Ingest);
        try
        {
            IngestView view =
                ActiveView<IngestView>(window);
            IngestViewModel model =
                Assert.IsType<IngestViewModel>(
                    view.DataContext);
            model.SourceDirectory =
                fixture.SourcePath;
            Assert.True(
                model.PreflightCommand
                    .CanExecute(null));

            await model.PreflightCommand
                .ExecuteAsync(null);
            Render();
            Assert.Equal(
                2,
                model.PreflightChecks.Count);
            Expander checks =
                view.FindControl<Expander>(
                    "PreflightChecksExpander")!;
            Assert.True(
                checks.IsEffectivelyVisible);
            checks.IsExpanded = true;
            Render();
            IngestPreflightCheckViewModel
                capacityCheck =
                    model.PreflightChecks[1];
            Expander capacityDetails =
                Assert.Single(
                    checks.GetVisualDescendants()
                        .OfType<Expander>(),
                    expander =>
                        ReferenceEquals(
                            expander.DataContext,
                            capacityCheck));
            AssertActionReachable(
                view,
                capacityDetails);
            capacityDetails.IsExpanded =
                true;
            Render();
            TextBlock capacityDiagnostic =
                Assert.Single(
                    capacityDetails
                        .GetVisualDescendants()
                        .OfType<TextBlock>(),
                    text =>
                        text.Text ==
                        capacityCheck
                            .DiagnosticDetail);
            AssertActionReachable(
                view,
                capacityDiagnostic);
            checks.IsExpanded = false;
            Render();
            Button preflightAction =
                Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<Button>(),
                    button =>
                        button.IsEffectivelyVisible &&
                        ReferenceEquals(
                            button.Command,
                            model.PreflightCommand));
            AssertActionReachable(
                view,
                preflightAction);

            Button preview =
                FindBoundButton(
                    view,
                    model.PreviewCommand);
            AssertActionReachable(
                view,
                preview);
            await model.PreviewCommand
                .ExecuteAsync(null);
            Render();
            Assert.Equal(2, model.Files.Count);
            Assert.True(model.HasPreviewSummary);
            Assert.True(model.HasApplicablePreview);
            Assert.False(
                preview.IsEffectivelyVisible);
            Assert.True(
                view.FindControl<AppDataGrid>(
                    "PreviewGrid")!
                    .IsEffectivelyVisible);
            ComboBox previewFilter =
                Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<ComboBox>(),
                    combo =>
                        ReferenceEquals(
                            combo.ItemsSource,
                            model
                                .PreviewFilterChoices));
            AssertActionReachable(
                view,
                previewFilter);
            previewFilter.SelectedItem =
                model.PreviewFilterChoices
                    .Single(choice =>
                        choice.Value ==
                        IngestPreviewFilter
                            .Cleanup);
            Render();
            Assert.Equal(
                IngestPreviewFilter.Cleanup,
                model.SelectedPreviewFilter);
            IngestFileItemViewModel
                cleanupItem =
                    Assert.Single(
                        model.Files);
            Assert.True(
                cleanupItem.IsCleanup);
            Assert.Same(
                model.Files,
                view.FindControl<AppDataGrid>(
                    "PreviewGrid")!
                    .ItemsSource);
            previewFilter.SelectedItem =
                model.PreviewFilterChoices
                    .Single(choice =>
                        choice.Value ==
                        IngestPreviewFilter.All);
            Render();
            Assert.Equal(2, model.Files.Count);

            Grid summary =
                view.FindControl<Grid>(
                    "PreviewSummaryLayout")!;
            bool narrow =
                view.Bounds.Width < 700;
            Assert.Equal(
                narrow ? 2 : 6,
                summary.ColumnDefinitions.Count);
            Assert.Equal(
                narrow ? 3 : 1,
                summary.RowDefinitions.Count);

            Button apply =
                FindBoundButton(
                    view,
                    model.ApplyCommand);
            Assert.True(
                model.ApplyCommand
                    .CanExecute(null));
            Assert.True(
                apply.IsEffectivelyVisible);
            AssertActionReachable(
                view,
                apply);
            Assert.Contains(
                view.GetVisualDescendants()
                    .OfType<
                        LocalizedFormatTextBlock>(),
                count =>
                    count.IsEffectivelyVisible &&
                    count.ResourceKey ==
                    "Ingest.Summary.CleanupCount");
            CaptureConfiguredState(
                window,
                "ingest");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Configured_organize_preview_keeps_scope_results_and_apply_reachable_at_900_by_600()
    {
        using var fixture = new ConfiguredFixture();
        var organizer =
            new PopulatedOrganizer(
                fixture.RootPath);
        using ServiceProvider services = BuildServices(
            fixture.Settings,
            collection =>
                collection.AddSingleton<
                    ILibraryOrganizer>(
                    organizer));
        App.UseServicesForTests(services);
        MainWindow window = OpenShell(
            services,
            ShellDestination.Organize);
        try
        {
            OrganizeView view =
                ActiveView<OrganizeView>(window);
            OrganizeViewModel model =
                Assert.IsType<OrganizeViewModel>(
                    view.DataContext);
            Button preview =
                FindBoundButton(
                    view,
                    model.PreviewCommand);
            AssertActionReachable(
                view,
                preview);
            await model.PreviewCommand
                .ExecuteAsync(null);
            Render();

            Assert.Equal(2, model.Moves.Count);
            Assert.True(model.HasPreview);
            Assert.False(
                preview.IsEffectivelyVisible);
            Assert.False(
                view.FindControl<Border>(
                    "OrganizeSetupCard")!
                    .IsEffectivelyVisible);
            AppDataGrid results =
                view.FindControl<AppDataGrid>(
                    "MovesGrid")!;
            Assert.True(
                results.IsEffectivelyVisible);
            Assert.True(
                results.Bounds.Height >= 120,
                $"Organize results retained only {results.Bounds.Height:0.#} px.");

            LocalizedFormatTextBlock count =
                view.FindControl<
                    LocalizedFormatTextBlock>(
                    "PlannedCount")!;
            AssertVisibleWithin(
                count,
                view,
                "Organize scope count");
            Button apply =
                FindBoundButton(
                    view,
                    model.ApplyCommand);
            Assert.True(
                model.ApplyCommand
                    .CanExecute(null));
            Assert.True(
                apply.IsEffectivelyVisible);
            AssertActionReachable(
                view,
                apply);
            CaptureConfiguredState(
                window,
                "organize");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Configured_devices_preview_stacks_editor_and_populated_results_with_one_lifecycle_primary()
    {
        using var fixture = new ConfiguredFixture();
        var sync =
            new PopulatedDeviceSyncService();
        using ServiceProvider services = BuildServices(
            fixture.Settings,
            collection =>
                collection.AddSingleton<
                    IDeviceSyncService>(sync));
        App.UseServicesForTests(services);
        MainWindow window = OpenShell(
            services,
            ShellDestination.Devices);
        try
        {
            DevicesView view =
                ActiveView<DevicesView>(window);
            DevicesViewModel model =
                Assert.IsType<DevicesViewModel>(
                    view.DataContext);
            await WaitForAsync(() =>
                !model.IsLoadingDevices);
            model.SourcePath =
                fixture.RootPath;
            model.DestinationPath =
                "Music";

            Expander advanced =
                Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<Expander>());
            advanced.IsExpanded = true;
            Render();
            ILocalizationService localization =
                services.GetRequiredService<
                    ILocalizationService>();
            CheckBox directTransfer =
                Assert.Single(
                    advanced
                        .GetVisualDescendants()
                        .OfType<CheckBox>());
            Assert.Equal(
                localization.Get(
                    "Devices.DirectTransfer"),
                directTransfer.Content);
            NumericUpDown[] advancedNumbers =
            [
                .. advanced
                    .GetVisualDescendants()
                    .OfType<NumericUpDown>(),
            ];
            Assert.Equal(2, advancedNumbers.Length);
            Assert.Contains(
                advancedNumbers,
                number =>
                    global::Avalonia.Automation
                        .AutomationProperties
                        .GetName(number) ==
                    localization.Get(
                        "Devices.MaximumRemovals"));
            Assert.Contains(
                advancedNumbers,
                number =>
                    global::Avalonia.Automation
                        .AutomationProperties
                        .GetName(number) ==
                    localization.Get(
                        "Devices.MtimeToleranceAutomation"));
            TextBox exclusions =
                Assert.Single(
                    advanced
                        .GetVisualDescendants()
                        .OfType<TextBox>(),
                    text =>
                        text.AcceptsReturn);
            Assert.Equal(
                localization.Get(
                    "Devices.ExclusionGlobsAutomation"),
                global::Avalonia.Automation
                    .AutomationProperties
                    .GetName(exclusions));
            AssertActionReachable(
                view,
                exclusions);
            advanced.IsExpanded = false;
            Render();

            await model.PreviewCommand
                .ExecuteAsync(null);
            Render();

            Assert.Equal(3, model.Actions.Count);
            Assert.Equal(12, model.Issues.Count);
            Assert.True(model.HasApplicablePreview);
            Grid layout =
                view.FindControl<Grid>(
                    "DevicesContentLayout")!;
            Assert.Single(
                layout.ColumnDefinitions);
            Assert.Equal(
                3,
                layout.RowDefinitions.Count);
            ScrollViewer configuration =
                view.FindControl<ScrollViewer>(
                    "DeviceConfigurationScroll")!;
            Grid results =
                view.FindControl<Grid>(
                    "DeviceResultsPane")!;
            Border actionsPane =
                view.FindControl<Border>(
                    "ActionsPane")!;
            ScrollViewer issueSummary =
                Assert.Single(
                    results.Children
                        .OfType<ScrollViewer>());
            Assert.True(
                issueSummary.MaxHeight <= 144,
                $"Device issue summary exceeded its standard cap: {issueSummary.MaxHeight:0.#}/144.");
            Assert.True(
                issueSummary.MaxHeight <=
                results.Bounds.Height -
                results.RowSpacing -
                actionsPane.Bounds.Height +
                1,
                $"Device issue summary did not reserve the compact actions area: max={issueSummary.MaxHeight:0.#}, results={results.Bounds.Height:0.#}, spacing={results.RowSpacing:0.#}.");
            Assert.True(
                issueSummary.Bounds.Height <=
                issueSummary.MaxHeight + 1,
                $"Device issue summary exceeded its cap: {issueSummary.Bounds.Height:0.#}/{issueSummary.MaxHeight:0.#}.");
            Assert.True(
                issueSummary.Extent.Height >
                issueSummary.Viewport.Height,
                $"Multiple issues did not produce a scrollable capped summary: extent={issueSummary.Extent.Height:0.#}, viewport={issueSummary.Viewport.Height:0.#}.");
            Assert.True(
                configuration.Bounds.Height >= 120,
                $"Device configuration retained only {configuration.Bounds.Height:0.#} px.");
            Assert.True(
                results.Bounds.Height >= 120,
                $"Device results retained only {results.Bounds.Height:0.#} px.");
            AppDataGrid actionsGrid =
                view.FindControl<AppDataGrid>(
                    "ActionsGrid")!;
            Assert.True(
                actionsGrid
                    .IsEffectivelyVisible);
            Assert.True(
                actionsGrid.Bounds.Height >=
                DevicesView
                    .CompactActionsMinimumHeight -
                1,
                $"Device actions retained only {actionsGrid.Bounds.Height:0.#} px; compact layouts require at least {DevicesView.CompactActionsMinimumHeight:0.#} px.");
            AssertVisibleWithin(
                actionsGrid,
                view,
                "Device actions");

            Button[] lifecycle =
            [
                view.FindControl<Button>(
                    "InitializeButton")!,
                view.FindControl<Button>(
                    "PreviewButton")!,
                view.FindControl<Button>(
                    "ApplyButton")!,
            ];
            Button primary =
                Assert.Single(
                    lifecycle,
                    button =>
                        button.IsEffectivelyVisible);
            Assert.Equal(
                "ApplyButton",
                primary.Name);
            Assert.True(
                model.ApplyCommand
                    .CanExecute(null));
            AssertActionReachable(
                view,
                primary);
            CaptureConfiguredState(
                window,
                "devices");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Configured_operations_collapses_empty_output_and_labels_populated_log_at_900_by_600()
    {
        using var fixture = new ConfiguredFixture();
        using ServiceProvider services =
            BuildServices(fixture.Settings);
        App.UseServicesForTests(services);
        MainWindow window = OpenShell(
            services,
            ShellDestination.Operations);
        try
        {
            OperationsView view =
                ActiveView<OperationsView>(
                    window);
            OperationsViewModel model =
                Assert.IsType<
                    OperationsViewModel>(
                    view.DataContext);
            ScrollViewer details =
                view.FindControl<ScrollViewer>(
                    "JobDetailsScroll")!;
            TextBox output =
                Assert.Single(
                    details.GetLogicalDescendants()
                        .OfType<TextBox>(),
                    text =>
                        text.IsReadOnly &&
                        text.AcceptsReturn);
            StackPanel outputField =
                Assert.IsType<StackPanel>(
                    output.Parent);

            Assert.False(model.HasJobOutput);
            Assert.False(
                outputField
                    .IsEffectivelyVisible);
            double emptyExtent =
                details.Extent.Height;

            string populatedOutput =
                string.Join(
                    Environment.NewLine,
                    Enumerable.Range(1, 80)
                        .Select(index =>
                            $"Fixture log line {index:00}: reviewed output remains available."));
            model.JobOutput =
                populatedOutput;
            Render();

            Assert.True(model.HasJobOutput);
            Assert.True(
                outputField
                    .IsEffectivelyVisible);
            Assert.Equal(
                populatedOutput,
                output.Text);
            TextBlock label =
                Assert.Single(
                    outputField.Children
                        .OfType<TextBlock>());
            Assert.True(
                label.IsEffectivelyVisible);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    label.Text));
            Assert.False(
                string.IsNullOrWhiteSpace(
                    global::Avalonia.Automation
                        .AutomationProperties
                        .GetName(output)));
            Assert.True(
                details.Extent.Height >
                emptyExtent,
                $"Populated output did not add labeled content: {emptyExtent:0.#} -> {details.Extent.Height:0.#}.");
            AssertActionReachable(
                view,
                outputField);
            CaptureConfiguredState(
                window,
                "operations");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Configured_settings_effective_policy_is_populated_prose_and_reachable_at_900_by_600()
    {
        using var fixture = new ConfiguredFixture();
        using ServiceProvider services =
            BuildServices(fixture.Settings);
        App.UseServicesForTests(services);
        MainWindow window = OpenShell(
            services,
            ShellDestination.Settings);
        try
        {
            SettingsView view =
                ActiveView<SettingsView>(window);
            SettingsViewModel model =
                Assert.IsType<SettingsViewModel>(
                    view.DataContext);
            Assert.Equal(
                LocalizedText.Get(
                    "Settings.Status.ConfigurationLoaded"),
                model.StatusMessage);
            model.SelectedTabIndex = 7;
            Render();

            ItemsControl summary =
                view.FindControl<ItemsControl>(
                    "EffectivePolicySummaryList")!;
            Assert.Equal(
                model.EffectivePolicySummaryItems
                    .Count,
                summary.ItemCount);
            Assert.True(
                summary.ItemCount >= 5);
            Assert.All(
                model.EffectivePolicySummaryItems,
                item =>
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(
                            item));
                    Assert.DoesNotContain(
                        Environment.NewLine,
                        item);
                });

            TextBlock details =
                view.FindControl<TextBlock>(
                    "EffectivePolicyDetailsText")!;
            Expander provenance =
                Assert.Single(
                    details.GetLogicalAncestors()
                        .OfType<Expander>());
            Assert.False(
                provenance.IsExpanded);

            Grid navigation =
                view.FindControl<Grid>(
                    "SettingsNavigationLayout")!;
            bool shouldUseRail =
                navigation.Bounds.Width >=
                SettingsView
                    .CategoryRailActivationWidth;
            Assert.Equal(
                shouldUseRail,
                view.FindControl<Border>(
                    "SettingsCategoryRail")!
                    .IsEffectivelyVisible);
            Assert.Equal(
                !shouldUseRail,
                view.FindControl<ComboBox>(
                    "SettingsCategoryPicker")!
                    .IsEffectivelyVisible);

            Button save =
                FindBoundButton(
                    view,
                    model.SaveConfigurationCommand,
                    requireVisible: true);
            AssertActionReachable(
                view,
                save);
            ScrollViewer policyScroll =
                summary.GetVisualAncestors()
                    .OfType<ScrollViewer>()
                    .First();
            AssertNoHorizontalOverflow(
                policyScroll,
                "Settings effective policy");
            CaptureConfiguredState(
                window,
                "settings");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void About_expanded_license_remains_scroll_reachable_with_compact_hero_at_900_by_600()
    {
        using var fixture = new ConfiguredFixture();
        using ServiceProvider services =
            BuildServices(fixture.Settings);
        App.UseServicesForTests(services);
        MainWindow window = OpenShell(
            services,
            ShellDestination.About);
        try
        {
            AboutView view =
                ActiveView<AboutView>(window);
            Border hero =
                view.FindControl<Border>(
                    "AboutHero")!;
            BrandMark mark =
                view.FindControl<BrandMark>(
                    "AboutBrandMark")!;
            Grid packages =
                view.FindControl<Grid>(
                    "PackageGrid")!;
            Assert.Equal(
                new Thickness(16),
                hero.Padding);
            Assert.Equal(80, mark.Width);
            Assert.Single(
                packages.ColumnDefinitions);

            Expander license =
                view.FindControl<Expander>(
                    "AvaloniaLicenseExpander")!;
            license.IsExpanded = true;
            Render();
            Button copy =
                view.FindControl<Button>(
                    "CopyAvaloniaLicenseButton")!;
            AssertActionReachable(
                view,
                copy);
            AssertNoHorizontalOverflow(
                view.FindControl<ScrollViewer>(
                    "AboutScroll")!,
                "About");
            CaptureConfiguredState(
                window,
                "about");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Populated_fields_dialog_keeps_distinct_add_paths_and_guarded_close_at_900_by_600()
    {
        using var fixture = new ConfiguredFixture();
        using ServiceProvider services = BuildServices(
            fixture.Settings,
            collection =>
                collection.AddSingleton<
                    IMetadataDocumentService>(
                    new PopulatedDocumentService()));
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        DialogService dialogs =
            services.GetRequiredService<
                DialogService>();
        Task<bool>? pending = null;
        try
        {
            window.Width = 900;
            window.Height = 600;
            window.WindowState =
                WindowState.Normal;
            window.Show();
            Render();
            pending = dialogs.ShowAsync(
                [Path.Combine(
                    fixture.RootPath,
                    "Track.flac")]);
            await WaitForAsync(() =>
                dialogs.Current is
                    FieldsRequest);
            FieldsRequest request =
                Assert.IsType<FieldsRequest>(
                    dialogs.Current);
            await request.ViewModel.Loading;
            Render();

            DialogHost host =
                Assert.Single(
                    window.GetVisualDescendants()
                        .OfType<DialogHost>());
            FieldsEditorView view =
                Assert.IsType<FieldsEditorView>(
                    host.FindControl<
                        ContentControl>(
                        "DialogContent")!
                        .Content);
            FieldsDialogViewModel model =
                request.ViewModel;
            Assert.True(model.Rows.Count >= 8);

            UniformGrid additions =
                view.FindControl<UniformGrid>(
                    "FieldAdditionChoices")!;
            Assert.Equal(
                view.Bounds.Width < 760
                    ? 1
                    : 2,
                additions.Columns);
            Button addField =
                FindBoundButton(
                    view,
                    model.AddFieldCommand);
            Button addUserString =
                FindBoundButton(
                    view,
                    model.AddUserStringCommand);
            Assert.NotSame(
                addField,
                addUserString);
            AssertActionReachable(
                view,
                addField);
            AssertActionReachable(
                view,
                addUserString);

            model.NewUserStringName =
                "CATALOG_ID";
            model.AddUserStringCommand
                .Execute(null);
            Render();
            Assert.Contains(
                model.Rows,
                row =>
                    row.IsUserString &&
                    row.UserStringKey ==
                    "CATALOG_ID");

            Button cancel =
                FindBoundButton(
                    view,
                    model.CancelCommand);
            AssertActionReachable(
                view,
                cancel);
            host.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent =
                        InputElement.KeyDownEvent,
                    Key = Key.Escape,
                });
            Render();
            Assert.True(
                model.IsConfirmingCancel);
            Assert.False(
                pending.IsCompleted);
            Assert.Same(
                request,
                dialogs.Current);
            CaptureConfiguredState(
                window,
                "fields-confirmation");

            model.CancelCommand
                .Execute(null);
            Assert.False(await pending);
            Assert.Null(dialogs.Current);
        }
        finally
        {
            if (pending is
                { IsCompleted: false })
            {
                dialogs.Complete(false);
                _ = await pending;
            }

            window.Hide();
        }
    }

    private static ServiceProvider BuildServices(
        IAppSettings settings,
        Action<IServiceCollection>?
            configure = null) =>
        Composition.BuildServices(
            collection =>
            {
                collection.AddSingleton(
                    settings);
                configure?.Invoke(
                    collection);
            });

    private static MainWindow OpenShell(
        IServiceProvider services,
        ShellDestination destination)
    {
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        window.Width = 900;
        window.Height = 600;
        window.WindowState =
            WindowState.Normal;
        window.Show();
        services
            .GetRequiredService<
                INavigationService>()
            .Navigate(destination);
        Render();
        return window;
    }

    private static void CaptureConfiguredState(
        MainWindow window,
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
        Render();
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
                $"configured-cross-app-{state}-900x600.png"),
            PngBitmapEncoderOptions.Default);
    }

    private static T ActiveView<T>(
        MainWindow window)
        where T : Control =>
        Assert.IsType<T>(
            window.FindControl<
                ContentControl>(
                "ContentHost")!
                .Content);

    private static Button FindBoundButton(
        Control root,
        System.Windows.Input.ICommand
            command,
        bool requireVisible = true) =>
        Assert.Single(
            root.GetVisualDescendants()
                .OfType<Button>(),
            button =>
                (!requireVisible ||
                 button.IsEffectivelyVisible) &&
                ReferenceEquals(
                    button.Command,
                    command));

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
            $"{action.Name ?? action.GetType().Name} was not reachable. {result.Detail}");
    }

    private static void AssertVisibleWithin(
        Control control,
        Control root,
        string identity)
    {
        Assert.True(
            UiViewportReachability
                .TryGetFullyVisibleBounds(
                    root,
                    control,
                    out _,
                    out string detail),
            $"{identity} was not visible. {detail}");
    }

    private static void AssertNoHorizontalOverflow(
        ScrollViewer scroll,
        string identity)
    {
        Assert.True(
            scroll.Viewport.Width > 0);
        Assert.True(
            scroll.Extent.Width <=
            scroll.Viewport.Width + 1,
            $"{identity} horizontally overflowed: extent {scroll.Extent.Width:0.#}, viewport {scroll.Viewport.Width:0.#}.");
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitForAsync(
        Func<bool> condition)
    {
        for (int attempt = 0;
             attempt < 120 &&
             !condition();
             attempt++)
        {
            Render();
            await Task.Delay(5);
        }

        Assert.True(condition());
    }

    private sealed class ConfiguredFixture :
        IDisposable
    {
        public ConfiguredFixture()
        {
            TempPath = Path.Combine(
                Path.GetTempPath(),
                "mlm-configured-ui-" +
                Guid.NewGuid().ToString("N"));
            RootPath = Path.Combine(
                TempPath,
                "Library");
            SourcePath = Path.Combine(
                TempPath,
                "Incoming");
            Directory.CreateDirectory(
                RootPath);
            Directory.CreateDirectory(
                SourcePath);
            string configurationPath =
                Path.Combine(
                    TempPath,
                    "library.xml");
            new EditableLibraryConfig
            {
                IndexTargets =
                [
                    new IndexTargetEntry
                    {
                        Target = RootPath,
                        IngestRole =
                            LibraryIngestRole.Cd,
                        Permissions =
                            LibraryRootPermissions
                                .All,
                        Organize = true,
                    },
                ],
            }.Save(configurationPath);
            Settings =
                new ConfiguredSettings(
                    configurationPath);
        }

        public string TempPath { get; }
        public string RootPath { get; }
        public string SourcePath { get; }
        public ConfiguredSettings Settings
        {
            get;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(
                    TempPath,
                    recursive: true);
            }
            catch (IOException)
            {
            }
            catch (
                UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class ConfiguredSettings :
        IAppSettings
    {
        private readonly Dictionary<
            string,
            string> _preferences = [];

        public ConfiguredSettings(
            string configurationPath)
        {
            ConfigPath =
                configurationPath;
            Configuration =
                new LibraryConfiguration(
                    configurationPath);
        }

        public string? ConfigPath { get; }
        public LibraryConfiguration?
            Configuration { get; }
        public event EventHandler?
            ConfigurationChanged;

        public AppConfigurationSnapshot
            GetSnapshot() =>
            new(
                ConfigPath,
                Configuration,
                1);

        public void LoadConfig(
            string path) =>
            ConfigurationChanged?.Invoke(
                this,
                EventArgs.Empty);

        public string?
            GetRememberedConfigPath() =>
            ConfigPath;

        public IReadOnlyList<string>
            RecentConfigPaths =>
            ConfigPath is null
                ? []
                : [ConfigPath];

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

    private sealed class PopulatedLibraryService(
        string rootPath) :
        ILibraryService
    {
        private readonly IReadOnlyList<
            TrackRecord> _records =
        [
            CreateTrack(
                rootPath,
                "Artist A",
                "Album A",
                "Track 1"),
            CreateTrack(
                rootPath,
                "Artist A",
                "Album A",
                "Track 2"),
            CreateTrack(
                rootPath,
                "Artist B",
                "Album B",
                "Track 3"),
        ];

        public bool IsReady => true;

        public Task<(
            int Added,
            int Modified,
            int Removed,
            int Unchanged)> IndexAsync(
            IProgress<IndexProgress>?
                progress = null,
            CancellationToken ct =
                default) =>
            Task.FromResult(
                (0, 0, 0, 3));

        public Task<LibrarySnapshot>
            BuildSnapshotAsync(
                LibraryGrouping grouping =
                    LibraryGrouping
                        .AlbumArtist,
                CancellationToken ct =
                    default) =>
            Task.FromException<
                LibrarySnapshot>(
                new NotSupportedException());

        public Task<IReadOnlyList<
            TrackRecord>>
            GetAllRecordsAsync(
                CancellationToken ct =
                    default) =>
            Task.FromResult(_records);

        public Task<IReadOnlyList<
            ScanRootHealth>>
            GetScanRootHealthAsync(
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<
                    ScanRootHealth>>(
                [
                    new(
                        rootPath,
                        ScanRootState.Healthy,
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        2,
                        2,
                        ""),
                    new(
                        rootPath + "-offline",
                        ScanRootState.Degraded,
                        DateTime.UtcNow,
                        DateTime.UtcNow
                            .AddDays(-1),
                        1,
                        1,
                        "Fixture warning"),
                ]);

        public Task<int>
            GetMaterializedArtworkFileCountAsync(
                CancellationToken ct =
                    default) =>
            Task.FromResult(2);

        public Task<AnalysisReport>
            CheckSetsAsync(
                CancellationToken ct =
                    default) =>
            Task.FromException<
                AnalysisReport>(
                new NotSupportedException());

        public Task<FileDetails?>
            GetFileDetailsAsync(
                string path,
                bool includeArtwork,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                FileDetails?>(null);

        public Task<byte[]?>
            GetFirstImageAsync(
                string path,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                byte[]?>(null);

        public Task<IReadOnlyList<
            byte[]?>>
            GetFirstImagesAsync(
                IReadOnlyList<string>
                    paths,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<
                    byte[]?>>(
                paths.Select(_ =>
                        (byte[]?)null)
                    .ToArray());

        public Task<IReadOnlyList<
            string>>
            GetImageSignaturesAsync(
                IReadOnlyList<string>
                    paths,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<string>>(
                paths.Select(_ => "")
                    .ToArray());

        private static TrackRecord CreateTrack(
            string rootPath,
            string artist,
            string album,
            string title) =>
            new()
            {
                Path = Path.Combine(
                    rootPath,
                    artist,
                    album,
                    title + ".flac"),
                Artist = artist,
                AlbumArtist = artist,
                Album = album,
                Title = title,
            };
    }

    private sealed class PopulatedPreflightService :
        IIngestPreflightService
    {
        public Task<IngestPreflightResult>
            CheckAsync(
                IngestRequest request,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                new IngestPreflightResult(
                [
                    new(
                        "Source",
                        IngestPreflightSeverity
                            .Pass,
                        "The source is readable."),
                    new(
                        "Capacity",
                        IngestPreflightSeverity
                            .Warning,
                        "Capacity will be checked again before apply."),
                ]));
    }

    private sealed class PopulatedIngestService :
        IIngestMusicService
    {
        public Task<IngestPlan>
            PreviewAsync(
                IngestRequest request,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                CreatePlan(request));

        public Task<IngestPlan>
            PreviewAsync(
                IngestRequest request,
                IProgress<IngestProgress>?
                    progress,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                CreatePlan(request));

        public Task<IngestResult>
            ApplyAsync(
                IngestPlan plan,
                IReadOnlyList<
                    IngestApprovalDecision>
                    approvals,
                IProgress<IngestProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();

        private static IngestPlan
            CreatePlan(
                IngestRequest request) =>
            new()
            {
                Request = request,
                Configuration =
                    new IngestMusicConfiguration
                    {
                        FfmpegPath =
                            "ffmpeg",
                        AacDestination = "",
                        CdDestination = "",
                        PairedCdDestination = "",
                        HighResolutionDestination =
                            "",
                        RemoveNonMusicAfterIngest =
                            true,
                        ConfiguredSourceDisposition =
                            LibrarySourceDisposition
                                .Quarantine,
                    },
                Albums = [],
                Files =
                [
                    new(
                        Path.Combine(
                            request
                                .SourceDirectory,
                            "Track 1.flac"),
                        "Lossless audio",
                        "Copy to the library"),
                    new(
                        Path.Combine(
                            request
                                .SourceDirectory,
                            "notes.txt"),
                        "Unsupported/non-audio",
                        "Quarantine with the source"),
                ],
                RequiredApprovals = [],
                Conflicts = [],
                IgnoredFiles = [],
                IgnoredFileSnapshots = [],
                SourceDirectories =
                [
                    request.SourceDirectory,
                ],
            };
    }

    private sealed class PopulatedOrganizer(
        string rootPath) :
        ILibraryOrganizer
    {
        public Task<IReadOnlyList<
            PlannedMove>>
            PreviewMovesAsync(
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<
                    PlannedMove>>(
                [
                    new(
                        Path.Combine(
                            rootPath,
                            "Track 1.flac"),
                        Path.Combine(
                            rootPath,
                            "01 Track 1.flac")),
                    new(
                        Path.Combine(
                            rootPath,
                            "Track 2.flac"),
                        Path.Combine(
                            rootPath,
                            "02 Track 2.flac")),
                ]);

        public Task<OrganizeResult>
            ApplyMovesAsync(
                IReadOnlyList<PlannedMove>
                    moves,
                IProgress<int>? progress =
                    null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();
    }

    private sealed class PopulatedDeviceSyncService :
        IDeviceSyncService
    {
        public Task<IReadOnlyList<
            DeviceSyncDevice>>
            EnumerateDevicesAsync(
                string? adbPath = null,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<
                    DeviceSyncDevice>>([]);

        public Task<
            DeviceSyncInitializationResult>
            InitializeAsync(
                DeviceSyncInitializationRequest
                    request,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();

        public Task<DeviceSyncPlan>
            PreviewAsync(
                DeviceSyncRequest request,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                new DeviceSyncPlan(
                    request,
                    "fixture-device",
                    "PLAN-DIGEST",
                    "fixture.plan",
                    [
                        new(
                            DeviceSyncMutationKind
                                .AddFile,
                            "Artist/Track 1.flac",
                            "New file",
                            false,
                            100,
                            1),
                        new(
                            DeviceSyncMutationKind
                                .UpdateFile,
                            "Artist/Track 2.flac",
                            "Metadata changed",
                            false,
                            200,
                            1),
                        new(
                            DeviceSyncMutationKind
                                .DeleteFile,
                            "Old Track.flac",
                            "Not in source",
                            false,
                            300,
                            1),
                    ],
                    0,
                    1,
                    2,
                    300,
                    Enumerable.Range(1, 12)
                        .Select(index =>
                            new OperationIssue(
                                $"fixture-warning-{index:00}",
                                OperationIssueSeverity
                                    .Warning,
                                $"Fixture issue {index:00} has enough detail to exercise the capped summary surface."))
                        .ToArray(),
                    DateTimeOffset.UtcNow));

        public Task<DeviceSyncResult>
            ApplyAsync(
                DeviceSyncPlan plan,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();

        public Task<DeviceSyncRestoreResult>
            RestoreAsync(
                DeviceSyncRestoreRequest request,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();
    }

    private sealed class PopulatedDocumentService :
        IMetadataDocumentService
    {
        public Task<MediaDocument> LoadAsync(
            string path,
            bool includeArtwork = true,
            CancellationToken ct = default)
        {
            ImmutableArray<MetadataValueSet>
                fields =
            [
                Known(TagFields.Title, "Track"),
                Known(TagFields.Artist, "Artist"),
                Known(TagFields.AlbumArtist, "Artist"),
                Known(TagFields.Album, "Album"),
                Known(TagFields.Genre, "Rock"),
                Known(TagFields.Composer, "Composer"),
                Known(TagFields.Comment, "Fixture"),
                Known(TagFields.Copyright, "2026"),
                Known(TagFields.TrackNumber, "1"),
                Known(TagFields.TotalTracks, "12"),
            ];
            return Task.FromResult(
                new MediaDocument(
                    path,
                    [
                        new(
                            "VorbisComment",
                            fields,
                            true,
                            true,
                            true,
                            true),
                    ],
                    [],
                    null,
                    new(
                        path,
                        10,
                        DateTime.UtcNow,
                        "hash"),
                    true));
        }

        private static MetadataValueSet Known(
            TagFields field,
            string value) =>
            new(
                MetadataFieldKey.Known(
                    field),
                [value]);
    }
}
