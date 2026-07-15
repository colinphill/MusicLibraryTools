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
