using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.IO;
using iTunesLib;
using iTunes;
using MusicFileUtilities;

namespace BackSyncPlaylists
{
    class Program
    {

        static VorbisComments ReadID3Metadata(string filename)
        {
            ID3v2Tag tag = new ID3v2Tag(filename);

            VorbisComments vc = new VorbisComments();

            try
            {
                vc.Comments.Add(new KeyValuePair<string, string>("TITLE", (tag.FindFrame("TIT2") as TextFrame).Text));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string, string>("TITLE", ""));
            }

            try
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ALBUM", (tag.FindFrame("TALB") as TextFrame).Text));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ALBUM", ""));
            }

            try
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ARTIST", (tag.FindFrame("TPE1") as TextFrame).Text));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ARTIST", ""));
            }

            try
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ALBUMARTIST", (tag.FindFrame("TPE2") as TextFrame).Text));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string, string>("ALBUMARTIST", ""));
            }

            try
            {
                vc.Comments.Add(new KeyValuePair<string, string>("TRACKNUMBER", int.Parse((tag.FindFrame("TRCK") as TextFrame).Text.Split("/".ToCharArray())[0]).ToString("D2")));
            }
            catch
            {
                vc.Comments.Add(new KeyValuePair<string, string>("TRACKNUMBER", "00"));
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


        static void Main(string[] args)
        {
            iTunesApp app = new iTunesApp();
            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);

            string wpldir = @"z:\itunes\lossless\wpl";

            //app.
            foreach (string wplfile in Directory.GetFiles(wpldir, "*.wpl"))
            {
                XDocument doc = XDocument.Load(wplfile);
                string plname = doc.Descendants("title").Single().Value;

                string[] plfiles = doc.Descendants("media").Select(n => Path.Combine(wpldir, n.Attribute("src").Value)).ToArray();

                IITPlaylist playlist = app.CreatePlaylist(plname);
                IITUserPlaylist libplaylist = playlist as IITUserPlaylist;


                foreach (string plfile in plfiles)
                {
                    try
                    {
                        Console.WriteLine(plfile);
                        VorbisComments tags = null;
                        if (Path.GetExtension(plfile).ToLower() == ".flac")
                        {
                            FLACFile f = new FLACFile(plfile);
                            tags = f;
                        }
                        else if (Path.GetExtension(plfile).ToLower() == ".ogg")
                        {
                            OggVorbisFile f = new OggVorbisFile(plfile);
                            tags = f;
                        }
                        else if (Path.GetExtension(plfile).ToLower() == ".m4a")
                        {
                            tags = ReadM4AMetadata(plfile);
                        }
                        else if (Path.GetExtension(plfile).ToLower() == ".mp3")
                        {
                            tags = ReadID3Metadata(plfile);
                        }
                        else
                            throw new InvalidDataException();

                        KeyValuePair<int, iTunesTrack>[] tracks = lib.Tracks.Where(kv => (kv.Value.Title.ToLower() == tags.Title.ToLower()) && (kv.Value.Album.ToLower() == tags.Album.ToLower()) && ((kv.Value.Artist.ToLower() == tags.Artist.ToLower()) || (kv.Value.Artist.ToLower() == tags.AlbumArtist.ToLower())) && ((kv.Value.TrackNumber == tags.TrackNumber) || (tags.TrackNumber == 0))).ToArray();
                        if (tracks.Length != 1)
                        {
                            Console.WriteLine();
                        }
                        else
                        {
                            int highpid = int.Parse(tracks[0].Value.PersistentID.Substring(0, 8), System.Globalization.NumberStyles.HexNumber);
                            int lowpid = int.Parse(tracks[0].Value.PersistentID.Substring(8), System.Globalization.NumberStyles.HexNumber);
                            IITTrack track = app.LibraryPlaylist.Tracks.get_ItemByPersistentID(highpid, lowpid);
                            if (track != null)
                            {
                                Console.WriteLine(track.Name);
                                libplaylist.AddTrack(track);
                            }
                            else
                                Console.WriteLine();
                            //libplaylist.AddFile(tracks[0].Value.LocalLocation);
                        }



                        Console.WriteLine();
                    }
                    catch
                    {

                    }

                }




                Console.WriteLine();

            }



        }
    }
}
