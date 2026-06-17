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

        private static Dictionary<TagFields, string> _reverseTagMappings = null;

        public static Dictionary<TagFields, string> ReverseTagMappings
        {
            get
            {
                if (_reverseTagMappings == null)
                {
                    _reverseTagMappings = new Dictionary<TagFields, string>();
                    foreach (var kv in TagMappings)
                        if (!_reverseTagMappings.ContainsKey(kv.Value))
                            _reverseTagMappings[kv.Value] = kv.Key;
                }
                return _reverseTagMappings;
            }
        }

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
        public byte[] Data;

        string IMetadataImage.Description => string.IsNullOrWhiteSpace(Description) ? PictureType.ToString() : Description;
        string IMetadataImage.Category => PictureType.ToString();
        string IMetadataImage.ImageType => MimeType;
        int IMetadataImage.Width => Width;
        int IMetadataImage.Height => Height;
        int IMetadataImage.Size => Data.Length;
        byte[] IMetadataImage.Data => Data;

        public string Hash
        {
            get;
            protected set;
        }

        public void HashImage(System.Security.Cryptography.HashAlgorithm hash)
        {
            Hash = Convert.ToBase64String(hash.ComputeHash(Data));
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
    public class VorbisComments : TagBase
    {

        #region IMetadataProvider Properties

        public override IEnumerable<KeyValuePair<string, string>> GetTextMetadata()
        {
            foreach (var field in GetKnownMetadata())
                yield return KeyValuePair.Create(field.Key.ToString(), field.Value);
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
            if (Encoding.ASCII.GetString(b, 0, 4) != "OggS")
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

                s.ReadExactly(pagedata, pagedata.Length - datalen, datalen);
            }
            while (!lastpage);

            ParsePage(pagedata);

            s.Close();
        }

        public void SaveTags(string outputPath = null)
        {
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

            string tempPath = target + ".tmp~";
            try
            {
                using FileStream source = new FileStream(Filename ?? target, FileMode.Open, FileAccess.Read);
                using FileStream dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write);

                int seqDelta = 0;
                int state = 0; // 0=Looking, 1=Skipping old continuation pages, 2=Copying

                while (source.Position < source.Length)
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
                    bool packetEndsHere = numSegs == 0 || segTable[numSegs - 1] < 255;

                    if (state == 0) // Looking for comment packet
                    {
                        if (!isCont && data.Length >= 7 &&
                            data[0] == 0x03 && data[1] == (byte)'v' && data[2] == (byte)'o' &&
                            data[3] == (byte)'r' && data[4] == (byte)'b' && data[5] == (byte)'i' && data[6] == (byte)'s')
                        {
                            int serial = OggReadInt32LE(hdr, 14);
                            int newSeq = OggReadInt32LE(hdr, 18) + seqDelta;
                            long granulePos = OggReadInt64LE(hdr, 6);
                            byte headerType = (byte)(hdr[5] & ~1); // clear continuation bit
                            int pagesWritten = WriteOggPacketPages(dest, newPacket, headerType, granulePos, serial, newSeq);
                            seqDelta += pagesWritten - 1;
                            state = packetEndsHere ? 2 : 1;
                        }
                        else
                        {
                            WriteOggPage(dest, hdr, segTable, data, seqDelta);
                        }
                    }
                    else if (state == 1) // Skipping old comment continuation pages
                    {
                        seqDelta--;
                        if (packetEndsHere) state = 2;
                    }
                    else // state == 2, Copying remaining pages
                    {
                        WriteOggPage(dest, hdr, segTable, data, seqDelta);
                    }
                }
            }
            catch
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                throw;
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(tempPath, target);
            Filename = target;
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