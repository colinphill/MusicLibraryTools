using MetadataCaching;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Owns the single <see cref="MetadataDatabase"/> connection (opened from the loaded
/// LibraryConfiguration's cache file) and serializes all access to it. Provides background
/// indexing (with progress + cancellation) and building a browsable snapshot from the cache.
/// </summary>
public interface ILibraryService
{
    /// <summary>True once a configuration is loaded and the cache DB can be opened.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Re-scan the configured index roots into the cache. Reports progress and honours cancellation.
    /// Returns the (added, modified, removed, unchanged) counts.
    /// </summary>
    Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
        IProgress<IndexProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Build a browsable tree snapshot from the current cache, grouped as requested.</summary>
    Task<LibrarySnapshot> BuildSnapshotAsync(LibraryGrouping grouping = LibraryGrouping.AlbumArtist, CancellationToken ct = default);

    /// <summary>Flatten the current cache into per-file records for the analyzers/duplicate finder.</summary>
    Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default);
}
