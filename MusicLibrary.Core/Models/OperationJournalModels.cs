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
