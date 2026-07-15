using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using iTunes.Binary;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record SmartStorageSourceTrack(
    int Id,
    string PersistentId,
    string? LocalPath,
    string Kind,
    bool HasVideo,
    string Artist,
    string AlbumArtist,
    string Album,
    string Title,
    string Genre,
    int TrackNumber,
    int Year);

public sealed record SmartStorageSourcePlaylist(
    string Name,
    bool IsMaster,
    IReadOnlyList<int> TrackIds);

public sealed record SmartStorageLibrarySnapshot(
    string? MusicFolderPath,
    IReadOnlyList<SmartStorageSourceTrack> Tracks,
    IReadOnlyList<SmartStorageSourcePlaylist> Playlists);

public interface ISmartStorageLibraryLoader
{
    Task<SmartStorageLibrarySnapshot> LoadAsync(string? libraryPath,
        CancellationToken ct = default);
}

public sealed class SmartStorageLibraryLoader : ISmartStorageLibraryLoader
{
    public Task<SmartStorageLibrarySnapshot> LoadAsync(string? libraryPath,
        CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        ItlLibrary library = ItlLibrary.Load(ItlFileEditor.ResolveLibraryPath(libraryPath));
        return new SmartStorageLibrarySnapshot(library.MusicFolderPath,
            library.Tracks.Select(track => new SmartStorageSourceTrack(track.Id,
                track.PersistentIdString, track.LocalPath, track.Kind ?? "", track.HasVideo,
                track.Artist ?? "", track.AlbumArtist ?? "", track.Album ?? "",
                track.Title ?? "", track.Genre ?? "", track.TrackNumber, track.Year)).ToArray(),
            library.Playlists.Select(playlist => new SmartStorageSourcePlaylist(
                playlist.DisplayName, playlist.IsMaster, playlist.TrackIds)).ToArray());
    }, ct);
}

public sealed record SmartStorageRequest(
    string Destination,
    bool Initialize = false,
    int MaxRemovals = 0,
    string? ItunesLibraryPath = null);

public sealed record SmartStoragePlan(
    SmartStorageRequest Request,
    string DestinationRoot,
    int LibraryTrackCount,
    int InstalledTrackCount,
    int UnchangedTrackCount,
    int StaleTrackCount,
    int PlaylistCount,
    int ArtworkCount,
    FileInventory SourceInventory,
    FileInventory DestinationInventory,
    IReadOnlyList<OperationPathSnapshot> ReviewedSourceSnapshots,
    FileMutationPlan MutationPlan,
    IReadOnlyList<OperationIssue> Issues)
{
    public bool CanApply => MutationPlan.CanApply;
}

public sealed record SmartStorageResult(
    int LibraryTrackCount,
    int PlaylistCount,
    int ArtworkCount,
    FileMutationSummary Mutations,
    IReadOnlyList<OperationIssue> Issues);

public interface ISmartStorageService
{
    Task<SmartStoragePlan> PreviewAsync(SmartStorageRequest request,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<SmartStorageResult> ApplyAsync(SmartStoragePlan plan,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Projects an iTunes library into the stable bucketed smart-storage format. Preview builds the
/// complete databases and playlists in memory and records their exact bytes; apply only validates
/// the reviewed inventories and executes the journaled file plan.
/// </summary>
public sealed class SmartStorageService : ISmartStorageService
{
    private const int BucketThreshold = 200;
    private const int MaxMappedNameLength = 40;
    private const int BucketDigits = 4;
    private const int MaxPlaylistCount = 500;
    private const string TargetMarker = ".update-smart-storage-root";
    private const string LegacyCommitManifest = ".update-smart-storage-database-commit";
    private readonly ISmartStorageLibraryLoader _libraries;
    private readonly IFileInventoryService _inventories;
    private readonly IFileMutationPlanExecutor _executor;

    public SmartStorageService(ISmartStorageLibraryLoader libraries,
        IFileInventoryService inventories, IFileMutationPlanExecutor executor)
    {
        _libraries = libraries;
        _inventories = inventories;
        _executor = executor;
    }

    public async Task<SmartStoragePlan> PreviewAsync(SmartStorageRequest request,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxRemovals < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxRemovals cannot be negative.");
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Destination));
        var issues = ValidateDestinationRoot(destination);
        progress?.Report(new(OperationPhase.LoadingLibrary, Message: "Loading iTunes library"));
        SmartStorageLibrarySnapshot library = await _libraries.LoadAsync(
            request.ItunesLibraryPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(library.MusicFolderPath))
            issues.Add(new("missing-music-root", OperationIssueSeverity.Blocker,
                "The iTunes library does not contain a source music folder."));
        else if (PathsOverlap(destination, library.MusicFolderPath))
            issues.Add(new("root-overlap", OperationIssueSeverity.Blocker,
                "The destination overlaps the iTunes music folder.", destination));
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return EmptyPlan(request, destination, issues);

        string musicRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library.MusicFolderPath!));
        progress?.Report(new(OperationPhase.IndexingSources,
            Message: "Inventorying source and smart-storage destination"));
        Task<FileInventory> sourceTask = _inventories.CaptureAsync(musicRoot, null, progress, ct);
        Task<FileInventory> destinationTask = _inventories.CaptureAsync(destination, null, progress, ct);
        await Task.WhenAll(sourceTask, destinationTask).ConfigureAwait(false);
        FileInventory sourceInventory = await sourceTask.ConfigureAwait(false);
        FileInventory destinationInventory = await destinationTask.ConfigureAwait(false);

        bool markerExists = destinationInventory.Files.ContainsKey(Path.Combine(destination, TargetMarker));
        bool databaseExists = destinationInventory.Files.ContainsKey(Path.Combine(destination, "filedb.xml"));
        if (!Directory.Exists(destination) && !request.Initialize)
            issues.Add(new("destination-missing", OperationIssueSeverity.Blocker,
                "The destination does not exist; preview again with Initialize enabled.", destination));
        if (!markerExists && !databaseExists && !request.Initialize)
            issues.Add(new("destination-unmanaged", OperationIssueSeverity.Blocker,
                "The destination has no smart-storage marker or database; enable Initialize after verifying it.",
                destination));
        if (destinationInventory.Files.ContainsKey(Path.Combine(destination, LegacyCommitManifest)))
            issues.Add(new("legacy-commit-interrupted", OperationIssueSeverity.Blocker,
                "A legacy database commit manifest is present. Restore its .bak generation before migrating this target.",
                Path.Combine(destination, LegacyCommitManifest)));
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return EmptyPlan(request, destination, issues, sourceInventory, destinationInventory);

        return await Task.Run(() => BuildPlan(request, destination, library, sourceInventory,
            destinationInventory, markerExists, issues, progress, ct), ct).ConfigureAwait(false);
    }

    public async Task<SmartStorageResult> ApplyAsync(SmartStoragePlan plan,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException("The smart-storage plan contains blocking issues.");
        progress?.Report(new(OperationPhase.Validating,
            Message: "Re-inventorying smart-storage inputs before the first write"));
        Task<FileInventory> sourceTask = _inventories.CaptureAsync(
            plan.SourceInventory.Root, null, progress, ct);
        Task<FileInventory> destinationTask = _inventories.CaptureAsync(
            plan.DestinationInventory.Root, null, progress, ct);
        await Task.WhenAll(sourceTask, destinationTask).ConfigureAwait(false);
        FileInventory currentSource = await sourceTask.ConfigureAwait(false);
        EnsureInventoryUnchanged(plan.DestinationInventory,
            await destinationTask.ConfigureAwait(false), "destination");
        foreach (OperationPathSnapshot snapshot in plan.ReviewedSourceSnapshots)
        {
            if (snapshot.Path is not null && currentSource.Files.TryGetValue(
                    snapshot.Path, out OperationPathSnapshot? current))
                EnsureSnapshot(snapshot, current);
            else
                ValidateSnapshot(snapshot);
        }
        FileMutationSummary mutations = await _executor.ApplyAsync(
            plan.MutationPlan, progress, ct).ConfigureAwait(false);
        return new(plan.LibraryTrackCount, plan.PlaylistCount, plan.ArtworkCount,
            mutations, plan.Issues);
    }

    private static SmartStoragePlan BuildPlan(SmartStorageRequest request, string destination,
        SmartStorageLibrarySnapshot library, FileInventory sourceInventory,
        FileInventory destinationInventory, bool markerExists, List<OperationIssue> issues,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new(OperationPhase.Planning, Message: "Loading existing smart-storage catalog"));
        SmartFileDatabase database = LoadFileDatabase(destination, destinationInventory);
        List<SmartArtwork> existingArtwork = LoadArtwork(destination, destinationInventory, database);
        string[] previousPlaylistFiles = database.Playlists
            .Select(playlist => FixPath(playlist.Name) + ".m3u").ToArray();

        var externalSnapshots = new List<OperationPathSnapshot>();
        var incoming = new SmartFileDatabase();
        SmartStorageSourceTrack[] eligibleTracks = library.Tracks.Where(IsLibraryTrackEligible).ToArray();
        int processed = 0;
        foreach (SmartStorageSourceTrack sourceTrack in eligibleTracks)
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(sourceTrack.LocalPath!);
            OperationPathSnapshot snapshot = SourceSnapshot(path, sourceInventory, externalSnapshots);
            if (!snapshot.Exists)
            {
                issues.Add(new("missing-source", OperationIssueSeverity.Blocker,
                    "A library source file is missing.", path));
                continue;
            }
            string artist = string.IsNullOrEmpty(sourceTrack.AlbumArtist)
                ? sourceTrack.Artist : sourceTrack.AlbumArtist;
            incoming.FindArtist(artist).FindAlbum(sourceTrack.Album).Tracks.Add(new SmartTrack
            {
                Index = sourceTrack.TrackNumber,
                LastModifiedTime = snapshot.LastWriteTimeUtc,
                Loc = path,
                FileName = Path.GetFileName(path),
                Name = sourceTrack.Title,
                Year = sourceTrack.Year,
                ContributingArtist = sourceTrack.Artist,
                PersistentID = sourceTrack.PersistentId,
                Genre = sourceTrack.Genre,
            });
            if ((++processed & 0xff) == 0)
                progress?.Report(new(OperationPhase.Planning, processed, eligibleTracks.Length,
                    path, "Mapping library tracks"));
        }
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return CreatePlan(request, destination, sourceInventory, destinationInventory,
                externalSnapshots, [], eligibleTracks.Length, 0, 0, 0, 0, issues);

        try
        {
            database.Map(incoming);
            database.Bucketize();
        }
        catch (Exception exception)
        {
            issues.Add(new("catalog-map-failed", OperationIssueSeverity.Blocker, exception.Message));
            return CreatePlan(request, destination, sourceInventory, destinationInventory,
                externalSnapshots, [], eligibleTracks.Length, 0, 0, 0, 0, issues);
        }

        string[] plannedPlaylistNames = library.Playlists
            .Where(playlist => playlist.TrackIds.Count <= MaxPlaylistCount && !playlist.IsMaster)
            .Select(playlist => FixPath(playlist.Name) + ".m3u").ToArray();
        ValidateMappedNames(database, plannedPlaylistNames, issues);
        int staleCount = database.Artists.SelectMany(artist => artist.Albums)
            .SelectMany(album => album.Tracks).Count(track => !track.Touched);
        if (staleCount > request.MaxRemovals)
            issues.Add(new("removal-limit", OperationIssueSeverity.Blocker,
                $"The catalog removes {staleCount:N0} tracks, exceeding MaxRemovals {request.MaxRemovals:N0}."));
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return CreatePlan(request, destination, sourceInventory, destinationInventory,
                externalSnapshots, [], eligibleTracks.Length, 0, 0, staleCount, 0, issues);

        var oldTrackPaths = database.Artists.SelectMany(artist => artist.Albums.Select(album => (artist, album)))
            .SelectMany(item => item.album.Tracks.Where(track => !track.Touched)
                .Select(track => (Path: TrackPath(destination, item.artist, item.album, track), Track: track)))
            .ToArray();
        var stalePathSet = oldTrackPaths.Select(item => item.Path).ToHashSet(PathComparer);
        RemoveStale(database);
        ValidateCurrentMappings(database, issues);

        var allTracks = database.Artists.SelectMany(artist => artist.Albums.SelectMany(album =>
                album.Tracks.Select(track => new CatalogTrack(artist, album, track))))
            .ToArray();
        var byPersistentId = allTracks.ToLookup(item => item.Track.PersistentID,
            StringComparer.OrdinalIgnoreCase);
        var sourceTracksById = library.Tracks.ToDictionary(track => track.Id);
        foreach (SmartStorageSourcePlaylist playlist in library.Playlists
                     .Where(playlist => playlist.TrackIds.Count <= MaxPlaylistCount && !playlist.IsMaster))
        {
            foreach (int trackId in playlist.TrackIds)
            {
                if (!sourceTracksById.TryGetValue(trackId, out SmartStorageSourceTrack? track))
                {
                    issues.Add(new("playlist-track-missing", OperationIssueSeverity.Blocker,
                        $"Playlist '{playlist.Name}' references missing track {trackId}."));
                    continue;
                }
                if (IsPlaylistTrackEligible(track) && byPersistentId[track.PersistentId].Count() != 1)
                    issues.Add(new("playlist-track-ambiguous", OperationIssueSeverity.Blocker,
                        $"Playlist track {track.PersistentId} cannot be mapped uniquely."));
            }
        }
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return CreatePlan(request, destination, sourceInventory, destinationInventory,
                externalSnapshots, [], eligibleTracks.Length, 0, 0, staleCount, 0, issues);

        var artworkByHash = existingArtwork.ToDictionary(artwork => artwork.Hash,
            StringComparer.Ordinal);
        var usedArtwork = new HashSet<string>(StringComparer.Ordinal);
        foreach (CatalogTrack item in allTracks)
        {
            ct.ThrowIfCancellationRequested();
            if (!item.Track.New && !string.IsNullOrEmpty(item.Track.ArtworkHash) &&
                artworkByHash.ContainsKey(item.Track.ArtworkHash))
            {
                usedArtwork.Add(item.Track.ArtworkHash);
                continue;
            }
            string hash = ReadArtwork(item.Track.Loc, artworkByHash);
            item.Track.ArtworkHash = hash;
            if (!string.IsNullOrEmpty(hash)) usedArtwork.Add(hash);
        }

        var actions = new List<FileMutationAction>();
        string recoveryRoot = destination + ".UpdateSmartStorage-quarantine" +
            Path.DirectorySeparatorChar + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var desiredMediaPaths = allTracks.ToDictionary(item =>
            TrackPath(destination, item.Artist, item.Album, item.Track), PathComparer);
        foreach ((string oldPath, _) in oldTrackPaths)
        {
            if (desiredMediaPaths.ContainsKey(oldPath)) continue;
            if (destinationInventory.Files.TryGetValue(oldPath, out OperationPathSnapshot? snapshot))
                actions.Add(Quarantine(oldPath, destination, recoveryRoot, snapshot));
        }

        int installed = 0, unchanged = 0;
        foreach ((string target, CatalogTrack item) in desiredMediaPaths.OrderBy(pair => pair.Key, PathComparer))
        {
            bool needsInstall = item.Track.New || stalePathSet.Contains(target) ||
                                !destinationInventory.Files.ContainsKey(target);
            if (!needsInstall) { unchanged++; continue; }
            OperationPathSnapshot sourceSnapshot = SourceSnapshot(
                item.Track.Loc, sourceInventory, externalSnapshots);
            OperationPathSnapshot destinationSnapshot = destinationInventory.Files.TryGetValue(
                target, out OperationPathSnapshot? existing) ? existing : OperationPathSnapshot.Missing(target);
            actions.Add(new(destinationSnapshot.Exists ? FileMutationKind.Replace : FileMutationKind.Copy,
                item.Track.Loc, target, sourceSnapshot, destinationSnapshot));
            installed++;
        }

        string playlistsDirectory = Path.Combine(destination, "Playlists");
        database.Playlists.Clear();
        var desiredPlaylistFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SmartStorageSourcePlaylist playlist in library.Playlists)
        {
            if (playlist.TrackIds.Count > MaxPlaylistCount || playlist.IsMaster) continue;
            var filePlaylist = new SmartPlaylist { Name = playlist.Name };
            using var stream = new MemoryStream();
            using (var writer = new StreamWriter(stream, Encoding.UTF8, 4096, leaveOpen: true))
            {
                writer.WriteLine("#EXTM3U");
                foreach (int trackId in playlist.TrackIds)
                {
                    SmartStorageSourceTrack sourceTrack = sourceTracksById[trackId];
                    if (!IsPlaylistTrackEligible(sourceTrack)) continue;
                    CatalogTrack mapped = byPersistentId[sourceTrack.PersistentId].Single();
                    filePlaylist.Items.Add(new SmartPlaylistItem
                    {
                        Artist = database.Artists.IndexOf(mapped.Artist),
                        Album = mapped.Artist.Albums.IndexOf(mapped.Album),
                        Track = mapped.Album.Tracks.IndexOf(mapped.Track),
                    });
                    writer.WriteLine("#EXTINF:-1," + sourceTrack.Artist.Replace("-", "") +
                        " - " + sourceTrack.Title.Replace("-", ""));
                    writer.WriteLine(Path.GetRelativePath(playlistsDirectory,
                        TrackPath(destination, mapped.Artist, mapped.Album, mapped.Track)));
                }
            }
            if (filePlaylist.Items.Count == 0) continue;
            database.Playlists.Add(filePlaylist);
            string fileName = FixPath(playlist.Name) + ".m3u";
            desiredPlaylistFiles.Add(fileName);
            AddGeneratedAction(actions, Path.Combine(playlistsDirectory, fileName),
                stream.ToArray(), destinationInventory);
        }
        foreach (string stalePlaylist in previousPlaylistFiles.Except(
                     desiredPlaylistFiles, StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(playlistsDirectory, stalePlaylist);
            if (destinationInventory.Files.TryGetValue(path, out OperationPathSnapshot? snapshot))
                actions.Add(Quarantine(path, destination, recoveryRoot, snapshot));
        }

        List<SmartArtwork> finalArtwork = existingArtwork.Where(artwork =>
                usedArtwork.Contains(artwork.Hash)).ToList();
        foreach (SmartArtwork artwork in artworkByHash.Values)
            if (usedArtwork.Contains(artwork.Hash) && finalArtwork.All(existing =>
                    !StringComparer.Ordinal.Equals(existing.Hash, artwork.Hash)))
                finalArtwork.Add(artwork);
        byte[] artworkBytes = BuildArtworkDatabase(database, finalArtwork);
        byte[] fileDatabaseBytes = Serialize(database);
        AddGeneratedAction(actions, Path.Combine(destination, "artworkdb.bin"),
            artworkBytes, destinationInventory);
        AddGeneratedAction(actions, Path.Combine(destination, "filedb.xml"),
            fileDatabaseBytes, destinationInventory);
        if (!markerExists)
            AddGeneratedAction(actions, Path.Combine(destination, TargetMarker),
                Encoding.UTF8.GetBytes("UpdateSmartStorage managed target" + Environment.NewLine),
                destinationInventory);

        progress?.Report(new(OperationPhase.Completed, actions.Count, actions.Count,
            Message: $"Planned {actions.Count:N0} smart-storage mutation(s)"));
        return CreatePlan(request, destination, sourceInventory, destinationInventory,
            externalSnapshots, actions, eligibleTracks.Length, installed, unchanged, staleCount,
            database.Playlists.Count, issues, finalArtwork.Count, recoveryRoot);
    }

    private static SmartStoragePlan CreatePlan(SmartStorageRequest request, string destination,
        FileInventory sourceInventory, FileInventory destinationInventory,
        IReadOnlyList<OperationPathSnapshot> externalSnapshots,
        IReadOnlyList<FileMutationAction> actions, int libraryTracks, int installed, int unchanged,
        int stale, int playlists, IReadOnlyList<OperationIssue> issues, int artwork = 0,
        string? recoveryRoot = null)
    {
        recoveryRoot ??= destination + ".UpdateSmartStorage-quarantine" +
            Path.DirectorySeparatorChar + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var mutationPlan = new FileMutationPlan("UpdateSmartStorage", destination, recoveryRoot,
            actions, issues, DateTimeOffset.UtcNow);
        return new(request, destination, libraryTracks, installed, unchanged, stale, playlists,
            artwork, sourceInventory, destinationInventory,
            externalSnapshots.DistinctBy(snapshot => snapshot.Path, PathComparer).ToArray(),
            mutationPlan, issues);
    }

    private static SmartStoragePlan EmptyPlan(SmartStorageRequest request, string destination,
        IReadOnlyList<OperationIssue> issues, FileInventory? source = null,
        FileInventory? destinationInventory = null)
    {
        source ??= new("", new Dictionary<string, OperationPathSnapshot>(), [], DateTimeOffset.UtcNow);
        destinationInventory ??= new(destination, new Dictionary<string, OperationPathSnapshot>(), [],
            DateTimeOffset.UtcNow);
        return CreatePlan(request, destination, source, destinationInventory, [], [],
            0, 0, 0, 0, 0, issues);
    }

    private static SmartFileDatabase LoadFileDatabase(string destination, FileInventory inventory)
    {
        string path = Path.Combine(destination, "filedb.xml");
        if (!inventory.Files.ContainsKey(path)) return new();
        using FileStream stream = File.OpenRead(path);
        return Deserialize<SmartFileDatabase>(stream);
    }

    private static List<SmartArtwork> LoadArtwork(string destination, FileInventory inventory,
        SmartFileDatabase database)
    {
        string binary = Path.Combine(destination, "artworkdb.bin");
        if (inventory.Files.ContainsKey(binary))
        {
            using FileStream stream = File.OpenRead(binary);
            var result = new List<SmartArtwork>();
            foreach (SmartArtworkReference reference in database.Artworks)
            {
                if (reference.Offset < 0 || reference.Length < 0 ||
                    reference.Offset + reference.Length > stream.Length)
                    throw new InvalidDataException("The artwork database contains an invalid range.");
                byte[] data = new byte[reference.Length];
                stream.Position = reference.Offset;
                stream.ReadExactly(data);
                result.Add(new(reference.Hash, reference.FileType, data));
            }
            return result;
        }
        string legacyXml = Path.Combine(destination, "artworkdb.xml");
        if (!inventory.Files.ContainsKey(legacyXml)) return [];
        using FileStream legacy = File.OpenRead(legacyXml);
        return Deserialize<SmartArtworkDatabase>(legacy).Artworks;
    }

    private static void RemoveStale(SmartFileDatabase database)
    {
        foreach (SmartArtist artist in database.Artists.ToArray())
        {
            foreach (SmartAlbum album in artist.Albums.ToArray())
            {
                album.Tracks.RemoveAll(track => !track.Touched);
                if (!album.Touched) artist.Albums.Remove(album);
            }
            if (!artist.Touched) database.Artists.Remove(artist);
        }
    }

    private static string ReadArtwork(string path, Dictionary<string, SmartArtwork> artwork)
    {
        try
        {
            IMediaFile media = MediaFile.GetFile(path, readOnly: true);
            IMetadataImage? image = media.Tags.SelectMany(tag => tag.GetImageMetadata()).FirstOrDefault();
            if (image is null || image.Data.Length == 0) return "";
            string fileType = image.ImageType.ToLowerInvariant() switch
            {
                "image/jpeg" or "jpg" or "jpeg" => "jpeg",
                "image/png" or "png" => "png",
                "image/gif" or "gif" => "gif",
                _ => "",
            };
            if (fileType.Length == 0) return "";
            string hash = Convert.ToBase64String(SHA1.HashData(image.Data));
            artwork.TryAdd(hash, new(hash, fileType, image.Data));
            return hash;
        }
        catch { return ""; }
    }

    private static byte[] BuildArtworkDatabase(SmartFileDatabase database,
        IReadOnlyList<SmartArtwork> artworks)
    {
        database.Artworks.Clear();
        using var stream = new MemoryStream();
        foreach (SmartArtwork artwork in artworks)
        {
            long offset = stream.Position;
            stream.Write(artwork.Data);
            database.Artworks.Add(new()
            {
                Hash = artwork.Hash,
                FileType = artwork.FileType,
                Offset = offset,
                Length = artwork.Data.Length,
            });
        }
        return stream.ToArray();
    }

    private static byte[] Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        new XmlSerializer(typeof(T)).Serialize(stream, value);
        return stream.ToArray();
    }

    private static T Deserialize<T>(Stream stream) =>
        (T)(new XmlSerializer(typeof(T)).Deserialize(stream) ??
            throw new InvalidDataException($"Could not deserialize {typeof(T).Name}."));

    private static void AddGeneratedAction(List<FileMutationAction> actions, string path,
        byte[] content, FileInventory inventory)
    {
        OperationPathSnapshot snapshot = inventory.Files.TryGetValue(path,
            out OperationPathSnapshot? existing) ? existing : OperationPathSnapshot.Missing(path);
        if (snapshot.Exists && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
        actions.Add(new(snapshot.Exists ? FileMutationKind.ReplaceGenerated : FileMutationKind.Write,
            "", path, null, snapshot, content.ToImmutableArray()));
    }

    private static FileMutationAction Quarantine(string path, string root, string recoveryRoot,
        OperationPathSnapshot snapshot) => new(FileMutationKind.Quarantine, path,
            Path.Combine(recoveryRoot, Path.GetRelativePath(root, path)), snapshot,
            OperationPathSnapshot.Missing(Path.Combine(recoveryRoot, Path.GetRelativePath(root, path))));

    private static OperationPathSnapshot SourceSnapshot(string path, FileInventory inventory,
        List<OperationPathSnapshot> external)
    {
        path = Path.GetFullPath(path);
        OperationPathSnapshot snapshot = inventory.Files.TryGetValue(path,
            out OperationPathSnapshot? inventoried) ? inventoried : Capture(path);
        external.Add(snapshot);
        return snapshot;
    }

    private static OperationPathSnapshot Capture(string path)
    {
        var file = new FileInfo(path);
        return file.Exists
            ? new(true, false, file.Length, file.LastWriteTimeUtc) { Path = file.FullName }
            : OperationPathSnapshot.Missing(path);
    }

    private static void ValidateSnapshot(OperationPathSnapshot expected)
    {
        if (expected.Path is null) throw new InvalidDataException("A source snapshot has no path.");
        OperationPathSnapshot current = Capture(expected.Path);
        EnsureSnapshot(expected, current);
    }

    private static void EnsureSnapshot(OperationPathSnapshot expected, OperationPathSnapshot current)
    {
        if (current.Exists != expected.Exists || current.Length != expected.Length ||
            current.LastWriteTimeUtc != expected.LastWriteTimeUtc)
            throw new InvalidOperationException($"Source changed after preview: {expected.Path}");
    }

    private static void EnsureInventoryUnchanged(FileInventory expected, FileInventory current,
        string role)
    {
        if (expected.Files.Count != current.Files.Count ||
            expected.Directories.Count != current.Directories.Count)
            throw new InvalidOperationException($"The {role} inventory changed after preview.");
        foreach ((string path, OperationPathSnapshot snapshot) in expected.Files)
            if (!current.Files.TryGetValue(path, out OperationPathSnapshot? candidate) ||
                snapshot.Length != candidate.Length ||
                snapshot.LastWriteTimeUtc != candidate.LastWriteTimeUtc)
                throw new InvalidOperationException($"The {role} inventory changed at '{path}'.");
        if (!expected.Directories.SequenceEqual(current.Directories, PathComparer))
            throw new InvalidOperationException($"The {role} directory inventory changed after preview.");
    }

    private static List<OperationIssue> ValidateDestinationRoot(string destination)
    {
        var issues = new List<OperationIssue>();
        string? root = Path.GetPathRoot(destination);
        if (root is not null && StringComparer.OrdinalIgnoreCase.Equals(
                Path.TrimEndingDirectorySeparator(destination), Path.TrimEndingDirectorySeparator(root)))
            issues.Add(new("filesystem-root", OperationIssueSeverity.Blocker,
                "A filesystem root cannot be used as smart storage.", destination));
        return issues;
    }

    private static bool PathsOverlap(string first, string second)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)) + Path.DirectorySeparatorChar;
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLibraryTrackEligible(SmartStorageSourceTrack track) =>
        !string.IsNullOrWhiteSpace(track.LocalPath) &&
        track.Kind.Contains("audio file", StringComparison.OrdinalIgnoreCase) &&
        !track.Kind.Contains("protected", StringComparison.OrdinalIgnoreCase) &&
        !Path.GetExtension(track.LocalPath).Equals(".m4p", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlaylistTrackEligible(SmartStorageSourceTrack track)
    {
        string kind = track.Kind;
        return !string.IsNullOrWhiteSpace(track.LocalPath) && !track.HasVideo &&
               !kind.Contains("protected", StringComparison.OrdinalIgnoreCase) &&
               !kind.Contains("book", StringComparison.OrdinalIgnoreCase) &&
               !kind.Contains("audible", StringComparison.OrdinalIgnoreCase) &&
               !kind.Contains("document", StringComparison.OrdinalIgnoreCase) &&
               !kind.Contains("app", StringComparison.OrdinalIgnoreCase) &&
               !kind.Contains("tone", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrackPath(string destination, SmartArtist artist,
        SmartAlbum album, SmartTrack track) => Path.Combine(destination,
        artist.Bucket.ToString("D" + BucketDigits), artist.MappedName,
        album.MappedName, track.MappedName);

    private static string FixPath(string item)
    {
        string fixedName = item;
        foreach (char character in Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()))
            fixedName = fixedName.Replace(character.ToString(), "");
        fixedName = fixedName.Replace("\"", "").Trim();
        return fixedName.TrimEnd('.');
    }

    internal static string MapName(string name)
    {
        string mapped = new(name.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(mapped)) return "UNPRINTABLE";
        return mapped.Length > MaxMappedNameLength ? mapped[..MaxMappedNameLength] : mapped;
    }

    private static void ValidateMappedNames(SmartFileDatabase database,
        IEnumerable<string> playlistNames, List<OperationIssue> issues)
    {
        foreach (var group in database.Artists.GroupBy(artist => $"{artist.Bucket}:{artist.MappedName}",
                     StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            issues.Add(new("artist-name-collision", OperationIssueSeverity.Blocker,
                "Artist target collision: " + string.Join(", ", group.Select(artist => artist.Name))));
        foreach (SmartArtist artist in database.Artists)
        {
            foreach (var group in artist.Albums.GroupBy(album => album.MappedName,
                         StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                issues.Add(new("album-name-collision", OperationIssueSeverity.Blocker,
                    $"Album target collision under '{artist.Name}': {group.Key}"));
            foreach (SmartAlbum album in artist.Albums)
                foreach (var group in album.Tracks.GroupBy(track => track.MappedName,
                             StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                    issues.Add(new("track-name-collision", OperationIssueSeverity.Blocker,
                        $"Track target collision under '{artist.Name} / {album.Name}': {group.Key}"));
        }
        foreach (var group in playlistNames.GroupBy(name => name,
                     StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            issues.Add(new("playlist-name-collision", OperationIssueSeverity.Blocker,
                "Playlist target collision: " + group.Key));
    }

    private static void ValidateCurrentMappings(SmartFileDatabase database, List<OperationIssue> issues) =>
        ValidateMappedNames(database, [], issues);

    private sealed record CatalogTrack(SmartArtist Artist, SmartAlbum Album, SmartTrack Track);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [XmlRoot("ArtworkDatabase")]
    public sealed class SmartArtworkDatabase
    {
        [XmlArray("Artworks"), XmlArrayItem("Artwork")]
        public List<SmartArtwork> Artworks { get; set; } = [];
    }

    public sealed record SmartArtwork
    {
        public string Hash { get; set; } = "";
        public string FileType { get; set; } = "";
        public byte[] Data { get; set; } = [];
        public SmartArtwork() { }
        public SmartArtwork(string hash, string fileType, byte[] data) =>
            (Hash, FileType, Data) = (hash, fileType, data);
    }

    [XmlRoot("FileDatabase")]
    public sealed class SmartFileDatabase
    {
        [XmlArray("Playlists"), XmlArrayItem("Playlist")]
        public List<SmartPlaylist> Playlists { get; set; } = [];
        [XmlArray("Artworks"), XmlArrayItem("Artwork")]
        public List<SmartArtworkReference> Artworks { get; set; } = [];
        [XmlArray("Artists"), XmlArrayItem("Artist")]
        public List<SmartArtist> Artists { get; set; } = [];
        public int BucketFormatWidth { get; set; } = BucketDigits;

        public SmartArtist FindArtist(string name)
        {
            SmartArtist? artist = Artists.SingleOrDefault(candidate =>
                candidate.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            if (artist is null)
            {
                artist = new() { Name = name, New = true };
                Artists.Add(artist);
            }
            artist.Touched = true;
            return artist;
        }

        public void Map(SmartFileDatabase incoming)
        {
            foreach (SmartArtist sourceArtist in incoming.Artists)
            {
                SmartArtist artist = FindArtist(sourceArtist.Name);
                artist.MappedName = MapName(artist.Name);
                foreach (SmartAlbum sourceAlbum in sourceArtist.Albums)
                {
                    SmartAlbum album = artist.FindAlbum(sourceAlbum.Name);
                    album.MappedName = MapName(album.Name);
                    foreach (SmartTrack sourceTrack in sourceAlbum.Tracks)
                    {
                        SmartTrack track = album.FindTrack(sourceTrack);
                        track.MappedName = MapName(Path.GetFileNameWithoutExtension(track.FileName)) +
                                           Path.GetExtension(track.FileName);
                    }
                }
            }
        }

        public void Bucketize()
        {
            var buckets = new Dictionary<int, int>();
            foreach (SmartArtist artist in Artists.Where(artist => artist.Bucket != 0))
                buckets[artist.Bucket] = buckets.GetValueOrDefault(artist.Bucket) + 1;
            foreach (SmartArtist artist in Artists.Where(artist => !artist.Touched && artist.Bucket != 0))
                buckets[artist.Bucket]--;
            foreach (SmartArtist artist in Artists.Where(artist => artist.Bucket == 0))
            {
                int bucket = buckets.Where(pair => pair.Value < BucketThreshold)
                    .Select(pair => pair.Key).Order().FirstOrDefault();
                if (bucket == 0)
                {
                    bucket = buckets.Count == 0 ? 1 : buckets.Keys.Max() + 1;
                    buckets[bucket] = 0;
                }
                artist.Bucket = bucket;
                buckets[bucket]++;
            }
        }
    }

    public sealed class SmartArtworkReference
    {
        public string Hash { get; set; } = "";
        public string FileType { get; set; } = "";
        public long Offset { get; set; }
        public int Length { get; set; }
    }

    public sealed class SmartTrack
    {
        public int Index { get; set; }
        [XmlIgnore] public int Year { get; set; }
        public DateTime LastModifiedTime { get; set; }
        [XmlIgnore] public string Loc { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Genre { get; set; } = "";
        public string ArtworkHash { get; set; } = "";
        public string ContributingArtist { get; set; } = "";
        [XmlIgnore] public string PersistentID { get; set; } = "";
        [XmlIgnore] public bool Touched { get; set; }
        [XmlIgnore] public bool New { get; set; }
        public string MappedName { get; set; } = "";

        public SmartTrack CloneIncoming() => new()
        {
            Index = Index, Year = Year, LastModifiedTime = LastModifiedTime, Loc = Loc,
            FileName = FileName, Name = Name, Genre = Genre,
            ContributingArtist = ContributingArtist, PersistentID = PersistentID,
            Touched = true, New = true,
        };
    }

    public sealed class SmartAlbum
    {
        public string Name { get; set; } = "";
        [XmlArray("Tracks"), XmlArrayItem("Track")]
        public List<SmartTrack> Tracks { get; set; } = [];
        [XmlIgnore] public bool Touched { get; set; }
        [XmlIgnore] public bool New { get; set; }
        public string MappedName { get; set; } = "";

        public SmartTrack FindTrack(SmartTrack incoming)
        {
            SmartTrack? track = Tracks.SingleOrDefault(candidate => candidate.FileName.Equals(
                incoming.FileName, StringComparison.CurrentCultureIgnoreCase));
            if (track is null || incoming.LastModifiedTime > track.LastModifiedTime)
            {
                if (track is not null) Tracks.Remove(track);
                track = incoming.CloneIncoming();
                Tracks.Add(track);
            }
            track.Touched = true;
            track.PersistentID = incoming.PersistentID;
            track.Loc = incoming.Loc;
            return track;
        }
    }

    public sealed class SmartArtist
    {
        public string Name { get; set; } = "";
        [XmlArray("Albums"), XmlArrayItem("Album")]
        public List<SmartAlbum> Albums { get; set; } = [];
        [XmlIgnore] public bool Touched { get; set; }
        [XmlIgnore] public bool New { get; set; }
        public int Bucket { get; set; }
        public string MappedName { get; set; } = "";

        public SmartAlbum FindAlbum(string name)
        {
            SmartAlbum? album = Albums.SingleOrDefault(candidate =>
                candidate.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            if (album is null)
            {
                album = new() { Name = name, New = true };
                Albums.Add(album);
            }
            album.Touched = true;
            return album;
        }
    }

    public sealed class SmartPlaylist
    {
        public string Name { get; set; } = "";
        [XmlArray("Items"), XmlArrayItem("Item")]
        public List<SmartPlaylistItem> Items { get; set; } = [];
    }

    public sealed class SmartPlaylistItem
    {
        public int Artist;
        public int Album;
        public int Track;
    }
}
