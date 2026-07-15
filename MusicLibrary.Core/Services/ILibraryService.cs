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

    /// <summary>
    /// Cross-reference the configured scan sets (config IndexTargets carry a Set number): flag files
    /// present in one set but missing (or ambiguous) in another. Empty unless 2+ sets are configured.
    /// </summary>
    Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default);

    /// <summary>
    /// Read one file's full cached metadata straight from the database (structured + known + raw text
    /// + optionally images), or null if the file isn't in the cache. Avoids re-parsing the file.
    /// </summary>
    Task<FileDetails?> GetFileDetailsAsync(string path, bool includeArtwork, CancellationToken ct = default);

    /// <summary>The bytes of a file's first embedded image from the cache (for thumbnails), or null.</summary>
    Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Batch form for virtualized grids. The returned list has the same order and length as
    /// <paramref name="paths"/>, with null entries for files without cached artwork.
    /// </summary>
    Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(IReadOnlyList<string> paths, CancellationToken ct = default);

    /// <summary>
    /// A per-path signature of each file's embedded-image hashes (order-independent; "" for none),
    /// for cheaply detecting whether a selection's artwork is uniform. Same order as the input.
    /// </summary>
    Task<IReadOnlyList<string>> GetImageSignaturesAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
}
