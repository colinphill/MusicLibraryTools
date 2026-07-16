using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class AnalyzerTests
{
    private static TrackRecord Rec(string path, string albumArtist, string album, string title,
        int? track = null, int? total = null, CodecType codec = CodecType.Lossless,
        uint sr = 44100, uint bps = 16, int? disc = null, int? discTotal = null) =>
        new()
        {
            Path = path,
            AlbumArtist = albumArtist,
            Album = album,
            StrippedAlbum = album,
            Title = title,
            TrackNumber = track,
            TrackTotal = total,
            DiscNumber = disc,
            DiscTotal = discTotal,
            CodecType = codec,
            CodecName = codec == CodecType.Lossy ? "MP3" : "FLAC",
            SampleRate = sr,
            BitsPerSample = bps,
        };

    [Fact]
    public void Lossless_FlagsOnlyLossyFiles()
    {
        var records = new[]
        {
            Rec("a.flac", "AA", "Album", "One", codec: CodecType.Lossless),
            Rec("b.mp3", "AA", "Album", "Two", codec: CodecType.Lossy),
            Rec("c.mp3", "AA", "Album", "Three", codec: CodecType.Lossy),
        };

        var report = LibraryAnalyzer.Lossless(records);

        Assert.Equal(2, report.Count);
        Assert.All(report.Findings, f => Assert.EndsWith(".mp3", f.Path));
    }

    [Fact]
    public void InconsistentTotals_FlagsDisagreeingTotals()
    {
        var records = new[]
        {
            Rec("1.flac", "AA", "Album", "One", track: 1, total: 10),
            Rec("2.flac", "AA", "Album", "Two", track: 2, total: 12), // disagrees
        };

        var report = LibraryAnalyzer.InconsistentTotals(records);

        Assert.Equal(2, report.Count); // both files in the album flagged
    }

    [Fact]
    public void InconsistentTotals_FlagsTrackExceedingTotal()
    {
        var records = new[]
        {
            Rec("1.flac", "AA", "Album", "One", track: 1, total: 5),
            Rec("2.flac", "AA", "Album", "Two", track: 9, total: 5), // 9 > 5
        };

        var report = LibraryAnalyzer.InconsistentTotals(records);

        Assert.Contains(report.Findings, f => f.Path == "2.flac");
    }

    [Fact]
    public void InconsistentTotals_CleanAlbumProducesNothing()
    {
        var records = new[]
        {
            Rec("1.flac", "AA", "Album", "One", track: 1, total: 2),
            Rec("2.flac", "AA", "Album", "Two", track: 2, total: 2),
        };

        Assert.Empty(LibraryAnalyzer.InconsistentTotals(records).Findings);
    }

    [Fact]
    public void InconsistentTotals_FlagsDisagreeingDiscTotalsAndDiscExceedingTotal()
    {
        var records = new[]
        {
            Rec("1.flac", "AA", "Album", "One", track: 1, total: 1, disc: 1, discTotal: 2),
            Rec("2.flac", "aa", "album", "Two", track: 1, total: 1, disc: 4, discTotal: 3),
        };

        var report = LibraryAnalyzer.InconsistentTotals(records);

        Assert.Contains(report.Findings, f => f.Description.Contains("disagreeing total discs"));
        Assert.Contains(report.Findings, f => f.Path == "2.flac" && f.Description.Contains("disc 4 exceeds"));
    }

    [Fact]
    public void Inconsistencies_FlagsMissingTrackNumberAndTotal()
    {
        var records = new[]
        {
            Rec("1.flac", "AA", "Album", "One", track: 1, total: 10),
            Rec("2.flac", "AA", "Album", "Two", track: null, total: null),  // missing both
        };

        var report = LibraryAnalyzer.Inconsistencies(records);

        Assert.Contains(report.Findings, f => f.Path == "2.flac" && f.Description.Contains("track number"));
        Assert.Contains(report.Findings, f => f.Path == "2.flac" && f.Description.Contains("total"));
    }

    [Fact]
    public void SimilarArtists_FlagsNearDuplicateNames()
    {
        var records = new[]
        {
            Rec("1.flac", "Portishead", "A", "x"),
            Rec("2.flac", "Portished", "B", "y"),   // one-char typo variant
            Rec("3.flac", "Radiohead", "C", "z"),
        };

        var report = LibraryAnalyzer.SimilarArtists(records); // default threshold

        Assert.Contains(report.Findings, f => f.Description.Contains("Portishead"));
        Assert.DoesNotContain(report.Findings, f => f.Description.Contains("Radiohead"));
    }

    [Fact]
    public void BasicMetadata_ReportsLegacyPerFileChecks()
    {
        var record = Rec("album\u00a0name/track.flac", "AA", "Album", "One",
            track: null, total: 0, disc: 1, discTotal: 2);

        var report = LibraryAnalyzer.BasicMetadata([record]);

        Assert.Contains(report.Findings, finding => finding.Problem == "Zero track total");
        Assert.Contains(report.Findings,
            finding => finding.Problem == "Missing or zero track number");
        Assert.Contains(report.Findings, finding => finding.Problem == "Disc metadata present");
        Assert.Contains(report.Findings,
            finding => finding.Problem == "Non-breaking space in path");
    }

    [Fact]
    public void LowResolutionCheck_RecognizesEitherDirectorySeparator()
    {
        var records = new[]
        {
            Rec(@"Z:\iTunes\HiRes\Stereo\one.flac", "AA", "A", "One"),
            Rec("/music/hires/multi/two.flac", "AA", "A", "Two"),
            Rec("/music/flac/three.flac", "AA", "A", "Three"),
        };

        var report = LibraryAnalyzer.LowResolutionInHighResolutionTree(records);

        Assert.Equal(2, report.Count);
        Assert.DoesNotContain(report.Findings, finding => finding.Path.EndsWith("three.flac"));
    }

    [Fact]
    public void ResolutionComparison_ReportsTrackCountMismatchAgainstStandardAlbum()
    {
        var records = new[]
        {
            Rec(@"Z:\iTunes\HiRes\Stereo\Artist\Album\01.flac",
                "Artist", "Album", "One", track: 1, sr: 96_000, bps: 24),
            Rec(@"Z:\iTunes\FLAC\Artist\Album\01.flac",
                "Artist", "Album", "One", track: 1),
            Rec(@"Z:\iTunes\FLAC\Artist\Album\02.flac",
                "Artist", "Album", "Two", track: 2),
        };

        var report = LibraryAnalyzer.CompareResolutionAlbums(records, "hires", "stereo");

        Assert.Equal(1, report.AlbumCount);
        Assert.Equal(1, report.MatchedCount);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(ResolutionComparisonKind.TrackCountMismatch, finding.Kind);
        Assert.Equal(1, finding.HighTrackCount);
        Assert.Equal(2, finding.StandardTrackCount);
    }

    [Fact]
    public void DuplicateFinder_GroupsSameTrackAcrossVersions()
    {
        var records = new[]
        {
            Rec("hi.flac", "AA", "Album", "Song (Remastered)", track: 1, sr: 96000, bps: 24),
            Rec("lo.mp3", "AA", "Album", "Song", track: 1, codec: CodecType.Lossy, sr: 44100),
            Rec("other.flac", "AA", "Album", "Different", track: 2),
        };

        var groups = DuplicateFinder.Find(records);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Tracks.Count);
        // Highest quality first.
        Assert.Equal("hi.flac", group.Tracks[0].Path);
    }

    [Fact]
    public void DuplicateFinder_DelimitersInFieldsDoNotCollide()
    {
        var records = new[]
        {
            Rec("one.flac", "a|b", "c", "Song", track: 1),
            Rec("two.flac", "a", "b|c", "Song", track: 1),
        };

        Assert.Empty(DuplicateFinder.Find(records));
    }

    [Fact]
    public void RepresentationsFindsMissingPurchasedTrackWithoutFlaggingSingleRoleAlbums()
    {
        var records = new[]
        {
            Rec(@"Z:\FLAC\Artist\Album\01.flac", "Artist", "Album", "One", track: 1),
            Rec(@"Z:\FLAC\Artist\Album\02.flac", "Artist", "Album", "Two", track: 2),
            Rec(@"Z:\iTunes\purchased sync\Artist\Album\01.m4a", "artist", "album", "One",
                track: 1, codec: CodecType.Lossy),
            Rec(@"Z:\FLAC\Artist\Only\01.flac", "Artist", "Only", "Solo", track: 1),
        };

        var report = RepresentationAnalyzer.Compare(records);

        var finding = Assert.Single(report.Findings, item =>
            item.Problem == "Missing representation counterpart");
        Assert.Equal(records[1].Path, finding.Path);
        Assert.Equal("Missing representation counterpart", finding.Problem);
        Assert.Contains("purchased audio", finding.Description);
        Assert.DoesNotContain(report.Findings, item => item.Path.Contains("Only"));
        Assert.Single(report.Findings, item => item.Problem == "Representation track-count drift");
    }

    [Fact]
    public void RepresentationsClassifiesHighResolutionFlacAndReportsAmbiguousCandidates()
    {
        var high = Rec(@"Z:\hires\Album\01.flac", "Artist", "Album", "One",
            track: 1, sr: 96_000, bps: 24);
        var records = new[]
        {
            high,
            Rec(@"Z:\FLAC\Album\01.flac", "Artist", "Album", "One", track: 1),
            Rec(@"Z:\FLAC\Album\01-copy.flac", "Artist", "Album", "One", track: 1),
        };

        var report = RepresentationAnalyzer.Compare(records);

        Assert.Equal(LibraryRepresentation.HighResolutionFlac, RepresentationAnalyzer.Classify(high));
        Assert.Equal(2, report.Findings.Count(item =>
            item.Problem == "Ambiguous representation counterpart"));
        Assert.DoesNotContain(report.Findings, item =>
            item.Problem == "Missing representation counterpart");
    }

    [Fact]
    public void RepresentationsReportsMetadataDurationAndArtworkDriftForMatchedTracks()
    {
        var cd = Rec(@"Z:\FLAC\Album\01.flac", "Artist", "Album", "Song",
            track: 1, total: 1) with
        {
            Artist = "Artist", ReleaseDate = "2020", DiscTotal = 1, DurationInSeconds = 180,
        };
        var high = Rec(@"Z:\hires\Album\01.flac", "Artist", "Album", "Song (Remastered)",
            track: 1, total: 2, sr: 96_000, bps: 24) with
        {
            Artist = "Different Artist", ReleaseDate = "2021", DiscTotal = 2, DurationInSeconds = 185,
        };

        var report = RepresentationAnalyzer.Compare([cd, high]);
        var candidates = RepresentationAnalyzer.ArtworkCandidatePaths([cd, high]);
        var artwork = RepresentationAnalyzer.CompareArtwork([cd, high], new Dictionary<string, string>
        {
            [cd.Path] = "cover-a",
            [high.Path] = "cover-b",
        });

        Assert.Contains(report.Findings, item => item.Problem == "Representation metadata drift" &&
            item.Description.Contains("title"));
        Assert.Contains(report.Findings, item => item.Problem == "Representation metadata drift" &&
            item.Description.Contains("track total"));
        Assert.Contains(report.Findings, item => item.Problem == "Representation duration drift");
        Assert.Equal(2, candidates.Count);
        Assert.Single(artwork.Findings, item => item.Problem == "Representation artwork drift");
    }

    [Fact]
    public void RepresentationArtworkReportsMixedRoleBeforeCrossRoleDrift()
    {
        var cd1 = Rec(@"Z:\FLAC\Album\01.flac", "Artist", "Album", "One", track: 1);
        var cd2 = Rec(@"Z:\FLAC\Album\02.flac", "Artist", "Album", "Two", track: 2);
        var high1 = Rec(@"Z:\hires\Album\01.flac", "Artist", "Album", "One", track: 1,
            sr: 96_000, bps: 24);
        var high2 = Rec(@"Z:\hires\Album\02.flac", "Artist", "Album", "Two", track: 2,
            sr: 96_000, bps: 24);
        var records = new[] { cd1, cd2, high1, high2 };
        var signatures = new Dictionary<string, string>
        {
            [cd1.Path] = "a", [cd2.Path] = "b", [high1.Path] = "a", [high2.Path] = "a",
        };

        var report = RepresentationAnalyzer.CompareArtwork(records, signatures);

        Assert.Single(report.Findings);
        Assert.Equal("Mixed representation artwork", report.Findings[0].Problem);
    }

    [Fact]
    public void DecodedAudioCandidatesRequireCompatibleKnownLosslessGeometry()
    {
        var cd = Rec(@"Z:\FLAC\Album\01.flac", "Artist", "Album", "One", track: 1) with
        {
            Channels = 2,
        };
        var purchasedAlac = Rec(@"Z:\iTunes\purchased sync\Album\01.m4a",
            "Artist", "Album", "One", track: 1) with
        {
            Channels = 2,
        };
        var high = Rec(@"Z:\hires\Album\01.flac", "Artist", "Album", "One", track: 1,
            sr: 96_000, bps: 24) with
        {
            Channels = 2,
        };

        var pairs = RepresentationAnalyzer.DecodedAudioCandidatePairs([cd, purchasedAlac, high]);

        var pair = Assert.Single(pairs);
        Assert.Equal(cd.Path, pair.FirstPath);
        Assert.Equal(purchasedAlac.Path, pair.SecondPath);
        Assert.DoesNotContain(high.Path, new[] { pair.FirstPath, pair.SecondPath });
    }
}
