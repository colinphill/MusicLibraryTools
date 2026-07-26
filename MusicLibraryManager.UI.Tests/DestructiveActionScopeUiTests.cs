using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using iTunes.Binary;
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

/// <summary>
/// Runtime evidence for the destructive-scope contract. These tests deliberately
/// drive the same preview commands used by the shipping views; they do not infer
/// safety from XAML strings or source layout.
/// </summary>
public sealed class DestructiveActionScopeUiTests
{
    [AvaloniaFact]
    public void Health_repair_actions_are_enabled_only_beside_the_exact_reviewed_count()
    {
        using var fixture = new ConfiguredFixture();
        using ServiceProvider services = BuildServices(fixture.Settings);
        App.UseServicesForTests(services);
        AnalyzerViewModel model =
            services.GetRequiredService<AnalyzerViewModel>();
        ILocalizationService localization =
            services.GetRequiredService<ILocalizationService>();
        ILibraryService library =
            services.GetRequiredService<ILibraryService>();
        string path = Path.Combine(
                fixture.RootPath,
                "Artist",
                "Album",
                "Track.flac");
            TrackRecord track = new()
            {
                Path = path,
                Artist = "Artist",
                Album = "Album",
                Title = "Track",
            };

            var metadataRepair = new AnalysisTagRepair(
                path,
                TagFields.Title,
                "Before",
                "After",
                "Reviewed title correction",
                100,
                DateTime.UtcNow);
            var metadataItem =
                new AnalysisRepairItemViewModel(metadataRepair)
                {
                    Disposition =
                        AnalysisRepairDisposition.Active,
                };
            var metadataRun =
                AnalysisRunViewModel.ForRepairs(
                    new AnalysisRepairPlan(
                        "Metadata repair",
                        [metadataRepair]),
                    [metadataItem],
                    [track],
                    "One metadata repair");

            var fileAction =
                new RepresentationRepairAction(
                    RepresentationRepairKind.Organize,
                    path,
                    Path.Combine(
                        fixture.RootPath,
                        "Artist",
                        "Album",
                        "01 Track.flac"),
                    "Move to the reviewed canonical path");
            AnalysisRunViewModel fileRun =
                AnalysisRunViewModel
                    .ForRepresentationRepairs(
                        [fileAction],
                        [],
                        [track],
                        "One file repair");
            Assert.Single(
                    fileRun
                        .RepresentationActionItems)
                .Disposition =
                AnalysisRepairDisposition.Active;

            var itlRepair = new ItlMetadataRepairItem(
                Guid.NewGuid(),
                1,
                1,
                path,
                new ItlCachedTrackMetadata
                {
                    Artist = "Artist",
                    Album = "Album",
                    Title = "After",
                },
                DateTime.UtcNow,
                [new(
                    "Title",
                    "Before",
                    "After")]);
            var itlItem =
                new ItlMetadataRepairItemViewModel(
                    itlRepair)
                {
                    Disposition =
                        AnalysisRepairDisposition.Active,
                };
            var itlRun =
                AnalysisRunViewModel.ForItlRepairs(
                    new ItlMetadataRepairPlan(
                        "Library.itl",
                        "HASH",
                        DateTimeOffset.UtcNow,
                        [itlRepair]),
                    [itlItem],
                    "One iTunes repair");

            var artworkCandidate =
                new ArtworkRepairCandidateViewModel(
                    path,
                    "Front cover",
                    "HASH",
                    "800 × 800",
                    800,
                    800,
                    64_000,
                    library,
                    null);
            var artworkItem =
                new ArtworkRepairItemViewModel(
                    ArtworkRepairKind.NormalizeFile,
                    "Track",
                    "Normalize the front cover",
                    [path],
                    [artworkCandidate],
                    showGallery: false,
                    maximumBytes: 1_000_000,
                    maximumDimension: 1_000,
                    artist: "Artist",
                    album: "Album")
                {
                    Disposition =
                        AnalysisRepairDisposition.Active,
                };
            var artworkRun =
                AnalysisRunViewModel.ForArtwork(
                    new AnalysisReport(
                        "Artwork",
                        []),
                    [track],
                    [artworkItem],
                    "One artwork repair");

        model.Runs.Add(metadataRun);
        model.Runs.Add(fileRun);
        model.Runs.Add(itlRun);
        model.Runs.Add(artworkRun);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Width = 900;
        window.Height = 600;
        services
            .GetRequiredService<INavigationService>()
            .Navigate(ShellDestination.Health);
        Render();
        HealthView view =
            Assert.IsType<HealthView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
        try
        {
            AssertRepairScope(
                metadataRun,
                model.ApplyRepairsCommand,
                "Health.Repairs.MetadataCount");
            AssertRepairScope(
                fileRun,
                model.ApplyRepresentationRepairsCommand,
                "Health.Repairs.FileCount");
            AssertRepairScope(
                itlRun,
                model.ApplyItlMetadataRepairsCommand,
                "Health.Repairs.ItunesCount");
            AssertRepairScope(
                artworkRun,
                model.ApplyArtworkRepairsCommand,
                "Health.Artwork.SelectedCount");
        }
        finally
        {
            window.Hide();
        }

        void AssertRepairScope(
            AnalysisRunViewModel run,
            System.Windows.Input.ICommand command,
            string countResourceKey)
        {
            model.SelectedRun = null;
            Render();
            model.SelectedRun = run;
            Render();
            Assert.Same(
                run,
                model.SelectedRun);
            TabControl results =
                view.FindControl<TabControl>(
                    "HealthResultsTabs")!;
            Assert.Equal(
                model.ActiveResultIndex,
                results.SelectedIndex);

            Button[] actions =
            [
                .. view.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(button =>
                        ReferenceEquals(
                            button.Command,
                            command)),
            ];
            LocalizedFormatTextBlock[] counts =
            [
                .. view.GetVisualDescendants()
                    .OfType<
                        LocalizedFormatTextBlock>()
                    .Where(control =>
                        control.ResourceKey ==
                        countResourceKey),
            ];
            Assert.True(
                actions.Length == 1,
                $"{countResourceKey}: expected one bound action, found {actions.Length}; selected tab {results.SelectedIndex}, active result {model.ActiveResultIndex}.");
            Assert.True(
                counts.Length == 1,
                $"{countResourceKey}: expected one count control, found {counts.Length}; selected tab {results.SelectedIndex}, active result {model.ActiveResultIndex}.");
            Button action = actions[0];
            LocalizedFormatTextBlock count =
                counts[0];
            Assert.True(
                command.CanExecute(null));
            Assert.True(
                action.IsEnabled);
            AssertVisibleInViewport(
                action,
                window);
            AssertVisibleInViewport(
                count,
                window);
            Assert.Same(
                model,
                count.DataContext);
            int modelCount =
                run.View switch
                {
                    AnalysisResultView.Repairs =>
                        model.RepairItems.Count,
                    AnalysisResultView
                        .RepresentationRepairs =>
                        model
                            .RepresentationActionItems
                            .Count,
                    AnalysisResultView.ItlRepairs =>
                        model.ItlRepairItems.Count,
                    AnalysisResultView
                        .ArtworkRepairs =>
                        model.ActiveArtworkRepairCount,
                    _ => -1,
                };
            Assert.True(
                modelCount == 1,
                $"{countResourceKey}: model count was {modelCount}.");
            long boundCount =
                Convert.ToInt64(
                    count.Value,
                    System.Globalization.CultureInfo
                        .InvariantCulture);
            Assert.True(
                boundCount == 1,
                $"{countResourceKey}: bound count was {boundCount} while the model count was {modelCount}.");
            Assert.Equal(
                localization.FormatCount(
                    countResourceKey,
                    1),
                count.Text);

            AnalysisRunViewModel empty =
                EmptyRunFor(
                    run.View);
            model.SelectedRun = null;
            Render();
            model.Runs.Insert(
                0,
                empty);
            Render();
            model.SelectedRun = empty;
            Render();
            Assert.Same(
                empty,
                model.SelectedRun);
            Assert.Equal(
                model.ActiveResultIndex,
                view.FindControl<TabControl>(
                        "HealthResultsTabs")!
                    .SelectedIndex);

            actions =
            [
                .. view.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(button =>
                        ReferenceEquals(
                            button.Command,
                            command)),
            ];
            counts =
            [
                .. view.GetVisualDescendants()
                    .OfType<
                        LocalizedFormatTextBlock>()
                    .Where(control =>
                        control.ResourceKey ==
                        countResourceKey),
            ];
            Assert.True(
                actions.Length == 1,
                $"{countResourceKey} zero state: expected one bound action, found {actions.Length}.");
            Assert.True(
                counts.Length == 1,
                $"{countResourceKey} zero state: expected one count control, found {counts.Length}.");
            action = actions[0];
            count = counts[0];
            Assert.False(
                command.CanExecute(null),
                $"{countResourceKey}: command remained enabled for the selected empty run '{model.SelectedRun?.Name}'.");
            Assert.False(
                action.IsEffectivelyEnabled,
                $"{countResourceKey}: action remained enabled for the selected empty run '{model.SelectedRun?.Name}'.");
            AssertVisibleInViewport(
                count,
                window);
            Assert.Equal(
                localization.FormatCount(
                    countResourceKey,
                    0),
                count.Text);
            model.SelectedRun = null;
            model.Runs.Remove(empty);
            Render();
        }
    }

    [AvaloniaFact]
    public async Task Devices_apply_is_enabled_only_with_a_visible_action_count_and_blockers_explain_invalid_plans()
    {
        using var fixture = new ConfiguredFixture();
        var sync = new DeviceSyncStub();
        using ServiceProvider services = BuildServices(
            fixture.Settings,
            collection =>
                collection.AddSingleton<
                    IDeviceSyncService>(sync));
        App.UseServicesForTests(services);
        DevicesViewModel model =
            services.GetRequiredService<
                DevicesViewModel>();
        var view = new DevicesView();
        ILocalizationService localization =
            services.GetRequiredService<
                ILocalizationService>();
        var window = Show(view);
        try
        {
            await WaitForAsync(
                () => !model.IsLoadingDevices);
            model.SourcePath =
                fixture.RootPath;
            model.DestinationPath =
                "Music";
            sync.BlockPreview = false;

            await model.PreviewCommand
                .ExecuteAsync(null);
            Render();

            Button apply =
                view.FindControl<Button>(
                    "ApplyButton")!;
            LocalizedFormatTextBlock count =
                Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<
                            LocalizedFormatTextBlock>(),
                    control =>
                        control.ResourceKey ==
                        "Devices.PlannedCount");
            Assert.True(
                model.ApplyCommand
                    .CanExecute(null));
            Assert.True(
                apply.IsEnabled);
            AssertVisibleInViewport(
                apply,
                window);
            AssertVisibleInViewport(
                count,
                window);
            Assert.Equal(
                localization.FormatCount(
                    "Devices.PlannedCount",
                    2),
                count.Text);

            sync.BlockPreview = true;
            model.SourcePath =
                fixture.RootPath +
                Path.DirectorySeparatorChar;
            await model.PreviewCommand
                .ExecuteAsync(null);
            Render();

            Assert.False(
                model.ApplyCommand
                    .CanExecute(null));
            Assert.False(
                apply.IsEffectivelyVisible);
            Assert.Equal(
                localization.FormatCount(
                    "Devices.PlannedCount",
                    0),
                count.Text);
            TextBlock status =
                FindVisibleText(
                    view,
                    model.StatusText);
            TextBlock diagnostic =
                FindVisibleText(
                    view,
                    DeviceSyncStub
                        .BlockerDetail);
            AssertVisibleInViewport(
                status,
                window);
            AssertVisibleInViewport(
                diagnostic,
                window);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Ingest_apply_is_enabled_only_with_visible_reviewed_summary_counts_and_conflicts_explain_blocking()
    {
        using var fixture = new ConfiguredFixture();
        var ingest = new IngestServiceStub();
        using ServiceProvider services = BuildServices(
            fixture.Settings,
            collection =>
                collection.AddSingleton<
                    IIngestMusicService>(ingest));
        App.UseServicesForTests(services);
        IngestViewModel model =
            services.GetRequiredService<
                IngestViewModel>();
        var view = new IngestView();
        ILocalizationService localization =
            services.GetRequiredService<
                ILocalizationService>();
        var window = Show(view);
        try
        {
            model.SourceDirectory =
                fixture.SourcePath;
            ingest.BlockPreview = false;

            await model.PreviewCommand
                .ExecuteAsync(null);
            Render();

            Button apply = FindBoundButton(
                view,
                model.ApplyCommand);
            Assert.True(
                model.ApplyCommand
                    .CanExecute(null));
            Assert.True(
                apply.IsEnabled);
            AssertVisibleInViewport(
                apply,
                window);
            AssertSummary(
                "Ingest.Summary.OutputCount",
                0);
            AssertSummary(
                "Ingest.Summary.ConflictCount",
                0);
            AssertSummary(
                "Ingest.Summary.CleanupCount",
                1);

            ingest.BlockPreview = true;
            await model.PreviewCommand
                .ExecuteAsync(null);
            Render();

            Assert.False(
                model.ApplyCommand
                    .CanExecute(null));
            Assert.False(
                apply.IsEffectivelyVisible);
            AssertSummary(
                "Ingest.Summary.ConflictCount",
                1);
            TextBlock explanation =
                FindVisibleText(
                    view,
                    localization.FormatCount(
                        "Ingest.Status.PreviewConflicts",
                        1));
            AssertVisibleInViewport(
                explanation,
                window);
        }
        finally
        {
            window.Hide();
        }

        void AssertSummary(
            string key,
            long expected)
        {
            LocalizedFormatTextBlock count =
                Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<
                            LocalizedFormatTextBlock>(),
                    control =>
                        control.ResourceKey ==
                        key);
            AssertVisibleInViewport(
                count,
                window);
            Assert.Equal(
                localization.FormatCount(
                    key,
                    expected),
                count.Text);
        }
    }

    [AvaloniaFact]
    public async Task Organize_apply_is_enabled_only_beside_the_exact_move_count_and_zero_preview_is_explained()
    {
        using var fixture = new ConfiguredFixture();
        var organizer =
            new OrganizerStub(
                fixture.RootPath);
        using ServiceProvider services = BuildServices(
            fixture.Settings,
            collection =>
                collection.AddSingleton<
                    ILibraryOrganizer>(
                    organizer));
        App.UseServicesForTests(services);
        OrganizeViewModel model =
            services.GetRequiredService<
                OrganizeViewModel>();
        var view = new OrganizeView();
        ILocalizationService localization =
            services.GetRequiredService<
                ILocalizationService>();
        var window = Show(view);
        try
        {
            organizer.ReturnMoves = true;
            await model.PreviewCommand
                .ExecuteAsync(null);
            Render();

            Button apply = FindBoundButton(
                view,
                model.ApplyCommand);
            LocalizedFormatTextBlock count =
                view.FindControl<
                    LocalizedFormatTextBlock>(
                    "PlannedCount")!;
            Assert.True(
                model.ApplyCommand
                    .CanExecute(null));
            Assert.True(
                apply.IsEnabled);
            AssertVisibleInViewport(
                apply,
                window);
            AssertVisibleInViewport(
                count,
                window);
            Assert.Equal(
                localization.FormatCount(
                    "Organize.PlannedCount",
                    2),
                count.Text);

            organizer.ReturnMoves = false;
            await model.PreviewCommand
                .ExecuteAsync(null);
            Render();

            Assert.False(
                model.ApplyCommand
                    .CanExecute(null));
            Assert.False(
                apply.IsEffectivelyVisible);
            Assert.Equal(
                localization.FormatCount(
                    "Organize.PlannedCount",
                    0),
                count.Text);
            TextBlock explanation =
                FindVisibleText(
                    view,
                    model.StatusText!);
            AssertVisibleInViewport(
                explanation,
                window);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    explanation.Text));
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Operations_restore_and_purge_actions_keep_the_reviewed_scope_visible_and_disable_empty_plans()
    {
        using var fixture = new ConfiguredFixture();
        var journals =
            new JournalServiceStub(
                fixture.RootPath);
        using ServiceProvider services = BuildServices(
            fixture.Settings,
            collection =>
                collection.AddSingleton<
                    IOperationJournalService>(
                    journals));
        App.UseServicesForTests(services);
        OperationsViewModel model =
            services.GetRequiredService<
                OperationsViewModel>();
        ILocalizationService localization =
            services.GetRequiredService<
                ILocalizationService>();
        OperationJournalSummary summary =
            journals.Summary;
        var run =
            new OperationRunViewModel(
                summary,
                localization);
        model.Runs.Add(run);
        model.SelectedRun = run;
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Width = 900;
        window.Height = 600;
        services
            .GetRequiredService<INavigationService>()
            .Navigate(ShellDestination.Operations);
        Render();
        OperationsView view =
            Assert.IsType<OperationsView>(
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
        try
        {
            TabControl tabs =
                view.GetVisualDescendants()
                    .OfType<TabControl>()
                    .Single();
            tabs.SelectedIndex = 2;
            Render();

            await model.OpenRunCommand
                .ExecuteAsync(run);
            model.SelectAllRestorableCommand
                .Execute(null);
            journals.EmptyRestore = false;
            await model.PreviewRestoreCommand
                .ExecuteAsync(null);
            Render();

            Button restore = FindBoundButton(
                view,
                model.ApplyRestoreCommand);
            TextBlock restoreScope =
                FindVisibleText(
                    view,
                    localization.FormatCount(
                        "Operations.RestorePreview.Ready",
                        2,
                        0,
                        0));
            Assert.True(
                model.ApplyRestoreCommand
                    .CanExecute(null));
            Assert.True(
                restore.IsEnabled);
            AssertVisibleInViewport(
                restore,
                window);
            AssertVisibleInViewport(
                restoreScope,
                window);

            model.ClearRestoreSelectionCommand
                .Execute(null);
            model.SelectAllRestorableCommand
                .Execute(null);
            journals.EmptyRestore = true;
            await model.PreviewRestoreCommand
                .ExecuteAsync(null);
            Render();

            Assert.False(
                model.ApplyRestoreCommand
                    .CanExecute(null));
            TextBlock noRestore =
                FindVisibleText(
                    view,
                    localization.Get(
                        "Operations.RestorePreview.None"));
            AssertVisibleInViewport(
                noRestore,
                window);

            journals.EmptyPurge = false;
            await model.PreviewPurgeCommand
                .ExecuteAsync(null);
            Render();

            string eligible =
                localization.FormatCount(
                    "Operations.PurgePreview.Eligible",
                    1,
                    1,
                    "64 B");
            string expectedPurge =
                localization.Format(
                    "Operations.PurgePreview.Summary",
                    eligible,
                    "",
                    0,
                    0,
                    "");
            TextBlock purgeScope =
                FindVisibleText(
                    view,
                    expectedPurge);
            Button maintenance =
                view.FindControl<Button>(
                    "RecoveryMaintenanceButton")!;
            MenuFlyout maintenanceMenu =
                Assert.IsType<MenuFlyout>(
                    maintenance.Flyout);
            MenuItem purge =
                maintenanceMenu.Items
                    .OfType<MenuItem>()
                    .Single(item =>
                        ReferenceEquals(
                            item.Command,
                            model
                                .ApplyPurgeCommand));
            maintenanceMenu.ShowAt(
                maintenance);
            Render();

            Assert.True(
                model.ApplyPurgeCommand
                    .CanExecute(null));
            Assert.True(
                purge.IsEnabled);
            Assert.True(
                purge.IsEffectivelyVisible);
            AssertVisibleInViewport(
                purgeScope,
                window);
            maintenanceMenu.Hide();

            journals.EmptyPurge = true;
            await model.PreviewPurgeCommand
                .ExecuteAsync(null);
            maintenanceMenu.ShowAt(
                maintenance);
            Render();

            Assert.False(
                model.ApplyPurgeCommand
                    .CanExecute(null));
            TextBlock noPurge =
                FindVisibleText(
                    view,
                    model.StatusText);
            AssertVisibleInViewport(
                noPurge,
                window);
            Assert.Contains(
                localization.Get(
                    "Operations.PurgePreview.NoneEligible"),
                noPurge.Text,
                StringComparison.Ordinal);
            maintenanceMenu.Hide();
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Settings_removal_actions_keep_root_or_profile_identity_visible_and_built_ins_cannot_be_deleted()
    {
        using var fixture = new ConfiguredFixture();
        using ServiceProvider services =
            BuildServices(fixture.Settings);
        App.UseServicesForTests(services);
        SettingsViewModel model =
            services.GetRequiredService<
                SettingsViewModel>();
        ILocalizationService localization =
            services.GetRequiredService<
                ILocalizationService>();
        var view = new SettingsView();
        var window = Show(view);
        try
        {
            model.SelectedTabIndex = 1;
            Render();
            IndexTargetEditorRow root =
                Assert.Single(
                    model.IndexTargets);
            Button removeRoot =
                view.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(button =>
                        ReferenceEquals(
                            button.Command,
                            model
                                .RemoveIndexTargetCommand));
            TextBox rootIdentity =
                view.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Single(text =>
                        ReferenceEquals(
                            text.DataContext,
                            root) &&
                        string.Equals(
                            text.Text,
                            root.Path,
                            StringComparison.Ordinal));
            Assert.True(
                removeRoot.IsEnabled);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    rootIdentity.Text));
            AssertVisibleInViewport(
                removeRoot,
                window);
            AssertVisibleInViewport(
                rootIdentity,
                window);

            model.CreateLibraryProfileCommand
                .Execute(null);
            LibraryProfile customLibraryProfile =
                Assert.IsType<LibraryProfile>(
                    model.SelectedLibraryProfile);
            model.SelectedTabIndex = 5;
            Render();
            ComboBox libraryPicker =
                Assert.IsType<ComboBox>(
                    view.FindControl<ComboBox>(
                        "ProfilePresetPicker"));
            Button libraryMore =
                Assert.IsType<Button>(
                    view.FindControl<Button>(
                        "LibraryProfileMoreButton"));
            MenuFlyout libraryMenu =
                Assert.IsType<MenuFlyout>(
                    libraryMore.Flyout);
            MenuItem deleteLibrary =
                Assert.Single(
                    libraryMenu.Items
                        .OfType<MenuItem>());
            libraryMenu.ShowAt(
                libraryMore);
            Render();

            Assert.True(
                model.DeleteLibraryProfileCommand
                    .CanExecute(null));
            Assert.True(
                deleteLibrary.IsEnabled);
            Assert.True(
                deleteLibrary
                    .IsEffectivelyVisible);
            Assert.Equal(
                customLibraryProfile,
                libraryPicker.SelectedItem);
            AssertVisibleInViewport(
                libraryPicker,
                window);
            AssertVisibleSelectedIdentity(
                libraryPicker,
                customLibraryProfile.Name);
            libraryMenu.Hide();

            model.SelectedLibraryProfile =
                model.LibraryProfiles
                    .First(profile =>
                        profile.Preset !=
                        LibraryProfilePreset.Custom);
            Render();
            Assert.False(
                model.DeleteLibraryProfileCommand
                    .CanExecute(null));
            TextBlock builtInExplanation =
                FindVisibleText(
                    view,
                    localization.Get(
                        "Settings.RootPolicy.BuiltInDescription"));
            AssertVisibleInViewport(
                builtInExplanation,
                window);

            model.SelectedTabIndex = 6;
            Render();
            ComboBox ingestPicker =
                Assert.IsType<ComboBox>(
                    view.FindControl<ComboBox>(
                        "IngestProfilePicker"));
            Button ingestMore =
                Assert.IsType<Button>(
                    view.FindControl<Button>(
                        "IngestProfileMoreButton"));
            MenuFlyout ingestMenu =
                Assert.IsType<MenuFlyout>(
                    ingestMore.Flyout);
            MenuItem deleteIngest =
                Assert.Single(
                    ingestMenu.Items
                        .OfType<MenuItem>());

            LibraryIngestProfile builtInIngest =
                Assert.IsType<
                    LibraryIngestProfile>(
                    ingestPicker.SelectedItem);
            Assert.False(
                model.DeleteIngestProfileCommand
                    .CanExecute(null));
            AssertVisibleInViewport(
                ingestPicker,
                window);
            AssertVisibleSelectedIdentity(
                ingestPicker,
                builtInIngest.Name);
            TextBlock ingestDeletionHelp =
                Assert.IsType<TextBlock>(
                    view.FindControl<TextBlock>(
                        "IngestProfileDeletionHelp"));
            Assert.Equal(
                localization.Get(
                    "Settings.Ingest.ProfileDeletionDescription"),
                ingestDeletionHelp.Text);
            Assert.True(
                ingestDeletionHelp
                    .IsEffectivelyVisible);
            AssertVisibleInViewport(
                ingestDeletionHelp,
                window);
            Border ingestProfileCard =
                ingestMore.GetVisualAncestors()
                    .OfType<Border>()
                    .First(border =>
                        border.Classes.Contains(
                            "card"));
            Assert.Contains(
                ingestDeletionHelp,
                ingestProfileCard
                    .GetVisualDescendants());

            model.CreateIngestProfileCommand
                .Execute(null);
            Render();
            LibraryIngestProfile customIngestProfile =
                Assert.IsType<
                    LibraryIngestProfile>(
                    model
                        .SelectedIngestProfile);
            ingestMenu.ShowAt(
                ingestMore);
            Render();

            Assert.True(
                model.DeleteIngestProfileCommand
                    .CanExecute(null));
            Assert.True(
                deleteIngest.IsEnabled);
            Assert.True(
                deleteIngest
                    .IsEffectivelyVisible);
            Assert.Equal(
                customIngestProfile,
                ingestPicker.SelectedItem);
            AssertVisibleInViewport(
                ingestPicker,
                window);
            AssertVisibleSelectedIdentity(
                ingestPicker,
                customIngestProfile.Name);
            ingestMenu.Hide();
        }
        finally
        {
            window.Hide();
        }
    }

    private static AnalysisRunViewModel
        EmptyRunFor(
            AnalysisResultView view) =>
        view switch
        {
            AnalysisResultView.Repairs =>
                AnalysisRunViewModel.ForRepairs(
                    new AnalysisRepairPlan(
                        "No metadata repairs",
                        []),
                    [],
                    [],
                    "No metadata repairs"),
            AnalysisResultView
                .RepresentationRepairs =>
                AnalysisRunViewModel
                    .ForRepresentationRepairs(
                        [],
                        [],
                        [],
                        "No file repairs"),
            AnalysisResultView.ItlRepairs =>
                AnalysisRunViewModel.ForItlRepairs(
                    new ItlMetadataRepairPlan(
                        "Library.itl",
                        "HASH",
                        DateTimeOffset.UtcNow,
                        []),
                    [],
                    "No iTunes repairs"),
            AnalysisResultView.ArtworkRepairs =>
                AnalysisRunViewModel.ForArtwork(
                    new AnalysisReport(
                        "Artwork",
                        []),
                    [],
                    [],
                    "No artwork repairs"),
            _ => throw new
                ArgumentOutOfRangeException(
                    nameof(view),
                    view,
                    "The test only models repair destinations."),
        };

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

    private static Window Show(
        Control view)
    {
        var window = new Window
        {
            Width = 1440,
            Height = 900,
            Content = view,
        };
        window.Show();
        Render();
        return window;
    }

    private static Button FindBoundButton(
        Control root,
        System.Windows.Input.ICommand
            command) =>
        Assert.Single(
            root.GetVisualDescendants()
                .OfType<Button>(),
            button =>
                button.IsEffectivelyVisible &&
                ReferenceEquals(
                    button.Command,
                    command));

    private static TextBlock FindVisibleText(
        Control root,
        string expected) =>
        Assert.Single(
            root.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text =>
                    text.IsEffectivelyVisible &&
                    string.Equals(
                        text.Text,
                        expected,
                        StringComparison.Ordinal))
                .Take(1));

    private static void
        AssertVisibleSelectedIdentity(
            ComboBox picker,
            string expected)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(
                expected));
        Assert.Contains(
            picker.GetVisualDescendants()
                .OfType<TextBlock>(),
            text =>
                text.IsEffectivelyVisible &&
                string.Equals(
                    text.Text,
                    expected,
                    StringComparison.Ordinal));
    }

    private static void
        AssertVisibleInViewport(
            Control control,
            Window window)
    {
        Assert.True(
            control.IsEffectivelyVisible,
            $"{control.Name ?? control.GetType().Name} is not effectively visible.");
        Assert.True(
            control.Bounds.Width > 0 &&
            control.Bounds.Height > 0,
            $"{control.Name ?? control.GetType().Name} has no arranged bounds.");
        Avalonia.Point? origin =
            control.TranslatePoint(
                default,
                window);
        Assert.NotNull(
            origin);
        var bounds = new Avalonia.Rect(
            origin.Value,
            control.Bounds.Size);
        Assert.True(
            bounds.Left >= -1 &&
            bounds.Top >= -1 &&
            bounds.Right <=
            window.Bounds.Width + 1 &&
            bounds.Bottom <=
            window.Bounds.Height + 1,
            $"{control.Name ?? control.GetType().Name} {bounds} is outside the effective {window.Bounds.Size} viewport.");
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
             attempt < 100 &&
             !condition();
             attempt++)
        {
            Render();
            await Task.Delay(5);
        }

        Assert.True(
            condition());
    }

    private sealed class ConfiguredFixture :
        IDisposable
    {
        public ConfiguredFixture()
        {
            TempPath = Path.Combine(
                Path.GetTempPath(),
                "mlm-destructive-scope-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(
                TempPath);
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
            var editable =
                new EditableLibraryConfig
                {
                    IndexTargets =
                    [
                        new IndexTargetEntry
                        {
                            Target =
                                RootPath,
                            IngestRole =
                                LibraryIngestRole.Cd,
                            Permissions =
                                LibraryRootPermissions
                                    .All,
                            Organize = true,
                        },
                    ],
                };
            editable.Save(
                configurationPath);
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

    private sealed class DeviceSyncStub :
        IDeviceSyncService
    {
        public const string BlockerDetail =
            "The fixture destination is intentionally blocked.";

        public bool BlockPreview { get; set; }

        public Task<
            IReadOnlyList<DeviceSyncDevice>>
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
                    default)
        {
            DeviceSyncPlan plan =
                BlockPreview
                    ? new(
                        request,
                        "fixture-device",
                        "",
                        "",
                        [],
                        0,
                        0,
                        0,
                        0,
                        [
                            new OperationIssue(
                                "fixture-blocker",
                                OperationIssueSeverity
                                    .Blocker,
                                BlockerDetail),
                        ],
                        DateTimeOffset
                            .UtcNow)
                    : new(
                        request,
                        "fixture-device",
                        "PLAN-DIGEST",
                        "fixture.plan",
                        [
                            new DeviceSyncAction(
                                DeviceSyncMutationKind
                                    .AddFile,
                                "Artist/Track 1.flac",
                                "New file",
                                false,
                                100,
                                1),
                            new DeviceSyncAction(
                                DeviceSyncMutationKind
                                    .DeleteFile,
                                "Old Track.flac",
                                "Not in source",
                                false,
                                200,
                                1),
                        ],
                        0,
                        1,
                        1,
                        100,
                        [],
                        DateTimeOffset
                            .UtcNow);
            return Task.FromResult(
                plan);
        }

        public Task<DeviceSyncResult>
            ApplyAsync(
                DeviceSyncPlan plan,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();

        public Task<
            DeviceSyncRestoreResult>
            RestoreAsync(
                DeviceSyncRestoreRequest request,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();
    }

    private sealed class IngestServiceStub :
        IIngestMusicService
    {
        public bool BlockPreview { get; set; }

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

        private IngestPlan CreatePlan(
            IngestRequest request)
        {
            var configuration =
                new IngestMusicConfiguration
                {
                    FfmpegPath =
                        "ffmpeg",
                    AacDestination =
                        "",
                    CdDestination =
                        "",
                    PairedCdDestination =
                        "",
                    HighResolutionDestination =
                        "",
                    RemoveNonMusicAfterIngest =
                        true,
                    ConfiguredSourceDisposition =
                        LibrarySourceDisposition
                            .Quarantine,
                };
            return new IngestPlan
            {
                Request = request,
                Configuration =
                    configuration,
                Albums = [],
                Files = [],
                RequiredApprovals = [],
                Conflicts =
                    BlockPreview
                        ? [
                            new IngestConflict(
                                "fixture-album",
                                Path.Combine(
                                    request
                                        .SourceDirectory,
                                    "conflict.flac"),
                                "The reviewed destination conflicts with an existing file."),
                        ]
                        : [],
                IgnoredFiles = [],
                IgnoredFileSnapshots = [],
                SourceDirectories =
                    BlockPreview
                        ? []
                        : [
                            request
                                .SourceDirectory,
                        ],
            };
        }
    }

    private sealed class OrganizerStub(
        string rootPath) :
        ILibraryOrganizer
    {
        public bool ReturnMoves { get; set; }

        public Task<
            IReadOnlyList<PlannedMove>>
            PreviewMovesAsync(
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<
                    PlannedMove>>(
                ReturnMoves
                    ? [
                        new PlannedMove(
                            Path.Combine(
                                rootPath,
                                "Track 1.flac"),
                            Path.Combine(
                                rootPath,
                                "01 Track 1.flac")),
                        new PlannedMove(
                            Path.Combine(
                                rootPath,
                                "Track 2.flac"),
                            Path.Combine(
                                rootPath,
                                "02 Track 2.flac")),
                    ]
                    : []);

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

    private sealed class JournalServiceStub(
        string rootPath) :
        IOperationJournalService
    {
        public OperationJournalSummary
            Summary { get; } =
            new(
                "Fixture ingest",
                OperationJournalKind.Ingest,
                OperationJournalState
                    .Completed,
                Path.Combine(
                    rootPath,
                    "recovery-run"),
                Path.Combine(
                    rootPath,
                    "recovery-run",
                    "journal.tsv"),
                DateTimeOffset.UtcNow
                    .AddDays(-120),
                1);

        public bool EmptyRestore { get; set; }
        public bool EmptyPurge { get; set; }

        public Task<
            OperationJournalDiscoveryResult>
            DiscoverAsync(
                IReadOnlyList<string>
                    searchRoots,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                new OperationJournalDiscoveryResult(
                    [Summary],
                    []));

        public Task<OperationBrowseResult>
            BrowseAsync(
                OperationJournalSummary run,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                new OperationBrowseResult(
                    rootPath,
                    [
                        new OperationFileEntry(
                            Path.Combine(
                                rootPath,
                                "Artist",
                                "Track.flac"),
                            Path.Combine(
                                rootPath,
                                "recovery-run",
                                "Track.flac"),
                            Path.Combine(
                                "Artist",
                                "Track.flac"),
                            OperationEntryKind
                                .Quarantined,
                            Exists: true,
                            IsDirectory: false),
                    ],
                    []));

        public Task<OperationRestorePlan>
            PreviewRestoreAsync(
                OperationJournalSummary run,
                IReadOnlyList<
                    OperationFileEntry>
                    entries,
                CancellationToken ct =
                    default)
        {
            IReadOnlyList<
                OperationRestoreAction>
                actions =
                EmptyRestore
                    ? []
                    : [
                        RestoreAction(
                            "Track.flac"),
                        RestoreAction(
                            "Cover.jpg"),
                    ];
            return Task.FromResult(
                new OperationRestorePlan(
                    run,
                    Path.Combine(
                        rootPath,
                        "restore.tsv"),
                    actions,
                    0));
        }

        public Task<OperationRestoreResult>
            ApplyRestoreAsync(
                OperationRestorePlan plan,
                IProgress<int>? progress =
                    null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();

        public Task<OperationPurgePlan>
            PreviewPurgeAsync(
                IReadOnlyList<
                    OperationJournalSummary>
                    runs,
                int retentionDays,
                DateTimeOffset? nowUtc =
                    null,
                CancellationToken ct =
                    default)
        {
            IReadOnlyList<
                OperationPurgeRun>
                purgeRuns =
                EmptyPurge
                    ? []
                    : [
                        new OperationPurgeRun(
                            Summary,
                            Path.Combine(
                                rootPath,
                                "purge-stage"),
                            [
                                new OperationPurgeManifestEntry(
                                    "Track.flac",
                                    IsDirectory:
                                        false,
                                    IsReparsePoint:
                                        false,
                                    Length: 64,
                                    LastWriteTimeUtc:
                                        DateTime
                                            .UtcNow),
                            ]),
                    ];
            return Task.FromResult(
                new OperationPurgePlan(
                    retentionDays,
                    DateTimeOffset.UtcNow
                        .AddDays(
                            -retentionDays),
                    purgeRuns,
                    0,
                    0,
                    0));
        }

        public Task<OperationPurgeResult>
            ApplyPurgeAsync(
                OperationPurgePlan plan,
                IProgress<int>? progress =
                    null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();

        private OperationRestoreAction
            RestoreAction(
                string fileName) =>
            new(
                Path.Combine(
                    rootPath,
                    "recovery-run",
                    fileName),
                Path.Combine(
                    rootPath,
                    fileName),
                Path.Combine(
                    rootPath,
                    "restore-backup",
                    fileName),
                new OperationPathSnapshot(
                    true,
                    false,
                    64,
                    DateTime.UtcNow),
                OperationPathSnapshot.Missing(
                    Path.Combine(
                        rootPath,
                        fileName)),
                OperationEntryKind
                    .Quarantined);
    }
}
