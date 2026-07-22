using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using iTunes.Binary;
using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public sealed record PlaylistExportRequest(
    string? ConfigurationPath = null,
    string? ItunesLibraryPath = null);

public sealed record PlaylistExportFile(
    string PlaylistName,
    string DestinationPath,
    int TrackCount,
    int ByteCount);

public sealed record PlaylistExportTargetPlan(
    string Target,
    string Type,
    IReadOnlyList<string> Sets,
    IReadOnlyList<PlaylistExportFile> Files,
    int MissingTrackCount);

public sealed record PlaylistExportPlan(
    PlaylistExportRequest Request,
    IReadOnlyList<PlaylistExportTargetPlan> Targets,
    FileMutationPlan MutationPlan,
    IReadOnlyList<OperationIssue> Issues)
{
    public bool CanApply => MutationPlan.CanApply;
}

public sealed record PlaylistExportResult(
    int PlaylistCount,
    FileMutationSummary Mutations,
    IReadOnlyList<OperationIssue> Issues);

public interface IPlaylistExportService
{
    Task<PlaylistExportPlan> PreviewAsync(
        PlaylistExportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<PlaylistExportResult> ApplyAsync(
        PlaylistExportPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Maps and renders every configured playlist target during preview. Rendered bytes are embedded in
/// the immutable mutation plan, so apply writes exactly what was reviewed and never remaps tracks.
/// </summary>
public sealed class PlaylistExportService : IPlaylistExportService
{
    private const int MaxPlaylistCount = 500;
    private const int SonosNamePad = 100;
    private readonly ILibraryOperationContextFactory _contexts;
    private readonly IFileInventoryService _inventories;
    private readonly IFileMutationPlanExecutor _executor;
    private readonly IReadOnlyList<IPlaylistWriter> _writers;
    private readonly IReadOnlyList<IPlaylistSource> _sources;

    private static readonly IPlaylistWriter[] BuiltInWriters =
        [new M3uPlaylistWriter(), new WplPlaylistWriter()];
    private static readonly IPlaylistSource[] BuiltInSources = [new M3uPlaylistSource()];
    private static readonly PlaylistWriterOptions LegacyM3uOptions = new()
    {
        PathStyle = PlaylistPathStyle.AsProvided,
        Encoding = Encoding.UTF8,
        EmitByteOrderMark = true,
        LineEnding = PlaylistLineEnding.Platform,
        IncludeExtendedInfo = true,
        FileNameTransform = FixPath,
        MaxTrackCount = MaxPlaylistCount,
    };
    private static readonly PlaylistWriterOptions LegacyWplOptions = LegacyM3uOptions with
    {
        FileNameTransform = static name => FixPath(name).PadRight(SonosNamePad),
    };

    public PlaylistExportService(ILibraryOperationContextFactory contexts,
        IFileInventoryService inventories, IFileMutationPlanExecutor executor,
        IEnumerable<IPlaylistWriter>? writers = null,
        IEnumerable<IPlaylistSource>? sources = null)
    {
        _contexts = contexts;
        _inventories = inventories;
        _executor = executor;
        IPlaylistWriter[] configured = writers?.ToArray() ?? [];
        _writers = configured.Length == 0 ? BuiltInWriters : configured;
        IPlaylistSource[] configuredSources = sources?.ToArray() ?? [];
        _sources = configuredSources.Length == 0 ? BuiltInSources : configuredSources;
    }

    public async Task<PlaylistExportPlan> PreviewAsync(
        PlaylistExportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var issues = new List<OperationIssue>();
        PlaylistInputContext input = await CreateInputContextAsync(
            request, issues, progress, ct).ConfigureAwait(false);
        bool clean = input.Configuration.CleanCrossSyncPlaylists;
        IReadOnlyList<LibraryPlaylistTarget> targets = input.Configuration.PlaylistTargets;
        if (targets.Count == 0)
            issues.Add(new("missing-targets", OperationIssueSeverity.Blocker,
                "At least one PlaylistTarget is required."));

        var configuredSets = input.IndexLocations.SelectMany(location => location.Sets)
            .ToHashSet(LibraryConfiguration.ScanSetComparer);
        foreach (LibraryPlaylistTarget target in targets)
        {
            string[] unknown = target.Sets.Where(set => !configuredSets.Contains(set)).ToArray();
            if (unknown.Length > 0)
                issues.Add(new("unknown-set", OperationIssueSeverity.Blocker,
                    "Playlist target references scan set(s) with no IndexTarget: " +
                    string.Join(",", unknown), target.Target));
        }
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return EmptyPlan(request, targets, issues);

        var inventories = new Dictionary<string, FileInventory>(PathComparer);
        foreach (string folder in targets.Select(target => Path.GetFullPath(target.Target))
                     .Distinct(PathComparer))
        {
            Func<string, bool>? includeFile = clean ? null : IsManagedPlaylist;
            inventories[folder] = await _inventories.CaptureAsync(folder,
                includeFile, progress, ct).ConfigureAwait(false);
        }

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        string firstFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targets[0].Target));
        string recoveryRoot = firstFolder + ".CrossSyncPlaylists-recovery" +
            Path.DirectorySeparatorChar + createdAt.UtcDateTime.ToString("yyyyMMdd-HHmmssfff");
        var desiredPaths = new HashSet<string>(PathComparer);
        var targetPlans = new List<PlaylistExportTargetPlan>();
        var generated = new List<GeneratedPlaylist>();

        int targetIndex = 0;
        foreach (LibraryPlaylistTarget target in targets)
        {
            ct.ThrowIfCancellationRequested();
            IPlaylistWriter[] matchingWriters = _writers.Where(candidate =>
                candidate.CanWrite(target.Type)).Take(2).ToArray();
            if (matchingWriters.Length != 1)
            {
                string code = matchingWriters.Length == 0
                    ? "playlist-writer-missing"
                    : "playlist-writer-ambiguous";
                string message = matchingWriters.Length == 0
                    ? $"No playlist writer is registered for type '{target.Type}'."
                    : $"Multiple playlist writers are registered for type '{target.Type}'.";
                issues.Add(new(code, OperationIssueSeverity.Blocker, message, target.Target));
                targetPlans.Add(new(target.Target, target.Type, target.Sets, [], 0));
                targetIndex++;
                continue;
            }
            IPlaylistWriter writer = matchingWriters[0];
            PlaylistWriterOptions writerOptions = GetWriterOptions(target);
            ResolvedPlaylistLocation[] locations = ResolveLocations(target, input.IndexLocations,
                issues);
            MetadataCache targetCache = SelectCache(input.Cache, locations);
            var files = new List<PlaylistExportFile>();
            int missing = 0;

            if (input.ItunesContext is { } catalog)
            {
                var mapper = new ItlMapper(catalog.ItunesLibrary, targetCache);
                foreach (ItlPlaylist playlist in catalog.ItunesLibrary.Playlists)
                {
                    ct.ThrowIfCancellationRequested();
                    if (playlist.TrackIds.Count > writerOptions.MaxTrackCount)
                    {
                        ReportTrackLimit(playlist.DisplayName, playlist.TrackIds.Count,
                            writerOptions.MaxTrackCount, target, issues);
                        continue;
                    }
                    RenderedPlaylist? rendered = Render(playlist, target, writer, writerOptions,
                        locations, targetCache, mapper, catalog.TracksById, issues, ref missing);
                    if (rendered is null)
                        continue;
                    AddRenderedPlaylist(playlist.DisplayName, rendered, target, targetIndex,
                        desiredPaths, generated, files, issues);
                }
            }
            else
            {
                foreach (PlaylistDocument playlist in input.Playlists)
                {
                    ct.ThrowIfCancellationRequested();
                    if (playlist.Tracks.Count > writerOptions.MaxTrackCount)
                    {
                        ReportTrackLimit(playlist.Name, playlist.Tracks.Count,
                            writerOptions.MaxTrackCount, target, issues);
                        continue;
                    }
                    RenderedPlaylist? rendered = Render(playlist, target, writer, writerOptions,
                        locations, input.Cache, targetCache, issues, ref missing);
                    if (rendered is null)
                        continue;
                    AddRenderedPlaylist(playlist.Name, rendered, target, targetIndex,
                        desiredPaths, generated, files, issues);
                }
            }

            targetPlans.Add(new(target.Target, target.Type, target.Sets, files, missing));
            targetIndex++;
        }

        var actions = new List<FileMutationAction>();
        if (clean)
        {
            foreach (var pair in inventories.OrderBy(pair => pair.Key, PathComparer))
                foreach (OperationPathSnapshot existing in pair.Value.Files.Values
                             .OrderBy(file => file.Path, PathComparer))
                {
                    string stagedDelete = Path.Combine(recoveryRoot, "deleted",
                        Path.GetFileName(pair.Key), Path.GetRelativePath(pair.Key, existing.Path!));
                    actions.Add(new(FileMutationKind.Delete, existing.Path!, stagedDelete,
                        existing, OperationPathSnapshot.Missing(stagedDelete)));
                }
        }

        foreach (GeneratedPlaylist output in generated.OrderBy(item => item.Path, PathComparer))
        {
            FileInventory inventory = inventories[Path.GetFullPath(targets[output.TargetIndex].Target)];
            if (inventory.Files.TryGetValue(output.Path, out OperationPathSnapshot? existing))
                actions.Add(new(clean ? FileMutationKind.Write : FileMutationKind.ReplaceGenerated,
                    "", output.Path, null, existing, output.Content));
            else
                actions.Add(new(FileMutationKind.Write, "", output.Path, null,
                    OperationPathSnapshot.Missing(output.Path), output.Content));
        }

        var mutationPlan = new FileMutationPlan("CrossSyncPlaylists", firstFolder, recoveryRoot,
            actions, issues, createdAt, RetainRecovery: true,
            PolicyFingerprint: input.Configuration.PolicySnapshot.Fingerprint,
            LibraryId: input.Configuration.LibraryId);
        return new(request, targetPlans, mutationPlan, issues);
    }

    public async Task<PlaylistExportResult> ApplyAsync(PlaylistExportPlan plan,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException("The reviewed playlist export plan is blocked.");
        FileMutationSummary mutations = await _executor.ApplyAsync(
            plan.MutationPlan, progress, ct).ConfigureAwait(false);
        return new(plan.Targets.Sum(target => target.Files.Count), mutations, plan.Issues);
    }

    private async Task<PlaylistInputContext> CreateInputContextAsync(
        PlaylistExportRequest request,
        List<OperationIssue> issues,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        LibraryConfiguration? probedConfiguration = null;
        if (!string.IsNullOrWhiteSpace(request.ConfigurationPath))
            probedConfiguration = new LibraryConfiguration(request.ConfigurationPath);

        bool useSources = probedConfiguration is not null &&
            probedConfiguration.PlaylistSources.Count > 0 &&
            !CatalogIsAvailable(request.ItunesLibraryPath ??
                probedConfiguration.ItunesLibraryPath);
        if (!useSources)
        {
            try
            {
                LibraryOperationContext catalog = await _contexts.CreateAsync(
                    request.ConfigurationPath, request.ItunesLibraryPath, progress, ct)
                    .ConfigureAwait(false);
                return new(catalog.Configuration, catalog.IndexLocations, catalog.Cache,
                    catalog, []);
            }
            catch (Exception error) when (request.ConfigurationPath is null &&
                                          error is InvalidOperationException or IOException)
            {
                IndexedLibraryOperationContext indexed = await _contexts.CreateIndexedAsync(
                    request.ConfigurationPath, progress, ct).ConfigureAwait(false);
                if (indexed.Configuration.PlaylistSources.Count == 0)
                    throw;
                issues.Add(new("catalog-unavailable-source-fallback",
                    OperationIssueSeverity.Warning,
                    "The iTunes catalog is unavailable; configured file playlists will be used " +
                    "instead."));
                IReadOnlyList<PlaylistDocument> playlists = await LoadSourcesAsync(
                    indexed.Configuration.PlaylistSources, issues, progress, ct)
                    .ConfigureAwait(false);
                return new(indexed.Configuration, indexed.IndexLocations, indexed.Cache,
                    null, playlists);
            }
        }

        IndexedLibraryOperationContext sourceContext = await _contexts.CreateIndexedAsync(
            request.ConfigurationPath, progress, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.ItunesLibraryPath ??
                sourceContext.Configuration.ItunesLibraryPath))
            issues.Add(new("catalog-unavailable-source-fallback",
                OperationIssueSeverity.Warning,
                "The configured iTunes catalog is unavailable; configured file playlists will " +
                "be used instead."));
        IReadOnlyList<PlaylistDocument> sourcePlaylists = await LoadSourcesAsync(
            sourceContext.Configuration.PlaylistSources, issues, progress, ct)
            .ConfigureAwait(false);
        return new(sourceContext.Configuration, sourceContext.IndexLocations,
            sourceContext.Cache, null, sourcePlaylists);
    }

    private async Task<IReadOnlyList<PlaylistDocument>> LoadSourcesAsync(
        IReadOnlyList<LibraryPlaylistSource> configuredSources,
        List<OperationIssue> issues,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var result = new List<PlaylistDocument>();
        foreach (LibraryPlaylistSource configured in configuredSources)
        {
            ct.ThrowIfCancellationRequested();
            IPlaylistSource[] providers = _sources.Where(source =>
                    source.Id.Equals(configured.Type, StringComparison.OrdinalIgnoreCase) &&
                    source.CanRead(configured.Location))
                .Take(2).ToArray();
            if (providers.Length != 1)
            {
                issues.Add(new(providers.Length == 0
                        ? "playlist-source-missing"
                        : "playlist-source-ambiguous",
                    OperationIssueSeverity.Blocker,
                    providers.Length == 0
                        ? $"No playlist source is registered for type '{configured.Type}'."
                        : $"Multiple playlist sources are registered for type '{configured.Type}'.",
                    configured.Location));
                continue;
            }

            progress?.Report(new(OperationPhase.LoadingLibrary,
                CurrentPath: configured.Location,
                Message: "Loading file playlists"));
            try
            {
                IReadOnlyList<PlaylistDocument> loaded = await providers[0].LoadAsync(
                    new(configured.Location, configured.Recursive), ct).ConfigureAwait(false);
                result.AddRange(loaded);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                issues.Add(new("playlist-source-unavailable", OperationIssueSeverity.Blocker,
                    $"Playlist source could not be loaded: {error.Message}",
                    configured.Location));
            }
        }
        if (configuredSources.Count > 0 && result.Count == 0 &&
            !issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            issues.Add(new("playlist-source-empty", OperationIssueSeverity.Blocker,
                "No playlists were found in the configured playlist sources."));
        return result;
    }

    private static bool CatalogIsAvailable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return File.Exists(ItlFileEditor.ResolveLibraryPath(path));
        }
        catch
        {
            return false;
        }
    }

    private static void ReportTrackLimit(string playlistName, int trackCount, int maximum,
        LibraryPlaylistTarget target, List<OperationIssue> issues) =>
        issues.Add(new("playlist-track-limit", OperationIssueSeverity.Warning,
            $"Playlist '{playlistName}' contains {trackCount} tracks and exceeds the " +
            $"{maximum}-track export limit; it was skipped.", target.Target));

    private static void AddRenderedPlaylist(string playlistName, RenderedPlaylist rendered,
        LibraryPlaylistTarget target, int targetIndex, HashSet<string> desiredPaths,
        List<GeneratedPlaylist> generated, List<PlaylistExportFile> files,
        List<OperationIssue> issues)
    {
        string? fullPath = ReserveDestination(Path.GetFullPath(rendered.DestinationPath),
            target.CollisionPolicy, desiredPaths, issues);
        if (fullPath is null)
            return;
        generated.Add(new(fullPath, rendered.Content, targetIndex));
        files.Add(new(playlistName, fullPath, rendered.TrackCount, rendered.Content.Length));
    }

    private static string? ReserveDestination(string requested,
        LibraryPathCollisionPolicy collisionPolicy, HashSet<string> desiredPaths,
        List<OperationIssue> issues)
    {
        if (desiredPaths.Add(requested))
            return requested;
        if (collisionPolicy == LibraryPathCollisionPolicy.Stop)
        {
            issues.Add(new("output-collision", OperationIssueSeverity.Blocker,
                "Multiple playlist exports map to the same destination.", requested));
            return null;
        }
        if (collisionPolicy == LibraryPathCollisionPolicy.PreserveExisting)
        {
            issues.Add(new("output-collision-preserved", OperationIssueSeverity.Warning,
                "A playlist destination was already reserved; the later playlist was skipped.",
                requested));
            return null;
        }

        string directory = Path.GetDirectoryName(requested)!;
        string stem = Path.GetFileNameWithoutExtension(requested);
        string extension = Path.GetExtension(requested);
        for (int attempt = 2; ; attempt++)
        {
            string suffix = collisionPolicy == LibraryPathCollisionPolicy.Hash
                ? "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    requested + "|" + attempt))).ToLowerInvariant()[..8]
                : $" ({attempt})";
            string candidate = Path.Combine(directory, stem + suffix + extension);
            if (desiredPaths.Add(candidate))
                return candidate;
        }
    }

    private static RenderedPlaylist? Render(ItlPlaylist playlist, LibraryPlaylistTarget target,
        IPlaylistWriter writer, PlaylistWriterOptions writerOptions,
        IReadOnlyList<ResolvedPlaylistLocation> locations, MetadataCache cache, ItlMapper mapper,
        IReadOnlyDictionary<int, ItlTrack> tracksById, List<OperationIssue> issues, ref int missing)
    {
        var tracks = new List<PlaylistWriterTrack>();
        foreach (int id in playlist.TrackIds)
        {
            if (!tracksById.TryGetValue(id, out ItlTrack? track))
                continue;
            string kind = track.Kind ?? "";
            if (track.HasVideo || string.IsNullOrWhiteSpace(track.LocalPath) ||
                kind.Contains("protected", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("book", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("audible", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("document", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("app", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("tone", StringComparison.OrdinalIgnoreCase))
                continue;

            string path;
            try { path = mapper[id]; }
            catch
            {
                missing++;
                issues.Add(new("track-not-mapped", OperationIssueSeverity.Warning,
                    $"Track could not be mapped for playlist '{playlist.DisplayName}'.",
                    track.LocalPath));
                continue;
            }
            int duration = cache[path].DurationInSeconds;
            if (duration == 0) duration = -1;
            path = Remap(path, locations);
            tracks.Add(new(path, duration,
                (track.Artist ?? "").Replace("-", "") + " - " +
                (track.Title ?? "").Replace("-", "")));
        }
        if (tracks.Count == 0)
            return null;

        PlaylistWriterOutput output = writer.Write(new(target.Type, playlist.DisplayName,
            target.Target, tracks), writerOptions);
        return new(output.DestinationPath, output.Content, output.TrackCount);
    }

    private static RenderedPlaylist? Render(PlaylistDocument playlist,
        LibraryPlaylistTarget target, IPlaylistWriter writer,
        PlaylistWriterOptions writerOptions,
        IReadOnlyList<ResolvedPlaylistLocation> locations,
        MetadataCache fullCache,
        MetadataCache targetCache,
        List<OperationIssue> issues,
        ref int missing)
    {
        var tracks = new List<PlaylistWriterTrack>();
        foreach (PlaylistTrackReference reference in playlist.Tracks)
        {
            string? path = MapSourceTrack(reference.Path, fullCache, targetCache);
            if (path is null)
            {
                missing++;
                issues.Add(new("track-not-mapped", OperationIssueSeverity.Warning,
                    $"Track could not be mapped for playlist '{playlist.Name}'.",
                    reference.Path));
                continue;
            }

            MetadataCacheEntry entry = targetCache.FileCache[path];
            int duration = reference.DurationSeconds ?? entry.DurationInSeconds;
            if (duration == 0)
                duration = -1;
            string displayText = reference.DisplayText ??
                ((entry.Artist ?? "") + " - " + (entry.Title ?? ""));
            tracks.Add(new(Remap(path, locations), duration, displayText));
        }
        if (tracks.Count == 0)
            return null;

        PlaylistWriterOutput output = writer.Write(new(target.Type, playlist.Name,
            target.Target, tracks), writerOptions);
        return new(output.DestinationPath, output.Content, output.TrackCount);
    }

    private static string? MapSourceTrack(string sourcePath, MetadataCache fullCache,
        MetadataCache targetCache)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(sourcePath);
        }
        catch
        {
            return null;
        }

        string? direct = FindCachePath(targetCache, normalized);
        if (direct is not null)
            return direct;
        string? sourceCachePath = FindCachePath(fullCache, normalized);
        if (sourceCachePath is null)
            return null;
        MetadataCacheEntry source = fullCache.FileCache[sourceCachePath];

        static bool Same(string? left, string? right) => string.Equals(
            left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        static bool SameNonBlank(string? left, string? right) =>
            !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
            Same(left, right);
        static bool SameArtist(MetadataCacheEntry left, MetadataCacheEntry right) =>
            SameNonBlank(left.Artist, right.Artist) ||
            SameNonBlank(left.Artist, right.AlbumArtist) ||
            SameNonBlank(left.AlbumArtist, right.Artist) ||
            SameNonBlank(left.AlbumArtist, right.AlbumArtist);

        IEnumerable<KeyValuePair<string, MetadataCacheEntry>> candidates =
            targetCache.FileCache.Where(candidate =>
                Same(source.Album, candidate.Value.Album) &&
                SameArtist(source, candidate.Value));
        if (source.TrackNumber is not null)
            candidates = candidates.Where(candidate =>
                candidate.Value.TrackNumber == source.TrackNumber);
        if (source.DiscNumber is not null)
            candidates = candidates.Where(candidate =>
                candidate.Value.DiscNumber == source.DiscNumber);
        KeyValuePair<string, MetadataCacheEntry>[] matches = candidates.ToArray();
        if (matches.Any(candidate => Same(candidate.Value.Title, source.Title)))
            matches = matches.Where(candidate => Same(candidate.Value.Title, source.Title))
                .ToArray();
        return matches
            .OrderByDescending(candidate => PathComparer.Equals(
                Path.GetFileNameWithoutExtension(candidate.Key),
                Path.GetFileNameWithoutExtension(sourceCachePath)))
            .ThenByDescending(candidate => candidate.Value.SampleRate)
            .ThenByDescending(candidate => candidate.Value.BitsPerSample)
            .ThenBy(candidate => candidate.Key, PathComparer)
            .Select(candidate => candidate.Key)
            .FirstOrDefault();
    }

    private static string? FindCachePath(MetadataCache cache, string path)
    {
        if (cache.FileCache.ContainsKey(path))
            return path;
        return cache.FileCache.Keys.FirstOrDefault(candidate => PathComparer.Equals(
            Path.GetFullPath(candidate), path));
    }

    private static PlaylistWriterOptions GetWriterOptions(LibraryPlaylistTarget target)
    {
        PlaylistWriterOptions legacy = target.Type.Equals("wpl",
            StringComparison.OrdinalIgnoreCase) ? LegacyWplOptions : LegacyM3uOptions;
        PlaylistPathStyle pathStyle = target.PathStyle switch
        {
            "legacy" or "provided" => PlaylistPathStyle.AsProvided,
            "absolute" => PlaylistPathStyle.Absolute,
            "relative" => PlaylistPathStyle.Relative,
            _ => throw new InvalidDataException(
                $"Unsupported playlist path style '{target.PathStyle}'."),
        };
        Encoding encoding = target.Encoding switch
        {
            "utf-8" => Encoding.UTF8,
            "utf-16" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            "ascii" => Encoding.ASCII,
            _ => throw new InvalidDataException(
                $"Unsupported playlist encoding '{target.Encoding}'."),
        };
        PlaylistLineEnding lineEnding = target.LineEnding switch
        {
            "platform" => PlaylistLineEnding.Platform,
            "crlf" => PlaylistLineEnding.CrLf,
            "lf" => PlaylistLineEnding.Lf,
            _ => throw new InvalidDataException(
                $"Unsupported playlist line ending '{target.LineEnding}'."),
        };
        Func<string, string> fileNameTransform = target.FileNameTransform switch
        {
            "legacy" => legacy.FileNameTransform,
            "preserve" => static name => name,
            "sanitize" => FixPath,
            "sonos" => static name => FixPath(name).PadRight(SonosNamePad),
            _ => throw new InvalidDataException(
                $"Unsupported playlist filename transform '{target.FileNameTransform}'."),
        };
        return new()
        {
            PathStyle = pathStyle,
            Encoding = encoding,
            EmitByteOrderMark = target.EmitByteOrderMark,
            LineEnding = lineEnding,
            IncludeExtendedInfo = target.IncludeExtendedInfo,
            FileNameTransform = fileNameTransform,
            MaxTrackCount = target.MaxTrackCount,
        };
    }

    private static MetadataCache SelectCache(MetadataCache source,
        IReadOnlyList<ResolvedPlaylistLocation> locations)
    {
        var selected = new MetadataCache(buildSecondaryIndexes: false);
        foreach (var pair in source.FileCache)
            if (locations.Any(location => IsWithin(pair.Key, location.Location.Target)))
                selected.FileCache[pair.Key] = pair.Value;
        return selected;
    }

    private static string Remap(string path, IReadOnlyList<ResolvedPlaylistLocation> locations)
    {
        string normalized = path.Replace('\\', '/');
        foreach (ResolvedPlaylistLocation location in locations)
        {
            string root = location.Location.Target.TrimEnd('\\', '/').Replace('\\', '/');
            if (normalized.StartsWith(root + "/", PathComparison))
            {
                string suffix = normalized[root.Length..];
                return string.IsNullOrEmpty(location.Offset)
                    ? suffix
                    : location.Offset.TrimEnd('/', '\\') + "/" + suffix.TrimStart('/');
            }
        }
        return normalized;
    }

    private static ResolvedPlaylistLocation[] ResolveLocations(LibraryPlaylistTarget target,
        IEnumerable<LibraryIndexLocation> configuredLocations, List<OperationIssue> issues)
    {
        var selected = target.Sets.ToHashSet(LibraryConfiguration.ScanSetComparer);
        var resolved = new List<ResolvedPlaylistLocation>();
        foreach (IGrouping<string, LibraryIndexLocation> group in configuredLocations.GroupBy(
                     location => Path.TrimEndingDirectorySeparator(location.Target), PathComparer))
        {
            var matches = group.SelectMany(location => location.Memberships
                    .Where(membership => selected.Contains(membership.Name))
                    .Select(membership => new
                    {
                        Location = location,
                        Offset = membership.Offset ?? location.DefaultOffset,
                    }))
                .ToArray();
            string?[] offsets = matches.Select(match => match.Offset)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (offsets.Length == 0)
                continue;
            if (offsets.Length > 1)
            {
                issues.Add(new("ambiguous-set-offset", OperationIssueSeverity.Blocker,
                    $"Playlist target selects scan sets with different offsets for index target '{group.Key}'.",
                    target.Target));
                continue;
            }
            resolved.Add(new(matches[0].Location, offsets[0]));
        }
        return resolved.OrderByDescending(item => item.Location.Target.Length).ToArray();
    }

    private static bool IsWithin(string path, string root)
    {
        string normalizedPath = Path.GetFullPath(path);
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
            PathComparison);
    }

    private static bool IsManagedPlaylist(string path) =>
        Path.GetExtension(path).Equals(".m3u", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".m3u8", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".wpl", StringComparison.OrdinalIgnoreCase);

    private static string FixPath(string value)
    {
        string result = value;
        foreach (char character in Path.GetInvalidFileNameChars()) result = result.Replace(character, '_');
        foreach (char character in Path.GetInvalidPathChars()) result = result.Replace(character, '_');
        result = result.Trim();
        if (result.EndsWith('.')) result = result[..^1];
        return result;
    }

    private static PlaylistExportPlan EmptyPlan(PlaylistExportRequest request,
        IReadOnlyList<LibraryPlaylistTarget> targets, IReadOnlyList<OperationIssue> issues)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string root = targets.FirstOrDefault()?.Target ??
            (string.IsNullOrWhiteSpace(request.ConfigurationPath)
                ? Environment.CurrentDirectory
                : Path.GetDirectoryName(Path.GetFullPath(request.ConfigurationPath))!);
        var mutations = new FileMutationPlan("CrossSyncPlaylists", root, "", [], issues, now);
        return new(request, [], mutations, issues);
    }

    private sealed record RenderedPlaylist(string DestinationPath,
        ImmutableArray<byte> Content, int TrackCount);
    private sealed record GeneratedPlaylist(string Path,
        ImmutableArray<byte> Content, int TargetIndex);
    private sealed record ResolvedPlaylistLocation(LibraryIndexLocation Location, string? Offset);
    private sealed record PlaylistInputContext(
        LibraryConfiguration Configuration,
        IReadOnlyList<LibraryIndexLocation> IndexLocations,
        MetadataCache Cache,
        LibraryOperationContext? ItunesContext,
        IReadOnlyList<PlaylistDocument> Playlists);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
