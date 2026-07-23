using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public enum PlaylistWorkspaceEncoding
{
    Utf8,
    Utf8WithBom,
    Utf16LittleEndian,
}

public sealed record PlaylistWorkspaceConfiguration(
    string Name,
    string Format,
    string OutputPath,
    PlaylistPathStyle PathStyle = PlaylistPathStyle.Relative,
    PlaylistWorkspaceEncoding Encoding = PlaylistWorkspaceEncoding.Utf8,
    PlaylistLineEnding LineEnding = PlaylistLineEnding.Platform,
    bool IncludeExtendedInfo = true,
    bool OnePlaylistPerGroup = false,
    MetadataFieldKey? GroupByField = null,
    string GroupFileNameTemplate = "{Group}");

public sealed record PlaylistWorkspaceRequest(
    IReadOnlyList<string> Paths,
    PlaylistWorkspaceConfiguration Configuration);

public sealed record PlaylistWorkspaceFilePlan(
    string Group,
    string DestinationPath,
    int TrackCount,
    int ByteCount);

public sealed record PlaylistWorkspacePlan(
    PlaylistWorkspaceRequest Request,
    IReadOnlyList<PlaylistWorkspaceFilePlan> Files,
    FileMutationPlan MutationPlan,
    IReadOnlyList<OperationIssue> Issues)
{
    public bool CanApply => MutationPlan.CanApply;
}

public sealed record PlaylistWorkspaceResult(
    int PlaylistCount,
    int TrackReferenceCount,
    FileMutationSummary Mutations,
    IReadOnlyList<OperationIssue> Issues);

public interface IPlaylistWorkspaceService
{
    Task<PlaylistWorkspacePlan> PreviewAsync(
        PlaylistWorkspaceRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<PlaylistWorkspaceResult> ApplyAsync(
        PlaylistWorkspacePlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Builds ad-hoc playlists from an ordered path selection. All bytes and
/// destination snapshots are captured during preview so apply writes exactly
/// the output that was reviewed.
/// </summary>
public sealed class PlaylistWorkspaceService(
    IMetadataDocumentService documents,
    IFileMutationPlanExecutor executor,
    IEnumerable<IPlaylistWriter> writers) : IPlaylistWorkspaceService
{
    public async Task<PlaylistWorkspacePlan> PreviewAsync(
        PlaylistWorkspaceRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Paths);
        ArgumentNullException.ThrowIfNull(request.Configuration);
        PlaylistWorkspaceConfiguration configuration =
            request.Configuration;
        var issues = Validate(request);
        IPlaylistWriter[] matching = writers
            .Where(writer => writer.CanWrite(configuration.Format))
            .Take(2)
            .ToArray();
        if (matching.Length != 1)
            issues.Add(new(
                matching.Length == 0
                    ? "playlist-workspace-writer-missing"
                    : "playlist-workspace-writer-ambiguous",
                OperationIssueSeverity.Blocker,
                matching.Length == 0
                    ? $"No writer is registered for '{configuration.Format}'."
                    : $"Multiple writers are registered for '{configuration.Format}'."));
        if (issues.Any(issue =>
                issue.Severity == OperationIssueSeverity.Blocker))
            return EmptyPlan(request, issues);

        var normalizedPaths = new List<string>(request.Paths.Count);
        foreach (string path in request.Paths.Where(path =>
                     !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                normalizedPaths.Add(Path.GetFullPath(path));
            }
            catch (Exception error) when (
                error is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                issues.Add(new(
                    "playlist-workspace-source-invalid",
                    OperationIssueSeverity.Blocker,
                    error.Message,
                    path));
            }
        }
        if (issues.Any(issue =>
                issue.Severity == OperationIssueSeverity.Blocker))
            return EmptyPlan(request, issues);
        string[] paths = normalizedPaths.ToArray();
        var tracks = new List<WorkspaceTrack>(paths.Length);
        for (int index = 0; index < paths.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            string path = paths[index];
            if (path.IndexOfAny(['\r', '\n']) >= 0)
            {
                issues.Add(new(
                    "playlist-workspace-path-line-break",
                    OperationIssueSeverity.Blocker,
                    "Playlist paths cannot contain line breaks.",
                    path));
                continue;
            }
            progress?.Report(new(
                OperationPhase.LoadingLibrary,
                index,
                paths.Length,
                path,
                $"Reading playlist track {index + 1:N0} of {paths.Length:N0}"));
            try
            {
                MediaDocument document = await documents.LoadAsync(
                    path, includeArtwork: false, ct).ConfigureAwait(false);
                tracks.Add(new(
                    document.Path,
                    BuildDisplayText(document),
                    DurationSeconds(document),
                    GroupValue(document, configuration.GroupByField)));
            }
            catch (Exception error) when (
                error is not OperationCanceledException)
            {
                issues.Add(new(
                    "playlist-workspace-source-unreadable",
                    OperationIssueSeverity.Blocker,
                    error.Message,
                    path));
            }
        }
        if (issues.Any(issue =>
                issue.Severity == OperationIssueSeverity.Blocker))
            return EmptyPlan(request, issues);

        string outputRoot = OutputRoot(configuration);
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        string recoveryRoot = Path.Combine(
            outputRoot,
            ".MusicLibraryManager-playlist-recovery",
            createdAt.UtcDateTime.ToString(
                "yyyyMMdd-HHmmssfff",
                CultureInfo.InvariantCulture));
        var actions = new List<FileMutationAction>();
        var files = new List<PlaylistWorkspaceFilePlan>();
        var destinations = new HashSet<string>(PathComparer);
        IReadOnlyList<PlaylistGroup> groups =
            BuildGroups(tracks, configuration);
        for (int index = 0; index < groups.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            PlaylistGroup group = groups[index];
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                groups.Count,
                Message:
                    $"Rendering playlist {index + 1:N0} of {groups.Count:N0}"));
            try
            {
                PlaylistWriterOutput output = matching[0].Write(
                    CreateWriterRequest(configuration, group),
                    CreateWriterOptions(configuration, group));
                string destination = Path.GetFullPath(
                    output.DestinationPath);
                if (!destinations.Add(destination))
                {
                    issues.Add(new(
                        "playlist-workspace-output-collision",
                        OperationIssueSeverity.Blocker,
                        "Two playlist groups resolve to the same output path.",
                        destination));
                    continue;
                }
                OperationPathSnapshot snapshot =
                    CaptureSnapshot(destination);
                if (snapshot.IsDirectory)
                {
                    issues.Add(new(
                        "playlist-workspace-output-is-directory",
                        OperationIssueSeverity.Blocker,
                        "The playlist output path is an existing directory.",
                        destination));
                    continue;
                }
                actions.Add(new(
                    snapshot.Exists
                        ? FileMutationKind.ReplaceGenerated
                        : FileMutationKind.Write,
                    "",
                    destination,
                    null,
                    snapshot,
                    output.Content));
                files.Add(new(
                    group.Key,
                    destination,
                    output.TrackCount,
                    output.Content.Length));
            }
            catch (Exception error) when (
                error is not OperationCanceledException)
            {
                issues.Add(new(
                    "playlist-workspace-render-failed",
                    OperationIssueSeverity.Blocker,
                    error.Message));
            }
        }

        var mutationPlan = new FileMutationPlan(
            "PlaylistWorkspace",
            outputRoot,
            recoveryRoot,
            actions,
            issues,
            createdAt,
            RetainRecovery: true);
        progress?.Report(new(
            OperationPhase.Completed,
            files.Count,
            files.Count,
            Message:
                $"Prepared {files.Count:N0} playlist(s) with " +
                $"{files.Sum(file => file.TrackCount):N0} track reference(s)"));
        return new(request, files, mutationPlan, issues);
    }

    public async Task<PlaylistWorkspaceResult> ApplyAsync(
        PlaylistWorkspacePlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException(
                "The reviewed playlist plan contains blocking issues.");
        FileMutationSummary mutations = await executor.ApplyAsync(
            plan.MutationPlan, progress, ct).ConfigureAwait(false);
        return new(
            plan.Files.Count,
            plan.Files.Sum(file => file.TrackCount),
            mutations,
            [.. plan.Issues, .. mutations.Issues]);
    }

    private static List<OperationIssue> Validate(
        PlaylistWorkspaceRequest request)
    {
        PlaylistWorkspaceConfiguration configuration =
            request.Configuration;
        var issues = new List<OperationIssue>();
        if (!request.Paths.Any(path =>
                !string.IsNullOrWhiteSpace(path)))
            issues.Add(new(
                "playlist-workspace-sources-empty",
                OperationIssueSeverity.Blocker,
                "Select at least one playlist track."));
        if (string.IsNullOrWhiteSpace(configuration.Name))
            issues.Add(new(
                "playlist-workspace-name-empty",
                OperationIssueSeverity.Blocker,
                "A playlist name is required."));
        if (string.IsNullOrWhiteSpace(configuration.Format))
            issues.Add(new(
                "playlist-workspace-format-empty",
                OperationIssueSeverity.Blocker,
                "A playlist format is required."));
        if (string.IsNullOrWhiteSpace(configuration.OutputPath))
            issues.Add(new(
                "playlist-workspace-output-empty",
                OperationIssueSeverity.Blocker,
                "A playlist output path is required."));
        else
        {
            try
            {
                string output = Path.GetFullPath(
                    configuration.OutputPath);
                if (configuration.OnePlaylistPerGroup &&
                    File.Exists(output))
                    issues.Add(new(
                        "playlist-workspace-output-is-file",
                        OperationIssueSeverity.Blocker,
                        "Grouped playlist output requires a directory.",
                        output));
                if (!configuration.OnePlaylistPerGroup &&
                    Directory.Exists(output))
                    issues.Add(new(
                        "playlist-workspace-output-is-directory",
                        OperationIssueSeverity.Blocker,
                        "Single playlist output requires a file path.",
                        output));
                if (!configuration.OnePlaylistPerGroup)
                {
                    string extension = Path.GetExtension(output);
                    string expected = Extension(configuration.Format);
                    if (extension.Length > 0 &&
                        !extension.Equals(
                            expected,
                            StringComparison.OrdinalIgnoreCase))
                        issues.Add(new(
                            "playlist-workspace-extension-mismatch",
                            OperationIssueSeverity.Blocker,
                            $"The selected format requires a '{expected}' output file.",
                            output));
                }
            }
            catch (Exception error) when (
                error is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                issues.Add(new(
                    "playlist-workspace-output-invalid",
                    OperationIssueSeverity.Blocker,
                    $"The playlist output path is invalid: {error.Message}"));
            }
        }
        if (configuration.OnePlaylistPerGroup &&
            configuration.GroupByField is null)
            issues.Add(new(
                "playlist-workspace-group-required",
                OperationIssueSeverity.Blocker,
                "Grouped playlist output requires a metadata field."));
        if (configuration.OnePlaylistPerGroup &&
            string.IsNullOrWhiteSpace(
                configuration.GroupFileNameTemplate))
            issues.Add(new(
                "playlist-workspace-template-empty",
                OperationIssueSeverity.Blocker,
                "A grouped playlist filename template is required."));
        if (configuration.Format.Equals(
                "m3u8",
                StringComparison.OrdinalIgnoreCase) &&
            configuration.Encoding ==
                PlaylistWorkspaceEncoding.Utf16LittleEndian)
            issues.Add(new(
                "playlist-workspace-m3u8-encoding",
                OperationIssueSeverity.Blocker,
                "M3U8 playlists must use UTF-8 encoding."));
        return issues;
    }

    private static PlaylistWriterRequest CreateWriterRequest(
        PlaylistWorkspaceConfiguration configuration,
        PlaylistGroup group)
    {
        string directory = configuration.OnePlaylistPerGroup
            ? Path.GetFullPath(configuration.OutputPath)
            : Path.GetDirectoryName(
                Path.GetFullPath(configuration.OutputPath))!;
        string name = configuration.OnePlaylistPerGroup
            ? $"{configuration.Name} - {group.Key}"
            : configuration.Name;
        return new(
            configuration.Format,
            name,
            directory,
            group.Tracks.Select(track => new PlaylistWriterTrack(
                track.Path,
                track.DurationSeconds,
                track.DisplayText)).ToArray());
    }

    private static PlaylistWriterOptions CreateWriterOptions(
        PlaylistWorkspaceConfiguration configuration,
        PlaylistGroup group)
    {
        string baseName = configuration.OnePlaylistPerGroup
            ? GroupFileName(configuration, group.Key)
            : SingleFileName(configuration);
        return new()
        {
            PathStyle = configuration.PathStyle,
            Encoding = Encoding(configuration.Encoding),
            EmitByteOrderMark =
                configuration.Encoding != PlaylistWorkspaceEncoding.Utf8,
            LineEnding = configuration.LineEnding,
            IncludeExtendedInfo = configuration.IncludeExtendedInfo,
            FileNameTransform = _ => baseName,
        };
    }

    private static string SingleFileName(
        PlaylistWorkspaceConfiguration configuration)
    {
        string fileName = Path.GetFileName(
            Path.GetFullPath(configuration.OutputPath));
        string extension = Extension(configuration.Format);
        return fileName.EndsWith(
            extension,
            StringComparison.OrdinalIgnoreCase)
            ? fileName[..^extension.Length]
            : fileName;
    }

    private static string GroupFileName(
        PlaylistWorkspaceConfiguration configuration,
        string group)
    {
        string safeGroup = SafeFileName(
            string.IsNullOrWhiteSpace(group) ? "Missing" : group);
        string fileName = configuration.GroupFileNameTemplate
            .Replace(
                "{Group}",
                safeGroup,
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                "{Name}",
                SafeFileName(configuration.Name),
                StringComparison.OrdinalIgnoreCase)
            .Trim();
        string extension = Extension(configuration.Format);
        if (fileName.EndsWith(
                extension,
                StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^extension.Length];
        return SafeFileName(fileName);
    }

    private static IReadOnlyList<PlaylistGroup> BuildGroups(
        IReadOnlyList<WorkspaceTrack> tracks,
        PlaylistWorkspaceConfiguration configuration)
    {
        if (!configuration.OnePlaylistPerGroup)
            return [new("", tracks.ToList())];
        var groups = new List<PlaylistGroup>();
        var indexes = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        foreach (WorkspaceTrack track in tracks)
        {
            string key = string.IsNullOrWhiteSpace(track.Group)
                ? "Missing"
                : track.Group;
            if (!indexes.TryGetValue(key, out int index))
            {
                index = groups.Count;
                indexes.Add(key, index);
                groups.Add(new(key, []));
            }
            groups[index].Tracks.Add(track);
        }
        return groups;
    }

    private static string GroupValue(
        MediaDocument document,
        MetadataFieldKey? field) =>
        field is null
            ? ""
            : string.Join(
                "; ",
                document.Values(field)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string BuildDisplayText(MediaDocument document)
    {
        string artist = string.Join(
            "; ",
            document.Values(MetadataFieldKey.Known(TagFields.Artist)));
        string title = string.Join(
            "; ",
            document.Values(MetadataFieldKey.Known(TagFields.Title)));
        string display = artist.Length > 0 && title.Length > 0
            ? $"{artist} - {title}"
            : title.Length > 0
                ? title
                : Path.GetFileNameWithoutExtension(document.Path);
        return display
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static int? DurationSeconds(MediaDocument document)
    {
        double duration = document.Codec?.DurationInSeconds ?? 0;
        return duration > 0 && duration <= int.MaxValue
            ? (int)Math.Round(
                duration,
                MidpointRounding.AwayFromZero)
            : null;
    }

    private static Encoding Encoding(
        PlaylistWorkspaceEncoding encoding) =>
        encoding switch
        {
            PlaylistWorkspaceEncoding.Utf8 =>
                new UTF8Encoding(false),
            PlaylistWorkspaceEncoding.Utf8WithBom =>
                new UTF8Encoding(true),
            PlaylistWorkspaceEncoding.Utf16LittleEndian =>
                new UnicodeEncoding(false, true),
            _ => throw new ArgumentOutOfRangeException(
                nameof(encoding)),
        };

    private static string Extension(string format) =>
        format.ToLowerInvariant() switch
        {
            "m3u" => ".m3u",
            "m3u8" => ".m3u8",
            "wpl" => ".wpl",
            _ => "." + format.Trim().TrimStart('.'),
        };

    private static string OutputRoot(
        PlaylistWorkspaceConfiguration configuration)
    {
        string output = Path.GetFullPath(configuration.OutputPath);
        return configuration.OnePlaylistPerGroup
            ? output
            : Path.GetDirectoryName(output)!;
    }

    private static string SafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
            result.Append(
                invalid.Contains(character) ||
                char.IsControl(character) ||
                character is '/' or '\\'
                    ? '_'
                    : character);
        string safe = result.ToString().Trim().Trim('.');
        return safe.Length == 0 ? "Playlist" : safe;
    }

    private static OperationPathSnapshot CaptureSnapshot(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            var directory = new DirectoryInfo(fullPath);
            return new(true, true, 0, directory.LastWriteTimeUtc)
            {
                Path = fullPath,
            };
        }
        var file = new FileInfo(fullPath);
        return file.Exists
            ? new(
                true,
                false,
                file.Length,
                file.LastWriteTimeUtc)
            {
                Path = fullPath,
            }
            : OperationPathSnapshot.Missing(fullPath);
    }

    private static PlaylistWorkspacePlan EmptyPlan(
        PlaylistWorkspaceRequest request,
        IReadOnlyList<OperationIssue> issues)
    {
        string root = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(
                request.Configuration.OutputPath))
        {
            try
            {
                root = OutputRoot(request.Configuration);
            }
            catch (Exception error) when (
                error is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
            }
        }
        var mutation = new FileMutationPlan(
            "PlaylistWorkspace",
            root,
            Path.Combine(
                root,
                ".MusicLibraryManager-playlist-recovery"),
            [],
            issues,
            DateTimeOffset.UtcNow);
        return new(request, [], mutation, issues);
    }

    private sealed record WorkspaceTrack(
        string Path,
        string DisplayText,
        int? DurationSeconds,
        string Group);

    private sealed record PlaylistGroup(
        string Key,
        List<WorkspaceTrack> Tracks);

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
