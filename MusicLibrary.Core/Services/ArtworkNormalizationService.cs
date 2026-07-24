using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using iTunes.Binary;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;
using SkiaSharp;

namespace MusicLibrary.Core.Services;

public sealed record ArtworkNormalizationRequest(
    string PlaylistName,
    string? ItunesLibraryPath = null,
    long MaximumBytes = 225 * 1024,
    int MaximumDimension = 600,
    int JpegQuality = 75);

public sealed record ArtworkCharacteristics(
    string MimeType,
    int Width,
    int Height,
    long Size,
    string Sha256);

/// <summary>One media-file mutation, including the exact encoded bytes reviewed in preview.</summary>
public sealed record ArtworkNormalizationItem(
    IReadOnlyList<int> TrackIds,
    string Path,
    OperationPathSnapshot FileSnapshot,
    ArtworkCharacteristics Current,
    ArtworkCharacteristics Proposed,
    ImmutableArray<byte> EncodedJpeg);

public sealed record ArtworkNormalizationPlan(
    ArtworkNormalizationRequest Request,
    string LibraryPath,
    OperationPathSnapshot LibrarySnapshot,
    string LibrarySha256,
    IReadOnlyList<ArtworkNormalizationItem> Items,
    int ScannedTrackCount,
    int ArtworkTrackCount,
    int UnchangedCount,
    IReadOnlyList<OperationIssue> Issues,
    string RecoveryRoot,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>
    /// Identifies the reviewed library policy. Compatibility/headless plans created without an
    /// application configuration leave both values unset.
    /// </summary>
    public Guid? LibraryId { get; init; }
    public string? PolicyFingerprint { get; init; }

    public bool CanApply => Issues.All(issue => issue.Severity != OperationIssueSeverity.Blocker);
}

public sealed record ArtworkNormalizationResult(
    int UpdatedFileCount,
    int UpdatedTrackCount,
    string? JournalPath,
    IReadOnlyList<OperationIssue> Issues)
{
    public IReadOnlyList<string> UpdatedPaths { get; init; } = [];
    public string? CacheError { get; init; }
}

public interface IArtworkNormalizationService
{
    Task<ArtworkNormalizationPlan> PreviewAsync(
        ArtworkNormalizationRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<ArtworkNormalizationResult> ApplyAsync(
        ArtworkNormalizationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Normalizes the single embedded image on tracks in an iTunes playlist. Preview is read-only and
/// owns the encoding decision; apply writes those exact bytes after revalidating the complete plan.
/// Every original file is retained in a durable recovery tree until normal retention cleanup.
/// </summary>
public sealed class ArtworkNormalizationService : IArtworkNormalizationService
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly IFileMutationCoordinator _mutations;
    private readonly IReindexService? _reindex;
    private readonly IAppSettings? _settings;

    public ArtworkNormalizationService(IFileMutationCoordinator? mutations = null)
    {
        _mutations = mutations ?? FileMutationCoordinator.Shared;
    }

    public ArtworkNormalizationService(
        IReindexService reindex,
        IFileMutationCoordinator? mutations = null)
    {
        ArgumentNullException.ThrowIfNull(reindex);
        _reindex = reindex;
        _mutations = mutations ?? FileMutationCoordinator.Shared;
    }

    /// <summary>Policy-aware application constructor. The shorter overloads retain headless use.</summary>
    public ArtworkNormalizationService(
        IAppSettings settings,
        IReindexService reindex,
        IFileMutationCoordinator mutations)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(reindex);
        ArgumentNullException.ThrowIfNull(mutations);
        _settings = settings;
        _reindex = reindex;
        _mutations = mutations;
    }

    public Task<ArtworkNormalizationPlan> PreviewAsync(
        ArtworkNormalizationRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        LibraryConfiguration? configuration = CurrentConfiguration(required: _settings is not null);
        return Task.Run(() => Preview(request, configuration, progress, ct), ct);
    }

    public async Task<ArtworkNormalizationResult> ApplyAsync(
        ArtworkNormalizationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException("The artwork plan contains blocking issues.");

        string[] paths = [plan.LibraryPath, .. plan.Items.Select(item => item.Path)];
        progress?.Report(new(OperationPhase.Validating,
            Message: "Waiting to validate the reviewed artwork plan"));
        using IDisposable lease = await _mutations.AcquireAsync(paths, ct).ConfigureAwait(false);
        ValidateCurrentPolicy(plan);
        ApplyOutcome applied = await Task.Run(() => Apply(plan, progress, ct), CancellationToken.None)
            .ConfigureAwait(false);

        ArtworkNormalizationResult result = applied.Result;
        if (_reindex is not null && applied.SavedFiles.Count > 0)
        {
            try
            {
                progress?.Report(new(OperationPhase.Applying, plan.Items.Count, plan.Items.Count,
                    Message: "Refreshing normalized artwork in the metadata cache"));
                await _reindex.ReindexFilesAsync(applied.SavedFiles, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = result with { CacheError = ex.Message };
            }
        }
        progress?.Report(new(OperationPhase.Completed, plan.Items.Count, plan.Items.Count,
            Message: plan.Items.Count == 0
                ? "No artwork changes are required"
                : result.CacheError is null
                ? $"Normalized {plan.Items.Count:N0} media file(s)"
                : $"Normalized {plan.Items.Count:N0} media file(s); cache refresh failed"));
        return result;
    }

    private sealed record ApplyOutcome(
        ArtworkNormalizationResult Result,
        IReadOnlyList<(string Path, IMediaFile File)> SavedFiles);

    private static ArtworkNormalizationPlan Preview(
        ArtworkNormalizationRequest request,
        LibraryConfiguration? configuration,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        string libraryPath = ItlFileEditor.ResolveLibraryPath(
            request.ItunesLibraryPath ?? configuration?.ItunesLibraryPath);
        Guid? libraryId = configuration?.LibraryId;
        string? policyFingerprint = configuration?.PolicySnapshot.Fingerprint;
        OperationPathSnapshot librarySnapshot = CaptureSnapshot(libraryPath);
        string libraryHash = HashFile(libraryPath, ct);
        var issues = new List<OperationIssue>();
        if (configuration is not null &&
            CatalogPolicyError(configuration, libraryPath) is { } catalogError)
        {
            issues.Add(new("catalog-policy-denied", OperationIssueSeverity.Blocker,
                catalogError, libraryPath));
            return Stamp(EmptyPlan(request, libraryPath, librarySnapshot, libraryHash, issues),
                libraryId, policyFingerprint);
        }
        if (configuration is not null && !HasArtworkWriteTarget(configuration))
        {
            issues.Add(new("artwork-policy-denied", OperationIssueSeverity.Blocker,
                "The active library policy has no root that permits embedded-artwork writes."));
            return Stamp(EmptyPlan(request, libraryPath, librarySnapshot, libraryHash, issues),
                libraryId, policyFingerprint);
        }

        progress?.Report(new(OperationPhase.LoadingLibrary, Message: "Loading iTunes library"));
        ItlDocument document = ItlDocument.Load(libraryPath);
        ItlRecord[] playlists = [.. document.FindPlaylists(
            request.PlaylistName, StringComparison.OrdinalIgnoreCase)];
        if (playlists.Length != 1)
        {
            issues.Add(new("playlist-ambiguous", OperationIssueSeverity.Blocker,
                $"Expected one playlist named '{request.PlaylistName}', found {playlists.Length}.",
                libraryPath));
            return Stamp(EmptyPlan(request, libraryPath, librarySnapshot, libraryHash, issues),
                libraryId, policyFingerprint);
        }

        Dictionary<int, ItlRecord> tracksById = document.Tracks.ToDictionary(ItlDocument.TrackIdOf);
        var paths = new Dictionary<string, List<int>>(PathComparer);
        int scannedTracks = 0;
        foreach (int trackId in playlists[0].Entries.Select(entry => entry.TrackId).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (!tracksById.TryGetValue(trackId, out ItlRecord? track) || track.GetHasVideo())
                continue;
            string? path = ItlLocation.ToLocalPath(track.GetString(ItlDataType.Location));
            if (string.IsNullOrWhiteSpace(path))
            {
                issues.Add(new("missing-location", OperationIssueSeverity.Warning,
                    $"Track {trackId} has no local file location."));
                continue;
            }
            path = Path.GetFullPath(path);
            scannedTracks++;
            if (!paths.TryGetValue(path, out List<int>? ids))
                paths[path] = ids = [];
            ids.Add(trackId);
        }

        var items = new List<ArtworkNormalizationItem>();
        int artworkTracks = 0;
        int unchanged = 0;
        int completed = 0;
        foreach ((string path, List<int> trackIds) in paths)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(OperationPhase.Planning, completed++, paths.Count, path,
                "Inspecting and encoding embedded artwork"));
            try
            {
                if (configuration is not null &&
                    ArtworkPolicyError(configuration, path) is { } policyError)
                {
                    issues.Add(new("artwork-policy-denied", OperationIssueSeverity.Blocker,
                        policyError, path));
                    continue;
                }
                OperationPathSnapshot snapshot = CaptureSnapshot(path);
                if (!snapshot.Exists || snapshot.IsDirectory)
                    throw new FileNotFoundException("The media file does not exist.", path);
                IMediaFile mediaFile = MediaFile.GetFile(path, readOnly: true);
                IMetadataImage[] images = [.. mediaFile.Tags.SelectMany(tag => tag.GetImageMetadata())];
                if (images.Length == 0)
                {
                    issues.Add(new("artwork-missing", OperationIssueSeverity.Warning,
                        "No embedded artwork was found.", path));
                    continue;
                }
                if (images.Length != 1)
                {
                    issues.Add(new("artwork-count", OperationIssueSeverity.Warning,
                        $"Expected one embedded image, found {images.Length}.", path));
                    continue;
                }

                artworkTracks += trackIds.Count;
                IMetadataImage source = images[0];
                using ArtworkImageProcessor.DecodedArtwork decoded =
                    ArtworkImageProcessor.Decode(source.Data);
                string detectedMimeType =
                    ArtworkImageProcessor.FormatDetails(decoded.Format).MimeType;
                ArtworkCharacteristics current = Describe(
                    detectedMimeType,
                    decoded.Bitmap.Width,
                    decoded.Bitmap.Height,
                    source.Data);
                bool isJpeg =
                    decoded.Format == SKEncodedImageFormat.Jpeg;
                bool needsChange = decoded.Bitmap.Width > request.MaximumDimension ||
                    decoded.Bitmap.Height > request.MaximumDimension || !isJpeg ||
                    source.Data.LongLength > request.MaximumBytes;
                if (!needsChange)
                {
                    unchanged += trackIds.Count;
                    continue;
                }

                if (ResolveWriter(mediaFile) is null)
                {
                    issues.Add(new("artwork-read-only", OperationIssueSeverity.Warning,
                        "This media format does not support embedded-artwork writes.", path));
                    continue;
                }
                using SKBitmap? oriented = ArtworkImageProcessor.NormalizeOrientation(
                    decoded.Bitmap, decoded.Origin);
                SKBitmap orientedImage = oriented ?? decoded.Bitmap;
                using SKBitmap? resized = ArtworkImageProcessor.ResizeToFit(
                    orientedImage, request.MaximumDimension);
                SKBitmap outputImage = resized ?? orientedImage;
                byte[] encoded = ArtworkImageProcessor.Encode(
                    outputImage,
                    SKEncodedImageFormat.Jpeg,
                    request.JpegQuality);
                if (encoded.LongLength > request.MaximumBytes)
                {
                    issues.Add(new("artwork-still-large", OperationIssueSeverity.Warning,
                        $"The normalized image is {encoded.LongLength:N0} bytes, above the " +
                        $"{request.MaximumBytes:N0}-byte limit.", path));
                    continue;
                }

                items.Add(new(trackIds.ToArray(), path, snapshot, current,
                    Describe(
                        "image/jpeg",
                        outputImage.Width,
                        outputImage.Height,
                        encoded),
                    encoded.ToImmutableArray()));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                issues.Add(new("media-read-failed", OperationIssueSeverity.Warning,
                    exception.Message, path));
            }
        }

        string recoveryRoot = Path.Combine(libraryPath + ".FixArtwork-quarantine",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        progress?.Report(new(OperationPhase.Completed, paths.Count, paths.Count,
            Message: $"Planned {items.Count:N0} artwork replacement(s)"));
        return Stamp(new(request, libraryPath, librarySnapshot, libraryHash, items,
            scannedTracks, artworkTracks, unchanged, issues, recoveryRoot,
            DateTimeOffset.UtcNow), libraryId, policyFingerprint);
    }

    private static ApplyOutcome Apply(
        ArtworkNormalizationPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        // The in-process lease is held by the caller; revalidate after acquiring it and before the
        // first write so no partially stale plan can begin.
        ValidatePlan(plan, ct);
        if (plan.Items.Count == 0)
            return new(new(0, 0, null, plan.Issues), []);

        ItlFileEditor.EnsureItunesIsClosed();
        ItlDocument document = ItlDocument.Load(plan.LibraryPath);
        Dictionary<int, ItlRecord> tracks = document.Tracks.ToDictionary(ItlDocument.TrackIdOf);
        Directory.CreateDirectory(plan.RecoveryRoot);
        string journalPath = Path.Combine(plan.RecoveryRoot, "journal.tsv");
        using var journalStream = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 4096, FileOptions.WriteThrough);
        using var journal = new StreamWriter(journalStream, new UTF8Encoding(false));
        string operationId = Guid.NewGuid().ToString("N");
        WriteJournal(journal, journalStream, $"BEGIN\t{operationId}");
        foreach (ArtworkNormalizationItem item in plan.Items)
            WriteJournal(journal, journalStream, $"PLAN_QUARANTINE\tARTWORK\t{item.Path}\t{BackupPath(plan.RecoveryRoot, item.Path)}");
        WriteJournal(journal, journalStream,
            $"PLAN_QUARANTINE\tITL\t{plan.LibraryPath}\t{BackupPath(plan.RecoveryRoot, plan.LibraryPath)}");

        var modified = new List<(ArtworkNormalizationItem Item, string Backup)>();
        var savedFiles = new List<(string Path, IMediaFile File)>();
        string? libraryBackup = null;
        try
        {
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                ArtworkNormalizationItem item = plan.Items[index];
                progress?.Report(new(OperationPhase.Applying, index, plan.Items.Count, item.Path,
                    "Replacing embedded artwork"));
                string backup = BackupPath(plan.RecoveryRoot, item.Path);
                CopyDurably(item.Path, backup);
                WriteJournal(journal, journalStream,
                    $"QUARANTINE\tARTWORK\t{item.Path}\t{backup}");
                modified.Add((item, backup));

                IMediaFile mediaFile = MediaFile.GetFile(item.Path);
                IArtworkWriter writer = ResolveWriter(mediaFile) ?? throw new NotSupportedException(
                    "This media format no longer supports embedded-artwork writes.");
                writer.SetImages([
                    new ArtworkImage(ID3v2Util.APICType.FrontCover, "image/jpeg", string.Empty,
                        item.EncodedJpeg.ToArray()),
                ]);
                mediaFile.SaveTags();
                VerifyWrittenArtwork(item);
                savedFiles.Add((item.Path, mediaFile));

                FileInfo file = new(item.Path);
                foreach (int trackId in item.TrackIds)
                {
                    if (!tracks.TryGetValue(trackId, out ItlRecord? track))
                        throw new InvalidDataException($"Track {trackId} disappeared from the iTunes library.");
                    track.SetArtworkCount(1);
                    track.SetSize((ulong)file.Length);
                    track.SetDateModified(file.LastWriteTimeUtc);
                }
                WriteJournal(journal, journalStream, $"INSTALL\tARTWORK\t{item.Path}");
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new(OperationPhase.Applying, plan.Items.Count, plan.Items.Count,
                plan.LibraryPath, "Saving iTunes artwork caches"));
            libraryBackup = BackupPath(plan.RecoveryRoot, plan.LibraryPath);
            CopyDurably(plan.LibraryPath, libraryBackup);
            WriteJournal(journal, journalStream,
                $"QUARANTINE\tITL\t{plan.LibraryPath}\t{libraryBackup}");
            ItlFileEditor.SaveValidated(document, plan.LibraryPath);
            _ = ItlDocument.Load(plan.LibraryPath);
            WriteJournal(journal, journalStream, $"INSTALL\tITL\t{plan.LibraryPath}");
            WriteJournal(journal, journalStream, $"COMMIT\t{operationId}");
            return new(
                new ArtworkNormalizationResult(
                    plan.Items.Count,
                    plan.Items.Sum(item => item.TrackIds.Count),
                    journalPath,
                    plan.Issues)
                {
                    UpdatedPaths = plan.Items.Select(item => item.Path).ToArray(),
                },
                savedFiles);
        }
        catch (Exception applyError)
        {
            progress?.Report(new(OperationPhase.RollingBack,
                Message: "Rolling back artwork normalization"));
            var rollbackErrors = new List<Exception>();
            if (libraryBackup is not null)
            {
                try { RestoreAtomically(libraryBackup, plan.LibraryPath, plan.LibrarySnapshot); }
                catch (Exception error) { rollbackErrors.Add(error); }
            }
            foreach ((ArtworkNormalizationItem item, string backup) in modified.AsEnumerable().Reverse())
            {
                try { RestoreAtomically(backup, item.Path, item.FileSnapshot); }
                catch (Exception error) { rollbackErrors.Add(error); }
            }
            try
            {
                WriteJournal(journal, journalStream, rollbackErrors.Count == 0
                    ? $"ROLLBACK\t{operationId}"
                    : $"ROLLBACK_FAILED\t{operationId}");
            }
            catch (Exception error) { rollbackErrors.Add(error); }
            if (rollbackErrors.Count > 0)
                throw new AggregateException(
                    "Artwork normalization failed and rollback was incomplete.",
                    [applyError, .. rollbackErrors]);
            throw;
        }
    }

    private LibraryConfiguration? CurrentConfiguration(bool required)
    {
        LibraryConfiguration? configuration = _settings?.GetSnapshot().Configuration;
        if (required && configuration is null)
            throw new InvalidOperationException(
                "Load a library configuration before normalizing artwork.");
        return configuration;
    }

    private void ValidateCurrentPolicy(ArtworkNormalizationPlan plan)
    {
        if (_settings is null)
        {
            if (plan.LibraryId is not null || plan.PolicyFingerprint is not null)
                throw new InvalidOperationException(
                    "The reviewed library policy is unavailable. Preview the artwork operation again.");
            return;
        }

        if (plan.LibraryId is not Guid libraryId ||
            string.IsNullOrWhiteSpace(plan.PolicyFingerprint))
            throw new InvalidOperationException(
                "The artwork plan does not identify its reviewed library policy. Preview again.");

        LibraryConfiguration configuration = CurrentConfiguration(required: true)!;
        if (configuration.LibraryId != libraryId ||
            !StringComparer.Ordinal.Equals(
                configuration.PolicySnapshot.Fingerprint, plan.PolicyFingerprint))
            throw new InvalidOperationException(
                "The library policy changed after preview. Preview artwork normalization again.");

        if (CatalogPolicyError(configuration, plan.LibraryPath) is { } catalogError)
            throw new InvalidOperationException(catalogError);
        if (!HasArtworkWriteTarget(configuration))
            throw new InvalidOperationException(
                "The active library policy has no root that permits embedded-artwork writes.");
        foreach (ArtworkNormalizationItem item in plan.Items)
            if (ArtworkPolicyError(configuration, item.Path) is { } policyError)
                throw new InvalidOperationException(policyError);
    }

    private static string? CatalogPolicyError(
        LibraryConfiguration configuration,
        string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(configuration.ItunesLibraryPath))
            return "The active library policy does not configure a media catalog.";
        return PathComparer.Equals(
            Path.GetFullPath(configuration.ItunesLibraryPath), Path.GetFullPath(libraryPath))
            ? null
            : "The reviewed iTunes library is not the catalog configured by the active library policy.";
    }

    private static bool HasArtworkWriteTarget(LibraryConfiguration configuration) =>
        configuration.IndexLocations.Any(location =>
            location.Permissions.HasFlag(LibraryRootPermissions.WriteArtwork) &&
            WritesEmbedded(configuration.GetEffectiveProfile(location).Artwork));

    private static string? ArtworkPolicyError(
        LibraryConfiguration configuration,
        string path)
    {
        LibraryIndexLocation[] roots = configuration.IndexLocations.ToArray();
        if (!LibraryRootPermissionPolicy.Allows(
                path, roots, LibraryRootPermissions.WriteArtwork))
            return $"The effective library root policy does not permit artwork writes to '{path}'.";

        LibraryIndexLocation? root = LibraryRootPermissionPolicy.MostSpecific(path, roots);
        if (root is null || !WritesEmbedded(configuration.GetEffectiveProfile(root).Artwork))
            return $"The effective artwork policy does not permit embedded artwork at '{path}'.";
        return null;
    }

    private static bool WritesEmbedded(LibraryArtworkPolicy policy) =>
        policy.Storage is LibraryArtworkStorage.Embedded or LibraryArtworkStorage.Both;

    private static ArtworkNormalizationPlan Stamp(
        ArtworkNormalizationPlan plan,
        Guid? libraryId,
        string? policyFingerprint) => plan with
    {
        LibraryId = libraryId,
        PolicyFingerprint = policyFingerprint,
    };

    private static void ValidateRequest(ArtworkNormalizationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlaylistName);
        if (request.MaximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaximumBytes must be positive.");
        if (request.MaximumDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaximumDimension must be positive.");
        if (request.JpegQuality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(request), "JpegQuality must be between 1 and 100.");
    }

    private static void ValidatePlan(ArtworkNormalizationPlan plan, CancellationToken ct)
    {
        ValidateSnapshot(plan.LibraryPath, plan.LibrarySnapshot);
        if (!StringComparer.Ordinal.Equals(HashFile(plan.LibraryPath, ct), plan.LibrarySha256))
            throw new InvalidOperationException("The iTunes library changed after preview. Preview again.");
        foreach (ArtworkNormalizationItem item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            ValidateSnapshot(item.Path, item.FileSnapshot);
            IMediaFile mediaFile = MediaFile.GetFile(item.Path, readOnly: true);
            IMetadataImage[] images = [.. mediaFile.Tags.SelectMany(tag => tag.GetImageMetadata())];
            if (images.Length != 1 ||
                !StringComparer.Ordinal.Equals(HashBytes(images[0].Data), item.Current.Sha256))
                throw new InvalidOperationException(
                    $"Embedded artwork changed after preview: {item.Path}. Preview again.");
            if (ResolveWriter(mediaFile) is null)
                throw new InvalidOperationException(
                    $"Artwork is no longer writable: {item.Path}. Preview again.");
        }
    }

    private static void VerifyWrittenArtwork(ArtworkNormalizationItem item)
    {
        IMediaFile verification = MediaFile.GetFile(item.Path, readOnly: true);
        IMetadataImage[] images = [.. verification.Tags.SelectMany(tag => tag.GetImageMetadata())];
        if (images.Length != 1)
            throw new InvalidDataException("Artwork verification did not find exactly one embedded image.");
        IMetadataImage image = images[0];
        if (image.Data.LongLength != item.Proposed.Size ||
            !StringComparer.Ordinal.Equals(HashBytes(image.Data), item.Proposed.Sha256))
            throw new InvalidDataException("Saved artwork bytes differ from the reviewed image.");
        using ArtworkImageProcessor.DecodedArtwork decoded =
            ArtworkImageProcessor.Decode(image.Data);
        if (decoded.Bitmap.Width != item.Proposed.Width ||
            decoded.Bitmap.Height != item.Proposed.Height)
            throw new InvalidDataException("Saved artwork dimensions differ from the reviewed image.");
    }

    private static ArtworkCharacteristics Describe(
        string mimeType, int width, int height, byte[] data) =>
        new(mimeType, width, height, data.LongLength, HashBytes(data));

    private static IArtworkWriter? ResolveWriter(IMediaFile file) =>
        file as IArtworkWriter ?? file.Tags.FirstOrDefault() as IArtworkWriter;

    private static OperationPathSnapshot CaptureSnapshot(string path)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            return new(true, true, 0, directory.LastWriteTimeUtc) { Path = path };
        }
        if (!File.Exists(path))
            return OperationPathSnapshot.Missing(path);
        var file = new FileInfo(path);
        return new(true, false, file.Length, file.LastWriteTimeUtc) { Path = path };
    }

    private static void ValidateSnapshot(string path, OperationPathSnapshot expected)
    {
        OperationPathSnapshot current = CaptureSnapshot(path);
        if (current.Exists != expected.Exists || current.IsDirectory != expected.IsDirectory ||
            current.Length != expected.Length || current.LastWriteTimeUtc != expected.LastWriteTimeUtc)
            throw new InvalidOperationException($"Path changed after preview: {path}. Preview again.");
    }

    private static string HashFile(string path, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        byte[] buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static string HashBytes(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string BackupPath(string recoveryRoot, string originalPath)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(originalPath).ToUpperInvariant())));
        return Path.Combine(recoveryRoot, "originals", key, Path.GetFileName(originalPath));
    }

    private static void CopyDurably(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
        using var stream = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Read, 1, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static void RestoreAtomically(
        string backup, string destination, OperationPathSnapshot snapshot)
    {
        string directory = Path.GetDirectoryName(destination)!;
        string temporary = Path.Combine(directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.restore.tmp");
        try
        {
            File.Copy(backup, temporary, overwrite: false);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Read,
                       FileShare.Read, 1, FileOptions.WriteThrough))
                stream.Flush(flushToDisk: true);
            File.Replace(temporary, destination, null, ignoreMetadataErrors: true);
            File.SetLastWriteTimeUtc(destination, snapshot.LastWriteTimeUtc);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteJournal(StreamWriter writer, FileStream stream, string line)
    {
        writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static ArtworkNormalizationPlan EmptyPlan(
        ArtworkNormalizationRequest request,
        string libraryPath,
        OperationPathSnapshot librarySnapshot,
        string libraryHash,
        IReadOnlyList<OperationIssue> issues) =>
        new(request, libraryPath, librarySnapshot, libraryHash, [], 0, 0, 0, issues,
            Path.Combine(libraryPath + ".FixArtwork-quarantine",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")), DateTimeOffset.UtcNow);
}
