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
    bool IsDirectory,
    RecoveryPayloadKind PayloadKind = RecoveryPayloadKind.FullOriginal,
    long RetainedBytes = 0,
    long OriginalBytes = 0,
    long PostEditBytes = 0,
    string? OriginalSha256 = null,
    string? PostEditSha256 = null,
    string? DeltaPath = null,
    DateTime? OriginalLastWriteTimeUtc = null,
    FileAttributes? OriginalAttributes = null,
    string? PayloadSha256 = null);

public sealed record OperationBrowseResult(
    string OriginalRoot,
    IReadOnlyList<OperationFileEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record OperationPathSnapshot(
    bool Exists,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc)
{
    /// <summary>
    /// Optional normalized path for plans that validate snapshots independently of a containing
    /// action. Older restore plans keep the path on their action and leave this unset.
    /// </summary>
    public string? Path { get; init; }

    public static OperationPathSnapshot Missing(string path) =>
        new(false, false, 0, DateTime.MinValue) { Path = System.IO.Path.GetFullPath(path) };
}

public sealed record OperationRestoreAction(
    string SourcePath,
    string DestinationPath,
    string CollisionBackupPath,
    OperationPathSnapshot SourceSnapshot,
    OperationPathSnapshot DestinationSnapshot,
    OperationEntryKind OriginalKind,
    RecoveryPayloadKind PayloadKind = RecoveryPayloadKind.FullOriginal,
    string? OriginalSha256 = null,
    string? PostEditSha256 = null,
    long OriginalLength = 0,
    long PostEditLength = 0,
    DateTime? OriginalLastWriteTimeUtc = null,
    FileAttributes? OriginalAttributes = null,
    string? PayloadSha256 = null);

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

/// <summary>
/// A restore transaction spanning one or more recovery runs. All sources and destinations are
/// prevalidated under one mutation lease before the first live file is changed.
/// </summary>
public sealed record OperationRestoreBatchPlan(
    IReadOnlyList<OperationRestorePlan> Plans)
{
    public IReadOnlyList<OperationRestoreAction> Actions =>
        Plans.SelectMany(plan => plan.Actions).ToArray();

    public bool CanApply => Actions.Count > 0;
}

public sealed record OperationRestoreBatchResult(
    int RestoredCount,
    int CollisionBackupCount,
    IReadOnlyList<string> RestoreJournalPaths);

/// <summary>
/// Durable state of an interrupted restore transaction. Unapplied means that any work observed
/// before the commit point has been rolled back; Committed means the restored files reached the
/// durable commit point but payload cleanup was interrupted; Consumed means cleanup completed.
/// </summary>
public enum OperationRestoreTransitionState
{
    Unapplied = 0,
    Committed = 1,
    Consumed = 2,
}

/// <summary>One filesystem item captured by an explicit operation-retention preview.</summary>
public sealed record OperationPurgeManifestEntry(
    string RelativePath,
    bool IsDirectory,
    bool IsReparsePoint,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record OperationPurgeRun(
    OperationJournalSummary Run,
    string StagingPath,
    IReadOnlyList<OperationPurgeManifestEntry> Manifest)
{
    public int FileCount => Manifest.Count(entry => !entry.IsDirectory);
    public int DirectoryCount => Manifest.Count(entry => entry.IsDirectory);
    public long TotalBytes => Manifest.Sum(entry => entry.Length);
    public int RestoreBackupFileCount => Manifest.Count(entry => !entry.IsDirectory &&
        (entry.RelativePath.Equals(".MusicLibrary.App-restore", StringComparison.OrdinalIgnoreCase) ||
         entry.RelativePath.StartsWith(".MusicLibrary.App-restore" + Path.DirectorySeparatorChar,
             StringComparison.OrdinalIgnoreCase)));
}

/// <summary>A no-write retention review. Apply revalidates every manifest before staging a run.</summary>
public sealed record OperationPurgePlan(
    int RetentionDays,
    DateTimeOffset CutoffUtc,
    IReadOnlyList<OperationPurgeRun> Runs,
    int ProtectedInterruptedCount,
    int ProtectedUnsafeCount,
    int NewerCount)
{
    public int FileCount => Runs.Sum(run => run.FileCount);
    public int DirectoryCount => Runs.Sum(run => run.DirectoryCount);
    public long TotalBytes => Runs.Sum(run => run.TotalBytes);
    public int RestoreBackupFileCount => Runs.Sum(run => run.RestoreBackupFileCount);
    public bool CanApply => Runs.Count > 0;
}

public sealed record OperationPurgeResult(int RunsDeleted, int FilesDeleted, long BytesDeleted);
