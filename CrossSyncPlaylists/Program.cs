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

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("CrossSyncPlaylists.log");

            LibraryConfiguration config = new LibraryConfiguration(args[0]);

            /*if (!LibraryConfiguration.Valid)
            {
                LogConsole.WriteLine("Invalid Library Configuration File");
                return;
            }*/

            Directory.CreateDirectory(config.PlaylistTargetFolder);

            //string destplaylistfolder = @"z:/iTunes/Lossless/WPL/";
            //string rootlibpath = @"z:/iTunes/AAC/";
            //string newrootpath = @"z:/iTunes/Lossless/";
            //string m3uoffset = @"../";
            //string syncdir = "Purchased Sync";
            //string indexdir = @"z:/iTunes/Lossless/Purchased Sync";

            // TODO: Cull Cached Metadata Without Matching Files

            LogConsole.WriteLine("Indexing Files...");
            MetadataCache cache = new MetadataCache();
            if (File.Exists(args[0] + ".cache"))
                cache.Load(args[0] + ".cache");
            else
            {
                try
                {
                    cache.Load(config.ReferenceConfig + ".cache");
                }
                catch
                {

                }
            }
#if true
            cache.BeginBuildCache();
            foreach (var iloc in config.IndexLocations)
                cache.BuildCache(iloc.Target, false);
            cache.EndBuildCache();
            cache.Save(args[0] + ".cache");
#endif
            LogConsole.WriteLine("Total Parsed Files: " + cache.FileCache.Count);

            LogConsole.WriteLine("Building Dictionaries...");

            var aapairs = cache.FileCache.Select(kv => (kv.Value.Artist, kv.Value.StrippedAlbum)).Concat(
                cache.FileCache.Where(kv=>!string.IsNullOrWhiteSpace(kv.Value.AlbumArtist)).Select(kv => (kv.Value.AlbumArtist, kv.Value.StrippedAlbum))).Distinct();
            var aadict = aapairs.ToDictionary(k => k, k => cache.FileCache.Where(kv => (kv.Value.StrippedAlbum == k.StrippedAlbum) && (
                (kv.Value.Artist == k.Item1) || (kv.Value.AlbumArtist == k.Item1))).ToArray());
        
            int missing = 0;
            var missingfiles = new Dictionary<string, bool>();

            LogConsole.WriteLine("Loading iTunes Library XML...");

            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);

            LogConsole.WriteLine("iTunes Library Size: " + lib.Tracks.Count.ToString() + "  Playlist Count: " + lib.Playlists.Count);

            if ((args.Length == 2) && (args[1].ToLower() == "clean"))
            {
                LogConsole.WriteLine("Cleaning Old Playlist Directories...");

                foreach (string file in Directory.GetFiles(config.PlaylistTargetFolder, "*.*"))
                    File.Delete(file);
            }

            bool checkmode = ((args.Length == 2) && (args[1].ToLower() == "check"));

            foreach (iTunesPlaylist pl in lib.Playlists.Values)
            {
                int count = 0;
                if ((pl.Items.Count > MAX_PLAYLIST_COUNT)&&(!(checkmode && (pl.Title.ToLower() == "library"))))
                    continue;

                if (checkmode && (pl.Title.ToLower() != "library"))
                    continue;

                LogConsole.WriteLine("Converting Playlist: " + pl.Title);

                string plfilename = (config.PlaylistType.ToLower() == "wpl") ? Path.Combine(config.PlaylistTargetFolder, FixPath(pl.Title).PadRight(SONOS_NAME_PAD) + ".wpl") :
                    Path.Combine(config.PlaylistTargetFolder, FixPath(pl.Title) + ".m3u");

                MemoryStream ms = new MemoryStream();
                //StreamWriter m3uw = new StreamWriter(ms, Encoding.GetEncoding(28591));
                StreamWriter m3uw = new StreamWriter(ms, Encoding.UTF8);
                //m3uw.NewLine = "\n";

                if (File.Exists(plfilename))
                {
                    LogConsole.WriteLine("Skipping Due To Preexisting File");
                    continue;
                }

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
                        KeyValuePair<string, MetadataCacheEntry>[] newfiles = new KeyValuePair<string, MetadataCacheEntry>[0];
                        bool hasaa = aadict.ContainsKey((track.Artist, track.Album));
                        bool hasaaa = (!string.IsNullOrWhiteSpace(track.AlbumArtist)) && (aadict.ContainsKey((track.AlbumArtist, track.Album)));

                        if (hasaa)
                            newfiles = newfiles.Concat(aadict[(track.Artist, track.Album)].Where(kv => kv.Value.TrackNumber == track.TrackNumber)).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                        if (hasaaa)
                            newfiles = newfiles.Concat(aadict[(track.AlbumArtist, track.Album)].Where(kv => kv.Value.TrackNumber == track.TrackNumber)).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                        if (newfiles.Length == 0)
                        {
                            if (hasaa)
                                newfiles = newfiles.Concat(aadict[(track.Artist, track.Album)].Where(kv => kv.Value.Title.Equals(track.Title, StringComparison.InvariantCultureIgnoreCase))).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                            if (hasaaa)
                                newfiles = newfiles.Concat(aadict[(track.AlbumArtist, track.Album)].Where(kv => kv.Value.Title.Equals(track.Title, StringComparison.InvariantCultureIgnoreCase))).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                        }

                        if (newfiles.Length == 0)
                        {
                            IMetadataProvider provider = Metadata.GetProvider(track.LocalLocation);
                            hasaa = aadict.ContainsKey((provider.Artist, provider.Album));
                            try
                            {
                                hasaaa = (!string.IsNullOrWhiteSpace(provider.AlbumArtist)) && (aadict.ContainsKey((provider.AlbumArtist, provider.Album)));
                            }
                            catch
                            {
                                hasaaa = false;
                            }
                            if (hasaa)
                                newfiles = newfiles.Concat(aadict[(provider.Artist, provider.Album)].Where(kv => kv.Value.Title.Equals(provider.Title, StringComparison.InvariantCultureIgnoreCase))).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                            if (hasaaa)
                                newfiles = newfiles.Concat(aadict[(provider.AlbumArtist, provider.Album)].Where(kv => kv.Value.Title.Equals(provider.Title, StringComparison.InvariantCultureIgnoreCase))).Distinct().OrderByDescending(kv => kv.Value.SampleRate).ThenByDescending(kv => kv.Value.BitsPerSample).ToArray();
                        }

                        if (newfiles.Count(kv => kv.Value.TrackNumber == track.TrackNumber) > 0)
                            newfiles = newfiles.Where(kv => kv.Value.TrackNumber == track.TrackNumber).ToArray();

                        filepath = newfiles[0].Key;
                    }
                    catch 
                    {
                        bool hashed = missingfiles.ContainsKey(track.LocalLocation);
                        LogConsole.WriteLine("FNF: " + track.LocalLocation + ((!hashed) ? " (1st)" : ""));
                        if (!hashed)
                        {
                            missing++;
                            missingfiles.Add(track.LocalLocation, true);
                        }
                    }
                 
                    if (!string.IsNullOrWhiteSpace(filepath))
                    {
                        foreach (var iloc in config.IndexLocations)
                        {
                            if (filepath.Replace('\\', '/').StartsWith(iloc.Target, StringComparison.InvariantCultureIgnoreCase))
                                filepath = iloc.Offset + "/" + filepath.Remove(0, iloc.Target.Length).Replace('\\','/');
                        }
                        seqel.Add(new XElement("media", new XAttribute("src", filepath)));
                        m3uw.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                        m3uw.WriteLine(filepath);
                        count++;
                    }

                }

                if ((count != 0)&&(!checkmode))
                {
                    countat.Value = count.ToString();
                    XmlWriterSettings settings = new XmlWriterSettings();
                    settings.OmitXmlDeclaration = true;
                    settings.Indent = true;
                    settings.CloseOutput = true;
                    if (config.PlaylistType.Equals("wpl", StringComparison.InvariantCultureIgnoreCase))
                    {
                        StreamWriter w = new StreamWriter(plfilename);
                        XmlWriter xw = XmlWriter.Create(w, settings);
                        pd.Save(xw);
                        xw.Close();
                    }
                    m3uw.Flush();
                    if (config.PlaylistType.Equals("m3u", StringComparison.InvariantCultureIgnoreCase))
                        File.WriteAllBytes(plfilename, ms.ToArray());
                }
                m3uw.Dispose();
                ms.Dispose();
            }

            LogConsole.WriteLine("Total FNF: " + missing.ToString());

            LogConsole.Close();


        }
    }
}

