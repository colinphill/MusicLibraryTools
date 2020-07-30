/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/ID3.cs $
 * $Date: 2014-09-27 10:37:30 -0600 (Sat, 27 Sep 2014) $
 * $Revision: 20 $
 * $Author: colin $
 * 
 */

using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace MusicFileUtilities
{

    public class ID3v2Util
    {

        public enum ID3Encoding : byte { ISO8859 = 0, MarkedUnicode = 1, BEUnicode = 2, UTF8 = 3 };

        [Serializable]
        public enum APICType : byte
        {
            Other = 0, FileIcon = 1, OtherFileIcon = 2, FrontCover = 3, BackCover = 4, LeafletPage = 5, Media = 6,
            LeadArtist = 7, Arist = 8, Conductor = 9, Band = 10, Composer = 11, Lyricist = 12, RecordingLocation = 13, DuringRecording = 14,
            DuringPerformance = 15, VideoScreenCapture = 16, BrightColoredFish = 17, Illustration = 18, BandLogo = 19, StudioLogo = 20
        };

        public static readonly IList<string> ID3v1Genres = new List<string> {
            "Blues", "Classic Rock", "Country", "Dance", "Disco",
		    "Funk", "Grunge", "Hip-Hop", "Jazz", "Metal",
		    "New Age", "Oldies", "Other", "Pop", "R&B",
		    "Rap", "Reggae", "Rock", "Techno", "Industrial",
		    "Alternative", "Ska", "Death Metal", "Pranks", "Soundtrack",
		    "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk",
            "Fusion", "Trance", "Classical", "Instrumental", "Acid",
		    "House", "Game", "Sound Clip", "Gospel", "Noise",
		    "AlternRock", "Bass", "Soul", "Punk", "Space", 
		    "Meditative", "Instrumental Pop", "Instrumental Rock", "Ethnic", "Gothic", 
		    "Darkwave", "Techno-Industrial", "Electronic", "Pop-Folk", "Eurodance", 
		    "Dream", "Southern Rock", "Comedy", "Cult", "Gangsta",
		    "Top 40", "Christian Rap", "Pop/Funk", "Jungle", "Native American",
		    "Cabaret", "New Wave", "Psychadelic", "Rave", "Showtunes",
		    "Trailer", "Lo-Fi", "Tribal", "Acid Punk", "Acid Jazz",
		    "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock",
		    "Folk", "Folk/Rock", "National Folk", "Swing", "Fast Fusion",
		    "Bebob", "Latin", "Revival", "Celtic", "Bluegrass",
		    "Avantgarde", "Gothic Rock", "Progressive Rock", "Psychedelic Rock", "Symphonic Rock",
		    "Slow Rock", "Big Band", "Chorus", "Easy Listening", "Acoustic", 
		    "Humour", "Speech", "Chanson", "Opera", "Chamber Music", "Sonata", 
		    "Symphony", "Booty Bass", "Primus", "Porn Groove", 
		    "Satire", "Slow Jam", "Club", "Tango", "Samba", 
		    "Folklore", "Ballad", "Power Ballad", "Rhythmic Soul", "Freestyle", 
		    "Duet", "Punk Rock", "Drum Solo", "A Capella", "Euro-House",
		    "Dance Hall" };

        public static Encoding ISO8859Encoding = Encoding.GetEncoding(1252,
            new EncoderExceptionFallback(), new DecoderExceptionFallback()); // iso-8859-1

        public static bool Allowv24Tags = false;

        private static Dictionary<string, string> _mappings = new Dictionary<string,string>();

        static ID3v2Util()
        {
            _mappings.Add("TT1", "TIT1");
            _mappings.Add("TT2", "TIT2");
            _mappings.Add("TT3", "TIT3");
            _mappings.Add("TP1", "TPE1");
            _mappings.Add("TP2", "TPE2");
            _mappings.Add("TP3", "TPE3");
            _mappings.Add("TP4", "TPE4");
            _mappings.Add("TCM", "TCOM");
            _mappings.Add("TXT", "TEXT");
            _mappings.Add("TLA", "TLAN");
            _mappings.Add("TAL", "TALB");
            _mappings.Add("TBP", "TBPM");
            _mappings.Add("TCO", "TCON");
            _mappings.Add("TCR", "TCOP");
            _mappings.Add("TDA", "TDAT");
            _mappings.Add("TEN", "TENC");
            _mappings.Add("TLE", "TLEN");
            _mappings.Add("TRK", "TRCK");
            _mappings.Add("TXX", "TXXX");
            _mappings.Add("TLN", "TLEN");
            _mappings.Add("TPA", "TPOS");
            _mappings.Add("TYE", "TYER");
            _mappings.Add("PIC", "APIC");
            _mappings.Add("COM", "COMM");
        }

        public static string GetNewID3v2Mapping(string frameid)
        {
            if (_mappings.ContainsKey(frameid))
                return _mappings[frameid];
            return "X" + frameid;
        }

    }

    public class ID3v2Frame
    {

        public void Write(FileStream s)
        {
            byte[] header = new byte[10];
            ID3v2Util.ISO8859Encoding.GetBytes(FrameID, 0, 4, header, 0);
            int datalen = Data.Length;
            header[4] = (byte)((datalen >> 24) & 0xff);
            header[5] = (byte)((datalen >> 16) & 0xff);
            header[6] = (byte)((datalen >> 8) & 0xff);
            header[7] = (byte)(datalen & 0xff);
            header[8] = (byte)((Flags >> 8) & 0xff);
            header[9] = (byte)(Flags & 0xff);
            s.Write(header, 0, 10);
            s.Write(Data, 0, datalen);
        }

        public string FrameID = "";
        public int Flags = 0;
        public byte[] Data = new byte[] { };

        public ID3v2Frame(ID3v2Frame from)
        {
            FrameID = from.FrameID;
            Flags = from.Flags;
            Data = from.Data;
        }

        public ID3v2Frame()
        {
        }

        protected string GetStringAt(ID3v2Util.ID3Encoding coding, int offset)
        {
            return GetStringAt(coding, offset, Data.Length - offset);
        }

        protected string GetStringAt(ID3v2Util.ID3Encoding coding, int offset, int length)
        {
            if (coding == ID3v2Util.ID3Encoding.ISO8859)
                return ID3v2Util.ISO8859Encoding.GetString(Data, offset, length);
            if (coding == ID3v2Util.ID3Encoding.MarkedUnicode)
            {
                bool bigendian = ((Data[offset] == 0xfe) && (Data[offset + 1] == 0xff));
                UnicodeEncoding encoding = new UnicodeEncoding(bigendian, false);
                return encoding.GetString(Data, offset + 2, length - 2);
            }

            /*if (ID3v2Util.Allowv24Tags)
                throw new InvalidDataException();*/

            if (coding == ID3v2Util.ID3Encoding.BEUnicode)
                return Encoding.BigEndianUnicode.GetString(Data, offset, length);
            if (coding == ID3v2Util.ID3Encoding.UTF8)
                return Encoding.UTF8.GetString(Data, offset, length);

            throw new InvalidDataException();
        }

        protected string GetNullTerminatedStringAt(ID3v2Util.ID3Encoding coding, int offset)
        {
            int length = 0;

            if ((coding == ID3v2Util.ID3Encoding.BEUnicode) || (coding == ID3v2Util.ID3Encoding.MarkedUnicode))
            {
                for (; length < (Data.Length - offset); length += 2)
                {
                    if ((Data[offset + length] == 0) && (Data[offset + length + 1] == 0))
                        break;
                }
            }
            else
            {
                for (; length < (Data.Length - offset); length ++)
                {
                    if (Data[offset + length] == 0)
                        break;
                }
            }

            return GetStringAt(coding, offset, length);
        }

        protected byte[] CodeString(ID3v2Util.ID3Encoding coding, string value)
        {
            if (coding == ID3v2Util.ID3Encoding.ISO8859)
                return ID3v2Util.ISO8859Encoding.GetBytes(value);
            if (coding == ID3v2Util.ID3Encoding.MarkedUnicode)
            {
                byte[] res = Encoding.Unicode.GetBytes(value);
                byte[] newres = new byte[res.Length + 2];
                newres[0] = 0xff;
                newres[1] = 0xfe;
                Array.Copy(res, 0, newres, 2, res.Length);
                return newres;
            }

            if (ID3v2Util.Allowv24Tags)
                throw new InvalidDataException();
            
            if (coding == ID3v2Util.ID3Encoding.BEUnicode)
                return Encoding.BigEndianUnicode.GetBytes(value);
            if (coding == ID3v2Util.ID3Encoding.UTF8)
                return Encoding.UTF8.GetBytes(value);

            throw new InvalidDataException();
        }

    }

    public class TextFrame : ID3v2Frame
    {
        public TextFrame()
        {
        }

        public TextFrame(ID3v2Frame from)
            : base(from)
        {
        }

        public string Text
        {
            get
            {
                return GetStringAt((ID3v2Util.ID3Encoding)Data[0], 1).Split("\0".ToCharArray())[0];
            }
            set
            {
                byte[] data;
                try
                {
                    data = CodeString(ID3v2Util.ID3Encoding.ISO8859, value);
                }
                catch
                {
                    if (ID3v2Util.Allowv24Tags)
                        data = CodeString(ID3v2Util.ID3Encoding.UTF8, value);
                    else
                        data = CodeString(ID3v2Util.ID3Encoding.MarkedUnicode, value);
                }
            }
        }

    }

    public class UserStringFrame : ID3v2Frame
    {
        public UserStringFrame()
        {
            FrameID = "TXXX";
        }

        public UserStringFrame(ID3v2Frame from)
            : base(from)
        {
            Decode();
        }

        private string _key = "";
        private string _value = "";

        private void Decode()
        {
            try
            {
                _key = GetNullTerminatedStringAt((ID3v2Util.ID3Encoding)Data[0], 1);
                _value = GetStringAt((ID3v2Util.ID3Encoding)Data[0],
                    CodeString((ID3v2Util.ID3Encoding)Data[0], _key + "\0").Length + 1).Split("\0".ToCharArray()).First();
            }
            catch
            {
                _key = "";
                _value = "";
            }
        }

        private void Encode()
        {
            byte [] k, v;
            try
            {
                k = CodeString(ID3v2Util.ID3Encoding.ISO8859, _key);
                v = CodeString(ID3v2Util.ID3Encoding.ISO8859, _value);
                Data[0] = (byte)ID3v2Util.ID3Encoding.ISO8859;
            }
            catch
            {
                if (ID3v2Util.Allowv24Tags)
                {
                    k = CodeString(ID3v2Util.ID3Encoding.UTF8, _key);
                    v = CodeString(ID3v2Util.ID3Encoding.UTF8, _value);
                    Data[0] = (byte)ID3v2Util.ID3Encoding.UTF8;
                }
                else
                {
                    k = CodeString(ID3v2Util.ID3Encoding.MarkedUnicode, _key);
                    v = CodeString(ID3v2Util.ID3Encoding.MarkedUnicode, _value);
                    Data[0] = (byte)ID3v2Util.ID3Encoding.MarkedUnicode;
                }
            }

            Data = new byte[k.Length + v.Length + 1];
            Array.Copy(k, 0, Data, 1, k.Length);
            Array.Copy(v, 0, Data, 1 + k.Length, v.Length);
        }

        public string Key
        {
            get
            {
                return _key;
            }
            set
            {
                _key = value;
                Encode();
            }
        }

        public string Value
        {
            get
            {
                return _value;
            }
            set
            {
                _value = value;
                Encode();
            }
        }

    }

    public class CommentFrame : ID3v2Frame
    {

        public CommentFrame(ID3v2Frame from)
            : base(from)
        {
            Decode();
        }

        public CommentFrame()
        {
            FrameID = "COMM";
        }

        private string _lang = "";
        private string _key = "";
        private string _value = "";

        private void Encode()
        {
        }

        private void Decode()
        {
            try
            {
                _lang = ID3v2Util.ISO8859Encoding.GetString(Data, 1, 3);
                _key = GetNullTerminatedStringAt((ID3v2Util.ID3Encoding)Data[0], 4);
                _value = GetStringAt((ID3v2Util.ID3Encoding)Data[0],
                    CodeString((ID3v2Util.ID3Encoding)Data[0], _key + "\0").Length + 4).Split("\0".ToCharArray()).First();
            }
            catch
            {
                _lang = "";
                _key = "";
                _value = "";
            }
        }

        public string Language
        {
            get
            {
                 return _lang;
            }
            set
            {
                _lang = value;
                Encode();
            }
        }

        public string Key
        {
            get
            {
                return _key;
            }
            set
            {
                _key = value;
                Encode();
            }
        }

        public string Value
        {
            get
            {
                return _value;
            }
            set
            {
                _value = value;
                Encode();
            }
        }
            
    }

    public class PictureFrame : ID3v2Frame
    {
        bool _v22style = false;

        private ID3v2Util.APICType _type;
        private string _mimetype;
        private string _description;
        private byte[] _picdata;
      
        public PictureFrame(ID3v2Frame from, bool v22stle)
            : this(from)
        {
            _v22style = v22stle;
        }

        public PictureFrame(ID3v2Frame from)
            : base(from)
        {
            Decode();
        }

        public PictureFrame()
        {
            FrameID = "APIC";
        }

        private void Decode()
        {
            ID3v2Util.ID3Encoding encoding = (ID3v2Util.ID3Encoding)Data[0];
            _mimetype = GetNullTerminatedStringAt(ID3v2Util.ID3Encoding.ISO8859, 1);
            int codelen = CodeString(ID3v2Util.ID3Encoding.ISO8859, _mimetype + "\0").Length;
            _type = (ID3v2Util.APICType)Data[codelen + 1];
            _description = GetNullTerminatedStringAt(encoding, codelen + 2);
            int codelen2 = CodeString(encoding, _description + "\0").Length;
            _picdata = new byte[Data.Length - codelen - codelen2 - 2];
            Array.Copy(Data, codelen + codelen2 + 2, _picdata, 0, _picdata.Length);
        }

        public void Encode(ID3v2Util.APICType type, string mimetype, string desc, byte[] data)
        {
            throw new NotImplementedException();
        }

        public ID3v2Util.APICType Type
        {
            get
            {
               return _type;
            }
        }

        public string MimeType
        {
            get
            {
                return _mimetype;
            }
        }

        public string Description
        {
            get
            {
                 return _description;
            }
        }

        public byte[] PictureData
        {
            get
            {
                return _picdata;
            }
        }

    }

    public class ID3v2Tag : IMetadataProvider
    {

        private int _headerversion = 0;
        private int _tagsize = 0;
        private List<ID3v2Frame> _frames = new List<ID3v2Frame>();
 
        public List<ID3v2Frame> Frames
        {
            get
            {
                return _frames;
            }
        }

        private void WriteHeader(FileStream s)
        {
            byte[] header = new byte[10];
            header[0] = 0x49;
            header[1] = 0x44;
            header[2] = 0x33;
            header[3] = (byte)_headerversion;
            header[4] = 0x00;
            header[5] = 0x00;
            header[6] = (byte)((_tagsize >> 21) & 0x7f);
            header[7] = (byte)((_tagsize >> 14) & 0x7f);
            header[8] = (byte)((_tagsize >> 7) & 0x7f);
            header[9] = (byte)(_tagsize & 0x7f);
            s.Write(header, 0, 10);
        }

        #region IMetadataProvider Properties
        public string Title
        {
            get
            {
                try
                {
                    return (FindFrame("TIT2") as TextFrame).Text;
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
                    return (FindFrame("TALB") as TextFrame).Text;
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
                    return (FindFrame("TPE1") as TextFrame).Text;
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
                    return (FindFrame("TPE2") as TextFrame).Text;
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
                    return int.Parse((FindFrame("TRCK") as TextFrame).Text.Split("/".ToCharArray())[0]);
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
                    return int.Parse((FindFrame("TCMP") as TextFrame).Text) != 0;
                }
                catch
                {
                    throw new NoMetadataException("Compilation");
                }
            }
        }
 

        #endregion

        /*public ID3v2Frame GetFrame(string id)
         {
             foreach (ID3v2Frame f in _frames)
                 if (f.FrameID == id)
                     return f;
             return null;
         }

         public string GetString(string id)
         {
             foreach (ID3v2Frame f in _frames)
                 if (f.FrameID == id)
                     return f.StringValue;
             return "";
         }

         public string GetUserString(string id)
         {
             foreach (ID3v2Frame f in _frames)
                 if (f.FrameID == "TXXX")
                 {
                     string[] vals = f.StringValue.Split("\0".ToCharArray());
                     if (vals[0] == id)
                         return vals[1];
                 }
             return "";
         }

         public void SetString(string id, string value)
         {
             foreach (ID3v2Frame f in _frames)
                 if (f.FrameID == id)
                 {
                     f.StringValue = value;
                     return;
                 }
             ID3v2Frame frm = new ID3v2Frame();
             frm.FrameID = id;
             frm.StringValue = value;
             _frames.Add(frm);
         }

         public void SetAttachedImage(ID3v2Util.APICType picturetype, string mimetype, byte[] data)
         {
             string newstring = "\0" + mimetype + "\0" + char.ConvertFromUtf32((byte)picturetype) + "\0";
             byte[] encoded = _8bitencoding.GetBytes(newstring);

             foreach (ID3v2Frame f in _frames)
                 if (f.FrameID == "APIC")
                 {
                     if (data[0] != 0)
                         throw new Exception("Can't Handle Unicode Encodings In APIC Frame");
                     string s = _8bitencoding.GetString(data, 0, data.Length);
                     string[] ses = s.Split("\0".ToCharArray(), 2, StringSplitOptions.RemoveEmptyEntries);
                     if (ses[1] == "" + char.ConvertFromUtf32((byte)picturetype))
                     {
                         f.Data = new byte[data.Length + encoded.Length];
                         Array.Copy(encoded, 0, f.Data, 0, encoded.Length);
                         Array.Copy(data, 0, f.Data, encoded.Length, data.Length);
                         return;
                     }
                 }

             ID3v2Frame frm = new ID3v2Frame();
             frm.FrameID = "APIC";
             frm.Data = new byte[data.Length + encoded.Length];
             Array.Copy(encoded, 0, frm.Data, 0, encoded.Length);
             Array.Copy(data, 0, frm.Data, encoded.Length, data.Length);
             _frames.Add(frm);
         }

         public void SetUserString(string id, string value)
         {
             foreach (ID3v2Frame f in _frames)
                 if (f.FrameID == "TXXX")
                 {
                     string[] vals = f.StringValue.Split("\0".ToCharArray());
                     if (vals[0] == id)
                     {
                         f.StringValue = id + "\0" + value;
                         return;
                     }
                 }
             ID3v2Frame frm = new ID3v2Frame();
             frm.FrameID = "TXXX";
             frm.StringValue = id + "\0" + value;
             _frames.Add(frm);
         }*/

        public ID3v2Frame FindFrame(string frame)
        {
            try
            {
                return _frames.Where(frm => frm.FrameID == frame).Single();
            }
            catch
            {
                return null;
            }
        }

        protected void ReadTag(Stream s)
        {
            bool doclose = false;
            BinaryReader r = new BinaryReader(s, Encoding.ASCII, true);
            byte[] header = r.ReadBytes(10);
            if (Encoding.ASCII.GetString(header, 0, 3) == "ID3")
            {
                _tagsize = header[6];
                _tagsize = (_tagsize * 128) + header[7];
                _tagsize = (_tagsize * 128) + header[8];
                _tagsize = (_tagsize * 128) + header[9];
                if ((header[3] == 0x03) || (header[3] == 0x04))
                {
                    if (header[5] != 0)
                    {
                        if (header[5] == 0x80)
                        {
                            // Unsync
                            List<byte> unsync = new List<byte>();
                            byte[] b = r.ReadBytes(_tagsize);
                            for (int i = 0; i < b.Length - 1; i++)
                            {
                                if ((b[i] == 0xff) && (b[i + 1] == 0x00))
                                {
                                    unsync.Add(0xff);
                                    i++;
                                }
                                else
                                    unsync.Add(b[i]);
                            }
                            unsync.Add(b[b.Length - 1]);
                            unsync.Add(0);
                            MemoryStream ms = new MemoryStream(unsync.ToArray());
                            r = new BinaryReader(ms);
                            doclose = true;
                        }
                        else
                            throw new Exception("Unsupported ID3v2 Header Features");
                    }
                    _headerversion = header[3];

                    byte[] frame = r.ReadBytes(10);
                    int offset = 10;
                    while ((frame[0] != 0) && (offset < _tagsize))
                    {
                        ID3v2Frame f = new ID3v2Frame();
                        f.FrameID = ID3v2Util.ISO8859Encoding.GetString(frame, 0, 4);
                        int framesize = frame[4];
                        if (header[3] == 4) // SyncSafe 2.4 only
                        {
                            framesize = (framesize * 128) + frame[5];
                            framesize = (framesize * 128) + frame[6];
                            framesize = (framesize * 128) + frame[7];
                        }
                        else
                        {
                            framesize = (framesize * 256) + frame[5];
                            framesize = (framesize * 256) + frame[6];
                            framesize = (framesize * 256) + frame[7];
                        }
                        f.Flags = (((int)frame[8]) << 8) + (int)frame[9];
                        f.Data = r.ReadBytes(framesize);

                        if (f.FrameID == "TXXX")
                            _frames.Add(new UserStringFrame(f));
                        else if (f.FrameID == "APIC")
                            _frames.Add(new PictureFrame(f));
                        else if (f.FrameID == "COMM")
                            _frames.Add(new CommentFrame(f));
                        else if (f.FrameID[0] == 'T')
                            _frames.Add(new TextFrame(f));
                        else
                            _frames.Add(f);

                        offset += framesize;
                        if (offset < _tagsize)
                        {
                            frame = r.ReadBytes(10);
                            offset += 10;
                        }
                    }
                }
                else
                {
                    if (header[3] != 0x02)
                        throw new Exception("Invalid ID3v2 Version");

                    // Load Legacy V2 Header
                    byte[] frame = r.ReadBytes(6);
                    int offset = 6;
                    while ((frame[0] != 0) && (offset < _tagsize))
                    {
                        ID3v2Frame f = new ID3v2Frame();
                        f.FrameID = ID3v2Util.GetNewID3v2Mapping(ID3v2Util.ISO8859Encoding.GetString(frame, 0, 3));
                        int framesize = frame[3];
                        framesize = (framesize * 256) + frame[4];
                        framesize = (framesize * 256) + frame[5];
                        f.Data = r.ReadBytes(framesize);

                        if (f.FrameID == "TXXX")
                            _frames.Add(new UserStringFrame(f));
                        else if (f.FrameID == "APIC")
                            _frames.Add(new PictureFrame(f, true));
                        else if (f.FrameID == "COMM")
                            _frames.Add(new CommentFrame(f));
                        else if (f.FrameID[0] == 'T')
                            _frames.Add(new TextFrame(f));
                        else
                            _frames.Add(f);

                        offset += framesize;
                        if (offset < _tagsize)
                        {
                            frame = r.ReadBytes(6);
                            offset += 6;
                        }
                    }

                }
            }
            if (doclose)
                r.Close();
        }

        public ID3v2Tag()
        {
        }

        /*protected void Write()
        {
            int size = 0;
            foreach (ID3v2Frame f in _frames)
                size += 10 + f.Data.Length;

            byte[] pad = new byte[(size <= _tagsize) ? (_tagsize - size) : 0];

            if (size <= _tagsize)
            {
                FileStream s = new FileStream(_filename, FileMode.Open, FileAccess.Read | FileAccess.Write);
                s.Seek(0, SeekOrigin.Begin);
                WriteHeader(s);
                foreach (ID3v2Frame f in _frames)
                    f.Write(s);
                s.Write(pad, 0, pad.Length);
                s.Close();
            }
            else
            {
                FileStream source = new FileStream(_filename, FileMode.Open, FileAccess.Read);
                FileStream dest = new FileStream(_filename + ".temp", FileMode.CreateNew, FileAccess.Write);
                source.Seek((_tagsize == 0) ? 0 : (_tagsize + 10), SeekOrigin.Begin);
                _tagsize = size + pad.Length;
                _headerversion = 3;
                WriteHeader(dest);
                foreach (ID3v2Frame f in _frames)
                    f.Write(dest);
                dest.Write(pad, 0, pad.Length);
                byte [] buffer = new byte[10240];
                while (source.Position < source.Length)
                {
                    long amount = source.Length - source.Position;
                    if (amount > 10240)
                        amount = 10240;
                    source.Read(buffer, 0, (int)amount);
                    dest.Write(buffer, 0, (int)amount);
                }
                source.Close();
                dest.Close();
                File.Delete(_filename);
                File.Move(_filename + ".temp", _filename);
            }


        }*/


    }

    public class MP3File : ID3v2Tag, ICodecProvider
    {
        private readonly uint[] _bitrates = { 0, 32000, 40000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 160000, 192000, 224000, 256000, 320000, 0 };
        private readonly uint[] _samplerates = { 44100, 48000, 32000, 0 };
        private readonly uint[] _channels = { 2, 2, 2, 1 };
        private readonly int[] _sideinfolen = { 32, 32, 32, 17 };

        public MP3File(string filename)
        {
            using (FileStream s = File.OpenRead(filename))
            {
                ReadTag(s);
                int b0 = -1, b1 = -1;
                while (s.Position < s.Length)
                {
                    b1 = s.ReadByte();
                    if ((b0 == 0xff) && ((b1 & 0xfa) == 0xfa))
                        break;
                    b0 = b1;
                }
                if (s.Position >= s.Length)
                    return;
                long datalength = s.Length - s.Position + 2;
                int b2 = s.ReadByte();
                int b3 = s.ReadByte();
                uint bitrate = _bitrates[b2 >> 4];
                AverageBitrate = bitrate;
                Samplerate = _samplerates[(b2 >> 2) & 3];
                Channels = _channels[(b3 >> 6) & 3];
                int sideinfolen = _sideinfolen[(b3 >> 6) & 3];
                int framesize = ((1152 / 8 * (int)bitrate) / (int)Samplerate);
                if ((b2 & 8) == 8)
                    framesize++;

                byte[] frame = new byte[framesize - 4];
                s.Read(frame, 0, frame.Length);

                int offset = sideinfolen;
                string id = Encoding.ASCII.GetString(frame, offset, 4);
                if ((id == "Xing" || (id == "Info")))
                {
                    uint frames = 0;
                    uint bytes = 0;
                    offset += 4;
                    uint flags = Tools.UInt32AtBE(frame, offset);
                    offset += 4;
                    if ((flags & 1) == 1)
                    {
                        frames = Tools.UInt32AtBE(frame, offset);
                        offset += 4;
                        AverageBitrate = (uint)(datalength / (frames * 1152 / Samplerate) * 8);
                    }
                    if ((bytes & 2) == 2)
                    {
                        bytes = Tools.UInt32AtBE(frame, offset);
                        offset += 4;
                    }
                    Duration = 26 * frames / 1000;
                }
                else if (Encoding.ASCII.GetString(frame, 32, 4) == "VBRI")
                {
                    offset += 10;
                    uint frames = Tools.UInt32AtBE(frame, offset);
                    AverageBitrate = (uint)(datalength / (frames * 1152 / Samplerate) * 8);
                    Duration = 26 * frames / 1000;
                }
                else
                {
                    // CBR
                    Duration = (uint)(datalength / framesize * 26 / 1000);
                }
            }

        }

        public string CodecName => "MP3";

        public CodecType CodecType => CodecType.Lossy;

        public uint AverageBitrate
        {
            get;
            protected set;
        }

        public uint Duration
        {
            get;
            protected set;
        }

        public uint MaxBitrate => AverageBitrate;

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

    }

    public class DSFFile : ID3v2Tag, ICodecProvider
    {
        public DSFFile(string filename)
        {
            using (FileStream s = File.OpenRead(filename))
            {
                byte[] header = new byte[4];
                s.Read(header, 0, 4);
                if (Encoding.ASCII.GetString(header, 0, 4) != "DSD ")
                    return;
                Array.Resize(ref header, 28);
                s.Read(header, 4, 24);
                long tagoffset = BitConverter.ToInt64(header, 20);
                if (tagoffset != 0)
                {
                    s.Seek(tagoffset, SeekOrigin.Begin);
                    ReadTag(s);
                    s.Seek(28, SeekOrigin.Begin);
                }
                s.Read(header, 0, 4);
                if (Encoding.ASCII.GetString(header, 0, 4) != "fmt ")
                    return;
                using (BinaryReader r = new BinaryReader(s, Encoding.ASCII, true))
                {
                    ulong chunksize = r.ReadUInt64();
                    uint formatversion = r.ReadUInt32();
                    uint formatid = r.ReadUInt32();
                    uint channeltype = r.ReadUInt32();
                    Channels = r.ReadUInt32();
                    Samplerate = r.ReadUInt32();
                    BitsPerSample = r.ReadUInt32();
                    Duration = (uint)(r.ReadUInt64() / Samplerate);
                }
            }

        }

        public uint Duration
        {
            get;
            protected set;
        }

        public string CodecName => "DSD";

        public CodecType CodecType => CodecType.Lossless;

        public uint AverageBitrate
        {
            get
            {
                return BitsPerSample * Samplerate * Channels;
            }
        }

        public uint MaxBitrate => AverageBitrate;

        public uint BitsPerSample
        {
            protected set;
            get;
        }

        public uint Samplerate
        {
            protected set;
            get;
        }

        public uint Channels
        {
            protected set;
            get;
        }

    }

}