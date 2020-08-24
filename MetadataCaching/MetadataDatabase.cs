using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Data.SQLite;
using System.IO;
using MusicFileUtilities;
using System.Threading;
using System.Data.Common;

namespace MetadataCaching
{
 
    public partial class MetadataCacheEntry
    {
        public MetadataCacheEntry(DbDataReader reader)
        {
            _lastwritetime = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
            _codecname = reader[2] as string;
            _codectype = (CodecType)Enum.Parse(typeof(CodecType), reader[3] as string);
            _averagebitrate = (uint)(long)reader[4];
            _maxbitrate = (uint)(long)reader[5];
            _bitspersample = (uint)(long)reader[6];
            _samplerate = (uint)(long)reader[7];
            _channels = (uint)(long)reader[8];
            _durationinseconds = (int)(long)reader[9] / 75;
            _artist = reader[10] as string;
            _albumartist = reader[11] as string;
            _album = reader[12] as string;
            _tracknumber = (int)(long)reader[13];
            _title = reader[14] as string;
            _compilation = false;
        }

    }


    public class MetadataDatabase : IDisposable
    {
        private SQLiteConnection conn_;

        private static readonly string creationsql_ =
            "PRAGMA foreign_keys = off;\r\n" +
            "BEGIN TRANSACTION;\r\n" +
            "CREATE TABLE AlbumArtists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Albums (ID INTEGER PRIMARY KEY, ScanSetID INTEGER REFERENCES ScanSets (ID) NOT NULL, AlbumArtistID INTEGER NOT NULL REFERENCES AlbumArtists (ID), Name TEXT NOT NULL, Path TEXT NOT NULL);\r\n" +
            "CREATE TABLE Artists (ID INTEGER PRIMARY KEY, Name TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE ScanSets (ID INTEGER PRIMARY KEY, Path TEXT UNIQUE NOT NULL);\r\n" +
            "CREATE TABLE Files (ID INTEGER PRIMARY KEY, ScanSetID INTEGER REFERENCES ScanSets (ID) NOT NULL, Path TEXT NOT NULL, FileSize INTEGER NOT NULL, LastWriteTime DATETIME NOT NULL, TrackID INTEGER REFERENCES Tracks (ID), CodecName TEXT NOT NULL, CodecType TEXT NOT NULL, AverageBitrate INTEGER NOT NULL, MaxBitrate INTEGER NOT NULL, BitsPerSample INTEGER NOT NULL, SampleRate INTEGER NOT NULL, Channels INTEGER NOT NULL, DurationInFrames INTEGER NOT NULL, UNIQUE(ScanSetID, Path));\r\n" +
            "CREATE TABLE Images (ID INTEGER PRIMARY KEY, FileID INTEGER REFERENCES Files (ID) NOT NULL, Description TEXT NOT NULL, Category TEXT NOT NULL, ImageType TEXT NOT NULL, Width INTEGER NOT NULL, Height INTEGER NOT NULL, Size INTEGER NOT NULL, Data BLOB NOT NULL);\r\n" +
            "CREATE TABLE Metadata (ID INTEGER PRIMARY KEY, FileID INTEGER REFERENCES Files (ID) NOT NULL, \"Key\" TEXT NOT NULL, Value TEXT NOT NULL);\r\n" +
            "CREATE TABLE Tracks (ID INTEGER PRIMARY KEY, ArtistID INTEGER REFERENCES Artists (ID) NOT NULL, AlbumID INTEGER REFERENCES Albums (ID) NOT NULL, Number INTEGER NOT NULL, Name TEXT NOT NULL);\r\n" +
            "CREATE INDEX AlbumsAlbumArtistIDIndex ON Albums (AlbumArtistID ASC);\r\n" +
            "CREATE INDEX AlbumsScanSetIDIndex ON Albums (ScanSetID ASC);\r\n" +
            "CREATE INDEX FilesPathIndex ON Files(Path ASC);\r\n" +
            "CREATE INDEX FilesScanSetIDIndex ON Files(ScanSetID ASC);\r\n" +
            "CREATE INDEX FilesTrackIDIndex ON Files(TrackID ASC);\r\n" +
            "CREATE INDEX ImagesFileIDIndex ON Images (FileID ASC);\r\n" +
            "CREATE INDEX MetadataKeyIndex ON Metadata(\"Key\" ASC);\r\n" +
            "CREATE INDEX MetadataFileIDIndex ON Metadata(FileID ASC);\r\n" +
            "CREATE INDEX TracksAlbumIDIndex ON Tracks (AlbumID ASC);\r\n" +
            "CREATE INDEX TracksArtistIDIndex ON Tracks (ArtistID ASC);\r\n" +
            "CREATE VIEW MetadataMapView AS SELECT *,\r\n" +
            "(SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'Artist') AS Artist,\r\n" +
            "COALESCE(\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'AlbumArtist'),\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'Artist')) AS AlbumArtist,\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'Album') AS Album,\r\n" +
            "   CAST((SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'TrackNumber') AS INTEGER) AS Number,\r\n" +
            "   (SELECT Value FROM Metadata WHERE FileID = Files.ID AND \"Key\" = 'Title') AS Name\r\n" +
            "   FROM Files;\r\n" +
            "CREATE VIEW MetadataSummaryView AS SELECT Files.*, Artists.Name AS Artist, AlbumArtists.Name AS AlbumArtist,\r\n" +
            "   Albums.Name AS Album, Tracks.Number AS TrackNumber, Tracks.Name AS Track FROM\r\n" +
            "   Files JOIN Tracks ON Files.TrackID = Tracks.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN\r\n" +
            "   Albums ON Tracks.AlbumID = Albums.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID;\r\n" +
            "COMMIT TRANSACTION;\r\n" +
            "PRAGMA foreign_keys = on\r\n";

        public MetadataDatabase(string filename)
        {
            bool createtables = !File.Exists(filename);
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = filename,
                DateTimeKind = DateTimeKind.Utc
            };
            conn_ = new SQLiteConnection(builder.ConnectionString);
            conn_.Open();
            if (createtables)
            {
                using (var comm = conn_.CreateCommand())
                {
                    comm.CommandText = creationsql_;
                    comm.ExecuteNonQuery();
                }
            }
        }

        public MetadataCache BuildCache(IEnumerable<string> paths)
        {
            var dbsets = new List<(string Path, int ID)>();
            using (var setscomm = conn_.CreateCommand())
            {
                setscomm.CommandText = "SELECT Path, ID FROM ScanSets";
                using (var reader = setscomm.ExecuteReader())
                    while (reader.Read())
                        dbsets.Add((reader.GetString(0), reader.GetInt32(1)));
            }

            MetadataCache cache = new MetadataCache();

            foreach (var path in paths)
            {
                var set = dbsets.SingleOrDefault(s => s.Path.Equals(path, StringComparison.InvariantCultureIgnoreCase));
                if (set == default)
                    continue;
                using (var querycomm = conn_.CreateCommand())
                {
                    querycomm.CommandText = "SELECT Path, LastWriteTime, CodecName, CodecType, AverageBitrate, MaxBitrate, BitsPerSample, SampleRate, Channels, DurationInFrames, Artist, AlbumArtist, Album, TrackNumber, Track FROM MetadataSummaryView WHERE ScanSetID = " + set.ID;
                    using (var reader = querycomm.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var ce = new MetadataCacheEntry(reader);
                            ce.Strip();
                            cache.AddDBCacheEntry(Path.Combine(set.Path, reader[0] as string), ce);
                        }
                    }
                }

            }

            return cache;
        }

        public (int Added, int Modified, int Removed, int Unchanged) IndexFiles(IEnumerable<string> paths, bool fixup = true, bool deletemissingsets = false)
        {
            return IndexFilesAsync(paths, fixup, deletemissingsets).GetAwaiter().GetResult();
        }

        public async Task<(int Added, int Modified, int Removed, int Unchanged)> IndexFilesAsync(IEnumerable<string> paths, bool fixup = true, bool deletemissingsets = false)
        {
            var sets = new List<(string Path, int ID)>();
            var missedsets = new List<int>();

            using (var setscomm = conn_.CreateCommand())
            {
                var modpaths = paths.Select(p => p.EndsWith(Path.PathSeparator.ToString()) ? p.Substring(0, p.Length - 1) : p);
                var dbsets = new List<(string Path, int ID, bool Hit)>();
                var missing = new List<string>();
                setscomm.CommandText = "SELECT Path, ID FROM ScanSets";
                using (var reader = setscomm.ExecuteReader())
                    while (reader.Read())
                        dbsets.Add((reader.GetString(0), reader.GetInt32(1), false));
                foreach (var path in modpaths)
                    if (dbsets.Select(s => s.Path).Count(predicate => predicate.Equals(path, StringComparison.InvariantCultureIgnoreCase)) == 0)
                        missing.Add(path);
                setscomm.CommandText = "INSERT INTO ScanSets (Path) VALUES (@Path);";
                var pathvar = setscomm.Parameters.Add("@Path", System.Data.DbType.String);
                using (var transaction = conn_.BeginTransaction())
                {
                    foreach (var path in missing)
                    {
                        pathvar.Value = path;
                        setscomm.ExecuteNonQuery();                       
                    }
                    transaction.Commit();
                }
                setscomm.CommandText = "SELECT Path, ID FROM ScanSets";
                dbsets.Clear();
                using (var reader = setscomm.ExecuteReader())
                    while (reader.Read())
                        dbsets.Add((reader.GetString(0), reader.GetInt32(1), modpaths.Count(p => p.Equals(reader.GetString(0), StringComparison.InvariantCultureIgnoreCase)) != 0));
                sets.AddRange(dbsets.Where(s => s.Hit).Select(s => (s.Path, s.ID)));
                missedsets.AddRange(dbsets.Where(s => !s.Hit).Select(s => (s.ID)));
            }

            int added = 0, modified = 0, removed = 0, unchanged = 0;

            var filequeue = new BlockingCollection<(int ID, int Set, string FileName, long Length, DateTime LastWriteTime, IMetadataProvider Metadata)>();

            var filesdict = new ConcurrentDictionary<(int ScanSetID, string Path), (int ID, long Length, DateTime LastWriteTime)>();
            var fileshitdict = new ConcurrentDictionary<(int ScanSetID, string Path), bool>();

            using (var getfilescomm = conn_.CreateCommand())
            {
                getfilescomm.CommandText = "SELECT ID, ScanSetID, Path, FileSize, LastWriteTime FROM Files";
                using (var reader = getfilescomm.ExecuteReader())
                    while (reader.Read())
                    {
                        var key = (reader.GetInt32(1), reader.GetString(2));
                        filesdict[key] = (reader.GetInt32(0), reader.GetInt64(3), DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
                        fileshitdict[key] = false;
                    }
            }

            var metadatareadtask = Task.Run(() =>
            {
                Parallel.ForEach(sets, (scanpath) =>
                {
                   DirectoryInfo di = new DirectoryInfo(scanpath.Path);
                   int scanset = scanpath.ID;
                   var files = di.EnumerateFiles("*", SearchOption.AllDirectories).AsParallel().Where(fsi => MetadataExtensions.ValidExtensions.Contains(Path.GetExtension(fsi.FullName).ToLower()) && ((fsi.Attributes & FileAttributes.Directory) == 0)).ToArray();

                   Parallel.ForEach(files, (fi) =>
                   {
                       var relativename = fi.FullName.Substring(scanpath.Path.Length + 1);
                       var key = (scanset, relativename);
                       bool scan = false;
                       int id = -1;
                       if (filesdict.ContainsKey(key))
                       {
                           fileshitdict[key] = true;
                           var file = filesdict[key];
                           if ((fi.LastWriteTimeUtc > file.Item3) || (fi.Length != file.Item2))
                           {
                               id = file.Item1;
                               Interlocked.Increment(ref modified);
                               scan = true;
                           }
                           else
                           {
                               Interlocked.Increment(ref unchanged);
                           }
                       }
                       else
                       {
                           scan = true;
                           Interlocked.Increment(ref added);
                       }
                       if (scan)
                           filequeue.Add((id, scanpath.ID, relativename, fi.Length, fi.LastWriteTimeUtc, Metadata.GetProvider(fi.FullName)));
                   });

                   foreach (var file in fileshitdict.Where(kv => (kv.Key.ScanSetID == scanset) && !kv.Value).Select(kv => kv.Key))
                   {
                       filequeue.Add((filesdict[file].Item1, scanpath.ID, null, 0, DateTime.MinValue, null));
                       removed++;
                   }

                });
                filequeue.CompleteAdding();
            });

            bool done = false;
            using (var transaction = conn_.BeginTransaction())
            {
                using (SQLiteCommand delcomm = conn_.CreateCommand(), filecomm = conn_.CreateCommand(), metacomm = conn_.CreateCommand(), imagecomm = conn_.CreateCommand())
                {
                    delcomm.CommandText = "DELETE FROM Files WHERE ID = @ID";
                    delcomm.Parameters.Clear();
                    var delidparam = delcomm.Parameters.Add("@ID", System.Data.DbType.Int32);

                    var setparam = filecomm.Parameters.Add("@Set", System.Data.DbType.Int32);
                    var pathparam = filecomm.Parameters.Add("@Path", System.Data.DbType.String);
                    var filesizeparam = filecomm.Parameters.Add("@FileSize", System.Data.DbType.Int64);
                    var lastwritetimeparam = filecomm.Parameters.Add("@LastWriteTime", System.Data.DbType.DateTime);
                    var codecnameparam = filecomm.Parameters.Add("@CodecName", System.Data.DbType.String);
                    var codectypeparam = filecomm.Parameters.Add("@CodecType", System.Data.DbType.String);
                    var averagebitrateparam = filecomm.Parameters.Add("@AverageBitrate", System.Data.DbType.Int32);
                    var maxbitrateparam = filecomm.Parameters.Add("@MaxBitrate", System.Data.DbType.Int32);
                    var bitspersampleparam = filecomm.Parameters.Add("@BitsPerSample", System.Data.DbType.Int32);
                    var samplerateparam = filecomm.Parameters.Add("@SampleRate", System.Data.DbType.Int32);
                    var channelsparam = filecomm.Parameters.Add("@Channels", System.Data.DbType.Int32);
                    var durationinframesparam = filecomm.Parameters.Add("@DurationInFrames", System.Data.DbType.Int32);
                    filecomm.CommandText = "INSERT INTO Files (Path, ScanSetID, FileSize, LastWriteTime, CodecName, CodecType, AverageBitrate, MaxBitrate, BitsPerSample, SampleRate, Channels, DurationInFrames)" +
                    " VALUES (@Path, @Set, @FileSize, @LastWriteTime, @CodecName, @CodecType, @AverageBitrate, @MaxBitrate, @BitsPerSample, @SampleRate, @Channels, @DurationInFrames);\r\n" +
                    "SELECT last_insert_rowid();";

                    metacomm.CommandText = "INSERT INTO Metadata (FileID, \"Key\", Value) VALUES (@FileID, @Key, @Value)";
                    var metafileidparam = metacomm.Parameters.Add("@FileID", System.Data.DbType.Int32);
                    var keyparam = metacomm.Parameters.Add("@Key", System.Data.DbType.String);
                    var valueparam = metacomm.Parameters.Add("@Value", System.Data.DbType.String);

                    imagecomm.CommandText = "INSERT INTO Images (FileID, Description, Category, ImageType, Width, Height, Size, Data) VALUES (@FileID, @Description, @Category, @ImageType, @Width, @Height, @Size, @Data)";
                    var imagefileidparam = imagecomm.Parameters.Add("@FileID", System.Data.DbType.Int32);
                    var descriptionparam = imagecomm.Parameters.Add("@Description", System.Data.DbType.String);
                    var categoryparam = imagecomm.Parameters.Add("@Category", System.Data.DbType.String);
                    var imagetypeparam = imagecomm.Parameters.Add("@ImageType", System.Data.DbType.String);
                    var widthparam = imagecomm.Parameters.Add("@Width", System.Data.DbType.Int32);
                    var heightparam = imagecomm.Parameters.Add("@Height", System.Data.DbType.Int32);
                    var sizeparam = imagecomm.Parameters.Add("@Size", System.Data.DbType.Int32);
                    var dataparam = imagecomm.Parameters.Add("@Data", System.Data.DbType.Object);

                    while (!done)
                    {

                        (int ID, int Set, string FileName, long Length, DateTime LastWriteTime, IMetadataProvider Metadata) file = (0, 0, null, 0, DateTime.MinValue, null);
                        try
                        {
                            file = filequeue.Take();
                        }
                        catch
                        {
                            done = true;
                            transaction.Commit();
                        }
                        if (!done)
                        {
                            if (file.ID != -1)
                            {
                                delidparam.Value = file.ID;
                                delcomm.ExecuteNonQuery();
                            }
                            if (file.Metadata != null)
                            {
                                IMetadataProvider mp = file.Metadata;
                                ICodecProvider cp = mp as ICodecProvider;
                                pathparam.Value = file.FileName;
                                filesizeparam.Value = file.Length;
                                lastwritetimeparam.Value = file.LastWriteTime;
                                codecnameparam.Value = cp.CodecName;
                                codectypeparam.Value = cp.CodecType.ToString();
                                averagebitrateparam.Value = cp.AverageBitrate;
                                maxbitrateparam.Value = cp.MaxBitrate;
                                bitspersampleparam.Value = cp.BitsPerSample;
                                samplerateparam.Value = cp.Samplerate;
                                channelsparam.Value = cp.Channels;
                                durationinframesparam.Value = cp.DurationInFrames;
                                setparam.Value = file.Set;

                                metafileidparam.Value = imagefileidparam.Value = (long)filecomm.ExecuteScalar();
                                foreach (var kv in mp.GetTextMetadata())
                                {
                                    keyparam.Value = kv.Key;
                                    valueparam.Value = kv.Value;
                                    metacomm.ExecuteNonQuery();
                                }

                                foreach (var image in mp.GetImageMetadata())
                                {
                                    descriptionparam.Value = image.Description;
                                    categoryparam.Value = image.Category;
                                    imagetypeparam.Value = image.ImageType;
                                    widthparam.Value = image.Width;
                                    heightparam.Value = image.Height;
                                    sizeparam.Value = image.Size;
                                    dataparam.Value = image.Data;
                                    imagecomm.ExecuteNonQuery();
                                }
                            }
                        }
                    }
                }
            }

            await metadatareadtask;

            if (deletemissingsets && (missedsets.Count != 0))
            {
                using (var setcomm = conn_.CreateCommand())
                {
                    setcomm.CommandText = "DELETE FROM ScanSets WHERE ID = @ID";
                    var idparam = setcomm.Parameters.Add("@ID", System.Data.DbType.Int32);
                    using (var transaction = conn_.BeginTransaction())
                    {
                        foreach (var set in missedsets)
                        {
                            idparam.Value = set;
                            setcomm.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }
                }
            }

            if (fixup && ((added != 0) || (modified != 0) || (removed != 0)))
                removed += Fixup();

            return (added, modified, removed, unchanged);
        }

        private int Fixup()
        {
            int removed = 0;
            using (var querycomm = conn_.CreateCommand())
            {
                var artistlist = new List<string>();
                var albumartistlist = new List<string>();

                querycomm.CommandText = "INSERT INTO Artists (Name) SELECT DISTINCT Artist FROM MetadataMapView WHERE Artist NOT IN (SELECT Name FROM Artists)";
                querycomm.Parameters.Clear();
                querycomm.ExecuteNonQuery();
                querycomm.CommandText = "INSERT INTO AlbumArtists (Name) SELECT DISTINCT AlbumArtist FROM MetadataMapView WHERE AlbumArtist NOT IN (SELECT Name FROM AlbumArtists)";
                querycomm.Parameters.Clear();
                querycomm.ExecuteNonQuery();

                var artistdict = new Dictionary<string, int>();
                querycomm.CommandText = "SELECT * FROM Artists";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        artistdict.Add(reader.GetString(1), reader.GetInt32(0));

                var albumartistdict = new Dictionary<string, int>();
                querycomm.CommandText = "SELECT * FROM AlbumArtists";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumartistdict.Add(reader.GetString(1), reader.GetInt32(0));

                var toupdate = new List<(int, int, string, string, string, string, int, string)>();
                querycomm.CommandText = "SELECT ID, ScanSetID, Path, Artist, AlbumArtist, Album, Number, Name FROM MetadataMapView WHERE TrackID IS NULL";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        toupdate.Add((reader.GetInt32(0), reader.GetInt32(1), reader[2] as string, reader[3] as string, reader[4] as string, reader[5] as string, reader.IsDBNull(6) ? 0 : reader.GetInt32(6), reader[7] as string));

                var distinctalbums = toupdate.Select(t => (t.Item2, Path.GetDirectoryName(t.Item3), t.Item5, t.Item6)).Distinct().ToDictionary(da => da, da => true);
                var albumsdict = new Dictionary<(int, string, string, string), int>();
                querycomm.CommandText = "SELECT Albums.ID, Albums.ScanSetID, Albums.Path, AlbumArtists.Name, Albums.Name FROM Albums JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumsdict.Add((reader.GetInt32(1), reader[2] as string, reader[3] as string, reader[4] as string), reader.GetInt32(0));

                var albumdifferences = distinctalbums.Keys.Except(albumsdict.Keys).ToArray();
                if (albumdifferences.Length > 0)
                {
                    using (var transaction = conn_.BeginTransaction())
                    {
                        querycomm.CommandText = "INSERT INTO Albums (Name, Path, AlbumArtistID, ScanSetID) VALUES (@Name, @Path, @AlbumArtistID, @ScanSetID)";
                        querycomm.Parameters.Clear();
                        var nameparam = querycomm.Parameters.Add("@Name", System.Data.DbType.String);
                        var pathparam = querycomm.Parameters.Add("@Path", System.Data.DbType.String);
                        var albumartistidparam = querycomm.Parameters.Add("@AlbumArtistID", System.Data.DbType.String);
                        var scansetidparam = querycomm.Parameters.Add("@ScanSetID", System.Data.DbType.Int32);
                        foreach (var album in albumdifferences)
                        {
                            nameparam.Value = album.Item4;
                            pathparam.Value = album.Item2;
                            albumartistidparam.Value = albumartistdict[album.Item3];
                            scansetidparam.Value = album.Item1;
                            querycomm.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }
                }

                albumsdict.Clear();
                querycomm.CommandText = "SELECT Albums.ID, Albums.ScanSetID, Albums.Path, AlbumArtists.Name, Albums.Name FROM Albums JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        albumsdict.Add((reader.GetInt32(1), reader[2] as string, reader[3] as string, reader[4] as string), reader.GetInt32(0));

                var distincttracks = toupdate.Select(t => (t.Item2, Path.GetDirectoryName(t.Item3), t.Item4, t.Item5, t.Item6, t.Item7, t.Item8)).Distinct().ToDictionary(dt => dt, dt => true);
                var tracksdict = new Dictionary<(int, string, string, string, string, int, string), int>();
                querycomm.CommandText = "SELECT Tracks.ID, Albums.ScanSetID, Albums.Path, Artists.Name, AlbumArtists.Name, Albums.Name, Tracks.Number, Tracks.Name FROM Tracks JOIN Albums ON Tracks.AlbumID = Albums.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                querycomm.Parameters.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        tracksdict.Add((reader.GetInt32(1), reader[2] as string, reader[3] as string, reader[4] as string, reader[5] as string, reader.GetInt32(6), reader[7] as string), reader.GetInt32(0));

                var trackdifferences = distincttracks.Keys.Except(tracksdict.Keys).ToArray();
                if (trackdifferences.Length > 0)
                {
                    using (var transaction = conn_.BeginTransaction())
                    {
                        querycomm.CommandText = "INSERT INTO Tracks (ArtistID, AlbumID, Number, Name) VALUES (@ArtistID, @AlbumID, @Number, @Name)";
                        querycomm.Parameters.Clear();
                        var artistidparam = querycomm.Parameters.Add("@ArtistID", System.Data.DbType.Int32);
                        var albumidparam = querycomm.Parameters.Add("@AlbumID", System.Data.DbType.Int32);
                        var Numberparam = querycomm.Parameters.Add("@Number", System.Data.DbType.Int32);
                        var Nameparam = querycomm.Parameters.Add("@Name", System.Data.DbType.String);
                        foreach (var track in trackdifferences)
                        {
                            artistidparam.Value = artistdict[track.Item3];
                            albumidparam.Value = albumsdict[(track.Item1, track.Item2, track.Item4, track.Item5)];
                            Numberparam.Value = track.Item6;
                            Nameparam.Value = track.Item7;
                            querycomm.ExecuteNonQuery();
                        }
                        transaction.Commit();
                    }
                }

                querycomm.CommandText = "SELECT Tracks.ID, Albums.ScanSetID, Albums.Path, Artists.Name, AlbumArtists.Name, Albums.Name, Tracks.Number, Tracks.Name FROM Tracks JOIN Albums ON Tracks.AlbumID = Albums.ID JOIN Artists ON Tracks.ArtistID = Artists.ID JOIN AlbumArtists ON Albums.AlbumArtistID = AlbumArtists.ID";
                querycomm.Parameters.Clear();
                tracksdict.Clear();
                using (var reader = querycomm.ExecuteReader())
                    while (reader.Read())
                        tracksdict.Add((reader.GetInt32(1), reader[2] as string, reader[3] as string, reader[4] as string, reader[5] as string, reader.GetInt32(6), reader[7] as string), reader.GetInt32(0));

                if (toupdate.Count > 0)
                {
                    querycomm.CommandText = "UPDATE Files SET TrackID = @TrackID WHERE ID = @ID";
                    querycomm.Parameters.Clear();
                    var trackidparam = querycomm.Parameters.Add("@TrackID", System.Data.DbType.String);
                    var idparam = querycomm.Parameters.Add("@ID", System.Data.DbType.String);
                    using (var transaction = conn_.BeginTransaction())
                    {
                        foreach (var u in toupdate)
                        {
                            idparam.Value = u.Item1;
                            trackidparam.Value = tracksdict[(u.Item2, Path.GetDirectoryName(u.Item3), u.Item4, u.Item5, u.Item6, u.Item7, u.Item8)];
                            querycomm.ExecuteNonQuery();
                        }
                        transaction.Commit();

                    }
                }

                using (var transaction = conn_.BeginTransaction())
                {
                    querycomm.Parameters.Clear();
                    querycomm.CommandText = "DELETE FROM Files WHERE ScanSetID NOT IN (SELECT ID FROM ScanSets);";
                    removed = querycomm.ExecuteNonQuery();
                    querycomm.CommandText =
                        "DELETE FROM Metadata WHERE FileID NOT IN (SELECT ID FROM Files);\r\n" +
                        "DELETE FROM Images WHERE FileID NOT IN (SELECT ID FROM Files);\r\n" +
                        "DELETE FROM Tracks WHERE ID NOT IN (SELECT TrackID FROM Files);\r\n" +
                        "DELETE FROM Artists WHERE ID NOT IN (SELECT ArtistID FROM Tracks);\r\n" +
                        "DELETE FROM Albums WHERE ID NOT IN (SELECT AlbumID FROM Tracks);\r\n" +
                        "DELETE FROM AlbumArtists WHERE ID NOT IN (SELECT AlbumArtistID FROM Albums);";
                    querycomm.ExecuteNonQuery();
                    transaction.Commit();
                }
            }

            return removed;
        }

        public void Dispose()
        {
            conn_.Dispose();
        }

    }
}
