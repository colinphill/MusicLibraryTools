using MetadataCaching;
using MusicLibrary.App.ViewModels;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class LibraryIndexProgressTests
{
    [Fact]
    public void StatusDescribesPhaseCountsThroughputAndBothTimers()
    {
        var progress = new IndexProgress(75, 50, 25, 900)
        {
            Phase = IndexPhase.Metadata,
            Enumerated = 1_000,
            DatabaseProcessed = 40,
            FilesPerSecond = 12.5,
            PhaseElapsed = TimeSpan.FromSeconds(6),
            Elapsed = TimeSpan.FromSeconds(9),
            Detail = "Reading changed and new file metadata",
        };

        string status = LibraryViewModel.DescribeProgress(progress);

        Assert.Contains("Reading metadata", status);
        Assert.Contains("75 read", status);
        Assert.Contains("12.5 files/s", status);
        Assert.Contains("stage 6.0s", status);
        Assert.Contains("total 9.0s", status);
    }

    [Fact]
    public void ArtworkStatusExplainsThatHydrationIsDeferred()
    {
        var progress = new IndexProgress(1, 1, 0, 0)
        {
            Phase = IndexPhase.Artwork,
            ArtworkDeferred = true,
            Detail = "Artwork is deferred and hydrates only when viewed or audited",
        };

        string status = LibraryViewModel.DescribeProgress(progress);

        Assert.Contains("Artwork", status);
        Assert.Contains("deferred", status);
        Assert.DoesNotContain("files/s", status);
    }

    [Fact]
    public void RootHealthStatusDistinguishesUnavailableFromLastSuccessfulScan()
    {
        string status = LibraryViewModel.DescribeRootHealth(
        [
            new ScanRootHealth("Z:\\Music", ScanRootState.Unavailable,
                new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc),
                0, 0, "The network path was not found"),
        ]);

        Assert.Contains("Unavailable", status);
        Assert.Contains("last success", status);
        Assert.Contains("network path", status);
    }
}
