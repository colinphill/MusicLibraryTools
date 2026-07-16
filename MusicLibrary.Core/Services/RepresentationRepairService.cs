using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public interface IRepresentationRepairService
{
    Task<RepresentationRepairPreview> PreviewAsync(
        IReadOnlyList<TrackRecord> records,
        LibraryConfiguration? configuration,
        CancellationToken ct = default);

    Task<RepresentationRepairApplyResult> ApplyAsync(
        IReadOnlyList<RepresentationRepairAction> actions,
        LibraryConfiguration? configuration,
        IProgress<RepresentationRepairProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Builds a non-mutating repair preview from cached representation metadata. Metadata copies use
/// the normal analysis repair model, so applying them still verifies the cached size/timestamp.
/// Derivation and organization actions retain source/destination snapshots and are applied only
/// through the explicit stale-checked workflow.
/// </summary>
public sealed class RepresentationRepairService : IRepresentationRepairService
{
    private readonly ILibraryOrganizer _organizer;
    private readonly IFfmpegRunner _ffmpeg;
    private readonly IFileMutationCoordinator _mutations;
    private readonly IReindexService? _reindex;

    public RepresentationRepairService(
        ILibraryOrganizer organizer,
        IFfmpegRunner? ffmpeg = null,
        IFileMutationCoordinator? mutations = null,
        IReindexService? reindex = null)
    {
        _organizer = organizer;
        _ffmpeg = ffmpeg ?? new FfmpegRunner();
        _mutations = mutations ?? FileMutationCoordinator.Shared;
        _reindex = reindex;
    }

    private static readonly LibraryRepresentation[] CanonicalOrder =
    [
        LibraryRepresentation.HighResolutionFlac,
        LibraryRepresentation.CdFlac,
        LibraryRepresentation.Purchased,
        LibraryRepresentation.GeneratedAac,
    ];

    private static readonly (TagFields Field, Func<TrackRecord, string?> Value)[] CopyFields =
    [
        (TagFields.Title, record => record.Title),
        (TagFields.Artist, record => record.Artist),
        (TagFields.AlbumArtist, record => record.AlbumArtist),
        (TagFields.Date, record => record.ReleaseDate),
        (TagFields.TotalTracks, record => record.TrackTotal?.ToString()),
        (TagFields.TotalDiscs, record => record.DiscTotal?.ToString()),
    ];

    public async Task<RepresentationRepairPreview> PreviewAsync(
        IReadOnlyList<TrackRecord> records,
        LibraryConfiguration? libraryConfiguration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var metadata = PreviewMetadataCopies(records, ct);
        var actions = new List<RepresentationRepairAction>();
        var warnings = new List<string>();

        IngestMusicConfiguration? configuration = null;
        if (libraryConfiguration is null)
        {
            warnings.Add("Derivation preview unavailable: load a library configuration first.");
        }
        else
        {
            try
            {
                configuration = IngestMusicConfiguration.FromLibraryConfiguration(libraryConfiguration);
                actions.AddRange(PreviewDerivations(records, configuration, ct));
                if (string.IsNullOrWhiteSpace(configuration.AacDestination))
                    warnings.Add(
                        "AAC derivation preview unavailable: assign an AAC fallback IndexTarget. " +
                        "Direct iTunes import is handled by the Ingest workflow.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Derivation preview unavailable: {ex.Message}");
            }
        }

        try
        {
            var representedPaths = records
                .Where(record => RepresentationAnalyzer.Classify(record) != LibraryRepresentation.Other)
                .Select(record => record.Path)
                .ToHashSet(PathComparer);
            var moves = await _organizer.PreviewMovesAsync(ct);
            actions.AddRange(moves
                .Where(move => representedPaths.Contains(move.Source))
                .Select(move => new RepresentationRepairAction(
                    RepresentationRepairKind.Organize,
                    move.Source,
                    move.Destination,
                    "Move this representation to its canonical artist/album/track path.",
                    move.ExpectedSource,
                    move.ExpectedDestination)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            warnings.Add($"Organization preview unavailable: {ex.Message}");
        }

        return new RepresentationRepairPreview(
            metadata,
            actions.OrderBy(action => action.Kind)
                .ThenBy(action => action.SourcePath, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            warnings);
    }

    public async Task<RepresentationRepairApplyResult> ApplyAsync(
        IReadOnlyList<RepresentationRepairAction> actions,
        LibraryConfiguration? libraryConfiguration,
        IProgress<RepresentationRepairProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
            return new([]);

        IngestMusicConfiguration? configuration = null;
        if (actions.Any(action => action.Kind != RepresentationRepairKind.Organize))
        {
            if (libraryConfiguration is null)
                throw new InvalidOperationException(
                    "Load a library configuration before applying representation derivations.");
            configuration = IngestMusicConfiguration.FromLibraryConfiguration(libraryConfiguration);
        }

        var results = new List<RepresentationRepairActionResult>(actions.Count);
        var invalid = ValidateActions(actions);
        var selectedCdSources = actions
            .Where(action => action.Kind == RepresentationRepairKind.DeriveCdFlac)
            .Select(action => action.SourcePath)
            .ToHashSet(PathComparer);
        var derivedCdBySource = new Dictionary<string, string>(PathComparer);
        int completed = 0;

        foreach (var action in actions
                     .Where(action => action.Kind != RepresentationRepairKind.Organize)
                     .OrderBy(action => action.Kind))
        {
            if (ct.IsCancellationRequested)
                return new(results, Cancelled: true);
            if (invalid.TryGetValue(action, out string? validationError))
            {
                results.Add(new(action, RepresentationRepairOutcome.Failed, validationError));
                progress?.Report(new(++completed, actions.Count, action.SourcePath, action.Kind));
                continue;
            }

            string inputPath = action.SourcePath;
            if (action.Kind == RepresentationRepairKind.DeriveAac &&
                selectedCdSources.Contains(action.SourcePath))
            {
                if (!derivedCdBySource.TryGetValue(action.SourcePath, out string? derivedInput))
                {
                    results.Add(new(action, RepresentationRepairOutcome.Failed,
                        "The paired CD FLAC derivation did not complete, so AAC derivation was not attempted."));
                    progress?.Report(new(++completed, actions.Count,
                        action.SourcePath, action.Kind));
                    continue;
                }
                inputPath = derivedInput;
            }

            RepresentationRepairActionResult result =
                await ApplyDerivationAsync(action, inputPath, configuration!, ct);
            results.Add(result);
            if (action.Kind == RepresentationRepairKind.DeriveCdFlac &&
                result.Outcome == RepresentationRepairOutcome.Applied)
            {
                derivedCdBySource[action.SourcePath] = action.DestinationPath;
            }
            progress?.Report(new(++completed, actions.Count, action.SourcePath, action.Kind));
        }

        var organizeActions = actions
            .Where(action => action.Kind == RepresentationRepairKind.Organize)
            .ToList();
        var validMoves = new List<(RepresentationRepairAction Action, PlannedMove Move)>();
        foreach (var action in organizeActions)
        {
            if (invalid.TryGetValue(action, out string? validationError))
            {
                results.Add(new(action, RepresentationRepairOutcome.Failed, validationError));
                progress?.Report(new(++completed, actions.Count, action.SourcePath, action.Kind));
            }
            else
            {
                validMoves.Add((action, new PlannedMove(
                    action.SourcePath,
                    action.DestinationPath,
                    action.ExpectedSource,
                    action.ExpectedDestination)));
            }
        }

        if (validMoves.Count > 0)
        {
            if (ct.IsCancellationRequested)
                return new(results, Cancelled: true);
            try
            {
                var moveProgress = new Progress<int>(done =>
                {
                    var action = validMoves[Math.Clamp(done - 1, 0, validMoves.Count - 1)].Action;
                    progress?.Report(new(completed + done, actions.Count,
                        action.SourcePath, action.Kind));
                });
                OrganizeResult organized = await _organizer.ApplyMovesAsync(
                    validMoves.Select(item => item.Move).ToList(), moveProgress, ct);
                var errors = organized.Errors.ToDictionary(
                    error => error.Source, error => error.Error, PathComparer);
                var cacheErrors = organized.CacheErrors.ToDictionary(
                    error => error.Source, error => error.Error, PathComparer);
                foreach (var (action, _) in validMoves)
                {
                    results.Add(errors.TryGetValue(action.SourcePath, out string? error)
                        ? new(action, RepresentationRepairOutcome.Failed, error)
                        : cacheErrors.TryGetValue(action.SourcePath, out string? cacheError)
                            ? new(action, RepresentationRepairOutcome.Applied,
                                $"Applied; cache refresh failed: {cacheError}")
                            : new(action, RepresentationRepairOutcome.Applied));
                }
                completed += validMoves.Count;
            }
            catch (OperationCanceledException)
            {
                return new(results, Cancelled: true);
            }
            catch (Exception ex)
            {
                results.AddRange(validMoves.Select(item =>
                    new RepresentationRepairActionResult(
                        item.Action, RepresentationRepairOutcome.Failed, ex.Message)));
            }
        }

        return new(results);
    }

    internal static AnalysisRepairPlan PreviewMetadataCopies(
        IReadOnlyList<TrackRecord> records,
        CancellationToken ct = default)
    {
        var repairs = new List<AnalysisTagRepair>();
        foreach (var album in records.GroupBy(AlbumKey))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var track in album.GroupBy(TrackKey))
            {
                var candidates = track
                    .Select(record => (Record: record, Role: RepresentationAnalyzer.Classify(record)))
                    .Where(item => item.Role != LibraryRepresentation.Other)
                    .GroupBy(item => item.Role)
                    .Where(group => group.Count() == 1)
                    .Select(group => group.Single())
                    .ToList();
                if (candidates.Count < 2)
                    continue;

                foreach (var (field, value) in CopyFields)
                {
                    var canonical = CanonicalOrder
                        .Select(role => candidates.FirstOrDefault(candidate => candidate.Role == role))
                        .FirstOrDefault(candidate => candidate.Record is not null &&
                            !string.IsNullOrWhiteSpace(value(candidate.Record)));
                    if (canonical.Record is null)
                        continue;
                    string after = value(canonical.Record)!.Trim();

                    foreach (var target in candidates.Where(candidate => candidate.Role != canonical.Role))
                    {
                        string? before = value(target.Record);
                        if (Normalize(before) == Normalize(after))
                            continue;
                        repairs.Add(new AnalysisTagRepair(
                            target.Record.Path,
                            field,
                            before,
                            after,
                            $"Copies {FieldName(field)} from the matched {Display(canonical.Role)} counterpart.",
                            target.Record.Length,
                            target.Record.LastWriteTime));
                    }
                }
            }
        }

        return new AnalysisRepairPlan("Copy representation metadata", repairs
            .GroupBy(repair => (repair.Path, repair.Field), PathFieldComparer.Instance)
            .Select(group => group.First())
            .OrderBy(repair => repair.Path, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(repair => repair.Field)
            .ToList());
    }

    internal static IReadOnlyList<RepresentationRepairAction> PreviewDerivations(
        IReadOnlyList<TrackRecord> records,
        IngestMusicConfiguration configuration,
        CancellationToken ct = default)
    {
        var actions = new List<RepresentationRepairAction>();
        var claimed = new HashSet<string>(records.Select(record => record.Path), PathComparer);
        foreach (var album in records.GroupBy(AlbumKey))
        foreach (var track in album.GroupBy(TrackKey))
        {
            ct.ThrowIfCancellationRequested();
            var byRole = track
                .Select(record => (Record: record, Role: RepresentationAnalyzer.Classify(record)))
                .Where(item => item.Role != LibraryRepresentation.Other)
                .GroupBy(item => item.Role)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Record);

            if (!byRole.ContainsKey(LibraryRepresentation.CdFlac) &&
                byRole.TryGetValue(LibraryRepresentation.HighResolutionFlac, out var highResolution))
            {
                string destination = ClaimCanonical(configuration.PairedCdDestination, highResolution,
                    ".flac", configuration, claimed);
                actions.Add(new(RepresentationRepairKind.DeriveCdFlac, highResolution.Path, destination,
                    "Downsample the high-resolution FLAC to a paired CD-quality FLAC, then copy normalized metadata.",
                    SourceSnapshot(highResolution),
                    DestinationSnapshot(destination)));
            }

            bool hasPortableCounterpart = byRole.ContainsKey(LibraryRepresentation.GeneratedAac) ||
                byRole.ContainsKey(LibraryRepresentation.Purchased);
            if (!hasPortableCounterpart &&
                !string.IsNullOrWhiteSpace(configuration.AacDestination))
            {
                TrackRecord? source = byRole.GetValueOrDefault(LibraryRepresentation.CdFlac) ??
                    byRole.GetValueOrDefault(LibraryRepresentation.HighResolutionFlac);
                if (source is not null)
                {
                    string destination = ClaimCanonical(configuration.AacDestination, source,
                        ".m4a", configuration, claimed);
                    actions.Add(new(RepresentationRepairKind.DeriveAac, source.Path, destination,
                        $"Encode AAC at {configuration.AacBitrateKbps:N0} kbit/s with " +
                        $"{configuration.AacEncoder}, then copy normalized metadata.",
                        SourceSnapshot(source),
                        DestinationSnapshot(destination)));
                }
            }
        }
        return actions;
    }

    private async Task<RepresentationRepairActionResult> ApplyDerivationAsync(
        RepresentationRepairAction action,
        string inputPath,
        IngestMusicConfiguration configuration,
        CancellationToken ct)
    {
        string destination = Path.GetFullPath(action.DestinationPath);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Destination has no parent directory: {destination}");
        string extension = Path.GetExtension(destination);
        string stage = Path.Combine(directory,
            $".{Path.GetFileNameWithoutExtension(destination)}.representation-{Guid.NewGuid():N}{extension}");

        using IDisposable lease = await _mutations.AcquireAsync(
            [action.SourcePath, inputPath, destination], ct);
        try
        {
            string? freshnessError = ValidateAction(action);
            if (freshnessError is not null)
                return new(action, RepresentationRepairOutcome.Failed, freshnessError);

            Directory.CreateDirectory(directory);
            if (action.Kind == RepresentationRepairKind.DeriveCdFlac)
            {
                await _ffmpeg.DeriveCdFlacAsync(
                    configuration.FfmpegPath, action.SourcePath, stage, ct);
            }
            else
            {
                await _ffmpeg.EncodeAacAsync(
                    configuration.FfmpegPath,
                    configuration.AacEncoder,
                    configuration.AacBitrateKbps,
                    inputPath,
                    stage,
                    ct);
            }

            CopyMetadataAndArtwork(action.SourcePath, stage);
            ValidateDerived(stage, action.Kind);

            freshnessError = ValidateAction(action);
            if (freshnessError is not null)
                return new(action, RepresentationRepairOutcome.Failed, freshnessError);

            File.Move(stage, destination);
            if (_reindex is not null)
            {
                try
                {
                    await _reindex.ReindexFileAsync(destination, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    return new(action, RepresentationRepairOutcome.Applied,
                        $"Applied; cache refresh failed: {ex.Message}");
                }
            }
            return new(action, RepresentationRepairOutcome.Applied);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(action, RepresentationRepairOutcome.Failed, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(stage))
                    File.Delete(stage);
            }
            catch { }
        }
    }

    private static Dictionary<RepresentationRepairAction, string> ValidateActions(
        IReadOnlyList<RepresentationRepairAction> actions)
    {
        var errors = new Dictionary<RepresentationRepairAction, string>();
        foreach (var action in actions)
        {
            string? error = ValidateAction(action);
            if (error is not null)
                errors[action] = error;
        }
        return errors;
    }

    private static string? ValidateAction(RepresentationRepairAction action)
    {
        try
        {
            string source = Path.GetFullPath(action.SourcePath);
            string destination = Path.GetFullPath(action.DestinationPath);
            if (PathComparer.Equals(source, destination))
                return "Source and destination are the same path.";

            OperationPathSnapshot actualSource = Capture(source);
            if (!Matches(action.ExpectedSource, actualSource))
                return $"Source changed since preview; preview again: {source}";
            if (!actualSource.Exists || actualSource.IsDirectory)
                return $"Source file is unavailable: {source}";

            OperationPathSnapshot actualDestination = Capture(destination);
            if (!Matches(action.ExpectedDestination, actualDestination))
                return $"Destination changed since preview; preview again: {destination}";
            if (actualDestination.Exists)
                return $"Destination already exists: {destination}";
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static bool Matches(
        OperationPathSnapshot? expected,
        OperationPathSnapshot actual) =>
        expected is null ||
        expected.Exists == actual.Exists &&
        expected.IsDirectory == actual.IsDirectory &&
        (!expected.Exists ||
            expected.Length == actual.Length &&
            expected.LastWriteTimeUtc == actual.LastWriteTimeUtc);

    private static OperationPathSnapshot SourceSnapshot(TrackRecord record) =>
        new(true, false, record.Length, record.LastWriteTime)
        {
            Path = Path.GetFullPath(record.Path),
        };

    private static OperationPathSnapshot DestinationSnapshot(string path) => Capture(path);

    private static OperationPathSnapshot Capture(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            var info = new FileInfo(fullPath);
            return new(true, false, info.Length, info.LastWriteTimeUtc) { Path = fullPath };
        }
        if (Directory.Exists(fullPath))
        {
            var info = new DirectoryInfo(fullPath);
            return new(true, true, 0, info.LastWriteTimeUtc) { Path = fullPath };
        }
        return OperationPathSnapshot.Missing(fullPath);
    }

    private static void CopyMetadataAndArtwork(string sourcePath, string destinationPath)
    {
        IMediaFile source = MediaFile.GetFile(sourcePath, readOnly: true);
        IMediaFile destination = MediaFile.GetFile(destinationPath);
        IMetadataWriter writer = destination as IMetadataWriter
            ?? destination.Tags.FirstOrDefault() as IMetadataWriter
            ?? throw new InvalidDataException(
                $"Generated file's tag format is not writable: {destinationPath}");

        foreach (var field in source.Tags.SelectMany(tag => tag.GetKnownMetadata())
                     .GroupBy(item => item.Key)
                     .Select(group => group.First()))
        {
            try { writer.SetField(field.Key, field.Value); }
            catch (ArgumentException) { }
        }

        IArtworkWriter? artworkWriter = destination as IArtworkWriter
            ?? destination.Tags.FirstOrDefault() as IArtworkWriter;
        if (artworkWriter is not null)
        {
            artworkWriter.SetImages(source.Tags.SelectMany(tag => tag.GetImageMetadata())
                .Select(image => new ArtworkImage(
                    ParsePictureType(image.Category),
                    NormalizeMime(image.ImageType),
                    image.Description ?? "",
                    image.Data))
                .ToList());
        }
        destination.SaveTags();
    }

    private static void ValidateDerived(string path, RepresentationRepairKind kind)
    {
        IMediaFile media = MediaFile.GetFile(path, readOnly: true);
        var codec = media.Codecs.First();
        if (codec.Channels != 2)
            throw new InvalidDataException($"Generated file is not stereo: {path}");
        if (kind == RepresentationRepairKind.DeriveCdFlac &&
            (codec.CodecType != CodecType.Lossless ||
             codec.Samplerate != 44_100 ||
             codec.BitsPerSample != 16))
        {
            throw new InvalidDataException(
                $"Generated CD FLAC has an unexpected audio format: {path}");
        }
        if (kind == RepresentationRepairKind.DeriveAac &&
            (codec.CodecType != CodecType.Lossy || codec.Samplerate != 44_100))
        {
            throw new InvalidDataException(
                $"Generated AAC has an unexpected audio format: {path}");
        }
    }

    private static ID3v2Util.APICType ParsePictureType(string? value) =>
        Enum.TryParse(value, true, out ID3v2Util.APICType type)
            ? type
            : ID3v2Util.APICType.FrontCover;

    private static string NormalizeMime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "image/jpeg";
        return value.Contains('/')
            ? value
            : $"image/{value.TrimStart('.').ToLowerInvariant()}";
    }

    private static string ClaimCanonical(string root, TrackRecord record, string extension,
        IngestMusicConfiguration configuration, HashSet<string> claimed)
    {
        string artist = record.EffectiveAlbumArtist.LimitLength(configuration.LengthLimit).FixPath();
        string album = (record.StrippedAlbum ?? record.Album ?? "Unknown Album")
            .FormatDisc(configuration.LengthLimit, configuration.DiscNumLengthLimit).FixPath();
        string title = (record.Title ?? "Untitled").LimitLength(configuration.LengthLimit).FixPath();
        string name = (record.TrackNumber is int number ? $"{number:D2} " : "") + title;
        string basePath = Path.Combine(root, artist, album, name);
        string destination = basePath + extension;
        int suffix = 2;
        while (!claimed.Add(destination))
            destination = basePath + $"_{suffix++}" + extension;
        return destination;
    }

    private static string AlbumKey(TrackRecord record) =>
        Normalize(record.EffectiveAlbumArtist) + "\0" + Normalize(record.StrippedAlbum ?? record.Album);

    private static string TrackKey(TrackRecord record) => record.TrackNumber is int track
        ? $"{record.DiscNumber ?? 1:D4}\0{track:D6}"
        : $"title\0{Normalize(record.Title)}";

    private static string Normalize(string? value) => string.Join(' ', (value ?? "").Trim()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string Display(LibraryRepresentation role) => role switch
    {
        LibraryRepresentation.HighResolutionFlac => "high-resolution FLAC",
        LibraryRepresentation.CdFlac => "CD FLAC",
        LibraryRepresentation.Purchased => "purchased audio",
        LibraryRepresentation.GeneratedAac => "generated AAC",
        _ => "other",
    };

    private static string FieldName(TagFields field) => field switch
    {
        TagFields.AlbumArtist => "album artist",
        TagFields.Date => "release date",
        TagFields.TotalTracks => "track total",
        TagFields.TotalDiscs => "disc total",
        _ => field.ToString().ToLowerInvariant(),
    };

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class PathFieldComparer : IEqualityComparer<(string Path, TagFields Field)>
    {
        public static PathFieldComparer Instance { get; } = new();
        public bool Equals((string Path, TagFields Field) x, (string Path, TagFields Field) y) =>
            x.Field == y.Field && PathComparer.Equals(x.Path, y.Path);
        public int GetHashCode((string Path, TagFields Field) value) =>
            HashCode.Combine(PathComparer.GetHashCode(value.Path), value.Field);
    }
}
