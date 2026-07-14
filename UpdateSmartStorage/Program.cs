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
using iTunes.Binary;

namespace UpdateSmartStorage
{
    public class Program
    {

        static SHA1 sha1_ = SHA1.Create();

        const int BUCKET_THRESHOLD = 200;
        const int MAX_MAPPED_NAME_LENGTH = 40;
        const int BUCKET_DIGITS = 4;
        const int MAX_PLAYLIST_COUNT = 500;
        const string TARGET_MARKER = ".update-smart-storage-root";
        const string DATABASE_COMMIT_MANIFEST = ".update-smart-storage-database-commit";
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

        static bool PathsOverlap(string first, string second)
        {
            string a = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string b = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }

        static bool IsPlaylistTrackEligible(ItlTrack track)
        {
            string kind = track.Kind ?? string.Empty;
            return !string.IsNullOrWhiteSpace(track.LocalPath) && !track.HasVideo &&
                !kind.Contains("protected", StringComparison.OrdinalIgnoreCase) &&
                !kind.Contains("book", StringComparison.OrdinalIgnoreCase) &&
                !kind.Contains("audible", StringComparison.OrdinalIgnoreCase) &&
                !kind.Contains("document", StringComparison.OrdinalIgnoreCase) &&
                !kind.Contains("app", StringComparison.OrdinalIgnoreCase) &&
                !kind.Contains("tone", StringComparison.OrdinalIgnoreCase);
        }

        static void EnsureSafeTarget(string baseDir, bool initialize)
        {
            string root = Path.GetPathRoot(baseDir);
            if (string.Equals(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to use a filesystem root as the destination.");

            if (!Directory.Exists(baseDir))
            {
                if (!initialize)
                    throw new InvalidOperationException("The destination does not exist. Create it deliberately with --initialize.");
                Directory.CreateDirectory(baseDir);
            }

            string marker = Path.Combine(baseDir, TARGET_MARKER);
            bool knownDatabase = File.Exists(Path.Combine(baseDir, "filedb.xml"));
            if (!File.Exists(marker) && !knownDatabase && !initialize)
                throw new InvalidOperationException($"The destination is not initialized. Verify it and rerun with --initialize to create {TARGET_MARKER}.");

            if (!File.Exists(marker))
                File.WriteAllText(marker, "UpdateSmartStorage managed target" + Environment.NewLine);
        }

        static void ValidateMappedNames(FileDatabase database, IEnumerable<string> playlistNames)
        {
            var collisions = new List<string>();
            foreach (var group in database.Artists.GroupBy(artist => $"{artist.Bucket}:{artist.MappedName}", StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                collisions.Add("artist target " + group.Key + ": " + string.Join(", ", group.Select(artist => artist.Name)));

            foreach (FileDatabase.Artist artist in database.Artists)
            {
                foreach (var group in artist.Albums.GroupBy(album => album.MappedName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                    collisions.Add($"albums under '{artist.Name}' mapped to '{group.Key}': {string.Join(", ", group.Select(album => album.Name))}");
                foreach (FileDatabase.Album album in artist.Albums)
                    foreach (var group in album.Tracks.GroupBy(track => track.MappedName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                        collisions.Add($"tracks under '{artist.Name} / {album.Name}' mapped to '{group.Key}'");
            }

            foreach (var group in playlistNames.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                collisions.Add("playlist target " + group.Key);

            if (collisions.Count != 0)
                throw new InvalidOperationException("Mapped-name collisions must be resolved before syncing:" + Environment.NewLine + string.Join(Environment.NewLine, collisions.Select(collision => "  " + collision)));
        }

        static void WriteAllBytesAtomically(string destination, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, destination, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        static void CopyAtomically(string source, string destination, bool overwrite = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, false);
                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    stream.Flush(true);
                File.Move(temporary, destination, overwrite);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
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
                        Tracks.Add(t = def);
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

        static void WriteDatabaseCommitManifest(string baseDir, string token, bool artworkExisted, bool fileExisted)
        {
            string manifest = Path.Combine(baseDir, DATABASE_COMMIT_MANIFEST);
            string temporary = manifest + ".tmp-" + token;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
                {
                    writer.WriteLine(token);
                    writer.WriteLine(artworkExisted ? "1" : "0");
                    writer.WriteLine(fileExisted ? "1" : "0");
                    writer.Flush();
                    stream.Flush(true);
                }
                File.Move(temporary, manifest);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        static void RecoverIncompleteDatabaseCommit(string baseDir)
        {
            string manifest = Path.Combine(baseDir, DATABASE_COMMIT_MANIFEST);
            if (!File.Exists(manifest))
                return;

            string[] lines = File.ReadAllLines(manifest);
            if (lines.Length != 3 || !Guid.TryParseExact(lines[0], "N", out _) ||
                (lines[1] != "0" && lines[1] != "1") || (lines[2] != "0" && lines[2] != "1"))
                throw new InvalidDataException($"Invalid database commit manifest: {manifest}");

            string token = lines[0];
            string artworkTarget = Path.Combine(baseDir, "artworkdb.bin");
            string fileTarget = Path.Combine(baseDir, "filedb.xml");
            string artworkTemporary = artworkTarget + ".tmp-" + token;
            string fileTemporary = fileTarget + ".tmp-" + token;
            string artworkBackup = artworkTarget + ".bak-" + token;
            string fileBackup = fileTarget + ".bak-" + token;

            void Restore(string target, string temporary, string backup, bool existed)
            {
                if (existed)
                {
                    if (File.Exists(backup))
                        CopyAtomically(backup, target);
                    else if (!File.Exists(temporary))
                        throw new IOException($"Cannot recover '{target}': both its staged file and rollback backup are missing.");
                }
                else if (!File.Exists(temporary) && File.Exists(target))
                    File.Delete(target);
            }

            Restore(artworkTarget, artworkTemporary, artworkBackup, lines[1] == "1");
            Restore(fileTarget, fileTemporary, fileBackup, lines[2] == "1");

            // The old pair is consistent again. Remove the manifest before cleaning staging
            // artifacts so recovery itself is idempotent across a crash during cleanup.
            File.Delete(manifest);
            foreach (string path in new[] { artworkTemporary, fileTemporary, artworkBackup, fileBackup })
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            LogConsole.WriteLine("Recovered an interrupted database commit.");
        }

        static void WriteDatabasesAtomically(string baseDir, FileDatabase fileDatabase, ArtworkDatabase artworkDatabase)
        {
            string artworkTarget = Path.Combine(baseDir, "artworkdb.bin");
            string fileTarget = Path.Combine(baseDir, "filedb.xml");
            string token = Guid.NewGuid().ToString("N");
            string artworkTemporary = artworkTarget + ".tmp-" + token;
            string fileTemporary = fileTarget + ".tmp-" + token;
            string artworkBackup = artworkTarget + ".bak-" + token;
            string fileBackup = fileTarget + ".bak-" + token;
            string manifest = Path.Combine(baseDir, DATABASE_COMMIT_MANIFEST);
            bool artworkExisted = File.Exists(artworkTarget);
            bool fileExisted = File.Exists(fileTarget);

            try
            {
                using (var stream = new FileStream(artworkTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fileDatabase.Artworks.Clear();
                    foreach (ArtworkDatabase.Artwork artwork in artworkDatabase.Artworks)
                    {
                        long offset = stream.Position;
                        fileDatabase.Artworks.Add(new FileDatabase.Artwork
                        {
                            FileType = artwork.FileType,
                            Hash = artwork.Hash,
                            Offset = offset,
                            Length = artwork.Data.Length
                        });
                        stream.Write(artwork.Data, 0, artwork.Data.Length);
                    }
                    stream.Flush(true);
                }

                using (var stream = new FileStream(fileTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fileDatabase.Serialize(stream);
                    stream.Flush(true);
                }

                // The durable manifest is published only after both staged files are flushed,
                // and before either live database is replaced. Startup recovery always rolls a
                // manifest-backed partial commit back to the previous consistent generation.
                WriteDatabaseCommitManifest(baseDir, token, artworkExisted, fileExisted);

                if (artworkExisted)
                    File.Replace(artworkTemporary, artworkTarget, artworkBackup, true);
                else
                    File.Move(artworkTemporary, artworkTarget);

                if (fileExisted)
                    File.Replace(fileTemporary, fileTarget, fileBackup, true);
                else
                    File.Move(fileTemporary, fileTarget);

                // Removing the manifest commits the pair. Backups are intentionally deleted
                // afterward, so a crash can only yield either a recoverable manifest or a fully
                // committed pair (possibly with harmless leftover backups).
                File.Delete(manifest);
                try { if (File.Exists(artworkBackup)) File.Delete(artworkBackup); } catch { }
                try { if (File.Exists(fileBackup)) File.Delete(fileBackup); } catch { }
            }
            catch (Exception commitException)
            {
                try
                {
                    RecoverIncompleteDatabaseCommit(baseDir);
                }
                catch (Exception recoveryException)
                {
                    throw new AggregateException("Database commit failed and automatic recovery was incomplete. The commit manifest was preserved for the next run.", commitException, recoveryException);
                }
                throw;
            }
            finally
            {
                if (!File.Exists(manifest))
                    foreach (string path in new[] { artworkTemporary, fileTemporary, artworkBackup, fileBackup })
                        try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        static void Main(string[] args)
        {
            LogConsole.SwitchFile("UpdateSmartStorage.log");

            bool apply = args.Skip(1).Any(arg => arg.Equals("--apply", StringComparison.OrdinalIgnoreCase));
            bool initialize = args.Skip(1).Any(arg => arg.Equals("--initialize", StringComparison.OrdinalIgnoreCase));
            int maxRemovals = 0;
            bool validArguments = args.Length != 0 && !args[0].StartsWith("--", StringComparison.Ordinal);
            for (int i = 1; validArguments && i < args.Length; i++)
            {
                if (args[i].Equals("--apply", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--initialize", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (args[i].Equals("--max-removals", StringComparison.OrdinalIgnoreCase) && ++i < args.Length && int.TryParse(args[i], out maxRemovals) && maxRemovals >= 0)
                    continue;
                validArguments = false;
            }

            if (!validArguments)
            {
                LogConsole.WriteLine("Usage: UpdateSmartStorage <destination> --apply [--initialize] [--max-removals <count>]");
                LogConsole.Close();
                return;
            }

            if (!apply)
            {
                LogConsole.WriteLine("No changes made. Pass --apply after verifying the required destination path.");
                LogConsole.Close();
                return;
            }

            string basedir = Path.GetFullPath(args[0]);

            LogConsole.WriteLine("Loading iTunes Library...");

            ItlLibrary lib = ItlLibrary.Load(ItlFileEditor.ResolveLibraryPath());
            Dictionary<int, ItlTrack> tracksById = lib.Tracks.ToDictionary(track => track.Id);
            if (string.IsNullOrWhiteSpace(lib.MusicFolderPath))
            {
                LogConsole.WriteLine("The binary library does not contain a source music folder; no changes were made.");
                LogConsole.Close();
                return;
            }

            if (PathsOverlap(basedir, lib.MusicFolderPath))
            {
                LogConsole.WriteLine("Refusing to continue because the destination overlaps the source iTunes music folder.");
                LogConsole.Close();
                return;
            }

            try
            {
                EnsureSafeTarget(basedir, initialize);
                RecoverIncompleteDatabaseCommit(basedir);
            }
            catch (Exception ex)
            {
                LogConsole.WriteLine("Destination safety check failed: " + ex.Message);
                LogConsole.Close();
                return;
            }

            if (!basedir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                basedir = basedir + Path.DirectorySeparatorChar;

            string playlistsdir = Path.Combine(basedir, "Playlists");

            Dictionary<string, DateTime> filetimes = new Dictionary<string, DateTime>(StringComparer.CurrentCultureIgnoreCase);

            Console.WriteLine("Enumerating Current Files");
            EnumerateDirectory(filetimes, new DirectoryInfo(lib.MusicFolderPath));

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
                            s.ReadExactly(aart.Data);
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
            var previousPlaylistFiles = new HashSet<string>(
                fdb.Playlists.Select(playlist => FixPath(playlist.Name) + ".m3u"),
                StringComparer.OrdinalIgnoreCase);

            ItlTrack[] library = lib.Tracks.Where(track =>
                !string.IsNullOrWhiteSpace(track.LocalPath) &&
                (track.Kind ?? "").Contains("audio file", StringComparison.OrdinalIgnoreCase) &&
                !(track.Kind ?? "").Contains("protected", StringComparison.OrdinalIgnoreCase)).ToArray();
            int libindex = 0;
            foreach (ItlTrack sourceTrack in library)
            {
                string loc = sourceTrack.LocalPath!;
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
                string artist = string.IsNullOrEmpty(sourceTrack.AlbumArtist) ? sourceTrack.Artist ?? string.Empty : sourceTrack.AlbumArtist;
                string album = sourceTrack.Album ?? string.Empty;
                string title = sourceTrack.Title ?? string.Empty;
                int tracknumber = sourceTrack.TrackNumber;
                FileDatabase.Track trk = new FileDatabase.Track();
                trk.Index = tracknumber;
                trk.LastModifiedTime = dt;
                trk.Loc = loc;
                trk.FileName = Path.GetFileName(loc);
                trk.Name = title;
                trk.Year = sourceTrack.Year;
                trk.ContributingArtist = sourceTrack.Artist ?? string.Empty;
                trk.PersistentID = sourceTrack.PersistentIdString;
                trk.Genre = sourceTrack.Genre ?? string.Empty;
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

            string[] plannedPlaylistFiles = lib.Playlists
                .Where(playlist => playlist.TrackIds.Count <= MAX_PLAYLIST_COUNT && !playlist.IsMaster)
                .Select(playlist => FixPath(playlist.DisplayName) + ".m3u")
                .ToArray();
            try
            {
                ValidateMappedNames(fdb, plannedPlaylistFiles);
            }
            catch (Exception ex)
            {
                LogConsole.WriteLine(ex.Message);
                LogConsole.Close();
                return;
            }

            int staleTrackCount = fdb.Artists.SelectMany(artist => artist.Albums).SelectMany(album => album.Tracks).Count(track => !track.Touched);
            if (staleTrackCount > maxRemovals)
            {
                LogConsole.WriteLine($"Safety stop: {staleTrackCount} stale tracks exceeds --max-removals {maxRemovals}. No library files were changed.");
                LogConsole.Close();
                return;
            }

            string[] missingSources = fdb.Artists
                .SelectMany(artist => artist.Albums)
                .SelectMany(album => album.Tracks)
                .Where(track => track.New && !File.Exists(track.Loc))
                .Select(track => track.Loc)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missingSources.Length != 0)
            {
                foreach (string source in missingSources)
                    LogConsole.WriteLine("Missing source file: " + source);
                LogConsole.WriteLine("Aborting before destination changes because one or more source files are unavailable.");
                LogConsole.Close();
                return;
            }

            Tuple<FileDatabase.Artist, FileDatabase.Album, FileDatabase.Track>[] alltracks = fdb.Artists
                .SelectMany(artist => artist.Albums.SelectMany(album => album.Tracks
                    .Where(track => track.Touched)
                    .Select(track => Tuple.Create(artist, album, track))))
                .ToArray();

            var currentByPersistentId = alltracks.ToLookup(track => track.Item3.PersistentID, StringComparer.OrdinalIgnoreCase);
            string[] invalidPlaylistIds = lib.Playlists
                .Where(playlist => playlist.TrackIds.Count <= MAX_PLAYLIST_COUNT && !playlist.IsMaster)
                .SelectMany(playlist => playlist.TrackIds.Select(item => tracksById[item]))
                .Where(IsPlaylistTrackEligible)
                .Select(track => track.PersistentIdString)
                .Where(id => currentByPersistentId[id].Count() != 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (invalidPlaylistIds.Length != 0)
            {
                foreach (string id in invalidPlaylistIds)
                    LogConsole.WriteLine("Playlist track could not be mapped uniquely: " + id);
                LogConsole.WriteLine("Aborting before destination changes because playlist mapping is incomplete.");
                LogConsole.Close();
                return;
            }

            string quarantineRoot = basedir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                ".UpdateSmartStorage-quarantine" + Path.DirectorySeparatorChar + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            var quarantined = new List<(string Original, string Quarantine)>();
            var createdFiles = new List<string>();

            try
            {

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
                            string quarantine = Path.Combine(quarantineRoot, Path.GetRelativePath(basedir, filename));
                            Console.WriteLine("Quarantining: " + filename + " -> " + quarantine);
                            if (File.Exists(filename))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
                                File.Move(filename, quarantine);
                                quarantined.Add((filename, quarantine));
                            }
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
                        stalealbums.Add(al);
                    }
                }
                foreach (FileDatabase.Album tr in stalealbums)
                    ar.Albums.Remove(tr);
                if (!ar.Touched)
                {
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
                            LogConsole.WriteLine("Copying " + tr.Loc + " To " + filename);
                            if (File.Exists(filename))
                            {
                                string previous = Path.Combine(quarantineRoot, Path.GetRelativePath(basedir, filename));
                                if (File.Exists(previous))
                                    previous += "." + Guid.NewGuid().ToString("N");
                                Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                                File.Move(filename, previous);
                                quarantined.Add((filename, previous));
                            }
                            CopyAtomically(tr.Loc, filename, overwrite: false);
                            createdFiles.Add(filename);
                        }
                    }
                }
            }

            adb.Artworks = artdict.Where(kv=> kv.Value.Touched).Select(kv => new ArtworkDatabase.Artwork { Touched = true, Hash = kv.Key, FileType = kv.Value.FileType, Data = kv.Value.Data }).ToList();
 
#endif

            Console.WriteLine("Updating User Playlists");

            Directory.CreateDirectory(playlistsdir);
            fdb.Playlists.Clear();
            var desiredPlaylistFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ItlPlaylist pl in lib.Playlists)
            {
                int icount = 0;
                if (pl.TrackIds.Count > MAX_PLAYLIST_COUNT || pl.IsMaster)
                    continue;

                FileDatabase.Playlist fpl = new FileDatabase.Playlist();
                fpl.Name = pl.DisplayName;

                string plname = FixPath(pl.DisplayName) + ".m3u";

                using (MemoryStream plms = new MemoryStream())
                {
                    using (StreamWriter plwriter = new StreamWriter(plms, Encoding.UTF8, 5123, true))
                    {
                        plwriter.WriteLine("#EXTM3U");
                        foreach (int item in pl.TrackIds)
                        {
                            ItlTrack track = tracksById[item];
                            if (!IsPlaylistTrackEligible(track))
                                continue;
                            plwriter.WriteLine("#EXTINF:-1," + (track.Artist ?? string.Empty).Replace("-", "") + " - " + (track.Title ?? string.Empty).Replace("-", ""));
                            Tuple<FileDatabase.Artist, FileDatabase.Album, FileDatabase.Track> titem = alltracks.Single(t => t.Item3.PersistentID == track.PersistentIdString);
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
                    desiredPlaylistFiles.Add(plname);

                    byte[] b = plms.ToArray();
                    string filename = Path.Combine(playlistsdir, plname);
                    Console.WriteLine("Updating Playlist: " + filename);
                    if (File.Exists(filename))
                    {
                        string previous = Path.Combine(quarantineRoot, Path.GetRelativePath(basedir, filename));
                        if (File.Exists(previous))
                            previous += "." + Guid.NewGuid().ToString("N");
                        Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                        File.Move(filename, previous);
                        quarantined.Add((filename, previous));
                    }
                    WriteAllBytesAtomically(filename, b);
                    createdFiles.Add(filename);
                }
            }

            foreach (string stalePlaylist in previousPlaylistFiles.Except(desiredPlaylistFiles, StringComparer.OrdinalIgnoreCase))
            {
                string filename = Path.Combine(playlistsdir, stalePlaylist);
                if (File.Exists(filename))
                {
                    string quarantine = Path.Combine(quarantineRoot, Path.GetRelativePath(basedir, filename));
                    if (File.Exists(quarantine))
                        quarantine += "." + Guid.NewGuid().ToString("N");
                    Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
                    Console.WriteLine("Quarantining stale managed playlist: " + filename);
                    File.Move(filename, quarantine);
                    quarantined.Add((filename, quarantine));
                }
            }

            Console.WriteLine("Writing Artwork Database");
            Console.WriteLine("Writing Synchronization Database");
            WriteDatabasesAtomically(basedir, fdb, adb);

            // Physical directory cleanup is deliberately last and best-effort: the database and
            // all managed files already describe a consistent new snapshot at this point.
            try { MetadataExtensions.CleanEmptyMusicFolders(new DirectoryInfo(basedir), false); }
            catch (Exception ex) { LogConsole.WriteLine("Empty-directory cleanup failed: " + ex.Message); }
            }
            catch (Exception ex)
            {
                LogConsole.WriteLine("Update failed; rolling back staged filesystem changes: " + ex.Message);
                foreach (string created in createdFiles.AsEnumerable().Reverse())
                {
                    try { if (File.Exists(created)) File.Delete(created); }
                    catch (Exception rollbackEx) { LogConsole.WriteLine($"Unable to remove new file {created}: {rollbackEx.Message}"); }
                }
                foreach (var move in quarantined.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(move.Quarantine) && !File.Exists(move.Original))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(move.Original)!);
                            File.Move(move.Quarantine, move.Original);
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        LogConsole.WriteLine($"Unable to restore {move.Original}: {rollbackEx.Message}");
                    }
                }
                LogConsole.Close();
                return;
            }

            LogConsole.Close();
            return;
        }
    }

}
