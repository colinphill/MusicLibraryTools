using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class AnalyzerViewModelTests
{
    [Fact]
    public void FindingRun_GroupsByProblemAndAlbumAndTracksDisposition()
    {
        var records = new[]
        {
            Track(Path.Combine("AA", "First", "01.flac"), "AA", "First"),
            Track(Path.Combine("AA", "First", "02.flac"), "AA", "First"),
            Track(Path.Combine("BB", "Second", "01.flac"), "BB", "Second"),
        };
        var report = new AnalysisReport("Health", [
            new(records[0].Path, "missing total", "Missing track total"),
            new(records[1].Path, "missing total", "Missing track total"),
            new(records[2].Path, "lossy", "Lossy codec"),
        ]);

        var run = AnalysisRunViewModel.ForFindings(report, records, "3 findings");

        Assert.Equal(2, run.FindingGroups.Count);
        var missing = run.FindingGroups.Single(group => group.Problem == "Missing track total");
        var album = Assert.Single(missing.Albums);
        Assert.Equal("AA — First", album.Album);
        Assert.Equal(2, album.ActiveCount);
        Assert.Equal(3, run.ActiveFindingCount);

        album.Findings[0].Disposition = AnalysisFindingDisposition.Deferred;

        Assert.Equal(1, album.ActiveCount);
        Assert.Equal(1, missing.ActiveCount);
        Assert.Equal(2, run.ActiveFindingCount);
    }

    [Fact]
    public async Task Analyzer_RetainsRunsAndRestoresEachTypedResult()
    {
        var records = new[]
        {
            Track("one.mp3", "AA", "Album", CodecType.Lossy, "Song", 1),
            Track("two.flac", "AA", "Album", CodecType.Lossless, "Song", 1),
        };
        var viewModel = Create(records);

        await viewModel.RunDuplicatesCommand.ExecuteAsync(null);
        var duplicateRun = Assert.Single(viewModel.Runs);
        Assert.Single(viewModel.Duplicates);

        await viewModel.RunLossyCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.Runs.Count);
        Assert.Equal("Lossy files", viewModel.SelectedRun!.Name);
        Assert.Single(viewModel.FindingGroups);
        Assert.Empty(viewModel.Duplicates);

        viewModel.SelectedRun = duplicateRun;

        Assert.Equal(AnalysisResultView.Duplicates, viewModel.ActiveView);
        Assert.Single(viewModel.Duplicates);
        Assert.Empty(viewModel.FindingGroups);
    }

    [Fact]
    public async Task Analyzer_FindingDispositionSurvivesNavigationBetweenRuns()
    {
        var viewModel = Create([
            Track("one.mp3", "AA", "Album", CodecType.Lossy, "One", 1),
            Track("two.flac", "AA", "Album", CodecType.Lossless, "Two", null),
        ]);
        await viewModel.RunLossyCommand.ExecuteAsync(null);
        var lossyRun = viewModel.SelectedRun!;
        var finding = Assert.Single(Assert.Single(lossyRun.FindingGroups).Albums).Findings[0];
        finding.Disposition = AnalysisFindingDisposition.Completed;

        await viewModel.RunInconsistenciesCommand.ExecuteAsync(null);
        viewModel.SelectedRun = lossyRun;

        Assert.Equal(AnalysisFindingDisposition.Completed,
            Assert.Single(Assert.Single(viewModel.FindingGroups).Albums).Findings[0].Disposition);
        Assert.Equal(0, lossyRun.ActiveFindingCount);
    }

    [Fact]
    public async Task Analyzer_RemoveAndClearResultsSelectAValidRun()
    {
        var viewModel = Create([Track("one.mp3", "AA", "Album", CodecType.Lossy, "One", 1)]);
        await viewModel.RunLossyCommand.ExecuteAsync(null);
        await viewModel.RunInconsistenciesCommand.ExecuteAsync(null);

        viewModel.RemoveRunCommand.Execute(null);

        Assert.Single(viewModel.Runs);
        Assert.Same(viewModel.Runs[0], viewModel.SelectedRun);

        viewModel.ClearRunsCommand.Execute(null);

        Assert.Empty(viewModel.Runs);
        Assert.Null(viewModel.SelectedRun);
        Assert.False(viewModel.HasRuns);
    }

    [Fact]
    public async Task Analyzer_MetadataRepairPreviewUsesTheCombinedSafePlan()
    {
        var record = Track("track.flac", "AA", "Album");
        var repairs = new TrackingRepairs(new AnalysisRepairPlan("Safe metadata repairs", [
            new(record.Path, TagFields.TotalTracks, null, "10", "reason", 100, DateTime.UtcNow),
        ]));
        var viewModel = new AnalyzerViewModel(
            new StubLibrary([record]), new StubReconciler(), repairs,
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")));

        await viewModel.PreviewMetadataRepairsCommand.ExecuteAsync(null);

        Assert.Equal(1, repairs.SafePreviewCalls);
        Assert.Equal("Safe metadata repairs", viewModel.SelectedRun!.Name);
        Assert.Single(viewModel.RepairItems);
    }

    [Fact]
    public async Task Analyzer_AlbumMatrixIsRetainedAsATypedRun()
    {
        var record = Track("track.flac", "AA", "Album") with { TrackTotal = null };
        var viewModel = Create([record]);

        await viewModel.RunAlbumMatrixCommand.ExecuteAsync(null);

        Assert.Equal(AnalysisResultView.Matrix, viewModel.ActiveView);
        Assert.Equal("Album metadata matrix", viewModel.SelectedRun!.Name);
        Assert.Single(viewModel.Matrices);
        Assert.Same(viewModel.Matrices[0], Assert.Single(viewModel.SelectedRun.Matrices));
    }

    private static AnalyzerViewModel Create(IReadOnlyList<TrackRecord> records) =>
        new(new StubLibrary(records), new StubReconciler(), new StubRepairs(),
            new AppSettings(Path.Combine(Path.GetTempPath(), $"analyzer-{Guid.NewGuid():N}.json")));

    private static TrackRecord Track(
        string path,
        string artist,
        string album,
        CodecType codec = CodecType.Lossless,
        string title = "Track",
        int? track = 1) => new()
        {
            Path = path,
            AlbumArtist = artist,
            HasAlbumArtist = true,
            Album = album,
            StrippedAlbum = album,
            Title = title,
            TrackNumber = track,
            TrackTotal = 1,
            CodecType = codec,
            CodecName = codec == CodecType.Lossy ? "MP3" : "FLAC",
        };

    private sealed class StubLibrary(IReadOnlyList<TrackRecord> records) : ILibraryService
    {
        public bool IsReady => true;
        public Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default) =>
            Task.FromResult(records);
        public Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default) =>
            Task.FromResult(new AnalysisReport("Cross-set check", []));
        public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LibrarySnapshot> BuildSnapshotAsync(
            LibraryGrouping grouping = LibraryGrouping.AlbumArtist, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<FileDetails?> GetFileDetailsAsync(
            string path, bool includeArtwork, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetImageSignaturesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubReconciler : IArtistReconciler
    {
        public IReadOnlyList<SimilarArtistGroup> FindSimilarArtists(
            IReadOnlyList<TrackRecord> records, double threshold = 0.2, CancellationToken ct = default) => [];
        public Task<int> RenameArtistAsync(
            IReadOnlyList<string> paths, string from, string to,
            IProgress<int>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private sealed class StubRepairs : IAnalysisRepairService
    {
        public AnalysisRepairPlan PreviewSafeRepairs(IReadOnlyList<TrackRecord> records) =>
            new("Safe metadata repairs", []);
        public AnalysisRepairPlan PreviewMissingAlbumArtists(IReadOnlyList<TrackRecord> records) =>
            new("Fill missing album artists", []);
        public AnalysisRepairPlan PreviewNumberingAndTotals(IReadOnlyList<TrackRecord> records) =>
            new("Repair numbering and totals", []);
        public AnalysisRepairPlan PreviewTextNormalization(IReadOnlyList<TrackRecord> records) =>
            new("Normalize metadata text", []);
        public Task<BatchWriteResult> ApplyAsync(
            AnalysisRepairPlan plan, IProgress<int>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(new BatchWriteResult([]));
    }

    private sealed class TrackingRepairs(AnalysisRepairPlan plan) : IAnalysisRepairService
    {
        public int SafePreviewCalls { get; private set; }
        public AnalysisRepairPlan PreviewSafeRepairs(IReadOnlyList<TrackRecord> records)
        {
            SafePreviewCalls++;
            return plan;
        }
        public AnalysisRepairPlan PreviewMissingAlbumArtists(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewNumberingAndTotals(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public AnalysisRepairPlan PreviewTextNormalization(IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();
        public Task<BatchWriteResult> ApplyAsync(
            AnalysisRepairPlan plan, IProgress<int>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
