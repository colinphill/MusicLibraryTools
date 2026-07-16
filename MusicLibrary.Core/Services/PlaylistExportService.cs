using System.Collections.Immutable;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using iTunes.Binary;
using MetadataCaching;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public sealed record PlaylistExportRequest(
    string? ConfigurationPath = null,
    string? ItunesLibraryPath = null,
    bool Clean = false);

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

    public PlaylistExportService(ILibraryOperationContextFactory contexts,
        IFileInventoryService inventories, IFileMutationPlanExecutor executor)
    {
        _contexts = contexts;
        _inventories = inventories;
        _executor = executor;
    }

    public async Task<PlaylistExportPlan> PreviewAsync(
        PlaylistExportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        LibraryOperationContext context = await _contexts.CreateAsync(
            request.ConfigurationPath, request.ItunesLibraryPath, progress, ct).ConfigureAwait(false);
        IReadOnlyList<LibraryPlaylistTarget> targets = context.Configuration.PlaylistTargets;
        var issues = new List<OperationIssue>();
        if (targets.Count == 0)
            issues.Add(new("missing-targets", OperationIssueSeverity.Blocker,
                "At least one PlaylistTarget is required."));

        var configuredSets = context.IndexLocations.SelectMany(location => location.Sets)
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
            inventories[folder] = await _inventories.CaptureAsync(folder,
                IsManagedPlaylist, progress, ct).ConfigureAwait(false);

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        string firstFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targets[0].Target));
        string recoveryRoot = firstFolder + ".CrossSyncPlaylists-quarantine" +
            Path.DirectorySeparatorChar + createdAt.UtcDateTime.ToString("yyyyMMdd-HHmmssfff");
        var desiredPaths = new HashSet<string>(PathComparer);
        var targetPlans = new List<PlaylistExportTargetPlan>();
        var generated = new List<GeneratedPlaylist>();

        int targetIndex = 0;
        foreach (LibraryPlaylistTarget target in targets)
        {
            ct.ThrowIfCancellationRequested();
            ResolvedPlaylistLocation[] locations = ResolveLocations(target, context.IndexLocations,
                issues);
            MetadataCache targetCache = SelectCache(context.Cache, locations);
            var mapper = new ItlMapper(context.ItunesLibrary, targetCache);
            var files = new List<PlaylistExportFile>();
            int missing = 0;

            foreach (ItlPlaylist playlist in context.ItunesLibrary.Playlists)
            {
                ct.ThrowIfCancellationRequested();
                if (playlist.TrackIds.Count > MaxPlaylistCount)
                    continue;
                RenderedPlaylist? rendered = Render(playlist, target, locations, targetCache,
                    mapper, context.TracksById, issues, ref missing);
                if (rendered is null)
                    continue;

                string fullPath = Path.GetFullPath(rendered.DestinationPath);
                if (!desiredPaths.Add(fullPath))
                {
                    issues.Add(new("output-collision", OperationIssueSeverity.Blocker,
                        "Multiple playlist exports map to the same destination.", fullPath));
                    continue;
                }
                generated.Add(new(fullPath, rendered.Content, targetIndex));
                files.Add(new(playlist.DisplayName, fullPath, rendered.TrackCount,
                    rendered.Content.Length));
            }

            targetPlans.Add(new(target.Target, target.Type, target.Sets, files, missing));
            targetIndex++;
        }

        var actions = new List<FileMutationAction>();
        foreach (GeneratedPlaylist output in generated.OrderBy(item => item.Path, PathComparer))
        {
            FileInventory inventory = inventories[Path.GetFullPath(targets[output.TargetIndex].Target)];
            if (inventory.Files.TryGetValue(output.Path, out OperationPathSnapshot? existing))
                actions.Add(new(FileMutationKind.ReplaceGenerated, "", output.Path, null,
                    existing, output.Content));
            else
                actions.Add(new(FileMutationKind.Write, "", output.Path, null,
                    OperationPathSnapshot.Missing(output.Path), output.Content));
        }

        if (request.Clean)
        {
            int folderIndex = 0;
            foreach (var pair in inventories.OrderBy(pair => pair.Key, PathComparer))
            {
                foreach (OperationPathSnapshot stale in pair.Value.Files.Values
                             .Where(file => !desiredPaths.Contains(file.Path!))
                             .OrderBy(file => file.Path, PathComparer))
                {
                    string quarantine = Path.Combine(recoveryRoot, "stale",
                        folderIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Path.GetRelativePath(pair.Key, stale.Path!));
                    actions.Add(new(FileMutationKind.Quarantine, stale.Path!, quarantine,
                        stale, OperationPathSnapshot.Missing(quarantine)));
                }
                folderIndex++;
            }
        }

        var mutationPlan = new FileMutationPlan("CrossSyncPlaylists", firstFolder, recoveryRoot,
            actions, issues, createdAt);
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

    private static RenderedPlaylist? Render(ItlPlaylist playlist, LibraryPlaylistTarget target,
        IReadOnlyList<ResolvedPlaylistLocation> locations, MetadataCache cache, ItlMapper mapper,
        IReadOnlyDictionary<int, ItlTrack> tracksById, List<OperationIssue> issues, ref int missing)
    {
        using var m3uStream = new MemoryStream();
        using var m3u = new StreamWriter(m3uStream, Encoding.UTF8, leaveOpen: true);
        m3u.WriteLine("#EXTM3U");

        XAttribute countAttribute;
        XElement sequence;
        var wpl = new XDocument(
            new XProcessingInstruction("wpl", "version=\"1.0\""),
            new XElement("smil",
                new XElement("head",
                    new XElement("meta", new XAttribute("name", "Generator"),
                        new XAttribute("content", "CrossSyncPlaylists")),
                    new XElement("meta", new XAttribute("name", "ItemCount"),
                        countAttribute = new XAttribute("content", "")),
                    new XElement("title", playlist.DisplayName)),
                new XElement("body", sequence = new XElement("seq"))));

        int count = 0;
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
            sequence.Add(new XElement("media", new XAttribute("src", path)));
            m3u.WriteLine("#EXTINF:" + duration + "," +
                (track.Artist ?? "").Replace("-", "") + " - " +
                (track.Title ?? "").Replace("-", ""));
            m3u.WriteLine(path);
            count++;
        }
        if (count == 0)
            return null;

        countAttribute.Value = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string safeName = FixPath(playlist.DisplayName);
        string destination = target.Type == "wpl"
            ? Path.Combine(target.Target, safeName.PadRight(SonosNamePad) + ".wpl")
            : Path.Combine(target.Target, safeName + ".m3u");
        byte[] content;
        if (target.Type == "wpl")
        {
            using var stream = new MemoryStream();
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = true,
                CloseOutput = false,
            };
            using (XmlWriter writer = XmlWriter.Create(stream, settings))
                wpl.Save(writer);
            content = stream.ToArray();
        }
        else
        {
            m3u.Flush();
            content = m3uStream.ToArray();
        }
        return new(destination, content.ToImmutableArray(), count);
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
            if (normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
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

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
