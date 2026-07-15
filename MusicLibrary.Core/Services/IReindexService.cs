using MusicFileUtilities;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Re-indexes a single file into the metadata cache immediately after it is edited on disk, so the
/// database stays in sync without a full re-scan. Implemented by the library service (which owns the
/// database connection).
/// </summary>
public interface IReindexService
{
    /// <summary>Re-parse the file and refresh its rows in the cache. No-op if no library is loaded.</summary>
    Task ReindexFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Refresh the cache from the media object that was just saved, avoiding a second read of a
    /// potentially remote file. Implementations that cannot consume it fall back to reparsing.
    /// </summary>
    Task ReindexFileAsync(string path, IMediaFile savedFile, CancellationToken ct = default)
        => ReindexFileAsync(path, ct);

    /// <summary>
    /// Refresh several saved parser objects as one cache operation. Implementations without a
    /// batch path retain correctness by falling back to the single-file overload.
    /// </summary>
    async Task ReindexFilesAsync(
        IReadOnlyList<(string Path, IMediaFile File)> files,
        CancellationToken ct = default)
    {
        foreach (var file in files)
            await ReindexFileAsync(file.Path, file.File, ct);
    }
}
