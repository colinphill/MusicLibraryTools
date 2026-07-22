using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public sealed record CrossLibrarySyncRequest(
    string? ConfigurationPath = null,
    string? ItunesLibraryPath = null);

public sealed record CrossLibrarySyncPlannedFile(
    int TrackId,
    string SourcePath,
    string DestinationPath,
    FileMutationKind? Mutation);

public sealed record CrossLibrarySyncPlan(
    CrossLibrarySyncRequest Request,
    string TargetRoot,
    IReadOnlyList<CrossLibrarySyncPlannedFile> Files,
    int UnchangedCount,
    int StaleCount,
    FileMutationPlan MutationPlan,
    IReadOnlyList<OperationIssue> Issues,
    LibraryExportProfile? ExportProfile = null,
    ExportTransportPlan? TransportPlan = null)
{
    public bool CanApply => MutationPlan.CanApply && (TransportPlan?.CanApply ?? true);
}

public sealed record CrossLibrarySyncResult(
    int DesiredCount,
    int UnchangedCount,
    FileMutationSummary Mutations,
    IReadOnlyList<OperationIssue> Issues);

public interface ICrossLibrarySyncService
{
    Task<CrossLibrarySyncPlan> PreviewAsync(
        CrossLibrarySyncRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<CrossLibrarySyncResult> ApplyAsync(
        CrossLibrarySyncPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Plans a deterministic projection of configured iTunes playlists into a target tree. Preview
/// inventories the destination once and records exact source/destination snapshots; apply executes
/// only those reviewed actions.
/// </summary>
public sealed class CrossLibrarySyncService : ICrossLibrarySyncService
{
    private readonly ILibraryOperationContextFactory _contexts;
    private readonly IFileInventoryService _inventories;
    private readonly IFileMutationPlanExecutor _executor;
    private readonly IMediaFormatRegistry _formats;
    private readonly IExportTransport _transport;

    public CrossLibrarySyncService(
        ILibraryOperationContextFactory contexts,
        IFileInventoryService inventories,
        IFileMutationPlanExecutor executor,
        IMediaFormatRegistry? formats = null,
        IExportTransport? transport = null)
    {
        _contexts = contexts;
        _inventories = inventories;
        _executor = executor;
        _formats = formats ?? MediaFormatRegistry.Default;
        _transport = transport ?? new LocalFileSystemExportTransport(executor);
    }

    public async Task<CrossLibrarySyncPlan> PreviewAsync(
        CrossLibrarySyncRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        LibraryOperationContext context = await _contexts.CreateAsync(
            request.ConfigurationPath, request.ItunesLibraryPath, progress, ct).ConfigureAwait(false);
        var issues = new List<OperationIssue>();
        string configuredTarget;
        try
        {
            configuredTarget = context.Configuration.CrossSyncTargetLibraryPath;
        }
        catch (InvalidDataException error)
        {
            return BlockedPlan(request, "", "missing-target", error.Message);
        }

        string targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredTarget));
        LibraryIndexLocation? syncTarget = context.IndexLocations.FirstOrDefault(location =>
            location.IsSyncTarget && PathComparer.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Target)), targetRoot));
        LibraryProfile targetProfile = syncTarget is null
            ? context.Configuration.ActiveProfile
            : context.Configuration.GetEffectiveProfile(syncTarget);
        if (syncTarget is not null &&
            !syncTarget.Permissions.HasFlag(LibraryRootPermissions.SynchronizeOutput))
            issues.Add(new("sync-not-permitted", OperationIssueSeverity.Blocker,
                "The selected root does not permit synchronization output.", targetRoot));
        string? filesystemRoot = Path.GetPathRoot(targetRoot);
        if (filesystemRoot is not null && PathComparer.Equals(
                targetRoot, Path.TrimEndingDirectorySeparator(filesystemRoot)))
            issues.Add(new("filesystem-root", OperationIssueSeverity.Blocker,
                "A filesystem root cannot be used as the synchronization target.", targetRoot));
        foreach (var source in context.IndexLocations.Where(location =>
                     !location.IsSyncTarget && PathsOverlap(targetRoot, location.Target)))
            issues.Add(new("source-overlap", OperationIssueSeverity.Blocker,
                "The synchronization target overlaps an indexed source root.", source.Target));
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return EmptyPlan(request, targetRoot, issues);

        IReadOnlyList<string> requestedPlaylists = context.Configuration.SyncPlaylists;
        if (requestedPlaylists.Count == 0)
            issues.Add(new("missing-playlists", OperationIssueSeverity.Blocker,
                "At least one SyncPlaylist entry is required."));
        var playlists = requestedPlaylists
            .Select(name => (Name: name, Playlist: context.ItunesLibrary.FindPlaylist(name)))
            .ToArray();
        foreach (var missing in playlists.Where(item => item.Playlist is null))
            issues.Add(new("playlist-not-found", OperationIssueSeverity.Blocker,
                $"Configured playlist was not found: {missing.Name}"));
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return EmptyPlan(request, targetRoot, issues);

        bool includeNonMusic = context.Configuration["DeleteNonMusic"].Length != 0;
        bool keepFolderImages = context.Configuration["KeepFolderImages"].Length != 0;
        bool deleteStaleFiles = context.Configuration.DeleteStaleCrossSyncFiles;
        bool IncludeDestinationFile(string path)
        {
            if (_formats.SupportsPath(path, MediaFormatCapabilities.LibraryIndex))
                return true;
            return includeNonMusic && !(keepFolderImages &&
                Path.GetFileNameWithoutExtension(path).Equals("folder",
                    StringComparison.OrdinalIgnoreCase));
        }

        FileInventory inventory = await _inventories.CaptureAsync(
            targetRoot, IncludeDestinationFile, progress, ct).ConfigureAwait(false);
        progress?.Report(new(OperationPhase.Planning, Message: "Projecting desired library state"));

        var cachedByPath = new Dictionary<string, MetadataCacheEntry>(context.Cache.FileCache,
            PathComparer);
        IReadOnlyDictionary<string, int> continuousTrackNumbers =
            targetProfile.Disc.Strategy == LibraryDiscStrategy.FlattenContinuous
                ? LibraryAlbumIdentityResolver.ContinuousTrackNumbers(
                    cachedByPath,
                    item => LibraryAlbumIdentityResolver.Key(
                        item.Value, targetProfile.AlbumIdentity),
                    item => item.Key,
                    item => item.Value.DiscNumber,
                    item => item.Value.TrackNumber)
                : new Dictionary<string, int>(PathComparer);
        var candidates = new List<Candidate>();
        var claimedDestinations = new Dictionary<string, string>(PathComparer);
        int examined = 0;
        foreach (var playlist in playlists)
        {
            foreach (int id in playlist.Playlist!.TrackIds)
            {
                ct.ThrowIfCancellationRequested();
                examined++;
                if (!context.TracksById.TryGetValue(id, out var track))
                {
                    issues.Add(new("track-not-found", OperationIssueSeverity.Warning,
                        $"Playlist '{playlist.Name}' references missing track id {id}."));
                    continue;
                }
                string kind = track.Kind ?? "";
                if (track.HasVideo || kind.Contains("audible", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(track.LocalPath))
                    continue;

                string source = NormalizeLibraryPath(track.LocalPath);
                if (!File.Exists(source))
                {
                    issues.Add(new("source-missing", OperationIssueSeverity.Blocker,
                        "A selected source track is missing.", source));
                    continue;
                }

                MetadataCacheEntry entry;
                if (!cachedByPath.TryGetValue(source, out entry!))
                {
                    try
                    {
                        entry = new MetadataCacheEntry(MediaFile.GetFile(source, readOnly: true),
                            File.GetLastWriteTimeUtc(source));
                        entry.Strip();
                    }
                    catch (Exception error)
                    {
                        issues.Add(new("metadata-read", OperationIssueSeverity.Blocker,
                            $"Could not read selected source metadata: {error.Message}", source));
                        continue;
                    }
                }

                string destination;
                try
                {
                    destination = LibraryPathLayoutResolver.Shared.Resolve(
                        targetRoot,
                        targetProfile,
                        LibraryPathMetadata.From(entry, source) with
                        {
                            FlattenedTrackNumber = continuousTrackNumbers
                                .GetValueOrDefault(source),
                        },
                        context.Configuration.LengthLimit,
                        context.Configuration.DiscNumLengthLimit);
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException)
                {
                    issues.Add(new("naming-policy", OperationIssueSeverity.Blocker,
                        error.Message, source));
                    continue;
                }
                if (claimedDestinations.TryGetValue(destination, out string? claimedSource) &&
                    !PathComparer.Equals(claimedSource, source))
                {
                    if (targetProfile.Naming.CollisionPolicy ==
                        LibraryPathCollisionPolicy.PreserveExisting)
                    {
                        issues.Add(new("destination-preserved",
                            OperationIssueSeverity.Warning,
                            $"Skipped '{source}' because '{destination}' is already claimed and " +
                            "the naming profile preserves existing destinations.", destination));
                        continue;
                    }

                    string initial = destination;
                    try
                    {
                        int collisionNumber = 2;
                        do
                        {
                            destination = LibraryPathLayoutResolver.Shared.ResolveCollision(
                                initial, source, targetProfile, collisionNumber++);
                            if (targetProfile.Naming.CollisionPolicy ==
                                LibraryPathCollisionPolicy.Hash)
                                break;
                        }
                        while (claimedDestinations.TryGetValue(destination,
                            out claimedSource) && !PathComparer.Equals(claimedSource, source));
                    }
                    catch (InvalidDataException error)
                    {
                        issues.Add(new("destination-collision",
                            OperationIssueSeverity.Blocker, error.Message, initial));
                        continue;
                    }

                    if (claimedDestinations.TryGetValue(destination, out claimedSource) &&
                        !PathComparer.Equals(claimedSource, source))
                    {
                        issues.Add(new("destination-collision",
                            OperationIssueSeverity.Blocker,
                            "The naming collision policy could not produce a unique destination.",
                            destination));
                        continue;
                    }
                }
                claimedDestinations.TryAdd(destination, source);
                candidates.Add(new(id, source, destination,
                    CaptureExisting(source), entry.LastWriteTime));
                if ((examined & 127) == 0)
                    progress?.Report(new(OperationPhase.Planning, examined,
                        CurrentPath: source, Message: $"Projected {examined:N0} playlist entries"));
            }
        }

        Candidate[][] collisions = candidates
            .GroupBy(candidate => candidate.DestinationPath, PathComparer)
            .Where(group => group.Select(candidate => candidate.SourcePath)
                .Distinct(PathComparer).Skip(1).Any())
            .Select(group => group.ToArray())
            .ToArray();
        foreach (Candidate[] collision in collisions)
            issues.Add(new("destination-collision", OperationIssueSeverity.Blocker,
                "Multiple source tracks map to the same destination: " +
                string.Join("; ", collision.Select(item => item.SourcePath).Distinct(PathComparer)),
                collision[0].DestinationPath));

        Candidate[] desired = candidates
            .GroupBy(candidate => candidate.DestinationPath, PathComparer)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.DestinationPath, PathComparer)
            .ToArray();
        var desiredPaths = desired.Select(candidate => candidate.DestinationPath)
            .ToHashSet(PathComparer);
        OperationPathSnapshot[] stale = inventory.Files.Values
            .Where(snapshot => !desiredPaths.Contains(snapshot.Path!))
            .OrderBy(snapshot => snapshot.Path, PathComparer)
            .ToArray();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        string recoveryRoot = targetRoot + ".CrossSyncMusic-recovery" +
            Path.DirectorySeparatorChar +
            createdAt.UtcDateTime.ToString("yyyyMMdd-HHmmssfff");
        var actions = new List<FileMutationAction>();
        var plannedFiles = new List<CrossLibrarySyncPlannedFile>();
        int unchanged = 0;
        foreach (Candidate candidate in desired)
        {
            if (inventory.Files.TryGetValue(candidate.DestinationPath, out var destination))
            {
                if (candidate.MetadataLastWriteTimeUtc > destination.LastWriteTimeUtc)
                {
                    actions.Add(new(FileMutationKind.Replace, candidate.SourcePath,
                        candidate.DestinationPath, candidate.SourceSnapshot, destination));
                    plannedFiles.Add(new(candidate.TrackId, candidate.SourcePath,
                        candidate.DestinationPath, FileMutationKind.Replace));
                }
                else
                {
                    unchanged++;
                    plannedFiles.Add(new(candidate.TrackId, candidate.SourcePath,
                        candidate.DestinationPath, null));
                }
            }
            else
            {
                var missingDestination = OperationPathSnapshot.Missing(candidate.DestinationPath);
                actions.Add(new(FileMutationKind.Copy, candidate.SourcePath,
                    candidate.DestinationPath, candidate.SourceSnapshot, missingDestination));
                plannedFiles.Add(new(candidate.TrackId, candidate.SourcePath,
                    candidate.DestinationPath, FileMutationKind.Copy));
            }
        }
        foreach (OperationPathSnapshot staleFile in stale)
        {
            if (deleteStaleFiles)
            {
                string stagedDelete = Path.Combine(recoveryRoot, "deleted",
                    Path.GetRelativePath(targetRoot, staleFile.Path!));
                actions.Add(new(FileMutationKind.Delete, staleFile.Path!, stagedDelete,
                    staleFile, null));
            }
            else
            {
                string quarantine = Path.Combine(recoveryRoot, "stale",
                    Path.GetRelativePath(targetRoot, staleFile.Path!));
                actions.Add(new(FileMutationKind.Quarantine, staleFile.Path!, quarantine,
                    staleFile, OperationPathSnapshot.Missing(quarantine)));
            }
        }

        var mutationPlan = new FileMutationPlan("CrossSyncMusic", targetRoot, recoveryRoot,
            actions, issues, createdAt, RetainRecovery: true,
            PolicyFingerprint: context.Configuration.PolicySnapshot.Fingerprint,
            LibraryId: context.Configuration.LibraryId);
        LibraryExportProfile exportProfile = LibraryExportProfile.LegacyCrossLibrary(
            targetRoot, requestedPlaylists, targetProfile.Id, deleteStaleFiles);
        ExportTransportPlan transportPlan = _transport.Prepare(exportProfile, mutationPlan);
        OperationIssue[] planIssues = issues.Concat(transportPlan.Issues).ToArray();
        return new(request, targetRoot, plannedFiles, unchanged, stale.Length,
            mutationPlan, planIssues, exportProfile, transportPlan);
    }

    public async Task<CrossLibrarySyncResult> ApplyAsync(
        CrossLibrarySyncPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException("The reviewed cross-library sync plan is blocked.");
        FileMutationSummary summary;
        if (plan.ExportProfile is not null && plan.TransportPlan is not null)
        {
            ExportTransportResult result = await _transport.ApplyAsync(
                plan.TransportPlan, plan.ExportProfile, progress, ct).ConfigureAwait(false);
            summary = result.Mutations;
        }
        else
        {
            // Compatibility for plans serialized or constructed before export transports existed.
            summary = await _executor.ApplyAsync(
                plan.MutationPlan, progress, ct).ConfigureAwait(false);
        }
        return new(plan.Files.Count, plan.UnchangedCount, summary, plan.Issues);
    }

    private static CrossLibrarySyncPlan BlockedPlan(CrossLibrarySyncRequest request,
        string targetRoot, string code, string message) =>
        EmptyPlan(request, targetRoot,
            [new OperationIssue(code, OperationIssueSeverity.Blocker, message)]);

    private static CrossLibrarySyncPlan EmptyPlan(CrossLibrarySyncRequest request,
        string targetRoot, IReadOnlyList<OperationIssue> issues)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string recovery = string.IsNullOrWhiteSpace(targetRoot)
            ? ""
            : targetRoot + ".CrossSyncMusic-quarantine" + Path.DirectorySeparatorChar +
              now.UtcDateTime.ToString("yyyyMMdd-HHmmssfff");
        var mutations = new FileMutationPlan("CrossSyncMusic", targetRoot, recovery, [], issues, now);
        return new(request, targetRoot, [], 0, 0, mutations, issues);
    }

    private static OperationPathSnapshot CaptureExisting(string path)
    {
        var info = new FileInfo(path);
        return new(true, false, info.Length, info.LastWriteTimeUtc) { Path = info.FullName };
    }

    private static string NormalizeLibraryPath(string path)
    {
        string normalized = path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\" + path[2..].Replace(@"\\", @"\")
            : path.Replace(@"\\", @"\");
        return Path.GetFullPath(normalized);
    }

    private static bool PathsOverlap(string first, string second)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)) +
            Path.DirectorySeparatorChar;
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)) +
            Path.DirectorySeparatorChar;
        return a.StartsWith(b, PathComparison) || b.StartsWith(a, PathComparison);
    }

    private sealed record Candidate(
        int TrackId,
        string SourcePath,
        string DestinationPath,
        OperationPathSnapshot SourceSnapshot,
        DateTime MetadataLastWriteTimeUtc);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
