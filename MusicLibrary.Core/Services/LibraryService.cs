using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;
using System.Text;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="ILibraryService"/>
public sealed class LibraryService : ILibraryService, ILibraryOrganizer, IReindexService,
    IArtworkMaterializationNotifier, IDisposable
{
    private readonly IAppSettings _settings;
    private readonly IFileMutationCoordinator _mutations;
    private readonly IItunesMediaMutationService? _itunes;

    // The MetadataDatabase wraps a single DbConnection and is not safe for concurrent use, so every
    // operation goes through this gate. One instance is opened lazily and reopened if the config changes.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MetadataDatabase? _db;
    private long _openedForVersion = -1;
    private string? _openedDatabaseSpec;
    private ItunesMediaReconciliationResult _lastItunesReconciliation =
        ItunesMediaReconciliationResult.NotConfigured;

    public event Action? ArtworkMaterializationChanged;

    public LibraryService(
        IAppSettings settings,
        IFileMutationCoordinator? mutations = null,
        IItunesMediaMutationService? itunes = null)
    {
        _settings = settings;
        _mutations = mutations ?? FileMutationCoordinator.Shared;
        _itunes = itunes ?? new ItunesMediaMutationService(settings);
    }

    /// <summary>
    /// Creates a non-persisting service instance for command-line workflows. The configuration is
    /// loaded directly and no application recent-file or preference state is read or written.
    /// </summary>
    public LibraryService(
        string configurationPath,
        IFileMutationCoordinator? mutations = null,
        IItunesMediaMutationService? itunes = null)
        : this(new CommandLineAppSettings(configurationPath), mutations, itunes)
    {
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
            var roots = GetScanRootDefinitions(context.Configuration);
            var db = GetDatabase(context);
            if (int.TryParse(_settings.GetPreference(IndexBenchmarkService.ReaderParallelismPreference),
                    out int parallelism))
                db.ScanParallelism = Math.Clamp(parallelism, 1, 64);
            var result = await Task.Run(() => db.IndexFiles(roots, deletemissingsets: false, progress, ct), ct);
            // MetadataDatabase deliberately commits and returns partial work on cancellation. Surface
            // the cancellation to callers after that safe partial commit instead of calling it success.
            ct.ThrowIfCancellationRequested();
            if (_itunes is not null && context.Configuration.ItunesLibraryPath is not null)
            {
                progress?.Report(new IndexProgress
                {
                    Phase = IndexPhase.Finalizing,
                    Detail = "Reconciling changed files with the configured iTunes library",
                });
                IReadOnlyList<IndexedFileSnapshot> files =
                    await Task.Run(() => db.GetIndexedFileSnapshots(
                        roots.Select(root => root.Path)), ct).ConfigureAwait(false);
                _lastItunesReconciliation = await _itunes.ReconcileAsync(
                    files.Select(file => new ItunesMediaIndexedFile(
                        file.Path, file.Length, file.LastWriteTimeUtc)).ToArray(),
                    roots.Select(root => root.Path).ToArray(),
                    ct).ConfigureAwait(false);
            }
            else
            {
                _lastItunesReconciliation = ItunesMediaReconciliationResult.NotConfigured;
            }
            return result;
        }
        finally
        {
            _gate.Release();
            ArtworkMaterializationChanged?.Invoke();
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
                        TagType = e.TagType,
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
                var setIds = locations.SelectMany(l => l.Sets)
                    .Distinct(LibraryConfiguration.ScanSetComparer)
                    .OrderBy(s => s, LibraryConfiguration.ScanSetComparer).ToList();
                if (setIds.Count < 2)
                    return new AnalysisReport("Cross-set check", []);

                // Build each target unfiltered, then apply normalized extension filters here. The
                // cache API compares literal extensions case-sensitively, while configuration files
                // historically contain a mix of ".flac", "*.flac", casing, and whitespace.
                var caches = new List<SetComparisonCache>(setIds.Count);
                foreach (var setId in setIds)
                {
                    var files = new Dictionary<string, MetadataCacheEntry>(FilePathComparer);
                    foreach (var location in locations.Where(l =>
                                 l.Sets.Contains(setId, LibraryConfiguration.ScanSetComparer)))
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
                    foreach (var other in caches.Where(c =>
                                 !LibraryConfiguration.ScanSetComparer.Equals(c.Set, target.Set)))
                    {
                        foreach (var (path, entry) in target.Files)
                        {
                            ct.ThrowIfCancellationRequested();
                            var matches = other.Albums
                                .GetValueOrDefault(entry.Album ?? "", [])
                                .Where(candidate => ExactSetMatch(entry, candidate.Entry))
                                .ToList();

                            if (matches.Count == 0)
                            {
                                var nearMatches = other.NearMatches(entry)
                                    .Select(candidate => new
                                    {
                                        Candidate = candidate,
                                        Differences = SetDifferences(entry, candidate.Entry),
                                    })
                                    .Where(candidate => candidate.Differences.Count == 1)
                                    .OrderBy(candidate => candidate.Candidate.Path,
                                        StringComparer.CurrentCultureIgnoreCase)
                                    .ToList();

                                if (nearMatches.Count == 1)
                                {
                                    var near = nearMatches[0];
                                    findings.Add(new AnalysisFinding(path,
                                        NearMatchDescription(
                                            other.Set,
                                            near.Candidate,
                                            near.Differences[0]),
                                        "Near counterpart mismatch"));
                                }
                                else if (nearMatches.Count > 1)
                                {
                                    findings.Add(new AnalysisFinding(path,
                                        AmbiguousNearMatchDescription(other.Set, nearMatches
                                            .Select(near => (
                                                near.Candidate,
                                                Difference: near.Differences[0]))
                                            .ToList()),
                                        "Ambiguous near counterpart"));
                                }
                                else
                                {
                                    findings.Add(new AnalysisFinding(path,
                                        $"missing from set {other.Set}",
                                        "Missing counterpart"));
                                }
                            }
                            else if (matches.Count > 1)
                                findings.Add(new AnalysisFinding(path,
                                    $"ambiguous match in set {other.Set} ({matches.Count})",
                                    "Ambiguous counterpart"));
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
        FileDetails? result;
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            result = await Task.Run(() => db.GetFileDetails(path, includeArtwork), ct);
        }
        finally
        {
            _gate.Release();
        }
        if (includeArtwork)
            ArtworkMaterializationChanged?.Invoke();
        return result;
    }

    public async Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default)
    {
        if (!IsReady)
            return null;
        byte[]? result;
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            result = await Task.Run(() => db.GetFirstImageData(path), ct);
        }
        finally
        {
            _gate.Release();
        }
        ArtworkMaterializationChanged?.Invoke();
        return result;
    }

    public async Task<IReadOnlyList<string>> GetImageSignaturesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (!IsReady || paths.Count == 0)
            return [];
        IReadOnlyList<string> result;
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            result = await Task.Run(
                () => (IReadOnlyList<string>)db.GetImageSignatures(paths), ct);
        }
        finally
        {
            _gate.Release();
        }
        ArtworkMaterializationChanged?.Invoke();
        return result;
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
        ArtworkMaterializationChanged?.Invoke();
    }

    public async Task<IReadOnlyList<PlannedMove>> PreviewMovesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var context = GetContext();
            var config = context.Configuration;
            var locations = config.IndexLocations.ToList();
            var targets = LibraryOrganizationPolicy.EligibleTargets(locations);
            var (lengthLimit, discLimit) = GetLimits(config);
            var db = GetDatabase(context);
            return await Task.Run(() =>
            {
                var moves = new List<PlannedMove>();
                foreach (var target in targets)
                {
                    var cache = db.BuildCache([target.Target], buildSecondaryIndexes: false);
                    // Track destinations already claimed in this preview so two sources don't plan
                    // the same target (the console tool relied on File.Move happening between checks).
                    var claimed = new HashSet<string>(FilePathComparer);
                    foreach (var (source, entry) in cache.FileCache)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!LibraryOrganizationPolicy.IsPathEligible(source, locations))
                            continue;
                        var dest = CanonicalPath(target, entry, source,
                            lengthLimit, discLimit, claimed);
                        if (dest is not null)
                        {
                            claimed.Add(dest);
                            moves.Add(new PlannedMove(
                                source,
                                dest,
                                CaptureOrganizationSnapshot(source),
                                CaptureOrganizationSnapshot(dest)));
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

    // Returns null when the file is already in place. Internal naming uses _N collision suffixes;
    // iTunes naming uses the native space-N convention.
    private static string? CanonicalPath(LibraryIndexLocation target,
        MetadataCacheEntry entry, string source,
        int lengthLimit, int discLimit, HashSet<string> claimed)
    {
        string tgt = LibraryCanonicalPath.Initial(target, entry, source, lengthLimit, discLimit);

        if (FilePathComparer.Equals(source, tgt) && source.IsNormalized())
            return null; // already canonical

        int index = target.UseItunesCanonicalNaming ? 1 : 2;
        while ((File.Exists(tgt) && !FilePathComparer.Equals(source, tgt)) || claimed.Contains(tgt))
        {
            tgt = LibraryCanonicalPath.Collision(target, entry, source,
                lengthLimit, discLimit, index++);
            if (FilePathComparer.Equals(source, tgt) && source.IsNormalized())
                return null; // the numbered target is the file itself
        }
        return tgt;
    }

    public async Task<OrganizeResult> ApplyMovesAsync(
        IReadOnlyList<PlannedMove> moves, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(moves);
        var context = GetContext();
        var config = context.Configuration;
        var locations = config.IndexLocations.ToList();
        var baseDirs = LibraryOrganizationPolicy.EligibleRoots(locations);
        PlannedMove? excluded = moves.FirstOrDefault(move =>
            !LibraryOrganizationPolicy.IsPathEligible(move.Source, locations));
        if (excluded is not null)
            throw new InvalidOperationException(
                $"Organization is disabled for '{excluded.Source}' by its IndexTarget configuration.");
        bool deleteNonMusic = config["DeleteNonMusic"].Length != 0;
        bool keepFolderImages = config["KeepFolderImages"].Length != 0;
        string[] mutationPaths = moves
            .SelectMany(move => new[] { move.Source, move.Destination })
            .Distinct(FilePathComparer)
            .ToArray();
        using IDisposable lease = await _mutations.AcquireAsync(mutationPaths, ct);
        ValidateOrganizationPlan(moves, ct);
        await using IItunesMediaMutationSession? itunesSession = _itunes is null
            ? null
            : await _itunes.BeginAsync(mutationPaths, backupFiles: false, ct);
        string? journalPath = BeginOrganizeJournal(baseDirs, moves);

        // Successful (source → destination) pairs, so we can sync the cache to exactly the moves that
        // happened — even if the operation is cancelled partway.
        var relocated = new List<(string Source, string Destination)>();
        try
        {
            await Task.Run(() =>
            {
                int done = 0;
                foreach (var move in moves)
                {
                    ct.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
                    File.Move(move.Source, move.Destination);
                    relocated.Add((move.Source, move.Destination));
                    TryAppendOrganizeJournal(journalPath,
                        $"MOVE\tORGANIZE\t{move.Source}\t{move.Destination}");
                    progress?.Report(++done);
                }
            }, ct);

            if (itunesSession is not null)
                await itunesSession.CommitAsync(relocated
                    .Select(move => ItunesMediaMutation.Relocate(
                        move.Source, move.Destination))
                    .ToArray(), CancellationToken.None);
            TryAppendOrganizeJournal(journalPath, "COMMIT\tORGANIZE");
            if (itunesSession is not null)
                await itunesSession.CompleteAsync(CancellationToken.None);
        }
        catch (Exception operationError)
        {
            TryAppendOrganizeJournal(journalPath,
                $"ROLLBACK_BEGIN\tORGANIZE\t{operationError.Message}");
            var rollbackErrors = new List<Exception>();
            foreach ((string source, string destination) in relocated.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(destination) && !File.Exists(source))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                        File.Move(destination, source);
                        TryAppendOrganizeJournal(journalPath,
                            $"ROLLBACK_MOVE\tORGANIZE\t{destination}\t{source}");
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                    TryAppendOrganizeJournal(journalPath,
                        $"ROLLBACK_FAILED\tORGANIZE\t{destination}\t{source}\t" +
                        rollbackError.Message);
                }
            }
            TryAppendOrganizeJournal(journalPath, rollbackErrors.Count == 0
                ? "ROLLBACK\tORGANIZE"
                : "ROLLBACK_INCOMPLETE\tORGANIZE");
            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, operationError);
                throw new AggregateException(
                    "Organization failed and rollback was incomplete.", rollbackErrors);
            }
            throw;
        }

        foreach (var baseDir in LibraryOrganizationPolicy.CleanupRoots(locations))
        {
            try
            {
                MetadataExtensions.CleanEmptyMusicFolders(
                    new DirectoryInfo(baseDir), deleteNonMusic, keepFolderImages);
            }
            catch
            {
                // Cleanup is best-effort and does not invalidate completed, journaled moves.
            }
        }
        IReadOnlyList<(string Source, string Error)> cacheErrors =
            await SyncMovesToCacheAsync(relocated, context);
        return new OrganizeResult(relocated.Count, [])
        {
            CacheErrors = cacheErrors,
            JournalPath = journalPath,
        };
    }

    public Task<ItunesMediaReconciliationResult> GetLastItunesReconciliationAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_lastItunesReconciliation);
    }

    public async Task<LibraryOperationCacheSnapshot> GetOperationCacheSnapshotAsync(
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            LibraryContext context = GetContext();
            LibraryIndexLocation[] locations = context.Configuration.IndexLocations.ToArray();
            MetadataCache cache = GetDatabase(context).BuildCache(
                locations.Select(location => location.Target)
                    .Distinct(FilePathComparer),
                buildSecondaryIndexes: false);
            return new(
                context.Configuration,
                context.ConfigPath,
                context.Version,
                locations,
                cache);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ScanRootHealth>> GetScanRootHealthAsync(CancellationToken ct = default)
    {
        if (!IsReady)
            return [];
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            return await Task.Run(db.GetScanRootHealth, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Core-model progress adapter for command-line and operation workflows that should not need a
    /// compile-time dependency on the metadata database implementation.
    /// </summary>
    public async Task<(int Added, int Modified, int Removed, int Unchanged)> IndexForOperationAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var adapter = progress is null ? null : new IndexOperationProgressAdapter(progress);
        var result = await IndexAsync(adapter, ct).ConfigureAwait(false);
        adapter?.ReportCompleted(result.Added, result.Modified, result.Removed, result.Unchanged);
        return result;
    }

    public async Task<IReadOnlyList<ArtworkAuditFile>> GetArtworkAuditFilesAsync(CancellationToken ct = default)
    {
        if (!IsReady)
            return [];
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            return await Task.Run(() => (IReadOnlyList<ArtworkAuditFile>)db.GetArtworkSummaries()
                .Select(file => new ArtworkAuditFile(
                    file.Path,
                    file.ArtworkScanned,
                    file.Images.Select(image => new ArtworkAuditImage(
                        image.Hash, image.ImageType, image.Category,
                        image.Width, image.Height, image.Size)).ToList()))
                .ToList(), ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> GetMaterializedArtworkFileCountAsync(CancellationToken ct = default)
    {
        if (!IsReady)
            return 0;
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            return await Task.Run(db.GetMaterializedArtworkFileCount, ct);
        }
        finally { _gate.Release(); }
    }

    private static OperationPathSnapshot CaptureOrganizationSnapshot(string path)
    {
        string fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        return file.Exists
            ? new OperationPathSnapshot(true, false, file.Length, file.LastWriteTimeUtc)
                { Path = fullPath }
            : OperationPathSnapshot.Missing(fullPath);
    }

    private static void ValidateOrganizationPlan(
        IReadOnlyList<PlannedMove> moves,
        CancellationToken ct)
    {
        var destinations = new HashSet<string>(FilePathComparer);
        var sources = moves.Select(move => Path.GetFullPath(move.Source))
            .ToHashSet(FilePathComparer);
        foreach (PlannedMove move in moves)
        {
            ct.ThrowIfCancellationRequested();
            string source = Path.GetFullPath(move.Source);
            string destination = Path.GetFullPath(move.Destination);
            if (!destinations.Add(destination))
                throw new InvalidOperationException(
                    $"Organization plan contains a duplicate destination: '{destination}'.");
            if (!FilePathComparer.Equals(source, destination) && sources.Contains(destination))
                throw new InvalidOperationException(
                    $"Organization plan would overwrite another planned source: '{destination}'.");
            if (move.ExpectedSource is not null)
                ValidateOrganizationSnapshot(move.ExpectedSource);
            if (move.ExpectedDestination is not null &&
                !ReferenceEquals(move.ExpectedSource, move.ExpectedDestination))
                ValidateOrganizationSnapshot(move.ExpectedDestination);
        }
    }

    private static void ValidateOrganizationSnapshot(OperationPathSnapshot expected)
    {
        string path = expected.Path ?? throw new InvalidOperationException(
            "Organization snapshots must include their normalized path.");
        var file = new FileInfo(path);
        if (file.Exists != expected.Exists)
            throw new InvalidOperationException(
                $"Stale organization plan: existence changed for '{path}'.");
        if (!file.Exists)
            return;
        if (file.Length != expected.Length ||
            Math.Abs((file.LastWriteTimeUtc - expected.LastWriteTimeUtc).TotalMilliseconds) > 500)
            throw new InvalidOperationException(
                $"Stale organization plan: file changed since preview: '{path}'.");
    }

    private static string? BeginOrganizeJournal(
        IReadOnlyList<string> baseDirectories,
        IReadOnlyList<PlannedMove> moves)
    {
        if (moves.Count == 0 || baseDirectories.Count == 0)
            return null;
        string container = baseDirectories[0]
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            ".OrganizeFiles-recovery";
        string run = Path.Combine(container, DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        if (Directory.Exists(run))
            run += "-" + Guid.NewGuid().ToString("N");
        string journal = Path.Combine(run, "journal.tsv");
        WriteOrganizeJournal(journal,
            ["BEGIN\tORGANIZE", .. moves.Select(move =>
                $"PLAN_MOVE\tORGANIZE\t{move.Source}\t{move.Destination}")]);
        return journal;
    }

    private static void TryAppendOrganizeJournal(string? path, string line)
    {
        if (path is null)
            return;
        try { WriteOrganizeJournal(path, [line]); }
        catch { /* A recorded plan still identifies the run as interrupted and recoverable. */ }
    }

    private static void WriteOrganizeJournal(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (string line in lines)
            writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static readonly StringComparer FilePathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class SetComparisonCache
    {
        public string Set { get; }
        public IReadOnlyDictionary<string, MetadataCacheEntry> Files { get; }
        public IReadOnlyDictionary<string, List<SetComparisonFile>> Albums { get; }
        private IReadOnlyDictionary<SetComparisonKey, List<SetComparisonFile>> NearIndexes { get; }

        public SetComparisonCache(string set, Dictionary<string, MetadataCacheEntry> files)
        {
            Set = set;
            Files = files;
            SetComparisonFile[] entries = files
                .Select(file => new SetComparisonFile(file.Key, file.Value))
                .ToArray();
            Albums = entries
                .GroupBy(file => file.Entry.Album ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            NearIndexes = entries
                .SelectMany(file => NearMatchKeys(file.Entry)
                    .Select(key => (Key: key, File: file)))
                .GroupBy(item => item.Key)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.File).ToList());
        }

        public IReadOnlyList<SetComparisonFile> NearMatches(MetadataCacheEntry entry)
        {
            var matches = new Dictionary<string, SetComparisonFile>(FilePathComparer);
            foreach (SetComparisonKey key in NearMatchKeys(entry))
            {
                if (!NearIndexes.TryGetValue(key, out var candidates))
                    continue;
                foreach (SetComparisonFile candidate in candidates)
                    matches[candidate.Path] = candidate;
            }
            return matches.Values.ToList();
        }
    }

    private sealed record SetComparisonFile(string Path, MetadataCacheEntry Entry);

    private enum SetComparisonField
    {
        Album,
        Performer,
        DiscNumber,
        TrackNumber,
        Title,
    }

    private readonly record struct SetComparisonKey(
        SetComparisonField Omitted,
        string Album,
        string Performer,
        int DiscNumber,
        int? TrackNumber,
        string Title);

    private sealed record SetComparisonDifference(
        SetComparisonField Field,
        string SourceValue,
        string CounterpartValue);

    private static IEnumerable<SetComparisonKey> NearMatchKeys(MetadataCacheEntry entry)
    {
        foreach (SetComparisonField omitted in Enum.GetValues<SetComparisonField>())
        {
            string[] performers = omitted == SetComparisonField.Performer
                ? [""]
                : PerformerKeys(entry);
            foreach (string performer in performers)
            {
                yield return new SetComparisonKey(
                    omitted,
                    omitted == SetComparisonField.Album ? "" : NormalizeSetText(entry.Album),
                    performer,
                    omitted == SetComparisonField.DiscNumber ? 0 : entry.DiscNumber ?? 1,
                    omitted == SetComparisonField.TrackNumber ? null : entry.TrackNumber,
                    omitted == SetComparisonField.Title ? "" : NormalizeSetText(entry.Title));
            }
        }
    }

    private static string[] PerformerKeys(MetadataCacheEntry entry) =>
        new[] { NormalizeSetText(entry.AlbumArtist), NormalizeSetText(entry.Artist) }
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeSetText(string? value) => (value ?? "").ToUpperInvariant();

    private static bool ExactSetMatch(MetadataCacheEntry source, MetadataCacheEntry candidate) =>
        SameText(source.Album, candidate.Album) &&
        SamePerformer(source, candidate) &&
        source.TrackNumber == candidate.TrackNumber &&
        (source.DiscNumber ?? 1) == (candidate.DiscNumber ?? 1) &&
        SameText(source.Title, candidate.Title);

    private static bool SamePerformer(MetadataCacheEntry left, MetadataCacheEntry right) =>
        (!string.IsNullOrWhiteSpace(left.AlbumArtist) &&
         !string.IsNullOrWhiteSpace(right.AlbumArtist) &&
         SameText(left.AlbumArtist, right.AlbumArtist)) ||
        SameText(left.Artist, right.Artist);

    private static IReadOnlyList<SetComparisonDifference> SetDifferences(
        MetadataCacheEntry source,
        MetadataCacheEntry counterpart)
    {
        var differences = new List<SetComparisonDifference>(5);
        if (!SameText(source.Album, counterpart.Album))
            differences.Add(new(SetComparisonField.Album,
                DisplaySetText(source.Album), DisplaySetText(counterpart.Album)));
        if (!SamePerformer(source, counterpart))
            differences.Add(new(SetComparisonField.Performer,
                DisplayPerformer(source), DisplayPerformer(counterpart)));
        if ((source.DiscNumber ?? 1) != (counterpart.DiscNumber ?? 1))
            differences.Add(new(SetComparisonField.DiscNumber,
                DisplayDisc(source.DiscNumber), DisplayDisc(counterpart.DiscNumber)));
        if (source.TrackNumber != counterpart.TrackNumber)
            differences.Add(new(SetComparisonField.TrackNumber,
                DisplayNumber(source.TrackNumber), DisplayNumber(counterpart.TrackNumber)));
        if (!SameText(source.Title, counterpart.Title))
            differences.Add(new(SetComparisonField.Title,
                DisplaySetText(source.Title), DisplaySetText(counterpart.Title)));
        return differences;
    }

    private static string NearMatchDescription(
        string set,
        SetComparisonFile counterpart,
        SetComparisonDifference difference) =>
        $"Near match in set {set}: {SetFieldName(difference.Field)} differs — " +
        $"{difference.SourceValue} vs {difference.CounterpartValue}. " +
        $"Counterpart: {counterpart.Path}";

    private static string AmbiguousNearMatchDescription(
        string set,
        IReadOnlyList<(SetComparisonFile Candidate, SetComparisonDifference Difference)> candidates) =>
        $"Ambiguous near match in set {set} ({candidates.Count}): " +
        string.Join("; ", candidates.Select(candidate =>
            $"{SetFieldName(candidate.Difference.Field)} " +
            $"{candidate.Difference.SourceValue} vs {candidate.Difference.CounterpartValue} " +
            $"at {candidate.Candidate.Path}"));

    private static string SetFieldName(SetComparisonField field) => field switch
    {
        SetComparisonField.Album => "album",
        SetComparisonField.Performer => "artist identity",
        SetComparisonField.DiscNumber => "disc number",
        SetComparisonField.TrackNumber => "track number",
        SetComparisonField.Title => "title",
        _ => field.ToString(),
    };

    private static string DisplaySetText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(missing)" : $"“{value}”";

    private static string DisplayPerformer(MetadataCacheEntry entry)
    {
        string albumArtist = DisplaySetText(entry.AlbumArtist);
        string artist = DisplaySetText(entry.Artist);
        return SameText(entry.AlbumArtist, entry.Artist) || string.IsNullOrWhiteSpace(entry.AlbumArtist)
            ? artist
            : $"album artist {albumArtist}, artist {artist}";
    }

    private static string DisplayDisc(int? value) =>
        value is null ? "(missing; treated as 1)" : value.Value.ToString();

    private static string DisplayNumber(int? value) =>
        value?.ToString() ?? "(missing)";

    public async Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (!IsReady || paths.Count == 0)
            return [];
        IReadOnlyList<byte[]?> result;
        await _gate.WaitAsync(ct);
        try
        {
            var db = GetDatabase(GetContext());
            result = await Task.Run(
                () => (IReadOnlyList<byte[]?>)db.GetFirstImageData(paths), ct);
        }
        finally
        {
            _gate.Release();
        }
        ArtworkMaterializationChanged?.Invoke();
        return result;
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
        ArtworkMaterializationChanged?.Invoke();
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
        ArtworkMaterializationChanged?.Invoke();
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

        IReadOnlyList<(string Source, string Error)> result;
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
            result = errors;
        }
        catch (Exception ex)
        {
            result = moves.Select(move => (move.Source, ex.Message)).ToList();
        }
        finally
        {
            _gate.Release();
        }
        ArtworkMaterializationChanged?.Invoke();
        return result;
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
        => config.IndexLocations.Select(l => l.Target)
            .Distinct(FilePathComparer).ToList();

    private static List<ScanRootDefinition> GetScanRootDefinitions(LibraryConfiguration config)
        => config.IndexLocations
            .GroupBy(location => Path.TrimEndingDirectorySeparator(location.Target), FilePathComparer)
            .Select(group => new ScanRootDefinition(
                group.First().Target,
                group.SelectMany(location => location.Sets)
                    .Distinct(LibraryConfiguration.ScanSetComparer)
                    .OrderBy(set => set, LibraryConfiguration.ScanSetComparer).ToArray()))
            .ToList();

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
