using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using iTunes.Binary;
using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace DumpITL.Tests;

public sealed class ItlMetadataRepairServiceTests
{
    [Fact]
    public async Task PreviewAndApplyRepairCacheFieldsAndPreserveUncachedMetadata()
    {
        using var workspace = new TemporaryDirectory();
        string mediaPath = Path.Combine(workspace.Path, "Track.m4a");
        string libraryPath = Path.Combine(workspace.Path, "Library.itl");
        ItlDocument source = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlRecord sourceTrack = source.Tracks.Single();
        source.SetTrackString(sourceTrack, ItlDataType.Location, mediaPath);
        source.SetTrackString(sourceTrack, ItlDataType.Title, "Wrong title");
        source.SetTrackString(sourceTrack, ItlDataType.Artist, "Wrong artist");
        source.SetTrackString(sourceTrack, ItlDataType.AlbumArtist, "Wrong artist");
        source.SetTrackString(sourceTrack, ItlDataType.Album, "Wrong album");
        source.SetTrackString(sourceTrack, ItlDataType.Comment, "Preserve this comment");
        ItlFileEditor.SaveValidated(source, libraryPath);

        var service = new ItlMetadataRepairService(new StubContextFactory(
            CreateContext(workspace.Path, mediaPath, libraryPath)));
        ItlMetadataRepairPlan plan = await service.PreviewAsync(
            ct: TestContext.Current.CancellationToken);

        ItlMetadataRepairItem item = Assert.Single(plan.Items);
        Assert.Contains(item.Differences, value => value.Field == "Title" &&
            value.Before == "Wrong title" && value.After == "Cached title");
        Assert.Contains(item.Differences, value => value.Field == "Album link");

        ItlMetadataRepairApplyResult result = await service.ApplyAsync(
            plan, [item.Id], ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Applied);
        Assert.True(File.Exists(libraryPath + ".bak"));
        ItlRecord repaired = ItlDocument.Load(libraryPath).Tracks.Single();
        Assert.Equal("Cached title", repaired.GetString(ItlDataType.Title));
        Assert.Equal("Cached artist", repaired.GetString(ItlDataType.Artist));
        Assert.Equal("Cached album artist", repaired.GetString(ItlDataType.AlbumArtist));
        Assert.Equal("Cached album", repaired.GetString(ItlDataType.Album));
        Assert.Equal("Preserve this comment", repaired.GetString(ItlDataType.Comment));
        Assert.Equal(7, repaired.GetTrackNumber());
        Assert.Equal(12, repaired.GetTrackCount());
        Assert.Equal(2026, repaired.GetYear());
    }

    [Fact]
    public void CacheRepairRemovesItlAlbumArtistWhenFileHasNoExplicitTag()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlRecord track = document.Tracks.Single();
        document.SetTrackString(track, ItlDataType.AlbumArtist, "Existing album artist");

        document.RepairLocalTrackFromCache(track, new ItlCachedTrackMetadata
        {
            Artist = "Track artist",
            Album = "Album",
            AlbumArtist = null,
            HasExplicitAlbumArtist = false,
        }, DateTime.UtcNow);

        Assert.Null(track.GetString(ItlDataType.AlbumArtist));
        ItlRecord linkedArtist = document.Artists.Single(value =>
            ItlDocument.RecordIdOf(value) == track.GetArtistId());
        Assert.Equal("Track artist",
            linkedArtist.Field((int)ItlDataType.ArtistRecordName)?.Text);
    }

    [Fact]
    public async Task ApplyRejectsAnItlChangedAfterPreview()
    {
        using var workspace = new TemporaryDirectory();
        string mediaPath = Path.Combine(workspace.Path, "Track.m4a");
        string libraryPath = Path.Combine(workspace.Path, "Library.itl");
        ItlDocument source = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        source.SetTrackString(source.Tracks.Single(), ItlDataType.Location, mediaPath);
        ItlFileEditor.SaveValidated(source, libraryPath);
        var service = new ItlMetadataRepairService(new StubContextFactory(
            CreateContext(workspace.Path, mediaPath, libraryPath)));
        ItlMetadataRepairPlan plan = await service.PreviewAsync(
            ct: TestContext.Current.CancellationToken);

        byte[] changed = File.ReadAllBytes(libraryPath);
        changed[^1] ^= 1;
        File.WriteAllBytes(libraryPath, changed);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(plan, plan.Items.Select(item => item.Id).ToArray(),
                ct: TestContext.Current.CancellationToken));
        Assert.Contains("changed after this preview", error.Message);
    }

    [Fact]
    public async Task PreviewAndApplyRemoveAlbumArtistWhenFileHasNoExplicitTag()
    {
        using var workspace = new TemporaryDirectory();
        string mediaPath = Path.Combine(workspace.Path, "Track.m4a");
        string libraryPath = Path.Combine(workspace.Path, "Library.itl");
        ItlDocument source = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlRecord track = source.Tracks.Single();
        source.SetTrackString(track, ItlDataType.Location, mediaPath);
        source.SetTrackString(track, ItlDataType.AlbumArtist, "Existing album artist");
        source.SetTrackString(track, ItlDataType.Title, "Wrong title");
        ItlFileEditor.SaveValidated(source, libraryPath);
        var service = new ItlMetadataRepairService(new StubContextFactory(
            CreateContext(workspace.Path, mediaPath, libraryPath, explicitAlbumArtist: false)));

        ItlMetadataRepairPlan plan = await service.PreviewAsync(
            ct: TestContext.Current.CancellationToken);

        ItlMetadataRepairItem item = Assert.Single(plan.Items);
        Assert.Contains(item.Differences, value => value.Field == "Album artist" &&
            value.Before == "Existing album artist" && value.After is null);
        await service.ApplyAsync(plan, [item.Id], ct: TestContext.Current.CancellationToken);
        Assert.Null(ItlDocument.Load(libraryPath).Tracks.Single()
            .GetString(ItlDataType.AlbumArtist));
    }

    private static LibraryOperationContext CreateContext(
        string workspace,
        string mediaPath,
        string libraryPath,
        bool explicitAlbumArtist = true)
    {
        string configPath = Path.Combine(workspace, "library.xml");
        new XDocument(new XElement("LibraryConfiguration",
            new XElement("DatabaseFile", Path.Combine(workspace, "cache.db")),
            new XElement("ItunesLibrary", libraryPath))).Save(configPath);
        var configuration = new LibraryConfiguration(configPath);
        var cache = new MetadataCache(buildSecondaryIndexes: false);
        cache.FileCache[mediaPath] = CacheEntry(explicitAlbumArtist);
        ItlLibrary library = ItlLibrary.Load(libraryPath);
        return new(configuration, [], cache, library,
            library.Tracks.ToDictionary(track => track.Id), libraryPath);
    }

    private static MetadataCacheEntry CacheEntry(bool explicitAlbumArtist)
    {
        var entry = (MetadataCacheEntry)RuntimeHelpers.GetUninitializedObject(
            typeof(MetadataCacheEntry));
        Set("_title", "Cached title");
        Set("_artist", "Cached artist");
        Set("_albumartist", explicitAlbumArtist ? "Cached album artist" : "Cached artist");
        Set("_hasalbumartist", explicitAlbumArtist);
        Set("_album", "Cached album");
        Set("_tracknumber", (int?)7);
        Set("_tracktotal", (int?)12);
        Set("_discnumber", (int?)1);
        Set("_disctotal", (int?)1);
        Set("_releasedate", "2026-07-19");
        Set("_compilation", false);
        Set("_lastwritetime", new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc));
        return entry;

        void Set(string name, object? value) => typeof(MetadataCacheEntry)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(entry, value);
    }

    private sealed class StubContextFactory(LibraryOperationContext context)
        : ILibraryOperationContextFactory
    {
        public Task<LibraryOperationContext> CreateAsync(
            string? configurationPath,
            string? itunesLibraryPath = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(context);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"itl-cache-repair-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
