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

    [Fact]
    public async Task SavedFile_IsReturnedAsSaved_WhenCacheRefreshFails()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var reindex = new ThrowingReindexService();
        var writer = new TagWriteService(reindex);

        var result = await writer.ApplyAsync(
            [media.Path], [new TagEdit(TagFields.Title, "Committed")]);

        var file = Assert.Single(result.Files);
        Assert.Equal(WriteOutcome.Saved, file.Outcome);
        Assert.Contains("cache unavailable", file.CacheError);
        Assert.False(reindex.ReceivedToken.CanBeCanceled);
        Assert.Equal("Committed", (await _reader.LoadAsync(media.Path)).Value!.Title);
    }

    [Fact]
    public async Task CancellationAfterDiskCommit_DoesNotSkipCacheRefreshOrHideSuccess()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        using var cts = new CancellationTokenSource();
        var reindex = new CancelDuringReindexService(cts);
        var writer = new TagWriteService(reindex);

        var result = await writer.ApplyAsync(
            [media.Path], [new TagEdit(TagFields.Title, "Committed")], ct: cts.Token);

        Assert.Equal(1, result.SavedCount);
        Assert.True(reindex.Called);
        Assert.False(reindex.ReceivedToken.CanBeCanceled);
    }

    [Fact]
    public async Task SamePathMutation_WaitsForExistingLease()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var coordinator = new FileMutationCoordinator();
        var lease = await coordinator.AcquireAsync(media.Path);
        try
        {
            var writer = new TagWriteService(mutations: coordinator);
            var pending = writer.ApplyAsync(
                [media.Path], [new TagEdit(TagFields.Title, "Serialized")]);

            Assert.NotSame(pending, await Task.WhenAny(pending, Task.Delay(100)));
            lease.Dispose();
            await pending;
        }
        finally
        {
            lease.Dispose();
        }

        Assert.Equal("Serialized", (await _reader.LoadAsync(media.Path)).Value!.Title);
    }

    [Fact]
    public async Task Batch_ReindexesEverySuccessfullyChangedFile()
    {
        using var first = MediaFixtures.Copy("sample.flac");
        using var second = MediaFixtures.Copy("sample.mp3");
        var reindex = new RecordingReindexService();
        var writer = new TagWriteService(reindex);

        var result = await writer.ApplyAsync(
            [first.Path, second.Path], [new TagEdit(TagFields.Album, "Reindexed")]);

        Assert.Equal(2, result.SavedCount);
        Assert.Equal(new[] { first.Path, second.Path }, reindex.Paths);
        Assert.All(reindex.Tokens, token => Assert.False(token.CanBeCanceled));
    }

    private sealed class ThrowingReindexService : IReindexService
    {
        public CancellationToken ReceivedToken { get; private set; }

        public Task ReindexFileAsync(string path, CancellationToken ct = default)
        {
            ReceivedToken = ct;
            throw new InvalidOperationException("cache unavailable");
        }
    }

    private sealed class CancelDuringReindexService(CancellationTokenSource cts) : IReindexService
    {
        public bool Called { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }

        public Task ReindexFileAsync(string path, CancellationToken ct = default)
        {
            Called = true;
            ReceivedToken = ct;
            cts.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReindexService : IReindexService
    {
        public List<string> Paths { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Task ReindexFileAsync(string path, CancellationToken ct = default)
        {
            Paths.Add(path);
            Tokens.Add(ct);
            return Task.CompletedTask;
        }
    }
}
