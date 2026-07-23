using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public interface IMetadataDocumentService
{
    Task<MediaDocument> LoadAsync(
        string path,
        bool includeArtwork = true,
        CancellationToken ct = default);
}

public interface IWorkbenchService
{
    Task<WorkbenchLoadResult> LoadAsync(
        WorkbenchLoadRequest request,
        CancellationToken ct = default);
}

public interface IMetadataOperationService
{
    Task<MetadataOperationPlan> PreviewAsync(
        IReadOnlyList<string> paths,
        OperationRecipe recipe,
        CancellationToken ct = default);

    Task<MetadataOperationPlan> PreviewEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagEdit>> editsByPath,
        string name,
        CancellationToken ct = default);

    Task<MetadataOperationPlan> PreviewValueEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> editsByPath,
        string name,
        CancellationToken ct = default);

    Task<MetadataApplyResult> ApplyAsync(
        MetadataOperationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public interface IEditHistoryService
{
    IReadOnlyList<EditHistoryEntry> Entries { get; }
    IReadOnlyList<EditHistoryEntry> RedoEntries { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Record(EditHistoryEntry entry);
    Task<int> UndoLatestAsync(
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}

/// <summary>Reads complete tag layers directly from disk for the workbench.</summary>
public sealed class MetadataDocumentService(
    IMediaFormatRegistry formats) : IMetadataDocumentService
{
    public Task<MediaDocument> LoadAsync(
        string path,
        bool includeArtwork = true,
        CancellationToken ct = default) =>
        Task.Run(() => Load(path, includeArtwork, formats, ct), ct);

    private static MediaDocument Load(
        string path,
        bool includeArtwork,
        IMediaFormatRegistry formats,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("The media file does not exist.", fullPath);

        IMediaFile file = MediaFile.GetFile(fullPath, readOnly: true,
            readArtwork: includeArtwork, knownLength: info.Length, formatRegistry: formats);
        var layers = file.Tags.Select(ProjectLayer).ToImmutableArray();
        IMetadataProvider? artworkLayer = file.Tags.FirstOrDefault(layer =>
            layer.GetImageMetadata().Any());
        ImmutableArray<ArtworkModel> artwork = !includeArtwork || artworkLayer is null
            ? []
            : artworkLayer.GetImageMetadata().Select(image => new ArtworkModel
            {
                Description = image.Description,
                Category = image.Category,
                ImageType = image.ImageType,
                Width = image.Width,
                Height = image.Height,
                Size = image.Size,
                Hash = image.Hash,
                Data = image.Data,
            }).ToImmutableArray();
        ICodecProvider? codec = file.Codecs.FirstOrDefault();
        CodecModel? codecModel = codec is null ? null : new CodecModel
        {
            CodecName = codec.CodecName,
            CodecType = codec.CodecType,
            AverageBitrate = codec.AverageBitrate,
            MaxBitrate = codec.MaxBitrate,
            BitsPerSample = codec.BitsPerSample,
            Samplerate = codec.Samplerate,
            Channels = codec.Channels,
            DurationInSeconds = codec.DurationInSeconds,
        };
        string hash = HashMetadata(layers, artwork);
        return new(
            fullPath,
            layers,
            artwork,
            codecModel,
            new(fullPath, info.Length, info.LastWriteTimeUtc, hash),
            formats.SupportsPath(fullPath, MediaFormatCapabilities.WriteMetadata));
    }

    private static TagLayerDocument ProjectLayer(IMetadataProvider layer)
    {
        var fields = layer.GetKnownMetadata()
            .GroupBy(pair => MetadataFieldKey.Known(pair.Key))
            .Select(group => new MetadataValueSet(
                group.Key,
                group.Select(pair => pair.Value).ToImmutableArray()))
            .ToList();
        if (layer is IUserStringMetadata custom)
        {
            fields.AddRange(custom.GetUserStrings()
                .GroupBy(pair => MetadataFieldKey.Custom(pair.Key))
                .Select(group => new MetadataValueSet(
                    group.Key,
                    group.Select(pair => pair.Value).ToImmutableArray())));
        }
        return new(
            layer.TagType ?? "Unknown",
            fields.OrderBy(field => field.Field.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            layer is IUserStringMetadata,
            layer is IMetadataWriter || layer is VorbisComments,
            layer is IMultiValueMetadataWriter,
            layer is IMultiValueUserStringMetadata);
    }

    private static string HashMetadata(
        ImmutableArray<TagLayerDocument> layers,
        ImmutableArray<ArtworkModel> artwork)
    {
        var text = new StringBuilder();
        foreach (TagLayerDocument layer in layers)
        {
            text.Append("layer:").Append(layer.TagType).Append('\n');
            foreach (MetadataValueSet field in layer.Fields)
            {
                text.Append(field.Field.IsKnown ? "known:" : "custom:")
                    .Append(field.Field.DisplayName).Append('=');
                foreach (string value in field.Values)
                    text.Append(value.Length).Append(':').Append(value);
                text.Append('\n');
            }
        }
        foreach (ArtworkModel image in artwork)
            text.Append("art:").Append(image.Category).Append(':')
                .Append(image.Description).Append(':').Append(image.Hash).Append(':')
                .Append(image.Size).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))
            .ToLowerInvariant();
    }
}

/// <summary>Expands ad-hoc workbench sources without requiring a configured library.</summary>
public sealed class WorkbenchService(
    IMetadataDocumentService documents,
    IMediaFormatRegistry formats) : IWorkbenchService
{
    public async Task<WorkbenchLoadResult> LoadAsync(
        WorkbenchLoadRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var issues = new List<OperationIssue>();
        IReadOnlyList<string> paths = await Task.Run(
            () => Expand(request, formats, issues, ct), ct);
        var loaded = new List<MediaDocument>(paths.Count);
        foreach (string path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                loaded.Add(await documents.LoadAsync(path, includeArtwork: false, ct));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                issues.Add(new("workbench.read", OperationIssueSeverity.Warning,
                    error.Message, path));
            }
        }
        return new([.. loaded], [.. issues]);
    }

    private static IReadOnlyList<string> Expand(
        WorkbenchLoadRequest request,
        IMediaFormatRegistry formats,
        List<OperationIssue> issues,
        CancellationToken ct)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        foreach (string source in request.Sources.Where(source =>
                     !string.IsNullOrWhiteSpace(source)))
        {
            ct.ThrowIfCancellationRequested();
            string fullPath;
            try { fullPath = Path.GetFullPath(source); }
            catch (Exception error)
            {
                issues.Add(new("workbench.path", OperationIssueSeverity.Warning,
                    error.Message, source));
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                foreach (string file in EnumerateDirectory(fullPath, request.Recursive, issues, ct))
                    AddMedia(file, formats, result, seen);
            }
            else if (File.Exists(fullPath) && IsPlaylist(fullPath))
            {
                foreach (string file in ReadPlaylist(fullPath, issues, ct))
                    AddMedia(file, formats, result, seen);
            }
            else if (File.Exists(fullPath))
                AddMedia(fullPath, formats, result, seen);
            else
                issues.Add(new("workbench.missing", OperationIssueSeverity.Warning,
                    "The source does not exist.", fullPath));
        }
        return result;
    }

    private static IEnumerable<string> EnumerateDirectory(
        string root,
        bool recursive,
        List<OperationIssue> issues,
        CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception error)
            {
                issues.Add(new("workbench.enumerate", OperationIssueSeverity.Warning,
                    error.Message, directory));
                continue;
            }
            foreach (string entry in entries.OrderBy(path => path, PathComparer))
            {
                ct.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch { continue; }
                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    yield return entry;
                    continue;
                }
                if (recursive && !attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push(entry);
            }
        }
    }

    private static bool IsPlaylist(string path) =>
        Path.GetExtension(path).Equals(".m3u", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".m3u8", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ReadPlaylist(
        string playlist,
        List<OperationIssue> issues,
        CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(playlist)!;
        string extension = Path.GetExtension(playlist);
        string[] lines;
        try { lines = File.ReadAllLines(playlist); }
        catch (Exception error)
        {
            issues.Add(new("workbench.playlist", OperationIssueSeverity.Warning,
                error.Message, playlist));
            yield break;
        }
        foreach (string original in lines)
        {
            ct.ThrowIfCancellationRequested();
            string value = original.Trim();
            if (extension.Equals(".cue", StringComparison.OrdinalIgnoreCase))
            {
                Match match = Regex.Match(value,
                    "^FILE\\s+(?:\"(?<path>[^\"]+)\"|(?<path>\\S+))\\s+",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
                if (!match.Success)
                    continue;
                value = match.Groups["path"].Value;
            }
            else if (value.Length == 0 || value.StartsWith('#'))
                continue;

            string path;
            try { path = Path.GetFullPath(value, directory); }
            catch { continue; }
            if (File.Exists(path))
                yield return path;
        }
    }

    private static void AddMedia(
        string path,
        IMediaFormatRegistry formats,
        List<string> result,
        HashSet<string> seen)
    {
        string fullPath = Path.GetFullPath(path);
        if (formats.SupportsPath(fullPath, MediaFormatCapabilities.ReadMetadata) &&
            seen.Add(fullPath))
            result.Add(fullPath);
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

/// <summary>
/// Builds typed before/after plans and applies them through staged, recoverable replacements.
/// </summary>
public sealed class MetadataOperationService(
    IMetadataDocumentService documents,
    IMediaFormatRegistry formats,
    IFileMutationPlanExecutor mutations,
    IAppSettings settings,
    IReindexService? reindex = null,
    IEditHistoryService? history = null) : IMetadataOperationService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public async Task<MetadataOperationPlan> PreviewAsync(
        IReadOnlyList<string> paths,
        OperationRecipe recipe,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(recipe);
        var plans = new List<MetadataFilePlan>(paths.Count);
        for (int index = 0; index < paths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            MediaDocument document;
            try
            {
                document = await documents.LoadAsync(paths[index], false, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                plans.Add(Unavailable(paths[index], error));
                continue;
            }
            Dictionary<MetadataFieldKey, ImmutableArray<string>> before = Flatten(document);
            var after = new Dictionary<MetadataFieldKey, ImmutableArray<string>>(before);
            var operationIssues = new List<OperationIssue>();
            foreach (MetadataOperation operation in recipe.EnabledOperations)
            {
                try
                {
                    if (Matches(operation.Condition, document, after))
                        ApplyOperation(operation, document, after, index, paths.Count);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    operationIssues.Add(new(
                        "metadata.operation",
                        OperationIssueSeverity.Blocker,
                        error.Message,
                        document.Path));
                    break;
                }
            }
            plans.Add(BuildPlan(document, before, after, operationIssues));
        }
        AddRecoverySpaceIssues(plans);
        return new(Guid.NewGuid(), recipe.Name, [.. plans], DateTimeOffset.UtcNow, recipe);
    }

    public async Task<MetadataOperationPlan> PreviewEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagEdit>> editsByPath,
        string name,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(editsByPath);
        var plans = new List<MetadataFilePlan>(editsByPath.Count);
        foreach ((string path, IReadOnlyList<TagEdit> edits) in editsByPath)
        {
            ct.ThrowIfCancellationRequested();
            MediaDocument document;
            try
            {
                document = await documents.LoadAsync(path, false, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                plans.Add(Unavailable(path, error));
                continue;
            }
            Dictionary<MetadataFieldKey, ImmutableArray<string>> before = Flatten(document);
            var after = new Dictionary<MetadataFieldKey, ImmutableArray<string>>(before);
            foreach (TagEdit edit in edits)
            {
                MetadataFieldKey field = edit.IsUserString
                    ? MetadataFieldKey.Custom(edit.UserStringKey!)
                    : MetadataFieldKey.Known(edit.Field);
                if (edit.Value is null)
                    after.Remove(field);
                else
                    after[field] = [edit.Value];
            }
            plans.Add(BuildPlan(document, before, after));
        }
        AddRecoverySpaceIssues(plans);
        return new(Guid.NewGuid(), name, [.. plans], DateTimeOffset.UtcNow);
    }

    public async Task<MetadataOperationPlan> PreviewValueEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> editsByPath,
        string name,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(editsByPath);
        var plans = new List<MetadataFilePlan>(editsByPath.Count);
        foreach ((string path, IReadOnlyList<MetadataValueEdit> edits) in editsByPath)
        {
            ct.ThrowIfCancellationRequested();
            MediaDocument document;
            try
            {
                document = await documents.LoadAsync(path, false, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                plans.Add(Unavailable(path, error));
                continue;
            }
            Dictionary<MetadataFieldKey, ImmutableArray<string>> before = Flatten(document);
            var after = new Dictionary<MetadataFieldKey, ImmutableArray<string>>(before);
            foreach (MetadataValueEdit edit in edits)
            {
                if (edit.Values.Length == 0)
                    after.Remove(edit.Field);
                else
                    after[edit.Field] = edit.Values;
            }
            plans.Add(BuildPlan(document, before, after));
        }
        AddRecoverySpaceIssues(plans);
        return new(Guid.NewGuid(), name, [.. plans], DateTimeOffset.UtcNow);
    }

    private static MetadataFilePlan Unavailable(string path, Exception error)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { fullPath = path; }
        return new(
            fullPath,
            new(fullPath, 0, DateTime.MinValue, ""),
            [],
            [],
            [new(
                "metadata.unavailable",
                OperationIssueSeverity.Blocker,
                error.Message,
                fullPath)]);
    }

    public async Task<MetadataApplyResult> ApplyAsync(
        MetadataOperationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException("The metadata plan has no applicable changes or contains blockers.");

        MetadataFilePlan[] changed = plan.Files.Where(file => file.CanApply).ToArray();
        foreach (MetadataFilePlan filePlan in changed)
        {
            ct.ThrowIfCancellationRequested();
            MediaDocument current = await documents.LoadAsync(filePlan.Path, false, ct);
            if (current.Snapshot.Length != filePlan.Snapshot.Length ||
                current.Snapshot.LastWriteTimeUtc != filePlan.Snapshot.LastWriteTimeUtc ||
                !StringComparer.Ordinal.Equals(
                    current.Snapshot.MetadataHash, filePlan.Snapshot.MetadataHash))
                throw new InvalidOperationException(
                    $"Stale plan: metadata changed since preview: '{filePlan.Path}'.");
        }

        var journals = new List<string>();
        int completed = 0;
        foreach (IGrouping<string, MetadataFilePlan> volume in changed.GroupBy(
                     file => Path.GetPathRoot(file.Path) ?? "", PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            MetadataFilePlan[] group = volume.ToArray();
            string commonRoot = CommonDirectory(group.Select(file => file.Path));
            string container = commonRoot + ".MusicLibraryManager-recovery";
            string recoveryRoot = Path.Combine(container,
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N"));
            var staged = new List<string>();
            try
            {
                var actions = new List<FileMutationAction>(group.Length);
                foreach (MetadataFilePlan filePlan in group)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new(OperationPhase.Applying, completed, changed.Length,
                        filePlan.Path, "Staging metadata changes"));
                    string stage = Stage(filePlan, formats);
                    staged.Add(stage);
                    var stageInfo = new FileInfo(stage);
                    actions.Add(new(
                        FileMutationKind.Replace,
                        stage,
                        filePlan.Path,
                        Snapshot(stageInfo),
                        new(true, false, filePlan.Snapshot.Length,
                            filePlan.Snapshot.LastWriteTimeUtc)
                        {
                            Path = filePlan.Path,
                        }));
                    completed++;
                }

                LibraryConfiguration? configuration = settings.GetSnapshot().Configuration;
                var mutationPlan = new FileMutationPlan(
                    "MusicLibraryManager",
                    commonRoot,
                    recoveryRoot,
                    actions,
                    [],
                    DateTimeOffset.UtcNow,
                    RetainRecovery: true,
                    PolicyFingerprint: configuration?.PolicySnapshot.Fingerprint,
                    LibraryId: configuration?.LibraryId);
                FileMutationSummary result = await mutations.ApplyAsync(mutationPlan, progress, ct);
                if (result.JournalPath is not null)
                    journals.Add(result.JournalPath);
            }
            finally
            {
                foreach (string stage in staged)
                    try { if (File.Exists(stage)) File.Delete(stage); } catch { }
            }
        }

        if (reindex is not null)
        {
            foreach (MetadataFilePlan changedFile in changed)
            {
                try { await reindex.ReindexFileAsync(changedFile.Path, CancellationToken.None); }
                catch { /* The files are authoritative; the next index pass repairs the cache. */ }
            }
        }

        if (history is not null)
            history.Record(new(
                plan.Id,
                plan.Name,
                DateTimeOffset.UtcNow,
                [.. journals],
                [.. changed.Select(file => file.Path)],
                plan.Recipe));
        return new(changed.Length, [.. journals],
            [.. changed.SelectMany(file => file.Issues)]);
    }

    private MetadataFilePlan BuildPlan(
        MediaDocument document,
        Dictionary<MetadataFieldKey, ImmutableArray<string>> before,
        Dictionary<MetadataFieldKey, ImmutableArray<string>> after,
        IEnumerable<OperationIssue>? operationIssues = null)
    {
        var issues = operationIssues?.ToList() ?? [];
        if (!document.IsWritable)
            issues.Add(new("metadata.read-only", OperationIssueSeverity.Blocker,
                "This media format is not writable.", document.Path));
        ValidatePolicy(document.Path, issues);
        var differences = before.Keys.Union(after.Keys)
            .Select(field => new MetadataFieldDifference(
                field,
                before.GetValueOrDefault(field, []),
                after.GetValueOrDefault(field, [])))
            .Where(difference => !difference.Before.SequenceEqual(difference.After))
            .OrderBy(difference => difference.Field.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        foreach (MetadataFieldDifference difference in differences
                     .Where(difference => difference.After.Length > 1))
        {
            bool supported = difference.Field.IsKnown
                ? document.TagLayers.Any(layer => layer.SupportsMultipleValues)
                : document.TagLayers.Any(layer => layer.SupportsCustomMultipleValues);
            if (!supported)
                issues.Add(new(
                    "metadata.multivalue-unsupported",
                    OperationIssueSeverity.Blocker,
                    $"The native tag writer cannot store multiple values for " +
                    $"'{difference.Field.DisplayName}' without loss.",
                    document.Path));
        }
        ImmutableArray<MetadataValueEdit> edits = differences
            .Select(difference => new MetadataValueEdit(
                difference.Field, difference.After))
            .ToImmutableArray();
        return new(document.Path, document.Snapshot, differences, edits, [.. issues]);
    }

    private void ValidatePolicy(string path, List<OperationIssue> issues)
    {
        LibraryConfiguration? configuration = settings.GetSnapshot().Configuration;
        if (configuration is null)
            return;
        LibraryIndexLocation[] roots = configuration.IndexLocations.ToArray();
        bool insideConfiguredRoot =
            LibraryRootPermissionPolicy.MostSpecific(path, roots) is not null;
        if (insideConfiguredRoot && !LibraryRootPermissionPolicy.Allows(
                path, roots, LibraryRootPermissions.WriteMetadata))
            issues.Add(new("metadata.permission", OperationIssueSeverity.Blocker,
                "The active library policy does not permit metadata writes.", path));
    }

    private static Dictionary<MetadataFieldKey, ImmutableArray<string>> Flatten(
        MediaDocument document) => document.TagLayers
        .SelectMany(layer => layer.Fields)
        .GroupBy(field => field.Field)
        .ToDictionary(
            group => group.Key,
            group => group.SelectMany(field => field.Values).ToImmutableArray());

    private static bool Matches(
        MetadataCondition? condition,
        MediaDocument document,
        Dictionary<MetadataFieldKey, ImmutableArray<string>> fields)
    {
        if (condition is null || condition.Operator == MetadataConditionOperator.Always)
            return true;
        ImmutableArray<string> values = condition.Field is null
            ? []
            : fields.GetValueOrDefault(condition.Field, []);
        string expected = condition.Value ?? "";
        bool result = condition.Operator switch
        {
            MetadataConditionOperator.Present => values.Length > 0,
            MetadataConditionOperator.Missing => values.Length == 0,
            MetadataConditionOperator.Equals => values.Any(value =>
                value.Equals(expected, StringComparison.OrdinalIgnoreCase)),
            MetadataConditionOperator.Contains => values.Any(value =>
                value.Contains(expected, StringComparison.OrdinalIgnoreCase)),
            MetadataConditionOperator.MatchesRegularExpression => values.Any(value =>
                Regex.IsMatch(value, expected,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout)),
            _ => true,
        };
        return condition.Negate ? !result : result;
    }

    private static void ApplyOperation(
        MetadataOperation operation,
        MediaDocument document,
        Dictionary<MetadataFieldKey, ImmutableArray<string>> fields,
        int index,
        int count)
    {
        switch (operation)
        {
            case AssignFieldOperation assign:
                fields[assign.Field] = [assign.Value];
                break;
            case RemoveFieldOperation remove:
                fields.Remove(remove.Field);
                break;
            case CopyFieldOperation copy:
                if (!copy.PreserveExisting || !fields.ContainsKey(copy.Destination))
                    fields[copy.Destination] = fields.GetValueOrDefault(copy.Source, []);
                break;
            case ReplaceTextOperation replace:
                if (fields.TryGetValue(replace.Field, out ImmutableArray<string> replaceValues))
                    fields[replace.Field] = replaceValues.Select(value => replace.RegularExpression
                            ? Regex.Replace(value, replace.Search, replace.Replacement,
                                replace.IgnoreCase
                                    ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                                    : RegexOptions.CultureInvariant,
                                RegexTimeout)
                            : value.Replace(replace.Search, replace.Replacement,
                                replace.IgnoreCase
                                    ? StringComparison.OrdinalIgnoreCase
                                    : StringComparison.Ordinal))
                        .ToImmutableArray();
                break;
            case ChangeCaseOperation change:
                if (fields.TryGetValue(change.Field, out ImmutableArray<string> caseValues))
                    fields[change.Field] = caseValues.Select(value =>
                            ChangeCase(value, change.Mode))
                        .ToImmutableArray();
                break;
            case TrimFieldOperation trim:
                if (fields.TryGetValue(trim.Field, out ImmutableArray<string> trimValues))
                    fields[trim.Field] = trimValues.Select(value =>
                            trim.NormalizeInternalWhitespace
                                ? Regex.Replace(value.Trim(), "\\s+", " ",
                                    RegexOptions.CultureInvariant, RegexTimeout)
                                : value.Trim())
                        .ToImmutableArray();
                break;
            case SequenceNumberOperation sequence:
                int number = checked(sequence.Start + index * sequence.Step);
                fields[sequence.Field] =
                    [sequence.PadWidth > 0
                        ? number.ToString($"D{sequence.PadWidth}", CultureInfo.InvariantCulture)
                        : number.ToString(CultureInfo.InvariantCulture)];
                if (sequence.TotalField is not null)
                    fields[sequence.TotalField] = [count.ToString(CultureInfo.InvariantCulture)];
                break;
            case CombineFieldsOperation combine:
                ImmutableArray<string> combinedValues =
                    fields.GetValueOrDefault(combine.First, [])
                        .Concat(fields.GetValueOrDefault(combine.Second, []))
                        .Where(value => !string.IsNullOrEmpty(value))
                        .ToImmutableArray();
                if (combinedValues.Length == 0)
                    fields.Remove(combine.Destination);
                else
                    fields[combine.Destination] =
                        [string.Join(combine.Separator, combinedValues)];
                break;
            case SplitFieldOperation split:
                if (fields.TryGetValue(split.Field, out ImmutableArray<string> splitValues))
                {
                    IEnumerable<string> values = splitValues.SelectMany(value =>
                        split.RegularExpression
                            ? Regex.Split(value, split.Separator,
                                RegexOptions.CultureInvariant, RegexTimeout)
                            : value.Split(split.Separator, StringSplitOptions.None));
                    if (split.RemoveEmptyValues)
                        values = values.Where(value => value.Length > 0);
                    ImmutableArray<string> result = values.ToImmutableArray();
                    if (result.Length == 0)
                        fields.Remove(split.Field);
                    else
                        fields[split.Field] = result;
                }
                break;
            case JoinFieldValuesOperation join:
                if (fields.TryGetValue(join.Field, out ImmutableArray<string> joinValues))
                {
                    if (joinValues.Length == 0)
                        fields.Remove(join.Field);
                    else
                        fields[join.Field] = [string.Join(join.Separator, joinValues)];
                }
                break;
            case DeduplicateFieldValuesOperation deduplicate:
                if (fields.TryGetValue(
                        deduplicate.Field, out ImmutableArray<string> duplicateValues))
                    fields[deduplicate.Field] = duplicateValues
                        .Distinct(deduplicate.IgnoreCase
                            ? StringComparer.CurrentCultureIgnoreCase
                            : StringComparer.CurrentCulture)
                        .ToImmutableArray();
                break;
            case ReorderFieldValuesOperation reorder:
                if (fields.TryGetValue(reorder.Field, out ImmutableArray<string> reorderValues))
                {
                    StringComparer comparer = reorder.IgnoreCase
                        ? StringComparer.CurrentCultureIgnoreCase
                        : StringComparer.CurrentCulture;
                    fields[reorder.Field] = reorder.Order switch
                    {
                        MetadataValueOrder.Ascending =>
                            reorderValues.OrderBy(value => value, comparer).ToImmutableArray(),
                        MetadataValueOrder.Descending =>
                            reorderValues.OrderByDescending(value => value, comparer)
                                .ToImmutableArray(),
                        MetadataValueOrder.Reverse => reorderValues.Reverse().ToImmutableArray(),
                        _ => reorderValues,
                    };
                }
                break;
            case ExtractPathComponentOperation extract:
                string? pathValue = ExtractPathComponent(document.Path, extract);
                if (pathValue is not null)
                    fields[extract.Field] = [pathValue];
                break;
        }
    }

    private static string? ExtractPathComponent(
        string path,
        ExtractPathComponentOperation operation)
    {
        string component = operation.Component switch
        {
            MetadataPathComponent.FileNameWithoutExtension =>
                Path.GetFileNameWithoutExtension(path),
            MetadataPathComponent.FileName => Path.GetFileName(path),
            MetadataPathComponent.ParentFolder =>
                GetParentFolderName(path, operation.ParentLevel),
            MetadataPathComponent.FullPath => path,
            _ => "",
        };
        if (operation.Pattern is null)
            return component;

        var expression = new Regex(
            operation.Pattern,
            RegexOptions.CultureInvariant,
            RegexTimeout);
        if (!expression.GetGroupNames().Contains(
                operation.CaptureGroup, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"The extraction pattern has no '{operation.CaptureGroup}' capture group.");
        Match match = expression.Match(component);
        return match.Success ? match.Groups[operation.CaptureGroup].Value : null;
    }

    private static string GetParentFolderName(string path, int level)
    {
        string? directory = Path.GetDirectoryName(path);
        for (int current = 1; current < level && directory is not null; current++)
            directory = Path.GetDirectoryName(directory);
        return directory is null
            ? ""
            : Path.GetFileName(directory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string ChangeCase(string value, MetadataCaseMode mode) => mode switch
    {
        MetadataCaseMode.Upper => value.ToUpper(CultureInfo.CurrentCulture),
        MetadataCaseMode.Lower => value.ToLower(CultureInfo.CurrentCulture),
        MetadataCaseMode.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
            value.ToLower(CultureInfo.CurrentCulture)),
        MetadataCaseMode.Sentence => value.Length == 0
            ? value
            : char.ToUpper(value[0], CultureInfo.CurrentCulture) +
              value[1..].ToLower(CultureInfo.CurrentCulture),
        _ => value,
    };

    private static string Stage(
        MetadataFilePlan plan,
        IMediaFormatRegistry formats)
    {
        string stage = Path.Combine(
            Path.GetDirectoryName(plan.Path)!,
            $".{Path.GetFileName(plan.Path)}.{Guid.NewGuid():N}.workbench-stage");
        try
        {
            IMediaFile file = MediaFile.GetFile(plan.Path, readOnly: false,
                readArtwork: true, formatRegistry: formats);
            IMetadataWriter? writer = file as IMetadataWriter ??
                file.Tags.OfType<IMetadataWriter>().FirstOrDefault();
            VorbisComments? vorbis = file as VorbisComments ??
                file.Tags.OfType<VorbisComments>().FirstOrDefault();
            IUserStringMetadata? custom = file as IUserStringMetadata ??
                file.Tags.OfType<IUserStringMetadata>().FirstOrDefault();
            if (writer is null && vorbis is null)
                throw new InvalidOperationException("The file has no writable metadata layer.");

            foreach (MetadataValueEdit edit in plan.Edits)
            {
                if (!edit.Field.IsKnown)
                {
                    string key = edit.Field.CustomName!;
                    if (custom is null)
                        throw new InvalidOperationException(
                            $"The tag format does not support custom field '{key}'.");
                    if (edit.Values.Length == 0)
                        custom.RemoveUserString(key);
                    else if (edit.Values.Length == 1)
                        custom.SetUserString(key, edit.Values[0]);
                    else if (custom is IMultiValueUserStringMetadata multiCustom)
                        multiCustom.SetUserStringValues(key, edit.Values);
                    else
                        throw new InvalidOperationException(
                            $"The tag format cannot store multiple values for custom field '{key}'.");
                    continue;
                }
                TagFields field = edit.Field.KnownField!.Value;
                if (edit.Values.Length == 0)
                {
                    if (writer is not null) writer.RemoveField(field);
                    else vorbis!.RemoveField(field);
                }
                else if (edit.Values.Length == 1)
                {
                    if (writer is not null) writer.SetField(field, edit.Values[0]);
                    else vorbis!.SetField(field, edit.Values[0]);
                }
                else if (writer is IMultiValueMetadataWriter multiWriter)
                    multiWriter.SetFieldValues(field, edit.Values);
                else if (vorbis is IMultiValueMetadataWriter multiVorbis)
                    multiVorbis.SetFieldValues(field, edit.Values);
                else
                    throw new InvalidOperationException(
                        $"The tag format cannot store multiple values for field '{field}'.");
            }
            file.SaveTags(stage);
            return stage;
        }
        catch
        {
            try { if (File.Exists(stage)) File.Delete(stage); } catch { }
            throw;
        }
    }

    private static OperationPathSnapshot Snapshot(FileInfo info) =>
        new(true, false, info.Length, info.LastWriteTimeUtc)
        {
            Path = info.FullName,
        };

    private static string CommonDirectory(IEnumerable<string> paths)
    {
        string[] directories = paths.Select(path =>
                Path.GetDirectoryName(Path.GetFullPath(path))!)
            .ToArray();
        string common = directories[0];
        while (directories.Any(directory => !IsWithin(directory, common)))
        {
            common = Path.GetDirectoryName(common)
                ?? throw new InvalidOperationException("Files do not share a usable source root.");
        }
        return Path.TrimEndingDirectorySeparator(common);
    }

    private static bool IsWithin(string path, string root)
    {
        string prefix = Path.TrimEndingDirectorySeparator(root) +
            Path.DirectorySeparatorChar;
        return PathComparer.Equals(path, root) ||
            path.StartsWith(prefix, PathComparison);
    }

    private static void AddRecoverySpaceIssues(List<MetadataFilePlan> plans)
    {
        foreach (IGrouping<string, MetadataFilePlan> volume in plans
                     .Where(plan => plan.HasChanges)
                     .GroupBy(plan => Path.GetPathRoot(plan.Path) ?? "", PathComparer))
        {
            try
            {
                string root = volume.Key;
                if (string.IsNullOrWhiteSpace(root))
                    continue;
                long required = checked(volume.Sum(plan => plan.Snapshot.Length) * 2);
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < required)
                {
                    foreach (MetadataFilePlan plan in volume)
                    {
                        var issue = new OperationIssue(
                            "metadata.recovery-space",
                            OperationIssueSeverity.Blocker,
                            $"At least {required:N0} free bytes are required to stage and retain recovery copies.",
                            plan.Path);
                        int index = plans.IndexOf(plan);
                        plans[index] = plan with { Issues = plan.Issues.Add(issue) };
                    }
                }
            }
            catch
            {
                // Remote and virtual filesystems often cannot report free space. Apply still
                // surfaces the concrete I/O error without treating an unknown estimate as denial.
            }
        }
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

public sealed class EditHistoryService(
    IAppSettings settings,
    IOperationJournalService journals) : IEditHistoryService
{
    private const string Preference = "manager.workbench.history.v1";
    private readonly HistoryState _state = Load(settings);

    public IReadOnlyList<EditHistoryEntry> Entries => _state.Undo;
    public IReadOnlyList<EditHistoryEntry> RedoEntries => _state.Redo;
    public bool CanUndo => _state.Undo.Count > 0;
    public bool CanRedo => _state.Redo.Any(entry => entry.Recipe is not null);

    public void Record(EditHistoryEntry entry)
    {
        _state.Undo.Insert(0, entry);
        if (_state.Undo.Count > 100)
            _state.Undo.RemoveRange(100, _state.Undo.Count - 100);
        _state.Redo.Clear();
        Persist();
    }

    public async Task<int> UndoLatestAsync(
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (_state.Undo.Count == 0)
            return 0;
        EditHistoryEntry entry = _state.Undo[0];
        int restored = 0;
        foreach (string journalPath in entry.JournalPaths.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            string runPath = Path.GetDirectoryName(journalPath)!;
            var summary = new OperationJournalSummary(
                "MusicLibraryManager",
                OperationJournalKind.Other,
                OperationJournalState.Completed,
                runPath,
                journalPath,
                entry.AppliedAtUtc,
                entry.Paths.Length);
            OperationBrowseResult browse = await journals.BrowseAsync(summary, ct);
            OperationFileEntry[] candidates = browse.Entries
                .Where(item => item.Kind == OperationEntryKind.Quarantined &&
                    item.Exists && item.CurrentPath is not null)
                .ToArray();
            OperationRestorePlan plan = await journals.PreviewRestoreAsync(
                summary, candidates, ct);
            OperationRestoreResult result = await journals.ApplyRestoreAsync(
                plan, progress, ct);
            restored += result.RestoredCount;
        }
        _state.Undo.RemoveAt(0);
        _state.Redo.Insert(0, entry);
        if (_state.Redo.Count > 100)
            _state.Redo.RemoveRange(100, _state.Redo.Count - 100);
        Persist();
        return restored;
    }

    private static HistoryState Load(IAppSettings settings)
    {
        try
        {
            string? json = settings.GetPreference(Preference);
            if (string.IsNullOrWhiteSpace(json))
                return new([], []);
            HistoryState? state = JsonSerializer.Deserialize<HistoryState>(json);
            if (state is not null)
                return state;
            List<EditHistoryEntry>? legacy =
                JsonSerializer.Deserialize<List<EditHistoryEntry>>(json);
            return new(legacy ?? [], []);
        }
        catch
        {
            try
            {
                string? json = settings.GetPreference(Preference);
                List<EditHistoryEntry>? legacy =
                    JsonSerializer.Deserialize<List<EditHistoryEntry>>(json ?? "");
                return new(legacy ?? [], []);
            }
            catch { return new([], []); }
        }
    }

    private void Persist()
    {
        try { settings.SetPreference(Preference, JsonSerializer.Serialize(_state)); }
        catch { /* History persistence is best effort; recovery remains discoverable. */ }
    }

    private sealed record HistoryState(
        List<EditHistoryEntry> Undo,
        List<EditHistoryEntry> Redo);
}
