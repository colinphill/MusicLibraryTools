using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using MusicFileUtilities;
using ConsoleTools;

namespace MetadataCaching
{

    [Serializable]
    public partial class MetadataCacheEntry
    {
        private static Regex stripre_ = new Regex(@" \((DSD|DSD64|DSD128|DSD256|DVD-V|DVD-A|HiRes|Hi-Res|DTS-CD)\)$", RegexOptions.IgnoreCase);

        private string _title = null;
        private string _album = null;
        private string _artist = null;
        private string _albumartist = null;
        private int? _tracknumber = null;
        private int? _tracktotal = null;
        private DateTime _lastwritetime;
        private bool _touched = false;
        private CodecType _codectype = CodecType.Lossy;
        private string _codecname = "";
        private uint _channels = 0;
        private uint _samplerate = 0;
        private uint _bitspersample = 0;
        private uint _averagebitrate = 0;
        private uint _maxbitrate = 0;
        private int _durationinseconds = 0;
        private string _releasedate = null;
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
            _albumartist = mp.AlbumArtist;
            _tracknumber = mp.TrackNumber;
            _tracktotal = mp.TrackTotal;
            _discnumber = mp.DiscNumber;
            _disctotal = mp.DiscTotal;
            _releasedate = mp.ReleaseDate;
            
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
        public int? TrackNumber => _tracknumber;
        public int? TrackTotal => _tracktotal;
        public int? DiscNumber => _discnumber;
        public int? DiscTotal => _disctotal;
        public string ReleaseDate => _releasedate;
        public DateTime LastWriteTime => _lastwritetime;
        public int DurationInSeconds => _durationinseconds;
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
        public static readonly string[] ValidExtensions = { ".dsf", ".m4a", ".mp3", ".flac", ".ogg", ".wv" };

        private Dictionary<string, MetadataCacheEntry> _filecache = new Dictionary<string, MetadataCacheEntry>();
        private Dictionary<string, List<string>> _albumcache = new Dictionary<string, List<string>>();
        private Dictionary<string, List<string>> _albumartistcache = new Dictionary<string, List<string>>();
        private Dictionary<string, List<string>> _artistcache = new Dictionary<string, List<string>>();

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

        public MetadataCache()
        {

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
        
        [Obsolete]
        public void Save(string path)
        {
            using (FileStream fs = File.Create(path))
            {
                BinaryFormatter formatter = new BinaryFormatter();
                formatter.Serialize(fs, _filecache);
                formatter.Serialize(fs, _albumcache);
                formatter.Serialize(fs, _artistcache);
                formatter.Serialize(fs, _albumartistcache);
            }
        }

        [Obsolete]
        public void Load(string path)
        {
            using (FileStream fs = File.Open(path, FileMode.Open))
            {
                BinaryFormatter formatter = new BinaryFormatter();
                _filecache = (Dictionary<string, MetadataCacheEntry>)formatter.Deserialize(fs);
                _albumcache = (Dictionary<string, List<string>>)formatter.Deserialize(fs);
                _artistcache = (Dictionary<string, List<string>>)formatter.Deserialize(fs);
                _albumartistcache = (Dictionary<string, List<string>>)formatter.Deserialize(fs);
                foreach (var f in _filecache)
                    f.Value.Strip();
            }
        }

        internal void AddDBCacheEntry(string file, MetadataCacheEntry ce)
        {
            _filecache.Add(file, ce);
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
            var files = di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Where(fsi => ValidExtensions.Contains(Path.GetExtension(fsi.FullName).ToLower()) && ((fsi.Attributes & FileAttributes.Directory) == 0)).ToArray();

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
                bag.Add((file.FileName, new MetadataCacheEntry(MediaFile.GetFile(file.FileName), file.LastModifiedTime)));
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
