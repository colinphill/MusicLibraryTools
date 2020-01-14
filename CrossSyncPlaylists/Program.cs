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

        static VorbisComments ReadID3Metadata(string filename)
        {
            ID3v2Tag tag = filename.ToLower().EndsWith(".mp3") ? (ID3v2Tag)new MP3File(filename) : (ID3v2Tag)new DSFFile(filename);
                        
            VorbisComments vc = new VorbisComments();

            try
            {
                vc.Comments.Add(new KeyValuePair<string,string>("TITLE", (tag.FindFrame("TIT2") as TextFrame).Text));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string,string>("TITLE", ""));
            }

            try
            {
                vc.Comments.Add(new KeyValuePair<string,string>("ALBUM", (tag.FindFrame("TALB") as TextFrame).Text));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string,string>("ALBUM", ""));
            }

            try
            {
                vc.Comments.Add(new KeyValuePair<string,string>("ARTIST", (tag.FindFrame("TPE1") as TextFrame).Text));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string,string>("ARTIST", ""));
            }

            try
            {
                vc.Comments.Add(new KeyValuePair<string,string>("ALBUMARTIST", (tag.FindFrame("TPE2") as TextFrame).Text));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string,string>("ALBUMARTIST", ""));
            }

            try
            {
                vc.Comments.Add(new KeyValuePair<string,string>("TRACKNUMBER", int.Parse((tag.FindFrame("TRCK") as TextFrame).Text.Split("/".ToCharArray())[0]).ToString("D2")));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string,string>("TRACKNUMBER", "00"));
            }

            return vc;
        }

        static VorbisComments ReadM4AMetadata(string filename)
        {
            RootAtom root = new RootAtom(filename);
            Atom_ilst atom = root.FindPath("moov.udta.meta.ilst") as Atom_ilst;

            VorbisComments vc = new VorbisComments();
            vc.Comments.Add(new KeyValuePair<string, string>("TITLE", (atom.FindPath("©nam.data") as Atom_data).Text));

            try
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ALBUM", (atom.FindPath("©alb.data") as Atom_data).Text));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ALBUM", ""));
            }

            try
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ARTIST", (atom.FindPath("©ART.data") as Atom_data).Text));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ARTIST", ""));
            }

            try
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ALBUMARTIST", (atom.FindPath("aART.data") as Atom_data).Text));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ALBUMARTIST", ""));
            }

            try
            {
                vc.Comments.Add(new KeyValuePair<string, string>("TRACKNUMBER", (atom.FindPath("trkn.data") as Atom_data).TrackNumber.ToString("D2")));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string, string>("TRACKNUMBER", "00"));
            }

            return vc;
        }

        static void IndexAudioFiles(string path, Dictionary<string, KeyValuePair<DateTime, VorbisComments>> files, Dictionary<string, bool> touchlist)
        {
            foreach (string dir in Directory.GetDirectories(path))
                IndexAudioFiles(dir, files, touchlist);

            string[] exts = { ".flac", ".ogg", ".m4a", ".mp3" };

            IEnumerable<string> filelist = Directory.GetFiles(path, "*.*").Where(s => exts.Contains(Path.GetExtension(s).ToLower()));

            foreach (string file in filelist)
            {
                DateTime lastwrite = File.GetLastWriteTimeUtc(file);
                if (files.ContainsKey(file))
                {
                    KeyValuePair<DateTime, VorbisComments> kv = files[file];
                    if (lastwrite <= kv.Key)
                    {
                        touchlist[file] = true;
                        continue;
                    }
                    files.Remove(file);
                }

                try
                {
                    VorbisComments vc;
                    if (Path.GetExtension(file).ToLower() == ".flac")
                    {
                        FLACFile f = new FLACFile(file);
                        vc = f;
                    }
                    else if (Path.GetExtension(file).ToLower() == ".ogg")
                    {
                        OggVorbisFile f = new OggVorbisFile(file);
                        vc = f;
                     }
                    else if (Path.GetExtension(file).ToLower() == ".m4a")
                    {
                        vc = ReadM4AMetadata(file);
                    }
                    else if (Path.GetExtension(file).ToLower() == ".mp3")
                    {
                        vc = ReadID3Metadata(file);
                    }
                    else
                        throw new InvalidDataException();

                    vc.Artworks.Clear();
                    files.Add(file, new KeyValuePair<DateTime, VorbisComments>(lastwrite, vc));
                    touchlist[file] = true;

                    if ((files.Count % 100) == 0)
                    {
                        Console.Write("Indexed Count: " + files.Count.ToString() + "      \r");
                        Console.Out.Flush();
                    }
                }
                catch
                {
                    Console.WriteLine("Failed Read Metadata: " + file);
                }
            }
        }
               
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

        static Dictionary<string, KeyValuePair<DateTime, VorbisComments>> LoadMetadataCache(string filename)
        {
            //return new Dictionary<string, KeyValuePair<DateTime, VorbisComments>>();
            try
            {
                FileStream fs = new FileStream(filename, FileMode.Open, FileAccess.Read);
                BinaryFormatter bf = new BinaryFormatter();
                Dictionary<string, KeyValuePair<DateTime, VorbisComments>> res =
                    (Dictionary<string, KeyValuePair<DateTime, VorbisComments>>)bf.Deserialize(fs);
                fs.Close();
                return res;
            }
            catch
            {
                return new Dictionary<string, KeyValuePair<DateTime, VorbisComments>>();
            }
        }

        static void SaveMetadataCache(string filename, Dictionary<string, KeyValuePair<DateTime, VorbisComments>> index)
        {
            FileStream fs = new FileStream(filename, FileMode.Create, FileAccess.Write);
            BinaryFormatter bf = new BinaryFormatter();
            bf.Serialize(fs, index);
            fs.Close();
        }

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("CrossSyncPlaylists.log");

            if (!LibraryConfiguration.Valid)
            {
                LogConsole.WriteLine("Invalid Library Configuration File");
                return;
            }

            Directory.CreateDirectory(LibraryConfiguration.WPLTargetFolder);

            //string destplaylistfolder = @"z:/iTunes/Lossless/WPL/";
            //string rootlibpath = @"z:/iTunes/AAC/";
            //string newrootpath = @"z:/iTunes/Lossless/";
            //string m3uoffset = @"../";
            //string syncdir = "Purchased Sync";
            //string indexdir = @"z:/iTunes/Lossless/Purchased Sync";

            // TODO: Cull Cached Metadata Without Matching Files

            Dictionary<string, bool> touchlist = new Dictionary<string, bool>();
 
            Dictionary<string, KeyValuePair<DateTime, VorbisComments>> parsedfiles = LoadMetadataCache("CrossSyncPlaylists.cache");

            foreach (string cachedfile in parsedfiles.Keys)
                touchlist.Add(cachedfile, false);
           
            Dictionary<string, bool> missingfiles = new Dictionary<string, bool>();

            LogConsole.WriteLine("Indexing FLAC/Ogg/M4A/MP3 Files...");
            IndexAudioFiles(LibraryConfiguration.CrossSyncTargetMusicFolder, parsedfiles, touchlist);

            foreach (string removed in touchlist.Where(k => k.Value == false).Select(k => k.Key))
            {
                LogConsole.WriteLine("Removed File: " + removed);
                parsedfiles.Remove(removed);
            }

            touchlist.Clear();

            LogConsole.WriteLine("Total Parsed Files: " + parsedfiles.Count + "                    ");

            SaveMetadataCache("CrossSyncPlaylists.cache", parsedfiles);
            
            int missing = 0;

            LogConsole.WriteLine("Loading iTunes Library XML...");

            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);

            LogConsole.WriteLine("iTunes Library Size: " + lib.Tracks.Count.ToString() + "  Playlist Count: " + lib.Playlists.Count);

            if ((args.Length == 1) && (args[0].ToLower() == "clean"))
            {
                LogConsole.WriteLine("Cleaning Old Playlist Directories...");

                foreach (string file in Directory.GetFiles(LibraryConfiguration.WPLTargetFolder, "*.*"))
                    File.Delete(file);
                foreach (string file in Directory.GetFiles(LibraryConfiguration.M3UTargetFolder, "*.*"))
                    File.Delete(file);
            }

            bool checkmode = ((args.Length == 1) && (args[0].ToLower() == "check"));

            foreach (iTunesPlaylist pl in lib.Playlists.Values)
            {
                int count = 0;
                if ((pl.Items.Count > MAX_PLAYLIST_COUNT)&&(!(checkmode && (pl.Title.ToLower() == "library"))))
                    continue;

                if (checkmode && (pl.Title.ToLower() != "library"))
                    continue;

                LogConsole.WriteLine("Converting Playlist: " + pl.Title);

                string plfilename = Path.Combine(LibraryConfiguration.WPLTargetFolder, FixPath(pl.Title).PadRight(SONOS_NAME_PAD) + ".wpl");
                string m3ufilename = Path.Combine(LibraryConfiguration.M3UTargetFolder, FixPath(pl.Title) + ".m3u");

                MemoryStream ms = new MemoryStream();
                StreamWriter m3uw = new StreamWriter(ms, Encoding.GetEncoding(28591));
                //m3uw.NewLine = "\n";

                if (File.Exists(plfilename) && File.Exists(m3ufilename))
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

                    iTunesTrack track = lib.Tracks[item];
                    if (track.Kind.ToLower().Contains("video") || (track.Type.ToLower() != "file") || (track.Kind.ToLower().Contains("protected")) || (track.Kind.ToLower().Contains("book")) || (track.Kind.ToLower().Contains("audible") ||
                        track.Kind.ToLower().Contains("document") || track.Kind.ToLower().Contains("app") || track.Kind.ToLower().Contains("tone")))
                        continue;

                    string filepath = Uri.UnescapeDataString(new Uri(LibraryConfiguration.CrossSyncSourceLibraryPath).MakeRelativeUri(new Uri(track.LocalLocation)).ToString());

                    // Handle protected files which have been reripped
                    if (Path.GetExtension(filepath).ToLower() == ".m4p")
                        filepath = Path.ChangeExtension(filepath, ".m4a");

                    // One off correction for voice control in car
                    filepath = filepath.Replace("Kesha", "Ke$ha");

                    string testpath = Path.Combine(LibraryConfiguration.CrossSyncTargetLibraryPath, filepath);
                    if (!File.Exists(testpath))
                    {
                        try
                        {
                            //IEnumerable<KeyValuePair<string, KeyValuePair<DateTime, VorbisComments>>> s = parsedfiles.Where(el => (el.Value.Value["ALBUM"] == track.Album) &&
                            //        (el.Value.Value["TITLE"] == track.Title));
                            KeyValuePair<string, KeyValuePair<DateTime, VorbisComments>> [] newfiles = parsedfiles.Where(el => (el.Value.Value["ALBUM"] == track.Album) &&
                                    (el.Value.Value["TITLE"] == track.Title) && ((el.Value.Value["ARTIST"] == track.Artist) || (el.Value.Value["ALBUMARTIST"] == track.AlbumArtist) ||
                                    (el.Value.Value["ALBUMARTIST"] == track.Artist))).ToArray();
                            string newpath;
                            try
                            {
                                newpath = newfiles.Single().Key;
                            }
                            catch
                            {
                                newpath = newfiles.Where(el => el.Value.Value.TrackNumber == track.TrackNumber).Single().Key;
                            }
                            Uri newuri = new Uri(LibraryConfiguration.CrossSyncTargetLibraryPath).MakeRelativeUri(new Uri(newpath));
                            filepath = Uri.UnescapeDataString(newuri.ToString());
                        }
                        catch
                        {
                            bool hashed = missingfiles.ContainsKey(testpath);

                            LogConsole.WriteLine("FNF: " + testpath + ((!hashed) ? " (1st)" : ""));

                            if (!hashed)
                            {
                                missing++;
                                missingfiles.Add(testpath, true);
                            }
                        }
                    }


                    testpath = LibraryConfiguration.CrossSyncTargetLibraryPath + filepath;
                    if (File.Exists(testpath))
                    {
                        string m3ufp = Path.Combine(LibraryConfiguration.M3UOffset, filepath);
                        filepath = Path.Combine(LibraryConfiguration.WPLOffset, filepath);

                        seqel.Add(new XElement("media", new XAttribute("src", filepath)));
                        m3uw.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                        m3uw.WriteLine(m3ufp);
                    }

                    count++;

                }

                if ((count != 0)&&(!checkmode))
                {
                    countat.Value = count.ToString();

                    XmlWriterSettings settings = new XmlWriterSettings();
                    settings.OmitXmlDeclaration = true;
                    settings.Indent = true;
                    settings.CloseOutput = true;
                    /*StreamWriter w = new StreamWriter(plfilename);
                    XmlWriter xw = XmlWriter.Create(w, settings);
                    pd.Save(xw);
                    xw.Close();*/

                    m3uw.Flush();
                    File.WriteAllBytes(m3ufilename, ms.ToArray());
                }

                m3uw.Dispose();
                ms.Dispose();

                
            }

            LogConsole.WriteLine("Total FNF: " + missing.ToString());

            LogConsole.Close();


        }
    }
}

