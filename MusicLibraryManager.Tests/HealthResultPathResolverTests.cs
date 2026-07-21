using iTunes.Binary;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class HealthResultPathResolverTests
{
    private const string Path = @"C:\Music\Artist\Album\Track.flac";

    [Fact]
    public void Resolves_each_single_path_health_result_shape()
    {
        var finding = new AnalysisFindingViewModel(
            new AnalysisFinding(Path, "Issue"), "Artist", "Album");
        var metadataRepair = new AnalysisRepairItemViewModel(
            new AnalysisTagRepair(Path, TagFields.Artist, "old", "new", "Reason", 1,
                DateTime.UnixEpoch));
        var fileRepair = new RepresentationRepairActionItemViewModel(
            new RepresentationRepairAction(RepresentationRepairKind.Organize, Path,
                @"C:\Organized\Track.flac", "Move"));
        var itlRepair = new ItlMetadataRepairItemViewModel(new ItlMetadataRepairItem(
            Guid.NewGuid(), 1, 1, Path, new ItlCachedTrackMetadata(), DateTime.UnixEpoch, []));
        var conflict = new AnalysisConflictGroupViewModel(
            new AnalysisTagConflict("Album", @"C:\Music\Artist\Album", TagFields.AlbumArtist,
                [], []));
        var track = new TrackRecord { Path = Path };
        var cell = new AnalysisMatrixCell("value");
        var matrixRow = new AlbumMetadataRow(Path, cell, cell, cell, cell, cell, cell, cell,
            cell, cell);
        var artistPath = new ArtistPathViewModel(Path);

        AssertPath(finding, Path);
        AssertPath(metadataRepair, Path);
        AssertPath(fileRepair, Path);
        AssertPath(itlRepair, Path);
        AssertPath(conflict, @"C:\Music\Artist\Album");
        AssertPath(track, Path);
        AssertPath(matrixRow, Path);
        AssertPath(artistPath, Path);
    }

    [Fact]
    public void Rejects_aggregate_unknown_and_empty_results()
    {
        Assert.False(HealthResultPathResolver.TryGetPath(
            new ArtistVariant("Artist", [Path, @"C:\Music\Other.flac"]), out _));
        Assert.False(HealthResultPathResolver.TryGetPath(new object(), out _));
        Assert.False(HealthResultPathResolver.TryGetPath(
            new TrackRecord { Path = "   " }, out _));
        Assert.False(HealthResultPathResolver.TryGetPath(null, out _));
    }

    private static void AssertPath(object result, string expected)
    {
        Assert.True(HealthResultPathResolver.TryGetPath(result, out string actual));
        Assert.Equal(expected, actual);
    }
}
