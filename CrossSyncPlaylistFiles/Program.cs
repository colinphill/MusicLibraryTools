/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/CrossSyncPlaylistFiles/Program.cs $
 * $Date: 2014-09-25 06:50:52 -0600 (Thu, 25 Sep 2014) $
 * $Revision: 17 $
 * $Author: colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Xml;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;

using iTunes;
using ConsoleTools;
using MusicLibraryTools;

namespace CrossSyncPlaylistFiles
{

    class Program
    {
        static int MAX_PLAYLIST_COUNT = 500;

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


        static void Main(string[] args)
        {
            LogConsole.SwitchFile("CrossSyncPlaylistFiles.log");

            bool apply = args.Any(arg => arg.Equals("--apply", StringComparison.OrdinalIgnoreCase));
            string[] operands = args.Where(arg => !arg.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (operands.Length != 1 || args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal) &&
                !arg.Equals("--apply", StringComparison.OrdinalIgnoreCase)))
            {
                LogConsole.WriteLine("Usage: CrossSyncPlaylistFiles <destination-folder> [--apply]");
                LogConsole.Close();
                return;
            }

            string destplaylistfolder = Path.GetFullPath(operands[0]);
            if (apply)
                Directory.CreateDirectory(destplaylistfolder);
            else
                LogConsole.WriteLine("Dry run: pass --apply to replace playlist folders.");
               // TODO: Cull Cached Metadata Without Matching Files

            LogConsole.WriteLine("Loading iTunes Library XML...");

            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);

            LogConsole.WriteLine("iTunes Library Size: " + lib.Tracks.Count.ToString() + "  Playlist Count: " + lib.Playlists.Count);

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (iTunesPlaylist pl in lib.Playlists.Values.OrderBy(v => v.Title, StringComparer.OrdinalIgnoreCase))
            {
                if ((pl.Items.Count > MAX_PLAYLIST_COUNT) || (pl.Title.ToLower() == "library"))
                    continue;

                LogConsole.WriteLine("Syncing Playlist: " + pl.Title);

                string pldirname = Path.Combine(destplaylistfolder, FixPath(pl.Title));
                if (!claimed.Add(pldirname))
                {
                    LogConsole.WriteLine("Skipping playlist with colliding sanitized name: " + pl.Title);
                    continue;
                }

                string staged = pldirname + ".tmp-" + Guid.NewGuid().ToString("N");
                string backup = pldirname + ".old-" + Guid.NewGuid().ToString("N");

                int[] items = pl.Items.Where(i =>
                    !(lib.Tracks[i].Kind ?? "").Contains("video", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(lib.Tracks[i].LocalLocation) &&
                    !Path.GetExtension(lib.Tracks[i].LocalLocation).Equals(".m4p", StringComparison.OrdinalIgnoreCase)).ToArray();

                LogConsole.WriteLine($"Would replace {pldirname} with {items.Length} file(s).");
                if (!apply)
                    continue;

                Directory.CreateDirectory(staged);

                try
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        iTunesTrack track = lib.Tracks[items[i]];
                        string newpath = Path.Combine(staged, (i + 1).ToString("D3") + " " + FixPath(track.Title + Path.GetExtension(track.LocalLocation)));
                        File.Copy(track.LocalLocation, newpath);
                        LogConsole.WriteLine(newpath);
                    }

                    if (Directory.Exists(pldirname))
                        Directory.Move(pldirname, backup);
                    try
                    {
                        Directory.Move(staged, pldirname);
                        if (Directory.Exists(backup))
                        {
                            try { Directory.Delete(backup, true); }
                            catch (Exception ex) { LogConsole.WriteLine($"Replacement succeeded; unable to remove backup {backup}: {ex.Message}"); }
                        }
                    }
                    catch
                    {
                        if (!Directory.Exists(pldirname) && Directory.Exists(backup))
                            Directory.Move(backup, pldirname);
                        throw;
                    }
                }
                finally
                {
                    if (Directory.Exists(staged))
                        Directory.Delete(staged, true);
                }
            }


            LogConsole.Close();


        }
    }
}

