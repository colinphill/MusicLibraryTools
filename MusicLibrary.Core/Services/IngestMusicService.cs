using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
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
        string[] destinations = [config.AacDestination, config.CdDestination, config.PairedCdDestination, config.HighResolutionDestination];
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
            if (!extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
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
            index => scanResults[index] = ScanPreviewFile(scanFiles[index].Snapshot));

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
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in scanned.GroupBy(t => AlbumKey(t.AlbumArtist, t.BaseAlbum)))
        {
            ct.ThrowIfCancellationRequested();
            var sourceTracks = group.ToList();
            string display = $"{sourceTracks[0].AlbumArtist} — {sourceTracks[0].BaseAlbum}";
            int before = conflicts.Count;
            var discs = sourceTracks.Where(t => t.DiscNumber.HasValue).Select(t => t.DiscNumber!.Value).Distinct().Order().ToArray();
            bool multiDisc = discs.Length > 1;
            if (sourceTracks.Any(t => t.DiscNumber.HasValue) && sourceTracks.Any(t => !t.DiscNumber.HasValue))
                conflicts.Add(new IngestConflict(group.Key, sourceRoot, "An album mixes tracks with and without DiscNumber."));
            foreach (var slot in sourceTracks.GroupBy(t => (Disc: t.DiscNumber ?? 1, t.TrackNumber)))
            {
                string[] titles = slot.Select(t => NormalizeKey(t.Title)).Distinct().ToArray();
                if (titles.Length > 1)
                    conflicts.Add(new IngestConflict(group.Key, slot.First().Path,
                        $"Disc {slot.Key.Disc}, track {slot.Key.TrackNumber} has conflicting titles."));
            }

            var trackPlans = new List<IngestTrackPlan>();
            foreach (var discGroup in sourceTracks.GroupBy(t => t.DiscNumber ?? 1))
            {
                int offset = multiDisc ? discGroup.Min(t => t.TrackNumber) - 1 : 0;
                int total = discGroup.Max(t => t.TrackNumber - offset);
                foreach (var track in discGroup)
                {
                    int normalizedTrack = track.TrackNumber - offset;
                    string normalizedAlbum = multiDisc ? $"{track.BaseAlbum} (Disc {discGroup.Key})" : track.BaseAlbum;
                    string identity = TrackKey(track.AlbumArtist, track.BaseAlbum, discGroup.Key, track.TrackNumber, track.Title);
                    trackPlans.Add(new IngestTrackPlan
                    {
                        Identity = identity,
                        SourcePath = track.Path,
                        Title = track.Title,
                        Artist = track.Artist,
                        AlbumArtist = track.AlbumArtist,
                        Album = normalizedAlbum,
                        TrackNumber = normalizedTrack,
                        TrackTotal = total,
                        OriginalDiscNumber = discGroup.Key,
                        SampleRate = track.SampleRate,
                        BitsPerSample = track.BitsPerSample,
                        Channels = track.Channels,
                        DurationInSeconds = track.Duration,
                        IsAlac = track.IsAlac,
                        IsHighResolution = track.SampleRate > 44100 || track.BitsPerSample > 16,
                        Compilation = track.Compilation,
                    });
                }
            }

            foreach (var identity in trackPlans.GroupBy(t => t.Identity))
            {
                if (identity.Count(t => !t.IsHighResolution) > 1)
                    conflicts.Add(new IngestConflict(group.Key, identity.First().SourcePath,
                        $"Multiple CD-quality sources match '{identity.First().Title}'."));
            }
            if (conflicts.Count != before)
                continue;

            bool hasHigh = trackPlans.Any(t => t.IsHighResolution);
            string cdRoot = hasHigh ? config.PairedCdDestination : config.CdDestination;
            var outputs = new List<IngestOutputPlan>();
            var missing = new List<string>();
            foreach (var identity in trackPlans.GroupBy(t => t.Identity))
            {
                var candidates = identity.ToList();
                foreach (var high in candidates.Where(t => t.IsHighResolution)
                             .OrderByDescending(t => t.SampleRate).ThenByDescending(t => t.BitsPerSample).ThenBy(t => t.SourcePath))
                {
                    string destination = ClaimCanonical(config.HighResolutionDestination, high, ".flac", config, claimed);
                    outputs.Add(Output(high, IngestOutputKind.HighResolutionFlac, high.SourcePath, destination));
                }

                var cd = candidates.SingleOrDefault(t => !t.IsHighResolution);
                bool derive = cd is null;
                if (derive)
                {
                    cd = candidates.Where(t => t.IsHighResolution)
                        .OrderByDescending(t => t.SampleRate).ThenByDescending(t => t.BitsPerSample)
                        .ThenBy(t => t.SourcePath, StringComparer.OrdinalIgnoreCase).First();
                    missing.Add($"{cd.TrackNumber:D2} {cd.Title}");
                }
                var selectedCd = cd!;
                string cdDestination = ClaimCanonical(cdRoot, selectedCd, ".flac", config, claimed);
                outputs.Add(Output(selectedCd, IngestOutputKind.CdFlac, selectedCd.SourcePath, cdDestination, derive));
                string aacDestination = itunesMediaFolder is null
                    ? ClaimCanonical(config.AacDestination, selectedCd, ".m4a", config, claimed)
                    : ClaimItunesCanonical(itunesMediaFolder, selectedCd, claimed);
                outputs.Add(Output(selectedCd, IngestOutputKind.Aac, selectedCd.SourcePath, aacDestination));
            }

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
            files.Add(new IngestFileSummary(file, "Unsupported/non-audio",
                config.RemoveNonMusicAfterIngest
                    ? PlanSourceDisposition(config)
                    : "Source → Leave unchanged"));

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
        };
    }

    private static PreviewFileResult ScanPreviewFile(IngestFileSnapshot snapshot)
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
            if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) && !alac)
                return PreviewFileResult.Ignored(snapshot);
            if (codec.CodecType != CodecType.Lossless)
                return PreviewFileResult.Ignored(snapshot);
            string artist = (tag.Artist ?? "").Trim();
            string albumArtist = (tag.AlbumArtist ?? "").Trim();
            string album = (tag.Album ?? "").Trim();
            string title = (tag.Title ?? "").Trim();
            if (string.IsNullOrWhiteSpace(albumArtist)) albumArtist = artist;
            string? compilationValue = tag.GetKnownMetadata()
                .FirstOrDefault(field => field.Key == TagFields.Compilation).Value;
            bool compilation = compilationValue is not null &&
                (compilationValue.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                 compilationValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                 compilationValue.Equals("yes", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(albumArtist) ||
                string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(title) || tag.TrackNumber is null)
                throw new InvalidDataException("Artist, album artist, album, title, and track number are required.");
            if (codec.Channels != 2)
                throw new InvalidDataException($"Only stereo input is supported (found {codec.Channels} channels).");
            if (codec.Samplerate < 44100 || codec.BitsPerSample < 16)
                throw new InvalidDataException($"Below-CD-quality input is unsupported ({codec.Samplerate} Hz/{codec.BitsPerSample}-bit).");

            var suffix = DiscSuffix.Match(album);
            string baseAlbum = suffix.Success ? suffix.Groups["album"].Value.Trim() : album;
            return PreviewFileResult.Scanned(new ScannedTrack(
                path, artist, albumArtist, baseAlbum, title, tag.TrackNumber.Value,
                tag.DiscNumber, codec.Samplerate, codec.BitsPerSample, codec.Channels,
                codec.DurationInSeconds, alac, compilation, snapshot));
        }
        catch (Exception ex)
        {
            return PreviewFileResult.Failed(new IngestConflict(path, path, ex.Message));
        }
    }

    public async Task<IngestResult> ApplyAsync(IngestPlan plan, IReadOnlyList<IngestApprovalDecision> approvals,
        IProgress<IngestProgress>? progress = null, CancellationToken ct = default)
    {
        if (!plan.CanApply)
            return new IngestResult([], true, "The preview contains conflicts or no applicable ingest or cleanup work.");
        var decisions = approvals.GroupBy(a => a.AlbumKey).ToDictionary(g => g.Key, g => g.Last().Approved, StringComparer.OrdinalIgnoreCase);
        foreach (var required in plan.RequiredApprovals)
            if (!decisions.TryGetValue(required.AlbumKey, out bool approved) || !approved)
                return new IngestResult([], true, $"CD-quality derivation was not approved for {required.AlbumDisplay}; nothing was changed.");

        bool hasAlbums = plan.Albums.Count > 0;
        if (hasAlbums && plan.ItunesLibrarySnapshot is not null)
            ItlFileEditor.EnsureItunesIsClosed();
        EnsureFresh(plan);
        if (hasAlbums)
        {
            await _ffmpeg.PreflightAsync(plan.Configuration.FfmpegPath, plan.Configuration.AacEncoder, ct);
            EnsureFresh(plan);
        }

        string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        string quarantineRoot = plan.Request.SourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + ".IngestMusic-quarantine" + Path.DirectorySeparatorChar + runId;
        var results = new List<IngestAlbumResult>();
        int completed = 0;
        bool cleanupNonMusic = plan.Configuration.RemoveNonMusicAfterIngest &&
            (plan.IgnoredFileSnapshots.Count > 0 || plan.SourceDirectories.Count > 0);
        int cleanupItems = cleanupNonMusic ? plan.IgnoredFileSnapshots.Count + 1 : 0;
        int total = plan.Albums.Sum(a => a.Outputs.Count + 1) + cleanupItems;
        foreach (var album in plan.Albums)
        {
            ct.ThrowIfCancellationRequested();
            int completedDuringAlbum = completed;
            progress?.Report(new IngestProgress(album.Display, "Staging outputs", completed, total));
            try
            {
                int installed = await ApplyAlbumAsync(plan, album, quarantineRoot, runId, (output, staged) =>
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
            completed++;
            progress?.Report(new IngestProgress("Non-music cleanup", "Empty folders removed", completed, total));
        }
        return new IngestResult(results, false);
    }

    private static void RemoveNonMusic(IngestPlan plan, string quarantineRoot, Action<string> fileCompleted,
        CancellationToken ct)
    {
        foreach (var source in plan.IgnoredFileSnapshots) EnsureFresh(source);
        var plannedMoves = plan.IgnoredFileSnapshots.Select(source =>
        {
            string relative = Path.GetRelativePath(plan.Request.SourceDirectory, source.Path);
            return (Original: source.Path, Quarantine: Path.Combine(quarantineRoot, relative));
        }).ToList();
        var moved = new List<(string Original, string Quarantine)>();
        string journalPath = Path.Combine(quarantineRoot, "journal.tsv");
        bool journalStarted = false;
        try
        {
            WriteJournal(journalPath,
                ["BEGIN\tNON_MUSIC_CLEANUP",
                 .. plannedMoves.Select(move => plan.Configuration.DeleteSourcesAfterIngest
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

            if (!plan.Configuration.DeleteSourcesAfterIngest)
            {
                foreach (string directory in plan.SourceDirectories.OrderBy(path => path.Length))
                {
                    ct.ThrowIfCancellationRequested();
                    string relative = Path.GetRelativePath(plan.Request.SourceDirectory, directory);
                    Directory.CreateDirectory(Path.Combine(quarantineRoot, relative));
                }
            }

            foreach (string directory in plan.SourceDirectories.OrderByDescending(path => path.Length))
            {
                ct.ThrowIfCancellationRequested();
                try { if (Directory.Exists(directory)) Directory.Delete(directory); }
                catch (IOException) { /* A new or unprocessed entry keeps this source folder in place. */ }
            }

            WriteJournal(journalPath,
                [.. moved.Select(move => plan.Configuration.DeleteSourcesAfterIngest
                     ? $"STAGE_DELETE\tNON_MUSIC\t{move.Original}\t{move.Quarantine}"
                     : $"QUARANTINE\tNON_MUSIC\t{move.Original}\t{move.Quarantine}"),
                 "COMMIT\tNON_MUSIC_CLEANUP"]);

            if (plan.Configuration.DeleteSourcesAfterIngest)
            {
                foreach (var move in moved)
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

    private async Task<int> ApplyAlbumAsync(IngestPlan plan, IngestAlbumPlan album, string quarantineRoot, string runId,
        Action<IngestOutputPlan, bool> outputProgress, CancellationToken ct)
    {
        foreach (var source in album.Sources) EnsureFresh(source);
        var staged = new ConcurrentDictionary<IngestOutputPlan, string>();
        var cdStages = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var installed = new List<string>();
        var quarantined = new List<(string Original, string Quarantine)>();
        var stageRoots = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
        string journalPath = Path.Combine(quarantineRoot, "journal.tsv");
        bool journalStarted = false;
        bool libraryCommitted = false;
        try
        {
            await Parallel.ForEachAsync(album.Outputs.Where(o => o.Kind != IngestOutputKind.Aac), parallel, async (output, token) =>
            {
                outputProgress(output, false);
                string root = output.Kind == IngestOutputKind.HighResolutionFlac
                    ? plan.Configuration.HighResolutionDestination
                    : album.HasHighResolution ? plan.Configuration.PairedCdDestination : plan.Configuration.CdDestination;
                string stageRoot = Path.Combine(root, ".IngestMusic-staging", runId, SafeToken(album.Key));
                Directory.CreateDirectory(stageRoot);
                stageRoots.TryAdd(stageRoot, 0);
                string stage = Path.Combine(stageRoot, Guid.NewGuid().ToString("N") + ".flac");
                if (output.DeriveCd)
                    await _ffmpeg.DeriveCdFlacAsync(plan.Configuration.FfmpegPath, output.SourcePath, stage, token);
                else if (output.Metadata.IsAlac)
                    await _ffmpeg.ConvertAlacToFlacAsync(plan.Configuration.FfmpegPath, output.SourcePath, stage, token);
                else
                    File.Copy(output.SourcePath, stage);
                Normalize(stage, output.Metadata, output.SourcePath);
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
                Normalize(stage, output.Metadata, output.SourcePath);
                Validate(stage, output);
                staged[output] = stage;
                outputProgress(output, true);
            });

            foreach (var output in album.Outputs)
            {
                string stage = staged[output];
                if (!File.Exists(output.DestinationPath)) continue;
                if (!await EquivalentAsync(plan.Configuration.FfmpegPath, stage, output.DestinationPath, output, ct))
                    throw new IOException($"Destination exists with different content: {output.DestinationPath}");
            }

            foreach (var source in album.Sources) EnsureFresh(source);
            var plannedQuarantine = album.Sources.Select(source =>
            {
                string relative = Path.GetRelativePath(plan.Request.SourceDirectory, source.Path);
                return (Original: source.Path, Quarantine: Path.Combine(quarantineRoot, relative));
            }).ToList();
            string[] mutationCandidates =
            [
                .. album.Outputs.Select(output => output.DestinationPath),
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
                 .. plannedQuarantine.Select(q => plan.Configuration.DeleteSourcesAfterIngest
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

            foreach (var move in plannedQuarantine)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(move.Quarantine)!);
                if (File.Exists(move.Quarantine)) throw new IOException($"Quarantine collision: {move.Quarantine}");
                File.Move(move.Original, move.Quarantine);
                quarantined.Add(move);
            }

            string[] commitJournal =
                [.. installed.Select(path => $"INSTALL\t{album.Key}\t{path}"),
                 .. quarantined.Select(q => plan.Configuration.DeleteSourcesAfterIngest
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
            if (plan.Configuration.DeleteSourcesAfterIngest)
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
            return installed.Count;
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
            foreach (string root in stageRoots.Keys.OrderByDescending(p => p.Length))
                CleanupStageDirectories(root);
        }
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
        => configuration.DeleteSourcesAfterIngest
            ? "Source → Delete after successful ingest"
            : "Source → Quarantine after successful ingest";

    private static string OutputName(IngestOutputKind kind) => kind switch
    {
        IngestOutputKind.HighResolutionFlac => "Hi-Res FLAC",
        IngestOutputKind.CdFlac => "CD FLAC",
        IngestOutputKind.Aac => "AAC",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private async Task<bool> EquivalentAsync(string ffmpeg, string staged, string existing, IngestOutputPlan output, CancellationToken ct)
    {
        try
        {
            Validate(existing, output);
            string first = await _ffmpeg.ComputeDecodedAudioHashAsync(ffmpeg, staged, ct);
            string second = await _ffmpeg.ComputeDecodedAudioHashAsync(ffmpeg, existing, ct);
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) && ArtworkCount(staged) == ArtworkCount(existing);
        }
        catch { return false; }
    }

    private static void Normalize(string path, IngestTrackPlan track, string artworkSource)
    {
        var media = MediaFile.GetFile(path);
        IMetadataWriter writer = media as IMetadataWriter
            ?? media.Tags.FirstOrDefault() as IMetadataWriter
            ?? throw new InvalidDataException($"Output tag format is not writable: {path}");
        writer.SetField(TagFields.Title, track.Title);
        writer.SetField(TagFields.Artist, track.Artist);
        writer.SetField(TagFields.AlbumArtist, track.AlbumArtist);
        writer.SetField(TagFields.Album, track.Album);
        writer.SetField(TagFields.TrackNumber, track.TrackNumber.ToString());
        writer.SetField(TagFields.TotalTracks, track.TrackTotal.ToString());
        if (track.Compilation)
            writer.SetField(TagFields.Compilation, "1");
        else
            writer.RemoveField(TagFields.Compilation);
        writer.RemoveField(TagFields.DiscNumber);
        writer.RemoveField(TagFields.TotalDiscs);

        if (media is IArtworkWriter artworkWriter)
        {
            var images = MediaFile.GetFile(artworkSource).Tags.SelectMany(t => t.GetImageMetadata())
                .Select(i => new ArtworkImage(ParsePictureType(i.Category), NormalizeMime(i.ImageType), i.Description ?? "", i.Data))
                .ToList();
            artworkWriter.SetImages(images);
        }
        media.SaveTags();
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
        uint rate = output.Kind == IngestOutputKind.HighResolutionFlac ? output.Metadata.SampleRate : 44100;
        uint bits = output.Kind == IngestOutputKind.HighResolutionFlac ? output.Metadata.BitsPerSample : 16;
        if (codec.Samplerate != rate || (output.Kind != IngestOutputKind.Aac && codec.BitsPerSample != bits))
            throw new InvalidDataException($"Generated file has unexpected audio format: {path}");
        if (codec.Channels != 2 || (output.Kind == IngestOutputKind.Aac ? codec.CodecType != CodecType.Lossy : codec.CodecType != CodecType.Lossless))
            throw new InvalidDataException($"Generated file has unexpected codec/channels: {path}");
        if (!Same(tag.Title, output.Metadata.Title) || !Same(tag.Album, output.Metadata.Album) ||
            !Same(tag.AlbumArtist, output.Metadata.AlbumArtist) || tag.TrackNumber != output.Metadata.TrackNumber ||
            tag.TrackTotal != output.Metadata.TrackTotal || tag.DiscNumber is not null || tag.DiscTotal is not null)
            throw new InvalidDataException($"Generated file metadata validation failed: {path}");
        int delta = Math.Abs((int)codec.DurationInSeconds - (int)output.Metadata.DurationInSeconds);
        if (delta > 1) throw new InvalidDataException($"Generated file duration changed unexpectedly: {path}");
    }

    private static int ArtworkCount(string path) => MediaFile.GetFile(path).Tags.Sum(t => t.GetImageMetadata().Count());
    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.Ordinal);

    private static void EnsureFresh(IngestPlan plan)
    {
        foreach (var source in plan.Albums.SelectMany(a => a.Sources)) EnsureFresh(source);
        if (plan.Configuration.RemoveNonMusicAfterIngest)
            foreach (var source in plan.IgnoredFileSnapshots) EnsureFresh(source);
        if (plan.Albums.Count > 0 && plan.ItunesLibrarySnapshot is not null)
            EnsureFresh(plan.ItunesLibrarySnapshot);
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
        string artist = track.AlbumArtist.LimitLength(config.LengthLimit).FixPath();
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
            track.AlbumArtist, track.Artist, track.Album, track.TrackNumber, track.Title, track.Compilation);
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
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
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

    private sealed record PreviewFile(IngestFileSnapshot Snapshot, bool Supported);

    private sealed record PreviewFileResult(
        ScannedTrack? Track,
        IngestConflict? Conflict,
        IngestFileSnapshot? IgnoredSnapshot)
    {
        public static PreviewFileResult Scanned(ScannedTrack track) => new(track, null, null);
        public static PreviewFileResult Failed(IngestConflict conflict) => new(null, conflict, null);
        public static PreviewFileResult Ignored(IngestFileSnapshot snapshot) => new(null, null, snapshot);
    }

    private sealed record ScannedTrack(string Path, string Artist, string AlbumArtist, string BaseAlbum,
        string Title, int TrackNumber, int? DiscNumber, uint SampleRate, uint BitsPerSample,
        uint Channels, uint Duration, bool IsAlac, bool Compilation, IngestFileSnapshot Snapshot);
}
