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

using iTunes.Binary;
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

            LogConsole.WriteLine("Loading iTunes Library...");

            ItlLibrary lib = ItlLibrary.Load(ItlFileEditor.ResolveLibraryPath());
            Dictionary<int, ItlTrack> tracksById = lib.Tracks.ToDictionary(track => track.Id);

            LogConsole.WriteLine("iTunes Library Size: " + lib.Tracks.Count.ToString() + "  Playlist Count: " + lib.Playlists.Count);

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ItlPlaylist pl in lib.Playlists.OrderBy(playlist => playlist.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                if (pl.TrackIds.Count > MAX_PLAYLIST_COUNT || pl.IsMaster)
                    continue;

                LogConsole.WriteLine("Syncing Playlist: " + pl.DisplayName);

                string pldirname = Path.Combine(destplaylistfolder, FixPath(pl.DisplayName));
                if (!claimed.Add(pldirname))
                {
                    LogConsole.WriteLine("Skipping playlist with colliding sanitized name: " + pl.DisplayName);
                    continue;
                }

                string staged = pldirname + ".tmp-" + Guid.NewGuid().ToString("N");
                string backup = pldirname + ".old-" + Guid.NewGuid().ToString("N");

                int[] items = pl.TrackIds.Where(id =>
                    !tracksById[id].HasVideo &&
                    !string.IsNullOrWhiteSpace(tracksById[id].LocalPath) &&
                    !Path.GetExtension(tracksById[id].LocalPath).Equals(".m4p", StringComparison.OrdinalIgnoreCase)).ToArray();

                LogConsole.WriteLine($"Would replace {pldirname} with {items.Length} file(s).");
                if (!apply)
                    continue;

                Directory.CreateDirectory(staged);

                try
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        ItlTrack track = tracksById[items[i]];
                        string newpath = Path.Combine(staged, (i + 1).ToString("D3") + " " +
                            FixPath((track.Title ?? string.Empty) + Path.GetExtension(track.LocalPath)));
                        File.Copy(track.LocalPath!, newpath);
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

