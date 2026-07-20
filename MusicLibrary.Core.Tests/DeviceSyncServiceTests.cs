using MusicLibrary.Core.Services;
using System.Text.Json;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class DeviceSyncServiceTests
{
    [Fact]
    public async Task PreviewReturnsStructuredReviewedPlan()
    {
        using var source = new TempDirectory();
        var runner = new StubRunner(SuccessJson(removals: 1));
        var service = new DeviceSyncService(runner);

        DeviceSyncPlan plan = await service.PreviewAsync(new(source.Path, "music",
            DeviceSerial: "phone", Exclusions: ["**/*.tmp"], MaxRemovals: 1),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        Assert.Equal("phone", plan.DeviceSerial);
        Assert.Equal("abc123", plan.PlanDigest);
        Assert.Equal(2, plan.Actions.Count);
        Assert.Contains(plan.Actions, action => action.Kind == DeviceSyncMutationKind.AddFile);
        Assert.Contains(plan.Actions, action => action.Kind == DeviceSyncMutationKind.DeleteFile);
        Assert.Contains("--dry-run", runner.Invocations.Single());
        int savedPlan = runner.Invocations.Single().ToList().IndexOf("--save-plan");
        Assert.Equal(plan.PlanFilePath, runner.Invocations.Single()[savedPlan + 1]);
        Assert.DoesNotContain("--max-removals", runner.Invocations.Single());
        Assert.Contains("**/*.tmp", runner.Invocations.Single());
    }

    [Fact]
    public async Task PreviewKeepsActionsButBlocksExcessRemovals()
    {
        using var source = new TempDirectory();
        var service = new DeviceSyncService(new StubRunner(SuccessJson(removals: 3)));

        DeviceSyncPlan plan = await service.PreviewAsync(new(source.Path, "music", MaxRemovals: 2),
            ct: TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Issues, issue => issue.Code == "removal-limit");
        Assert.NotEmpty(plan.Actions);
    }

    [Fact]
    public async Task ApplyExecutesSavedPlanWithoutRescanningSourceOrDestination()
    {
        using var source = new TempDirectory();
        var runner = new StubRunner(SuccessJson(removals: 1),
            SuccessJson(removals: 1, status: "success", recoveryId: "run-1", applied: true));
        var service = new DeviceSyncService(runner);
        DeviceSyncPlan plan = await service.PreviewAsync(new(source.Path, "music", MaxRemovals: 1),
            ct: TestContext.Current.CancellationToken);

        DeviceSyncResult result = await service.ApplyAsync(plan,
            ct: TestContext.Current.CancellationToken);

        IReadOnlyList<string> apply = runner.Invocations[1];
        Assert.Equal("apply", apply[0]);
        int planIndex = apply.ToList().IndexOf("--plan");
        Assert.Equal(plan.PlanFilePath, apply[planIndex + 1]);
        Assert.DoesNotContain("--expect-plan", apply);
        Assert.DoesNotContain("--max-removals", apply);
        Assert.DoesNotContain(source.Path, apply);
        Assert.DoesNotContain("music", apply);
        Assert.Equal("run-1", result.RecoveryId);
        Assert.Equal(2, result.QuarantinedCount);
        Assert.Equal(123, result.TransferredBytes);
    }

    [Fact]
    public async Task ApplySurfacesChangedPlanBeforeMutation()
    {
        using var source = new TempDirectory();
        var runner = new StubRunner(SuccessJson(), ErrorJson(6, "destination changed after the plan was saved"));
        var service = new DeviceSyncService(runner);
        DeviceSyncPlan plan = await service.PreviewAsync(new(source.Path, "music"),
            ct: TestContext.Current.CancellationToken);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(plan, ct: TestContext.Current.CancellationToken));

        Assert.Contains("changed after the plan was saved", error.Message);
    }

    [Fact]
    public async Task RestoreExecutesNamedRecoveryRunWithoutScanning()
    {
        var runner = new StubRunner(RestoreJson("run-1"));
        var service = new DeviceSyncService(runner);

        DeviceSyncRestoreResult result = await service.RestoreAsync(
            new("/sdcard/Music", "run-1", "phone", "C:\\adb.exe"),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("run-1", result.RecoveryId);
        Assert.Equal("phone", result.DeviceSerial);
        Assert.Equal(["restore", "--json", "--adb", "C:\\adb.exe", "--serial", "phone",
            "--recovery", "run-1", "/sdcard/Music"], runner.Invocations.Single());
    }

    [Fact]
    public async Task RestoreSurfacesNativeSafetyFailure()
    {
        var runner = new StubRunner(ErrorJson(4, "recovery run has already been restored"));
        var service = new DeviceSyncService(runner);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreAsync(new("music", "run-1"),
                ct: TestContext.Current.CancellationToken));

        Assert.Contains("already been restored", error.Message);
    }

    [Fact]
    public async Task InitializeUsesExplicitAdoptAndConnectionSelection()
    {
        var runner = new StubRunner(new SyncerProcessResult(0, "Initialized music on phone", ""));
        var service = new DeviceSyncService(runner);

        DeviceSyncInitializationResult result = await service.InitializeAsync(
            new("music", "phone", "C:\\adb.exe", Adopt: true),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Initialized music on phone", result.Message);
        Assert.Equal(["init", "--adb", "C:\\adb.exe", "--serial", "phone", "--adopt", "music"],
            runner.Invocations.Single());
    }

    [Fact]
    public async Task InitializeFailureSurfacesNativeErrorWithoutParsingJson()
    {
        var runner = new StubRunner(new SyncerProcessResult(5, "",
            "syncer: destination must be below internal shared storage"));
        var service = new DeviceSyncService(runner);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InitializeAsync(new("/invalid", Adopt: true),
                ct: TestContext.Current.CancellationToken));

        Assert.Equal("syncer: destination must be below internal shared storage", error.Message);
    }

    private static SyncerProcessResult SuccessJson(
        int removals = 0, string status = "dry_run", string? recoveryId = null, bool applied = false)
    {
        string json = JsonSerializer.Serialize(new
        {
            schema = 1, status, device = "phone", source = "source", destination = "music",
            plan_digest = "abc123", recovery_id = recoveryId, removal_count = removals,
            actions = new object[]
            {
                new { action = "add_file", path = "Artist/song.flac", reason = "destination path is missing", type = "file", size = 123, modified_seconds = 10 },
                new { action = "delete_file", path = "old.flac", reason = "destination only", type = "file", size = 10, modified_seconds = 9 },
            },
            planned = new { files = 1, directories = 0, deletions = removals, bytes = 123 },
            applied = new { files = applied ? 1 : 0, directories = 0, deletions = applied ? removals : 0, bytes = applied ? 123 : 0 },
            quarantined_count = applied ? removals + 1 : 0,
            transferred_bytes = applied ? 123 : 0, duration_ms = 1, error = (object?)null,
        });
        return new(0, json, "");
    }

    private static SyncerProcessResult ErrorJson(int code, string message) => new(code,
        JsonSerializer.Serialize(new
        {
            schema = 1, status = "error", device = (string?)null, source = (string?)null,
            destination = (string?)null, plan_digest = (string?)null, actions = Array.Empty<object>(),
            recovery_id = (string?)null, removal_count = 0,
            planned = new { files = 0, directories = 0, deletions = 0, bytes = 0 },
            applied = new { files = 0, directories = 0, deletions = 0, bytes = 0 },
            quarantined_count = 0,
            transferred_bytes = 0, duration_ms = 0, error = new { code, message },
        }), "");

    private static SyncerProcessResult RestoreJson(string recoveryId) => new(0,
        JsonSerializer.Serialize(new
        {
            schema = 1, status = "restored", device = "phone", destination = "/sdcard/Music",
            recovery_id = recoveryId, duration_ms = 1, error = (object?)null,
        }), "");

    private sealed class StubRunner(params SyncerProcessResult[] results) : ISyncerProcessRunner
    {
        private readonly Queue<SyncerProcessResult> _results = new(results);
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public Task<SyncerProcessResult> RunAsync(IReadOnlyList<string> arguments,
            IProgress<MusicLibrary.Core.Models.OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Invocations.Add(arguments.ToArray());
            return Task.FromResult(_results.Dequeue());
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
