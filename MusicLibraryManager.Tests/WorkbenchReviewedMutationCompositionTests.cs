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
        int fileCommitNotifications = 0;
        workbench.FileOperationsCommitted += plans =>
        {
            fileCommitNotifications++;
            return Task.CompletedTask;
        };

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
        Assert.Equal(1, fileCommitNotifications);
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
        int fileCommitNotifications = 0;
        workbench.FileOperationsCommitted += plans =>
        {
            fileCommitNotifications++;
            return Task.CompletedTask;
        };

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
        Assert.Equal(0, fileCommitNotifications);
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
        Assert.Contains(
            "Applied",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Committed_file_intent_is_consumed_before_throwing_refresh_and_subscriber_failure_is_a_warning()
    {
        string source = Path.GetFullPath(
            "file-commit-source.flac");
        string destination = Path.GetFullPath(
            "file-commit-copy.flac");
        MediaDocument document = Document(source);
        var events = new List<string>();
        var files =
            new RecordingFileOperationService(events);
        var loader =
            new DocumentWorkbenchService(
                document,
                throwAfterLoadCall: 1);
        WorkbenchViewModel workbench = CreateWorkbench(
            document,
            new RecordingMetadataService(events),
            files,
            new RecordingTranscodeService(events),
            loader);
        await workbench.AddSourcesAsync([source]);
        int notifications = 0;
        bool observedAfterApply = false;
        IReadOnlyList<ReviewedFileOperationPlan>?
            committed = null;
        workbench.FileOperationsCommitted += plans =>
        {
            notifications++;
            observedAfterApply =
                files.ApplyCompleted;
            committed = plans;
            throw new InvalidOperationException(
                "Injected subscriber refresh failure.");
        };
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    source,
                    destination)),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);
        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, files.ApplyCalls);
        Assert.Equal(1, notifications);
        Assert.True(observedAfterApply);
        ReviewedFileOperationPlan committedPlan =
            Assert.Single(committed!);
        FileMutationAction committedAction =
            Assert.Single(
                committedPlan.MutationPlan.Actions);
        Assert.Equal(source, committedAction.SourcePath);
        Assert.Equal(
            destination,
            committedAction.DestinationPath);
        Assert.Empty(workbench.PendingChanges);
        Assert.False(workbench.HasUnsavedChanges);
        Assert.Contains(
            "Injected subscriber refresh failure.",
            workbench.StatusDiagnosticDetail);
        Assert.Contains(
            "Injected workbench refresh failure.",
            workbench.StatusDiagnosticDetail);
        Assert.Contains(
            "Applied",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        ReviewedFileOperationKind.Copy,
        FileMutationKind.Copy)]
    [InlineData(
        ReviewedFileOperationKind.Move,
        FileMutationKind.Move)]
    [InlineData(
        ReviewedFileOperationKind.Rename,
        FileMutationKind.Move)]
    [InlineData(
        ReviewedFileOperationKind.Quarantine,
        FileMutationKind.Quarantine)]
    public async Task File_commit_notification_preserves_the_reviewed_operation_kind_matrix(
        ReviewedFileOperationKind operationKind,
        FileMutationKind mutationKind)
    {
        string source = Path.GetFullPath(
            $"kind-{operationKind}-source.flac");
        string destination = Path.GetFullPath(
            $"kind-{operationKind}-destination.flac");
        var files =
            new RecordingFileOperationService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(source),
            new RecordingMetadataService([]),
            files,
            new RecordingTranscodeService([]));
        int notifications = 0;
        IReadOnlyList<ReviewedFileOperationPlan>?
            committed = null;
        workbench.FileOperationsCommitted += plans =>
        {
            notifications++;
            committed = plans;
            return Task.CompletedTask;
        };
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    source,
                    destination,
                    operationKind,
                    mutationKind)),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, files.ApplyCalls);
        Assert.Equal(1, notifications);
        ReviewedFileOperationPlan plan =
            Assert.Single(committed!);
        Assert.Equal(
            operationKind,
            plan.Request.Kind);
        Assert.Equal(
            mutationKind,
            Assert.Single(plan.Items).MutationKind);
        Assert.Equal(
            mutationKind,
            Assert.Single(
                plan.MutationPlan.Actions).Kind);
        Assert.Empty(workbench.PendingChanges);
    }

    [Fact]
    public async Task Partial_transcode_defers_overlapping_file_action_but_applies_and_notifies_disjoint_action()
    {
        string failedSource = Path.GetFullPath(
            "partial-failed.flac");
        string readySource = Path.GetFullPath(
            "partial-ready.flac");
        string failedCopy = Path.GetFullPath(
            "partial-failed-copy.flac");
        string readyCopy = Path.GetFullPath(
            "partial-ready-copy.flac");
        MediaDocument failedDocument =
            Document(failedSource);
        MediaDocument readyDocument =
            Document(readySource);
        var events = new List<string>();
        var metadata =
            new RecordingMetadataService(events);
        var files =
            new RecordingFileOperationService(events);
        var dialogs =
            new RecordingDecisionDialogs(
                accept: true,
                events);
        var transcodes =
            new RecordingTranscodeService(
                events,
                [failedSource]);
        WorkbenchViewModel workbench = CreateWorkbench(
            failedDocument,
            metadata,
            files,
            transcodes,
            dialogs: dialogs);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    failedDocument,
                    readyDocument)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    failedSource,
                    failedCopy)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    readySource,
                    readyCopy)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                MultiTranscodePlan(
                    (
                        failedSource,
                        Path.GetFullPath(
                            "partial-failed-output.flac")),
                    (
                        readySource,
                        Path.GetFullPath(
                            "partial-ready-output.flac")))),
            TestContext.Current.CancellationToken));
        int notifications = 0;
        IReadOnlyList<ReviewedFileOperationPlan>?
            committed = null;
        workbench.FileOperationsCommitted += plans =>
        {
            notifications++;
            committed = plans;
            return Task.CompletedTask;
        };

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ConfirmationCalls);
        Assert.Equal(1, transcodes.ApplyCalls);
        Assert.Equal(1, files.ApplyCalls);
        Assert.Equal(1, notifications);
        Assert.All(
            Assert.Single(committed!)
                .MutationPlan.Actions,
            action => Assert.Equal(
                readySource,
                action.SourcePath));
        Assert.All(
            Assert.Single(files.AppliedPlans)
                .MutationPlan.Actions,
            action => Assert.Equal(
                readySource,
                action.SourcePath));
        ReviewedMediaMutationUnit retained =
            Assert.Single(
                workbench.PendingMutationUnits);
        Assert.Equal(failedSource, retained.SourcePath);
        Assert.Equal(
            [
                ReviewedMediaMutationKind.Metadata,
                ReviewedMediaMutationKind.FileOperation,
                ReviewedMediaMutationKind.Transcode,
            ],
            retained.MutationKinds);
        Assert.Equal(3, workbench.PendingChanges.Count);
        Assert.Contains(
            workbench.PendingChanges,
            row => row.Before == failedSource &&
                row.After == failedCopy);
        Assert.DoesNotContain(
            workbench.PendingChanges,
            row => row.Before == readySource);
    }

    [Fact]
    public async Task Cancelled_transcode_and_file_operation_retain_original_multi_source_ordinal_context_for_retry()
    {
        string cancelledSource = Path.GetFullPath(
            "ordinal-cancelled.flac");
        string readySource = Path.GetFullPath(
            "ordinal-ready.flac");
        string destinationDirectory = Path.GetFullPath(
            "ordinal-copies");
        string cancelledCopy = Path.Combine(
            destinationDirectory,
            "copy-1.flac");
        string readyCopy = Path.Combine(
            destinationDirectory,
            "copy-2.flac");
        var events = new List<string>();
        var files =
            new RecordingFileOperationService(events);
        var dialogs =
            new RecordingDecisionDialogs(
                accept: true,
                events);
        var transcodes =
            new RecordingTranscodeService(
                events,
                cancelledSources: [cancelledSource]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(cancelledSource),
            new RecordingMetadataService(events),
            files,
            transcodes,
            dialogs: dialogs);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    new(
                        [cancelledSource, readySource],
                        ReviewedFileOperationKind.Copy,
                        destinationDirectory,
                        "copy-{Index}{Extension}"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                MultiTranscodePlan(
                    (
                        cancelledSource,
                        Path.GetFullPath(
                            "ordinal-cancelled-output.flac")),
                    (
                        readySource,
                        Path.GetFullPath(
                            "ordinal-ready-output.flac")))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ConfirmationCalls);
        Assert.Equal(
            readyCopy,
            Assert.Single(
                Assert.Single(files.AppliedPlans)
                    .MutationPlan.Actions)
                .DestinationPath);
        Assert.Contains(
            workbench.PendingChanges,
            row => row.Before == cancelledSource &&
                row.After == cancelledCopy);
        Assert.DoesNotContain(
            workbench.PendingChanges,
            row => row.Before == readySource);

        transcodes.AllowAllSources();
        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(2, files.ApplyCalls);
        Assert.Equal(
            cancelledCopy,
            Assert.Single(
                files.AppliedPlans[1]
                    .MutationPlan.Actions)
                .DestinationPath);
        Assert.Empty(workbench.PendingChanges);
        Assert.False(workbench.HasUnsavedChanges);
    }

    [Fact]
    public async Task All_failed_transcodes_can_apply_disjoint_file_work_after_one_batch_decision()
    {
        string failedSource = Path.GetFullPath(
            "all-failed-file-transcode.flac");
        string fileSource = Path.GetFullPath(
            "all-failed-file-ready.flac");
        string fileDestination = Path.GetFullPath(
            "all-failed-file-copy.flac");
        var events = new List<string>();
        var dialogs =
            new RecordingDecisionDialogs(
                accept: true,
                events);
        var files =
            new RecordingFileOperationService(events);
        var transcodes =
            new RecordingTranscodeService(
                events,
                failedSources: [failedSource]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(failedSource),
            new RecordingMetadataService(events),
            files,
            transcodes,
            dialogs: dialogs);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    fileSource,
                    fileDestination)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    failedSource,
                    Path.GetFullPath(
                        "all-failed-file-output.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ConfirmationCalls);
        Assert.Equal(0, transcodes.ApplyCalls);
        Assert.Equal(1, transcodes.DiscardCalls);
        Assert.Equal(1, files.ApplyCalls);
        Assert.True(
            events.IndexOf("partial-confirm") <
            events.IndexOf("file-apply"));
        MetadataPreviewRow retained =
            Assert.Single(
                workbench.PendingChanges);
        Assert.Equal(
            failedSource,
            retained.Before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task All_failed_transcodes_apply_disjoint_metadata_only_when_batch_decision_is_accepted(
        bool accept)
    {
        string failedSource = Path.GetFullPath(
            $"all-failed-metadata-transcode-{accept}.flac");
        MediaDocument metadataDocument = Document(
            Path.GetFullPath(
                $"all-failed-metadata-ready-{accept}.flac"));
        var events = new List<string>();
        var dialogs =
            new RecordingDecisionDialogs(
                accept,
                events);
        var metadata =
            new RecordingMetadataService(events);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(failedSource),
            metadata,
            new RecordingFileOperationService(events),
            new RecordingTranscodeService(
                events,
                failedSources: [failedSource]),
            dialogs: dialogs);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    metadataDocument)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    failedSource,
                    Path.GetFullPath(
                        $"all-failed-metadata-output-{accept}.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ConfirmationCalls);
        Assert.Equal(
            accept ? 1 : 0,
            metadata.ApplyCalls);
        Assert.Equal(
            accept ? 1 : 2,
            workbench.PendingChanges.Count);
        if (accept)
            Assert.True(
                events.IndexOf("partial-confirm") <
                events.IndexOf("metadata-apply"));
        else
            Assert.DoesNotContain(
                "metadata-apply",
                events);
    }

    [Fact]
    public async Task All_failed_transcodes_can_apply_disjoint_metadata_and_file_work_as_one_decision()
    {
        string failedSource = Path.GetFullPath(
            "all-failed-both-transcode.flac");
        MediaDocument metadataDocument = Document(
            Path.GetFullPath(
                "all-failed-both-metadata.flac"));
        string fileSource = Path.GetFullPath(
            "all-failed-both-file.flac");
        var events = new List<string>();
        var dialogs =
            new RecordingDecisionDialogs(
                accept: true,
                events);
        var metadata =
            new RecordingMetadataService(events);
        var files =
            new RecordingFileOperationService(events);
        var transcodes =
            new RecordingTranscodeService(
                events,
                failedSources: [failedSource]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(failedSource),
            metadata,
            files,
            transcodes,
            dialogs: dialogs);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    metadataDocument)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    fileSource,
                    Path.GetFullPath(
                        "all-failed-both-file-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    failedSource,
                    Path.GetFullPath(
                        "all-failed-both-output.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ConfirmationCalls);
        Assert.Equal(1, metadata.ApplyCalls);
        Assert.Equal(1, files.ApplyCalls);
        Assert.True(
            events.IndexOf("partial-confirm") <
            events.IndexOf("metadata-apply"));
        Assert.True(
            events.IndexOf("partial-confirm") <
            events.IndexOf("file-apply"));
        MetadataPreviewRow retained =
            Assert.Single(
                workbench.PendingChanges);
        Assert.Equal(
            failedSource,
            retained.Before);
    }

    [Fact]
    public async Task Back_commits_nothing_from_all_failed_transcode_metadata_and_file_batch()
    {
        string failedSource = Path.GetFullPath(
            "all-failed-back-transcode.flac");
        MediaDocument metadataDocument = Document(
            Path.GetFullPath(
                "all-failed-back-metadata.flac"));
        string fileSource = Path.GetFullPath(
            "all-failed-back-file.flac");
        var events = new List<string>();
        var dialogs =
            new RecordingDecisionDialogs(
                accept: false,
                events);
        var metadata =
            new RecordingMetadataService(events);
        var files =
            new RecordingFileOperationService(events);
        var transcodes =
            new RecordingTranscodeService(
                events,
                failedSources: [failedSource]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(failedSource),
            metadata,
            files,
            transcodes,
            dialogs: dialogs);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(metadataDocument)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    fileSource,
                    Path.GetFullPath(
                        "all-failed-back-file-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    failedSource,
                    Path.GetFullPath(
                        "all-failed-back-output.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ConfirmationCalls);
        Assert.Equal(0, metadata.ApplyCalls);
        Assert.Equal(0, transcodes.ApplyCalls);
        Assert.Equal(0, files.ApplyCalls);
        Assert.Equal(1, transcodes.DiscardCalls);
        Assert.Equal(
            3,
            workbench.PendingChanges.Count);
        Assert.DoesNotContain(
            events,
            value => value.EndsWith(
                "-apply",
                StringComparison.Ordinal));
        Assert.Contains(
            "Back",
            workbench.StatusDiagnosticDetail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "stopped safely",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Back_retains_staged_transcode_error_code_and_diagnostic_detail_in_review()
    {
        string failedSource = Path.GetFullPath(
            "retained-stage-diagnostic.flac");
        MediaDocument readyMetadata = Document(
            Path.GetFullPath(
                "retained-stage-diagnostic-metadata.flac"));
        var events = new List<string>();
        var dialogs =
            new RecordingDecisionDialogs(
                accept: false,
                events);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(failedSource),
            new RecordingMetadataService(events),
            new RecordingFileOperationService(events),
            new RecordingTranscodeService(
                events,
                failedSources: [failedSource]),
            dialogs: dialogs);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    readyMetadata)),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    failedSource,
                    Path.GetFullPath(
                        "retained-stage-diagnostic-output.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        MetadataPreviewRow retained =
            Assert.Single(
                workbench.PendingChanges,
                row => row.Field.Contains(
                    "Transcode",
                    StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            "injected",
            retained.DiagnosticDetail);
        Assert.Contains(
            "Injected transcode failure.",
            retained.DiagnosticDetail);
    }

    [Fact]
    public async Task File_intent_added_while_partial_decision_is_open_is_not_applied_by_that_decision()
    {
        string failedSource = Path.GetFullPath(
            "decision-race-failed.flac");
        string includedSource = Path.GetFullPath(
            "decision-race-included.flac");
        string lateSource = Path.GetFullPath(
            "decision-race-late.flac");
        string lateMetadata = Path.GetFullPath(
            "decision-race-late-metadata.flac");
        string lateTranscode = Path.GetFullPath(
            "decision-race-late-transcode.flac");
        var events = new List<string>();
        var dialogs =
            new BlockingDecisionDialogs(events);
        var files =
            new RecordingFileOperationService(events);
        var transcodes =
            new RecordingTranscodeService(
                events,
                failedSources: [failedSource]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(failedSource),
            new RecordingMetadataService(events),
            files,
            transcodes,
            dialogs: dialogs);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    includedSource,
                    Path.GetFullPath(
                        "decision-race-included-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    failedSource,
                    Path.GetFullPath(
                        "decision-race-output.flac"))),
            TestContext.Current.CancellationToken));

        Task apply =
            workbench.ApplyCommand.ExecuteAsync(null);
        await dialogs.ConfirmationStarted.Task
            .WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current
                    .CancellationToken);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    lateSource,
                    Path.GetFullPath(
                        "decision-race-late-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    Document(lateMetadata))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    lateTranscode,
                    Path.GetFullPath(
                        "decision-race-late-transcode-output.flac"))),
            TestContext.Current.CancellationToken));
        dialogs.ReleaseConfirmation.SetResult(true);
        await apply;

        FileMutationAction appliedAction =
            Assert.Single(
                Assert.Single(files.AppliedPlans)
                    .MutationPlan.Actions);
        Assert.Equal(
            includedSource,
            appliedAction.SourcePath);
        Assert.Contains(
            workbench.PendingChanges,
            row => row.Before == lateSource);
        Assert.Contains(
            workbench.PendingChanges,
            row => row.Before == failedSource);
        Assert.Contains(
            workbench.PendingMutationUnits,
            unit => unit.SourcePath == lateMetadata);
        Assert.Contains(
            workbench.PendingMutationUnits,
            unit => unit.SourcePath == lateTranscode);
        Assert.Equal(0, transcodes.ApplyCalls);
    }

    [Fact]
    public async Task File_operation_preflight_block_invokes_finished_and_prevents_executor()
    {
        string source = Path.GetFullPath(
            "preflight-block-source.flac");
        string destination = Path.GetFullPath(
            "preflight-block-copy.flac");
        var files =
            new RecordingFileOperationService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(source),
            new RecordingMetadataService([]),
            files,
            new RecordingTranscodeService([]));
        int secondPreflightCalls = 0;
        int finishedCalls = 0;
        IReadOnlyList<ReviewedFileOperationPlan>?
            finishedPlans = null;
        workbench.FileOperationsPreflight += _ =>
            throw new InvalidOperationException(
                "Injected preflight failure.");
        workbench.FileOperationsPreflight += plans =>
        {
            secondPreflightCalls++;
            Assert.Equal(
                destination,
                Assert.Single(
                    Assert.Single(plans)
                        .MutationPlan.Actions)
                    .DestinationPath);
            return Task.FromResult<string?>(
                "Library has an active draft.");
        };
        workbench.FileOperationsApplyFinished += plans =>
        {
            finishedCalls++;
            finishedPlans = plans;
            return Task.CompletedTask;
        };
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    source,
                    destination)),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, secondPreflightCalls);
        Assert.Equal(1, finishedCalls);
        Assert.Equal(destination, Assert.Single(
            Assert.Single(finishedPlans!)
                .MutationPlan.Actions).DestinationPath);
        Assert.Equal(0, files.ApplyCalls);
        Assert.Single(workbench.PendingChanges);
        Assert.Contains(
            "Library has an active draft.",
            workbench.StatusDiagnosticDetail);
        Assert.Contains(
            "pending",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task File_operation_finished_runs_when_executor_fails()
    {
        string source = Path.GetFullPath(
            "preflight-failed-apply-source.flac");
        var files =
            new RecordingFileOperationService(
                [],
                throwOnApply: true);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(source),
            new RecordingMetadataService([]),
            files,
            new RecordingTranscodeService([]));
        int preflightCalls = 0;
        int finishedCalls = 0;
        workbench.FileOperationsPreflight += _ =>
        {
            preflightCalls++;
            return Task.FromResult<string?>(null);
        };
        workbench.FileOperationsApplyFinished += _ =>
        {
            finishedCalls++;
            return Task.CompletedTask;
        };
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    source,
                    Path.GetFullPath(
                        "preflight-failed-apply-copy.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, preflightCalls);
        Assert.Equal(1, finishedCalls);
        Assert.Equal(1, files.ApplyCalls);
        Assert.Single(workbench.PendingChanges);
    }

    [Fact]
    public async Task File_operation_finished_subscriber_failure_is_a_warning_after_durable_apply()
    {
        string source = Path.GetFullPath(
            "finished-warning-source.flac");
        var files =
            new RecordingFileOperationService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(source),
            new RecordingMetadataService([]),
            files,
            new RecordingTranscodeService([]));
        workbench.FileOperationsPreflight += _ =>
            Task.FromResult<string?>(null);
        workbench.FileOperationsApplyFinished += _ =>
            throw new InvalidOperationException(
                "Injected finished subscriber failure.");
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    source,
                    Path.GetFullPath(
                        "finished-warning-copy.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, files.ApplyCalls);
        Assert.Empty(workbench.PendingChanges);
        Assert.Contains(
            "Applied",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Injected finished subscriber failure.",
            workbench.StatusDiagnosticDetail);
    }

    [Fact]
    public async Task Metadata_commit_is_not_retryable_when_document_refresh_throws()
    {
        string source = Path.GetFullPath(
            "metadata-refresh-failure.flac");
        MediaDocument document = Document(source);
        var metadata =
            new RecordingMetadataService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            document,
            metadata,
            new RecordingFileOperationService([]),
            new RecordingTranscodeService([]),
            new DocumentWorkbenchService(
                document,
                throwAfterLoadCall: 1));
        await workbench.AddSourcesAsync([source]);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(document)),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);
        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, metadata.ApplyCalls);
        Assert.Empty(workbench.PendingChanges);
        Assert.False(workbench.HasUnsavedChanges);
        Assert.Contains(
            "Injected workbench refresh failure.",
            workbench.StatusDiagnosticDetail);
        Assert.Contains(
            "Applied",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Metadata_commit_surfaces_nonthrowing_document_refresh_issues()
    {
        string source = Path.GetFullPath(
            "metadata-refresh-warning.flac");
        MediaDocument document = Document(source);
        var metadata =
            new RecordingMetadataService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            document,
            metadata,
            new RecordingFileOperationService([]),
            new RecordingTranscodeService([]),
            new DocumentWorkbenchService(
                document,
                loadIssues:
                [
                    new(
                        "workbench.refresh-warning",
                        OperationIssueSeverity.Warning,
                        "Injected workbench refresh warning.",
                        source),
                ]));
        await workbench.AddSourcesAsync([source]);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(document)),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, metadata.ApplyCalls);
        Assert.Empty(workbench.PendingChanges);
        Assert.Contains(
            "Injected workbench refresh warning.",
            workbench.StatusDiagnosticDetail);
        Assert.Contains(
            "Applied",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transcode_commit_is_not_retryable_when_document_refresh_throws()
    {
        string source = Path.GetFullPath(
            "transcode-refresh-failure.flac");
        string destination = Path.GetFullPath(
            "transcode-refresh-output.flac");
        MediaDocument document = Document(source);
        var transcodes =
            new RecordingTranscodeService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            document,
            new RecordingMetadataService([]),
            new RecordingFileOperationService([]),
            transcodes,
            new DocumentWorkbenchService(
                document,
                throwAfterLoadCall: 1));
        await workbench.AddSourcesAsync([source]);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    source,
                    destination)),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);
        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, transcodes.ApplyCalls);
        Assert.Empty(workbench.PendingChanges);
        Assert.False(workbench.HasUnsavedChanges);
        Assert.Contains(
            "Injected workbench refresh failure.",
            workbench.StatusDiagnosticDetail);
        Assert.Contains(
            "Applied",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Undo_commit_surfaces_history_and_refresh_warnings_without_becoming_retryable()
    {
        string source = Path.GetFullPath(
            "undo-refresh-failure.flac");
        MediaDocument document = Document(source);
        var entry = new EditHistoryEntry(
            Guid.NewGuid(),
            "Applied metadata",
            DateTimeOffset.UtcNow,
            [],
            [source],
            null);
        var history = new RecordingUndoHistory(
            entry,
            [
                new(
                    "undo.catalog-warning",
                    OperationIssueSeverity.Warning,
                    "Injected undo reconciliation warning.",
                    source),
            ]);
        WorkbenchViewModel workbench = CreateWorkbench(
            document,
            new RecordingMetadataService([]),
            new RecordingFileOperationService([]),
            new RecordingTranscodeService([]),
            new DocumentWorkbenchService(
                document,
                throwAfterLoadCall: 1),
            history);
        await workbench.AddSourcesAsync([source]);

        await workbench.UndoCommand.ExecuteAsync(null);

        Assert.Equal(1, history.UndoCalls);
        Assert.False(history.CanUndo);
        Assert.False(
            workbench.UndoCommand.CanExecute(null));
        Assert.Contains(
            "Injected undo reconciliation warning.",
            workbench.StatusDiagnosticDetail);
        Assert.Contains(
            "Injected workbench refresh failure.",
            workbench.StatusDiagnosticDetail);
        Assert.Contains(
            "Restored",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "failed",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "cancel",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_entry_snapshot_excludes_intents_added_during_initial_file_refresh()
    {
        string included = Path.GetFullPath(
            "entry-refresh-included.flac");
        string lateMetadata = Path.GetFullPath(
            "entry-refresh-late-metadata.flac");
        string lateFile = Path.GetFullPath(
            "entry-refresh-late-file.flac");
        string lateTranscode = Path.GetFullPath(
            "entry-refresh-late-transcode.flac");
        var metadata = new RecordingMetadataService([]);
        var files = new RecordingFileOperationService(
            [],
            pauseOnPreviewCall: 1);
        var transcodes = new RecordingTranscodeService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(included),
            metadata,
            files,
            transcodes);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    included,
                    Path.GetFullPath(
                        "entry-refresh-included-copy.flac"))),
            TestContext.Current.CancellationToken));

        Task apply =
            workbench.ApplyCommand.ExecuteAsync(null);
        await files.PreviewPaused.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    Document(lateMetadata))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    lateFile,
                    Path.GetFullPath(
                        "entry-refresh-late-file-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    lateTranscode,
                    Path.GetFullPath(
                        "entry-refresh-late-output.flac"))),
            TestContext.Current.CancellationToken));
        files.ReleasePreview.TrySetResult(true);

        await apply;

        FileMutationAction applied =
            Assert.Single(
                Assert.Single(files.AppliedPlans)
                    .MutationPlan.Actions);
        Assert.Equal(included, applied.SourcePath);
        Assert.Equal(0, metadata.ApplyCalls);
        Assert.Equal(0, metadata.StageCalls);
        Assert.Equal(0, transcodes.ApplyCalls);
        Assert.Equal(
            [lateFile, lateMetadata, lateTranscode],
            workbench.PendingMutationUnits
                .Select(unit => unit.SourcePath)
                .OrderBy(path => path));
    }

    [Fact]
    public async Task Apply_entry_snapshot_excludes_intents_added_during_direct_preview()
    {
        string directSource = Path.GetFullPath(
            "entry-direct-source.flac");
        string lateMetadata = Path.GetFullPath(
            "entry-direct-late-metadata.flac");
        string lateFile = Path.GetFullPath(
            "entry-direct-late-file.flac");
        string lateTranscode = Path.GetFullPath(
            "entry-direct-late-transcode.flac");
        var metadata = new RecordingMetadataService(
            [],
            pauseValuePreview: true);
        var files = new RecordingFileOperationService([]);
        var transcodes = new RecordingTranscodeService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(directSource),
            metadata,
            files,
            transcodes);
        await workbench.AddSourcesAsync([directSource]);
        Assert.Single(workbench.Files).Title =
            "Captured direct edit";

        Task apply =
            workbench.ApplyCommand.ExecuteAsync(null);
        await metadata.ValuePreviewStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    Document(lateMetadata))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    lateFile,
                    Path.GetFullPath(
                        "entry-direct-late-file-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    lateTranscode,
                    Path.GetFullPath(
                        "entry-direct-late-output.flac"))),
            TestContext.Current.CancellationToken));
        metadata.ReleaseValuePreview.TrySetResult(true);

        await apply;

        Assert.Equal(1, metadata.ApplyCalls);
        Assert.Equal(0, files.ApplyCalls);
        Assert.Equal(0, transcodes.ApplyCalls);
        Assert.False(
            Assert.Single(workbench.Files).HasChanges);
        Assert.Equal(
            [lateFile, lateMetadata, lateTranscode],
            workbench.PendingMutationUnits
                .Select(unit => unit.SourcePath)
                .OrderBy(path => path));
    }

    [Fact]
    public async Task Apply_entry_snapshot_excludes_intents_added_during_metadata_stage()
    {
        string included = Path.GetFullPath(
            "entry-stage-included.flac");
        string lateMetadata = Path.GetFullPath(
            "entry-stage-late-metadata.flac");
        string lateFile = Path.GetFullPath(
            "entry-stage-late-file.flac");
        string lateTranscode = Path.GetFullPath(
            "entry-stage-late-transcode.flac");
        var metadata = new RecordingMetadataService(
            [],
            pauseStage: true);
        var files = new RecordingFileOperationService([]);
        var transcodes = new RecordingTranscodeService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(included),
            metadata,
            files,
            transcodes);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    Document(included))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    included,
                    Path.GetFullPath(
                        "entry-stage-included-output.flac"))),
            TestContext.Current.CancellationToken));

        Task apply =
            workbench.ApplyCommand.ExecuteAsync(null);
        await metadata.StageStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    Document(lateMetadata))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    lateFile,
                    Path.GetFullPath(
                        "entry-stage-late-file-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    lateTranscode,
                    Path.GetFullPath(
                        "entry-stage-late-output.flac"))),
            TestContext.Current.CancellationToken));
        metadata.ReleaseStage.TrySetResult(true);

        await apply;

        Assert.Equal(1, metadata.StageCalls);
        Assert.Equal(1, transcodes.ApplyCalls);
        Assert.Equal(0, files.ApplyCalls);
        Assert.Equal(
            [lateFile, lateMetadata, lateTranscode],
            workbench.PendingMutationUnits
                .Select(unit => unit.SourcePath)
                .OrderBy(path => path));
    }

    [Fact]
    public async Task Apply_entry_snapshot_does_not_take_late_transcode_branch_during_metadata_apply()
    {
        string included = Path.GetFullPath(
            "entry-apply-included.flac");
        string lateMetadata = Path.GetFullPath(
            "entry-apply-late-metadata.flac");
        string lateFile = Path.GetFullPath(
            "entry-apply-late-file.flac");
        string lateTranscode = Path.GetFullPath(
            "entry-apply-late-transcode.flac");
        var metadata = new RecordingMetadataService(
            [],
            pauseApply: true);
        var files = new RecordingFileOperationService([]);
        var transcodes = new RecordingTranscodeService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(included),
            metadata,
            files,
            transcodes);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    Document(included))),
            TestContext.Current.CancellationToken));

        Task apply =
            workbench.ApplyCommand.ExecuteAsync(null);
        await metadata.ApplyPaused.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedMetadataMutationIntent.Create(
                MetadataPlan(
                    Document(lateMetadata))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    lateFile,
                    Path.GetFullPath(
                        "entry-apply-late-file-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    lateTranscode,
                    Path.GetFullPath(
                        "entry-apply-late-output.flac"))),
            TestContext.Current.CancellationToken));
        metadata.ReleaseApply.TrySetResult(true);

        await apply;

        Assert.Equal(1, metadata.ApplyCalls);
        Assert.Equal(0, metadata.StageCalls);
        Assert.Equal(0, files.ApplyCalls);
        Assert.Equal(0, transcodes.ApplyCalls);
        Assert.Equal(
            [lateFile, lateMetadata, lateTranscode],
            workbench.PendingMutationUnits
                .Select(unit => unit.SourcePath)
                .OrderBy(path => path));
    }

    [Fact]
    public async Task No_ready_transcode_discards_stage_once_and_retains_diagnostics()
    {
        string failed = Path.GetFullPath(
            "no-ready-transcode.flac");
        var transcodes = new RecordingTranscodeService(
            [],
            failedSources: [failed]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(failed),
            new RecordingMetadataService([]),
            new RecordingFileOperationService([]),
            transcodes);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    failed,
                    Path.GetFullPath(
                        "no-ready-output.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(0, transcodes.ApplyCalls);
        Assert.Equal(1, transcodes.DiscardCalls);
        Assert.Contains(
            "No transcode output",
            workbench.StatusDiagnosticDetail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Injected transcode failure.",
            workbench.StatusDiagnosticDetail,
            StringComparison.Ordinal);
        Assert.Contains(
            workbench.PendingChanges,
            row => row.Before == failed &&
                row.DiagnosticDetail?.Contains(
                    "Injected transcode failure.",
                    StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Transcode_cleanup_failure_is_warning_and_disjoint_file_still_commits()
    {
        string failed = Path.GetFullPath(
            "cleanup-warning-transcode.flac");
        string readyFile = Path.GetFullPath(
            "cleanup-warning-file.flac");
        var dialogs =
            new RecordingDecisionDialogs(
                accept: true,
                []);
        var files = new RecordingFileOperationService([]);
        var transcodes = new RecordingTranscodeService(
            [],
            failedSources: [failed],
            throwOnDiscard: true);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(failed),
            new RecordingMetadataService([]),
            files,
            transcodes,
            dialogs: dialogs);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    readyFile,
                    Path.GetFullPath(
                        "cleanup-warning-file-copy.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    failed,
                    Path.GetFullPath(
                        "cleanup-warning-output.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, transcodes.DiscardCalls);
        Assert.Equal(1, files.ApplyCalls);
        Assert.Contains(
            "Applied",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Injected transcode cleanup failure.",
            workbench.StatusDiagnosticDetail);
    }

    [Fact]
    public async Task Failed_transcode_retry_applies_newly_ready_item_and_retains_other_failure()
    {
        string first = Path.GetFullPath(
            "retry-first.flac");
        string second = Path.GetFullPath(
            "retry-second.flac");
        string initiallyReady = Path.GetFullPath(
            "retry-initially-ready.flac");
        var dialogs =
            new RecordingDecisionDialogs(
                accept: true,
                []);
        var transcodes = new RecordingTranscodeService(
            [],
            failedSources: [first, second]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(first),
            new RecordingMetadataService([]),
            new RecordingFileOperationService([]),
            transcodes,
            dialogs: dialogs);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                MultiTranscodePlan(
                    (
                        first,
                        Path.GetFullPath(
                            "retry-first-output.flac")),
                    (
                        second,
                        Path.GetFullPath(
                            "retry-second-output.flac")),
                    (
                        initiallyReady,
                        Path.GetFullPath(
                            "retry-initial-output.flac")))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);
        transcodes.AllowSource(first);
        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(2, dialogs.ConfirmationCalls);
        Assert.Equal(2, transcodes.ApplyCalls);
        MetadataPreviewRow retained =
            Assert.Single(workbench.PendingChanges);
        Assert.Equal(second, retained.Before);
        Assert.Contains(
            "Injected transcode failure.",
            retained.DiagnosticDetail);
    }

    [Fact]
    public async Task All_ready_transcodes_apply_without_partial_confirmation()
    {
        string source = Path.GetFullPath(
            "all-ready-transcode.flac");
        var dialogs =
            new RecordingDecisionDialogs(
                accept: false,
                []);
        var transcodes = new RecordingTranscodeService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(source),
            new RecordingMetadataService([]),
            new RecordingFileOperationService([]),
            transcodes,
            dialogs: dialogs);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    source,
                    Path.GetFullPath(
                        "all-ready-output.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(0, dialogs.ConfirmationCalls);
        Assert.Equal(1, transcodes.ApplyCalls);
        Assert.Equal(0, transcodes.DiscardCalls);
        Assert.Empty(workbench.PendingChanges);
    }

    [Fact]
    public async Task Empty_refreshed_file_batch_is_a_safe_no_op()
    {
        string source = Path.GetFullPath(
            "empty-refresh-source.flac");
        var files = new RecordingFileOperationService(
            [],
            emptyOnPreviewCall: 1);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(source),
            new RecordingMetadataService([]),
            files,
            new RecordingTranscodeService([]));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    source,
                    Path.GetFullPath(
                        "empty-refresh-copy.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, files.PreviewCalls);
        Assert.Equal(0, files.ApplyCalls);
        Assert.Empty(workbench.PendingChanges);
        Assert.DoesNotContain(
            "failed",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task File_preflight_operation_cancellation_remains_cancellation()
    {
        string source = Path.GetFullPath(
            "preflight-cancel-source.flac");
        var files = new RecordingFileOperationService([]);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(source),
            new RecordingMetadataService([]),
            files,
            new RecordingTranscodeService([]));
        int finishedCalls = 0;
        workbench.FileOperationsPreflight += _ =>
            throw new OperationCanceledException(
                "Injected preflight cancellation.");
        workbench.FileOperationsApplyFinished += _ =>
        {
            finishedCalls++;
            return Task.CompletedTask;
        };
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    source,
                    Path.GetFullPath(
                        "preflight-cancel-copy.flac"))),
            TestContext.Current.CancellationToken));

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(0, files.ApplyCalls);
        Assert.Equal(1, finishedCalls);
        Assert.Contains(
            "cancel",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "pending",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_observes_file_and_transcode_reconciliation_without_remaining_cancellable()
    {
        string transcodeSource = Path.GetFullPath(
            "reconcile-transcode.flac");
        string fileSource = Path.GetFullPath(
            "reconcile-file.flac");
        var transcodeCompletion =
            new TaskCompletionSource<
                IReadOnlyList<OperationIssue>>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        var fileCompletion =
            new TaskCompletionSource<
                IReadOnlyList<OperationIssue>>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        var transcodes = new RecordingTranscodeService(
            [],
            postCommitReconciliation:
                transcodeCompletion.Task);
        var files = new RecordingFileOperationService(
            [],
            postCommitReconciliation:
                fileCompletion.Task);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(transcodeSource),
            new RecordingMetadataService([]),
            files,
            transcodes);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedTranscodeMutationIntent.Create(
                TranscodePlan(
                    transcodeSource,
                    Path.GetFullPath(
                        "reconcile-transcode-output.flac"))),
            TestContext.Current.CancellationToken));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    fileSource,
                    Path.GetFullPath(
                        "reconcile-file-copy.flac"))),
            TestContext.Current.CancellationToken));

        Task apply =
            workbench.ApplyCommand.ExecuteAsync(null);
        await WaitUntilAsync(
            () => transcodes.ApplyCalls == 1 &&
                files.ApplyCalls == 1);
        Assert.False(apply.IsCompleted);
        Assert.True(workbench.IsBusy);
        workbench.CancelCommand.Execute(null);
        transcodeCompletion.TrySetResult(
        [
            new(
                "transcode.reconciliation-warning",
                OperationIssueSeverity.Warning,
                "Injected transcode reconciliation warning."),
        ]);
        fileCompletion.TrySetResult(
        [
            new(
                "file.reconciliation-warning",
                OperationIssueSeverity.Warning,
                "Injected file reconciliation warning."),
        ]);

        await apply;

        Assert.Contains(
            "Applied",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "cancel",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Injected transcode reconciliation warning.",
            workbench.StatusDiagnosticDetail);
        Assert.Contains(
            "Injected file reconciliation warning.",
            workbench.StatusDiagnosticDetail);
    }

    [Fact]
    public async Task Undo_observes_delayed_reconciliation_after_durable_restore_without_remaining_cancellable()
    {
        string source = Path.GetFullPath(
            "undo-delayed-reconciliation.flac");
        var completion =
            new TaskCompletionSource<
                IReadOnlyList<OperationIssue>>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        var history = new RecordingUndoHistory(
            new(
                Guid.NewGuid(),
                "Applied metadata",
                DateTimeOffset.UtcNow,
                [],
                [source],
                null),
            [],
            completion.Task);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(source),
            new RecordingMetadataService([]),
            new RecordingFileOperationService([]),
            new RecordingTranscodeService([]),
            history: history);
        await workbench.AddSourcesAsync([source]);

        Task undo =
            workbench.UndoCommand.ExecuteAsync(null);
        await WaitUntilAsync(
            () => history.UndoCalls == 1);
        Assert.False(undo.IsCompleted);
        Assert.True(workbench.IsBusy);
        workbench.CancelCommand.Execute(null);
        completion.TrySetResult(
        [
            new(
                "undo.delayed-warning",
                OperationIssueSeverity.Warning,
                "Injected delayed undo warning.",
                source),
        ]);

        await undo;

        Assert.Contains(
            "Restored",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "cancel",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Injected delayed undo warning.",
            workbench.StatusDiagnosticDetail);
    }

    [Fact]
    public async Task Apply_progress_from_previous_generation_cannot_change_next_operation()
    {
        string first = Path.GetFullPath(
            "progress-generation-first.flac");
        string second = Path.GetFullPath(
            "progress-generation-second.flac");
        var files = new RecordingFileOperationService(
            [],
            pauseOnPreviewCall: 3);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(first),
            new RecordingMetadataService([]),
            files,
            new RecordingTranscodeService([]));
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    first,
                    Path.GetFullPath(
                        "progress-generation-first-copy.flac"))),
            TestContext.Current.CancellationToken));
        var progressContext =
            new QueuedSynchronizationContext();
        SynchronizationContext? previousContext =
            SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            progressContext);
        try
        {
            await workbench.ApplyCommand
                .ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(
                previousContext);
        }
        IProgress<OperationProgress> staleProgress =
            Assert.IsAssignableFrom<
                IProgress<OperationProgress>>(
                    files.LastApplyProgress);
        Assert.True(await workbench.AddPendingMutationAsync(
            ReviewedFileOperationMutationIntent.Create(
                FileOperationPlan(
                    second,
                    Path.GetFullPath(
                        "progress-generation-second-copy.flac"))),
            TestContext.Current.CancellationToken));

        Task secondApply =
            workbench.ApplyCommand.ExecuteAsync(null);
        await files.PreviewPaused.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        staleProgress.Report(
            new(
                OperationPhase.Applying,
                1,
                1,
                Message: "STALE APPLY PROGRESS"));
        progressContext.RunAll();

        Assert.NotEqual(
            "STALE APPLY PROGRESS",
            workbench.ProgressText);
        files.ReleasePreview.TrySetResult(true);
        await secondApply;
    }

    [Fact]
    public async Task Undo_progress_from_completed_generation_cannot_change_terminal_state()
    {
        string source = Path.GetFullPath(
            "undo-progress-generation.flac");
        var history = new RecordingUndoHistory(
            new(
                Guid.NewGuid(),
                "Applied metadata",
                DateTimeOffset.UtcNow,
                [],
                [source],
                null),
            []);
        WorkbenchViewModel workbench = CreateWorkbench(
            Document(source),
            new RecordingMetadataService([]),
            new RecordingFileOperationService([]),
            new RecordingTranscodeService([]),
            history: history);
        await workbench.AddSourcesAsync([source]);
        var progressContext =
            new QueuedSynchronizationContext();
        SynchronizationContext? previousContext =
            SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            progressContext);
        try
        {
            await workbench.UndoCommand
                .ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(
                previousContext);
        }
        IProgress<int> staleProgress =
            Assert.IsAssignableFrom<IProgress<int>>(
                history.LastProgress);

        staleProgress.Report(99);
        progressContext.RunAll();

        Assert.Equal("", workbench.ProgressText);
        Assert.Equal(0, workbench.ProgressValue);
        Assert.Equal(1, workbench.ProgressMaximum);
        Assert.Contains(
            "Restored",
            workbench.StatusText,
            StringComparison.OrdinalIgnoreCase);
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

    private static async Task WaitUntilAsync(
        Func<bool> condition)
    {
        DateTime deadline =
            DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    "Timed out waiting for the test condition.");
            await Task.Delay(
                10,
                TestContext.Current.CancellationToken);
        }
    }

    private static WorkbenchViewModel CreateWorkbench(
        MediaDocument document,
        IMetadataOperationService metadata,
        IReviewedFileOperationService fileOperations,
        IAudioTranscodeService transcodes,
        IWorkbenchService? workbench = null,
        IEditHistoryService? history = null,
        IDialogCoordinator? dialogs = null)
    {
        var settings = new FakeSettings();
        var journals = new OperationJournalService(
            new FileMutationCoordinator());
        return new(
            workbench ??
                new DocumentWorkbenchService(document),
            metadata,
            new MetadataOperationCatalog(),
            new OperationRecipeStore(settings),
            new FakeAcoustIdDiscoveryService(),
            new FakeMusicBrainzMetadataProvider(),
            new MusicBrainzReleaseMappingService(),
            new FakeCoverArtArchiveProvider(),
            new FakeThumbnails(),
            history ??
                new EditHistoryService(
                    settings,
                    journals),
            new FakeFilePicker(),
            dialogs ??
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
        params MediaDocument[] documents)
    {
        MetadataFieldKey field =
            MetadataFieldKey.Known(
                TagFields.Title);
        return new(
            Guid.NewGuid(),
            "Metadata",
            [
                .. documents.Select(document =>
                    new MetadataFilePlan(
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
                        [])),
            ],
            DateTimeOffset.UtcNow);
    }

    private static ReviewedFileOperationPlan
        FileOperationPlan(
            string source,
            string destination,
            ReviewedFileOperationKind operationKind =
                ReviewedFileOperationKind.Copy,
            FileMutationKind? mutationKind = null)
    {
        var request =
            new ReviewedFileOperationRequest(
                [source],
                operationKind,
                Path.GetDirectoryName(destination),
                Path.GetFileName(destination));
        return FileOperationPlan(
            request,
            mutationKind);
    }

    private static ReviewedFileOperationPlan
        FileOperationPlan(
            ReviewedFileOperationRequest request,
            FileMutationKind? mutationKind = null)
    {
        FileMutationKind actionKind =
            mutationKind ??
            request.Kind switch
            {
                ReviewedFileOperationKind.Move or
                    ReviewedFileOperationKind.Rename =>
                    FileMutationKind.Move,
                ReviewedFileOperationKind.Quarantine =>
                    FileMutationKind.Quarantine,
                _ => FileMutationKind.Copy,
            };
        ReviewedFileOperationItem[] items =
        [
            .. request.SourcePaths.Select(
                (source, index) =>
                {
                    string fileName =
                        request.FileNameTemplate
                            .Replace(
                                "{Index}",
                                (index + 1).ToString(
                                    System.Globalization
                                        .CultureInfo
                                        .InvariantCulture),
                                StringComparison.Ordinal)
                            .Replace(
                                "{Name}",
                                Path.GetFileNameWithoutExtension(
                                    source),
                                StringComparison.Ordinal)
                            .Replace(
                                "{Extension}",
                                Path.GetExtension(source),
                                StringComparison.Ordinal);
                    string destination = Path.Combine(
                        request.DestinationDirectory ??
                        Path.GetDirectoryName(source)!,
                        fileName);
                    return new ReviewedFileOperationItem(
                        source,
                        destination,
                        actionKind,
                        []);
                }),
        ];
        return new(
            request,
            items,
            new(
                "test",
                request.DestinationDirectory ??
                Path.GetTempPath(),
                Path.Combine(
                    Path.GetTempPath(),
                    Guid.NewGuid().ToString("N")),
                [
                    .. items.Select(item =>
                        new FileMutationAction(
                            actionKind,
                            item.SourcePath,
                            item.DestinationPath!,
                            OperationPathSnapshot.Missing(
                                item.SourcePath),
                            OperationPathSnapshot.Missing(
                                item.DestinationPath!))),
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

    private static AudioTranscodePlan MultiTranscodePlan(
        params (string Source, string Destination)[] files)
    {
        var settings =
            new AudioTranscodeSettings(
                AudioTranscodeFormatIds.Flac,
                AudioTranscodeEncoderIds.Automatic,
                AudioTranscodeRateMode.Lossless);
        return new(
            Guid.NewGuid(),
            new(
                [
                    .. files.Select(file =>
                        file.Source),
                ],
                settings,
                new(
                    AudioTranscodeDestinationMode
                        .Alongside,
                    null,
                    true,
                    "{Name}{Extension}",
                    AudioTranscodeCollisionPolicy.Stop)),
            [
                .. files.Select(file =>
                    new AudioTranscodePlanItem(
                        Guid.NewGuid(),
                        file.Source,
                        file.Destination,
                        OperationPathSnapshot.Missing(
                            file.Source),
                        OperationPathSnapshot.Missing(
                            file.Destination),
                        "",
                        settings,
                        [])),
            ],
            [],
            DateTimeOffset.UtcNow,
            1);
    }

    private sealed class DocumentWorkbenchService(
        MediaDocument document,
        int? throwAfterLoadCall = null,
        IReadOnlyList<OperationIssue>? loadIssues = null) :
        IWorkbenchService
    {
        private int _loadCalls;

        public Task<WorkbenchLoadResult> LoadAsync(
            WorkbenchLoadRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _loadCalls++;
            if (throwAfterLoadCall is int threshold &&
                _loadCalls > threshold)
                throw new InvalidOperationException(
                    "Injected workbench refresh failure.");
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
                    loadIssues?.ToImmutableArray() ??
                    []));
        }
    }

    private sealed class RecordingUndoHistory(
        EditHistoryEntry entry,
        IReadOnlyList<OperationIssue> undoIssues,
        Task<IReadOnlyList<OperationIssue>>?
            undoReconciliation = null) :
        IEditHistoryService
    {
        private readonly List<EditHistoryEntry>
            _entries = [entry];

        public IReadOnlyList<EditHistoryEntry>
            Entries => _entries;

        public IReadOnlyList<EditHistoryEntry>
            RedoEntries { get; } = [];

        public IReadOnlyList<OperationIssue>
            LastUndoIssues { get; private set; } = [];

        public Task<IReadOnlyList<OperationIssue>>
            LastUndoReconciliation { get; private set; } =
                Task.FromResult<IReadOnlyList<OperationIssue>>(
                    []);

        public bool CanUndo =>
            _entries.Count > 0;

        public bool CanRedo => false;

        public int UndoCalls { get; private set; }
        public IProgress<int>? LastProgress { get; private set; }

        public void Record(EditHistoryEntry historyEntry) =>
            _entries.Insert(0, historyEntry);

        public Task<int> UndoLatestAsync(
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastProgress = progress;
            UndoCalls++;
            _entries.RemoveAt(0);
            LastUndoIssues = undoIssues;
            LastUndoReconciliation =
                undoReconciliation ??
                Task.FromResult(undoIssues);
            progress?.Report(1);
            return Task.FromResult(1);
        }
    }

    private sealed class RecordingDecisionDialogs(
        bool accept,
        List<string> events) :
        IDialogCoordinator
    {
        public int ConfirmationCalls { get; private set; }

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string primaryText)
        {
            ConfirmationCalls++;
            events.Add("partial-confirm");
            return Task.FromResult(
                accept);
        }

        public Task ShowMessageAsync(
            string title,
            string message) =>
            Task.CompletedTask;
    }

    private sealed class BlockingDecisionDialogs(
        List<string> events) :
        IDialogCoordinator
    {
        public TaskCompletionSource<bool>
            ConfirmationStarted { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>
            ReleaseConfirmation { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

        public async Task<bool> ConfirmAsync(
            string title,
            string message,
            string primaryText)
        {
            events.Add("partial-confirm");
            ConfirmationStarted.TrySetResult(true);
            return await ReleaseConfirmation.Task;
        }

        public Task ShowMessageAsync(
            string title,
            string message) =>
            Task.CompletedTask;
    }

    private sealed class RecordingMetadataService(
        List<string> events,
        bool pauseValuePreview = false,
        bool pauseStage = false,
        bool pauseApply = false) :
        FakeMetadataOperationService,
        IMetadataOperationService
    {
        public int ApplyCalls { get; private set; }
        public int StageCalls { get; private set; }
        public TaskCompletionSource<bool>
            ValuePreviewStarted { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>
            ReleaseValuePreview { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>
            StageStarted { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>
            ReleaseStage { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>
            ApplyPaused { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>
            ReleaseApply { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

        public override async Task<MetadataApplyResult> ApplyAsync(
            MetadataOperationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            events.Add("metadata-apply");
            ApplyCalls++;
            if (pauseApply)
            {
                ApplyPaused.TrySetResult(true);
                await ReleaseApply.Task.WaitAsync(
                    ct);
            }
            return await base.ApplyAsync(
                plan,
                progress,
                ct);
        }

        async Task<MetadataOperationPlan>
            IMetadataOperationService.PreviewValueEditsAsync(
                IReadOnlyDictionary<
                    string,
                    IReadOnlyList<MetadataValueEdit>>
                    editsByPath,
                string name,
                IProgress<OperationProgress>? progress,
                CancellationToken ct)
        {
            if (pauseValuePreview)
            {
                ValuePreviewStarted.TrySetResult(true);
                await ReleaseValuePreview.Task.WaitAsync(
                    ct);
            }
            return await base.PreviewValueEditsAsync(
                editsByPath,
                name,
                progress,
                ct);
        }

        public async Task<MetadataOperationStageResult> StageAsync(
            MetadataOperationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            events.Add("metadata-stage");
            StageCalls++;
            if (pauseStage)
            {
                StageStarted.TrySetResult(true);
                await ReleaseStage.Task.WaitAsync(
                    ct);
            }
            return new MetadataOperationStageResult(
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
                ]);
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
        bool throwOnApply = false,
        int? pauseOnPreviewCall = null,
        int? emptyOnPreviewCall = null,
        Task<IReadOnlyList<OperationIssue>>?
            postCommitReconciliation = null) :
        IReviewedFileOperationService
    {
        public int ApplyCalls { get; private set; }
        public int PreviewCalls { get; private set; }
        public bool ApplyCompleted { get; private set; }
        public IProgress<OperationProgress>?
            LastApplyProgress { get; private set; }
        public TaskCompletionSource<bool>
            PreviewPaused { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>
            ReleasePreview { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        public List<ReviewedFileOperationPlan>
            AppliedPlans { get; } = [];

        public async Task<ReviewedFileOperationPlan> PreviewAsync(
            ReviewedFileOperationRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            events.Add("file-preview");
            PreviewCalls++;
            if (pauseOnPreviewCall == PreviewCalls)
            {
                PreviewPaused.TrySetResult(true);
                await ReleasePreview.Task.WaitAsync(
                    ct);
            }
            ReviewedFileOperationPlan plan =
                FileOperationPlan(
                    request);
            if (emptyOnPreviewCall == PreviewCalls)
                return plan with
                {
                    Items = [],
                    MutationPlan =
                        plan.MutationPlan with
                        {
                            Actions = [],
                        },
                };
            if (blockOnPreviewCall == PreviewCalls)
            {
                string source =
                    request.SourcePaths[0];
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
            return plan;
        }

        public Task<FileMutationSummary> ApplyAsync(
            ReviewedFileOperationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            events.Add("file-apply");
            ApplyCalls++;
            LastApplyProgress = progress;
            if (throwOnApply)
                throw new InvalidOperationException(
                    "Injected file-operation apply failure.");
            AppliedPlans.Add(plan);
            ApplyCompleted = true;
            FileMutationSummary summary =
                new(
                    plan.MutationPlan.Actions.Count,
                    0,
                    0,
                    0,
                    null,
                    []);
            if (postCommitReconciliation is not null)
                summary = summary with
                {
                    PostCommitReconciliation =
                        new(
                            postCommitReconciliation),
                };
            return Task.FromResult(
                summary);
        }
    }

    private sealed class RecordingTranscodeService(
        List<string> events,
        IEnumerable<string>? failedSources = null,
        IEnumerable<string>? cancelledSources = null,
        bool throwOnDiscard = false,
        Task<IReadOnlyList<OperationIssue>>?
            postCommitReconciliation = null) :
        IAudioTranscodeService
    {
        private readonly HashSet<string> _failedSources =
            (failedSources ?? [])
            .ToHashSet(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
        private readonly HashSet<string> _cancelledSources =
            (cancelledSources ?? [])
            .ToHashSet(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
        public int ApplyCalls { get; private set; }
        public int DiscardCalls { get; private set; }

        public void AllowAllSources()
        {
            _failedSources.Clear();
            _cancelledSources.Clear();
        }

        public void AllowSource(string source)
        {
            _failedSources.Remove(
                source);
            _cancelledSources.Remove(
                source);
        }

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
                Stage(plan));
        }

        public Task<AudioTranscodeStageResult>
            StageWithSourceOverridesAsync(
                AudioTranscodePlan plan,
                IReadOnlyDictionary<string, string>
                    sourceOverrides,
                IProgress<OperationProgress>? progress = null,
                CancellationToken ct = default)
        {
            events.Add(
                "transcode-stage-overrides");
            return Task.FromResult(
                Stage(plan));
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
            AudioTranscodeApplyResult result =
                new(
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
                    []);
            if (postCommitReconciliation is not null)
                result = result with
                {
                    PostCommitReconciliation =
                        new(
                            postCommitReconciliation),
                };
            return Task.FromResult(
                result);
        }

        public Task DiscardStageAsync(
            AudioTranscodeStageResult stage,
            CancellationToken ct = default)
        {
            events.Add("transcode-discard");
            DiscardCalls++;
            if (throwOnDiscard)
                throw new InvalidOperationException(
                    "Injected transcode cleanup failure.");
            return Task.CompletedTask;
        }

        private AudioTranscodeStageResult
            Stage(
                AudioTranscodePlan plan) =>
            new(
                plan,
                [
                    .. plan.Items.Select(item =>
                        new AudioTranscodeStagedItem(
                            item,
                            _failedSources.Contains(
                                item.SourcePath)
                                ? AudioTranscodeStageState
                                    .Failed
                                : _cancelledSources.Contains(
                                    item.SourcePath)
                                    ? AudioTranscodeStageState
                                        .Cancelled
                                : AudioTranscodeStageState
                                    .Ready,
                            _failedSources.Contains(
                                item.SourcePath) ||
                            _cancelledSources.Contains(
                                item.SourcePath)
                                ? null
                                : item.DestinationPath +
                                    ".stage",
                            _failedSources.Contains(
                                item.SourcePath) ||
                            _cancelledSources.Contains(
                                item.SourcePath)
                                ? null
                                : "hash",
                            _failedSources.Contains(
                                item.SourcePath) ||
                            _cancelledSources.Contains(
                                item.SourcePath)
                                ? 0
                                : 1,
                            _failedSources.Contains(
                                item.SourcePath)
                                ? "injected"
                                : null,
                            _failedSources.Contains(
                                item.SourcePath)
                                ? "Injected transcode failure."
                                : null)),
                ]);
    }
}
