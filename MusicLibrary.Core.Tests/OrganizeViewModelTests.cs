using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class OrganizeViewModelTests
{
    [Fact]
    public async Task ApplyRequiresAMoveCountAndRecoverySummaryConfirmation()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "organize-view-model-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string configPath = Path.Combine(root, "library.xml");
            new EditableLibraryConfig().Save(configPath);
            var settings = new AppSettings(Path.Combine(root, "settings.json"));
            settings.LoadConfig(configPath);
            var organizer = new RecordingOrganizer([
                new PlannedMove(Path.Combine(root, "one.flac"), Path.Combine(root, "A", "one.flac")),
                new PlannedMove(Path.Combine(root, "two.flac"), Path.Combine(root, "A", "two.flac")),
            ]);
            var dialogs = new RecordingDialogs { ConfirmApplyResult = false };
            var viewModel = new OrganizeViewModel(organizer, settings, dialogs);

            await viewModel.PreviewCommand.ExecuteAsync(null);
            await viewModel.ApplyCommand.ExecuteAsync(null);

            Assert.Equal(0, organizer.ApplyCalls);
            Assert.True(viewModel.ApplyCommand.CanExecute(null));
            Assert.Contains(
                "Apply 2 planned moves",
                dialogs.ApplyMessage);
            Assert.Contains("Recovery is available", dialogs.ApplyMessage);
            Assert.False(viewModel.CancelCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingOrganizer(IReadOnlyList<PlannedMove> moves) : ILibraryOrganizer
    {
        public int ApplyCalls { get; private set; }

        public Task<IReadOnlyList<PlannedMove>> PreviewMovesAsync(CancellationToken ct = default) =>
            Task.FromResult(moves);

        public Task<OrganizeResult> ApplyMovesAsync(
            IReadOnlyList<PlannedMove> moves,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            ApplyCalls++;
            return Task.FromResult(new OrganizeResult(moves.Count, []));
        }
    }

    private sealed class RecordingDialogs : IDialogService
    {
        public bool ConfirmApplyResult { get; init; }
        public string? ApplyMessage { get; private set; }

        public Task<bool> ShowFieldsEditorAsync(IReadOnlyList<string> paths) => Task.FromResult(false);
        public Task<string?> ShowConfigEditorAsync(string? existingPath) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmApplyAsync(string title, string message, string primaryText)
        {
            ApplyMessage = message;
            return Task.FromResult(ConfirmApplyResult);
        }
        public Task<bool> ConfirmCdDerivationAsync(IngestApprovalItem item) => Task.FromResult(false);
        public Task<bool> ConfirmRestoreAsync(OperationRestorePlan plan) => Task.FromResult(false);
        public Task<bool> ConfirmPurgeAsync(OperationPurgePlan plan) => Task.FromResult(false);
    }
}
