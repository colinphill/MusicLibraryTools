namespace MusicLibrary.Core.Models;

/// <summary>A single planned rename/move from the current path to its canonical location.</summary>
public sealed record PlannedMove(string Source, string Destination);

/// <summary>Outcome of applying a set of planned moves.</summary>
public sealed record OrganizeResult(int Moved, IReadOnlyList<(string Source, string Error)> Errors)
{
    /// <summary>Cache-refresh failures for files that were successfully moved on disk.</summary>
    public IReadOnlyList<(string Source, string Error)> CacheErrors { get; init; } = [];

    public int MoveFailedCount => Errors.Count;
    public int CacheFailedCount => CacheErrors.Count;
    public int FailedCount => MoveFailedCount + CacheFailedCount;
}
