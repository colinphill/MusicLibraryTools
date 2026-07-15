namespace MusicLibrary.Core.Models;

public enum OperationJournalKind
{
    Ingest,
    Organize,
    Sync,
    Device,
    Other,
}

public enum OperationJournalState
{
    Completed,
    Interrupted,
    RolledBack,
    Unknown,
}

/// <summary>A lightweight, read-only description of one discovered mutation/quarantine run.</summary>
public sealed record OperationJournalSummary(
    string ToolName,
    OperationJournalKind Kind,
    OperationJournalState State,
    string RunPath,
    string? JournalPath,
    DateTimeOffset CreatedAtUtc,
    int? AffectedItemCount);

public sealed record OperationJournalDiscoveryResult(
    IReadOnlyList<OperationJournalSummary> Runs,
    IReadOnlyList<string> Warnings);

public enum OperationEntryKind
{
    Quarantined,
    Moved,
    Created,
    Deleted,
    Planned,
    Unknown,
}

/// <summary>One journaled or physically discovered item mapped back to its original path.</summary>
public sealed record OperationFileEntry(
    string OriginalPath,
    string? CurrentPath,
    string RelativePath,
    OperationEntryKind Kind,
    bool Exists,
    bool IsDirectory);

public sealed record OperationBrowseResult(
    string OriginalRoot,
    IReadOnlyList<OperationFileEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record OperationPathSnapshot(
    bool Exists,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record OperationRestoreAction(
    string SourcePath,
    string DestinationPath,
    string CollisionBackupPath,
    OperationPathSnapshot SourceSnapshot,
    OperationPathSnapshot DestinationSnapshot,
    OperationEntryKind OriginalKind);

public sealed record OperationRestorePlan(
    OperationJournalSummary Run,
    string RestoreJournalPath,
    IReadOnlyList<OperationRestoreAction> Actions,
    int SkippedCount)
{
    public int CollisionCount => Actions.Count(action => action.DestinationSnapshot.Exists);
    public bool CanApply => Actions.Count > 0;
}

public sealed record OperationRestoreResult(int RestoredCount, int CollisionBackupCount);
