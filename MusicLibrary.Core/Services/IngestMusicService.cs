using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Xml.Linq;
using MusicFileUtilities;
using MusicLibraryTools;
using MusicLibrary.Core.Models;
using iTunes.Binary;

namespace MusicLibrary.Core.Services;

public sealed class IngestMusicService : IIngestMusicService
{
    private static readonly Regex DiscSuffix = new(
        @"^(?<album>.+?)\s+\(Disc\s+(?<disc>\d+)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly IFfmpegRunner _ffmpeg;
    private readonly IAppSettings? _settings;
    private readonly int _previewParallelism;
    private readonly IItunesMediaMutationService _itunes;

    public IngestMusicService(
        IFfmpegRunner ffmpeg,
        IAppSettings? settings = null,
        int? previewParallelism = null,
        IItunesMediaMutationService? itunes = null)
    {
        _ffmpeg = ffmpeg;
        _settings = settings;
        _previewParallelism = Math.Clamp(previewParallelism ?? GetDefaultPreviewParallelism(), 1, 64);
        _itunes = itunes ?? new ItunesMediaMutationService(settings);
    }

    private static int GetDefaultPreviewParallelism()
    {
        string? configured = Environment.GetEnvironmentVariable("MLT_INGEST_PARALLELISM");
        return int.TryParse(configured, out int value) ? value : 16;
    }

    public Task<IngestPlan> PreviewAsync(IngestRequest request, CancellationToken ct = default)
        => Task.Run(() => Preview(request, ct), ct);

    private IngestPlan Preview(IngestRequest request, CancellationToken ct)
    {
        string sourceRoot = Path.GetFullPath(request.SourceDirectory);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceRoot}");
        var resolved = IngestMusicConfiguration.Resolve(request, _settings);
        var config = resolved.Configuration;
        LibraryProfile ingestProfile = config.Profile;
        LibraryIngestRecipe[] enabledRecipes = ingestProfile.Ingest.Recipes
            .Where(recipe => recipe.Enabled)
            .ToArray();
        bool legacyPipeline = ingestProfile.Preset ==
            LibraryProfilePreset.LegacyMusicLibraryTools;
        string? itunesMediaFolder = null;
        IngestFileSnapshot? itunesLibrarySnapshot = null;
        if (!string.IsNullOrWhiteSpace(config.ItunesLibraryPath))
        {
            ItlLibrary library = ItlLibrary.Load(config.ItunesLibraryPath);
            itunesMediaFolder = library.MusicFolderPath;
            if (string.IsNullOrWhiteSpace(itunesMediaFolder))
                throw new InvalidDataException("The configured iTunes library does not contain a media storage folder.");
            var libraryFile = new FileInfo(config.ItunesLibraryPath);
            itunesLibrarySnapshot = new IngestFileSnapshot(
                libraryFile.FullName, libraryFile.Length, libraryFile.LastWriteTimeUtc);
            config = config with { AacDestination = Path.Combine(itunesMediaFolder, "Music") };
        }
        string[] destinations = (legacyPipeline
                ? [config.AacDestination, config.CdDestination,
                    config.PairedCdDestination, config.HighResolutionDestination]
                : enabledRecipes.Select(recipe => config.ResolveTarget(recipe)?.Target)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Cast<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)
            .ToArray();
        if (destinations.Any(d => PathsOverlap(sourceRoot, d)))
            throw new InvalidDataException("The source directory must not overlap an ingestion destination.");
        if (destinations.SelectMany((a, i) => destinations.Skip(i + 1).Select(b => (a, b))).Any(p => PathsOverlap(p.a, p.b)))
            throw new InvalidDataException("Ingestion destination directories must not overlap each other.");
        var sourceDirectories = new List<string>();
        var scanFiles = new List<PreviewFile>();
        // One buffered traversal supplies paths, size/timestamp snapshots, and the directory list.
        // The previous implementation walked the whole tree once for files, once for directories,
        // then issued a FileInfo metadata request for every file -- particularly costly over SMB.
        foreach (var entry in new MusicFileEnumerator(sourceRoot, skipItlpPackages: false))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FileType == MFEType.Directory)
            {
                sourceDirectories.Add(entry.Name);
                continue;
            }

            string extension = Path.GetExtension(entry.Name);
            var snapshot = new IngestFileSnapshot(entry.Name, entry.Size, entry.Modified);
            if (!enabledRecipes.Any(recipe => recipe.InputExtensions.Contains(
                    extension, StringComparer.OrdinalIgnoreCase)))
            {
                scanFiles.Add(new PreviewFile(snapshot, Supported: false));
                continue;
            }

            scanFiles.Add(new PreviewFile(snapshot, Supported: true));
        }

        var scanResults = new PreviewFileResult[scanFiles.Count];
        var supportedIndexes = new List<int>(scanFiles.Count);
        for (int index = 0; index < scanFiles.Count; index++)
        {
            if (scanFiles[index].Supported)
                supportedIndexes.Add(index);
            else
                scanResults[index] = PreviewFileResult.Ignored(scanFiles[index].Snapshot);
        }

        // Parsing is independent per file. Bound the global reader count so high-latency opens can
        // overlap without allowing a large incoming tree to flood the share or retain unbounded tag
        // buffers. Results are written by index and merged below in original enumeration order.
        Parallel.ForEach(
            supportedIndexes,
            new ParallelOptions { MaxDegreeOfParallelism = _previewParallelism, CancellationToken = ct },
            index => scanResults[index] = ScanPreviewFile(
                scanFiles[index].Snapshot, ingestProfile, enabledRecipes));

        var conflicts = new List<IngestConflict>();
        var ignoredSnapshots = new List<IngestFileSnapshot>();
        var scanned = new List<ScannedTrack>();
        foreach (var result in scanResults)
        {
            if (result.Track is not null)
                scanned.Add(result.Track);
            else if (result.Conflict is not null)
                conflicts.Add(result.Conflict);
            else if (result.IgnoredSnapshot is not null)
                ignoredSnapshots.Add(result.IgnoredSnapshot);
        }
        var ignored = ignoredSnapshots.Select(snapshot => snapshot.Path).ToList();

        var albums = new List<IngestAlbumPlan>();
        var files = new List<IngestFileSummary>();
        var approvals = new List<IngestApprovalItem>();
        var claimed = new HashSet<string>(PathComparer);
        var claimedSidecars = new Dictionary<string, string>(PathComparer);

        foreach (var group in scanned.GroupBy(t => AlbumKey(t.EffectiveAlbumArtist, t.BaseAlbum)))
        {
            ct.ThrowIfCancellationRequested();
            var sourceTracks = group.ToList();
            string display = $"{sourceTracks[0].EffectiveAlbumArtist} — {sourceTracks[0].BaseAlbum}";
            int before = conflicts.Count;
            var discs = sourceTracks.Where(t => t.DiscNumber.HasValue).Select(t => t.DiscNumber!.Value).Distinct().Order().ToArray();
            bool multiDisc = discs.Length > 1;
            if (sourceTracks.Any(t => t.DiscNumber.HasValue) && sourceTracks.Any(t => !t.DiscNumber.HasValue))
                conflicts.Add(new IngestConflict(group.Key, sourceRoot, "An album mixes tracks with and without DiscNumber."));
            foreach (var slot in sourceTracks.Where(track => track.HadTrackNumber)
                         .GroupBy(t => (Disc: t.DiscNumber ?? 1, t.TrackNumber)))
            {
                string[] titles = slot.Select(t => NormalizeKey(t.Title)).Distinct().ToArray();
                if (titles.Length > 1)
                    conflicts.Add(new IngestConflict(group.Key, slot.First().Path,
                        $"Disc {slot.Key.Disc}, track {slot.Key.TrackNumber} has conflicting titles."));
            }

            List<IngestTrackPlan> trackPlans = BuildTrackPlans(
                sourceTracks, ingestProfile, multiDisc);

            foreach (var identity in trackPlans.GroupBy(t => t.Identity))
            {
                if (identity.Count(t => !t.IsHighResolution) > 1)
                    conflicts.Add(new IngestConflict(group.Key, identity.First().SourcePath,
                        $"Multiple CD-quality sources match '{identity.First().Title}'."));
            }
            if (conflicts.Count != before)
                continue;

            bool hasHigh = trackPlans.Any(t => t.IsHighResolution);
            var outputs = new List<IngestOutputPlan>();
            var missing = new List<string>();
            if (legacyPipeline)
                BuildLegacyOutputs(config, trackPlans, hasHigh, itunesMediaFolder,
                    claimed, outputs, missing);
            else
                BuildRecipeOutputs(config, trackPlans, enabledRecipes,
                    itunesMediaFolder, claimed, claimedSidecars, outputs,
                    conflicts, group.Key);
            if (conflicts.Count != before)
                continue;

            var snapshots = sourceTracks.Select(t => t.Snapshot).ToList();
            var album = new IngestAlbumPlan
            {
                Key = group.Key,
                Display = display,
                Tracks = trackPlans,
                Outputs = outputs,
                Sources = snapshots,
                HasHighResolution = hasHigh,
            };
            albums.Add(album);
            if (missing.Count > 0)
                approvals.Add(new IngestApprovalItem(group.Key, display, missing));

            foreach (var track in trackPlans)
            {
                var operations = outputs.Where(o => string.Equals(o.SourcePath, track.SourcePath, StringComparison.OrdinalIgnoreCase))
                    .Select(output => output.Kind switch
                    {
                        IngestOutputKind.HighResolutionFlac when track.IsAlac =>
                            $"Hi-Res FLAC → Transcode from ALAC, normalize metadata, move and rename to {output.DestinationPath}",
                        IngestOutputKind.HighResolutionFlac =>
                            $"Hi-Res FLAC → Normalize metadata, move and rename to {output.DestinationPath}",
                        IngestOutputKind.CdFlac when output.DeriveCd =>
                            $"CD FLAC → Transcode from Hi-Res, normalize metadata, move and rename to {output.DestinationPath}",
                        IngestOutputKind.CdFlac when track.IsAlac =>
                            $"CD FLAC → Transcode from ALAC, normalize metadata, move and rename to {output.DestinationPath}",
                        IngestOutputKind.CdFlac =>
                            $"CD FLAC → Normalize metadata, move and rename to {output.DestinationPath}",
                        IngestOutputKind.Aac =>
                            $"AAC → Transcode from CD FLAC, normalize metadata, move and rename to {output.DestinationPath}",
                        IngestOutputKind.Recipe =>
                            $"{output.RecipeId}: {output.Action}, " +
                            (output.PreserveMetadata ? "preserve metadata" : "normalize metadata") +
                            ArtworkPlanSummary(output) +
                            (output.OutputRepresentationRole == LibraryRepresentationRole.Ignore
                                ? ""
                                : $", representation {output.OutputRepresentationRole}") +
                            $", install at {output.DestinationPath}",
                        _ => throw new ArgumentOutOfRangeException(),
                    })
                    .ToList();
                operations.Add(PlanSourceDisposition(config));
                string sourceType = track.IsHighResolution
                    ? track.IsAlac ? "Hi-Res ALAC" : "Hi-Res FLAC"
                    : track.IsAlac ? "CD-quality ALAC" : "CD FLAC";
                files.Add(new IngestFileSummary(track.SourcePath, sourceType, string.Join(Environment.NewLine, operations)));
            }

        }

        foreach (string file in ignored)
        {
            LibrarySidecarDisposition disposition = config.SidecarDispositionFor(file, sourceRoot);
            files.Add(new IngestFileSummary(file, "Unsupported/non-audio",
                config.RemoveNonMusicAfterIngest
                    ? PlanSidecarDisposition(disposition)
                    : "Source → Leave unchanged"));
        }

        return new IngestPlan
        {
            Request = request with
            {
                SourceDirectory = sourceRoot,
                ConfigurationPath = resolved.ConfigurationPath,
            },
            Configuration = config,
            Albums = albums,
            Files = files,
            RequiredApprovals = approvals,
            Conflicts = conflicts,
            IgnoredFiles = ignored,
            IgnoredFileSnapshots = ignoredSnapshots,
            SourceDirectories = sourceDirectories,
            ItunesLibrarySnapshot = itunesLibrarySnapshot,
            PolicyFingerprint = config.PolicySnapshot?.Fingerprint,
        };
    }

    private static List<IngestTrackPlan> BuildTrackPlans(
        IReadOnlyList<ScannedTrack> sourceTracks,
        LibraryProfile profile,
        bool multiDisc)
    {
        var plans = new List<IngestTrackPlan>(sourceTracks.Count);
        var flattened = sourceTracks
            .Select(track => (
                Disc: track.DiscNumber ?? 1,
                track.TrackNumber,
                MissingKey: track.HadTrackNumber ? "" : track.Path))
            .Distinct()
            .OrderBy(slot => slot.Disc)
            .ThenBy(slot => slot.TrackNumber)
            .ThenBy(slot => slot.MissingKey, PathComparer)
            .Select((slot, index) => (slot, Number: index + 1))
            .ToDictionary(item => item.slot, item => item.Number);
        int albumTotal = flattened.Count;

        foreach (IGrouping<int, ScannedTrack> discGroup in sourceTracks.GroupBy(
                     track => track.DiscNumber ?? 1))
        {
            int[] numberedTracks = discGroup.Where(track => track.HadTrackNumber)
                .Select(track => track.TrackNumber)
                .Where(number =>
                    profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools ||
                    number > 0)
                .ToArray();
            int legacyOffset = profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools &&
                               multiDisc && numberedTracks.Length > 0
                ? numberedTracks.Min() - 1
                : 0;
            int inferredDiscTotal = numberedTracks.Length == 0
                ? 0
                : numberedTracks.Max() - legacyOffset;
            foreach (ScannedTrack track in discGroup)
            {
                int projectedTrack = profile.Disc.Strategy == LibraryDiscStrategy.FlattenContinuous
                    ? flattened[(discGroup.Key, track.TrackNumber,
                        track.HadTrackNumber ? "" : track.Path)]
                    : track.HadTrackNumber ? track.TrackNumber - legacyOffset : 0;
                int projectedTotal = profile.Disc.TrackTotalScope == LibraryTrackTotalScope.Album
                    ? albumTotal
                    : profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools
                        ? inferredDiscTotal
                        : track.TrackTotal is > 0 ? track.TrackTotal.Value : inferredDiscTotal;
                string projectedAlbum = profile.Disc.Strategy == LibraryDiscStrategy.AlbumSuffix &&
                                        multiDisc
                    ? $"{track.BaseAlbum} (Disc {discGroup.Key})"
                    : track.BaseAlbum;
                string identity = track.HadTrackNumber
                    ? TrackKey(track.EffectiveAlbumArtist, track.BaseAlbum,
                        discGroup.Key, track.TrackNumber, track.Title)
                    : TrackKey(track.EffectiveAlbumArtist, track.BaseAlbum,
                        discGroup.Key, track.TrackNumber,
                        track.Title + "\u001f" + track.Path);
                bool highResolution =
                    track.SampleRate >= profile.Quality.HighResolutionMinimumSampleRateHz ||
                    track.BitsPerSample >= profile.Quality.HighResolutionMinimumBitsPerSample;
                plans.Add(new IngestTrackPlan
                {
                    Identity = identity,
                    SourcePath = track.Path,
                    Title = track.Title,
                    Artist = track.Artist,
                    AlbumArtist = track.AlbumArtist,
                    Album = projectedAlbum,
                    TrackNumber = projectedTrack,
                    HadTrackNumber = track.HadTrackNumber ||
                        profile.Disc.Strategy == LibraryDiscStrategy.FlattenContinuous,
                    TrackTotal = projectedTotal,
                    OriginalDiscNumber = discGroup.Key,
                    HadDiscNumber = track.DiscNumber.HasValue ||
                        profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools,
                    OriginalTrackTotal = track.TrackTotal,
                    OriginalDiscTotal = track.DiscTotal,
                    SampleRate = track.SampleRate,
                    BitsPerSample = track.BitsPerSample,
                    Channels = track.Channels,
                    DurationInSeconds = track.Duration,
                    IsAlac = track.IsAlac,
                    IsLossless = track.IsLossless,
                    CodecName = track.CodecName,
                    SourceExtension = track.Extension,
                    IsHighResolution = highResolution,
                    Compilation = track.Compilation,
                });
            }
        }
        return plans;
    }

    private static void BuildLegacyOutputs(
        IngestMusicConfiguration config,
        IReadOnlyList<IngestTrackPlan> trackPlans,
        bool hasHigh,
        string? itunesMediaFolder,
        HashSet<string> claimed,
        List<IngestOutputPlan> outputs,
        List<string> missing)
    {
        string cdRoot = hasHigh ? config.PairedCdDestination : config.CdDestination;
        foreach (IGrouping<string, IngestTrackPlan> identity in trackPlans.GroupBy(
                     track => track.Identity))
        {
            List<IngestTrackPlan> candidates = identity.ToList();
            foreach (IngestTrackPlan high in candidates.Where(track => track.IsHighResolution)
                         .OrderByDescending(track => track.SampleRate)
                         .ThenByDescending(track => track.BitsPerSample)
                         .ThenBy(track => track.SourcePath))
            {
                string destination = ClaimCanonical(
                    config.HighResolutionDestination, high, ".flac", config, claimed);
                outputs.Add(Output(high, IngestOutputKind.HighResolutionFlac,
                    high.SourcePath, destination));
            }

            IngestTrackPlan? cd = candidates.SingleOrDefault(track => !track.IsHighResolution);
            bool derive = cd is null;
            if (derive)
            {
                cd = candidates.Where(track => track.IsHighResolution)
                    .OrderByDescending(track => track.SampleRate)
                    .ThenByDescending(track => track.BitsPerSample)
                    .ThenBy(track => track.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .First();
                missing.Add($"{cd.TrackNumber:D2} {cd.Title}");
            }
            IngestTrackPlan selectedCd = cd!;
            string cdDestination = ClaimCanonical(
                cdRoot, selectedCd, ".flac", config, claimed);
            outputs.Add(Output(selectedCd, IngestOutputKind.CdFlac,
                selectedCd.SourcePath, cdDestination, derive));
            string aacDestination = itunesMediaFolder is null
                ? ClaimCanonical(config.AacDestination, selectedCd, ".m4a", config, claimed)
                : ClaimItunesCanonical(itunesMediaFolder, selectedCd, claimed);
            outputs.Add(Output(selectedCd, IngestOutputKind.Aac,
                selectedCd.SourcePath, aacDestination));
        }
    }

    private static void BuildRecipeOutputs(
        IngestMusicConfiguration config,
        IReadOnlyList<IngestTrackPlan> trackPlans,
        IReadOnlyList<LibraryIngestRecipe> recipes,
        string? itunesMediaFolder,
        HashSet<string> claimed,
        Dictionary<string, string> claimedSidecars,
        List<IngestOutputPlan> outputs,
        List<IngestConflict> conflicts,
        string albumKey)
    {
        foreach (IGrouping<string, IngestTrackPlan> identity in trackPlans.GroupBy(
                     track => track.Identity))
        {
            foreach (LibraryIngestRecipe recipe in recipes)
            {
                IngestTrackPlan? selected = identity
                    .Where(track => RecipeMatches(recipe, track))
                    .OrderByDescending(track => track.SampleRate)
                    .ThenByDescending(track => track.BitsPerSample)
                    .ThenBy(track => track.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (selected is null)
                    continue;

                LibraryIndexLocation? target = config.ResolveTarget(recipe);
                bool itunesTarget = target is null &&
                    recipe.DestinationLegacyRole == LibraryIngestRole.AacFallback &&
                    !string.IsNullOrWhiteSpace(itunesMediaFolder);
                if (target is null && !itunesTarget)
                {
                    conflicts.Add(new(albumKey, selected.SourcePath,
                        $"Ingest recipe '{recipe.Name}' has no available destination."));
                    continue;
                }
                if (target is not null &&
                    !target.Permissions.HasFlag(LibraryRootPermissions.IngestOutput))
                {
                    conflicts.Add(new(albumKey, target.Target,
                        $"Ingest recipe '{recipe.Name}' targets a root that does not permit ingest output."));
                    continue;
                }

                string extension = string.IsNullOrWhiteSpace(recipe.OutputExtension)
                    ? selected.SourceExtension
                    : NormalizeExtension(recipe.OutputExtension);
                LibraryProfile namingProfile = config.ResolveProfile(recipe);
                LibraryProfile destinationProfile =
                    config.ResolveDestinationProfile(recipe);
                if (recipe.CollisionPolicy is LibraryPathCollisionPolicy collision)
                    namingProfile = namingProfile with
                    {
                        Naming = namingProfile.Naming with { CollisionPolicy = collision },
                    };
                string destination;
                try
                {
                    destination = itunesTarget
                        ? ClaimItunesCanonical(itunesMediaFolder!, selected, claimed)
                        : ClaimProfilePath(target!.Target, selected, extension, config,
                            namingProfile, claimed);
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException)
                {
                    conflicts.Add(new(albumKey, selected.SourcePath, error.Message));
                    continue;
                }
                IReadOnlyList<IngestArtworkArtifactPlan> artworkArtifacts = [];
                try
                {
                    artworkArtifacts = PlanArtworkArtifacts(
                        selected.SourcePath, destination, recipe.PreserveArtwork,
                        destinationProfile.Artwork);
                    if (artworkArtifacts.Count > 0 && target is not null &&
                        !target.Permissions.HasFlag(LibraryRootPermissions.WriteArtwork))
                        throw new InvalidDataException(
                            $"Ingest recipe '{recipe.Name}' transfers artwork to a root that " +
                            "does not permit artwork writes.");
                    foreach (IngestArtworkArtifactPlan artifact in artworkArtifacts.Where(
                                 artifact => artifact.SidecarDestination is not null))
                    {
                        string sidecar = artifact.SidecarDestination!;
                        if (claimedSidecars.TryGetValue(sidecar, out string? claimedHash) &&
                            !StringComparer.Ordinal.Equals(claimedHash, artifact.Sha256))
                            throw new InvalidDataException(
                                $"Artwork sidecar collision at '{sidecar}'.");
                        if (File.Exists(sidecar) && !StringComparer.Ordinal.Equals(
                                FileSha256(sidecar), artifact.Sha256))
                            throw new InvalidDataException(
                                $"Artwork sidecar already exists with different content: {sidecar}");
                        claimedSidecars[sidecar] = artifact.Sha256;
                    }
                }
                catch (Exception error) when (error is InvalidDataException or IOException or
                                                   NotSupportedException)
                {
                    conflicts.Add(new(albumKey, selected.SourcePath, error.Message));
                    continue;
                }
                outputs.Add(new IngestOutputPlan
                {
                    Identity = selected.Identity,
                    Kind = IngestOutputKind.Recipe,
                    Metadata = selected,
                    SourcePath = selected.SourcePath,
                    DestinationPath = destination,
                    DestinationRoot = target?.Target ?? itunesMediaFolder,
                    RecipeId = recipe.Id,
                    Action = recipe.Action,
                    OutputCodec = recipe.Codec,
                    Encoder = recipe.Encoder,
                    BitrateKbps = recipe.BitrateKbps,
                    SampleRateHz = recipe.SampleRateHz,
                    BitsPerSample = recipe.BitsPerSample,
                    OutputChannels = recipe.OutputChannels,
                    PreserveMetadata = recipe.PreserveMetadata,
                    PreserveArtwork = recipe.PreserveArtwork,
                    PreserveDiscTags = destinationProfile.Disc.PreserveDiscTags,
                    OutputRepresentationRole = recipe.OutputRepresentationRole,
                    ArtworkPolicy = destinationProfile.Artwork,
                    MetadataPolicy = destinationProfile.Metadata,
                    DiscPolicy = destinationProfile.Disc,
                    ArtworkArtifacts = artworkArtifacts,
                });
            }
        }
    }

    private static bool RecipeMatches(
        LibraryIngestRecipe recipe,
        IngestTrackPlan track)
    {
        if (!recipe.InputExtensions.Contains(
                track.SourceExtension, StringComparer.OrdinalIgnoreCase))
            return false;
        if (recipe.RequireLossless is bool lossless && track.IsLossless != lossless)
            return false;
        if (recipe.InputChannels is int channels && track.Channels != channels)
            return false;
        bool rate = recipe.MinimumSampleRateHz is not int minimumRate ||
                    track.SampleRate >= minimumRate;
        bool bits = recipe.MinimumBitsPerSample is not int minimumBits ||
                    track.BitsPerSample >= minimumBits;
        return recipe.MatchAnyQualityMinimum && recipe.MinimumSampleRateHz is not null &&
               recipe.MinimumBitsPerSample is not null
            ? rate || bits
            : rate && bits;
    }

    private static string ClaimProfilePath(
        string root,
        IngestTrackPlan track,
        string extension,
        IngestMusicConfiguration config,
        LibraryProfile profile,
        HashSet<string> claimed)
    {
        string initial = LibraryPathLayoutResolver.Shared.Resolve(
            root,
            profile,
            LibraryPathMetadata.From(track, extension),
            config.LengthLimit,
            config.DiscNumLengthLimit);
        string candidate = initial;
        int collision = profile.Naming.UseItunesCanonicalNaming ? 1 : 2;
        while (!claimed.Add(candidate))
            candidate = LibraryPathLayoutResolver.Shared.ResolveCollision(
                initial, track.SourcePath, profile, collision++);
        return candidate;
    }

    private static string NormalizeExtension(string extension) =>
        extension.StartsWith('.') ? extension : "." + extension;

    private static PreviewFileResult ScanPreviewFile(
        IngestFileSnapshot snapshot,
        LibraryProfile profile,
        IReadOnlyList<LibraryIngestRecipe> recipes)
    {
        string path = snapshot.Path;
        string extension = Path.GetExtension(path);
        try
        {
            // Preview never saves this object. Read-only MP4 parsing skips unrelated atom payloads
            // while preserving the same codec/tag projection used to build the plan.
            var media = MediaFile.GetFile(path, readOnly: true);
            var tag = media.Tags.FirstOrDefault() ?? throw new InvalidDataException("No metadata tag was found.");
            var codec = media.Codecs.FirstOrDefault() ?? throw new InvalidDataException("No audio stream was found.");
            bool alac = codec.CodecName.Equals("ALAC", StringComparison.OrdinalIgnoreCase);
            LibraryIngestRecipe[] matches = recipes.Where(recipe =>
                RecipeMatches(recipe, extension, codec)).ToArray();
            if (matches.Length == 0)
                return PreviewFileResult.Ignored(snapshot);
            string artist = (tag.Artist ?? "").Trim();
            string? albumArtist = tag.HasAlbumArtist &&
                !string.IsNullOrWhiteSpace(tag.AlbumArtist)
                    ? tag.AlbumArtist.Trim()
                    : null;
            string album = (tag.Album ?? "").Trim();
            string title = (tag.Title ?? "").Trim();
            string? compilationValue = tag.GetKnownMetadata()
                .FirstOrDefault(field => field.Key == TagFields.Compilation).Value;
            bool compilation = compilationValue is not null &&
                (compilationValue.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                 compilationValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                 compilationValue.Equals("yes", StringComparison.OrdinalIgnoreCase));
            bool legacy = profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools;
            if (legacy && (string.IsNullOrWhiteSpace(artist) ||
                string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(title) ||
                tag.TrackNumber is null))
                throw new InvalidDataException("Artist, album, title, and track number are required.");
            if (profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools &&
                extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) && !alac)
                return PreviewFileResult.Ignored(snapshot);
            if (profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools && codec.Channels != 2)
                throw new InvalidDataException($"Only stereo input is supported (found {codec.Channels} channels).");
            if (profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools &&
                (codec.Samplerate < 44100 || codec.BitsPerSample < 16))
                throw new InvalidDataException($"Below-CD-quality input is unsupported ({codec.Samplerate} Hz/{codec.BitsPerSample}-bit).");

            var suffix = DiscSuffix.Match(album);
            bool inferSuffix = suffix.Success && profile.Disc.InferAlbumSuffix;
            string baseAlbum = inferSuffix ? suffix.Groups["album"].Value.Trim() : album;
            int? discNumber = tag.DiscNumber;
            if (discNumber is null && inferSuffix &&
                int.TryParse(suffix.Groups["disc"].Value, out int parsedDisc))
                discNumber = parsedDisc;
            return PreviewFileResult.Scanned(new ScannedTrack(
                path, artist, albumArtist, baseAlbum, title, tag.TrackNumber ?? 0,
                legacy ? tag.TrackNumber.HasValue : tag.TrackNumber is > 0,
                tag.TrackTotal, discNumber, tag.DiscTotal, codec.Samplerate,
                codec.BitsPerSample, codec.Channels, codec.DurationInSeconds, alac,
                codec.CodecType == CodecType.Lossless, codec.CodecName, extension,
                compilation, snapshot));
        }
        catch (Exception ex)
        {
            return PreviewFileResult.Failed(new IngestConflict(path, path, ex.Message));
        }
    }

    private static bool RecipeMatches(
        LibraryIngestRecipe recipe,
        string extension,
        ICodecProvider codec)
    {
        if (!recipe.Enabled || !recipe.InputExtensions.Contains(
                extension, StringComparer.OrdinalIgnoreCase))
            return false;
        bool lossless = codec.CodecType == CodecType.Lossless;
        if (recipe.RequireLossless is bool requiredLossless && lossless != requiredLossless)
            return false;
        if (recipe.InputChannels is int channels && codec.Channels != channels)
            return false;

        bool rateMatches = recipe.MinimumSampleRateHz is not int minimumRate ||
                           codec.Samplerate >= minimumRate;
        bool bitsMatch = recipe.MinimumBitsPerSample is not int minimumBits ||
                         codec.BitsPerSample >= minimumBits;
        if (recipe.MatchAnyQualityMinimum &&
            recipe.MinimumSampleRateHz is not null && recipe.MinimumBitsPerSample is not null)
            return rateMatches || bitsMatch;
        return rateMatches && bitsMatch;
    }

    public async Task<IngestResult> ApplyAsync(IngestPlan plan, IReadOnlyList<IngestApprovalDecision> approvals,
        IProgress<IngestProgress>? progress = null, CancellationToken ct = default)
    {
        if (!plan.CanApply)
            return new IngestResult([], true, "The preview contains conflicts or no applicable ingest or cleanup work.");
        if (plan.PolicyFingerprint is not null &&
            CurrentPolicyFingerprint(plan) is { } currentFingerprint &&
            !string.Equals(plan.PolicyFingerprint, currentFingerprint, StringComparison.Ordinal))
            return new IngestResult([], true,
                "The library policy changed after preview; preview the ingest again.");
        var decisions = approvals.GroupBy(a => a.AlbumKey).ToDictionary(g => g.Key, g => g.Last().Approved, StringComparer.OrdinalIgnoreCase);
        foreach (var required in plan.RequiredApprovals)
            if (!decisions.TryGetValue(required.AlbumKey, out bool approved) || !approved)
                return new IngestResult([], true, $"CD-quality derivation was not approved for {required.AlbumDisplay}; nothing was changed.");

        bool hasAlbums = plan.Albums.Count > 0;
        if (hasAlbums && plan.ItunesLibrarySnapshot is not null)
            ItlFileEditor.EnsureItunesIsClosed();
        EnsureFresh(plan);
        bool needsFfmpeg = plan.Albums.SelectMany(album => album.Outputs).Any(output =>
            output.Kind != IngestOutputKind.Recipe || output.Action != LibraryIngestAction.Copy);
        string? automaticAacEncoder = null;
        if (hasAlbums && needsFfmpeg)
        {
            string[] encoders = IngestPreflightService.RequiredEncoders(plan.Configuration);
            if (encoders.Length == 0)
                encoders = [""];
            foreach (string encoder in encoders)
                await _ffmpeg.PreflightAsync(plan.Configuration.FfmpegPath, encoder, ct);
            bool needsAutomaticAac = plan.Configuration.Profile.Preset !=
                                     LibraryProfilePreset.LegacyMusicLibraryTools &&
                plan.Albums.SelectMany(album => album.Outputs).Any(output =>
                    output.Kind == IngestOutputKind.Recipe &&
                    output.Action == LibraryIngestAction.Transcode &&
                    string.IsNullOrWhiteSpace(output.Encoder) &&
                    (output.OutputCodec ?? Path.GetExtension(output.DestinationPath)
                        .TrimStart('.')).Equals("aac", StringComparison.OrdinalIgnoreCase));
            if (needsAutomaticAac)
                automaticAacEncoder = await _ffmpeg.ResolveEncoderAsync(
                    plan.Configuration.FfmpegPath,
                    [plan.Configuration.AacEncoder, "aac"], ct);
            EnsureFresh(plan);
        }

        string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        string quarantineRoot = plan.Request.SourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + ".IngestMusic-quarantine" + Path.DirectorySeparatorChar + runId;
        var results = new List<IngestAlbumResult>();
        int completed = 0;
        IngestFileSnapshot[] sidecarsToClean = plan.IgnoredFileSnapshots.Where(source =>
            plan.Configuration.SidecarDispositionFor(
                source.Path, plan.Request.SourceDirectory) is
                LibrarySidecarDisposition.Quarantine or LibrarySidecarDisposition.Delete)
            .ToArray();
        bool cleanDirectories = plan.Configuration.SourceDisposition !=
                                LibrarySourceDisposition.Preserve &&
                                plan.SourceDirectories.Count > 0;
        bool cleanupNonMusic = plan.Configuration.RemoveNonMusicAfterIngest &&
            (sidecarsToClean.Length > 0 || cleanDirectories);
        int cleanupItems = cleanupNonMusic
            ? sidecarsToClean.Length + (cleanDirectories ? 1 : 0)
            : 0;
        int total = plan.Albums.Sum(a => a.Outputs.Count + 1) + cleanupItems;
        foreach (var album in plan.Albums)
        {
            ct.ThrowIfCancellationRequested();
            int completedDuringAlbum = completed;
            progress?.Report(new IngestProgress(album.Display, "Staging outputs", completed, total));
            try
            {
                int installed = await ApplyAlbumAsync(plan, album, quarantineRoot, runId,
                    automaticAacEncoder, (output, staged) =>
                {
                    int current = staged ? Interlocked.Increment(ref completedDuringAlbum) : Volatile.Read(ref completedDuringAlbum);
                    string operation = staged
                        ? $"Staged {OutputName(output.Kind)}: {Path.GetFileName(output.DestinationPath)}"
                        : $"Processing {OutputName(output.Kind)}: {Path.GetFileName(output.SourcePath)}";
                    progress?.Report(new IngestProgress(album.Display, operation, current, total,
                        output.SourcePath, IngestFileProgressState.InProgress));
                }, ct);
                foreach (var source in album.Sources)
                    progress?.Report(new IngestProgress(album.Display, "Source complete", completedDuringAlbum, total,
                        source.Path, IngestFileProgressState.Completed));
                results.Add(new IngestAlbumResult(album.Key, true, installed));
            }
            catch (OperationCanceledException)
            {
                foreach (var source in album.Sources)
                    progress?.Report(new IngestProgress(album.Display, "Cancelled", completedDuringAlbum, total,
                        source.Path, IngestFileProgressState.Failed));
                throw;
            }
            catch (Exception ex)
            {
                foreach (var source in album.Sources)
                    progress?.Report(new IngestProgress(album.Display, ex.Message, completedDuringAlbum, total,
                        source.Path, IngestFileProgressState.Failed));
                results.Add(new IngestAlbumResult(album.Key, false, 0, ex.Message));
            }
            completed += album.Outputs.Count + 1;
            progress?.Report(new IngestProgress(album.Display, "Complete", completed, total));
        }
        if (cleanupNonMusic && results.All(result => result.Success))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new IngestProgress("Non-music cleanup", "Preparing", completed, total));
            RemoveNonMusic(plan, quarantineRoot, path =>
            {
                completed++;
                progress?.Report(new IngestProgress("Non-music cleanup", "Source complete", completed, total,
                    path, IngestFileProgressState.Completed));
            }, ct);
            if (cleanDirectories)
            {
                completed++;
                progress?.Report(new IngestProgress(
                    "Non-music cleanup", "Empty folders removed", completed, total));
            }
        }
        return new IngestResult(results, false);
    }

    private static void RemoveNonMusic(IngestPlan plan, string quarantineRoot, Action<string> fileCompleted,
        CancellationToken ct)
    {
        var selected = plan.IgnoredFileSnapshots.Select(source => new
            {
                Source = source,
                Disposition = plan.Configuration.SidecarDispositionFor(
                    source.Path, plan.Request.SourceDirectory),
            })
            .Where(item => item.Disposition is LibrarySidecarDisposition.Quarantine or
                LibrarySidecarDisposition.Delete)
            .ToArray();
        foreach (var item in selected) EnsureFresh(item.Source);
        var plannedMoves = selected.Select(item =>
        {
            string relative = Path.GetRelativePath(
                plan.Request.SourceDirectory, item.Source.Path);
            return (Original: item.Source.Path,
                Quarantine: Path.Combine(quarantineRoot, relative),
                Delete: item.Disposition == LibrarySidecarDisposition.Delete);
        }).ToList();
        var moved = new List<(string Original, string Quarantine, bool Delete)>();
        bool handleDirectories = plan.Configuration.SourceDisposition !=
                                 LibrarySourceDisposition.Preserve;
        string journalPath = Path.Combine(quarantineRoot, "journal.tsv");
        bool journalStarted = false;
        try
        {
            WriteJournal(journalPath,
                ["BEGIN\tNON_MUSIC_CLEANUP",
                 .. plannedMoves.Select(move => move.Delete
                     ? $"PLAN_DELETE\tNON_MUSIC\t{move.Original}"
                     : $"PLAN_QUARANTINE\tNON_MUSIC\t{move.Original}\t{move.Quarantine}")]);
            journalStarted = true;

            foreach (var move in plannedMoves)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(move.Quarantine)!);
                if (File.Exists(move.Quarantine))
                    throw new IOException($"Quarantine collision: {move.Quarantine}");
                File.Move(move.Original, move.Quarantine);
                moved.Add(move);
            }

            if (handleDirectories && !ShouldDelete(plan.Configuration))
            {
                foreach (string directory in plan.SourceDirectories.OrderBy(path => path.Length))
                {
                    ct.ThrowIfCancellationRequested();
                    string relative = Path.GetRelativePath(plan.Request.SourceDirectory, directory);
                    Directory.CreateDirectory(Path.Combine(quarantineRoot, relative));
                }
            }

            if (handleDirectories)
                foreach (string directory in plan.SourceDirectories.OrderByDescending(path => path.Length))
                {
                    ct.ThrowIfCancellationRequested();
                    try { if (Directory.Exists(directory)) Directory.Delete(directory); }
                    catch (IOException) { /* A new or preserved entry keeps this folder in place. */ }
                }

            WriteJournal(journalPath,
                [.. moved.Select(move => move.Delete
                     ? $"STAGE_DELETE\tNON_MUSIC\t{move.Original}\t{move.Quarantine}"
                     : $"QUARANTINE\tNON_MUSIC\t{move.Original}\t{move.Quarantine}"),
                 "COMMIT\tNON_MUSIC_CLEANUP"]);

            foreach (var move in moved.Where(item => item.Delete))
            {
                try
                {
                    File.SetAttributes(move.Quarantine, FileAttributes.Normal);
                    File.Delete(move.Quarantine);
                    WriteJournal(journalPath, [$"DELETE\tNON_MUSIC\t{move.Original}"]);
                    DeleteEmptyParents(Path.GetDirectoryName(move.Quarantine), quarantineRoot);
                }
                catch (Exception ex)
                {
                    try { WriteJournal(journalPath, [$"DELETE_FAILED\tNON_MUSIC\t{move.Quarantine}\t{ex.Message}"]); } catch { }
                }
            }
            foreach (var move in moved) fileCompleted(move.Original);
        }
        catch
        {
            foreach (var move in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(move.Quarantine) && !File.Exists(move.Original))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(move.Original)!);
                        File.Move(move.Quarantine, move.Original);
                    }
                }
                catch { }
            }
            foreach (string directory in plan.SourceDirectories.OrderBy(path => path.Length))
                try { Directory.CreateDirectory(directory); } catch { }
            if (journalStarted)
                try { WriteJournal(journalPath, ["ROLLBACK\tNON_MUSIC_CLEANUP"]); } catch { }
            throw;
        }
    }

    private async Task<int> ApplyAlbumAsync(IngestPlan plan, IngestAlbumPlan album,
        string quarantineRoot, string runId, string? automaticAacEncoder,
        Action<IngestOutputPlan, bool> outputProgress, CancellationToken ct)
    {
        foreach (var source in album.Sources) EnsureFresh(source);
        var staged = new ConcurrentDictionary<IngestOutputPlan, string>();
        var stagedSidecars = new ConcurrentDictionary<string, string>(PathComparer);
        var cdStages = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var installed = new List<string>();
        var quarantined = new List<(string Original, string Quarantine)>();
        var stageRoots = new ConcurrentDictionary<string, byte>(PathComparer);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
        string journalPath = Path.Combine(quarantineRoot, "journal.tsv");
        bool journalStarted = false;
        bool libraryCommitted = false;
        try
        {
            await Parallel.ForEachAsync(album.Outputs.Where(o => o.Kind != IngestOutputKind.Aac), parallel, async (output, token) =>
            {
                outputProgress(output, false);
                string root = output.Kind == IngestOutputKind.Recipe
                    ? output.DestinationRoot ?? Path.GetDirectoryName(output.DestinationPath)!
                    : output.Kind == IngestOutputKind.HighResolutionFlac
                        ? plan.Configuration.HighResolutionDestination
                        : album.HasHighResolution
                            ? plan.Configuration.PairedCdDestination
                            : plan.Configuration.CdDestination;
                string stageRoot = Path.Combine(root, ".IngestMusic-staging", runId, SafeToken(album.Key));
                Directory.CreateDirectory(stageRoot);
                stageRoots.TryAdd(stageRoot, 0);
                string stageExtension = output.Kind == IngestOutputKind.Recipe
                    ? Path.GetExtension(output.DestinationPath)
                    : ".flac";
                string stage = Path.Combine(
                    stageRoot, Guid.NewGuid().ToString("N") + stageExtension);
                if (output.Kind == IngestOutputKind.Recipe)
                {
                    IReadOnlyList<StagedSidecarArtifact> artwork =
                        await StageRecipeOutputAsync(plan.Configuration, output, stage,
                            automaticAacEncoder, token);
                    foreach (StagedSidecarArtifact artifact in artwork)
                    {
                        if (stagedSidecars.TryAdd(
                                artifact.DestinationPath, artifact.StagePath))
                            continue;
                        string existingStage = stagedSidecars[artifact.DestinationPath];
                        if (!StringComparer.Ordinal.Equals(
                                FileSha256(existingStage), FileSha256(artifact.StagePath)))
                            throw new InvalidDataException(
                                $"Artwork sidecar collision at '{artifact.DestinationPath}'.");
                        File.Delete(artifact.StagePath);
                    }
                }
                else if (output.DeriveCd)
                    await _ffmpeg.DeriveCdFlacAsync(plan.Configuration.FfmpegPath, output.SourcePath, stage, token);
                else if (output.Metadata.IsAlac)
                    await _ffmpeg.ConvertAlacToFlacAsync(plan.Configuration.FfmpegPath, output.SourcePath, stage, token);
                else
                    File.Copy(output.SourcePath, stage);
                if (output.Kind != IngestOutputKind.Recipe)
                    Normalize(stage, output.Metadata, output.SourcePath,
                        plan.Configuration.Profile.Disc,
                        plan.Configuration.Profile.Metadata, copyArtwork: true);
                Validate(stage, output);
                staged[output] = stage;
                if (output.Kind == IngestOutputKind.CdFlac)
                    cdStages[output.Identity] = stage;
                outputProgress(output, true);
            });

            await Parallel.ForEachAsync(album.Outputs.Where(o => o.Kind == IngestOutputKind.Aac), parallel, async (output, token) =>
            {
                outputProgress(output, false);
                string stageRoot = Path.Combine(plan.Configuration.AacDestination, ".IngestMusic-staging", runId, SafeToken(album.Key));
                Directory.CreateDirectory(stageRoot);
                stageRoots.TryAdd(stageRoot, 0);
                string stage = Path.Combine(stageRoot, Guid.NewGuid().ToString("N") + ".m4a");
                await _ffmpeg.EncodeAacAsync(plan.Configuration.FfmpegPath, plan.Configuration.AacEncoder,
                    plan.Configuration.AacBitrateKbps, cdStages[output.Identity], stage, token);
                Normalize(stage, output.Metadata, output.SourcePath,
                    plan.Configuration.Profile.Disc,
                    plan.Configuration.Profile.Metadata, copyArtwork: true);
                Validate(stage, output);
                staged[output] = stage;
                outputProgress(output, true);
            });

            foreach (var output in album.Outputs)
            {
                string stage = staged[output];
                if (!File.Exists(output.DestinationPath)) continue;
                if (!await EquivalentAsync(plan.Configuration.FfmpegPath, stage,
                        output.DestinationPath, output, ct) ||
                    !ExistingArtworkMatches(output.DestinationPath, output))
                    throw new IOException($"Destination exists with different content: {output.DestinationPath}");
            }
            foreach ((string destination, string stage) in stagedSidecars)
                if (File.Exists(destination))
                {
                    if (!StringComparer.Ordinal.Equals(
                            FileSha256(stage), FileSha256(destination)))
                        throw new IOException(
                            $"Artwork sidecar exists with different content: {destination}");
                    File.Delete(stage);
                    stagedSidecars.TryRemove(destination, out _);
                }

            foreach (var source in album.Sources) EnsureFresh(source);
            List<(string Original, string Quarantine)> plannedQuarantine =
                plan.Configuration.SourceDisposition == LibrarySourceDisposition.Preserve
                    ? []
                    : album.Sources.Select(source =>
                    {
                        string relative = Path.GetRelativePath(
                            plan.Request.SourceDirectory, source.Path);
                        return (Original: source.Path,
                            Quarantine: Path.Combine(quarantineRoot, relative));
                    }).ToList();
            string[] mutationCandidates =
            [
                .. album.Outputs.Select(output => output.DestinationPath),
                .. stagedSidecars.Keys,
                .. plannedQuarantine.Select(move => move.Original),
            ];
            await using IItunesMediaMutationSession itunesSession =
                await _itunes.BeginAsync(
                    mutationCandidates,
                    backupFiles: false,
                    plan.Configuration.ItunesLibraryPath,
                    ct);
            WriteJournal(journalPath,
                [$"BEGIN\t{album.Key}",
                 .. album.Outputs.Where(o => !File.Exists(o.DestinationPath)).Select(o => $"PLAN_INSTALL\t{album.Key}\t{o.DestinationPath}"),
                 .. stagedSidecars.Keys.Select(path => $"PLAN_INSTALL\t{album.Key}\t{path}"),
                 .. plannedQuarantine.Select(q => ShouldDelete(plan.Configuration)
                     ? $"PLAN_DELETE\t{album.Key}\t{q.Original}"
                     : $"PLAN_QUARANTINE\t{album.Key}\t{q.Original}\t{q.Quarantine}")]);
            journalStarted = true;

            foreach (var output in album.Outputs)
            {
                string stage = staged[output];
                if (File.Exists(output.DestinationPath))
                {
                    File.Delete(stage);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(output.DestinationPath)!);
                File.Move(stage, output.DestinationPath);
                installed.Add(output.DestinationPath);
            }
            foreach ((string destination, string stage) in stagedSidecars)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(stage, destination);
                installed.Add(destination);
            }

            foreach (var move in plannedQuarantine)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(move.Quarantine)!);
                if (File.Exists(move.Quarantine)) throw new IOException($"Quarantine collision: {move.Quarantine}");
                File.Move(move.Original, move.Quarantine);
                quarantined.Add(move);
            }

            string[] commitJournal =
                [.. installed.Select(path => $"INSTALL\t{album.Key}\t{path}"),
                 .. quarantined.Select(q => ShouldDelete(plan.Configuration)
                     ? $"STAGE_DELETE\t{album.Key}\t{q.Original}\t{q.Quarantine}"
                     : $"QUARANTINE\t{album.Key}\t{q.Original}\t{q.Quarantine}"),
                  $"COMMIT\t{album.Key}"];
            if (!string.IsNullOrWhiteSpace(plan.Configuration.ItunesLibraryPath))
            {
                await itunesSession.CommitAsync(
                [
                    .. album.Outputs
                        .Where(output => output.Kind == IngestOutputKind.Aac)
                        .Select(output => ItunesMediaMutation.Add(output.DestinationPath)),
                    .. quarantined.Select(move =>
                        ItunesMediaMutation.Remove(move.Original)),
                ], CancellationToken.None);
                await itunesSession.CompleteAsync(CancellationToken.None);
                libraryCommitted = true;
                // The library replacement is the final commit point. A journal failure after it
                // must not roll back files that the now-committed library references.
                try { WriteJournal(journalPath, commitJournal); } catch { }
            }
            else
            {
                WriteJournal(journalPath, commitJournal);
            }
            if (ShouldDelete(plan.Configuration))
            {
                foreach (var move in quarantined)
                {
                    try
                    {
                        File.SetAttributes(move.Quarantine, FileAttributes.Normal);
                        File.Delete(move.Quarantine);
                        WriteJournal(journalPath, [$"DELETE\t{album.Key}\t{move.Original}"]);
                        DeleteEmptyParents(Path.GetDirectoryName(move.Quarantine), quarantineRoot);
                    }
                    catch (Exception ex)
                    {
                        // The ingest is already committed. Preserve an undeletable file in quarantine
                        // and record it instead of reporting a rollback that did not happen.
                        try { WriteJournal(journalPath, [$"DELETE_FAILED\t{album.Key}\t{move.Quarantine}\t{ex.Message}"]); } catch { }
                    }
                }
            }
            return installed.Count(path => album.Outputs.Any(output =>
                PathComparer.Equals(path, output.DestinationPath)));
        }
        catch
        {
            if (!libraryCommitted)
            {
                foreach (var move in quarantined.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(move.Quarantine) && !File.Exists(move.Original))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(move.Original)!);
                            File.Move(move.Quarantine, move.Original);
                        }
                    }
                    catch { }
                }
                foreach (string path in installed.AsEnumerable().Reverse())
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                if (journalStarted)
                    try { WriteJournal(journalPath, [$"ROLLBACK\t{album.Key}"]); } catch { }
            }
            throw;
        }
        finally
        {
            foreach (string stage in staged.Values)
                try { if (File.Exists(stage)) File.Delete(stage); } catch { }
            foreach (string stage in stagedSidecars.Values)
                try { if (File.Exists(stage)) File.Delete(stage); } catch { }
            foreach (string root in stageRoots.Keys.OrderByDescending(p => p.Length))
                CleanupStageDirectories(root);
        }
    }

    private async Task<IReadOnlyList<StagedSidecarArtifact>> StageRecipeOutputAsync(
        IngestMusicConfiguration configuration,
        IngestOutputPlan output,
        string stage,
        string? automaticAacEncoder,
        CancellationToken ct)
    {
        string sourceExtension = Path.GetExtension(output.SourcePath);
        string destinationExtension = Path.GetExtension(stage);
        switch (output.Action)
        {
            case LibraryIngestAction.Copy:
                if (!sourceExtension.Equals(destinationExtension,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Copy recipe '{output.RecipeId}' cannot change {sourceExtension} to " +
                        $"{destinationExtension}; choose a transcode action.");
                File.Copy(output.SourcePath, stage);
                break;

            case LibraryIngestAction.Remux:
                await _ffmpeg.RemuxAsync(configuration.FfmpegPath,
                    output.SourcePath, stage, ct).ConfigureAwait(false);
                break;

            case LibraryIngestAction.Transcode:
                string codec = (output.OutputCodec ?? destinationExtension.TrimStart('.'))
                    .Trim().ToLowerInvariant();
                if (codec == "m4a")
                    codec = "aac";
                await _ffmpeg.TranscodeAsync(
                    configuration.FfmpegPath,
                    output.SourcePath,
                    stage,
                    new FfmpegTranscodeOptions(
                        codec,
                        output.Encoder ?? (codec == "aac"
                            ? automaticAacEncoder ?? "aac"
                            : null),
                        output.BitrateKbps ?? (codec == "aac"
                            ? configuration.AacBitrateKbps
                            : null),
                        output.SampleRateHz,
                        output.BitsPerSample,
                        output.OutputChannels),
                    ct).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(output.Action));
        }

        LibraryArtworkPolicy artworkPolicy = output.ArtworkPolicy ??
            configuration.Profile.Artwork;
        LibraryMetadataPolicy metadataPolicy = output.MetadataPolicy ??
            configuration.Profile.Metadata;
        LibraryDiscPolicy discPolicy = output.DiscPolicy ??
            configuration.Profile.Disc;
        bool identityArtwork = artworkPolicy.Storage == LibraryArtworkStorage.Embedded &&
                               artworkPolicy.Roles == LibraryArtworkRoleSelection.AllRoles &&
                               artworkPolicy.Encoding == LibraryArtworkEncoding.PreserveSource &&
                               artworkPolicy.MaximumDimension == 0 &&
                               artworkPolicy.MaximumEncodedBytes == 0 ||
                               artworkPolicy.Storage == LibraryArtworkStorage.None &&
                               ArtworkCount(output.SourcePath) == 0;
        bool exactPreservingCopy = output.Action == LibraryIngestAction.Copy &&
                                   output.PreserveMetadata && output.PreserveArtwork &&
                                   output.PreserveDiscTags &&
                                   metadataPolicy.PreservesAllSupportedMetadata &&
                                   identityArtwork;
        IReadOnlyList<PreparedArtworkTransfer> transfers;
        if (!exactPreservingCopy)
            transfers = Normalize(stage, output.Metadata, output.SourcePath,
                discPolicy with
                {
                    PreserveDiscTags = output.PreserveDiscTags,
                },
                metadataPolicy,
                output.PreserveArtwork,
                preserveAdditionalMetadata: output.PreserveMetadata,
                artworkPolicy: artworkPolicy);
        else
            transfers = PrepareArtworkTransfers(output.SourcePath, artworkPolicy);

        ValidateArtworkPlan(output, transfers);
        if (!output.PreserveArtwork || !ArtworkService.WritesSidecars(artworkPolicy))
            return [];
        string sidecarStageDirectory = stage + ".sidecars";
        var result = new List<StagedSidecarArtifact>();
        for (var index = 0; index < transfers.Count; index++)
        {
            string? destination = output.ArtworkArtifacts[index].SidecarDestination;
            if (destination is null)
                continue;
            Directory.CreateDirectory(sidecarStageDirectory);
            string stagePath = Path.Combine(sidecarStageDirectory,
                Path.GetFileName(destination));
            if (File.Exists(stagePath))
            {
                if (!StringComparer.Ordinal.Equals(
                        FileSha256(stagePath), Sha256(transfers[index].Prepared.Data)))
                    throw new InvalidDataException(
                        $"Artwork sidecar collision at '{destination}'.");
                continue;
            }
            File.WriteAllBytes(stagePath, transfers[index].Prepared.Data);
            result.Add(new(stagePath, destination));
        }
        return result;
    }

    private static void CleanupStageDirectories(string albumStageRoot)
    {
        try
        {
            if (Directory.Exists(albumStageRoot))
                Directory.Delete(albumStageRoot, true);

            string? runStageRoot = Directory.GetParent(albumStageRoot)?.FullName;
            if (runStageRoot is not null && Directory.Exists(runStageRoot))
                Directory.Delete(runStageRoot);

            string? stagingRoot = runStageRoot is null ? null : Directory.GetParent(runStageRoot)?.FullName;
            if (stagingRoot is not null && Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot);
        }
        catch
        {
            // A non-empty ancestor belongs to another album or concurrent ingest and must remain.
        }
    }

    private static void DeleteEmptyParents(string? path, string stopAt)
    {
        string boundary = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string boundaryPrefix = boundary + Path.DirectorySeparatorChar;
        while (path is not null)
        {
            string current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!current.StartsWith(boundaryPrefix, StringComparison.OrdinalIgnoreCase)) break;
            try { Directory.Delete(current); }
            catch { break; }
            path = Directory.GetParent(current)?.FullName;
        }
    }

    private static string PlanSourceDisposition(IngestMusicConfiguration configuration)
        => configuration.SourceDisposition == LibrarySourceDisposition.Preserve
            ? "Source - Leave unchanged"
            : ShouldDelete(configuration)
            ? "Source → Delete after successful ingest"
            : "Source → Quarantine after successful ingest";

    private static string ArtworkPlanSummary(IngestOutputPlan output)
    {
        if (!output.PreserveArtwork || output.ArtworkPolicy?.Storage ==
            LibraryArtworkStorage.None)
            return ", omit artwork";
        string storage = output.ArtworkPolicy?.Storage.ToString().ToLowerInvariant() ??
            "embedded";
        string[] sidecars = output.ArtworkArtifacts
            .Select(artifact => artifact.SidecarDestination)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(PathComparer)
            .ToArray();
        return sidecars.Length == 0
            ? $", preserve {output.ArtworkArtifacts.Count:N0} artwork image(s) as {storage}"
            : $", preserve {output.ArtworkArtifacts.Count:N0} artwork image(s) as {storage}: " +
              string.Join(", ", sidecars);
    }

    private static string PlanSidecarDisposition(LibrarySidecarDisposition disposition) =>
        disposition switch
        {
            LibrarySidecarDisposition.Delete =>
                "Source → Delete after successful ingest",
            LibrarySidecarDisposition.Quarantine =>
                "Source → Quarantine after successful ingest",
            _ => "Source → Leave unchanged",
        };

    private static bool ShouldDelete(IngestMusicConfiguration configuration) =>
        configuration.SourceDisposition == LibrarySourceDisposition.Delete;

    private static string OutputName(IngestOutputKind kind) => kind switch
    {
        IngestOutputKind.HighResolutionFlac => "Hi-Res FLAC",
        IngestOutputKind.CdFlac => "CD FLAC",
        IngestOutputKind.Aac => "AAC",
        IngestOutputKind.Recipe => "Recipe output",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private async Task<bool> EquivalentAsync(string ffmpeg, string staged, string existing, IngestOutputPlan output, CancellationToken ct)
    {
        try
        {
            Validate(existing, output);
            if (output.Kind == IngestOutputKind.Recipe &&
                output.Action == LibraryIngestAction.Copy)
            {
                await using FileStream stagedStream = File.OpenRead(staged);
                await using FileStream existingStream = File.OpenRead(existing);
                byte[] stagedHash = await SHA256.HashDataAsync(stagedStream, ct);
                byte[] existingHash = await SHA256.HashDataAsync(existingStream, ct);
                return stagedHash.AsSpan().SequenceEqual(existingHash);
            }
            string first = await _ffmpeg.ComputeDecodedAudioHashAsync(ffmpeg, staged, ct);
            string second = await _ffmpeg.ComputeDecodedAudioHashAsync(ffmpeg, existing, ct);
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) && ArtworkCount(staged) == ArtworkCount(existing);
        }
        catch { return false; }
    }

    private static IReadOnlyList<IngestArtworkArtifactPlan> PlanArtworkArtifacts(
        string sourcePath,
        string destinationPath,
        bool preserveArtwork,
        LibraryArtworkPolicy policy)
    {
        if (!preserveArtwork || policy.Storage == LibraryArtworkStorage.None)
            return [];
        IReadOnlyList<PreparedArtworkTransfer> transfers =
            PrepareArtworkTransfers(sourcePath, policy);
        string destinationDirectory = Path.GetDirectoryName(
            Path.GetFullPath(destinationPath))!;
        return transfers.Select((transfer, index) =>
        {
            string? sidecar = ArtworkService.WritesSidecars(policy)
                ? Path.Combine(destinationDirectory, ArtworkService.SidecarFileName(
                    policy, transfer.Input.Type, index + 1,
                    transfer.Prepared.Extension))
                : null;
            return new IngestArtworkArtifactPlan(
                ArtworkService.RoleName(transfer.Input.Type, index + 1),
                transfer.Prepared.MimeType,
                transfer.Prepared.Width,
                transfer.Prepared.Height,
                transfer.Prepared.Data.LongLength,
                Sha256(transfer.Prepared.Data),
                sidecar);
        }).ToArray();
    }

    private static IReadOnlyList<PreparedArtworkTransfer> PrepareArtworkTransfers(
        string sourcePath,
        LibraryArtworkPolicy policy)
    {
        if (policy.Storage == LibraryArtworkStorage.None)
            return [];
        IMediaFile source = MediaFile.GetFile(sourcePath, readOnly: true);
        ArtworkInput[] inputs = source.Tags.SelectMany(tag => tag.GetImageMetadata())
            .Select(image => new ArtworkInput(
                ParsePictureType(image.Category),
                NormalizeMime(image.ImageType),
                image.Data,
                image.Description ?? ""))
            .Where(input => policy.Roles != LibraryArtworkRoleSelection.FrontCoverOnly ||
                            input.Type == ID3v2Util.APICType.FrontCover)
            .ToArray();
        return inputs.Select(input => new PreparedArtworkTransfer(
            input,
            ArtworkService.PrepareArtwork(
                input.Data, input.MimeType, policy, requestedMaximumDimension: 0)))
            .ToArray();
    }

    private static string FileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static void ValidateArtworkPlan(
        IngestOutputPlan output,
        IReadOnlyList<PreparedArtworkTransfer> transfers)
    {
        if (transfers.Count != output.ArtworkArtifacts.Count)
            throw new InvalidDataException(
                $"Artwork changed after preview for '{output.SourcePath}'.");
        for (var index = 0; index < transfers.Count; index++)
        {
            PreparedArtworkTransfer actual = transfers[index];
            IngestArtworkArtifactPlan expected = output.ArtworkArtifacts[index];
            if (!StringComparer.Ordinal.Equals(
                    ArtworkService.RoleName(actual.Input.Type, index + 1), expected.Role) ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    actual.Prepared.MimeType, expected.MimeType) ||
                actual.Prepared.Width != expected.Width ||
                actual.Prepared.Height != expected.Height ||
                actual.Prepared.Data.LongLength != expected.EncodedBytes ||
                !StringComparer.Ordinal.Equals(
                    Sha256(actual.Prepared.Data), expected.Sha256))
                throw new InvalidDataException(
                    $"Artwork changed after preview for '{output.SourcePath}'.");
        }
    }

    private static bool ExistingArtworkMatches(
        string path,
        IngestOutputPlan output)
    {
        if (output.Kind != IngestOutputKind.Recipe || output.ArtworkPolicy is null)
            return true;
        IngestArtworkArtifactPlan[] expected = output.PreserveArtwork &&
            ArtworkService.WritesEmbedded(output.ArtworkPolicy)
                ? output.ArtworkArtifacts.ToArray()
                : [];
        try
        {
            var actual = MediaFile.GetFile(path, readOnly: true).Tags
                .SelectMany(tag => tag.GetImageMetadata()).ToArray();
            if (actual.Length != expected.Length)
                return false;
            for (var index = 0; index < actual.Length; index++)
                if (!StringComparer.Ordinal.Equals(
                        Sha256(actual[index].Data), expected[index].Sha256) ||
                    !StringComparer.OrdinalIgnoreCase.Equals(
                        NormalizeMime(actual[index].ImageType), expected[index].MimeType) ||
                    !StringComparer.Ordinal.Equals(
                        ArtworkService.RoleName(
                            ParsePictureType(actual[index].Category), index + 1),
                        expected[index].Role))
                    return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<PreparedArtworkTransfer> Normalize(
        string path,
        IngestTrackPlan track,
        string metadataSource,
        LibraryDiscPolicy discPolicy,
        LibraryMetadataPolicy metadataPolicy,
        bool copyArtwork,
        bool preserveAdditionalMetadata = false,
        LibraryArtworkPolicy? artworkPolicy = null)
    {
        var media = MediaFile.GetFile(path);
        IMetadataWriter writer = media as IMetadataWriter
            ?? media.Tags.FirstOrDefault() as IMetadataWriter
            ?? throw new InvalidDataException($"Output tag format is not writable: {path}");
        IMediaFile? sourceMedia = preserveAdditionalMetadata || copyArtwork
            ? MediaFile.GetFile(metadataSource, readOnly: true)
            : null;
        if (preserveAdditionalMetadata && sourceMedia is not null)
        {
            foreach (KeyValuePair<TagFields, string> field in sourceMedia.Tags
                         .SelectMany(tag => tag.GetKnownMetadata()))
            {
                if (!ShouldPreserve(field.Key, metadataPolicy))
                    continue;
                writer.SetField(field.Key, field.Value);
            }

            IUserStringMetadata? sourceStrings = sourceMedia as IUserStringMetadata ??
                sourceMedia.Tags.OfType<IUserStringMetadata>().FirstOrDefault();
            IUserStringMetadata? destinationStrings = media as IUserStringMetadata ??
                media.Tags.OfType<IUserStringMetadata>().FirstOrDefault();
            if (metadataPolicy.PreserveCustomFields && sourceStrings is not null &&
                destinationStrings is not null)
                foreach (KeyValuePair<string, string> field in sourceStrings.GetUserStrings())
                    destinationStrings.SetUserString(field.Key, field.Value);
        }

        writer.SetField(TagFields.Title, track.Title);
        writer.SetField(TagFields.Artist, track.Artist);
        if (!string.IsNullOrWhiteSpace(track.AlbumArtist))
            writer.SetField(TagFields.AlbumArtist, track.AlbumArtist);
        else
            writer.RemoveField(TagFields.AlbumArtist);
        writer.SetField(TagFields.Album, track.Album);
        if (track.HadTrackNumber)
            writer.SetField(TagFields.TrackNumber, track.TrackNumber.ToString());
        else
            writer.RemoveField(TagFields.TrackNumber);
        if (track.HadTrackNumber)
            writer.SetField(TagFields.TotalTracks, track.TrackTotal.ToString());
        else
            writer.RemoveField(TagFields.TotalTracks);
        if (metadataPolicy.PreserveCompilationSemantics && track.Compilation)
            writer.SetField(TagFields.Compilation, "1");
        else
            writer.RemoveField(TagFields.Compilation);
        if (discPolicy.PreserveDiscTags && track.HadDiscNumber &&
            track.OriginalDiscNumber > 0)
        {
            writer.SetField(TagFields.DiscNumber, track.OriginalDiscNumber.ToString());
            if (track.OriginalDiscTotal is > 0)
                writer.SetField(TagFields.TotalDiscs, track.OriginalDiscTotal.Value.ToString());
            else
                writer.RemoveField(TagFields.TotalDiscs);
        }
        else
        {
            writer.RemoveField(TagFields.DiscNumber);
            writer.RemoveField(TagFields.TotalDiscs);
        }

        IReadOnlyList<PreparedArtworkTransfer> transfers = [];
        if (copyArtwork && sourceMedia is not null)
        {
            if (artworkPolicy is null)
            {
                transfers = sourceMedia.Tags.SelectMany(tag => tag.GetImageMetadata())
                    .Select(image =>
                    {
                        var input = new ArtworkInput(ParsePictureType(image.Category),
                            NormalizeMime(image.ImageType), image.Data,
                            image.Description ?? "");
                        return new PreparedArtworkTransfer(input,
                            new ArtworkService.PreparedArtwork(
                                image.Data, input.MimeType,
                                Path.GetExtension(input.MimeType) switch
                                {
                                    ".png" => ".png",
                                    _ => ".jpg",
                                }, 0, 0));
                    }).ToArray();
            }
            else
            {
                transfers = PrepareArtworkTransfers(metadataSource, artworkPolicy);
            }
        }

        IArtworkWriter? artworkWriter = media as IArtworkWriter ??
            media.Tags.OfType<IArtworkWriter>().FirstOrDefault();
        if (artworkWriter is not null)
        {
            if (copyArtwork && transfers.Count > 0 &&
                (artworkPolicy is null || ArtworkService.WritesEmbedded(artworkPolicy)))
            {
                var images = transfers
                    .Select(transfer => new ArtworkImage(
                        transfer.Input.Type,
                        transfer.Prepared.MimeType,
                        transfer.Input.Description ?? "",
                        transfer.Prepared.Data))
                    .ToList();
                artworkWriter.SetImages(images);
            }
            else
            {
                artworkWriter.RemoveImages();
            }
        }
        media.SaveTags();
        return transfers;
    }

    private static bool ShouldPreserve(
        TagFields field,
        LibraryMetadataPolicy policy)
    {
        string name = field.ToString();
        if (name.StartsWith("ReplayGain_", StringComparison.Ordinal))
            return policy.PreserveReplayGain;
        if (name.StartsWith("MusicBrainz_", StringComparison.Ordinal))
            return policy.PreserveMusicBrainzIdentifiers;
        if (field == TagFields.Compilation)
            return policy.PreserveCompilationSemantics;
        return true;
    }

    private static ID3v2Util.APICType ParsePictureType(string? value)
        => Enum.TryParse<ID3v2Util.APICType>(value, true, out var type) ? type : ID3v2Util.APICType.FrontCover;

    private static string NormalizeMime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "image/jpeg";
        return value.Contains('/') ? value : $"image/{value.TrimStart('.').ToLowerInvariant()}";
    }

    private static void Validate(string path, IngestOutputPlan output)
    {
        var media = MediaFile.GetFile(path);
        var tag = media.Tags.First();
        var codec = media.Codecs.First();
        if (output.Kind == IngestOutputKind.Recipe)
        {
            uint expectedRate = (uint)(output.SampleRateHz ??
                (int)output.Metadata.SampleRate);
            uint expectedBits = (uint)(output.BitsPerSample ??
                (int)output.Metadata.BitsPerSample);
            uint expectedChannels = (uint)(output.OutputChannels ??
                (int)output.Metadata.Channels);
            if (codec.Samplerate != expectedRate || codec.Channels != expectedChannels ||
                codec.CodecType == CodecType.Lossless && codec.BitsPerSample != expectedBits)
                throw new InvalidDataException(
                    $"Recipe output has an unexpected audio format: {path}");
            if (!string.IsNullOrWhiteSpace(output.OutputCodec) &&
                !codec.CodecName.Contains(output.OutputCodec,
                    StringComparison.OrdinalIgnoreCase) &&
                !(output.OutputCodec.Equals("aac", StringComparison.OrdinalIgnoreCase) &&
                  codec.CodecType == CodecType.Lossy))
                throw new InvalidDataException(
                    $"Recipe output has unexpected codec '{codec.CodecName}': {path}");
            if (!output.PreserveMetadata &&
                (!Same(tag.Title, output.Metadata.Title) ||
                 !Same(tag.Album, output.Metadata.Album) ||
                 !Same(tag.AlbumArtist, output.Metadata.AlbumArtist) ||
                 tag.TrackNumber != ExpectedTrackNumber(output.Metadata) ||
                 tag.TrackTotal != ExpectedTrackTotal(output.Metadata)))
                throw new InvalidDataException(
                    $"Recipe output metadata validation failed: {path}");
            if (output.PreserveDiscTags)
            {
                IMetadataProvider sourceTag = MediaFile.GetFile(
                    output.SourcePath, readOnly: true).Tags.First();
                if (tag.DiscNumber != sourceTag.DiscNumber ||
                    tag.DiscTotal != sourceTag.DiscTotal)
                    throw new InvalidDataException(
                        $"Recipe output did not preserve disc metadata: {path}");
            }
            if (!output.PreserveDiscTags && tag.DiscNumber is not null)
                throw new InvalidDataException(
                    $"Recipe output retained disc metadata contrary to policy: {path}");
            int recipeDelta = Math.Abs((int)codec.DurationInSeconds -
                                       (int)output.Metadata.DurationInSeconds);
            if (recipeDelta > 1)
                throw new InvalidDataException(
                    $"Recipe output duration changed unexpectedly: {path}");
            return;
        }
        uint rate = output.Kind == IngestOutputKind.HighResolutionFlac ? output.Metadata.SampleRate : 44100;
        uint bits = output.Kind == IngestOutputKind.HighResolutionFlac ? output.Metadata.BitsPerSample : 16;
        if (codec.Samplerate != rate || (output.Kind != IngestOutputKind.Aac && codec.BitsPerSample != bits))
            throw new InvalidDataException($"Generated file has unexpected audio format: {path}");
        if (codec.Channels != 2 || (output.Kind == IngestOutputKind.Aac ? codec.CodecType != CodecType.Lossy : codec.CodecType != CodecType.Lossless))
            throw new InvalidDataException($"Generated file has unexpected codec/channels: {path}");
        if (!Same(tag.Title, output.Metadata.Title) || !Same(tag.Album, output.Metadata.Album) ||
            !Same(tag.AlbumArtist, output.Metadata.AlbumArtist) ||
            tag.TrackNumber != ExpectedTrackNumber(output.Metadata) ||
            tag.TrackTotal != ExpectedTrackTotal(output.Metadata) ||
            tag.DiscNumber is not null || tag.DiscTotal is not null)
            throw new InvalidDataException($"Generated file metadata validation failed: {path}");
        int delta = Math.Abs((int)codec.DurationInSeconds - (int)output.Metadata.DurationInSeconds);
        if (delta > 1) throw new InvalidDataException($"Generated file duration changed unexpectedly: {path}");
    }

    private static int ArtworkCount(string path) => MediaFile.GetFile(path).Tags.Sum(t => t.GetImageMetadata().Count());
    private static bool Same(string? a, string? b) => string.Equals(
        string.IsNullOrWhiteSpace(a) ? null : a.Trim(),
        string.IsNullOrWhiteSpace(b) ? null : b.Trim(),
        StringComparison.Ordinal);

    private static int? ExpectedTrackNumber(IngestTrackPlan track) =>
        track.HadTrackNumber ? track.TrackNumber : null;

    private static int? ExpectedTrackTotal(IngestTrackPlan track) =>
        track.HadTrackNumber ? track.TrackTotal : null;

    private static void EnsureFresh(IngestPlan plan)
    {
        foreach (var source in plan.Albums.SelectMany(a => a.Sources)) EnsureFresh(source);
        if (plan.Configuration.RemoveNonMusicAfterIngest)
            foreach (var source in plan.IgnoredFileSnapshots.Where(item =>
                         plan.Configuration.SidecarDispositionFor(
                             item.Path, plan.Request.SourceDirectory) is
                             LibrarySidecarDisposition.Quarantine or
                             LibrarySidecarDisposition.Delete))
                EnsureFresh(source);
        if (plan.Albums.Count > 0 && plan.ItunesLibrarySnapshot is not null)
            EnsureFresh(plan.ItunesLibrarySnapshot);
    }

    private string? CurrentPolicyFingerprint(IngestPlan plan)
    {
        AppConfigurationSnapshot? snapshot = _settings?.GetSnapshot();
        if (snapshot?.Configuration is { } active &&
            (string.IsNullOrWhiteSpace(plan.Request.ConfigurationPath) ||
             snapshot.ConfigPath is not null && string.Equals(
                 Path.GetFullPath(snapshot.ConfigPath),
                 Path.GetFullPath(plan.Request.ConfigurationPath),
                 OperatingSystem.IsWindows()
                     ? StringComparison.OrdinalIgnoreCase
                     : StringComparison.Ordinal)))
            return active.PolicySnapshot.Fingerprint;

        if (plan.Request.ConfigurationPath is not { Length: > 0 } path ||
            !File.Exists(path))
            return null;
        try
        {
            XDocument document = XDocument.Load(path);
            return document.Root?.Name.LocalName == "LibraryConfiguration"
                ? new LibraryConfiguration(path).PolicySnapshot.Fingerprint
                : null;
        }
        catch
        {
            // Freshness validation reports changed/missing source files separately. A malformed
            // policy file must still prevent Apply rather than silently using the preview policy.
            return "invalid-policy-file";
        }
    }

    private static void EnsureFresh(IngestFileSnapshot source)
    {
        var info = new FileInfo(source.Path);
        if (!info.Exists || info.Length != source.Length || info.LastWriteTimeUtc != source.LastWriteTimeUtc)
            throw new InvalidOperationException($"Source changed since preview; preview again: {source.Path}");
    }

    private static IngestOutputPlan Output(IngestTrackPlan track, IngestOutputKind kind, string source, string destination, bool derive = false)
        => new() { Identity = track.Identity, Kind = kind, Metadata = track, SourcePath = source, DestinationPath = destination, DeriveCd = derive };

    private static string ClaimCanonical(string root, IngestTrackPlan track, string extension,
        IngestMusicConfiguration config, HashSet<string> claimed)
    {
        string artist = track.EffectiveAlbumArtist.LimitLength(config.LengthLimit).FixPath();
        string album = track.Album.FormatDisc(config.LengthLimit, config.DiscNumLengthLimit).FixPath();
        string title = track.Title.LimitLength(config.LengthLimit).FixPath();
        string relative = Path.Combine(artist, album, $"{track.TrackNumber:D2} {title}");
        string destination = Path.Combine(root, relative + extension).Normalize();
        int suffix = 2;
        while (!claimed.Add(destination))
            destination = Path.Combine(root, relative + $"_{suffix++}" + extension).Normalize();
        return destination;
    }

    private static string ClaimItunesCanonical(string mediaFolder, IngestTrackPlan track,
        HashSet<string> claimed)
    {
        string destination = ItlMediaOrganization.CanonicalMusicPath(mediaFolder,
            track.AlbumArtist, track.Artist, track.Album, track.TrackNumber, track.Title,
            track.Compilation, discNumber: null);
        string directory = Path.GetDirectoryName(destination)!;
        string extension = Path.GetExtension(destination);
        string stem = Path.GetFileNameWithoutExtension(destination);
        int suffix = 1;
        string candidate = destination;
        while (!claimed.Add(candidate))
            candidate = Path.Combine(directory, $"{stem} {suffix++}{extension}").Normalize();
        return candidate;
    }

    private static string AlbumKey(string artist, string album) => $"{NormalizeKey(artist)}\u001f{NormalizeKey(album)}";
    private static string TrackKey(string artist, string album, int disc, int track, string title)
        => $"{AlbumKey(artist, album)}\u001f{disc}\u001f{track}\u001f{NormalizeKey(title)}";
    private static string NormalizeKey(string value) => value.Trim().ToUpperInvariant();
    private static string SafeToken(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16];

    private static bool PathsOverlap(string first, string second)
    {
        string a = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string b = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, PathComparison) || b.StartsWith(a, PathComparison);
    }

    private static void WriteJournal(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        foreach (string line in lines) writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record PreviewFile(IngestFileSnapshot Snapshot, bool Supported);

    private sealed record PreparedArtworkTransfer(
        ArtworkInput Input,
        ArtworkService.PreparedArtwork Prepared);

    private sealed record StagedSidecarArtifact(
        string StagePath,
        string DestinationPath);

    private sealed record PreviewFileResult(
        ScannedTrack? Track,
        IngestConflict? Conflict,
        IngestFileSnapshot? IgnoredSnapshot)
    {
        public static PreviewFileResult Scanned(ScannedTrack track) => new(track, null, null);
        public static PreviewFileResult Failed(IngestConflict conflict) => new(null, conflict, null);
        public static PreviewFileResult Ignored(IngestFileSnapshot snapshot) => new(null, null, snapshot);
    }

    private sealed record ScannedTrack(
        string Path,
        string Artist,
        string? AlbumArtist,
        string BaseAlbum,
        string Title,
        int TrackNumber,
        bool HadTrackNumber,
        int? TrackTotal,
        int? DiscNumber,
        int? DiscTotal,
        uint SampleRate,
        uint BitsPerSample,
        uint Channels,
        uint Duration,
        bool IsAlac,
        bool IsLossless,
        string CodecName,
        string Extension,
        bool Compilation,
        IngestFileSnapshot Snapshot)
    {
        public string EffectiveAlbumArtist =>
            !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist : Artist;
    }
}
