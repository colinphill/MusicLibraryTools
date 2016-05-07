using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using ConsoleTools;

namespace MusicFileUtilities
{

    [Serializable]
    public class MetadataCacheEntry : IMetadataProvider
    {
        private string _title = "";
        private string _album = "";
        private string _artist = "";
        private string _albumartist = "";
        private int _tracknumber = 0;
        private DateTime _scantime = DateTime.UtcNow;
        private bool _touched = false;

        public MetadataCacheEntry(IMetadataProvider mp)
        {

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
        }

        public string Title
        {
            get
            {
                return _title;
            }
        }

        public string Album
        {
            get
            {
                return _album;
            }
        }

        public string Artist
        {
            get
            {
                return _artist;
            }
        }

        public string AlbumArtist
        {
            get
            {
                return _albumartist;
            }
        }

        public int TrackNumber
        {
            get
            {
                return _tracknumber;
            }
        }

        public DateTime ScanTime
        {
            get
            {
                return _scantime;
            }
        }

        public void Touch()
        {
            _touched = true;
        }

        public void UnTouch()
        {
            _touched = false;
        }

        public bool Touched
        {
            get
            {
                return _touched;
            }
        }

    }

    public class MetadataCache
    {
        private Dictionary<string, MetadataCacheEntry> _filecache = new Dictionary<string, MetadataCacheEntry>();
        private Dictionary<string, List<string>> _albumcache = new Dictionary<string, List<string>>();
        private Dictionary<string, List<string>> _albumartistcache = new Dictionary<string, List<string>>();
        private Dictionary<string, List<string>> _artistcache = new Dictionary<string, List<string>>();

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

        public IMetadataProvider this[string k]
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

        public void BuildCache(string basepath, bool untouchall = true)
        {
            if (untouchall)
            {
                foreach (KeyValuePair<string, MetadataCacheEntry> kv in _filecache)
                    kv.Value.UnTouch();
            }

            LogConsole.WriteLine(LogVerbosity.Chatty, "Checking Directory - " + basepath);

            string[] subdirs = Directory.GetDirectories(basepath);
            foreach (string subdir in subdirs)
                BuildCache(subdir, false);

            List<string> files = new List<string>(Directory.GetFiles(basepath, "*.mp3"));
            files.AddRange(Directory.GetFiles(basepath, "*.m4a"));
            files.AddRange(Directory.GetFiles(basepath, "*.ogg"));
            files.AddRange(Directory.GetFiles(basepath, "*.flac"));
            files.AddRange(Directory.GetFiles(basepath, "*.wma"));

            foreach (string file in files)
            {
                LogConsole.WriteLine(LogVerbosity.Verbose, "Checking File - " + file);

                if (_filecache.ContainsKey(file))
                {
                    if (File.GetLastWriteTimeUtc(file) > _filecache[file].ScanTime)
                    {
                        UnReferenceFile(file);
                        _filecache[file] = new MetadataCacheEntry(Metadata.GetProvider(file));
                        CrossReferenceFile(file);
                    }
                }
                else
                {
                    _filecache[file] = new MetadataCacheEntry(Metadata.GetProvider(file));
                    CrossReferenceFile(file);
                }
                _filecache[file].Touch();
            }

            if (untouchall)
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
            }
        }


    }
}
