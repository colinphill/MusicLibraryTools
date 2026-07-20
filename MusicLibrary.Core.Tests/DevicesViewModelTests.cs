using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class DevicesViewModelTests
{
    [Fact]
    public async Task PreviewLaunchFailureShowsAnExplicitDialog()
    {
        using var temp = new TempDirectory();
        var dialogs = new RecordingDialogs();
        var viewModel = new DevicesViewModel(
            new FailingDeviceSyncService(),
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), dialogs, new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);

        Assert.Equal("Preview Android synchronization failed", dialogs.Title);
        Assert.Contains("syncer executable was not found", dialogs.Message);
        Assert.Equal(dialogs.Message, viewModel.StatusText);
    }

    [Fact]
    public async Task SuccessfulApplyEnablesOneClickRestore()
    {
        using var temp = new TempDirectory();
        var sync = new RestorableDeviceSyncService(temp.Path);
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.True(viewModel.RestoreCommand.CanExecute(null));
        await viewModel.RestoreCommand.ExecuteAsync(null);

        Assert.NotNull(sync.RestoreRequest);
        Assert.Equal("run-1", sync.RestoreRequest!.RecoveryId);
        Assert.Equal("/sdcard/Music", sync.RestoreRequest.Destination);
        Assert.False(viewModel.RestoreCommand.CanExecute(null));
        Assert.Contains("Restored recovery run run-1", viewModel.StatusText);
    }

    private sealed class FailingDeviceSyncService : IDeviceSyncService
    {
        public Task<DeviceSyncInitializationResult> InitializeAsync(
            DeviceSyncInitializationRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => throw Failure();

        public Task<DeviceSyncPlan> PreviewAsync(
            DeviceSyncRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => throw Failure();

        public Task<DeviceSyncResult> ApplyAsync(
            DeviceSyncPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => throw Failure();

        public Task<DeviceSyncRestoreResult> RestoreAsync(
            DeviceSyncRestoreRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => throw Failure();

        private static FileNotFoundException Failure() =>
            new("The packaged syncer executable was not found.");
    }

    private sealed class RestorableDeviceSyncService(string source) : IDeviceSyncService
    {
        public DeviceSyncRestoreRequest? RestoreRequest { get; private set; }

        public Task<DeviceSyncInitializationResult> InitializeAsync(
            DeviceSyncInitializationRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(
                new DeviceSyncInitializationResult(request.Destination, "phone", request.Adopt, "Initialized"));

        public Task<DeviceSyncPlan> PreviewAsync(
            DeviceSyncRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(new DeviceSyncPlan(request, "phone", "digest",
                Path.Combine(source, "plan.json"), [], 0, 1, 0, 10, [], DateTimeOffset.UtcNow));

        public Task<DeviceSyncResult> ApplyAsync(
            DeviceSyncPlan plan, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(
                new DeviceSyncResult(0, 1, 0, 10, "run-1", "phone", []));

        public Task<DeviceSyncRestoreResult> RestoreAsync(
            DeviceSyncRestoreRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            RestoreRequest = request;
            return Task.FromResult(new DeviceSyncRestoreResult(
                request.Destination, request.RecoveryId, request.DeviceSerial ?? "phone"));
        }
    }

    private sealed class RecordingDialogs : IDialogCoordinator
    {
        public string? Title { get; private set; }
        public string? Message { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string primaryText) =>
            Task.FromResult(true);

        public Task ShowMessageAsync(string title, string message)
        {
            Title = title;
            Message = message;
            return Task.CompletedTask;
        }
    }

    private sealed class StubFiles : IFilePickerService
    {
        public Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerType>? types = null) =>
            Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedName, string extension) =>
            Task.FromResult<string?>(null);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "DevicesViewModelTests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
