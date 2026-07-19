using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using iTunes.Binary;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class PlaylistExportServiceTests
{
    [Fact]
    public async Task PreviewEmbedsRenderedBytesAndApplyWritesThoseExactBytes()
    {
        using var workspace = new TempDirectory();
        LibraryOperationContext context = CreateContext(workspace.Path);
        var service = CreateService(context);

        PlaylistExportPlan plan = await service.PreviewAsync(
            new(Path.Combine(workspace.Path, "library.xml")),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        FileMutationAction write = Assert.Single(plan.MutationPlan.Actions,
            action => action.Kind == FileMutationKind.Write &&
                      action.DestinationPath.EndsWith("Favorites.m3u",
                          StringComparison.OrdinalIgnoreCase));
        Assert.False(write.Content.IsDefaultOrEmpty);
        Assert.Contains("#EXTM3U", Encoding.UTF8.GetString(write.Content.AsSpan()));
        Assert.False(File.Exists(write.DestinationPath));

        PlaylistExportResult result = await service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(write.DestinationPath));
        Assert.Equal(write.Content.ToArray(), File.ReadAllBytes(write.DestinationPath));
        Assert.Equal(plan.Targets.Sum(target => target.Files.Count), result.PlaylistCount);
    }

    [Fact]
    public async Task CleanDeletesAllTargetContentsAndWritesFreshFilesWithoutRecoveryArtifacts()
    {
        using var workspace = new TempDirectory();
        LibraryOperationContext context = CreateContext(workspace.Path);
        string targetRoot = context.Configuration.PlaylistTargets.Single().Target;
        string existingPlaylist = Path.Combine(targetRoot, "Favorites.m3u");
        string unrelated = Path.Combine(targetRoot, "nested", "unrelated.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        await File.WriteAllTextAsync(existingPlaylist, "old playlist",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(unrelated, "unrelated",
            TestContext.Current.CancellationToken);
        var service = CreateService(context);

        PlaylistExportPlan plan = await service.PreviewAsync(
            new(Path.Combine(workspace.Path, "library.xml"), Clean: true),
            ct: TestContext.Current.CancellationToken);

        Assert.False(plan.MutationPlan.RetainRecovery);
        Assert.Equal(string.Empty, plan.MutationPlan.RecoveryRoot);
        Assert.Equal(2, plan.MutationPlan.Actions.Count(action =>
            action.Kind == FileMutationKind.Delete));
        Assert.DoesNotContain(plan.MutationPlan.Actions, action =>
            action.Kind is FileMutationKind.Quarantine or FileMutationKind.ReplaceGenerated);
        Assert.Contains(plan.MutationPlan.Actions, action =>
            action.Kind == FileMutationKind.Write && action.DestinationPath == existingPlaylist);

        PlaylistExportResult result = await service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Mutations.Deleted);
        Assert.Equal(0, result.Mutations.Quarantined);
        Assert.Null(result.Mutations.JournalPath);
        Assert.True(File.Exists(existingPlaylist));
        Assert.Contains("#EXTM3U", await File.ReadAllTextAsync(existingPlaylist,
            TestContext.Current.CancellationToken));
        Assert.False(File.Exists(unrelated));
        Assert.False(Directory.Exists(Path.GetDirectoryName(unrelated)));
        Assert.Empty(Directory.GetDirectories(workspace.Path, "playlists.CrossSyncPlaylists-*"));
    }

    [Fact]
    public async Task ApplyRejectsDestinationCreatedAfterPreviewBeforeWritingAnything()
    {
        using var workspace = new TempDirectory();
        var service = CreateService(CreateContext(workspace.Path));
        PlaylistExportPlan plan = await service.PreviewAsync(
            new(Path.Combine(workspace.Path, "library.xml")),
            ct: TestContext.Current.CancellationToken);
        FileMutationAction first = plan.MutationPlan.Actions.First(action =>
            action.Kind == FileMutationKind.Write);
        Directory.CreateDirectory(Path.GetDirectoryName(first.DestinationPath)!);
        File.WriteAllText(first.DestinationPath, "external change");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken));

        Assert.Equal("external change", File.ReadAllText(first.DestinationPath));
        Assert.False(Directory.Exists(plan.MutationPlan.RecoveryRoot));
        Assert.DoesNotContain(plan.MutationPlan.Actions.Where(action => action != first),
            action => File.Exists(action.DestinationPath));
    }

    [Fact]
    public async Task PerSetOffsetOverridesTheIndexRootDefault()
    {
        using var workspace = new TempDirectory();
        var service = CreateService(CreateContext(workspace.Path, overrideOffset: true));

        PlaylistExportPlan plan = await service.PreviewAsync(
            new(Path.Combine(workspace.Path, "library.xml")),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        FileMutationAction write = Assert.Single(plan.MutationPlan.Actions,
            action => action.DestinationPath.EndsWith("Favorites.m3u",
                StringComparison.OrdinalIgnoreCase));
        string text = Encoding.UTF8.GetString(write.Content.AsSpan()).Replace('\\', '/');
        Assert.Contains("../override/track.flac", text);
    }

    [Fact]
    public async Task ConflictingOffsetsForSelectedSetsBlockTheExport()
    {
        using var workspace = new TempDirectory();
        var service = CreateService(CreateContext(workspace.Path, conflictingOffsets: true));

        PlaylistExportPlan plan = await service.PreviewAsync(
            new(Path.Combine(workspace.Path, "library.xml")),
            ct: TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Issues, issue => issue.Code == "ambiguous-set-offset" &&
            issue.Severity == OperationIssueSeverity.Blocker);
    }

    private static PlaylistExportService CreateService(LibraryOperationContext context) =>
        new(new StubContextFactory(context), new FileInventoryService(),
            new FileMutationPlanExecutor(new FileMutationCoordinator()));

    private static LibraryOperationContext CreateContext(string workspace,
        bool overrideOffset = false, bool conflictingOffsets = false)
    {
        string sourceRoot = Directory.CreateDirectory(Path.Combine(workspace, "source")).FullName;
        string targetRoot = Path.Combine(workspace, "playlists");
        string source = Path.Combine(sourceRoot, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), source);
        string configPath = Path.Combine(workspace, "library.xml");
        var indexTarget = new XElement("IndexTarget",
            new XAttribute("Path", sourceRoot),
            new XAttribute("Offset", "../default"),
            new XElement("Set", new XAttribute("Name", "Primary"),
                overrideOffset || conflictingOffsets
                    ? new XAttribute("Offset", overrideOffset ? "../override" : "../one")
                    : null));
        if (conflictingOffsets)
            indexTarget.Add(new XElement("Set", new XAttribute("Name", "Car2"),
                new XAttribute("Offset", "../two")));
        new XDocument(new XElement("LibraryConfiguration",
            new XElement("DatabaseFile", Path.Combine(workspace, "cache.db")),
            indexTarget,
            new XElement("PlaylistTarget", new XAttribute("Type", "m3u"),
                new XAttribute("Set", conflictingOffsets ? "Primary,Car2" : "Primary"), targetRoot),
            new XElement("LengthLimit", "255"),
            new XElement("DiscNumLengthLimit", "255"))).Save(configPath);
        var configuration = new LibraryConfiguration(configPath);

        var entry = new MetadataCacheEntry(MediaFile.GetFile(source, readOnly: true),
            File.GetLastWriteTimeUtc(source));
        entry.Strip();
        var cache = new MetadataCache();
        cache.FileCache[source] = entry;

        byte[] header = new byte[800];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(44), 3);
        var track = new ItlTrack
        {
            Id = 1,
            Header = header,
            DataObjects =
            [
                StringObject(ItlDataType.Location, new Uri(source).AbsoluteUri),
                StringObject(ItlDataType.Title, "TestTitle"),
                StringObject(ItlDataType.Artist, "TestArtist"),
                StringObject(ItlDataType.Album, "TestAlbum"),
            ],
        };
        var master = new ItlPlaylist { Name = "####!####", TrackIds = [1] };
        var playlist = new ItlPlaylist { Name = "Favorites", TrackIds = [1] };
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
            Playlists = [master, playlist],
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
            "mlplaylist_" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
