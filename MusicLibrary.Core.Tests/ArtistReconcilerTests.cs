using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class ArtistReconcilerTests
{
    private static TrackRecord Rec(string path, string albumArtist) => new()
    {
        Path = path,
        AlbumArtist = albumArtist,
        Album = "A",
        Title = "T",
        CodecType = CodecType.Lossless,
    };

    private readonly ArtistReconciler _reconciler = new(new MediaFileService(), new TagWriteService());

    [Fact]
    public void FindSimilarArtists_ClustersNearDuplicateSpellings()
    {
        var records = new[]
        {
            Rec("1.flac", "Portishead"),
            Rec("2.flac", "Portishead"),
            Rec("3.flac", "Portished"),   // typo variant
            Rec("4.flac", "Radiohead"),   // unrelated
        };

        var groups = _reconciler.FindSimilarArtists(records);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Variants.Count);
        Assert.Equal("Portishead", group.Suggested);          // more tracks → suggested canonical
        Assert.Equal(3, group.AllPaths.Count);
        Assert.DoesNotContain(group.Variants, v => v.Name == "Radiohead");
    }

    [Fact]
    public void FindSimilarArtists_ReportsTrackScanAndPairwiseComparisonProgress()
    {
        TrackRecord[] records =
        [
            Rec("1.flac", "Portishead"),
            Rec("2.flac", "Portished"),
            Rec("3.flac", "Radiohead"),
        ];
        var updates = new List<AnalysisProgress>();

        _ = _reconciler.FindSimilarArtists(records, 0.2,
            new SynchronousProgress<AnalysisProgress>(updates.Add));

        AnalysisProgress trackFinal = updates.Last(update => update.Unit == "tracks");
        Assert.Equal(records.Length, trackFinal.Completed);
        Assert.Equal(records.Length, trackFinal.Total);
        AnalysisProgress comparisonFinal = updates.Last(
            update => update.Unit == "artist-name comparisons");
        Assert.Equal(3, comparisonFinal.Completed);
        Assert.Equal(3, comparisonFinal.Total);
    }

    [Fact]
    public void FindSimilarArtists_ClustersNormalizedVariations()
    {
        var records = new[]
        {
            Rec("1.flac", "The Beatles"),
            Rec("2.flac", "Beatles"),
            Rec("3.flac", "Beatlés"),     // article + diacritic variations
            Rec("4.flac", "R.E.M."),
            Rec("5.flac", "REM"),         // punctuation variation
            Rec("6.flac", "Radiohead"),   // unrelated
        };

        // threshold 0 disables fuzzy matching, so ONLY the variations check can cluster these.
        var groups = _reconciler.FindSimilarArtists(records, threshold: 0);

        Assert.Contains(groups, g => g.Variants.Any(v => v.Name == "The Beatles")
            && g.Variants.Any(v => v.Name == "Beatles")
            && g.Variants.Any(v => v.Name == "Beatlés"));
        Assert.Contains(groups, g => g.Variants.Any(v => v.Name == "R.E.M.")
            && g.Variants.Any(v => v.Name == "REM"));
        Assert.DoesNotContain(groups, g => g.Variants.Any(v => v.Name == "Radiohead"));
    }

    [Fact]
    public void FindSimilarArtists_FuzzyMatchesCanonicalForms()
    {
        // The fuzzy pass (checkartists 453-500) runs on the canonical forms: "The Beatles" → "beatles"
        // is one typo away from "Beetles" → "beetles", so they cluster even though the raw names differ.
        var records = new[]
        {
            Rec("1.flac", "The Beatles"),
            Rec("2.flac", "Beetles"),
            Rec("3.flac", "Radiohead"),
        };

        var group = Assert.Single(_reconciler.FindSimilarArtists(records));   // default threshold 0.2
        Assert.Equal(2, group.Variants.Count);
        Assert.Contains(group.Variants, v => v.Name == "The Beatles");
        Assert.Contains(group.Variants, v => v.Name == "Beetles");
    }

    [Fact]
    public void FindSimilarArtists_NoFalsePositivesForDistinctNames()
    {
        var records = new[]
        {
            Rec("1.flac", "Radiohead"),
            Rec("2.flac", "Portishead"),
            Rec("3.flac", "Massive Attack"),
        };

        Assert.Empty(_reconciler.FindSimilarArtists(records));
    }

    [Fact]
    public void FindSimilarArtists_IncludesTrackArtistWhenAlbumArtistDiffers()
    {
        var records = new[]
        {
            Rec("1.flac", "Various Artists") with { Artist = "The Beatles" },
            Rec("2.flac", "Various Artists") with { Artist = "Beatles" },
        };

        var group = Assert.Single(_reconciler.FindSimilarArtists(records, threshold: 0),
            candidate => candidate.Variants.Any(variant => variant.Name == "The Beatles"));

        Assert.Contains(group.Variants, variant => variant.Name == "Beatles");
    }

    [Fact]
    public async Task RenameArtist_RewritesMatchingFiles()
    {
        using var media = MediaFixtures.Copy("sample.flac");   // Artist=TestArtist

        var changed = await _reconciler.RenameArtistAsync([media.Path], "TestArtist", "Corrected Artist");
        Assert.Equal(1, changed);

        var reload = await new MediaFileService().LoadAsync(media.Path);
        Assert.Equal("Corrected Artist", reload.Value!.Artist);
    }

    [Fact]
    public async Task RenameArtist_LeavesNonMatchingFilesAlone()
    {
        using var media = MediaFixtures.Copy("sample.flac");   // Artist=TestArtist

        var changed = await _reconciler.RenameArtistAsync([media.Path], "SomeoneElse", "Corrected");
        Assert.Equal(0, changed);

        var reload = await new MediaFileService().LoadAsync(media.Path);
        Assert.Equal("TestArtist", reload.Value!.Artist);   // unchanged
    }
}
