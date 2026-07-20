using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class DeviceSyncServiceTests
{
    [Fact]
    public async Task PreviewReturnsStructuredReviewedPlan()
    {
        using var source = new TempDirectory();
        var adapter = new StubSyncerClientAdapter { PlanResult = SuccessPlan(removals: 1) };
        var service = new DeviceSyncService(adapter);

        var request = new DeviceSyncRequest(source.Path, "music",
            DeviceSerial: "phone", Exclusions: ["**/*.tmp"], MaxRemovals: 1);
        DeviceSyncPlan plan = await service.PreviewAsync(request,
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        Assert.Equal("phone", plan.DeviceSerial);
        Assert.Equal("abc123", plan.PlanDigest);
        Assert.Equal(2, plan.Actions.Count);
        Assert.Contains(plan.Actions, action => action.Kind == DeviceSyncMutationKind.AddFile);
        Assert.Contains(plan.Actions, action => action.Kind == DeviceSyncMutationKind.DeleteFile);
        Assert.Equal(request, adapter.PlanInvocations.Single().Request);
        Assert.Equal(plan.PlanFilePath, adapter.PlanInvocations.Single().PlanFilePath);
        Assert.EndsWith(".json", plan.PlanFilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewKeepsActionsButBlocksExcessRemovals()
    {
        using var source = new TempDirectory();
        var service = new DeviceSyncService(new StubSyncerClientAdapter
        {
            PlanResult = SuccessPlan(removals: 3),
        });

        DeviceSyncPlan plan = await service.PreviewAsync(new(source.Path, "music", MaxRemovals: 2),
            ct: TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Issues, issue => issue.Code == "removal-limit");
        Assert.NotEmpty(plan.Actions);
    }

    [Fact]
    public async Task PreviewDistinguishesNoMaximumFromZeroAllowedRemovals()
    {
        using var source = new TempDirectory();
        var service = new DeviceSyncService(new StubSyncerClientAdapter
        {
            PlanResult = SuccessPlan(removals: 1),
        });

        DeviceSyncPlan unlimited = await service.PreviewAsync(
            new(source.Path, "music", MaxRemovals: null),
            ct: TestContext.Current.CancellationToken);
        DeviceSyncPlan noneAllowed = await service.PreviewAsync(
            new(source.Path, "music", MaxRemovals: 0),
            ct: TestContext.Current.CancellationToken);

        Assert.True(unlimited.CanApply);
        Assert.False(noneAllowed.CanApply);
        Assert.Contains(noneAllowed.Issues, issue => issue.Code == "removal-limit");
        File.Delete(unlimited.PlanFilePath);
        File.Delete(noneAllowed.PlanFilePath);
    }

    [Fact]
    public async Task ApplyExecutesSavedManagedPlanWithoutRescanning()
    {
        using var source = new TempDirectory();
        var adapter = new StubSyncerClientAdapter
        {
            PlanResult = SuccessPlan(removals: 1),
            ApplyResult = new(0, 1, 2, 123, "run-1", "phone", "abc123"),
        };
        var service = new DeviceSyncService(adapter);
        DeviceSyncPlan plan = await service.PreviewAsync(new(source.Path, "music", MaxRemovals: 1),
            ct: TestContext.Current.CancellationToken);

        DeviceSyncResult result = await service.ApplyAsync(plan,
            ct: TestContext.Current.CancellationToken);

        Assert.Same(plan, adapter.ApplyInvocations.Single());
        Assert.Single(adapter.PlanInvocations);
        Assert.Equal("run-1", result.RecoveryId);
        Assert.Equal(2, result.QuarantinedCount);
        Assert.Equal(123, result.TransferredBytes);
    }

    [Fact]
    public async Task ApplySurfacesChangedPlanBeforeMutation()
    {
        using var source = new TempDirectory();
        var adapter = new StubSyncerClientAdapter
        {
            PlanResult = SuccessPlan(),
            ApplyError = new InvalidOperationException("destination changed after the plan was saved"),
        };
        var service = new DeviceSyncService(adapter);
        DeviceSyncPlan plan = await service.PreviewAsync(new(source.Path, "music"),
            ct: TestContext.Current.CancellationToken);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(plan, ct: TestContext.Current.CancellationToken));

        Assert.Contains("changed after the plan was saved", error.Message);
    }

    [Fact]
    public async Task ApplyRejectsAnUnexpectedManagedPlanDigest()
    {
        using var source = new TempDirectory();
        var adapter = new StubSyncerClientAdapter
        {
            PlanResult = SuccessPlan(),
            ApplyResult = new(0, 1, 0, 123, "run-1", "phone", "different"),
        };
        var service = new DeviceSyncService(adapter);
        DeviceSyncPlan plan = await service.PreviewAsync(new(source.Path, "music"),
            ct: TestContext.Current.CancellationToken);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(plan, ct: TestContext.Current.CancellationToken));

        Assert.Contains("unexpected plan digest", error.Message);
    }

    [Fact]
    public async Task RestoreExecutesNamedRecoveryRunWithoutScanning()
    {
        var adapter = new StubSyncerClientAdapter
        {
            RestoreResult = new("/sdcard/Music", "run-1", "phone"),
        };
        var service = new DeviceSyncService(adapter);
        var request = new DeviceSyncRestoreRequest(
            "/sdcard/Music", "run-1", "phone", "C:\\adb.exe");

        DeviceSyncRestoreResult result = await service.RestoreAsync(request,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("run-1", result.RecoveryId);
        Assert.Equal("phone", result.DeviceSerial);
        Assert.Equal(request, adapter.RestoreInvocations.Single());
        Assert.Empty(adapter.PlanInvocations);
    }

    [Fact]
    public async Task RestoreSurfacesManagedSafetyFailure()
    {
        var adapter = new StubSyncerClientAdapter
        {
            RestoreError = new InvalidOperationException("recovery run has already been restored"),
        };
        var service = new DeviceSyncService(adapter);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreAsync(new("music", "run-1"),
                ct: TestContext.Current.CancellationToken));

        Assert.Contains("already been restored", error.Message);
    }

    [Fact]
    public async Task InitializeUsesExplicitAdoptAndConnectionSelection()
    {
        var adapter = new StubSyncerClientAdapter
        {
            InitializationResult = new("music", "phone", true, "Initialized music on phone"),
        };
        var service = new DeviceSyncService(adapter);
        var request = new DeviceSyncInitializationRequest(
            "music", "phone", "C:\\adb.exe", Adopt: true);

        DeviceSyncInitializationResult result = await service.InitializeAsync(request,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Initialized music on phone", result.Message);
        Assert.True(result.Adopted);
        Assert.Equal(request, adapter.InitializationInvocations.Single());
    }

    [Fact]
    public async Task InitializeFailureSurfacesManagedError()
    {
        var adapter = new StubSyncerClientAdapter
        {
            InitializationError = new InvalidOperationException(
                "destination must be below internal shared storage"),
        };
        var service = new DeviceSyncService(adapter);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InitializeAsync(new("/invalid", Adopt: true),
                ct: TestContext.Current.CancellationToken));

        Assert.Equal("destination must be below internal shared storage", error.Message);
    }

    private static ManagedSyncerPlan SuccessPlan(int removals = 0) => new(
        "phone", "abc123",
        [
            new(DeviceSyncMutationKind.AddFile, "Artist/song.flac",
                "destination path is missing", false, 123, 10),
            new(DeviceSyncMutationKind.DeleteFile, "old.flac",
                "destination only", false, 10, 9),
        ],
        DirectoryCount: 0,
        FileCount: 1,
        RemovalCount: removals,
        TransferBytes: 123);

    private sealed class StubSyncerClientAdapter : ISyncerClientAdapter
    {
        public ManagedSyncerPlan PlanResult { get; init; } = SuccessPlan();
        public ManagedSyncerApplyResult ApplyResult { get; init; } =
            new(0, 1, 0, 123, "run-1", "phone", "abc123");
        public DeviceSyncInitializationResult InitializationResult { get; init; } =
            new("music", "phone", false, "Initialized music on phone");
        public DeviceSyncRestoreResult RestoreResult { get; init; } =
            new("music", "run-1", "phone");
        public Exception? ApplyError { get; init; }
        public Exception? InitializationError { get; init; }
        public Exception? RestoreError { get; init; }

        public List<(DeviceSyncRequest Request, string PlanFilePath)> PlanInvocations { get; } = [];
        public List<DeviceSyncPlan> ApplyInvocations { get; } = [];
        public List<DeviceSyncInitializationRequest> InitializationInvocations { get; } = [];
        public List<DeviceSyncRestoreRequest> RestoreInvocations { get; } = [];

        public Task<DeviceSyncInitializationResult> InitializeAsync(
            DeviceSyncInitializationRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            InitializationInvocations.Add(request);
            return InitializationError is null
                ? Task.FromResult(InitializationResult)
                : Task.FromException<DeviceSyncInitializationResult>(InitializationError);
        }

        public Task<ManagedSyncerPlan> PlanPushAsync(
            DeviceSyncRequest request,
            string planFilePath,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PlanInvocations.Add((request, planFilePath));
            return Task.FromResult(PlanResult);
        }

        public Task<ManagedSyncerApplyResult> ApplyPlanAsync(
            DeviceSyncPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ApplyInvocations.Add(plan);
            return ApplyError is null
                ? Task.FromResult(ApplyResult)
                : Task.FromException<ManagedSyncerApplyResult>(ApplyError);
        }

        public Task<DeviceSyncRestoreResult> RestoreAsync(
            DeviceSyncRestoreRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RestoreInvocations.Add(request);
            return RestoreError is null
                ? Task.FromResult(RestoreResult)
                : Task.FromException<DeviceSyncRestoreResult>(RestoreError);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "DeviceSyncTests", Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
