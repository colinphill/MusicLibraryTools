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
using System.Diagnostics.Tracing;
using System.Security.Cryptography;

namespace MusicFileUtilities
{

    public static class MP4Util
    {

        public const int DEMAND_BLOCK_SIZE = 0x100000;
        
        public static Dictionary<string, Type> AtomTypes = new Dictionary<string, Type>();

        public static Encoding TypeEncoding = null;

        public static Encoding ShiftJISEncoding = null;

        static MP4Util()
        {
            Init();
        }

        public delegate IEnumerable<KeyValuePair<TagFields, string>> HandleAtom(ContainerAtom atom);

        private static IEnumerable<KeyValuePair<TagFields, string>> HandleNullAtom(ContainerAtom atom)
        {
            yield break;
        }

        private static IEnumerable<KeyValuePair<TagFields, string>> HandleTrackDiscAtom(ContainerAtom atom)
        {
            Atom_data da = atom.FindPath("data") as Atom_data;
            if (da.IsTrackNumber)
            {
                yield return KeyValuePair.Create(TagFields.TrackNumber, da.TrackNumber.ToString());
                if (da.TotalTracks != 0)
                    yield return KeyValuePair.Create(TagFields.TotalTracks, da.TotalTracks.ToString());
            }
            if (da.IsDiscNumber)
            {
                yield return KeyValuePair.Create(TagFields.DiscNumber, da.DiscNumber.ToString());
                if (da.TotalDiscs != 0)
                    yield return KeyValuePair.Create(TagFields.TotalDiscs, da.TotalDiscs.ToString());
            }
        }

        public static Dictionary<string, HandleAtom> SpecialMapping = new Dictionary<string, HandleAtom>()
        {
            { "trkn",  new HandleAtom(HandleTrackDiscAtom) },
            { "disk",  new HandleAtom(HandleTrackDiscAtom) },
        };

        public static Dictionary<string, ID3v2Util.APICType> ImageMapping = new Dictionary<string, ID3v2Util.APICType>()
        {
            {"covr", ID3v2Util.APICType.FrontCover },
        };

        public static Dictionary<string, TagFields> TagMapping = new Dictionary<string, TagFields>()
        {
            { "Acoustid Id", TagFields.AcoustID_ID },
            { "Acoustid Fingerprint", TagFields.AcoustID_Fingerprint },
            { "©alb", TagFields.Album },
            { "aART", TagFields.AlbumArtist },
            { "soaa", TagFields.AlbumArtistSort },
            { "soal", TagFields.AlbumSort },
            { "©ART", TagFields.Artist },
            { "soar", TagFields.ArtistSort },
            { "ARTISTS", TagFields.Artists },
            { "ASIN", TagFields.ASIN },
            { "BARCODE", TagFields.Barcode },
            { "tmpo", TagFields.BPM },
            { "CATALOGNUMBER", TagFields.CatalogNumber },
            { "©cmt", TagFields.Comment },
            { "cpil", TagFields.Compilation },
            { "©wrt", TagFields.Composer },
            { "soco", TagFields.ComposerSort },
            { "CONDUCTOR", TagFields.Conductor },
            { "cprt", TagFields.Copyright },
            { "DISCSUBTITLE", TagFields.DiscSubtitle },
            { "©too", TagFields.EncodedBy },
            { "ENGINEER", TagFields.Engineer },
            { "pgap", TagFields.Gapless },
            { "©gen", TagFields.Genre },
            { "gnre", TagFields.Genre },
            { "©grp", TagFields.Grouping },
            { "initialkey", TagFields.Key },
            { "ISRC", TagFields.ISRC },
            { "LANGUAGE", TagFields.Language },
            { "LICENSE", TagFields.License },
            { "LYRICIST", TagFields.Lyricist },
            { "Performer", TagFields.Performer },
            { "©lyr", TagFields.Lyrics },
            { "MEDIA", TagFields.Media },
            { "DJMIXER", TagFields.DJMixer },
            { "MIXER", TagFields.Mixer },
            { "MOOD", TagFields.Mood },
            { "©mvn", TagFields.Movement },
            { "©mvc", TagFields.MovementTotal },
            { "©mvi", TagFields.MovementNumber },
            { "MusicBrainz Artist Id", TagFields.MusicBrainz_ArtistID },
            { "MusicBrainz Disc Id", TagFields.MusicBrainz_DiscID },
            { "MusicBrainz Original Artist Id", TagFields.MusicBrainz_OriginalArtistID },
            { "MusicBrainz Original Album Id", TagFields.MusicBrainz_OriginalAlbumID },
            { "MusicBrainz Track Id", TagFields.MusicBrainz_RecordingID },
            { "MusicBrainz Album Artist Id", TagFields.MusicBrainz_AlbumArtistID },
            { "MusicBrainz Release Group Id", TagFields.MusicBrainz_ReleaseGroupID },
            { "MusicBrainz Album Id", TagFields.MusicBrainz_AlbumID },
            { "MusicBrainz Release Track Id", TagFields.MusicBrainz_TrackID },
            { "MusicBrainz Work Id", TagFields.MusicBrainz_WorkID },
            { "PRODUCER", TagFields.Producer },
            { "LABEL", TagFields.Label },
            { "MusicBrainz Album Release Country", TagFields.ReleaseCountry },
            { "©day", TagFields.Date },
            { "MusicBrainz Album Status", TagFields.ReleaseStatus },
            { "MusicBrainz Album Type", TagFields.ReleaseType },
            { "REMIXER", TagFields.Remixer },
            { "REPLAYGAIN_ALBUM_GAIN", TagFields.ReplayGain_Album_Gain },
            { "REPLAYGAIN_ALBUM_PEAK", TagFields.ReplayGain_Album_Peak },
            { "REPLAYGAIN_ALBUM_RANGE", TagFields.ReplayGain_Album_Range },
            { "REPLAYGAIN_REFERENCE_LOUDNESS", TagFields.ReplayGain_Reference_Loudness },
            { "REPLAYGAIN_TRACK_GAIN", TagFields.ReplayGain_Track_Gain },
            { "REPLAYGAIN_TRACK_PEAK", TagFields.ReplayGain_Track_Peak },
            { "REPLAYGAIN_TRACK_RANGE", TagFields.ReplayGain_Track_Range },
            { "SCRIPT", TagFields.Script },
            { "tvsh", TagFields.Show },
            { "sosn", TagFields.ShowSort },
            { "SUBTITLE", TagFields.Subtitle },
            { "©nam", TagFields.Title },
            { "sonm", TagFields.TitleSort },
            { "©wrk", TagFields.Work },
        };

        // Maps each TagFields value to the canonical atom key for writing.
        // Standard 4-byte iTunes keys are preferred over freeform string keys.
        // Lazily built once (thread-safe) — the old null-check pattern could race.
        private static readonly Lazy<Dictionary<TagFields, string>> _reverseTagMapping =
            new Lazy<Dictionary<TagFields, string>>(BuildReverseTagMapping);

        public static Dictionary<TagFields, string> ReverseTagMapping => _reverseTagMapping.Value;

        private static Dictionary<TagFields, string> BuildReverseTagMapping()
        {
            var map = new Dictionary<TagFields, string>();
            // First pass: 4-byte standard keys
            foreach (var kv in TagMapping)
            {
                if (!map.ContainsKey(kv.Value))
                {
                    try { if (TypeEncoding.GetBytes(kv.Key).Length == 4) map[kv.Value] = kv.Key; }
                    catch { }
                }
            }
            // Second pass: freeform keys for any fields not yet covered
            foreach (var kv in TagMapping)
                if (!map.ContainsKey(kv.Value))
                    map[kv.Value] = kv.Key;
            return map;
        }

        public static void Init()
        {
            TypeEncoding = Encoding.GetEncoding(28591, new EncoderExceptionFallback(), new DecoderExceptionFallback()); // iso-8859-1

            if (MetadataOptions.UseLegacyEncodings)
                ShiftJISEncoding = Encoding.GetEncoding(932, new EncoderExceptionFallback(), new DecoderExceptionFallback());

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

        // Direct construction for the known atom types. Activator.CreateInstance resolves the
        // (Atom, Stream) constructor by reflection on every atom, which dominates parse CPU
        // on a library scan; this keeps the AtomTypes registry but bypasses reflection for
        // every type registered by Init().
        public static Atom CreateAtom(Type t, Atom a, Stream s)
        {
            if (t == typeof(ContainerAtom)) return new ContainerAtom(a, s);
            if (t == typeof(Atom_data)) return new Atom_data(a, s);
            if (t == typeof(StringAtom)) return new StringAtom(a, s);
            if (t == typeof(DataAtom)) return new DataAtom(a, s);
            if (t == typeof(FullContainerAtom)) return new FullContainerAtom(a, s);
            if (t == typeof(Atom_ftyp)) return new Atom_ftyp(a, s);
            if (t == typeof(Atom_mvhd)) return new Atom_mvhd(a, s);
            if (t == typeof(Atom_hdlr)) return new Atom_hdlr(a, s);
            if (t == typeof(Atom_free)) return new Atom_free(a, s);
            if (t == typeof(Atom_co64)) return new Atom_co64(a, s);
            if (t == typeof(Atom_stco)) return new Atom_stco(a, s);
            if (t == typeof(Atom_stsd)) return new Atom_stsd(a, s);
            if (t == typeof(Atom_ilst)) return new Atom_ilst(a, s);
            if (t == typeof(DemandAtom)) return new DemandAtom(a, s);
            if (t == typeof(CodecAtom)) return new CodecAtom(a, s);
            if (t == typeof(Atom_alac)) return new Atom_alac(a, s);
            if (t == typeof(Atom_mp4a_esds)) return new Atom_mp4a_esds(a, s);
            return Activator.CreateInstance(t, new object[] { a, s }) as Atom;
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
        // Absolute offset this atom was parsed from in the source file (-1 for atoms created in
        // memory). In-place saves seek here to overwrite an atom without moving anything else.
        protected long _fileoffset = -1;
        // Type is consulted repeatedly (dictionary lookups, FindPath comparisons); decode once.
        private string _typestring = null;

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

        public long FileOffset
        {
            get
            {
                return _fileoffset;
            }
        }

        internal void CommitFileOffset(long offset)
        {
            _fileoffset = offset;
        }

        internal ContainerAtom ParentAtom => _parent;

        internal bool WouldHeaderResizeAfter(long sizeDelta)
        {
            Int128 newSize = (Int128)_size + sizeDelta;
            if (newSize < 8 || newSize > long.MaxValue)
                throw new OverflowException("The adjusted MP4 atom size is outside the supported range.");
            return (newSize > uint.MaxValue) != (_headersize >= 16);
        }

        // True when writing this atom would require a different-length size field than it occupies
        // on disk (32-bit <-> 64-bit extended size), which would shift its own body. Never happens
        // for moov in practice, but guards the in-place path against it.
        public bool WouldHeaderResize
        {
            get
            {
                return (_size > 0xffffffff) != (_headersize >= 16);
            }
        }

        public string Type
        {
            get
            {
                return _typestring ??= MP4Util.TypeEncoding.GetString(_type);
            }
            set
            {
                _type = MP4Util.TypeEncoding.GetBytes(value);
                _typestring = value;
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
            _typestring = a._typestring;
            _size = a._size;
            _headersize = a._headersize;
            // A typed atom (moov/mdat/...) is built by copying the base Atom that read the header;
            // the original file offset must survive that upgrade or in-place saves can't locate it.
            _fileoffset = a._fileoffset;
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
            _fileoffset = offs;
            ulong size = ReadUint32(s);
            s.ReadExactly(_type);
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
            Span<byte> b = stackalloc byte[2];
            s.ReadExactly(b);
            return (((uint)b[0]) << 8) | (uint)b[1];
        }

        protected uint ReadUint32(Stream s)
        {
            Span<byte> b = stackalloc byte[4];
            s.ReadExactly(b);
            return (((uint)b[0]) << 24) | (((uint)b[1]) << 16) | (((uint)b[2]) << 8) | (uint)b[3];
        }

        protected ulong ReadUint64(Stream s)
        {
            Span<byte> b = stackalloc byte[8];
            s.ReadExactly(b);
            ulong res = 0;
            for (int i = 0; i < 8; i++)
                res = (res << 8) | b[i];
            return res;
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

        public virtual void FixFileOffsets(long delta)
        {
            // Nothing in base class
        }

        public virtual void ValidateFileOffsetAdjustment(long delta)
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
        private long? _pendingOffset;

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
            using FileStream ds = new FileStream(_demandpath, FileMode.Open, FileAccess.Read);
            ds.Seek(_offset, SeekOrigin.Begin);
            // Do not rebind the demand source while a staged rewrite is still fallible. RootAtom
            // commits this offset to the final destination only after the replacement rename.
            _pendingOffset = s.Position;
            while (todo > 0)
            {
                int doing = (int)((todo > MP4Util.DEMAND_BLOCK_SIZE) ? MP4Util.DEMAND_BLOCK_SIZE : todo);
                ds.ReadExactly(b, 0, doing);
                s.Write(b, 0, doing);
                todo -= doing;
            }
        }

        internal void CommitRewrite(string path)
        {
            if (_pendingOffset.HasValue)
            {
                _demandpath = path;
                _offset = _pendingOffset.Value;
                _pendingOffset = null;
            }
        }

        internal void AbortRewrite()
        {
            _pendingOffset = null;
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
                s.ReadExactly(_data);
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
                    uint scale = Uint32At(20);
                    ulong duration = Uint64At(24);
                    if (scale == 0)
                        return 0;
                    return (uint)(75 * duration / scale);
                }
                else
                {
                    uint scale = Uint32At(12);
                    uint duration = Uint32At(16);
                    if (scale == 0)
                        return 0;
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

    public class Atom_data : DataAtom, IMetadataImage
    {

        public enum DataTypes : uint
        {
            Implicit = 0, UTF8 = 1, UTF16BE = 2, SJIS = 3, HTML = 6, XML = 7, UUID = 8, ISRC = 9, MI3P = 10,
            GIF = 12, JPEG = 13, PNG = 14, URL = 15, Duration = 16, DateTimeUTC = 17, Genres = 18, Integer = 21, RIAAPA = 24,
            UPC = 25, BMP = 27, Invalid = 0xffffffff
        };

        private byte[] _imagedata;
        private int _imagewidth;
        private int _imageheight;
        private bool _dimsComputed;

        // The image payload is _data minus the 8-byte data-atom header. Copied lazily: the
        // eager copy doubled the memory traffic of every cover-art atom on a scan even when
        // nothing looked at the image.
        private byte[] ImageBytes
        {
            get
            {
                if (_imagedata == null && IsImage && _data.Length >= 8)
                {
                    _imagedata = new byte[_data.Length - 8];
                    Array.Copy(_data, 8, _imagedata, 0, _imagedata.Length);
                }
                return _imagedata;
            }
        }

        // Dimensions are decoded lazily so a scan that only reads text tags doesn't pay to
        // parse every embedded cover image, and probed in place so they don't force the
        // payload copy either.
        private void EnsureDimensions()
        {
            if (_dimsComputed) return;
            _dimsComputed = true;
            ReadOnlySpan<byte> imagedata = _imagedata != null
                ? _imagedata
                : (IsImage && _data.Length > 8 ? _data.AsSpan(8) : default);
            if (!imagedata.IsEmpty)
            {
                var img = ImageFile.GetImageDimensions(imagedata);
                _imagewidth = img.Width;
                _imageheight = img.Height;
            }
        }

        public string ImageToMimeType()
        {
            if (DataType == DataTypes.GIF)
                return "image/gif";
            if (DataType == DataTypes.JPEG)
                return "image/jpeg";
            if (DataType == DataTypes.PNG)
                return "image/png";
            if (DataType == DataTypes.BMP)
                return "image/bmp";
            return "image/unknown";
        }

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
                            if (MP4Util.ShiftJISEncoding == null)
                                throw new UnsupportedMetadataEncodingException("Shift-JIS");
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
                        if (MP4Util.ShiftJISEncoding == null)
                            throw new UnsupportedMetadataEncodingException("Shift-JIS");
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

        public byte[] ImageData => ImageBytes;
        public int ImageWidth { get { EnsureDimensions(); return _imagewidth; } }
        public int ImageHeight { get { EnsureDimensions(); return _imageheight; } }

        string IMetadataImage.Description => "";

        string IMetadataImage.Category => MP4Util.ImageMapping[_parent.Type].ToString();

        string IMetadataImage.ImageType => ImageToMimeType();

        int IMetadataImage.Width { get { EnsureDimensions(); return _imagewidth; } }

        int IMetadataImage.Height { get { EnsureDimensions(); return _imageheight; } }

        int IMetadataImage.Size => _imagedata?.Length ?? Math.Max(0, _data.Length - 8);

        byte[] IMetadataImage.Data => ImageBytes;

        public string Hash
        {
            get;
            protected set;
        }

        void IMetadataImage.HashImage(HashAlgorithm hash)
        {
            // Hash straight over the payload slice of _data: materializing ImageBytes just to
            // hash it would copy every cover on every scan.
            Hash = Convert.ToBase64String(_imagedata != null
                ? hash.ComputeHash(_imagedata)
                : hash.ComputeHash(_data, 8, Math.Max(0, _data.Length - 8)));
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
            s.ReadExactly(_data, 8, (int)(fi.Length));
            s.Close();
            // The cached copy/dimensions (if any) describe the previous image.
            _imagedata = null;
            _dimsComputed = false;
        }

        // In-memory counterpart to LoadImage: the value bytes sit after the data atom's 8-byte
        // header (4-byte type + 4-byte reserved), which ResizeData(clear:true) zeroes.
        public void LoadImageBytes(byte[] imageData, DataTypes type)
        {
            ResizeData(imageData.Length + 8, true);
            DataType = type;
            Array.Copy(imageData, 0, _data, 8, imageData.Length);
            _imagedata = null;
            _dimsComputed = false;
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

        // Builds a fresh `free` padding atom of the given payload size, ready to be added to a
        // container. The caller propagates the size upward (adjust_free: false) so the padding is
        // not immediately re-absorbed. Used to reserve in-place edit room inside moov.
        public Atom_free(ContainerAtom ca, long payloadBytes)
            : base(ca)
        {
            Type = "free";
            _size = 8 + (ulong)payloadBytes;
            _headersize = 8;
            _deltasize = (long)_size;
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
        // Keep a single 64-bit representation in memory for both stco and co64.  Apart from the
        // entry width, the atoms have the same FullBox layout.  Sharing the list lets a full-file
        // rewrite change the serialized representation without replacing nodes in the atom tree.
        private readonly List<ulong> _offsets = new List<ulong>();

        public Atom_stco(Atom a, Stream s, bool init)
            : base(a, s)
        {
            if (init)
                ReadOffsets(s, 4);
        }

        public Atom_stco(Atom a, Stream s)
            : this(a, s, true)
        {
        }

        public override void WriteAtom(Stream s)
        {
            base.WriteAtom(s);
            WriteUint32(s, (uint)(_offsets.Count));
            if (Uses64BitOffsets)
            {
                foreach (ulong o in _offsets)
                    WriteUint64(s, o);
            }
            else
            {
                foreach (ulong o in _offsets)
                {
                    if (o > uint.MaxValue)
                        throw new InvalidOperationException("An MP4 stco offset no longer fits in 32 bits.");
                    WriteUint32(s, (uint)o);
                }
            }
        }

        internal bool Uses64BitOffsets => Type == "co64";

        internal bool AdjustedOffsetsFitUInt32(long delta)
        {
            foreach (ulong offset in _offsets)
            {
                Int128 value = (Int128)offset + delta;
                if (value < 0 || value > uint.MaxValue)
                    return false;
            }
            return true;
        }

        internal void Validate64BitOffsetAdjustment(long delta)
        {
            foreach (ulong offset in _offsets)
            {
                Int128 value = (Int128)offset + delta;
                if (value < 0 || value > ulong.MaxValue)
                    throw new InvalidOperationException(
                        "An MP4 chunk offset moved outside the valid unsigned 64-bit range.");
            }
        }

        // Changes only the table representation.  Offset values are adjusted later, after all
        // width changes have propagated to moov and the final mdat position is known.
        internal void SetUses64BitOffsets(bool use64Bit)
        {
            if (Uses64BitOffsets == use64Bit)
                return;

            long sizeDelta = checked(_offsets.Count * (use64Bit ? 4L : -4L));
            // Atom.NonRecursiveTouch predates extended-size support and cannot propagate the extra
            // eight header bytes through every ancestor.  Tables this large are impractical to
            // materialize here; reject that boundary instead of emitting inconsistent sizes.
            if (sizeDelta != 0)
            {
                // RootAtom is a tree sentinel rather than a serialized atom; stop before it.
                for (Atom atom = this; atom.ParentAtom != null; atom = atom.ParentAtom)
                    if (atom.WouldHeaderResizeAfter(sizeDelta))
                        throw new NotSupportedException(
                            "Changing an MP4 chunk-offset table would resize a 32/64-bit atom header.");
            }
            Type = use64Bit ? "co64" : "stco";
            Touch(sizeDelta, adjust_free: false);
        }

        protected void ReadOffsets(Stream s, int entryWidth)
        {
            uint count = ReadUint32(s);
            // Clamp to the bytes actually present in the atom and the stream (malformed
            // counts/sizes would otherwise over-allocate), then parse from one bulk read.
            long atomPayloadBytes = Math.Max((long)_size - (long)_headersize - 8, 0);
            long available = Math.Min(atomPayloadBytes / entryWidth, (s.Length - s.Position) / entryWidth);
            if (count > available)
                count = (uint)Math.Max(available, 0);

            byte[] buf = new byte[checked((int)((long)count * entryWidth))];
            s.ReadExactly(buf);
            _offsets.Capacity = (int)count;
            for (int i = 0; i < buf.Length; i += entryWidth)
            {
                ulong offset = 0;
                for (int j = 0; j < entryWidth; j++)
                    offset = (offset << 8) | buf[i + j];
                _offsets.Add(offset);
            }
        }

        public virtual void AdjustOffset(long delta)
        {
            ValidateFileOffsetAdjustment(delta);
            ulong[] adjusted = new ulong[_offsets.Count];
            for (int i = 0; i < _offsets.Count; i++)
            {
                Int128 value = (Int128)_offsets[i] + delta;
                adjusted[i] = (ulong)value;
            }

            Touch(0);
            for (int i = 0; i < _offsets.Count; i++)
                _offsets[i] = adjusted[i];
        }

        public override void FixFileOffsets(long delta)
        {
            AdjustOffset(delta);
        }

        public override void ValidateFileOffsetAdjustment(long delta)
        {
            Validate64BitOffsetAdjustment(delta);
            if (!Uses64BitOffsets && !AdjustedOffsetsFitUInt32(delta))
                throw new InvalidOperationException(
                    "An MP4 stco offset no longer fits in 32 bits; conversion to co64 is required.");
        }

    }

    public class Atom_co64 : Atom_stco
    {
        public Atom_co64(Atom a, Stream s)
            : base(a, s, false)
        {
            ReadOffsets(s, 8);
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
                if (MP4Util.AtomTypes.TryGetValue(sa.Type, out Type Atom_type) ||
                    MP4Util.AtomTypes.TryGetValue(Type + "." + sa.Type, out Atom_type))
                {
                    Atom sa2 = (Atom_type == typeof(Atom)) ? sa : MP4Util.CreateAtom(Atom_type, sa, s);
                    _children.Add(sa2);
                }
                else if (forced_subatom != null)
                {
                    Atom sa2 = MP4Util.CreateAtom(forced_subatom, sa, s);
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

        public override void ValidateFileOffsetAdjustment(long delta)
        {
            foreach (Atom a in _children)
                a.ValidateFileOffsetAdjustment(delta);
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

    public class RootAtom : ContainerAtom
    {
        public RootAtom(string path)
        {
            ReadFile(path);
        }

        private string _associatedpath;

        public string Path
        {
            get
            {
                return _associatedpath;
            }
        }

        public void ReadFile(string path)
        {
            using Stream s = Tools.OpenReadSequential(path);

            long length = s.Length;
            while (s.Position < length)
            {
                long pos = s.Position;

                Atom a = new Atom(s, this);
                if (MP4Util.AtomTypes.TryGetValue(a.Type, out Type Atom_type))
                {
                    a = (Atom_type == typeof(Atom)) ? a : MP4Util.CreateAtom(Atom_type, a, s);
                }
                else if (MP4Util.LoadData)
                    a = new DataAtom(a, s);
                Children.Add(a);

                s.Seek(pos + a.Size, SeekOrigin.Begin);
            }

            _associatedpath = path;
         }

        // Payload bytes reserved for the in-place edit pad seeded into moov on a full rewrite.
        // ~2KB covers typical text-tag churn; larger edits (artwork) simply fall back to a rewrite.
        private const long MoovPaddingBytes = 2048;

        public void WriteFile(string path)
        {
            // Since we're rebuilding the file anyway, reserve some free padding inside moov (for
            // faststart layouts that lack it) so the NEXT tag edit can be absorbed in place.
            EnsureMoovPadding();

            List<Atom_stco> chunkOffsetAtoms = EnumerateChunkOffsetAtoms(this).ToList();
            var widthChanges = new List<(Atom_stco Atom, bool PreviousWidth)>();
            List<(Atom Atom, long Offset)> committedOffsets = null;
            long offsetDelta = 0;
            bool offsetsAdjusted = false;
            string tpath = null;
            try
            {
                // Width changes alter moov and can therefore alter mdat's staged position. Promote
                // overflowing stco tables to co64 until the layout is stable, then opportunistically
                // demote each co64 whose values still fit after its own shrink is accounted for.
                SelectChunkOffsetWidths(chunkOffsetAtoms, widthChanges);
                (committedOffsets, offsetDelta) = CalculateStagedLayout();
                ValidateFileOffsetAdjustment(offsetDelta);
                if (offsetDelta != 0)
                {
                    FixFileOffsets(offsetDelta);
                    offsetsAdjusted = true;
                }

                tpath = Tools.CreateSiblingTempPath(path);
                using (FileStream s = new FileStream(tpath, FileMode.CreateNew, FileAccess.Write))
                {
                    WriteAtom(s);
                    s.Flush(flushToDisk: true);
                }

                Tools.AtomicReplace(tpath, path);
            }
            catch
            {
                // Restore widths before numeric offsets: a co64 table may have been demoted only
                // because the final negative delta made it fit, while a promoted stco may currently
                // contain values above uint.MaxValue. The original representation can safely hold
                // the values produced by reversing the adjustment.
                for (int i = widthChanges.Count - 1; i >= 0; i--)
                    widthChanges[i].Atom.SetUses64BitOffsets(widthChanges[i].PreviousWidth);
                if (offsetsAdjusted)
                    FixFileOffsets(-offsetDelta);
                foreach (DemandAtom demand in _children.OfType<DemandAtom>())
                    demand.AbortRewrite();
                if (tpath != null)
                    Tools.DeleteIfExists(tpath);
                throw;
            }

            string committedPath = System.IO.Path.GetFullPath(path);
            foreach (DemandAtom demand in _children.OfType<DemandAtom>())
                demand.CommitRewrite(committedPath);
            foreach (var (atom, offset) in committedOffsets)
                atom.CommitFileOffset(offset);
            _associatedpath = path;
            Untouch();
        }

        private (List<(Atom Atom, long Offset)> AtomOffsets, long MediaDelta) CalculateStagedLayout()
        {
            // All mdat atoms may share one global stco/co64 adjustment only when they move by the
            // same amount. If metadata sits between multiple mdats, selective table remapping is
            // required; rejecting that layout is safer than corrupting a subset of the chunks.
            var atomOffsets = new List<(Atom Atom, long Offset)>();
            var mediaDeltas = new HashSet<long>();
            long stagedOffset = 0;
            foreach (Atom atom in _children)
            {
                atomOffsets.Add((atom, stagedOffset));
                if (atom is DemandAtom)
                {
                    if (atom.FileOffset < 0)
                        throw new InvalidOperationException("Cannot relocate an MP4 mdat with no source offset.");
                    mediaDeltas.Add(stagedOffset - atom.FileOffset);
                }
                stagedOffset = checked(stagedOffset + atom.Size);
            }

            if (mediaDeltas.Count > 1)
                throw new NotSupportedException(
                    "This MP4 has multiple mdat atoms that would move by different amounts; selective chunk-offset remapping is required.");

            return (atomOffsets, mediaDeltas.Count == 0 ? 0 : mediaDeltas.Single());
        }

        private void SelectChunkOffsetWidths(
            IReadOnlyList<Atom_stco> chunkOffsetAtoms,
            ICollection<(Atom_stco Atom, bool PreviousWidth)> widthChanges)
        {
            // Promote one table at a time and recalculate. Growing moov can move mdat farther and
            // make another table overflow too, so a single pass is insufficient.
            while (true)
            {
                long mediaDelta = CalculateStagedLayout().MediaDelta;
                Atom_stco toPromote = null;
                foreach (Atom_stco atom in chunkOffsetAtoms)
                {
                    atom.Validate64BitOffsetAdjustment(mediaDelta);
                    if (!atom.Uses64BitOffsets && !atom.AdjustedOffsetsFitUInt32(mediaDelta))
                    {
                        toPromote = atom;
                        break;
                    }
                }

                if (toPromote == null)
                    break;
                widthChanges.Add((toPromote, false));
                toPromote.SetUses64BitOffsets(true);
            }

            // A demotion is retained only if the layout produced by the smaller table leaves every
            // chunk table valid in its selected representation. Start over after every success:
            // one table's shrink can make an earlier table eligible on the next pass.
            while (true)
            {
                bool acceptedDemotion = false;
                foreach (Atom_stco atom in chunkOffsetAtoms)
                {
                    if (!atom.Uses64BitOffsets)
                        continue;

                    bool demoted = false;
                    try
                    {
                        // Do not require a pre-demotion fit: shrinking this table may itself move
                        // mdat backward far enough to bring a near-boundary value below uint.Max.
                        atom.SetUses64BitOffsets(false);
                        demoted = true;
                        long candidateDelta = CalculateStagedLayout().MediaDelta;
                        foreach (Atom_stco candidate in chunkOffsetAtoms)
                        {
                            candidate.Validate64BitOffsetAdjustment(candidateDelta);
                            if (!candidate.Uses64BitOffsets && !candidate.AdjustedOffsetsFitUInt32(candidateDelta))
                                throw new InvalidOperationException(
                                    "The candidate stco layout does not fit in 32-bit chunk offsets.");
                        }

                        widthChanges.Add((atom, true));
                        acceptedDemotion = true;
                        break;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException)
                    {
                        if (demoted)
                            atom.SetUses64BitOffsets(true);
                    }
                }

                if (!acceptedDemotion)
                    break;
            }
        }

        private static IEnumerable<Atom_stco> EnumerateChunkOffsetAtoms(ContainerAtom container)
        {
            foreach (Atom atom in container.Children)
            {
                if (atom is Atom_stco chunkOffsets)
                    yield return chunkOffsets;
                if (atom is ContainerAtom childContainer)
                    foreach (Atom_stco nested in EnumerateChunkOffsetAtoms(childContainer))
                        yield return nested;
            }
        }

        // Reserves a `free` padding atom inside moov so a later tag edit can be absorbed in place
        // (Path A) rather than copying the whole audio payload again. Only meaningful — and only
        // applied — for faststart files (moov before mdat) that don't already carry padding:
        //  - moov-last files already get full in-place coverage from Path B, so padding them would
        //    just bloat every file for no gain;
        //  - files that already have ample free space in moov (e.g. iTunes-authored) are left alone.
        // The pad goes in udta (alongside meta), where Touch's free-adjust will find it. No-op if
        // there's no udta to hold it (e.g. an untagged file).
        private void EnsureMoovPadding()
        {
            int moovIdx = -1, mdatIdx = -1;
            for (int i = 0; i < _children.Count; i++)
            {
                if (moovIdx < 0 && _children[i].Type == "moov") moovIdx = i;
                if (mdatIdx < 0 && _children[i].Type == "mdat") mdatIdx = i;
            }
            if (moovIdx < 0 || mdatIdx < 0 || moovIdx > mdatIdx)
                return; // no moov/mdat, or moov already trails mdat (Path B territory)

            if (_children[moovIdx] is not ContainerAtom moov)
                return;
            if (moov.ComputeFreeSpace() >= MoovPaddingBytes)
                return; // already has room to absorb edits in place
            if (moov.FindPath("udta") is not ContainerAtom udta)
                return;

            Atom_free pad = new Atom_free(udta, MoovPaddingBytes - 8);
            udta.Children.Add(pad);
            // Propagate the pad's size up to moov WITHOUT triggering free-absorption (which would
            // immediately soak the pad back into itself).
            udta.Touch(pad.Size, false);
        }

        // Persists tag edits without copying the (large) audio payload, but ONLY when it can prove
        // the audio bytes cannot move. Returns false — caller must fall back to WriteFile — in every
        // other case. Two safe layouts:
        //
        //   Path B  moov is the last top-level atom. Its size may change freely: mdat precedes it, so
        //           the audio and every stco/co64 chunk offset (which point into mdat) stay valid. We
        //           rewrite only moov's byte range and truncate/extend at EOF.
        //   Path A  no top-level atom changed size (a `free` atom absorbed the delta). Every offset is
        //           preserved, so we overwrite each changed atom over its original range in place.
        //
        // Bails when audio would shift, an original offset is unknown, or the trailing atom's size
        // field would grow/shrink (32<->64 bit). stco/co64 are never adjusted here: in both paths the
        // audio does not move, so leaving FixFileOffsets to the WriteFile fallback is correct.
        public bool TrySaveInPlace()
        {
            if (_associatedpath == null || !File.Exists(_associatedpath) || _children.Count == 0)
                return false;

            // Every top-level atom must know where it came from (all parsed atoms do).
            foreach (Atom a in _children)
                if (a.FileOffset < 0)
                    return false;

            Atom last = _children[_children.Count - 1];

            // Look at which top-level atoms before the trailing one changed size. Any such change
            // shifts the file offset of everything after it (including the audio), which an in-place
            // write can't accommodate — UNLESS the changed atom is a `free` padding atom that Touch
            // grew/shrank to absorb the edit. free is not real data, so we can reclaim it (restore
            // it to its on-disk size) and let the whole delta fall on the trailing atom instead.
            bool anyBeforeLastChanged = false;
            bool beforeLastAllFree = true;
            for (int i = 0; i < _children.Count - 1; i++)
            {
                if (_children[i].DeltaSize == 0)
                    continue;
                anyBeforeLastChanged = true;
                if (!(_children[i] is Atom_free))
                    beforeLastAllFree = false;
            }

            // Path A: nothing moved (e.g. a free atom *inside* moov absorbed the delta, so moov's own
            //         size is unchanged) — overwrite each changed atom over its original range.
            // Path B: only the trailing atom (moov, after mdat) needs to resize, once any pre-mdat
            //         free padding is reclaimed — rewrite its range and truncate/extend at EOF;
            //         mdat and its chunk offsets are untouched.
            bool pathA = !anyBeforeLastChanged && last.DeltaSize == 0;
            bool pathB = last.DeltaSize != 0 && !last.WouldHeaderResize
                         && (!anyBeforeLastChanged || beforeLastAllFree);

            if (!pathA && !pathB)
                return false;

            // Commit the reclaim only now that Path B is certain, so a fallback to WriteFile never
            // sees a half-adjusted tree.
            if (pathB && anyBeforeLastChanged)
            {
                for (int i = 0; i < _children.Count - 1; i++)
                    if (_children[i] is Atom_free && _children[i].DeltaSize != 0)
                        _children[i].NonRecursiveTouch(-_children[i].DeltaSize);
            }

            using FileStream s = new FileStream(_associatedpath, FileMode.Open, FileAccess.ReadWrite);
            foreach (Atom a in _children)
            {
                bool changed = a.Touched || (a is ContainerAtom c && c.Modified);
                if (!changed)
                    continue;
                s.Seek(a.FileOffset, SeekOrigin.Begin);
                a.WriteAtom(s);
            }
            // Path B may have shrunk the tail (leaving stale bytes) or grown it; the valid file ends
            // exactly where the last write finished. Path A preserves length, so this is a no-op there.
            if (pathB)
                s.SetLength(s.Position);

            Untouch();
            return true;
        }

        public override void WriteAtom(Stream s)
        {
            foreach (Atom a in _children)
                a.WriteAtom(s);
        }

    }

    public class MP4File : TagBase, ICodecProvider, IMediaFile, IMetadataWriter, IArtworkWriter
    {
  
        #region IMetadataProvider Properties

        public override IEnumerable<KeyValuePair<TagFields, string>> GetKnownMetadata()
        {
            // A file without an iTunes metadata list (common for non-iTunes muxers) has no
            // known tags; don't dereference a null ilst. (SetField already guards this.)
            Atom_ilst ilst = root_.FindPath("moov.udta.meta.ilst") as Atom_ilst;
            if (ilst == null)
                yield break;
            foreach (Atom atom in ilst.Children)
            {
                ContainerAtom ca = atom as ContainerAtom;
                if (MP4Util.TagMapping.ContainsKey(atom.Type))
                {
                    var key = MP4Util.TagMapping[atom.Type];
                    foreach (Atom childatom in ca.FindMultiplePath("data"))
                    {
                        Atom_data da = childatom as Atom_data;
                        if (da.IsText)
                            yield return KeyValuePair.Create(key, da.Text);
                        // Only emit integers of a recognized width; a non-standard width would
                        // make the Uint64 accessor throw and abort the whole enumeration.
                        else if (da.DataType == Atom_data.DataTypes.Integer && (da.IsUint8 || da.IsUint16 || da.IsUint32 || da.IsUint64))
                            yield return KeyValuePair.Create(key, da.Uint64.ToString());
                        else if (da.IsBoolean)
                            yield return KeyValuePair.Create(key, da.BoolValue ? "1" : "0");
                        else if (da.IsEnumeratedGenre)
                        {
                            foreach (var g in da.EnumeratedGenres)
                                yield return KeyValuePair.Create(key, g);
                        }
                        else if (da.IsRating)
                            yield return KeyValuePair.Create(key, da.Rating.ToString());
                    }
                }
                else if (MP4Util.SpecialMapping.ContainsKey(atom.Type))
                {
                    foreach (var kv in MP4Util.SpecialMapping[atom.Type](ca))
                        yield return kv;
                }
                else if (atom.Type == "----")
                {
                    string keystr = (ca.FindPath("name") as StringAtom).Text;
                    if (MP4Util.TagMapping.ContainsKey(keystr))
                    {
                        var key = MP4Util.TagMapping[keystr];
                        foreach (Atom childatom in (atom as ContainerAtom).FindMultiplePath("data"))
                        {
                            Atom_data da = childatom as Atom_data;
                            if (da.IsText)
                                yield return KeyValuePair.Create(key, da.Text);
                        }
                    }

                }
            }

            yield break;
        }

        public override IEnumerable<KeyValuePair<string, string>> GetTextMetadata()
        {
            foreach (var field in GetKnownMetadata())
                yield return KeyValuePair.Create(field.Key.ToString(), field.Value);
        }

        public override IEnumerable<IMetadataImage> GetImageMetadata()
        {
            Atom_ilst ilst = root_.FindPath("moov.udta.meta.ilst") as Atom_ilst;
            if (ilst == null)
                yield break;
            foreach (Atom atom in ilst.Children)
            {
                ContainerAtom ca = atom as ContainerAtom;
                if (MP4Util.ImageMapping.ContainsKey(ca.Type))
                {
                    var mapping = MP4Util.ImageMapping[ca.Type];
                    foreach (Atom childatom in ca.FindMultiplePath("data"))
                    {
                        Atom_data da = childatom as Atom_data;
                        if (da.IsImage)
                            yield return da;
                    }
                }
            }
            yield break;
        }

        public override string TagType => "MP4";

        #endregion

        public IEnumerable<ICodecProvider> Codecs
        {
            get
            {
                yield return this;
            }
        }

        public IEnumerable<IMetadataProvider> Tags
        {
            get
            {
                yield return this;
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

        private RootAtom root_;

        public RootAtom Root
        {
            get { return root_; }
        }

        public MP4File(string filename)
        {
            root_ = new RootAtom(filename);
            ParseCodecInfo();
            Atom_mvhd mvhd = root_.FindPath("moov.mvhd") as Atom_mvhd;
            DurationInFrames = mvhd.DurationInFrames;
            ParseStandardFields();
        }

        protected void ParseCodecInfo()
        {
            ContainerAtom stsd = root_.FindPath("moov.trak.mdia.minf.stbl.stsd") as ContainerAtom;
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

        public void SetField(TagFields field, string value)
        {
            Atom_ilst ilst = root_.FindPath("moov.udta.meta.ilst") as Atom_ilst
                ?? throw new InvalidOperationException("No ilst atom found.");

            // trkn: TrackNumber / TotalTracks stored as binary in Atom_data
            if (field == TagFields.TrackNumber || field == TagFields.TotalTracks)
            {
                Atom_data da = GetOrCreateTrackDiscDataAtom(ilst, "trkn");
                if (field == TagFields.TrackNumber)
                    da.TrackNumber = (value == null) ? 0 : uint.Parse(value);
                else
                    da.TotalTracks = (value == null) ? 0 : uint.Parse(value);
                return;
            }
            if (field == TagFields.DiscNumber || field == TagFields.TotalDiscs)
            {
                Atom_data da = GetOrCreateTrackDiscDataAtom(ilst, "disk");
                if (field == TagFields.DiscNumber)
                    da.DiscNumber = (value == null) ? 0 : uint.Parse(value);
                else
                    da.TotalDiscs = (value == null) ? 0 : uint.Parse(value);
                return;
            }

            if (!MP4Util.ReverseTagMapping.TryGetValue(field, out string atomKey))
                throw new ArgumentException($"Unsupported tag field for MP4: {field}");

            bool isStandard = false;
            try { isStandard = MP4Util.TypeEncoding.GetBytes(atomKey).Length == 4; }
            catch { }

            if (isStandard)
            {
                if (value == null)
                {
                    var toRemove = ilst.Children.FirstOrDefault(a => a.Type == atomKey);
                    if (toRemove != null) { ilst.Children.Remove(toRemove); ilst.Touch(-(long)toRemove.Size); }
                    return;
                }
                GetOrCreateStandardDataAtom(ilst, atomKey).Text = value;
            }
            else
            {
                if (value == null)
                {
                    var toRemove = ilst.Children.FirstOrDefault(a =>
                        a.Type == "----" && (a as ContainerAtom)?.FindPath("name") is StringAtom sa && sa.Text == atomKey);
                    if (toRemove != null) { ilst.Children.Remove(toRemove); ilst.Touch(-(long)toRemove.Size); }
                    return;
                }
                GetOrCreateFreeformDataAtom(ilst, atomKey).Text = value;
            }
        }

        public void RemoveField(TagFields field)
        {
            Atom_ilst ilst = root_.FindPath("moov.udta.meta.ilst") as Atom_ilst
                ?? throw new InvalidOperationException("No ilst atom found.");

            if (field == TagFields.TrackNumber || field == TagFields.TotalTracks)
            {
                var existing = ilst.Children.FirstOrDefault(a => a.Type == "trkn") as ContainerAtom;
                if (existing == null) return;
                var da = existing.FindPath("data") as Atom_data;
                if (field == TagFields.TrackNumber) da.TrackNumber = 0; else da.TotalTracks = 0;
                if (da.TrackNumber == 0 && da.TotalTracks == 0)
                    { ilst.Children.Remove(existing); ilst.Touch(-(long)existing.Size); }
                return;
            }
            if (field == TagFields.DiscNumber || field == TagFields.TotalDiscs)
            {
                var existing = ilst.Children.FirstOrDefault(a => a.Type == "disk") as ContainerAtom;
                if (existing == null) return;
                var da = existing.FindPath("data") as Atom_data;
                if (field == TagFields.DiscNumber) da.DiscNumber = 0; else da.TotalDiscs = 0;
                if (da.DiscNumber == 0 && da.TotalDiscs == 0)
                    { ilst.Children.Remove(existing); ilst.Touch(-(long)existing.Size); }
                return;
            }

            SetField(field, null);
        }

        // IArtworkWriter: write the cover into the 'covr' atom under ilst, creating it if absent
        // (same pattern as the text fields above). The atom tree resizes/reflows on Touch.
        public void SetFrontCover(byte[] imageData, string mimeType)
        {
            if (imageData == null || imageData.Length == 0)
            {
                RemoveImages();
                return;
            }
            Atom_ilst ilst = root_.FindPath("moov.udta.meta.ilst") as Atom_ilst
                ?? throw new InvalidOperationException("No ilst atom found; cannot add artwork.");

            // Replacing the cover: keep a single image, so drop any extra covr atoms first.
            var extra = ilst.Children.Where(a => a.Type == "covr").Skip(1).ToList();
            foreach (var c in extra) { ilst.Children.Remove(c); ilst.Touch(-(long)c.Size); }

            var da = GetOrCreateStandardDataAtom(ilst, "covr");
            da.LoadImageBytes(imageData, MimeToDataType(mimeType));
        }

        public void RemoveImages()
        {
            if (root_.FindPath("moov.udta.meta.ilst") is not Atom_ilst ilst)
                return;
            var covrs = ilst.Children.Where(a => a.Type == "covr").ToList();
            foreach (var c in covrs) { ilst.Children.Remove(c); ilst.Touch(-(long)c.Size); }
        }

        public void SetImages(IReadOnlyList<ArtworkImage> images)
        {
            Atom_ilst ilst = root_.FindPath("moov.udta.meta.ilst") as Atom_ilst
                ?? throw new InvalidOperationException("No ilst atom found; cannot add artwork.");
            RemoveImages();
            if (images.Count == 0)
                return;

            // MP4 has no per-image picture type; store every image as a data atom under one covr.
            ContainerAtom covr = ilst.CreateChild("covr") as ContainerAtom;
            foreach (var img in images)
            {
                Atom_data da = new Atom_data(covr) { Type = "data" };
                covr.Children.Add(da);
                covr.Touch((long)da.Size);
                da.LoadImageBytes(img.Data, MimeToDataType(img.MimeType));
            }
        }

        private static Atom_data.DataTypes MimeToDataType(string mimeType) => (mimeType ?? "").ToLowerInvariant() switch
        {
            "image/png" => Atom_data.DataTypes.PNG,
            "image/gif" => Atom_data.DataTypes.GIF,
            "image/bmp" => Atom_data.DataTypes.BMP,
            _ => Atom_data.DataTypes.JPEG,
        };

        private Atom_data GetOrCreateStandardDataAtom(Atom_ilst ilst, string atomType)
        {
            var existing = ilst.Children.FirstOrDefault(a => a.Type == atomType) as ContainerAtom;
            if (existing != null)
                return existing.FindPath("data") as Atom_data;

            ContainerAtom ca = ilst.CreateChild(atomType) as ContainerAtom;
            Atom_data da = new Atom_data(ca) { Type = "data" };
            ca.Children.Add(da);
            ca.Touch((long)da.Size);
            return da;
        }

        private Atom_data GetOrCreateTrackDiscDataAtom(Atom_ilst ilst, string atomType)
        {
            var existing = ilst.Children.FirstOrDefault(a => a.Type == atomType) as ContainerAtom;
            if (existing != null)
                return existing.FindPath("data") as Atom_data;

            ContainerAtom ca = ilst.CreateChild(atomType) as ContainerAtom;
            Atom_data da = new Atom_data(ca) { Type = "data" };
            ca.Children.Add(da);
            ca.Touch((long)da.Size);
            return da;
        }

        private Atom_data GetOrCreateFreeformDataAtom(Atom_ilst ilst, string key)
        {
            var existing = ilst.Children
                .Where(a => a.Type == "----")
                .Select(a => a as ContainerAtom)
                .FirstOrDefault(ca => (ca?.FindPath("name") as StringAtom)?.Text == key);
            if (existing != null)
                return existing.FindPath("data") as Atom_data;

            ContainerAtom freeform = ilst.CreateChild("----") as ContainerAtom;

            StringAtom mean = new StringAtom(freeform) { Type = "mean" };
            freeform.Children.Add(mean);
            freeform.Touch((long)mean.Size);
            mean.Text = "com.apple.iTunes";

            StringAtom name = new StringAtom(freeform) { Type = "name" };
            freeform.Children.Add(name);
            freeform.Touch((long)name.Size);
            name.Text = key;

            Atom_data da = new Atom_data(freeform) { Type = "data" };
            freeform.Children.Add(da);
            freeform.Touch((long)da.Size);
            return da;
        }

        public void SaveTags(string outputPath = null) => Save(outputPath);

        // Test-only: records whether the most recent Save() took the in-place fast path (true) or
        // the full-rewrite fallback (false). Lets tests assert the perf win actually fired instead
        // of silently regressing to a whole-file copy.
        internal bool LastSaveWasInPlace { get; private set; }

        public void Save(string outputPath = null)
        {
            // Fast path: overwrite an existing file in place without copying the audio payload when
            // the layout makes it provably safe (see RootAtom.TrySaveInPlace). Otherwise rebuild.
            if (outputPath == null && root_.TrySaveInPlace())
            {
                LastSaveWasInPlace = true;
                return;
            }
            LastSaveWasInPlace = false;
            root_.WriteFile(outputPath ?? root_.Path);
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
            get;
            private set;
        }

        public uint AverageBitrate
        {
            get;
            private set;
        }

        public Atom_mp4a_esds(Atom a, Stream s)
            : base(a, s)
        {
            MaxBitrate = AverageBitrate = 0;
            int offset = 4;
            if (_data[offset++] != 3)
                return;
            while ((_data[offset] == 0x80) || (_data[offset] == 0x81) || (_data[offset] == 0xfe))
                offset++;
            offset += 4;
            if (_data[offset++] != 4)
                return;
            while ((_data[offset] == 0x80) || (_data[offset] == 0x81) || (_data[offset] == 0xfe))
                offset++;
            MaxBitrate = Uint32At(offset + 6);
            AverageBitrate = Uint32At(offset + 10);
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
                // 28 bytes consumed above (version + 24-byte ALACSpecificConfig). The offset
                // passed here must match, otherwise InitChildren misreads a bogus child atom
                // out of the following sibling's bytes and corrupts the file on rewrite.
                InitChildren(s, null, 28);
            }
            else
            {
                _data = new byte[_size - _headersize - 4];
                s.ReadExactly(_data, 0, _data.Length);
            }
        }

        public Atom_alac(ContainerAtom ca)
            : base(ca)
        {

        }

        public override void WriteAtom(Stream s)
        {
            WriteHeader(s);
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
            else
                s.Write(_data, 0, _data.Length);
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
            s.ReadExactly(_reserved0, 0, 6);
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
                s.ReadExactly(_data);
            }
        }

        public CodecAtom(ContainerAtom ca)
            : base(ca)
        {
        }

        public override void WriteAtom(Stream s)
        {
            WriteHeader(s);
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
