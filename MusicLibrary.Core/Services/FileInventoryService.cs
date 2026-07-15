using System.IO.Enumeration;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IFileInventoryService
{
    Task<FileInventory> CaptureAsync(
        string root,
        Func<string, bool>? includeFile = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Captures file identity and directory structure in one buffered traversal. FileSystemEntry
/// supplies size and timestamp from the enumeration record, avoiding a second metadata round-trip
/// per file on network shares.
/// </summary>
public sealed class FileInventoryService : IFileInventoryService
{
    public Task<FileInventory> CaptureAsync(
        string root,
        Func<string, bool>? includeFile = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return Task.Run(() => Capture(fullRoot, includeFile, progress, ct), ct);
    }

    private static FileInventory Capture(
        string root,
        Func<string, bool>? includeFile,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var files = new Dictionary<string, OperationPathSnapshot>(PathComparer);
        var directories = new List<string>();
        if (!Directory.Exists(root))
            return new(root, files, directories, DateTimeOffset.UtcNow);

        int count = 0;
        var enumerable = new FileSystemEnumerable<InventoryEntry>(root, Transform,
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                BufferSize = 64 * 1024,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => true,
        };
        foreach (InventoryEntry entry in enumerable)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
            {
                directories.Add(entry.Path);
                continue;
            }
            if (includeFile is not null && !includeFile(entry.Path))
                continue;
            files[entry.Path] = new(true, false, entry.Length, entry.LastWriteTimeUtc)
                { Path = entry.Path };
            count++;
            if ((count & 255) == 0)
                progress?.Report(new(OperationPhase.InventoryingDestination, count,
                    CurrentPath: entry.Path, Message: $"Inventoried {count:N0} files"));
        }

        directories.Sort(PathComparer);
        progress?.Report(new(OperationPhase.InventoryingDestination, count, count,
            root, $"Inventoried {count:N0} files"));
        return new(root, files, directories, DateTimeOffset.UtcNow);
    }

    private readonly record struct InventoryEntry(
        string Path,
        bool IsDirectory,
        long Length,
        DateTime LastWriteTimeUtc);

    private static InventoryEntry Transform(ref FileSystemEntry entry) =>
        new(entry.ToFullPath(), entry.IsDirectory, entry.IsDirectory ? 0 : entry.Length,
            entry.LastWriteTimeUtc.UtcDateTime);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
