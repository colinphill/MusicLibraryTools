using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using MusicFileUtilities;
using MusicLibraryTools;
using System.Xml.Serialization;
using System.Security.Cryptography;
using ConsoleTools;
using iTunes;

namespace UpdateSmartStorage
{
    public class Program
    {

        static SHA1 sha1_ = SHA1.Create();

        const int BUCKET_THRESHOLD = 200;
        const int MAX_MAPPED_NAME_LENGTH = 40;
        const int BUCKET_DIGITS = 4;
        const int MAX_PLAYLIST_COUNT = 500;
        static string FixPath(string item)
        {
            string fix = item;
            foreach (char c in Path.GetInvalidFileNameChars())
                fix = fix.Replace(c.ToString(), "");
            foreach (char c in Path.GetInvalidPathChars())
                fix = fix.Replace(c.ToString(), "");
            fix = fix.Replace("\"", "");
            fix = fix.Trim();
            while (fix.EndsWith("."))
                fix = fix.Remove(fix.Length - 1);
            return fix;
        }

        public class XmlCloneable<T>
        {
            private static XmlSerializer ser_ = new XmlSerializer(typeof(T));
            public void Serialize(Stream s)
            {
                ser_.Serialize(s, this);
            }
            public static T Deserialize(Stream s)
            {
                return (T)ser_.Deserialize(s);
            }
            public T Clone()
            {
                using (MemoryStream s = new MemoryStream())
                {
                    Serialize(s);
                    s.Seek(0, SeekOrigin.Begin);
                    return Deserialize(s);
                }
            }
        }

        public static string MapName(string name)
        {
            string newname = name;
            for (int i = 0; i < newname.Length; i++)
            {
                while ((newname.Length > i)&&(!char.IsLetterOrDigit(newname[i])))
                    newname = newname.Remove(i, 1);
            }
            if (string.IsNullOrWhiteSpace(newname))
                return "UNPRINTABLE";
            if (newname.Length > MAX_MAPPED_NAME_LENGTH)
                return newname.Substring(0, MAX_MAPPED_NAME_LENGTH);
            return newname;
        }

        static int EnumerateDirectory(Dictionary<string, DateTime> dict, DirectoryInfo dir, int count = -1)
        {
            int startcount = count;
            if (count == -1)
                count = 0;
            foreach (DirectoryInfo di in dir.EnumerateDirectories())
                count = EnumerateDirectory(dict, di, count);
            foreach (FileInfo file in dir.EnumerateFiles())
            {
                dict.Add(file.FullName, file.LastWriteTimeUtc);
                count++;
                if ((count % 1000) == 0)
                {
                    Console.Write(count + "\r");
                    Console.Out.Flush();
                }
            }
            if (startcount == -1)
                Console.WriteLine(count);
            return count;
        }

        static string GetRelativePath(string filespec, string folder)
        {
            Uri pathUri = new Uri(filespec);
            // Folders must end in a slash
            if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                folder += Path.DirectorySeparatorChar;
            }
            Uri folderUri = new Uri(folder);
            return Uri.UnescapeDataString(folderUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }

        public class ArtworkDatabase : XmlCloneable<ArtworkDatabase>
        {

            public class Artwork
            {
                public string Hash { get; set; }
                public string FileType { get; set; }
                public byte[] Data { get; set; }
                [XmlIgnore]
                public bool Touched { get; set; }
            }

            [XmlArray("Artworks")]
            [XmlArrayItem("Artwork")]
            public List<Artwork> Artworks { get; set; } = new List<Artwork>();

        }

        public class FileDatabase : XmlCloneable<FileDatabase>
        {
            public class Artwork
            {
                public string Hash { get; set; }
                public string FileType { get; set; }
                public long Offset { get; set; }
                public int Length { get; set; }
            }

            public class Track
            {
                public int Index { get; set; }
                [XmlIgnore]
                public int Year { get; set; }
                public DateTime LastModifiedTime { get; set; }
                [XmlIgnore]
                public string Loc { get; set; }
                public string FileName { get; set; }
                public string Name { get; set; }
                public string Genre { get; set; }
                public string ArtworkHash { get; set; }
                public string ContributingArtist { get; set; }
                [XmlIgnore]
                public string PersistentID { get; set; }
                [XmlIgnore]
                public bool Touched { get; set; }
                [XmlIgnore]
                public bool New { get; set; }
                public string MappedName { get; set; }
                public Track(Track ot)
                {
                    Index = ot.Index;
                    Year = ot.Year;
                    LastModifiedTime = ot.LastModifiedTime;
                    Loc = ot.Loc;
                    FileName = ot.FileName;
                    Name = ot.Name;
                    ContributingArtist = ot.ContributingArtist;
                    PersistentID = ot.PersistentID;
                    Touched = ot.Touched;
                    New = ot.New;
                    Genre = ot.Genre;
                }
                public Track()
                {

                }
            }

            public class Album
            {
                public string Name { get; set; }
                [XmlArray("Tracks")]
                [XmlArrayItem("Track")]
                public List<Track> Tracks { get; set; } = new List<Track>();
                [XmlIgnore]
                public bool Touched { get; set; }
                [XmlIgnore]
                public bool New { get; set; }
                public string MappedName { get; set; }
                public Album(string name)
                {
                    Name = name;
                }
                public Album()
                {

                }
                public Track FindTrack(Track ot)
                {
                    Track def = new Track(ot);
                    def.Touched = def.New = true;
                    Track t = Tracks.SingleOrDefault(tr => tr.FileName.Equals(ot.FileName, StringComparison.CurrentCultureIgnoreCase));
                    if (t == null)
                        Tracks.Add(t = def);
                    else if (ot.LastModifiedTime > t.LastModifiedTime)
                    {
                        Tracks.Remove(t);
                        Tracks.Add(def);
                    }
                    t.Touched = true;
                    t.PersistentID = ot.PersistentID;
                    return t;
                }
            }

            public class Artist
            {
                public string Name { get; set; }
                [XmlArray("Albums")]
                [XmlArrayItem("Album")]
                public List<Album> Albums { get; set; } = new List<Album>();
                [XmlIgnore]
                public bool Touched { get; set; }
                [XmlIgnore]
                public bool New { get; set; }
                public int Bucket { get; set; }
                public string MappedName { get; set; }
                public Artist(string name)
                {
                    Name = name;
                }
                public Artist()
                {

                }
                public Album FindAlbum(string name)
                {
                    Album def = new Album(name);
                    def.Touched = def.New = true;
                    Album al = Albums.SingleOrDefault(a => a.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
                    if (al == null)
                        Albums.Add(al = def);
                    al.Touched = true;
                    return al;
                }

            }

            public class Playlist
            {
                public class Item
                {
                    public int Artist;
                    public int Album;
                    public int Track;
                }
                public string Name { get; set; }
                [XmlArray("Items")]
                [XmlArrayItem("Item")]
                public List<Item> Items { get; set; } = new List<Item>();
            }

            [XmlArray("Playlists")]
            [XmlArrayItem("Playlist")]
            public List<Playlist> Playlists { get; set; } = new List<Playlist>();

            [XmlArray("Artworks")]
            [XmlArrayItem("Artwork")]
            public List<Artwork> Artworks { get; set; } = new List<Artwork>();

            [XmlArray("Artists")]
            [XmlArrayItem("Artist")]
            public List<Artist> Artists { get; set; } = new List<Artist>();

            public int BucketFormatWidth { get; set; } = BUCKET_DIGITS;

            public Artist FindArtist(string name)
            {
                Artist def = new Artist(name);
                def.Touched = def.New = true;
                Artist ar = Artists.SingleOrDefault(a => a.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
                if (ar == null)
                    Artists.Add(ar = def);
                ar.Touched = true;
                return ar;
            }

            public void Map(FileDatabase other)
            {
                foreach (Artist oar in other.Artists)
                {
                    Artist ar = FindArtist(oar.Name);
                    ar.MappedName = MapName(ar.Name);
                    foreach (Album oal in oar.Albums)
                    {
                        Album al = ar.FindAlbum(oal.Name);
                        al.MappedName = MapName(al.Name);
                        foreach (Track otr in oal.Tracks)
                        {
                            Track tr = al.FindTrack(otr);
                            tr.MappedName = MapName(Path.GetFileNameWithoutExtension(tr.FileName)) + Path.GetExtension(tr.FileName);
                        }
                    }
                }
            }

            public void Bucketize()
            {
                Dictionary<int, int> buckets = new Dictionary<int, int>();
                List<Artist> unbucketized = new List<Artist>();
                foreach (Artist ar in Artists)
                {
                    if (ar.Bucket != 0)
                    {
                        if (buckets.ContainsKey(ar.Bucket))
                            buckets[ar.Bucket]++;
                        else
                            buckets.Add(ar.Bucket, 1);
                    }
                    else
                        unbucketized.Add(ar);
                }
                foreach (Artist ar in Artists.Where(a => !a.Touched))
                {
                    if (ar.Bucket != 0)
                        buckets[ar.Bucket]--;
                    //ar.Bucket = 0;
                }
                foreach (Artist ar in unbucketized)
                {
                    int bucket = buckets.Where(kv => kv.Value < BUCKET_THRESHOLD).Select(kv => kv.Key).OrderBy(k => k).FirstOrDefault();
                    if (bucket == 0)
                    {
                        ar.Bucket = (buckets.Count() == 0) ? 1 : buckets.Max(kv => kv.Key) + 1;
                        buckets.Add(ar.Bucket, 1);
                    }
                    else
                        buckets[ar.Bucket = bucket]++;
                }
            }

        }

        static string ReadArtwork(string filename, Dictionary<string, ArtworkDatabase.Artwork> db)
        {
            string datatype = string.Empty;
            byte[] data = null;
            string ext = Path.GetExtension(filename);
            if (ext.Equals(".m4a", StringComparison.InvariantCultureIgnoreCase))
            {
                RootAtom root = new RootAtom(filename);
                Atom_data dataatom = root.FindPath("moov.udta.meta.ilst.covr.data") as Atom_data;
                if (dataatom != null)
                {
                    data = dataatom.Data;
                    switch (dataatom.DataType)
                    {
                        case Atom_data.DataTypes.PNG:
                            datatype = "png";
                            break;
                        case Atom_data.DataTypes.JPEG:
                            datatype = "jpeg";
                            break;
                        case Atom_data.DataTypes.GIF:
                            datatype = "gif";
                            break;
                        default:
                            data = null;
                            break;
                    }
                }
            }
            else if (ext.Equals(".mp3", StringComparison.InvariantCultureIgnoreCase))
            {
                ID3v2Tag tag = new MP3File(filename);
                PictureFrame pf = tag.Frames.FirstOrDefault(f => f is PictureFrame) as PictureFrame;
                if (pf != null)
                {
                    data = pf.PictureData;
                    if (pf.MimeType.Equals("image/jpeg", StringComparison.InvariantCultureIgnoreCase) || pf.MimeType.Equals("JPG", StringComparison.InvariantCultureIgnoreCase) || pf.MimeType.Equals("JPEG", StringComparison.InvariantCultureIgnoreCase))
                        datatype = "jpeg";
                    else if (pf.MimeType.Equals("image/png", StringComparison.InvariantCultureIgnoreCase) || pf.MimeType.Equals("PNG", StringComparison.InvariantCultureIgnoreCase))
                        datatype = "png";
                    else if (pf.MimeType.Equals("image/gif", StringComparison.InvariantCultureIgnoreCase) || pf.MimeType.Equals("GIF", StringComparison.InvariantCultureIgnoreCase))
                        datatype = "gif";
                    else
                        data = null;
                }
            }
            else
            {
                Console.WriteLine();
            }
            if (data != null)
            {
                string hash = Convert.ToBase64String(sha1_.ComputeHash(data));
                if (!db.ContainsKey(hash))
                    db.Add(hash, new ArtworkDatabase.Artwork { Hash = hash, Touched = true, FileType = datatype, Data = data });
                else
                    db[hash].Touched = true;
                return hash;
            }
            return string.Empty;
        }

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("UpdateSmartStorage.log");

            /*if (args.Length == 0)
            {
                LogConsole.WriteLine("Usage: UpdateSmartStorage <destination>");
                return;
            }*/

            LogConsole.WriteLine("Loading iTunes Library XML...");

            string iTunesLibraryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "iTunes", "iTunes Music Library.xml");
            if (Environment.GetEnvironmentVariable("ITUNES_XML") != null)
                iTunesLibraryFile = Environment.GetEnvironmentVariable("ITUNES_XML");
            iTunesLibrary lib = new iTunesLibrary(iTunesLibraryFile);

            //string basedir = args[0];
            //string basedir = @"d:\testfolder\";
            string basedir = @"E:\Music\";

            if (!basedir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                basedir = basedir + Path.DirectorySeparatorChar;

            string playlistsdir = Path.Combine(basedir, "Playlists");
            try
            {
                Directory.Delete(playlistsdir, true);
            }
            catch
            {

            }

            Directory.CreateDirectory(playlistsdir);

            Dictionary<string, DateTime> filetimes = new Dictionary<string, DateTime>(StringComparer.CurrentCultureIgnoreCase);

            Console.WriteLine("Enumerating Current Files");
            EnumerateDirectory(filetimes, new DirectoryInfo(lib.LocalMusicFolder));

            Console.WriteLine("Enumerating iTunes Library");
            FileDatabase ifdb = new FileDatabase();
            FileDatabase fdb = new FileDatabase();
            ArtworkDatabase adb = new ArtworkDatabase();

            if (File.Exists(Path.Combine(basedir, "filedb.xml")))
            {
                LogConsole.WriteLine("Reading File Database");
                using (FileStream s = File.OpenRead(Path.Combine(basedir, "filedb.xml")))
                    fdb = FileDatabase.Deserialize(s);
                if (File.Exists(Path.Combine(basedir, "artworkdb.bin")))
                {
                    LogConsole.WriteLine("Reconstructing Artwork Database");
                    using (FileStream s = File.OpenRead(Path.Combine(basedir, "artworkdb.bin")))
                    {
                        foreach (FileDatabase.Artwork art in fdb.Artworks)
                        {
                            ArtworkDatabase.Artwork aart = new ArtworkDatabase.Artwork() { Hash = art.Hash, FileType = art.FileType, Data = new byte[art.Length] };
                            s.Seek(art.Offset, SeekOrigin.Begin);
                            s.Read(aart.Data, 0, art.Length);
                            adb.Artworks.Add(aart);
                        }
                    }
                }
            }
            if ((adb.Artworks.Count == 0)&&(File.Exists(Path.Combine(basedir, "artworkdb.xml"))))
            {
                LogConsole.WriteLine("Reading Artwork Database");
                using (FileStream s = File.OpenRead(Path.Combine(basedir, "artworkdb.xml")))
                    adb = ArtworkDatabase.Deserialize(s);
            }

            Dictionary<string, ArtworkDatabase.Artwork> artdict = adb.Artworks.ToDictionary(a => a.Hash);

            KeyValuePair<int, iTunesTrack>[] library = lib.Tracks.Where(kv => (kv.Value.Type == "File") && (kv.Value.Kind.Contains("audio file") && (!kv.Value.Kind.ToLower().Contains("protected")))).ToArray();
            int libindex = 0;
            foreach (var kv in library)
            {
                string loc = kv.Value.LocalLocation;
                DateTime dt;
                try
                {
                    dt = filetimes[loc];
                }
                catch
                {
                    // Outside Normal Folder
                    dt = File.GetLastWriteTimeUtc(loc);
                }
                string artist = string.IsNullOrEmpty(kv.Value.AlbumArtist) ? kv.Value.Artist : kv.Value.AlbumArtist;
                string album = kv.Value.Album;
                string title = kv.Value.Title;
                int tracknumber = kv.Value.TrackNumber ?? 0;
                FileDatabase.Track trk = new FileDatabase.Track();
                trk.Index = tracknumber;
                trk.LastModifiedTime = dt;
                trk.Loc = loc;
                trk.FileName = Path.GetFileName(loc);
                trk.Name = title;
                trk.Year = kv.Value.Year ?? 0;
                trk.ContributingArtist = kv.Value.Artist;
                trk.PersistentID = kv.Value.PersistentID;
                trk.Genre = kv.Value.Genre;
                if (!Path.GetExtension(trk.FileName).Equals(".m4p", StringComparison.InvariantCultureIgnoreCase))
                {
                    ifdb.FindArtist(artist).FindAlbum(album).Tracks.Add(trk);
                    libindex++;
                    if ((libindex % 1000) == 0)
                    {
                        Console.Write(libindex + "\r");
                        Console.Out.Flush();
                    }
                }
            }
            Console.WriteLine(libindex);

            Console.WriteLine("Mapping...");
            fdb.Map(ifdb);
            fdb.Bucketize();

            Tuple<FileDatabase.Artist, FileDatabase.Album, FileDatabase.Track>[] alltracks = fdb.Artists.SelectMany(a => a.Albums.SelectMany(al => al.Tracks.Select(tr => new Tuple<FileDatabase.Artist, FileDatabase.Album, FileDatabase.Track>(a, al, tr)))).ToArray();

            Console.WriteLine("Deleting Old Tracks/Albums/Artists");
            List<FileDatabase.Artist> staleartists = new List<FileDatabase.Artist>();
            foreach (FileDatabase.Artist ar in fdb.Artists)
            {
                string arpath = Path.Combine(basedir, ar.Bucket.ToString("D" + BUCKET_DIGITS.ToString()), ar.MappedName);
                List<FileDatabase.Album> stalealbums = new List<FileDatabase.Album>();
                foreach (FileDatabase.Album al in ar.Albums)
                {
                    string alpath = Path.Combine(arpath, al.MappedName);
                    List<FileDatabase.Track> staletracks = new List<FileDatabase.Track>();
                    foreach (FileDatabase.Track tr in al.Tracks)
                    {
                        if (!tr.Touched)
                        {
                            string filename = Path.Combine(alpath, tr.MappedName);
                            Console.WriteLine("Deleting: " + filename);
                            File.Delete(filename);
                            staletracks.Add(tr);
                        }
                        else if (!string.IsNullOrEmpty(tr.ArtworkHash))
                        {
                            if (!artdict.ContainsKey(tr.ArtworkHash))
                            {
                                string filename = Path.Combine(alpath, tr.MappedName);
                                Console.WriteLine("BUG: Fixing Missing Artwork: " + filename);
                                string hash = ReadArtwork(filename, artdict);
                                tr.ArtworkHash = hash;
                            }
                            else
                                artdict[tr.ArtworkHash].Touched = true;
                        }
                    }
                    foreach (FileDatabase.Track tr in staletracks)
                        al.Tracks.Remove(tr);
                    if (!al.Touched)
                    {
                        Console.WriteLine("Deleting: " + alpath);
                        Directory.Delete(alpath);
                        stalealbums.Add(al);
                    }
                }
                foreach (FileDatabase.Album tr in stalealbums)
                    ar.Albums.Remove(tr);
                if (!ar.Touched)
                {
                    Console.WriteLine("Deleting: " + arpath);
                    Directory.Delete(arpath);
                    staleartists.Add(ar);
                }
            }
            foreach (FileDatabase.Artist ar in staleartists)
                fdb.Artists.Remove(ar);

            Console.WriteLine("Updating Tracks/Albums/Artists");

#if true
            foreach (FileDatabase.Artist ar in fdb.Artists)
            {
                string arpath = Path.Combine(basedir, ar.Bucket.ToString("D" + BUCKET_DIGITS.ToString()), ar.MappedName);
                if (ar.New)
                    Directory.CreateDirectory(arpath);
                foreach (FileDatabase.Album al in ar.Albums)
                {
                    string alpath = Path.Combine(arpath, al.MappedName);
                    if (al.New)
                        Directory.CreateDirectory(alpath);
                    foreach (FileDatabase.Track tr in al.Tracks)
                    {
                        if (tr.New)
                        {
                            string hash = ReadArtwork(tr.Loc, artdict);
                            tr.ArtworkHash = hash;
                            string filename = Path.Combine(alpath, tr.MappedName);
                            foreach (string oldfile in Directory.GetFiles(alpath, Path.GetFileNameWithoutExtension(tr.MappedName) + ".*"))
                                File.Delete(oldfile);
                            LogConsole.WriteLine("Copying " + tr.Loc + " To " + filename);
                            File.Copy(tr.Loc, filename);
                        }
                    }
                }
            }

            adb.Artworks = artdict.Where(kv=> kv.Value.Touched).Select(kv => new ArtworkDatabase.Artwork { Touched = true, Hash = kv.Key, FileType = kv.Value.FileType, Data = kv.Value.Data }).ToList();
 
#endif

            Console.WriteLine("Updating User Playlists");

            fdb.Playlists.Clear();
            foreach (iTunesPlaylist pl in lib.Playlists.Values)
            {
                int icount = 0;
                if ((pl.Items.Count > MAX_PLAYLIST_COUNT) || ((pl.Title.ToLower() == "library")))
                    continue;

                FileDatabase.Playlist fpl = new FileDatabase.Playlist();
                fpl.Name = pl.Title;

                string plname = FixPath(pl.Title) + ".m3u";

                using (MemoryStream plms = new MemoryStream())
                {
                    using (StreamWriter plwriter = new StreamWriter(plms, Encoding.UTF8, 5123, true))
                    {
                        plwriter.WriteLine("#EXTM3U");
                        foreach (int item in pl.Items)
                        {
                            iTunesTrack track = lib.Tracks[item];
                            if (track.Kind.ToLower().Contains("video") || (track.Type.ToLower() != "file") || (track.Kind.ToLower().Contains("protected")) || (track.Kind.ToLower().Contains("book")) || (track.Kind.ToLower().Contains("audible") ||
                                track.Kind.ToLower().Contains("document") || track.Kind.ToLower().Contains("app") || track.Kind.ToLower().Contains("tone")))
                                continue;
                            plwriter.WriteLine("#EXTINF:-1," + track.Artist.Replace("-", "") + " - " + track.Title.Replace("-", ""));
                            Tuple<FileDatabase.Artist, FileDatabase.Album, FileDatabase.Track> titem = alltracks.Single(t => t.Item3.PersistentID == track.PersistentID);
                            FileDatabase.Playlist.Item plitem = new FileDatabase.Playlist.Item();
                            plitem.Artist = fdb.Artists.IndexOf(titem.Item1);
                            plitem.Album = titem.Item1.Albums.IndexOf(titem.Item2);
                            plitem.Track = titem.Item2.Tracks.IndexOf(titem.Item3);
                            fpl.Items.Add(plitem);
                            string trackfile = Path.Combine(basedir, titem.Item1.Bucket.ToString("D" + BUCKET_DIGITS.ToString()),
                                titem.Item1.MappedName, titem.Item2.MappedName, titem.Item3.MappedName);

                            plwriter.WriteLine(GetRelativePath(trackfile, Path.Combine(playlistsdir)));
                            icount++;
                        }
                    }

                    if (icount == 0)
                        continue;

                    fdb.Playlists.Add(fpl);

                    byte[] b = plms.ToArray();
                    string filename = Path.Combine(playlistsdir, plname);
                    Console.WriteLine("Updating Playlist: " + filename);
                    File.WriteAllBytes(filename, b);
                }
            }

            Console.WriteLine("Writing Artwork Database");
            //using (FileStream fs = File.Create(Path.Combine(basedir, "artworkdb.xml")))
            //    adb.Serialize(fs);
            using (FileStream fs = File.Create(Path.Combine(basedir, "artworkdb.bin")))
            {
                fdb.Artworks.Clear();
                foreach (ArtworkDatabase.Artwork art in adb.Artworks)
                {
                    long offset = fs.Position;
                    fdb.Artworks.Add(new FileDatabase.Artwork() { FileType = art.FileType, Hash = art.Hash, Offset = offset, Length = art.Data.Length });
                    fs.Write(art.Data, 0, art.Data.Length);
                }
            }

            Console.WriteLine("Writing Synchronization Database");
            using (FileStream fs = File.Create(Path.Combine(basedir, "filedb.xml")))
                fdb.Serialize(fs);


            return;
        }
    }

}
