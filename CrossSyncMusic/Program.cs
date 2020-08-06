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

        static void PopulateHits(DirectoryInfo di, Dictionary<string, HitRecord> hits)
        {
            LogConsole.WriteLine("Scanning: " + di.FullName);

            var files = di.GetFileSystemInfos("*", SearchOption.AllDirectories).Where(fi => MetadataCache.ValidExtensions.Contains(Path.GetExtension(fi.Name).ToLower()));

            foreach (var file in files)
                hits.Add(file.FullName.ToLower(), new HitRecord { LastModifiedTime = file.LastWriteTimeUtc });
        }

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("CrossSyncMusic.log");

            string[] audioextensions = new string[] { ".m4a", ".mp3" }; //, ".flac", ".ogg" };

            if (args.Length != 1)
            {
                LogConsole.WriteLine("Usage: CrossSyncMusic <libraryconfiguration.xml>");
                return;
            }

            LibraryConfiguration config = new LibraryConfiguration(args[0]);

            Directory.CreateDirectory(config.CrossSyncTargetLibraryPath);

            LogConsole.WriteLine("Loading iTunes Library XML...");
            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);
            
            var namehits = new Dictionary<string, HitRecord>();

            var targetdi = new DirectoryInfo(config.CrossSyncTargetLibraryPath);
            PopulateHits(targetdi, namehits);

            MetadataCache cache = new MetadataCache();
            if (File.Exists(args[0] + ".cache"))
                cache.Load(args[0] + ".cache");
            else if (File.Exists(config.ReferenceConfig + ".cache"))
                cache.Load(config.ReferenceConfig + ".cache");
#if true
            cache.BeginBuildCache();
            foreach (var iloc in config.IndexLocations)
                cache.BuildCache(iloc.Target, false);
            cache.EndBuildCache();
            cache.Save(args[0] + ".cache");
#endif

            int tracks = 0;
            int converted = 0;

            foreach (string playlist in config["SyncPlaylist"])
            {

                iTunesPlaylist pl = lib.FindPlaylist(playlist);
                if (pl == null)
                {
                    LogConsole.WriteLine("Error: Can't Find Playlist: " + playlist);
                    continue;
                }

                foreach (int id in pl.Items)
                {
                    iTunesTrack trk = lib.Tracks[id];

                    if (trk.Type == "File")
                    {
                        if ((!trk.Kind.ToLower().Contains("video"))&&(!trk.Kind.ToLower().Contains("audible")))
                        {
                            //if (trk.Genre == "Podcast")
                            //    continue;
                            tracks++;

                            LogConsole.WriteLine("Checking Track: " + trk.LocalLocation);

                            MetadataCacheEntry entry = null;
                            if (cache.FileCache.ContainsKey(trk.LocalLocation))
                                entry = cache[trk.LocalLocation];
                            else 
                            {
                                try
                                {
                                    entry = cache.FileCache.Single(kv => kv.Key.Equals(trk.LocalLocation, StringComparison.InvariantCultureIgnoreCase)).Value;
                                }
                                catch
                                {
                                    LogConsole.WriteLine("WARNING: Out of tree");
                                    entry = new MetadataCacheEntry(Metadata.GetProvider(trk.LocalLocation), File.GetLastWriteTimeUtc(trk.LocalLocation));
                                    entry.Strip();
                                }
                            }

                            string dest = Path.Combine(config.CrossSyncTargetLibraryPath, entry.FormatPath(config.LengthLimit, config.DiscNumLengthLimit) + Path.GetExtension(trk.LocalLocation));

                            Directory.CreateDirectory(Path.GetDirectoryName(dest));

                            if (namehits.ContainsKey(dest.ToLower()))
                            {
                                if (entry.LastWriteTime > namehits[dest.ToLower()].LastModifiedTime)
                                {
                                    LogConsole.WriteLine("Rewriting: " + dest);
                                    File.Copy(trk.LocalLocation, dest, true);
                                }
                                else
                                {
                                    LogConsole.WriteLine("Skipping: " + dest);
                                }
                            }
                            else
                            {
                                namehits.Add(dest.ToLower(), new HitRecord { LastModifiedTime = entry.LastWriteTime });
                                converted++;
                                LogConsole.WriteLine("Copying: " + dest);
                                File.Copy(trk.LocalLocation, dest);
                            }
                            namehits[dest.ToLower()].Hit = true;

                        }
                    }
                }

            }

            LogConsole.WriteLine("Copied: " + converted.ToString());

            LogConsole.SwitchFile("CrossSyncMisses.log");

            // TODO: Delete Missed Files To Keep Folder Clean

            foreach (string file in namehits.Keys.OrderBy(x => x))
                if (!namehits[file].Hit)
                {
                    LogConsole.WriteLine("Miss: " + file);
                    File.Delete(file);// (, Path.Combine(config.TrashTargetFolder, Path.GetFileName(file)));/ ;
                }

            LogConsole.WriteLine("Total Misses: " + (namehits.Where(kv => kv.Value.Hit == false).Count()).ToString());

            MetadataExtensions.CleanEmptyMusicFolders(targetdi);
    
            LogConsole.Close();

        }
    }
}
