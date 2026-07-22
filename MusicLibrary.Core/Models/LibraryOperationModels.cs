using System.Collections.Immutable;

namespace MusicLibrary.Core.Models;

public enum OperationPhase
{
    LoadingConfiguration,
    LoadingLibrary,
    IndexingSources,
    InventoryingDestination,
    Planning,
    Validating,
    Applying,
    RollingBack,
    Completed,
}

public enum OperationItemStatus
{
    InProgress,
    Complete,
    Failed,
}

public sealed record OperationProgress(
    OperationPhase Phase,
    int Completed = 0,
    int? Total = null,
    string? CurrentPath = null,
    string? Message = null,
    OperationItemStatus? ItemStatus = null);

public enum OperationIssueSeverity { Information, Warning, Blocker }

public sealed record OperationIssue(
    string Code,
    OperationIssueSeverity Severity,
    string Message,
    string? Path = null);

public enum FileMutationKind { Copy, Replace, Write, ReplaceGenerated, Quarantine, Delete }

/// <summary>
/// One immutable filesystem decision. Source and destination snapshots describe the exact state
/// reviewed during preview; execution rejects the complete plan before its first mutation if any
/// snapshot has become stale.
/// </summary>
public sealed record FileMutationAction(
    FileMutationKind Kind,
    string SourcePath,
    string DestinationPath,
    OperationPathSnapshot? ExpectedSource,
    OperationPathSnapshot? ExpectedDestination,
    ImmutableArray<byte> Content = default);

public sealed record FileMutationPlan(
    string ToolName,
    string DestinationRoot,
    string RecoveryRoot,
    IReadOnlyList<FileMutationAction> Actions,
    IReadOnlyList<OperationIssue> Issues,
    DateTimeOffset CreatedAtUtc,
    bool RetainRecovery = true,
    string? PolicyFingerprint = null,
    Guid? LibraryId = null)
{
    public bool CanApply => Issues.All(issue => issue.Severity != OperationIssueSeverity.Blocker);
}

public sealed record FileMutationSummary(
    int Copied,
    int Replaced,
    int Quarantined,
    int Deleted,
    string? JournalPath,
    IReadOnlyList<OperationIssue> Issues);

public sealed record FileInventory(
    string Root,
    IReadOnlyDictionary<string, OperationPathSnapshot> Files,
    IReadOnlyList<string> Directories,
    DateTimeOffset CapturedAtUtc);
