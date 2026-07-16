using System.Buffers.Binary;
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

public sealed class CarCardServiceTests
{
    [Fact]
    public async Task InitializePreviewIsReadOnlyAndApplyCreatesBalancedProjection()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string source = Path.Combine(sourceRoot, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), source);
        string destination = Path.Combine(workspace.Path, "card");
        var service = CreateService(CreateContext(workspace.Path, sourceRoot, destination, source));

        CarCardPlan plan = await service.PreviewAsync(new(Path.Combine(workspace.Path, "library.xml"),
            Initialize: true), ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        Assert.False(Directory.Exists(destination));
        Assert.Contains(plan.MutationPlan.Actions, action => action.Kind == FileMutationKind.Copy);
        Assert.Contains(plan.MutationPlan.Actions, action =>
            action.DestinationPath.EndsWith("syncdb.xml", StringComparison.OrdinalIgnoreCase));
        CarCardResult result = await service.ApplyAsync(plan, ct: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(destination, ".update-car-card-root")));
        Assert.True(File.Exists(Path.Combine(destination, "syncdb.xml")));
        Assert.True(Directory.EnumerateFiles(destination, "track.flac", SearchOption.AllDirectories).Any());
        Assert.True(File.Exists(Path.Combine(destination, "Playlists", "Favorites.m3u")));
        Assert.True(File.Exists(result.Mutations.JournalPath));

        CarCardPlan second = await service.PreviewAsync(new(Path.Combine(workspace.Path, "library.xml")),
            ct: TestContext.Current.CancellationToken);
        Assert.True(second.CanApply);
        Assert.Empty(second.MutationPlan.Actions);
    }

    [Fact]
    public async Task SourceChangeAfterPreviewIsRejectedBeforeTargetCreation()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string source = Path.Combine(sourceRoot, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), source);
        string destination = Path.Combine(workspace.Path, "card");
        var service = CreateService(CreateContext(workspace.Path, sourceRoot, destination, source));
        CarCardPlan plan = await service.PreviewAsync(new(Path.Combine(workspace.Path, "library.xml"),
            Initialize: true), ct: TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(source, "changed", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(plan,
            ct: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(destination));
        Assert.False(Directory.Exists(plan.MutationPlan.RecoveryRoot));
    }

    [Fact]
    public async Task RemovedLibraryTrackRequiresExplicitAllowance()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string source = Path.Combine(sourceRoot, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), source);
        string destination = Path.Combine(workspace.Path, "card");
        string config = Path.Combine(workspace.Path, "library.xml");
        var initial = CreateService(CreateContext(workspace.Path, sourceRoot, destination, source));
        CarCardPlan initialPlan = await initial.PreviewAsync(new(config, Initialize: true),
            ct: TestContext.Current.CancellationToken);
        await initial.ApplyAsync(initialPlan, ct: TestContext.Current.CancellationToken);

        var empty = CreateService(CreateContext(workspace.Path, sourceRoot, destination, source,
            includeTrack: false));
        CarCardPlan blocked = await empty.PreviewAsync(new(config),
            ct: TestContext.Current.CancellationToken);
        Assert.False(blocked.CanApply);
        Assert.Equal(1, blocked.RemovedTrackCount);
        Assert.Contains(blocked.Issues, issue => issue.Code == "removal-approval");

        CarCardPlan approved = await empty.PreviewAsync(new(config, MaxRemovals: 1),
            ct: TestContext.Current.CancellationToken);
        Assert.True(approved.CanApply);
        Assert.Contains(approved.MutationPlan.Actions, action =>
            action.Kind == FileMutationKind.Quarantine &&
            action.SourcePath.EndsWith("track.flac", StringComparison.OrdinalIgnoreCase));
    }

    private static CarCardService CreateService(LibraryOperationContext context) =>
        new(new StubContextFactory(context), new FileInventoryService(),
            new FileMutationPlanExecutor(new FileMutationCoordinator()));

    private static LibraryOperationContext CreateContext(string workspace, string sourceRoot,
        string destination, string source, bool includeTrack = true)
    {
        string configPath = Path.Combine(workspace, "library.xml");
        new XDocument(new XElement("LibraryConfiguration",
            new XElement("DatabaseFile", Path.Combine(workspace, "cache.db")),
            new XElement("IndexTarget", sourceRoot), new XElement("BaseDir", destination),
            new XElement("SyncTarget", Path.Combine(workspace, "unused")),
            new XElement("LengthLimit", "255"), new XElement("DiscNumLengthLimit", "255"))).Save(configPath);
        var configuration = new LibraryConfiguration(configPath);
        var entry = new MetadataCacheEntry(MediaFile.GetFile(source, readOnly: true),
            File.GetLastWriteTimeUtc(source));
        typeof(MetadataCacheEntry).GetField("_length", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(entry, new FileInfo(source).Length);
        entry.Strip();
        var cache = new MetadataCache();
        cache.FileCache[source] = entry;
        byte[] header = new byte[800];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(44), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(128), 42);
        var track = new ItlTrack { Id = 1, Header = header, DataObjects =
        [
            StringObject(ItlDataType.Title, entry.Title), StringObject(ItlDataType.Artist, entry.Artist),
            StringObject(ItlDataType.AlbumArtist, entry.AlbumArtist), StringObject(ItlDataType.Album, entry.Album),
            StringObject(ItlDataType.Kind, "FLAC audio file"),
            StringObject(ItlDataType.Location, new Uri(source).AbsoluteUri),
        ] };
        var library = new ItlLibrary
        {
            Envelope = new ItlEnvelope { Version = "test", LibraryPersistentId = 0,
                SectionCount = 0, MaxCryptSize = 0, FileLength = 0, RawHeader = [], Body = [] },
            Sections = [], Tracks = includeTrack ? [track] : [], Albums = [], Artists = [],
            Playlists = [new ItlPlaylist { Name = "####!####", TrackIds = includeTrack ? [1] : [] },
                new ItlPlaylist { Name = "Favorites", TrackIds = includeTrack ? [1] : [] }],
        };
        return new(configuration, configuration.IndexLocations.ToArray(), cache, library,
            new Dictionary<int, ItlTrack> { [1] = track }, Path.Combine(workspace, "test.itl"));
    }

    private static ItlDataObject StringObject(ItlDataType type, string? value)
    {
        var result = new ItlDataObject { Type = (int)type, Raw = [] };
        typeof(ItlDataObject).GetProperty("Text", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(result, value ?? "");
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
            "mlcar_" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
