using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class OperationJournalServiceTests
{
    [Fact]
    public async Task DiscoveryFindsSiblingIngestRunsAndFlagsAnUnfinishedGroup()
    {
        using var temp = new TempDirectory();
        string source = temp.Directory("incoming");
        string run = temp.Directory("incoming.IngestMusic-quarantine", "20260715-120000000");
        File.WriteAllLines(Path.Combine(run, "journal.tsv"),
        [
            "BEGIN\talbum-one",
            $"QUARANTINE\talbum-one\t{Path.Combine(source, "one.flac")}\t{Path.Combine(run, "one.flac")}",
            "COMMIT\talbum-one",
            "BEGIN\talbum-two",
        ]);

        var result = await new OperationJournalService().DiscoverAsync([source]);

        var summary = Assert.Single(result.Runs);
        Assert.Equal("IngestMusic", summary.ToolName);
        Assert.Equal(OperationJournalKind.Ingest, summary.Kind);
        Assert.Equal(OperationJournalState.Interrupted, summary.State);
        Assert.Equal(1, summary.AffectedItemCount);
        Assert.Equal(Path.Combine(run, "journal.tsv"), summary.JournalPath);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task DiscoveryClassifiesJournalLessOrganizeAndSyncQuarantines()
    {
        using var temp = new TempDirectory();
        string incoming = temp.Directory("incoming");
        string mirror = temp.Directory("mirror");
        temp.Directory("incoming.SortDownloads-quarantine", "20260715-120000000");
        temp.Directory("mirror.CrossSyncMusic-quarantine", "20260715-130000000");

        var result = await new OperationJournalService().DiscoverAsync([incoming, mirror]);

        Assert.Equal(2, result.Runs.Count);
        Assert.Contains(result.Runs, run =>
            run.ToolName == "SortDownloads" && run.Kind == OperationJournalKind.Organize &&
            run.State == OperationJournalState.Unknown && run.JournalPath is null);
        Assert.Contains(result.Runs, run =>
            run.ToolName == "CrossSyncMusic" && run.Kind == OperationJournalKind.Sync &&
            run.State == OperationJournalState.Unknown);
    }

    [Theory]
    [InlineData("COMMIT", OperationJournalState.Completed)]
    [InlineData("ROLLED_BACK", OperationJournalState.RolledBack)]
    [InlineData(null, OperationJournalState.Interrupted)]
    public async Task DiscoveryReadsUpdateCarCardTerminalState(
        string? terminal,
        OperationJournalState expected)
    {
        using var temp = new TempDirectory();
        string device = temp.Directory("card");
        string run = temp.Directory("card.UpdateCarCard-recovery", "20260715-140000000");
        var lines = new List<string> { "MOVE\tZmlyc3Q=\tc2Vjb25k", "CREATE\tdGhpcmQ=\t" };
        if (terminal is not null) lines.Add(terminal);
        File.WriteAllLines(Path.Combine(run, "journal.tsv"), lines);

        var result = await new OperationJournalService().DiscoverAsync([device]);

        var summary = Assert.Single(result.Runs);
        Assert.Equal(OperationJournalKind.Device, summary.Kind);
        Assert.Equal(expected, summary.State);
        Assert.Equal(2, summary.AffectedItemCount);
    }

    [Fact]
    public async Task BrowseIngestMapsJournalAndPhysicalFoldersToTheOriginalHierarchy()
    {
        using var temp = new TempDirectory();
        string source = temp.Directory("incoming");
        string run = temp.Directory("incoming.IngestMusic-quarantine", "20260715-150000000");
        string albumDirectory = Path.Combine(run, "Artist", "Album");
        Directory.CreateDirectory(albumDirectory);
        string quarantined = Path.Combine(albumDirectory, "one.flac");
        File.WriteAllText(quarantined, "audio");
        string original = Path.Combine(source, "Artist", "Album", "one.flac");
        string stagedDelete = Path.Combine(albumDirectory, "two.flac");
        File.WriteAllText(stagedDelete, "audio");
        string stagedOriginal = Path.Combine(source, "Artist", "Album", "two.flac");
        string journal = Path.Combine(run, "journal.tsv");
        File.WriteAllLines(journal,
        [
            "BEGIN\talbum",
            $"QUARANTINE\talbum\t{original}\t{quarantined}",
            $"PLAN_DELETE\talbum\t{stagedOriginal}",
            "COMMIT\talbum",
        ]);
        var service = new OperationJournalService();
        var summary = Assert.Single((await service.DiscoverAsync([source])).Runs);

        var browse = await service.BrowseAsync(summary);

        Assert.Equal(source, browse.OriginalRoot);
        var file = Assert.Single(browse.Entries, entry => entry.OriginalPath == original);
        Assert.Equal(original, file.OriginalPath);
        Assert.Equal(Path.Combine("Artist", "Album", "one.flac"), file.RelativePath);
        Assert.Equal(OperationEntryKind.Quarantined, file.Kind);
        Assert.True(file.Exists);
        var recoveredPlan = Assert.Single(browse.Entries, entry => entry.OriginalPath == stagedOriginal);
        Assert.Equal(stagedDelete, recoveredPlan.CurrentPath);
        Assert.Equal(OperationEntryKind.Quarantined, recoveredPlan.Kind);
        Assert.Contains(browse.Entries, entry => entry.IsDirectory && entry.RelativePath == "Artist");
        Assert.Contains(browse.Entries, entry => entry.IsDirectory &&
            entry.RelativePath == Path.Combine("Artist", "Album"));
    }

    [Fact]
    public async Task BrowseFolderOnlySyncQuarantineReconstructsOriginalPaths()
    {
        using var temp = new TempDirectory();
        string target = temp.Directory("mirror");
        string run = temp.Directory("mirror.CrossSyncMusic-quarantine", "20260715-160000000");
        string current = Path.Combine(run, "Artist", "song.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "audio");
        var service = new OperationJournalService();
        var summary = Assert.Single((await service.DiscoverAsync([target])).Runs);

        var browse = await service.BrowseAsync(summary);

        var file = Assert.Single(browse.Entries, entry => !entry.IsDirectory);
        Assert.Equal(Path.Combine(target, "Artist", "song.flac"), file.OriginalPath);
        Assert.Equal(current, file.CurrentPath);
        Assert.Equal(OperationEntryKind.Quarantined, file.Kind);
    }

    [Fact]
    public async Task BrowseOrganizeJournalShowsSourceToDestinationMove()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("music");
        string run = temp.Directory("music.OrganizeFiles-recovery", "20260715-170000000");
        string source = Path.Combine(root, "old.flac");
        string destination = Path.Combine(root, "Artist", "Album", "01 Song.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "audio");
        File.WriteAllLines(Path.Combine(run, "journal.tsv"),
        [
            "BEGIN\tORGANIZE",
            $"PLAN_MOVE\tORGANIZE\t{source}\t{destination}",
            $"MOVE\tORGANIZE\t{source}\t{destination}",
            "COMMIT\tORGANIZE",
        ]);
        var service = new OperationJournalService();
        var summary = Assert.Single((await service.DiscoverAsync([root])).Runs);

        var entry = Assert.Single((await service.BrowseAsync(summary)).Entries);

        Assert.Equal(source, entry.OriginalPath);
        Assert.Equal(destination, entry.CurrentPath);
        Assert.Equal(OperationEntryKind.Moved, entry.Kind);
        Assert.True(entry.Exists);
    }

    [Fact]
    public async Task BrowseDeviceJournalPreservesBackupMoveWhenAReplacementWasCreated()
    {
        using var temp = new TempDirectory();
        string device = temp.Directory("card");
        string run = temp.Directory("card.UpdateCarCard-recovery", "20260715-180000000");
        string original = Path.Combine(device, "syncdb.xml");
        string backup = Path.Combine(run, "data", "backup-syncdb.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(original, "new");
        File.WriteAllText(backup, "old");
        static string Encode(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        File.WriteAllLines(Path.Combine(run, "journal.tsv"),
        [
            $"MOVE\t{Encode(original)}\t{Encode(backup)}",
            $"CREATE\t{Encode(original)}\t",
            "COMMIT",
        ]);
        var service = new OperationJournalService();
        var summary = Assert.Single((await service.DiscoverAsync([device])).Runs);

        var entry = Assert.Single((await service.BrowseAsync(summary)).Entries);

        Assert.Equal(original, entry.OriginalPath);
        Assert.Equal(backup, entry.CurrentPath);
        Assert.Equal(OperationEntryKind.Moved, entry.Kind);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "operation-journal-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => System.IO.Directory.CreateDirectory(Path);

        public string Directory(params string[] parts)
        {
            string path = parts.Aggregate(Path, System.IO.Path.Combine);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
