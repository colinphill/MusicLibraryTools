using System.Collections.Immutable;
using System.Globalization;
using System.IO.Enumeration;
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

public interface IRecoverySpaceProbe
{
    long? GetAvailableFreeSpace(string root);
}

public sealed class SystemRecoverySpaceProbe : IRecoverySpaceProbe
{
    public static SystemRecoverySpaceProbe Instance { get; } = new();

    private SystemRecoverySpaceProbe()
    {
    }

    public long? GetAvailableFreeSpace(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady
                ? drive.AvailableFreeSpace
                : null;
        }
        catch
        {
            // Remote and virtual filesystems may not expose capacity.
            return null;
        }
    }
}

public interface IWorkbenchService
{
    Task<WorkbenchLoadResult> LoadAsync(
        WorkbenchLoadRequest request,
        CancellationToken ct = default);

    Task<WorkbenchLoadResult> LoadAsync(
        WorkbenchLoadRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default) =>
        LoadAsync(request, ct);
}

public interface IMetadataOperationService
{
    Task<MetadataOperationPlan> PreviewAsync(
        IReadOnlyList<string> paths,
        OperationRecipe recipe,
        CancellationToken ct = default);

    Task<MetadataOperationPlan> PreviewAsync(
        IReadOnlyList<string> paths,
        OperationRecipe recipe,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default) =>
        PreviewAsync(paths, recipe, ct);

    Task<MetadataOperationPlan> PreviewEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagEdit>> editsByPath,
        string name,
        CancellationToken ct = default);

    Task<MetadataOperationPlan> PreviewEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagEdit>> editsByPath,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default) =>
        PreviewEditsAsync(editsByPath, name, ct);

    Task<MetadataOperationPlan> PreviewValueEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> editsByPath,
        string name,
        CancellationToken ct = default);

    Task<MetadataOperationPlan> PreviewValueEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> editsByPath,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default) =>
        PreviewValueEditsAsync(editsByPath, name, ct);

    Task<MetadataOperationPlan> PreviewValueEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> editsByPath,
        IReadOnlyDictionary<string, MetadataEditSourceExpectation>
            sourceExpectations,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default) =>
        Task.FromException<MetadataOperationPlan>(
            new NotSupportedException(
                "This metadata operation service cannot validate source expectations."));

    Task<MetadataOperationPlan> PreviewArtworkEditsAsync(
        IReadOnlyDictionary<string, ArtworkValueEdit> editsByPath,
        string name,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        Task.FromException<MetadataOperationPlan>(
            new NotSupportedException(
                "Artwork-aware metadata preview is not implemented."));

    Task<MetadataOperationPlan> PreviewArtworkSetsAsync(
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest> requestsByPath,
        string name,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        Task.FromException<MetadataOperationPlan>(
            new NotSupportedException(
                "Multi-artwork metadata preview is not implemented."));

    Task<MetadataOperationPlan> PreviewArtworkSetsAsync(
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest> requestsByPath,
        IReadOnlyDictionary<
            string,
            MetadataEditSourceExpectation>
            sourceExpectations,
        string name,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        Task.FromException<MetadataOperationPlan>(
            new NotSupportedException(
                "This metadata operation service cannot validate artwork source expectations."));

    Task<MetadataOperationPlan> PreviewTagLayerEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagLayerEdit>> editsByPath,
        string name,
        CancellationToken ct = default);

    Task<MetadataOperationPlan> PreviewTagLayerEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagLayerEdit>> editsByPath,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default) =>
        PreviewTagLayerEditsAsync(editsByPath, name, ct);

    Task<MetadataOperationPlan> PreviewTagLayerConversionsAsync(
        IReadOnlyDictionary<string, TagLayerConversionEdit> editsByPath,
        string name,
        CancellationToken ct = default);

    Task<MetadataOperationPlan> PreviewTagLayerConversionsAsync(
        IReadOnlyDictionary<string, TagLayerConversionEdit> editsByPath,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default) =>
        PreviewTagLayerConversionsAsync(editsByPath, name, ct);

    Task<MetadataOperationPlan> PreviewId3VersionEditsAsync(
        IReadOnlyDictionary<string, Id3VersionEdit> editsByPath,
        string name,
        CancellationToken ct = default);

    Task<MetadataOperationPlan> PreviewId3VersionEditsAsync(
        IReadOnlyDictionary<string, Id3VersionEdit> editsByPath,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default) =>
        PreviewId3VersionEditsAsync(editsByPath, name, ct);

    Task<MetadataApplyResult> ApplyAsync(
        MetadataOperationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<MetadataOperationStageResult> StageAsync(
        MetadataOperationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        Task.FromException<MetadataOperationStageResult>(
            new NotSupportedException(
                "Reviewed metadata staging is not implemented."));

    Task CompleteStagedApplyAsync(
        MetadataOperationStageResult stage,
        IReadOnlyList<string> journalPaths,
        bool recordHistory,
        CancellationToken ct = default) =>
        Task.CompletedTask;

    Task DiscardStageAsync(
        MetadataOperationStageResult stage,
        CancellationToken ct = default) =>
        Task.CompletedTask;
}

public interface IEditHistoryService
{
    IReadOnlyList<EditHistoryEntry> Entries { get; }
    IReadOnlyList<EditHistoryEntry> RedoEntries { get; }
    IReadOnlyList<OperationIssue> LastUndoIssues => [];
    Task<IReadOnlyList<OperationIssue>> LastUndoReconciliation =>
        Task.FromResult<IReadOnlyList<OperationIssue>>([]);
    bool ReconcilesInternalCatalogOnUndo => false;
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Record(EditHistoryEntry entry);
    Task<int> UndoLatestAsync(
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}

/// <summary>Reads complete tag layers directly from disk for the workbench.</summary>
public sealed class MetadataDocumentService(
    IMediaFormatRegistry formats,
    IMetadataFieldMappingService? fieldMappings = null) : IMetadataDocumentService
{
    public Task<MediaDocument> LoadAsync(
        string path,
        bool includeArtwork = true,
        CancellationToken ct = default) =>
        Task.Run(
            () => Load(path, includeArtwork, formats, fieldMappings, ct),
            ct);

    private static MediaDocument Load(
        string path,
        bool includeArtwork,
        IMediaFormatRegistry formats,
        IMetadataFieldMappingService? fieldMappings,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("The media file does not exist.", fullPath);

        IMediaFile file = MediaFile.GetFile(fullPath, readOnly: true,
            readArtwork: includeArtwork, knownLength: info.Length, formatRegistry: formats);
        IReadOnlyList<MetadataFieldMapping> mappings =
            fieldMappings?.GetForPath(fullPath) ?? [];
        var layers = file.Tags
            .Select(layer => ProjectLayer(layer, mappings))
            .ToImmutableArray();
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
        ImmutableArray<MediaChapter> chapters =
            file is IChapterMetadata chapterMetadata
                ? chapterMetadata.Chapters.ToImmutableArray()
                : [];
        ImmutableArray<TagLayerDescriptor> editableTagLayers =
            file is ITagLayerEditor layerEditor
                ? layerEditor.EditableTagLayers.ToImmutableArray()
                : [];
        bool hasExplicitlyAbsentId3 = editableTagLayers.Any(layer =>
            layer.Kind == TagLayerKind.Id3v2 && !layer.IsPresent);
        ID3v2Version? id3Version = hasExplicitlyAbsentId3
            ? null
            : file.Tags
                .OfType<ID3v2Tag>()
                .Select(tag => (ID3v2Version?)tag.Version)
                .FirstOrDefault();
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
        string hash = HashMetadata(
            layers, artwork, chapters, editableTagLayers);
        return new(
            fullPath,
            layers,
            artwork,
            codecModel,
            new(fullPath, info.Length, info.LastWriteTimeUtc, hash),
            formats.SupportsPath(fullPath, MediaFormatCapabilities.WriteMetadata))
        {
            Chapters = chapters,
            EditableTagLayers = editableTagLayers,
            Id3Version = id3Version,
        };
    }

    private static TagLayerDocument ProjectLayer(
        IMetadataProvider layer,
        IReadOnlyList<MetadataFieldMapping> mappings)
    {
        var fields = layer.GetKnownMetadata()
            .GroupBy(pair => MetadataFieldKey.Known(pair.Key))
            .Select(group => new MetadataValueSet(
                group.Key,
                group.Select(pair => pair.Value).ToImmutableArray()))
            .ToList();
        if (layer is IUserStringMetadata custom)
        {
            KeyValuePair<string, string>[] customValues =
                custom.GetAddressableUserStrings().ToArray();
            fields.AddRange(customValues
                .GroupBy(pair => MetadataFieldKey.Custom(pair.Key))
                .Select(group => new MetadataValueSet(
                    group.Key,
                    group.Select(pair => pair.Value).ToImmutableArray())));
            foreach (MetadataFieldMapping mapping in mappings)
            {
                ImmutableArray<string> values = customValues
                    .Where(pair => string.Equals(
                        pair.Key,
                        mapping.NativeFieldName,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Value)
                    .ToImmutableArray();
                if (values.IsDefaultOrEmpty)
                    continue;
                fields.RemoveAll(field =>
                    field.Field.KnownField == mapping.Field ||
                    string.Equals(
                        field.Field.CustomName,
                        mapping.NativeFieldName,
                        StringComparison.OrdinalIgnoreCase));
                fields.Add(new(
                    MetadataFieldKey.Known(mapping.Field),
                    values));
            }
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
        ImmutableArray<ArtworkModel> artwork,
        ImmutableArray<MediaChapter> chapters,
        ImmutableArray<TagLayerDescriptor> editableTagLayers)
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
        foreach (MediaChapter chapter in chapters)
            text.Append("chapter:")
                .Append(chapter.StartNanoseconds).Append(':')
                .Append(chapter.EndNanoseconds).Append(':')
                .Append(chapter.Language).Append(':')
                .Append(chapter.Title).Append(':')
                .Append(chapter.Uid).Append('\n');
        foreach (TagLayerDescriptor layer in editableTagLayers)
            text.Append("editable-layer:")
                .Append(layer.Kind).Append(':')
                .Append(layer.IsPresent).Append(':')
                .Append(layer.IsPrimary).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))
            .ToLowerInvariant();
    }

    /// <summary>
    /// Returns the canonical metadata fingerprint used by interactive source expectations.
    /// Embedded artwork is intentionally excluded so the value is stable for both lightweight
    /// and artwork-inclusive document loads.
    /// </summary>
    public static string CreateMetadataFingerprint(
        MediaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return
        HashMetadata(
            document.TagLayers,
            [],
            document.Chapters,
            document.EditableTagLayers);
    }

    /// <summary>
    /// Returns the canonical, order-independent fingerprint used by artwork source expectations.
    /// It intentionally matches the library cache's sorted SHA-256 image-signature format.
    /// </summary>
    public static string CreateArtworkFingerprint(
        MediaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return CreateArtworkFingerprint(
            document.Artwork);
    }

    public static string CreateArtworkFingerprint(
        IReadOnlyList<ArtworkModel> artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        return string.Join(
            "|",
            artwork
                .Select(image =>
                    Convert.ToBase64String(
                        SHA256.HashData(
                            image.Data)))
                .OrderBy(
                    hash => hash,
                    StringComparer.Ordinal));
    }
}

/// <summary>Expands ad-hoc workbench sources without requiring a configured library.</summary>
public sealed class WorkbenchService(
    IMetadataDocumentService documents,
    IMediaFormatRegistry formats) : IWorkbenchService
{
    public async Task<WorkbenchLoadResult> LoadAsync(
        WorkbenchLoadRequest request,
        CancellationToken ct = default) =>
        await LoadAsync(request, progress: null, ct);

    public async Task<WorkbenchLoadResult> LoadAsync(
        WorkbenchLoadRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        progress?.Report(new(
            OperationPhase.Planning,
            Message: "Scanning Workbench sources"));
        var issues = new List<OperationIssue>();
        IReadOnlyList<string> paths = await Task.Run(
            () => Expand(request, formats, issues, progress, ct), ct);
        var loaded = new List<MediaDocument>(paths.Count);
        for (int index = 0; index < paths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            string path = paths[index];
            progress?.Report(new(
                OperationPhase.IndexingSources,
                index,
                paths.Count,
                path,
                $"Reading metadata {index + 1:N0} of {paths.Count:N0}"));
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
        progress?.Report(new(
            OperationPhase.Completed,
            paths.Count,
            paths.Count,
            Message: $"Loaded {loaded.Count:N0} Workbench file(s)"));
        return new([.. loaded], [.. issues]);
    }

    private static IReadOnlyList<string> Expand(
        WorkbenchLoadRequest request,
        IMediaFormatRegistry formats,
        List<OperationIssue> issues,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        int scanned = 0;
        void ReportScanned(string path)
        {
            scanned++;
            if ((scanned & 255) == 0)
                progress?.Report(new(
                    OperationPhase.Planning,
                    scanned,
                    CurrentPath: path,
                    Message: $"Scanned {scanned:N0} Workbench source entries"));
        }

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

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(fullPath);
            }
            catch (Exception error) when (
                error is FileNotFoundException or DirectoryNotFoundException)
            {
                issues.Add(new("workbench.missing", OperationIssueSeverity.Warning,
                    "The source does not exist.", fullPath));
                continue;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                issues.Add(new("workbench.source", OperationIssueSeverity.Warning,
                    error.Message, fullPath));
                continue;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                foreach (string file in EnumerateDirectory(
                             fullPath,
                             request.Recursive,
                             issues,
                             ReportScanned,
                             ct))
                    AddMedia(file, formats, result, seen);
            }
            else if (IsPlaylist(fullPath))
            {
                foreach (string file in ReadPlaylist(fullPath, issues, ct))
                    AddMedia(file, formats, result, seen);
            }
            else
                AddMedia(fullPath, formats, result, seen);
        }
        progress?.Report(new(
            OperationPhase.Planning,
            scanned,
            scanned,
            Message: $"Scanned {scanned:N0} Workbench source entries"));
        return result;
    }

    private static IEnumerable<string> EnumerateDirectory(
        string root,
        bool recursive,
        List<OperationIssue> issues,
        Action<string> reportScanned,
        CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            IReadOnlyList<WorkbenchFileSystemEntry> entries;
            try
            {
                entries = ReadDirectoryEntries(
                    directory,
                    reportScanned,
                    ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                issues.Add(new("workbench.enumerate", OperationIssueSeverity.Warning,
                    error.Message, directory));
                continue;
            }
            foreach (WorkbenchFileSystemEntry entry in entries.OrderBy(
                         item => item.Path,
                         PathComparer))
            {
                ct.ThrowIfCancellationRequested();
                if (!entry.IsDirectory)
                {
                    yield return entry.Path;
                    continue;
                }
                if (recursive && !entry.IsReparsePoint)
                    pending.Push(entry.Path);
            }
        }
    }

    private static IReadOnlyList<WorkbenchFileSystemEntry> ReadDirectoryEntries(
        string directory,
        Action<string> reportScanned,
        CancellationToken ct)
    {
        var enumerable = new FileSystemEnumerable<WorkbenchFileSystemEntry>(
            directory,
            (ref FileSystemEntry entry) =>
            {
                ct.ThrowIfCancellationRequested();
                string path = entry.ToFullPath();
                reportScanned(path);
                return new(
                    path,
                    entry.IsDirectory,
                    entry.Attributes.HasFlag(FileAttributes.ReparsePoint));
            },
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                BufferSize = 64 * 1024,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
            })
        {
            ShouldIncludePredicate = static (ref FileSystemEntry _) => true,
        };
        return enumerable.ToList();
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
            bool available = false;
            try
            {
                available =
                    !File.GetAttributes(path).HasFlag(FileAttributes.Directory);
            }
            catch (Exception error) when (
                error is FileNotFoundException or DirectoryNotFoundException)
            {
                issues.Add(new(
                    "workbench.playlist-missing",
                    OperationIssueSeverity.Warning,
                    "The playlist entry does not exist.",
                    path));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                issues.Add(new(
                    "workbench.playlist-unavailable",
                    OperationIssueSeverity.Warning,
                    error.Message,
                    path));
            }
            if (available)
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

    private readonly record struct WorkbenchFileSystemEntry(
        string Path,
        bool IsDirectory,
        bool IsReparsePoint);
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
    IEditHistoryService? history = null,
    IMetadataFieldMappingService? fieldMappings = null,
    IRecoverySpaceProbe? recoverySpace = null,
    IReviewedChangeBatchService? reviewedChanges = null,
    IFileSystemVolumeIdentityProvider? volumeIdentities = null) :
    IMetadataOperationService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly IFileSystemVolumeIdentityProvider
        _volumeIdentities =
            volumeIdentities ??
            new FileSystemVolumeIdentityProvider();

    /// <summary>
    /// Retains the public constructor signature used before reviewed batch and volume services
    /// were added. Optional arguments are embedded by callers, so this overload also preserves
    /// binary compatibility for clients compiled against that signature.
    /// </summary>
    public MetadataOperationService(
        IMetadataDocumentService documents,
        IMediaFormatRegistry formats,
        IFileMutationPlanExecutor mutations,
        IAppSettings settings,
        IReindexService? reindex,
        IEditHistoryService? history,
        IMetadataFieldMappingService? fieldMappings,
        IRecoverySpaceProbe? recoverySpace) :
        this(
            documents,
            formats,
            mutations,
            settings,
            reindex,
            history,
            fieldMappings,
            recoverySpace,
            reviewedChanges: null,
            volumeIdentities: null)
    {
    }

    public async Task<MetadataOperationPlan> PreviewAsync(
        IReadOnlyList<string> paths,
        OperationRecipe recipe,
        CancellationToken ct = default) =>
        await PreviewAsync(paths, recipe, progress: null, ct);

    public async Task<MetadataOperationPlan> PreviewAsync(
        IReadOnlyList<string> paths,
        OperationRecipe recipe,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(recipe);
        var plans = new List<MetadataFilePlan>(paths.Count);
        for (int index = 0; index < paths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                paths.Count,
                paths[index],
                $"Previewing metadata {index + 1:N0} of {paths.Count:N0}"));
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
        progress?.Report(new(
            OperationPhase.Completed,
            paths.Count,
            paths.Count,
            Message: $"Previewed {paths.Count:N0} file(s)"));
        return new(Guid.NewGuid(), recipe.Name, [.. plans], DateTimeOffset.UtcNow, recipe);
    }

    public async Task<MetadataOperationPlan> PreviewEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagEdit>> editsByPath,
        string name,
        CancellationToken ct = default) =>
        await PreviewEditsAsync(editsByPath, name, progress: null, ct);

    public async Task<MetadataOperationPlan> PreviewEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagEdit>> editsByPath,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(editsByPath);
        var plans = new List<MetadataFilePlan>(editsByPath.Count);
        int index = 0;
        foreach ((string path, IReadOnlyList<TagEdit> edits) in
                 editsByPath.OrderBy(
                     item => PathSortKey(item.Key),
                     PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                editsByPath.Count,
                path,
                $"Previewing metadata {index + 1:N0} of {editsByPath.Count:N0}"));
            index++;
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
        progress?.Report(new(
            OperationPhase.Completed,
            editsByPath.Count,
            editsByPath.Count,
            Message: $"Previewed {editsByPath.Count:N0} file(s)"));
        return new(Guid.NewGuid(), name, [.. plans], DateTimeOffset.UtcNow);
    }

    public async Task<MetadataOperationPlan> PreviewValueEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> editsByPath,
        string name,
        CancellationToken ct = default) =>
        await PreviewValueEditsAsync(editsByPath, name, progress: null, ct);

    public async Task<MetadataOperationPlan> PreviewValueEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> editsByPath,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default)
        => await PreviewValueEditsCoreAsync(
            editsByPath,
            sourceExpectations: null,
            name,
            progress,
            ct);

    public async Task<MetadataOperationPlan> PreviewValueEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> editsByPath,
        IReadOnlyDictionary<string, MetadataEditSourceExpectation>
            sourceExpectations,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(
            sourceExpectations);
        return await PreviewValueEditsCoreAsync(
            editsByPath,
            sourceExpectations,
            name,
            progress,
            ct);
    }

    private async Task<MetadataOperationPlan>
        PreviewValueEditsCoreAsync(
            IReadOnlyDictionary<
                string,
                IReadOnlyList<MetadataValueEdit>>
                editsByPath,
            IReadOnlyDictionary<
                string,
                MetadataEditSourceExpectation>?
                sourceExpectations,
            string name,
            IProgress<OperationProgress>? progress,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(editsByPath);
        var plans = new List<MetadataFilePlan>(editsByPath.Count);
        int index = 0;
        foreach ((string path, IReadOnlyList<MetadataValueEdit> edits) in
                 editsByPath.OrderBy(
                     item => PathSortKey(item.Key),
                     PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                editsByPath.Count,
                path,
                $"Previewing metadata {index + 1:N0} of {editsByPath.Count:N0}"));
            index++;
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
            var operationIssues =
                new List<OperationIssue>();
            if (sourceExpectations?.TryGetValue(
                    path,
                    out MetadataEditSourceExpectation?
                        expected) == true)
                AddSourceExpectationIssues(
                    document,
                    expected,
                    operationIssues,
                    validateArtworkFingerprint:
                        false);
            foreach (MetadataValueEdit edit in edits)
            {
                if (edit.Values.Length == 0)
                    after.Remove(edit.Field);
                else
                    after[edit.Field] = edit.Values;
            }
            plans.Add(BuildPlan(
                document,
                before,
                after,
                operationIssues));
        }
        AddRecoverySpaceIssues(plans);
        progress?.Report(new(
            OperationPhase.Completed,
            editsByPath.Count,
            editsByPath.Count,
            Message: $"Previewed {editsByPath.Count:N0} file(s)"));
        return new(Guid.NewGuid(), name, [.. plans], DateTimeOffset.UtcNow);
    }

    private static ImmutableArray<string>
        PrimaryValues(
            MediaDocument document,
            MetadataFieldKey field) =>
        document.TagLayers
            .FirstOrDefault()
            ?.Fields
            .Where(value =>
                Equals(value.Field, field))
            .SelectMany(value => value.Values)
            .ToImmutableArray() ??
        [];

    private static void AddSourceExpectationIssues(
        MediaDocument document,
        MetadataEditSourceExpectation expected,
        List<OperationIssue> issues,
        bool validateArtworkFingerprint)
    {
        bool sourceChanged =
            (expected.Length is { } length &&
             document.Snapshot.Length != length) ||
            (expected.LastWriteTimeUtc is
                 { } lastWriteTimeUtc &&
             document.Snapshot.LastWriteTimeUtc !=
                 lastWriteTimeUtc) ||
            (!string.IsNullOrWhiteSpace(
                 expected.MetadataHash) &&
             !StringComparer.Ordinal.Equals(
                 expected.MetadataHash,
                 MetadataDocumentService
                     .CreateMetadataFingerprint(
                         document))) ||
            (validateArtworkFingerprint &&
             expected.ArtworkFingerprint is
                 not null &&
             !StringComparer.Ordinal.Equals(
                 expected.ArtworkFingerprint,
                 MetadataDocumentService
                     .CreateArtworkFingerprint(
                         document)));
        if (sourceChanged)
        {
            issues.Add(new(
                "metadata.edit-source-changed",
                OperationIssueSeverity.Blocker,
                "The file changed after editing " +
                "started. Reload it before " +
                "applying the pending change.",
                document.Path));
        }

        foreach ((MetadataFieldKey field,
                     ImmutableArray<string>
                         originalValues) in
                 expected.OriginalValues)
        {
            ImmutableArray<string> currentValues =
                PrimaryValues(
                    document,
                    field);
            if (currentValues.SequenceEqual(
                    originalValues,
                    StringComparer.Ordinal))
                continue;
            string valueKey =
                MetadataGridValueKey.For(field);
            issues.Add(new(
                "metadata.edit-field-changed:" +
                valueKey,
                OperationIssueSeverity.Blocker,
                $"The {field.DisplayName} " +
                "value changed after editing " +
                "started. Reload it before " +
                "applying the pending change.",
                document.Path));
        }
    }

    public async Task<MetadataOperationPlan> PreviewTagLayerEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagLayerEdit>> editsByPath,
        string name,
        CancellationToken ct = default) =>
        await PreviewTagLayerEditsAsync(
            editsByPath, name, progress: null, ct);

    public async Task<MetadataOperationPlan> PreviewTagLayerEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagLayerEdit>> editsByPath,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(editsByPath);
        var plans = new List<MetadataFilePlan>(editsByPath.Count);
        int index = 0;
        foreach ((string path, IReadOnlyList<TagLayerEdit> requested) in
                 editsByPath.OrderBy(
                     item => PathSortKey(item.Key),
                     PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                editsByPath.Count,
                path,
                $"Previewing tag layers {index + 1:N0} of {editsByPath.Count:N0}"));
            index++;
            MediaDocument document;
            try
            {
                document = await documents.LoadAsync(path, true, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                plans.Add(Unavailable(path, error));
                continue;
            }

            var issues = new List<OperationIssue>();
            if (!document.IsWritable)
                issues.Add(new(
                    "metadata.read-only",
                    OperationIssueSeverity.Blocker,
                    "This media format is not writable.",
                    document.Path));
            ValidatePolicy(document.Path, issues);
            var supported = document.EditableTagLayers
                .ToDictionary(layer => layer.Kind);
            var state = supported.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.IsPresent);
            var effective = new List<TagLayerEdit>();
            var seen = new HashSet<TagLayerKind>();
            foreach (TagLayerEdit edit in requested)
            {
                if (!seen.Add(edit.Kind))
                {
                    issues.Add(new(
                        "tag-layer.duplicate",
                        OperationIssueSeverity.Blocker,
                        $"Only one operation per tag layer may be previewed at a time: " +
                        $"{edit.Kind}.",
                        document.Path));
                    continue;
                }
                if (!supported.TryGetValue(
                        edit.Kind, out TagLayerDescriptor? descriptor))
                {
                    issues.Add(new(
                        "tag-layer.unsupported",
                        OperationIssueSeverity.Blocker,
                        $"The native format handler cannot add or remove {edit.Kind} tags.",
                        document.Path));
                    continue;
                }

                bool isPresent = state[edit.Kind];
                bool canApply = edit.Mode switch
                {
                    TagLayerEditMode.Add =>
                        !isPresent && descriptor.CanAdd,
                    TagLayerEditMode.Remove =>
                        isPresent && descriptor.CanRemove,
                    _ => false,
                };
                if (!canApply)
                {
                    issues.Add(new(
                        edit.Mode == TagLayerEditMode.Add
                            ? "tag-layer.already-present"
                            : "tag-layer.not-present",
                        OperationIssueSeverity.Blocker,
                        edit.Mode == TagLayerEditMode.Add
                            ? $"The {descriptor.DisplayName} tag layer is already present."
                            : $"The {descriptor.DisplayName} tag layer is not present.",
                        document.Path));
                    continue;
                }
                state[edit.Kind] = edit.Mode == TagLayerEditMode.Add;
                effective.Add(edit);
            }

            TagLayerEdit? copiedId3v1 = effective.FirstOrDefault(edit =>
                edit.Kind == TagLayerKind.Id3v1 &&
                edit.Mode == TagLayerEditMode.Add &&
                edit.CopyMode == TagLayerCopyMode.CopyPrimary);
            if (copiedId3v1 is not null)
            {
                try
                {
                    IMediaFile media = MediaFile.GetFile(
                        document.Path,
                        readOnly: false,
                        readArtwork: true,
                        formatRegistry: formats);
                    ((ITagLayerEditor)media).AddTagLayer(
                        TagLayerKind.Id3v1,
                        TagLayerCopyMode.CopyPrimary);
                    ID3v1Tag id3v1 = media.Tags.OfType<ID3v1Tag>().Single();
                    foreach (ID3v1CompatibilityIssue issue in
                             id3v1.GetCompatibilityIssues())
                        issues.Add(new(
                            "id3v1.truncation",
                            OperationIssueSeverity.Warning,
                            issue.Message,
                            document.Path));
                }
                catch (Exception error) when (
                    error is not OperationCanceledException)
                {
                    issues.Add(new(
                        "id3v1.preview",
                        OperationIssueSeverity.Blocker,
                        error.Message,
                        document.Path));
                }
            }

            ImmutableArray<TagLayerDifference> differences =
                [.. supported.Values
                    .Where(layer =>
                        layer.IsPresent != state[layer.Kind])
                    .OrderBy(layer => layer.Kind)
                    .Select(layer => new TagLayerDifference(
                        layer.Kind,
                        layer.IsPresent,
                        state[layer.Kind]))];
            plans.Add(new(
                document.Path,
                document.Snapshot,
                [],
                [],
                [.. issues],
                TagLayerEdits: [.. effective],
                TagLayerDifferences: differences));
        }
        AddRecoverySpaceIssues(plans);
        progress?.Report(new(
            OperationPhase.Completed,
            editsByPath.Count,
            editsByPath.Count,
            Message: $"Previewed tag layers for {editsByPath.Count:N0} file(s)"));
        return new(
            Guid.NewGuid(), name, [.. plans], DateTimeOffset.UtcNow);
    }

    public async Task<MetadataOperationPlan> PreviewTagLayerConversionsAsync(
        IReadOnlyDictionary<string, TagLayerConversionEdit> editsByPath,
        string name,
        CancellationToken ct = default) =>
        await PreviewTagLayerConversionsAsync(
            editsByPath, name, progress: null, ct);

    public async Task<MetadataOperationPlan> PreviewTagLayerConversionsAsync(
        IReadOnlyDictionary<string, TagLayerConversionEdit> editsByPath,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(editsByPath);
        var plans = new List<MetadataFilePlan>(editsByPath.Count);
        int index = 0;
        foreach ((string path, TagLayerConversionEdit edit) in
                 editsByPath.OrderBy(
                     item => PathSortKey(item.Key),
                     PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.Planning,
                index++,
                editsByPath.Count,
                path,
                "Previewing tag-layer conversion"));
            MediaDocument document;
            try
            {
                document = await documents.LoadAsync(path, true, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                plans.Add(Unavailable(path, error));
                continue;
            }
            var issues = new List<OperationIssue>();
            if (!document.IsWritable)
                issues.Add(new(
                    "metadata.read-only",
                    OperationIssueSeverity.Blocker,
                    "This media format is not writable.",
                    document.Path));
            ValidatePolicy(document.Path, issues);
            var compatibility = new List<string>();
            bool converted = false;
            try
            {
                IMediaFile media = MediaFile.GetFile(
                    document.Path,
                    readOnly: false,
                    readArtwork: true,
                    formatRegistry: formats);
                if (media is not ITagLayerEditor editor)
                    throw new NotSupportedException(
                        "The native format handler cannot convert tag layers.");
                IMetadataProvider source = FindTagLayer(media, edit.Source)
                    ?? throw new InvalidOperationException(
                        $"The {edit.Source} source layer is not present.");
                KeyValuePair<TagFields, string>[] sourceValues =
                    source.GetKnownMetadata().ToArray();
                editor.CopyTagLayer(edit.Source, edit.Target);
                IMetadataProvider target = FindTagLayer(media, edit.Target)
                    ?? throw new InvalidOperationException(
                        $"The {edit.Target} target layer was not created.");
                HashSet<TagFields> targetFields = target.GetKnownMetadata()
                    .Select(value => value.Key)
                    .ToHashSet();
                foreach (TagFields field in sourceValues
                             .Select(value => value.Key)
                             .Distinct()
                             .Where(field => !targetFields.Contains(field)))
                    compatibility.Add(
                        $"{field} has no representation in {edit.Target}.");
                if (target is ID3v1Tag id3v1)
                    compatibility.AddRange(id3v1.GetCompatibilityIssues()
                        .Select(issue => issue.Message));
                foreach (string issue in compatibility.Distinct())
                    issues.Add(new(
                        "tag-layer.conversion-loss",
                        OperationIssueSeverity.Warning,
                        issue,
                        document.Path));
                converted = true;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                issues.Add(new(
                    "tag-layer.conversion",
                    OperationIssueSeverity.Blocker,
                    error.Message,
                    document.Path));
            }
            plans.Add(new(
                document.Path,
                document.Snapshot,
                [],
                [],
                [.. issues],
                TagLayerConversions: converted ? [edit] : [],
                TagLayerConversionDifferences:
                [
                    new(edit.Source, edit.Target, [.. compatibility]),
                ]));
        }
        AddRecoverySpaceIssues(plans);
        progress?.Report(new(
            OperationPhase.Completed,
            editsByPath.Count,
            editsByPath.Count,
            Message: $"Previewed {editsByPath.Count:N0} tag conversion(s)"));
        return new(
            Guid.NewGuid(), name, [.. plans], DateTimeOffset.UtcNow);
    }

    public async Task<MetadataOperationPlan> PreviewArtworkEditsAsync(
        IReadOnlyDictionary<string, ArtworkValueEdit> editsByPath,
        string name,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(editsByPath);
        var plans = new List<MetadataFilePlan>(editsByPath.Count);
        int index = 0;
        foreach ((string path, ArtworkValueEdit edit) in
                 editsByPath.OrderBy(
                     item => PathSortKey(item.Key),
                     PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                editsByPath.Count,
                path,
                $"Previewing artwork {index + 1:N0} of {editsByPath.Count:N0}"));
            index++;
            MediaDocument document;
            try
            {
                document = await documents.LoadAsync(path, true, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                plans.Add(Unavailable(path, error));
                continue;
            }
            var issues = new List<OperationIssue>();
            LibraryArtworkPolicy policy =
                ResolveArtworkPolicy(document.Path, issues);
            if (!formats.SupportsPath(
                    document.Path, MediaFormatCapabilities.WriteArtwork))
                issues.Add(new(
                    "artwork.unsupported",
                    OperationIssueSeverity.Blocker,
                    "The native format handler cannot write embedded artwork.",
                    document.Path));
            ImmutableArray<ArtworkInput> before =
                [.. document.Artwork.Select(ToArtworkInput)];
            ArtworkInput? replacement = edit.Image;
            if (edit.Mode is ArtworkValueEditMode.ReplaceFrontCover or
                    ArtworkValueEditMode.ReplaceAll &&
                replacement is null)
            {
                issues.Add(new(
                    "artwork.image-required",
                    OperationIssueSeverity.Blocker,
                    "This artwork operation requires an image.",
                    document.Path));
            }
            if (edit.Mode == ArtworkValueEditMode.ReplaceFrontCover &&
                replacement is not null)
            {
                replacement = replacement with
                    {
                        Type = ID3v2Util.APICType.FrontCover,
                    };
            }
            ImmutableArray<ArtworkInput> requested = edit.Mode switch
            {
                ArtworkValueEditMode.ReplaceAll when replacement is not null =>
                    [replacement],
                ArtworkValueEditMode.ReplaceFrontCover when replacement is not null =>
                    [replacement, .. before.Where(image =>
                        image.Type != ID3v2Util.APICType.FrontCover)],
                ArtworkValueEditMode.RemoveFrontCover =>
                    [.. before.Where(image =>
                        image.Type != ID3v2Util.APICType.FrontCover)],
                ArtworkValueEditMode.RemoveAll => [],
                ArtworkValueEditMode.ReplaceAll or
                    ArtworkValueEditMode.ReplaceFrontCover => before,
                _ => throw new ArgumentOutOfRangeException(nameof(edit.Mode)),
            };
            if (policy.Roles == LibraryArtworkRoleSelection.FrontCoverOnly)
                requested = [.. requested
                    .Where(image =>
                        image.Type == ID3v2Util.APICType.FrontCover)
                    .Take(1)];
            ImmutableArray<ArtworkInput> after;
            try
            {
                after = [.. requested.Select(image =>
                {
                    ArtworkService.PreparedArtwork prepared =
                        ArtworkService.PrepareArtwork(
                            image.Data, image.MimeType, policy, 0);
                    return image with
                    {
                        Data = prepared.Data,
                        MimeType = prepared.MimeType,
                    };
                })];
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                issues.Add(new(
                    "artwork.prepare",
                    OperationIssueSeverity.Blocker,
                    error.Message,
                    document.Path));
                after = before;
            }
            ImmutableArray<ArtworkDescriptor> beforeDescription =
                [.. before.Select(DescribeArtwork)];
            ImmutableArray<ArtworkDescriptor> afterDescription =
                [.. after.Select(DescribeArtwork)];
            ArtworkSetDifference? difference =
                beforeDescription.SequenceEqual(afterDescription)
                    ? null
                    : new(beforeDescription, afterDescription);
            if (difference is not null)
            {
                try
                {
                    IMediaFile nativeFile = MediaFile.GetFile(
                        document.Path,
                        readOnly: false,
                        readArtwork: true,
                        formatRegistry: formats);
                    IArtworkWriter? nativeArtwork =
                        nativeFile as IArtworkWriter ??
                        nativeFile.Tags.OfType<IArtworkWriter>()
                            .FirstOrDefault();
                    if (nativeArtwork is null)
                        throw new InvalidOperationException(
                            "The native tag layer cannot write artwork.");
                    nativeArtwork.SetImages(
                        after.Select(image => new ArtworkImage(
                            image.Type,
                            image.MimeType,
                            image.Description ?? "",
                            image.Data)).ToArray());
                }
                catch (Exception error)
                {
                    issues.Add(new(
                        "artwork.native-unsupported",
                        OperationIssueSeverity.Blocker,
                        error.Message,
                        document.Path));
                }
            }
            plans.Add(new(
                document.Path,
                document.Snapshot,
                [],
                [],
                [.. issues],
                difference is null ? null : new(after),
                difference));
        }
        AddRecoverySpaceIssues(plans);
        progress?.Report(new(
            OperationPhase.Completed,
            editsByPath.Count,
            editsByPath.Count,
            Message: $"Previewed artwork for {editsByPath.Count:N0} file(s)"));
        return new(Guid.NewGuid(), name, [.. plans], DateTimeOffset.UtcNow);
    }

    public Task<MetadataOperationPlan> PreviewArtworkSetsAsync(
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest> requestsByPath,
        string name,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        PreviewArtworkSetsCoreAsync(
            requestsByPath,
            sourceExpectations: null,
            name,
            progress,
            ct);

    public Task<MetadataOperationPlan> PreviewArtworkSetsAsync(
        IReadOnlyDictionary<
            string,
            ArtworkSetPreviewRequest> requestsByPath,
        IReadOnlyDictionary<
            string,
            MetadataEditSourceExpectation>
            sourceExpectations,
        string name,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(
            sourceExpectations);
        return PreviewArtworkSetsCoreAsync(
            requestsByPath,
            sourceExpectations,
            name,
            progress,
            ct);
    }

    private async Task<MetadataOperationPlan>
        PreviewArtworkSetsCoreAsync(
            IReadOnlyDictionary<
                string,
                ArtworkSetPreviewRequest>
                requestsByPath,
            IReadOnlyDictionary<
                string,
                MetadataEditSourceExpectation>?
                sourceExpectations,
            string name,
            IProgress<OperationProgress>? progress,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requestsByPath);
        var plans = new List<MetadataFilePlan>(
            requestsByPath.Count);
        int index = 0;
        foreach ((
                     string path,
                     ArtworkSetPreviewRequest request)
                 in requestsByPath)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                requestsByPath.Count,
                path,
                $"Previewing artwork set {index + 1:N0} of " +
                $"{requestsByPath.Count:N0}"));
            index++;
            MediaDocument document;
            try
            {
                document = await documents.LoadAsync(
                    path,
                    true,
                    ct);
            }
            catch (Exception error) when (
                error is not OperationCanceledException)
            {
                plans.Add(Unavailable(path, error));
                continue;
            }

            var issues = new List<OperationIssue>();
            if (sourceExpectations?.TryGetValue(
                    path,
                    out MetadataEditSourceExpectation?
                        expected) == true)
                AddSourceExpectationIssues(
                    document,
                    expected,
                    issues,
                    validateArtworkFingerprint:
                        true);
            LibraryArtworkPolicy policy =
                ResolveArtworkPolicy(document.Path, issues);
            if (!formats.SupportsPath(
                    document.Path,
                    MediaFormatCapabilities.WriteArtwork))
                issues.Add(new(
                    "artwork.unsupported",
                    OperationIssueSeverity.Blocker,
                    "The native format handler cannot write embedded artwork.",
                    document.Path));
            if (request.MaxDimension < 0)
                issues.Add(new(
                    "artwork.dimension",
                    OperationIssueSeverity.Blocker,
                    "The artwork maximum dimension cannot be negative.",
                    document.Path));

            ImmutableArray<ArtworkInput> before =
                [.. document.Artwork.Select(ToArtworkInput)];
            ImmutableArray<ArtworkInput> requested =
                request.Images;
            if (policy.Roles ==
                LibraryArtworkRoleSelection.FrontCoverOnly)
                requested =
                [
                    .. requested
                        .Where(image =>
                            image.Type ==
                            ID3v2Util.APICType.FrontCover)
                        .Take(1),
                ];
            ImmutableArray<ArtworkInput> after;
            try
            {
                after =
                [
                    .. requested.Select(image =>
                    {
                        ArtworkService.PreparedArtwork prepared =
                            ArtworkService.PrepareArtwork(
                                image.Data,
                                image.MimeType,
                                policy,
                                request.MaxDimension);
                        return image with
                        {
                            Data = prepared.Data,
                            MimeType = prepared.MimeType,
                        };
                    }),
                ];
            }
            catch (Exception error) when (
                error is not OperationCanceledException)
            {
                issues.Add(new(
                    "artwork.prepare",
                    OperationIssueSeverity.Blocker,
                    error.Message,
                    document.Path));
                after = before;
            }

            ImmutableArray<ArtworkDescriptor> beforeDescription =
                [.. before.Select(DescribeArtwork)];
            ImmutableArray<ArtworkDescriptor> afterDescription =
                [.. after.Select(DescribeArtwork)];
            ArtworkSetDifference? difference =
                beforeDescription.SequenceEqual(afterDescription)
                    ? null
                    : new(
                        beforeDescription,
                        afterDescription);
            if (difference is not null)
            {
                try
                {
                    IMediaFile nativeFile = MediaFile.GetFile(
                        document.Path,
                        readOnly: false,
                        readArtwork: true,
                        formatRegistry: formats);
                    IArtworkWriter? nativeArtwork =
                        nativeFile as IArtworkWriter ??
                        nativeFile.Tags
                            .OfType<IArtworkWriter>()
                            .FirstOrDefault();
                    if (nativeArtwork is null)
                        throw new InvalidOperationException(
                            "The native tag layer cannot write artwork.");
                    nativeArtwork.SetImages(
                        after.Select(image =>
                            new ArtworkImage(
                                image.Type,
                                image.MimeType,
                                image.Description ?? "",
                                image.Data))
                            .ToArray());
                }
                catch (Exception error)
                {
                    issues.Add(new(
                        "artwork.native-unsupported",
                        OperationIssueSeverity.Blocker,
                        error.Message,
                        document.Path));
                }
            }
            plans.Add(new(
                document.Path,
                document.Snapshot,
                [],
                [],
                [.. issues],
                difference is null
                    ? null
                    : new(after),
                difference));
        }
        AddRecoverySpaceIssues(plans);
        progress?.Report(new(
            OperationPhase.Completed,
            requestsByPath.Count,
            requestsByPath.Count,
            Message:
                $"Previewed artwork sets for " +
                $"{requestsByPath.Count:N0} file(s)"));
        return new(
            Guid.NewGuid(),
            name,
            [.. plans],
            DateTimeOffset.UtcNow);
    }

    public async Task<MetadataOperationPlan> PreviewId3VersionEditsAsync(
        IReadOnlyDictionary<string, Id3VersionEdit> editsByPath,
        string name,
        CancellationToken ct = default) =>
        await PreviewId3VersionEditsAsync(
            editsByPath, name, progress: null, ct);

    public async Task<MetadataOperationPlan> PreviewId3VersionEditsAsync(
        IReadOnlyDictionary<string, Id3VersionEdit> editsByPath,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(editsByPath);
        var plans = new List<MetadataFilePlan>(editsByPath.Count);
        int index = 0;
        foreach ((string path, Id3VersionEdit edit) in
                 editsByPath.OrderBy(
                     item => PathSortKey(item.Key),
                     PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                editsByPath.Count,
                path,
                $"Previewing ID3 conversion {index + 1:N0} of " +
                $"{editsByPath.Count:N0}"));
            index++;
            MediaDocument document;
            try
            {
                document = await documents.LoadAsync(path, true, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                plans.Add(Unavailable(path, error));
                continue;
            }

            var issues = new List<OperationIssue>();
            if (!document.IsWritable)
                issues.Add(new(
                    "metadata.read-only",
                    OperationIssueSeverity.Blocker,
                    "This media format is not writable.",
                    document.Path));
            ValidatePolicy(document.Path, issues);
            if (document.Id3Version is not { } sourceVersion)
            {
                issues.Add(new(
                    "id3-version.unsupported",
                    OperationIssueSeverity.Blocker,
                    "The file does not contain a native writable ID3v2 tag.",
                    document.Path));
                plans.Add(new(
                    document.Path,
                    document.Snapshot,
                    [],
                    [],
                    [.. issues]));
                continue;
            }
            if (sourceVersion == edit.TargetVersion &&
                edit.TextEncodingPolicy is null)
            {
                issues.Add(new(
                    "id3-version.already-target",
                    OperationIssueSeverity.Blocker,
                    $"The tag is already ID3v2.{(int)edit.TargetVersion}.",
                    document.Path));
                plans.Add(new(
                    document.Path,
                    document.Snapshot,
                    [],
                    [],
                    [.. issues]));
                continue;
            }

            ID3VersionConversionResult? conversion = null;
            ImmutableArray<ID3VersionConversionIssue> conversionIssues = [];
            bool converted = false;
            try
            {
                IMediaFile media = MediaFile.GetFile(
                    document.Path,
                    readOnly: false,
                    readArtwork: true,
                    formatRegistry: formats);
                ID3v2Tag? tag = media as ID3v2Tag ??
                    media.Tags.OfType<ID3v2Tag>().FirstOrDefault();
                if (tag is null)
                    throw new InvalidOperationException(
                        "The native ID3v2 tag is not writable.");
                conversion = sourceVersion == edit.TargetVersion
                    ? new(
                        sourceVersion,
                        edit.TargetVersion,
                        0,
                        [])
                    : tag.ChangeVersion(
                        edit.TargetVersion,
                        new ID3VersionConversionOptions
                        {
                            DropUnsupportedFrames =
                                edit.DropUnsupportedFrames,
                            CoalesceTextValues =
                                edit.CoalesceTextValues,
                            MultiValueSeparator =
                                edit.MultiValueSeparator,
                        });
                if (edit.TextEncodingPolicy is { } encoding)
                    tag.SetTextEncodingPolicy(encoding);
                conversionIssues = [.. conversion.Issues];
                converted = true;
            }
            catch (ID3VersionConversionException error)
            {
                conversionIssues = [.. error.Issues];
                foreach (ID3VersionConversionIssue issue in error.Issues)
                    issues.Add(new(
                        "id3-version.lossy",
                        OperationIssueSeverity.Blocker,
                        $"{issue.FrameID}: {issue.Message}",
                        document.Path));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                issues.Add(new(
                    "id3-version.convert",
                    OperationIssueSeverity.Blocker,
                    error.Message,
                    document.Path));
            }
            if (converted)
            {
                foreach (ID3VersionConversionIssue issue in conversionIssues)
                    issues.Add(new(
                        "id3-version.lossy",
                        OperationIssueSeverity.Warning,
                        $"{issue.FrameID}: {issue.Message}",
                        document.Path));
            }
            plans.Add(new(
                document.Path,
                document.Snapshot,
                [],
                [],
                [.. issues],
                Id3VersionEdit: converted ? edit : null,
                Id3VersionDifference: new(
                    sourceVersion,
                    edit.TargetVersion,
                    conversion?.ConvertedFrameCount ?? 0,
                    conversionIssues,
                    edit.TextEncodingPolicy)));
        }
        AddRecoverySpaceIssues(plans);
        progress?.Report(new(
            OperationPhase.Completed,
            editsByPath.Count,
            editsByPath.Count,
            Message: $"Previewed {editsByPath.Count:N0} ID3 conversion(s)"));
        return new(
            Guid.NewGuid(), name, [.. plans], DateTimeOffset.UtcNow);
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
        MetadataOperationStageResult stage =
            await StageAsync(plan, progress, ct)
                .ConfigureAwait(false);
        try
        {
            return await ApplyStagedAsync(
                    stage,
                    progress,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            await DiscardStageAsync(
                    stage,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    internal async Task<MetadataApplyResult>
        ApplyStagedAsync(
            MetadataOperationStageResult stage,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        IProgress<OperationProgress>? safeProgress =
            progress is null
                ? null
                : new SafeOperationProgress(progress);
        var journals = new List<string>();
        RecoveryStorageSummary recoveryStorage = RecoveryStorageSummary.Empty;
        IReadOnlyList<FileMutationSummary> results;
        if (stage.Participants.Length == 1)
        {
            results =
            [
                await mutations.ApplyAsync(
                        stage.Participants[0],
                        safeProgress,
                        ct)
                    .ConfigureAwait(false),
            ];
        }
        else
        {
            IReviewedChangeBatchService batchService =
                reviewedChanges ??
                throw new InvalidOperationException(
                    "Multi-volume metadata changes require the reviewed-change batch service.");
            ReviewedChangeBatchPlan batch =
                batchService.CreatePlan(
                    stage.Participants);
            ReviewedChangeBatchResult batchResult =
                await batchService.ApplyAsync(
                        batch,
                        safeProgress,
                        ct)
                    .ConfigureAwait(false);
            results = batchResult.ParticipantResults;
        }
        foreach (FileMutationSummary result in results)
        {
            if (result.JournalPath is not null)
                journals.Add(result.JournalPath);
            if (result.RecoveryStorage is not null)
                recoveryStorage =
                    recoveryStorage.Add(
                        result.RecoveryStorage);
        }
        var issues = stage.Plan.Files
            .Where(file => file.CanApply)
            .SelectMany(file => file.Issues)
            .ToList();
        try
        {
            // Every executor result represents a durable commit. Cancellation and presentation
            // failures after this boundary must not turn a committed edit into a reported
            // transaction failure or prevent best-effort history/cache finalization.
            await CompleteStagedApplyAsync(
                    stage,
                    journals,
                    recordHistory: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            issues.Add(new(
                "metadata.post-commit-finalization",
                OperationIssueSeverity.Warning,
                "Metadata changes were committed, but post-commit " +
                $"bookkeeping requires reconciliation: {error.Message}"));
        }
        ReportSafely(progress, new(
            OperationPhase.Completed,
            stage.ChangedFiles,
            stage.ChangedFiles,
            Message:
                $"Saved metadata to " +
                $"{stage.ChangedFiles:N0} file(s)"));
        return new(
            stage.ChangedFiles,
            [.. journals],
            [.. issues],
            recoveryStorage.FullOriginalCount +
                recoveryStorage.ReverseDeltaCount > 0
                ? recoveryStorage
                : null);
    }

    public async Task<MetadataOperationStageResult> StageAsync(
        MetadataOperationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException(
                "The metadata plan has no applicable changes or contains blockers.");

        MetadataFilePlan[] changed =
        [
            .. plan.Files
                .Where(file => file.CanApply)
                .OrderBy(
                    file => PathSortKey(file.Path),
                    PathComparer),
        ];
        foreach (MetadataFilePlan filePlan in changed)
        {
            ct.ThrowIfCancellationRequested();
            MediaDocument current = await documents.LoadAsync(
                filePlan.Path,
                filePlan.ArtworkEdit is not null ||
                !filePlan.TagLayerEdits.IsDefaultOrEmpty ||
                !filePlan.TagLayerConversions.IsDefaultOrEmpty ||
                filePlan.Id3VersionEdit is not null,
                ct).ConfigureAwait(false);
            if (current.Snapshot.Length != filePlan.Snapshot.Length ||
                current.Snapshot.LastWriteTimeUtc !=
                filePlan.Snapshot.LastWriteTimeUtc ||
                !StringComparer.Ordinal.Equals(
                    current.Snapshot.MetadataHash,
                    filePlan.Snapshot.MetadataHash))
                throw new InvalidOperationException(
                    $"Stale plan: metadata changed since preview: '{filePlan.Path}'.");
        }

        var stagedFiles = new List<MetadataStagedFile>();
        var participants = new List<FileMutationPlan>();
        int completed = 0;
        try
        {
            var volumeRows = changed
                .Select(file => new
                {
                    File = file,
                    Identity =
                        _volumeIdentities.GetIdentity(
                            file.Path),
                })
                .ToArray();
            foreach (var volume in
                     volumeRows
                         .GroupBy(
                             row =>
                                 VolumePartitionKey(
                                     row.Identity),
                             StringComparer.Ordinal)
                         .OrderBy(
                             group => group.Key,
                             StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var firstRow = volume.First();
                FileSystemVolumeIdentity identity =
                    firstRow.Identity;
                MetadataFilePlan[] group =
                [
                    .. volume.Select(row =>
                        row.File),
                ];
                string commonRoot =
                    CommonDirectory(group.Select(file =>
                        file.Path));
                string container =
                    RecoveryContainer(
                        commonRoot,
                        group[0].Path,
                        identity);
                string recoveryRoot = Path.Combine(
                    container,
                    DateTime.UtcNow.ToString(
                        "yyyyMMdd-HHmmssfff",
                        CultureInfo.InvariantCulture) +
                    "-" +
                    Guid.NewGuid().ToString("N"));
                EnsureSameVolume(
                    identity,
                    recoveryRoot);
                var actions =
                    new List<FileMutationAction>(
                        group.Length);
                foreach (MetadataFilePlan filePlan in group)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new(
                        OperationPhase.Applying,
                        completed,
                        changed.Length,
                        filePlan.Path,
                        "Staging metadata changes"));
                    string stagedPath = Stage(
                        filePlan,
                        formats,
                        fieldMappings);
                    stagedFiles.Add(new(
                        filePlan.Path,
                        stagedPath));
                    var stageInfo =
                        new FileInfo(stagedPath);
                    actions.Add(new(
                        FileMutationKind.Replace,
                        stagedPath,
                        filePlan.Path,
                        Snapshot(stageInfo),
                        new(
                            true,
                            false,
                            filePlan.Snapshot.Length,
                            filePlan.Snapshot
                                .LastWriteTimeUtc)
                        {
                            Path = filePlan.Path,
                        }));
                    completed++;
                }

                LibraryConfiguration? configuration =
                    settings.GetSnapshot().Configuration;
                participants.Add(new(
                    "MusicLibraryManager",
                    commonRoot,
                    recoveryRoot,
                    actions,
                    [],
                    DateTimeOffset.UtcNow,
                    RetainRecovery: true,
                    PolicyFingerprint:
                        configuration?
                            .PolicySnapshot.Fingerprint,
                    LibraryId:
                        configuration?.LibraryId,
                    RecoveryPayloadPolicy:
                        RecoveryPayloadPolicy
                            .AdaptiveReverseDelta));
            }
            return new(
                plan,
                [.. participants],
                [.. stagedFiles]);
        }
        catch
        {
            foreach (MetadataStagedFile staged in
                     stagedFiles)
                TryDelete(staged.StagedPath);
            throw;
        }
    }

    public Task CompleteStagedApplyAsync(
        MetadataOperationStageResult stage,
        IReadOnlyList<string> journalPaths,
        bool recordHistory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        MetadataFilePlan[] changed =
        [
            .. stage.Plan.Files
                .Where(file => file.CanApply)
                .OrderBy(
                    file => PathSortKey(file.Path),
                    PathComparer),
        ];
        if (reindex is not null)
            QueueCacheRefresh(
                changed,
                reindex,
                formats,
                fieldMappings);
        if (recordHistory && history is not null)
            history.Record(new(
                stage.Plan.Id,
                stage.Plan.Name,
                DateTimeOffset.UtcNow,
                [.. journalPaths],
                [.. changed.Select(file =>
                    file.Path)],
                stage.Plan.Recipe));
        return Task.CompletedTask;
    }

    public Task DiscardStageAsync(
        MetadataOperationStageResult stage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        foreach (MetadataStagedFile file in stage.Files)
        {
            ct.ThrowIfCancellationRequested();
            TryDelete(file.StagedPath);
        }
        return Task.CompletedTask;
    }

    private static void QueueCacheRefresh(
        IReadOnlyList<MetadataFilePlan> changed,
        IReindexService reindex,
        IMediaFormatRegistry formats,
        IMetadataFieldMappingService? fieldMappings)
    {
        // A full index owns the library database gate for its duration. The files are already
        // committed at this point, so waiting for that gate would leave the foreground save
        // activity running—sometimes for hours. Queue the best-effort refresh instead; it will
        // acquire the gate after the index and repair the affected cache rows in commit order.
        _ = Task.Run(
            async () =>
            {
                foreach (MetadataFilePlan changedFile in changed)
                {
                    try
                    {
                        IMediaFile saved = MediaFile.GetFile(
                            changedFile.Path,
                            readOnly: true,
                            readArtwork: true,
                            formatRegistry: formats);
                        await reindex.ReindexFileAsync(
                                changedFile.Path,
                                fieldMappings?.ProjectForCache(
                                    changedFile.Path, saved) ?? saved,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // The files are authoritative; the next index pass repairs the cache.
                    }
                }
            },
            CancellationToken.None);
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
        ValidateNativeEdits(document, edits, issues);
        return new(document.Path, document.Snapshot, differences, edits, [.. issues]);
    }

    private void ValidateNativeEdits(
        MediaDocument document,
        ImmutableArray<MetadataValueEdit> edits,
        List<OperationIssue> issues)
    {
        if (edits.IsDefaultOrEmpty || !document.IsWritable)
            return;
        IMediaFile file;
        try
        {
            file = MediaFile.GetFile(
                document.Path,
                readOnly: false,
                readArtwork: false,
                formatRegistry: formats);
        }
        catch (Exception error)
        {
            issues.Add(new(
                "metadata.native-open",
                OperationIssueSeverity.Blocker,
                error.Message,
                document.Path));
            return;
        }

        foreach (MetadataValueEdit edit in edits)
        {
            try
            {
                ApplyNativeEdit(
                    file, document.Path, edit, fieldMappings);
                bool isMapped = edit.Field.KnownField is { } known &&
                    fieldMappings?.TryGet(
                        document.Path, known, out _) == true;
                if (!isMapped)
                    ValidateNativeProjection(
                        file, document.Path, edit, issues);
            }
            catch (Exception error)
            {
                issues.Add(new(
                    "metadata.native-unsupported",
                    OperationIssueSeverity.Blocker,
                    $"'{edit.Field.DisplayName}' cannot be written by the " +
                    $"native tag handler: {error.Message}",
                    document.Path));
            }
        }
    }

    private static void ApplyNativeEdit(
        IMediaFile file,
        string path,
        MetadataValueEdit edit,
        IMetadataFieldMappingService? fieldMappings)
    {
        IMetadataWriter? writer = file as IMetadataWriter ??
            file.Tags.OfType<IMetadataWriter>().FirstOrDefault();
        VorbisComments? vorbis = file as VorbisComments ??
            file.Tags.OfType<VorbisComments>().FirstOrDefault();
        IUserStringMetadata? custom = file as IUserStringMetadata ??
            file.Tags.OfType<IUserStringMetadata>().FirstOrDefault();
        if (!edit.Field.IsKnown)
        {
            string key = edit.Field.CustomName!;
            if (custom is null)
                throw new InvalidOperationException(
                    $"Custom field '{key}' is not supported.");
            if (edit.Values.Length == 0)
                custom.RemoveUserString(key);
            else if (edit.Values.Length == 1)
                custom.SetUserString(key, edit.Values[0]);
            else if (custom is IMultiValueUserStringMetadata multiCustom)
                multiCustom.SetUserStringValues(key, edit.Values);
            else
                throw new InvalidOperationException(
                    $"Multiple values for custom field '{key}' are not supported.");
            return;
        }

        TagFields field = edit.Field.KnownField!.Value;
        if (fieldMappings?.TryGet(
                path, field, out string nativeFieldName) == true)
        {
            if (custom is null)
                throw new InvalidOperationException(
                    $"Mapped native field '{nativeFieldName}' is not supported.");
            try
            {
                if (writer is not null) writer.RemoveField(field);
                else vorbis?.RemoveField(field);
            }
            catch (ArgumentException) { }
            if (edit.Values.Length == 0)
                custom.RemoveUserString(nativeFieldName);
            else if (edit.Values.Length == 1)
                custom.SetUserString(nativeFieldName, edit.Values[0]);
            else if (custom is IMultiValueUserStringMetadata mappedMulti)
                mappedMulti.SetUserStringValues(nativeFieldName, edit.Values);
            else
                throw new InvalidOperationException(
                    $"Mapped field '{nativeFieldName}' cannot store multiple values.");
            return;
        }
        if (writer is null && vorbis is null)
            throw new InvalidOperationException(
                "The file has no writable metadata layer.");
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
                $"Multiple values for '{field}' are not supported.");
    }

    private static void ValidateNativeProjection(
        IMediaFile file,
        string path,
        MetadataValueEdit edit,
        List<OperationIssue> issues)
    {
        IMetadataProvider? provider = file.Tags.FirstOrDefault();
        if (provider is null)
            return;
        ImmutableArray<string> projected;
        if (edit.Field.IsKnown)
            projected = provider.GetKnownMetadata()
                .Where(value =>
                    value.Key == edit.Field.KnownField!.Value)
                .Select(value => value.Value)
                .ToImmutableArray();
        else if (provider is IUserStringMetadata custom)
            projected = custom.GetUserStrings()
                .Where(value => string.Equals(
                    value.Key,
                    edit.Field.CustomName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(value => value.Value)
                .ToImmutableArray();
        else
            return;
        if (!projected.SequenceEqual(edit.Values))
            issues.Add(new(
                "metadata.native-normalization",
                OperationIssueSeverity.Warning,
                $"'{edit.Field.DisplayName}' will be normalized from " +
                $"'{string.Join("; ", edit.Values)}' to " +
                $"'{string.Join("; ", projected)}'.",
                path));
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

    private LibraryArtworkPolicy ResolveArtworkPolicy(
        string path,
        List<OperationIssue> issues)
    {
        LibraryProfile legacy = LibraryProfilePresets.Create(
            LibraryProfilePreset.LegacyMusicLibraryTools);
        LibraryConfiguration? configuration =
            settings.GetSnapshot().Configuration;
        if (configuration is null)
            return legacy.Artwork;
        LibraryIndexLocation[] roots =
            configuration.IndexLocations.ToArray();
        LibraryIndexLocation? root =
            LibraryRootPermissionPolicy.MostSpecific(path, roots);
        if (root is null)
            return legacy.Artwork;
        if (!LibraryRootPermissionPolicy.Allows(
                path, roots, LibraryRootPermissions.WriteArtwork))
            issues.Add(new(
                "artwork.permission",
                OperationIssueSeverity.Blocker,
                "The active library policy does not permit artwork writes.",
                path));
        LibraryArtworkPolicy policy =
            configuration.GetEffectiveProfile(root).Artwork;
        if (policy.Storage != LibraryArtworkStorage.Embedded)
            issues.Add(new(
                "artwork.storage",
                OperationIssueSeverity.Blocker,
                policy.Storage == LibraryArtworkStorage.None
                    ? "The effective library policy disables artwork storage."
                    : "This staged artwork operation currently supports embedded-only " +
                      "policies; sidecar artwork requires a multi-artifact recovery plan.",
                path));
        return policy;
    }

    private static ArtworkInput ToArtworkInput(ArtworkModel image)
    {
        ID3v2Util.APICType type =
            Enum.TryParse(
                image.Category, ignoreCase: true,
                out ID3v2Util.APICType parsed)
                ? parsed
                : ID3v2Util.APICType.Other;
        return new(
            type,
            string.IsNullOrWhiteSpace(image.ImageType)
                ? "image/jpeg"
                : image.ImageType,
            image.Data,
            image.Description ?? "");
    }

    private static ArtworkDescriptor DescribeArtwork(ArtworkInput image) =>
        new(
            image.Type,
            image.MimeType,
            image.Description ?? "",
            image.Data.Length,
            Convert.ToHexString(SHA256.HashData(image.Data))
                .ToLowerInvariant());

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
        if (condition is null)
            return true;
        ImmutableArray<string> values = condition.Field is null
            ? []
            : fields.GetValueOrDefault(condition.Field, []);
        string expected = condition.Value ?? "";
        bool result = condition.Operator switch
        {
            MetadataConditionOperator.Always => true,
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
        IMediaFormatRegistry formats,
        IMetadataFieldMappingService? fieldMappings)
    {
        string stage = Path.Combine(
            Path.GetDirectoryName(plan.Path)!,
            $".{Path.GetFileNameWithoutExtension(plan.Path)}." +
            $"{Guid.NewGuid():N}.workbench-stage" +
            Path.GetExtension(plan.Path));
        try
        {
            IMediaFile file = MediaFile.GetFile(plan.Path, readOnly: false,
                readArtwork: true, formatRegistry: formats);
            if (!plan.TagLayerEdits.IsDefaultOrEmpty)
            {
                if (file is not ITagLayerEditor layerEditor)
                    throw new InvalidOperationException(
                        "The file has no editable tag-layer envelope.");
                foreach (TagLayerEdit edit in plan.TagLayerEdits)
                {
                    if (edit.Mode == TagLayerEditMode.Add)
                        layerEditor.AddTagLayer(edit.Kind, edit.CopyMode);
                    else
                        layerEditor.RemoveTagLayer(edit.Kind);
                }
            }
            if (!plan.TagLayerConversions.IsDefaultOrEmpty)
            {
                if (file is not ITagLayerEditor layerEditor)
                    throw new InvalidOperationException(
                        "The file cannot convert tag layers.");
                foreach (TagLayerConversionEdit conversion in
                         plan.TagLayerConversions)
                    layerEditor.CopyTagLayer(
                        conversion.Source, conversion.Target);
            }
            if (plan.Id3VersionEdit is { } versionEdit)
            {
                ID3v2Tag? id3 = file as ID3v2Tag ??
                    file.Tags.OfType<ID3v2Tag>().FirstOrDefault();
                if (id3 is null)
                    throw new InvalidOperationException(
                        "The file has no writable ID3v2 tag.");
                if (id3.Version != (int)versionEdit.TargetVersion)
                    id3.ChangeVersion(
                        versionEdit.TargetVersion,
                        new ID3VersionConversionOptions
                        {
                            DropUnsupportedFrames =
                                versionEdit.DropUnsupportedFrames,
                            CoalesceTextValues =
                                versionEdit.CoalesceTextValues,
                            MultiValueSeparator =
                                versionEdit.MultiValueSeparator,
                        });
                if (versionEdit.TextEncodingPolicy is { } encoding)
                    id3.SetTextEncodingPolicy(encoding);
            }
            foreach (MetadataValueEdit edit in plan.Edits)
                ApplyNativeEdit(
                    file, plan.Path, edit, fieldMappings);
            if (plan.ArtworkEdit is not null)
            {
                IArtworkWriter? artworkWriter = file as IArtworkWriter ??
                    file.Tags.OfType<IArtworkWriter>().FirstOrDefault();
                if (artworkWriter is null)
                    throw new InvalidOperationException(
                        "The file has no writable artwork layer.");
                artworkWriter.SetImages(
                    plan.ArtworkEdit.Images.Select(image => new ArtworkImage(
                        image.Type,
                        image.MimeType,
                        image.Description ?? "",
                        image.Data)).ToArray());
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

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static IMetadataProvider? FindTagLayer(
        IMediaFile media,
        TagLayerKind kind) => kind switch
        {
            TagLayerKind.Id3v1 =>
                media.Tags.OfType<ID3v1Tag>().FirstOrDefault(),
            TagLayerKind.Id3v2 =>
                media.Tags.OfType<ID3v2Tag>().FirstOrDefault(),
            TagLayerKind.ApeV2 =>
                media.Tags.OfType<APETag>().FirstOrDefault(),
            _ => null,
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

    private string RecoveryContainer(
        string commonRoot,
        string firstPath,
        FileSystemVolumeIdentity identity)
    {
        string firstDirectory =
            Path.GetDirectoryName(
                Path.GetFullPath(firstPath))!;
        string commonName =
            Path.GetFileName(
                Path.TrimEndingDirectorySeparator(
                    commonRoot));
        string firstDirectoryName =
            Path.GetFileName(
                Path.TrimEndingDirectorySeparator(
                    firstDirectory));
        var candidates = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(commonName))
        {
            candidates.Add(
                commonRoot +
                ".MusicLibraryManager-recovery");
        }
        candidates.Add(
            Path.Combine(
                commonRoot,
                (string.IsNullOrWhiteSpace(commonName)
                    ? "library"
                    : commonName) +
                ".MusicLibraryManager-recovery"));
        candidates.Add(
            Path.Combine(
                firstDirectory,
                (string.IsNullOrWhiteSpace(
                        firstDirectoryName)
                    ? "library"
                    : firstDirectoryName) +
                ".MusicLibraryManager-recovery"));

        foreach (string candidate in
                 candidates.Distinct(PathComparer))
        {
            try
            {
                FileSystemVolumeIdentity candidateIdentity =
                    _volumeIdentities.GetIdentity(candidate);
                if (StringComparer.Ordinal.Equals(
                        candidateIdentity.Key,
                        identity.Key))
                    return candidate;
            }
            catch
            {
            }
        }
        throw new InvalidOperationException(
            $"No same-volume metadata recovery root is available for '{firstPath}'.");
    }

    private void EnsureSameVolume(
        FileSystemVolumeIdentity expected,
        string recoveryRoot)
    {
        FileSystemVolumeIdentity actual =
            _volumeIdentities.GetIdentity(
                recoveryRoot);
        if (!StringComparer.Ordinal.Equals(
                expected.Key,
                actual.Key))
        {
            throw new InvalidOperationException(
                $"Metadata recovery root '{recoveryRoot}' is not on the source volume.");
        }
    }

    private static string VolumePartitionKey(
        FileSystemVolumeIdentity identity)
    {
        string root = PathSortKey(
            identity.RootPath);
        if (OperatingSystem.IsWindows())
            root = root.ToUpperInvariant();
        return identity.Key + "\0" + root;
    }

    private static bool IsWithin(string path, string root)
    {
        string prefix = Path.TrimEndingDirectorySeparator(root) +
            Path.DirectorySeparatorChar;
        return PathComparer.Equals(path, root) ||
            path.StartsWith(prefix, PathComparison);
    }

    private void AddRecoverySpaceIssues(List<MetadataFilePlan> plans)
    {
        var volumeRows = plans
            .Where(plan => plan.HasChanges)
            .Select(plan => new
            {
                Plan = plan,
                Identity =
                    _volumeIdentities.GetIdentity(
                        plan.Path),
            })
            .ToArray();
        foreach (var volume in volumeRows
                     .GroupBy(
                         row =>
                             VolumePartitionKey(
                                 row.Identity),
                         StringComparer.Ordinal)
                     .OrderBy(
                         group => group.Key,
                         StringComparer.Ordinal))
        {
            string root =
                volume.First().Identity.RootPath;
            if (string.IsNullOrWhiteSpace(root))
                continue;
            try
            {
                // The output stage is always required. Recovery is selected adaptively at apply:
                // a small reverse delta is retained when possible and the existing full-original
                // path remains the safe fallback. Apply performs the final capacity check while
                // holding the mutation lease and before changing any live file.
                long required = checked(
                    volume.Sum(row =>
                        row.Plan.Snapshot.Length));
                long? available =
                    (recoverySpace ?? SystemRecoverySpaceProbe.Instance)
                    .GetAvailableFreeSpace(root);
                if (available is not null && available < required)
                {
                    foreach (var row in volume)
                    {
                        MetadataFilePlan plan =
                            row.Plan;
                        var issue = new OperationIssue(
                            "metadata.recovery-space",
                            OperationIssueSeverity.Blocker,
                            $"At least {required:N0} free bytes are estimated for metadata staging.",
                            plan.Path);
                        int index = plans.IndexOf(plan);
                        plans[index] = plan with { Issues = plan.Issues.Add(issue) };
                    }
                }
            }
            catch
            {
                // An unknown estimate must not deny remote or virtual filesystems. Apply still
                // surfaces a concrete I/O failure if storage cannot accept the data.
            }
        }
    }

    private static string PathSortKey(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static void ReportSafely(
        IProgress<OperationProgress>? progress,
        OperationProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch
        {
            // A progress observer cannot change the outcome after the durable commit point.
        }
    }

    private sealed class SafeOperationProgress(
        IProgress<OperationProgress> inner) :
        IProgress<OperationProgress>
    {
        public void Report(
            OperationProgress value) =>
            ReportSafely(inner, value);
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

public sealed class EditHistoryService : IEditHistoryService
{
    private const string Preference = "manager.workbench.history.v1";
    private readonly IAppSettings _settings;
    private readonly IOperationJournalService _journals;
    private readonly HistoryState _state;
    private readonly object _undoIssueGate = new();
    private Guid _lastUndoEntryId;

    public EditHistoryService(
        IAppSettings settings,
        IOperationJournalService journals)
    {
        _settings = settings;
        _journals = journals;
        _state = Load(settings);
        ReconcilePendingTransition();
    }

    public IReadOnlyList<EditHistoryEntry> Entries => _state.Undo;
    public IReadOnlyList<EditHistoryEntry> RedoEntries => _state.Redo;
    public IReadOnlyList<OperationIssue> LastUndoIssues
    {
        get;
        private set;
    } = [];
    public Task<IReadOnlyList<OperationIssue>>
        LastUndoReconciliation
    {
        get;
        private set;
    } = Task.FromResult<IReadOnlyList<OperationIssue>>([]);
    public bool ReconcilesInternalCatalogOnUndo =>
        _journals.ReconcilesInternalCatalogOnRestore;
    public bool CanUndo =>
        _state.PendingTransition?.Stage !=
            HistoryTransitionStage.Committed &&
        _state.Undo.Count > 0;
    public bool CanRedo =>
        _state.PendingTransition is null &&
        _state.Redo.Any(entry =>
            entry.Recipe is not null);

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
        lock (_undoIssueGate)
        {
            LastUndoIssues = [];
            LastUndoReconciliation =
                Task.FromResult<IReadOnlyList<OperationIssue>>(
                    []);
            _lastUndoEntryId = Guid.Empty;
        }
        if (_state.PendingTransition is not null)
        {
            ReconcilePendingTransition();
            if (_state.PendingTransition is not null)
                throw new InvalidOperationException(
                    "The previous undo could not be reconciled. " +
                    "Recovery data and edit history were retained.");
        }
        if (_state.Undo.Count == 0)
            return 0;
        EditHistoryEntry entry = _state.Undo[0];
        _lastUndoEntryId = entry.Id;
        var restorePlans = new List<OperationRestorePlan>();
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
            OperationBrowseResult browse = await _journals.BrowseAsync(summary, ct);
            OperationFileEntry[] candidates = browse.Entries
                .Where(item => item.Kind is
                        OperationEntryKind.Quarantined or
                        OperationEntryKind.Moved or
                        OperationEntryKind.Created &&
                    item.Exists && item.CurrentPath is not null)
                .ToArray();
            OperationRestorePlan plan = await _journals.PreviewRestoreAsync(
                summary, candidates, ct);
            if (plan.CanApply)
                restorePlans.Add(plan);
        }
        if (restorePlans.Count == 0)
            throw new InvalidOperationException(
                "No retained recovery payload is available for the latest edit.");

        OperationRestoreBatchPlan batch = await _journals.PreviewRestoreBatchAsync(
            restorePlans, ct);
        _state.PendingTransition = new(
            entry.Id,
            HistoryTransitionStage.Prepared,
            [.. restorePlans.Select(item => item.RestoreJournalPath)]);
        try
        {
            PersistRequired();
        }
        catch
        {
            _state.PendingTransition = null;
            throw;
        }
        OperationRestoreBatchResult result;
        try
        {
            result = await _journals.ApplyRestoreBatchAsync(batch, progress, ct);
        }
        catch (Exception applyError)
        {
            try
            {
                OperationRestoreReconciliationResult reconciliation =
                    await _journals.ReconcileRestoreBatchDetailedAsync(
                        _state.PendingTransition!.RestoreJournalPaths,
                        reconcileInternalCatalog: true,
                        ct: CancellationToken.None);
                if (reconciliation.State is
                    OperationRestoreTransitionState.Committed or
                    OperationRestoreTransitionState.Consumed)
                {
                    var issues = new List<OperationIssue>(
                        reconciliation.Issues)
                    {
                        new(
                            "edit-history.undo-reconciled",
                            OperationIssueSeverity.Warning,
                            "Undo committed and required post-commit " +
                            "reconciliation: " +
                            applyError.Message),
                    };
                    CompleteCommittedUndo(
                        entry,
                        issues,
                        retainReconciliation:
                        reconciliation.State ==
                        OperationRestoreTransitionState
                            .Committed);
                    return batch.Actions.Count;
                }
                _state.PendingTransition = null;
                PersistRequired();
            }
            catch (Exception reconciliationError)
            {
                Persist();
                LastUndoIssues =
                [
                    new(
                        "edit-history.undo-reconciliation-failed",
                        OperationIssueSeverity.Warning,
                        "Undo failed before its durable outcome could be " +
                        "confirmed: " +
                        reconciliationError.Message),
                ];
            }
            throw;
        }

        var resultIssues =
            new List<OperationIssue>(result.Issues);
        CompleteCommittedUndo(
            entry,
            resultIssues,
            retainReconciliation:
                result.TransitionState ==
                    OperationRestoreTransitionState
                        .Committed &&
                result.PostCommitReconciliation is not null);
        ObservePostCommitReconciliation(
            entry.Id,
            _state.PendingTransition?
                .RestoreJournalPaths ??
            [],
            result.PostCommitReconciliation,
            resultIssues);
        return result.RestoredCount;
    }

    private void CompleteCommittedUndo(
        EditHistoryEntry entry,
        ICollection<OperationIssue> issues,
        bool retainReconciliation = false)
    {
        _state.PendingTransition =
            _state.PendingTransition is null
                ? new(
                    entry.Id,
                    HistoryTransitionStage.Committed,
                    [])
                : _state.PendingTransition with
                {
                    Stage =
                        HistoryTransitionStage.Committed,
                };
        try
        {
            PersistRequired();
        }
        catch (Exception error)
        {
            issues.Add(new(
                "edit-history.undo-state-persistence-failed",
                OperationIssueSeverity.Warning,
                "Undo committed, but its intermediate history state " +
                "could not be persisted: " + error.Message));
        }

        try
        {
            CompletePendingTransition(
                entry,
                retainReconciliation);
        }
        catch (Exception error)
        {
            // CompletePendingTransition mutates the in-memory lists before
            // persistence. Do not put the committed entry back into Undo or
            // allow the operation to replay.
            issues.Add(new(
                "edit-history.undo-finalization-failed",
                OperationIssueSeverity.Warning,
                "Undo committed, but its history finalization could not " +
                "be persisted: " + error.Message));
        }
        lock (_undoIssueGate)
            LastUndoIssues = [.. issues];
    }

    private void ObservePostCommitReconciliation(
        Guid entryId,
        ImmutableArray<string> restoreJournalPaths,
        PostCommitReconciliationHandle? reconciliation,
        IReadOnlyList<OperationIssue> initialIssues)
    {
        if (reconciliation is null)
        {
            LastUndoReconciliation =
                Task.FromResult<IReadOnlyList<OperationIssue>>(
                    []);
            return;
        }

        LastUndoReconciliation = ObserveAsync();
        return;

        async Task<IReadOnlyList<OperationIssue>> ObserveAsync()
        {
            IReadOnlyList<OperationIssue> issues;
            try
            {
                issues = await reconciliation.Completion
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                issues =
                [
                    new(
                        "edit-history.undo-reconciliation-failed",
                        OperationIssueSeverity.Warning,
                        "Undo committed, but post-commit reconciliation " +
                        "could not finish: " + error.Message),
                ];
            }
            lock (_undoIssueGate)
            {
                if (_lastUndoEntryId == entryId)
                    LastUndoIssues =
                    [
                        .. initialIssues,
                        .. issues,
                    ];
            }
            if (_state.PendingTransition?.EntryId ==
                entryId)
            {
                if (issues.Count == 0)
                {
                    _state.PendingTransition = null;
                }
                else
                {
                    _state.PendingTransition = new(
                        entryId,
                        HistoryTransitionStage.Committed,
                        restoreJournalPaths);
                }
                Persist();
            }
            return issues;
        }
    }

    private void CompletePendingTransition(
        EditHistoryEntry entry,
        bool retainReconciliation = false)
    {
        HistoryTransition? transition =
            _state.PendingTransition;
        int index = _state.Undo.FindIndex(item => item.Id == entry.Id);
        if (index >= 0)
            _state.Undo.RemoveAt(index);
        if (_state.Redo.All(item => item.Id != entry.Id))
            _state.Redo.Insert(0, entry);
        if (_state.Redo.Count > 100)
            _state.Redo.RemoveRange(100, _state.Redo.Count - 100);
        _state.PendingTransition =
            retainReconciliation &&
            transition is not null
                ? transition with
                {
                    Stage =
                        HistoryTransitionStage.Committed,
                }
                : null;
        PersistRequired();
    }

    private void ReconcilePendingTransition()
    {
        HistoryTransition? transition = _state.PendingTransition;
        if (transition is null)
            return;
        EditHistoryEntry? entry =
            _state.Undo.FirstOrDefault(item =>
                item.Id == transition.EntryId) ??
            _state.Redo.FirstOrDefault(item =>
                item.Id == transition.EntryId);
        OperationRestoreReconciliationResult reconciliation;
        try
        {
            reconciliation =
                _journals.ReconcileRestoreBatchDetailedAsync(
                        transition.RestoreJournalPaths,
                        reconcileInternalCatalog: true,
                        ct: CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
        }
        catch (Exception error)
        {
            if (transition.Stage ==
                    HistoryTransitionStage.Committed &&
                entry is not null)
            {
                var issues = new List<OperationIssue>
                {
                    new(
                        "edit-history.undo-reconciliation-failed",
                        OperationIssueSeverity.Warning,
                        "Undo was already committed, but startup " +
                        "reconciliation could not finish: " +
                        error.Message),
                };
                CompleteCommittedUndo(
                    entry,
                    issues,
                    retainReconciliation: true);
            }
            // A prepared transition whose journal outcome is unknown stays
            // intact so a later startup can determine whether it committed.
            return;
        }
        LastUndoIssues =
        [
            .. reconciliation.Issues,
        ];
        if (entry is not null &&
            reconciliation.State is
                OperationRestoreTransitionState.Committed or
                OperationRestoreTransitionState.Consumed)
        {
            var issues =
                new List<OperationIssue>(
                    reconciliation.Issues);
            CompleteCommittedUndo(
                entry,
                issues,
                retainReconciliation:
                    reconciliation.State ==
                    OperationRestoreTransitionState
                        .Committed);
            return;
        }
        _state.PendingTransition = null;
        Persist();
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
        try { _settings.SetPreference(Preference, JsonSerializer.Serialize(_state)); }
        catch { /* History persistence is best effort; recovery remains discoverable. */ }
    }

    private void PersistRequired() =>
        _settings.SetPreference(
            Preference,
            JsonSerializer.Serialize(_state));

    private enum HistoryTransitionStage
    {
        Prepared,
        Committed,
    }

    private sealed record HistoryTransition(
        Guid EntryId,
        HistoryTransitionStage Stage,
        ImmutableArray<string> RestoreJournalPaths);

    private sealed class HistoryState(
        List<EditHistoryEntry> undo,
        List<EditHistoryEntry> redo)
    {
        public List<EditHistoryEntry> Undo { get; set; } = undo;
        public List<EditHistoryEntry> Redo { get; set; } = redo;
        public HistoryTransition? PendingTransition { get; set; }
    }
}
