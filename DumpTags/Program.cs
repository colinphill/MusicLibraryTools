/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/DumpTags/Program.cs $
 * $Date: 2014-09-26 05:47:24 -0600 (Fri, 26 Sep 2014) $
 * $Revision: 18 $
 * $Author: colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

using MusicFileUtilities;
using ConsoleTools;

namespace DumpTags
{
    class Program
    {

        static void DecodeiTunNORM(string value)
        {
            string[] svals = value.Split(" \t\0\n\r".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            uint[] vals = new uint[svals.Length];

            for(int i=0;i<svals.Length;i++)
                vals[i] = uint.Parse(svals[i], System.Globalization.NumberStyles.HexNumber);

            double lgain = Math.Log10(vals[0] / 1000.0) / -0.1;
            double rgain = Math.Log10(vals[1] / 1000.0) / -0.1;

            LogConsole.Write("SoundCheck Gain: " + Math.Round((lgain + rgain) / 2.0, 1).ToString() + " dB");
        }

        static void DumpID3Tags(string filename)
        {
            ID3v2Tag tag = filename.ToLower().EndsWith(".mp3") ? (ID3v2Tag)new MP3File(filename) : (ID3v2Tag)new DSFFile(filename);
            foreach (ID3v2Frame frame in tag.Frames)
            {
                LogConsole.Write("Frame: " + frame.FrameID + " - ");

                if (frame is UserStringFrame)
                {
                    UserStringFrame usf = frame as UserStringFrame;
                    LogConsole.WriteLine("User String: " + usf.Key + " - " + usf.Value);
                }
                else if (frame is CommentFrame)
                {
                    CommentFrame cf = frame as CommentFrame;
                    LogConsole.WriteLine("Comment (" + cf.Language + "): " + cf.Key + " - " + cf.Value);
                    if (cf.Key == "iTunNORM")
                    {
                        DecodeiTunNORM(cf.Value);
                        LogConsole.WriteLine();
                    }
                }
                else if (frame is PictureFrame)
                {
                    PictureFrame pf = frame as PictureFrame;
                    LogConsole.WriteLine("Picture:");
                    LogConsole.WriteLine("\tType: " + pf.Type.ToString());
                    LogConsole.WriteLine("\tMime-Type: " + pf.MimeType);
                    LogConsole.WriteLine("\tDescription: " + pf.Description);

                    FileStream s = new FileStream("artwork.bin", FileMode.Create, FileAccess.Write);
                    s.Write(pf.PictureData, 0, pf.PictureData.Length);
                    s.Close();
                }
                else if (frame is TextFrame)
                {
                    TextFrame tf = frame as TextFrame;
                    LogConsole.WriteLine(tf.Text);
                }
                else
                    LogConsole.WriteLine("Non Text Frame");
            }
        }

        static void DumpQTTags(string filename)
        {
            RootAtom root = new RootAtom(filename);
            Atom_ilst ilst = root.FindPath("moov.udta.meta.ilst") as Atom_ilst;

            foreach (Atom a in ilst.Children)
            {
                foreach (Atom sa in (a as ContainerAtom).Children)
                {
                    LogConsole.Write("Atom: " + a.Type + " ");
                    Atom_data da = sa as Atom_data;
                    if (da != null)
                    {
                        if (da.IsText)
                        {
                            LogConsole.Write("Text: " + da.Text);
                            if ((a.Type == "----"))
                            {
                                if ((((a as ContainerAtom).FindPath("name") as StringAtom).Text) == "iTunNORM")
                                {
                                    LogConsole.WriteLine();
                                    DecodeiTunNORM(da.Text);
                                }
                            }
                        }
                        else if (da.IsEnumeratedGenre)
                        {
                            LogConsole.Write("Genre(s): ");
                            foreach (string s in da.EnumeratedGenres)
                                LogConsole.Write(s + " ");
                        }
                        else if (da.IsTrackNumber)
                            LogConsole.Write("Track: " + da.TrackNumber + "/" + da.TotalTracks);
                        else if (da.IsDiscNumber)
                            LogConsole.Write("Disc: " + da.DiscNumber + "/" + da.TotalDiscs);
                        else if (da.IsBoolean)
                            LogConsole.Write("Boolean: " + da.BoolValue.ToString());
                        else if (da.IsRating)
                            LogConsole.Write("Rating: " + da.Rating.ToString());
                        else if (da.IsImage)
                        {
                            LogConsole.Write("Image (" + da.DataType.ToString() + ")");
                        }
                        else if (da.DataType == Atom_data.DataTypes.Integer)
                        {
                            LogConsole.Write("Integer: " + da.Uint64);
                        }
                    }
                    StringAtom stra = sa as StringAtom;
                    if (stra != null)
                        LogConsole.Write("String (" + stra.Type + "): " + stra.Text);

                    LogConsole.WriteLine();
                }
            }
        }

        static void DumpVorbisComments(VorbisComments vc)
        {
            LogConsole.WriteLine("Vendor: " + vc.Vendor);

            foreach (KeyValuePair<string, string> kv in vc.Comments)
                LogConsole.WriteLine(kv.Key + "=" + kv.Value);
            
            LogConsole.WriteLine();
            
            foreach (VorbisArtwork art in vc.Artworks)
            {
                LogConsole.WriteLine("Artwork:");
                LogConsole.WriteLine("\tFormat: " + art.MimeType);
                LogConsole.WriteLine("\tType: " + art.PictureType.ToString());
                LogConsole.WriteLine();
            }
        }

        static void DumpASFTags(string filename)
        {
            ASFFile f = new ASFFile(filename);

            foreach (KeyValuePair<string, string> kv in f.TextFields)
                LogConsole.WriteLine(kv.Key + "=" + kv.Value);

            foreach (WMPicture p in f.Pictures)
            {
                LogConsole.WriteLine("Picture:");
                LogConsole.WriteLine("\tType: " + p.Type.ToString());
                LogConsole.WriteLine("\tMime-Type: " + p.MimeType);
                LogConsole.WriteLine("\tDescription: " + p.Description);
            }
        }

        static void DumpFLACTags(string filename)
        {
            FLACFile f = new FLACFile(filename);
            DumpVorbisComments(f);
        }

        static void DumpOggVorbisTags(string filename)
        {
            OggVorbisFile f = new OggVorbisFile(filename);
            DumpVorbisComments(f);
        }

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("DumpTags.log");

            if (args.Length < 1)
            {
                LogConsole.WriteLine("Usage: DumpTags <filename> [filename]");
                return;
            }

            foreach (string arg in args)
            {
                LogConsole.WriteLine("File: " + arg);
                switch (Path.GetExtension(arg).ToLower())
                {
                    case ".m4a":
                        DumpQTTags(arg);
                        break;

                    case ".flac":
                        DumpFLACTags(arg);
                        break;

                    case ".ogg":
                        DumpOggVorbisTags(arg);
                        break;

                    case ".mp3":
                    case ".dsf":
                        DumpID3Tags(arg);
                        break;

                    case ".wma":
                        DumpASFTags(arg);
                        break;

                    default:
                        LogConsole.WriteLine("Unrecognized File Extension");
                        break;
                }
                LogConsole.WriteLine();
            }

            LogConsole.Close();
           
        }
    }
}
