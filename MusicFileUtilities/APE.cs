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
        public static Dictionary<string, TagFields> TagMappings = new Dictionary<string, TagFields>()
        {
            { "ACOUSTID_ID", TagFields.AcoustID_ID },
            { "ACOUSTID_FINGERPRINT", TagFields.AcoustID_Fingerprint },
            { "Album", TagFields.Album },
            { "Album Artist", TagFields.AlbumArtist },
            { "AlbumArtist", TagFields.AlbumArtist },
            { "ALBUMARTISTSORT", TagFields.AlbumArtistSort },
            { "ALBUMSORT", TagFields.AlbumSort },
            { "Arranger", TagFields.Arranger },
            { "Artist", TagFields.Artist },
            { "ARTISTSORT", TagFields.ArtistSort },
            { "Artists", TagFields.Artists },
            { "ASIN", TagFields.ASIN },
            { "Barcode", TagFields.Barcode },
            { "BPM", TagFields.BPM },
            { "CatalogNumber", TagFields.CatalogNumber },
            { "Comment", TagFields.Comment },
            { "Compilation", TagFields.Compilation },
            { "Composer", TagFields.Composer },
            { "COMPOSERSORT", TagFields.ComposerSort },
            { "Conductor", TagFields.Conductor },
            { "Copyright", TagFields.Copyright },
            { "Disc", TagFields.DiscNumber },
            { "DiscSubtitle", TagFields.DiscSubtitle },
            { "EncodedBy", TagFields.EncodedBy },
            { "EncoderSettings", TagFields.EncoderSettings },
            { "Engineer", TagFields.Engineer },
            { "Genre", TagFields.Genre },
            { "Grouping", TagFields.Grouping },
            { "KEY", TagFields.Key },
            { "ISRC", TagFields.ISRC },
            { "Language", TagFields.Language },
            { "LICENSE", TagFields.License },
            { "Lyricist", TagFields.Lyricist },
            { "Lyrics", TagFields.Lyrics },
            { "Media", TagFields.Media },
            { "DJMixer", TagFields.DJMixer },
            { "Mixer", TagFields.Mixer },
            { "Mood", TagFields.Mood },
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
            { "Performer", TagFields.Performer },
            { "Producer", TagFields.Producer },
            { "RATING", TagFields.Rating },
            { "Label", TagFields.Label },
            { "RELEASECOUNTRY", TagFields.ReleaseCountry },
            { "Year", TagFields.Date },
            { "MUSICBRAINZ_ALBUMSTATUS", TagFields.ReleaseStatus },
            { "MUSICBRAINZ_ALBUMTYPE", TagFields.ReleaseType },
            { "MixArtist", TagFields.Remixer },
            { "REPLAYGAIN_ALBUM_GAIN", TagFields.ReplayGain_Album_Gain },
            { "REPLAYGAIN_ALBUM_PEAK", TagFields.ReplayGain_Album_Peak },
            { "REPLAYGAIN_ALBUM_RANGE", TagFields.ReplayGain_Album_Range },
            { "REPLAYGAIN_REFERENCE_LOUDNESS", TagFields.ReplayGain_Reference_Loudness },
            { "REPLAYGAIN_TRACK_GAIN", TagFields.ReplayGain_Track_Gain },
            { "REPLAYGAIN_TRACK_PEAK", TagFields.ReplayGain_Track_Peak },
            { "REPLAYGAIN_TRACK_RANGE", TagFields.ReplayGain_Track_Range },
            { "Script", TagFields.Script },
            { "SHOWMOVEMENT", TagFields.ShowMovement },
            { "Track", TagFields.TrackNumber },
            { "Title", TagFields.Title },
            { "TITLESORT", TagFields.TitleSort },
            { "weblink", TagFields.Website },
            { "WORK", TagFields.Work },
            { "Writer", TagFields.Writer },
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
            var tagmap = APEUtil.TagMappings.ToDictionary(kv => kv.Key.ToUpper(), kv => kv.Value);
            foreach (var kv in TextItems)
                if (tagmap.ContainsKey(kv.Key.ToUpper()))
                {
                    var tag = tagmap[kv.Key.ToUpper()];
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
                        yield return KeyValuePair.Create(tagmap[kv.Key.ToUpper()], kv.Value);
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
