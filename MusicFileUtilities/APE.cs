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
        private int _picdataoffset = -1; // offset of the picture payload within _rawvalue
        private string _category;
        private byte[] _rawvalue;
        private int _width;
        private int _height;
        private bool _dimsComputed;

        // The original APE item value (filename\0picdata), kept so Save() can round-trip the
        // artwork byte-for-byte instead of dropping it. See [[ape-artwork-save-dataloss]].
        public byte[] RawValue => _rawvalue;
        public string Key => _category;

        // The picture payload is copied out of RawValue lazily (RawValue is retained for the
        // save round-trip anyway); the eager copy doubled the memory traffic of every embedded
        // picture on a scan even when nothing looked at the image.
        private byte[] PicData
        {
            get
            {
                if (_picdata == null && _picdataoffset >= 0)
                {
                    int datalen = Math.Max(0, _rawvalue.Length - _picdataoffset);
                    _picdata = new byte[datalen];
                    if (datalen > 0)
                        Array.Copy(_rawvalue, _picdataoffset, _picdata, 0, datalen);
                }
                return _picdata;
            }
        }

        // Dimensions are decoded lazily so a scan reading only text tags doesn't parse covers,
        // and probed in place so they don't force the payload copy either.
        private void EnsureDimensions()
        {
            if (_dimsComputed) return;
            _dimsComputed = true;
            ReadOnlySpan<byte> picdata = _picdata != null
                ? _picdata
                : (_picdataoffset >= 0 && _picdataoffset < _rawvalue.Length ? _rawvalue.AsSpan(_picdataoffset) : default);
            if (!picdata.IsEmpty)
            {
                var img = ImageFile.GetImageDimensions(picdata);
                _width = img.Width;
                _height = img.Height;
            }
        }

        string IMetadataImage.Description => string.IsNullOrWhiteSpace(_description) ? _category : _description;
        string IMetadataImage.Category => _category;
        string IMetadataImage.ImageType => _mimetype;
        int IMetadataImage.Width { get { EnsureDimensions(); return _width; } }
        int IMetadataImage.Height { get { EnsureDimensions(); return _height; } }
        int IMetadataImage.Size => _picdata?.Length ?? (_picdataoffset >= 0 ? Math.Max(0, _rawvalue.Length - _picdataoffset) : 0);
        byte[] IMetadataImage.Data => PicData;

        public APEArtwork(string key, byte[] value)
        {
            _rawvalue = value;
            string filename = "";
            int offset = 0;
            // Bounded: a binary item with no null terminator must not run off the end.
            while (offset < value.Length && value[offset] != 0)
                filename += (char)value[offset++];
            offset++; // skip the null separator
            _picdataoffset = offset;
            string ext = Path.GetExtension(filename).ToLower();
            _mimetype = ext.Length > 1 ? ext.Substring(1) : "";
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
        }

        public string Hash
        {
            get;
            protected set;
        }

        public void HashImage(System.Security.Cryptography.HashAlgorithm hash)
        {
            // Hash straight over the payload slice of RawValue: materializing PicData just to
            // hash it would copy every cover on every scan.
            Hash = Convert.ToBase64String(_picdata != null
                ? hash.ComputeHash(_picdata)
                : hash.ComputeHash(_rawvalue, _picdataoffset, Math.Max(0, _rawvalue.Length - _picdataoffset)));
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

        // Read-side lookups, built once. Previously rebuilt on every GetKnownMetadata call.
        public static readonly Dictionary<string, TagFields> SensitiveMap =
            TagMappings.Where(kv => kv.Key.CaseSensitive)
                       .GroupBy(kv => kv.Key.Key)
                       .ToDictionary(g => g.Key, g => g.First().Value);

        public static readonly Dictionary<string, TagFields> InsensitiveMap =
            TagMappings.Where(kv => !kv.Key.CaseSensitive)
                       .GroupBy(kv => kv.Key.Key.ToUpper())
                       .ToDictionary(g => g.Key, g => g.First().Value);

        // Canonical write key for each TagField (case-sensitive key preferred; first match wins).
        // Lazily built once (thread-safe); the old null-check pattern could race.
        private static readonly Lazy<Dictionary<TagFields, string>> _reverseTagMappings =
            new Lazy<Dictionary<TagFields, string>>(() =>
            {
                var map = new Dictionary<TagFields, string>();
                foreach (var kv in TagMappings.Where(k => k.Key.CaseSensitive))
                    if (!map.ContainsKey(kv.Value))
                        map[kv.Value] = kv.Key.Key;
                foreach (var kv in TagMappings.Where(k => !k.Key.CaseSensitive))
                    if (!map.ContainsKey(kv.Value))
                        map[kv.Value] = kv.Key.Key;
                return map;
            });

        public static Dictionary<TagFields, string> ReverseTagMappings => _reverseTagMappings.Value;

    }

    public class APETag : TagBase, IArtworkWriter
    {
        public override string TagType => "APE";

        public override IEnumerable<KeyValuePair<string, string>> GetTextMetadata()
        {
            foreach (var field in GetKnownMetadata())
                yield return KeyValuePair.Create(field.Key.ToString(), field.Value);
        }

        public override IEnumerable<KeyValuePair<TagFields, string>> GetKnownMetadata()
        {
            var sensmap = APEUtil.SensitiveMap;
            var insensmap = APEUtil.InsensitiveMap;
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

        // Byte offset in the source file where audio data ends and the APE tag begins.
        // Set by ReadTag; used by the file-level Save().
        public long AudioEndOffset { get; private set; } = -1;

        public void SetField(TagFields field, string value)
        {
            // The compound Track/Disc fields are handled below before the mapping guard,
            // because TotalTracks/TotalDiscs have no standalone key in ReverseTagMappings
            // (they are stored inside the "Track"/"Disc" value as "N/total").
            bool isCompound = field == TagFields.TrackNumber || field == TagFields.TotalTracks
                           || field == TagFields.DiscNumber || field == TagFields.TotalDiscs;
            string key = null;
            if (!isCompound && !APEUtil.ReverseTagMappings.TryGetValue(field, out key))
                throw new ArgumentException($"Unsupported tag field for APE: {field}");

            // TrackNumber and TotalTracks share the "Track" key as "N/total"
            if (field == TagFields.TrackNumber || field == TagFields.TotalTracks)
            {
                string trackKey = "Track";
                var existing = TextItems.FirstOrDefault(kv => string.Equals(kv.Key, trackKey, StringComparison.OrdinalIgnoreCase));
                string[] parts = (existing.Key != null ? existing.Value : "").Split('/');
                string num = parts.Length >= 1 ? parts[0] : "";
                string tot = parts.Length >= 2 ? parts[1] : "";
                if (field == TagFields.TrackNumber) num = value ?? "";
                else tot = value ?? "";
                TextItems.RemoveAll(kv => string.Equals(kv.Key, trackKey, StringComparison.OrdinalIgnoreCase));
                string combined = string.IsNullOrEmpty(tot) ? num : num + "/" + tot;
                if (!string.IsNullOrEmpty(combined))
                    TextItems.Add(new KeyValuePair<string, string>(trackKey, combined));
                return;
            }
            if (field == TagFields.DiscNumber || field == TagFields.TotalDiscs)
            {
                string discKey = "Disc";
                var existing = TextItems.FirstOrDefault(kv => string.Equals(kv.Key, discKey, StringComparison.OrdinalIgnoreCase));
                string[] parts = (existing.Key != null ? existing.Value : "").Split('/');
                string num = parts.Length >= 1 ? parts[0] : "";
                string tot = parts.Length >= 2 ? parts[1] : "";
                if (field == TagFields.DiscNumber) num = value ?? "";
                else tot = value ?? "";
                TextItems.RemoveAll(kv => string.Equals(kv.Key, discKey, StringComparison.OrdinalIgnoreCase));
                string combined = string.IsNullOrEmpty(tot) ? num : num + "/" + tot;
                if (!string.IsNullOrEmpty(combined))
                    TextItems.Add(new KeyValuePair<string, string>(discKey, combined));
                return;
            }

            TextItems.RemoveAll(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (value != null)
                TextItems.Add(new KeyValuePair<string, string>(key, value));
        }

        public void RemoveField(TagFields field) => SetField(field, null);

        // IArtworkWriter: APEv2 stores a picture as a binary item whose value is "filename\0picdata";
        // the reader derives the MIME type from the filename extension (see APEArtwork ctor), so the
        // synthetic filename here must carry the matching extension. ToByteArray serializes
        // ArtworkItems, so a subsequent Save round-trips the new cover.
        public void SetFrontCover(byte[] imageData, string mimeType)
        {
            if (imageData == null || imageData.Length == 0)
            {
                RemoveImages();
                return;
            }
            const string key = "Cover Art (Front)";
            byte[] fnBytes = Encoding.UTF8.GetBytes("cover" + ExtForMime(mimeType));
            byte[] raw = new byte[fnBytes.Length + 1 + imageData.Length];
            Array.Copy(fnBytes, 0, raw, 0, fnBytes.Length);
            raw[fnBytes.Length] = 0; // null separator between filename and payload
            Array.Copy(imageData, 0, raw, fnBytes.Length + 1, imageData.Length);

            ArtworkItems.RemoveAll(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            ArtworkItems.Add(new KeyValuePair<string, APEArtwork>(key, new APEArtwork(key, raw)));
        }

        public void RemoveImages() => ArtworkItems.Clear();

        public void SetImages(IReadOnlyList<ArtworkImage> images)
        {
            ArtworkItems.Clear();
            foreach (var img in images)
            {
                var key = KeyForType(img.Type);
                byte[] fnBytes = Encoding.UTF8.GetBytes("cover" + ExtForMime(img.MimeType));
                byte[] raw = new byte[fnBytes.Length + 1 + img.Data.Length];
                Array.Copy(fnBytes, 0, raw, 0, fnBytes.Length);
                raw[fnBytes.Length] = 0;
                Array.Copy(img.Data, 0, raw, fnBytes.Length + 1, img.Data.Length);
                // One APE binary item per key; a repeated type overwrites the earlier one.
                ArtworkItems.RemoveAll(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                ArtworkItems.Add(new KeyValuePair<string, APEArtwork>(key, new APEArtwork(key, raw)));
            }
        }

        // APEv2 convention keys embedded pictures as "Cover Art (Front)", "Cover Art (Back)", etc.
        private static string KeyForType(ID3v2Util.APICType type) => type switch
        {
            ID3v2Util.APICType.FrontCover => "Cover Art (Front)",
            ID3v2Util.APICType.BackCover => "Cover Art (Back)",
            ID3v2Util.APICType.Media => "Cover Art (Media)",
            ID3v2Util.APICType.LeafletPage => "Cover Art (Leaflet)",
            _ => $"Cover Art ({type})",
        };

        private static string ExtForMime(string mimeType) => (mimeType ?? "").ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            _ => ".jpg",
        };

        // Serialize one APE item: [value length LE][flags LE][key]\0[value].
        // flags bit 1 (value 2) marks a binary item; 0 marks UTF-8 text.
        private static byte[] BuildItem(string key, byte[] valBytes, uint flags)
        {
            byte[] keyBytes = Encoding.ASCII.GetBytes(key);
            byte[] item = new byte[8 + keyBytes.Length + 1 + valBytes.Length];
            Tools.ToLE(valBytes.Length).CopyTo(item, 0);  // value length LE
            Tools.ToLE(flags).CopyTo(item, 4);            // item flags LE
            Array.Copy(keyBytes, 0, item, 8, keyBytes.Length);
            item[8 + keyBytes.Length] = 0;                // null separator
            Array.Copy(valBytes, 0, item, 8 + keyBytes.Length + 1, valBytes.Length);
            return item;
        }

        public byte[] ToByteArray()
        {
            // Gather all items: text items (flags=0) and binary/artwork items (flags=2).
            // Binary and artwork items must be preserved or a tag rewrite silently strips
            // embedded cover art and other binary fields. See [[ape-artwork-save-dataloss]].
            var items = new List<byte[]>();

            foreach (var kv in TextItems)
                items.Add(BuildItem(kv.Key, Encoding.UTF8.GetBytes(kv.Value), 0));

            foreach (var kv in BinaryItems)
                items.Add(BuildItem(kv.Key, kv.Value, 2));

            foreach (var kv in ArtworkItems)
                items.Add(BuildItem(kv.Key, kv.Value.RawValue, 2));

            int itemCount = items.Count;
            int tagSize = items.Sum(i => i.Length); // items only, no header/footer in size field

            // APE header (32 bytes): preamble(8) + version(4) + size(4) + count(4) + flags(4) + reserved(8)
            byte[] header = new byte[32];
            Encoding.ASCII.GetBytes("APETAGEX").CopyTo(header, 0);
            Tools.ToLE(2000).CopyTo(header, 8);           // version 2000
            Tools.ToLE(tagSize + 32).CopyTo(header, 12);  // size includes footer
            Tools.ToLE(itemCount).CopyTo(header, 16);
            Tools.ToLE(0xA0000000u).CopyTo(header, 20);   // flags: has header, is header
            // reserved 8 bytes already zero

            // APE footer (32 bytes): same but flags indicate it's a footer
            byte[] footer = new byte[32];
            Encoding.ASCII.GetBytes("APETAGEX").CopyTo(footer, 0);
            Tools.ToLE(2000).CopyTo(footer, 8);
            Tools.ToLE(tagSize + 32).CopyTo(footer, 12);
            Tools.ToLE(itemCount).CopyTo(footer, 16);
            Tools.ToLE(0x80000000u).CopyTo(footer, 20);   // flags: has header, is footer

            var result = new List<byte>(32 + tagSize + 32);
            result.AddRange(header);
            foreach (var item in items)
                result.AddRange(item);
            result.AddRange(footer);
            return result.ToArray();
        }

        public bool ReadTag(Stream s)
        {
            long streamLength = s.Length;
            if (streamLength < 32)
                return false;
            byte[] preamble = new byte[8];
            byte[] headerfooter = new byte[24];
            s.Seek(0, SeekOrigin.Begin);
            s.ReadExactly(preamble);
            if (Encoding.ASCII.GetString(preamble) == "APETAGEX")
                s.ReadExactly(headerfooter);
            else
            {
                s.Seek(-32, SeekOrigin.End);
                s.ReadExactly(preamble);
                if (Encoding.ASCII.GetString(preamble) == "APETAGEX")
                    s.ReadExactly(headerfooter);
                else
                    return false;
                int sizeFromFooter = Tools.Int32AtLE(headerfooter, 4);
                // Audio ends where the APE tag block starts.
                // The size field covers items + footer (32). Check for a leading header.
                long footerPos = streamLength - 32;
                long itemsStart = footerPos - (sizeFromFooter - 32);
                // Check if a header precedes the items
                if (itemsStart >= 32)
                {
                    s.Seek(itemsStart - 32, SeekOrigin.Begin);
                    byte[] maybePreamble = new byte[8];
                    s.ReadExactly(maybePreamble);
                    AudioEndOffset = Encoding.ASCII.GetString(maybePreamble) == "APETAGEX"
                        ? itemsStart - 32
                        : itemsStart;
                }
                else
                    AudioEndOffset = itemsStart;
                if (sizeFromFooter < 0 || sizeFromFooter > streamLength)
                    return false;
                s.Seek(-sizeFromFooter, SeekOrigin.End);
            }
            // NOTE: in the header-first branch above, AudioEndOffset stays -1 (we can't infer the
            // audio layout of a tag-at-start file); Save() then falls back to a full copy. That
            // path isn't reached by WavPack, whose tag is always at the end.
            int tagsize = Tools.Int32AtLE(headerfooter, 4);
            int itemcount = Tools.Int32AtLE(headerfooter, 8);
            // Item count/sizes come from the file; validate before allocating/indexing so a
            // corrupt tag can't OOM or read out of bounds.
            if (tagsize < 0 || tagsize > streamLength - s.Position)
                return false;
            byte[] tag = new byte[tagsize];
            s.ReadExactly(tag);
            int offset = 0;

            for(int i=0;i<itemcount;i++)
            {
                if (offset + 8 > tag.Length)
                    break;
                int itemlen = Tools.Int32AtLE(tag, offset);
                int itemflags = Tools.Int32AtLE(tag, offset+4);
                string itemkey = "";
                offset += 8;
                while (offset < tag.Length && tag[offset] != 0)
                    itemkey += (char)tag[offset++];
                if (offset >= tag.Length)
                    break;                 // unterminated key
                offset++;                  // skip the null separator
                if (itemlen < 0 || (long)offset + itemlen > tag.Length)
                    break;                 // bad/oversized value length (long math avoids overflow)
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
