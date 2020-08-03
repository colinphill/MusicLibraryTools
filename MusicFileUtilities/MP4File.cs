/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/MP4File.cs $
 * $Date: 2014-09-27 10:37:30 -0600 (Sat, 27 Sep 2014) $
 * $Revision: 20 $
 * $Author: colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.ComponentModel;

namespace MusicFileUtilities
{

    public static class MP4Util
    {

        public const int DEMAND_BLOCK_SIZE = 0x10000;
        
        public static Dictionary<string, Type> AtomTypes = new Dictionary<string, Type>();

        public static Encoding TypeEncoding = Encoding.GetEncoding(1252,
           new EncoderExceptionFallback(), new DecoderExceptionFallback()); // iso-8859-1

        public static Encoding ShiftJISEncoding = Encoding.GetEncoding(932,
            new EncoderExceptionFallback(), new DecoderExceptionFallback());

        static MP4Util()
        {
            Init();
        }

        public delegate IEnumerable<KeyValuePair<string, string>> HandleAtom(ContainerAtom atom);

        private static IEnumerable<KeyValuePair<string, string>> HandleNullAtom(ContainerAtom atom)
        {
            yield break;
        }

        private static IEnumerable<KeyValuePair<string, string>> HandleTrackDiscAtom(ContainerAtom atom)
        {
            Atom_data da = atom.FindPath("data") as Atom_data;
            if (da.IsTrackNumber)
            {
                yield return new KeyValuePair<string, string>("TRACKNUMBER", da.TrackNumber.ToString());
                if (da.TotalTracks != 0)
                    yield return new KeyValuePair<string, string>("TRACKTOTAL", da.TotalTracks.ToString());
            }
            if (da.IsDiscNumber)
            {
                yield return new KeyValuePair<string, string>("DISCNUMBER", da.DiscNumber.ToString());
                if (da.TotalTracks != 0)
                    yield return new KeyValuePair<string, string>("DISCTOTAL", da.TotalDiscs.ToString());
            }
        }

        public static Dictionary<string, HandleAtom> SpecialMapping = new Dictionary<string, HandleAtom>()
        {
            { "trkn",  new HandleAtom(HandleTrackDiscAtom) },
            { "disk",  new HandleAtom(HandleTrackDiscAtom) },
        };

        public static Dictionary<string, string> VorbisCommentMapping = new Dictionary<string, string>()
        {
            {"©alb", "ALBUM"},
            {"soal", "ALBUMSORT"},
            {"aART", "ALBUMARTIST"},
            {"soaa", "ALBUMARTISTSORT"},
            {"©ART", "ARTIST"},
            {"soar", "ARTISTSORT"},
            {"tmpo", "BPM"},
            {"©cmt", "COMMENT"},
            {"cpil", "COMPILATION"},
            {"©wrt", "COMPOSER"},
            {"soco", "COMPOSERSORT"},
            {"©con", "CONDUCTOR"},
            {"©grp", "CONTENTGROUP"},
            {"cprt", "COPYRIGHT"},
            {"desc", "DESCRIPTION"},
            {"©gen", "GENRE"},
            {"gnre", "GENRE"},
            {"©mvn", "MOVEMENTNAME"},
            {"©mvi", "MOVEMENT"},
            {"©mvc", "MOVEMENTTOTAL"},
            {"pcst", "PODCAST"},
            {"catg", "PODCASTCATEGORY"},
            {"ldes", "PODCASTDESC"},
            {"egid", "PODCASTID"},
            {"keyw", "PODCASTKEYWORDS"},
            {"purl", "PODCASTURL"},
            {"©nam", "TITLE"},
            {"sonm", "TITLESORT"},
            {"©lyr", "UNSYNCEDLYRICS"},
            {"©day", "YEAR"},
        };

        public static void Init()
        {
            AtomTypes.Add("ftyp", typeof(Atom_ftyp));
            AtomTypes.Add("moov", typeof(ContainerAtom));
            AtomTypes.Add("mvhd", typeof(Atom_mvhd));
            AtomTypes.Add("trak", typeof(ContainerAtom));

            AtomTypes.Add("edts", typeof(ContainerAtom));
            AtomTypes.Add("mdia", typeof(ContainerAtom));
            AtomTypes.Add("minf", typeof(ContainerAtom));
            AtomTypes.Add("dinf", typeof(ContainerAtom));
            AtomTypes.Add("stbl", typeof(ContainerAtom));
            AtomTypes.Add("mvex", typeof(ContainerAtom));
            AtomTypes.Add("moof", typeof(ContainerAtom));
            AtomTypes.Add("traf", typeof(ContainerAtom));
            AtomTypes.Add("mfra", typeof(ContainerAtom));
            AtomTypes.Add("skip", typeof(ContainerAtom));

            AtomTypes.Add("meta", typeof(FullContainerAtom));

            AtomTypes.Add("ipro", typeof(ContainerAtom));
            AtomTypes.Add("sinf", typeof(ContainerAtom));
            AtomTypes.Add("fiin", typeof(ContainerAtom));
            AtomTypes.Add("paen", typeof(ContainerAtom));

            AtomTypes.Add("meco", typeof(ContainerAtom));
            AtomTypes.Add("udta", typeof(ContainerAtom));

            AtomTypes.Add("hdlr", typeof(Atom_hdlr));

            AtomTypes.Add("free", typeof(Atom_free));

            AtomTypes.Add("co64", typeof(Atom_co64));
            AtomTypes.Add("stco", typeof(Atom_stco));

            AtomTypes.Add("data", typeof(Atom_data));
            AtomTypes.Add("stsd", typeof(Atom_stsd));

            AtomTypes.Add("ilst", typeof(Atom_ilst));

            AtomTypes.Add("mean", typeof(StringAtom));
            AtomTypes.Add("name", typeof(StringAtom));

            AtomTypes.Add("mdat", typeof(DemandAtom));

            AtomTypes.Add("stsd.mp4a", typeof(CodecAtom));
            AtomTypes.Add("stsd.alac", typeof(CodecAtom));

            AtomTypes.Add("alac.alac", typeof(Atom_alac));
            AtomTypes.Add("mp4a.esds", typeof(Atom_mp4a_esds));
        }

        public static bool LoadData
        {
            get
            {
                return true;
            }
        }
    }


    public class Atom
    {
        protected ContainerAtom _parent = null;
        protected byte[] _type = new byte[4];
        protected ulong _size = 8;
        protected ulong _headersize = 8;
        protected bool _touched = false;
        protected long _deltasize = 0;

        protected Atom()
        {
        }

        public long Size
        {
            get
            {
                return (long)_size;
            }
        }

        public string Type
        {
            get
            {
                return MP4Util.TypeEncoding.GetString(_type);
            }
            set
            {
                _type = MP4Util.TypeEncoding.GetBytes(value);
            }
        }

        public bool Removed
        {
            get
            {
                return (_size == 0);
            }
        }

        public Atom(Atom a)
        {
            _parent = a._parent;
            _type = a._type;
            _size = a._size;
            _headersize = a._headersize;
        }

        protected Atom(ContainerAtom ca)
        {
            _parent = ca;
            _touched = true;
            _deltasize = 8;
        }

        public Atom(Stream s)
        {
            long offs = s.Position;
            ulong size = ReadUint32(s);
            s.Read(_type, 0, 4);
            if (size == 1)
            {
                size = ReadUint64(s);
                _headersize += 8;
            }
            else if (size == 0)
                size = (ulong)(s.Length - offs);
            _size = size;
        }

        public Atom(Stream s, ContainerAtom parent)
            : this(s)
        {
            _parent = parent;
        }

        protected uint ReadUint16(Stream s)
        {
            byte[] b = new byte[2];
            s.Read(b, 0, 2);
            return (((uint)b[0]) << 8) | (uint)b[1];
        }

        protected uint ReadUint32(Stream s)
        {
            uint u = ReadUint16(s);
            return (((uint)u) << 16) | (uint)ReadUint16(s);
        }

        protected ulong ReadUint64(Stream s)
        {
            uint u = ReadUint32(s);
            return (((ulong)u) << 32) | (ulong)ReadUint32(s);
        }

        protected void WriteUint16(Stream s, uint u)
        {
            byte[] b = new byte[2];
            b[0] = (byte)((u >> 8) & 0xff);
            b[1] = (byte)(u & 0xff);
            s.Write(b, 0, 2);
        }
        
        protected void WriteUint32(Stream s, uint u)
        {
            WriteUint16(s, (uint)(u >> 16));
            WriteUint16(s, (uint)(u & 0xffffu));
        }

        protected void WriteUint64(Stream s, ulong u)
        {
            WriteUint32(s, (uint)(u >> 32));
            WriteUint32(s, (uint)(u & 0xfffffffful));
        }

        public override string ToString()
        {
            return Type;
        }

        protected void WriteHeader(Stream s)
        {
            if (_size > 0xffffffff)
            {
                WriteUint32(s, 1);
                s.Write(_type, 0, 4);
                WriteUint64(s, _size);
            }
            else
            {
                WriteUint32(s, (uint)_size);
                s.Write(_type, 0, 4);
            }
        }

        public virtual void WriteAtom(Stream s)
        {
            throw new NotImplementedException();
        }

        public void Touch(long delta_size)
        {
            Touch(delta_size, true);
        }

        public virtual void NonRecursiveTouch(long delta_size)
        {
            _deltasize += delta_size;
            _size = (ulong)((long)_size + delta_size);
            _touched = true;
            // TODO: Handle Changes between 64/32 bit sizes (this will never happen though)
        }

        public virtual void Touch(long delta_size, bool adjust_free)
        {
            NonRecursiveTouch(delta_size);
            if ((_parent != null)&&(delta_size != 0))
                _parent.Touch(delta_size, adjust_free);
        }

        public virtual void Remove()
        {
            Touch(-(long)_size, true);
        }

        public bool Touched
        {
            get
            {
                return _touched;
            }
        }

        public long DeltaSize
        {
            get
            {
                return _deltasize;
            }
        }

        public long DeltaSizeBefore
        {
            get
            {
                long sum = 0;
                Atom a = this;
                while (a._parent != null)
                {
                    int index = _parent.Children.IndexOf(this);
                    for (int i = 0; i < index; i++)
                        sum += _parent.Children[i]._deltasize;
                    a = a._parent;
                }
                return sum;
            }
        }

        public virtual void FixFileOffsets(long delta)
        {
            // Nothing in base class
        }

        public virtual void Untouch()
        {
            _deltasize = 0;
            _touched = false;
        }

    }

    public class DemandAtom : Atom
    {
        protected string _demandpath;
        protected long _offset;

        public DemandAtom(Atom a, Stream s)
            : base(a)
        {
            if (!(s is FileStream))
                throw new InvalidOperationException();
            _demandpath = (s as FileStream).Name;
            _offset = s.Position;
        }

        public override void WriteAtom(Stream s)
        {
            if (!(s is FileStream))
                throw new InvalidOperationException();
            WriteHeader(s);
            byte[] b = new byte[MP4Util.DEMAND_BLOCK_SIZE];
            long todo = (long)(_size - _headersize);
            FileStream ds = new FileStream(_demandpath, FileMode.Open, FileAccess.Read);
            ds.Seek(_offset, SeekOrigin.Begin);
            // The new demand location is the new file
            _demandpath = (s as FileStream).Name;
            _offset = s.Position;
            while (todo > 0)
            {
                int doing = (int)((todo > MP4Util.DEMAND_BLOCK_SIZE) ? MP4Util.DEMAND_BLOCK_SIZE : todo);
                ds.Read(b, 0, doing);
                s.Write(b, 0, doing);
                todo -= doing;
            }
            ds.Close();
        }
    }

    public class DataAtom : Atom
    {
        protected byte[] _data = new byte[0];

        public byte[] Data
        {
            get
            {
                return _data;
            }
        }

        protected DataAtom(ContainerAtom ca)
            : base(ca)
        {
        }

        public DataAtom(Atom a, Stream s)
            : base(a)
        {
            if (s != null)
            {
                _data = new byte[_size - _headersize];
                s.Read(_data, 0, _data.Length);
            }
        }

        public override void WriteAtom(Stream s)
        {
            WriteHeader(s);
            s.Write(_data, 0, _data.Length);
        }

        protected ulong Uint64At(int offset)
        {
            ulong res = _data[offset];
            res = (res << 8) | _data[offset + 1];
            res = (res << 8) | _data[offset + 2];
            res = (res << 8) | _data[offset + 3];
            res = (res << 8) | _data[offset + 4];
            res = (res << 8) | _data[offset + 5];
            res = (res << 8) | _data[offset + 6];
            res = (res << 8) | _data[offset + 7];
            return res;
        }

        protected void Uint64At(int offset, ulong newval)
        {
            _data[offset] = (byte)((newval >> 56) & 0xff);
            _data[offset + 1] = (byte)((newval >> 48) & 0xff);
            _data[offset + 2] = (byte)((newval >> 40) & 0xff);
            _data[offset + 3] = (byte)((newval >> 32) & 0xff);
            _data[offset + 4] = (byte)((newval >> 24) & 0xff);
            _data[offset + 5] = (byte)((newval >> 16) & 0xff);
            _data[offset + 6] = (byte)((newval >> 8) & 0xff);
            _data[offset + 7] = (byte)(newval & 0xff);
        }

        protected uint Uint32At(int offset)
        {
            uint res = _data[offset];
            res = (res << 8) | _data[offset + 1];
            res = (res << 8) | _data[offset + 2];
            res = (res << 8) | _data[offset + 3];
            return res;
        }

        protected void Uint32At(int offset, uint newval)
        {
            _data[offset] = (byte)((newval >> 24) & 0xff);
            _data[offset + 1] = (byte)((newval >> 16) & 0xff);
            _data[offset + 2] = (byte)((newval >> 8) & 0xff);
            _data[offset + 3] = (byte)(newval & 0xff);
        }

        protected uint Uint16At(int offset)
        {
            uint res = _data[offset];
            res = (res << 8) | _data[offset + 1];
            return res;
        }

        protected void Uint16At(int offset, uint newval)
        {
            _data[offset] = (byte)((newval >> 8) & 0xff);
            _data[offset + 1] = (byte)(newval & 0xff);
        }

        protected void ResizeData(int newsize)
        {
            ResizeData(newsize, false);
        }

        protected void ResizeData(int newsize, bool clear)
        {
            int delta = newsize - _data.Length;
            Array.Resize(ref _data, newsize);
            if (clear)
                Array.Clear(_data, 0, newsize);
            Touch(delta);
        }
    }

    public class Atom_mvhd : DataAtom
    {
        public Atom_mvhd(ContainerAtom ca)
            : base(ca)
        {
        }

        public Atom_mvhd(Atom a, Stream s)
            : base(a, s)
        {
        }

        public uint DurationInFrames
        {
            get
            {
                if (_data[0] == 1)
                {
                    ulong scale = Uint64At(20);
                    ulong duration = Uint64At(28);
                    return (uint)(75 * duration / scale);
                }
                else
                {
                    uint scale = Uint32At(12);
                    uint duration = Uint32At(16);
                    return 75 * duration / scale;
                }
            }
        }

    }

    public class StringAtom : DataAtom
    {

        public StringAtom(ContainerAtom ca)
            : base(ca)
        {
        }

        public StringAtom(Atom a, Stream s)
            : base(a, s)
        {
        }
        
        public string Text
        {
            get
            {
                return Encoding.UTF8.GetString(_data, 4, _data.Length - 4);
            }
            set
            {
                byte[] b = Encoding.UTF8.GetBytes(value);
                ResizeData(4 + b.Length);
                Array.Copy(b, 0, _data, 4, b.Length);
            }
        }

        public override string ToString()
        {
            return Text;
        }
    }

    public class Atom_data : DataAtom
    {

        public enum DataTypes : uint
        {
            Implicit = 0, UTF8 = 1, UTF16BE = 2, SJIS = 3, HTML = 6, XML = 7, UUID = 8, ISRC = 9, MI3P = 10,
            GIF = 12, JPEG = 13, PNG = 14, URL = 15, Duration = 16, DateTimeUTC = 17, Genres = 18, Integer = 21, RIAAPA = 24,
            UPC = 25, BMP = 27, Invalid = 0xffffffff
        };

        public enum ContentRating : byte
        {
            Unspecified = 0,
            Clean = 2,
            Explicit = 4
        };

        public Atom_data(Atom a, Stream s)
            : base(a, s)
        {
        }

        public Atom_data(ContainerAtom ca)
            : base(ca)
        {
            _data = new byte[8];
            Uint16At(0, (uint)DataTypes.Invalid);
            _size = 16;
        }

        public DataTypes DataType
        {
            get
            {
                return (DataTypes)Uint32At(0);
            }
            set
            {
                Uint32At(0, (uint)value);
                Touch(0);
            }
        }

        public bool IsText
        {
            get
            {
                DataTypes t = DataType;
                return ((t == DataTypes.UTF8) || (t == DataTypes.UTF16BE) || (t == DataTypes.SJIS) || (t == DataTypes.ISRC) || (t == DataTypes.MI3P) ||
                    (t == DataTypes.URL) || (t == DataTypes.UPC));
            }
        }

        public bool IsImage
        {
            get
            {
                DataTypes t = DataType;
                return ((t == DataTypes.GIF) || (t == DataTypes.JPEG) || (t == DataTypes.PNG) || (t == DataTypes.BMP));
            }
        }

        public string Text
        {
            get
            {
                if (IsText)
                {
                    switch (DataType)
                    {
                        case DataTypes.UTF16BE:
                            return Encoding.BigEndianUnicode.GetString(_data, 8, _data.Length - 8);

                        case DataTypes.SJIS:
                            return MP4Util.ShiftJISEncoding.GetString(_data, 8, _data.Length - 8);

                        default:
                            return Encoding.UTF8.GetString(_data, 8, _data.Length - 8);
                    }
                }
                throw new InvalidOperationException();
            }
            set
            {
                if (!IsText)
                    DataType = DataTypes.UTF8;
                byte[] b;
                switch (DataType)
                {
                    case DataTypes.UTF16BE:
                        b = Encoding.BigEndianUnicode.GetBytes(value);
                        break;

                    case DataTypes.SJIS:
                        b = MP4Util.ShiftJISEncoding.GetBytes(value);
                        break;

                    default:
                        b = Encoding.UTF8.GetBytes(value);
                        break;
                }
                ResizeData(b.Length + 8);
                Array.Copy(b, 0, _data, 8, b.Length);
            }

        }

        public bool IsEnumeratedGenre
        {
            get
            {
                if ((_parent == null) || (_parent.Type != "gnre"))
                    return false;
                return ((DataType == DataTypes.Implicit) && (_data.Length >= 10));
            }
        }

        public string[] EnumeratedGenres
        {
            get
            {
                if (IsEnumeratedGenre)
                {
                    List<string> genres = new List<string>();
                    for (int i = 8; i < _data.Length; i += 2)
                        genres.Add(ID3v2Util.ID3v1Genres[(int)(Uint16At(i) - 1)]);
                    return genres.ToArray();
                }
                throw new InvalidOperationException();
            }
            set
            {
                ResizeData(8 + 2 * value.Length, true);
                int delta = 8 + 2 * value.Length - _data.Length;
                DataType = DataTypes.Implicit;

                for (int i = 0; i < value.Length; i++)
                {
                    bool found = false;
                    for (int j = 0; j < ID3v2Util.ID3v1Genres.Count; j++)
                        if (ID3v2Util.ID3v1Genres[j].ToLower() == value[i].ToLower())
                        {
                            found = true;
                            Uint16At(8 + i * 2, (uint)(j + 1));
                            break;
                        }
                    if (!found)
                        throw new InvalidDataException();
                }
            }
        }

        public bool IsBoolean
        {
            get
            {
                if ((_parent == null) || ((_parent.Type != "cpil") && (_parent.Type != "pgap") && (_parent.Type != "hdvd") && (_parent.Type != "pcst")))
                    return false;
                return ((DataType == DataTypes.Integer) && ((_size - _headersize) == 9));
            }
        }

        public bool IsUint8
        {
            get
            {
                return ((DataType == DataTypes.Integer) && ((_size - _headersize) == 9));
            }
        }

        public bool IsUint16
        {
            get
            {
                return ((DataType == DataTypes.Integer) && ((_size - _headersize) == 10));
            }
        }

        public bool IsUint32
        {
            get
            {
                return ((DataType == DataTypes.Integer) && ((_size - _headersize) == 12));
            }
        }

        public bool IsUint64
        {
            get
            {
                return ((DataType == DataTypes.Integer) && ((_size - _headersize) == 16));
            }
        }

        public byte Uint8
        {
            get
            {
                if (!IsUint8)
                    throw new InvalidOperationException();
                return _data[8];
            }
            set
            {
                if (!IsUint8)
                {
                    ResizeData(9, true);
                    DataType = DataTypes.Integer;
                }
                else
                    Touch(0);
                _data[8] = value;
            }
        }

        public ushort Uint16
        {
            get
            {
                if (!IsUint16)
                    return Uint8;
                return (ushort)Uint16At(8);
            }
            set
            {
                if (!IsUint16)
                {
                    ResizeData(10, true);
                    DataType = DataTypes.Integer;
                }
                else
                    Touch(0);
                Uint16At(8, value);
            }
        }

        public uint Uint32
        {
            get
            {
                if (!IsUint32)
                    return Uint16;
                return Uint32At(8);
            }
            set
            {
                if (!IsUint32)
                {
                    ResizeData(12, true);
                    DataType = DataTypes.Integer;
                }
                else
                    Touch(0);
                Uint32At(8, value);
            }
        }

        public ulong Uint64
        {
            get
            {
                if (!IsUint64)
                    return Uint32;
                return Uint64At(8);
            }
            set
            {
                if (!IsUint64)
                {
                    ResizeData(16, true);
                    DataType = DataTypes.Integer;
                }
                else
                    Touch(0);
                Uint64At(8, value);
            }
        }
        
        public bool IsRating
        {
            get
            {
                if ((_parent == null) || ((_parent.Type != "rtng")))
                    return false;
                return ((DataType == DataTypes.Integer) && ((_size - _headersize) == 9));
            }
        }

        public bool BoolValue
        {
            get
            {
                if (IsBoolean)
                    return (_data[8] == 1);
                throw new InvalidOperationException();
            }
            set
            {
                if (!IsBoolean)
                {
                    ResizeData(9, true);
                    DataType = DataTypes.Integer;
                }
                else
                    Touch(0);
                _data[8] = (value) ? (byte)1 : (byte)0;
            }
        }

        public ContentRating Rating
        {
            get
            {
                if (IsRating)
                    return (ContentRating)_data[8];
                throw new InvalidOperationException();
            }
            set
            {
                if (!IsRating)
                {
                    ResizeData(9, true);
                    DataType = DataTypes.Integer;
                }
                else
                    Touch(0);
                _data[8] = (byte)value;
            }
        }

        public bool IsTrackNumber
        {
            get
            {
                if ((_parent == null) || (_parent.Type != "trkn"))
                    return false;
                return (DataType == DataTypes.Implicit);
            }
        }

        public bool IsDiscNumber
        {
            get
            {
                if ((_parent == null) || (_parent.Type != "disk"))
                    return false;
                return (DataType == DataTypes.Implicit);
            }
        }

        public uint TrackNumber
        {
            get
            {
                if (IsTrackNumber)
                {
                    if (Uint16At(8) != 0)
                        throw new InvalidOperationException();
                    return Uint16At(10);
                }
                throw new InvalidOperationException();
            }
            set
            {
                if (!IsTrackNumber)
                {
                    ResizeData(16, true);
                    DataType = DataTypes.Implicit;
                }
                else
                    Touch(0);
                Uint16At(10, value);
            }
        }

        public uint TotalTracks
        {
            get
            {
                if (IsTrackNumber)
                {
                    if (Uint16At(8) != 0)
                        throw new InvalidOperationException();
                    return Uint16At(12);
                }
                throw new InvalidOperationException();
            }
            set
            {
                if (!IsTrackNumber)
                    // Force Resize/Creation
                    TrackNumber = 0;
                Uint16At(12, value);
            }
        }

        public uint DiscNumber
        {
            get
            {
                if (IsDiscNumber)
                {
                    if (Uint16At(8) != 0)
                        throw new InvalidOperationException();
                    return Uint16At(10);
                }
                throw new InvalidOperationException();
            }
            set
            {
                if (!IsDiscNumber)
                {
                    ResizeData(14, true);
                    DataType = DataTypes.Implicit;
                }
                else
                    Touch(0);
                Uint16At(10, value);
            }
        }

        public uint TotalDiscs
        {
            get
            {
                if (IsDiscNumber)
                {
                    if (Uint16At(8) != 0)
                        throw new InvalidOperationException();
                    return Uint16At(12);
                }
                throw new InvalidOperationException();
            }
            set
            {
                if (!IsDiscNumber)
                    // Force Resize/Creation
                    DiscNumber = 0;
                Uint16At(12, value);
            }
        }

        public void SaveImage(string path)
        {
            if (!IsImage)
                throw new InvalidOperationException();
            Stream s = new FileStream(path, FileMode.Create, FileAccess.Write);
            s.Write(_data, 8, _data.Length - 8);
            s.Close();
        }

        public void LoadImage(string path)
        {
            FileInfo fi = new FileInfo(path);
            ResizeData((int)(fi.Length + 8), true);
            string ext = Path.GetExtension(path);
            switch (ext.ToLower())
            {
                case ".png":
                    DataType = DataTypes.PNG;
                    break;
                case ".gif":
                    DataType = DataTypes.GIF;
                    break;
                case ".jpg":
                case ".jpeg":
                    DataType = DataTypes.JPEG;
                    break;
                case ".bmp":
                    DataType = DataTypes.BMP;
                    break;
                default:
                    throw new InvalidDataException();
            }
            Stream s = new FileStream(path, FileMode.Open, FileAccess.Read);
            s.Read(_data, 8, (int)(fi.Length));
            s.Close();
        }

        public override string ToString()
        {
            return (IsText) ? Text : DataType.ToString();
        }
    }

    public class Atom_free : Atom
    {
        public Atom_free(Atom a, Stream s)
            : base(a)
        {
        }

        public override void WriteAtom(Stream s)
        {
            WriteHeader(s);
            byte[] b = new byte[_size - _headersize];
            s.Write(b, 0, b.Length);
        }

        public override void Touch(long delta_size, bool adjust_free)
        {
            // Adjusting a free atom should never adjust other free atoms
            base.Touch(delta_size, false);
        }

    }

    public class Atom_hdlr : DataAtom
    {
        public Atom_hdlr(Atom a, Stream s)
            : base(a, s)
        {
        }

        public bool IsiTunesMetadata
        {
            get
            {
                return (Encoding.ASCII.GetString(_data, 8, 4) == "mdir");
            }
        }
    }

    public class Atom_stco : FullAtom
    {
        private List<uint> _offsets = new List<uint>();

        public Atom_stco(Atom a, Stream s, bool init)
            : base(a, s)
        {
            if (init)
            {
                uint count = ReadUint32(s);
                for (uint i = 0; i < count; i++)
                    _offsets.Add(ReadUint32(s));
            }
        }

        public Atom_stco(Atom a, Stream s)
            : this(a, s, true)
        {
        }

        public override void WriteAtom(Stream s)
        {
            base.WriteAtom(s);
            WriteUint32(s, (uint)(_offsets.Count));
            foreach (uint o in _offsets)
                WriteUint32(s, o);
        }

        public virtual void AdjustOffset(long delta)
        {
            Touch(0);
            for (int i = 0; i < _offsets.Count; i++)
                _offsets[i] = (uint)((int)_offsets[i] + delta);
        }

        public override void FixFileOffsets(long delta)
        {
            AdjustOffset(delta);
        }

    }

    public class Atom_co64 : Atom_stco
    {
        private List<ulong> _offsets = new List<ulong>();

        public Atom_co64(Atom a, Stream s)
            : base(a, s, false)
        {
            //_versionandflags = _versionandflags;
            uint count = ReadUint32(s);
            for (uint i = 0; i < count; i++)
                _offsets.Add(ReadUint64(s));
        }

        public override void WriteAtom(Stream s)
        {
            base.WriteAtom(s);
            WriteUint32(s, (uint)(_offsets.Count));
            foreach (ulong o in _offsets)
                WriteUint64(s, o);
        }

        public override void AdjustOffset(long delta)
        {
            Touch(0);
            for (int i = 0; i < _offsets.Count; i++)
                _offsets[i] = (ulong)((long)_offsets[i] + delta);
        }
    }

    public class ContainerAtom : Atom
    {
        public Atom this[int index]
        {
            get
            {
                return _children[index];
            }
        }

        protected List<Atom> _children = new List<Atom>();

        protected ContainerAtom()
        {
        }

        public ContainerAtom(ContainerAtom ca)
            : base(ca)
        {
        }

        public override void WriteAtom(Stream s)
        {
            WriteHeader(s);
            foreach (Atom a in _children)
                a.WriteAtom(s);
        }

        public bool Modified
        {
            get
            {
                foreach (Atom a in _children)
                {
                    if (a.Touched)
                        return true;
                    if (a is ContainerAtom)
                        if ((a as ContainerAtom).Modified)
                            return true;
                }
                return false;
            }
        }

        public List<Atom> Children
        {
            get
            {
                return _children;
            }
        }

        protected void InitChildren(Stream s, Type forced_subatom, long offset)
        {
            long pos = s.Position;
            while (s.Position < ((pos - (long)_headersize - offset) + (long)_size))
            {
                long spos = s.Position;

                Atom sa = new Atom(s, this);
                if (MP4Util.AtomTypes.ContainsKey(sa.Type))
                {
                    Type Atom_type = MP4Util.AtomTypes[sa.Type];
                    Atom sa2 = (Atom_type == typeof(Atom)) ? sa : Activator.CreateInstance(Atom_type, new object[] { sa, s }) as Atom;
                    _children.Add(sa2);
                }
                else if (MP4Util.AtomTypes.ContainsKey(Type + "." + sa.Type))
                {
                    Type Atom_type = MP4Util.AtomTypes[Type + "." + sa.Type];
                    Atom sa2 = (Atom_type == typeof(Atom)) ? sa : Activator.CreateInstance(Atom_type, new object[] { sa, s }) as Atom;
                    _children.Add(sa2);
                }
                else if (forced_subatom != null)
                {
                    Atom sa2 = Activator.CreateInstance(forced_subatom, new object[] { sa, s }) as Atom;
                    _children.Add(sa2);
                }
                else
                {
                    if (MP4Util.LoadData)
                    {
                        Atom sa2 = new DataAtom(sa, s);
                        _children.Add(sa2);
                    }
                    else
                        _children.Add(sa);
                }

                s.Seek(spos + sa.Size, SeekOrigin.Begin);
            }
        }

        public ContainerAtom(Atom a, Stream s)
            : this(a, s, false)
        {
        }

        public ContainerAtom(Atom a, Stream s, bool defer)
            : base(a)
        {
            if (!defer)
                InitChildren(s, null, 0);
        }

        public Atom FindPath(string path)
        {
            string[] entry = path.Split(".".ToCharArray(), 2);
            foreach (Atom a in _children)
                if ((a.Type == entry[0]) && (!a.Removed))
                {
                    if (entry.Length == 1)
                        return a;
                    if (!(a is ContainerAtom))
                        return null;
                    return (a as ContainerAtom).FindPath(entry[1]);
                }
            return null;
        }

        public Atom[] FindMultiplePath(string path)
        {
            List<Atom> result = new List<Atom>();
            string[] entry = path.Split(".".ToCharArray(), 2);
            foreach (Atom a in _children)
                if ((a.Type == entry[0]) && (!a.Removed))
                {
                    if (entry.Length == 1)
                        result.Add(a);
                    else
                    {
                        if (!(a is ContainerAtom))
                            return null;
                        return (a as ContainerAtom).FindMultiplePath(entry[1]);
                    }
                }
            return result.ToArray();
        }

        public void CleanRemovedAtoms()
        {
            List<Atom> removed = new List<Atom>();
            foreach (Atom a in _children)
            {
                if (a.Removed)
                    removed.Add(a);
                if (a is ContainerAtom)
                    (a as ContainerAtom).CleanRemovedAtoms();
            }
            foreach (Atom a in removed)
                Children.Remove(a);
        }

        public override void Touch(long delta_size, bool adjust_free)
        {
            if ((adjust_free)&&(delta_size != 0))
            {
                foreach (Atom a in _children)
                {
                    if (a is Atom_free)
                    {
                        if (((long)a.Size - delta_size) > 16)
                        {
                            a.NonRecursiveTouch(-delta_size);
                            return;
                        }
                    }
                }
            }
            base.Touch(delta_size, adjust_free);
        }

        public override void Untouch()
        {
            foreach (Atom a in _children)
                a.Untouch();
            base.Untouch();
        }

        public virtual Atom CreateChild(string Atomtype)
        {
            if (MP4Util.AtomTypes.ContainsKey(Atomtype))
            {
                Type t = MP4Util.AtomTypes[Atomtype];
                Atom a = Activator.CreateInstance(t, new object[] { this }) as Atom;
                a.Type = Atomtype;
                _children.Add(a);
                Touch(a.Size);
                return a;
            }
            else
                throw new InvalidDataException();
        }

        public Atom CreatePath(string path)
        {
            string[] entry = path.Split(".".ToCharArray(), 2);
            foreach (Atom a in _children)
                if ((a.Type == entry[0]) && (!a.Removed))
                {
                    if (entry.Length == 1)
                        return a;
                    if (!(a is ContainerAtom))
                        return null;
                    return (a as ContainerAtom).CreatePath(entry[1]);
                }
            Atom ca = CreateChild(entry[0]);
            if (entry.Length == 1)
                return ca;
            return (ca as ContainerAtom).CreatePath(entry[1]);
        }

        public override void FixFileOffsets(long delta)
        {
            foreach (Atom a in _children)
                a.FixFileOffsets(delta);
        }

        public long ComputeFreeSpace()
        {
            long sum = 0;
            foreach (Atom a in _children)
            {
                if (a is Atom_free)
                    sum += a.Size;
                if (a is ContainerAtom)
                    sum += (a as ContainerAtom).ComputeFreeSpace();
            }
            return sum;
        }

        public void RemoveFreeSpace()
        {
            foreach (Atom a in _children)
            {
                if (a is Atom_free)
                    a.Remove();
                if (a is ContainerAtom)
                    (a as ContainerAtom).RemoveFreeSpace();
            }
        }

    }

    public class RootAtom : ContainerAtom, IMetadataProvider, ICodecProvider
    {

        #region IMetadataProvider Properties
        public string Title
        {
            get
            {
                try
                {
                    Atom_ilst atom = FindPath("moov.udta.meta.ilst") as Atom_ilst;
                    return (atom.FindPath("©nam.data") as Atom_data).Text;
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
                    Atom_ilst atom = FindPath("moov.udta.meta.ilst") as Atom_ilst;
                    return (atom.FindPath("©alb.data") as Atom_data).Text;
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
                    Atom_ilst atom = FindPath("moov.udta.meta.ilst") as Atom_ilst;
                    return (atom.FindPath("©ART.data") as Atom_data).Text;
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
                    Atom_ilst atom = FindPath("moov.udta.meta.ilst") as Atom_ilst;
                    return (atom.FindPath("aART.data") as Atom_data).Text;
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
                    Atom_ilst atom = FindPath("moov.udta.meta.ilst") as Atom_ilst;
                    return (int)((atom.FindPath("trkn.data") as Atom_data).TrackNumber);
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
                    Atom_ilst atom = FindPath("moov.udta.meta.ilst") as Atom_ilst;
                    return (atom.FindPath("cpil.data") as Atom_data).BoolValue;
                }
                catch
                {
                    throw new NoMetadataException("Compilation");
                }
            }
        }

        public IEnumerable<KeyValuePair<string, string>> GetTextMetadata()
        {
            Atom_ilst ilst = FindPath("moov.udta.meta.ilst") as Atom_ilst;
            foreach (Atom atom in ilst.Children)
            {
                ContainerAtom ca = atom as ContainerAtom;
                if (MP4Util.VorbisCommentMapping.ContainsKey(atom.Type))
                {
                    string key = MP4Util.VorbisCommentMapping[atom.Type];
                    foreach (Atom childatom in ca.FindMultiplePath("data"))
                    {
                        Atom_data da = childatom as Atom_data;
                        if (da.IsText)
                            yield return new KeyValuePair<string, string>(key, da.Text);
                        else if (da.DataType == Atom_data.DataTypes.Integer)
                            yield return new KeyValuePair<string, string>(key, da.Uint64.ToString());
                        else if (da.IsBoolean)
                            yield return new KeyValuePair<string, string>(key, da.BoolValue ? "1" : "0");
                        else if (da.IsEnumeratedGenre)
                        {
                            foreach (var g in da.EnumeratedGenres)
                                yield return new KeyValuePair<string, string>(key, g);
                        }
                        else if (da.IsRating)
                            yield return new KeyValuePair<string, string>(key, da.Rating.ToString());
                    }
                }
                else if (MP4Util.SpecialMapping.ContainsKey(atom.Type))
                {
                    foreach (var kv in MP4Util.SpecialMapping[atom.Type](ca))
                        yield return kv;
                }
                else if (atom.Type == "----")
                {
                    string key = (ca.FindPath("name") as StringAtom).Text;
                    foreach (Atom childatom in (atom as ContainerAtom).FindMultiplePath("data"))
                    {
                        Atom_data da = childatom as Atom_data;
                        if (da.IsText)
                            yield return new KeyValuePair<string, string>(key, da.Text);
                    }

                }
            }

            yield break;
        }

        #endregion

        private string _associatedpath;

        public string Path
        {
            get
            {
                return _associatedpath;
            }
        }

        public string CodecName
        {
            get;
            protected set;
        }

        public CodecType CodecType
        {
            get;
            protected set;
        }

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

        public uint BitsPerSample
        {
            get;
            protected set;
        }

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

        public uint DurationInFrames
        {
            get;
            protected set;
        }

        public uint DurationInSeconds => DurationInFrames / 75;

        public RootAtom()
        {
        }

        protected void ParseCodecInfo()
        {
            ContainerAtom stsd = FindPath("moov.trak.mdia.minf.stbl.stsd") as ContainerAtom;
            if ((stsd != null)&&(stsd.Children.Count == 1))
            {
                CodecAtom codec = stsd.Children[0] as CodecAtom;
                if (codec.Type == "mp4a")
                {
                    CodecName = "AAC";
                    CodecType = CodecType.Lossy;
                    Samplerate = codec.SampleRate;
                    BitsPerSample = codec.SampleSize;
                    Channels = codec.Channels;
                    Atom_mp4a_esds esds = codec.Children[0] as Atom_mp4a_esds;
                    if (esds != null)
                    {
                        AverageBitrate = esds.AverageBitrate;
                        MaxBitrate = esds.MaxBitrate;
                    }
                }
                if (codec.Type == "alac")
                {
                    CodecName = "ALAC";
                    CodecType = CodecType.Lossless;
                    Atom_alac alac = codec.Children[0] as Atom_alac;
                    if (alac != null)
                    {
                        Samplerate = alac.SampleRate;
                        BitsPerSample = alac.BitDepth;
                        Channels = alac.NumChannels;
                        AverageBitrate = alac.AverageBitrate;
                        MaxBitrate = alac.AverageBitrate;
                    }
                }
            }
        }

        public RootAtom(string path)
        {
            ReadFile(path);
        }

        public void ReadFile(string path)
        {
            Stream s = new FileStream(path, FileMode.Open, FileAccess.Read);

            while (s.Position < s.Length)
            {
                long pos = s.Position;

                Atom a = new Atom(s, this);
                if (MP4Util.AtomTypes.ContainsKey(a.Type))
                {
                    Type Atom_type = MP4Util.AtomTypes[a.Type];
                    a = (Atom_type == typeof(Atom)) ? a : Activator.CreateInstance(Atom_type, new object[] { a, s }) as Atom;
                }
                else if (MP4Util.LoadData)
                    a = new DataAtom(a, s);
                Children.Add(a);

                s.Seek(pos + a.Size, SeekOrigin.Begin);
            }

            s.Close();
            _associatedpath = path;
            ParseCodecInfo();

            Atom_mvhd mvhd = FindPath("moov.mvhd") as Atom_mvhd;
            DurationInFrames = mvhd.DurationInFrames;
        }

        public void WriteFile(string path)
        {
            Stream s = new FileStream(path, FileMode.Create, FileAccess.Write);
            WriteAtom(s);
            s.Close();
            _associatedpath = path;
            Untouch();
        }

        public void ModifyFile()
        {
            if ((Touched)||(!File.Exists(_associatedpath)))
                throw new InvalidOperationException();
            Stream s = new FileStream(_associatedpath, FileMode.Open, FileAccess.ReadWrite);
            foreach (Atom a in _children)
            {
                bool writeatom = a.Touched || (a.DeltaSizeBefore != 0);
                if ((!writeatom) && (a is ContainerAtom))
                    writeatom = (a as ContainerAtom).Modified;
                if (writeatom)
                    a.WriteAtom(s);
                else
                    s.Seek(a.Size, SeekOrigin.Current);
            }
            s.Close();
            Untouch();
        }

        public override void WriteAtom(Stream s)
        {
            foreach (Atom a in _children)
                a.WriteAtom(s);
        }

     }

    public class FullContainerAtom : ContainerAtom
    {
        protected uint _versionandflags = 0;

        public FullContainerAtom(Atom a, Stream s)
            : base(a, s, true)
        {
            _versionandflags = ReadUint32(s);
            InitChildren(s, null, 4);
        }

        public FullContainerAtom(ContainerAtom ca)
            : base(ca)
        {
        }

        public override void WriteAtom(Stream s)
        {
            WriteHeader(s);
            WriteUint32(s, _versionandflags);
            foreach (Atom a in _children)
                a.WriteAtom(s);
        }
    }

    public class Atom_stsd : ContainerAtom
    {
        protected uint _versionandflags = 0;
        protected uint _elements;

        public Atom_stsd(Atom a, Stream s)
            : base(a, s, true)
        {
            _versionandflags = ReadUint32(s);
            _elements = ReadUint32(s);
            InitChildren(s, null, 8);
        }

        public Atom_stsd(ContainerAtom ca)
            : base(ca)
        {
        }

        public override void WriteAtom(Stream s)
        {
            WriteHeader(s);
            WriteUint32(s, _versionandflags);
            WriteUint32(s, _elements);
            foreach (Atom a in _children)
                a.WriteAtom(s);
        }
    }

    public class Atom_mp4a_esds : DataAtom
    {
        public uint MaxBitrate
        {
            get
            {
                return Uint32At(22);
            }
        }


        public uint AverageBitrate
        {
            get
            {
                return Uint32At(26);
            }
        }

        public Atom_mp4a_esds(Atom a, Stream s)
            : base(a, s)
        {

        }

    }

    public class Atom_alac : ContainerAtom
    {
        protected uint _version;
        // Version 0
        protected uint _framelength;
        protected byte _compatibleversion;
        protected byte _pb;
        protected byte _mb;
        protected byte _kb;
        protected uint _maxrun;
        protected uint _maxframebytes;
        // Other Version
        protected byte[] _data;

        public uint SampleRate
        {
            protected set;
            get;
        }

        public byte NumChannels
        {
            protected set;
            get;
        }

        public uint AverageBitrate
        {
            protected set;
            get;
        }

        public byte BitDepth
        {
            protected set;
            get;
        }

        public Atom_alac(Atom a, Stream s)
            : base(a, s, true)
        {
            _version = ReadUint32(s);
            if (_version == 0)
            {
                _framelength = ReadUint32(s);
                _compatibleversion = (byte)s.ReadByte();
                BitDepth = (byte)s.ReadByte();
                _pb = (byte)s.ReadByte();
                _mb = (byte)s.ReadByte();
                _kb = (byte)s.ReadByte();
                NumChannels = (byte)s.ReadByte();
                _maxrun = ReadUint16(s);
                _maxframebytes = ReadUint32(s);
                AverageBitrate = ReadUint32(s);
                SampleRate = ReadUint32(s);
                InitChildren(s, null, 24);
            }
            else
            {
                _data = new byte[_size - _headersize - 4];
                s.Read(_data, 0, _data.Length);
            }
        }

        public Atom_alac(ContainerAtom ca)
            : base(ca)
        {

        }

        public override void WriteAtom(Stream s)
        {
            WriteUint32(s, _version);
            if (_version == 0)
            {
                WriteUint32(s, _framelength);
                s.WriteByte(_compatibleversion);
                s.WriteByte(BitDepth);
                s.WriteByte(_pb);
                s.WriteByte(_mb);
                s.WriteByte(_kb);
                s.WriteByte(NumChannels);
                WriteUint16(s, _maxrun);
                WriteUint32(s, _maxframebytes);
                WriteUint32(s, AverageBitrate);
                WriteUint32(s, SampleRate);
                foreach (Atom a in _children)
                    a.WriteAtom(s);
            }
        }

    }

    public class CodecAtom : ContainerAtom
    {
        protected byte[] _reserved0 = new byte[6];
        protected uint _dref;
        protected uint _audioencodingversion;
        // Version 0
        protected uint _audioencodingrevision;
        protected uint _audioencodingvendor;
        protected uint _compressionid;
        protected uint _packetsize;
        protected uint _sampleratefrac;
        // Other Version
        protected byte[] _data;

        public uint Channels
        {
            get;
            protected set;
        }

        public uint SampleRate
        {
            get;
            protected set;
        }

        public uint SampleSize
        {
            get;
            protected set;
        }
        
        public CodecAtom(Atom a, Stream s)
            : base(a, s, true)
        {
            s.Read(_reserved0, 0, 6);
            _dref = ReadUint16(s);
            _audioencodingversion = ReadUint16(s);
            if (_audioencodingversion == 0)
            {
                _audioencodingrevision = ReadUint16(s);
                _audioencodingvendor = ReadUint32(s);
                Channels = ReadUint16(s);
                SampleSize = ReadUint16(s);
                _compressionid = ReadUint16(s);
                _packetsize = ReadUint16(s);
                SampleRate = ReadUint16(s);
                _sampleratefrac = ReadUint16(s);
                InitChildren(s, null, 28);
            }
            else
            {
                _data = new byte[_size - _headersize - 10];
                s.Read(_data, 0, _data.Length);
            }
        }

        public CodecAtom(ContainerAtom ca)
            : base(ca)
        {
        }

        public override void WriteAtom(Stream s)
        {
            s.Write(_reserved0, 0, 6);
            WriteUint16(s, _dref);
            WriteUint16(s, _audioencodingversion);
            if (_audioencodingversion == 0)
            {
                WriteUint16(s, _audioencodingrevision);
                WriteUint32(s, _audioencodingvendor);
                WriteUint16(s, Channels);
                WriteUint16(s, SampleSize);
                WriteUint16(s, _compressionid);
                WriteUint16(s, _packetsize);
                WriteUint16(s, SampleRate);
                WriteUint16(s, _sampleratefrac);
                foreach (Atom a in _children)
                    a.WriteAtom(s);
            }
            else
                s.Write(_data, 0, _data.Length);
        }

    }

    public class Atom_ilst : ContainerAtom
    {
        public Atom_ilst(Atom a, Stream s)
            : base(a, s, true)
        {
            InitChildren(s, typeof(ContainerAtom), 0);
        }

        public Atom_ilst(ContainerAtom ca)
            : base(ca)
        {
        }

        public override Atom CreateChild(string Atomtype)
        {
            ContainerAtom a = new ContainerAtom(this);
            a.Type = Atomtype;
            _children.Add(a);
            Touch(a.Size);
            return a;
        }
    }

    public class FullAtom : Atom
    {
        protected uint _versionandflags;

        public FullAtom(Atom a, Stream s)
            : base(a)
        {
            _versionandflags = ReadUint32(s);
        }

        public override void WriteAtom(Stream s)
        {
            WriteHeader(s);
            WriteUint32(s, _versionandflags);
        }
    }

    public class Atom_ftyp : Atom
    {
        protected uint _majorbrand;
        protected uint _minorversion;
        protected List<uint> _compatiblebrands = new List<uint>();

        public Atom_ftyp(Atom a, Stream s)
            : base(a)
        {
            _majorbrand = ReadUint32(s);
            _minorversion = ReadUint32(s);
            for (uint i = 16; i < _size; i += 4)
                _compatiblebrands.Add(ReadUint32(s));
        }

        public override void WriteAtom(Stream s)
        {
            WriteHeader(s);
            WriteUint32(s, _majorbrand);
            WriteUint32(s, _minorversion);
            foreach (uint b in _compatiblebrands)
                WriteUint32(s, b);
        }
    }

}
