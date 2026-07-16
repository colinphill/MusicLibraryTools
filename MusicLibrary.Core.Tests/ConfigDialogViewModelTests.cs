using MusicLibrary.App.Services;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Models;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ConfigDialogViewModelTests
{
    [Fact]
    public async Task ImportLegacyIngestConfigurationAssignsIndexTargetRolesAndBehavior()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ingest-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string legacyPath = Path.Combine(root, "ingest.xml");
            new IngestMusicConfiguration
            {
                FfmpegPath = "custom-ffmpeg",
                ItunesLibraryPath = Path.Combine(root, "library.itl"),
                AacDestination = Path.Combine(root, "aac"),
                CdDestination = Path.Combine(root, "cd"),
                PairedCdDestination = Path.Combine(root, "paired"),
                HighResolutionDestination = Path.Combine(root, "hires"),
                AacEncoder = "aac-encoder",
                AacBitrateKbps = 320,
                DeleteSourcesAfterIngest = true,
                RemoveNonMusicAfterIngest = true,
            }.Save(legacyPath);
            var viewModel = new ConfigDialogViewModel(new ImportFileDialogs(legacyPath), null);

            await viewModel.ImportIngestConfigurationCommand.ExecuteAsync(null);

            Assert.Equal("custom-ffmpeg", viewModel.FfmpegPath);
            Assert.Equal("aac-encoder", viewModel.AacEncoder);
            Assert.Equal(320, viewModel.AacBitrateKbps);
            Assert.True(viewModel.DeleteSourcesAfterIngest);
            Assert.True(viewModel.RemoveNonMusicAfterIngest);
            Assert.Equal(Path.Combine(root, "cd"), Target(viewModel, LibraryIngestRole.Cd));
            Assert.Equal(Path.Combine(root, "paired"), Target(viewModel, LibraryIngestRole.CdFallback));
            Assert.Equal(Path.Combine(root, "hires"), Target(viewModel, LibraryIngestRole.HiRes));
            Assert.Equal(Path.Combine(root, "aac"), Target(viewModel, LibraryIngestRole.AacFallback));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Target(ConfigDialogViewModel viewModel, LibraryIngestRole role) =>
        Assert.Single(viewModel.IndexTargets, target => target.IngestRole == role).Target;

    private sealed class ImportFileDialogs(string path) : IFileDialogService
    {
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(path);

        public Task<string?> PickFolderAsync(string title) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(
            string title,
            string? suggestedName = null,
            string? defaultExtension = null,
            IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(null);
    }
}
