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
    private readonly IWavpackRunner _wavpack;
    private readonly IAppSettings? _settings;
    private readonly int _previewParallelism;
    private readonly IItunesMediaMutationService _itunes;

    public IngestMusicService(
        IFfmpegRunner ffmpeg,
        IAppSettings? settings = null,
        int? previewParallelism = null,
        IItunesMediaMutationService? itunes = null,
        IWavpackRunner? wavpack = null)
    {
        _ffmpeg = ffmpeg;
        _wavpack = wavpack ?? new WavpackRunner();
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
        => PreviewAsync(request, progress: null, ct);

    public Task<IngestPlan> PreviewAsync(
        IngestRequest request,
        IProgress<IngestProgress>? progress,
        CancellationToken ct = default)
        => Task.Run(() => Preview(request, progress, ct), ct);

    private IngestPlan Preview(
        IngestRequest request,
        IProgress<IngestProgress>? progress,
        CancellationToken ct)
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
        string[] destinations = enabledRecipes
            .Select(recipe => config.ResolveTarget(recipe)?.Target)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(PathComparer)
            .ToArray();
        string[] explicitSources = request.SourceFiles?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray() ?? [];
        if (explicitSources.Length > 0 &&
            explicitSources.Any(path =>
                !IsWithin(path, sourceRoot)))
            throw new InvalidDataException(
                "Every selected ingest file must be inside the source directory.");
        IEnumerable<string> isolationRoots =
            explicitSources.Length == 0
                ? [sourceRoot]
                : explicitSources
                    .Select(path =>
                        Path.GetDirectoryName(path)!)
                    .Distinct(PathComparer);
        if (destinations.Any(destination =>
                isolationRoots.Any(source =>
                    PathsOverlap(source, destination))))
            throw new InvalidDataException("The source directory must not overlap an ingestion destination.");
        if (destinations.SelectMany((a, i) => destinations.Skip(i + 1).Select(b => (a, b))).Any(p => PathsOverlap(p.a, p.b)))
            throw new InvalidDataException("Ingestion destination directories must not overlap each other.");
        var sourceDirectories = new List<string>();
        var scanFiles = new List<PreviewFile>();
        int discoveredFiles = 0;
        // One buffered traversal supplies paths, size/timestamp snapshots, and the directory list.
        // The previous implementation walked the whole tree once for files, once for directories,
        // then issued a FileInfo metadata request for every file -- particularly costly over SMB.
        void AddFile(
            string path,
            long size,
            DateTime modified)
        {
            ct.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path);
            var snapshot = new IngestFileSnapshot(
                path,
                size,
                modified);
            discoveredFiles++;
            if (discoveredFiles == 1 || (discoveredFiles & 31) == 0)
                progress?.Report(new(
                    "Preview",
                    "Discovering source files",
                    discoveredFiles,
                    0,
                    path,
                    IngestFileProgressState.InProgress));
            if (!enabledRecipes.Any(recipe => recipe.InputExtensions.Contains(
                    extension, StringComparer.OrdinalIgnoreCase)))
            {
                scanFiles.Add(new PreviewFile(snapshot, Supported: false));
                return;
            }

            scanFiles.Add(new PreviewFile(snapshot, Supported: true));
        }
        if (explicitSources.Length > 0)
        {
            foreach (string path in explicitSources)
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        "A selected ingest file is unavailable.",
                        path);
                var info = new FileInfo(path);
                AddFile(
                    info.FullName,
                    info.Length,
                    info.LastWriteTimeUtc);
            }
        }
        else
        {
            foreach (var entry in new MusicFileEnumerator(
                         sourceRoot,
                         skipItlpPackages: false))
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FileType == MFEType.Directory)
                {
                    sourceDirectories.Add(entry.Name);
                    continue;
                }
                AddFile(
                    entry.Name,
                    entry.Size,
                    entry.Modified);
            }
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
        bool inferSuffixForGrouping = enabledRecipes.Any(recipe =>
            config.ResolveDestinationProfile(recipe).Disc.InferAlbumSuffix);
        int completedScans = scanFiles.Count - supportedIndexes.Count;
        progress?.Report(new(
            "Preview",
            "Reading source metadata",
            completedScans,
            scanFiles.Count));
        object progressSync = new();

        // Parsing is independent per file. Bound the global reader count so high-latency opens can
        // overlap without allowing a large incoming tree to flood the share or retain unbounded tag
        // buffers. Results are written by index and merged below in original enumeration order.
        Parallel.ForEach(
            supportedIndexes,
            new ParallelOptions { MaxDegreeOfParallelism = _previewParallelism, CancellationToken = ct },
            index =>
            {
                PreviewFile file = scanFiles[index];
                scanResults[index] = ScanPreviewFile(
                    file.Snapshot, ingestProfile, enabledRecipes,
                    inferSuffixForGrouping);
                int completed = Interlocked.Increment(
                    ref completedScans);
                lock (progressSync)
                    progress?.Report(new(
                        "Preview",
                        "Reading source metadata",
                        completed,
                        scanFiles.Count,
                        file.Snapshot.Path,
                        IngestFileProgressState.Completed));
            });

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
            BuildRecipeOutputs(config, trackPlans, enabledRecipes,
                itunesMediaFolder, claimed, claimedSidecars, outputs,
                conflicts, missing, group.Key, hasHigh);
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
                            (output.AddToMediaCatalog ? ", add to media catalog" : "") +
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

        var plan = new IngestPlan
        {
            Request = request with
            {
                SourceDirectory = sourceRoot,
                ConfigurationPath = resolved.ConfigurationPath,
                SourceFiles =
                    explicitSources.Length == 0
                        ? null
                        : explicitSources,
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
        progress?.Report(new(
            "Preview",
            "Preview complete",
            scanFiles.Count,
            scanFiles.Count));
        return plan;
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
                    SourceAlbum = track.SourceAlbum,
                    InferredBaseAlbum = track.InferredBaseAlbum,
                    TrackNumber = projectedTrack,
                    HadTrackNumber = track.HadTrackNumber ||
                        profile.Disc.Strategy == LibraryDiscStrategy.FlattenContinuous,
                    OriginalTrackNumber = track.HadTrackNumber ? track.TrackNumber : null,
                    HadOriginalTrackNumber = track.HadTrackNumber,
                    TrackTotal = projectedTotal,
                    OriginalDiscNumber = discGroup.Key,
                    HadDiscNumber = track.DiscNumber.HasValue ||
                        profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools,
                    TaggedDiscNumber = track.TaggedDiscNumber,
                    InferredDiscNumber = track.InferredDiscNumber,
                    ProjectedDiscNumber = profile.Disc.Strategy ==
                                              LibraryDiscStrategy.PreserveTags &&
                                          (track.DiscNumber.HasValue ||
                                           profile.Preset ==
                                           LibraryProfilePreset.LegacyMusicLibraryTools)
                        ? discGroup.Key
                        : null,
                    ProjectedDiscTotal = profile.Disc.Strategy ==
                                             LibraryDiscStrategy.PreserveTags
                        ? track.DiscTotal
                        : null,
                    PathDiscNumber = PathDiscNumber(profile.Disc.Strategy,
                        track.DiscNumber.HasValue || profile.Preset ==
                        LibraryProfilePreset.LegacyMusicLibraryTools, discGroup.Key),
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

    private static IngestTrackPlan ProjectForDestination(
        IngestTrackPlan selected,
        IReadOnlyList<IngestTrackPlan> albumTracks,
        LibraryProfile profile)
    {
        int? EffectiveDisc(IngestTrackPlan track) => track.TaggedDiscNumber is > 0
            ? track.TaggedDiscNumber
            : profile.Disc.InferAlbumSuffix && track.InferredDiscNumber is > 0
                ? track.InferredDiscNumber
                : null;

        bool legacy = profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools;
        int? selectedEffectiveDisc = EffectiveDisc(selected);
        bool hadDiscNumber = selectedEffectiveDisc is > 0 || legacy;
        int discNumber = selectedEffectiveDisc ??
                         (selected.OriginalDiscNumber > 0
                             ? selected.OriginalDiscNumber
                             : 1);
        int[] representedDiscs = albumTracks
            .Select(EffectiveDisc)
            .Where(number => number is > 0)
            .Select(number => number!.Value)
            .Distinct()
            .ToArray();
        bool multiDisc = representedDiscs.Length > 1;

        int OriginalTrack(IngestTrackPlan track) =>
            track.OriginalTrackNumber ?? track.TrackNumber;
        bool HadOriginalTrack(IngestTrackPlan track) =>
            track.HadOriginalTrackNumber ||
            track.OriginalTrackNumber is null && track.HadTrackNumber;
        int DiscSlot(IngestTrackPlan track) => EffectiveDisc(track) ??
            (legacy && track.OriginalDiscNumber > 0 ? track.OriginalDiscNumber : 1);

        var flattened = albumTracks
            .Select(track => (
                Disc: DiscSlot(track),
                Track: OriginalTrack(track),
                MissingKey: HadOriginalTrack(track) ? "" : track.SourcePath))
            .Distinct()
            .OrderBy(slot => slot.Disc)
            .ThenBy(slot => slot.Track)
            .ThenBy(slot => slot.MissingKey, PathComparer)
            .Select((slot, index) => (slot, Number: index + 1))
            .ToDictionary(item => item.slot, item => item.Number);
        IngestTrackPlan[] discTracks = albumTracks.Where(track =>
            DiscSlot(track) == discNumber).ToArray();
        int[] numberedTracks = discTracks
            .Where(HadOriginalTrack)
            .Select(OriginalTrack)
            .Where(number => legacy || number > 0)
            .ToArray();
        int legacyOffset = legacy && multiDisc && numberedTracks.Length > 0
            ? numberedTracks.Min() - 1
            : 0;
        int inferredDiscTrackTotal = numberedTracks.Length == 0
            ? 0
            : numberedTracks.Max() - legacyOffset;
        bool hadOriginalTrack = HadOriginalTrack(selected);
        int originalTrack = OriginalTrack(selected);
        bool flatten = profile.Disc.Strategy == LibraryDiscStrategy.FlattenContinuous;
        int projectedTrack = flatten
            ? flattened[(discNumber, originalTrack,
                hadOriginalTrack ? "" : selected.SourcePath)]
            : hadOriginalTrack ? originalTrack - legacyOffset : 0;
        int projectedTotal = profile.Disc.TrackTotalScope == LibraryTrackTotalScope.Album
            ? flattened.Count
            : legacy
                ? inferredDiscTrackTotal
                : selected.OriginalTrackTotal is > 0
                    ? selected.OriginalTrackTotal.Value
                    : inferredDiscTrackTotal;

        string sourceAlbum = string.IsNullOrWhiteSpace(selected.SourceAlbum)
            ? selected.Album
            : selected.SourceAlbum;
        string baseAlbum = profile.Disc.InferAlbumSuffix &&
                           !string.IsNullOrWhiteSpace(selected.InferredBaseAlbum)
            ? selected.InferredBaseAlbum
            : sourceAlbum;
        string projectedAlbum = profile.Disc.Strategy == LibraryDiscStrategy.AlbumSuffix &&
                                multiDisc && hadDiscNumber
            ? WithDiscSuffix(baseAlbum, discNumber)
            : baseAlbum;
        bool preserveDiscTags = profile.Disc.Strategy == LibraryDiscStrategy.PreserveTags;

        return selected with
        {
            Album = projectedAlbum,
            TrackNumber = projectedTrack,
            HadTrackNumber = hadOriginalTrack || flatten,
            TrackTotal = projectedTotal,
            OriginalDiscNumber = discNumber,
            HadDiscNumber = hadDiscNumber,
            ProjectedDiscNumber = preserveDiscTags && hadDiscNumber
                ? discNumber
                : null,
            ProjectedDiscTotal = preserveDiscTags
                ? selected.OriginalDiscTotal
                : null,
            PathDiscNumber = PathDiscNumber(
                profile.Disc.Strategy, hadDiscNumber, discNumber),
        };
    }

    private static int? PathDiscNumber(
        LibraryDiscStrategy strategy,
        bool hadDiscNumber,
        int discNumber) => hadDiscNumber && strategy is
        LibraryDiscStrategy.PreserveTags or
        LibraryDiscStrategy.DiscFolder or
        LibraryDiscStrategy.FileNamePrefix
            ? discNumber
            : null;

    private static string WithDiscSuffix(string album, int discNumber)
    {
        Match suffix = DiscSuffix.Match(album);
        return suffix.Success && int.TryParse(
                   suffix.Groups["disc"].Value, out int existingDisc) &&
               existingDisc == discNumber
            ? album
            : $"{album} (Disc {discNumber})";
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
        List<string> missing,
        string albumKey,
        bool albumHasHighResolution)
    {
        foreach (IGrouping<string, IngestTrackPlan> identity in trackPlans.GroupBy(
                     track => track.Identity))
        {
            foreach (LibraryIngestRecipe recipe in recipes)
            {
                if (!AlbumMatches(recipe.AlbumCondition, albumHasHighResolution))
                    continue;
                IngestTrackPlan[] candidates = identity
                    .Where(track => RecipeMatches(recipe, track))
                    .ToArray();
                IngestTrackPlan? selected = SelectRecipeSource(
                    candidates, recipe.SourceSelection);
                if (selected is null)
                    continue;
                bool usedHighResolutionFallback =
                    recipe.SourceSelection == LibraryIngestSourceSelection.PreferCdQuality &&
                    selected.IsHighResolution &&
                    !candidates.Any(track => !track.IsHighResolution);
                if (usedHighResolutionFallback && recipe.RequireFallbackApproval)
                    missing.Add($"{selected.TrackNumber:D2} {selected.Title}");

                LibraryIndexLocation? target = config.ResolveTarget(recipe);
                bool catalogTarget = target is null && recipe.AddToMediaCatalog &&
                    !string.IsNullOrWhiteSpace(itunesMediaFolder);
                if (target is null && !catalogTarget)
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
                int? outputChannels = ResolveOutputChannels(
                    recipe.OutputChannels, selected.Channels);
                LibraryIngestAction action = ResolveRecipeAction(
                    recipe, selected, extension, outputChannels);
                if (WavpackDsdConstraintError(
                        recipe, selected, extension, outputChannels) is { } wavpackError)
                {
                    conflicts.Add(new(albumKey, selected.SourcePath, wavpackError));
                    continue;
                }
                LibraryProfile destinationProfile =
                    config.ResolveDestinationProfile(recipe);
                IngestTrackPlan projected = ProjectForDestination(
                    selected, trackPlans, destinationProfile);
                LibraryProfile namingProfile = destinationProfile;
                if (recipe.CollisionPolicy is LibraryPathCollisionPolicy collision)
                    namingProfile = namingProfile with
                    {
                        Naming = namingProfile.Naming with { CollisionPolicy = collision },
                    };
                string destination;
                try
                {
                    destination = catalogTarget
                        ? ClaimItunesCanonical(itunesMediaFolder!, projected, claimed)
                        : ClaimProfilePath(target!.Target, projected, extension, config,
                            namingProfile, claimed);
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException)
                {
                    conflicts.Add(new(albumKey, selected.SourcePath, error.Message));
                    continue;
                }
                if (recipe.AddToMediaCatalog &&
                    (string.IsNullOrWhiteSpace(itunesMediaFolder) ||
                     !IsWithin(destination, itunesMediaFolder)))
                {
                    conflicts.Add(new(albumKey, destination,
                        $"Ingest recipe '{recipe.Name}' requests catalog insertion, but its " +
                        "destination is outside the configured iTunes Media folder."));
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
                    Metadata = projected,
                    SourcePath = selected.SourcePath,
                    DestinationPath = destination,
                    DestinationRoot = target?.Target ?? itunesMediaFolder,
                    RecipeId = recipe.Id,
                    Action = action,
                    OutputCodec = recipe.Codec,
                    Encoder = recipe.Encoder,
                    ExtraFfmpegOptions = recipe.ExtraFfmpegOptions,
                    AddToMediaCatalog = recipe.AddToMediaCatalog,
                    BitrateKbps = recipe.BitrateKbps,
                    SampleRateHz = recipe.SampleRateHz,
                    BitsPerSample = recipe.BitsPerSample,
                    OutputChannels = outputChannels,
                    DeriveCd = usedHighResolutionFallback,
                    PreserveMetadata = recipe.PreserveMetadata,
                    PreserveArtwork = recipe.PreserveArtwork,
                    PreserveDiscTags = destinationProfile.Disc.Strategy ==
                        LibraryDiscStrategy.PreserveTags,
                    OutputRepresentationRole = recipe.OutputRepresentationRole,
                    ArtworkPolicy = destinationProfile.Artwork,
                    MetadataPolicy = destinationProfile.Metadata,
                    DiscPolicy = destinationProfile.Disc,
                    ArtworkArtifacts = artworkArtifacts,
                });
            }
        }
    }

    private static bool AlbumMatches(
        LibraryIngestAlbumCondition condition,
        bool hasHighResolution) => condition switch
        {
            LibraryIngestAlbumCondition.Any => true,
            LibraryIngestAlbumCondition.HasHighResolution => hasHighResolution,
            LibraryIngestAlbumCondition.HasNoHighResolution => !hasHighResolution,
            _ => false,
        };

    private static IngestTrackPlan? SelectRecipeSource(
        IReadOnlyList<IngestTrackPlan> candidates,
        LibraryIngestSourceSelection selection)
    {
        IEnumerable<IngestTrackPlan> ordered = candidates
            .OrderByDescending(track => track.SampleRate)
            .ThenByDescending(track => track.BitsPerSample)
            .ThenBy(track => track.SourcePath, StringComparer.OrdinalIgnoreCase);
        if (selection == LibraryIngestSourceSelection.PreferCdQuality)
            return ordered.FirstOrDefault(track => !track.IsHighResolution) ??
                   ordered.FirstOrDefault();
        return ordered.FirstOrDefault();
    }

    private static int? ResolveOutputChannels(
        LibraryChannelSelection? selection,
        uint sourceChannels) => selection switch
        {
            LibraryChannelSelection.Stereo => 2,
            LibraryChannelSelection.Multi => checked((int)sourceChannels),
            null => null,
            _ => throw new ArgumentOutOfRangeException(nameof(selection)),
        };

    private static LibraryIngestAction ResolveRecipeAction(
        LibraryIngestRecipe recipe,
        IngestTrackPlan source,
        string outputExtension,
        int? outputChannels)
    {
        if (recipe.Action != LibraryIngestAction.Transcode ||
            !source.SourceExtension.Equals(outputExtension,
                StringComparison.OrdinalIgnoreCase))
            return recipe.Action;

        string sourceCodec = NormalizeCodecName(source.CodecName);
        string outputCodec = NormalizeCodecName(
            recipe.Codec ?? outputExtension.TrimStart('.'));
        if (sourceCodec.Length == 0 ||
            !sourceCodec.Equals(outputCodec, StringComparison.OrdinalIgnoreCase))
            return recipe.Action;

        bool audioFormatChanges =
            recipe.SampleRateHz is int sampleRate &&
                source.SampleRate != (uint)sampleRate ||
            recipe.BitsPerSample is int bitsPerSample &&
                source.BitsPerSample != (uint)bitsPerSample ||
            outputChannels is int channels &&
                source.Channels != (uint)channels;
        bool encodingRequested =
            recipe.BitrateKbps is not null ||
            !string.IsNullOrWhiteSpace(recipe.Encoder) ||
            !string.IsNullOrWhiteSpace(recipe.ExtraFfmpegOptions);
        return audioFormatChanges || encodingRequested
            ? recipe.Action
            : LibraryIngestAction.Copy;
    }

    private static string NormalizeCodecName(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "m4a" => "aac",
            "ogg" or "libvorbis" => "vorbis",
            "wavpack" => "wv",
            var codec => codec,
        };

    private static string? WavpackDsdConstraintError(
        LibraryIngestRecipe recipe,
        IngestTrackPlan source,
        string outputExtension,
        int? outputChannels)
    {
        if (!IsWavpackDsdTranscode(
                recipe.Action, source.SourceExtension, outputExtension, recipe.Codec))
            return null;
        if (recipe.SampleRateHz is int sampleRate &&
            source.SampleRate != (uint)sampleRate)
            return $"WavPack DSD preserves the source sample rate of " +
                   $"{source.SampleRate:N0} Hz; recipe '{recipe.Name}' requests " +
                   $"{sampleRate:N0} Hz.";
        if (recipe.BitsPerSample is int bitsPerSample &&
            source.BitsPerSample != (uint)bitsPerSample)
            return $"WavPack DSD preserves the source bit depth of " +
                   $"{source.BitsPerSample}; recipe '{recipe.Name}' requests " +
                   $"{bitsPerSample}.";
        if (outputChannels is int channels &&
            source.Channels != (uint)channels)
            return $"WavPack DSD preserves the source channel count of " +
                   $"{source.Channels}; recipe '{recipe.Name}' requests {channels}.";
        return null;
    }

    private static bool IsWavpackDsdTranscode(
        LibraryIngestAction action,
        string sourceExtension,
        string outputExtension,
        string? codec) =>
        action == LibraryIngestAction.Transcode &&
        sourceExtension.Equals(".dsf", StringComparison.OrdinalIgnoreCase) &&
        outputExtension.Equals(".wv", StringComparison.OrdinalIgnoreCase) &&
        NormalizeCodecName(codec ?? "wv") == "wv";

    private static bool UsesWavpackDsd(IngestOutputPlan output) =>
        IsWavpackDsdTranscode(
            output.Action,
            output.Metadata.SourceExtension,
            Path.GetExtension(output.DestinationPath),
            output.OutputCodec);

    private static bool RecipeMatches(
        LibraryIngestRecipe recipe,
        IngestTrackPlan track)
    {
        if (!recipe.InputExtensions.Contains(
                track.SourceExtension, StringComparer.OrdinalIgnoreCase))
            return false;
        if (recipe.RequireLossless is bool lossless && track.IsLossless != lossless)
            return false;
        if (!ChannelsMatch(recipe.InputChannels, track.Channels))
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

    private static bool ChannelsMatch(
        LibraryChannelSelection? selection,
        uint channels) => selection switch
        {
            LibraryChannelSelection.Stereo => channels == 2,
            LibraryChannelSelection.Multi => channels > 2,
            null => true,
            _ => false,
        };

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
        IReadOnlyList<LibraryIngestRecipe> recipes,
        bool inferSuffixForGrouping)
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
            string? inferredBaseAlbum = suffix.Success
                ? suffix.Groups["album"].Value.Trim()
                : null;
            int? inferredDiscNumber = suffix.Success && int.TryParse(
                suffix.Groups["disc"].Value, out int parsedDisc)
                    ? parsedDisc
                    : null;
            bool inferSuffix = inferredBaseAlbum is not null && inferSuffixForGrouping;
            string baseAlbum = inferSuffix ? suffix.Groups["album"].Value.Trim() : album;
            int? taggedDiscNumber = tag.DiscNumber;
            int? discNumber = taggedDiscNumber ?? (inferSuffix ? inferredDiscNumber : null);
            return PreviewFileResult.Scanned(new ScannedTrack(
                path, artist, albumArtist, album, baseAlbum, inferredBaseAlbum, title,
                tag.TrackNumber ?? 0,
                legacy ? tag.TrackNumber.HasValue : tag.TrackNumber is > 0,
                tag.TrackTotal, discNumber, taggedDiscNumber, inferredDiscNumber,
                tag.DiscTotal, codec.Samplerate,
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
        if (!ChannelsMatch(recipe.InputChannels, codec.Channels))
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
        IngestOutputPlan[] plannedOutputs = plan.Albums
            .SelectMany(album => album.Outputs).ToArray();
        bool needsFfmpeg = plannedOutputs.Any(output =>
            output.Action != LibraryIngestAction.Copy && !UsesWavpackDsd(output));
        bool needsWavpack = plannedOutputs.Any(UsesWavpackDsd);
        string? automaticAacEncoder = null;
        if (hasAlbums && needsFfmpeg)
        {
            string[] encoders = IngestPreflightService.RequiredEncoders(plan.Configuration);
            if (encoders.Length == 0)
                encoders = [""];
            foreach (string encoder in encoders)
                await _ffmpeg.PreflightAsync(plan.Configuration.FfmpegPath, encoder, ct);
            bool needsAutomaticAac = plan.Albums.SelectMany(album => album.Outputs).Any(output =>
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
        if (hasAlbums && needsWavpack)
        {
            await _wavpack.PreflightAsync(plan.Configuration.WavpackPath, ct);
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
                        .Where(output => output.AddToMediaCatalog)
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

            case LibraryIngestAction.Transcode when UsesWavpackDsd(output):
                await _wavpack.EncodeDsdAsync(
                    configuration.WavpackPath,
                    output.SourcePath,
                    stage,
                    ct).ConfigureAwait(false);
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
                        output.OutputChannels)
                    {
                        ExtraOptions = output.ExtraFfmpegOptions,
                    },
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
                                   IsIdentityMetadataProjection(output.Metadata,
                                       discPolicy) &&
                                   identityArtwork;
        IReadOnlyList<PreparedArtworkTransfer> transfers;
        if (!exactPreservingCopy)
            transfers = Normalize(stage, output.Metadata, output.SourcePath,
                discPolicy,
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
        // File.Copy preserves the source attributes on Windows. A read-only source therefore
        // produces a read-only staging file, even though staging is intentionally mutable while
        // destination metadata and artwork are projected. Never alter the source; only clear the
        // attribute on the private staged copy immediately before saving its tags.
        FileAttributes attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
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
                foreach (KeyValuePair<string, string> field in
                         sourceStrings.GetAddressableUserStrings())
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
        int? projectedDiscNumber = ExpectedDiscNumber(track, discPolicy);
        int? projectedDiscTotal = ExpectedDiscTotal(track, discPolicy);
        if (projectedDiscNumber is > 0)
        {
            writer.SetField(TagFields.DiscNumber, projectedDiscNumber.Value.ToString());
            if (projectedDiscTotal is > 0)
                writer.SetField(TagFields.TotalDiscs, projectedDiscTotal.Value.ToString());
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
                !NormalizeCodecName(codec.CodecName).Equals(
                    NormalizeCodecName(output.OutputCodec),
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
            LibraryDiscPolicy outputDiscPolicy = output.DiscPolicy ??
                (output.PreserveDiscTags
                    ? new LibraryDiscPolicy(LibraryDiscStrategy.PreserveTags,
                        LibraryTrackTotalScope.PerDisc, false)
                    : new LibraryDiscPolicy(LibraryDiscStrategy.AlbumSuffix,
                        LibraryTrackTotalScope.PerDisc, false));
            if (tag.DiscNumber != ExpectedDiscNumber(output.Metadata, outputDiscPolicy) ||
                tag.DiscTotal != ExpectedDiscTotal(output.Metadata, outputDiscPolicy))
                throw new InvalidDataException(
                    $"Recipe output disc metadata does not match its destination projection: " +
                    path);
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

    private static int? ExpectedDiscNumber(
        IngestTrackPlan track,
        LibraryDiscPolicy policy) => track.ProjectedDiscNumber is > 0
        ? track.ProjectedDiscNumber
        : policy.Strategy == LibraryDiscStrategy.PreserveTags &&
          track.HadDiscNumber && track.OriginalDiscNumber > 0
            ? track.OriginalDiscNumber
            : null;

    private static int? ExpectedDiscTotal(
        IngestTrackPlan track,
        LibraryDiscPolicy policy) => ExpectedDiscNumber(track, policy) is not null
        ? track.ProjectedDiscTotal ?? track.OriginalDiscTotal
        : null;

    private static bool IsIdentityMetadataProjection(
        IngestTrackPlan track,
        LibraryDiscPolicy policy)
    {
        string sourceAlbum = string.IsNullOrWhiteSpace(track.SourceAlbum)
            ? track.Album
            : track.SourceAlbum;
        int? originalTrack = track.HadOriginalTrackNumber
            ? track.OriginalTrackNumber
            : track.HadTrackNumber
                ? track.TrackNumber
                : null;
        return Same(track.Album, sourceAlbum) &&
               ExpectedTrackNumber(track) == originalTrack &&
               ExpectedDiscNumber(track, policy) == track.TaggedDiscNumber &&
               ExpectedDiscTotal(track, policy) == track.OriginalDiscTotal;
    }

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

    private static string ClaimItunesCanonical(string mediaFolder, IngestTrackPlan track,
        HashSet<string> claimed)
    {
        string destination = ItlMediaOrganization.CanonicalMusicPath(mediaFolder,
            track.AlbumArtist, track.Artist, track.Album, track.TrackNumber, track.Title,
            track.Compilation, discNumber: track.PathDiscNumber);
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

    private static bool IsWithin(string path, string root)
    {
        string candidate = Path.GetFullPath(path);
        string parent = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return candidate.StartsWith(parent, PathComparison);
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
        string SourceAlbum,
        string BaseAlbum,
        string? InferredBaseAlbum,
        string Title,
        int TrackNumber,
        bool HadTrackNumber,
        int? TrackTotal,
        int? DiscNumber,
        int? TaggedDiscNumber,
        int? InferredDiscNumber,
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
