using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class LibraryPendingCompositionTests
{
    [Fact]
    public async Task Inspector_then_legacy_preview_apply_as_one_plan()
    {
        const string path =
            @"C:\Music\Library-composed.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);

        viewModel.Inspector.Fields.Single(field =>
            field.Field == TagFields.Artist).Value =
                "Inspector artist";
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.Equal(2, viewModel.PendingChanges.Count);
        Assert.Contains(
            viewModel.PendingChanges,
            row => row.Field == "Artist" &&
                row.After == "Inspector artist");
        Assert.Contains(
            viewModel.PendingChanges,
            row => row.Field == "Title" &&
                row.After == "Reviewed");
        Assert.True(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
        await WaitForAuthoritativePreviewAsync(
            viewModel);

        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        MetadataFilePlan applied = Assert.Single(
            operations.AppliedPlan!.Files);
        Assert.Equal(
            [TagFields.Title, TagFields.Artist],
            applied.Edits.Select(edit =>
                    edit.Field.KnownField)
                .ToArray());
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(
            viewModel.Inspector.HasUnsavedChanges);
        Assert.False(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
    }

    [Fact]
    public async Task Inspector_then_legacy_preview_discard_clears_both_without_apply()
    {
        const string path =
            @"C:\Music\Library-discard.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);

        Assert.False(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
        viewModel.Inspector.Fields.Single(field =>
            field.Field == TagFields.Artist).Value =
                "Inspector artist";
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.Equal(2, viewModel.PendingChanges.Count);

        await viewModel.RevertPendingChangesCommand
            .ExecuteAsync(null);

        Assert.Null(operations.AppliedPlan);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Empty(
            viewModel.OperationPreviewChanges);
        Assert.False(
            viewModel.Inspector.HasUnsavedChanges);
        Assert.False(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
    }

    [Fact]
    public async Task Failed_composed_apply_preserves_the_unapplied_inspector_draft()
    {
        const string path =
            @"C:\Music\Library-failed-composed.flac";
        var operations =
            new FailingApplyMetadataOperationService();
        (LibraryViewModel viewModel, _) =
            CreateLibrary(
                path,
                operations);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);

        EditableTagField artist =
            viewModel.Inspector.Fields.Single(field =>
                field.Field == TagFields.Artist);
        artist.Value = "Unapplied inspector artist";
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);
        await WaitForAuthoritativePreviewAsync(
            viewModel);

        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.Equal(1, operations.ApplyCalls);
        Assert.True(
            viewModel.Inspector.HasUnsavedChanges);
        Assert.Equal(
            "Unapplied inspector artist",
            artist.Value);
        Assert.Equal(2, viewModel.PendingChanges.Count);
        Assert.Single(
            viewModel.OperationPreviewChanges);
        Assert.True(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
    }

    [Fact]
    public async Task Cancellation_after_durable_apply_does_not_restore_the_consumed_draft()
    {
        const string path =
            @"C:\Music\Library-committed-cancel.flac";
        var operations =
            new CommittedThenCancelMetadataOperationService();
        (LibraryViewModel viewModel, _) =
            CreateLibrary(
                path,
                operations);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);

        viewModel.Inspector.Fields.Single(field =>
            field.Field == TagFields.Artist).Value =
                "Committed inspector artist";
        viewModel.OperationEditor.OperationValue =
            "Committed title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);
        await WaitForAuthoritativePreviewAsync(
            viewModel);
        operations.AfterCommit = () =>
            viewModel.CancelLibraryOperationCommand
                .Execute(null);

        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.Equal(1, operations.ApplyCalls);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Empty(
            viewModel.OperationPreviewChanges);
        Assert.False(
            viewModel.Inspector.HasUnsavedChanges);
        Assert.False(
            viewModel.RevertPendingChangesCommand
                .CanExecute(null));
        Assert.False(
            viewModel.ApplyLibraryOperationCommand
                .CanExecute(null));
    }

    [Fact]
    public async Task Conflicting_legacy_and_inline_values_block_the_combined_plan()
    {
        const string path =
            @"C:\Music\Library-conflict.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(viewModel.Rows);
        await viewModel.SelectAsync([row]);
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);

        row.Title = "Different inline title";
        await WaitForAuthoritativePreviewAsync(
            viewModel);

        Assert.False(
            viewModel.CanApplyPendingChanges);
        Assert.False(
            viewModel.ApplyLibraryOperationCommand
                .CanExecute(null));
        Assert.Contains(
            viewModel.PendingChanges,
            change =>
                change.HasDiagnosticDetail &&
                change.DiagnosticDetail!.Contains(
                    "different values",
                    StringComparison.Ordinal));
        Assert.Null(operations.AppliedPlan);
    }

    [Fact]
    public async Task Different_source_snapshots_cannot_bypass_a_stale_direct_draft()
    {
        const string path =
            @"C:\Music\Library-snapshot-conflict.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(viewModel.Rows);
        await viewModel.SelectAsync([row]);
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);
        operations.Snapshots[path] = new(
            path,
            2,
            new DateTime(
                2026,
                7,
                26,
                1,
                0,
                0,
                DateTimeKind.Utc),
            "changed-hash");

        row.Artist = "Inline artist";
        await WaitForAuthoritativePreviewAsync(
            viewModel);

        Assert.False(
            viewModel.CanApplyPendingChanges);
        Assert.Contains(
            viewModel.PendingChanges,
            change =>
                change.HasDiagnosticDetail &&
                change.DiagnosticDetail!.Contains(
                    "different source snapshots",
                    StringComparison.Ordinal));
        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);
        Assert.Null(operations.AppliedPlan);
        Assert.True(row.HasChanges);
    }

    [Fact]
    public async Task Identical_legacy_and_inline_edits_are_deduplicated()
    {
        const string path =
            @"C:\Music\Library-identical.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(viewModel.Rows);
        await viewModel.SelectAsync([row]);
        viewModel.OperationEditor.OperationValue =
            "Legacy title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);
        row.Title = "Reviewed";
        await WaitForAuthoritativePreviewAsync(
            viewModel);

        Assert.True(
            viewModel.ApplyLibraryOperationCommand
                .CanExecute(null));
        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        MetadataFilePlan applied =
            Assert.Single(
                operations.AppliedPlan!.Files);
        MetadataValueEdit edit =
            Assert.Single(applied.Edits);
        Assert.Equal(
            TagFields.Title,
            edit.Field.KnownField);
        Assert.Equal(
            ["Reviewed"],
            edit.Values);
    }

    [Fact]
    public async Task Missing_committed_overlay_is_not_resurrected_after_targeted_cache_removal()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "MusicLibraryManager-LibraryOverlay-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            "removed.flac");
        await File.WriteAllTextAsync(
            path,
            "fixture",
            TestContext.Current
                .CancellationToken);
        var records = new List<TrackRecord>();
        try
        {
            (LibraryViewModel viewModel, _) =
                CreateLibrary(
                    path,
                    records: records);
            await viewModel.ReloadAsync();
            LibraryRow row =
                Assert.Single(viewModel.Rows);
            row.Title = "Committed title";
            await WaitForAuthoritativePreviewAsync(
                viewModel);
            await viewModel.ApplyLibraryOperationCommand
                .ExecuteAsync(null);
            Assert.False(row.HasChanges);

            records.Clear();
            File.Delete(path);
            await viewModel.ReloadAsync();

            Assert.Empty(viewModel.Rows);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(
                    directory,
                    recursive: true);
        }
    }

    [Fact]
    public async Task Committed_row_observer_failure_does_not_leave_later_rows_pending()
    {
        const string firstPath =
            @"C:\Music\observer-first.flac";
        const string secondPath =
            @"C:\Music\observer-second.flac";
        var records = new List<TrackRecord>
        {
            new()
            {
                Path = secondPath,
                Artist = "Second artist",
                AlbumArtist = "Second artist",
                Album = "Album",
                Title = "Second title",
                CodecName = "FLAC",
                CodecType =
                    CodecType.Lossless,
                LastWriteTime =
                    new DateTime(2026, 7, 25),
            },
        };
        (LibraryViewModel viewModel, _) =
            CreateLibrary(
                firstPath,
                records: records);
        await viewModel.ReloadAsync();
        LibraryRow first = viewModel.Rows.Single(
            row => row.Path == firstPath);
        LibraryRow second = viewModel.Rows.Single(
            row => row.Path == secondPath);
        first.Title = "Applied first title";
        second.Title = "Applied second title";
        await WaitForAuthoritativePreviewAsync(
            viewModel);
        first.PropertyChanged +=
            (_, args) =>
            {
                if (args.PropertyName ==
                    nameof(LibraryRow.HasChanges))
                    throw new InvalidOperationException(
                        "Injected row observer failure.");
            };

        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.False(first.HasChanges);
        Assert.False(second.HasChanges);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Contains(
            "Injected row observer failure.",
            viewModel.OperationDiagnosticDetail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Committed_collection_observer_failure_is_a_warning_and_does_not_restore_retry_state()
    {
        const string path =
            @"C:\Music\observer-collection.flac";
        (LibraryViewModel viewModel, _) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);
        viewModel.Inspector.Fields.Single(field =>
            field.Field == TagFields.Artist).Value =
                "Applied artist";
        viewModel.OperationEditor.OperationValue =
            "Applied title";
        await viewModel.PreviewLibraryOperationCommand
            .ExecuteAsync(null);
        await WaitForAuthoritativePreviewAsync(
            viewModel);
        viewModel.PendingChanges
            .CollectionChanged +=
            (_, _) => throw new
                InvalidOperationException(
                    "Injected pending collection observer failure.");

        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.Empty(
            viewModel.OperationPreviewChanges);
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(
            viewModel.Inspector.HasUnsavedChanges);
        Assert.False(
            viewModel.ApplyLibraryOperationCommand
                .CanExecute(null));
        Assert.DoesNotContain(
            "failed",
            viewModel.OperationStatus,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Injected pending collection observer failure.",
            viewModel.OperationDiagnosticDetail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Committed_multi_field_inspector_observer_failure_advances_every_field()
    {
        const string path =
            @"C:\Music\observer-inspector-fields.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
                CreateLibrary(path);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);
        EditableTagField title =
            viewModel.Inspector.Fields.Single(field =>
                field.Field == TagFields.Title);
        EditableTagField artist =
            viewModel.Inspector.Fields.Single(field =>
                field.Field == TagFields.Artist);
        title.Value = "Applied title";
        artist.Value = "Applied artist";
        await WaitForAuthoritativePreviewAsync(
            viewModel);
        title.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName ==
                nameof(
                    EditableTagField.IsModified))
                throw new InvalidOperationException(
                    "Injected inspector observer failure.");
        };

        await viewModel.ApplyLibraryOperationCommand
            .ExecuteAsync(null);

        MetadataFilePlan applied =
            Assert.Single(
                operations.AppliedPlan!.Files);
        Assert.Equal(
            2,
            applied.Edits.Length);
        Assert.False(title.IsModified);
        Assert.False(artist.IsModified);
        Assert.False(
            viewModel.Inspector.HasUnsavedChanges);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Contains(
            "Injected inspector observer failure.",
            viewModel.OperationDiagnosticDetail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queued_progress_from_a_completed_operation_cannot_resurrect_terminal_progress()
    {
        const string path =
            @"C:\Music\queued-progress.flac";
        (LibraryViewModel viewModel, _) =
            CreateLibrary(path);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);
        var context =
            new QueuedSynchronizationContext();
        SynchronizationContext? previous =
            SynchronizationContext.Current;
        SynchronizationContext
            .SetSynchronizationContext(context);
        try
        {
            await viewModel
                .PreviewLibraryOperationCommand
                .ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext
                .SetSynchronizationContext(previous);
        }

        Assert.False(viewModel.IsOperationBusy);
        Assert.True(
            viewModel
                .IsOperationProgressIndeterminate);
        Assert.Equal(
            0,
            viewModel.OperationProgressValue);
        Assert.Equal(
            "",
            viewModel.OperationProgressText);

        context.RunAll();

        Assert.False(viewModel.IsOperationBusy);
        Assert.True(
            viewModel
                .IsOperationProgressIndeterminate);
        Assert.Equal(
            0,
            viewModel.OperationProgressValue);
        Assert.Equal(
            "",
            viewModel.OperationProgressText);
    }

    [Fact]
    public async Task Queued_progress_from_an_old_operation_cannot_overwrite_a_later_active_operation()
    {
        const string path =
            @"C:\Music\queued-progress-generation.flac";
        (LibraryViewModel viewModel,
            FakeMetadataOperationService operations) =
                CreateLibrary(path);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);
        var context =
            new QueuedSynchronizationContext();
        SynchronizationContext? previous =
            SynchronizationContext.Current;
        SynchronizationContext
            .SetSynchronizationContext(context);
        try
        {
            await viewModel
                .PreviewLibraryOperationCommand
                .ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext
                .SetSynchronizationContext(previous);
        }

        operations.ApplyRelease =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        Task apply =
            viewModel.ApplyLibraryOperationCommand
                .ExecuteAsync(null);
        await operations.ApplyStarted.Task.WaitAsync(
            TestContext.Current
                .CancellationToken);
        Assert.True(viewModel.IsOperationBusy);
        string activeProgressText =
            viewModel.OperationProgressText;

        context.RunAll();

        Assert.True(viewModel.IsOperationBusy);
        Assert.Equal(
            activeProgressText,
            viewModel.OperationProgressText);
        Assert.Equal(
            0,
            viewModel.OperationProgressValue);

        operations.ApplyRelease.SetResult(true);
        await apply;
        Assert.False(viewModel.IsOperationBusy);
    }

    [Theory]
    [InlineData(
        false,
        ReviewedFileOperationKind.Move,
        FileMutationKind.Move)]
    [InlineData(
        true,
        ReviewedFileOperationKind.Move,
        FileMutationKind.Move)]
    [InlineData(
        false,
        ReviewedFileOperationKind.Rename,
        FileMutationKind.Move)]
    [InlineData(
        true,
        ReviewedFileOperationKind.Rename,
        FileMutationKind.Move)]
    [InlineData(
        false,
        ReviewedFileOperationKind.Quarantine,
        FileMutationKind.Quarantine)]
    [InlineData(
        true,
        ReviewedFileOperationKind.Quarantine,
        FileMutationKind.Quarantine)]
    [InlineData(
        false,
        ReviewedFileOperationKind.Quarantine,
        FileMutationKind.Delete)]
    [InlineData(
        true,
        ReviewedFileOperationKind.Quarantine,
        FileMutationKind.Delete)]
    public async Task Workbench_path_change_is_blocked_by_a_library_draft_created_after_preview(
        bool useInspector,
        ReviewedFileOperationKind operationKind,
        FileMutationKind mutationKind)
    {
        string source = Path.GetFullPath(
            $"library-late-draft-{operationKind}-{mutationKind}-{useInspector}.flac");
        string destination = Path.GetFullPath(
            $"library-late-draft-destination-{operationKind}-{mutationKind}-{useInspector}.flac");
        var fileOperations =
            new CoordinatedFileOperationService(
                mutationKind);
        WorkbenchViewModel workbench =
            CreateFileOperationWorkbench(
                fileOperations);
        (LibraryViewModel library, _) =
            CreateLibrary(
                source,
                workbench: workbench,
                fileOperations: fileOperations);
        await library.ReloadAsync();
        LibraryRow row = Assert.Single(
            library.Rows);
        await library.SelectAsync([row]);

        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            source,
                            destination,
                            operationKind,
                            mutationKind)),
                TestContext.Current
                    .CancellationToken));

        if (useInspector)
            library.Inspector.Fields.Single(field =>
                field.Field == TagFields.Title).Value =
                    "Late inspector draft";
        else
            row.Title = "Late inline draft";

        await workbench.ApplyCommand
            .ExecuteAsync(null);

        Assert.Equal(
            0,
            fileOperations.ApplyCalls);
        Assert.Single(
            workbench.PendingMutationUnits);
        Assert.True(
            useInspector
                ? library.Inspector
                    .HasUnsavedChanges
                : row.HasChanges);
        Assert.False(
            library.IsOperationBusy);
        Assert.True(
            library.CanEditPendingChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workbench_copy_allows_an_unrelated_library_draft_and_preserves_it(
        bool useInspector)
    {
        string source = Path.GetFullPath(
            $"library-copy-draft-{useInspector}.flac");
        string destination = Path.GetFullPath(
            $"library-copy-destination-{useInspector}.flac");
        var fileOperations =
            new CoordinatedFileOperationService(
                FileMutationKind.Copy);
        WorkbenchViewModel workbench =
            CreateFileOperationWorkbench(
                fileOperations);
        (LibraryViewModel library, _) =
            CreateLibrary(
                source,
                workbench: workbench,
                fileOperations: fileOperations);
        await library.ReloadAsync();
        LibraryRow row = Assert.Single(
            library.Rows);
        await library.SelectAsync([row]);
        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            source,
                            destination,
                            ReviewedFileOperationKind
                                .Copy,
                            FileMutationKind.Copy)),
                TestContext.Current
                    .CancellationToken));
        if (useInspector)
            library.Inspector.Fields.Single(field =>
                field.Field == TagFields.Title).Value =
                    "Inspector draft retained";
        else
            row.Title = "Inline draft retained";

        await workbench.ApplyCommand
            .ExecuteAsync(null);

        Assert.Equal(
            1,
            fileOperations.ApplyCalls);
        Assert.Empty(
            workbench.PendingMutationUnits);
        Assert.True(
            library.HasUnsavedChanges);
        Assert.False(
            library.IsOperationBusy);
        Assert.True(
            library.CanEditPendingChanges);
    }

    [Fact]
    public async Task Workbench_path_change_allows_a_draft_for_a_different_library_path()
    {
        string draftedSource = Path.GetFullPath(
            "library-unrelated-draft.flac");
        string movedSource = Path.GetFullPath(
            "library-unrelated-move.flac");
        string destination = Path.GetFullPath(
            "library-unrelated-destination.flac");
        var records = new List<TrackRecord>
        {
            new()
            {
                Path = movedSource,
                Artist = "Other artist",
                AlbumArtist = "Other artist",
                Album = "Other album",
                Title = "Other title",
                CodecName = "FLAC",
                CodecType =
                    CodecType.Lossless,
                LastWriteTime =
                    new DateTime(2026, 7, 25),
            },
        };
        var fileOperations =
            new CoordinatedFileOperationService(
                FileMutationKind.Move);
        WorkbenchViewModel workbench =
            CreateFileOperationWorkbench(
                fileOperations);
        (LibraryViewModel library, _) =
            CreateLibrary(
                draftedSource,
                records: records,
                workbench: workbench,
                fileOperations: fileOperations);
        await library.ReloadAsync();
        LibraryRow draft = library.Rows.Single(
            row => row.Path == draftedSource);
        draft.Title = "Unrelated draft";
        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            movedSource,
                            destination,
                            ReviewedFileOperationKind
                                .Move,
                            FileMutationKind.Move)),
                TestContext.Current
                    .CancellationToken));

        await workbench.ApplyCommand
            .ExecuteAsync(null);

        Assert.Equal(
            1,
            fileOperations.ApplyCalls);
        Assert.Empty(
            workbench.PendingMutationUnits);
        Assert.True(draft.HasChanges);
    }

    [Fact]
    public async Task Workbench_path_change_is_blocked_by_a_reviewed_library_metadata_plan()
    {
        string source = Path.GetFullPath(
            "library-reviewed-plan-source.flac");
        string destination = Path.GetFullPath(
            "library-reviewed-plan-destination.flac");
        var fileOperations =
            new CoordinatedFileOperationService(
                FileMutationKind.Move);
        WorkbenchViewModel workbench =
            CreateFileOperationWorkbench(
                fileOperations);
        (LibraryViewModel library, _) =
            CreateLibrary(
                source,
                workbench: workbench,
                fileOperations: fileOperations);
        await library.ReloadAsync();
        await library.SelectAsync(
            [Assert.Single(library.Rows)]);
        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            source,
                            destination,
                            ReviewedFileOperationKind
                                .Move,
                            FileMutationKind.Move)),
                TestContext.Current
                    .CancellationToken));
        library.OperationEditor.OperationValue =
            "Reviewed library title";
        await library
            .PreviewLibraryOperationCommand
            .ExecuteAsync(null);

        await workbench.ApplyCommand
            .ExecuteAsync(null);

        Assert.Equal(
            0,
            fileOperations.ApplyCalls);
        Assert.Single(
            workbench.PendingMutationUnits);
        Assert.True(
            library.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workbench_path_change_reserves_library_editing_until_apply_finishes(
        bool failApply)
    {
        string source = Path.GetFullPath(
            $"library-reservation-{failApply}.flac");
        string destination = Path.GetFullPath(
            $"library-reservation-destination-{failApply}.flac");
        LibraryViewModel? library = null;
        LibraryRow? reservedRow = null;
        EditableTagField? reservedField =
            null;
        var fileOperations =
            new CoordinatedFileOperationService(
                FileMutationKind.Move,
                failApply,
                () =>
                {
                    Assert.NotNull(library);
                    Assert.True(
                        library.IsOperationBusy);
                    Assert.False(
                        library
                            .CanEditPendingChanges);
                    Assert.True(
                        library
                            .IsPendingEditReadOnly);
                    Assert.NotNull(reservedRow);
                    Assert.NotNull(reservedField);
                    Assert.Throws<
                        InvalidOperationException>(
                        () => reservedRow.Title =
                            "Forbidden late row edit");
                    Assert.Throws<
                        InvalidOperationException>(
                        () =>
                            reservedRow
                                .MetadataValues[
                                MetadataGridValueKey
                                    .For(
                                        MetadataFieldKey
                                            .Known(
                                                TagFields
                                                    .Title))] =
                                "Forbidden late map edit");
                    Assert.Throws<
                        InvalidOperationException>(
                        () => reservedField.Value =
                            "Forbidden late inspector edit");
                    Assert.False(
                        reservedRow.HasChanges);
                });
        WorkbenchViewModel workbench =
            CreateFileOperationWorkbench(
                fileOperations);
        (library, _) =
            CreateLibrary(
                source,
                workbench: workbench,
                fileOperations: fileOperations);
        await library.ReloadAsync();
        reservedRow =
            Assert.Single(library.Rows);
        await library.SelectAsync(
            [reservedRow]);
        reservedField =
            library.Inspector.Fields.Single(
                field =>
                    field.Field ==
                    TagFields.Title);
        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            source,
                            destination,
                            ReviewedFileOperationKind
                                .Move,
                            FileMutationKind.Move)),
                TestContext.Current
                    .CancellationToken));

        await workbench.ApplyCommand
            .ExecuteAsync(null);

        Assert.Equal(
            1,
            fileOperations.ApplyCalls);
        Assert.False(
            library.IsOperationBusy);
        Assert.True(
            library.CanEditPendingChanges);
        Assert.False(
            library.IsPendingEditReadOnly);
        Assert.Equal(
            failApply,
            workbench.PendingMutationUnits.Count() ==
                1);
        LibraryRow editableRow =
            Assert.Single(library.Rows);
        editableRow.Title =
            "Allowed after reservation";
        reservedField.Value =
            "Allowed inspector edit after reservation";
        Assert.True(editableRow.HasChanges);
        Assert.True(
            library.Inspector.HasUnsavedChanges);
    }

    [Fact]
    public async Task Workbench_cancelled_path_change_releases_every_library_edit_guard()
    {
        string source = Path.GetFullPath(
            "library-reservation-cancel.flac");
        string destination = Path.GetFullPath(
            "library-reservation-cancel-destination.flac");
        var fileOperations =
            new CoordinatedFileOperationService(
                FileMutationKind.Move,
                applyFailure:
                    new OperationCanceledException(
                        "Injected cancellation."));
        WorkbenchViewModel workbench =
            CreateFileOperationWorkbench(
                fileOperations);
        (LibraryViewModel library, _) =
            CreateLibrary(
                source,
                workbench: workbench,
                fileOperations: fileOperations);
        await library.ReloadAsync();
        LibraryRow row =
            Assert.Single(library.Rows);
        await library.SelectAsync([row]);
        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            source,
                            destination,
                            ReviewedFileOperationKind
                                .Move,
                            FileMutationKind.Move)),
                TestContext.Current
                    .CancellationToken));

        await workbench.ApplyCommand
            .ExecuteAsync(null);

        Assert.Equal(
            1,
            fileOperations.ApplyCalls);
        Assert.Single(
            workbench.PendingMutationUnits);
        Assert.False(library.IsOperationBusy);
        row.Title = "Allowed after cancellation";
        library.Inspector.Fields.Single(field =>
            field.Field == TagFields.Title).Value =
                "Allowed inspector edit";
        Assert.True(row.HasChanges);
        Assert.True(
            library.Inspector.HasUnsavedChanges);
    }

    [Fact]
    public async Task Later_preflight_blocker_releases_an_earlier_library_reservation()
    {
        string source = Path.GetFullPath(
            "library-later-preflight-block.flac");
        string destination = Path.GetFullPath(
            "library-later-preflight-destination.flac");
        var fileOperations =
            new CoordinatedFileOperationService(
                FileMutationKind.Move);
        WorkbenchViewModel workbench =
            CreateFileOperationWorkbench(
                fileOperations);
        (LibraryViewModel library, _) =
            CreateLibrary(
                source,
                workbench: workbench,
                fileOperations: fileOperations);
        await library.ReloadAsync();
        LibraryRow row =
            Assert.Single(library.Rows);
        await library.SelectAsync([row]);
        workbench.FileOperationsPreflight +=
            _ => Task.FromResult<string?>(
                "Injected later preflight blocker.");
        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            source,
                            destination,
                            ReviewedFileOperationKind
                                .Move,
                            FileMutationKind.Move)),
                TestContext.Current
                    .CancellationToken));

        await workbench.ApplyCommand
            .ExecuteAsync(null);

        Assert.Equal(
            0,
            fileOperations.ApplyCalls);
        Assert.Single(
            workbench.PendingMutationUnits);
        Assert.False(library.IsOperationBusy);
        row.Title =
            "Allowed after downstream blocker";
        Assert.True(row.HasChanges);
    }

    [Fact]
    public async Task Normalized_equivalent_path_cannot_bypass_library_draft_preflight()
    {
        string source = Path.GetFullPath(
            "library-normalized-source.flac");
        string equivalentSource = Path.Combine(
            Path.GetDirectoryName(source)!,
            ".",
            Path.GetFileName(source));
        string destination = Path.GetFullPath(
            "library-normalized-destination.flac");
        var fileOperations =
            new CoordinatedFileOperationService(
                FileMutationKind.Move);
        WorkbenchViewModel workbench =
            CreateFileOperationWorkbench(
                fileOperations);
        (LibraryViewModel library, _) =
            CreateLibrary(
                source,
                workbench: workbench,
                fileOperations: fileOperations);
        await library.ReloadAsync();
        LibraryRow row =
            Assert.Single(library.Rows);
        row.Title = "Draft on normalized path";
        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            equivalentSource,
                            destination,
                            ReviewedFileOperationKind
                                .Move,
                            FileMutationKind.Move)),
                TestContext.Current
                    .CancellationToken));

        await workbench.ApplyCommand
            .ExecuteAsync(null);

        Assert.Equal(
            0,
            fileOperations.ApplyCalls);
        Assert.Single(
            workbench.PendingMutationUnits);
        Assert.True(row.HasChanges);
        Assert.False(library.IsOperationBusy);
    }

    [Fact]
    public async Task One_conflicting_path_blocks_the_entire_mixed_file_operation_batch()
    {
        string first = Path.GetFullPath(
            "library-mixed-first.flac");
        string second = Path.GetFullPath(
            "library-mixed-second.flac");
        string firstDestination =
            Path.GetFullPath(
                "library-mixed-first-moved.flac");
        string secondDestination =
            Path.GetFullPath(
                "library-mixed-second-moved.flac");
        var records = new List<TrackRecord>
        {
            new()
            {
                Path = second,
                Artist = "Second artist",
                AlbumArtist = "Second artist",
                Album = "Album",
                Title = "Second title",
                CodecName = "FLAC",
                CodecType =
                    CodecType.Lossless,
                LastWriteTime =
                    new DateTime(2026, 7, 25),
            },
        };
        var fileOperations =
            new CoordinatedFileOperationService(
                FileMutationKind.Move);
        WorkbenchViewModel workbench =
            CreateFileOperationWorkbench(
                fileOperations);
        (LibraryViewModel library, _) =
            CreateLibrary(
                first,
                records: records,
                workbench: workbench,
                fileOperations: fileOperations);
        await library.ReloadAsync();
        library.Rows.Single(row =>
            row.Path == first).Title =
                "Conflicting draft";
        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            first,
                            firstDestination,
                            ReviewedFileOperationKind
                                .Move,
                            FileMutationKind.Move)),
                TestContext.Current
                    .CancellationToken));
        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            second,
                            secondDestination,
                            ReviewedFileOperationKind
                                .Move,
                            FileMutationKind.Move)),
                TestContext.Current
                    .CancellationToken));

        await workbench.ApplyCommand
            .ExecuteAsync(null);

        Assert.Equal(
            0,
            fileOperations.ApplyCalls);
        Assert.Equal(
            2,
            workbench.PendingMutationUnits.Count());
        Assert.False(library.IsOperationBusy);
    }

    [Fact]
    public async Task Unrelated_path_remains_model_editable_during_scoped_reservation_and_is_previewed_after_release()
    {
        string movedSource = Path.GetFullPath(
            "library-scoped-moved.flac");
        string unrelated = Path.GetFullPath(
            "library-scoped-unrelated.flac");
        string destination = Path.GetFullPath(
            "library-scoped-destination.flac");
        var records = new List<TrackRecord>
        {
            new()
            {
                Path = unrelated,
                Artist = "Other artist",
                AlbumArtist = "Other artist",
                Album = "Other album",
                Title = "Other title",
                CodecName = "FLAC",
                CodecType =
                    CodecType.Lossless,
                LastWriteTime =
                    new DateTime(2026, 7, 25),
            },
        };
        LibraryRow? unrelatedRow = null;
        var fileOperations =
            new CoordinatedFileOperationService(
                FileMutationKind.Move,
                applying: () =>
                {
                    Assert.NotNull(unrelatedRow);
                    unrelatedRow.Title =
                        "Draft created while busy";
                    Assert.True(
                        unrelatedRow.HasChanges);
                });
        WorkbenchViewModel workbench =
            CreateFileOperationWorkbench(
                fileOperations);
        (LibraryViewModel library, _) =
            CreateLibrary(
                movedSource,
                records: records,
                workbench: workbench,
                fileOperations: fileOperations);
        await library.ReloadAsync();
        unrelatedRow = library.Rows.Single(row =>
            row.Path == unrelated);
        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            movedSource,
                            destination,
                            ReviewedFileOperationKind
                                .Move,
                            FileMutationKind.Move)),
                TestContext.Current
                    .CancellationToken));

        await workbench.ApplyCommand
            .ExecuteAsync(null);
        await WaitForAuthoritativePreviewAsync(
            library);

        LibraryRow retainedDraft =
            library.Rows.Single(row =>
                row.Path == unrelated);
        Assert.True(retainedDraft.HasChanges);
        Assert.Equal(
            "Draft created while busy",
            retainedDraft.Title);
        Assert.True(
            library.IsDirectPendingPreviewReady);
        Assert.False(library.IsOperationBusy);
    }

    [Theory]
    [InlineData(
        nameof(LibraryViewModel.SelectedPaths))]
    [InlineData(
        nameof(LibraryViewModel.Rows))]
    public async Task Selection_remap_or_reload_observer_failure_does_not_leave_stale_rows_or_reservation(
        string observedProperty)
    {
        string source = Path.GetFullPath(
            $"library-remap-source-{observedProperty}.flac");
        string destination = Path.GetFullPath(
            $"library-remap-destination-{observedProperty}.flac");
        var records = new List<TrackRecord>();
        var fileOperations =
            new CoordinatedFileOperationService(
                FileMutationKind.Move,
                applying: () =>
                {
                    records[0] = records[0] with
                    {
                        Path = destination,
                    };
                });
        WorkbenchViewModel workbench =
            CreateFileOperationWorkbench(
                fileOperations);
        (LibraryViewModel library, _) =
            CreateLibrary(
                source,
                records: records,
                workbench: workbench,
                fileOperations: fileOperations);
        await library.ReloadAsync();
        await library.SelectAsync(
            [Assert.Single(library.Rows)]);
        library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName ==
                observedProperty)
                throw new InvalidOperationException(
                    "Injected remap/reload observer failure.");
        };
        Assert.True(
            await workbench.AddPendingMutationAsync(
                ReviewedFileOperationMutationIntent
                    .Create(
                        FileOperationPlan(
                            source,
                            destination,
                            ReviewedFileOperationKind
                                .Move,
                            FileMutationKind.Move)),
                TestContext.Current
                    .CancellationToken));

        await workbench.ApplyCommand
            .ExecuteAsync(null);

        LibraryRow reloaded =
            Assert.Single(library.Rows);
        Assert.Equal(
            destination,
            reloaded.Path);
        Assert.Equal(
            [destination],
            library.SelectedPaths);
        Assert.Empty(
            workbench.PendingMutationUnits);
        Assert.False(library.IsOperationBusy);
        reloaded.Title =
            "Allowed after remap warning";
        Assert.True(reloaded.HasChanges);
    }

    [Fact]
    public async Task Late_artwork_draft_rebases_to_the_exact_applied_artwork_fingerprint()
    {
        const string path =
            @"C:\Music\late-artwork.flac";
        var originalArtwork = new ArtworkModel
        {
            Category = "FrontCover",
            Description = "Original",
            ImageType = "image/jpeg",
            Width = 100,
            Height = 100,
            Size = 3,
            Data = [1, 2, 3],
        };
        var model = new MediaFileModel
        {
            Path = path,
            Title = "Title",
            Artist = "Artist",
            IsWritable = true,
            KnownFields =
            [
                new(
                    TagFields.Title,
                    "Title"),
            ],
            Artwork = [originalArtwork],
        };
        var cache =
            new FakeLibrary([]);
        cache.ImageSignatures[path] =
            MetadataDocumentService
                .CreateArtworkFingerprint(
                    model.Artwork);
        var inspector =
            new SelectionInspectorViewModel(
                new FakeMediaService(model),
                cache,
                new FakeTagWriter(),
                new FakeArtworkService(),
                new FakeFilePicker(),
                new FakeDialogs(),
                new FakeFieldsEditor(),
                new FakeThumbnails(),
                new AppActivityService());
        await inspector.LoadAsync(
            new SelectionContext([path]));
        ArtworkPreviewItem item =
            Assert.Single(
                inspector.ArtworkItems);
        item.ReplaceContent(
            source: null,
            "image/png",
            [4, 5, 6],
            "replacement");
        var captured =
            inspector
                .CreatePendingOperationInputs();
        ArtworkSetPreviewRequest
            capturedRequest =
                Assert.Single(
                    captured.ArtworkEdits!)
                    .Value;
        string appliedFingerprint =
            SelectionInspectorViewModel
                .CreateArtworkFingerprint(
                    capturedRequest.Images);

        item.Description =
            "Newer description";
        SelectionInspectorViewModel
            .PendingChangesAcceptance
            acceptance = Assert.IsType<
                SelectionInspectorViewModel
                    .PendingChangesAcceptance>(
                inspector
                    .AcceptPendingChangesState(
                        captured.ValueEdits,
                        captured.ArtworkEdits,
                        new Dictionary<
                            string,
                            string>
                        {
                            [path] =
                                appliedFingerprint,
                        }));
        inspector
            .PublishPendingChangesAcceptance(
                acceptance);

        Assert.True(
            inspector.HasUnsavedArtworkChanges);
        Assert.Equal(
            "Newer description",
            item.Description);
        Assert.Equal(
            appliedFingerprint,
            inspector
                .CreatePendingSourceExpectations()[
                    path]
                .ArtworkFingerprint);
    }

    private static (
        LibraryViewModel ViewModel,
        FakeMetadataOperationService Operations)
        CreateLibrary(
            string path,
            FakeMetadataOperationService? operations =
                null,
            List<TrackRecord>? records = null,
            WorkbenchViewModel? workbench = null,
            IReviewedFileOperationService?
                fileOperations = null)
    {
        var record = new TrackRecord
        {
            Path = path,
            Artist = "Original artist",
            AlbumArtist = "Original artist",
            Album = "Album",
            Title = "Original title",
            CodecName = "FLAC",
            CodecType = CodecType.Lossless,
            LastWriteTime =
                new DateTime(2026, 7, 25),
        };
        records ??= [];
        records.Add(record);
        var library =
            new FakeLibrary(records);
        var settings =
            new FakeSettings();
        operations ??=
            new FakeMetadataOperationService();
        var inspector =
            new SelectionInspectorViewModel(
                new FakeMediaService(
                    new MediaFileModel
                    {
                        Path = path,
                        Title = record.Title,
                        Artist = record.Artist,
                        IsWritable = true,
                        KnownFields =
                        [
                            new(
                                TagFields.Title,
                                record.Title),
                            new(
                                TagFields.Artist,
                                record.Artist),
                        ],
                    }),
                library,
                new FakeTagWriter(),
                new FakeArtworkService(),
                new FakeFilePicker(),
                new FakeDialogs(),
                new FakeFieldsEditor(),
                new FakeThumbnails(),
                new AppActivityService(),
                operations);
        var indexing =
            new IndexingViewModel(
                library,
                settings,
                new AppActivityService());
        return (
            new LibraryViewModel(
                library,
                new FakeReindex(),
                settings,
                inspector,
                new NavigationService(),
                indexing,
                new FakeThumbnails(),
                workbench: workbench,
                metadataOperations: operations,
                operationCatalog:
                    new MetadataOperationCatalog(),
                files: new FakeFilePicker(),
                fileOperations: fileOperations),
            operations);
    }

    private static WorkbenchViewModel
        CreateFileOperationWorkbench(
            IReviewedFileOperationService
                fileOperations)
    {
        var settings = new FakeSettings();
        var journals = new OperationJournalService(
            new FileMutationCoordinator());
        return new(
            new EmptyWorkbenchService(),
            new FakeMetadataOperationService(),
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
            fileOperations: fileOperations);
    }

    private static ReviewedFileOperationPlan
        FileOperationPlan(
            string source,
            string destination,
            ReviewedFileOperationKind operationKind,
            FileMutationKind mutationKind)
    {
        var request =
            new ReviewedFileOperationRequest(
                [source],
                operationKind,
                Path.GetDirectoryName(destination),
                Path.GetFileName(destination));
        var item =
            new ReviewedFileOperationItem(
                source,
                destination,
                mutationKind,
                []);
        return new(
            request,
            [item],
            new(
                "library-coordination-test",
                Path.GetDirectoryName(destination) ??
                Path.GetTempPath(),
                Path.Combine(
                    Path.GetTempPath(),
                    Guid.NewGuid().ToString("N")),
                [
                    new(
                        mutationKind,
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

    private static async Task
        WaitForAuthoritativePreviewAsync(
            LibraryViewModel viewModel)
    {
        for (int attempt = 0;
             attempt < 100 &&
             !viewModel
                 .IsDirectPendingPreviewReady;
             attempt++)
            await Task.Delay(
                20,
                TestContext.Current
                    .CancellationToken);
        Assert.True(
            viewModel
                .IsDirectPendingPreviewReady);
    }

    private sealed class
        FailingApplyMetadataOperationService :
        FakeMetadataOperationService
    {
        public int ApplyCalls { get; private set; }

        public override Task<MetadataApplyResult>
            ApplyAsync(
                MetadataOperationPlan plan,
                IProgress<OperationProgress>? progress =
                    null,
                CancellationToken ct = default)
        {
            ApplyCalls++;
            throw new InvalidOperationException(
                "Simulated apply failure.");
        }
    }

    private sealed class
        CommittedThenCancelMetadataOperationService :
        FakeMetadataOperationService
    {
        public int ApplyCalls { get; private set; }
        public Action? AfterCommit { get; set; }

        public override async Task<MetadataApplyResult>
            ApplyAsync(
                MetadataOperationPlan plan,
                IProgress<OperationProgress>? progress =
                    null,
                CancellationToken ct = default)
        {
            ApplyCalls++;
            MetadataApplyResult result =
                await base.ApplyAsync(
                    plan,
                    progress,
                    ct);
            AfterCommit?.Invoke();
            return result;
        }
    }

    private sealed class EmptyWorkbenchService :
        IWorkbenchService
    {
        public Task<WorkbenchLoadResult> LoadAsync(
            WorkbenchLoadRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new WorkbenchLoadResult(
                    [],
                    []));
        }
    }

    private sealed class
        CoordinatedFileOperationService(
            FileMutationKind mutationKind,
            bool failApply = false,
            Action? applying = null,
            Exception? applyFailure = null) :
        IReviewedFileOperationService
    {
        public int ApplyCalls { get; private set; }

        public Task<ReviewedFileOperationPlan>
            PreviewAsync(
                ReviewedFileOperationRequest request,
                IProgress<OperationProgress>? progress =
                    null,
                CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            string source = Assert.Single(
                request.SourcePaths);
            string destination = Path.Combine(
                request.DestinationDirectory ??
                Path.GetDirectoryName(source)!,
                request.FileNameTemplate);
            return Task.FromResult(
                FileOperationPlan(
                    source,
                    destination,
                    request.Kind,
                    mutationKind));
        }

        public Task<FileMutationSummary> ApplyAsync(
            ReviewedFileOperationPlan plan,
            IProgress<OperationProgress>? progress =
                null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ApplyCalls++;
            applying?.Invoke();
            if (applyFailure is not null)
                throw applyFailure;
            if (failApply)
                throw new InvalidOperationException(
                    "Injected file operation failure.");
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
}
