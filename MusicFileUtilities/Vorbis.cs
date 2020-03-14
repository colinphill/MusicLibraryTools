/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/Vorbis.cs $
 * $Date: 2014-10-18 06:43:07 -0600 (Sat, 18 Oct 2014) $
 * $Revision: 23 $
 * $Author: colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace MusicFileUtilities
{

    public class VorbisTools
    {


    }

    [Serializable]
    public class VorbisArtwork
    {
        public ID3v2Util.APICType PictureType;
        public string MimeType;
        public string Description;
        public int Width;
        public int Height;
        public int Depth;
        public int ColorsUsed;
        public byte[] Data;

        public byte[] ToByteArray()
        {
            List<byte> l = new List<byte>();
            l.AddRange(Tools.ToBE((int)PictureType));

            byte [] b = Encoding.UTF8.GetBytes(MimeType);
            l.AddRange(Tools.ToBE((int)b.Length));
            l.AddRange(b);

            b = Encoding.UTF8.GetBytes(Description);
            l.AddRange(Tools.ToBE((int)b.Length));
            l.AddRange(b);

            l.AddRange(Tools.ToBE(Width));
            l.AddRange(Tools.ToBE(Height));
            l.AddRange(Tools.ToBE(Depth));
            l.AddRange(Tools.ToBE(ColorsUsed));

            l.AddRange(Tools.ToBE(Data.Length));

            l.AddRange(Data);

            return l.ToArray();
        }

        public VorbisArtwork()
        {
        }

        public void FromByteArray(byte[] b)
        {
            int offset = 0;
            
            PictureType = (ID3v2Util.APICType)Tools.Int32AtBE(b, offset);
            offset += 4;

            int mimelen = Tools.Int32AtBE(b, offset);
            offset += 4;
            
            MimeType = Encoding.UTF8.GetString(b, offset, mimelen);
            offset += mimelen;

            int desclen = Tools.Int32AtBE(b, offset);
            offset += 4;

            Description = Encoding.UTF8.GetString(b, offset, desclen);
            offset += desclen;

            Width = Tools.Int32AtBE(b, offset);
            offset += 4;

            Height = Tools.Int32AtBE(b, offset);
            offset += 4;

            Depth = Tools.Int32AtBE(b, offset);
            offset += 4;

            ColorsUsed = Tools.Int32AtBE(b, offset);
            offset += 4;

            int datasize = Tools.Int32AtBE(b, offset);
            offset += 4;

            Data = new byte[datasize];
            Array.Copy(b, offset, Data, 0, Data.Length);
        }

        public VorbisArtwork(byte[] b)
        {
            FromByteArray(b);
        }

    }

    [Serializable]
    public class VorbisComments : IMetadataProvider
    {

        #region IMetadataProvider Properties
        public string Title
        {
            get
            {
                try
                {
                    return this["TITLE"];
                }
                catch
                {
                    throw new NoMetadataException("Title");
                }
            }
        }

        public string Album
        {
            get
            {
                try
                {
                    return this["ALBUM"];
                }
                catch
                {
                    throw new NoMetadataException("Album");
                }
            }
        }

        public string Artist
        {
            get
            {
                try
                {
                    return this["ARTIST"];
                }
                catch
                {
                    throw new NoMetadataException("Artist");
                }
            }
        }

        public string AlbumArtist
        {
            get
            {
                try
                {
                    return this["ALBUMARTIST"];
                }
                catch
                {
                    throw new NoMetadataException("AlbumArtist");
                }
            }
        }

        public int TrackNumber
        {
            get
            {
                try
                {
                    return int.Parse(this["TRACKNUMBER"].Split('/')[0]);
                }
                catch
                {
                    throw new NoMetadataException("TrackNumber");
                }
            }
        }

        public bool Compilation
        {
            get
            {
                try
                {
                    return int.Parse(this["COMPILATION"]) != 0;
                }
                catch
                {
                    throw new NoMetadataException("Compilation");
                }
            }
        }


        #endregion

        public string Vendor;
        public List<KeyValuePair<string, string>> Comments = new List<KeyValuePair<string, string>>();
        public List<VorbisArtwork> Artworks = new List<VorbisArtwork>();

        public string this[string key]
        {
            get
            {
                foreach (KeyValuePair<string, string> kv in Comments)
                {
                    if (kv.Key == key)
                        return kv.Value;
                }
                throw new KeyNotFoundException();
            }
            set
            {
                foreach (KeyValuePair<string, string> kv in Comments)
                {
                    if (kv.Key == key)
                    {
                        Comments.Remove(kv);
                        break;
                    }
                }
                Comments.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        public VorbisComments()
        {
        }

        public byte[] ToByteArray(bool includeart)
        {
            List<byte> l = new List<byte>();

            byte[] b = Encoding.UTF8.GetBytes(Vendor);
            l.AddRange(Tools.ToLE((int)b.Length));
            l.AddRange(b);

            int commentcount = Comments.Count + (includeart ? Artworks.Count : 0);
            l.AddRange(Tools.ToLE(commentcount));

            foreach (KeyValuePair<string,string> comment in Comments)
            {
                string combine = comment.Key + "=" + comment.Value;
                b = Encoding.UTF8.GetBytes(combine);
                l.AddRange(Tools.ToLE((int)b.Length));
                l.AddRange(b);
            }

            if (includeart)
            {
                foreach (VorbisArtwork art in Artworks)
                {
                    string combine = "METADATA_PICTURE_BLOCK=" + Convert.ToBase64String(art.ToByteArray(), Base64FormattingOptions.InsertLineBreaks);
                    b = Encoding.UTF8.GetBytes(combine);
                    l.AddRange(Tools.ToLE((int)b.Length));
                    l.AddRange(b);
                }
            }

            return l.ToArray();
        }

        public void FromByteArray(byte[] b)
        {
            int offset = 0;
            
            int vendorlength = Tools.Int32AtLE(b, offset);
            offset += 4;

            Vendor = Encoding.UTF8.GetString(b, offset, vendorlength);

            offset += vendorlength;
            int commentlistlength = Tools.Int32AtLE(b, offset);

            offset += 4;

            for (int i = 0; i < commentlistlength; i++)
            {
                int commentlength = Tools.Int32AtLE(b, offset);
                offset += 4;

                string comment = Encoding.UTF8.GetString(b, offset, commentlength);

                string[] split = comment.Split(new char[] { '=' }, 2);
                split[0] = split[0].ToUpper();

                if (split[0] == "METADATA_BLOCK_PICTURE")
                {
                    VorbisArtwork art = new VorbisArtwork(Convert.FromBase64String(split[1]));
                    Artworks.Add(art);
                }
                else
                    Comments.Add(new KeyValuePair<string, string>(split[0], split[1]));

                offset += commentlength;
            }
        }

        public VorbisComments(byte [] b)
        {
            FromByteArray(b);
        }



    }

    [Serializable]
    public class OggVorbisFile : VorbisComments, ICodecProvider
    {
        public string Filename
        {
            get;
            set;
        }

        public string CodecName => "Vorbis";

        public CodecType CodecType => CodecType.Lossy;

        public uint AverageBitrate
        {
            get;
            protected set;
        }

        public uint MaxBitrate
        {
            get;
            protected set;
        }

        public uint BitsPerSample => 16;

        public uint Samplerate
        {
            get;
            protected set;
        }

        public uint Channels
        {
            get;
            protected set;
        }

        private bool ReadPageHeader(Stream s, out int datalen, out bool continuation, out bool firstpage)
        {
            byte[] b = new byte[27];
            s.Read(b, 0, 27);
            if (Encoding.ASCII.GetString(b, 0, 4) != "OggS")
                throw new InvalidDataException();
            byte[] segs = new byte[b[26]];
            s.Read(segs, 0, b[26]);
            int sum = 0;
            foreach (byte seg in segs)
                sum += seg;
            datalen = sum;
            continuation = ((b[5] & 1) == 1);
            firstpage = ((b[5] & 2) == 2);
            return ((b[5] & 4) == 4);
        }

        private void ParsePage(byte[] page)
        {
            if (page.Length < 7)
                return;
            if (Encoding.ASCII.GetString(page, 1, 6) != "vorbis")
                return;
            if (page[0] == 3) // Comment Block
            {
                byte[] stripped = new byte[page.Length - 7];
                Array.Copy(page, 7, stripped, 0, page.Length - 7);
                FromByteArray(stripped);
            }
            if (page[0] == 1) // Information Header
            {
                byte[] stripped = new byte[page.Length - 7];
                Array.Copy(page, 7, stripped, 0, page.Length - 7);
                Channels = stripped[4];
                Samplerate = Tools.UInt32AtLE(stripped, 5);
                MaxBitrate = Tools.UInt32AtLE(stripped, 9);
                AverageBitrate = Tools.UInt32AtLE(stripped, 13);
                if (MaxBitrate == 0)
                    MaxBitrate = AverageBitrate;
            }
        }

        public OggVorbisFile(string filename)
        {
            Filename = filename;

            bool lastpage;
            byte[] pagedata = new byte[] { };

            FileStream s = new FileStream(filename, FileMode.Open, FileAccess.Read);
            do
            {
                int datalen;
                bool continuation, firstpage;

                lastpage = ReadPageHeader(s, out datalen, out continuation, out firstpage);

                if (continuation)
                    Array.Resize(ref pagedata, pagedata.Length + datalen);
                else
                {
                    if (!firstpage)
                        ParsePage(pagedata);
                    pagedata = new byte[datalen];
                }

                s.Read(pagedata, pagedata.Length - datalen, datalen);
            } 
            while (!lastpage);

            ParsePage(pagedata);
            
            s.Close();
        }



    }


}