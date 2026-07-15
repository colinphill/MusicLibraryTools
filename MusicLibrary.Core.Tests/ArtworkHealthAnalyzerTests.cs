using MusicFileUtilities;
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
