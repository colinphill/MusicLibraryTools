using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class LibraryServiceRegressionTests
{
    [Fact]
    public async Task LoadingNewConfig_DoesNotBlockActiveIndex_AndQueuedIndexUsesNewRoots()
    {
        using var temp = new TempDirectory();
        var rootA = temp.CreateDirectory("a");
        var rootB = temp.CreateDirectory("b");
        var songA = temp.CopyFixture(rootA, "a.flac");
        var songB = temp.CopyFixture(rootB, "b.flac");
        await new TagWriteService().ApplyAsync(
            [songB], [new TagEdit(TagFields.Title, "Config B Track")]);

        var configA = temp.WriteConfig("a.xml", "a.db", new IndexTargetEntry { Target = rootA });
        var configB = temp.WriteConfig("b.xml", "b.db", new IndexTargetEntry { Target = rootB });
        var settings = new AppSettings(temp.File("settings.json"));
        settings.LoadConfig(configA);
        using var library = new LibraryService(settings);
        var blocker = new BlockingProgress();
        var firstIndex = library.IndexAsync(blocker);
        Task? load = null;

        try
        {
            Assert.True(blocker.Entered.Wait(TimeSpan.FromSeconds(5)), "first index never reached final progress");
            load = Task.Run(() => settings.LoadConfig(configB));
            Assert.Same(load, await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(2))));
            await load;
        }
        finally
        {
            blocker.Release.Set();
            if (load is not null)
                await load;
        }

        await firstIndex;
        await library.IndexAsync();
        var records = await library.GetAllRecordsAsync();

        var record = Assert.Single(records);
        Assert.Equal(songB, record.Path);
        Assert.Equal("Config B Track", record.Title);
        Assert.DoesNotContain(records, r => r.Path == songA);
    }

    [Fact]
    public async Task Index_CancellationReportedByProgress_PropagatesAfterSafePartialCommit()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("music");
        temp.CopyFixture(root, "song.flac");
        var config = temp.WriteConfig("library.xml", "cache.db", new IndexTargetEntry { Target = root });
        var settings = new AppSettings(temp.File("settings.json"));
        settings.LoadConfig(config);
        using var library = new LibraryService(settings);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            library.IndexAsync(new CancelOnReport(cts), cts.Token));
    }

    [Fact]
    public async Task IndexUsesThePersistedBoundedReaderParallelism()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("music");
        temp.CopyFixture(root, "song.flac");
        var config = temp.WriteConfig("library.xml", "cache.db", new IndexTargetEntry { Target = root });
        var settings = new AppSettings(temp.File("settings.json"));
        settings.SetPreference(IndexBenchmarkService.ReaderParallelismPreference, "3");
        settings.LoadConfig(config);
        using var library = new LibraryService(settings);
        var reports = new CollectingProgress();

        await library.IndexAsync(reports);

        Assert.NotEmpty(reports.Values);
        Assert.All(reports.Values, report => Assert.Equal(3, report.ReaderParallelism));
    }

    [Fact]
    public async Task OfflineRootDoesNotAbortHealthyRootOrRemoveItsCachedFilesAndRetainsLastSuccess()
    {
        using var temp = new TempDirectory();
        var healthyRoot = temp.CreateDirectory("healthy");
        var offlineRoot = temp.CreateDirectory("offline");
        var first = temp.CopyFixture(healthyRoot, "first.flac");
        var cachedOffline = temp.CopyFixture(offlineRoot, "offline.flac");
        var config = temp.WriteConfig("library.xml", "cache.db",
            new IndexTargetEntry { Target = healthyRoot },
            new IndexTargetEntry { Target = offlineRoot });
        var settings = new AppSettings(temp.File("settings.json"));
        settings.LoadConfig(config);
        using var library = new LibraryService(settings);
        await library.IndexAsync();
        var firstHealth = await library.GetScanRootHealthAsync();
        Assert.All(firstHealth, root =>
        {
            Assert.Equal(ScanRootState.Healthy, root.State);
            Assert.NotNull(root.LastSuccessUtc);
        });
        DateTime offlineSuccess = Assert.Single(firstHealth,
            root => root.Root == offlineRoot).LastSuccessUtc!.Value;

        Directory.Move(offlineRoot, offlineRoot + "-disconnected");
        var second = temp.CopyFixture(healthyRoot, "second.flac");

        var result = await library.IndexAsync();
        var records = await library.GetAllRecordsAsync();
        var health = await library.GetScanRootHealthAsync();

        Assert.Equal(1, result.Added);
        Assert.Contains(records, record => record.Path == first);
        Assert.Contains(records, record => record.Path == second);
        Assert.Contains(records, record => record.Path == cachedOffline);
        var healthy = Assert.Single(health, root => root.Root == healthyRoot);
        Assert.Equal(ScanRootState.Healthy, healthy.State);
        Assert.Equal(2, healthy.Enumerated);
        var unavailable = Assert.Single(health, root => root.Root == offlineRoot);
        Assert.Equal(ScanRootState.Unavailable, unavailable.State);
        Assert.Equal(offlineSuccess, unavailable.LastSuccessUtc);
        Assert.NotNull(unavailable.Error);
    }

    [Fact]
    public async Task RelativeSqlitePrefix_ResolvesBesideConfig()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("music");
        temp.CopyFixture(root, "song.flac");
        var config = temp.WriteConfig("library.xml", "sqlite:cache.db", new IndexTargetEntry { Target = root });
        var settings = new AppSettings(temp.File("settings.json"));
        settings.LoadConfig(config);
        using var library = new LibraryService(settings);

        await library.IndexAsync();

        Assert.True(File.Exists(temp.File("cache.db")));
        Assert.NotEmpty(await library.GetAllRecordsAsync());
    }

    [Fact]
    public async Task ApplyMoves_SeparatelyReportsCacheRefreshFailureAfterSuccessfulMove()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("music");
        var source = temp.CopyFixture(root, "song.flac");
        var destination = System.IO.Path.Combine(root, "moved.txt");
        var config = temp.WriteConfig("library.xml", "cache.db", new IndexTargetEntry { Target = root });
        var settings = new AppSettings(temp.File("settings.json"));
        settings.LoadConfig(config);
        using var library = new LibraryService(settings);
        await library.IndexAsync();

        var result = await library.ApplyMovesAsync([new PlannedMove(source, destination)]);

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.MoveFailedCount);
        Assert.Equal(1, result.CacheFailedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task CheckSets_RejectsSoleCandidateWithDifferentTitle()
    {
        using var temp = new TempDirectory();
        var (library, first, second) = await CreateTwoSetLibrary(temp);
        using (library)
        {
            await new TagWriteService(library).ApplyAsync(
                [second], [new TagEdit(TagFields.Title, "Different recording")]);

            var report = await library.CheckSetsAsync();

            Assert.Contains(report.Findings, f => f.Path == first);
            Assert.Contains(report.Findings, f => f.Path == second);
        }
    }

    [Fact]
    public async Task CheckSets_RequiresMatchingDiscNumber()
    {
        using var temp = new TempDirectory();
        var (library, first, second) = await CreateTwoSetLibrary(temp);
        using (library)
        {
            var writer = new TagWriteService(library);
            await writer.ApplyAsync([first], [new TagEdit(TagFields.DiscNumber, "1")]);
            await writer.ApplyAsync([second], [new TagEdit(TagFields.DiscNumber, "2")]);

            var report = await library.CheckSetsAsync();

            Assert.Contains(report.Findings, f => f.Path == first);
            Assert.Contains(report.Findings, f => f.Path == second);
        }
    }

    [Fact]
    public async Task CheckSets_NormalizesWildcardWhitespaceAndExtensionCase()
    {
        using var temp = new TempDirectory();
        var firstRoot = temp.CreateDirectory("set1");
        var secondRoot = temp.CreateDirectory("set2");
        temp.CopyFixture(firstRoot, "shared.flac");
        temp.CopyFixture(secondRoot, "shared.flac");
        var extra = temp.CopyFixture(firstRoot, "extra.flac");
        await new TagWriteService().ApplyAsync(
            [extra],
            [new TagEdit(TagFields.Title, "Extra"), new TagEdit(TagFields.TrackNumber, "4")]);

        var config = temp.WriteConfig("sets.xml", "sets.db",
            new IndexTargetEntry { Target = firstRoot, Set = 1, Filter = " *.FLAC " },
            new IndexTargetEntry { Target = secondRoot, Set = 2, Filter = "*.flac" });
        var settings = new AppSettings(temp.File("settings.json"));
        settings.LoadConfig(config);
        using var library = new LibraryService(settings);
        await library.IndexAsync();

        var report = await library.CheckSetsAsync();

        var finding = Assert.Single(report.Findings);
        Assert.Equal(extra, finding.Path);
        Assert.Contains("set 2", finding.Description);
    }

    private static async Task<(LibraryService Library, string First, string Second)> CreateTwoSetLibrary(
        TempDirectory temp)
    {
        var firstRoot = temp.CreateDirectory("set1");
        var secondRoot = temp.CreateDirectory("set2");
        var first = temp.CopyFixture(firstRoot, "song.flac");
        var second = temp.CopyFixture(secondRoot, "song.flac");
        var config = temp.WriteConfig("sets.xml", "sets.db",
            new IndexTargetEntry { Target = firstRoot, Set = 1 },
            new IndexTargetEntry { Target = secondRoot, Set = 2 });
        var settings = new AppSettings(temp.File("settings.json"));
        settings.LoadConfig(config);
        var library = new LibraryService(settings);
        await library.IndexAsync();
        return (library, first, second);
    }

    private sealed class CancelOnReport(CancellationTokenSource cts) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => cts.Cancel();
    }

    private sealed class BlockingProgress : IProgress<IndexProgress>
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public void Report(IndexProgress value)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class CollectingProgress : IProgress<IndexProgress>
    {
        private readonly object sync = new();
        private readonly List<IndexProgress> values = [];
        public IReadOnlyList<IndexProgress> Values
        {
            get { lock (sync) return values.ToList(); }
        }
        public void Report(IndexProgress value)
        {
            lock (sync) values.Add(value);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mlcore_" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public string CreateDirectory(string name)
        {
            var path = File(name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CopyFixture(string directory, string name)
        {
            var path = System.IO.Path.Combine(directory, name);
            System.IO.File.Copy(MediaFixtures.Path_("sample.flac"), path);
            return path;
        }

        public string WriteConfig(string name, string database, params IndexTargetEntry[] targets)
        {
            var path = File(name);
            new EditableLibraryConfig
            {
                DatabaseFile = database,
                IndexTargets = [.. targets],
            }.Save(path);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
