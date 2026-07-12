/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/CrossSyncMusic/Program.cs $
 * $Date: 2014-09-25 06:50:52 -0600 (Thu, 25 Sep 2014) $
 * $Revision: 17 $
 * $Author: colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

using iTunes;
using ConsoleTools;
using MusicLibraryTools;
using MusicFileUtilities;
using MetadataCaching;

namespace CrossSyncMusic
{

    class HitRecord
    {
        public bool Hit
        {
            get;
            set;
        } = false;
        public DateTime LastModifiedTime
        {
            get;
            set;
        }
    }

    class Program
    {

        static bool PathsOverlap(string first, string second)
        {
            string a = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string b = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }

        static void CopyAtomically(string source, string destination)
        {
            string temporary = destination + ".crosssync-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, false);
                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    stream.Flush(true);
                File.Move(temporary, destination, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        static void PopulateHits(DirectoryInfo di, Dictionary<string, HitRecord> hits, bool includeNonMusic, bool keepFolderImages)
        {
            LogConsole.WriteLine("Scanning: " + di.FullName);

            if (!di.Exists)
                return;

            var files = di.EnumerateFiles("*", SearchOption.AllDirectories).Where(file =>
                MetadataCache.ValidExtensions.Contains(Path.GetExtension(file.Name).ToLowerInvariant()) ||
                (includeNonMusic && !(keepFolderImages && Path.GetFileNameWithoutExtension(file.Name).Equals("folder", StringComparison.OrdinalIgnoreCase))));

            foreach (var file in files)
                hits[file.FullName] = new HitRecord { LastModifiedTime = file.LastWriteTimeUtc };
        }

        static bool TryGetMaxRemovals(string[] args, out int maxRemovals)
        {
            maxRemovals = 0;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("--max-removals=", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(args[i].Substring("--max-removals=".Length), out maxRemovals) || maxRemovals < 0)
                        return false;
                }
                else if (args[i].Equals("--max-removals", StringComparison.OrdinalIgnoreCase))
                {
                    if (++i >= args.Length || !int.TryParse(args[i], out maxRemovals) || maxRemovals < 0)
                        return false;
                }
            }
            return true;
        }

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("CrossSyncMusic.log");

            if (args.Length == 0 || !TryGetMaxRemovals(args, out int maxRemovals))
            {
                LogConsole.WriteLine("Usage: CrossSyncMusic <libraryconfiguration.xml> [--apply] [--max-removals <count>]");
                LogConsole.WriteLine("The default mode only reports changes. Stale files are quarantined, never deleted.");
                LogConsole.Close();
                return;
            }

            bool apply = args.Skip(1).Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));

            LibraryConfiguration config = new LibraryConfiguration(args[0]);

            bool deletenonmusic = config["DeleteNonMusic"].Count() != 0;
            bool keepfolderimages = config["KeepFolderImages"].Count() != 0;
            var indexLocations = config.IndexLocations.ToArray();
 
            string targetPath = Path.GetFullPath(config.CrossSyncTargetLibraryPath);
            string targetRoot = Path.GetPathRoot(targetPath);
            if (string.Equals(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                targetRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                LogConsole.WriteLine("Error: refusing to use a filesystem root as the synchronization target.");
                LogConsole.Close();
                return;
            }
            if (indexLocations.Any(location => PathsOverlap(targetPath, location.Target)))
            {
                LogConsole.WriteLine("Error: refusing to continue because the synchronization target overlaps an indexed source location.");
                LogConsole.Close();
                return;
            }

            LogConsole.WriteLine("Loading iTunes Library XML...");
            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);

            string[] requestedPlaylists = config["SyncPlaylist"];
            if (requestedPlaylists.Length == 0)
            {
                LogConsole.WriteLine("Error: no SyncPlaylist entries are configured; no destination changes were made.");
                LogConsole.Close();
                return;
            }

            var syncPlaylists = requestedPlaylists.Select(name => (Name: name, Playlist: lib.FindPlaylist(name))).ToArray();
            string[] missingPlaylists = syncPlaylists.Where(p => p.Playlist == null).Select(p => p.Name).ToArray();
            if (missingPlaylists.Length != 0)
            {
                foreach (string playlist in missingPlaylists)
                    LogConsole.WriteLine("Error: Can't Find Playlist: " + playlist);
                LogConsole.WriteLine("Aborting before destination changes because the configured playlist set is incomplete.");
                LogConsole.Close();
                return;
            }

            if (!apply)
                LogConsole.WriteLine("Dry-run mode; pass --apply to modify the target.");
            
            var namehits = new Dictionary<string, HitRecord>(StringComparer.OrdinalIgnoreCase);

            var targetdi = new DirectoryInfo(targetPath);
            PopulateHits(targetdi, namehits, deletenonmusic, keepfolderimages);

            using MetadataDatabase db = MetadataDatabase.OpenDatabase(config.DatabaseFile); // TBD Dispose
            db.IndexFiles(indexLocations.Select(l => l.Target));
            var cache = db.BuildCache(indexLocations.Select(l => l.Target));

            var plannedTracks = new List<(iTunesTrack Track, MetadataCacheEntry Entry, string Destination)>();

            foreach (var syncPlaylist in syncPlaylists)
            {
                iTunesPlaylist pl = syncPlaylist.Playlist;

                foreach (int id in pl.Items)
                {
                    iTunesTrack trk = lib.Tracks[id];

                    if (string.Equals(trk.Type, "File", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!(trk.Kind ?? "").Contains("video", StringComparison.OrdinalIgnoreCase) &&
                            !(trk.Kind ?? "").Contains("audible", StringComparison.OrdinalIgnoreCase))
                        {
                            string localloc = trk.LocalLocation;
                            if (localloc.StartsWith(@"\\"))
                                localloc = @"\\" + localloc.Substring(2).Replace(@"\\", @"\");
                            else
                                localloc = localloc.Replace(@"\\", @"\");
                            LogConsole.WriteLine("Checking Track: " + localloc);
                            

                            MetadataCacheEntry entry = null;
                            if (cache.FileCache.ContainsKey(localloc))
                                entry = cache[localloc];
                            else 
                            {
                                try
                                {
                                    entry = cache.FileCache.Single(kv => kv.Key.Equals(localloc, StringComparison.InvariantCultureIgnoreCase)).Value;
                                }
                                catch
                                {
                                    LogConsole.WriteLine("WARNING: Out of tree");
                                    entry = new MetadataCacheEntry(MediaFile.GetFile(trk.LocalLocation), File.GetLastWriteTimeUtc(trk.LocalLocation));
                                    entry.Strip();
                                }
                            }

                            string dest = Path.Combine(targetPath, entry.FormatPath(config.LengthLimit, config.DiscNumLengthLimit) + Path.GetExtension(trk.LocalLocation));
                            plannedTracks.Add((trk, entry, dest));
                        }
                    }
                }
            }

            var destinationCollisions = plannedTracks
                .GroupBy(item => item.Destination, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(item => Path.GetFullPath(item.Track.LocalLocation)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .ToArray();
            if (destinationCollisions.Length != 0)
            {
                foreach (var collision in destinationCollisions)
                {
                    LogConsole.WriteLine("Destination collision: " + collision.Key);
                    foreach (string source in collision.Select(item => item.Track.LocalLocation).Distinct(StringComparer.OrdinalIgnoreCase))
                        LogConsole.WriteLine("  " + source);
                }
                LogConsole.WriteLine("Aborting before target changes because multiple source tracks map to the same destination.");
                LogConsole.Close();
                return;
            }

            var desiredDestinations = new HashSet<string>(plannedTracks.Select(item => item.Destination), StringComparer.OrdinalIgnoreCase);
            string[] misses = namehits.Keys.Where(file => !desiredDestinations.Contains(file)).OrderBy(file => file).ToArray();
            if (apply && misses.Length > maxRemovals)
            {
                LogConsole.WriteLine($"Safety stop: {misses.Length} stale files exceeds --max-removals {maxRemovals}. No target files were changed.");
                LogConsole.Close();
                return;
            }

            if (apply)
                Directory.CreateDirectory(targetPath);

            int converted = 0;
            foreach (var planned in plannedTracks.GroupBy(item => item.Destination, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                iTunesTrack trk = planned.Track;
                MetadataCacheEntry entry = planned.Entry;
                string dest = planned.Destination;

                if (apply)
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));

                if (namehits.ContainsKey(dest))
                {
                    if (entry.LastWriteTime > namehits[dest].LastModifiedTime)
                    {
                        LogConsole.WriteLine("Rewriting: " + dest);
                        if (apply)
                            CopyAtomically(trk.LocalLocation, dest);
                    }
                    else
                        LogConsole.WriteLine("Skipping: " + dest);
                }
                else
                {
                    namehits.Add(dest, new HitRecord { LastModifiedTime = entry.LastWriteTime });
                    converted++;
                    LogConsole.WriteLine("Copying: " + dest);
                    if (apply)
                        CopyAtomically(trk.LocalLocation, dest);
                }
                namehits[dest].Hit = true;
            }

            LogConsole.WriteLine("Copied: " + converted.ToString());

            LogConsole.SwitchFile("CrossSyncMisses.log");

            foreach (string file in misses)
                LogConsole.WriteLine("Miss: " + file);

            if (apply && misses.Length != 0)
            {
                string quarantineRoot = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    ".CrossSyncMusic-quarantine" + Path.DirectorySeparatorChar + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
                foreach (string file in misses)
                {
                    string relative = Path.GetRelativePath(targetPath, file);
                    string quarantineFile = Path.Combine(quarantineRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(quarantineFile));
                    LogConsole.WriteLine("Quarantining: " + file + " -> " + quarantineFile);
                    File.Move(file, quarantineFile);
                }
            }

            LogConsole.WriteLine("Total Misses: " + misses.Length.ToString());

            if (apply)
                MetadataExtensions.CleanEmptyMusicFolders(targetdi, false, keepfolderimages);
    
            LogConsole.Close();

        }
    }
}
