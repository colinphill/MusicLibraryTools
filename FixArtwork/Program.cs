/*
 * FixArtwork: Resize artwork for Sonos iPhone controller/convert to JPEG/store in tags. 
 * 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/FixArtwork/Program.cs $
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
using System.Runtime.InteropServices;

using iTunesLib;
using System.Drawing;
using System.Drawing.Imaging;
using ConsoleTools;

namespace FixArtwork
{

    class Program
    {

        const long THRESHOLD = 225 * 1024;
        const int SIZE = 600;

        static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("FixArtwork.log");

            iTunesApp app = new iTunesApp();

            IITPlaylist lib = null;
            if (args.Length > 0)
            {
                foreach (IITSource s in app.Sources)
                {
                    lib = s.Playlists.get_ItemByName(args[0]);
                    Marshal.FinalReleaseComObject(s);
                    if (lib != null)
                        break;
                }
                if (lib == null)
                    lib = app.LibraryPlaylist;
            }
            else
            {
                LogConsole.WriteLine("Usage: FixArtwork <playlist>");
                return;
            }

            int tracks = 0;
            int artwork = 0;
            int missing = 0;
            int errors = 0;
            int large = 0;
   
            foreach (IITTrack trk in lib.Tracks)
            {
                if (trk.Kind == ITTrackKind.ITTrackKindFile)
                {
                    IITFileOrCDTrack filetrk = trk as IITFileOrCDTrack;
                    if (filetrk.VideoKind == ITVideoKind.ITVideoKindNone)
                    {
                        tracks++;
                        IITArtworkCollection artcol = filetrk.Artwork;
                        if (artcol.Count == 1)
                        {
                            try
                            {
                                IITArtwork art = artcol[1];
                                string ext = (art.Format == ITArtworkFormat.ITArtworkFormatBMP) ? "bmp" :
                                    (art.Format == ITArtworkFormat.ITArtworkFormatJPEG) ? "jpg" :
                                    (art.Format == ITArtworkFormat.ITArtworkFormatPNG) ? "png" : "unknown";

                                LogConsole.WriteLine(tracks.ToString() + " Artwork (" + ext + "): " + filetrk.Location);
                                string artfile = Environment.CurrentDirectory + "\\temp." + ext;
                                LogConsole.WriteLine("Saving Artwork: " + artfile);
                                art.SaveArtworkToFile(artfile);

                                FileStream artstream = File.OpenRead(artfile);
                                long len = artstream.Length;
                                artstream.Close();

                                if (true) // ((len > THRESHOLD)||(art.Format != ITArtworkFormat.ITArtworkFormatJPEG)||(art.IsDownloadedArtwork))
                                {
                                    LogConsole.WriteLine("Artwork Size: " + len);
                                    Bitmap im = new Bitmap(artfile);
                                    LogConsole.WriteLine("Dimensions: " + im.Width + "x" + im.Height);

                                    if ((len <= THRESHOLD) && (im.Width <= SIZE) && (im.Height <= SIZE) && (art.Format == ITArtworkFormat.ITArtworkFormatJPEG) && (art.IsDownloadedArtwork))
                                    {
                                        im.Dispose();
                                        large++;
                                        art.SetArtworkFromFile(artfile);

                                    }
                                    else
                                    {

                                        ImageCodecInfo jpgenc = GetEncoder(ImageFormat.Jpeg);
                                        EncoderParameters encparms = new EncoderParameters(1);
                                        encparms.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);

                                        im.Save("temp2.jpg", jpgenc, encparms);
 
                                        artstream = File.OpenRead("temp2.jpg");
                                        len = artstream.Length;
                                        artstream.Close();
                                        LogConsole.WriteLine("Artwork Size: " + len);
                                        if ((len > THRESHOLD)||(im.Width > SIZE)||(im.Height > SIZE))
                                        {
                                            LogConsole.WriteLine("Error: Still Over Threshold, Attempting Resize");

                                            int newwidth = im.Width, newheight = im.Height;
                                            bool resize = false;

                                            if ((im.Width > SIZE) && (im.Width > im.Height))
                                            {
                                                newwidth = SIZE;
                                                newheight = im.Height * SIZE / im.Width;
                                                resize = true;
                                            }
                                            else if (im.Height > SIZE)
                                            {
                                                newheight = SIZE;
                                                newwidth = im.Width * SIZE / im.Height;
                                                resize = true;
                                            }

                                            if (resize)
                                            {
                                                Bitmap b = new Bitmap(newwidth, newheight);
                                                Graphics g = Graphics.FromImage(b);
                                                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                                                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                                g.DrawImage(im, new Rectangle(0, 0, newwidth, newheight));
                                                g.Dispose();
                                                im.Dispose();
                                                im = b;
                                            }

                                            im.Save("temp3.jpg", jpgenc, encparms);
                                            artstream = File.OpenRead("temp3.jpg");
                                            len = artstream.Length;
                                            artstream.Close();
                                            LogConsole.WriteLine("Artwork Size: " + len);
                                            if (len > THRESHOLD)
                                            {
                                                LogConsole.WriteLine("Error: Still Over Threshold");
                                            }
                                            else
                                            {
                                                LogConsole.WriteLine("Adding Artwork: temp3.jpg");
                                                art.SetArtworkFromFile(Environment.CurrentDirectory + "\\temp3.jpg");
                                            }

                                            large++;
                                            File.Delete("temp3.jpg");
                                        }
                                        else if (art.IsDownloadedArtwork)
                                        {
                                            LogConsole.WriteLine("Adding Artwork: temp2.jpg");
                                            art.SetArtworkFromFile(Environment.CurrentDirectory + "\\temp2.jpg");
                                        }
                                        File.Delete("temp2.jpg");

                                        im.Dispose();

                                    }

                                  }

                                LogConsole.WriteLine("Deleting Artwork : " + artfile);
                                File.Delete(artfile);
                                artwork++;

                                Marshal.FinalReleaseComObject(art);
                            }
                            catch (Exception ex)
                            {
                                LogConsole.WriteLine("Problem With File: " + filetrk.Location);
                                LogConsole.WriteLine(ex.Message + " (" + ex.GetType().FullName + ")");
                            }
                        }
  
                        else if (artcol.Count == 0)
                        {
                            LogConsole.WriteLine("Error: No Artwork: " + filetrk.Location);
                            missing++;
                        }
                        else
                        {
                            LogConsole.WriteLine("Error: Multiple Artwork: " + filetrk.Location);
                            errors++;
                        }

                        Marshal.FinalReleaseComObject(artcol);

                    }
                }

                Marshal.FinalReleaseComObject(trk);
            }

            Marshal.FinalReleaseComObject(app);

            LogConsole.WriteLine();
            LogConsole.WriteLine("Summary:");
            LogConsole.WriteLine("Total Tracks Processed:         " + tracks);
            LogConsole.WriteLine("Tracks With Artwork:            " + artwork);
            LogConsole.WriteLine("Tracks Without Artwork:         " + missing);
            LogConsole.WriteLine("Tracks With Multiple Artwork:   " + errors);
            LogConsole.WriteLine("Tracks With Fixed Artwork:      " + large);

            LogConsole.End();
            
        }
    }
}
