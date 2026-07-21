using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class SelectionInspectorFailureTests
{
    [Fact]
    public async Task Partial_tag_failure_keeps_proposed_edits_ready_to_retry()
    {
        MediaFileModel[] models =
        [
            Model(@"C:\one.flac", "One"),
            Model(@"C:\two.flac", "Two"),
        ];
        var inspector = Create(models, new PartialTagWriter(), new PartialArtworkService());
        await inspector.LoadAsync(new SelectionContext(models.Select(model => model.Path).ToArray()));
        EditableTagField artist = inspector.Fields.Single(field => field.Field == TagFields.Artist);
        artist.Value = "Canonical artist";

        await inspector.SaveTagsCommand.ExecuteAsync(null);

        Assert.True(artist.IsModified);
        Assert.Equal("Canonical artist", artist.Value);
        Assert.True(inspector.HasUnsavedChanges);
        Assert.True(inspector.SaveTagsCommand.CanExecute(null));
        Assert.Contains("remain ready to retry", inspector.StatusMessage);
    }

    [Fact]
    public async Task Partial_artwork_failure_keeps_prepared_set_ready_to_retry()
    {
        MediaFileModel[] models =
        [
            Model(@"C:\one.flac", "One"),
            Model(@"C:\two.flac", "Two"),
        ];
        var artwork = new PartialArtworkService();
        var inspector = Create(models, new PartialTagWriter(), artwork,
            new FakeFilePicker(@"C:\cover.jpg"));
        await inspector.LoadAsync(new SelectionContext(models.Select(model => model.Path).ToArray()));
        await inspector.AddArtworkCommand.ExecuteAsync(null);

        await inspector.SaveArtworkSetCommand.ExecuteAsync(null);

        Assert.Equal(2, artwork.SaveCallCount);
        Assert.Single(inspector.ArtworkItems);
        Assert.True(inspector.HasPendingArtworkChanges);
        Assert.True(inspector.HasUnsavedChanges);
        Assert.True(inspector.SaveArtworkSetCommand.CanExecute(null));
        Assert.Contains("remain ready to retry", inspector.StatusMessage);
    }

    private static SelectionInspectorViewModel Create(
        IReadOnlyList<MediaFileModel> models,
        ITagWriteService tags,
        IArtworkService artwork,
        IFilePickerService? files = null) =>
        new(
            new FakeMediaService(models.ToArray()),
            new FakeLibrary([]),
            tags,
            artwork,
            files ?? new FakeFilePicker(),
            new FakeDialogs(),
            new FakeFieldsEditor(),
            new FakeThumbnails(),
            new AppActivityService());

    private static MediaFileModel Model(string path, string title) => new()
    {
        Path = path,
        Title = title,
        Artist = "Artist",
        IsWritable = true,
        KnownFields =
        [
            new TagFieldValue(TagFields.Title, title),
            new TagFieldValue(TagFields.Artist, "Artist"),
        ],
    };

    private sealed class PartialTagWriter : ITagWriteService
    {
        public Task<BatchWriteResult> ApplyAsync(
            IReadOnlyList<string> paths,
            IReadOnlyList<TagEdit> edits,
            IProgress<int>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(new BatchWriteResult(paths.Select((path, index) => new FileWriteResult
            {
                Path = path,
                Outcome = index == 0 ? WriteOutcome.Saved : WriteOutcome.Failed,
                Error = index == 0 ? null : "Fixture write failure",
            }).ToArray()));
    }

    private sealed class PartialArtworkService : IArtworkService
    {
        public int SaveCallCount { get; private set; }

        public bool SupportsWrite(string musicPath) => true;

        public Task<ArtworkOpResult> SetCoverFromFileAsync(
            string musicPath, string imagePath, int maxDimension = 0,
            CancellationToken ct = default) => Success();

        public Task<ArtworkOpResult> ScrubAsync(
            string musicPath, int maxDimension, int quality = 90,
            CancellationToken ct = default) => Success();

        public Task<ArtworkOpResult> RemoveAsync(
            string musicPath, CancellationToken ct = default) => Success();

        public Task<PreparedImage?> PrepareFromFileAsync(
            string imagePath, int maxDimension = 0, CancellationToken ct = default) =>
            Task.FromResult<PreparedImage?>(new PreparedImage([1, 2, 3], "image/jpeg", 600, 600));

        public Task<PreparedImage?> PrepareFromBytesAsync(
            byte[] data, int maxDimension = 0, int quality = 90,
            CancellationToken ct = default) =>
            Task.FromResult<PreparedImage?>(new PreparedImage(data, "image/jpeg", 600, 600));

        public Task<ArtworkOpResult> SaveImagesAsync(
            string musicPath, IReadOnlyList<ArtworkInput> images,
            CancellationToken ct = default)
        {
            SaveCallCount++;
            return SaveCallCount == 1
                ? Success()
                : Task.FromResult(new ArtworkOpResult
                {
                    Success = false,
                    Error = "Fixture artwork failure",
                });
        }

        private static Task<ArtworkOpResult> Success() =>
            Task.FromResult(new ArtworkOpResult { Success = true });
    }
}
