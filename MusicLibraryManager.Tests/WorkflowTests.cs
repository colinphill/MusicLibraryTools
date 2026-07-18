using MusicLibraryManager.Presentation;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public void Operations_catalog_excludes_android_device_sync()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"manager-workflow-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings(settingsPath);
            var viewModel = new OperationsViewModel(
                new OperationJournalService(), new StubFiles(), new StubDialogs(), settings,
                new UnifiedJobService());

            Assert.DoesNotContain(viewModel.JobCatalog, job => job.Id == "device-sync");
            Assert.Contains(viewModel.JobCatalog, job => job.Id == "smart-storage");
            Assert.Contains(viewModel.JobCatalog, job => job.Id == "car-card");
        }
        finally
        {
            try { File.Delete(settingsPath); } catch { }
        }
    }

    [Fact]
    public void Operations_preferences_use_manager_namespace()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"manager-workflow-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings(settingsPath);
            var viewModel = new OperationsViewModel(
                new OperationJournalService(), new StubFiles(), new StubDialogs(), settings);

            viewModel.SearchRoot = @"C:\Music\Recovery";
            viewModel.RetentionDays = 45;

            Assert.Equal(@"C:\Music\Recovery", settings.GetPreference("manager.operations.searchRoot.v1"));
            Assert.Equal("45", settings.GetPreference("manager.operations.retentionDays.v1"));
            Assert.Null(settings.GetPreference("Operations.SearchRoot"));
        }
        finally
        {
            try { File.Delete(settingsPath); } catch { }
        }
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
        public Task<bool> ConfirmCdDerivationAsync(IngestApprovalItem item) => Task.FromResult(false);
        public Task<bool> ConfirmRestoreAsync(OperationRestorePlan plan) => Task.FromResult(false);
        public Task<bool> ConfirmPurgeAsync(OperationPurgePlan plan) => Task.FromResult(false);
    }
}
