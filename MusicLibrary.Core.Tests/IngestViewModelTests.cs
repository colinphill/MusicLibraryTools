using MusicLibraryManager.Presentation;
using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class IngestViewModelTests
{
    [Fact]
    public void IngestReadinessReportsMissingRolesWithoutRejectingTheLibraryConfiguration()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ingest-readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "library.xml");
            new EditableLibraryConfig
            {
                IndexTargets =
                [
                    new() { Target = Path.Combine(root, "cd"), IngestRole = LibraryIngestRole.Cd },
                ],
            }.Save(config);
            var settings = new AppSettings(Path.Combine(root, "settings.json"));
            settings.LoadConfig(config);
            var viewModel = Create(settings, new StubPreflight());
            viewModel.SourceDirectory = root;

            Assert.False(viewModel.IsConfigurationReady);
            Assert.False(viewModel.PreviewCommand.CanExecute(null));
            Assert.Contains("CD fallback", viewModel.ConfigurationReadinessText,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Hi-res", viewModel.ConfigurationReadinessText,
                StringComparison.OrdinalIgnoreCase);

            WriteReadyLibraryConfig(config);
            settings.LoadConfig(config);

            Assert.True(viewModel.IsConfigurationReady);
            Assert.True(viewModel.PreviewCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HistoryFiltersIngestRunsAndRaisesRecoveryNavigation()
    {
        string source = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"ingest-history-{Guid.NewGuid():N}")).FullName;
        string state = Path.Combine(source, "settings.json");
        try
        {
            var interrupted = new OperationJournalSummary("IngestMusic", OperationJournalKind.Ingest,
                OperationJournalState.Interrupted, Path.Combine(source, "run"), "journal.tsv",
                DateTimeOffset.UtcNow, 2);
            var organize = new OperationJournalSummary("OrganizeFiles", OperationJournalKind.Organize,
                OperationJournalState.Completed, Path.Combine(source, "organize"), "journal.tsv",
                DateTimeOffset.UtcNow, 1);
            var journals = new StubJournals([interrupted, organize]);
            var viewModel = new IngestViewModel(new StubIngest(), new StubFiles(), new StubDialogs(),
                new AppSettings(state), new StubLibrary(), journals: journals)
            {
                SourceDirectory = source,
            };

            await viewModel.RefreshHistoryCommand.ExecuteAsync(null);

            var item = Assert.Single(viewModel.History);
            Assert.True(item.IsInterrupted);
            Assert.Equal(1, viewModel.InterruptedHistoryCount);
            OperationJournalSummary? requested = null;
            viewModel.RecoveryRequested += run => requested = run;
            viewModel.SelectedHistory = item;
            viewModel.OpenHistoryCommand.Execute(null);
            Assert.Same(interrupted, requested);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public async Task PreviewBuildsSummaryCountsAndCategoryFilters()
    {
        var track = new IngestTrackPlan
        {
            Identity = "track", SourcePath = "source.flac", Title = "Song", Artist = "Artist",
            AlbumArtist = "Artist", Album = "Album", TrackNumber = 1, TrackTotal = 1,
            OriginalDiscNumber = 1, SampleRate = 44_100, BitsPerSample = 16, Channels = 2,
            DurationInSeconds = 180, IsAlac = false, IsHighResolution = false,
        };
        var outputs = new[]
        {
            new IngestOutputPlan { Identity = "track", Kind = IngestOutputKind.CdFlac, Metadata = track,
                SourcePath = track.SourcePath, DestinationPath = "cd.flac" },
            new IngestOutputPlan { Identity = "track", Kind = IngestOutputKind.Aac, Metadata = track,
                SourcePath = track.SourcePath, DestinationPath = "aac.m4a" },
        };
        var configuration = new IngestMusicConfiguration
        {
            FfmpegPath = "ffmpeg", AacDestination = "aac", CdDestination = "cd",
            PairedCdDestination = "paired", HighResolutionDestination = "hires",
        };
        var plan = new IngestPlan
        {
            Request = new("source", "config.xml"), Configuration = configuration,
                    Albums = [new IngestAlbumPlan { Key = "album", Display = "Artist — Album",
                Tracks = [track], Outputs = outputs, Sources = [], HasHighResolution = false }],
            Files = [new("source.flac", "CD FLAC", "Create outputs"),
                new("cover.jpg", "Unsupported/non-audio", "Quarantine")],
            RequiredApprovals = [],
            Conflicts = [new("album", "source.flac", "Duplicate track")],
            IgnoredFiles = ["cover.jpg"],
            IgnoredFileSnapshots = [new("cover.jpg", 10, DateTime.UtcNow)],
            SourceDirectories = ["source", "source/subfolder"],
        };
        string state = Path.Combine(Path.GetTempPath(), $"ingest-summary-{Guid.NewGuid():N}.json");
        string configPath = Path.Combine(Path.GetTempPath(), $"ingest-summary-{Guid.NewGuid():N}.xml");
        try
        {
            WriteReadyLibraryConfig(configPath);
            var settings = new AppSettings(state);
            settings.LoadConfig(configPath);
            var viewModel = new IngestViewModel(new StubIngest(plan), new StubFiles(), new StubDialogs(),
                settings, new StubLibrary());
            viewModel.SourceDirectory = "source";

            await viewModel.PreviewCommand.ExecuteAsync(null);

            Assert.Equal(1, viewModel.AlbumCount);
            Assert.Equal(2, viewModel.OutputCount);
            Assert.Equal(1, viewModel.ConflictCount);
            Assert.Equal(3, viewModel.CleanupCount);
            viewModel.SelectedPreviewFilter = IngestPreviewFilter.Outputs;
            Assert.Equal(2, viewModel.Files.Count);
            Assert.All(viewModel.Files, item => Assert.True(item.IsOutput));
            viewModel.SelectedPreviewFilter = IngestPreviewFilter.Conflicts;
            Assert.Single(viewModel.Files);
            Assert.True(viewModel.Files[0].IsConflict);
        }
        finally
        {
            if (File.Exists(state)) File.Delete(state);
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task ApplyRequiresARecoverySummaryConfirmation()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ingest-confirm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string configPath = Path.Combine(root, "library.xml");
        string settingsPath = Path.Combine(root, "settings.json");
        try
        {
            WriteReadyLibraryConfig(configPath);
            var settings = new AppSettings(settingsPath);
            settings.LoadConfig(configPath);
            var plan = new IngestPlan
            {
                Request = new(root),
                Configuration = new IngestMusicConfiguration
                {
                    FfmpegPath = "ffmpeg",
                    AacDestination = "aac",
                    CdDestination = "cd",
                    PairedCdDestination = "paired",
                    HighResolutionDestination = "hires",
                    RemoveNonMusicAfterIngest = true,
                },
                Albums = [],
                Files = [new("cover.jpg", "Unsupported/non-audio", "Quarantine")],
                RequiredApprovals = [],
                Conflicts = [],
                IgnoredFiles = ["cover.jpg"],
                IgnoredFileSnapshots = [new("cover.jpg", 10, DateTime.UtcNow)],
                SourceDirectories = [root],
            };
            var ingest = new StubIngest(plan);
            var dialogs = new StubDialogs { ConfirmApplyResult = false };
            var viewModel = new IngestViewModel(ingest, new StubFiles(), dialogs,
                settings, new StubLibrary())
            {
                SourceDirectory = root,
            };

            await viewModel.PreviewCommand.ExecuteAsync(null);
            await viewModel.ApplyCommand.ExecuteAsync(null);

            Assert.Equal(0, ingest.ApplyCalls);
            Assert.True(viewModel.ApplyCommand.CanExecute(null));
            Assert.Contains("clean up 2 source item", dialogs.ApplyMessage);
            Assert.Contains("Recovery is available", dialogs.ApplyMessage);
            Assert.False(viewModel.CancelCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecentDropsAndPreflightPersistAsWorkflowState()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ingest-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string dropped = Directory.CreateDirectory(Path.Combine(root, "dropped")).FullName;
        string config = Path.Combine(root, "library.xml");
        string state = Path.Combine(root, "settings.json");
        try
        {
            var settings = new AppSettings(state);
            WriteReadyLibraryConfig(config);
            settings.LoadConfig(config);
            var preflight = new StubPreflight();
            var viewModel = Create(settings, preflight);
            viewModel.SourceDirectory = source;

            viewModel.SetDroppedSource(dropped);
            await viewModel.PreflightCommand.ExecuteAsync(null);

            Assert.Equal(dropped, viewModel.RecentSources[0]);
            Assert.True(viewModel.HasPreflightChecks);
            Assert.Equal(1, preflight.Calls);

            var restoredSettings = new AppSettings(state);
            restoredSettings.LoadConfig(config);
            var restored = Create(restoredSettings, new StubPreflight());

            Assert.Equal(dropped, restored.SourceDirectory);
            Assert.Contains(dropped, restored.RecentSources);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IngestViewModel Create(IAppSettings settings, IIngestPreflightService preflight) =>
        new(new StubIngest(), new StubFiles(), new StubDialogs(), settings, new StubLibrary(), preflight);

    private static void WriteReadyLibraryConfig(string path)
    {
        new EditableLibraryConfig
        {
            IndexTargets =
            [
                new() { Target = "cd", IngestRole = LibraryIngestRole.Cd },
                new() { Target = "paired", IngestRole = LibraryIngestRole.CdFallback },
                new() { Target = "hires", IngestRole = LibraryIngestRole.HiRes },
                new() { Target = "aac", IngestRole = LibraryIngestRole.AacFallback },
            ],
        }.Save(path);
    }

    private sealed class StubPreflight : IIngestPreflightService
    {
        public int Calls { get; private set; }
        public Task<IngestPreflightResult> CheckAsync(IngestRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new IngestPreflightResult([
                new("Configuration", IngestPreflightSeverity.Pass, "Ready"),
            ]));
        }
    }

    private sealed class StubJournals(IReadOnlyList<OperationJournalSummary> runs) : IOperationJournalService
    {
        public Task<OperationJournalDiscoveryResult> DiscoverAsync(IReadOnlyList<string> searchRoots,
            CancellationToken ct = default) => Task.FromResult(new OperationJournalDiscoveryResult(runs, []));
        public Task<OperationBrowseResult> BrowseAsync(OperationJournalSummary run,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationRestorePlan> PreviewRestoreAsync(OperationJournalSummary run,
            IReadOnlyList<OperationFileEntry> entries, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<OperationRestoreResult> ApplyRestoreAsync(OperationRestorePlan plan,
            IProgress<int>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<OperationPurgePlan> PreviewPurgeAsync(IReadOnlyList<OperationJournalSummary> runs,
            int retentionDays, DateTimeOffset? nowUtc = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<OperationPurgeResult> ApplyPurgeAsync(OperationPurgePlan plan,
            IProgress<int>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubIngest(IngestPlan? plan = null) : IIngestMusicService
    {
        public int ApplyCalls { get; private set; }
        public Task<IngestPlan> PreviewAsync(IngestRequest request, CancellationToken ct = default) =>
            plan is null ? throw new NotSupportedException() : Task.FromResult(plan);
        public Task<IngestResult> ApplyAsync(IngestPlan plan,
            IReadOnlyList<IngestApprovalDecision> approvals, IProgress<IngestProgress>? progress = null,
            CancellationToken ct = default)
        {
            ApplyCalls++;
            return Task.FromResult(new IngestResult([], false));
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
        public Task<bool> ConfirmRestoreAsync(OperationRestorePlan plan) => Task.FromResult(false);
        public Task<bool> ConfirmPurgeAsync(OperationPurgePlan plan) => Task.FromResult(false);
    }

    private sealed class StubLibrary : ILibraryService
    {
        public bool IsReady => false;
        public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LibrarySnapshot> BuildSnapshotAsync(LibraryGrouping grouping = LibraryGrouping.AlbumArtist,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<FileDetails?> GetFileDetailsAsync(string path, bool includeArtwork,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(IReadOnlyList<string> paths,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetImageSignaturesAsync(IReadOnlyList<string> paths,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
