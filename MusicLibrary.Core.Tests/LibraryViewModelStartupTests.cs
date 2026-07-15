using MetadataCaching;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class LibraryViewModelStartupTests
{
    [Fact]
    public async Task LoadingConfigurationDefersIndexUntilStartupCoordinatorRuns()
    {
        using var temp = new TempDirectory();
        string music = temp.Directory("music");
        string config = Path.Combine(temp.Path, "library.xml");
        new EditableLibraryConfig
        {
            DatabaseFile = Path.Combine(temp.Path, "cache.db"),
            IndexTargets = [new() { Target = music }],
        }.Save(config);
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        var library = new RecordingLibraryService();
        var viewModel = new LibraryViewModel(
            library, new IndexBenchmarkService(settings), settings);

        settings.LoadConfig(config);

        Assert.Equal(0, library.IndexCalls);
        Assert.True(viewModel.IndexCommand.CanExecute(null));
        Assert.Contains("automatic indexing will follow", viewModel.StatusText);

        await viewModel.StartAutomaticIndexAsync();

        Assert.Equal(1, library.IndexCalls);
    }

    private sealed class RecordingLibraryService : ILibraryService
    {
        public bool IsReady => true;
        public int IndexCalls { get; private set; }

        public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
        {
            IndexCalls++;
            return Task.FromResult((0, 0, 0, 0));
        }

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
            System.IO.Path.GetTempPath(), "library-startup-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => System.IO.Directory.CreateDirectory(Path);

        public string Directory(string name)
        {
            string path = System.IO.Path.Combine(Path, name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
