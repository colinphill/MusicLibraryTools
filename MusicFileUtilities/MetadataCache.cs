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
using ConsoleTools;

namespace MusicFileUtilities
{

    [Serializable]
    public class MetadataCacheEntry : IMetadataProvider
    {
        private static Regex stripre_ = new Regex(@" \((DSD|DSD64|DSD128|DSD256|DVD-V|DVD-A|HiRes|Hi-Res|DTS-CD)\)$", RegexOptions.IgnoreCase);

        private string _title = "";
        private string _album = "";
        private string _artist = "";
        private string _albumartist = "";
        private int _tracknumber = 0;
        private DateTime _lastwritetime;
        private bool _touched = false;
        private CodecType _codectype = CodecType.Lossy;
        private string _codecname = "";
        private uint _channels = 0;
        private uint _samplerate = 0;
        private uint _bitspersample = 0;
        private uint _averagebitrate = 0;
        private uint _maxbitrate = 0;
        private bool _compilation = false;
        private int _durationinseconds = 0;
        [NonSerialized]
        private string _strippedalbum = "";

        public MetadataCacheEntry(IMetadataProvider mp, DateTime lastwritetime)
        {
            _lastwritetime = lastwritetime;

            try
            {
                _title = mp.Title;
            }
            catch
            {
            }
            try
            {
                _album = mp.Album;
            }
            catch
            {
            }
            try
            {
                _artist = mp.Artist;
            }
            catch
            {
            }
            try
            {
                _albumartist = mp.AlbumArtist;
            }
            catch
            {
            }
            try
            {
                _tracknumber = mp.TrackNumber;
            }
            catch
            {
            }
            try
            {
                _compilation = mp.Compilation;
            }
            catch
            {
            }
            ICodecProvider codec = mp as ICodecProvider;
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
        public int TrackNumber => _tracknumber;
        public DateTime LastWriteTime => _lastwritetime;
        public bool Compilation => _compilation;
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
            string alb = StrippedAlbum;
            string ttl = Title.LimitLength(length);
            var m = MetadataCache.DiscNumRegex.Match(alb);
            alb = m.Success ? (m.Groups[1].Value.LimitLength(discnumlength) + " (Disc " + m.Groups[2].Value + ")") : alb.LimitLength(length);
            art = art.FixPath();
            alb = alb.FixPath();
            ttl = ttl.FixPath();
            string tgt = Path.Combine(art, alb, TrackNumber.ToString("D2") + " " + ttl);
            return tgt;
        }

    }

    public class MetadataCache
    {
        public static readonly Regex DiscNumRegex = new Regex(@"(.+)[ \t]+\(Disc (.+)\)", RegexOptions.IgnoreCase);
        public static readonly string[] ValidExtensions = { ".dsf", ".m4a", ".mp3", ".flac", ".ogg" };

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

        public void BeginBuildCache()
        {
            foreach (KeyValuePair<string, MetadataCacheEntry> kv in _filecache)
                kv.Value.UnTouch();
        }

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
                bag.Add((file.FileName, new MetadataCacheEntry(Metadata.GetProvider(file.FileName), file.LastModifiedTime)));
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
