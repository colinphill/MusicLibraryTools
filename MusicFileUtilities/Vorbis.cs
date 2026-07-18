using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Runtime.CompilerServices;

namespace MusicFileUtilities
{

    public class VorbisUtil
    {

        public static Dictionary<string, TagFields> TagMappings = new Dictionary<string, TagFields>()
        {
            { "ACOUSTID_ID", TagFields.AcoustID_ID },
            { "ACOUSTID_FINGERPRINT", TagFields.AcoustID_Fingerprint },
            { "ALBUM", TagFields.Album },
            { "ALBUMARTIST", TagFields.AlbumArtist },
            { "ALBUMARTISTSORT", TagFields.AlbumArtistSort },
            { "ALBUMSORT", TagFields.AlbumSort },
            { "ARRANGER", TagFields.Arranger },
            { "ARTIST", TagFields.Artist },
            { "ARTISTSORT", TagFields.ArtistSort },
            { "ARTISTS", TagFields.Artists },
            { "ASIN", TagFields.ASIN },
            { "BARCODE", TagFields.Barcode },
            { "BPM", TagFields.BPM },
            { "CATALOGNUMBER", TagFields.CatalogNumber },
            { "COMMENT", TagFields.Comment },
            { "COMPILATION", TagFields.Compilation },
            { "COMPOSER", TagFields.Composer },
            { "COMPOSERSORT", TagFields.ComposerSort },
            { "CONDUCTOR", TagFields.Conductor },
            { "COPYRIGHT", TagFields.Copyright },
            { "DISCNUMBER", TagFields.DiscNumber },
            { "DISCSUBTITLE", TagFields.DiscSubtitle },
            { "ENCODEDBY", TagFields.EncodedBy },
            { "ENCODERSETTINGS", TagFields.EncoderSettings },
            { "ENGINEER", TagFields.Engineer },
            { "GENRE", TagFields.Genre },
            { "GROUPING", TagFields.Grouping },
            { "KEY", TagFields.Key },
            { "ISRC", TagFields.ISRC },
            { "LANGUAGE", TagFields.Language },
            { "LICENSE", TagFields.License },
            { "LYRICIST", TagFields.Lyricist },
            { "LYRICS", TagFields.Lyrics },
            { "MEDIA", TagFields.Media },
            { "DJMIXER", TagFields.DJMixer },
            { "MIXER", TagFields.Mixer },
            { "MOOD", TagFields.Mood },
            { "MOVEMENTNAME", TagFields.Movement },
            { "MOVEMENTTOTAL", TagFields.MovementTotal },
            { "MOVEMENT", TagFields.MovementNumber },
            { "MUSICBRAINZ_ARTISTID", TagFields.MusicBrainz_ArtistID },
            { "MUSICBRAINZ_DISCID", TagFields.MusicBrainz_DiscID },
            { "MUSICBRAINZ_ORIGINALARTISTID", TagFields.MusicBrainz_OriginalArtistID },
            { "MUSICBRAINZ_ORIGINALALBUMID", TagFields.MusicBrainz_OriginalAlbumID },
            { "MUSICBRAINZ_TRACKID", TagFields.MusicBrainz_RecordingID },
            { "MUSICBRAINZ_ALBUMARTISTID", TagFields.MusicBrainz_AlbumArtistID },
            { "MUSICBRAINZ_RELEASEGROUPID", TagFields.MusicBrainz_ReleaseGroupID },
            { "MUSICBRAINZ_ALBUMID", TagFields.MusicBrainz_AlbumID },
            { "MUSICBRAINZ_RELEASETRACKID", TagFields.MusicBrainz_TrackID },
            { "MUSICBRAINZ_WORKID", TagFields.MusicBrainz_WorkID },
            { "ORIGINALFILENAME", TagFields.OriginalFileName },
            { "ORIGINALDATE", TagFields.OriginalDate },
            { "ORIGINALYEAR", TagFields.OriginalYear },
            { "PERFORMER", TagFields.Performer },
            { "PRODUCER", TagFields.Producer },
            { "RATING", TagFields.Rating },
            { "LABEL", TagFields.Label },
            { "RELEASECOUNTRY", TagFields.ReleaseCountry },
            { "DATE", TagFields.Date },
            { "RELEASESTATUS", TagFields.ReleaseStatus },
            { "RELEASETYPE", TagFields.ReleaseType },
            { "REMIXER", TagFields.Remixer },
            { "REPLAYGAIN_ALBUM_GAIN", TagFields.ReplayGain_Album_Gain },
            { "REPLAYGAIN_ALBUM_PEAK", TagFields.ReplayGain_Album_Peak },
            { "REPLAYGAIN_ALBUM_RANGE", TagFields.ReplayGain_Album_Range },
            { "REPLAYGAIN_REFERENCE_LOUDNESS", TagFields.ReplayGain_Reference_Loudness },
            { "REPLAYGAIN_TRACK_GAIN", TagFields.ReplayGain_Track_Gain },
            { "REPLAYGAIN_TRACK_PEAK", TagFields.ReplayGain_Track_Peak },
            { "REPLAYGAIN_TRACK_RANGE", TagFields.ReplayGain_Track_Range },
            { "SCRIPT", TagFields.Script },
            { "SHOWMOVEMENT", TagFields.ShowMovement },
            { "DISCTOTAL", TagFields.TotalDiscs },
            { "TOTALDISCS", TagFields.TotalDiscs },
            { "TRACKTOTAL", TagFields.TotalTracks },
            { "TOTALTRACKS", TagFields.TotalTracks },
            { "TRACKNUMBER", TagFields.TrackNumber },
            { "TITLE", TagFields.Title },
            { "TITLESORT", TagFields.TitleSort },
            { "WEBSITE", TagFields.Website },
            { "WORK", TagFields.Work },
            { "WRITER", TagFields.Writer },
        };

        // Lazily built once (thread-safe); the old null-check pattern could race under
        // parallel writes.
        private static readonly Lazy<Dictionary<TagFields, string>> _reverseTagMappings =
            new Lazy<Dictionary<TagFields, string>>(() =>
            {
                var map = new Dictionary<TagFields, string>();
                foreach (var kv in TagMappings)
                    if (!map.ContainsKey(kv.Value))
                        map[kv.Value] = kv.Key;
                return map;
            });

        public static Dictionary<TagFields, string> ReverseTagMappings => _reverseTagMappings.Value;

    }

    [Serializable]
    public class VorbisArtwork : IMetadataImage
    {
        public ID3v2Util.APICType PictureType;
        public string MimeType;
        public string Description;
        public int Width;
        public int Height;
        public int Depth;
        public int ColorsUsed;

        // The image payload is materialized lazily from the source block (which FLAC retains
        // anyway for its save-time comparison); the eager copy doubled the memory traffic of
        // every embedded picture on a scan even when nothing looked at the image.
        private byte[] _data;
        private byte[] _source;
        private int _sourceoffset;
        private int _sourcesize;

        public byte[] Data
        {
            get
            {
                if (_data == null && _source != null)
                {
                    _data = new byte[_sourcesize];
                    Array.Copy(_source, _sourceoffset, _data, 0, _sourcesize);
                    _source = null;
                }
                return _data;
            }
            set
            {
                _data = value;
                _source = null;
            }
        }

        string IMetadataImage.Description => string.IsNullOrWhiteSpace(Description) ? PictureType.ToString() : Description;
        string IMetadataImage.Category => PictureType.ToString();
        string IMetadataImage.ImageType => MimeType;
        int IMetadataImage.Width => Width;
        int IMetadataImage.Height => Height;
        int IMetadataImage.Size => _data?.Length ?? (_source != null ? _sourcesize : 0);
        byte[] IMetadataImage.Data => Data;

        public string Hash
        {
            get;
            protected set;
        }

        public void HashImage(System.Security.Cryptography.HashAlgorithm hash)
        {
            // Hash straight over the source block slice: materializing Data just to hash it
            // would copy every cover on every scan.
            Hash = Convert.ToBase64String(_data == null && _source != null
                ? hash.ComputeHash(_source, _sourceoffset, _sourcesize)
                : hash.ComputeHash(Data));
        }

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
            // Lengths come straight from file bytes; validate every read so a corrupt block
            // throws a clean InvalidDataException instead of IndexOutOfRange/OutOfMemory.
            void Need(long n)
            {
                if (n < 0 || offset + n > b.Length)
                    throw new InvalidDataException("Malformed Vorbis picture block");
            }

            Need(4);
            PictureType = (ID3v2Util.APICType)Tools.Int32AtBE(b, offset);
            offset += 4;

            Need(4);
            int mimelen = Tools.Int32AtBE(b, offset);
            offset += 4;

            Need(mimelen);
            MimeType = Encoding.UTF8.GetString(b, offset, mimelen);
            offset += mimelen;

            Need(4);
            int desclen = Tools.Int32AtBE(b, offset);
            offset += 4;

            Need(desclen);
            Description = Encoding.UTF8.GetString(b, offset, desclen);
            offset += desclen;

            Need(4);
            Width = Tools.Int32AtBE(b, offset);
            offset += 4;

            Need(4);
            Height = Tools.Int32AtBE(b, offset);
            offset += 4;

            Need(4);
            Depth = Tools.Int32AtBE(b, offset);
            offset += 4;

            Need(4);
            ColorsUsed = Tools.Int32AtBE(b, offset);
            offset += 4;

            Need(4);
            int datasize = Tools.Int32AtBE(b, offset);
            offset += 4;

            Need(datasize);
            _data = null;
            _source = b;
            _sourceoffset = offset;
            _sourcesize = datasize;
        }

        public VorbisArtwork(byte[] b)
        {
            FromByteArray(b);
        }

    }

    [Serializable]
    public class VorbisComments : TagBase, IArtworkWriter, IUserStringMetadata
    {
        // IArtworkWriter: replace the front cover in the PICTURE list, probing dimensions the same
        // way the reader does. FLAC/Ogg serialize Artworks on save (see FLAC.SaveTags / ToByteArray).
        public void SetFrontCover(byte[] imageData, string mimeType)
        {
            if (imageData == null || imageData.Length == 0)
            {
                RemoveImages();
                return;
            }
            Artworks.RemoveAll(a => a.PictureType == ID3v2Util.APICType.FrontCover);
            var (w, h) = ImageFile.GetImageDimensions(imageData);
            Artworks.Add(new VorbisArtwork
            {
                PictureType = ID3v2Util.APICType.FrontCover,
                MimeType = mimeType,
                Description = "",
                Width = w,
                Height = h,
                Depth = 0,
                ColorsUsed = 0,
                Data = imageData,
            });
        }

        public void RemoveImages() => Artworks.Clear();

        public void SetImages(IReadOnlyList<ArtworkImage> images)
        {
            Artworks.Clear();
            foreach (var img in images)
            {
                var (w, h) = ImageFile.GetImageDimensions(img.Data);
                Artworks.Add(new VorbisArtwork
                {
                    PictureType = img.Type,
                    MimeType = img.MimeType,
                    Description = img.Description ?? "",
                    Width = w,
                    Height = h,
                    Depth = 0,
                    ColorsUsed = 0,
                    Data = img.Data,
                });
            }
        }

        #region IMetadataProvider Properties

        public override IEnumerable<KeyValuePair<string, string>> GetTextMetadata()
        {
            foreach (var field in GetKnownMetadata())
                yield return KeyValuePair.Create(field.Key.ToString(), field.Value);
        }

        public IEnumerable<KeyValuePair<string, string>> GetUserStrings()
        {
            foreach (var comment in Comments)
                if (!VorbisUtil.TagMappings.ContainsKey(comment.Key))
                    yield return comment;
        }

        public override IEnumerable<KeyValuePair<TagFields, string>> GetKnownMetadata()
        {
            foreach (var kv in Comments)
                if (VorbisUtil.TagMappings.ContainsKey(kv.Key))
                {
                    var tag = VorbisUtil.TagMappings[kv.Key];
                    if (tag == TagFields.TrackNumber)
                    {
                        var s = kv.Value.Split('/');
                        yield return KeyValuePair.Create(tag, s[0]);
                        if (s.Length > 1)
                            yield return KeyValuePair.Create(TagFields.TotalTracks, s[1]);
                        
                    }
                    else if (tag == TagFields.DiscNumber)
                    {
                        var s = kv.Value.Split('/');
                        yield return KeyValuePair.Create(tag, s[0]);
                        if (s.Length > 1)
                            yield return KeyValuePair.Create(TagFields.TotalDiscs, s[1]);
                    }
                    else if (tag == TagFields.MovementNumber)
                    {
                        var s = kv.Value.Split('/');
                        yield return KeyValuePair.Create(tag, s[0]);
                        if (s.Length > 1)
                            yield return KeyValuePair.Create(TagFields.MovementTotal, s[1]);
                    }
                    else
                        yield return KeyValuePair.Create(VorbisUtil.TagMappings[kv.Key], kv.Value);
                }
        }

        public override IEnumerable<IMetadataImage> GetImageMetadata()
        {
            foreach (var image in Artworks)
                yield return image;
        }

        public override string TagType => "Vorbis";

        #endregion

        // A FLAC file is valid without a VORBIS_COMMENT block. Give newly-created comments a
        // usable vendor so adding the first tag does not pass null to Encoding.GetBytes().
        public string Vendor = "MusicFileUtilities";
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
                    string combine = "METADATA_BLOCK_PICTURE=" + Convert.ToBase64String(art.ToByteArray(), Base64FormattingOptions.InsertLineBreaks);
                    b = Encoding.UTF8.GetBytes(combine);
                    l.AddRange(Tools.ToLE((int)b.Length));
                    l.AddRange(b);
                }
            }

            return l.ToArray();
        }

        public void FromByteArray(byte[] b, bool readArtwork = true)
        {
            int offset = 0;
            // All lengths are read from file bytes, so bounds-check every read; a corrupt
            // block stops parsing cleanly instead of throwing IndexOutOfRange/OutOfMemory.
            bool CanRead(long n) => n >= 0 && offset + n <= b.Length;

            if (!CanRead(4)) return;
            int vendorlength = Tools.Int32AtLE(b, offset);
            offset += 4;

            if (!CanRead(vendorlength)) return;
            Vendor = Encoding.UTF8.GetString(b, offset, vendorlength);
            offset += vendorlength;

            if (!CanRead(4)) { ParseStandardFields(); return; }
            int commentlistlength = Tools.Int32AtLE(b, offset);
            offset += 4;

            for (int i = 0; i < commentlistlength; i++)
            {
                if (!CanRead(4)) break;
                int commentlength = Tools.Int32AtLE(b, offset);
                offset += 4;

                if (!CanRead(commentlength)) break;
                int separator = b.AsSpan(offset, commentlength).IndexOf((byte)'=');
                if (separator < 0)
                {
                    offset += commentlength;
                    continue;
                }
                string key = Encoding.UTF8.GetString(b, offset, separator).ToUpperInvariant();
                int valueOffset = offset + separator + 1;
                int valueLength = commentlength - separator - 1;
                offset += commentlength;

                if (key == "METADATA_BLOCK_PICTURE")
                {
                    try
                    {
                        if (readArtwork)
                            Artworks.Add(new VorbisArtwork(Convert.FromBase64String(
                                Encoding.UTF8.GetString(b, valueOffset, valueLength))));
                    }
                    catch { /* skip a single malformed embedded picture */ }
                }
                else
                    Comments.Add(new KeyValuePair<string, string>(
                        key, Encoding.UTF8.GetString(b, valueOffset, valueLength)));
            }
            ParseStandardFields();
        }

        public VorbisComments(byte [] b)
        {
            FromByteArray(b);
        }

        public void SetField(TagFields field, string value)
        {
            // TotalTracks and TotalDiscs are stored as separate fields in Vorbis,
            // unlike ID3/APE which use "N/total" notation.
            if (!VorbisUtil.ReverseTagMappings.TryGetValue(field, out string key))
                throw new ArgumentException($"Unsupported tag field for Vorbis: {field}");

            Comments.RemoveAll(c => c.Key == key);
            if (value != null)
                Comments.Add(new KeyValuePair<string, string>(key, value));
        }

        public void RemoveField(TagFields field) => SetField(field, null);

        public void SetUserString(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A user-string key is required.", nameof(key));
            key = key.Trim().ToUpperInvariant();
            if (key.Contains('='))
                throw new ArgumentException("A Vorbis comment key cannot contain '='.", nameof(key));
            Comments.RemoveAll(comment => string.Equals(
                comment.Key, key, StringComparison.OrdinalIgnoreCase));
            if (value is not null)
                Comments.Add(KeyValuePair.Create(key, value));
        }

        public void RemoveUserString(string key) => SetUserString(key, null);

    }

    [Serializable]
    public class OggVorbisFile : VorbisComments, ICodecProvider, IMediaFile
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

        public uint DurationInFrames => 0;
        public uint DurationInSeconds => 0;

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

        private bool ReadPageHeader(Stream s, out int datalen, out bool continuation, out bool firstpage)
        {
            byte[] b = new byte[27];
            s.ReadExactly(b);
            if (!b.AsSpan(0, 4).SequenceEqual("OggS"u8))
                throw new InvalidDataException();
            byte[] segs = new byte[b[26]];
            s.ReadExactly(segs);
            int sum = 0;
            foreach (byte seg in segs)
                sum += seg;
            datalen = sum;
            continuation = ((b[5] & 1) == 1);
            firstpage = ((b[5] & 2) == 2);
            return ((b[5] & 4) == 4);
        }

        private bool _gotinfo;
        private bool _gotcomments;
        private readonly bool _readArtwork;
        internal bool LastSaveWasInPlace { get; private set; }

        private void ParsePage(byte[] page)
        {
            if (page.Length < 7)
                return;
            if (!page.AsSpan(1, 6).SequenceEqual("vorbis"u8))
                return;
            if (page[0] == 3) // Comment Block
            {
                byte[] stripped = new byte[page.Length - 7];
                Array.Copy(page, 7, stripped, 0, page.Length - 7);
                FromByteArray(stripped, _readArtwork);
                _gotcomments = true;
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
                _gotinfo = true;
            }
        }

        public OggVorbisFile(string filename, bool readArtwork = true)
        {
            Filename = filename;
            _readArtwork = readArtwork;

            bool lastpage;
            // Accumulate a packet's pages here. This previously used Array.Resize per
            // continuation page, recopying the whole buffer each time (O(n^2) for a packet
            // spanning many pages); a List grows amortized O(1).
            var pagedata = new List<byte>();

            using FileStream s = Tools.OpenReadSequential(filename);
            do
            {
                int datalen;
                bool continuation, firstpage;

                lastpage = ReadPageHeader(s, out datalen, out continuation, out firstpage);

                if (!continuation)
                {
                    if (!firstpage)
                        ParsePage(pagedata.ToArray());
                    pagedata.Clear();
                    // The identification and comment headers always precede the audio pages;
                    // once both have been parsed there is nothing left to learn from the rest
                    // of the stream (duration is not derived from it), so don't read it.
                    if (_gotinfo && _gotcomments)
                        break;
                }

                byte[] buf = new byte[datalen];
                s.ReadExactly(buf);
                pagedata.AddRange(buf);
            }
            while (!lastpage);

            ParsePage(pagedata.ToArray());

        }

        public void SaveTags(string outputPath = null)
        {
            LastSaveWasInPlace = false;
            string target = outputPath ?? Filename
                ?? throw new InvalidOperationException("No filename associated with this file.");

            // Build Vorbis comment packet: [0x03 "vorbis"] + VorbisComments data + framing bit
            byte[] commentData = ToByteArray(true);
            byte[] newPacket = new byte[7 + commentData.Length + 1];
            newPacket[0] = 0x03;
            newPacket[1] = (byte)'v'; newPacket[2] = (byte)'o'; newPacket[3] = (byte)'r';
            newPacket[4] = (byte)'b'; newPacket[5] = (byte)'i'; newPacket[6] = (byte)'s';
            Array.Copy(commentData, 0, newPacket, 7, commentData.Length);
            newPacket[7 + commentData.Length] = 0x01; // framing bit

            if (outputPath == null && TrySaveCommentInPlace(newPacket))
            {
                LastSaveWasInPlace = true;
                return;
            }

            string tempPath = Tools.CreateSiblingTempPath(target);
            try
            {
                {
                    using FileStream source = Tools.OpenReadSequential(Filename ?? target);
                    using FileStream dest = Tools.CreateWriteSequential(tempPath);
                    long sourceLength = source.Length;

                    int seqDelta = 0;
                    int state = 0; // 0=Looking, 1=Skipping old continuation pages, 2=Copying
                    int? editedSerial = null;
                    bool editedStreamEnded = false;

                    while (source.Position < sourceLength)
                    {
                        byte[] hdr = new byte[27];
                        if (source.Read(hdr, 0, 27) < 27) break;
                        if (hdr[0] != (byte)'O' || hdr[1] != (byte)'g' || hdr[2] != (byte)'g' || hdr[3] != (byte)'S')
                            throw new InvalidDataException("Invalid OGG page");

                        int numSegs = hdr[26];
                        byte[] segTable = new byte[numSegs];
                        source.ReadExactly(segTable, 0, numSegs);
                        int dataLen = 0;
                        foreach (byte sg in segTable) dataLen += sg;
                        byte[] data = new byte[dataLen];
                        if (dataLen > 0) source.ReadExactly(data);

                        bool isCont = (hdr[5] & 1) != 0;
                        int pageSerial = OggReadInt32LE(hdr, 14);

                        if (state == 0) // Looking for comment packet
                        {
                            if (!isCont && data.Length >= 7 &&
                                data[0] == 0x03 && data[1] == (byte)'v' && data[2] == (byte)'o' &&
                                data[3] == (byte)'r' && data[4] == (byte)'b' && data[5] == (byte)'i' && data[6] == (byte)'s')
                            {
                                editedSerial = pageSerial;
                                int newSeq = OggReadInt32LE(hdr, 18);
                                long granulePos = OggReadInt64LE(hdr, 6);
                                byte headerType = (byte)(hdr[5] & ~1); // clear continuation bit
                                int pagesWritten = WriteOggPacketPages(dest, newPacket, headerType, granulePos, pageSerial, newSeq);

                                // The comment packet is only the FIRST packet on this page: the
                                // vorbis setup header normally shares it. Replacing the whole page
                                // used to drop the setup header and leave the file undecodable, so
                                // re-emit whatever follows the comment packet on its own page.
                                var (ends, segsUsed, bytesUsed) = MeasureFirstPacket(segTable, numSegs);
                                if (ends)
                                {
                                    if (segsUsed < numSegs)
                                    {
                                        WriteRawPage(dest, headerType, granulePos, pageSerial, newSeq + pagesWritten,
                                                     segTable[segsUsed..], data[bytesUsed..]);
                                        pagesWritten++;
                                    }
                                    state = 2;
                                }
                                else
                                    state = 1;
                                seqDelta += pagesWritten - 1;
                            }
                            else
                            {
                                WriteOggPage(dest, hdr, segTable, data, 0);
                            }
                        }
                        else if (state == 1) // Old comment packet continues on this logical stream
                        {
                            // Ogg logical streams may be interleaved. Pages from another serial are
                            // unrelated to the continued comment and must pass through untouched.
                            if (pageSerial != editedSerial)
                            {
                                WriteOggPage(dest, hdr, segTable, data, 0);
                                continue;
                            }

                            if (!isCont)
                                throw new InvalidDataException("Invalid OGG comment continuation");

                            var (ends, segsUsed, bytesUsed) = MeasureFirstPacket(segTable, numSegs);
                            if (ends)
                            {
                                if (segsUsed < numSegs)
                                {
                                    // Packets after the old comment's tail (the setup header) must
                                    // survive; they take over this page's sequence slot.
                                    int seq = OggReadInt32LE(hdr, 18) + seqDelta;
                                    long granulePos = OggReadInt64LE(hdr, 6);
                                    byte headerType = (byte)(hdr[5] & ~1);
                                    WriteRawPage(dest, headerType, granulePos, pageSerial, seq,
                                                 segTable[segsUsed..], data[bytesUsed..]);
                                }
                                else
                                    seqDelta--;
                                state = 2;
                            }
                            else
                                seqDelta--;
                        }
                        else // state == 2, Copying remaining pages
                        {
                            bool adjustThisPage = !editedStreamEnded && pageSerial == editedSerial;
                            WriteOggPage(dest, hdr, segTable, data, adjustThisPage ? seqDelta : 0);
                        }

                        if (!editedStreamEnded && pageSerial == editedSerial && (hdr[5] & 4) != 0)
                            editedStreamEnded = true;
                    }

                    dest.Flush(flushToDisk: true);
                }

                Tools.AtomicReplace(tempPath, target);
            }
            catch
            {
                Tools.DeleteIfExists(tempPath);
                throw;
            }
            Filename = target;
        }

        private sealed record InPlaceCommentPage(
            long Offset, byte[] Header, byte[] Segments, byte[] Data, int CommentBytes);

        // Same-length packets retain exactly the same Ogg lacing and page sequence. Patch only the
        // comment bytes and page CRCs; setup/audio packets sharing those pages remain untouched.
        private bool TrySaveCommentInPlace(byte[] newPacket)
        {
            if (Filename is null)
                return false;

            using FileStream stream = new FileStream(
                Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                Tools.ParseReadBufferSize, FileOptions.RandomAccess);
            var pages = new List<InPlaceCommentPage>();
            int? editedSerial = null;
            int oldPacketLength = 0;
            bool complete = false;

            while (stream.Position < stream.Length)
            {
                long pageOffset = stream.Position;
                byte[] header = new byte[27];
                if (stream.Read(header, 0, header.Length) != header.Length)
                    return false;
                if (header[0] != (byte)'O' || header[1] != (byte)'g' || header[2] != (byte)'g' || header[3] != (byte)'S')
                    return false;

                byte[] segments = new byte[header[26]];
                stream.ReadExactly(segments);
                int dataLength = 0;
                foreach (byte segment in segments)
                    dataLength += segment;
                byte[] data = new byte[dataLength];
                stream.ReadExactly(data);

                bool continuation = (header[5] & 1) != 0;
                int serial = OggReadInt32LE(header, 14);
                if (editedSerial is null)
                {
                    if (continuation || data.Length < 7 || data[0] != 0x03 ||
                        data[1] != (byte)'v' || data[2] != (byte)'o' || data[3] != (byte)'r' ||
                        data[4] != (byte)'b' || data[5] != (byte)'i' || data[6] != (byte)'s')
                        continue;
                    editedSerial = serial;
                }
                else if (serial != editedSerial.Value)
                {
                    // Pages from an interleaved logical stream are unrelated to this packet.
                    continue;
                }
                else if (!continuation)
                {
                    return false;
                }

                var (ends, _, bytesUsed) = MeasureFirstPacket(segments, segments.Length);
                oldPacketLength += bytesUsed;
                if (oldPacketLength > newPacket.Length)
                    return false;
                pages.Add(new InPlaceCommentPage(pageOffset, header, segments, data, bytesUsed));
                if (ends)
                {
                    complete = true;
                    break;
                }
            }

            if (!complete || oldPacketLength != newPacket.Length)
                return false;

            int packetOffset = 0;
            foreach (var page in pages)
            {
                Array.Copy(newPacket, packetOffset, page.Data, 0, page.CommentBytes);
                packetOffset += page.CommentBytes;
                page.Header[22] = page.Header[23] = page.Header[24] = page.Header[25] = 0;
                uint crc = OggCRC(page.Header, 0, page.Header.Length);
                crc = OggCRC(page.Segments, 0, page.Segments.Length, crc);
                crc = OggCRC(page.Data, 0, page.Data.Length, crc);
                OggWriteUInt32LE(page.Header, 22, crc);

                stream.Seek(page.Offset, SeekOrigin.Begin);
                stream.Write(page.Header, 0, page.Header.Length);
                stream.Write(page.Segments, 0, page.Segments.Length);
                stream.Write(page.Data, 0, page.Data.Length);
            }
            stream.Flush(flushToDisk: true);
            return true;
        }

        // Walks the lacing values of a page's FIRST packet: how many segments and bytes it
        // spans, and whether it ends on this page (a lacing value < 255 terminates a packet).
        private static (bool Ends, int SegsUsed, int BytesUsed) MeasureFirstPacket(byte[] segTable, int numSegs)
        {
            int bytes = 0;
            for (int i = 0; i < numSegs; i++)
            {
                bytes += segTable[i];
                if (segTable[i] < 255)
                    return (true, i + 1, bytes);
            }
            return (false, numSegs, bytes);
        }

        // Writes a page from scratch (fresh header + CRC) around an existing lacing/payload.
        private static void WriteRawPage(Stream dest, byte headerType, long granulePos, int serial, int seq, byte[] segTable, byte[] data)
        {
            byte[] hdr = new byte[27];
            hdr[0] = (byte)'O'; hdr[1] = (byte)'g'; hdr[2] = (byte)'g'; hdr[3] = (byte)'S';
            hdr[4] = 0;
            hdr[5] = headerType;
            OggWriteInt64LE(hdr, 6, granulePos);
            OggWriteInt32LE(hdr, 14, serial);
            OggWriteInt32LE(hdr, 18, seq);
            hdr[26] = (byte)segTable.Length;
            uint crc = OggCRC(hdr, 0, 27);
            crc = OggCRC(segTable, 0, segTable.Length, crc);
            crc = OggCRC(data, 0, data.Length, crc);
            OggWriteUInt32LE(hdr, 22, crc);
            dest.Write(hdr, 0, 27);
            dest.Write(segTable, 0, segTable.Length);
            dest.Write(data, 0, data.Length);
        }

        private static void WriteOggPage(Stream dest, byte[] hdr, byte[] segTable, byte[] data, int seqDelta)
        {
            if (seqDelta == 0)
            {
                dest.Write(hdr, 0, 27);
                dest.Write(segTable, 0, segTable.Length);
                dest.Write(data, 0, data.Length);
                return;
            }
            byte[] h = (byte[])hdr.Clone();
            OggWriteInt32LE(h, 18, OggReadInt32LE(h, 18) + seqDelta);
            h[22] = h[23] = h[24] = h[25] = 0; // zero CRC field
            uint crc = OggCRC(h, 0, 27);
            crc = OggCRC(segTable, 0, segTable.Length, crc);
            crc = OggCRC(data, 0, data.Length, crc);
            OggWriteUInt32LE(h, 22, crc);
            dest.Write(h, 0, 27);
            dest.Write(segTable, 0, segTable.Length);
            dest.Write(data, 0, data.Length);
        }

        private static int WriteOggPacketPages(Stream dest, byte[] packet, byte headerType, long granulePos, int serial, int startSeq)
        {
            // Generate all lacing segments for the packet
            var allSegs = new List<byte>();
            int rem = packet.Length;
            while (rem >= 255) { allSegs.Add(255); rem -= 255; }
            allSegs.Add((byte)rem); // final segment (0 if length is multiple of 255)

            int segOffset = 0;
            int dataOffset = 0;
            int pagesWritten = 0;

            while (segOffset < allSegs.Count)
            {
                int segsThisPage = Math.Min(255, allSegs.Count - segOffset);
                byte[] segArr = allSegs.GetRange(segOffset, segsThisPage).ToArray();
                int pageDataLen = 0;
                foreach (byte sg in segArr) pageDataLen += sg;

                bool isLastPage = (segOffset + segsThisPage == allSegs.Count);
                bool isFirstPage = (pagesWritten == 0);
                byte thisHeaderType = (byte)(headerType | (isFirstPage ? 0 : 1));
                long thisGranule = isLastPage ? granulePos : unchecked((long)0xFFFFFFFFFFFFFFFFL);

                byte[] hdr = new byte[27];
                hdr[0] = (byte)'O'; hdr[1] = (byte)'g'; hdr[2] = (byte)'g'; hdr[3] = (byte)'S';
                hdr[4] = 0;
                hdr[5] = thisHeaderType;
                OggWriteInt64LE(hdr, 6, thisGranule);
                OggWriteInt32LE(hdr, 14, serial);
                OggWriteInt32LE(hdr, 18, startSeq + pagesWritten);
                hdr[26] = (byte)segsThisPage;

                byte[] pageData = new byte[pageDataLen];
                Array.Copy(packet, dataOffset, pageData, 0, pageDataLen);

                uint crc = OggCRC(hdr, 0, 27);
                crc = OggCRC(segArr, 0, segArr.Length, crc);
                crc = OggCRC(pageData, 0, pageData.Length, crc);
                OggWriteUInt32LE(hdr, 22, crc);

                dest.Write(hdr, 0, 27);
                dest.Write(segArr, 0, segArr.Length);
                dest.Write(pageData, 0, pageData.Length);

                segOffset += segsThisPage;
                dataOffset += pageDataLen;
                pagesWritten++;
            }
            return pagesWritten;
        }

        private static readonly uint[] _oggCrcTable = BuildOggCrcTable();
        private static uint[] BuildOggCrcTable()
        {
            uint[] table = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                uint crc = (uint)i << 24;
                for (int j = 0; j < 8; j++)
                    crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04c11db7u : crc << 1;
                table[i] = crc;
            }
            return table;
        }

        private static uint OggCRC(byte[] buf, int offset, int count, uint crc = 0)
        {
            for (int i = offset; i < offset + count; i++)
                crc = (crc << 8) ^ _oggCrcTable[(crc >> 24) ^ buf[i]];
            return crc;
        }

        private static long OggReadInt64LE(byte[] b, int off) =>
            (long)((ulong)b[off] | ((ulong)b[off+1] << 8) | ((ulong)b[off+2] << 16) | ((ulong)b[off+3] << 24) |
                   ((ulong)b[off+4] << 32) | ((ulong)b[off+5] << 40) | ((ulong)b[off+6] << 48) | ((ulong)b[off+7] << 56));

        private static int OggReadInt32LE(byte[] b, int off) =>
            (int)((uint)b[off] | ((uint)b[off+1] << 8) | ((uint)b[off+2] << 16) | ((uint)b[off+3] << 24));

        private static void OggWriteInt64LE(byte[] b, int off, long v)
        {
            ulong u = (ulong)v;
            b[off] = (byte)u; b[off+1] = (byte)(u >> 8); b[off+2] = (byte)(u >> 16); b[off+3] = (byte)(u >> 24);
            b[off+4] = (byte)(u >> 32); b[off+5] = (byte)(u >> 40); b[off+6] = (byte)(u >> 48); b[off+7] = (byte)(u >> 56);
        }

        private static void OggWriteInt32LE(byte[] b, int off, int v)
        {
            uint u = (uint)v;
            b[off] = (byte)u; b[off+1] = (byte)(u >> 8); b[off+2] = (byte)(u >> 16); b[off+3] = (byte)(u >> 24);
        }

        private static void OggWriteUInt32LE(byte[] b, int off, uint v)
        {
            b[off] = (byte)v; b[off+1] = (byte)(v >> 8); b[off+2] = (byte)(v >> 16); b[off+3] = (byte)(v >> 24);
        }


    }


}
