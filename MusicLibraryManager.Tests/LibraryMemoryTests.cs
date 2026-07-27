using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class LibraryMemoryTests
{
    private const int ProductionScaleTrackCount =
        111_302;
    private readonly ITestOutputHelper _output;

    public LibraryMemoryTests(
        ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public void CompactRowsStayWithinRetainedOverheadBudget()
    {
        var sharedRecord = new TrackRecord
        {
            Path = @"C:\Music\Artist\Album\01 Song.flac",
            Title = "Song",
            Artist = "Artist",
            AlbumArtist = "Artist",
            Album = "Album",
            Genre = "Genre",
            Composer = "Composer",
            Grouping = "Grouping",
            ReleaseDate = "2026",
            TrackNumber = 1,
            TrackTotal = 10,
            DiscNumber = 1,
            DiscTotal = 1,
            CodecName = "FLAC",
            TagType = "Vorbis comments",
            SampleRate = 96_000,
            BitsPerSample = 24,
            AverageBitRate = 2_400,
            Channels = 2,
            DurationInSeconds = 240,
            Length = 80_000_000,
            LastWriteTime = DateTime.UtcNow,
        };

        ForceCollection();
        long before =
            GC.GetTotalMemory(
                forceFullCollection: true);
        var rows = new LibraryRow[
            ProductionScaleTrackCount];
        for (int index = 0;
             index < rows.Length;
             index++)
            rows[index] =
                new LibraryRow(
                    sharedRecord);
        long after =
            GC.GetTotalMemory(
                forceFullCollection: true);

        double retainedPerRow =
            (after - before) /
            (double)rows.Length;
        _output.WriteLine(
            $"Compact retained row overhead: {retainedPerRow:N1} bytes per row.");
        Assert.True(
            retainedPerRow <= 1_536,
            $"Compact Library rows retained {retainedPerRow:N1} bytes per row.");
        Assert.All(
            rows.Take(32),
            row =>
            {
                Assert.Empty(
                    row.MetadataValues);
                Assert.Empty(
                    row.SearchText);
            });
        GC.KeepAlive(rows);
        GC.KeepAlive(sharedRecord);
    }

    [Fact]
    public async Task ActualCatalogCompactProjectionMeetsManagedBudget()
    {
        string? configurationPath =
            Environment.GetEnvironmentVariable(
                "MLM_MEMORY_CONFIG");
        if (string.IsNullOrWhiteSpace(
                configurationPath))
        {
            _output.WriteLine(
                "Set MLM_MEMORY_CONFIG to run the opt-in real-catalog memory gate.");
            return;
        }

        using var library =
            new LibraryService(
                configurationPath);
        long peakPrivateBytes = 0;
        using var sampling =
            new CancellationTokenSource();
        Task sampler = Task.Run(
            async () =>
            {
                while (!sampling
                    .IsCancellationRequested)
                {
                    using System.Diagnostics
                        .Process process =
                            System.Diagnostics
                                .Process
                                .GetCurrentProcess();
                    peakPrivateBytes = Math.Max(
                        peakPrivateBytes,
                        process
                            .PrivateMemorySize64);
                    try
                    {
                        await Task.Delay(
                            25,
                            sampling.Token);
                    }
                    catch (
                        OperationCanceledException)
                    {
                        break;
                    }
                }
            },
            TestContext.Current
                .CancellationToken);

        IReadOnlyList<TrackRecord> records =
            await library
                .GetBrowseRecordsAsync(
                    TestContext.Current
                        .CancellationToken);
        LibraryRow[] rows = records
            .Select(record =>
                new LibraryRow(record))
            .ToArray();
        ForceCollection();
        long liveManagedBytes =
            GC.GetTotalMemory(
                forceFullCollection: true);
        using System.Diagnostics.Process
            current =
                System.Diagnostics.Process
                    .GetCurrentProcess();
        long privateBytes =
            current.PrivateMemorySize64;
        sampling.Cancel();
        await sampler;

        _output.WriteLine(
            $"Tracks: {rows.Length:N0}");
        _output.WriteLine(
            $"Live managed: {liveManagedBytes / 1024d / 1024d:N1} MiB");
        _output.WriteLine(
            $"Private: {privateBytes / 1024d / 1024d:N1} MiB");
        _output.WriteLine(
            $"Peak private: {peakPrivateBytes / 1024d / 1024d:N1} MiB");
        Assert.True(
            liveManagedBytes <=
            1L * 1024 * 1024 * 1024,
            "The compact real-catalog projection exceeded the 1 GiB managed budget.");
        Assert.True(
            privateBytes <=
            1536L * 1024 * 1024,
            "The compact real-catalog projection exceeded the 1.5 GiB private-memory budget.");
        Assert.True(
            peakPrivateBytes <=
            2L * 1024 * 1024 * 1024,
            "The compact real-catalog projection exceeded the 2 GiB peak budget.");

        for (int cycle = 0;
             cycle < 5;
             cycle++)
            await RunBrowseCycleAsync(
                library,
                TestContext.Current
                    .CancellationToken);
        ForceCollection();
        long postCycleManagedBytes =
            GC.GetTotalMemory(
                forceFullCollection: true);
        long cycleGrowth =
            postCycleManagedBytes -
            liveManagedBytes;
        _output.WriteLine(
            $"Five-cycle managed growth: {cycleGrowth / 1024d / 1024d:N1} MiB");
        Assert.True(
            cycleGrowth < 100L * 1024 * 1024,
            "Five compact reload/filter/sort cycles retained at least 100 MiB.");
        GC.KeepAlive(rows);
        GC.KeepAlive(records);
    }

    private static async Task
        RunBrowseCycleAsync(
            ILibraryService library,
            CancellationToken cancellationToken)
    {
        IReadOnlyList<TrackRecord> records =
            await library
                .GetBrowseRecordsAsync(
                    cancellationToken);
        LibraryRow[] rows = records
            .Select(record =>
                new LibraryRow(record))
            .Where(row =>
                row.Details["Codec"]
                    .Contains(
                        "FLAC",
                        StringComparison
                            .OrdinalIgnoreCase))
            .OrderBy(row => row.Title)
            .Take(4_096)
            .ToArray();
        GC.KeepAlive(rows);
        GC.KeepAlive(records);
    }

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
