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
    public void AlbumArtistConflicts_GroupMultiDiscPackagesWithoutChoosingAValue()
    {
        string album = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(album, "Disc 1", "01.flac"), "Album", "Guest", "First Artist"),
            Track(Path.Combine(album, "Disc 1", "02.flac"), "Album", "Guest", "Second Artist"),
            Track(Path.Combine(album, "Disc 2", "01.flac"), "Album", "Guest", "First Artist"),
            Track(Path.Combine(album, "Disc 2", "02.flac"), "Album", "Guest", null),
        };
        var service = new AnalysisRepairService(new RecordingWriter());

        var conflict = Assert.Single(service.FindAlbumArtistConflicts(records));

        Assert.Equal("Album", conflict.Album);
        Assert.Equal(album, conflict.Directory);
        Assert.Equal(TagFields.AlbumArtist, conflict.Field);
        Assert.Equal(4, conflict.Targets.Count);
        Assert.Collection(conflict.Options,
            option =>
            {
                Assert.Equal("First Artist", option.Value);
                Assert.Equal(2, option.FileCount);
            },
            option =>
            {
                Assert.Equal("Second Artist", option.Value);
                Assert.Equal(1, option.FileCount);
            });
    }

    [Fact]
    public void AlbumArtistConflicts_IgnoreCaseOnlyVariants()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01.flac"), "Album", "Artist", "Canonical Artist"),
            Track(Path.Combine(folder, "02.flac"), "Album", "Artist", "canonical artist"),
        };

        var conflicts = new AnalysisRepairService(new RecordingWriter())
            .FindAlbumArtistConflicts(records);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void ConflictRepairPreview_ChangesOnlyFilesThatDifferFromTheExplicitChoice()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01.flac"), "Album", "Guest", "First Artist"),
            Track(Path.Combine(folder, "02.flac"), "Album", "Guest", "Second Artist"),
            Track(Path.Combine(folder, "03.flac"), "Album", "Guest", null),
        };
        var service = new AnalysisRepairService(new RecordingWriter());
        var conflict = Assert.Single(service.FindAlbumArtistConflicts(records));

        var plan = service.PreviewConflictRepairs([
            new AnalysisConflictResolution(conflict, "Second Artist"),
        ]);

        Assert.Equal("Resolve album artist conflicts", plan.Name);
        Assert.Equal(2, plan.Items.Count);
        Assert.DoesNotContain(plan.Items, repair => repair.Path == records[1].Path);
        Assert.All(plan.Items, repair =>
        {
            Assert.Equal(TagFields.AlbumArtist, repair.Field);
            Assert.Equal("Second Artist", repair.After);
            Assert.Contains("User selected", repair.Reason);
        });
        Assert.Contains(plan.Items, repair => repair.Path == records[2].Path && repair.Before is null);
    }

    [Fact]
    public void ConflictRepairPreview_RejectsValuesNotPresentInTheConflict()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01.flac"), "Album", "Artist", "First"),
            Track(Path.Combine(folder, "02.flac"), "Album", "Artist", "Second"),
        };
        var service = new AnalysisRepairService(new RecordingWriter());
        var conflict = Assert.Single(service.FindAlbumArtistConflicts(records));

        Assert.Throws<ArgumentException>(() => service.PreviewConflictRepairs([
            new AnalysisConflictResolution(conflict, "Invented value"),
        ]));
    }

    [Fact]
    public async Task ConflictRepairPreview_PreservesSnapshotsForStaleCheckedApply()
    {
        using var temp = new TempDirectory();
        string first = temp.File("first.flac", "first");
        string second = temp.File("second.flac", "second");
        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);
        var records = new[]
        {
            Track(first, "Album", "Artist", "First") with
            {
                Length = firstInfo.Length,
                LastWriteTime = firstInfo.LastWriteTimeUtc,
            },
            Track(second, "Album", "Artist", "Second") with
            {
                Length = secondInfo.Length,
                LastWriteTime = secondInfo.LastWriteTimeUtc,
            },
        };
        var writer = new RecordingWriter();
        var service = new AnalysisRepairService(writer);
        var conflict = Assert.Single(service.FindAlbumArtistConflicts(records));
        var plan = service.PreviewConflictRepairs([
            new AnalysisConflictResolution(conflict, "Second"),
        ]);
        File.AppendAllText(first, " changed");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(plan));

        Assert.Contains("Source changed since the repair preview", error.Message);
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public void PreviewNumbering_UsesCalibratedFilenameAndPeerTotal()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01 - One.flac"), "Album", "Artist", "Artist", 1),
            Track(Path.Combine(folder, "02 - Two.flac"), "Album", "Artist", "Artist"),
            Track(Path.Combine(folder, "03 - Three.flac"), "Album", "Artist", "Artist", 3, 3),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewNumberingAndTotals(records);

        Assert.Contains(plan.Items, repair =>
            repair.Path == records[1].Path && repair.Field == TagFields.TrackNumber && repair.After == "2");
        Assert.Equal(2, plan.Items.Count(repair => repair.Field == TagFields.TotalTracks));
        Assert.All(plan.Items.Where(repair => repair.Field == TagFields.TotalTracks),
            repair => Assert.Equal("3", repair.After));
    }

    [Fact]
    public void PreviewNumbering_SkipsUncalibratedOrAmbiguousFilenames()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01 - One.flac"), "Album", "Artist", "Artist", 1),
            Track(Path.Combine(folder, "bonus.flac"), "Album", "Artist", "Artist"),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewNumberingAndTotals(records);

        Assert.Empty(plan.Items);
    }

    [Fact]
    public void PreviewTotals_RepairsOnlyValuesProvenInvalidByTheTrackSequence()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01.flac"), "Album", "Artist", "Artist", 1, 2),
            Track(Path.Combine(folder, "02.flac"), "Album", "Artist", "Artist", 2, 3),
            Track(Path.Combine(folder, "03.flac"), "Album", "Artist", "Artist", 3, 3),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewNumberingAndTotals(records);

        var repair = Assert.Single(plan.Items);
        Assert.Equal(records[0].Path, repair.Path);
        Assert.Equal(TagFields.TotalTracks, repair.Field);
        Assert.Equal("2", repair.Before);
        Assert.Equal("3", repair.After);
    }

    [Fact]
    public void PreviewTotals_SkipsConflictingPlausibleTotals()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01.flac"), "Album", "Artist", "Artist", 1, 2),
            Track(Path.Combine(folder, "02.flac"), "Album", "Artist", "Artist", 2, 3),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewNumberingAndTotals(records);

        Assert.Empty(plan.Items);
    }

    [Fact]
    public void PreviewDiscs_UsesACompleteSetOfExplicitDiscFolders()
    {
        string album = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(album, "Disc 1", "01.flac"), "Album", "Artist", "Artist", 1),
            Track(Path.Combine(album, "Disc 2", "01.flac"), "Album", "Artist", "Artist", 1),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewNumberingAndTotals(records);

        Assert.Equal(2, plan.Items.Count(repair => repair.Field == TagFields.DiscNumber));
        Assert.Equal(2, plan.Items.Count(repair => repair.Field == TagFields.TotalDiscs));
        Assert.Contains(plan.Items, repair =>
            repair.Path == records[0].Path && repair.Field == TagFields.DiscNumber && repair.After == "1");
        Assert.Contains(plan.Items, repair =>
            repair.Path == records[1].Path && repair.Field == TagFields.DiscNumber && repair.After == "2");
        Assert.All(plan.Items.Where(repair => repair.Field == TagFields.TotalDiscs),
            repair => Assert.Equal("2", repair.After));
    }

    [Fact]
    public void PreviewDiscs_SkipsIncompleteDiscFolderSequences()
    {
        string album = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(album, "Disc 1", "01.flac"), "Album", "Artist", "Artist", 1),
            Track(Path.Combine(album, "Disc 3", "01.flac"), "Album", "Artist", "Artist", 1),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewNumberingAndTotals(records);

        Assert.Empty(plan.Items);
    }

    [Fact]
    public void PreviewText_TrimsEdgesWithoutCreatingAnAbsentAlbumArtist()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var record = Track(Path.Combine(folder, "01.flac"), " Album ", " Artist ", null) with
        {
            AlbumArtist = " Artist ", // parser fallback; the explicit-presence flag remains false
            Title = " Title ",
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewTextNormalization([record]);

        Assert.Equal(3, plan.Items.Count);
        Assert.Contains(plan.Items, repair => repair.Field == TagFields.Artist && repair.After == "Artist");
        Assert.Contains(plan.Items, repair => repair.Field == TagFields.Album && repair.After == "Album");
        Assert.Contains(plan.Items, repair => repair.Field == TagFields.Title && repair.After == "Title");
        Assert.DoesNotContain(plan.Items, repair => repair.Field == TagFields.AlbumArtist);
    }

    [Fact]
    public void PreviewText_UsesStrictPeerMajorityForCaseAndWhitespaceVariants()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01.flac"), "Album", "The Beatles", "The Beatles"),
            Track(Path.Combine(folder, "02.flac"), "Album", "The Beatles", "The Beatles"),
            Track(Path.Combine(folder, "03.flac"), "Album", "the  beatles", "The Beatles"),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewTextNormalization(records);

        var repair = Assert.Single(plan.Items);
        Assert.Equal(records[2].Path, repair.Path);
        Assert.Equal(TagFields.Artist, repair.Field);
        Assert.Equal("the  beatles", repair.Before);
        Assert.Equal("The Beatles", repair.After);
    }

    [Fact]
    public void PreviewText_SkipsTiedCaseVariantsAndDirtyMajorities()
    {
        string tied = Path.Combine("library", "Tied");
        string dirty = Path.Combine("library", "Dirty");
        string plurality = Path.Combine("library", "Plurality");
        var records = new[]
        {
            Track(Path.Combine(tied, "01.flac"), "Album", "Artist", "Artist"),
            Track(Path.Combine(tied, "02.flac"), "Album", "artist", "Artist"),
            Track(Path.Combine(dirty, "01.flac"), "Album", "The  Beatles", "Artist"),
            Track(Path.Combine(dirty, "02.flac"), "Album", "The  Beatles", "Artist"),
            Track(Path.Combine(dirty, "03.flac"), "Album", "The Beatles", "Artist"),
            Track(Path.Combine(plurality, "01.flac"), "Album", "Beatles", "Artist"),
            Track(Path.Combine(plurality, "02.flac"), "Album", "Beatles", "Artist"),
            Track(Path.Combine(plurality, "03.flac"), "Album", "beatles", "Artist"),
            Track(Path.Combine(plurality, "04.flac"), "Album", "BEATLES", "Artist"),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewTextNormalization(records);

        Assert.Empty(plan.Items);
    }

    [Fact]
    public void PreviewSafeRepairs_CombinesIndependentRepairTypes()
    {
        string folder = Path.Combine("library", "Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01 - One.flac"), "Album", " Artist ", null, 1, 2),
            Track(Path.Combine(folder, "02 - Two.flac"), "Album", " Artist ", null),
        };

        var plan = new AnalysisRepairService(new RecordingWriter()).PreviewSafeRepairs(records);

        Assert.Contains(plan.Items, repair => repair.Field == TagFields.AlbumArtist);
        Assert.Contains(plan.Items, repair => repair.Field == TagFields.TrackNumber);
        Assert.Contains(plan.Items, repair => repair.Field == TagFields.TotalTracks);
        Assert.Contains(plan.Items, repair => repair.Field == TagFields.Artist && repair.After == "Artist");
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
    public async Task Apply_WritesAllSelectedFieldsForAFileInOnePass()
    {
        using var temp = new TempDirectory();
        string path = temp.File("track.flac", "content");
        var info = new FileInfo(path);
        var plan = Plan(
            new AnalysisTagRepair(path, TagFields.TrackNumber, null, "2", "reason",
                info.Length, info.LastWriteTimeUtc),
            new AnalysisTagRepair(path, TagFields.TotalTracks, null, "10", "reason",
                info.Length, info.LastWriteTimeUtc));
        var writer = new RecordingWriter();
        int progress = 0;

        var result = await new AnalysisRepairService(writer).ApplyAsync(
            plan, new InlineProgress(value => progress = value));

        Assert.Equal(1, result.SavedCount);
        var call = Assert.Single(writer.Calls);
        Assert.Equal(path, Assert.Single(call.Paths));
        Assert.Equal(2, call.Edits.Count);
        Assert.Contains(call.Edits, edit => edit == new TagEdit(TagFields.TrackNumber, "2"));
        Assert.Contains(call.Edits, edit => edit == new TagEdit(TagFields.TotalTracks, "10"));
        Assert.Equal(1, progress);
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

    [Fact]
    public async Task PreviewAndApply_WritesNumberAndTotalTogetherAndRefreshesTheCache()
    {
        using var temp = new TempDirectory();
        string music = System.IO.Path.Combine(temp.Path, "music");
        Directory.CreateDirectory(music);
        string first = System.IO.Path.Combine(music, "01 - First.flac");
        string second = System.IO.Path.Combine(music, "02 - Second.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), first);
        File.Copy(MediaFixtures.Path_("sample.flac"), second);
        var firstWriter = Assert.IsAssignableFrom<IMetadataWriter>(MediaFile.GetFile(first));
        firstWriter.SetField(TagFields.TrackNumber, "1");
        firstWriter.SetField(TagFields.TotalTracks, "2");
        firstWriter.Save();
        var secondWriter = Assert.IsAssignableFrom<IMetadataWriter>(MediaFile.GetFile(second));
        secondWriter.SetField(TagFields.TrackNumber, null!);
        secondWriter.SetField(TagFields.TotalTracks, null!);
        secondWriter.Save();
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

        var plan = service.PreviewNumberingAndTotals(await library.GetAllRecordsAsync());
        var result = await service.ApplyAsync(plan);

        Assert.Equal(1, result.SavedCount);
        var parsed = MediaFile.GetFile(second).Tags.First();
        Assert.Equal(2, parsed.TrackNumber);
        Assert.Equal(2, parsed.TrackTotal);
        var cached = (await library.GetFileDetailsAsync(second, includeArtwork: false))!.Entry;
        Assert.Equal(2, cached.TrackNumber);
        Assert.Equal(2, cached.TrackTotal);
    }

    [Fact]
    public async Task PreviewAndApply_NormalizesRealFileTextAndRefreshesTheCache()
    {
        using var temp = new TempDirectory();
        string music = System.IO.Path.Combine(temp.Path, "music");
        Directory.CreateDirectory(music);
        string path = System.IO.Path.Combine(music, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), path);
        var writer = Assert.IsAssignableFrom<IMetadataWriter>(MediaFile.GetFile(path));
        writer.SetField(TagFields.Artist, " TestArtist ");
        writer.Save();
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

        var plan = service.PreviewTextNormalization(await library.GetAllRecordsAsync());
        var result = await service.ApplyAsync(plan);

        Assert.Equal(1, result.SavedCount);
        Assert.Equal("TestArtist", MediaFile.GetFile(path).Tags.First().Artist);
        Assert.Equal("TestArtist",
            (await library.GetFileDetailsAsync(path, includeArtwork: false))!.Entry.Artist);
    }

    private static TrackRecord Track(
        string path, string album, string artist, string? albumArtist,
        int? trackNumber = null, int? trackTotal = null,
        int? discNumber = null, int? discTotal = null) => new()
    {
        Path = path,
        Album = album,
        Artist = artist,
        AlbumArtist = albumArtist,
        HasAlbumArtist = albumArtist is not null,
        TrackNumber = trackNumber,
        TrackTotal = trackTotal,
        DiscNumber = discNumber,
        DiscTotal = discTotal,
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
