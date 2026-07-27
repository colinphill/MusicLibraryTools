using System.Collections.Concurrent;
using System.Globalization;
using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class TerminalProgressLifecycleTests
{
    [Theory]
    [InlineData(ProgressOutcome.Success,
        "Index.Status.CompleteFormat",
        AppActivityState.Completed)]
    [InlineData(ProgressOutcome.Cancelled,
        "Index.Status.Cancelled",
        AppActivityState.Cancelled)]
    [InlineData(ProgressOutcome.Failed,
        "Index.Status.Failed",
        AppActivityState.Failed)]
    public async Task Indexing_progress_cannot_overtake_terminal_state(
        ProgressOutcome outcome,
        string expectedStatus,
        AppActivityState expectedActivityState)
    {
        var context =
            new ManualSynchronizationContext();
        var activities =
            new AppActivityService();
        var viewModel =
            new IndexingViewModel(
                new ProgressLibrary(outcome),
                new FakeSettings(),
                activities,
                new KeyLocalizationService());
        viewModel.IndexCompleted +=
            () => throw new InvalidOperationException(
                "index observer");

        Task operation =
            StartOnContext(
                context,
                () => viewModel.IndexCommand
                    .ExecuteAsync(null));
        await PumpAsync(
            context,
            operation);

        Assert.Equal(
            expectedStatus,
            viewModel.StatusText);
        Assert.False(
            viewModel.IsIndexing);
        Assert.False(
            viewModel.CancelCommand.CanExecute(
                null));
        Assert.Contains(
            "index observer",
            viewModel.DiagnosticDetail);
        Assert.Equal(
            expectedActivityState,
            Assert.Single(
                    activities.Activities)
                .State);
    }

    [Fact]
    public async Task Device_preview_preserves_its_terminal_status_after_queued_progress()
    {
        var context =
            new ManualSynchronizationContext();
        var activities =
            new AppActivityService();
        var viewModel =
            new DevicesViewModel(
                new ProgressDeviceSync(),
                new FakeSettings(),
                new NullPicker(),
                new CoordinatorDialogs(),
                activities,
                new KeyLocalizationService())
            {
                SourcePath = @"C:\Music",
                DestinationPath = "music",
            };

        Assert.True(
            viewModel.PreviewCommand.CanExecute(
                null));
        Task operation =
            StartOnContext(
                context,
                () => viewModel.PreviewCommand
                    .ExecuteAsync(null));
        await PumpAsync(
            context,
            operation);

        Assert.Equal(
            "Devices.Status.PreviewReady",
            viewModel.StatusText);
        Assert.False(
            viewModel.IsBusy);
        Assert.False(
            viewModel.CancelCommand.CanExecute(
                null));
        Assert.Equal(
            AppActivityState.Completed,
            Assert.Single(
                    activities.Activities,
                    item => item.Title ==
                        "Devices.Activity.Preview.Title")
                .State);
    }

    [Fact]
    public async Task Organize_apply_preserves_success_when_progress_and_refresh_observers_are_hostile()
    {
        using var tree =
            new TempTree();
        string config =
            Path.Combine(
                tree.Path,
                "library.xml");
        new EditableLibraryConfig()
            .Save(config);
        var settings =
            new AppSettings(
                Path.Combine(
                    tree.Path,
                    "settings.json"));
        settings.LoadConfig(
            config);
        var context =
            new ManualSynchronizationContext();
        var activities =
            new AppActivityService();
        var organizer =
            new ProgressOrganizer(
            [
                new(
                    Path.Combine(
                        tree.Path,
                        "source.flac"),
                    Path.Combine(
                        tree.Path,
                        "Artist",
                        "track.flac")),
            ]);
        var viewModel =
            new OrganizeViewModel(
                organizer,
                settings,
                new CoreDialogs(),
                activities,
                new KeyLocalizationService());
        viewModel.MovesApplied +=
            () => throw new InvalidOperationException(
                "organize observer");

        await viewModel.PreviewCommand
            .ExecuteAsync(null);
        Assert.True(
            viewModel.ApplyCommand.CanExecute(
                null));
        Task operation =
            StartOnContext(
                context,
                () => viewModel.ApplyCommand
                    .ExecuteAsync(null));
        await PumpAsync(
            context,
            operation);

        Assert.Equal(
            "Organize.Status.ApplyComplete",
            viewModel.StatusText);
        Assert.False(
            viewModel.IsBusy);
        Assert.False(
            viewModel.CancelCommand.CanExecute(
                null));
        Assert.Contains(
            "organize observer",
            viewModel.DiagnosticDetail);
        Assert.Equal(
            AppActivityState.Completed,
            Assert.Single(
                    activities.Activities,
                    item => item.Title ==
                        "Organize.Activity.Apply.Title")
                .State);
    }

    [Fact]
    public async Task Reviewed_file_preview_keeps_accepted_terminal_state_after_queued_progress()
    {
        string source =
            Path.GetFullPath(
                "terminal-progress-source.flac");
        var context =
            new ManualSynchronizationContext();
        var editor =
            new ReviewedFileOperationEditorViewModel(
                new ProgressReviewedFileOperations(),
                new NullPicker(),
                () => [source],
                _ => Task.FromResult(true),
                localization:
                new KeyLocalizationService())
            {
                SelectedKind =
                    ReviewedFileOperationKind.Rename,
            };
        editor.PreviewAddedToReview +=
            (_, _) => throw new InvalidOperationException(
                "review observer");

        Assert.True(
            editor.PreviewCommand.CanExecute(
                null));
        Task operation =
            StartOnContext(
                context,
                () => editor.PreviewCommand
                    .ExecuteAsync(null));
        await PumpAsync(
            context,
            operation);

        Assert.Equal(
            "ReviewedFileOperation.Status.AddedToReview.One",
            editor.Status);
        Assert.Contains(
            "review observer",
            editor.StatusDiagnosticDetail);
        Assert.False(
            editor.IsBusy);
        Assert.False(
            editor.CancelCommand.CanExecute(
                null));
        Assert.False(
            editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task Ingest_preview_and_apply_each_drain_progress_before_terminal_status()
    {
        using var tree =
            new TempTree();
        string config =
            Path.Combine(
                tree.Path,
                "library.xml");
        WriteReadyLibraryConfig(
            config);
        var settings =
            new AppSettings(
                Path.Combine(
                    tree.Path,
                    "settings.json"));
        settings.LoadConfig(
            config);
        IngestPlan plan =
            CleanupPlan(
                tree.Path);
        var ingest =
            new ProgressIngest(
                plan);
        var context =
            new ManualSynchronizationContext();
        var activities =
            new AppActivityService();
        var viewModel =
            new IngestViewModel(
                ingest,
                new NullFileDialogs(),
                new CoreDialogs(),
                settings,
                new NonReadyLibrary(),
                activities: activities,
                localization:
                new KeyLocalizationService())
            {
                SourceDirectory =
                    tree.Path,
            };
        viewModel.IngestCompleted +=
            () => throw new InvalidOperationException(
                "ingest observer");

        Task preview =
            StartOnContext(
                context,
                () => viewModel.PreviewCommand
                    .ExecuteAsync(null));
        await PumpAsync(
            context,
            preview);
        Assert.Equal(
            "Ingest.Status.PreviewCleanupReady",
            viewModel.StatusText);
        Assert.True(
            viewModel.ApplyCommand.CanExecute(
                null));

        Task apply =
            StartOnContext(
                context,
                () => viewModel.ApplyCommand
                    .ExecuteAsync(null));
        await PumpAsync(
            context,
            apply);

        Assert.Equal(
            "Ingest.Status.CleanupComplete",
            viewModel.StatusText);
        Assert.Contains(
            "ingest observer",
            viewModel.DiagnosticDetail);
        Assert.False(
            viewModel.IsBusy);
        Assert.False(
            viewModel.CancelCommand.CanExecute(
                null));
        Assert.Equal(
            2,
            activities.Activities.Count);
        Assert.All(
            activities.Activities,
            activity => Assert.Equal(
                AppActivityState.Completed,
                activity.State));
    }

    [Fact]
    public async Task Operations_job_preview_drains_queued_progress_before_terminal_status()
    {
        using var tree =
            new TempTree();
        string config =
            Path.Combine(
                tree.Path,
                "library.xml");
        new EditableLibraryConfig
        {
            ItunesLibraryPath =
                Path.Combine(
                    tree.Path,
                    "library.itl"),
        }.Save(config);
        var settings =
            new AppSettings(
                Path.Combine(
                    tree.Path,
                    "settings.json"));
        settings.LoadConfig(
            config);
        var context =
            new ManualSynchronizationContext();
        var activities =
            new AppActivityService();
        var viewModel =
            new OperationsViewModel(
                new UnsupportedJournals(),
                new NullFileDialogs(),
                new CoreDialogs(),
                settings,
                jobs: new RedundancyJobCatalog(),
                redundancyAnalysis:
                new ProgressRedundancyAnalysis(),
                activities: activities,
                localization:
                new KeyLocalizationService());

        Assert.True(
            viewModel.PreviewJobCommand
                .CanExecute(null));
        Task operation =
            StartOnContext(
                context,
                () => viewModel.PreviewJobCommand
                    .ExecuteAsync(null));
        await PumpAsync(
            context,
            operation);

        Assert.Equal(
            "Operations.Job.Status.PreviewCompleted",
            viewModel.JobStatus);
        Assert.False(
            viewModel.IsBusy);
        Assert.False(
            viewModel.CancelCommand.CanExecute(
                null));
        Assert.Equal(
            AppActivityState.Completed,
            Assert.Single(
                    activities.Activities,
                    item => item.Title ==
                        "Operations.Activity.Preview.Title")
                .State);
    }

    [Fact]
    public async Task Operations_purge_drains_queued_progress_before_terminal_status()
    {
        using var tree =
            new TempTree();
        var journals =
            new ProgressPurgeJournals(
                tree.Path);
        var context =
            new ManualSynchronizationContext();
        var activities =
            new AppActivityService();
        var viewModel =
            new OperationsViewModel(
                journals,
                new NullFileDialogs(),
                new CoreDialogs
                {
                    ConfirmPurge = true,
                },
                new FakeSettings(),
                activities: activities,
                localization:
                new KeyLocalizationService())
            {
                SearchRoot =
                    tree.Path,
            };

        await viewModel.RefreshCommand
            .ExecuteAsync(null);
        await viewModel.PreviewPurgeCommand
            .ExecuteAsync(null);
        Assert.True(
            viewModel.ApplyPurgeCommand
                .CanExecute(null));
        Task operation =
            StartOnContext(
                context,
                () => viewModel.ApplyPurgeCommand
                    .ExecuteAsync(null));
        await PumpAsync(
            context,
            operation);

        Assert.Equal(
            "Operations.Status.PurgeCompleted.One",
            viewModel.StatusText);
        Assert.False(
            viewModel.IsBusy);
        Assert.False(
            viewModel.CancelCommand.CanExecute(
                null));
        Assert.Equal(
            AppActivityState.Completed,
            Assert.Single(
                    activities.Activities,
                    item => item.Title ==
                        "Operations.Activity.Purge.Title")
                .State);
    }

    private static Task StartOnContext(
        SynchronizationContext context,
        Func<Task> start)
    {
        SynchronizationContext? previous =
            SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(
                context);
            return start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(
                previous);
        }
    }

    private static async Task PumpAsync(
        ManualSynchronizationContext context,
        Task operation)
    {
        DateTime deadline =
            DateTime.UtcNow.AddSeconds(10);
        while (!operation.IsCompleted)
        {
            context.RunAll();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    "The queued presentation operation did not complete.");
            await Task.Delay(
                    1,
                    TestContext.Current
                        .CancellationToken)
                .ConfigureAwait(false);
        }
        context.RunAll();
        await operation.ConfigureAwait(
            false);
    }

    private static void WriteReadyLibraryConfig(
        string path) =>
        new EditableLibraryConfig
        {
            IndexTargets =
            [
                new()
                {
                    Target = "cd",
                    IngestRole =
                        LibraryIngestRole.Cd,
                },
                new()
                {
                    Target = "paired",
                    IngestRole =
                        LibraryIngestRole.CdFallback,
                },
                new()
                {
                    Target = "hires",
                    IngestRole =
                        LibraryIngestRole.HiRes,
                },
                new()
                {
                    Target = "aac",
                    IngestRole =
                        LibraryIngestRole.AacFallback,
                },
            ],
        }.Save(path);

    private static IngestPlan CleanupPlan(
        string sourceRoot) =>
        new()
        {
            Request =
                new(sourceRoot),
            Configuration =
                new()
                {
                    FfmpegPath = "ffmpeg",
                    AacDestination = "aac",
                    CdDestination = "cd",
                    PairedCdDestination = "paired",
                    HighResolutionDestination = "hires",
                    RemoveNonMusicAfterIngest = true,
                },
            Albums = [],
            Files =
            [
                new(
                    Path.Combine(
                        sourceRoot,
                        "cover.jpg"),
                    "Unsupported/non-audio",
                    "Quarantine"),
            ],
            RequiredApprovals = [],
            Conflicts = [],
            IgnoredFiles =
            [
                Path.Combine(
                    sourceRoot,
                    "cover.jpg"),
            ],
            IgnoredFileSnapshots =
            [
                new(
                    Path.Combine(
                        sourceRoot,
                        "cover.jpg"),
                    10,
                    DateTime.UtcNow),
            ],
            SourceDirectories =
            [
                sourceRoot,
            ],
        };

    public enum ProgressOutcome
    {
        Success,
        Cancelled,
        Failed,
    }

    private sealed class ProgressLibrary(
        ProgressOutcome outcome) :
        ILibraryService
    {
        public bool IsReady =>
            true;

        public Task<(
            int Added,
            int Modified,
            int Removed,
            int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.Run(
                () =>
                {
                    progress?.Report(
                        new(1, 0, 0, 0)
                        {
                            Phase =
                                IndexPhase.Metadata,
                            Enumerated = 1,
                            Detail =
                                "queued index progress",
                        });
                    return outcome switch
                    {
                        ProgressOutcome.Success =>
                            (1, 0, 0, 0),
                        ProgressOutcome.Cancelled =>
                            throw new OperationCanceledException(
                                ct),
                        _ =>
                            throw new InvalidOperationException(
                                "index failure"),
                    };
                },
                ct);

        public Task<LibrarySnapshot> BuildSnapshotAsync(
            LibraryGrouping grouping =
                LibraryGrouping.AlbumArtist,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TrackRecord>>
            GetAllRecordsAsync(
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AnalysisReport> CheckSetsAsync(
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FileDetails?> GetFileDetailsAsync(
            string path,
            bool includeArtwork,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<byte[]?> GetFirstImageAsync(
            string path,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<byte[]?>>
            GetFirstImagesAsync(
                IReadOnlyList<string> paths,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>>
            GetImageSignaturesAsync(
                IReadOnlyList<string> paths,
                CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ProgressDeviceSync :
        IDeviceSyncService
    {
        public Task<DeviceSyncInitializationResult>
            InitializeAsync(
                DeviceSyncInitializationRequest request,
                IProgress<OperationProgress>? progress = null,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DeviceSyncPlan> PreviewAsync(
            DeviceSyncRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.Run(
                () =>
                {
                    progress?.Report(
                        new(
                            OperationPhase.Planning,
                            1,
                            1,
                            "track.flac",
                            "queued device progress"));
                    return new DeviceSyncPlan(
                        request,
                        "device",
                        "digest",
                        "plan.json",
                        [],
                        0,
                        0,
                        0,
                        0,
                        [],
                        DateTimeOffset.UtcNow);
                },
                ct);

        public Task<DeviceSyncResult> ApplyAsync(
            DeviceSyncPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DeviceSyncRestoreResult> RestoreAsync(
            DeviceSyncRestoreRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ProgressOrganizer(
        IReadOnlyList<PlannedMove> moves) :
        ILibraryOrganizer
    {
        public Task<IReadOnlyList<PlannedMove>>
            PreviewMovesAsync(
                CancellationToken ct = default) =>
            Task.FromResult(
                moves);

        public Task<OrganizeResult> ApplyMovesAsync(
            IReadOnlyList<PlannedMove> selected,
            IProgress<int>? progress = null,
            CancellationToken ct = default) =>
            Task.Run(
                () =>
                {
                    progress?.Report(
                        selected.Count);
                    return new OrganizeResult(
                        selected.Count,
                        []);
                },
                ct);
    }

    private sealed class ProgressReviewedFileOperations :
        IReviewedFileOperationService
    {
        public Task<ReviewedFileOperationPlan>
            PreviewAsync(
                ReviewedFileOperationRequest request,
                IProgress<OperationProgress>? progress = null,
                CancellationToken ct = default) =>
            Task.Run(
                () =>
                {
                    string source =
                        request.SourcePaths[0];
                    string destination =
                        source + ".renamed";
                    progress?.Report(
                        new(
                            OperationPhase.Planning,
                            1,
                            1,
                            source,
                            "queued file progress"));
                    var action =
                        new FileMutationAction(
                            FileMutationKind.Move,
                            source,
                            destination,
                            null,
                            null);
                    var mutation =
                        new FileMutationPlan(
                            "Reviewed file operation",
                            Path.GetDirectoryName(
                                destination) ?? ".",
                            Path.GetDirectoryName(
                                destination) ?? ".",
                            [action],
                            [],
                            DateTimeOffset.UtcNow);
                    return new ReviewedFileOperationPlan(
                        request,
                        [
                            new(
                                source,
                                destination,
                                FileMutationKind.Move,
                                []),
                        ],
                        mutation);
                },
                ct);

        public Task<FileMutationSummary> ApplyAsync(
            ReviewedFileOperationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ProgressIngest(
        IngestPlan plan) :
        IIngestMusicService
    {
        public Task<IngestPlan> PreviewAsync(
            IngestRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(
                plan);

        public Task<IngestPlan> PreviewAsync(
            IngestRequest request,
            IProgress<IngestProgress>? progress,
            CancellationToken ct = default) =>
            Task.Run(
                () =>
                {
                    progress?.Report(
                        new(
                            "",
                            "queued preview progress",
                            1,
                            1));
                    return plan;
                },
                ct);

        public Task<IngestResult> ApplyAsync(
            IngestPlan reviewedPlan,
            IReadOnlyList<IngestApprovalDecision> approvals,
            IProgress<IngestProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.Run(
                () =>
                {
                    progress?.Report(
                        new(
                            "",
                            "queued apply progress",
                            1,
                            1));
                    return new IngestResult(
                        [],
                        false);
                },
                ct);
    }

    private sealed class RedundancyJobCatalog :
        IUnifiedJobService
    {
        public IReadOnlyList<UnifiedJobDescriptor>
            Catalog { get; } =
            [
                new(
                    "redundancies",
                    "Redundancies",
                    "Find redundant files.",
                    UnifiedJobApplyMode.ReadOnly,
                    [],
                    "",
                    0),
            ];
    }

    private sealed class ProgressRedundancyAnalysis :
        IRedundancyAnalysisService
    {
        public Task<RedundancyAnalysisResult>
            AnalyzeAsync(
                string? libraryPath = null,
                IProgress<OperationProgress>? progress = null,
                CancellationToken ct = default) =>
            Task.Run(
                () =>
                {
                    progress?.Report(
                        new(
                            OperationPhase.LoadingLibrary,
                            1,
                            1,
                            "library.itl",
                            "queued job progress"));
                    return new RedundancyAnalysisResult(
                        libraryPath ?? "library.itl",
                        0,
                        []);
                },
                ct);
    }

    private sealed class UnsupportedJournals :
        IOperationJournalService
    {
        public Task<OperationJournalDiscoveryResult>
            DiscoverAsync(
                IReadOnlyList<string> searchRoots,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationBrowseResult> BrowseAsync(
            OperationJournalSummary run,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationRestorePlan>
            PreviewRestoreAsync(
                OperationJournalSummary run,
                IReadOnlyList<OperationFileEntry> entries,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationRestoreResult>
            ApplyRestoreAsync(
                OperationRestorePlan plan,
                IProgress<int>? progress = null,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationPurgePlan>
            PreviewPurgeAsync(
                IReadOnlyList<OperationJournalSummary> runs,
                int retentionDays,
                DateTimeOffset? nowUtc = null,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationPurgeResult>
            ApplyPurgeAsync(
                OperationPurgePlan plan,
                IProgress<int>? progress = null,
                CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ProgressPurgeJournals :
        IOperationJournalService
    {
        private readonly OperationJournalSummary _summary;

        public ProgressPurgeJournals(
            string root)
        {
            _summary =
                new(
                    "MusicLibraryManager",
                    OperationJournalKind.ReviewedChange,
                    OperationJournalState.Completed,
                    Path.Combine(
                        root,
                        "run"),
                    Path.Combine(
                        root,
                        "run",
                        "journal.tsv"),
                    DateTimeOffset.UtcNow
                        .AddDays(-100),
                    1);
        }

        public Task<OperationJournalDiscoveryResult>
            DiscoverAsync(
                IReadOnlyList<string> searchRoots,
                CancellationToken ct = default) =>
            Task.FromResult(
                new OperationJournalDiscoveryResult(
                    [_summary],
                    []));

        public Task<OperationPurgePlan>
            PreviewPurgeAsync(
                IReadOnlyList<OperationJournalSummary> runs,
                int retentionDays,
                DateTimeOffset? nowUtc = null,
                CancellationToken ct = default) =>
            Task.FromResult(
                new OperationPurgePlan(
                    retentionDays,
                    DateTimeOffset.UtcNow,
                    [
                        new(
                            _summary,
                            _summary.RunPath + ".purge",
                            [
                                new(
                                    "payload.bin",
                                    false,
                                    false,
                                    32,
                                    DateTime.UtcNow),
                            ]),
                    ],
                    0,
                    0,
                    0));

        public Task<OperationPurgeResult>
            ApplyPurgeAsync(
                OperationPurgePlan plan,
                IProgress<int>? progress = null,
                CancellationToken ct = default) =>
            Task.Run(
                () =>
                {
                    progress?.Report(
                        1);
                    return new OperationPurgeResult(
                        1,
                        1,
                        32);
                },
                ct);

        public Task<OperationBrowseResult> BrowseAsync(
            OperationJournalSummary run,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationRestorePlan>
            PreviewRestoreAsync(
                OperationJournalSummary run,
                IReadOnlyList<OperationFileEntry> entries,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationRestoreResult>
            ApplyRestoreAsync(
                OperationRestorePlan plan,
                IProgress<int>? progress = null,
                CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NonReadyLibrary :
        ILibraryService
    {
        public bool IsReady =>
            false;

        public Task<(
            int Added,
            int Modified,
            int Removed,
            int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LibrarySnapshot> BuildSnapshotAsync(
            LibraryGrouping grouping =
                LibraryGrouping.AlbumArtist,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TrackRecord>>
            GetAllRecordsAsync(
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AnalysisReport> CheckSetsAsync(
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FileDetails?> GetFileDetailsAsync(
            string path,
            bool includeArtwork,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<byte[]?> GetFirstImageAsync(
            string path,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<byte[]?>>
            GetFirstImagesAsync(
                IReadOnlyList<string> paths,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>>
            GetImageSignaturesAsync(
                IReadOnlyList<string> paths,
                CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NullPicker :
        IFilePickerService
    {
        public Task<string?> PickFileAsync(
            string title,
            IReadOnlyList<FilePickerType>? types = null) =>
            Task.FromResult<string?>(
                null);

        public Task<string?> PickFolderAsync(
            string title) =>
            Task.FromResult<string?>(
                null);

        public Task<string?> SaveFileAsync(
            string title,
            string suggestedName,
            string extension) =>
            Task.FromResult<string?>(
                null);
    }

    private sealed class NullFileDialogs :
        IFileDialogService
    {
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(
                null);

        public Task<string?> PickFolderAsync(
            string title) =>
            Task.FromResult<string?>(
                null);

        public Task<string?> PickSaveFileAsync(
            string title,
            string? suggestedName = null,
            string? defaultExtension = null,
            IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(
                null);
    }

    private sealed class CoordinatorDialogs :
        IDialogCoordinator
    {
        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string primaryText) =>
            Task.FromResult(
                true);

        public Task ShowMessageAsync(
            string title,
            string message) =>
            Task.CompletedTask;
    }

    private sealed class CoreDialogs :
        IDialogService
    {
        public bool ConfirmPurge { get; init; }

        public Task<bool> ShowFieldsEditorAsync(
            IReadOnlyList<string> paths) =>
            Task.FromResult(
                false);

        public Task<string?> ShowConfigEditorAsync(
            string? existingPath) =>
            Task.FromResult<string?>(
                null);

        public Task<bool> ConfirmApplyAsync(
            string title,
            string message,
            string primaryText) =>
            Task.FromResult(
                true);

        public Task<bool> ConfirmCdDerivationAsync(
            IngestApprovalItem item) =>
            Task.FromResult(
                false);

        public Task<bool> ConfirmRestoreAsync(
            OperationRestorePlan plan) =>
            Task.FromResult(
                false);

        public Task<bool> ConfirmPurgeAsync(
            OperationPurgePlan plan) =>
            Task.FromResult(
                ConfirmPurge);
    }

    private sealed class KeyLocalizationService :
        ILocalizationService
    {
        public CultureInfo CurrentUICulture { get; } =
            CultureInfo.GetCultureInfo(
                "en-US");

        public IReadOnlyList<CultureInfo>
            SupportedCultures { get; } =
            [
                CultureInfo.GetCultureInfo(
                    "en-US"),
            ];

        public event EventHandler? CultureChanged;

        public string Get(
            string key) =>
            key;

        public string Format(
            string key,
            params object?[] arguments) =>
            key;

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            $"{key}.{(count == 1 ? "One" : "Other")}";

        public IReadOnlyDictionary<string, string>
            Snapshot() =>
            new Dictionary<string, string>();

        public void SetCulture(
            string cultureName) =>
            CultureChanged?.Invoke(
                this,
                EventArgs.Empty);
    }

    private sealed class ManualSynchronizationContext :
        SynchronizationContext
    {
        private readonly ConcurrentQueue<(
            SendOrPostCallback Callback,
            object? State)> _pending = [];

        public override void Post(
            SendOrPostCallback callback,
            object? state) =>
            _pending.Enqueue(
                (callback, state));

        public void RunAll()
        {
            SynchronizationContext? previous =
                Current;
            SetSynchronizationContext(
                this);
            try
            {
                while (_pending.TryDequeue(
                           out var item))
                    item.Callback(
                        item.State);
            }
            finally
            {
                SetSynchronizationContext(
                    previous);
            }
        }
    }

    private sealed class TempTree :
        IDisposable
    {
        public TempTree()
        {
            Path =
                Directory.CreateDirectory(
                    System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        "terminal-progress",
                        Guid.NewGuid().ToString(
                            "N")))
                .FullName;
        }

        public string Path { get; }

        public void Dispose() =>
            Directory.Delete(
                Path,
                recursive: true);
    }
}
