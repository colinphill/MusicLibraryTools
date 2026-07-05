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
