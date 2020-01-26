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

namespace CrossSyncMusic
{
    class Program
    {

        static string FixPath(string item)
        {
            string fix = item;
            foreach (char c in Path.GetInvalidFileNameChars())
                fix = fix.Replace(c, '_');
            foreach (char c in Path.GetInvalidPathChars())
                fix = fix.Replace(c, '_');
            fix = fix.Trim();
            if (fix.EndsWith("."))
                fix = fix.Remove(fix.Length - 1);
            return fix;
        }

        static void PopulateHits(string tgt, Dictionary<string, bool> hits, string[] extensions)
        {
            LogConsole.WriteLine("Scanning: " + tgt);
            string[] dirs = Directory.GetDirectories(tgt);
            foreach (string dir in dirs)
                PopulateHits(dir, hits, extensions);
            dirs = Directory.GetDirectories(tgt);
            string[] files = Directory.GetFiles(tgt);

            if ((files.Length == 1) && (Path.GetFileName(files[0]).ToLower() == "thumbs.db"))
            {
                File.SetAttributes(files[0], FileAttributes.Normal);
                File.Delete(files[0]);
                files = new string[0];
            }

            if ((files.Length == 0)&&(dirs.Length == 0))
            {
                LogConsole.WriteLine("Cleaning Empty Directory: " + tgt);
                Directory.Delete(tgt);
                return;
            }
            foreach (string file in files)
                if (extensions.Contains(Path.GetExtension(file).ToLower()))
                    hits.Add(file.ToLower(), false);
        }

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("CrossSyncMusic.log");

            string[] audioextensions = new string[] { ".m4a", ".mp3" }; //, ".flac", ".ogg" };

            if (args.Length < 2)
            {
                LogConsole.WriteLine("Usage: CrossSyncMusic <libraryconfiguration.xml> <playlistname> [playlistname] [playlistname] ...");
                return;
            }

            LibraryConfiguration config = new LibraryConfiguration(args[0]);

            Directory.CreateDirectory(config.CrossSyncTargetLibraryPath);

            LogConsole.WriteLine("Loading iTunes Library XML...");
            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);
            
            //string tgt = LibraryConfiguration.CrossSyncTargetMusicFolder;
            //string trash = LibraryConfiguration.TrashTargetFolder;

            Dictionary<string, bool> namehits = new Dictionary<string, bool>();
            PopulateHits(config.CrossSyncTargetLibraryPath, namehits, audioextensions);

            int tracks = 0;
            int converted = 0;

            for (int i = 1; i < args.Length; i++)
            {

                iTunesPlaylist pl = lib.FindPlaylist(args[i]);
                if (pl == null)
                {
                    LogConsole.WriteLine("Error: Can't Find Playlist: " + args[i]);
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

                            string artist = trk.Artist;
                            if ((trk.AlbumArtist != null) && (trk.AlbumArtist != ""))
                                artist = trk.AlbumArtist;
                            artist = FixPath(artist);
                            string album = FixPath(trk.Album);
                            string title = FixPath(trk.Title);
                            int track = (trk.TrackNumber == null) ? 0 : (int)trk.TrackNumber;

                            if (title.Length > 50)
                                title = title.Substring(0, 50).Trim();

                            string dest = Path.Combine(config.CrossSyncTargetLibraryPath, artist, album, track.ToString("D2") + " " + title + Path.GetExtension(trk.LocalLocation));

                            Directory.CreateDirectory(Path.GetDirectoryName(dest));

                            namehits[dest.ToLower()] = true;

                            if (File.Exists(dest))
                            {
                                if (File.GetLastWriteTimeUtc(trk.LocalLocation) > File.GetLastWriteTimeUtc(dest))
                                {
                                    LogConsole.WriteLine("Rewriting: " + dest);
                                    File.Delete(dest);
                                }
                            }

                            if (File.Exists(dest))
                            {
                                LogConsole.WriteLine("Skipping: " + dest);
                            }
                            else
                            {
                                converted++;
                                LogConsole.WriteLine("Copying: " + dest);
                                File.Copy(trk.LocalLocation, dest);
                            }
                        }
                    }
                }

            }

            LogConsole.WriteLine("Copied: " + converted.ToString());

            LogConsole.SwitchFile("CrossSyncMisses.log");

            // TODO: Delete Missed Files To Keep Folder Clean

            foreach (string file in namehits.Keys.OrderBy(x => x))
                if (!namehits[file])
                {
                    LogConsole.WriteLine("Miss: " + file);
                    File.Move(file, Path.Combine(config.TrashTargetFolder, Path.GetFileName(file)));
                }

            LogConsole.WriteLine("Total Misses: " + (namehits.Where(kv => kv.Value == false).Count()).ToString());

            LogConsole.Close();

        }
    }
}
