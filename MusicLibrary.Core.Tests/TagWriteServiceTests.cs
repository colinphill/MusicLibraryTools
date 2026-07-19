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

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.ogg")]
    [InlineData("sample_alac.m4a")]
    [InlineData("sample.wv")]
    public async Task RoundTrips_ArbitraryUserString(string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);

        BatchWriteResult write = await _writer.ApplyAsync(
            [media.Path],
            [TagEdit.UserString("CUSTOM_APP_NOTE", "Remember this")]);

        Assert.Equal(1, write.SavedCount);
        OperationResult<MediaFileModel> reload = await _reader.LoadDirectAsync(
            media.Path, includeArtwork: false);
        Assert.True(reload.Success, reload.Error);
        Assert.Contains(reload.Value!.TextFields, field =>
            field.Key.Equals("CUSTOM_APP_NOTE", StringComparison.OrdinalIgnoreCase) &&
            field.Value == "Remember this");

        BatchWriteResult remove = await _writer.ApplyAsync(
            [media.Path],
            [TagEdit.UserString("custom_app_note", null)]);
        Assert.Equal(1, remove.SavedCount);
        reload = await _reader.LoadDirectAsync(media.Path, includeArtwork: false);
        Assert.DoesNotContain(reload.Value!.TextFields, field =>
            field.Key.Equals("CUSTOM_APP_NOTE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AppliesId3VersionUpgradeTogetherWithMetadataEdits()
    {
        using var media = MediaFixtures.Copy("sample.mp3");
        var source = Assert.IsType<MP3File>(MediaFile.GetFile(media.Path));
        source.ChangeVersion(ID3v2Version.V22);
        source.Save();

        BatchWriteResult result = await _writer.ApplyAsync(
            [media.Path],
            [
                new TagEdit(
                    TagFields.NullField,
                    "ID3v2.3",
                    ID3v2Version.V23),
                new TagEdit(TagFields.Title, "Upgraded title"),
            ]);

        Assert.Equal(1, result.SavedCount);
        var reopened = Assert.IsType<MP3File>(MediaFile.GetFile(media.Path));
        Assert.Equal(3, reopened.Version);
        Assert.Equal("Upgraded title", reopened.Title);
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

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.ogg")]
    [InlineData("sample_alac.m4a")]
    [InlineData("sample_aac.m4a")]
    [InlineData("sample.wv")]
    public async Task RemovingField_ClearsIt(string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);

        // Baseline fixture has Genre=Rock; a null value removes it.
        var result = await _writer.ApplyAsync([media.Path], [new TagEdit(TagFields.Genre, null)]);
        Assert.Equal(1, result.SavedCount);

        var reload = await _reader.LoadAsync(media.Path);
        var genre = reload.Value!.KnownFields.FirstOrDefault(f => f.Field == TagFields.Genre);
        Assert.Null(genre); // no Genre entry remains
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.ogg")]
    [InlineData("sample_alac.m4a")]
    [InlineData("sample_aac.m4a")]
    [InlineData("sample.wv")]
    public async Task RemovingAllKnownFields_ClearsThem(string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);
        OperationResult<MediaFileModel> initial = await _reader.LoadDirectAsync(
            media.Path, includeArtwork: false);
        TagEdit[] removals = initial.Value!.KnownFields
            .Select(field => field.Field)
            .Distinct()
            .Select(field => new TagEdit(field, null))
            .ToArray();

        BatchWriteResult result = await _writer.ApplyAsync([media.Path], removals);

        Assert.Equal(1, result.SavedCount);
        OperationResult<MediaFileModel> reload = await _reader.LoadDirectAsync(
            media.Path, includeArtwork: false);
        Assert.Empty(reload.Value!.KnownFields);
    }

    [Theory]
    [InlineData("sample_alac.m4a")]
    [InlineData("sample_aac.m4a")]
    public async Task RemovingMp4TrackAndDiscNumbers_PreservesTotalsWithoutZeroFields(
        string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);
        BatchWriteResult setup = await _writer.ApplyAsync(
            [media.Path],
            [
                new TagEdit(TagFields.TrackNumber, "3"),
                new TagEdit(TagFields.TotalTracks, "12"),
                new TagEdit(TagFields.DiscNumber, "1"),
                new TagEdit(TagFields.TotalDiscs, "2"),
            ]);
        Assert.Equal(1, setup.SavedCount);

        BatchWriteResult remove = await _writer.ApplyAsync(
            [media.Path],
            [
                new TagEdit(TagFields.TrackNumber, null),
                new TagEdit(TagFields.DiscNumber, null),
            ]);

        Assert.Equal(1, remove.SavedCount);
        OperationResult<MediaFileModel> reload = await _reader.LoadDirectAsync(
            media.Path, includeArtwork: false);
        Assert.DoesNotContain(reload.Value!.KnownFields,
            field => field.Field is TagFields.TrackNumber or TagFields.DiscNumber);
        Assert.Contains(reload.Value.KnownFields,
            field => field.Field == TagFields.TotalTracks && field.Value == "12");
        Assert.Contains(reload.Value.KnownFields,
            field => field.Field == TagFields.TotalDiscs && field.Value == "2");
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

    [Fact]
    public async Task SavedFile_UsesParsedObjectForCacheRefresh()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var reindex = new ParsedRecordingReindexService();
        var writer = new TagWriteService(reindex);

        var result = await writer.ApplyAsync(
            [media.Path], [new TagEdit(TagFields.Title, "No second parse")]);

        Assert.Equal(1, result.SavedCount);
        Assert.Equal(1, reindex.ParsedCalls);
        Assert.Equal(0, reindex.ReparseCalls);
        Assert.NotNull(reindex.SavedFile);
    }

    [Fact]
    public async Task ExistingValue_SkipsPhysicalWriteAndReindex()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var reindex = new RecordingReindexService();
        var writer = new TagWriteService(reindex);

        var result = await writer.ApplyAsync(
            [media.Path], [new TagEdit(TagFields.Title, "TestTitle")]);

        Assert.Equal(0, result.SavedCount);
        Assert.Equal(WriteOutcome.Skipped, Assert.Single(result.Files).Outcome);
        Assert.Empty(reindex.Paths);
    }

    [Fact]
    public async Task Batch_UsesBatchedParsedCacheRefresh()
    {
        using var first = MediaFixtures.Copy("sample.flac");
        using var second = MediaFixtures.Copy("sample.mp3");
        var reindex = new BatchRecordingReindexService();
        var writer = new TagWriteService(reindex, maxParallelism: 2);

        var result = await writer.ApplyAsync(
            [first.Path, second.Path], [new TagEdit(TagFields.Title, "Batched refresh")]);

        Assert.Equal(2, result.SavedCount);
        Assert.Equal(0, reindex.SingleCalls);
        Assert.Equal(2, reindex.Batches.SelectMany(batch => batch).Count());
        Assert.All(reindex.Batches.SelectMany(batch => batch), item => Assert.NotNull(item.File));
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

    private sealed class ParsedRecordingReindexService : IReindexService
    {
        public int ReparseCalls { get; private set; }
        public int ParsedCalls { get; private set; }
        public IMediaFile? SavedFile { get; private set; }

        public Task ReindexFileAsync(string path, CancellationToken ct = default)
        {
            ReparseCalls++;
            return Task.CompletedTask;
        }

        public Task ReindexFileAsync(string path, IMediaFile savedFile, CancellationToken ct = default)
        {
            ParsedCalls++;
            SavedFile = savedFile;
            return Task.CompletedTask;
        }
    }

    private sealed class BatchRecordingReindexService : IReindexService
    {
        public int SingleCalls { get; private set; }
        public List<IReadOnlyList<(string Path, IMediaFile File)>> Batches { get; } = [];

        public Task ReindexFileAsync(string path, CancellationToken ct = default)
        {
            SingleCalls++;
            return Task.CompletedTask;
        }

        public Task ReindexFilesAsync(
            IReadOnlyList<(string Path, IMediaFile File)> files,
            CancellationToken ct = default)
        {
            Batches.Add(files);
            return Task.CompletedTask;
        }
    }
}
