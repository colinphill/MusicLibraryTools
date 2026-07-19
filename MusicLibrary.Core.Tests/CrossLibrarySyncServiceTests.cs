using System.Reflection;
using System.Xml.Linq;
using iTunes.Binary;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class CrossLibrarySyncServiceTests
{
    [Fact]
    public async Task PreviewAndApplyUseReviewedCopyAndQuarantineActions()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string targetRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "target")).FullName;
        string source = Path.Combine(sourceRoot, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), source);
        string stale = Path.Combine(targetRoot, "stale.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), stale);

        LibraryOperationContext context = CreateContext(workspace.Path, sourceRoot, targetRoot, source);
        var service = CreateService(context);
        var request = new CrossLibrarySyncRequest(Path.Combine(workspace.Path, "library.xml"));

        CrossLibrarySyncPlan plan = await service.PreviewAsync(
            request, ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        Assert.Contains(plan.MutationPlan.Actions, action => action.Kind == FileMutationKind.Copy);
        FileMutationAction quarantine = Assert.Single(plan.MutationPlan.Actions,
            action => action.Kind == FileMutationKind.Quarantine);
        Assert.Equal(stale, quarantine.SourcePath);
        Assert.True(File.Exists(stale));
        Assert.False(File.Exists(plan.Files.Single().DestinationPath));

        CrossLibrarySyncResult result = await service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Mutations.Copied);
        Assert.Equal(1, result.Mutations.Quarantined);
        Assert.True(File.Exists(plan.Files.Single().DestinationPath));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(quarantine.DestinationPath));
        Assert.True(File.Exists(result.Mutations.JournalPath));
    }

    [Fact]
    public async Task ConfiguredDeletionPermanentlyDeletesWithoutRecoveryArtifacts()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string targetRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "target")).FullName;
        string source = Path.Combine(sourceRoot, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), source);
        string stale = Path.Combine(targetRoot, "stale.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), stale);

        LibraryOperationContext context = CreateContext(
            workspace.Path, sourceRoot, targetRoot, source, deleteStaleFiles: true);
        var service = CreateService(context);
        var request = new CrossLibrarySyncRequest(Path.Combine(workspace.Path, "library.xml"));

        CrossLibrarySyncPlan plan = await service.PreviewAsync(
            request, ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        FileMutationAction delete = Assert.Single(plan.MutationPlan.Actions,
            action => action.Kind == FileMutationKind.Delete);
        Assert.Equal(stale, delete.SourcePath);
        Assert.False(plan.MutationPlan.RetainRecovery);
        Assert.Equal(string.Empty, plan.MutationPlan.RecoveryRoot);
        Assert.True(File.Exists(stale));

        CrossLibrarySyncResult result = await service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Mutations.Deleted);
        Assert.Equal(0, result.Mutations.Quarantined);
        Assert.False(File.Exists(stale));
        Assert.Null(result.Mutations.JournalPath);
        Assert.Empty(Directory.GetDirectories(workspace.Path, "target.CrossSyncMusic-*"));
    }

    [Fact]
    public async Task ConfiguredDeletionReplacesWithoutQuarantiningPreviousFile()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string targetRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "target")).FullName;
        string source = Path.Combine(sourceRoot, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), source);
        LibraryOperationContext context = CreateContext(
            workspace.Path, sourceRoot, targetRoot, source, deleteStaleFiles: true);
        var service = CreateService(context);
        var request = new CrossLibrarySyncRequest(Path.Combine(workspace.Path, "library.xml"));
        CrossLibrarySyncPlan initial = await service.PreviewAsync(
            request, ct: TestContext.Current.CancellationToken);
        string destination = initial.Files.Single().DestinationPath;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "old destination",
            TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source).AddMinutes(-1));

        CrossLibrarySyncPlan plan = await service.PreviewAsync(
            request, ct: TestContext.Current.CancellationToken);

        Assert.Contains(plan.MutationPlan.Actions,
            action => action.Kind == FileMutationKind.Replace && action.DestinationPath == destination);
        CrossLibrarySyncResult result = await service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Mutations.Replaced);
        Assert.Equal(0, result.Mutations.Quarantined);
        Assert.Null(result.Mutations.JournalPath);
        Assert.Equal(await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(workspace.Path, "target.CrossSyncMusic-*"));
    }

    [Fact]
    public async Task ApplyRejectsStaleSourceBeforeCreatingRecoveryTreeOrDestination()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string targetRoot = Path.Combine(workspace.Path, "target");
        string source = Path.Combine(sourceRoot, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), source);

        LibraryOperationContext context = CreateContext(workspace.Path, sourceRoot, targetRoot, source);
        var service = CreateService(context);
        CrossLibrarySyncPlan plan = await service.PreviewAsync(
            new(Path.Combine(workspace.Path, "library.xml")),
            ct: TestContext.Current.CancellationToken);
        Assert.True(plan.CanApply);

        File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(targetRoot));
        Assert.False(Directory.Exists(plan.MutationPlan.RecoveryRoot));
    }

    [Fact]
    public async Task StaleFilesDoNotRequireASeparateRemovalApproval()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string targetRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "target")).FullName;
        string source = Path.Combine(sourceRoot, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), source);
        string stale = Path.Combine(targetRoot, "stale.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), stale);

        var service = CreateService(CreateContext(workspace.Path, sourceRoot, targetRoot, source));
        CrossLibrarySyncPlan plan = await service.PreviewAsync(
            new(Path.Combine(workspace.Path, "library.xml")),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        Assert.DoesNotContain(plan.Issues, issue => issue.Code == "removal-limit");
        Assert.Contains(plan.MutationPlan.Actions, action =>
            action.Kind == FileMutationKind.Quarantine && action.SourcePath == stale);
        Assert.True(File.Exists(stale));
        Assert.False(Directory.Exists(plan.MutationPlan.RecoveryRoot));
    }

    private static CrossLibrarySyncService CreateService(LibraryOperationContext context) =>
        new(new StubContextFactory(context), new FileInventoryService(),
            new FileMutationPlanExecutor(new FileMutationCoordinator()));

    private static LibraryOperationContext CreateContext(string workspace, string sourceRoot,
        string targetRoot, string source, bool deleteStaleFiles = false)
    {
        string configPath = Path.Combine(workspace, "library.xml");
        new XDocument(new XElement("LibraryConfiguration",
            new XElement("DatabaseFile", Path.Combine(workspace, "cache.db")),
            new XElement("IndexTarget", new XAttribute("Set", "1"), sourceRoot),
            new XElement("IndexTarget",
                new XAttribute("Path", targetRoot),
                new XAttribute("SyncTarget", true)),
            new XElement("SyncPlaylist", "Test Sync"),
            new XElement("CrossSyncMusicSettings",
                new XAttribute("DeleteStaleFiles", deleteStaleFiles)),
            new XElement("LengthLimit", "255"),
            new XElement("DiscNumLengthLimit", "255"))).Save(configPath);
        var configuration = new LibraryConfiguration(configPath);

        var entry = new MetadataCacheEntry(MediaFile.GetFile(source, readOnly: true),
            File.GetLastWriteTimeUtc(source));
        entry.Strip();
        var cache = new MetadataCache();
        cache.FileCache[source] = entry;

        var track = new ItlTrack
        {
            Id = 1,
            Header = new byte[800],
            DataObjects = [StringObject(ItlDataType.Location, new Uri(source).AbsoluteUri)],
        };
        var playlist = new ItlPlaylist { Name = "Test Sync", TrackIds = [1] };
        var library = new ItlLibrary
        {
            Envelope = new ItlEnvelope
            {
                Version = "test",
                LibraryPersistentId = 0,
                SectionCount = 0,
                MaxCryptSize = 0,
                FileLength = 0,
                RawHeader = [],
                Body = [],
            },
            Sections = [],
            Tracks = [track],
            Albums = [],
            Artists = [],
            Playlists = [playlist],
        };
        return new(configuration, configuration.IndexLocations.ToArray(), cache, library,
            new Dictionary<int, ItlTrack> { [1] = track }, Path.Combine(workspace, "test.itl"));
    }

    private static ItlDataObject StringObject(ItlDataType type, string value)
    {
        var result = new ItlDataObject { Type = (int)type, Raw = [] };
        typeof(ItlDataObject).GetProperty("Text", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(result, value);
        return result;
    }

    private sealed class StubContextFactory(LibraryOperationContext context)
        : ILibraryOperationContextFactory
    {
        public Task<LibraryOperationContext> CreateAsync(string? configurationPath,
            string? itunesLibraryPath = null, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(context);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "mlcross_" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
