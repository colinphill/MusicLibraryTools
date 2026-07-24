using System.Globalization;
using System.Text.RegularExpressions;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public interface IReviewedFileOperationService
{
    Task<ReviewedFileOperationPlan> PreviewAsync(
        ReviewedFileOperationRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<FileMutationSummary> ApplyAsync(
        ReviewedFileOperationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Builds immutable, collision-resolved copy/move/rename/quarantine plans for arbitrary files.
/// The ordinary mutation executor supplies whole-plan stale checks, policy-fingerprint checks,
/// durable recovery journals, catalog relocation, rollback, and Operations-based restore.
/// </summary>
public sealed class ReviewedFileOperationService(
    IFileMutationPlanExecutor executor,
    IAppSettings settings) : IReviewedFileOperationService
{
    private static readonly Regex TemplateToken = new(
        @"\{(?<name>[^{}]+)\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Task<ReviewedFileOperationPlan> PreviewAsync(
        ReviewedFileOperationRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(
            () => Preview(request, progress, ct),
            ct);
    }

    public Task<FileMutationSummary> ApplyAsync(
        ReviewedFileOperationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return executor.ApplyAsync(plan.MutationPlan, progress, ct);
    }

    private ReviewedFileOperationPlan Preview(
        ReviewedFileOperationRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        string[] sources = request.SourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        string? destinationDirectory =
            string.IsNullOrWhiteSpace(request.DestinationDirectory)
                ? null
                : Path.GetFullPath(request.DestinationDirectory);
        string? commonSourceDirectory = sources.Length == 0
            ? null
            : CommonDirectory(sources);
        var claimed = new HashSet<string>(PathComparer);
        var items = new List<ReviewedFileOperationItem>(sources.Length);
        var actions = new List<FileMutationAction>(sources.Length);
        var issues = new List<OperationIssue>();
        LibraryConfiguration? configuration =
            settings.GetSnapshot().Configuration;

        if (sources.Length == 0)
            issues.Add(new(
                "file-operation.empty",
                OperationIssueSeverity.Blocker,
                "Choose at least one source file."));
        if (request.Kind != ReviewedFileOperationKind.Rename &&
            destinationDirectory is null)
            issues.Add(new(
                "file-operation.destination-required",
                OperationIssueSeverity.Blocker,
                "Choose a destination folder."));
        if (destinationDirectory is not null &&
            File.Exists(destinationDirectory))
            issues.Add(new(
                "file-operation.destination-file",
                OperationIssueSeverity.Blocker,
                "The reviewed destination folder is an existing file.",
                destinationDirectory));
        if (string.IsNullOrWhiteSpace(request.FileNameTemplate))
            issues.Add(new(
                "file-operation.template-required",
                OperationIssueSeverity.Blocker,
                "Enter a filename template."));

        for (int index = 0; index < sources.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            string source = sources[index];
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                sources.Length,
                source,
                $"Planning {request.Kind.ToString().ToLowerInvariant()}"));
            var itemIssues = new List<OperationIssue>();
            OperationPathSnapshot sourceSnapshot = Capture(source);
            if (!sourceSnapshot.Exists)
                itemIssues.Add(new(
                    "file-operation.source-missing",
                    OperationIssueSeverity.Blocker,
                    "The source file no longer exists.",
                    source));
            else if (sourceSnapshot.IsDirectory)
                itemIssues.Add(new(
                    "file-operation.source-directory",
                    OperationIssueSeverity.Blocker,
                    "This editor accepts files, not directories.",
                    source));

            string? destination = ResolveDestination(
                request,
                source,
                index,
                destinationDirectory,
                commonSourceDirectory,
                itemIssues);
            if (destination is not null &&
                PathComparer.Equals(source, destination))
            {
                itemIssues.Add(new(
                    "file-operation.unchanged",
                    OperationIssueSeverity.Information,
                    "The source already has the reviewed name and location.",
                    source));
                destination = null;
            }

            FileMutationKind mutationKind =
                request.Kind switch
                {
                    ReviewedFileOperationKind.Copy =>
                        FileMutationKind.Copy,
                    ReviewedFileOperationKind.Move or
                    ReviewedFileOperationKind.Rename =>
                        FileMutationKind.Move,
                    ReviewedFileOperationKind.Quarantine =>
                        FileMutationKind.Quarantine,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(request.Kind)),
                };

            if (destination is not null)
            {
                destination = ResolveCollision(
                    source,
                    destination,
                    request.CollisionPolicy,
                    claimed,
                    itemIssues);
                ValidatePermissions(
                    request.Kind,
                    source,
                    destination,
                    configuration,
                    itemIssues);
                if (!itemIssues.Any(issue =>
                        issue.Severity ==
                        OperationIssueSeverity.Blocker))
                {
                    claimed.Add(destination);
                    actions.Add(new(
                        mutationKind,
                        source,
                        destination,
                        sourceSnapshot,
                        Capture(destination)));
                }
            }

            issues.AddRange(itemIssues);
            items.Add(new(
                source,
                destination,
                mutationKind,
                itemIssues));
        }

        DetectSourceDestinationCycles(actions, issues);
        if (issues.Any(issue =>
                issue.Code == "file-operation.cycle"))
            actions.Clear();

        string anchor = commonSourceDirectory ??
            destinationDirectory ??
            Path.GetTempPath();
        string recoveryContainer = RecoveryContainer(anchor);
        string recoveryRoot = Path.Combine(
            recoveryContainer,
            DateTime.UtcNow.ToString(
                "yyyyMMdd-HHmmssfff",
                CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N"));
        var mutationPlan = new FileMutationPlan(
            "MusicLibraryManager",
            destinationDirectory ?? anchor,
            recoveryRoot,
            actions,
            issues,
            DateTimeOffset.UtcNow,
            RetainRecovery: true,
            PolicyFingerprint:
                configuration?.PolicySnapshot.Fingerprint,
            LibraryId: configuration?.LibraryId);
        progress?.Report(new(
            OperationPhase.Completed,
            sources.Length,
            sources.Length,
            Message:
                $"Reviewed {sources.Length:N0} file operation(s)"));
        return new(request, items, mutationPlan);
    }

    private static string? ResolveDestination(
        ReviewedFileOperationRequest request,
        string source,
        int index,
        string? destinationDirectory,
        string? commonSourceDirectory,
        List<OperationIssue> issues)
    {
        string expanded = ExpandTemplate(
            request.FileNameTemplate,
            source,
            index,
            issues);
        if (string.IsNullOrWhiteSpace(expanded))
            return null;
        if (expanded.IndexOfAny(
                [Path.DirectorySeparatorChar,
                 Path.AltDirectorySeparatorChar]) >= 0 ||
            expanded.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            issues.Add(new(
                "file-operation.invalid-name",
                OperationIssueSeverity.Blocker,
                $"The template produced an invalid filename: '{expanded}'.",
                source));
            return null;
        }

        string directory;
        if (request.Kind == ReviewedFileOperationKind.Rename)
        {
            directory = Path.GetDirectoryName(source)!;
        }
        else
        {
            if (destinationDirectory is null)
                return null;
            directory = destinationDirectory;
            if (request.PreserveRelativeLayout &&
                commonSourceDirectory is not null)
            {
                string relative = Path.GetRelativePath(
                    commonSourceDirectory,
                    Path.GetDirectoryName(source)!);
                if (relative != ".")
                    directory = Path.Combine(directory, relative);
            }
        }
        return Path.GetFullPath(Path.Combine(directory, expanded));
    }

    private static string ExpandTemplate(
        string template,
        string source,
        int index,
        List<OperationIssue> issues)
    {
        string name = Path.GetFileNameWithoutExtension(source);
        string extension = Path.GetExtension(source);
        bool unknown = false;
        string expanded = TemplateToken.Replace(
            template,
            match =>
            {
                string token = match.Groups["name"].Value;
                if (token.Equals(
                        "Name",
                        StringComparison.OrdinalIgnoreCase))
                    return name;
                if (token.Equals(
                        "Extension",
                        StringComparison.OrdinalIgnoreCase))
                    return extension;
                if (token.Equals(
                        "Index",
                        StringComparison.OrdinalIgnoreCase))
                    return (index + 1).ToString(
                        CultureInfo.InvariantCulture);
                unknown = true;
                return match.Value;
            });
        if (unknown)
            issues.Add(new(
                "file-operation.unknown-token",
                OperationIssueSeverity.Blocker,
                "Filename templates support only {Name}, {Extension}, and {Index}.",
                source));
        return expanded.Trim();
    }

    private static string ResolveCollision(
        string source,
        string destination,
        ReviewedFileCollisionPolicy policy,
        HashSet<string> claimed,
        List<OperationIssue> issues)
    {
        if (!File.Exists(destination) &&
            !Directory.Exists(destination) &&
            !claimed.Contains(destination))
            return destination;
        if (PathComparer.Equals(source, destination))
            return destination;
        if (policy == ReviewedFileCollisionPolicy.Stop)
        {
            issues.Add(new(
                "file-operation.collision",
                OperationIssueSeverity.Blocker,
                "The reviewed destination already exists or is claimed by another source.",
                destination));
            return destination;
        }

        string directory = Path.GetDirectoryName(destination)!;
        string name = Path.GetFileNameWithoutExtension(destination);
        string extension = Path.GetExtension(destination);
        for (int suffix = 2; ; suffix++)
        {
            string candidate = Path.Combine(
                directory,
                $"{name}_{suffix}{extension}");
            if (!File.Exists(candidate) &&
                !Directory.Exists(candidate) &&
                !claimed.Contains(candidate))
                return candidate;
        }
    }

    private static void ValidatePermissions(
        ReviewedFileOperationKind kind,
        string source,
        string destination,
        LibraryConfiguration? configuration,
        List<OperationIssue> issues)
    {
        if (configuration is null)
            return;
        LibraryIndexLocation[] roots =
            configuration.IndexLocations.ToArray();
        if (kind != ReviewedFileOperationKind.Copy &&
            LibraryRootPermissionPolicy.MostSpecific(
                source,
                roots) is not null &&
            !LibraryRootPermissionPolicy.Allows(
                source,
                roots,
                LibraryRootPermissions.OrganizeFiles))
            issues.Add(new(
                "file-operation.source-permission",
                OperationIssueSeverity.Blocker,
                "The active library policy does not permit moving this source.",
                source));
        if (LibraryRootPermissionPolicy.MostSpecific(
                destination,
                roots) is not null &&
            !LibraryRootPermissionPolicy.Allows(
                destination,
                roots,
                LibraryRootPermissions.OrganizeFiles))
            issues.Add(new(
                "file-operation.destination-permission",
                OperationIssueSeverity.Blocker,
                "The active library policy does not permit file operations at this destination.",
                destination));
    }

    private static void DetectSourceDestinationCycles(
        IReadOnlyList<FileMutationAction> actions,
        List<OperationIssue> issues)
    {
        HashSet<string> sources = actions
            .Where(action =>
                action.Kind == FileMutationKind.Move)
            .Select(action => action.SourcePath)
            .ToHashSet(PathComparer);
        foreach (FileMutationAction action in actions.Where(
                     action =>
                         action.Kind == FileMutationKind.Move &&
                         sources.Contains(action.DestinationPath)))
            issues.Add(new(
                "file-operation.cycle",
                OperationIssueSeverity.Blocker,
                "A reviewed destination is also a move source. Choose a suffix or a different template.",
                action.DestinationPath));
    }

    private static OperationPathSnapshot Capture(string path)
    {
        string fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (file.Exists)
            return new(
                true,
                false,
                file.Length,
                file.LastWriteTimeUtc)
            {
                Path = fullPath,
            };
        var directory = new DirectoryInfo(fullPath);
        if (directory.Exists)
            return new(
                true,
                true,
                0,
                directory.LastWriteTimeUtc)
            {
                Path = fullPath,
            };
        return OperationPathSnapshot.Missing(fullPath);
    }

    private static string CommonDirectory(
        IReadOnlyList<string> paths)
    {
        string common = Path.GetDirectoryName(paths[0])!;
        string root = Path.GetPathRoot(common) ?? "";
        foreach (string path in paths.Skip(1))
        {
            string directory = Path.GetDirectoryName(path)!;
            if (!PathComparer.Equals(
                    Path.GetPathRoot(directory),
                    root))
                return Path.GetDirectoryName(paths[0])!;
            while (!IsWithin(directory, common))
                common = Path.GetDirectoryName(common) ?? root;
        }
        return Path.TrimEndingDirectorySeparator(common);
    }

    private static string RecoveryContainer(string anchor)
    {
        string normalized =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(anchor));
        string root = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(normalized) ?? normalized);
        return PathComparer.Equals(normalized, root)
            ? Path.Combine(
                normalized,
                ".MusicLibraryManager-recovery")
            : normalized +
              ".MusicLibraryManager-recovery";
    }

    private static bool IsWithin(
        string path,
        string root)
    {
        string normalizedPath =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
        string normalizedRoot =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(root));
        return PathComparer.Equals(
                   normalizedPath,
                   normalizedRoot) ||
               normalizedPath.StartsWith(
                   normalizedRoot +
                   Path.DirectorySeparatorChar,
                   PathComparison);
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
