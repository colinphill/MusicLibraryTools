/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/CheckRedundancies/Program.cs $
 * $Date: 2012-12-21 17:25:46 -0700 (Fri, 21 Dec 2012) $
 * $Revision: 11 $
 * $Author: Colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Text.RegularExpressions;

using iTunes;
using ConsoleTools;

namespace CheckRedundancies
{
    class Program
    {
        static void Main(string[] args)
        {
            LogConsole.SwitchFile("CheckRedundancies.log");
            LogConsole.WriteLine("Loading iTunes Library XML...");

            Regex verre = new Regex(@"^(.*)[ \t]+\(.*\)[ \t]*$", RegexOptions.IgnoreCase);

            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);

            LogConsole.WriteLine("iTunes Library Size: " + lib.Tracks.Count.ToString());

            Dictionary<(string Artist, string Title), List<iTunesTrack>> hash = new();

            LogConsole.WriteLine("Scanning Library");

            foreach (iTunesTrack track in lib.Tracks.Values.Where(t => !string.Equals(t.Type, "remote", StringComparison.OrdinalIgnoreCase)))
            {
                string artist = (track.Artist ?? string.Empty).Trim().ToUpperInvariant();
                string title = track.Title ?? string.Empty;
                Match m = verre.Match(title);
                string basetitle = (m.Success ? m.Groups[1].Value : title).Trim().ToUpperInvariant();
                var key = (artist, basetitle);
                List<iTunesTrack> list;
                if (hash.ContainsKey(key))
                    list = hash[key];
                else
                    hash.Add(key, list = new List<iTunesTrack>());
                list.Add(track);
            }

            LogConsole.WriteLine("Finding Redundancies");
            IEnumerable<List<iTunesTrack>> reds = hash.Values.Where(l => l.Count > 1);

            LogConsole.WriteLine();
            foreach (List<iTunesTrack> tracks in reds)
            {
                foreach (iTunesTrack track in tracks)
                    LogConsole.WriteLine(track.Artist + " - " + track.Title + " (" + track.Album + ")");
                LogConsole.WriteLine();
            }

            LogConsole.Close();
        }
    }
}
