using iTunes.Binary;
using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public sealed record LibraryOperationContext(
    LibraryConfiguration Configuration,
    IReadOnlyList<LibraryIndexLocation> IndexLocations,
    MetadataCache Cache,
    ItlLibrary ItunesLibrary,
    IReadOnlyDictionary<int, ItlTrack> TracksById,
    string ItunesLibraryPath);

public interface ILibraryOperationContextFactory
{
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

    public Task<LibraryOperationContext> CreateAsync(
        string? configurationPath,
        string? itunesLibraryPath = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            if (_library is null)
                throw new InvalidOperationException(
                    "No active library cache is available and no configuration path was supplied.");
            return CreateFromActiveCacheAsync(itunesLibraryPath, progress, ct);
        }
        return Task.Run((Func<Task<LibraryOperationContext>>)(async () =>
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
                var indexProgress = progress is null ? null : new Progress<IndexProgress>(value =>
                    progress.Report(new(OperationPhase.IndexingSources, value.Scanned,
                        CurrentPath: null,
                        Message: $"Indexed {value.Scanned:N0}; {value.Added:N0} added, " +
                                 $"{value.Modified:N0} modified")));
                await database.IndexFilesAsync(
                    locations.Select(location => new ScanRootDefinition(location.Target, location.Sets)),
                    progress: indexProgress, ct: ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                MetadataCache cache = database.BuildCache(
                    locations.Select(location => location.Target).Distinct(PathComparer));

                string resolvedLibraryPath = ItlFileEditor.ResolveLibraryPath(
                    itunesLibraryPath ?? configuration.ItunesLibraryPath);
                progress?.Report(new(OperationPhase.LoadingLibrary,
                    CurrentPath: resolvedLibraryPath, Message: "Loading iTunes library"));
                ItlLibrary library = ItlLibrary.Load(resolvedLibraryPath);
                var tracks = library.Tracks.ToDictionary(track => track.Id);
                return new(configuration, locations, cache, library, tracks, resolvedLibraryPath);
            }
        }), ct);
    }

    private async Task<LibraryOperationContext> CreateFromActiveCacheAsync(
        string? itunesLibraryPath,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new(OperationPhase.LoadingConfiguration,
            Message: "Using the active library configuration and cache"));
        LibraryOperationCacheSnapshot snapshot =
            await _library!.GetOperationCacheSnapshotAsync(ct).ConfigureAwait(false);
        string configuredLibraryPath =
            itunesLibraryPath ?? snapshot.Configuration.ItunesLibraryPath
            ?? throw new InvalidOperationException(
                "Set the iTunes library path in the active library configuration.");
        string resolvedLibraryPath = ItlFileEditor.ResolveLibraryPath(configuredLibraryPath);
        progress?.Report(new(OperationPhase.LoadingLibrary,
            CurrentPath: resolvedLibraryPath, Message: "Loading iTunes library"));
        ItlLibrary library = await Task.Run(() => ItlLibrary.Load(resolvedLibraryPath), ct)
            .ConfigureAwait(false);
        return new(
            snapshot.Configuration,
            snapshot.IndexLocations,
            snapshot.Cache,
            library,
            library.Tracks.ToDictionary(track => track.Id),
            resolvedLibraryPath);
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
