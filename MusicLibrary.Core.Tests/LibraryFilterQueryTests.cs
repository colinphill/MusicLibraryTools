using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class LibraryFilterQueryTests
{
    [Fact]
    public void PlainTextPreservesWholePatternBehavior()
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create("dark side", FilterMode.Substring);
        DetailsRow row = Row(title: "Speak to Me", artist: "Pink Floyd",
            album: "The Dark Side of the Moon", codec: "FLAC");

        Assert.False(query.IsAdvanced);
        Assert.True(query.IsMatch(row, row.SearchText));
        Assert.False(query.IsMatch(row, "dark\nside"));
    }

    [Fact]
    public void ColumnTermsSupportBooleanOperatorsAndParentheses()
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create(
            "Artist:\"Miles Davis\" AND (Codec:FLAC OR Codec:ALAC) AND NOT Album:Live",
            FilterMode.Substring);

        Assert.True(query.IsValid);
        Assert.True(query.IsAdvanced);
        Assert.True(query.IsMatch(
            Row("So What", "Miles Davis", "Kind of Blue", "FLAC"), ""));
        Assert.False(query.IsMatch(
            Row("So What", "Miles Davis", "Live in Europe", "FLAC"), ""));
        Assert.False(query.IsMatch(
            Row("So What", "Miles Davis", "Kind of Blue", "MP3"), ""));
    }

    [Fact]
    public void BooleanOperatorsAreCaseInsensitive()
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create(
            "Artist:Miles aNd (Codec:FLAC oR Codec:ALAC) AND nOt Album:Live",
            FilterMode.Substring);

        Assert.True(query.IsValid);
        Assert.True(query.IsAdvanced);
        Assert.True(query.IsMatch(
            Row("So What", "Miles Davis", "Kind of Blue", "FLAC"), ""));
        Assert.False(query.IsMatch(
            Row("So What", "Miles Davis", "Live in Europe", "ALAC"), ""));
    }

    [Theory]
    [InlineData("\"and\"")]
    [InlineData("\"AND\"")]
    public void QuotedBooleanWordIsTreatedAsLiteralText(string text)
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create(text, FilterMode.Substring);
        DetailsRow row = Row("Rock and Roll", "Artist", "Album", "FLAC");

        Assert.True(query.IsValid);
        Assert.True(query.IsAdvanced);
        Assert.True(query.IsMatch(row, row.SearchText));
        Assert.False(query.IsMatch(row, "Rock Roll"));
    }

    [Fact]
    public void AdjacentTermsImplyAndAndColumnAliasesIgnorePunctuation()
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create(
            "album-artist:Coltrane SampleRate:44,100", FilterMode.Substring);
        DetailsRow row = Row("Naima", "John Coltrane", "Giant Steps", "FLAC",
            albumArtist: "John Coltrane", sampleRate: 44_100);

        Assert.True(query.IsMatch(row, ""));
    }

    [Fact]
    public void SelectedModeAppliesToEveryTerm()
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create(
            "Artist:Miles* AND Codec:FL?C", FilterMode.Glob);

        Assert.True(query.IsMatch(
            Row("Blue in Green", "Miles Davis", "Kind of Blue", "FLAC"), ""));
        Assert.False(query.IsMatch(
            Row("Blue in Green", "Bill Evans", "Kind of Blue", "FLAC"), ""));
    }

    [Fact]
    public void AndHasHigherPrecedenceThanOr()
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create(
            "Artist:Miles OR Artist:Coltrane AND Codec:MP3", FilterMode.Substring);

        Assert.True(query.IsMatch(Row("One", "Miles Davis", "Album", "FLAC"), ""));
        Assert.True(query.IsMatch(Row("Two", "John Coltrane", "Album", "MP3"), ""));
        Assert.False(query.IsMatch(Row("Three", "John Coltrane", "Album", "FLAC"), ""));
    }

    [Fact]
    public void DriveQualifiedPlainTextDoesNotBecomeAColumnExpression()
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create(
            @"Z:\iTunes\FLAC", FilterMode.Substring);

        Assert.False(query.IsAdvanced);
        Assert.True(query.IsMatch(Row("One", "Artist", "Album", "FLAC"),
            @"Z:\iTunes\FLAC\Artist\Album\One.flac"));
    }

    [Theory]
    [InlineData("Artist:Miles AND (Codec:FLAC OR Codec:ALAC")]
    [InlineData("Artist:")]
    [InlineData("Artist:Miles OR")]
    [InlineData("Artst:Miles")]
    public void MalformedQueriesAreInvalid(string text)
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create(text, FilterMode.Substring);

        Assert.True(query.IsAdvanced);
        Assert.False(query.IsValid);
        Assert.False(query.IsMatch(Row("x", "Miles", "x", "FLAC"), ""));
        Assert.False(string.IsNullOrWhiteSpace(query.Error));
    }

    [Fact]
    public void InvalidRegexInOneTermInvalidatesTheQuery()
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create(
            "Artist:\"([broken\" AND Codec:FLAC", FilterMode.Regex);

        Assert.False(query.IsValid);
        Assert.Contains("pattern", query.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private static DetailsRow Row(string title, string artist, string album, string codec,
        string? albumArtist = null, int sampleRate = 0)
    {
        var row = new DetailsRow(new TrackRecord
        {
            Path = title + ".flac",
            Title = title,
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = album,
            CodecName = codec,
            SampleRate = (uint)sampleRate,
        });
        row.RebuildSearchText(DetailsColumns.All.Select(column => column.Key).ToArray());
        return row;
    }
}
