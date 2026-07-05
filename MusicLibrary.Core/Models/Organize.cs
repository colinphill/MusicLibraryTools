namespace MusicLibrary.Core.Models;

/// <summary>A single planned rename/move from the current path to its canonical location.</summary>
public sealed record PlannedMove(string Source, string Destination);

/// <summary>Outcome of applying a set of planned moves.</summary>
public sealed record OrganizeResult(int Moved, IReadOnlyList<(string Source, string Error)> Errors)
{
    public int FailedCount => Errors.Count;
}
