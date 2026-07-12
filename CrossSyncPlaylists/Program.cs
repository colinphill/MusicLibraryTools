/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/CrossSyncPlaylists/Program.cs $
 * $Date: 2014-09-26 05:47:24 -0600 (Fri, 26 Sep 2014) $
 * $Revision: 18 $
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

using MusicFileUtilities;
using iTunes;
using ConsoleTools;
using MusicLibraryTools;
using MetadataCaching;

namespace CrossSyncPlaylists
{
    
    class Program
    {
        static int MAX_PLAYLIST_COUNT = 500;
        const int SONOS_NAME_PAD = 100;
               
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

        static void WriteAtomically(string path, byte[] data)
        {
            string temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.WriteThrough))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("CrossSyncPlaylists.log");

            if (args.Length == 0)
            {
                LogConsole.WriteLine("Usage: CrossSyncPlaylists <libraryconfiguration.xml> [clean|check] [--apply]");
                LogConsole.Close();
                return;
            }

            LibraryConfiguration config = new LibraryConfiguration(args[0]);

            /*if (!LibraryConfiguration.Valid)
            {
                LogConsole.WriteLine("Invalid Library Configuration File");
                return;
            }*/

            bool apply = args.Skip(1).Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
            if (apply)
                Directory.CreateDirectory(config.PlaylistTargetFolder);
            else
                LogConsole.WriteLine("Dry run: pass --apply to write playlist files.");

            //string destplaylistfolder = @"z:/iTunes/Lossless/WPL/";
            //string rootlibpath = @"z:/iTunes/AAC/";
            //string newrootpath = @"z:/iTunes/Lossless/";
            //string m3uoffset = @"../";
            //string syncdir = "Purchased Sync";
            //string indexdir = @"z:/iTunes/Lossless/Purchased Sync";

            // TODO: Cull Cached Metadata Without Matching Files

            LogConsole.WriteLine("Indexing Files...");

            using MetadataDatabase db = MetadataDatabase.OpenDatabase(config.DatabaseFile); // TBD Dispose
            db.IndexFiles(config.IndexLocations.Select(l => l.Target));
            var cache = db.BuildCache(config.IndexLocations.Select(l => l.Target));

            LogConsole.WriteLine("Total Parsed Files: " + cache.FileCache.Count);

            LogConsole.WriteLine("Loading iTunes Library XML...");

            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);

            LogConsole.WriteLine("iTunes Library Size: " + lib.Tracks.Count.ToString() + "  Playlist Count: " + lib.Playlists.Count);

            bool clean = args.Skip(1).Any(a => a.Equals("clean", StringComparison.OrdinalIgnoreCase));
            var desiredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var claimedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            LogConsole.WriteLine("Mapping Library...");

            iTunesMapper mapper = new iTunesMapper(lib, cache);

            int missing = 0;

            if (args.Skip(1).Any(a => a.Equals("check", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var track in mapper.MissingTracks)
                {
                    Console.WriteLine("FNF: " + lib.Tracks[track].LocalLocation);
                    missing++;
                }
                LogConsole.WriteLine("Total FNF: " + missing.ToString());
                LogConsole.Close();
                return;
            }

            foreach (iTunesPlaylist pl in lib.Playlists.Values)
            {
                int count = 0;
                if (pl.Items.Count > MAX_PLAYLIST_COUNT)
                    continue;
                 
                LogConsole.WriteLine("Converting Playlist: " + pl.Title);

                string plfilename = (config.PlaylistType.ToLower() == "wpl") ? Path.Combine(config.PlaylistTargetFolder, FixPath(pl.Title).PadRight(SONOS_NAME_PAD) + ".wpl") :
                    Path.Combine(config.PlaylistTargetFolder, FixPath(pl.Title) + ".m3u");
                if (!claimedOutputs.Add(Path.GetFullPath(plfilename)))
                {
                    LogConsole.WriteLine("Skipping playlist with colliding sanitized name: " + pl.Title);
                    continue;
                }

                MemoryStream ms = new MemoryStream();
                //StreamWriter m3uw = new StreamWriter(ms, Encoding.GetEncoding(28591));
                StreamWriter m3uw = new StreamWriter(ms, Encoding.UTF8);
                //m3uw.NewLine = "\n";

                m3uw.WriteLine("#EXTM3U");

                XElement seqel;
                XAttribute countat;
                XDocument pd = new XDocument();
                pd.Add(
                    new XProcessingInstruction("wpl", "version=\"1.0\""),
                    new XElement("smil",
                        new XElement("head",
                            new XElement("meta",
                                new XAttribute("name", "Generator"),
                                new XAttribute("content", "CrossSyncPlaylists")),
                            new XElement("meta",
                                new XAttribute("name", "ItemCount"),
                                countat = new XAttribute("content", "")),
                            new XElement("title", pl.Title)),
                            new XElement("body",
                                seqel = new XElement("seq"))));
              
                foreach (int item in pl.Items)
                {
                    string filepath = null;

                    iTunesTrack track = lib.Tracks[item];
                    if (track.Kind.ToLower().Contains("video") || (track.Type.ToLower() != "file") || (track.Kind.ToLower().Contains("protected")) || (track.Kind.ToLower().Contains("book")) || (track.Kind.ToLower().Contains("audible") ||
                        track.Kind.ToLower().Contains("document") || track.Kind.ToLower().Contains("app") || track.Kind.ToLower().Contains("tone")))
                        continue;

                    try
                    {
                        filepath = mapper[item];
                    }
                    catch 
                    {
                        LogConsole.WriteLine("FNF: " + track.LocalLocation);
                        missing++;
                    }
                 
                    if (!string.IsNullOrWhiteSpace(filepath))
                    {
                        int duration = cache[filepath].DurationInSeconds;
                        if (duration == 0)
                            duration = -1;
                        foreach (var iloc in config.IndexLocations)
                        {
                            if (filepath.Replace('\\', '/').StartsWith(iloc.Target.Replace('\\', '/') + "/", StringComparison.InvariantCultureIgnoreCase))
                                filepath = iloc.Offset + filepath.Remove(0, iloc.Target.Length).Replace('\\','/');
                        }
                        seqel.Add(new XElement("media", new XAttribute("src", filepath)));
                        m3uw.WriteLine("#EXTINF:" + duration + "," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                        m3uw.WriteLine(filepath);
                        count++;
                    }

                }

                if (count != 0)
                {
                    countat.Value = count.ToString();
                    XmlWriterSettings settings = new XmlWriterSettings();
                    settings.OmitXmlDeclaration = true;
                    settings.Indent = true;
                    settings.CloseOutput = false;
                    byte[] output;
                    if (config.PlaylistType.Equals("wpl", StringComparison.InvariantCultureIgnoreCase))
                    {
                        using var wpl = new MemoryStream();
                        using (XmlWriter xw = XmlWriter.Create(wpl, settings))
                            pd.Save(xw);
                        output = wpl.ToArray();
                    }
                    else
                    {
                        m3uw.Flush();
                        output = ms.ToArray();
                    }
                    if (apply)
                        WriteAtomically(plfilename, output);
                    else
                        LogConsole.WriteLine("Would update: " + plfilename);
                    desiredFiles.Add(Path.GetFullPath(plfilename));
                }
                m3uw.Dispose();
                ms.Dispose();
            }

            if (clean && apply)
            {
                LogConsole.WriteLine("Removing obsolete playlists...");
                foreach (string file in Directory.GetFiles(config.PlaylistTargetFolder, "*.*"))
                    if ((Path.GetExtension(file).Equals(".m3u", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetExtension(file).Equals(".wpl", StringComparison.OrdinalIgnoreCase)) &&
                        !desiredFiles.Contains(Path.GetFullPath(file)))
                        File.Delete(file);
            }
            else if (clean)
                LogConsole.WriteLine("Would remove obsolete managed .m3u/.wpl files when run with --apply.");

            LogConsole.WriteLine("Total FNF: " + missing.ToString());

            LogConsole.Close();


        }
    }
}

