using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using MusicFileUtilities;
using MusicLibraryTools;
using ConsoleTools;
using iTunes;

namespace UpdateCarCard
{
    class Program
    {
        static string FixPath(string item)
        {
            string fix = item;
            foreach (char c in Path.GetInvalidFileNameChars())
                fix = fix.Replace(c.ToString(), "");
            foreach (char c in Path.GetInvalidPathChars())
                fix = fix.Replace(c.ToString(), "");
            fix = fix.Replace('$', 's');
            fix = fix.Trim();
            while (fix.EndsWith("."))
                fix = fix.Remove(fix.Length - 1);
            return fix;
        }

        static void DeleteEmptyFolders(string basedir)
        {
            foreach (string dir in Directory.GetDirectories(basedir))
                DeleteEmptyFolders(dir);
            if ((Directory.GetDirectories(basedir).Length == 0) && (Directory.GetFiles(basedir).Length == 0))
                Directory.Delete(basedir);
        }

        static void IndexDirectory(string basedir, Dictionary<string, bool> hits)
        {
            foreach (string dir in Directory.GetDirectories(basedir))
                IndexDirectory(dir, hits);
            foreach (string file in Directory.GetFiles(basedir, "*.m3u"))
                File.Delete(file);
            foreach (string file in Directory.GetFiles(basedir))
                hits.Add(file.ToLower(), false);
        }

        static int MAX_PLAYLIST_COUNT = 500;
        
        static void Main(string[] args)
        {
            LogConsole.SwitchFile("UpdateCarCard.log");

            if (args.Length != 1)
            {
                LogConsole.WriteLine("Usage: UpdateCarCard <destination>");
                return;
            }

            string basedir = args[0];
            if (!basedir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                basedir = basedir + Path.DirectorySeparatorChar;

            string artistsdir = Path.Combine(args[0], "Artists");
            string albumsdir = Path.Combine(args[0], "Albums");
            string playlistsdir = Path.Combine(args[0], "Playlists");
            string contributingartistsdir = Path.Combine(args[0], "Contributing Artists");

            Directory.CreateDirectory(artistsdir);
            Directory.CreateDirectory(albumsdir);
            Directory.CreateDirectory(playlistsdir);
            
            if (Directory.Exists(contributingartistsdir))
                Directory.Delete(contributingartistsdir, true);
            
            Directory.CreateDirectory(contributingartistsdir);

            foreach (string file in Directory.GetFiles(albumsdir, "*.m3u"))
                File.Delete(file);

            foreach (string file in Directory.GetFiles(playlistsdir, "*.m3u"))
                File.Delete(file);

            LogConsole.WriteLine("Loading iTunes Library XML...");
            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);

            string[] kinds = lib.Tracks.Select(t => t.Value.Kind).Distinct().ToArray();

            KeyValuePair<int, iTunesTrack> [] library = lib.Tracks.Where(kv => (kv.Value.Type == "File") && (kv.Value.Kind.Contains("audio file") && (!kv.Value.Kind.ToLower().Contains("protected")))).ToArray();
            int count = 0;
            
            string[] artists = library.Select(kv => string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) ? kv.Value.Artist : kv.Value.AlbumArtist).Distinct().ToArray();

            Dictionary<string, string> idmap = new Dictionary<string, string>();
            
            Dictionary<string, bool> hits = new Dictionary<string,bool>();
            IndexDirectory(artistsdir, hits);

            foreach (string artist in artists)
            {
                iTunesTrack[] tracks = library.Where(kv => string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) ? (kv.Value.Artist == artist) : (kv.Value.AlbumArtist == artist)).Select(kv => kv.Value).ToArray();
                string[] albums = tracks.Select(t => t.Album).Distinct().ToArray();

                string fnartist = artist;
                fnartist = fnartist.ToLower().StartsWith("the ") ? fnartist.Substring(4) : fnartist;
                fnartist = fnartist.ToLower().StartsWith("a ") ? fnartist.Substring(2) : fnartist;
                fnartist = FixPath(fnartist);
                //fnartist = fnartist.Replace("Ke$ha", "Kesha");

                string artistdir = Path.Combine(artistsdir, fnartist);
                Directory.CreateDirectory(artistdir);

                using (StreamWriter allwriter = new StreamWriter(Path.Combine(artistdir, "All Tracks.m3u")),
                    allalbumswriter = new StreamWriter(Path.Combine(artistdir, "All Albums.m3u")),
                    allalbumsbyyearwriter = new StreamWriter(Path.Combine(artistdir, "All Albums By Year.m3u")))
             {
                    allwriter.WriteLine("#EXTM3U");
                    allalbumswriter.WriteLine("#EXTM3U");
                    allalbumsbyyearwriter.WriteLine("#EXTM3U");

                    foreach (string album in albums.OrderBy(a => a.ToLower()))
                    {
                        string fnalbum = FixPath(album);

                        string albumdir = Path.Combine(artistdir, fnalbum);
                        Directory.CreateDirectory(albumdir);

                        string albumplaylist = Path.Combine(albumsdir, fnalbum + ".m3u");
                        
                        string[] multiartists = library.Where(kv => kv.Value.Album.ToLower() == album.ToLower()).Select(kv => string.IsNullOrWhiteSpace(kv.Value.AlbumArtist) ? kv.Value.Artist : kv.Value.AlbumArtist).Distinct().ToArray();
                        if (multiartists.Length > 1)
                            albumplaylist = Path.Combine(albumsdir, fnalbum + " (" + fnartist + ").m3u");

                        using (StreamWriter albumwriter = new StreamWriter(albumplaylist))
                        {
                            albumwriter.WriteLine("#EXTM3U");

                            iTunesTrack[] albumtracks = tracks.Where(t => t.Album == album).OrderBy(t => t.TrackNumber).ToArray();

                            foreach (iTunesTrack track in albumtracks)
                            {
                                int tracknumber = track.TrackNumber ?? 0;
                                string fntrack = tracknumber.ToString("D3") + " " + FixPath(track.Title) + Path.GetExtension(track.LocalLocation);
                                string trackfile = Path.Combine(albumdir, fntrack);
                                bool copy = !File.Exists(trackfile) ? true : File.GetLastWriteTimeUtc(track.LocalLocation) > File.GetLastWriteTimeUtc(trackfile);
                                count++;
                                if (copy)
                                {
                                    LogConsole.WriteLine(track.LocalLocation + " -> " + trackfile + " (" + count + "/" + library.Length + ")");
                                    File.Copy(track.LocalLocation, trackfile, true);

                                }
                                hits[trackfile.ToLower()] = true;
                                idmap.Add(track.PersistentID, trackfile);

                                allalbumswriter.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                                allalbumswriter.WriteLine(trackfile.Replace(basedir, Path.DirectorySeparatorChar.ToString()));
                                albumwriter.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                                albumwriter.WriteLine(trackfile.Replace(basedir, Path.DirectorySeparatorChar.ToString()));

                            }
                        }

                        LogConsole.WriteLine(albumdir);
                    }

                    foreach (string album in albums.OrderBy(a => tracks.Where(t => t.Album == a).Select(t => t.Year).Max()).ThenBy(a => a.ToLower()))
                    {
                        iTunesTrack[] albumtracks = tracks.Where(t => t.Album == album).OrderBy(t => t.TrackNumber).ToArray();
                        foreach (iTunesTrack track in albumtracks)
                        {
                            allalbumsbyyearwriter.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                            allalbumsbyyearwriter.WriteLine(idmap[track.PersistentID].Replace(basedir, Path.DirectorySeparatorChar.ToString()));
                        }
                    }

                    foreach (iTunesTrack track in tracks.OrderBy(t => t.Title.ToLower()))
                    {
                        allwriter.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                        allwriter.WriteLine(idmap[track.PersistentID].Replace(basedir, Path.DirectorySeparatorChar.ToString()));
                    }


                }

                //LogConsole.WriteLine(fnartist);
            }

            string[] contributingartists = library.Select(kv =>kv.Value.Artist).Distinct().ToArray();

            foreach (string artist in contributingartists) 
            {
                iTunesTrack[] tracks = library.Where(kv => kv.Value.Artist == artist).Select(kv => kv.Value).ToArray();
                string[] albums = tracks.Select(t => t.Album).Distinct().ToArray();

                string fnartist = artist;
                fnartist = fnartist.ToLower().StartsWith("the ") ? fnartist.Substring(4) : fnartist;
                fnartist = fnartist.ToLower().StartsWith("a ") ? fnartist.Substring(2) : fnartist;
                fnartist = FixPath(fnartist);
                //fnartist = fnartist.Replace("Ke$ha", "Kesha");

                string artistdir = Path.Combine(contributingartistsdir, fnartist);
                Directory.CreateDirectory(artistdir);

                using (StreamWriter allwriter = new StreamWriter(Path.Combine(artistdir, "All Tracks.m3u")),
                    allalbumswriter = new StreamWriter(Path.Combine(artistdir, "All Albums.m3u")),
                    allalbumsbyyearwriter = new StreamWriter(Path.Combine(artistdir, "All Albums By Year.m3u")))
                {
                    allwriter.WriteLine("#EXTM3U");
                    allalbumswriter.WriteLine("#EXTM3U");
                    allalbumsbyyearwriter.WriteLine("#EXTM3U");

                    foreach (string album in albums.OrderBy(a => a.ToLower()))
                    {
                        string fnalbum = FixPath(album);

                        string albumplaylist = Path.Combine(artistdir, fnalbum + ".m3u");

                        using (StreamWriter albumwriter = new StreamWriter(albumplaylist))
                        {
                            albumwriter.WriteLine("#EXTM3U");

                            iTunesTrack[] albumtracks = tracks.Where(t => t.Album == album).OrderBy(t => t.TrackNumber).ToArray();

                            foreach (iTunesTrack track in albumtracks)
                            {
                                allalbumswriter.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                                allalbumswriter.WriteLine(idmap[track.PersistentID].Replace(basedir, Path.DirectorySeparatorChar.ToString()));
                                albumwriter.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                                albumwriter.WriteLine(idmap[track.PersistentID].Replace(basedir, Path.DirectorySeparatorChar.ToString()));
                            }
                        }

                        LogConsole.WriteLine(albumplaylist);
                    }

                    foreach (string album in albums.OrderBy(a => tracks.Where(t => t.Album == a).Select(t => t.Year).Max()).ThenBy(a => a.ToLower()))
                    {
                        iTunesTrack[] albumtracks = tracks.Where(t => t.Album == album).OrderBy(t => t.TrackNumber).ToArray();
                        foreach (iTunesTrack track in albumtracks)
                        {
                            allalbumsbyyearwriter.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                            allalbumsbyyearwriter.WriteLine(idmap[track.PersistentID].Replace(basedir, Path.DirectorySeparatorChar.ToString()));
                        }
                    }

                    foreach (iTunesTrack track in tracks.OrderBy(t => t.Title.ToLower()))
                    {
                        allwriter.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                        allwriter.WriteLine(idmap[track.PersistentID].Replace(basedir, Path.DirectorySeparatorChar.ToString()));
                    }


                }

                //LogConsole.WriteLine(fnartist);
            }


            foreach (iTunesPlaylist pl in lib.Playlists.Values)
            {
                count = 0;
                if ((pl.Items.Count > MAX_PLAYLIST_COUNT) || ((pl.Title.ToLower() == "library")))
                    continue;

                using (StreamWriter plwriter = new StreamWriter(Path.Combine(playlistsdir, FixPath(pl.Title) + ".m3u")))
                {
                    plwriter.WriteLine("#EXTM3U");

                    foreach (int item in pl.Items)
                    {

                        iTunesTrack track = lib.Tracks[item];
                        if (track.Kind.ToLower().Contains("video") || (track.Type.ToLower() != "file") || (track.Kind.ToLower().Contains("protected")) || (track.Kind.ToLower().Contains("book")) || (track.Kind.ToLower().Contains("audible") ||
                            track.Kind.ToLower().Contains("document") || track.Kind.ToLower().Contains("app") || track.Kind.ToLower().Contains("tone")))
                            continue;
                        plwriter.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                        plwriter.WriteLine(idmap[track.PersistentID].Replace(basedir, Path.DirectorySeparatorChar.ToString()));
                        count++;
                    }
                }

                if (count == 0)
                    File.Delete(Path.Combine(playlistsdir, FixPath(pl.Title) + ".m3u"));

            }


            foreach (string file in hits.Where(kv => kv.Value == false).Select(kv => kv.Key))
            {
                LogConsole.WriteLine("Deleting Miss: " + file);
                File.Delete(file);
            }

            DeleteEmptyFolders(artistsdir);



        }
    }
}
