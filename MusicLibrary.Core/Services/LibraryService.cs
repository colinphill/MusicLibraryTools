using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="ILibraryService"/>
public sealed class LibraryService : ILibraryService, ILibraryOrganizer, IReindexService, IDisposable
{
    private readonly IAppSettings _settings;
    private readonly IFileMutationCoordinator _mutations;

    // The MetadataDatabase wraps a single DbConnection and is not safe for concurrent use, so every
    // operation goes through this gate. One instance is opened lazily and reopened if the config changes.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MetadataDatabase? _db;
    private long _openedForVersion = -1;
    private string? _openedDatabaseSpec;

    public LibraryService(IAppSettings settings, IFileMutationCoordinator? mutations = null)
    {
        _settings = settings;
        _mutations = mutations ?? FileMutationCoordinator.Shared;
    }

    public bool IsReady => _settings.GetSnapshot().Configuration is not null;

    private sealed record LibraryContext(
        LibraryConfiguration Configuration,
        string? ConfigPath,
        long Version);

    private LibraryContext GetContext()
    {
        var snapshot = _settings.GetSnapshot();
        return snapshot.Configuration is { } configuration
            ? new LibraryContext(configuration, snapshot.ConfigPath, snapshot.Version)
            : throw new InvalidOperationException("No library configuration is loaded.");
    }

    public async Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
        IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var context = GetContext();
            var roots = GetRoots(context.Configuration);
            var db = GetDatabase(context);
            var result = await Task.Run(() => db.IndexFiles(roots, deletemissingsets: false, progress, ct), ct);
            // MetadataDatabase deliberately commits and returns partial work on cancellation. Surface
            // the cancellation to callers after that safe partial commit instead of calling it success.
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LibrarySnapshot> BuildSnapshotAsync(LibraryGrouping grouping = LibraryGrouping.AlbumArtist, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var context = GetContext();
            var roots = GetRoots(context.Configuration);
            var db = GetDatabase(context);
            return await Task.Run(() =>
            {
                var cache = db.BuildCache(roots, buildSecondaryIndexes: false);
                return Project(cache, grouping, ct);
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var context = GetContext();
            var roots = GetRoots(context.Configuration);
            var db = GetDatabase(context);
            return await Task.Run(() =>
            {
                var cache = db.BuildCache(roots, buildSecondaryIndexes: false);
                var records = new List<TrackRecord>(cache.FileCache.Count);
                foreach (var (path, e) in cache.FileCache)
                {
                    ct.ThrowIfCancellationRequested();
                    records.Add(new TrackRecord
                    {
                        Path = path,
                        Artist = e.Artist,
                        AlbumArtist = e.AlbumArtist,
                        HasAlbumArtist = e.HasAlbumArtist,
                        Album = e.Album,
                        StrippedAlbum = e.StrippedAlbum,
                        Title = e.Title,
                        ReleaseDate = e.ReleaseDate,
                        TrackNumber = e.TrackNumber,
                        TrackTotal = e.TrackTotal,
                        DiscNumber = e.DiscNumber,
                        DiscTotal = e.DiscTotal,
                        CodecName = e.CodecName,
                        CodecType = e.CodecType,
                        SampleRate = e.SampleRate,
                        BitsPerSample = e.BitsPerSample,
                        AverageBitRate = e.AverageBitRate,
                        Channels = e.Channels,
                        DurationInSeconds = e.DurationInSeconds,
                        Length = e.Length,
                        LastWriteTime = e.LastWriteTime,
                    });
                }
                return (IReadOnlyList<TrackRecord>)records;
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var context = GetContext();
            var config = context.Configuration;
            var db = GetDatabase(context);
            return await Task.Run(() =>
            {
                var locations = config.IndexLocations.ToList();
                var setIds = locations.Select(l => l.Set).Distinct().OrderBy(s => s).ToList();
                if (setIds.Count < 2)
                    return new AnalysisReport("Cross-set check", []);

                // Build each target unfiltered, then apply normalized extension filters here. The
                // cache API compares literal extensions case-sensitively, while configuration files
                // historically contain a mix of ".flac", "*.flac", casing, and whitespace.
                var caches = new List<SetComparisonCache>(setIds.Count);
                foreach (var setId in setIds)
                {
                    var files = new Dictionary<string, MetadataCacheEntry>(FilePathComparer);
                    foreach (var location in locations.Where(l => l.Set == setId))
                    {
                        var extensions = ParseExtensionFilter(location.Filter);
                        foreach (var (path, entry) in db.BuildCache([location.Target], buildSecondaryIndexes: false).FileCache)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (extensions is null || extensions.Contains(Path.GetExtension(path)))
                                files[path] = entry;
                        }
                    }
                    caches.Add(new SetComparisonCache(setId, files));
                }

                var findings = new List<AnalysisFinding>();
                foreach (var target in caches)
                {
                    foreach (var other in caches.Where(c => c.Set != target.Set))
                    {
                        foreach (var (path, entry) in target.Files)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!other.Albums.TryGetValue(entry.Album ?? "", out var albumFiles))
                            {
                                findings.Add(new AnalysisFinding(path, $"missing from set {other.Set}"));
                                continue;
                            }

                            var matches = albumFiles
                                .Where(f =>
                                    (SameText(f.AlbumArtist, entry.AlbumArtist) || SameText(f.Artist, entry.Artist)) &&
                                    f.TrackNumber == entry.TrackNumber &&
                                    (f.DiscNumber ?? 1) == (entry.DiscNumber ?? 1) &&
                                    SameText(f.Title, entry.Title))
                                .ToList();

                            if (matches.Count == 0)
                                findings.Add(new AnalysisFinding(path, $"missing from set {other.Set}"));
                            else if (matches.Count > 1)
                                findings.Add(new AnalysisFinding(path, $"ambiguous match in set {other.Set} ({matches.Count})"));
                        }
                    }
                }

                return new AnalysisReport("Cross-set check",
                    findings.OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase).ToList());
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FileDetails?> GetFileDetailsAsync(string path, bool includeArtwork, CancellationToken ct = default)
    {
        if (!IsReady)
            return null;
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            return await Task.Run(() => db.GetFileDetails(path, includeArtwork), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default)
    {
        if (!IsReady)
            return null;
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            return await Task.Run(() => db.GetFirstImageData(path), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetImageSignaturesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (!IsReady || paths.Count == 0)
            return [];
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            return await Task.Run(() => (IReadOnlyList<string>)db.GetImageSignatures(paths), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReindexFileAsync(string path, CancellationToken ct = default)
    {
        if (!IsReady)
            return;
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            await Task.Run(() => db.ReindexFile(path), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PlannedMove>> PreviewMovesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var context = GetContext();
            var config = context.Configuration;
            var baseDirs = GetRoots(config);
            var (lengthLimit, discLimit) = GetLimits(config);
            var db = GetDatabase(context);
            return await Task.Run(() =>
            {
                var moves = new List<PlannedMove>();
                foreach (var baseDir in baseDirs)
                {
                    var cache = db.BuildCache([baseDir], buildSecondaryIndexes: false);
                    // Track destinations already claimed in this preview so two sources don't plan
                    // the same target (the console tool relied on File.Move happening between checks).
                    var claimed = new HashSet<string>(FilePathComparer);
                    foreach (var (source, entry) in cache.FileCache)
                    {
                        ct.ThrowIfCancellationRequested();
                        var dest = CanonicalPath(baseDir, entry, source, lengthLimit, discLimit, claimed);
                        if (dest is not null)
                        {
                            claimed.Add(dest);
                            moves.Add(new PlannedMove(source, dest));
                        }
                    }
                }
                return (IReadOnlyList<PlannedMove>)moves;
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Mirrors OrganizeFiles: canonical path via FormatPath, Unicode-normalized, with _N collision
    // suffixes. Returns null when the file is already in place (no move needed).
    private static string? CanonicalPath(string baseDir, MetadataCacheEntry entry, string source,
        int lengthLimit, int discLimit, HashSet<string> claimed)
    {
        var ext = Path.GetExtension(source);
        var tgt = Path.Combine(baseDir, entry.FormatPath(lengthLimit, discLimit) + ext).Normalize();

        if (FilePathComparer.Equals(source, tgt) && source.IsNormalized())
            return null; // already canonical

        int index = 2;
        while ((File.Exists(tgt) && !FilePathComparer.Equals(source, tgt)) || claimed.Contains(tgt))
        {
            tgt = Path.Combine(baseDir, entry.FormatPath(lengthLimit, discLimit) + $"_{index++}" + ext).Normalize();
            if (FilePathComparer.Equals(source, tgt) && source.IsNormalized())
                return null; // the numbered target is the file itself
        }
        return tgt;
    }

    public async Task<OrganizeResult> ApplyMovesAsync(
        IReadOnlyList<PlannedMove> moves, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var context = GetContext();
        var config = context.Configuration;
        var baseDirs = GetRoots(config);
        bool deleteNonMusic = config["DeleteNonMusic"].Length != 0;
        bool keepFolderImages = config["KeepFolderImages"].Length != 0;

        // Successful (source → destination) pairs, so we can sync the cache to exactly the moves that
        // happened — even if the operation is cancelled partway.
        var relocated = new List<(string Source, string Destination)>();
        OrganizeResult result;
        IReadOnlyList<(string Source, string Error)> cacheErrors = [];
        try
        {
            result = await Task.Run(async () =>
            {
                int moved = 0, done = 0;
                var errors = new List<(string Source, string Error)>();

                foreach (var move in moves)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var mutation = await _mutations.AcquireAsync(
                            [move.Source, move.Destination], ct);
                        Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
                        File.Move(move.Source, move.Destination);
                        relocated.Add((move.Source, move.Destination));
                        moved++;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        errors.Add((move.Source, ex.Message));
                    }
                    progress?.Report(++done);
                }

                // Remove folders emptied by the moves (mirrors OrganizeFiles' cleanup step).
                foreach (var baseDir in baseDirs)
                {
                    try { MetadataExtensions.CleanEmptyMusicFolders(new DirectoryInfo(baseDir), deleteNonMusic, keepFolderImages); }
                    catch { /* cleanup is best-effort */ }
                }

                return new OrganizeResult(moved, errors);
            }, ct);
        }
        finally
        {
            // Attempt to keep the cache in sync with every completed move, even on cancellation. Any
            // refresh failures are returned separately from filesystem move failures.
            cacheErrors = await SyncMovesToCacheAsync(relocated, context);
        }
        return result with { CacheErrors = cacheErrors };
    }

    private static readonly StringComparer FilePathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class SetComparisonCache
    {
        public int Set { get; }
        public IReadOnlyDictionary<string, MetadataCacheEntry> Files { get; }
        public IReadOnlyDictionary<string, List<MetadataCacheEntry>> Albums { get; }

        public SetComparisonCache(int set, Dictionary<string, MetadataCacheEntry> files)
        {
            Set = set;
            Files = files;
            Albums = files.Values
                .GroupBy(e => e.Album ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (!IsReady || paths.Count == 0)
            return [];
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            return await Task.Run(() => (IReadOnlyList<byte[]?>)db.GetFirstImageData(paths), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReindexFileAsync(string path, IMediaFile savedFile, CancellationToken ct = default)
    {
        if (!IsReady)
            return;
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            await Task.Run(() => db.ReindexFile(path, savedFile), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReindexFilesAsync(
        IReadOnlyList<(string Path, IMediaFile File)> files,
        CancellationToken ct = default)
    {
        if (!IsReady || files.Count == 0)
            return;
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            await Task.Run(() => db.ReindexFiles(files), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HashSet<string>? ParseExtensionFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return null;

        var extensions = filter
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Path.GetExtension(value) is { Length: > 0 } extension
                ? extension
                : "." + value.TrimStart('.', '*'))
            .Where(extension => extension.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return extensions.Count == 0 ? null : extensions;
    }

    private static bool SameText(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<(string Source, string Error)>> SyncMovesToCacheAsync(
        IReadOnlyList<(string Source, string Destination)> moves,
        LibraryContext context)
    {
        if (moves.Count == 0 || !IsReady)
            return [];

        var errors = new List<(string Source, string Error)>();
        await _gate.WaitAsync();
        try
        {
            // Synchronize the database belonging to the configuration under which the moves were
            // performed, even if the user loaded another configuration while the batch was running.
            var db = GetDatabase(context);
            await Task.Run(() =>
            {
                foreach (var (source, destination) in moves)
                {
                    try
                    {
                        // Add the new entry first: if removing the stale entry then fails, the cache
                        // is temporarily duplicated rather than losing the successfully moved file.
                        if (!db.ReindexFile(destination))
                            throw new InvalidOperationException("Destination is outside the configuration's indexed roots.");
                        db.RemoveFile(source);
                    }
                    catch (Exception ex)
                    {
                        errors.Add((source, ex.Message));
                    }
                }
            });
            return errors;
        }
        catch (Exception ex)
        {
            return moves.Select(move => (move.Source, ex.Message)).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (int Length, int Disc) GetLimits(LibraryConfiguration config)
    {
        // LengthLimit/DiscNumLengthLimit throw if the elements are absent; fall back to permissive defaults.
        int length = 255, disc = 255;
        try { length = config.LengthLimit; } catch { }
        try { disc = config.DiscNumLengthLimit; } catch { }
        return (length, disc);
    }

    private static LibrarySnapshot Project(MetadataCache cache, LibraryGrouping grouping, CancellationToken ct)
    {
        var artists = new List<ArtistGroup>();
        int total = 0;

        var byArtist = cache.FileCache
            .GroupBy(kv => Coalesce(kv.Value.AlbumArtist, kv.Value.Artist, "Unknown Artist"))
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var artistGroup in byArtist)
        {
            ct.ThrowIfCancellationRequested();
            var albums = new List<AlbumGroup>();

            var byAlbum = artistGroup
                .GroupBy(kv => Coalesce(kv.Value.Album, "Unknown Album"))
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var albumGroup in byAlbum)
            {
                var tracks = albumGroup
                    .Select(kv => new TrackItem
                    {
                        Path = kv.Key,
                        Title = kv.Value.Title,
                        TrackNumber = kv.Value.TrackNumber,
                        DiscNumber = kv.Value.DiscNumber,
                        CodecName = kv.Value.CodecName,
                        CodecType = kv.Value.CodecType,
                        DurationInSeconds = kv.Value.DurationInSeconds,
                    })
                    .OrderBy(t => t.DiscNumber ?? 0)
                    .ThenBy(t => t.TrackNumber ?? 0)
                    .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                total += tracks.Count;
                albums.Add(new AlbumGroup(albumGroup.Key, tracks));
            }

            artists.Add(new ArtistGroup(artistGroup.Key, albums));
        }

        // Album grouping flattens every album across artists into one alphabetized list; distinct
        // albums that happen to share a title stay separate (they were grouped per-artist above).
        IReadOnlyList<object> roots = grouping switch
        {
            LibraryGrouping.Album => artists
                .SelectMany(a => a.Albums)
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Cast<object>()
                .ToList(),
            _ => artists.Cast<object>().ToList(),
        };

        return new LibrarySnapshot { Roots = roots, TotalTracks = total };
    }

    private static string Coalesce(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? values[^1] ?? "";

    private static List<string> GetRoots(LibraryConfiguration config)
        => config.IndexLocations.Select(l => l.Target).ToList();

    private MetadataDatabase GetDatabase(LibraryContext context)
    {
        var databaseSpec = ResolveDatabaseSpec(context.Configuration.DatabaseFile, context.ConfigPath);
        if (_db is not null &&
            _openedForVersion == context.Version &&
            string.Equals(_openedDatabaseSpec, databaseSpec, StringComparison.Ordinal))
            return _db;

        _db?.Dispose();
        _db = MetadataDatabase.OpenDatabase(databaseSpec);
        _openedForVersion = context.Version;
        _openedDatabaseSpec = databaseSpec;
        return _db;
    }

    private static string ResolveDatabaseSpec(string databaseSpec, string? configPath)
    {
        var prefix = "";
        var path = databaseSpec;
        if (databaseSpec.StartsWith("sqlite:", StringComparison.OrdinalIgnoreCase))
        {
            prefix = databaseSpec[..7];
            path = databaseSpec[7..];
            if (path.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return prefix + path;
        }
        else if (databaseSpec.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
        {
            // SQL connection specifications are opaque rather than filesystem paths.
            return databaseSpec;
        }

        if (!Path.IsPathRooted(path) && configPath is not null)
            path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, path);

        return prefix + path;
    }

    public void Dispose()
    {
        _db?.Dispose();
        _db = null;
        _gate.Dispose();
    }
}
