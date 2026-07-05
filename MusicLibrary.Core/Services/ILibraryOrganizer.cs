using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Reorganizes files into their canonical Artist/Album/## Title layout (reimplements OrganizeFiles).
/// Preview never touches the filesystem; Apply performs the moves the user confirmed.
/// </summary>
public interface ILibraryOrganizer
{
    /// <summary>Compute the moves needed to canonicalize the library. No filesystem changes.</summary>
    Task<IReadOnlyList<PlannedMove>> PreviewMovesAsync(CancellationToken ct = default);

    /// <summary>Apply the given moves, then clean up emptied folders. Reports per-file progress.</summary>
    Task<OrganizeResult> ApplyMovesAsync(
        IReadOnlyList<PlannedMove> moves, IProgress<int>? progress = null, CancellationToken ct = default);
}
