using MusicLibraryManager.Presentation;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class OperationsViewModelTests
{
    [Fact]
    public void ConfigurationDependentJobsDoNotExposePerRunConfigurationOverrides()
    {
        using var temp = new TempDirectory();
        string library = Path.Combine(temp.Path, "iTunes Library.itl");
        string configPath = Path.Combine(temp.Path, "library.xml");
        new EditableLibraryConfig { ItunesLibraryPath = library }.Save(configPath);
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        settings.LoadConfig(configPath);
        var jobs = new UnifiedJobService();

        var viewModel = new OperationsViewModel(
            new RecordingJournals(new([], [])), new StubFiles(), new StubDialogs(), settings, jobs)
        {
            SelectedJob = jobs.Catalog.Single(job => job.Id == "playlist-sync"),
        };

        Assert.Equal("playlist-sync", viewModel.SelectedJob!.Id);
    }

    [Fact]
    public async Task UnifiedJobRequiresPreviewAndPersistsPreviewAndApplyHistory()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        var jobs = new UnifiedJobService();
        var crossSync = new StubCrossLibrarySyncService();
        var viewModel = new OperationsViewModel(
            new RecordingJournals(new([], [])), new StubFiles(), new StubDialogs(), settings,
            jobs, crossSync)
        {
            SelectedJob = jobs.Catalog.Single(job => job.Id == "cross-library-sync"),
        };

        Assert.False(viewModel.ShowRemovalLimit);

        Assert.False(viewModel.ApplyJobCommand.CanExecute(null));
        await viewModel.PreviewJobCommand.ExecuteAsync(null);
        Assert.True(viewModel.ApplyJobCommand.CanExecute(null));
        await viewModel.ApplyJobCommand.ExecuteAsync(null);

        Assert.Equal(1, crossSync.PreviewCalls);
        Assert.Equal(new CrossLibrarySyncRequest(null, null),
            crossSync.LastRequest);
        Assert.Equal(1, crossSync.ApplyCalls);
        Assert.Same(crossSync.PreviewedPlan, crossSync.AppliedPlan);
        Assert.Equal(2, viewModel.JobHistory.Count);
        Assert.Equal("Applied", viewModel.JobHistory[0].State);
    }

    [Fact]
    public async Task UnifiedJobApplyRequiresARecoverySummaryConfirmation()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        var jobs = new UnifiedJobService();
        var crossSync = new StubCrossLibrarySyncService();
        var dialogs = new StubDialogs { ConfirmApplyResult = false };
        var viewModel = new OperationsViewModel(
            new RecordingJournals(new([], [])), new StubFiles(), dialogs, settings,
            jobs, crossSync)
        {
            SelectedJob = jobs.Catalog.Single(job => job.Id == "cross-library-sync"),
        };

        await viewModel.PreviewJobCommand.ExecuteAsync(null);
        await viewModel.ApplyJobCommand.ExecuteAsync(null);

        Assert.Equal(0, crossSync.ApplyCalls);
        Assert.True(viewModel.ApplyJobCommand.CanExecute(null));
        Assert.Contains("0 planned file mutation", dialogs.ApplyMessage);
        Assert.Contains("Recovery is available", dialogs.ApplyMessage);
    }

    [Fact]
    public async Task ConfiguredExportJobUsesTypedPreviewAndApplyService()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        var descriptor = new UnifiedJobDescriptor(
            UnifiedJobService.ConfiguredExportJobPrefix + "portable",
            "Export: Portable", "Configured export", UnifiedJobApplyMode.ApplyFlag,
            [], "", 0);
        var jobs = new StubJobService(descriptor);
        var export = new StubConfiguredExportService();
        var dialogs = new StubDialogs();
        var viewModel = new OperationsViewModel(
            new RecordingJournals(new([], [])), new StubFiles(), dialogs, settings,
            jobs: jobs, configuredExport: export)
        {
            SelectedJob = descriptor,
        };

        await viewModel.PreviewJobCommand.ExecuteAsync(null);

        Assert.Equal("portable", export.LastRequest?.ProfileId);
        Assert.True(viewModel.ApplyJobCommand.CanExecute(null));
        Assert.Contains("1 desired", viewModel.JobOutput);

        await viewModel.ApplyJobCommand.ExecuteAsync(null);

        Assert.Equal(1, export.ApplyCalls);
        Assert.Contains("1 copied", viewModel.JobOutput);
        Assert.Contains("Recovery is available", dialogs.ApplyMessage);
    }

    [Fact]
    public async Task ArtworkNormalizationPublishesUpdatedPathsAndCacheWarning()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        string library = Path.Combine(temp.Path, "iTunes Library.itl");
        string config = Path.Combine(temp.Path, "library.xml");
        new EditableLibraryConfig { ItunesLibraryPath = library }.Save(config);
        settings.LoadConfig(config);
        var jobs = new UnifiedJobService();
        var normalization = new StubArtworkNormalizationService(
            [Path.Combine(temp.Path, "one.mp3"), Path.Combine(temp.Path, "two.m4a")],
            "cache unavailable");
        var viewModel = new OperationsViewModel(
            new RecordingJournals(new([], [])),
            new StubFiles(),
            new StubDialogs(),
            settings,
            jobs: jobs,
            artworkNormalization: normalization)
        {
            SelectedJob = jobs.Catalog.Single(job => job.Id == "artwork-normalization"),
            JobPlaylistName = "Artwork",
        };
        IReadOnlyList<string>? affected = null;
        viewModel.ArtworkNormalized += paths => affected = paths;

        await viewModel.PreviewJobCommand.ExecuteAsync(null);
        await viewModel.ApplyJobCommand.ExecuteAsync(null);

        Assert.Equal(normalization.Paths, affected);
        Assert.Contains("Cache warning: cache unavailable", viewModel.JobOutput);
    }

    [Fact]
    public async Task RefreshIncludesRememberedIngestAndAdditionalRootsAndProjectsRuns()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        string ingest = Path.Combine(temp.Path, "incoming");
        string device = Path.Combine(temp.Path, "device");
        settings.SetPreference("manager.ingest.source.v1", ingest);
        var summary = new OperationJournalSummary(
            "UpdateCarCard", OperationJournalKind.Device, OperationJournalState.Interrupted,
            Path.Combine(device, "run"), Path.Combine(device, "run", "journal.tsv"),
            DateTimeOffset.UtcNow, 12);
        var journals = new RecordingJournals(new([summary], []));
        var viewModel = new OperationsViewModel(journals, new StubFiles(), new StubDialogs(), settings)
        {
            SearchRoot = device,
        };

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Contains(ingest, journals.Roots!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(device, journals.Roots!, StringComparer.OrdinalIgnoreCase);
        var run = Assert.Single(viewModel.Runs);
        Assert.True(run.IsInterrupted);
        Assert.Contains("12", run.AffectedItems);
        Assert.Contains("1 operation run", viewModel.StatusText);
    }

    [Fact]
    public async Task RefreshReportsWarningsWithoutDroppingDiscoveredRuns()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        var summary = new OperationJournalSummary(
            "IngestMusic", OperationJournalKind.Ingest, OperationJournalState.Completed,
            temp.Path, null, DateTimeOffset.UtcNow, null);
        var journals = new RecordingJournals(new([summary], ["offline"]));
        var viewModel = new OperationsViewModel(journals, new StubFiles(), new StubDialogs(), settings)
        {
            SearchRoot = temp.Path,
        };

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Runs);
        Assert.Contains("1 root(s) could not be scanned", viewModel.StatusText);
    }

    [Fact]
    public async Task RunContentsRemainLazyUntilBrowseAndBuildTheOriginalHierarchy()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        string originalRoot = Path.Combine(temp.Path, "incoming");
        var summary = new OperationJournalSummary(
            "IngestMusic", OperationJournalKind.Ingest, OperationJournalState.Completed,
            Path.Combine(temp.Path, "run"), Path.Combine(temp.Path, "run", "journal.tsv"),
            DateTimeOffset.UtcNow, 1);
        var entry = new OperationFileEntry(
            Path.Combine(originalRoot, "Artist", "Album", "song.flac"),
            Path.Combine(temp.Path, "run", "Artist", "Album", "song.flac"),
            Path.Combine("Artist", "Album", "song.flac"),
            OperationEntryKind.Quarantined,
            true,
            false);
        var journals = new RecordingJournals(
            new([summary], []),
            new(originalRoot, [entry], []));
        var viewModel = new OperationsViewModel(journals, new StubFiles(), new StubDialogs(), settings)
        {
            SearchRoot = originalRoot,
        };

        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(0, journals.BrowseCalls);

        await viewModel.OpenRunCommand.ExecuteAsync(Assert.Single(viewModel.Runs));

        Assert.Equal(1, journals.BrowseCalls);
        Assert.True(viewModel.ShowBrowser);
        var root = Assert.Single(viewModel.RootNodes);
        var artist = Assert.Single(root.Children);
        var album = Assert.Single(artist.Children);
        var file = Assert.Single(album.Children);
        Assert.Equal("song.flac", file.Name);
        Assert.Equal("Quarantined", file.StateText);
        Assert.Equal(entry.CurrentPath, file.CurrentPath);
    }

    [Fact]
    public async Task RestoreRequiresSelectionThenPreviewBeforeApply()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        string originalRoot = Path.Combine(temp.Path, "incoming");
        var summary = new OperationJournalSummary(
            "IngestMusic", OperationJournalKind.Ingest, OperationJournalState.Completed,
            Path.Combine(temp.Path, "run"), null, DateTimeOffset.UtcNow, 1);
        var recoverable = new OperationFileEntry(
            Path.Combine(originalRoot, "song.flac"), Path.Combine(temp.Path, "run", "song.flac"),
            "song.flac", OperationEntryKind.Quarantined, true, false);
        var created = new OperationFileEntry(
            Path.Combine(originalRoot, "created.flac"), Path.Combine(originalRoot, "created.flac"),
            "created.flac", OperationEntryKind.Created, true, false);
        var journals = new RecordingJournals(
            new([summary], []), new(originalRoot, [recoverable, created], []));
        var viewModel = new OperationsViewModel(journals, new StubFiles(), new StubDialogs(), settings)
        {
            SearchRoot = originalRoot,
        };
        await viewModel.RefreshCommand.ExecuteAsync(null);
        await viewModel.OpenRunCommand.ExecuteAsync(Assert.Single(viewModel.Runs));

        Assert.False(viewModel.PreviewRestoreCommand.CanExecute(null));
        viewModel.SelectAllRestorableCommand.Execute(null);
        Assert.True(viewModel.PreviewRestoreCommand.CanExecute(null));

        await viewModel.PreviewRestoreCommand.ExecuteAsync(null);
        Assert.True(viewModel.ShowRestorePreview);
        Assert.Single(journals.PreviewEntries!);
        Assert.Equal(recoverable.OriginalPath, journals.PreviewEntries![0].OriginalPath);

        await viewModel.ApplyRestoreCommand.ExecuteAsync(null);
        Assert.Equal(1, journals.ApplyCalls);
        Assert.False(viewModel.ShowRestorePreview);
        Assert.Contains("Restored 1 item", viewModel.StatusText);
    }

    [Fact]
    public async Task PurgeRequiresPreviewPersistsRetentionAndRemovesAppliedRuns()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        var summary = new OperationJournalSummary(
            "IngestMusic", OperationJournalKind.Ingest, OperationJournalState.Completed,
            Path.Combine(temp.Path, "container", "run"), null,
            DateTimeOffset.UtcNow.AddDays(-100), 1);
        var journals = new RecordingJournals(new([summary], []));
        var viewModel = new OperationsViewModel(journals, new StubFiles(), new StubDialogs(), settings)
        {
            SearchRoot = temp.Path,
        };
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.RetentionDays = 45;

        Assert.False(viewModel.ApplyPurgeCommand.CanExecute(null));
        Assert.True(viewModel.PreviewPurgeCommand.CanExecute(null));
        await viewModel.PreviewPurgeCommand.ExecuteAsync(null);

        Assert.True(viewModel.ShowPurgePreview);
        Assert.True(viewModel.ApplyPurgeCommand.CanExecute(null));
        Assert.Equal(45, journals.PreviewRetentionDays);
        Assert.Equal("45", settings.GetPreference("manager.operations.retentionDays.v1"));

        await viewModel.ApplyPurgeCommand.ExecuteAsync(null);

        Assert.Equal(1, journals.PurgeApplyCalls);
        Assert.Empty(viewModel.Runs);
        Assert.False(viewModel.ShowPurgePreview);
        Assert.Contains("Purged 1 run", viewModel.StatusText);
    }

    private sealed class RecordingJournals(
        OperationJournalDiscoveryResult result,
        OperationBrowseResult? browse = null) : IOperationJournalService
    {
        public IReadOnlyList<string>? Roots { get; private set; }
        public int BrowseCalls { get; private set; }
        public IReadOnlyList<OperationFileEntry>? PreviewEntries { get; private set; }
        public int ApplyCalls { get; private set; }
        public int? PreviewRetentionDays { get; private set; }
        public int PurgeApplyCalls { get; private set; }

        public Task<OperationJournalDiscoveryResult> DiscoverAsync(
            IReadOnlyList<string> searchRoots,
            CancellationToken ct = default)
        {
            Roots = searchRoots;
            return Task.FromResult(result);
        }

        public Task<OperationBrowseResult> BrowseAsync(
            OperationJournalSummary run,
            CancellationToken ct = default)
        {
            BrowseCalls++;
            return Task.FromResult(browse ?? new OperationBrowseResult(run.RunPath, [], []));
        }

        public Task<OperationRestorePlan> PreviewRestoreAsync(
            OperationJournalSummary run,
            IReadOnlyList<OperationFileEntry> entries,
            CancellationToken ct = default)
        {
            PreviewEntries = entries;
            var actions = entries.Select(entry => new OperationRestoreAction(
                entry.CurrentPath!, entry.OriginalPath,
                Path.Combine(run.RunPath, "collision-" + Path.GetFileName(entry.OriginalPath)),
                new(true, false, 1, DateTime.UtcNow),
                new(false, false, 0, default), entry.Kind)).ToList();
            return Task.FromResult(new OperationRestorePlan(
                run, Path.Combine(run.RunPath, "restore.tsv"), actions, 0));
        }

        public Task<OperationRestoreResult> ApplyRestoreAsync(
            OperationRestorePlan plan,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            ApplyCalls++;
            return Task.FromResult(new OperationRestoreResult(plan.Actions.Count, plan.CollisionCount));
        }

        public Task<OperationPurgePlan> PreviewPurgeAsync(
            IReadOnlyList<OperationJournalSummary> runs,
            int retentionDays,
            DateTimeOffset? nowUtc = null,
            CancellationToken ct = default)
        {
            PreviewRetentionDays = retentionDays;
            var purgeRuns = runs.Select(run => new OperationPurgeRun(
                run,
                Path.Combine(Path.GetDirectoryName(run.RunPath)!, ".MusicLibrary.App-purge-staging", "run"),
                [new("song.flac", false, false, 5, DateTime.UtcNow)])).ToList();
            return Task.FromResult(new OperationPurgePlan(
                retentionDays, DateTimeOffset.UtcNow.AddDays(-retentionDays), purgeRuns, 0, 0, 0));
        }

        public Task<OperationPurgeResult> ApplyPurgeAsync(
            OperationPurgePlan plan,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            PurgeApplyCalls++;
            return Task.FromResult(new OperationPurgeResult(plan.Runs.Count, plan.FileCount, plan.TotalBytes));
        }
    }

    private sealed class StubFiles : IFileDialogService
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFileAsync(string title, string? suggestedName = null,
            string? defaultExtension = null, IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(null);
    }

    private sealed class StubCrossLibrarySyncService : ICrossLibrarySyncService
    {
        public int PreviewCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public CrossLibrarySyncPlan? PreviewedPlan { get; private set; }
        public CrossLibrarySyncPlan? AppliedPlan { get; private set; }
        public CrossLibrarySyncRequest? LastRequest { get; private set; }

        public Task<CrossLibrarySyncPlan> PreviewAsync(CrossLibrarySyncRequest request,
            IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
        {
            PreviewCalls++;
            LastRequest = request;
            var mutations = new FileMutationPlan("CrossSyncMusic", "target", "recovery", [], [],
                DateTimeOffset.UtcNow);
            PreviewedPlan = new(request, "target", [], 0, 0, mutations, []);
            return Task.FromResult(PreviewedPlan);
        }

        public Task<CrossLibrarySyncResult> ApplyAsync(CrossLibrarySyncPlan plan,
            IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
        {
            ApplyCalls++;
            AppliedPlan = plan;
            return Task.FromResult(new CrossLibrarySyncResult(0, 0,
                new FileMutationSummary(0, 0, 0, 0, null, []), []));
        }
    }

    private sealed class StubJobService(params UnifiedJobDescriptor[] jobs) : IUnifiedJobService
    {
        public IReadOnlyList<UnifiedJobDescriptor> Catalog { get; } = jobs;
    }

    private sealed class StubConfiguredExportService : IConfiguredExportService
    {
        public ConfiguredExportRequest? LastRequest { get; private set; }
        public int ApplyCalls { get; private set; }

        public Task<ConfiguredExportPlan> PreviewAsync(
            ConfiguredExportRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            var profile = new LibraryExportProfile(
                request.ProfileId, "Portable", true,
                ExportSelectionPolicy.EntireLibrary,
                new(ExportTransformMode.Copy),
                new(PreserveSourceLayout: true),
                new(ExportArtworkMode.Embedded, FrontCoverOnly: false),
                new(),
                new(LocalFileSystemExportTransport.ProviderId, "destination"),
                new());
            var mutations = new FileMutationPlan(
                "ConfiguredExport", "destination", "recovery",
                [new(FileMutationKind.Copy, "source.flac", "destination.flac",
                    new(true, false, 1, DateTime.UtcNow),
                    OperationPathSnapshot.Missing("destination.flac"))],
                [], DateTimeOffset.UtcNow);
            var transport = new ExportTransportPlan(
                profile.Id, profile.Fingerprint, LocalFileSystemExportTransport.ProviderId,
                "destination", mutations, []);
            return Task.FromResult(new ConfiguredExportPlan(
                request, profile, Guid.NewGuid(), "library-fingerprint", profile.Fingerprint,
                "destination",
                [new("source.flac", "destination.flac", FileMutationKind.Copy)],
                0, 0, transport, []));
        }

        public Task<ConfiguredExportResult> ApplyAsync(
            ConfiguredExportPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ApplyCalls++;
            return Task.FromResult(new ConfiguredExportResult(
                plan.Profile!.Id, plan.Files.Count, plan.UnchangedCount,
                new(1, 0, 0, 0, "journal.tsv", []), []));
        }
    }

    private sealed class StubArtworkNormalizationService(
        IReadOnlyList<string> paths,
        string? cacheError) : IArtworkNormalizationService
    {
        public IReadOnlyList<string> Paths { get; } = paths;

        public Task<ArtworkNormalizationPlan> PreviewAsync(
            ArtworkNormalizationRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            var snapshot = new OperationPathSnapshot(false, false, 0, default);
            return Task.FromResult(new ArtworkNormalizationPlan(
                request,
                request.ItunesLibraryPath ?? "library.itl",
                snapshot,
                "",
                [],
                0,
                0,
                0,
                [],
                "recovery",
                DateTimeOffset.UtcNow));
        }

        public Task<ArtworkNormalizationResult> ApplyAsync(
            ArtworkNormalizationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(new ArtworkNormalizationResult(Paths.Count, Paths.Count, null, [])
            {
                UpdatedPaths = Paths,
                CacheError = cacheError,
            });
    }

    private sealed class StubDialogs : IDialogService
    {
        public bool ConfirmApplyResult { get; init; } = true;
        public string? ApplyMessage { get; private set; }
        public Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths) => Task.FromResult(false);
        public Task<string?> ShowConfigEditorAsync(string? existingPath) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmApplyAsync(string title, string message, string primaryText)
        {
            ApplyMessage = message;
            return Task.FromResult(ConfirmApplyResult);
        }
        public Task<bool> ConfirmCdDerivationAsync(IngestApprovalItem item) => Task.FromResult(false);
        public Task<bool> ConfirmRestoreAsync(OperationRestorePlan plan) => Task.FromResult(true);
        public Task<bool> ConfirmPurgeAsync(OperationPurgePlan plan) => Task.FromResult(true);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "operations-view-model-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
