namespace MusicLibrary.Core.Models;

/// <summary>
/// A single reviewed rename/move from the current path to its canonical location. Snapshots are
/// populated by Core previews so apply can reject stale plans before moving the first file.
/// </summary>
public sealed record PlannedMove(
    string Source,
    string Destination,
    OperationPathSnapshot? ExpectedSource = null,
    OperationPathSnapshot? ExpectedDestination = null);

/// <summary>Outcome of applying a set of planned moves.</summary>
public sealed record OrganizeResult(int Moved, IReadOnlyList<(string Source, string Error)> Errors)
{
    public string? JournalPath { get; init; }

    /// <summary>Cache-refresh failures for files that were successfully moved on disk.</summary>
    public IReadOnlyList<(string Source, string Error)> CacheErrors { get; init; } = [];

    public int MoveFailedCount => Errors.Count;
    public int CacheFailedCount => CacheErrors.Count;
    public int FailedCount => MoveFailedCount + CacheFailedCount;
}
