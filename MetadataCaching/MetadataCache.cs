using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using MusicFileUtilities;
using ConsoleTools;

namespace MetadataCaching
{
    public static class CachedMetadataKeys
    {
        public const string CustomPrefix = "__CUSTOM__:";
        public const string CacheFeature =
            "native-custom-metadata-v1";

        public static string Custom(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return CustomPrefix + key.Trim();
        }

        public static bool TryGetCustomName(
            string key,
            out string customName)
        {
            if (key.StartsWith(
                    CustomPrefix,
                    StringComparison.Ordinal))
            {
                customName = key[CustomPrefix.Length..];
                return customName.Length > 0;
            }
            customName = null;
            return false;
        }
    }

    [Serializable]
    public partial class MetadataCacheEntry
    {
        private static Regex stripre_ = new Regex(@" \((DSD|DSD64|DSD128|DSD256|DVD-V|DVD-A|HiRes|Hi-Res|DTS-CD)\)$", RegexOptions.IgnoreCase);
        private static readonly Regex releaseyearre_ = new(
            @"(?<!\d)(?<year>\d{4})(?!\d)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private string _title = null;
        private string _album = null;
        private string _artist = null;
        private string _albumartist = null;
        private int? _tracknumber = null;
        private int? _tracktotal = null;
        private DateTime _lastwritetime;
        private long _length;
        private bool _hasalbumartist;
        private bool _compilation;
        private bool _touched = false;
        private CodecType _codectype = CodecType.Lossy;
        private string _codecname = "";
        private string _tagtype = "";
        private uint _channels = 0;
        private uint _samplerate = 0;
        private uint _bitspersample = 0;
        private uint _averagebitrate = 0;
        private uint _maxbitrate = 0;
        private int _durationinseconds = 0;
        private string _releasedate = null;
        private string _genre = null;
        private string _composer = null;
        private string _grouping = null;
        private Dictionary<string, string[]> _metadata =
            new(StringComparer.OrdinalIgnoreCase);
        private int? _discnumber = null;
        private int? _disctotal = null;
        [NonSerialized]
        private string _strippedalbum = null;

        public MetadataCacheEntry(IMediaFile file, DateTime lastwritetime)
        {
            var mp = file.Tags.First();
            var codec = file.Codecs.First();

            _lastwritetime = lastwritetime;
            _title = mp.Title;
            _album = mp.Album;
            _artist = mp.Artist;
            _hasalbumartist = mp.HasAlbumArtist &&
                !string.IsNullOrWhiteSpace(mp.AlbumArtist);
            _albumartist = _hasalbumartist ? mp.AlbumArtist : string.Empty;
            KeyValuePair<TagFields, string>[] known =
                mp.GetKnownMetadata().ToArray();
            SetIndexedMetadata(known);
            IEnumerable<KeyValuePair<string, string>> cached =
                known.Select(field =>
                    KeyValuePair.Create(
                        field.Key.ToString(),
                        field.Value));
            if (mp is IUserStringMetadata custom)
                cached = cached.Concat(
                    custom.GetAddressableUserStrings().Select(field =>
                        KeyValuePair.Create(
                            CachedMetadataKeys.Custom(field.Key),
                            field.Value)));
            SetCachedMetadata(cached);
            _tracknumber = mp.TrackNumber;
            _tracktotal = mp.TrackTotal;
            _discnumber = mp.DiscNumber;
            _disctotal = mp.DiscTotal;
            _releasedate = mp.ReleaseDate;
            _tagtype = mp.TagType;
            
            if (codec != null)
            {
                _codecname = codec.CodecName;
                _codectype = codec.CodecType;
                _channels = codec.Channels;
                _bitspersample = codec.BitsPerSample;
                _samplerate = codec.Samplerate;
                _averagebitrate = codec.AverageBitrate;
                _maxbitrate = codec.MaxBitrate;
                _durationinseconds = (int)codec.DurationInSeconds;
            }
        }

        public string CodecName => _codecname;
        public string TagType => _tagtype;
        public CodecType CodecType => _codectype;
        public uint Channels => _channels;
        public uint BitsPerSample => _bitspersample;
        public uint SampleRate => _samplerate;
        public uint AverageBitRate => _averagebitrate;
        public uint MaxBitRate => _maxbitrate;

        public string Title => _title;
        public string Album => _album;
        public string StrippedAlbum => _strippedalbum;
        public string Artist => _artist;
        public string AlbumArtist => _albumartist;
        public bool HasAlbumArtist => _hasalbumartist;
        public bool Compilation => _compilation;
        public int? TrackNumber => _tracknumber;
        public int? TrackTotal => _tracktotal;
        public int? DiscNumber => _discnumber;
        public int? DiscTotal => _disctotal;
        public string ReleaseDate => _releasedate;
        public string Genre => _genre;
        public string Composer => _composer;
        public string Grouping => _grouping;
        public int? Year => ParseYear(_releasedate);
        public DateTime LastWriteTime => _lastwritetime;
        public long Length => _length;
        public int DurationInSeconds => _durationinseconds;
        public IReadOnlyDictionary<string, string[]> Metadata =>
            _metadata;

        private static bool IsTrue(string value) =>
            value is not null &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", StringComparison.OrdinalIgnoreCase));

        internal void SetIndexedMetadata(
            string genre,
            string composer,
            string grouping)
        {
            _genre = genre;
            _composer = composer;
            _grouping = grouping;
        }

        private void SetIndexedMetadata(
            IEnumerable<KeyValuePair<TagFields, string>> metadata)
        {
            foreach (KeyValuePair<TagFields, string> item in metadata)
            {
                switch (item.Key)
                {
                    case TagFields.Compilation:
                        _compilation |= IsTrue(item.Value);
                        break;
                    case TagFields.Genre:
                        _genre = AppendDistinct(_genre, item.Value);
                        break;
                    case TagFields.Composer:
                        _composer = AppendDistinct(_composer, item.Value);
                        break;
                    case TagFields.Grouping:
                        _grouping = AppendDistinct(_grouping, item.Value);
                        break;
                }
            }
        }

        internal void SetCachedMetadata(
            IEnumerable<KeyValuePair<string, string>> metadata)
        {
            _metadata = metadata
                .Where(field =>
                    !string.IsNullOrWhiteSpace(field.Key))
                .GroupBy(
                    field => field.Key,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(field => field.Value ?? "")
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static string AppendDistinct(string existing, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return existing;
            if (string.IsNullOrWhiteSpace(existing))
                return value;
            return existing.Split(';', StringSplitOptions.TrimEntries)
                .Contains(value, StringComparer.Ordinal)
                ? existing
                : existing + "; " + value;
        }

        private static int? ParseYear(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            Match match = releaseyearre_.Match(value);
            return match.Success && int.TryParse(match.Groups["year"].Value, out int year)
                ? year
                : null;
        }
        public void Touch()
        {
            _touched = true;
        }

        public void UnTouch()
        {
            _touched = false;
        }

        public void Strip()
        {
            _strippedalbum = stripre_.Replace(_album, "");
        }

        internal void Strip(Dictionary<string, string> sharedStrings)
        {
            // SQLite materializes a new string object for each row even when thousands of tracks
            // share an artist, album, codec, or release date. Canonicalize the low-cardinality
            // values while the cache is built, then let the temporary dictionary go; the entries
            // retain only one instance of each equal value. Titles are intentionally excluded
            // because they are usually unique and would just enlarge the temporary lookup table.
            _artist = Share(sharedStrings, _artist);
            _albumartist = Share(sharedStrings, _albumartist);
            _album = Share(sharedStrings, _album);
            _codecname = Share(sharedStrings, _codecname);
            _releasedate = Share(sharedStrings, _releasedate);
            _genre = Share(sharedStrings, _genre);
            _composer = Share(sharedStrings, _composer);
            _grouping = Share(sharedStrings, _grouping);
            _strippedalbum = Share(sharedStrings, stripre_.Replace(_album, ""));
        }

        private static string Share(Dictionary<string, string> sharedStrings, string value)
        {
            if (value is null)
                return null;
            if (sharedStrings.TryGetValue(value, out string shared))
                return shared;
            sharedStrings.Add(value, value);
            return value;
        }

        public bool Touched => _touched;

        public string FormatPath(int length, int discnumlength)
        {
            string art = (string.IsNullOrWhiteSpace(AlbumArtist) ? Artist : AlbumArtist).LimitLength(length);
            string alb = StrippedAlbum.FormatDisc(length, discnumlength);
            string ttl = Title.LimitLength(length);
            var m = MetadataExtensions.DiscNumRegex.Match(alb);
            art = art.FixPath();
            alb = alb.FixPath();
            ttl = ttl.FixPath();
            string tgt = Path.Combine(art, alb, ((TrackNumber != null) ? (TrackNumber.Value.ToString("D2") + " ") : "") + ttl);
            return tgt;
        }

    }

    public class MetadataCache
    {
        // Compatibility view for older callers. MediaFormatRegistry owns the capability data.
        public static readonly string[] ValidExtensions = MediaFormatRegistry.Default
            .GetExtensions(MediaFormatCapabilities.LibraryIndex).ToArray();

        private Dictionary<string, MetadataCacheEntry> _filecache = new Dictionary<string, MetadataCacheEntry>();
        private Dictionary<string, List<string>> _albumcache = new Dictionary<string, List<string>>();
        private Dictionary<string, List<string>> _albumartistcache = new Dictionary<string, List<string>>();
        private Dictionary<string, List<string>> _artistcache = new Dictionary<string, List<string>>();
        private readonly bool _buildSecondaryIndexes;

        public Dictionary<string, MetadataCacheEntry> FileCache
        {
            get
            {
                return _filecache;
            }
        }

        public Dictionary<string, List<string>> ArtistCache
        {
            get
            {
                return _artistcache;
            }
        }

        public IEnumerable<string> Artists
        {
            get
            {
                return _artistcache.Keys;
            }
        }

        public MetadataCache(bool buildSecondaryIndexes = true)
        {
            _buildSecondaryIndexes = buildSecondaryIndexes;
        }

        public Dictionary<string, List<string>> AlbumArtistCache
        {
            get
            {
                return _albumartistcache;
            }
        }

        public IEnumerable<string> AlbumArtists
        {
            get
            {
                return _albumartistcache.Keys;
            }
        }

        public Dictionary<string, List<string>> AlbumCache
        {
            get
            {
                return _albumcache;
            }
        }

        public IEnumerable<string> Albums
        {
            get
            {
                return _albumcache.Keys;
            }
        }

        public MetadataCacheEntry this[string k]
        {
            get
            {
                return _filecache[k];
            }
        }

        public IEnumerable<string> Files
        {
            get
            {
                return _filecache.Keys;
            }
        }
        
        // Note: the former BinaryFormatter-based Save/Load lived here. BinaryFormatter was
        // removed from the runtime in .NET 9+, and the SQLite-backed MetadataDatabase is the
        // current persistence path, so they were deleted rather than ported.

        internal void AddDBCacheEntry(string file, MetadataCacheEntry ce)
        {
            _filecache.Add(file, ce);
            if (!_buildSecondaryIndexes)
                return;
            if (!_albumcache.ContainsKey(ce.Album))
                _albumcache.Add(ce.Album, new List<string>());
            if (!_artistcache.ContainsKey(ce.Artist))
                _artistcache.Add(ce.Artist, new List<string>());
            if (!_albumartistcache.ContainsKey(ce.AlbumArtist))
                _albumartistcache.Add(ce.AlbumArtist, new List<string>());
            _albumcache[ce.Album].Add(file);
            _artistcache[ce.Artist].Add(file);
            _albumartistcache[ce.AlbumArtist].Add(file);
        }

        private void CrossReferenceFile(string file)
        {
            MetadataCacheEntry ce = _filecache[file];
            if (!_albumcache.ContainsKey(ce.Album))
                _albumcache.Add(ce.Album, new List<string>());
            if (!_artistcache.ContainsKey(ce.Artist))
                _artistcache.Add(ce.Artist, new List<string>());
            if (!_albumartistcache.ContainsKey(ce.AlbumArtist))
                _albumartistcache.Add(ce.AlbumArtist, new List<string>());
            _albumcache[ce.Album].Add(file);
            _artistcache[ce.Artist].Add(file);
            _albumartistcache[ce.AlbumArtist].Add(file);
        }

        private void UnReferenceFile(string file)
        {
            MetadataCacheEntry ce = _filecache[file];
            _albumcache[ce.Album].Remove(file);
            _artistcache[ce.Artist].Remove(file);
            _albumartistcache[ce.AlbumArtist].Remove(file);
            if (_albumcache[ce.Album].Count == 0)
                _albumcache.Remove(ce.Album);
            if (_artistcache[ce.Artist].Count == 0)
                _artistcache.Remove(ce.Artist);
            if (_albumartistcache[ce.AlbumArtist].Count == 0)
                _albumartistcache.Remove(ce.AlbumArtist);
        }

        [Obsolete]
        public void BeginBuildCache()
        {
            foreach (KeyValuePair<string, MetadataCacheEntry> kv in _filecache)
                kv.Value.UnTouch();
        }

        [Obsolete]
        public void EndBuildCache()
        {
            List<string> toremove = new List<string>();
            foreach (KeyValuePair<string, MetadataCacheEntry> kv in _filecache)
            {
                if (!kv.Value.Touched)
                {
                    UnReferenceFile(kv.Key);
                    toremove.Add(kv.Key);
                }
            }
            foreach (string key in toremove)
                _filecache.Remove(key);
            foreach (var f in _filecache)
                f.Value.Strip();
        }

        [Obsolete]
        public void BuildCache(string basepath, bool untouchall = true)
        {
            if (untouchall)
                BeginBuildCache();

            LogConsole.WriteLine(LogVerbosity.Chatty, "Checking Directory - " + basepath);

            DirectoryInfo di = new DirectoryInfo(basepath);
            var files = di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Where(fsi =>
                MediaFormatRegistry.Default.SupportsPath(fsi.FullName, MediaFormatCapabilities.LibraryIndex) &&
                ((fsi.Attributes & FileAttributes.Directory) == 0)).ToArray();

            /*string[] subdirs = Directory.GetDirectories(basepath);
            foreach (string subdir in subdirs)
                BuildCache(subdir, false);

            List<string> files = new List<string>(Directory.GetFiles(basepath, "*.mp3"));
            files.AddRange(Directory.GetFiles(basepath, "*.dsf"));
            files.AddRange(Directory.GetFiles(basepath, "*.m4a"));
            files.AddRange(Directory.GetFiles(basepath, "*.ogg"));
            files.AddRange(Directory.GetFiles(basepath, "*.flac"));
            */

            var filestoscan = new List<(string FileName, DateTime LastModifiedTime)>();

            foreach (var fi in files)
            {
                LogConsole.WriteLine(LogVerbosity.Verbose, "Checking File - " + fi.FullName);

                if (_filecache.ContainsKey(fi.FullName))
                {
                    if (fi.LastWriteTimeUtc > _filecache[fi.FullName].LastWriteTime)
                    {
                        UnReferenceFile(fi.FullName);
                        filestoscan.Add((fi.FullName, fi.LastWriteTimeUtc));
                        //_filecache[fi.FullName] = new MetadataCacheEntry(Metadata.GetProvider(fi.FullName));
                        //CrossReferenceFile(fi.FullName);
                    }
                    else
                        _filecache[fi.FullName].Touch();
                }
                else
                {
                    filestoscan.Add((fi.FullName, fi.LastWriteTimeUtc));
                    //_filecache[fi.FullName] = new MetadataCacheEntry(Metadata.GetProvider(fi.FullName));
                    //CrossReferenceFile(fi.FullName);
                }
            }

            var bag = new ConcurrentBag<(string FileName, MetadataCacheEntry Entry)>();
            Parallel.ForEach(filestoscan, (file) =>
            {
                bag.Add((file.FileName, new MetadataCacheEntry(
                    MediaFile.GetFile(file.FileName, readOnly: true), file.LastModifiedTime)));
            });
            foreach (var entry in bag)
            {
                entry.Entry.Touch();
                _filecache[entry.FileName] = entry.Entry;
                CrossReferenceFile(entry.FileName);
            }

            if (untouchall)
                EndBuildCache();
        }


    }
}
