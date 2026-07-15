using MusicFileUtilities;
using MusicLibrary.App.Services;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ArtworkViewModelTests
{
    [Fact]
    public async Task CanonicalSelectionPreviewsSavingsThenWritesOneImageToEveryTarget()
    {
        var media = new StubMedia(new Dictionary<string, ArtworkModel>
        {
            ["one.flac"] = Image("first", 1_000, 1),
            ["two.flac"] = Image("second", 2_000, 2),
        });
        var artwork = new StubArtwork();
        var viewModel = new ArtworkViewModel(artwork, media, new StubFiles());
        await viewModel.SetTargetsAsync(["one.flac", "two.flac"]);

        Assert.Equal(2, viewModel.Images.Count);
        viewModel.SelectCanonicalCommand.Execute(viewModel.Images[0]);
        await viewModel.PreviewNormalizationCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasNormalizationPreview);
        Assert.Contains("save", viewModel.NormalizationPreviewText, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.ApplyNormalizationCommand.CanExecute(null));

        await viewModel.ApplyNormalizationCommand.ExecuteAsync(null);

        Assert.Equal(2, artwork.Saves.Count);
        Assert.All(artwork.Saves, save => Assert.Single(save.Images));
        Assert.All(artwork.Saves, save => Assert.Equal(100, save.Images[0].Data.Length));
    }

    private static ArtworkModel Image(string hash, int size, byte value) => new()
    {
        Hash = hash,
        ImageType = "image/jpeg",
        Category = "FrontCover",
        Width = 1000,
        Height = 1000,
        Size = size,
        Data = [value],
    };

    private sealed class StubMedia(IReadOnlyDictionary<string, ArtworkModel> images) : IMediaFileService
    {
        public Task<OperationResult<MediaFileModel>> LoadAsync(string path, CancellationToken ct = default) =>
            LoadAsync(path, true, ct);
        public Task<OperationResult<MediaFileModel>> LoadAsync(string path, bool includeArtwork,
            CancellationToken ct = default) => Task.FromResult(OperationResult<MediaFileModel>.Ok(new()
            {
                Path = path,
                Artwork = includeArtwork ? [images[path]] : [],
            }));
    }

    private sealed class StubArtwork : IArtworkService
    {
        public List<(string Path, IReadOnlyList<ArtworkInput> Images)> Saves { get; } = [];
        public bool SupportsWrite(string musicPath) => true;
        public Task<PreparedImage?> PrepareFromBytesAsync(byte[] data, int maxDimension = 0,
            int quality = 90, CancellationToken ct = default) =>
            Task.FromResult<PreparedImage?>(new(new byte[100], "image/jpeg", 500, 500));
        public Task<ArtworkOpResult> SaveImagesAsync(string musicPath, IReadOnlyList<ArtworkInput> images,
            CancellationToken ct = default)
        {
            Saves.Add((musicPath, images));
            return Task.FromResult(new ArtworkOpResult { Success = true });
        }
        public Task<ArtworkOpResult> SetCoverFromFileAsync(string musicPath, string imagePath,
            int maxDimension = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArtworkOpResult> ScrubAsync(string musicPath, int maxDimension, int quality = 90,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArtworkOpResult> RemoveAsync(string musicPath, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PreparedImage?> PrepareFromFileAsync(string imagePath, int maxDimension = 0,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubFiles : IFileDialogService
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFileAsync(string title, string? suggestedName = null,
            string? defaultExtension = null, IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(null);
    }
}
