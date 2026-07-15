using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class AlbumMetadataMatrixTests
{
    [Fact]
    public void CleanAlbumProducesNoMatrix()
    {
        var records = new[]
        {
            Track(Path.Combine("Artist", "Album", "01.flac"), 1, 2, "One"),
            Track(Path.Combine("Artist", "Album", "02.flac"), 2, 2, "Two"),
        };

        Assert.Empty(AlbumMetadataMatrixBuilder.Build(records));
    }

    [Fact]
    public void MatrixHighlightsDuplicateNumbersAndDisagreeingTotals()
    {
        var records = new[]
        {
            Track(Path.Combine("Artist", "Album", "one.flac"), 1, 2, "One"),
            Track(Path.Combine("Artist", "Album", "two.flac"), 1, 3, "Two"),
        };

        var matrix = Assert.Single(AlbumMetadataMatrixBuilder.Build(records));

        Assert.All(matrix.Rows, row => Assert.True(row.TrackNumber.IsInconsistent));
        Assert.All(matrix.Rows, row => Assert.True(row.TrackTotal.IsInconsistent));
        Assert.Contains("duplicated", matrix.Rows[0].TrackNumber.Reason);
        Assert.Contains("disagree", matrix.Rows[0].TrackTotal.Reason);
    }

    [Fact]
    public void MatrixHighlightsExactAlbumAndAlbumArtistVariantsButNotCompilationArtists()
    {
        string folder = Path.Combine("Various", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01.flac"), 1, 2, "One") with
            {
                Artist = "First Artist", AlbumArtist = "Various Artists", Album = "Album",
            },
            Track(Path.Combine(folder, "02.flac"), 2, 2, "Two") with
            {
                Artist = "Second Artist", AlbumArtist = "various artists", Album = "album",
            },
        };

        var matrix = Assert.Single(AlbumMetadataMatrixBuilder.Build(records));

        Assert.All(matrix.Rows, row => Assert.True(row.AlbumArtist.IsInconsistent));
        Assert.All(matrix.Rows, row => Assert.True(row.Album.IsInconsistent));
        Assert.All(matrix.Rows, row => Assert.False(row.Artist.IsInconsistent));
    }

    [Fact]
    public void ExplicitDiscFoldersAreOneMatrixAndExposeMultiDiscNaming()
    {
        string album = Path.Combine("Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(album, "Disc 1", "01.flac"), 1, 1, "One") with
            {
                Album = "Album (Disc 1)", DiscNumber = 1, DiscTotal = 2,
            },
            Track(Path.Combine(album, "Disc 2", "01.flac"), 1, 1, "Two") with
            {
                Album = "Album (Disc 2)", DiscNumber = 2, DiscTotal = 2,
            },
        };

        var matrix = Assert.Single(AlbumMetadataMatrixBuilder.Build(records));

        Assert.Equal(2, matrix.Rows.Count);
        Assert.Equal(album, matrix.Root);
        Assert.All(matrix.Rows, row => Assert.True(row.Album.IsInconsistent));
        Assert.All(matrix.Rows, row => Assert.False(row.DiscNumber.IsInconsistent));
        Assert.All(matrix.Rows, row => Assert.False(row.DiscTotal.IsInconsistent));
    }

    [Fact]
    public void MultiDiscFoldersHighlightMissingDiscTagsAndTotals()
    {
        string album = Path.Combine("Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(album, "CD 1", "01.flac"), 1, 1, "One"),
            Track(Path.Combine(album, "CD 2", "01.flac"), 1, 1, "Two"),
        };

        var matrix = Assert.Single(AlbumMetadataMatrixBuilder.Build(records));

        Assert.All(matrix.Rows, row => Assert.True(row.DiscNumber.IsInconsistent));
        Assert.All(matrix.Rows, row => Assert.True(row.DiscTotal.IsInconsistent));
    }

    [Fact]
    public void MatrixHighlightsMissingAndDifferingReleaseDates()
    {
        string folder = Path.Combine("Artist", "Album");
        var records = new[]
        {
            Track(Path.Combine(folder, "01.flac"), 1, 3, "One") with { ReleaseDate = "2020" },
            Track(Path.Combine(folder, "02.flac"), 2, 3, "Two") with { ReleaseDate = "2021" },
            Track(Path.Combine(folder, "03.flac"), 3, 3, "Three"),
        };

        var matrix = Assert.Single(AlbumMetadataMatrixBuilder.Build(records));

        Assert.All(matrix.Rows, row => Assert.True(row.ReleaseDate.IsInconsistent));
        Assert.Contains("missing", matrix.Rows[2].ReleaseDate.Reason);
        Assert.Contains("differ", matrix.Rows[0].ReleaseDate.Reason);
    }

    [Fact]
    public void MissingExplicitAlbumArtistIsHighlightedEvenWhenFallbackValueExists()
    {
        var record = Track(Path.Combine("Artist", "Album", "01.flac"), 1, 1, "One") with
        {
            HasAlbumArtist = false,
            AlbumArtist = "Artist",
        };

        var row = Assert.Single(Assert.Single(AlbumMetadataMatrixBuilder.Build([record])).Rows);

        Assert.True(row.AlbumArtist.IsInconsistent);
        Assert.Equal("(missing)", row.AlbumArtist.Display);
    }

    private static TrackRecord Track(string path, int track, int total, string title) => new()
    {
        Path = path,
        Artist = "Artist",
        AlbumArtist = "Artist",
        HasAlbumArtist = true,
        Album = "Album",
        StrippedAlbum = "Album",
        Title = title,
        TrackNumber = track,
        TrackTotal = total,
    };
}
