using System.ComponentModel;
using MetadataCaching;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class LibraryViewPersistenceTests
{
    [Fact]
    public void SavedView_RoundTripsFilterScopeAndColumnLayout()
    {
        using var temp = new TempDirectory();
        string statePath = Path.Combine(temp.Path, "settings.json");
        var settings = new AppSettings(statePath);
        var viewModel = Create(settings);
        foreach (var column in viewModel.Columns)
            column.IsSelected = column.Key is "Artist" or "Album";
        viewModel.SaveGridLayout(
            [("Album", 245), ("Artist", 180)],
            new LibrarySortLayout("Album", ListSortDirection.Descending));
        viewModel.SelectedFilterMode = FilterMode.Regex;
        viewModel.SelectedScope = viewModel.FilterScopes.Single(scope => scope.Key == "Artist");
        viewModel.FilterText = "^(Miles|Coltrane)$";
        viewModel.SavedViewName = "Jazz artists";

        viewModel.SaveCurrentView();

        var restored = Create(new AppSettings(statePath));
        var saved = Assert.Single(restored.SavedViews);
        LibrarySortLayout? appliedSort = null;
        restored.SortLayoutChanged += sort => appliedSort = sort;
        restored.SelectedSavedView = saved;
        Assert.Equal("Jazz artists", restored.SavedViewName);
        Assert.Equal("^(Miles|Coltrane)$", restored.FilterText);
        Assert.Equal(FilterMode.Regex, restored.SelectedFilterMode);
        Assert.Equal("Artist", restored.SelectedScope?.Key);
        Assert.Equal(["Album", "Artist"], restored.VisibleColumns.Select(column => column.Key));
        Assert.Equal(245, restored.WidthFor("Album"));
        Assert.Equal(180, restored.WidthFor("Artist"));
        Assert.Equal(new LibrarySortLayout("Album", ListSortDirection.Descending), saved.Sort);
        Assert.Equal(saved.Sort, appliedSort);
    }

    [Fact]
    public void SavedView_SameNameReplacesExistingViewCaseInsensitively()
    {
        using var temp = new TempDirectory();
        string statePath = Path.Combine(temp.Path, "settings.json");
        var settings = new AppSettings(statePath);
        var viewModel = Create(settings);
        viewModel.SavedViewName = "Recent";
        viewModel.FilterText = "2026";
        viewModel.SaveCurrentView();
        viewModel.SavedViewName = "recent";
        viewModel.FilterText = "2025";

        viewModel.SaveCurrentView();

        Assert.Equal("2025", Assert.Single(viewModel.SavedViews).FilterText);
        Assert.Single(Create(new AppSettings(statePath)).SavedViews);
    }

    [Fact]
    public async Task ReindexSelection_DeduplicatesPathsAndReloadsRows()
    {
        using var temp = new TempDirectory();
        var library = new ReadyLibraryService();
        var reindex = new TrackingReindexService();
        var viewModel = new DetailsGridViewModel(
            library, reindex, new AppSettings(Path.Combine(temp.Path, "settings.json")));

        await viewModel.ReindexPathsAsync(["one.flac", "ONE.FLAC", "two.flac"]);

        Assert.Equal(["one.flac", "two.flac"], reindex.Paths);
        Assert.Equal(1, library.LoadCalls);
        Assert.Equal("Reindexed 2 selected file(s).", viewModel.StatusText);
    }

    private static DetailsGridViewModel Create(IAppSettings settings) =>
        new(new StubLibraryService(), new StubReindexService(), settings);

    private sealed class StubReindexService : IReindexService
    {
        public Task ReindexFileAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TrackingReindexService : IReindexService
    {
        public List<string> Paths { get; } = [];
        public Task ReindexFileAsync(string path, CancellationToken ct = default)
        {
            Paths.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class ReadyLibraryService : ILibraryService
    {
        public bool IsReady => true;
        public int LoadCalls { get; private set; }
        public Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default)
        {
            LoadCalls++;
            return Task.FromResult<IReadOnlyList<TrackRecord>>([]);
        }
        public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LibrarySnapshot> BuildSnapshotAsync(
            LibraryGrouping grouping = LibraryGrouping.AlbumArtist, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<FileDetails?> GetFileDetailsAsync(
            string path, bool includeArtwork, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetImageSignaturesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubLibraryService : ILibraryService
    {
        public bool IsReady => false;
        public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
            IProgress<MetadataCaching.IndexProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LibrarySnapshot> BuildSnapshotAsync(
            LibraryGrouping grouping = LibraryGrouping.AlbumArtist, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<FileDetails?> GetFileDetailsAsync(
            string path, bool includeArtwork, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetImageSignaturesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "library-view-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
