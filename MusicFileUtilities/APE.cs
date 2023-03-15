using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MusicFileUtilities
{

    public class APEArtwork : IMetadataImage
    {
        private string _mimetype;
        private string _description;
        private byte[] _picdata;
        private string _category;
        private int _width;
        private int _height;

        string IMetadataImage.Description => string.IsNullOrWhiteSpace(_description) ? _category : _description;
        string IMetadataImage.Category => _category;
        string IMetadataImage.ImageType => _mimetype;
        int IMetadataImage.Width => _width;
        int IMetadataImage.Height => _height;
        int IMetadataImage.Size => _picdata.Length;
        byte[] IMetadataImage.Data => _picdata;

        public APEArtwork(string key, byte[] value)
        {
            string filename = "";
            int offset = 0;
            while (value[offset] != 0)
                filename += (char)value[offset++];
            offset++;
            _picdata = new byte[value.Length - offset];
            Array.Copy(value, offset, _picdata, 0, _picdata.Length);
            _mimetype = Path.GetExtension(filename).ToLower().Substring(1);
            if ((_mimetype.ToLower() == "jpeg") || (_mimetype.ToLower() == "jpg"))
                _mimetype = "image/jpeg";
            if (_mimetype.ToLower() == "png")
                _mimetype = "image/png";
            if (_mimetype.ToLower() == "bmp")
                _mimetype = "image/bmp";
            if (_mimetype.ToLower() == "gif")
                _mimetype = "image/gif";
            _description = filename;
            _category = key;
            var img = ImageFile.GetImageDimensions(_picdata);
            _width = img.Width;
            _height = img.Height;
        }

        public string Hash
        {
            get;
            protected set;
        }

        public void HashImage(System.Security.Cryptography.HashAlgorithm hash)
        {
            Hash = Convert.ToBase64String(hash.ComputeHash(_picdata));
        }

    }

    internal class APEUtil
    {
        public static Dictionary<(string Key, bool CaseSensitive), TagFields> TagMappings = new ()
        {
            { ("ACOUSTID_ID", true), TagFields.AcoustID_ID },
            { ("ACOUSTID_FINGERPRINT", true), TagFields.AcoustID_Fingerprint },
            { ("Album", false), TagFields.Album },
            { ("Album Artist", false), TagFields.AlbumArtist },
            { ("AlbumArtist", false), TagFields.AlbumArtist },
            { ("ALBUMARTISTSORT", true), TagFields.AlbumArtistSort },
            { ("ALBUMSORT", true), TagFields.AlbumSort },
            { ("Arranger", false), TagFields.Arranger },
            { ("Artist", false), TagFields.Artist },
            { ("ARTISTSORT", true), TagFields.ArtistSort },
            { ("Artists", false), TagFields.Artists },
            { ("ASIN", true), TagFields.ASIN },
            { ("Barcode", false), TagFields.Barcode },
            { ("BPM", true), TagFields.BPM },
            { ("CatalogNumber", false), TagFields.CatalogNumber },
            { ("Comment", false), TagFields.Comment },
            { ("Compilation", false), TagFields.Compilation },
            { ("Composer", false), TagFields.Composer },
            { ("COMPOSERSORT", true), TagFields.ComposerSort },
            { ("Conductor", false), TagFields.Conductor },
            { ("Copyright", false), TagFields.Copyright },
            { ("Disc", false), TagFields.DiscNumber },
            { ("DiscSubtitle", false), TagFields.DiscSubtitle },
            { ("EncodedBy", false), TagFields.EncodedBy },
            { ("EncoderSettings", false), TagFields.EncoderSettings },
            { ("Engineer", false), TagFields.Engineer },
            { ("Genre", false), TagFields.Genre },
            { ("Grouping", false), TagFields.Grouping },
            { ("KEY", false), TagFields.Key },
            { ("ISRC", false), TagFields.ISRC },
            { ("Language", false), TagFields.Language },
            { ("LICENSE", true), TagFields.License },
            { ("Lyricist", false), TagFields.Lyricist },
            { ("Lyrics", false), TagFields.Lyrics },
            { ("Media", false), TagFields.Media },
            { ("DJMixer", false), TagFields.DJMixer },
            { ("Mixer", false), TagFields.Mixer },
            { ("Mood", false), TagFields.Mood },
            { ("MOVEMENTNAME", true), TagFields.Movement },
            { ("MOVEMENTTOTAL", true), TagFields.MovementTotal },
            { ("MOVEMENT", true), TagFields.MovementNumber },
            { ("MUSICBRAINZ_ARTISTID", true), TagFields.MusicBrainz_ArtistID },
            { ("MUSICBRAINZ_DISCID", true), TagFields.MusicBrainz_DiscID },
            { ("MUSICBRAINZ_ORIGINALARTISTID", true), TagFields.MusicBrainz_OriginalArtistID },
            { ("MUSICBRAINZ_ORIGINALALBUMID", true), TagFields.MusicBrainz_OriginalAlbumID },
            { ("MUSICBRAINZ_TRACKID", true), TagFields.MusicBrainz_RecordingID },
            { ("MUSICBRAINZ_ALBUMARTISTID", true), TagFields.MusicBrainz_AlbumArtistID },
            { ("MUSICBRAINZ_RELEASEGROUPID", true), TagFields.MusicBrainz_ReleaseGroupID },
            { ("MUSICBRAINZ_ALBUMID", true), TagFields.MusicBrainz_AlbumID },
            { ("MUSICBRAINZ_RELEASETRACKID", true), TagFields.MusicBrainz_TrackID },
            { ("MUSICBRAINZ_WORKID", true), TagFields.MusicBrainz_WorkID },
            { ("ORIGINALFILENAME", true), TagFields.OriginalFileName },
            { ("ORIGINALDATE", true), TagFields.OriginalDate },
            { ("ORIGINALYEAR", true), TagFields.OriginalYear },
            { ("Performer", false), TagFields.Performer },
            { ("Producer", false), TagFields.Producer },
            { ("RATING", true), TagFields.Rating },
            { ("Label", false), TagFields.Label },
            { ("RELEASECOUNTRY", true), TagFields.ReleaseCountry },
            { ("Year", false), TagFields.Date },
            { ("MUSICBRAINZ_ALBUMSTATUS", true), TagFields.ReleaseStatus },
            { ("MUSICBRAINZ_ALBUMTYPE", true), TagFields.ReleaseType },
            { ("MixArtist", false), TagFields.Remixer },
            { ("REPLAYGAIN_ALBUM_GAIN", true), TagFields.ReplayGain_Album_Gain },
            { ("REPLAYGAIN_ALBUM_PEAK", true), TagFields.ReplayGain_Album_Peak },
            { ("REPLAYGAIN_ALBUM_RANGE", true), TagFields.ReplayGain_Album_Range },
            { ("REPLAYGAIN_REFERENCE_LOUDNESS", true), TagFields.ReplayGain_Reference_Loudness },
            { ("REPLAYGAIN_TRACK_GAIN", true), TagFields.ReplayGain_Track_Gain },
            { ("REPLAYGAIN_TRACK_PEAK", true), TagFields.ReplayGain_Track_Peak },
            { ("REPLAYGAIN_TRACK_RANGE", true), TagFields.ReplayGain_Track_Range },
            { ("Script", false), TagFields.Script },
            { ("SHOWMOVEMENT", true), TagFields.ShowMovement },
            { ("Track", false), TagFields.TrackNumber },
            { ("Title", false), TagFields.Title },
            { ("TITLESORT", true), TagFields.TitleSort },
            { ("weblink", false), TagFields.Website },
            { ("WORK", true), TagFields.Work },
            { ("Writer", false), TagFields.Writer },
        };

    }

    public class APETag : TagBase
    {
        public override string TagType => "APE";

        public override IEnumerable<KeyValuePair<string, string>> GetTextMetadata()
        {
            foreach (var field in GetKnownMetadata())
                yield return KeyValuePair.Create(field.Key.ToString(), field.Value);
        }

        public override IEnumerable<KeyValuePair<TagFields, string>> GetKnownMetadata()
        {
            var sensmap = APEUtil.TagMappings.Where(kv => kv.Key.CaseSensitive).ToDictionary(kv => kv.Key.Key, kv => kv.Value);
            var insensmap = APEUtil.TagMappings.Where(kv => !kv.Key.CaseSensitive).ToDictionary(kv => kv.Key.Key.ToUpper(), kv => kv.Value);
            foreach (var kv in TextItems)
            {
                string ukey = kv.Key.ToUpper();
                TagFields tag = TagFields.NullField;
                if (sensmap.ContainsKey(kv.Key))
                    tag = sensmap[kv.Key];
                if (insensmap.ContainsKey(ukey))
                    tag = insensmap[ukey];

                if (tag != TagFields.NullField)
                {
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
                        yield return KeyValuePair.Create(tag, kv.Value);
                }
            }
        }

        public override IEnumerable<IMetadataImage> GetImageMetadata()
        {
            return ArtworkItems.Select(kv => kv.Value);
        }

        public List<KeyValuePair<string, string>> TextItems = new ();
        public List<KeyValuePair<string, byte[]>> BinaryItems = new();
        public List<KeyValuePair<string, APEArtwork>> ArtworkItems = new ();

        public bool ReadTag(Stream s)
        {
            if (s.Length < 32)
                return false;
            s.Seek(0, SeekOrigin.Begin);
            byte[] preamble = new byte[8];
            byte[] headerfooter = new byte[24];
            s.Seek(0, SeekOrigin.Begin);
            s.Read(preamble, 0, 8);
            if (Encoding.ASCII.GetString(preamble) == "APETAGEX")
                s.Read(headerfooter, 0, 24);
            else
            {
                s.Seek(-32, SeekOrigin.End);
                s.Read(preamble, 0, 8);
                if (Encoding.ASCII.GetString(preamble) == "APETAGEX")
                    s.Read(headerfooter, 0, 24);
                else
                    return false;
                s.Seek(-Tools.Int32AtLE(headerfooter, 4), SeekOrigin.End);
            }
            int tagsize = Tools.Int32AtLE(headerfooter, 4);
            int itemcount = Tools.Int32AtLE(headerfooter, 8);
            byte[] tag = new byte[tagsize];
            s.Read(tag, 0, tagsize);
            int offset = 0;

            for(int i=0;i<itemcount;i++)
            {
                int itemlen = Tools.Int32AtLE(tag, offset);
                int itemflags = Tools.Int32AtLE(tag, offset+4);
                string itemkey = "";
                offset += 8;
                while (tag[offset] != 0)
                    itemkey += (char)tag[offset++];
                offset++;
                if ((itemflags & 6) == 0)
                {
                    string itemvalue = Encoding.UTF8.GetString(tag, offset, itemlen);
                    TextItems.AddRange(itemvalue.Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).Select(s => new KeyValuePair<string, string>(itemkey, s)));
                } 
                else if ((itemflags & 6) == 2)
                {
                    byte[] bindata = new byte[itemlen];
                    Array.Copy(tag, offset, bindata, 0, itemlen);
                    if (itemkey.StartsWith("Cover Art", StringComparison.InvariantCultureIgnoreCase))
                        ArtworkItems.Add(new KeyValuePair<string, APEArtwork>(itemkey, new APEArtwork(itemkey, bindata)));
                    else
                        BinaryItems.Add(new KeyValuePair<string, byte[]>(itemkey, bindata));
                }
                offset += itemlen;
            }

            ParseStandardFields();
            return true;
        }


    }
}
