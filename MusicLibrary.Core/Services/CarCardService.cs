using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record CarCardRequest(string? ConfigurationPath = null, bool Rebalance = false,
    bool FixErrors = false, bool Initialize = false, int MaxRemovals = 0,
    string? ItunesLibraryPath = null);

public sealed record CarCardPlan(CarCardRequest Request, string DestinationRoot,
    int LibraryTrackCount, int InstalledTrackCount, int UnchangedTrackCount,
    int RemovedTrackCount, int PlaylistCount, FileInventory DestinationInventory,
    IReadOnlyList<OperationPathSnapshot> ReviewedSourceSnapshots,
    FileMutationPlan MutationPlan, IReadOnlyList<OperationIssue> Issues)
{
    public bool CanApply => MutationPlan.CanApply;
}

public sealed record CarCardResult(int LibraryTrackCount, int PlaylistCount,
    FileMutationSummary Mutations, IReadOnlyList<OperationIssue> Issues);

public interface ICarCardService
{
    Task<CarCardPlan> PreviewAsync(CarCardRequest request,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<CarCardResult> ApplyAsync(CarCardPlan plan,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Projects the indexed iTunes library into the balanced car-card layout. Planning constructs the
/// complete target catalog and playlists without modifying the target; apply executes only the
/// reviewed, journaled file plan.
/// </summary>
public sealed class CarCardService : ICarCardService
{
    private const string Marker = ".update-car-card-root";
    private const int MaxPlaylistTracks = 500;
    private readonly ILibraryOperationContextFactory _contexts;
    private readonly IFileInventoryService _inventories;
    private readonly IFileMutationPlanExecutor _executor;

    public CarCardService(ILibraryOperationContextFactory contexts,
        IFileInventoryService inventories, IFileMutationPlanExecutor executor) =>
        (_contexts, _inventories, _executor) = (contexts, inventories, executor);

    public async Task<CarCardPlan> PreviewAsync(CarCardRequest request,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxRemovals < 0) throw new ArgumentOutOfRangeException(nameof(request));
        LibraryOperationContext context = await _contexts.CreateAsync(request.ConfigurationPath,
            request.ItunesLibraryPath, progress, ct).ConfigureAwait(false);
        string[] roots = context.Configuration["BaseDir"];
        if (roots.Length != 1 || string.IsNullOrWhiteSpace(roots[0]))
            return Empty(request, "", [new("base-dir", OperationIssueSeverity.Blocker,
                "The configuration must contain exactly one non-empty BaseDir.")]);
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(roots[0]));
        var issues = ValidateRoots(destination, context);
        progress?.Report(new(OperationPhase.InventoryingDestination,
            Message: "Inventorying car-card destination"));
        FileInventory inventory = await _inventories.CaptureAsync(destination, null, progress, ct)
            .ConfigureAwait(false);
        bool marker = inventory.Files.ContainsKey(Path.Combine(destination, Marker));
        bool database = inventory.Files.ContainsKey(Path.Combine(destination, "syncdb.xml"));
        if (!Directory.Exists(destination) && !request.Initialize)
            issues.Add(new("destination-missing", OperationIssueSeverity.Blocker,
                "The destination does not exist; enable Initialize after verifying it.", destination));
        if (!marker && !database && !request.Initialize)
            issues.Add(new("destination-unmanaged", OperationIssueSeverity.Blocker,
                "The target has no car-card marker or sync database; enable Initialize after verifying it.", destination));
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return Empty(request, destination, issues, inventory);
        return await Task.Run(() => BuildPlan(request, destination, context, inventory,
            marker, issues, progress, ct), ct).ConfigureAwait(false);
    }

    public async Task<CarCardResult> ApplyAsync(CarCardPlan plan,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply) throw new InvalidOperationException("The car-card plan contains blocking issues.");
        progress?.Report(new(OperationPhase.Validating,
            Message: "Re-inventorying car-card destination before the first write"));
        EnsureInventoryUnchanged(plan.DestinationInventory,
            await _inventories.CaptureAsync(plan.DestinationRoot, null, progress, ct).ConfigureAwait(false));
        FileMutationSummary mutations = await _executor.ApplyAsync(plan.MutationPlan, progress, ct)
            .ConfigureAwait(false);
        return new(plan.LibraryTrackCount, plan.PlaylistCount, mutations, plan.Issues);
    }

    private static CarCardPlan BuildPlan(CarCardRequest request, string root,
        LibraryOperationContext context, FileInventory inventory, bool markerExists,
        List<OperationIssue> issues, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        bool walkman = context.Configuration["WalkmanMode"].Length != 0;
        CarCardBalanceSettings settings;
        try
        {
            settings = new(ReadInt(context, "BalanceSize", 15, minimum: 2),
                ReadInt(context, "RebalanceSize", 25, minimum: 2),
                ReadInt(context, "MaxDepthDisparity", 0, minimum: 0),
                ReadInt(context, "BalanceBreak", 20, minimum: 0));
        }
        catch (Exception exception)
        {
            issues.Add(new("balance-settings", OperationIssueSeverity.Blocker, exception.Message));
            return Empty(request, root, issues, inventory);
        }

        CarSyncDatabase oldDatabase;
        try { oldDatabase = LoadDatabase(root, inventory); }
        catch (Exception exception)
        {
            issues.Add(new("sync-database", OperationIssueSeverity.Blocker,
                "Could not read syncdb.xml: " + exception.Message, Path.Combine(root, "syncdb.xml")));
            return Empty(request, root, issues, inventory);
        }

        var database = new CarSyncDatabase
        {
            ArtistStructure = oldDatabase.ArtistStructure.DeepClone(),
            ContributingArtistStructure = oldDatabase.ContributingArtistStructure.DeepClone(),
            AlbumsStructure = oldDatabase.AlbumsStructure.DeepClone(),
            BalanceSize = settings.BalanceSize, RebalanceSize = settings.RebalanceSize,
            BalanceBreak = settings.BalanceBreak, MaxDepthDisparity = settings.MaxDepthDisparity,
        };
        progress?.Report(new(OperationPhase.Planning, Message: "Mapping iTunes tracks to indexed media"));
        var mapper = new ItlMapper(context.ItunesLibrary, context.Cache);
        var snapshots = new Dictionary<string, OperationPathSnapshot>(PathComparer);
        var sourceById = new Dictionary<int, CarTrack>();
        var eligible = context.ItunesLibrary.Tracks.Where(IsEligible).ToArray();
        foreach (var source in eligible)
        {
            ct.ThrowIfCancellationRequested();
            if (mapper.MissingTracks.Contains(source.Id))
            {
                issues.Add(new("unmapped-track", OperationIssueSeverity.Blocker,
                    $"Could not map iTunes track {source.Id}: {source.Artist} - {source.Title}"));
                continue;
            }
            string location = Path.GetFullPath(mapper[source.Id]);
            MetadataCacheEntry entry = context.Cache[location];
            var snapshot = new OperationPathSnapshot(true, false, entry.Length,
                AsUtc(entry.LastWriteTime)) { Path = location };
            snapshots[location] = snapshot;
            string artist = Limit(string.IsNullOrEmpty(source.AlbumArtist) ? source.Artist : source.AlbumArtist, 32);
            string album = (source.Album ?? "").FormatDisc(32, 28);
            var track = new CarTrack
            {
                Index = source.TrackNumber, DiscNumber = entry.DiscNumber ?? 0,
                LastModifiedTime = entry.LastWriteTime, Loc = location,
                FileName = Path.GetFileName(location), Name = Limit(source.Title, 32),
                Year = source.Year, ContributingArtist = Limit(source.Artist, 32),
                PersistentID = source.PersistentIdString,
            };
            database.FileDatabase.FindArtist(artist).FindAlbum(album).Tracks.Add(track);
            sourceById[source.Id] = track;
        }
        foreach (CarAlbum album in database.FileDatabase.Artists.SelectMany(artist => artist.Albums))
            album.Tracks = album.Tracks.Distinct(new CarTrackComparer()).ToList();
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return Empty(request, root, issues, inventory);

        Dictionary<string, string> artistMap = BuildArtistMap(database, contributing: false);
        Dictionary<string, string> contributingMap = walkman
            ? new(PathComparer) : BuildArtistMap(database, contributing: true);
        string[] albumPlaylistNames = BuildAlbumNames(database, artistMap).Values.ToArray();
        SynchronizeTree(database.ArtistStructure, database.ArtistMap.Keys, settings);
        if (!walkman)
        {
            SynchronizeTree(database.ContributingArtistStructure,
                database.ContributingArtistMap.Keys, settings);
            SynchronizeTree(database.AlbumsStructure, albumPlaylistNames, settings);
        }
        bool force = request.Rebalance || oldDatabase.BalanceSize != settings.BalanceSize ||
            oldDatabase.RebalanceSize != settings.RebalanceSize ||
            oldDatabase.BalanceBreak != settings.BalanceBreak ||
            oldDatabase.MaxDepthDisparity != settings.MaxDepthDisparity;
        database.ArtistStructure.Rebalance(settings, force);
        if (!walkman)
        {
            database.ContributingArtistStructure.Rebalance(settings, force);
            database.AlbumsStructure.Rebalance(settings, force);
        }

        string artistsRoot = Path.Combine(root, "Artists");
        string albumsRoot = Path.Combine(root, "Albums");
        string contributingRoot = Path.Combine(root, "Contributing Artists");
        string playlistsRoot = walkman ? root : Path.Combine(root, "Playlists");
        var desiredTracks = new Dictionary<string, CarTrack>(PathComparer);
        foreach (CarArtist artist in database.FileDatabase.Artists)
        foreach (CarAlbum album in artist.Albums)
        foreach (CarTrack track in album.Tracks)
        {
            string mappedArtist = artistMap[artist.Name];
            string target = Path.Combine(artistsRoot,
                database.ArtistStructure.FindNode(mappedArtist, settings).Path, mappedArtist,
                album.Name.FixPath(), track.FileName);
            if (!desiredTracks.TryAdd(target, track) &&
                !PathComparer.Equals(desiredTracks[target].Loc, track.Loc))
                issues.Add(new("track-collision", OperationIssueSeverity.Blocker,
                    "Multiple source tracks map to the same destination.", target));
        }
        var oldTracks = EnumerateTrackPaths(oldDatabase, artistsRoot, settings).ToDictionary(
            pair => pair.Path, pair => pair.Track, PathComparer);
        // Rebalancing changes physical paths but does not remove library tracks. Gate only tracks
        // that disappeared from the desired catalog, not safe copy-plus-quarantine path moves.
        int removed = CountRemovedTracks(oldDatabase.FileDatabase, database.FileDatabase);
        if (removed > 0 && request.MaxRemovals == 0)
            issues.Add(new("removal-approval", OperationIssueSeverity.Blocker,
                $"The plan removes {removed:N0} track(s); specify MaxRemovals to approve removals."));
        else if (removed > request.MaxRemovals)
            issues.Add(new("removal-limit", OperationIssueSeverity.Blocker,
                $"The plan removes {removed:N0} track(s), exceeding MaxRemovals={request.MaxRemovals:N0}."));

        var playlistBytes = BuildPlaylists(context, database, artistMap, contributingMap,
            sourceById, root, artistsRoot, albumsRoot, contributingRoot, playlistsRoot, settings, walkman, issues);

        string recovery = root + ".UpdateCarCard-recovery" + Path.DirectorySeparatorChar +
                          DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var actions = new List<FileMutationAction>();
        foreach ((string path, CarTrack _) in oldTracks)
            if (!desiredTracks.ContainsKey(path) && inventory.Files.TryGetValue(path, out var stale))
                actions.Add(Quarantine(path, root, recovery, stale));
        foreach ((string path, CarTrack track) in desiredTracks)
        {
            OperationPathSnapshot source = snapshots[track.Loc];
            OperationPathSnapshot target = inventory.Files.TryGetValue(path, out var existing)
                ? existing : OperationPathSnapshot.Missing(path);
            if (target.Exists && target.Length == source.Length &&
                Math.Abs((target.LastWriteTimeUtc - source.LastWriteTimeUtc).TotalSeconds) < 1) continue;
            actions.Add(new(target.Exists ? FileMutationKind.Replace : FileMutationKind.Copy,
                track.Loc, path, source, target));
        }
        var managedPlaylists = EnumerateManagedPlaylistPaths(oldDatabase, root, artistsRoot,
            albumsRoot, contributingRoot, playlistsRoot, settings, walkman);
        foreach (string path in managedPlaylists.Except(playlistBytes.Keys, PathComparer))
            if (inventory.Files.TryGetValue(path, out var stale))
                actions.Add(Quarantine(path, root, recovery, stale));
        foreach ((string path, byte[] bytes) in playlistBytes)
            AddGenerated(actions, path, bytes, inventory);
        database.HashSet = CreateHashes(playlistBytes, root, artistsRoot, albumsRoot,
            contributingRoot, playlistsRoot);
        AddGenerated(actions, Path.Combine(root, "syncdb.xml"), Serialize(database), inventory);
        if (!markerExists) AddGenerated(actions, Path.Combine(root, Marker),
            Encoding.UTF8.GetBytes("UpdateCarCard managed target" + Environment.NewLine), inventory);
        int installed = actions.Count(action => action.Kind is FileMutationKind.Copy or FileMutationKind.Replace);
        int unchanged = desiredTracks.Count - installed;
        var plan = new FileMutationPlan("UpdateCarCard", root, recovery, actions, issues, DateTimeOffset.UtcNow);
        progress?.Report(new(OperationPhase.Completed, actions.Count, actions.Count,
            Message: $"Planned {actions.Count:N0} car-card mutation(s)"));
        return new(request, root, eligible.Length, installed, unchanged, removed,
            playlistBytes.Count, inventory, snapshots.Values.ToArray(), plan, issues);
    }

    private static Dictionary<string, byte[]> BuildPlaylists(LibraryOperationContext context,
        CarSyncDatabase database, Dictionary<string, string> artistMap,
        Dictionary<string, string> contributingMap, Dictionary<int, CarTrack> sourceById,
        string root, string artistsRoot, string albumsRoot, string contributingRoot,
        string playlistsRoot, CarCardBalanceSettings settings, bool walkman,
        List<OperationIssue> issues)
    {
        var result = new Dictionary<string, byte[]>(PathComparer);
        var albumNames = BuildAlbumNames(database, artistMap);
        foreach (var artistGroup in database.FileDatabase.Artists.GroupBy(
                     artist => artistMap[artist.Name], PathComparer))
        {
            string mapped = artistGroup.Key;
            string artistRoot = Path.Combine(artistsRoot,
                database.ArtistStructure.FindNode(mapped, settings).Path, mapped);
            var albums = artistGroup.SelectMany(artist => artist.Albums.Select(album => (artist, album))).ToArray();
            var all = albums.SelectMany(item => item.album.Tracks.Select(track => (item.artist, item.album, track)))
                .OrderBy(item => item.track.Name).ToArray();
            if (!walkman)
            {
                AddPlaylist(result, Path.Combine(artistRoot, "All Tracks.m3u"), all.Select(item =>
                    (mapped, item.track.Name, Path.Combine(item.album.Name.FixPath(), item.track.FileName))), issues);
                AddPlaylist(result, Path.Combine(artistRoot, "All Albums.m3u"), albums
                    .OrderBy(item => item.album.Name, PathComparer)
                    .SelectMany(item => item.album.Tracks.OrderBy(TrackOrder).ThenBy(track => track.FileName)
                        .Select(track => (mapped, track.Name,
                            Path.Combine(item.album.Name.FixPath(), track.FileName)))), issues);
                AddPlaylist(result, Path.Combine(artistRoot, "All Albums By Year.m3u"), albums
                    .OrderBy(item => item.album.Tracks.Select(track => track.Year).DefaultIfEmpty().Max())
                    .ThenBy(item => item.album.Name, PathComparer)
                    .SelectMany(item => item.album.Tracks.OrderBy(TrackOrder).ThenBy(track => track.FileName)
                        .Select(track => (mapped, track.Name,
                            Path.Combine(item.album.Name.FixPath(), track.FileName)))), issues);
                foreach (var item in albums)
                {
                    string file = albumNames[(item.artist.Name, item.album.Name)];
                    string path = Path.Combine(albumsRoot,
                        database.AlbumsStructure.FindNode(file, settings).Path, file);
                    AddPlaylist(result, path, item.album.Tracks.OrderBy(TrackOrder).ThenBy(track => track.FileName)
                        .Select(track => (mapped, track.Name, Path.GetRelativePath(Path.GetDirectoryName(path)!,
                            Path.Combine(artistRoot, item.album.Name.FixPath(), track.FileName)))), issues);
                }
            }
        }
        if (!walkman)
        foreach (var contributorGroup in database.FileDatabase.Artists.SelectMany(artist =>
                     artist.Albums.SelectMany(album => album.Tracks.Select(track => (artist, album, track))))
                     .GroupBy(item => contributingMap[item.track.ContributingArtist], PathComparer))
        {
            string mappedContributor = contributorGroup.Key;
            string targetRoot = Path.Combine(contributingRoot,
                database.ContributingArtistStructure.FindNode(mappedContributor, settings).Path,
                mappedContributor);
            var tracks = contributorGroup.ToArray();
            AddPlaylist(result, Path.Combine(targetRoot, "All Tracks.m3u"), tracks.OrderBy(x => x.track.Name)
                .Select(x => (mappedContributor, x.track.Name, Path.GetRelativePath(targetRoot,
                    TrackTarget(x.artist, x.album, x.track, database, artistMap, artistsRoot, settings)))), issues);
            AddPlaylist(result, Path.Combine(targetRoot, "All Albums.m3u"), tracks
                .OrderBy(x => x.album.Name).ThenBy(x => x.track, Comparer<CarTrack>.Create((a,b) => TrackOrder(a).CompareTo(TrackOrder(b))))
                .Select(x => (mappedContributor, x.track.Name, Path.GetRelativePath(targetRoot,
                    TrackTarget(x.artist, x.album, x.track, database, artistMap, artistsRoot, settings)))), issues);
            AddPlaylist(result, Path.Combine(targetRoot, "All Albums By Year.m3u"), tracks
                .OrderBy(x => x.track.Year).ThenBy(x => x.album.Name).ThenBy(x => TrackOrder(x.track))
                .Select(x => (mappedContributor, x.track.Name, Path.GetRelativePath(targetRoot,
                    TrackTarget(x.artist, x.album, x.track, database, artistMap, artistsRoot, settings)))), issues);
            foreach (var albumGroup in tracks.GroupBy(x =>
                         (Artist: x.artist.Name, Album: x.album.Name)))
            {
                string file = albumGroup.Key.Album;
                if (tracks.Select(x => x.album.Name).Count(name => PathComparer.Equals(name, file)) > 1)
                    file += " (" + albumGroup.Key.Artist + ")";
                file = file.FixPath() + ".m3u";
                AddPlaylist(result, Path.Combine(targetRoot, file), albumGroup.OrderBy(x => TrackOrder(x.track))
                    .ThenBy(x => x.track.FileName).Select(x => (mappedContributor, x.track.Name,
                        Path.GetRelativePath(targetRoot, TrackTarget(x.artist, x.album, x.track,
                            database, artistMap, artistsRoot, settings)))), issues);
            }
        }
        foreach (var playlist in context.ItunesLibrary.Playlists.Where(p => !p.IsMaster && p.TrackIds.Count <= MaxPlaylistTracks))
        {
            string path = Path.Combine(playlistsRoot, playlist.DisplayName.FixPath() + ".m3u");
            var playlistTracks = playlist.TrackIds.Where(sourceById.ContainsKey).Select(id =>
            {
                CarTrack track = sourceById[id];
                CarArtist artist = database.FileDatabase.Artists.Single(a => a.Albums.Any(al => al.Tracks.Contains(track)));
                CarAlbum album = artist.Albums.Single(al => al.Tracks.Contains(track));
                return (artist.Name, track.Name, Path.GetRelativePath(playlistsRoot,
                    TrackTarget(artist, album, track, database, artistMap, artistsRoot, settings)));
            }).ToArray();
            if (playlistTracks.Length > 0) AddPlaylist(result, path, playlistTracks, issues);
        }
        return result;
    }

    private static void AddPlaylist(Dictionary<string, byte[]> result, string path,
        IEnumerable<(string Artist, string Title, string Path)> tracks, List<OperationIssue> issues)
    {
        byte[] bytes = M3u(tracks);
        if (!result.TryAdd(path, bytes)) issues.Add(new("playlist-collision",
            OperationIssueSeverity.Blocker, "Multiple playlists map to the same destination.", path));
    }

    private static byte[] M3u(IEnumerable<(string Artist, string Title, string Path)> tracks)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.WriteLine("#EXTM3U");
            foreach (var track in tracks)
            {
                writer.WriteLine($"#EXTINF:-1,{track.Artist.Replace("-", "")} - {track.Title.Replace("-", "")}");
                writer.WriteLine(track.Path);
            }
        }
        return stream.ToArray();
    }

    private static string TrackTarget(CarArtist artist, CarAlbum album, CarTrack track,
        CarSyncDatabase database, Dictionary<string, string> artistMap, string artistsRoot,
        CarCardBalanceSettings settings)
    {
        string mapped = artistMap[artist.Name];
        return Path.Combine(artistsRoot, database.ArtistStructure.FindNode(mapped, settings).Path,
            mapped, album.Name.FixPath(), track.FileName);
    }

    private static Dictionary<string, string> BuildArtistMap(CarSyncDatabase database, bool contributing)
    {
        IEnumerable<string> names = contributing
            ? database.FileDatabase.Artists.SelectMany(a => a.Albums).SelectMany(a => a.Tracks)
                .Select(t => t.ContributingArtist)
            : database.FileDatabase.Artists.Select(a => a.Name);
        var forward = new Dictionary<string, string>(PathComparer);
        Dictionary<string, List<string>> grouped = names.Distinct(PathComparer)
            .GroupBy(FixArtistPath, PathComparer).ToDictionary(g => g.Key,
                g => g.OrderBy(x => x, PathComparer).ToList(), PathComparer);
        foreach ((string mapped, List<string> originals) in grouped)
            foreach (string original in originals) forward[original] = mapped;
        if (contributing) database.ContributingArtistMap = grouped;
        else database.ArtistMap = grouped;
        return forward;
    }

    private static Dictionary<(string Artist, string Album), string> BuildAlbumNames(
        CarSyncDatabase database, Dictionary<string, string> artistMap)
    {
        var albums = database.FileDatabase.Artists.SelectMany(artist => artist.Albums.Select(album =>
            (Artist: artist.Name, Album: album.Name))).ToArray();
        var result = new Dictionary<(string, string), string>();
        foreach (var item in albums)
        {
            string name = item.Album;
            if (albums.Count(other => PathComparer.Equals(other.Album, name)) > 1)
                name += " (" + artistMap[item.Artist] + ")";
            result[(item.Artist, item.Album)] = name.FixPath() + ".m3u";
        }
        return result;
    }

    private static IEnumerable<(string Path, CarTrack Track)> EnumerateTrackPaths(
        CarSyncDatabase database, string artistsRoot, CarCardBalanceSettings settings)
    {
        Dictionary<string, string> reverse = database.ArtistMap.SelectMany(pair => pair.Value
            .Select(value => (value, pair.Key))).ToDictionary(x => x.value, x => x.Key, PathComparer);
        foreach (CarArtist artist in database.FileDatabase.Artists)
        {
            string mapped = reverse.GetValueOrDefault(artist.Name) ?? FixArtistPath(artist.Name);
            foreach (CarAlbum album in artist.Albums)
            foreach (CarTrack track in album.Tracks)
                yield return (Path.Combine(artistsRoot,
                    database.ArtistStructure.FindNode(mapped, settings).Path, mapped,
                    album.Name.FixPath(), track.FileName), track);
        }
    }

    private static int CountRemovedTracks(CarFileDatabase oldDatabase, CarFileDatabase desired)
    {
        var comparer = new CarTrackComparer();
        int removed = 0;
        foreach (CarArtist oldArtist in oldDatabase.Artists)
        foreach (CarAlbum oldAlbum in oldArtist.Albums)
        {
            CarAlbum? desiredAlbum = desired.Artists.FirstOrDefault(artist =>
                    PathComparer.Equals(artist.Name, oldArtist.Name))?.Albums.FirstOrDefault(album =>
                    PathComparer.Equals(album.Name, oldAlbum.Name));
            removed += desiredAlbum is null ? oldAlbum.Tracks.Count : oldAlbum.Tracks.Count(oldTrack =>
                !desiredAlbum.Tracks.Contains(oldTrack, comparer));
        }
        return removed;
    }

    private static IEnumerable<string> EnumerateManagedPlaylistPaths(CarSyncDatabase database,
        string root, string artistsRoot, string albumsRoot, string contributingRoot,
        string playlistsRoot, CarCardBalanceSettings settings, bool walkman)
    {
        foreach (CarPlaylistHash hash in database.HashSet.Playlists)
            yield return Path.Combine(playlistsRoot, hash.Name);
        foreach (CarPlaylistHash hash in database.HashSet.Albums)
            yield return Path.Combine(albumsRoot, database.AlbumsStructure.FindNode(hash.Name, settings).Path, hash.Name);
        foreach ((string artist, List<CarPlaylistHash> hashes) in database.HashSet.Artists)
        {
            string mapped = database.ArtistMap.FirstOrDefault(p => p.Value.Contains(artist, PathComparer)).Key ?? FixArtistPath(artist);
            string dir = Path.Combine(artistsRoot, database.ArtistStructure.FindNode(mapped, settings).Path, mapped);
            foreach (CarPlaylistHash hash in hashes) yield return Path.Combine(dir, hash.Name);
        }
        if (!walkman)
        foreach ((string artist, List<CarPlaylistHash> hashes) in database.HashSet.ContributingArtists)
        {
            string mapped = database.ContributingArtistMap.FirstOrDefault(p => p.Value.Contains(artist, PathComparer)).Key ?? FixArtistPath(artist);
            string dir = Path.Combine(contributingRoot,
                database.ContributingArtistStructure.FindNode(mapped, settings).Path, mapped);
            foreach (CarPlaylistHash hash in hashes) yield return Path.Combine(dir, hash.Name);
        }
    }

    private static CarSyncHashSet CreateHashes(Dictionary<string, byte[]> files, string root,
        string artistsRoot, string albumsRoot, string contributingRoot, string playlistsRoot)
    {
        var hashes = new CarSyncHashSet();
        foreach ((string path, byte[] bytes) in files)
        {
            var hash = new CarPlaylistHash { Name = Path.GetFileName(path),
                Hash = Convert.ToBase64String(SHA1.HashData(bytes)) };
            if (IsUnder(path, playlistsRoot) && !PathComparer.Equals(playlistsRoot, root)) hashes.Playlists.Add(hash);
            else if (IsUnder(path, albumsRoot)) hashes.Albums.Add(hash);
            else if (IsUnder(path, contributingRoot)) AddHash(hashes.ContributingArtists,
                Path.GetFileName(Path.GetDirectoryName(path)!), hash);
            else if (IsUnder(path, artistsRoot)) AddHash(hashes.Artists,
                Path.GetFileName(Path.GetDirectoryName(path)!), hash);
            else hashes.Playlists.Add(hash);
        }
        return hashes;
    }

    private static void AddHash(Dictionary<string, List<CarPlaylistHash>> dictionary,
        string key, CarPlaylistHash hash)
    {
        if (!dictionary.TryGetValue(key, out var list)) dictionary[key] = list = [];
        list.Add(hash);
    }

    private static void SynchronizeTree(CarBalancedPathNode tree, IEnumerable<string> desired,
        CarCardBalanceSettings settings)
    {
        string[] values = desired.Distinct(PathComparer).ToArray();
        foreach (string old in tree.GetAllItems().Except(values, PathComparer).ToArray()) tree.RemoveItem(old, settings);
        foreach (string value in values) tree.AddItem(value, settings);
    }

    private static CarSyncDatabase LoadDatabase(string root, FileInventory inventory)
    {
        string path = Path.Combine(root, "syncdb.xml");
        if (!inventory.Files.ContainsKey(path)) return new();
        using FileStream stream = File.OpenRead(path);
        return (CarSyncDatabase)(new XmlSerializer(typeof(CarSyncDatabase)).Deserialize(stream)
            ?? throw new InvalidDataException("Empty sync database."));
    }

    private static byte[] Serialize(CarSyncDatabase database)
    {
        using var stream = new MemoryStream();
        new XmlSerializer(typeof(CarSyncDatabase)).Serialize(stream, database);
        return stream.ToArray();
    }

    private static void AddGenerated(List<FileMutationAction> actions, string path, byte[] bytes,
        FileInventory inventory)
    {
        OperationPathSnapshot target = inventory.Files.TryGetValue(path, out var existing)
            ? existing : OperationPathSnapshot.Missing(path);
        if (target.Exists && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        actions.Add(new(target.Exists ? FileMutationKind.ReplaceGenerated : FileMutationKind.Write,
            "", path, null, target, bytes.ToImmutableArray()));
    }

    private static FileMutationAction Quarantine(string path, string root, string recovery,
        OperationPathSnapshot snapshot)
    {
        string destination = Path.Combine(recovery, Path.GetRelativePath(root, path));
        return new(FileMutationKind.Quarantine, path, destination, snapshot,
            OperationPathSnapshot.Missing(destination));
    }

    private static int ReadInt(LibraryOperationContext context, string key, int fallback, int minimum)
    {
        string? value = context.Configuration[key].FirstOrDefault();
        if (value is null) return fallback;
        if (!int.TryParse(value, out int result) || result < minimum)
            throw new InvalidDataException($"{key} must be an integer of at least {minimum}.");
        return result;
    }

    private static List<OperationIssue> ValidateRoots(string root, LibraryOperationContext context)
    {
        var issues = new List<OperationIssue>();
        string? filesystem = Path.GetPathRoot(root);
        if (filesystem is not null && PathComparer.Equals(Path.TrimEndingDirectorySeparator(root),
                Path.TrimEndingDirectorySeparator(filesystem)))
            issues.Add(new("filesystem-root", OperationIssueSeverity.Blocker,
                "A filesystem root cannot be used as the car-card target.", root));
        foreach (var source in context.IndexLocations)
            if (PathsOverlap(root, source.Target)) issues.Add(new("root-overlap",
                OperationIssueSeverity.Blocker, "The target overlaps an indexed source root.", source.Target));
        return issues;
    }

    private static CarCardPlan Empty(CarCardRequest request, string root,
        IReadOnlyList<OperationIssue> issues, FileInventory? inventory = null)
    {
        inventory ??= new(root, new Dictionary<string, OperationPathSnapshot>(), [], DateTimeOffset.UtcNow);
        string recovery = root + ".UpdateCarCard-recovery" + Path.DirectorySeparatorChar +
                          DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        return new(request, root, 0, 0, 0, 0, 0, inventory, [],
            new("UpdateCarCard", root, recovery, [], issues, DateTimeOffset.UtcNow), issues);
    }

    private static void EnsureInventoryUnchanged(FileInventory expected, FileInventory current)
    {
        if (expected.Files.Count != current.Files.Count || expected.Directories.Count != current.Directories.Count)
            throw new InvalidOperationException("The car-card destination changed after preview.");
        foreach ((string path, OperationPathSnapshot snapshot) in expected.Files)
            if (!current.Files.TryGetValue(path, out var candidate) || snapshot.Length != candidate.Length ||
                snapshot.LastWriteTimeUtc != candidate.LastWriteTimeUtc)
                throw new InvalidOperationException($"The car-card destination changed at '{path}'.");
        if (!expected.Directories.SequenceEqual(current.Directories, PathComparer))
            throw new InvalidOperationException("The car-card destination directories changed after preview.");
    }

    private static bool IsEligible(iTunes.Binary.ItlTrack track) =>
        !string.IsNullOrWhiteSpace(track.LocalPath) &&
        (track.Kind ?? "").Contains("audio file", StringComparison.OrdinalIgnoreCase) &&
        !(track.Kind ?? "").Contains("protected", StringComparison.OrdinalIgnoreCase);
    private static string Limit(string? value, int length) =>
        (value ?? "")[..Math.Min((value ?? "").Length, length)].Trim();
    private static int TrackOrder(CarTrack track) => track.DiscNumber * 10000 + track.Index;
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
    private static string FixArtistPath(string value)
    {
        if (value.StartsWith("a ", StringComparison.CurrentCultureIgnoreCase)) value = value[2..];
        if (value.StartsWith("the ", StringComparison.CurrentCultureIgnoreCase)) value = value[4..];
        return value.FixPath();
    }
    private static bool IsUnder(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool PathsOverlap(string first, string second) =>
        IsUnder(first, second) || IsUnder(second, first) || PathComparer.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)));
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
