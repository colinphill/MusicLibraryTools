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
                Marshal.FinalReleaseComObject(app);
                return;
            }

            using StreamWriter w = new StreamWriter("ArtworkSizes.dat");

            int tracks = 0, noartwork = 0;
            var albums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (IITTrack trk in lib.Tracks)
            {
                try
                {
                    if (trk.Kind != ITTrackKind.ITTrackKindFile)
                        continue;

                    IITFileOrCDTrack filetrk = trk as IITFileOrCDTrack;
                    if (filetrk.VideoKind != ITVideoKind.ITVideoKindNone || filetrk.Genre == "Podcast")
                        continue;

                    tracks++;
                    string artist = string.IsNullOrEmpty(filetrk.AlbumArtist) ? filetrk.Artist : filetrk.AlbumArtist;
                    string album = filetrk.Album;
                    if (!albums.Add((artist ?? "") + "\0" + (album ?? "")))
                        continue;

                    LogConsole.WriteLine(tracks.ToString() + ") Checking Track: " + filetrk.Location);

                    IITArtworkCollection artcol = null;
                    try
                    {
                        artcol = filetrk.Artwork;
                        if (artcol.Count == 1)
                        {
                            IITArtwork art = artcol[1];
                            string ext = (art.Format == ITArtworkFormat.ITArtworkFormatBMP) ? "bmp" :
                                (art.Format == ITArtworkFormat.ITArtworkFormatJPEG) ? "jpg" :
                                (art.Format == ITArtworkFormat.ITArtworkFormatPNG) ? "png" : "unknown";

                            string artfile = Path.Combine(Path.GetTempPath(), $"mlt-art-{Guid.NewGuid():N}.{ext}");
                            LogConsole.WriteLine("Saving Artwork: " + artfile);
                            try
                            {
                                art.SaveArtworkToFile(artfile);
                                using Image im = Image.FromFile(artfile);
                                w.WriteLine(artist + "|" + album + "|" + im.Width.ToString() + "|" + im.Height.ToString() + "|" + new FileInfo(artfile).Length.ToString());
                            }
                            finally
                            {
                                Marshal.FinalReleaseComObject(art);
                                try
                                {
                                    if (File.Exists(artfile))
                                        File.Delete(artfile);
                                }
                                catch (Exception ex)
                                {
                                    LogConsole.WriteLine($"Unable to delete temporary artwork {artfile}: {ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            w.WriteLine(artist + "|" + album + "|" + "0|0");
                            noartwork++;
                        }
                    }
                    finally
                    {
                        if (artcol != null)
                            Marshal.FinalReleaseComObject(artcol);
                    }
                }
                catch (Exception ex)
                {
                    LogConsole.WriteLine($"Unable to inspect track: {ex.Message}");
                }
                finally
                {
                    Marshal.FinalReleaseComObject(trk);
                }
            }

            Marshal.FinalReleaseComObject(lib);
            Marshal.FinalReleaseComObject(app);

            LogConsole.WriteLine("Analyzed Tracks: " + tracks.ToString());
            LogConsole.WriteLine("Analyzed Albums: " + albums.Count.ToString());
            LogConsole.WriteLine("Albums Without Artwork: " + noartwork.ToString());

        }
    }
}
