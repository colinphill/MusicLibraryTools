using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class AnalysisRepairServiceTests
{
    [Fact]
    public void PreviewMissingAlbumArtists_UsesExistingAlbumArtistForMissingPeers()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01.flac"), "Album", "Guest", "Canonical Artist"),
            Track(Path.Combine(folder, "02.flac"), "Album", "Different Guest", null),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewMissingAlbumArtists(records);

        var repair = Assert.Single(plan.Items);
        Assert.Equal(records[1].Path, repair.Path);
        Assert.Equal(TagFields.AlbumArtist, repair.Field);
        Assert.Null(repair.Before);
        Assert.Equal("Canonical Artist", repair.After);
        Assert.Contains("already used", repair.Reason);
    }

    [Fact]
    public void PreviewMissingAlbumArtists_UsesSharedArtistButSkipsAmbiguousCompilations()
    {
        string single = Path.Combine("library", "Solo", "Album");
        string compilation = Path.Combine("library", "Various", "Album");
        var records = new[]
        {
            Track(Path.Combine(single, "01.flac"), "Album", "Solo Artist", null),
            Track(Path.Combine(single, "02.flac"), "Album", "Solo Artist", null),
            Track(Path.Combine(compilation, "01.flac"), "Album", "First Artist", null),
            Track(Path.Combine(compilation, "02.flac"), "Album", "Second Artist", null),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewMissingAlbumArtists(records);

        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, repair => Assert.Equal("Solo Artist", repair.After));
        Assert.DoesNotContain(plan.Items, repair => repair.Path.StartsWith(compilation, StringComparison.Ordinal));
    }

    [Fact]
    public void PreviewMissingAlbumArtists_SkipsConflictingExistingAlbumArtists()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01.flac"), "Album", "Artist", "First"),
            Track(Path.Combine(folder, "02.flac"), "Album", "Artist", "Second"),
            Track(Path.Combine(folder, "03.flac"), "Album", "Artist", null),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewMissingAlbumArtists(records);

        Assert.Empty(plan.Items);
    }

    [Fact]
    public async Task Apply_RejectsAnyChangedSourceBeforeWriting()
    {
        using var temp = new TempDirectory();
        string path = temp.File("track.flac", "original");
        var info = new FileInfo(path);
        var plan = Plan(new AnalysisTagRepair(
            path, TagFields.AlbumArtist, null, "Artist", "reason", info.Length, info.LastWriteTimeUtc));
        File.AppendAllText(path, "changed");
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AnalysisRepairService(writer).ApplyAsync(plan));

        Assert.Contains("Source changed since the repair preview", error.Message);
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task Apply_GroupsIdenticalEditsAndReportsResults()
    {
        using var temp = new TempDirectory();
        string first = temp.File("one.flac", "one");
        string second = temp.File("two.flac", "two");
        string third = temp.File("three.flac", "three");
        var plan = Plan(
            Repair(first, "Artist A"),
            Repair(second, "Artist A"),
            Repair(third, "Artist B"));
        var writer = new RecordingWriter();
        int progress = 0;

        var result = await new AnalysisRepairService(writer).ApplyAsync(
            plan, new InlineProgress(value => progress = value));

        Assert.Equal(3, result.SavedCount);
        Assert.Equal(2, writer.Calls.Count);
        Assert.Equal([first, second], writer.Calls[0].Paths);
        Assert.Equal("Artist A", Assert.Single(writer.Calls[0].Edits).Value);
        Assert.Equal("Artist B", Assert.Single(writer.Calls[1].Edits).Value);
        Assert.Equal(3, progress);

        AnalysisTagRepair Repair(string path, string value)
        {
            var info = new FileInfo(path);
            return new AnalysisTagRepair(
                path, TagFields.AlbumArtist, null, value, "reason", info.Length, info.LastWriteTimeUtc);
        }
    }

    [Fact]
    public async Task PreviewAndApply_RepairsARealIndexedFileAndRefreshesTheCache()
    {
        using var temp = new TempDirectory();
        string music = System.IO.Path.Combine(temp.Path, "music");
        Directory.CreateDirectory(music);
        string path = System.IO.Path.Combine(music, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), path);
        Assert.False(MediaFile.GetFile(path).Tags.First().HasAlbumArtist);
        string configPath = System.IO.Path.Combine(temp.Path, "library.xml");
        new EditableLibraryConfig
        {
            DatabaseFile = "cache.db",
            IndexTargets = [new IndexTargetEntry { Target = music }],
        }.Save(configPath);
        var settings = new AppSettings(System.IO.Path.Combine(temp.Path, "settings.json"));
        settings.LoadConfig(configPath);
        using var library = new LibraryService(settings);
        await library.IndexAsync();
        var service = new AnalysisRepairService(new TagWriteService(library));

        var plan = service.PreviewMissingAlbumArtists(await library.GetAllRecordsAsync());
        var result = await service.ApplyAsync(plan);

        Assert.Equal(1, result.SavedCount);
        Assert.True(MediaFile.GetFile(path).Tags.First().HasAlbumArtist);
        Assert.Equal("TestArtist", MediaFile.GetFile(path).Tags.First().AlbumArtist);
        Assert.Equal("TestArtist", (await library.GetFileDetailsAsync(path, includeArtwork: false))!.Entry.AlbumArtist);
    }

    private static TrackRecord Track(
        string path, string album, string artist, string? albumArtist) => new()
    {
        Path = path,
        Album = album,
        Artist = artist,
        AlbumArtist = albumArtist,
        HasAlbumArtist = albumArtist is not null,
        Length = 10,
        LastWriteTime = DateTime.UtcNow,
    };

    private static AnalysisRepairPlan Plan(params AnalysisTagRepair[] repairs) =>
        new("Test repairs", repairs);

    private sealed class RecordingWriter : ITagWriteService
    {
        public List<(IReadOnlyList<string> Paths, IReadOnlyList<TagEdit> Edits)> Calls { get; } = [];

        public Task<BatchWriteResult> ApplyAsync(
            IReadOnlyList<string> paths,
            IReadOnlyList<TagEdit> edits,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            Calls.Add((paths, edits));
            for (int index = 1; index <= paths.Count; index++)
                progress?.Report(index);
            return Task.FromResult(new BatchWriteResult(paths.Select(path => new FileWriteResult
            {
                Path = path,
                Outcome = WriteOutcome.Saved,
            }).ToList()));
        }
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "analysis-repair-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public string File(string name, string contents)
        {
            string path = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
