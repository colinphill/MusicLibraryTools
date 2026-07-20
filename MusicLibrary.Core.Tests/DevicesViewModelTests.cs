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

    [Fact]
    public async Task DirectApplyDoesNotSaveRecoveryInformation()
    {
        using var temp = new TempDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        var sync = new DirectDeviceSyncService(temp.Path);
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(settingsPath), new StubFiles(),
            new RecordingDialogs(), new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        await viewModel.ApplyCommand.ExecuteAsync(null);
        Assert.True(viewModel.RestoreCommand.CanExecute(null));

        viewModel.Direct = true;
        await viewModel.PreviewCommand.ExecuteAsync(null);
        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.False(viewModel.RestoreCommand.CanExecute(null));
        Assert.DoesNotContain("Recovery run:", viewModel.StatusText);
        var reloaded = new DevicesViewModel(sync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        Assert.False(reloaded.RestoreCommand.CanExecute(null));
    }

    [Fact]
    public void MaximumRemovalsPersistsBlankAndZeroAsDifferentValues()
    {
        using var temp = new TempDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        var sync = new DirectDeviceSyncService(temp.Path);
        var blank = new DevicesViewModel(sync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        Assert.Null(blank.MaxRemovals);

        blank.MaxRemovals = 0;
        var reloaded = new DevicesViewModel(sync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());

        Assert.Equal(0, reloaded.MaxRemovals);
    }

    [Fact]
    public async Task ApplyShowsBlankInProgressAndCompleteStatusesPerAction()
    {
        using var temp = new TempDirectory();
        var sync = new ProgressDeviceSyncService(temp.Path);
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);

        Assert.All(viewModel.Actions, row => Assert.Equal("", row.Status));
        Task apply = viewModel.ApplyCommand.ExecuteAsync(null);
        await sync.ApplyStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => viewModel.Actions[0].Status == "In progress");
        Assert.False(viewModel.IsConfigurationEnabled);
        Assert.Equal("", viewModel.Actions[1].Status);

        sync.ReleaseApply.TrySetResult();
        await apply;

        Assert.True(viewModel.IsConfigurationEnabled);
        Assert.All(viewModel.Actions, row => Assert.Equal("Complete", row.Status));
    }

    [Fact]
    public async Task ApplyMarksTheActiveActionFailedAndLeavesFutureActionsBlank()
    {
        using var temp = new TempDirectory();
        var sync = new ProgressDeviceSyncService(temp.Path) { FailApply = true };
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        Task apply = viewModel.ApplyCommand.ExecuteAsync(null);
        await sync.ApplyStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => viewModel.Actions[0].Status == "In progress");

        sync.ReleaseApply.TrySetResult();
        await apply;
        await WaitUntilAsync(() => viewModel.Actions[0].Status == "Failed");

        Assert.True(viewModel.IsConfigurationEnabled);
        Assert.Equal("", viewModel.Actions[1].Status);
    }

    [Fact]
    public async Task CancellingApplyReenablesConfiguration()
    {
        using var temp = new TempDirectory();
        var sync = new ProgressDeviceSyncService(temp.Path);
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        Task apply = viewModel.ApplyCommand.ExecuteAsync(null);
        await sync.ApplyStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(viewModel.IsConfigurationEnabled);

        viewModel.CancelCommand.Execute(null);
        await apply;

        Assert.True(viewModel.IsConfigurationEnabled);
        Assert.Contains("cancelled", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
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

    private sealed class ProgressDeviceSyncService(string source) : IDeviceSyncService
    {
        public TaskCompletionSource ApplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseApply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailApply { get; init; }

        public Task<DeviceSyncInitializationResult> InitializeAsync(
            DeviceSyncInitializationRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(
                new DeviceSyncInitializationResult(request.Destination, "phone", request.Adopt, "Initialized"));

        public Task<DeviceSyncPlan> PreviewAsync(
            DeviceSyncRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(new DeviceSyncPlan(request, "phone", "digest",
                Path.Combine(source, "plan.json"),
                [
                    new(DeviceSyncMutationKind.AddFile, "one.flac", "missing", false, 10, 1),
                    new(DeviceSyncMutationKind.AddFile, "two.flac", "missing", false, 20, 2),
                ],
                0, 2, 0, 30, [], DateTimeOffset.UtcNow));

        public async Task<DeviceSyncResult> ApplyAsync(
            DeviceSyncPlan plan, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            progress?.Report(new(OperationPhase.Applying, CurrentPath: "one.flac",
                ItemStatus: OperationItemStatus.InProgress));
            ApplyStarted.TrySetResult();
            await ReleaseApply.Task.WaitAsync(ct);
            if (FailApply)
            {
                progress?.Report(new(OperationPhase.Applying, CurrentPath: "one.flac",
                    ItemStatus: OperationItemStatus.Failed));
                throw new InvalidOperationException("transfer failed");
            }
            progress?.Report(new(OperationPhase.Applying, CurrentPath: "one.flac",
                ItemStatus: OperationItemStatus.Complete));
            progress?.Report(new(OperationPhase.Applying, CurrentPath: "two.flac",
                ItemStatus: OperationItemStatus.InProgress));
            progress?.Report(new(OperationPhase.Applying, CurrentPath: "two.flac",
                ItemStatus: OperationItemStatus.Complete));
            return new(0, 2, 0, 30, "run-1", "phone", []);
        }

        public Task<DeviceSyncRestoreResult> RestoreAsync(
            DeviceSyncRestoreRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(
                new DeviceSyncRestoreResult(request.Destination, request.RecoveryId, "phone"));
    }

    private sealed class DirectDeviceSyncService(string source) : IDeviceSyncService
    {
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
                new DeviceSyncResult(0, 1, plan.Request.Direct ? 0 : 1, 10,
                    plan.Request.Direct ? null : "run-before-direct", "phone", []));

        public Task<DeviceSyncRestoreResult> RestoreAsync(
            DeviceSyncRestoreRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => throw new InvalidOperationException("No recovery is available.");
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
