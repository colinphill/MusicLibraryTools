namespace MusicLibrary.Core.Services;

/// <summary>Writes replacement files through a flushed sibling temporary file.</summary>
internal static class AtomicFile
{
    public static void Write(string path, Action<Stream> write)
        => WriteMany([(path, write)]);

    /// <summary>
    /// Stages every document before replacing any target and rolls already-replaced targets back
    /// if a later replacement fails. This keeps a portable configuration and its machine-binding
    /// companion consistent for all ordinary I/O failures.
    /// </summary>
    public static void WriteMany(
        IReadOnlyList<(string Path, Action<Stream> Write)> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return;

        var staged = new List<StagedWrite>(writes.Count);
        var committed = new List<StagedWrite>(writes.Count);
        var retainedBackups = new HashSet<string>(PathComparer);
        try
        {
            var targets = new HashSet<string>(PathComparer);
            foreach ((string path, Action<Stream> write) in writes)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                ArgumentNullException.ThrowIfNull(write);
                string fullPath = Path.GetFullPath(path);
                if (!targets.Add(fullPath))
                    throw new ArgumentException(
                        $"Atomic write contains duplicate target '{fullPath}'.", nameof(writes));
                string directory = Path.GetDirectoryName(fullPath)
                    ?? throw new InvalidOperationException(
                        $"Cannot determine the directory for '{path}'.");
                Directory.CreateDirectory(directory);
                string token = Guid.NewGuid().ToString("N");
                string temporary = Path.Combine(directory,
                    $".{Path.GetFileName(fullPath)}.{token}.tmp");
                string? backup = File.Exists(fullPath)
                    ? Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{token}.rollback")
                    : null;
                var item = new StagedWrite(fullPath, temporary, backup);
                staged.Add(item);
                using var stream = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 64 * 1024, FileOptions.WriteThrough);
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            foreach (StagedWrite item in staged)
            {
                if (item.Backup is not null)
                    File.Copy(item.Target, item.Backup, overwrite: false);
                File.Move(item.Temporary, item.Target, overwrite: true);
                committed.Add(item);
            }
        }
        catch
        {
            foreach (StagedWrite item in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (item.Backup is not null && File.Exists(item.Backup))
                        File.Move(item.Backup, item.Target, overwrite: true);
                    else if (File.Exists(item.Target))
                        File.Delete(item.Target);
                }
                catch
                {
                    // Preserve the original exception. Any rollback artifact is retained below if
                    // it cannot be restored, so a caller can recover it manually.
                    if (item.Backup is not null)
                        retainedBackups.Add(item.Backup);
                }
            }
            throw;
        }
        finally
        {
            foreach (StagedWrite item in staged)
            {
                try { File.Delete(item.Temporary); }
                catch { /* best effort after a failed replacement */ }
                try
                {
                    if (item.Backup is not null && !retainedBackups.Contains(item.Backup))
                        File.Delete(item.Backup);
                }
                catch { /* retain a rollback copy if cleanup fails */ }
            }
        }
    }

    private sealed record StagedWrite(string Target, string Temporary, string? Backup);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
