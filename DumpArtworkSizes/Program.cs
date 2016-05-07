/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/DumpArtworkSizes/Program.cs $
 * $Date: 2013-01-06 06:58:46 -0700 (Sun, 06 Jan 2013) $
 * $Revision: 13 $
 * $Author: colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Drawing;
using System.Runtime.InteropServices;
using iTunesLib;
using ConsoleTools;

namespace DumpArtworkSizes
{

    class Program
    {

        static void Main(string[] args)
        {

            LogConsole.SwitchFile("DumpArtworkSizes.log");

            if (args.Length != 1)
            {
                LogConsole.WriteLine("Usage: DumpArtworkSizes <playlistname>");
                return;
            }

            iTunesApp app = new iTunesApp();
            IITPlaylist lib = null;

            foreach (IITSource s in app.Sources)
            {
                lib = s.Playlists.get_ItemByName(args[0]);
                Marshal.FinalReleaseComObject(s);
                if (lib != null)
                    break;
            }
            if (lib == null)
            {
                LogConsole.WriteLine("Error Locating Playlist: " + args[0]);
                return;
            }

            StreamWriter w = new StreamWriter("ArtworkSizes.dat");

            int tracks = 0, noartwork = 0;
            Dictionary<string, bool> albums = new Dictionary<string, bool>();

            foreach (IITTrack trk in lib.Tracks)
            {
                if (trk.Kind == ITTrackKind.ITTrackKindFile)
                {
                    IITFileOrCDTrack filetrk = trk as IITFileOrCDTrack;
                    if (filetrk.VideoKind == ITVideoKind.ITVideoKindNone)
                    {
                        if (filetrk.Genre == "Podcast")
                            continue;
                        tracks++;

                        string artist = filetrk.Artist;
                        if ((filetrk.AlbumArtist != null) && (filetrk.AlbumArtist != ""))
                            artist = filetrk.AlbumArtist;
                        string album = filetrk.Album;

                        string key = artist + "," + album;
                        if (albums.ContainsKey(key))
                            continue;

                        albums.Add(key, true);

                        LogConsole.WriteLine(tracks.ToString() + ") Checking Track: " + filetrk.Location);

                        IITArtworkCollection artcol = filetrk.Artwork;
                        if (artcol.Count == 1)
                        {
                            IITArtwork art = artcol[1];
                            string ext = (art.Format == ITArtworkFormat.ITArtworkFormatBMP) ? "bmp" :
                                (art.Format == ITArtworkFormat.ITArtworkFormatJPEG) ? "jpg" :
                                (art.Format == ITArtworkFormat.ITArtworkFormatPNG) ? "png" : "unknown";

                            string artfile = Environment.CurrentDirectory + "\\temp." + ext;
                            LogConsole.WriteLine("Saving Artwork: " + artfile);
                            art.SaveArtworkToFile(artfile);
                            // TODO: Analyze
                            Image im = Image.FromFile(artfile);
                            w.WriteLine(artist + "|" + album + "|" + im.Width.ToString() + "|" + im.Height.ToString() + "|" + new FileInfo(artfile).Length.ToString());
                            im.Dispose();
                            Marshal.FinalReleaseComObject(art);
                        }
                        else
                        {
                            w.WriteLine(artist + "|" + album + "|" + "0|0");
                            noartwork++;
                        }
                        Marshal.FinalReleaseComObject(artcol);
                        

                    }

                    Marshal.FinalReleaseComObject(trk);
                }
            }

            Marshal.FinalReleaseComObject(app);

            LogConsole.WriteLine("Analyzed Tracks: " + tracks.ToString());
            LogConsole.WriteLine("Analyzed Albums: " + albums.Keys.Count.ToString());
            LogConsole.WriteLine("Albums Without Artwork: " + noartwork.ToString());

            w.Close();

        }
    }
}
