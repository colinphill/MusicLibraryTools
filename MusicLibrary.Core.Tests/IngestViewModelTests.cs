using MusicLibrary.App.Services;
using MusicLibrary.App.ViewModels;
using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class IngestViewModelTests
{
    [Fact]
    public async Task PresetsRecentDropsAndPreflightPersistAsWorkflowState()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ingest-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string dropped = Directory.CreateDirectory(Path.Combine(root, "dropped")).FullName;
        string config = Path.Combine(root, "ingest.xml");
        string state = Path.Combine(root, "settings.json");
        try
        {
            var settings = new AppSettings(state);
            var preflight = new StubPreflight();
            var viewModel = Create(settings, preflight);
            viewModel.SourceDirectory = source;
            viewModel.ConfigurationPath = config;
            viewModel.PresetName = "Downloads";

            viewModel.SavePresetCommand.Execute(null);
            viewModel.SetDroppedSource(dropped);
            await viewModel.PreflightCommand.ExecuteAsync(null);

            Assert.Equal("Downloads", Assert.Single(viewModel.Presets).Name);
            Assert.Equal(dropped, viewModel.RecentSources[0]);
            Assert.True(viewModel.HasPreflightChecks);
            Assert.Equal(1, preflight.Calls);

            var restored = Create(new AppSettings(state), new StubPreflight());
            var preset = Assert.Single(restored.Presets);
            restored.SelectedPreset = preset;

            Assert.Equal(source, restored.SourceDirectory);
            Assert.Equal(config, restored.ConfigurationPath);
            Assert.Contains(dropped, restored.RecentSources);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IngestViewModel Create(IAppSettings settings, IIngestPreflightService preflight) =>
        new(new StubIngest(), new StubFiles(), new StubDialogs(), settings, new StubLibrary(), preflight);

    private sealed class StubPreflight : IIngestPreflightService
    {
        public int Calls { get; private set; }
        public Task<IngestPreflightResult> CheckAsync(IngestRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new IngestPreflightResult([
                new("Configuration", IngestPreflightSeverity.Pass, "Ready"),
            ]));
        }
    }

    private sealed class StubIngest : IIngestMusicService
    {
        public Task<IngestPlan> PreviewAsync(IngestRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IngestResult> ApplyAsync(IngestPlan plan,
            IReadOnlyList<IngestApprovalDecision> approvals, IProgress<IngestProgress>? progress = null,
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

    private sealed class StubDialogs : IDialogService
    {
        public Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths) => Task.FromResult(false);
        public Task<string?> ShowConfigEditorAsync(string? existingPath) => Task.FromResult<string?>(null);
        public Task<string?> ShowIngestConfigEditorAsync(string? existingPath) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmCdDerivationAsync(IngestApprovalItem item) => Task.FromResult(false);
        public Task<bool> ConfirmRestoreAsync(OperationRestorePlan plan) => Task.FromResult(false);
        public Task<bool> ConfirmPurgeAsync(OperationPurgePlan plan) => Task.FromResult(false);
    }

    private sealed class StubLibrary : ILibraryService
    {
        public bool IsReady => false;
        public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LibrarySnapshot> BuildSnapshotAsync(LibraryGrouping grouping = LibraryGrouping.AlbumArtist,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<FileDetails?> GetFileDetailsAsync(string path, bool includeArtwork,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(IReadOnlyList<string> paths,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetImageSignaturesAsync(IReadOnlyList<string> paths,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
