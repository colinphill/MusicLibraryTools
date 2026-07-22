using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Xml;
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
    [Theory]
    [InlineData("m3u")]
    [InlineData("wpl")]
    public async Task DefaultWritersPreserveLegacySerializedBytes(string playlistType)
    {
        using var workspace = new TempDirectory();
        LibraryOperationContext context = CreateContext(workspace.Path,
            playlistType: playlistType);
        var service = CreateService(context);

        PlaylistExportPlan plan = await service.PreviewAsync(
            new(Path.Combine(workspace.Path, "library.xml")),
            ct: TestContext.Current.CancellationToken);

        PlaylistExportFile file = Assert.Single(Assert.Single(plan.Targets).Files,
            file => file.PlaylistName == "Favorites");
        FileMutationAction write = Assert.Single(plan.MutationPlan.Actions,
            action => action.DestinationPath == file.DestinationPath);
        int duration = context.Cache.FileCache.Values.Single().DurationInSeconds;
        if (duration == 0) duration = -1;
        byte[] expected = playlistType == "wpl"
            ? RenderLegacyWpl("Favorites", "../default/track.flac")
            : RenderLegacyM3u(duration, "../default/track.flac");
        Assert.Equal(expected, write.Content.ToArray());
    }

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
    public async Task CleanDeletesAllTargetContentsWithARecoveryJournal()
    {
        using var workspace = new TempDirectory();
        LibraryOperationContext context = CreateContext(workspace.Path, clean: true);
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
            new(Path.Combine(workspace.Path, "library.xml")),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.MutationPlan.RetainRecovery);
        Assert.NotEmpty(plan.MutationPlan.RecoveryRoot);
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
        Assert.True(File.Exists(result.Mutations.JournalPath));
        Assert.True(File.Exists(existingPlaylist));
        Assert.Contains("#EXTM3U", await File.ReadAllTextAsync(existingPlaylist,
            TestContext.Current.CancellationToken));
        Assert.False(File.Exists(unrelated));
        Assert.Single(Directory.GetDirectories(
            workspace.Path, "playlists.CrossSyncPlaylists-*"));
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

    [Fact]
    public async Task OversizedPlaylistIsReportedInsteadOfSilentlySkipped()
    {
        using var workspace = new TempDirectory();
        var service = CreateService(CreateContext(workspace.Path, favoriteTrackCount: 501));

        PlaylistExportPlan plan = await service.PreviewAsync(
            new(Path.Combine(workspace.Path, "library.xml")),
            ct: TestContext.Current.CancellationToken);

        OperationIssue issue = Assert.Single(plan.Issues,
            issue => issue.Code == "playlist-track-limit");
        Assert.Equal(OperationIssueSeverity.Warning, issue.Severity);
        Assert.Contains("Favorites", issue.Message);
        Assert.Contains("501", issue.Message);
        Assert.DoesNotContain(Assert.Single(plan.Targets).Files,
            file => file.PlaylistName == "Favorites");
        Assert.True(plan.CanApply);
    }

    [Fact]
    public async Task ConfiguredM3uSourceExportsWithoutLoadingItunesAndUsesTargetOptions()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(
            Path.Combine(workspace.Path, "source")).FullName;
        string mediaRoot = Directory.CreateDirectory(
            Path.Combine(workspace.Path, "portable")).FullName;
        string inputTrack = Path.Combine(sourceRoot, "track.flac");
        string outputTrack = Path.Combine(mediaRoot, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), inputTrack);
        File.Copy(MediaFixtures.Path_("sample.flac"), outputTrack);
        string sourcePlaylist = Path.Combine(workspace.Path, "Favorites.m3u8");
        await File.WriteAllTextAsync(sourcePlaylist,
            "#EXTM3U\n#EXTINF:123,Source display\n" + inputTrack + "\n",
            new UTF8Encoding(false), TestContext.Current.CancellationToken);
        string playlistOutput = Path.Combine(workspace.Path, "playlists");
        string configPath = Path.Combine(workspace.Path, "library.xml");
        new XDocument(new XElement("LibraryConfiguration",
            new XElement("DatabaseFile", Path.Combine(workspace.Path, "cache.db")),
            new XElement("IndexTarget", new XAttribute("Path", sourceRoot),
                new XElement("Set", new XAttribute("Name", "Source"))),
            new XElement("IndexTarget", new XAttribute("Path", mediaRoot),
                new XAttribute("Offset", "media"),
                new XElement("Set", new XAttribute("Name", "Portable"))),
            new XElement("PlaylistSource", new XAttribute("Type", "m3u"),
                sourcePlaylist),
            new XElement("PlaylistTarget", new XAttribute("Type", "m3u8"),
                new XAttribute("Set", "Portable"),
                new XAttribute("PathStyle", "provided"),
                new XAttribute("Bom", false),
                new XAttribute("LineEnding", "lf"),
                new XAttribute("ExtInf", false),
                new XAttribute("FileNameTransform", "preserve"),
                new XAttribute("MaxTracks", 10),
                playlistOutput))).Save(configPath);
        var configuration = new LibraryConfiguration(configPath);
        var cache = new MetadataCache();
        cache.FileCache[inputTrack] = CreateCacheEntry(inputTrack);
        cache.FileCache[outputTrack] = CreateCacheEntry(outputTrack);
        var indexed = new IndexedLibraryOperationContext(configuration,
            configuration.IndexLocations.ToArray(), cache);
        var factory = new IndexedOnlyContextFactory(indexed);
        var service = new PlaylistExportService(factory, new FileInventoryService(),
            new FileMutationPlanExecutor(new FileMutationCoordinator()));

        PlaylistExportPlan plan = await service.PreviewAsync(new(configPath),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        Assert.Equal(0, factory.ItunesLoadCount);
        PlaylistExportFile file = Assert.Single(Assert.Single(plan.Targets).Files);
        Assert.EndsWith("Favorites.m3u8", file.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
        FileMutationAction write = Assert.Single(plan.MutationPlan.Actions,
            action => action.DestinationPath == file.DestinationPath);
        Assert.Equal("media/track.flac\n",
            Encoding.UTF8.GetString(write.Content.AsSpan()).Replace('\\', '/'));
    }

    [Fact]
    public async Task SourcePlaylistNameCollisionsCanBeSuffixed()
    {
        using var workspace = new TempDirectory();
        string mediaRoot = Directory.CreateDirectory(
            Path.Combine(workspace.Path, "media")).FullName;
        string track = Path.Combine(mediaRoot, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), track);
        string sources = Directory.CreateDirectory(
            Path.Combine(workspace.Path, "sources")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(sources, "nested")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sources, "Favorites.m3u"), track,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(nested, "Favorites.m3u8"), track,
            TestContext.Current.CancellationToken);
        string configPath = Path.Combine(workspace.Path, "library.xml");
        new XDocument(new XElement("LibraryConfiguration",
            new XElement("DatabaseFile", Path.Combine(workspace.Path, "cache.db")),
            new XElement("IndexTarget", new XAttribute("Path", mediaRoot),
                new XElement("Set", new XAttribute("Name", "Portable"))),
            new XElement("PlaylistSource", new XAttribute("Type", "m3u"),
                new XAttribute("Recursive", true), sources),
            new XElement("PlaylistTarget", new XAttribute("Type", "m3u"),
                new XAttribute("Set", "Portable"),
                new XAttribute("Collision", "Suffix"),
                Path.Combine(workspace.Path, "playlists")))).Save(configPath);
        var configuration = new LibraryConfiguration(configPath);
        var cache = new MetadataCache();
        cache.FileCache[track] = CreateCacheEntry(track);
        var indexed = new IndexedLibraryOperationContext(configuration,
            configuration.IndexLocations.ToArray(), cache);
        var service = new PlaylistExportService(new IndexedOnlyContextFactory(indexed),
            new FileInventoryService(),
            new FileMutationPlanExecutor(new FileMutationCoordinator()));

        PlaylistExportPlan plan = await service.PreviewAsync(new(configPath),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        string[] names = Assert.Single(plan.Targets).Files
            .Select(file => Path.GetFileName(file.DestinationPath)).Order().ToArray();
        Assert.Equal(["Favorites (2).m3u", "Favorites.m3u"], names);
    }

    private static PlaylistExportService CreateService(LibraryOperationContext context) =>
        new(new StubContextFactory(context), new FileInventoryService(),
            new FileMutationPlanExecutor(new FileMutationCoordinator()));

    private static MetadataCacheEntry CreateCacheEntry(string path)
    {
        var entry = new MetadataCacheEntry(MediaFile.GetFile(path, readOnly: true),
            File.GetLastWriteTimeUtc(path));
        entry.Strip();
        return entry;
    }

    private static LibraryOperationContext CreateContext(string workspace,
        bool overrideOffset = false, bool conflictingOffsets = false, bool clean = false,
        string playlistType = "m3u", int favoriteTrackCount = 1)
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
            new XElement("PlaylistTarget", new XAttribute("Type", playlistType),
                new XAttribute("Set", conflictingOffsets ? "Primary,Car2" : "Primary"), targetRoot),
            new XElement("CrossSyncPlaylistsSettings", new XAttribute("Clean", clean)),
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
        var playlist = new ItlPlaylist
        {
            Name = "Favorites",
            TrackIds = Enumerable.Repeat(1, favoriteTrackCount).ToList(),
        };
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

    private static byte[] RenderLegacyM3u(int duration, string path)
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.WriteLine("#EXTM3U");
        writer.WriteLine($"#EXTINF:{duration},TestArtist - TestTitle");
        writer.WriteLine(path);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] RenderLegacyWpl(string name, string path)
    {
        var document = new XDocument(
            new XProcessingInstruction("wpl", "version=\"1.0\""),
            new XElement("smil",
                new XElement("head",
                    new XElement("meta", new XAttribute("name", "Generator"),
                        new XAttribute("content", "CrossSyncPlaylists")),
                    new XElement("meta", new XAttribute("name", "ItemCount"),
                        new XAttribute("content", "1")),
                    new XElement("title", name)),
                new XElement("body", new XElement("seq",
                    new XElement("media", new XAttribute("src", path))))));
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            CloseOutput = false,
        };
        using (XmlWriter writer = XmlWriter.Create(stream, settings))
            document.Save(writer);
        return stream.ToArray();
    }

    private sealed class StubContextFactory(LibraryOperationContext context)
        : ILibraryOperationContextFactory
    {
        public Task<LibraryOperationContext> CreateAsync(string? configurationPath,
            string? itunesLibraryPath = null, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(context);
    }

    private sealed class IndexedOnlyContextFactory(IndexedLibraryOperationContext context)
        : ILibraryOperationContextFactory
    {
        public int ItunesLoadCount { get; private set; }

        public Task<IndexedLibraryOperationContext> CreateIndexedAsync(
            string? configurationPath,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(context);

        public Task<LibraryOperationContext> CreateAsync(string? configurationPath,
            string? itunesLibraryPath = null, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ItunesLoadCount++;
            throw new InvalidOperationException("iTunes should not be loaded for file playlists.");
        }
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
