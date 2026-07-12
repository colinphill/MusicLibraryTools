using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using iTunesLib;

namespace FixiTunesDupes
{
    class Program
    {
        private readonly record struct PersistentId(int High, int Low);

        private readonly record struct TrackIdentity(
            int Disc,
            int Track,
            string Title,
            string Artist,
            string Album,
            int Duration);

        private sealed record TrackCandidate(PersistentId Id, DateTime DateAdded, string Path);

        private sealed record PlaylistSnapshot(
            IITSource Source,
            IITUserPlaylist Playlist,
            string Name,
            List<PersistentId> Items);

        private sealed record StagedPlaylist(
            PlaylistSnapshot Snapshot,
            IITUserPlaylist Replacement,
            string TemporaryName,
            string BackupName)
        {
            public bool Switched { get; set; }
        }

        private static string Normalize(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();

        private static bool TryParseArguments(string[] args, out bool apply, out int maxRemovals)
        {
            apply = false;
            maxRemovals = 0;
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index].Equals("--apply", StringComparison.OrdinalIgnoreCase))
                    apply = true;
                else if (args[index].Equals("--max-removals", StringComparison.OrdinalIgnoreCase) &&
                    ++index < args.Length && int.TryParse(args[index], out maxRemovals) && maxRemovals >= 0)
                {
                }
                else
                    return false;
            }
            return true;
        }

        private static string? TryHashFile(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skipping '{path}' because its content could not be verified: {ex.Message}");
                return null;
            }
        }

        private static List<PlaylistSnapshot> SnapshotUserPlaylists(iTunesApp app)
        {
            var snapshots = new List<PlaylistSnapshot>();
            foreach (IITSource source in app.Sources)
            {
                foreach (IITPlaylist playlist in source.Playlists)
                {
                    if (playlist.Kind != ITPlaylistKind.ITPlaylistKindUser)
                        continue;

                    IITUserPlaylist? userPlaylist = playlist as IITUserPlaylist;
                    // Smart and special playlists derive membership automatically and do not
                    // accept AddTrack. Ordinary user playlists are snapshotted without a size
                    // cutoff, including every item and repeated occurrence in its original order.
                    if (userPlaylist == null || userPlaylist.Smart ||
                        userPlaylist.SpecialKind != ITUserPlaylistSpecialKind.ITUserPlaylistSpecialKindNone)
                        continue;

                    var items = new List<PersistentId>();
                    foreach (IITTrack track in playlist.Tracks)
                    {
                        app.GetITObjectPersistentIDs(track, out int high, out int low);
                        items.Add(new PersistentId(high, low));
                    }
                    snapshots.Add(new PlaylistSnapshot(source, userPlaylist, playlist.Name, items));
                }
            }
            return snapshots;
        }

        private static bool RewriteAffectedPlaylists(
            iTunesApp app,
            IITPlaylist library,
            IEnumerable<PlaylistSnapshot> snapshots,
            IReadOnlyDictionary<PersistentId, PersistentId> replacements)
        {
            PlaylistSnapshot[] affected = snapshots.Where(snapshot => snapshot.Items.Any(replacements.ContainsKey)).ToArray();
            if (affected.Length == 0)
                return true;

            var staged = new List<StagedPlaylist>();
            try
            {
                // Build and fully populate every replacement before changing an original playlist.
                foreach (PlaylistSnapshot snapshot in affected)
                {
                    string token = Guid.NewGuid().ToString("N");
                    string temporaryName = "FixiTunesDupes staging " + token;
                    string backupName = "FixiTunesDupes backup " + token;
                    object source = snapshot.Source;
                    IITUserPlaylist replacement = (IITUserPlaylist)app.CreatePlaylistInSource(temporaryName, ref source);
                    var stage = new StagedPlaylist(snapshot, replacement, temporaryName, backupName);
                    staged.Add(stage);

                    object parent = snapshot.Playlist.get_Parent();
                    replacement.set_Parent(ref parent);
                    replacement.Shuffle = snapshot.Playlist.Shuffle;
                    replacement.SongRepeat = snapshot.Playlist.SongRepeat;
                    replacement.Shared = snapshot.Playlist.Shared;

                    foreach (PersistentId item in snapshot.Items)
                    {
                        PersistentId desired = replacements.TryGetValue(item, out PersistentId retained) ? retained : item;
                        IITTrack track = library.Tracks.ItemByPersistentID[desired.High, desired.Low];
                        if (track == null)
                            throw new InvalidOperationException($"Playlist '{snapshot.Name}' references a track that is no longer in the library.");
                        replacement.AddTrack(track);
                    }

                    if (replacement.Tracks.Count != snapshot.Items.Count)
                        throw new InvalidOperationException($"Playlist '{snapshot.Name}' staging count did not match its source.");
                }

                // Switch names only after all staged playlists are complete. Keep the originals
                // as backups until every switch succeeds so a rename failure can be rolled back.
                foreach (StagedPlaylist stage in staged)
                {
                    stage.Snapshot.Playlist.Name = stage.BackupName;
                    try
                    {
                        stage.Replacement.Name = stage.Snapshot.Name;
                        stage.Switched = true;
                    }
                    catch
                    {
                        stage.Snapshot.Playlist.Name = stage.Snapshot.Name;
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Playlist staging failed; no duplicate library entries will be removed: " + ex.Message);
                foreach (StagedPlaylist stage in staged.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (stage.Switched)
                            stage.Replacement.Name = stage.TemporaryName;
                        stage.Snapshot.Playlist.Name = stage.Snapshot.Name;
                    }
                    catch { }
                    try { stage.Replacement.Delete(); } catch { }
                }
                return false;
            }

            bool originalsRemoved = true;
            foreach (StagedPlaylist stage in staged)
            {
                try
                {
                    stage.Snapshot.Playlist.Delete();
                    Console.WriteLine("Rewrote playlist: " + stage.Snapshot.Name);
                }
                catch (Exception ex)
                {
                    originalsRemoved = false;
                    Console.WriteLine($"Could not remove playlist backup '{stage.BackupName}': {ex.Message}");
                }
            }
            return originalsRemoved;
        }

        static void Main(string[] args)
        {
            if (!TryParseArguments(args, out bool apply, out int maxRemovals))
            {
                Console.WriteLine("Usage: FixiTunesDupes [--apply] [--max-removals <count>]");
                return;
            }

            if (!apply)
                Console.WriteLine("Dry-run mode; pass --apply to rewrite affected playlists and remove byte-identical duplicate entries.");

            iTunesApp app = new iTunesApp();
            IITPlaylist library = app.LibraryPlaylist;
            List<PlaylistSnapshot> playlists = SnapshotUserPlaylists(app);

            var metadataGroups = new Dictionary<TrackIdentity, List<TrackCandidate>>();
            foreach (IITTrack track in library.Tracks)
            {
                if (track.Kind != ITTrackKind.ITTrackKindFile)
                    continue;

                IITFileOrCDTrack fileTrack = (IITFileOrCDTrack)track;
                if (fileTrack.VideoKind != ITVideoKind.ITVideoKindNone)
                    continue;

                app.GetITObjectPersistentIDs(fileTrack, out int high, out int low);
                var identity = new TrackIdentity(
                    fileTrack.DiscNumber,
                    fileTrack.TrackNumber,
                    Normalize(fileTrack.Name),
                    Normalize(fileTrack.Artist),
                    Normalize(fileTrack.Album),
                    fileTrack.Duration);

                if (!metadataGroups.TryGetValue(identity, out List<TrackCandidate>? candidates))
                    metadataGroups.Add(identity, candidates = new List<TrackCandidate>());
                candidates.Add(new TrackCandidate(new PersistentId(high, low), fileTrack.DateAdded, fileTrack.Location));
            }

            var replacements = new Dictionary<PersistentId, PersistentId>();
            var duplicates = new List<TrackCandidate>();
            foreach (var metadataGroup in metadataGroups.Where(group => group.Value.Count > 1))
            {
                // Metadata only identifies candidates. Require complete byte-for-byte equality
                // before planning removal, so similar tags can never collapse different audio.
                var verifiedGroups = metadataGroup.Value
                    .Select(candidate => (Candidate: candidate, Hash: TryHashFile(candidate.Path)))
                    .Where(item => item.Hash != null)
                    .GroupBy(item => item.Hash!, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1);

                foreach (var verifiedGroup in verifiedGroups)
                {
                    TrackCandidate retained = verifiedGroup.Select(item => item.Candidate)
                        .OrderByDescending(candidate => candidate.DateAdded).First();
                    Console.WriteLine($"Keeping: {retained.Path}");

                    foreach (TrackCandidate duplicate in verifiedGroup.Select(item => item.Candidate).Where(candidate => candidate.Id != retained.Id))
                    {
                        replacements.Add(duplicate.Id, retained.Id);
                        duplicates.Add(duplicate);
                        int playlistOccurrences = playlists.Sum(playlist => playlist.Items.Count(item => item == duplicate.Id));
                        Console.WriteLine($"Duplicate: {duplicate.Path} ({playlistOccurrences} ordinary playlist occurrence(s))");
                    }
                }
            }

            if (!apply)
            {
                Console.WriteLine($"Verified duplicate entries: {duplicates.Count}. No changes made.");
                return;
            }

            if (duplicates.Count > maxRemovals)
            {
                Console.WriteLine($"Safety stop: {duplicates.Count} duplicate removals exceeds --max-removals {maxRemovals}. No playlists or library entries were changed.");
                return;
            }

            if (!RewriteAffectedPlaylists(app, library, playlists, replacements))
            {
                Console.WriteLine("Duplicate removal cancelled because playlist replacement was incomplete.");
                return;
            }

            int removed = 0;
            foreach (TrackCandidate duplicate in duplicates)
            {
                IITTrack track = library.Tracks.ItemByPersistentID[duplicate.Id.High, duplicate.Id.Low];
                if (track != null)
                {
                    track.Delete();
                    removed++;
                }
            }

            Console.WriteLine($"Verified duplicate entries removed: {removed}.");
        }
    }
}
