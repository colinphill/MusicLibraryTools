using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="ILibraryService"/>
public sealed class LibraryService : ILibraryService, ILibraryOrganizer, IReindexService, IDisposable
{
    private readonly IAppSettings _settings;

    // The MetadataDatabase wraps a single DbConnection and is not safe for concurrent use, so every
    // operation goes through this gate. One instance is opened lazily and reopened if the config changes.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MetadataDatabase? _db;
    private string? _openedForConfig;

    public LibraryService(IAppSettings settings)
    {
        _settings = settings;
        _settings.ConfigurationChanged += (_, _) => InvalidateDatabase();
    }

    public bool IsReady => _settings.Configuration is not null;

    public async Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
        IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        var roots = GetRoots();
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase();
            return await Task.Run(() => db.IndexFiles(roots, deletemissingsets: false, progress, ct), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LibrarySnapshot> BuildSnapshotAsync(LibraryGrouping grouping = LibraryGrouping.AlbumArtist, CancellationToken ct = default)
    {
        var roots = GetRoots();
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase();
            return await Task.Run(() =>
            {
                var cache = db.BuildCache(roots);
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
        var roots = GetRoots();
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase();
            return await Task.Run(() =>
            {
                var cache = db.BuildCache(roots);
                var records = new List<TrackRecord>(cache.FileCache.Count);
                foreach (var (path, e) in cache.FileCache)
                {
                    ct.ThrowIfCancellationRequested();
                    records.Add(new TrackRecord
                    {
                        Path = path,
                        Artist = e.Artist,
                        AlbumArtist = e.AlbumArtist,
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

    public async Task<FileDetails?> GetFileDetailsAsync(string path, bool includeArtwork, CancellationToken ct = default)
    {
        if (!IsReady)
            return null;
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase();
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
            var db = GetDatabase();
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
            var db = GetDatabase();
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
            var db = GetDatabase();
            await Task.Run(() => db.ReindexFile(path), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PlannedMove>> PreviewMovesAsync(CancellationToken ct = default)
    {
        var config = _settings.Configuration
            ?? throw new InvalidOperationException("No library configuration is loaded.");
        var baseDirs = config.IndexLocations.Select(l => l.Target).ToList();
        var (lengthLimit, discLimit) = GetLimits(config);

        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase();
            return await Task.Run(() =>
            {
                var moves = new List<PlannedMove>();
                foreach (var baseDir in baseDirs)
                {
                    var cache = db.BuildCache([baseDir]);
                    // Track destinations already claimed in this preview so two sources don't plan
                    // the same target (the console tool relied on File.Move happening between checks).
                    var claimed = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
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

        if (source.Equals(tgt, StringComparison.InvariantCultureIgnoreCase) && source.IsNormalized())
            return null; // already canonical

        int index = 2;
        while ((File.Exists(tgt) || claimed.Contains(tgt)) && source.IsNormalized())
        {
            tgt = Path.Combine(baseDir, entry.FormatPath(lengthLimit, discLimit) + $"_{index++}" + ext).Normalize();
            if (source.Equals(tgt, StringComparison.InvariantCultureIgnoreCase) && source.IsNormalized())
                return null; // the numbered target is the file itself
        }
        return tgt;
    }

    public async Task<OrganizeResult> ApplyMovesAsync(
        IReadOnlyList<PlannedMove> moves, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var config = _settings.Configuration
            ?? throw new InvalidOperationException("No library configuration is loaded.");
        var baseDirs = config.IndexLocations.Select(l => l.Target).ToList();
        bool deleteNonMusic = config["DeleteNonMusic"].Length != 0;
        bool keepFolderImages = config["KeepFolderImages"].Length != 0;

        // Successful (source → destination) pairs, so we can sync the cache to exactly the moves that
        // happened — even if the operation is cancelled partway.
        var relocated = new List<(string Source, string Destination)>();
        try
        {
            return await Task.Run(() =>
            {
                int moved = 0, done = 0;
                var errors = new List<(string Source, string Error)>();

                foreach (var move in moves)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
                        File.Move(move.Source, move.Destination);
                        relocated.Add((move.Source, move.Destination));
                        moved++;
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
            // Keep the cache in sync with the moves: drop the stale entry at the old path and index the
            // file at its new path. Runs even on cancel (with its own token) so the cache never lies.
            await SyncMovesToCacheAsync(relocated);
        }
    }

    private async Task SyncMovesToCacheAsync(IReadOnlyList<(string Source, string Destination)> moves)
    {
        if (moves.Count == 0 || !IsReady)
            return;

        await _gate.WaitAsync();
        try
        {
            var db = GetDatabase();
            await Task.Run(() =>
            {
                foreach (var (source, destination) in moves)
                {
                    try
                    {
                        db.RemoveFile(source);
                        db.ReindexFile(destination);
                    }
                    catch { /* one bad file shouldn't abort the cache sync */ }
                }
            });
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

    private List<string> GetRoots()
    {
        var config = _settings.Configuration
            ?? throw new InvalidOperationException("No library configuration is loaded.");
        return config.IndexLocations.Select(l => l.Target).ToList();
    }

    private MetadataDatabase GetDatabase()
    {
        var config = _settings.Configuration
            ?? throw new InvalidOperationException("No library configuration is loaded.");

        if (_db is not null && _openedForConfig == _settings.ConfigPath)
            return _db;

        _db?.Dispose();

        // The cache filename in the config may be relative; resolve it next to the config file so
        // the GUI's working directory doesn't matter.
        var dbFile = config.DatabaseFile;
        if (!Path.IsPathRooted(dbFile) && _settings.ConfigPath is { } cfgPath)
            dbFile = Path.Combine(Path.GetDirectoryName(cfgPath)!, dbFile);

        _db = MetadataDatabase.OpenDatabase(dbFile);
        _openedForConfig = _settings.ConfigPath;
        return _db;
    }

    private void InvalidateDatabase()
    {
        _gate.Wait();
        try
        {
            _db?.Dispose();
            _db = null;
            _openedForConfig = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _db?.Dispose();
        _gate.Dispose();
    }
}
