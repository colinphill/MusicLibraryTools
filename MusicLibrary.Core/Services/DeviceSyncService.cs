using MusicLibrary.Core.Models;
using Syncer;

namespace MusicLibrary.Core.Services;

public sealed record DeviceSyncRequest(
    string Source,
    string Destination,
    string? DeviceSerial = null,
    string? AdbPath = null,
    IReadOnlyList<string>? Exclusions = null,
    int MtimeToleranceSeconds = 60,
    bool DeleteExtras = true,
    bool Direct = false,
    int? MaxRemovals = null);

public sealed record DeviceSyncInitializationRequest(
    string Destination,
    string? DeviceSerial = null,
    string? AdbPath = null,
    bool Adopt = false);

public sealed record DeviceSyncRestoreRequest(
    string Destination,
    string RecoveryId,
    string? DeviceSerial = null,
    string? AdbPath = null);

public enum DeviceSyncMutationKind
{
    CreateDirectory,
    ReplaceWithDirectory,
    AddFile,
    UpdateFile,
    ReplaceWithFile,
    DeleteFile,
    DeleteDirectory,
    DeleteOther,
}

public sealed record DeviceSyncAction(
    DeviceSyncMutationKind Kind,
    string RelativePath,
    string Reason,
    bool IsDirectory,
    long Length,
    long ModifiedSeconds);

public sealed record DeviceSyncPlan(
    DeviceSyncRequest Request,
    string DeviceSerial,
    string PlanDigest,
    string PlanFilePath,
    IReadOnlyList<DeviceSyncAction> Actions,
    int DirectoryCount,
    int FileCount,
    int RemovalCount,
    long TransferBytes,
    IReadOnlyList<OperationIssue> Issues,
    DateTimeOffset CreatedAtUtc)
{
    public bool CanApply => !string.IsNullOrWhiteSpace(PlanDigest) &&
        !string.IsNullOrWhiteSpace(PlanFilePath) &&
        Issues.All(issue => issue.Severity != OperationIssueSeverity.Blocker);
}

public sealed record DeviceSyncResult(
    int CreatedDirectoryCount,
    int CopiedFileCount,
    int QuarantinedCount,
    long TransferredBytes,
    string? RecoveryId,
    string DeviceSerial,
    IReadOnlyList<OperationIssue> Issues);

public sealed record DeviceSyncInitializationResult(
    string Destination,
    string? DeviceSerial,
    bool Adopted,
    string Message);

public sealed record DeviceSyncRestoreResult(
    string Destination,
    string RecoveryId,
    string DeviceSerial);

/// <summary>
/// A device reported by ADB. <see cref="Id"/> is the composite model-and-serial profile identity;
/// <see cref="Serial"/> is the raw ADB selector used by synchronization requests.
/// </summary>
public sealed record DeviceSyncDevice(
    string Id,
    string Serial,
    string DisplayName,
    string State,
    bool IsReady,
    string? Model = null,
    string? Product = null,
    string? Device = null,
    string? TransportId = null,
    string? Connection = null);

public interface IDeviceSyncService
{
    Task<IReadOnlyList<DeviceSyncDevice>> EnumerateDevicesAsync(
        string? adbPath = null,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeviceSyncDevice>>([]);

    Task<DeviceSyncInitializationResult> InitializeAsync(
        DeviceSyncInitializationRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<DeviceSyncPlan> PreviewAsync(
        DeviceSyncRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<DeviceSyncResult> ApplyAsync(
        DeviceSyncPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<DeviceSyncRestoreResult> RestoreAsync(
        DeviceSyncRestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed record ManagedSyncerPlan(
    string DeviceSerial,
    string PlanDigest,
    IReadOnlyList<DeviceSyncAction> Actions,
    int DirectoryCount,
    int FileCount,
    int RemovalCount,
    long TransferBytes);

public sealed record ManagedSyncerApplyResult(
    int CreatedDirectoryCount,
    int CopiedFileCount,
    int QuarantinedCount,
    long TransferredBytes,
    string? RecoveryId,
    string DeviceSerial,
    string PlanDigest);

/// <summary>Application boundary around the managed Syncer.Client library.</summary>
public interface ISyncerClientAdapter
{
    Task<IReadOnlyList<DeviceSyncDevice>> EnumerateDevicesAsync(
        string? adbPath = null,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeviceSyncDevice>>([]);

    Task<DeviceSyncInitializationResult> InitializeAsync(
        DeviceSyncInitializationRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<ManagedSyncerPlan> PlanPushAsync(
        DeviceSyncRequest request,
        string planFilePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<ManagedSyncerApplyResult> ApplyPlanAsync(
        DeviceSyncPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<DeviceSyncRestoreResult> RestoreAsync(
        DeviceSyncRestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Uses Syncer.Client in-process. Only ADB and the matching Android server are external; no host
/// syncer command is launched and no command output needs to be parsed.
/// </summary>
public sealed class SyncerClientAdapter : ISyncerClientAdapter
{
    public async Task<IReadOnlyList<DeviceSyncDevice>> EnumerateDevicesAsync(
        string? adbPath = null,
        CancellationToken ct = default)
    {
        SyncerClient client = CreateClient(adbPath, null);
        IReadOnlyList<SyncerDevice> devices = await InvokeAsync(
            () => client.EnumerateDevicesAsync(ct)).ConfigureAwait(false);
        return devices.Select(device => new DeviceSyncDevice(
            device.Id,
            device.Serial,
            device.DisplayName,
            device.State,
            device.IsReady,
            device.Model,
            device.Product,
            device.Device,
            device.TransportId,
            device.Connection)).ToArray();
    }

    public async Task<DeviceSyncInitializationResult> InitializeAsync(
        DeviceSyncInitializationRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        SyncerClient client = CreateClient(request.AdbPath, request.DeviceSerial);
        InitializeResult result = await InvokeAsync(() => client.InitializeAsync(
            request.Destination.Trim(), new InitializeOptions { Adopt = request.Adopt },
            AdaptProgress(progress), ct)).ConfigureAwait(false);
        return new(result.Destination, result.DeviceSerial, request.Adopt,
            $"Initialized {result.Destination} on {result.DeviceSerial}");
    }

    public async Task<ManagedSyncerPlan> PlanPushAsync(
        DeviceSyncRequest request,
        string planFilePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        SyncerClient client = CreateClient(request.AdbPath, request.DeviceSerial);
        var options = new PushOptions
        {
            Exclusions = (request.Exclusions ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray(),
            ModificationTimeTolerance = TimeSpan.FromSeconds(request.MtimeToleranceSeconds),
            DeleteExtraneous = request.DeleteExtras,
            Direct = request.Direct,
            // Core retains the reviewed actions when the threshold is exceeded, then blocks Apply.
            MaximumRemovals = null,
        };
        SyncPlan plan = await InvokeAsync(() => client.PlanPushAsync(
            request.Source, request.Destination.Trim(), options, AdaptProgress(progress), ct))
            .ConfigureAwait(false);
        await InvokeAsync(async () =>
        {
            await client.SavePlanAsync(plan, planFilePath, ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

        DeviceSyncAction[] actions = plan.Directories
            .Concat(plan.Files)
            .Concat(plan.Deletions)
            .Select(ToDeviceAction)
            .ToArray();
        return new(plan.DeviceSerial, plan.Digest, actions,
            plan.Directories.Count, plan.Files.Count, checked((int)plan.RemovalCount),
            checked((long)plan.TransferBytes));
    }

    public async Task<ManagedSyncerApplyResult> ApplyPlanAsync(
        DeviceSyncPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        SyncerClient client = CreateClient(plan.Request.AdbPath, plan.Request.DeviceSerial);
        SyncPlan savedPlan = await InvokeAsync(() => client.LoadPlanAsync(plan.PlanFilePath, ct))
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(savedPlan.Digest, plan.PlanDigest))
            throw new InvalidOperationException("The saved sync plan no longer matches the reviewed plan.");

        SyncResult result = await InvokeAsync(() => client.ApplyPlanAsync(
            savedPlan, AdaptProgress(progress), ct)).ConfigureAwait(false);
        return new(result.Applied.Directories, result.Applied.Files, result.QuarantinedCount,
            checked((long)result.TransferredBytes), result.RecoveryId, result.DeviceSerial,
            result.PlanDigest);
    }

    public async Task<DeviceSyncRestoreResult> RestoreAsync(
        DeviceSyncRestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        SyncerClient client = CreateClient(request.AdbPath, request.DeviceSerial);
        RestoreResult result = await InvokeAsync(() => client.RestoreAsync(
            request.Destination.Trim(), request.RecoveryId.Trim(), AdaptProgress(progress), ct))
            .ConfigureAwait(false);
        return new(result.Destination, result.RecoveryId, result.DeviceSerial);
    }

    private static SyncerClient CreateClient(string? adbPath, string? deviceSerial) => new(new()
    {
        AdbPath = Normalize(adbPath),
        DeviceSerial = Normalize(deviceSerial),
        ServerDirectory = LocateServerDirectory(),
    });

    private static string? LocateServerDirectory()
    {
        string? configured = Normalize(Environment.GetEnvironmentVariable("MLT_SYNCER_SERVER_PATH"));
        if (configured is not null) return Path.GetFullPath(configured);
        string packaged = Path.Combine(AppContext.BaseDirectory, "tools", "syncer");
        return Directory.Exists(packaged) ? packaged : null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DeviceSyncAction ToDeviceAction(PlannedChange change) => new(
        change.Action switch
        {
            PlanAction.CreateDirectory => DeviceSyncMutationKind.CreateDirectory,
            PlanAction.ReplaceWithDirectory => DeviceSyncMutationKind.ReplaceWithDirectory,
            PlanAction.AddFile => DeviceSyncMutationKind.AddFile,
            PlanAction.UpdateFile => DeviceSyncMutationKind.UpdateFile,
            PlanAction.ReplaceWithFile => DeviceSyncMutationKind.ReplaceWithFile,
            PlanAction.DeleteFile => DeviceSyncMutationKind.DeleteFile,
            PlanAction.DeleteDirectory => DeviceSyncMutationKind.DeleteDirectory,
            PlanAction.DeleteOther => DeviceSyncMutationKind.DeleteOther,
            _ => throw new InvalidDataException($"Unknown syncer plan action '{change.Action}'."),
        },
        change.Path,
        change.Reason,
        change.Entry.Type == EntryType.Directory,
        checked((long)change.Entry.Size),
        change.Entry.ModifiedSeconds);

    private static IProgress<SyncerProgress>? AdaptProgress(IProgress<OperationProgress>? progress) =>
        progress is null ? null : new SynchronousProgress<SyncerProgress>(value =>
            progress.Report(value.Stage switch
            {
                SyncerProgressStage.ScanningSource => new(OperationPhase.IndexingSources,
                    CurrentPath: value.Path, Message: "Scanning the synchronization source"),
                SyncerProgressStage.ScanningDestination => new(OperationPhase.InventoryingDestination,
                    CurrentPath: value.Path, Message: "Scanning the managed Android destination"),
                SyncerProgressStage.SelectedChange => new(OperationPhase.Planning,
                    CurrentPath: value.Path, Message: "Selecting synchronization changes"),
                SyncerProgressStage.StagingFile => new(OperationPhase.Applying,
                    CurrentPath: value.Path, Message: "Staging file on the Android device",
                    ItemStatus: OperationItemStatus.InProgress),
                SyncerProgressStage.TransferringFile => new(OperationPhase.Applying,
                    CurrentPath: value.Path, Message: "Transferring file to the Android device",
                    ItemStatus: OperationItemStatus.InProgress),
                SyncerProgressStage.Applying => new(OperationPhase.Applying,
                    CurrentPath: value.Path, Message: "Applying synchronization changes",
                    ItemStatus: OperationItemStatus.InProgress),
                SyncerProgressStage.CompletedChange => new(OperationPhase.Applying,
                    CurrentPath: value.Path, Message: "Synchronization change complete",
                    ItemStatus: OperationItemStatus.Complete),
                SyncerProgressStage.FailedChange => new(OperationPhase.Applying,
                    CurrentPath: value.Path, Message: "Synchronization change failed",
                    ItemStatus: OperationItemStatus.Failed),
                _ => new(OperationPhase.Applying, CurrentPath: value.Path),
            }));

    private static async Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SyncerException error)
        {
            throw new InvalidOperationException(error.Message, error);
        }
    }
}

/// <summary>
/// Typed application service for Syncer.Client's managed Android mirror. Preview persists a
/// deterministic action artifact; Apply reloads and validates that artifact without repeating
/// full source or destination inventories. Restore reverses a completed sync from its recovery
/// journal.
/// </summary>
public sealed class DeviceSyncService(ISyncerClientAdapter syncer) : IDeviceSyncService
{
    public Task<IReadOnlyList<DeviceSyncDevice>> EnumerateDevicesAsync(
        string? adbPath = null,
        CancellationToken ct = default) =>
        syncer.EnumerateDevicesAsync(adbPath, ct);

    public async Task<DeviceSyncInitializationResult> InitializeAsync(
        DeviceSyncInitializationRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Destination))
            throw new ArgumentException("An Android destination is required.", nameof(request));
        progress?.Report(new(OperationPhase.Validating,
            Message: "Initializing managed Android destination"));
        return await syncer.InitializeAsync(request, progress, ct).ConfigureAwait(false);
    }

    public async Task<DeviceSyncPlan> PreviewAsync(
        DeviceSyncRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        Validate(request);
        progress?.Report(new(OperationPhase.IndexingSources,
            Message: "Scanning the source and managed Android destination"));
        string planFile = CreatePlanFilePath();
        ManagedSyncerPlan managedPlan;
        try
        {
            managedPlan = await syncer.PlanPushAsync(request, planFile, progress, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            TryDeletePlan(planFile);
            throw;
        }

        var issues = new List<OperationIssue>();
        if (request.MaxRemovals is { } maximumRemovals &&
            managedPlan.RemovalCount > maximumRemovals)
            issues.Add(new("removal-limit", OperationIssueSeverity.Blocker,
                $"The plan removes {managedPlan.RemovalCount:N0} entries, exceeding MaxRemovals {maximumRemovals:N0}."));
        if (request.Direct)
            issues.Add(new("direct-mode", OperationIssueSeverity.Warning,
                "Direct mode does not stage the complete transfer and saves no recovery information; replacements and deletions are permanent."));

        return new(request, managedPlan.DeviceSerial, managedPlan.PlanDigest, planFile,
            managedPlan.Actions, managedPlan.DirectoryCount, managedPlan.FileCount,
            managedPlan.RemovalCount, managedPlan.TransferBytes, issues, DateTimeOffset.UtcNow);
    }

    public async Task<DeviceSyncResult> ApplyAsync(
        DeviceSyncPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException("The device-sync plan contains blocking issues.");
        progress?.Report(new(OperationPhase.Validating,
            Message: "Validating affected paths from the saved sync plan"));
        ManagedSyncerApplyResult result = await syncer.ApplyPlanAsync(plan, progress, ct)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(result.PlanDigest, plan.PlanDigest))
            throw new InvalidOperationException("syncer applied an unexpected plan digest.");
        progress?.Report(new(OperationPhase.Completed,
            Message: "Android synchronization completed"));
        TryDeletePlan(plan.PlanFilePath);
        return new(result.CreatedDirectoryCount, result.CopiedFileCount,
            result.QuarantinedCount, result.TransferredBytes, result.RecoveryId,
            result.DeviceSerial, plan.Issues);
    }

    public async Task<DeviceSyncRestoreResult> RestoreAsync(
        DeviceSyncRestoreRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Destination))
            throw new ArgumentException("An Android destination is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RecoveryId))
            throw new ArgumentException("A recovery run identifier is required.", nameof(request));
        progress?.Report(new(OperationPhase.Validating,
            Message: "Restoring the previous Android synchronization"));
        DeviceSyncRestoreResult result = await syncer.RestoreAsync(request, progress, ct)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(result.RecoveryId, request.RecoveryId.Trim()))
            throw new InvalidOperationException("syncer restored an unexpected recovery run.");
        progress?.Report(new(OperationPhase.Completed,
            Message: "Android synchronization restored"));
        return result;
    }

    private static string CreatePlanFilePath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MusicLibraryManager", "syncer-plans");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
    }

    internal static void TryDeletePlan(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); }
        catch { }
    }

    private static void Validate(DeviceSyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Source))
            throw new ArgumentException("A local source directory is required.", nameof(request));
        if (!Directory.Exists(request.Source))
            throw new DirectoryNotFoundException($"Source directory was not found: {request.Source}");
        if (string.IsNullOrWhiteSpace(request.Destination))
            throw new ArgumentException("An Android destination is required.", nameof(request));
        if (request.MtimeToleranceSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MtimeToleranceSeconds cannot be negative.");
        if (request.MaxRemovals is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxRemovals cannot be negative.");
    }
}
