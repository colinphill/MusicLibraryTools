using iTunes.Binary;
using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Catalog-independent state used by filesystem, analysis, and playlist-provider workflows.
/// Loading this context never requires iTunes or another external media catalog.
/// </summary>
public sealed record IndexedLibraryOperationContext(
    LibraryConfiguration Configuration,
    IReadOnlyList<LibraryIndexLocation> IndexLocations,
    MetadataCache Cache);

public sealed record LibraryOperationContext(
    LibraryConfiguration Configuration,
    IReadOnlyList<LibraryIndexLocation> IndexLocations,
    MetadataCache Cache,
    ItlLibrary ItunesLibrary,
    IReadOnlyDictionary<int, ItlTrack> TracksById,
    string ItunesLibraryPath);

public interface ILibraryOperationContextFactory
{
    async Task<IndexedLibraryOperationContext> CreateIndexedAsync(
        string? configurationPath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        LibraryOperationContext context = await CreateAsync(
            configurationPath, progress: progress, ct: ct).ConfigureAwait(false);
        return new(context.Configuration, context.IndexLocations, context.Cache);
    }

    Task<LibraryOperationContext> CreateAsync(
        string? configurationPath,
        string? itunesLibraryPath = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>Loads configuration, source metadata, and ITL state exactly once for a planning run.</summary>
public sealed class LibraryOperationContextFactory : ILibraryOperationContextFactory
{
    private readonly ILibraryService? _library;

    public LibraryOperationContextFactory(ILibraryService? library = null) =>
        _library = library;

    public async Task<LibraryOperationContext> CreateAsync(
        string? configurationPath,
        string? itunesLibraryPath = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        IndexedLibraryOperationContext indexed = await CreateIndexedAsync(
            configurationPath, progress, ct).ConfigureAwait(false);
        string configuredLibraryPath = itunesLibraryPath ?? indexed.Configuration.ItunesLibraryPath
            ?? throw new InvalidOperationException(
                "Set the iTunes library path in the active library configuration.");
        string resolvedLibraryPath = ItlFileEditor.ResolveLibraryPath(configuredLibraryPath);
        progress?.Report(new(OperationPhase.LoadingLibrary,
            CurrentPath: resolvedLibraryPath, Message: "Loading iTunes library"));
        ItlLibrary library = await Task.Run(() => ItlLibrary.Load(resolvedLibraryPath), ct)
            .ConfigureAwait(false);
        return new(indexed.Configuration, indexed.IndexLocations, indexed.Cache, library,
            library.Tracks.ToDictionary(track => track.Id), resolvedLibraryPath);
    }

    public Task<IndexedLibraryOperationContext> CreateIndexedAsync(
        string? configurationPath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            if (_library is null)
                throw new InvalidOperationException(
                    "No active library cache is available and no configuration path was supplied.");
            return CreateIndexedFromActiveCacheAsync(progress, ct);
        }

        return Task.Run((Func<Task<IndexedLibraryOperationContext>>)(async () =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(OperationPhase.LoadingConfiguration,
                CurrentPath: configurationPath, Message: "Loading library configuration"));
            var configuration = new LibraryConfiguration(configurationPath);
            var locations = configuration.IndexLocations.ToArray();

            progress?.Report(new(OperationPhase.IndexingSources,
                Message: $"Indexing {locations.Length:N0} source root(s)"));
            using (MetadataDatabase database = MetadataDatabase.OpenDatabase(configuration.DatabaseFile))
            {
                var indexProgress = progress is null
                    ? null
                    : new IndexOperationProgressAdapter(progress);
                var indexResult = await database.IndexFilesAsync(
                    locations.Select(location => new ScanRootDefinition(
                        location.Target, location.Sets)
                    {
                        Formats = location.IndexFormats,
                        IncludePatterns = location.IndexIncludePatterns,
                        ExcludePatterns = location.IndexExcludePatterns,
                        ReadArtworkAtIndexTime = configuration
                            .GetEffectiveProfile(location).Artwork.ReadAtIndexTime,
                    }),
                    progress: indexProgress, ct: ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                indexProgress?.ReportCompleted(
                    indexResult.Added, indexResult.Modified,
                    indexResult.Removed, indexResult.Unchanged);
                MetadataCache cache = database.BuildCache(
                    locations.Select(location => location.Target).Distinct(PathComparer));

                return new(configuration, locations, cache);
            }
        }), ct);
    }

    private async Task<IndexedLibraryOperationContext> CreateIndexedFromActiveCacheAsync(
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new(OperationPhase.LoadingConfiguration,
            Message: "Using the active library configuration and cache"));
        LibraryOperationCacheSnapshot snapshot =
            await _library!.GetOperationCacheSnapshotAsync(ct).ConfigureAwait(false);
        return new(snapshot.Configuration, snapshot.IndexLocations, snapshot.Cache);
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
