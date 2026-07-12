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

            if (args.Length is < 1 or > 2 ||
                (args.Length == 2 && !args[1].Equals("--apply", StringComparison.OrdinalIgnoreCase)))
            {
                LogConsole.WriteLine("Usage: FixArtwork <playlist> [--apply]");
                LogConsole.End();
                return;
            }

            bool apply = args.Length == 2;
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
                LogConsole.WriteLine("Error: Playlist not found: " + args[0]);
                Marshal.FinalReleaseComObject(app);
                LogConsole.End();
                return;
            }

            if (!apply)
                LogConsole.WriteLine("Dry run: pass --apply to update iTunes artwork.");

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
                            string prefix = Path.Combine(Path.GetTempPath(), "mlt-fixart-" + Guid.NewGuid().ToString("N"));
                            string artfile = null;
                            string temp2 = prefix + "-2.jpg";
                            string temp3 = prefix + "-3.jpg";
                            IITArtwork art = null;
                            Bitmap im = null;
                            EncoderParameters encparms = null;
                            try
                            {
                                art = artcol[1];
                                string ext = (art.Format == ITArtworkFormat.ITArtworkFormatBMP) ? "bmp" :
                                    (art.Format == ITArtworkFormat.ITArtworkFormatJPEG) ? "jpg" :
                                    (art.Format == ITArtworkFormat.ITArtworkFormatPNG) ? "png" : "unknown";

                                LogConsole.WriteLine(tracks.ToString() + " Artwork (" + ext + "): " + filetrk.Location);
                                artfile = prefix + "." + ext;
                                LogConsole.WriteLine("Saving Artwork: " + artfile);
                                art.SaveArtworkToFile(artfile);

                                FileStream artstream = File.OpenRead(artfile);
                                long len = artstream.Length;
                                long origlen = len;
                                artstream.Close();

                                if (true) // ((len > THRESHOLD)||(art.Format != ITArtworkFormat.ITArtworkFormatJPEG)||(art.IsDownloadedArtwork))
                                {
                                    LogConsole.WriteLine("Artwork Size: " + len);
                                    im = new Bitmap(artfile);
                                    LogConsole.WriteLine("Dimensions: " + im.Width + "x" + im.Height);

                                    if ((len <= THRESHOLD) && (im.Width <= SIZE) && (im.Height <= SIZE) && (art.Format == ITArtworkFormat.ITArtworkFormatJPEG) && (art.IsDownloadedArtwork))
                                    {
                                        LogConsole.WriteLine("Downloaded Artwork");
                                        im.Dispose();
                                        large++;
                                        if (apply)
                                            art.SetArtworkFromFile(artfile);
                                    }
                                    else
                                    {

                                        ImageCodecInfo jpgenc = GetEncoder(ImageFormat.Jpeg);
                                        encparms = new EncoderParameters(1);
                                        encparms.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);

                                        im.Save(temp2, jpgenc, encparms);
 
                                        artstream = File.OpenRead(temp2);
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
                                                using Graphics g = Graphics.FromImage(b);
                                                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                                                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                                g.DrawImage(im, new Rectangle(0, 0, newwidth, newheight));
                                                im.Dispose();
                                                im = b;
                                            }

                                            im.Save(temp3, jpgenc, encparms);
                                            artstream = File.OpenRead(temp3);
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
                                                if (apply)
                                                    art.SetArtworkFromFile(temp3);
                                            }

                                            large++;
                                            File.Delete(temp3);
                                        }
                                        else if (art.IsDownloadedArtwork || (art.Format != ITArtworkFormat.ITArtworkFormatJPEG) || (origlen > THRESHOLD))
                                        {
                                            LogConsole.WriteLine("Adding Artwork: temp2.jpg");
                                            if (apply)
                                                art.SetArtworkFromFile(temp2);
                                            large++;
                                        }
                                        File.Delete(temp2);

                                        im.Dispose();

                                    }

                                  }

                                LogConsole.WriteLine("Deleting Artwork : " + artfile);
                                File.Delete(artfile);
                                artwork++;

                            }
                            catch (Exception ex)
                            {
                                errors++;
                                LogConsole.WriteLine("Problem With File: " + filetrk.Location);
                                LogConsole.WriteLine(ex.Message + " (" + ex.GetType().FullName + ")");
                            }
                            finally
                            {
                                im?.Dispose();
                                encparms?.Dispose();
                                if (art != null)
                                    Marshal.FinalReleaseComObject(art);
                                foreach (string temp in new[] { artfile, temp2, temp3 })
                                {
                                    try
                                    {
                                        if (!string.IsNullOrEmpty(temp) && File.Exists(temp))
                                            File.Delete(temp);
                                    }
                                    catch (Exception ex)
                                    {
                                        LogConsole.WriteLine($"Unable to delete temporary file {temp}: {ex.Message}");
                                    }
                                }
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

            Marshal.FinalReleaseComObject(lib);
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
