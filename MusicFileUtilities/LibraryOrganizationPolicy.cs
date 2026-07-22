using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicLibraryTools;

/// <summary>
/// Resolves IndexTarget organization eligibility, including duplicate and nested roots. Excluded
/// targets remain indexed and available to every non-organization workflow.
/// </summary>
public static class LibraryOrganizationPolicy
{
    public static IReadOnlyList<LibraryIndexLocation> EligibleTargets(
        IEnumerable<LibraryIndexLocation> locations) =>
        locations
            .GroupBy(location => Path.TrimEndingDirectorySeparator(location.Target), PathComparer)
            .Where(group => group.All(CanOrganize))
            .Select(group =>
            {
                LibraryIndexLocation[] targets = group.ToArray();
                if (targets.Select(target => target.UseItunesCanonicalNaming).Distinct().Count() > 1)
                    throw new InvalidDataException(
                        $"IndexTarget '{targets[0].Target}' has conflicting ItunesCanonicalNaming values.");
                return targets[0];
            })
            .ToArray();

    public static IReadOnlyList<string> EligibleRoots(
        IEnumerable<LibraryIndexLocation> locations) =>
        EligibleTargets(locations)
            .Select(target => target.Target)
            .ToArray();

    public static bool IsPathEligible(
        string path, IEnumerable<LibraryIndexLocation> locations)
    {
        LibraryIndexLocation[] matches = locations
            .Where(location => IsWithinOrEqual(path, location.Target))
            .ToArray();
        return matches.Length > 0 && matches.All(CanOrganize);
    }

    /// <summary>
    /// Empty-folder cleanup is recursive, so an eligible parent containing an excluded nested root
    /// is omitted from cleanup. Files in the eligible portion can still be organized.
    /// </summary>
    public static IReadOnlyList<string> CleanupRoots(
        IEnumerable<LibraryIndexLocation> locations)
    {
        LibraryIndexLocation[] materialized = locations.ToArray();
        string[] excludedRoots = materialized
            .Where(location => !CanOrganize(location))
            .Select(location => Path.TrimEndingDirectorySeparator(location.Target))
            .Distinct(PathComparer)
            .ToArray();
        return EligibleRoots(materialized)
            .Where(root => !excludedRoots.Any(excluded => IsWithinOrEqual(excluded, root)))
            .ToArray();
    }

    private static bool IsWithinOrEqual(string path, string root)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return PathComparer.Equals(normalizedPath, normalizedRoot) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                   PathComparison);
    }

    private static bool CanOrganize(LibraryIndexLocation location) =>
        location.Organize &&
        location.Permissions.HasFlag(LibraryRootPermissions.OrganizeFiles);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
