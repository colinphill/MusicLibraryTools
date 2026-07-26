using System.Collections.Immutable;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class WorkbenchReviewedMutationCompositionTests
{
    [Fact]
    public async Task Same_source_mutations_compose_as_one_review_unit_and_apply_in_existing_transaction_order()
    {
        string source = Path.GetFullPath(
            "composed-source.flac");
        string copied = Path.GetFullPath(
            "composed-copy.flac");
        string transcoded = Path.GetFullPath(
            "composed-output.flac");
        MediaDocument document = Document(source);
        var events = new List<string>();
        var metadata =
            new RecordingMetadataService(events);
        var files =
            new RecordingFileOperationService(events);
        var transcodes =
            new RecordingTranscodeService(events);
        WorkbenchViewModel workbench = CreateWorkbench(
            document,
            metadata,
            files,
            transcodes);
        await workbench.AddSourcesAsync([source]);

        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(document)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(source, copied)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(source, transcoded)),
            TestContext.Current.CancellationToken));

        ReviewedMediaMutationUnit unit =
            Assert.Single(
                workbench.PendingMutationUnits);
        Assert.Equal(
            [
                ReviewedMediaMutationKind.Metadata,
                ReviewedMediaMutationKind.FileOperation,
                ReviewedMediaMutationKind.Transcode,
            ],
            unit.MutationKinds);

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(
            [
                "file-preview",
                "metadata-stage",
                "transcode-stage-overrides",
                "transcode-apply",
                "metadata-complete",
                "file-preview",
                "file-apply",
            ],
            events.Where(value =>
                value != "transcode-discard"));
        Assert.Equal(1, metadata.StageCalls);
        Assert.Equal(1, transcodes.ApplyCalls);
        Assert.Equal(1, files.ApplyCalls);
        Assert.Empty(workbench.PendingChanges);
    }

    [Fact]
    public async Task Late_file_operation_failure_retains_only_the_uncommitted_intent_after_metadata_and_transcode_commit()
    {
        string source = Path.GetFullPath(
            "late-file-failure-source.flac");
        string copied = Path.GetFullPath(
            "late-file-failure-copy.flac");
        string transcoded = Path.GetFullPath(
            "late-file-failure-output.flac");
        MediaDocument document = Document(source);
        var events = new List<string>();
        var metadata =
            new RecordingMetadataService(events);
        var files =
            new RecordingFileOperationService(
                events,
                throwOnApply: true);
        var transcodes =
            new RecordingTranscodeService(events);
        WorkbenchViewModel workbench = CreateWorkbench(
            document,
            metadata,
            files,
            transcodes);
        await workbench.AddSourcesAsync([source]);

        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(document)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(source, copied)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(source, transcoded)),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(
            [
                "file-preview",
                "metadata-stage",
                "transcode-stage-overrides",
                "transcode-apply",
                "metadata-complete",
                "file-preview",
                "file-apply",
            ],
            events.Where(value =>
                value != "transcode-discard"));
        Assert.Equal(1, metadata.StageCalls);
        Assert.Equal(1, transcodes.ApplyCalls);
        Assert.Equal(1, files.ApplyCalls);
        ReviewedMediaMutationUnit retained =
            Assert.Single(
                workbench.PendingMutationUnits);
        Assert.Equal(
            source,
            retained.SourcePath);
        Assert.Equal(
            [ReviewedMediaMutationKind.FileOperation],
            retained.MutationKinds);
        MetadataPreviewRow pending =
            Assert.Single(
                workbench.PendingChanges);
        Assert.Equal(
            source,
            pending.Before);
        Assert.Equal(
            copied,
            pending.After);
        Assert.Equal(
            "Injected file-operation apply failure.",
            workbench.StatusDiagnosticDetail);
    }

    [Fact]
    public async Task Disjoint_units_are_accumulated_in_deterministic_path_and_kind_order()
    {
        string alpha = Path.GetFullPath(
            "alpha.flac");
        string middle = Path.GetFullPath(
            "middle.flac");
        string zulu = Path.GetFullPath(
            "zulu.flac");
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(alpha),
            new RecordingMetadataService([]),
            new RecordingFileOperationService([]),
            new RecordingTranscodeService([]));

        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    zulu,
                    Path.GetFullPath(
                        "zulu-output.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    middle,
                    Path.GetFullPath(
                        "middle-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    Document(alpha))),
            TestContext.Current.CancellationToken));

        Assert.Equal(
            [alpha, middle, zulu],
            workbench.PendingMutationUnits
                .Select(unit =>
                    unit.SourcePath));
        Assert.Equal(
            [
                ReviewedMediaMutationKind.Metadata,
                ReviewedMediaMutationKind.FileOperation,
                ReviewedMediaMutationKind.Transcode,
            ],
            workbench.PendingMutationUnits
                .Select(unit =>
                    Assert.Single(
                        unit.MutationKinds)));
    }

    [Fact]
    public async Task Later_blocked_transcode_prevents_earlier_metadata_or_file_commit()
    {
        string source = Path.GetFullPath(
            "blocked-composition.flac");
        MediaDocument document = Document(source);
        var events = new List<string>();
        var metadata =
            new RecordingMetadataService(events);
        var files =
            new RecordingFileOperationService(events);
        var transcodes =
            new RecordingTranscodeService(events);
        WorkbenchViewModel workbench = CreateWorkbench(
            document,
            metadata,
            files,
            transcodes);

        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(document)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    source,
                    Path.GetFullPath(
                        "blocked-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    source,
                    Path.GetFullPath(
                        "blocked-output.flac"),
                    blocked: true)),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Empty(events);
        Assert.Equal(0, metadata.ApplyCalls);
        Assert.Equal(0, metadata.StageCalls);
        Assert.Equal(0, files.ApplyCalls);
        Assert.Equal(0, transcodes.ApplyCalls);
        Assert.Equal(3, workbench.PendingChanges.Count);
    }

    [Fact]
    public async Task File_operation_revalidation_blocker_prevents_earlier_metadata_commit()
    {
        string source = Path.GetFullPath(
            "blocked-file-operation.flac");
        MediaDocument document = Document(source);
        var events = new List<string>();
        var metadata =
            new RecordingMetadataService(events);
        var files =
            new RecordingFileOperationService(
                events,
                blockOnPreviewCall: 1);
        var transcodes =
            new RecordingTranscodeService(events);
        WorkbenchViewModel workbench = CreateWorkbench(
            document,
            metadata,
            files,
            transcodes);

        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(document)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    source,
                    Path.GetFullPath(
                        "blocked-file-copy.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(["file-preview"], events);
        Assert.Equal(0, metadata.ApplyCalls);
        Assert.Equal(0, metadata.StageCalls);
        Assert.Equal(0, files.ApplyCalls);
        Assert.Equal(2, workbench.PendingChanges.Count);
    }

    [Fact]
    public async Task Replacing_transcode_and_file_operation_on_same_source_are_refused_before_commit()
    {
        string source = Path.GetFullPath(
            "replace-conflict.flac");
        MediaDocument document = Document(source);
        var events = new List<string>();
        var metadata =
            new RecordingMetadataService(events);
        var files =
            new RecordingFileOperationService(events);
        var transcodes =
            new RecordingTranscodeService(events);
        WorkbenchViewModel workbench = CreateWorkbench(
            document,
            metadata,
            files,
            transcodes);

        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    source,
                    Path.GetFullPath(
                        "replace-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    source,
                    source,
                    replaceOriginal: true)),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Empty(events);
        Assert.Equal(0, files.ApplyCalls);
        Assert.Equal(0, transcodes.ApplyCalls);
        Assert.Equal(2, workbench.PendingChanges.Count);
    }

    private static WorkbenchViewModel CreateWorkbench(
        MediaDocument document,
        IMetadataOperationService metadata,
        IReviewedFileOperationService fileOperations,
        IAudioTranscodeService transcodes)
    {
        var settings = new FakeSettings();
        var journals = new OperationJournalService(
            new FileMutationCoordinator());
        return new(
            new DocumentWorkbenchService(document),
            metadata,
            new MetadataOperationCatalog(),
            new OperationRecipeStore(settings),
            new FakeAcoustIdDiscoveryService(),
            new FakeMusicBrainzMetadataProvider(),
            new MusicBrainzReleaseMappingService(),
            new FakeCoverArtArchiveProvider(),
            new FakeThumbnails(),
            new EditHistoryService(
                settings,
                journals),
            new FakeFilePicker(),
            new FakeDialogs(),
            settings,
            fileOperations: fileOperations,
            transcodes: transcodes);
    }

    private static MediaDocument Document(
        string path) =>
        new(
            path,
            [
                new(
                    "Vorbis comments",
                    [
                        new(
                            MetadataFieldKey.Known(
                                TagFields.Title),
                            ["Before"]),
                    ],
                    true,
                    true,
                    true,
                    true),
            ],
            [],
            null,
            new(
                path,
                1,
                new DateTime(
                    2026,
                    7,
                    25,
                    12,
                    0,
                    0,
                    DateTimeKind.Utc),
                "metadata-hash"),
            true);

    private static MetadataOperationPlan MetadataPlan(
        MediaDocument document)
    {
        MetadataFieldKey field =
            MetadataFieldKey.Known(
                TagFields.Title);
        return new(
            Guid.NewGuid(),
            "Metadata",
            [
                new(
                    document.Path,
                    document.Snapshot,
                    [
                        new(
                            field,
                            ["Before"],
                            ["After"]),
                    ],
                    [
                        new(
                            field,
                            ["After"]),
                    ],
                    []),
            ],
            DateTimeOffset.UtcNow);
    }

    private static ReviewedFileOperationPlan
        FileOperationPlan(
            string source,
            string destination)
    {
        var request =
            new ReviewedFileOperationRequest(
                [source],
                ReviewedFileOperationKind.Copy,
                Path.GetDirectoryName(destination),
                Path.GetFileName(destination));
        var item = new ReviewedFileOperationItem(
            source,
            destination,
            FileMutationKind.Copy,
            []);
        return new(
            request,
            [item],
            new(
                "test",
                Path.GetDirectoryName(destination)!,
                Path.Combine(
                    Path.GetTempPath(),
                    Guid.NewGuid().ToString("N")),
                [
                    new(
                        FileMutationKind.Copy,
                        source,
                        destination,
                        OperationPathSnapshot.Missing(
                            source),
                        OperationPathSnapshot.Missing(
                            destination)),
                ],
                [],
                DateTimeOffset.UtcNow));
    }

    private static AudioTranscodePlan TranscodePlan(
        string source,
        string destination,
        bool blocked = false,
        bool replaceOriginal = false)
    {
        var settings =
            new AudioTranscodeSettings(
                AudioTranscodeFormatIds.Flac,
                AudioTranscodeEncoderIds.Automatic,
                AudioTranscodeRateMode.Lossless);
        var request = new AudioTranscodeRequest(
            [source],
            settings,
            new(
                replaceOriginal
                    ? AudioTranscodeDestinationMode
                        .ReplaceOriginal
                    : AudioTranscodeDestinationMode
                        .Alongside,
                null,
                true,
                "{Name}{Extension}",
                AudioTranscodeCollisionPolicy.Stop));
        ImmutableArray<OperationIssue> issues =
            blocked
                ?
                [
                    new(
                        "blocked",
                        OperationIssueSeverity.Blocker,
                        "Blocked transcode",
                        source),
                ]
                : [];
        return new(
            Guid.NewGuid(),
            request,
            [
                new(
                    Guid.NewGuid(),
                    source,
                    destination,
                    OperationPathSnapshot.Missing(
                        source),
                    OperationPathSnapshot.Missing(
                        destination),
                    "",
                    settings,
                    issues),
            ],
            [],
            DateTimeOffset.UtcNow,
            1);
    }

    private sealed class DocumentWorkbenchService(
        MediaDocument document) :
        IWorkbenchService
    {
        public Task<WorkbenchLoadResult> LoadAsync(
            WorkbenchLoadRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            bool includesSource =
                request.Sources.Any(path =>
                    StringComparer.OrdinalIgnoreCase.Equals(
                        Path.GetFullPath(path),
                        document.Path));
            return Task.FromResult(
                new WorkbenchLoadResult(
                    includesSource
                        ? [document]
                        : [],
                    []));
        }
    }

    private sealed class RecordingMetadataService(
        List<string> events) :
        FakeMetadataOperationService,
        IMetadataOperationService
    {
        public int ApplyCalls { get; private set; }
        public int StageCalls { get; private set; }

        public override Task<MetadataApplyResult> ApplyAsync(
            MetadataOperationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            events.Add("metadata-apply");
            ApplyCalls++;
            return base.ApplyAsync(
                plan,
                progress,
                ct);
        }

        public Task<MetadataOperationStageResult> StageAsync(
            MetadataOperationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            events.Add("metadata-stage");
            StageCalls++;
            return Task.FromResult(
                new MetadataOperationStageResult(
                    plan,
                    [],
                    [
                        .. plan.Files
                            .Where(file =>
                                file.HasChanges)
                            .Select(file =>
                                new MetadataStagedFile(
                                    file.Path,
                                    file.Path +
                                    ".metadata-stage")),
                    ]));
        }

        public Task CompleteStagedApplyAsync(
            MetadataOperationStageResult stage,
            IReadOnlyList<string> journalPaths,
            bool recordHistory,
            CancellationToken ct = default)
        {
            events.Add("metadata-complete");
            return Task.CompletedTask;
        }

        public Task DiscardStageAsync(
            MetadataOperationStageResult stage,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingFileOperationService(
        List<string> events,
        int? blockOnPreviewCall = null,
        bool throwOnApply = false) :
        IReviewedFileOperationService
    {
        public int ApplyCalls { get; private set; }
        public int PreviewCalls { get; private set; }

        public Task<ReviewedFileOperationPlan> PreviewAsync(
            ReviewedFileOperationRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            events.Add("file-preview");
            PreviewCalls++;
            string source =
                Assert.Single(
                    request.SourcePaths);
            string destination = Path.Combine(
                request.DestinationDirectory!,
                request.FileNameTemplate);
            ReviewedFileOperationPlan plan =
                FileOperationPlan(
                    source,
                    destination);
            if (blockOnPreviewCall == PreviewCalls)
            {
                var blocker = new OperationIssue(
                    "file-operation.blocked",
                    OperationIssueSeverity.Blocker,
                    "Blocked file operation",
                    source);
                plan = plan with
                {
                    Items =
                    [
                        .. plan.Items.Select(item =>
                            item with
                            {
                                Issues = [blocker],
                            }),
                    ],
                    MutationPlan =
                        plan.MutationPlan with
                        {
                            Issues = [blocker],
                        },
                };
            }
            return Task.FromResult(
                plan);
        }

        public Task<FileMutationSummary> ApplyAsync(
            ReviewedFileOperationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            events.Add("file-apply");
            ApplyCalls++;
            if (throwOnApply)
                throw new InvalidOperationException(
                    "Injected file-operation apply failure.");
            return Task.FromResult(
                new FileMutationSummary(
                    plan.MutationPlan.Actions.Count,
                    0,
                    0,
                    0,
                    null,
                    []));
        }
    }

    private sealed class RecordingTranscodeService(
        List<string> events) :
        IAudioTranscodeService
    {
        public int ApplyCalls { get; private set; }

        public Task<AudioTranscodePlan> PreviewAsync(
            AudioTranscodeRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AudioTranscodeStageResult> StageAsync(
            AudioTranscodePlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            events.Add("transcode-stage");
            return Task.FromResult(
                ReadyStage(plan));
        }

        public Task<AudioTranscodeStageResult>
            StageWithSourceOverridesAsync(
                AudioTranscodePlan plan,
                IReadOnlyDictionary<string, string>
                    sourceOverrides,
                IProgress<OperationProgress>? progress = null,
                CancellationToken ct = default)
        {
            Assert.Single(sourceOverrides);
            events.Add(
                "transcode-stage-overrides");
            return Task.FromResult(
                ReadyStage(plan));
        }

        public Task<AudioTranscodeApplyResult> ApplyAsync(
            AudioTranscodeStageResult stage,
            IReadOnlySet<Guid>? readyItemIds = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AudioTranscodeApplyResult> ApplyBatchAsync(
            IReadOnlyList<AudioTranscodeStageResult> stages,
            IReadOnlySet<Guid>? readyItemIds = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            ApplyReviewedBatchAsync(
                stages,
                [],
                readyItemIds,
                progress,
                ct);

        public Task<AudioTranscodeApplyResult>
            ApplyReviewedBatchAsync(
                IReadOnlyList<AudioTranscodeStageResult>
                    stages,
                IReadOnlyList<FileMutationPlan>
                    additionalParticipants,
                IReadOnlySet<Guid>? readyItemIds = null,
                IProgress<OperationProgress>? progress = null,
                CancellationToken ct = default)
        {
            events.Add("transcode-apply");
            ApplyCalls++;
            AudioTranscodeStagedItem[] ready =
            [
                .. stages.SelectMany(stage =>
                    stage.ReadyItems)
                    .Where(item =>
                        readyItemIds is null ||
                        readyItemIds.Contains(
                            item.PlanItem.Id)),
            ];
            return Task.FromResult(
                new AudioTranscodeApplyResult(
                    ready.Length,
                    [],
                    [
                        .. ready.Select(item =>
                            item.PlanItem.SourcePath),
                    ],
                    [
                        .. ready.Select(item =>
                            item.PlanItem
                                .DestinationPath),
                    ],
                    []));
        }

        public Task DiscardStageAsync(
            AudioTranscodeStageResult stage,
            CancellationToken ct = default)
        {
            events.Add("transcode-discard");
            return Task.CompletedTask;
        }

        private static AudioTranscodeStageResult
            ReadyStage(
                AudioTranscodePlan plan) =>
            new(
                plan,
                [
                    .. plan.Items.Select(item =>
                        new AudioTranscodeStagedItem(
                            item,
                            AudioTranscodeStageState.Ready,
                            item.DestinationPath +
                            ".stage",
                            "hash",
                            1)),
                ]);
    }
}
