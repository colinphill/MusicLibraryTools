using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public sealed record ConfiguredExportRequest(
    string ProfileId,
    string? ConfigurationPath = null);

public sealed record ConfiguredExportFile(
    string SourcePath,
    string DestinationPath,
    FileMutationKind? Mutation);

public sealed record ConfiguredExportPlan(
    ConfiguredExportRequest Request,
    LibraryExportProfile? Profile,
    Guid LibraryId,
    string LibraryFingerprint,
    string ProfileFingerprint,
    string DestinationRoot,
    IReadOnlyList<ConfiguredExportFile> Files,
    int UnchangedCount,
    int ExtraFileCount,
    ExportTransportPlan? TransportPlan,
    IReadOnlyList<OperationIssue> Issues)
{
    public bool CanApply => Profile is not null && TransportPlan?.CanApply == true &&
        Issues.All(issue => issue.Severity != OperationIssueSeverity.Blocker);
}

public sealed record ConfiguredExportResult(
    string ProfileId,
    int DesiredFileCount,
    int UnchangedCount,
    FileMutationSummary Mutations,
    IReadOnlyList<OperationIssue> Issues);

public interface IConfiguredExportService
{
    Task<ConfiguredExportPlan> PreviewAsync(
        ConfiguredExportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<ConfiguredExportResult> ApplyAsync(
        ConfiguredExportPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Executes the deliberately small first generic-export surface: cache-backed track selection,
/// byte-preserving copies, shared naming, and a reviewed local-filesystem reconciliation plan.
/// Unsupported policy dimensions are blockers instead of being silently ignored.
/// </summary>
public sealed class ConfiguredExportService : IConfiguredExportService
{
    private const string ToolName = "ConfiguredExport";
    private readonly ILibraryOperationContextFactory _contexts;
    private readonly IFileInventoryService _inventories;
    private readonly IPathLayoutResolver _paths;
    private readonly IAppSettings? _settings;
    private readonly IReadOnlyList<IExportTransport> _transports;

    public ConfiguredExportService(
        ILibraryOperationContextFactory contexts,
        IFileInventoryService inventories,
        IEnumerable<IExportTransport> transports,
        IPathLayoutResolver? paths = null,
        IAppSettings? settings = null)
    {
        _contexts = contexts;
        _inventories = inventories;
        _transports = transports.ToArray();
        _paths = paths ?? LibraryPathLayoutResolver.Shared;
        _settings = settings;
    }

    public async Task<ConfiguredExportPlan> PreviewAsync(
        ConfiguredExportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfileId);

        IndexedLibraryOperationContext context = await _contexts.CreateIndexedAsync(
            request.ConfigurationPath, progress, ct).ConfigureAwait(false);
        LibraryConfiguration configuration = context.Configuration;
        string libraryFingerprint = configuration.PolicySnapshot.Fingerprint;
        LibraryExportProfile? profile = configuration.ExportProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, request.ProfileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
            return MissingProfile(request, configuration, libraryFingerprint);

        var issues = new List<OperationIssue>();
        ValidateSupportedPolicy(profile, issues);
        string destinationRoot = NormalizeDestination(profile, issues);
        LibraryIndexLocation? destinationLocation = FindDestinationRoot(
            context.IndexLocations, destinationRoot, issues);
        ValidateDestination(destinationRoot, destinationLocation, context.IndexLocations, issues);

        IExportTransport? transport = ResolveTransport(profile, issues);
        LibraryProfile namingProfile = ResolveNamingProfile(configuration, profile, issues);
        IReadOnlyList<(string Path, MetadataCacheEntry Entry)> selected = SelectSources(
            profile, context, destinationRoot, destinationLocation, issues);
        IReadOnlyDictionary<string, int> continuousTrackNumbers =
            namingProfile.Disc.Strategy == LibraryDiscStrategy.FlattenContinuous
                ? LibraryAlbumIdentityResolver.ContinuousTrackNumbers(
                    selected,
                    item => LibraryAlbumIdentityResolver.Key(
                        item.Entry, namingProfile.AlbumIdentity),
                    item => item.Path,
                    item => item.Entry.DiscNumber,
                    item => item.Entry.TrackNumber)
                : new Dictionary<string, int>(PathComparer);

        progress?.Report(new(OperationPhase.Planning, Message: "Resolving export destinations"));
        var claimed = new Dictionary<string, string>(PathComparer);
        var projected = new List<ProjectedFile>(selected.Count);
        int selectedIndex = 0;
        foreach ((string source, MetadataCacheEntry entry) in selected)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.Planning,
                selectedIndex++,
                selected.Count,
                source,
                "Resolving export destinations"));
            if (!TryCapture(source, out OperationPathSnapshot? sourceSnapshot))
            {
                issues.Add(new("export-source-missing", OperationIssueSeverity.Blocker,
                    "An indexed export source no longer exists.", source));
                continue;
            }

            try
            {
                string initial = profile.Naming.PreserveSourceLayout
                    ? PreserveLayoutPath(source, destinationRoot, context.IndexLocations,
                        destinationLocation)
                    : _paths.Resolve(destinationRoot, namingProfile,
                        LibraryPathMetadata.From(entry, source) with
                        {
                            FlattenedTrackNumber = continuousTrackNumbers
                                .GetValueOrDefault(source),
                        }, configuration.LengthLimit,
                        configuration.DiscNumLengthLimit);
                string destination = ClaimDestination(
                    initial, source, destinationRoot, namingProfile, claimed);
                projected.Add(new(source, destination, sourceSnapshot));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                issues.Add(new("export-naming", OperationIssueSeverity.Blocker,
                    error.Message, source));
            }
        }

        FileInventory inventory = string.IsNullOrWhiteSpace(destinationRoot)
            ? new("", new Dictionary<string, OperationPathSnapshot>(), [], DateTimeOffset.UtcNow)
            : await _inventories.CaptureAsync(destinationRoot, progress: progress, ct: ct)
                .ConfigureAwait(false);
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        string recoveryRoot = string.IsNullOrWhiteSpace(destinationRoot)
            ? ""
            : destinationRoot + ".ConfiguredExport-recovery" + Path.DirectorySeparatorChar +
              createdAt.UtcDateTime.ToString("yyyyMMdd-HHmmssfff");
        var actions = new List<FileMutationAction>();
        var files = new List<ConfiguredExportFile>();
        var desiredPaths = projected.Select(item => item.DestinationPath).ToHashSet(PathComparer);
        int unchanged = 0;
        foreach (ProjectedFile item in projected.OrderBy(item => item.DestinationPath, PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            if (!inventory.Files.TryGetValue(item.DestinationPath, out OperationPathSnapshot? existing))
            {
                actions.Add(new(FileMutationKind.Copy, item.SourcePath, item.DestinationPath,
                    item.SourceSnapshot, OperationPathSnapshot.Missing(item.DestinationPath)));
                files.Add(new(item.SourcePath, item.DestinationPath, FileMutationKind.Copy));
                continue;
            }

            if (SameFileIdentity(item.SourceSnapshot, existing))
            {
                unchanged++;
                files.Add(new(item.SourcePath, item.DestinationPath, null));
                continue;
            }

            if (profile.Reconciliation.ReplaceChangedFiles)
            {
                actions.Add(new(FileMutationKind.Replace, item.SourcePath, item.DestinationPath,
                    item.SourceSnapshot, existing));
                files.Add(new(item.SourcePath, item.DestinationPath, FileMutationKind.Replace));
            }
            else
            {
                unchanged++;
                files.Add(new(item.SourcePath, item.DestinationPath, null));
                issues.Add(new("export-existing-preserved", OperationIssueSeverity.Warning,
                    "The destination differs from its source and ReplaceChangedFiles is disabled.",
                    item.DestinationPath));
            }
        }

        OperationPathSnapshot[] extras = inventory.Files.Values
            .Where(item => item.Path is not null && !desiredPaths.Contains(item.Path))
            .OrderBy(item => item.Path, PathComparer)
            .ToArray();
        int removalCount = profile.Reconciliation.ExtraFiles ==
                           ExportExtraFileDisposition.Preserve
            ? 0
            : extras.Length;
        if (profile.Reconciliation.MaximumRemovals is int maximum && removalCount > maximum)
            issues.Add(new("export-removal-limit", OperationIssueSeverity.Blocker,
                $"The export would reconcile {removalCount:N0} extra files, exceeding the " +
                $"configured maximum of {maximum:N0}.", destinationRoot));

        foreach (OperationPathSnapshot extra in extras)
        {
            ct.ThrowIfCancellationRequested();
            string path = extra.Path!;
            switch (profile.Reconciliation.ExtraFiles)
            {
                case ExportExtraFileDisposition.Preserve:
                    break;
                case ExportExtraFileDisposition.Quarantine:
                    string quarantine = Path.Combine(recoveryRoot, "extras",
                        Path.GetRelativePath(destinationRoot, path));
                    actions.Add(new(FileMutationKind.Quarantine, path, quarantine,
                        extra, OperationPathSnapshot.Missing(quarantine)));
                    break;
                case ExportExtraFileDisposition.Delete:
                    string stagedDelete = Path.Combine(recoveryRoot, "deleted",
                        Path.GetRelativePath(destinationRoot, path));
                    actions.Add(new(FileMutationKind.Delete, path, stagedDelete,
                        extra, OperationPathSnapshot.Missing(stagedDelete)));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        OperationIssue[] reviewedIssues = issues.ToArray();
        var mutations = new FileMutationPlan(
            ToolName, destinationRoot, recoveryRoot, actions, reviewedIssues, createdAt,
            RetainRecovery: true, PolicyFingerprint: libraryFingerprint,
            LibraryId: configuration.LibraryId);
        ExportTransportPlan? transportPlan = transport?.Prepare(profile, mutations);
        if (transportPlan is not null && transportPlan.Issues.Count > 0)
            reviewedIssues = [.. reviewedIssues, .. transportPlan.Issues];

        progress?.Report(new(
            OperationPhase.Completed,
            files.Count,
            files.Count,
            Message:
                $"Prepared {files.Count:N0} export file(s) and " +
                $"{extras.Length:N0} reconciliation candidate(s)"));
        return new(request, profile, configuration.LibraryId, libraryFingerprint,
            profile.Fingerprint, destinationRoot, files, unchanged, extras.Length,
            transportPlan, reviewedIssues);
    }

    public async Task<ConfiguredExportResult> ApplyAsync(
        ConfiguredExportPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply || plan.Profile is null || plan.TransportPlan is null)
            throw new InvalidOperationException("The configured export preview contains blockers.");

        LibraryConfiguration currentConfiguration = LoadCurrentConfiguration(plan.Request);
        if (currentConfiguration.LibraryId != plan.LibraryId ||
            !string.Equals(currentConfiguration.PolicySnapshot.Fingerprint,
                plan.LibraryFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The library policy changed after preview. Preview the export again.");
        LibraryExportProfile currentProfile = currentConfiguration.ExportProfiles
            .FirstOrDefault(candidate => string.Equals(
                candidate.Id, plan.Profile.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "The configured export profile was removed after preview.");
        if (!currentProfile.Enabled ||
            !string.Equals(currentProfile.Fingerprint, plan.ProfileFingerprint,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The export profile changed after preview. Preview the export again.");

        IExportTransport transport = _transports.FirstOrDefault(candidate =>
            string.Equals(candidate.Descriptor.Id, plan.TransportPlan.TransportId,
                StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidOperationException(
                $"Export transport '{plan.TransportPlan.TransportId}' is no longer available.");
        ExportTransportResult result = await transport.ApplyAsync(
            plan.TransportPlan, currentProfile, progress, ct).ConfigureAwait(false);
        return new(currentProfile.Id, plan.Files.Count, plan.UnchangedCount,
            result.Mutations, result.Issues);
    }

    private static void ValidateSupportedPolicy(
        LibraryExportProfile profile,
        ICollection<OperationIssue> issues)
    {
        if (!profile.Enabled)
            issues.Add(Blocker("export-profile-disabled",
                $"Export profile '{profile.Name}' is disabled."));
        if (profile.Selection.Kind is not (ExportSelectionKind.EntireLibrary or
                                           ExportSelectionKind.ExplicitTracks))
            issues.Add(Blocker("export-selection-unsupported",
                $"Selection mode '{profile.Selection.Kind}' is not yet supported."));
        if (profile.Transform.Mode is not (ExportTransformMode.Preserve or
                                           ExportTransformMode.Copy))
            issues.Add(Blocker("export-transform-unsupported",
                $"Transform mode '{profile.Transform.Mode}' is not yet supported."));
        bool identityArtwork = profile.Artwork.Mode == ExportArtworkMode.Embedded &&
                               !profile.Artwork.FrontCoverOnly &&
                               profile.Artwork.PreserveEncoding &&
                               profile.Artwork.MaximumDimension is null &&
                               profile.Artwork.MaximumBytes is null;
        if (!identityArtwork)
            issues.Add(Blocker("export-artwork-unsupported",
                "This first export provider only preserves existing embedded artwork exactly; " +
                "artwork filtering, conversion, omission, and sidecar generation are not yet supported."));
        if (profile.Playlists.Enabled)
            issues.Add(Blocker("export-playlists-unsupported",
                "Playlist generation is not yet supported by configured exports."));
        if (profile.Reconciliation.RemoveEmptyDirectories)
            issues.Add(Blocker("export-empty-directories-unsupported",
                "Removing empty destination directories is not yet supported."));
    }

    private static string NormalizeDestination(
        LibraryExportProfile profile,
        ICollection<OperationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(profile.Transport.Destination))
        {
            issues.Add(Blocker("export-destination-missing",
                $"Export profile '{profile.Name}' has no destination."));
            return "";
        }
        try
        {
            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(profile.Transport.Destination));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or
                                            PathTooLongException)
        {
            issues.Add(new("export-destination-invalid", OperationIssueSeverity.Blocker,
                error.Message, profile.Transport.Destination));
            return "";
        }
    }

    private static LibraryIndexLocation? FindDestinationRoot(
        IReadOnlyList<LibraryIndexLocation> locations,
        string destinationRoot,
        ICollection<OperationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
            return null;
        LibraryIndexLocation[] matches = locations.Where(location => PathComparer.Equals(
            NormalizePath(location.Target), destinationRoot)).ToArray();
        if (matches.Length == 0)
        {
            issues.Add(new("export-root-unconfigured", OperationIssueSeverity.Blocker,
                "The export destination must be a configured library root.", destinationRoot));
            return null;
        }
        if (matches.Any(location =>
                !location.Permissions.HasFlag(LibraryRootPermissions.SynchronizeOutput)))
            issues.Add(new("export-root-permission", OperationIssueSeverity.Blocker,
                "The export destination root does not permit SynchronizeOutput.",
                destinationRoot));
        return matches[0];
    }

    private static void ValidateDestination(
        string destinationRoot,
        LibraryIndexLocation? destinationLocation,
        IReadOnlyList<LibraryIndexLocation> locations,
        ICollection<OperationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
            return;
        string? filesystemRoot = Path.GetPathRoot(destinationRoot);
        if (filesystemRoot is not null && PathComparer.Equals(
                destinationRoot, Path.TrimEndingDirectorySeparator(filesystemRoot)))
            issues.Add(new("export-filesystem-root", OperationIssueSeverity.Blocker,
                "A filesystem root cannot be used as an export destination.", destinationRoot));
        foreach (LibraryIndexLocation source in locations.Where(location =>
                     destinationLocation is null || location.RootId != destinationLocation.RootId))
            if (PathsOverlap(destinationRoot, source.Target))
                issues.Add(new("export-root-overlap", OperationIssueSeverity.Blocker,
                    "The export destination overlaps an indexed source root.", source.Target));
    }

    private IExportTransport? ResolveTransport(
        LibraryExportProfile profile,
        ICollection<OperationIssue> issues)
    {
        if (!string.Equals(profile.Transport.ProviderId,
                LocalFileSystemExportTransport.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Blocker("export-transport-unsupported",
                $"Transport '{profile.Transport.ProviderId}' is not supported by configured exports."));
            return null;
        }
        IExportTransport? transport = _transports.FirstOrDefault(candidate => string.Equals(
            candidate.Descriptor.Id, LocalFileSystemExportTransport.ProviderId,
            StringComparison.OrdinalIgnoreCase));
        if (transport is null)
            issues.Add(Blocker("export-transport-unavailable",
                "The local-filesystem export transport is not available."));
        return transport;
    }

    private static LibraryProfile ResolveNamingProfile(
        LibraryConfiguration configuration,
        LibraryExportProfile profile,
        ICollection<OperationIssue> issues)
    {
        bool hasProfileReference =
            !string.IsNullOrWhiteSpace(profile.Naming.LibraryProfileId);
        bool hasSelfContainedTemplates = !hasProfileReference &&
            (!string.IsNullOrWhiteSpace(profile.Naming.FolderTemplate) ||
             !string.IsNullOrWhiteSpace(profile.Naming.FileNameTemplate));
        LibraryProfile selected = hasSelfContainedTemplates
            ? LibraryProfilePresets.Create(
                LibraryProfilePreset.Custom, "export-generic", "Generic export naming")
            : configuration.ActiveProfile;
        if (hasProfileReference)
        {
            LibraryProfile? referenced = configuration.Profiles.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, profile.Naming.LibraryProfileId,
                    StringComparison.OrdinalIgnoreCase));
            if (referenced is null)
                issues.Add(Blocker("export-naming-profile-missing",
                    $"Naming profile '{profile.Naming.LibraryProfileId}' was not found."));
            else
                selected = referenced;
        }

        LibraryNamingPolicy naming = selected.Naming;
        if (!string.IsNullOrWhiteSpace(profile.Naming.FolderTemplate))
            naming = naming with { DirectoryTemplate = profile.Naming.FolderTemplate };
        if (!string.IsNullOrWhiteSpace(profile.Naming.FileNameTemplate))
            naming = naming with { FileNameTemplate = profile.Naming.FileNameTemplate };
        if (profile.Naming.CollisionPolicy is { } collision)
            naming = naming with { CollisionPolicy = collision };
        if (hasSelfContainedTemplates)
            naming = naming with
            {
                UseItunesCanonicalNaming = false,
                LegacySanitization = false,
            };
        return selected with { Naming = naming };
    }

    private static IReadOnlyList<(string Path, MetadataCacheEntry Entry)> SelectSources(
        LibraryExportProfile profile,
        IndexedLibraryOperationContext context,
        string destinationRoot,
        LibraryIndexLocation? destinationLocation,
        ICollection<OperationIssue> issues)
    {
        var available = context.Cache.FileCache
            .Where(pair => string.IsNullOrWhiteSpace(destinationRoot) ||
                           !IsWithin(pair.Key, destinationRoot))
            .OrderBy(pair => pair.Key, PathComparer)
            .ToArray();
        if (profile.Selection.Kind == ExportSelectionKind.EntireLibrary)
            return available.Select(pair => (pair.Key, pair.Value)).ToArray();
        if (profile.Selection.Kind != ExportSelectionKind.ExplicitTracks)
            return [];

        var selected = new Dictionary<string, MetadataCacheEntry>(PathComparer);
        foreach (string value in profile.Selection.Values)
        {
            KeyValuePair<string, MetadataCacheEntry>[] matches = FindExplicitMatches(
                value, available, context.IndexLocations, destinationLocation);
            if (matches.Length == 0)
                issues.Add(new("export-track-not-found", OperationIssueSeverity.Blocker,
                    "An explicitly selected track is not present in the indexed cache.", value));
            else if (matches.Length > 1)
                issues.Add(new("export-track-ambiguous", OperationIssueSeverity.Blocker,
                    "A relative explicit-track selection matches more than one indexed file.", value));
            else
                selected[matches[0].Key] = matches[0].Value;
        }
        return selected.Select(pair => (pair.Key, pair.Value))
            .OrderBy(pair => pair.Key, PathComparer).ToArray();
    }

    private static KeyValuePair<string, MetadataCacheEntry>[] FindExplicitMatches(
        string value,
        IReadOnlyList<KeyValuePair<string, MetadataCacheEntry>> available,
        IReadOnlyList<LibraryIndexLocation> locations,
        LibraryIndexLocation? destinationLocation)
    {
        if (Path.IsPathRooted(value))
        {
            string full = Path.GetFullPath(value);
            return available.Where(pair => PathComparer.Equals(
                Path.GetFullPath(pair.Key), full)).ToArray();
        }
        string relative = NormalizeRelative(value);
        return available.Where(pair => locations.Any(location =>
                (destinationLocation is null || location.RootId != destinationLocation.RootId) &&
                IsWithin(pair.Key, location.Target) &&
                RelativeComparer.Equals(
                    NormalizeRelative(Path.GetRelativePath(location.Target, pair.Key)), relative)))
            .ToArray();
    }

    private string ClaimDestination(
        string initial,
        string source,
        string destinationRoot,
        LibraryProfile namingProfile,
        IDictionary<string, string> claimed)
    {
        string candidate = Path.GetFullPath(initial);
        for (var collision = 2; claimed.ContainsKey(candidate); collision++)
        {
            candidate = _paths.ResolveCollision(
                initial, source, namingProfile, collision);
            if (!IsWithin(candidate, destinationRoot))
                throw new InvalidDataException(
                    "The collision policy resolved outside the export destination. " +
                    "Use stop, suffix, or hash for exports.");
        }
        if (!IsWithin(candidate, destinationRoot))
            throw new InvalidDataException(
                "The naming policy resolved outside the export destination.");
        claimed[candidate] = source;
        return candidate;
    }

    private static string PreserveLayoutPath(
        string source,
        string destinationRoot,
        IReadOnlyList<LibraryIndexLocation> locations,
        LibraryIndexLocation? destinationLocation)
    {
        LibraryIndexLocation? owner = locations
            .Where(location =>
                (destinationLocation is null || location.RootId != destinationLocation.RootId) &&
                IsWithin(source, location.Target))
            .OrderByDescending(location => NormalizePath(location.Target).Length)
            .FirstOrDefault();
        if (owner is null)
            throw new InvalidDataException(
                "The indexed source is not contained by a configured source root.");
        string relative = Path.GetRelativePath(owner.Target, source);
        if (relative == ".." || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("The preserved source path escapes its root.");
        return Path.Combine(destinationRoot, relative);
    }

    private LibraryConfiguration LoadCurrentConfiguration(ConfiguredExportRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ConfigurationPath))
            return new LibraryConfiguration(Path.GetFullPath(request.ConfigurationPath));
        return _settings?.GetSnapshot().Configuration ??
            throw new InvalidOperationException(
                "The active library configuration is no longer available.");
    }

    private static ConfiguredExportPlan MissingProfile(
        ConfiguredExportRequest request,
        LibraryConfiguration configuration,
        string fingerprint)
    {
        OperationIssue[] issues =
        [
            new("export-profile-not-found", OperationIssueSeverity.Blocker,
                $"Export profile '{request.ProfileId}' was not found."),
        ];
        return new(request, null, configuration.LibraryId, fingerprint, "", "", [], 0, 0,
            null, issues);
    }

    private static bool TryCapture(
        string path,
        out OperationPathSnapshot snapshot)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            snapshot = OperationPathSnapshot.Missing(path);
            return false;
        }
        snapshot = new(true, false, file.Length, file.LastWriteTimeUtc)
            { Path = file.FullName };
        return true;
    }

    private static bool SameFileIdentity(
        OperationPathSnapshot source,
        OperationPathSnapshot destination) =>
        source.Length == destination.Length &&
        source.LastWriteTimeUtc == destination.LastWriteTimeUtc;

    private static OperationIssue Blocker(string code, string message) =>
        new(code, OperationIssueSeverity.Blocker, message);

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string NormalizeRelative(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

    private static bool IsWithin(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;
        string normalizedPath = Path.GetFullPath(path);
        string normalizedRoot = NormalizePath(root);
        return PathComparer.Equals(normalizedPath, normalizedRoot) ||
               normalizedPath.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool PathsOverlap(string first, string second) =>
        IsWithin(first, second) || IsWithin(second, first);

    private sealed record ProjectedFile(
        string SourcePath,
        string DestinationPath,
        OperationPathSnapshot SourceSnapshot);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparer RelativeComparer = PathComparer;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
