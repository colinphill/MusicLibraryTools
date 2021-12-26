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

        public IEnumerable<KeyValuePair<string, string>> GetTextMetadata()
        {
            foreach (var field in GetKnownMetadata())
                yield return KeyValuePair.Create(field.Key.ToString(), field.Value);
        }

        public IEnumerable<KeyValuePair<TagFields, string>> GetKnownMetadata()
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

        public IEnumerable<IMetadataImage> GetImageMetadata()
        {
            foreach (var image in Artworks)
                yield return image;
        }

        public string TagType => "Vorbis";

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

        public uint DurationInFrames => 0;
        public uint DurationInSeconds => 0;

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