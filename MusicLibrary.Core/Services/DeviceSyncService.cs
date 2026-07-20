using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MusicLibrary.Core.Models;

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
    int MaxRemovals = 0);

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

public interface IDeviceSyncService
{
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

public sealed record SyncerProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface ISyncerProcessRunner
{
    Task<SyncerProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class SyncerProcessRunner : ISyncerProcessRunner
{
    public async Task<SyncerProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        string executable = LocateExecutable();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        for (int index = 0; index < arguments.Count; index++)
        {
            start.ArgumentList.Add(arguments[index]);
            if (index == 0) start.ArgumentList.Add("--cancel-stdin");
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Unable to start syncer.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Unable to start syncer at '{executable}': {error.Message}", error);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = ConsumeProgressAsync(
            process.StandardError, progress, CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("cancel").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    process.StandardInput.Close();
                    Task gracefulExit = process.WaitForExitAsync(CancellationToken.None);
                    if (await Task.WhenAny(gracefulExit, Task.Delay(TimeSpan.FromSeconds(5))) != gracefulExit)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                        await gracefulExit.ConfigureAwait(false);
                }
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch { }
            throw;
        }

        string output = await stdout.ConfigureAwait(false);
        string errorOutput = await stderr.ConfigureAwait(false);
        return new(process.ExitCode, output, errorOutput);
    }

    private static async Task<string> ConsumeProgressAsync(
        StreamReader reader,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            lines.Add(line);
            if (!string.IsNullOrWhiteSpace(line))
                progress?.Report(new(OperationPhase.Applying, Message: line.Trim()));
        }
        return string.Join(Environment.NewLine, lines);
    }

    internal static string LocateExecutable()
    {
        string? configured = Environment.GetEnvironmentVariable("MLT_SYNCER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        string name = OperatingSystem.IsWindows() ? "syncer.exe" : "syncer";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "tools", "syncer", name),
            Path.Combine(AppContext.BaseDirectory, name),
        ];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                $"The packaged syncer executable was not found. Set MLT_SYNCER_PATH or place '{name}' under tools/syncer.");
    }
}

/// <summary>
/// Typed application adapter for syncer's managed Android mirror. Preview persists syncer's
/// deterministic action artifact; Apply executes that artifact after validating its digest,
/// device identity, source files, and affected destination paths without repeating full inventories.
/// Restore reverses a completed sync from its native recovery journal.
/// </summary>
public sealed class DeviceSyncService(ISyncerProcessRunner runner) : IDeviceSyncService
{
    public async Task<DeviceSyncInitializationResult> InitializeAsync(
        DeviceSyncInitializationRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Destination))
            throw new ArgumentException("An Android destination is required.", nameof(request));

        var arguments = new List<string> { "init" };
        AddConnectionArguments(arguments, request.AdbPath, request.DeviceSerial);
        if (request.Adopt) arguments.Add("--adopt");
        arguments.Add(request.Destination.Trim());
        progress?.Report(new(OperationPhase.Validating, Message: "Initializing managed Android destination"));
        SyncerProcessResult result = await runner.RunAsync(arguments, progress, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw SyncerFailure(result);
        return new(request.Destination.Trim(), request.DeviceSerial, request.Adopt,
            result.StandardOutput.Trim());
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
        SyncerProcessResult result;
        SyncerResponse response;
        try
        {
            var arguments = BuildPushArguments(request, dryRun: true, savePlan: planFile);
            result = await runner.RunAsync(arguments, progress, ct).ConfigureAwait(false);
            response = ParseResponse(result);
            if (result.ExitCode != 0 || response.Error is not null)
            {
                TryDeletePlan(planFile);
                return BlockedPlan(request, response, result);
            }
        }
        catch
        {
            TryDeletePlan(planFile);
            throw;
        }

        var issues = new List<OperationIssue>();
        if (response.RemovalCount > request.MaxRemovals)
            issues.Add(new("removal-limit", OperationIssueSeverity.Blocker,
                $"The plan removes {response.RemovalCount:N0} entries, exceeding MaxRemovals {request.MaxRemovals:N0}."));
        if (request.Direct)
            issues.Add(new("direct-mode", OperationIssueSeverity.Warning,
                "Direct mode does not stage the complete transfer before changing the destination."));

        return new(request, response.Device ?? request.DeviceSerial ?? "",
            response.PlanDigest ?? "", planFile, response.Actions,
            response.PlannedDirectories, response.PlannedFiles, response.RemovalCount,
            response.PlannedBytes, issues, DateTimeOffset.UtcNow);
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
        var arguments = BuildApplyArguments(plan);
        SyncerProcessResult result = await runner.RunAsync(arguments, progress, ct).ConfigureAwait(false);
        SyncerResponse response = ParseResponse(result);
        if (result.ExitCode != 0 || response.Error is not null)
            throw SyncerFailure(result, response);
        if (!StringComparer.Ordinal.Equals(response.PlanDigest, plan.PlanDigest))
            throw new InvalidOperationException("syncer applied an unexpected plan digest.");
        progress?.Report(new(OperationPhase.Completed,
            Message: "Android synchronization completed"));
        TryDeletePlan(plan.PlanFilePath);
        return new(response.AppliedDirectories, response.AppliedFiles,
            response.QuarantinedCount, response.TransferredBytes,
            response.RecoveryId, response.Device ?? plan.DeviceSerial, plan.Issues);
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
        var arguments = new List<string> { "restore", "--json" };
        AddConnectionArguments(arguments, request.AdbPath, request.DeviceSerial);
        arguments.Add("--recovery");
        arguments.Add(request.RecoveryId.Trim());
        arguments.Add(request.Destination.Trim());
        SyncerProcessResult process = await runner.RunAsync(arguments, progress, ct).ConfigureAwait(false);
        RestoreResponse response = ParseRestoreResponse(process);
        if (process.ExitCode != 0 || response.Error is not null)
            throw SyncerFailure(process, response.Error?.Message);
        if (!StringComparer.Ordinal.Equals(response.RecoveryId, request.RecoveryId.Trim()))
            throw new InvalidOperationException("syncer restored an unexpected recovery run.");
        progress?.Report(new(OperationPhase.Completed,
            Message: "Android synchronization restored"));
        return new(request.Destination.Trim(), response.RecoveryId,
            response.Device ?? request.DeviceSerial ?? "");
    }

    private static List<string> BuildPushArguments(
        DeviceSyncRequest request, bool dryRun, string? savePlan)
    {
        var arguments = new List<string> { "push", "--json" };
        AddConnectionArguments(arguments, request.AdbPath, request.DeviceSerial);
        foreach (string exclusion in request.Exclusions ?? [])
        {
            if (string.IsNullOrWhiteSpace(exclusion)) continue;
            arguments.Add("--exclude");
            arguments.Add(exclusion.Trim());
        }
        arguments.Add("--mtime-tolerance");
        arguments.Add(request.MtimeToleranceSeconds.ToString(CultureInfo.InvariantCulture));
        if (!request.DeleteExtras) arguments.Add("--no-delete");
        if (request.Direct) arguments.Add("--direct");
        if (dryRun) arguments.Add("--dry-run");
        if (savePlan is not null)
        {
            arguments.Add("--save-plan");
            arguments.Add(savePlan);
        }
        arguments.Add(request.Source);
        arguments.Add(request.Destination);
        return arguments;
    }

    private static List<string> BuildApplyArguments(DeviceSyncPlan plan)
    {
        var arguments = new List<string> { "apply", "--json" };
        AddConnectionArguments(arguments, plan.Request.AdbPath, plan.Request.DeviceSerial);
        arguments.Add("--plan");
        arguments.Add(plan.PlanFilePath);
        return arguments;
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

    private static void AddConnectionArguments(List<string> arguments, string? adbPath, string? serial)
    {
        if (!string.IsNullOrWhiteSpace(adbPath))
        {
            arguments.Add("--adb");
            arguments.Add(adbPath.Trim());
        }
        if (!string.IsNullOrWhiteSpace(serial))
        {
            arguments.Add("--serial");
            arguments.Add(serial.Trim());
        }
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
        if (request.MaxRemovals < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxRemovals cannot be negative.");
    }

    private static DeviceSyncPlan BlockedPlan(
        DeviceSyncRequest request, SyncerResponse response, SyncerProcessResult process)
    {
        string message = response.Error?.Message ?? process.StandardError.Trim();
        if (string.IsNullOrWhiteSpace(message))
            message = $"syncer exited with code {process.ExitCode}.";
        return new(request, response.Device ?? request.DeviceSerial ?? "", "", "", response.Actions,
            response.PlannedDirectories, response.PlannedFiles, response.RemovalCount,
            response.PlannedBytes,
            [new("syncer-" + (response.Error?.Code ?? process.ExitCode),
                OperationIssueSeverity.Blocker, message)], DateTimeOffset.UtcNow);
    }

    private static Exception SyncerFailure(SyncerProcessResult process, SyncerResponse? response = null)
    {
        string message = response?.Error?.Message ?? process.StandardError.Trim();
        if (string.IsNullOrWhiteSpace(message) && response is null)
            message = process.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(message))
            message = $"syncer exited with code {process.ExitCode}.";
        return new InvalidOperationException(message);
    }

    private static Exception SyncerFailure(SyncerProcessResult process, string? nativeMessage)
    {
        string message = nativeMessage ?? process.StandardError.Trim();
        if (string.IsNullOrWhiteSpace(message)) message = process.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(message)) message = $"syncer exited with code {process.ExitCode}.";
        return new InvalidOperationException(message);
    }

    private static RestoreResponse ParseRestoreResponse(SyncerProcessResult result)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1)
                throw new InvalidDataException("Unsupported syncer JSON schema.");
            SyncerError? error = null;
            if (root.TryGetProperty("error", out JsonElement errorElement) &&
                errorElement.ValueKind == JsonValueKind.Object)
                error = new(errorElement.GetProperty("code").GetInt32(),
                    errorElement.GetProperty("message").GetString() ?? "syncer failed");
            return new(
                root.TryGetProperty("device", out JsonElement device) && device.ValueKind == JsonValueKind.String
                    ? device.GetString() : null,
                root.TryGetProperty("recovery_id", out JsonElement recovery) && recovery.ValueKind == JsonValueKind.String
                    ? recovery.GetString() ?? "" : "",
                error);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("syncer returned invalid JSON output.", error);
        }
    }

    private static SyncerResponse ParseResponse(SyncerProcessResult result)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1)
                throw new InvalidDataException("Unsupported syncer JSON schema.");
            var actions = new List<DeviceSyncAction>();
            if (root.TryGetProperty("actions", out JsonElement actionArray))
            {
                foreach (JsonElement action in actionArray.EnumerateArray())
                {
                    string type = action.GetProperty("type").GetString() ?? "other";
                    actions.Add(new(ParseKind(action.GetProperty("action").GetString()),
                        action.GetProperty("path").GetString() ?? "",
                        action.GetProperty("reason").GetString() ?? "",
                        type == "directory",
                        checked((long)action.GetProperty("size").GetUInt64()),
                        action.GetProperty("modified_seconds").GetInt64()));
                }
            }
            JsonElement planned = root.GetProperty("planned");
            JsonElement applied = root.GetProperty("applied");
            SyncerError? error = null;
            if (root.TryGetProperty("error", out JsonElement errorElement) &&
                errorElement.ValueKind == JsonValueKind.Object)
                error = new(errorElement.GetProperty("code").GetInt32(),
                    errorElement.GetProperty("message").GetString() ?? "syncer failed");
            return new(
                root.TryGetProperty("device", out JsonElement device) && device.ValueKind == JsonValueKind.String
                    ? device.GetString() : null,
                root.TryGetProperty("plan_digest", out JsonElement digest) && digest.ValueKind == JsonValueKind.String
                    ? digest.GetString() : null,
                root.TryGetProperty("plan_file", out JsonElement planFile) && planFile.ValueKind == JsonValueKind.String
                    ? planFile.GetString() : null,
                root.TryGetProperty("recovery_id", out JsonElement recovery) && recovery.ValueKind == JsonValueKind.String
                    ? recovery.GetString() : null,
                actions,
                planned.GetProperty("directories").GetInt32(),
                planned.GetProperty("files").GetInt32(),
                root.TryGetProperty("removal_count", out JsonElement removals) ? removals.GetInt32() :
                    planned.GetProperty("deletions").GetInt32(),
                checked((long)planned.GetProperty("bytes").GetUInt64()),
                applied.GetProperty("directories").GetInt32(),
                applied.GetProperty("files").GetInt32(),
                applied.GetProperty("deletions").GetInt32(),
                root.TryGetProperty("quarantined_count", out JsonElement quarantined)
                    ? quarantined.GetInt32()
                    : applied.GetProperty("deletions").GetInt32(),
                checked((long)root.GetProperty("transferred_bytes").GetUInt64()), error);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("syncer returned invalid JSON output.", error);
        }
    }

    private static DeviceSyncMutationKind ParseKind(string? value) => value switch
    {
        "create_directory" => DeviceSyncMutationKind.CreateDirectory,
        "replace_with_directory" => DeviceSyncMutationKind.ReplaceWithDirectory,
        "add_file" => DeviceSyncMutationKind.AddFile,
        "update_file" => DeviceSyncMutationKind.UpdateFile,
        "replace_with_file" => DeviceSyncMutationKind.ReplaceWithFile,
        "delete_file" => DeviceSyncMutationKind.DeleteFile,
        "delete_directory" => DeviceSyncMutationKind.DeleteDirectory,
        "delete_other" => DeviceSyncMutationKind.DeleteOther,
        _ => throw new InvalidDataException($"Unknown syncer plan action '{value}'."),
    };

    private sealed record SyncerError(int Code, string Message);
    private sealed record RestoreResponse(string? Device, string RecoveryId, SyncerError? Error);
    private sealed record SyncerResponse(
        string? Device,
        string? PlanDigest,
        string? PlanFile,
        string? RecoveryId,
        IReadOnlyList<DeviceSyncAction> Actions,
        int PlannedDirectories,
        int PlannedFiles,
        int RemovalCount,
        long PlannedBytes,
        int AppliedDirectories,
        int AppliedFiles,
        int AppliedDeletions,
        int QuarantinedCount,
        long TransferredBytes,
        SyncerError? Error);
}
