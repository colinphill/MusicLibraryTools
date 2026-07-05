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
}
