using System.Collections.Immutable;

namespace MusicLibraryManager.Presentation;

/// <summary>
/// Stable, non-localized identity for the Library result scope captured by a
/// Workbench handoff.
/// </summary>
public enum WorkbenchHandoffScopeKind
{
    Selected = 0,
    VisibleResults = 1,
    AllResults = 2,
}

/// <summary>
/// An immutable action scope captured from the Library surface. Both the
/// visible selection bar and its grid context menu use the same snapshot so a
/// later selection or filter change cannot redirect an already-open action.
/// </summary>
public sealed record LibraryActionScopeSnapshot(
    WorkbenchHandoffScopeKind ScopeKind,
    ImmutableArray<string> CapturedPaths,
    bool CanHandoff,
    bool CanCopyPaths,
    bool CanReveal,
    bool CanRefreshAffectedPaths)
{
    public bool HasPaths => !CapturedPaths.IsDefaultOrEmpty;
}

/// <summary>
/// An immutable snapshot of a request to continue Library work in a specific
/// Workbench destination. Paths are captured when the request is created so a
/// later filter or selection change cannot change its meaning.
/// </summary>
public sealed record WorkbenchHandoffRequest(
    WorkbenchSection DestinationSection,
    WorkbenchHandoffScopeKind ScopeKind,
    ImmutableArray<string> CapturedPaths)
{
    public static WorkbenchHandoffRequest Create(
        WorkbenchSection destinationSection,
        WorkbenchHandoffScopeKind scopeKind,
        IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return new(
            destinationSection,
            scopeKind,
            [
                .. paths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(comparer),
            ]);
    }
}
