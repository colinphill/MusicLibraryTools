using System.Runtime.InteropServices;
using System.Text;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Identifies the mounted filesystem that owns a path. Implementations must also support paths
/// that do not exist yet so recovery and staging locations can be validated before creation.
/// </summary>
public interface IFileSystemVolumeIdentityProvider
{
    FileSystemVolumeIdentity GetIdentity(string path);
}

public sealed record FileSystemVolumeIdentity(
    string Key,
    string RootPath);

/// <summary>
/// Resolves Windows volume GUIDs and the longest mounted-filesystem root on Unix. When the
/// platform cannot enumerate mounts, the fallback is deliberately conservative and treats the
/// nearest existing directory as its own volume rather than combining potentially distinct
/// filesystems.
/// </summary>
public sealed class FileSystemVolumeIdentityProvider :
    IFileSystemVolumeIdentityProvider
{
    private static readonly TimeSpan MountRefreshInterval =
        TimeSpan.FromSeconds(1);
    private readonly object _mountGate = new();
    private string[] _unixMountRoots = [];
    private DateTime _nextMountRefreshUtc;

    public FileSystemVolumeIdentity GetIdentity(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() &&
            TryGetWindowsVolume(fullPath, out FileSystemVolumeIdentity? windows))
            return windows!;

        if (!OperatingSystem.IsWindows())
        {
            string? mount = GetUnixMountRoots()
                .FirstOrDefault(root => IsWithin(fullPath, root));
            if (mount is not null)
                return new(
                    "mount:" + NormalizeKey(mount),
                    mount);
        }

        string fallback = NearestExistingDirectory(fullPath);
        return new(
            "fallback:" + NormalizeKey(fallback),
            fallback);
    }

    private string[] GetUnixMountRoots()
    {
        lock (_mountGate)
        {
            DateTime now = DateTime.UtcNow;
            if (_unixMountRoots.Length > 0 &&
                now < _nextMountRefreshUtc)
                return _unixMountRoots;

            var roots = new HashSet<string>(PathComparer);
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                    AddMountRoot(roots, drive.RootDirectory.FullName);
            }
            catch
            {
            }

            if (OperatingSystem.IsLinux())
            {
                try
                {
                    foreach (string line in
                             File.ReadLines("/proc/self/mountinfo"))
                    {
                        string[] fields = line.Split(
                            ' ',
                            StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length > 4)
                            AddMountRoot(
                                roots,
                                DecodeMountPath(fields[4]));
                    }
                }
                catch
                {
                }
            }

            if (roots.Count == 0)
            {
                string root =
                    Path.GetPathRoot(
                        Path.GetFullPath(
                            Environment.CurrentDirectory)) ??
                    Path.DirectorySeparatorChar.ToString();
                AddMountRoot(roots, root);
            }

            _unixMountRoots =
            [
                .. roots.OrderByDescending(
                        root => root.Length)
                    .ThenBy(
                        root => root,
                        PathComparer),
            ];
            _nextMountRefreshUtc =
                now + MountRefreshInterval;
            return _unixMountRoots;
        }
    }

    private static void AddMountRoot(
        HashSet<string> roots,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            roots.Add(
                PreserveRoot(
                    Path.GetFullPath(path)));
        }
        catch
        {
        }
    }

    private static bool TryGetWindowsVolume(
        string path,
        out FileSystemVolumeIdentity? identity)
    {
        identity = null;
        string probe = ExistingProbePath(path);
        var mountBuffer = new StringBuilder(1024);
        if (!GetVolumePathName(
                probe,
                mountBuffer,
                mountBuffer.Capacity))
            return false;

        string mount = PreserveRoot(
            Path.GetFullPath(
                mountBuffer.ToString()));
        var nameBuffer = new StringBuilder(1024);
        string key = GetVolumeNameForVolumeMountPoint(
            EnsureTrailingSeparator(mount),
            nameBuffer,
            nameBuffer.Capacity)
            ? nameBuffer.ToString()
            : mount;
        identity = new(
            "windows:" + key.ToUpperInvariant(),
            mount);
        return true;
    }

    private static string ExistingProbePath(string path)
    {
        string current = path;
        while (!File.Exists(current) &&
               !Directory.Exists(current))
        {
            string? parent =
                Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                PathComparer.Equals(parent, current))
                break;
            current = parent;
        }
        return current;
    }

    private static string NearestExistingDirectory(
        string path)
    {
        string current = File.Exists(path)
            ? Path.GetDirectoryName(path)!
            : path;
        while (!Directory.Exists(current))
        {
            string? parent =
                Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                PathComparer.Equals(parent, current))
            {
                return PreserveRoot(
                    Path.GetPathRoot(path) ??
                    current);
            }
            current = parent;
        }
        return PreserveRoot(
            Path.GetFullPath(current));
    }

    private static bool IsWithin(
        string path,
        string root)
    {
        string normalizedPath =
            Path.GetFullPath(path);
        string normalizedRoot =
            PreserveRoot(
                Path.GetFullPath(root));
        if (PathComparer.Equals(
                normalizedPath,
                normalizedRoot))
            return true;
        string prefix = EnsureTrailingSeparator(
            normalizedRoot);
        return normalizedPath.StartsWith(
            prefix,
            PathComparison);
    }

    private static string PreserveRoot(string path)
    {
        string root = Path.GetPathRoot(path) ?? "";
        return PathComparer.Equals(path, root)
            ? root
            : Path.TrimEndingDirectorySeparator(path);
    }

    private static string EnsureTrailingSeparator(
        string path) =>
        Path.EndsInDirectorySeparator(path)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string NormalizeKey(string path)
    {
        string normalized = PreserveRoot(
            Path.GetFullPath(path));
        return OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    private static string DecodeMountPath(
        string value)
    {
        var decoded = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' &&
                index + 3 < value.Length &&
                IsOctal(value[index + 1]) &&
                IsOctal(value[index + 2]) &&
                IsOctal(value[index + 3]))
            {
                int code =
                    (value[index + 1] - '0') * 64 +
                    (value[index + 2] - '0') * 8 +
                    value[index + 3] - '0';
                decoded.Append((char)code);
                index += 3;
            }
            else
            {
                decoded.Append(value[index]);
            }
        }
        return decoded.ToString();
    }

    private static bool IsOctal(char value) =>
        value is >= '0' and <= '7';

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string fileName,
        StringBuilder volumePathName,
        int bufferLength);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool
        GetVolumeNameForVolumeMountPoint(
            string volumeMountPoint,
            StringBuilder volumeName,
            int bufferLength);
}
