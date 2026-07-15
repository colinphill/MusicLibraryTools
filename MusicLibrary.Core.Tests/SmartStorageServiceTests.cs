using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class SmartStorageServiceTests
{
    [Fact]
    public async Task InitializePreviewIsReadOnlyAndApplyCreatesReviewedProjection()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string source = Path.Combine(sourceRoot, "song.mp3");
        await File.WriteAllTextAsync(source, "audio", TestContext.Current.CancellationToken);
        string destination = Path.Combine(workspace.Path, "storage");
        SmartStorageService service = CreateService(Library(sourceRoot, [Track(1, "A1", source)],
            [new("Favorites", false, [1])]));

        SmartStoragePlan plan = await service.PreviewAsync(
            new(destination, Initialize: true), ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        Assert.False(Directory.Exists(destination));
        Assert.Contains(plan.MutationPlan.Actions, action =>
            action.DestinationPath.EndsWith("filedb.xml", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.MutationPlan.Actions, action =>
            action.DestinationPath.EndsWith("Favorites.m3u", StringComparison.OrdinalIgnoreCase));

        SmartStorageResult result = await service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(destination, ".update-smart-storage-root")));
        Assert.Equal("audio", await File.ReadAllTextAsync(
            Path.Combine(destination, "0001", "Artist", "Album", "song.mp3"),
            TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(destination, "Playlists", "Favorites.m3u")));
        Assert.True(File.Exists(Path.Combine(destination, "filedb.xml")));
        Assert.True(File.Exists(Path.Combine(destination, "artworkdb.bin")));
        Assert.True(File.Exists(result.Mutations.JournalPath));

        SmartStoragePlan secondPlan = await service.PreviewAsync(
            new(destination), ct: TestContext.Current.CancellationToken);
        Assert.True(secondPlan.CanApply);
        Assert.Empty(secondPlan.MutationPlan.Actions);
    }

    [Fact]
    public async Task SourceChangeAfterPreviewIsRejectedBeforeInitialization()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string source = Path.Combine(sourceRoot, "song.mp3");
        await File.WriteAllTextAsync(source, "audio", TestContext.Current.CancellationToken);
        string destination = Path.Combine(workspace.Path, "storage");
        SmartStorageService service = CreateService(Library(sourceRoot, [Track(1, "A1", source)], []));
        SmartStoragePlan plan = await service.PreviewAsync(
            new(destination, Initialize: true), ct: TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(source, "changed", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(destination));
        Assert.False(Directory.Exists(plan.MutationPlan.RecoveryRoot));
    }

    [Fact]
    public async Task StaleTrackRequiresRemovalAllowanceAndIsThenQuarantined()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string source = Path.Combine(sourceRoot, "song.mp3");
        await File.WriteAllTextAsync(source, "audio", TestContext.Current.CancellationToken);
        string destination = Path.Combine(workspace.Path, "storage");
        SmartStorageService initial = CreateService(Library(sourceRoot, [Track(1, "A1", source)], []));
        SmartStoragePlan initialPlan = await initial.PreviewAsync(
            new(destination, Initialize: true), ct: TestContext.Current.CancellationToken);
        await initial.ApplyAsync(initialPlan, ct: TestContext.Current.CancellationToken);
        string target = Path.Combine(destination, "0001", "Artist", "Album", "song.mp3");
        var emptyLibrary = Library(sourceRoot, [], []);

        SmartStoragePlan blocked = await CreateService(emptyLibrary).PreviewAsync(
            new(destination), ct: TestContext.Current.CancellationToken);
        Assert.False(blocked.CanApply);
        Assert.Contains(blocked.Issues, issue => issue.Code == "removal-limit");
        Assert.True(File.Exists(target));

        SmartStorageService cleanup = CreateService(emptyLibrary);
        SmartStoragePlan plan = await cleanup.PreviewAsync(
            new(destination, MaxRemovals: 1), ct: TestContext.Current.CancellationToken);
        SmartStorageResult result = await cleanup.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(target));
        Assert.True(Directory.EnumerateFiles(plan.MutationPlan.RecoveryRoot, "song.mp3",
            SearchOption.AllDirectories).Any());
        Assert.Equal(1, result.Mutations.Quarantined);
    }

    [Fact]
    public async Task MappedTargetCollisionIsBlocking()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string first = Path.Combine(sourceRoot, "one.mp3");
        string second = Path.Combine(sourceRoot, "two.mp3");
        await File.WriteAllTextAsync(first, "one", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "two", TestContext.Current.CancellationToken);
        SmartStorageSourceTrack one = Track(1, "A1", first) with { AlbumArtist = "A-B" };
        SmartStorageSourceTrack two = Track(2, "A2", second) with { AlbumArtist = "AB" };

        SmartStoragePlan plan = await CreateService(Library(sourceRoot, [one, two], [])).PreviewAsync(
            new(Path.Combine(workspace.Path, "storage"), Initialize: true),
            ct: TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Issues, issue => issue.Code == "artist-name-collision");
    }

    private static SmartStorageService CreateService(SmartStorageLibrarySnapshot library) => new(
        new FakeLibraryLoader(library), new FileInventoryService(), new FileMutationPlanExecutor());

    private static SmartStorageLibrarySnapshot Library(string root,
        IReadOnlyList<SmartStorageSourceTrack> tracks,
        IReadOnlyList<SmartStorageSourcePlaylist> playlists) => new(root, tracks, playlists);

    private static SmartStorageSourceTrack Track(int id, string persistentId, string path) => new(
        id, persistentId, path, "MPEG audio file", false, "Artist", "Artist", "Album",
        "Song", "Rock", 1, 2020);

    private sealed class FakeLibraryLoader(SmartStorageLibrarySnapshot library)
        : ISmartStorageLibraryLoader
    {
        public Task<SmartStorageLibrarySnapshot> LoadAsync(string? libraryPath,
            CancellationToken ct = default) => Task.FromResult(library);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "SmartStorageTests", Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
