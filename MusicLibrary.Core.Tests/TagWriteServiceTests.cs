using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class TagWriteServiceTests
{
    private readonly TagWriteService _writer = new();
    private readonly MediaFileService _reader = new();

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample_alac.m4a")]
    [InlineData("sample.wv")]
    public async Task RoundTrips_TitleAndAlbumArtist(string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);

        var edits = new[]
        {
            new TagEdit(TagFields.Title, "NewTitle"),
            new TagEdit(TagFields.AlbumArtist, "NewAlbumArtist"),
        };

        var result = await _writer.ApplyAsync([media.Path], edits);

        Assert.Equal(1, result.SavedCount);
        Assert.Equal(0, result.FailedCount);

        var reload = await _reader.LoadAsync(media.Path);
        Assert.True(reload.Success, reload.Error);
        Assert.Equal("NewTitle", reload.Value!.Title);
        Assert.Equal("NewAlbumArtist", reload.Value!.AlbumArtist);
    }

    [Fact]
    public async Task RoundTrips_Ogg_ViaVorbisCommentsFallback()
    {
        // OggVorbisFile does not implement IMetadataWriter; the service must fall back to the
        // VorbisComments base and still save.
        using var media = MediaFixtures.Copy("sample.ogg");

        var result = await _writer.ApplyAsync([media.Path], [new TagEdit(TagFields.Title, "OggTitle")]);

        Assert.Equal(1, result.SavedCount);

        var reload = await _reader.LoadAsync(media.Path);
        Assert.True(reload.Success, reload.Error);
        Assert.Equal("OggTitle", reload.Value!.Title);
    }

    [Fact]
    public async Task Batch_ContinuesPastAFailingFile()
    {
        using var good = MediaFixtures.Copy("sample.flac");
        var missing = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N") + ".flac");

        var result = await _writer.ApplyAsync(
            [good.Path, missing],
            [new TagEdit(TagFields.Album, "BatchAlbum")]);

        Assert.Equal(1, result.SavedCount);
        Assert.Equal(1, result.FailedCount);

        var reload = await _reader.LoadAsync(good.Path);
        Assert.Equal("BatchAlbum", reload.Value!.Album);
    }

    [Fact]
    public async Task Batch_AppliesSameEditToEveryFile()
    {
        using var a = MediaFixtures.Copy("sample.flac");
        using var b = MediaFixtures.Copy("sample.mp3");

        var result = await _writer.ApplyAsync(
            [a.Path, b.Path],
            [new TagEdit(TagFields.AlbumArtist, "SharedAA")]);

        Assert.Equal(2, result.SavedCount);

        foreach (var path in new[] { a.Path, b.Path })
        {
            var reload = await _reader.LoadAsync(path);
            Assert.Equal("SharedAA", reload.Value!.AlbumArtist);
        }
    }

    [Fact]
    public async Task RemovingField_ClearsIt()
    {
        using var media = MediaFixtures.Copy("sample.flac");

        // Baseline fixture has Genre=Rock; a null value removes it.
        var result = await _writer.ApplyAsync([media.Path], [new TagEdit(TagFields.Genre, null)]);
        Assert.Equal(1, result.SavedCount);

        var reload = await _reader.LoadAsync(media.Path);
        var genre = reload.Value!.KnownFields.FirstOrDefault(f => f.Field == TagFields.Genre);
        Assert.Null(genre); // no Genre entry remains
    }
}
