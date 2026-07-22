using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryTools;
using MusicFileUtilities;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ArtworkRepairPlannerTests
{
    [Fact]
    public void RepairGroupsKeepAlbumAndFileActionsInASelectableHierarchy()
    {
        var album = new ArtworkRepairItemViewModel(
            ArtworkRepairKind.NormalizeAlbum, "Artist — Album", "album", ["one.flac"],
            [], true, 100_000, 500, "blocked", "Artist", "Album");
        var file = new ArtworkRepairItemViewModel(
            ArtworkRepairKind.NormalizeFile, "two.flac", "file", ["two.flac"],
            [], false, 100_000, 500, "blocked", "Artist", "Album");

        IReadOnlyList<ArtworkRepairCategoryGroupViewModel> groups =
            ArtworkRepairCategoryGroupViewModel.Build([file, album]);

        Assert.Equal(["Mixed album artwork", "File artwork"],
            groups.Select(group => group.Category));
        ArtworkRepairArtistGroupViewModel artist = Assert.Single(groups[0].Artists);
        Assert.Equal("Artist", artist.Artist);
        ArtworkRepairAlbumGroupViewModel groupedAlbum = Assert.Single(artist.Albums);
        Assert.Equal("Album", groupedAlbum.Album);
        Assert.Same(album, Assert.Single(groupedAlbum.Items));
        groups[0].Disposition = AnalysisRepairDisposition.Filter;
        Assert.Equal(AnalysisRepairDisposition.Filter, album.Disposition);
    }

    [Fact]
    public void AutomaticCandidateSelectionUsesPixelsThenFileSizeAndMarksActive()
    {
        ArtworkRepairCandidateViewModel[] candidates =
        [
            Candidate("first", 1_000, 1_000, 500_000),
            Candidate("largest-file", 2_000, 500, 700_000),
            Candidate("highest-resolution", 1_200, 1_000, 400_000),
        ];
        var repair = new ArtworkRepairItemViewModel(
            ArtworkRepairKind.NormalizeAlbum, "Artist — Album", "album", ["one.flac"],
            candidates, true, 100_000, 500, artist: "Artist", album: "Album");

        Assert.True(repair.SelectCandidateAndActivate(ArtworkCandidateSelectionRule.First));
        Assert.Equal("first", repair.SelectedCandidate!.Label);
        Assert.Equal(AnalysisRepairDisposition.Active, repair.Disposition);

        Assert.True(repair.SelectCandidateAndActivate(
            ArtworkCandidateSelectionRule.HighestResolution));
        Assert.Equal("highest-resolution", repair.SelectedCandidate!.Label);

        Assert.True(repair.SelectCandidateAndActivate(ArtworkCandidateSelectionRule.LargestFile));
        Assert.Equal("largest-file", repair.SelectedCandidate!.Label);
    }

    [Fact]
    public async Task MixedAlbumOffersEveryCoverAndTargetsEveryScannedFile()
    {
        TrackRecord[] records =
        [
            Track("one.flac", "One"),
            Track("two.flac", "Two"),
        ];
        ArtworkAuditFile[] audit =
        [
            new("one.flac", true,
                [new("hash-one", "image/jpeg", "FrontCover", 900, 900, 300_000)]),
            new("two.flac", true,
                [new("hash-two", "image/jpeg", "FrontCover", 700, 700, 200_000)]),
        ];
        var settings = new LibraryArtworkHealthSettings(
            2_000_000, 2_000, RepairTargetByteSize: 123_456,
            RepairTargetDimension: 777);
        var library = new PlannerLibrary(records, audit, new Dictionary<string, byte[]>
        {
            ["one.flac"] = [1, 2, 3],
            ["two.flac"] = [4, 5, 6],
        });

        var thumbnails = new RecordingThumbnailService();
        IReadOnlyList<ArtworkRepairItemViewModel> repairs =
            await ArtworkRepairPlanner.BuildAsync(records, audit, settings,
                library, new WritableArtworkService(), thumbnails);

        ArtworkRepairItemViewModel repair = Assert.Single(repairs);
        Assert.Equal(ArtworkRepairKind.NormalizeAlbum, repair.Kind);
        Assert.Equal(["one.flac", "two.flac"], repair.AffectedPaths.Select(path => path.Path));
        Assert.Equal(["One", "Two"], repair.Candidates.Select(candidate => candidate.Label));
        Assert.True(repair.ShowGallery);
        Assert.Equal(123_456, repair.MaximumBytes);
        Assert.Equal(777, repair.MaximumDimension);
        Assert.Equal(0, library.FirstImageReadCount);
        Assert.Equal(0, thumbnails.CallCount);

        await repair.Candidates[0].EnsureThumbnailAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, library.FirstImageReadCount);
        Assert.Equal(1, thumbnails.CallCount);
        Assert.NotNull(repair.Candidates[0].ImageSource);
    }

    [Fact]
    public async Task PlannerReportsEveryPostScanPhaseToCompletion()
    {
        TrackRecord[] records = [Track("one.flac", "One"), Track("two.flac", "Two")];
        ArtworkAuditFile[] audit =
        [
            new("one.flac", true,
                [new("hash-one", "image/jpeg", "FrontCover", 900, 900, 300_000)]),
            new("two.flac", true,
                [new("hash-two", "image/jpeg", "FrontCover", 700, 700, 200_000)]),
        ];
        var settings = new LibraryArtworkHealthSettings(2_000_000, 2_000);
        var library = new PlannerLibrary(records, audit, new Dictionary<string, byte[]>());
        var updates = new List<AnalysisProgress>();

        _ = await ArtworkRepairPlanner.BuildAsync(
            records, audit, settings, library, new WritableArtworkService(), null,
            null, new SynchronousProgress<AnalysisProgress>(updates.Add));

        Assert.Contains(updates, update => update.Stage == "Planning album artwork repairs" &&
            update.Completed == records.Length && update.Total == records.Length);
        Assert.Contains(updates, update => update.Stage == "Planning file artwork repairs" &&
            update.Completed == audit.Length && update.Total == audit.Length);
        Assert.Contains(updates, update => update.Stage == "Preparing artwork repair choices" &&
            update.Completed == 1 && update.Total == 1);
    }

    private static TrackRecord Track(string path, string title) => new()
    {
        Path = path,
        AlbumArtist = "Artist",
        HasAlbumArtist = true,
        Album = "Album",
        StrippedAlbum = "Album",
        Title = title,
    };

    private static ArtworkRepairCandidateViewModel Candidate(
        string label,
        int width,
        int height,
        long size) => new(
        $"{label}.flac", label, label, label, width, height, size, null!, null);

    private sealed class PlannerLibrary(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkAuditFile> audit,
        IReadOnlyDictionary<string, byte[]> images) : ILibraryService
    {
        public int FirstImageReadCount { get; private set; }
        public bool IsReady => true;
        public Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default) =>
            Task.FromResult(records);
        public Task<IReadOnlyList<ArtworkAuditFile>> GetArtworkAuditFilesAsync(
            CancellationToken ct = default) => Task.FromResult(audit);
        public Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<byte[]?>>(paths
                .Select(path => images.TryGetValue(path, out byte[]? data) ? data : null).ToArray());
        public Task<IReadOnlyList<string>> GetImageSignaturesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LibrarySnapshot> BuildSnapshotAsync(
            LibraryGrouping grouping = LibraryGrouping.AlbumArtist,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<FileDetails?> GetFileDetailsAsync(
            string path, bool includeArtwork, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default)
        {
            FirstImageReadCount++;
            return Task.FromResult(images.TryGetValue(path, out byte[]? data) ? data : null);
        }
    }

    private sealed class RecordingThumbnailService : IThumbnailService
    {
        public int CallCount { get; private set; }
        public Task<object?> CreateImageSourceAsync(byte[] data, int decodePixelWidth = 0,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<object?>(new object());
        }
    }

    private sealed class WritableArtworkService : IArtworkService
    {
        public bool SupportsWrite(string musicPath) => true;
        public Task<ArtworkOpResult> SetCoverFromFileAsync(string musicPath, string imagePath,
            int maxDimension = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArtworkOpResult> ScrubAsync(string musicPath, int maxDimension,
            int quality = 90, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArtworkOpResult> RemoveAsync(string musicPath,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PreparedImage?> PrepareFromFileAsync(string imagePath,
            int maxDimension = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PreparedImage?> PrepareFromBytesAsync(byte[] data,
            int maxDimension = 0, int quality = 90, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ArtworkOpResult> SaveImagesAsync(string musicPath,
            IReadOnlyList<ArtworkInput> images, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
