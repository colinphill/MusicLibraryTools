using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class LibraryOperationContextFactoryTests
{
    [Fact]
    public async Task IndexedContextDoesNotRequireAnItunesLibrary()
    {
        string root = Path.Combine(Path.GetTempPath(), $"operation-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string configPath = Path.Combine(root, "library.xml");
            new EditableLibraryConfig().Save(configPath);
            var configuration = new LibraryConfiguration(configPath);
            var cache = new MetadataCache(buildSecondaryIndexes: false);
            var library = new SnapshotLibrary(new(
                configuration,
                configPath,
                7,
                [],
                cache));
            var factory = new LibraryOperationContextFactory(library);

            IndexedLibraryOperationContext context = await factory.CreateIndexedAsync(null);

            Assert.Same(configuration, context.Configuration);
            Assert.Same(cache, context.Cache);
            Assert.Empty(context.IndexLocations);
            Assert.Equal(1, library.SnapshotCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NullConfigurationPathUsesTheActiveLibraryCacheSnapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"operation-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string configPath = Path.Combine(root, "library.xml");
            new EditableLibraryConfig().Save(configPath);
            var configuration = new LibraryConfiguration(configPath);
            var library = new SnapshotLibrary(new(
                configuration,
                configPath,
                7,
                [],
                new MetadataCache(buildSecondaryIndexes: false)));
            var factory = new LibraryOperationContextFactory(library);

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => factory.CreateAsync(null));

            Assert.Equal(1, library.SnapshotCalls);
            Assert.Contains("iTunes library path", error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SnapshotLibrary(LibraryOperationCacheSnapshot snapshot) : ILibraryService
    {
        public int SnapshotCalls { get; private set; }
        public bool IsReady => true;

        public Task<LibraryOperationCacheSnapshot> GetOperationCacheSnapshotAsync(
            CancellationToken ct = default)
        {
            SnapshotCalls++;
            return Task.FromResult(snapshot);
        }

        public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LibrarySnapshot> BuildSnapshotAsync(
            LibraryGrouping grouping = LibraryGrouping.AlbumArtist,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FileDetails?> GetFileDetailsAsync(
            string path, bool includeArtwork, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<byte[]?> GetFirstImageAsync(
            string path, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetImageSignaturesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
