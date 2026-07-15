using MusicLibrary.App.Services;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class OperationsViewModelTests
{
    [Fact]
    public async Task RefreshIncludesRememberedIngestAndAdditionalRootsAndProjectsRuns()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        string ingest = Path.Combine(temp.Path, "incoming");
        string device = Path.Combine(temp.Path, "device");
        settings.SetPreference("Ingest.SourceDirectory", ingest);
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
        Assert.Equal("45", settings.GetPreference("Operations.RetentionDays"));

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

    private sealed class StubDialogs : IDialogService
    {
        public Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths) => Task.FromResult(false);
        public Task<string?> ShowConfigEditorAsync(string? existingPath) => Task.FromResult<string?>(null);
        public Task<string?> ShowIngestConfigEditorAsync(string? existingPath) => Task.FromResult<string?>(null);
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
