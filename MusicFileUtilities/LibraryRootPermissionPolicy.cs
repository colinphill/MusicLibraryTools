#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicLibraryTools;

/// <summary>
/// Resolves independently enforced write capabilities for a path. Nested or duplicate roots are
/// conservative: every matching root must permit the requested mutation.
/// </summary>
public static class LibraryRootPermissionPolicy
{
    public static bool Allows(
        string path,
        IEnumerable<LibraryIndexLocation> locations,
        LibraryRootPermissions permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(locations);
        if (permission == LibraryRootPermissions.None)
            return true;

        LibraryIndexLocation[] matches = locations.Where(location =>
            IsWithinOrEqual(path, location.Target)).ToArray();
        return matches.Length > 0 && matches.All(location =>
            location.Permissions.HasFlag(permission));
    }

    public static LibraryIndexLocation? MostSpecific(
        string path,
        IEnumerable<LibraryIndexLocation> locations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(locations);
        return locations.Where(location => IsWithinOrEqual(path, location.Target))
            .OrderByDescending(location => Path.GetFullPath(location.Target).Length)
            .FirstOrDefault();
    }

    private static bool IsWithinOrEqual(string path, string root)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return PathComparer.Equals(normalizedPath, normalizedRoot) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                   PathComparison);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
