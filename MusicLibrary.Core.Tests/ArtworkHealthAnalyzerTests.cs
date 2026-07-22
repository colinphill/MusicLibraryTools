using MusicFileUtilities;
using MusicLibraryTools;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ArtworkHealthAnalyzerTests
{
    [Fact]
    public void AnalyzeFindsDeferredMissingMixedOversizedUnreadableAndDuplicateArtwork()
    {
        var records = new[]
        {
            Track("deferred.flac"), Track("missing.flac"), Track("large.flac"),
            Track("other.flac"), Track("invalid.flac"),
        };
        var large = new ArtworkAuditImage("large", "image/jpeg", "FrontCover", 3000, 3000, 3_000_000);
        var artwork = new[]
        {
            new ArtworkAuditFile("deferred.flac", false, []),
            new ArtworkAuditFile("missing.flac", true, []),
            new ArtworkAuditFile("large.flac", true, [large, large]),
            new ArtworkAuditFile("other.flac", true,
                [new("other", "image/png", "FrontCover", 500, 500, 50_000)]),
            new ArtworkAuditFile("invalid.flac", true,
                [new("", "", "FrontCover", 0, 0, 0)]),
        };

        var report = ArtworkHealthAnalyzer.Analyze(records, artwork);

        foreach (string problem in new[]
                 {
                     "Artwork scan deferred", "Missing artwork", "Mixed album artwork",
                     "Oversized artwork", "Unreadable artwork", "Duplicate embedded artwork",
                 })
            Assert.Contains(report.Findings, finding => finding.Problem == problem);

        Assert.Equal(
            ["invalid.flac", "large.flac", "missing.flac", "other.flac"],
            report.Findings.Where(finding => finding.Problem == "Mixed album artwork")
                .Select(finding => finding.Path).Order().ToArray());
    }

    [Fact]
    public void AnalyzeUsesConfiguredOversizedThresholdsWithStrictComparison()
    {
        TrackRecord record = Track("cover.flac");
        var artwork = new[]
        {
            new ArtworkAuditFile(record.Path, true,
                [new("cover", "image/jpeg", "FrontCover", 1_500, 1_000, 1_500_000)]),
        };

        AnalysisReport exactLimits = ArtworkHealthAnalyzer.Analyze([record], artwork,
            1_500_000, 1_500);
        Assert.DoesNotContain(exactLimits.Findings,
            finding => finding.Problem == "Oversized artwork");

        AnalysisReport encodedSizeExceeded = ArtworkHealthAnalyzer.Analyze([record], artwork,
            1_499_999, 2_000);
        Assert.Contains(encodedSizeExceeded.Findings,
            finding => finding.Problem == "Oversized artwork");

        var portraitArtwork = new[]
        {
            new ArtworkAuditFile(record.Path, true,
                [new("portrait", "image/jpeg", "FrontCover", 1_000, 1_500, 100_000)]),
        };
        AnalysisReport dimensionExceeded = ArtworkHealthAnalyzer.Analyze([record], portraitArtwork,
            2_000_000, 1_499);
        Assert.Contains(dimensionExceeded.Findings,
            finding => finding.Problem == "Oversized artwork");
    }

    [Fact]
    public void AnalyzeReportsAlbumComparisonAfterTheFileScan()
    {
        TrackRecord[] records = [Track("one.flac"), Track("two.flac")];
        ArtworkAuditFile[] artwork =
        [
            new("one.flac", true,
                [new("one", "image/jpeg", "FrontCover", 500, 500, 50_000)]),
            new("two.flac", true,
                [new("two", "image/jpeg", "FrontCover", 500, 500, 50_000)]),
        ];
        var updates = new List<AnalysisProgress>();

        _ = ArtworkHealthAnalyzer.Analyze(records, artwork, null,
            ArtworkHealthAnalyzer.OversizedByteThreshold,
            ArtworkHealthAnalyzer.OversizedDimensionThreshold,
            new SynchronousProgress<AnalysisProgress>(updates.Add));

        Assert.Contains(updates, update => update.Stage == "Checking artwork metadata" &&
            update.Completed == records.Length);
        Assert.Contains(updates, update => update.Stage == "Comparing album artwork" &&
            update.Completed == records.Length);
    }

    [Fact]
    public void GenericAlbumIdentityKeepsEditionsSeparateForArtworkComparison()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "artwork-identity-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            EditableLibraryConfig editable = EditableLibraryConfig.CreateNew();
            editable.ActiveProfileId = LibraryProfilePresets.ArtistAlbumId;
            editable.Save(path);
            var configuration = new LibraryConfiguration(path);
            TrackRecord first = Track("original.flac") with
            {
                Album = "Album (Original)",
                StrippedAlbum = "Album",
            };
            TrackRecord remaster = Track("remaster.flac") with
            {
                Album = "Album (Remastered)",
                StrippedAlbum = "Album",
            };
            ArtworkAuditFile[] artwork =
            [
                new(first.Path, true,
                    [new("original", "image/jpeg", "FrontCover", 500, 500, 50_000)]),
                new(remaster.Path, true,
                    [new("remaster", "image/jpeg", "FrontCover", 500, 500, 50_000)]),
            ];

            AnalysisReport report = ArtworkHealthAnalyzer.Analyze(
                [first, remaster], artwork, configuration,
                ArtworkHealthAnalyzer.OversizedByteThreshold,
                ArtworkHealthAnalyzer.OversizedDimensionThreshold);

            Assert.DoesNotContain(report.Findings,
                finding => finding.Problem == "Mixed album artwork");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static TrackRecord Track(string path) => new()
    {
        Path = path,
        Artist = "Artist",
        AlbumArtist = "Artist",
        HasAlbumArtist = true,
        Album = "Album",
        StrippedAlbum = "Album",
        Title = Path.GetFileNameWithoutExtension(path),
        CodecType = CodecType.Lossless,
    };
}
