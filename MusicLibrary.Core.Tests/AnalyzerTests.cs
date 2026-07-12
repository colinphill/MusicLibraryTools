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
}
