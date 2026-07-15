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
        var viewModel = new OperationsViewModel(journals, new StubFiles(), settings)
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
        var viewModel = new OperationsViewModel(journals, new StubFiles(), settings)
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
        var viewModel = new OperationsViewModel(journals, new StubFiles(), settings)
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

    private sealed class RecordingJournals(
        OperationJournalDiscoveryResult result,
        OperationBrowseResult? browse = null) : IOperationJournalService
    {
        public IReadOnlyList<string>? Roots { get; private set; }
        public int BrowseCalls { get; private set; }

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
