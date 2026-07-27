using System.Collections.Immutable;
using System.Globalization;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class RestorePresentationRegressionTests
{
    [Fact]
    public async Task Operations_restore_consumes_durable_plan_when_post_commit_browse_fails()
    {
        var journals = new RestoreJournalStub
        {
            FailBrowseAfterApply = true,
        };
        var viewModel = CreateOperationsViewModel(journals);

        await viewModel.OpenRunFromHistoryAsync(journals.Summary);
        viewModel.SelectAllRestorableCommand.Execute(null);
        await viewModel.PreviewRestoreCommand.ExecuteAsync(null);

        Assert.True(viewModel.ShowRestorePreview);
        Assert.True(viewModel.ApplyRestoreCommand.CanExecute(null));

        await viewModel.ApplyRestoreCommand.ExecuteAsync(null);
        await viewModel.RestorePostCommitObservation;

        Assert.Equal(1, journals.ApplyCalls);
        Assert.False(viewModel.ShowRestorePreview);
        Assert.False(viewModel.ApplyRestoreCommand.CanExecute(null));
        Assert.Equal(
            "Operations.Status.RestoreCompleted.One",
            viewModel.StatusText);
        Assert.Equal(
            RestoreJournalStub.PostCommitBrowseFailure,
            viewModel.StatusDiagnosticDetail);
        Assert.DoesNotContain(
            "RestoreFailed",
            viewModel.StatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operations_restore_surfaces_catalog_reconciliation_issues()
    {
        var issue = new OperationIssue(
            "catalog-refresh",
            OperationIssueSeverity.Warning,
            "The restored file could not be refreshed in the catalog.",
            @"C:\Music\track.flac");
        var journals = new RestoreJournalStub
        {
            RestoreIssues = [issue],
        };
        var viewModel = CreateOperationsViewModel(journals);

        await viewModel.OpenRunFromHistoryAsync(journals.Summary);
        viewModel.SelectAllRestorableCommand.Execute(null);
        await viewModel.PreviewRestoreCommand.ExecuteAsync(null);
        await viewModel.ApplyRestoreCommand.ExecuteAsync(null);
        await viewModel.RestorePostCommitObservation;

        Assert.Equal(
            "Operations.Status.RestoreCompleted.One",
            viewModel.StatusText);
        Assert.Equal(
            $"{issue.Path}: {issue.Message}",
            viewModel.StatusDiagnosticDetail);
        Assert.True(viewModel.HasStatusDiagnosticDetail);
    }

    [Fact]
    public async Task Operations_restore_terminalizes_before_delayed_post_commit_reconciliation()
    {
        var reconciliation =
            new TaskCompletionSource<
                IReadOnlyList<OperationIssue>>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var journals =
            new RestoreJournalStub
            {
                PostCommitReconciliation =
                    reconciliation.Task,
            };
        var activities =
            new AppActivityService();
        var viewModel =
            CreateOperationsViewModel(
                journals,
                activities);

        await viewModel.OpenRunFromHistoryAsync(
            journals.Summary);
        viewModel.SelectAllRestorableCommand
            .Execute(null);
        await viewModel.PreviewRestoreCommand
            .ExecuteAsync(null);
        await viewModel.ApplyRestoreCommand
            .ExecuteAsync(null);

        Assert.False(
            viewModel.IsBusy);
        Assert.False(
            viewModel.CancelCommand.CanExecute(
                null));
        Assert.Equal(
            "Operations.Status.RestoreCompleted.One",
            viewModel.StatusText);
        Assert.False(
            viewModel.RestorePostCommitObservation
                .IsCompleted);
        AppActivity activity =
            Assert.Single(
                activities.Activities,
                item => item.Title ==
                    "Operations.Activity.Restore.Title");
        Assert.Equal(
            AppActivityState.Completed,
            activity.State);
        Assert.False(
            activity.CanCancel);

        var warning =
            new OperationIssue(
                "late-catalog-warning",
                OperationIssueSeverity.Warning,
                "The committed restore needs catalog attention.",
                @"C:\Music\track.flac");
        reconciliation.SetResult(
            [warning]);
        await viewModel.RestorePostCommitObservation;

        Assert.Equal(
            $"{warning.Path}: {warning.Message}",
            viewModel.StatusDiagnosticDetail);
        Assert.Equal(
            "Operations.Status.RestoreCompleted.One",
            viewModel.StatusText);
    }

    [Fact]
    public async Task Operations_restore_does_not_append_late_warnings_to_a_newer_status()
    {
        var reconciliation =
            new TaskCompletionSource<
                IReadOnlyList<OperationIssue>>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var journals =
            new RestoreJournalStub
            {
                PostCommitReconciliation =
                    reconciliation.Task,
            };
        var viewModel =
            CreateOperationsViewModel(
                journals);

        await viewModel.OpenRunFromHistoryAsync(
            journals.Summary);
        viewModel.SelectAllRestorableCommand
            .Execute(null);
        await viewModel.PreviewRestoreCommand
            .ExecuteAsync(null);
        await viewModel.ApplyRestoreCommand
            .ExecuteAsync(null);

        await viewModel.RefreshCommand
            .ExecuteAsync(null);
        Assert.Equal(
            "Operations.Status.NoSearchRoots",
            viewModel.StatusText);

        reconciliation.SetResult(
        [
            new(
                "stale-warning",
                OperationIssueSeverity.Warning,
                "This belongs to the prior restore."),
        ]);
        await viewModel.RestorePostCommitObservation;

        Assert.Equal(
            "Operations.Status.NoSearchRoots",
            viewModel.StatusText);
        Assert.Null(
            viewModel.StatusDiagnosticDetail);
    }

    [Fact]
    public async Task Operations_restore_does_not_append_late_warnings_to_a_new_restore_preview()
    {
        var reconciliation =
            new TaskCompletionSource<
                IReadOnlyList<OperationIssue>>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var journals =
            new RestoreJournalStub
            {
                KeepEntryAfterApply = true,
                PostCommitReconciliation =
                    reconciliation.Task,
            };
        var viewModel =
            CreateOperationsViewModel(
                journals);

        await viewModel.OpenRunFromHistoryAsync(
            journals.Summary);
        viewModel.SelectAllRestorableCommand
            .Execute(null);
        await viewModel.PreviewRestoreCommand
            .ExecuteAsync(null);
        await viewModel.ApplyRestoreCommand
            .ExecuteAsync(null);

        viewModel.SelectAllRestorableCommand
            .Execute(null);
        await viewModel.PreviewRestoreCommand
            .ExecuteAsync(null);
        Assert.Equal(
            "Operations.RestorePreview.Ready.One",
            viewModel.StatusText);

        reconciliation.SetResult(
        [
            new(
                "stale-preview-warning",
                OperationIssueSeverity.Warning,
                "This belongs to the completed restore."),
        ]);
        await viewModel.RestorePostCommitObservation;

        Assert.Equal(
            "Operations.RestorePreview.Ready.One",
            viewModel.StatusText);
        Assert.Null(
            viewModel.StatusDiagnosticDetail);
        Assert.True(
            viewModel.ShowRestorePreview);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task Library_undo_honors_catalog_ownership_and_surfaces_history_issues(
        bool historyReconcilesCatalog,
        int expectedFallbackReindexCalls)
    {
        const string path = @"C:\Music\undo.flac";
        var library = new FakeLibrary(
        [
            new TrackRecord
            {
                Path = path,
                Artist = "Artist",
                AlbumArtist = "Artist",
                Album = "Album",
                Title = "Title",
                CodecName = "FLAC",
                CodecType = CodecType.Lossless,
                DurationInSeconds = 120,
                LastWriteTime = new DateTime(2026, 1, 1),
            },
        ]);
        var settings = new FakeSettings();
        var activities = new AppActivityService();
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(),
            library,
            new FakeTagWriter(),
            new FakeArtworkService(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeFieldsEditor(),
            new FakeThumbnails(),
            activities);
        var warning = new OperationIssue(
            "undo-catalog-warning",
            OperationIssueSeverity.Warning,
            "The restored catalog row needs attention.",
            path);
        var history = new UndoHistoryStub(
            new EditHistoryEntry(
                Guid.NewGuid(),
                "Reviewed edit",
                DateTimeOffset.UtcNow,
                ImmutableArray<string>.Empty,
                [path],
                null),
            historyReconcilesCatalog,
            [warning]);
        var reindex = new FakeReindex();
        var viewModel = new LibraryViewModel(
            library,
            reindex,
            settings,
            inspector,
            new NavigationService(),
            new IndexingViewModel(
                library,
                settings,
                activities),
            new FakeThumbnails(),
            dialogs: new FakeDialogs(),
            history: history,
            localization: new KeyLocalizationService());
        await viewModel.ReloadAsync();

        await viewModel.UndoLibraryOperationCommand.ExecuteAsync(null);

        Assert.Equal(1, history.UndoCalls);
        Assert.Equal(expectedFallbackReindexCalls, reindex.Paths.Count);
        if (historyReconcilesCatalog)
            Assert.Empty(reindex.Paths);
        else
            Assert.Equal([path], reindex.Paths);
        Assert.Equal(
            "Library.Operation.Restore.Complete.One",
            viewModel.OperationStatus);
        Assert.Equal(
            $"{warning.Path}: {warning.Message}",
            viewModel.OperationDiagnosticDetail);
    }

    private static OperationsViewModel CreateOperationsViewModel(
        RestoreJournalStub journals,
        IActivityService? activities = null) =>
        new(
            journals,
            new NullFileDialogService(),
            new ConfirmingOperationDialogService(),
            new FakeSettings(),
            activities: activities,
            localization: new KeyLocalizationService());

    private sealed class RestoreJournalStub : IOperationJournalService
    {
        public const string PostCommitBrowseFailure =
            "The restored run could not be refreshed.";

        private readonly OperationFileEntry _entry = new(
            @"C:\Music\track.flac",
            @"C:\Recovery\track.flac",
            "track.flac",
            OperationEntryKind.Quarantined,
            true,
            false);

        public OperationJournalSummary Summary { get; } = new(
            "MusicLibraryManager",
            OperationJournalKind.ReviewedChange,
            OperationJournalState.Completed,
            @"C:\Recovery\run",
            @"C:\Recovery\run\journal.tsv",
            DateTimeOffset.UtcNow,
            1);

        public bool FailBrowseAfterApply { get; init; }
        public bool KeepEntryAfterApply { get; init; }
        public IReadOnlyList<OperationIssue> RestoreIssues { get; init; } = [];
        public Task<IReadOnlyList<OperationIssue>>?
            PostCommitReconciliation
        {
            get;
            init;
        }
        public int ApplyCalls { get; private set; }

        public Task<OperationJournalDiscoveryResult> DiscoverAsync(
            IReadOnlyList<string> searchRoots,
            CancellationToken ct = default) =>
            Task.FromResult(
                new OperationJournalDiscoveryResult([Summary], []));

        public Task<OperationBrowseResult> BrowseAsync(
            OperationJournalSummary run,
            CancellationToken ct = default)
        {
            if (ApplyCalls > 0 && FailBrowseAfterApply)
                throw new InvalidOperationException(
                    PostCommitBrowseFailure);
            IReadOnlyList<OperationFileEntry> entries =
                ApplyCalls == 0 ||
                KeepEntryAfterApply
                    ? [_entry]
                    : [];
            return Task.FromResult(
                new OperationBrowseResult(
                    @"C:\Music",
                    entries,
                    []));
        }

        public Task<OperationRestorePlan> PreviewRestoreAsync(
            OperationJournalSummary run,
            IReadOnlyList<OperationFileEntry> entries,
            CancellationToken ct = default)
        {
            OperationFileEntry entry = Assert.Single(entries);
            var action = new OperationRestoreAction(
                entry.CurrentPath!,
                entry.OriginalPath,
                entry.OriginalPath + ".existing",
                new OperationPathSnapshot(
                    true,
                    false,
                    100,
                    DateTime.UtcNow),
                OperationPathSnapshot.Missing(
                    entry.OriginalPath),
                entry.Kind);
            return Task.FromResult(
                new OperationRestorePlan(
                    run,
                    @"C:\Recovery\run\restore.tsv",
                    [action],
                    0));
        }

        public Task<OperationRestoreResult> ApplyRestoreAsync(
            OperationRestorePlan plan,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            ApplyCalls++;
            progress?.Report(1);
            return Task.FromResult(
                new OperationRestoreResult(1, 0)
                {
                    Issues = RestoreIssues,
                    PostCommitReconciliation =
                        PostCommitReconciliation is null
                            ? null
                            : new(
                                PostCommitReconciliation),
                });
        }

        public Task<OperationPurgePlan> PreviewPurgeAsync(
            IReadOnlyList<OperationJournalSummary> runs,
            int retentionDays,
            DateTimeOffset? nowUtc = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationPurgeResult> ApplyPurgeAsync(
            OperationPurgePlan plan,
            IProgress<int>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class UndoHistoryStub(
        EditHistoryEntry entry,
        bool reconcilesInternalCatalog,
        IReadOnlyList<OperationIssue> issues) :
        IEditHistoryService
    {
        private readonly List<EditHistoryEntry> _entries = [entry];

        public IReadOnlyList<EditHistoryEntry> Entries => _entries;
        public IReadOnlyList<EditHistoryEntry> RedoEntries => [];
        public IReadOnlyList<OperationIssue> LastUndoIssues => issues;
        public bool ReconcilesInternalCatalogOnUndo =>
            reconcilesInternalCatalog;
        public bool CanUndo => _entries.Count > 0;
        public bool CanRedo => false;
        public int UndoCalls { get; private set; }

        public void Record(EditHistoryEntry historyEntry) =>
            _entries.Insert(0, historyEntry);

        public Task<int> UndoLatestAsync(
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UndoCalls++;
            _entries.RemoveAt(0);
            progress?.Report(1);
            return Task.FromResult(1);
        }
    }

    private sealed class NullFileDialogService : IFileDialogService
    {
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(
            string title,
            string? suggestedName = null,
            string? defaultExtension = null,
            IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(null);
    }

    private sealed class ConfirmingOperationDialogService :
        IDialogService
    {
        public Task<bool> ShowFieldsEditorAsync(
            IReadOnlyList<string> paths) =>
            Task.FromResult(false);

        public Task<string?> ShowConfigEditorAsync(
            string? existingPath) =>
            Task.FromResult<string?>(null);

        public Task<bool> ConfirmApplyAsync(
            string title,
            string message,
            string primaryText) =>
            Task.FromResult(true);

        public Task<bool> ConfirmCdDerivationAsync(
            IngestApprovalItem item) =>
            Task.FromResult(false);

        public Task<bool> ConfirmRestoreAsync(
            OperationRestorePlan plan) =>
            Task.FromResult(true);

        public Task<bool> ConfirmPurgeAsync(
            OperationPurgePlan plan) =>
            Task.FromResult(false);
    }

    private sealed class KeyLocalizationService :
        ILocalizationService
    {
        public CultureInfo CurrentUICulture { get; } =
            CultureInfo.GetCultureInfo("en-US");

        public IReadOnlyList<CultureInfo> SupportedCultures { get; } =
            [CultureInfo.GetCultureInfo("en-US")];

        public event EventHandler? CultureChanged;

        public string Get(string key) => key;

        public string Format(
            string key,
            params object?[] arguments) =>
            key;

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            $"{key}.{(count == 1 ? "One" : "Other")}";

        public IReadOnlyDictionary<string, string> Snapshot() =>
            new Dictionary<string, string>();

        public void SetCulture(string cultureName) =>
            CultureChanged?.Invoke(this, EventArgs.Empty);
    }
}
