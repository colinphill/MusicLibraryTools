using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibraryTools;
using System.Collections.Immutable;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Publishes changes that can affect the number of files with materialized cached artwork.
/// Notifications are raised after the cache operation has committed and released its database lock.
/// </summary>
public interface IArtworkMaterializationNotifier
{
    event Action? ArtworkMaterializationChanged;
}

/// <summary>
/// Aggregate catalog counts for overview surfaces. Computing this projection must not materialize
/// per-file metadata or artwork.
/// </summary>
public readonly record struct LibrarySummary(
    int TrackCount,
    int AlbumCount,
    int ArtistCount);

/// <summary>Selected metadata values for one requested Library path.</summary>
public sealed record LibraryMetadataProjection(
    string Path,
    IReadOnlyDictionary<
        MetadataFieldKey,
        ImmutableArray<string>> Values);

public enum LibraryMetadataSortKind
{
    Text,
    Numeric,
    Date,
}

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

    /// <summary>
    /// Outcome of the most recent index-time reconciliation with the configured binary iTunes
    /// library. A configured library can report review items without making unsafe identity guesses.
    /// </summary>
    Task<ItunesMediaReconciliationResult> GetLastItunesReconciliationAsync(
        CancellationToken ct = default) =>
        Task.FromResult(ItunesMediaReconciliationResult.NotConfigured);

    /// <summary>Last attempt/success and current availability for every cached scan root.</summary>
    Task<IReadOnlyList<ScanRootHealth>> GetScanRootHealthAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ScanRootHealth>>([]);

    /// <summary>Build a browsable tree snapshot from the current cache, grouped as requested.</summary>
    Task<LibrarySnapshot> BuildSnapshotAsync(LibraryGrouping grouping = LibraryGrouping.AlbumArtist, CancellationToken ct = default);

    /// <summary>Flatten the current cache into per-file records for the analyzers/duplicate finder.</summary>
    Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default);

    /// <summary>
    /// Return the compact scalar projection used by the Library grid and scalar-only analyzers.
    /// Arbitrary metadata and artwork are deliberately excluded.
    /// </summary>
    async Task<IReadOnlyList<TrackRecord>> GetBrowseRecordsAsync(
        CancellationToken ct = default)
        => await GetAllRecordsAsync(ct);

    /// <summary>
    /// Return overview counts without retaining the catalog. The default keeps test and external
    /// implementations compatible; the production service uses a database aggregate projection.
    /// </summary>
    async Task<LibrarySummary> GetLibrarySummaryAsync(
        CancellationToken ct = default)
    {
        IReadOnlyList<TrackRecord> records =
            await GetBrowseRecordsAsync(ct);
        int albums = records
            .Select(record => (
                record.EffectiveAlbumArtist,
                record.Album ?? ""))
            .Distinct()
            .Count();
        int artists = records
            .Select(record =>
                record.EffectiveAlbumArtist)
            .Distinct(
                StringComparer.OrdinalIgnoreCase)
            .Count();
        return new(records.Count, albums, artists);
    }

    /// <summary>
    /// Load only the requested metadata fields for the requested paths. Results retain path order.
    /// </summary>
    async Task<IReadOnlyList<
        LibraryMetadataProjection>>
        GetMetadataProjectionAsync(
            IReadOnlyList<string> paths,
            IReadOnlyList<
                MetadataFieldKey> fields,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fields);
        IReadOnlyList<TrackRecord> records =
            await GetAllRecordsAsync(ct);
        var byPath = records.ToDictionary(
            record => record.Path,
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        return paths.Select(path =>
        {
            var values = new Dictionary<
                MetadataFieldKey,
                ImmutableArray<string>>();
            if (byPath.TryGetValue(
                    path,
                    out TrackRecord? record))
            {
                foreach (MetadataFieldKey field
                         in fields)
                {
                    string key =
                        field.KnownField?
                            .ToString() ??
                        CachedMetadataKeys
                            .Custom(
                                field
                                    .CustomName!);
                    if (record.Metadata
                        .TryGetValue(
                            key,
                            out string[]?
                                fieldValues))
                        values[field] =
                            [.. fieldValues];
                }
            }
            return new
                LibraryMetadataProjection(
                    path,
                    values);
        }).ToArray();
    }

    /// <summary>
    /// Return paths in ascending order for a metadata field without retaining that field in browse
    /// rows. Production implementations execute this against the catalog.
    /// </summary>
    async Task<IReadOnlyList<string>>
        GetMetadataSortOrderAsync(
            MetadataFieldKey field,
            LibraryMetadataSortKind sortKind,
            CancellationToken ct = default)
    {
        IReadOnlyList<TrackRecord> records =
            await GetAllRecordsAsync(ct);
        string key = field.KnownField?
            .ToString() ??
            CachedMetadataKeys.Custom(
                field.CustomName!);
        return records
            .OrderBy(
                record =>
                    record.Metadata
                        .GetValueOrDefault(
                            key)?
                        .FirstOrDefault() ?? "",
                StringComparer
                    .CurrentCultureIgnoreCase)
            .Select(record => record.Path)
            .ToArray();
    }

    /// <summary>
    /// Capture the active configuration and a metadata snapshot from the already-open cache.
    /// Configuration-dependent App operations use this instead of reopening or re-indexing the
    /// configured database independently.
    /// </summary>
    Task<LibraryOperationCacheSnapshot> GetOperationCacheSnapshotAsync(
        CancellationToken ct = default) =>
        Task.FromException<LibraryOperationCacheSnapshot>(
            new NotSupportedException("This library service does not expose operation snapshots."));

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

    /// <summary>Cached artwork metadata only; never hydrates deferred artwork or selects image blobs.</summary>
    Task<IReadOnlyList<ArtworkAuditFile>> GetArtworkAuditFilesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ArtworkAuditFile>>([]);

    /// <summary>
    /// Count cached files that have completed deferred artwork hydration and contain at least one
    /// materialized image. Does not hydrate artwork or read image blobs.
    /// </summary>
    Task<int> GetMaterializedArtworkFileCountAsync(CancellationToken ct = default) =>
        Task.FromResult(0);
}

public sealed record LibraryOperationCacheSnapshot(
    LibraryConfiguration Configuration,
    string? ConfigurationPath,
    long ConfigurationVersion,
    IReadOnlyList<LibraryIndexLocation> IndexLocations,
    MetadataCache Cache);
